﻿using System.Collections;
using Gazillion;
using MHServerEmu.Core.Collections;
using MHServerEmu.Core.Extensions;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Memory;
using MHServerEmu.Core.Network;
using MHServerEmu.Core.Serialization;
using MHServerEmu.Games.Common;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.Events.Templates;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.LiveTuning;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Network;
using MHServerEmu.Games.Powers;
using MHServerEmu.Games.Powers.Conditions;
using MHServerEmu.Games.Properties;

namespace MHServerEmu.Games.Entities
{
    public class ConditionCollection : ISerialize
    {
        public const int MaxConditions = 256;
        public const ulong InvalidConditionId = 0;

        private static readonly Logger Logger = LogManager.CreateLogger();

        private readonly WorldEntity _owner;

        private readonly SortedDictionary<ulong, Condition> _currentConditions = new();
        private readonly Dictionary<StackId, int> _stackCountCache = new();

        private readonly EventGroup _pendingEvents = new();

        private uint _version = 0;
        private ulong _nextConditionId = 1;

        public ulong NextConditionId { get => _nextConditionId++; }

        public KeywordsMask ConditionKeywordsMask { get; } = new();

        public ConditionCollection(WorldEntity owner)
        {
            _owner = owner;
        }

        public bool Serialize(Archive archive)
        {
            bool success = true;

            if (archive.IsTransient)
            {
                if (archive.IsPacking)
                {
                    if (_currentConditions.Count >= MaxConditions)
                        return Logger.ErrorReturn(false, $"Serialize(): _currentConditionDict.Count >= MaxConditions");

                    uint numConditions = (uint)_currentConditions.Count;
                    success &= Serializer.Transfer(archive, ref numConditions);

                    foreach (Condition condition in _currentConditions.Values)
                        success &= condition.Serialize(archive, _owner);
                }
                else
                {
                    if (_currentConditions.Count != 0)
                        return Logger.ErrorReturn(false, $"Serialize(): _currrentConditionDict is not empty");

                    uint numConditions = 0;
                    success &= Serializer.Transfer(archive, ref numConditions);

                    if (numConditions >= MaxConditions)
                        return Logger.ErrorReturn(false, $"Serialize(): numConditions >= MaxConditions");

                    for (uint i = 0; i < numConditions; i++)
                    {
                        Condition condition = AllocateCondition();
                        success &= condition.Serialize(archive, _owner);
                        if (InsertCondition(condition) == false && condition.IsInCollection == false)
                            DeleteCondition(condition);
                    }
                }
            }
            else if (archive.IsPersistent && archive.Version >= ArchiveVersion.ImplementedConditionPersistence)
            {
                uint numConditions = (uint)GetPersistentConditionCount();
                success &= Serializer.Transfer(archive, ref numConditions);

                // When GetPersistentConditionCount() fails validation, it returns 0. Return early to avoid partial writes.
                if (numConditions == 0)
                    return success;

                if (archive.IsPacking)
                {
                    foreach (Condition condition in _currentConditions.Values)
                    {
                        if (condition.IsPersistToDB() == false)
                            continue;

                        using PropertyCollection properties = ObjectPoolManager.Instance.Get<PropertyCollection>();
                        ConditionStore conditionStore = new(properties);
                        success &= condition.SaveToConditionStore(ref conditionStore);
                        success &= conditionStore.Serialize(archive);
                        numConditions--;
                    }

                    // This is very bad and should never happen
                    if (numConditions != 0)
                        Logger.Error($"Serialize(): Count mismatch when serializing persistent conditions for owner [{_owner}]");
                }
                else
                {
                    for (uint i = 0; i < numConditions; i++)
                    {
                        using PropertyCollection properties = ObjectPoolManager.Instance.Get<PropertyCollection>();
                        ConditionStore conditionStore = new(properties);
                        success &= conditionStore.Serialize(archive);

                        Condition condition = AllocateCondition();
                        if (condition.InitializeFromConditionStore(NextConditionId, ref conditionStore, _owner))
                        {
                            InsertCondition(condition);
                        }
                        else
                        {
                            success = false;
                            Logger.Error($"Serialize(): Failed to initialize condition from ConditionStore for owner [{_owner}]");
                            DeleteCondition(condition);
                        }
                    }
                }
            }

            return success;
        }

        public bool OnUnpackComplete(Archive archive)
        {
            if (archive.IsUnpacking == false) return Logger.WarnReturn(false, "OnUnpackComplete(): archive.IsUnpacking == false");
            if (_owner == null) return Logger.WarnReturn(false, "OnUnpackComplete(): _owner == null");

            Avatar avatar = _owner.GetSelfOrOwnerOfType<Avatar>();
            ulong ownerPlayerDbId = avatar != null ? avatar.OwnerPlayerDbId : 0;

            foreach (Condition condition in this)
            {
                condition.Properties.Bind(_owner, AOINetworkPolicyValues.AllChannels);
                condition.RestoreCreatorIdIfPossible(_owner.Id, ownerPlayerDbId);   // Owner's entity id changes on transfer
                condition.CacheStackId();

                if (condition.SerializationFlags.HasFlag(ConditionSerializationFlags.IsDisabled))
                    IncrementStackCountCache(condition);    // Just increment the stack instead of doing the whole OnInsertCondition() callback
                else
                    OnInsertCondition(condition);           // Apply this condition to the owner
            }

            // Validate stacks
            foreach (Condition condition in this)
            {
                StackingBehaviorPrototype stackingBehaviorProto = condition.GetStackingBehaviorPrototype();
                if (stackingBehaviorProto == null)
                    continue;

                if (GetNumberOfStacks(condition) > stackingBehaviorProto.MaxNumStacks)
                {
                    Logger.Warn($"OnUnpackComplete(): The number of stacks for condition [{condition}] exceeds the maximum of {stackingBehaviorProto.MaxNumStacks} for owner [{_owner}]");
                    RemoveStack(condition.StackId);
                }
            }

            return true;
        }

        /// <summary>
        /// Returns the <see cref="Condition"/> with the specified condition id (key).
        /// Returns <see langword="null"/> if no <see cref="Condition"/> with such id is present in this <see cref="ConditionCollection"/>.
        /// </summary>
        public Condition GetCondition(ulong conditionId)
        {
            if (_currentConditions.TryGetValue(conditionId, out Condition condition) == false)
                return null;

            return condition;
        }

        /// <summary>
        /// Returns the <see cref="Condition"/> with the specified <see cref="PrototypeId"/>.
        /// Returns <see langword="null"/> if no <see cref="Condition"/> with such prototype is present in this <see cref="ConditionCollection"/>.
        /// </summary>
        public Condition GetConditionByRef(PrototypeId conditionRef)
        {
            if (conditionRef == PrototypeId.Invalid) return Logger.WarnReturn<Condition>(null, $"GetConditionByRef(): conditionRef == PrototypeId.Invalid");

            foreach (Condition condition in _currentConditions.Values)
            {
                if (condition.ConditionPrototypeRef == conditionRef)
                    return condition;
            }

            return null; 
        }

        /// <summary>
        /// Returns the id (key) of the condition with the specified <see cref="PrototypeId"/>.
        /// Returns invalid id (0) if no condition with such prototype is present in this <see cref="ConditionCollection"/>.
        /// </summary>
        public ulong GetConditionIdByRef(PrototypeId conditionRef)
        {
            Condition condition = GetConditionByRef(conditionRef);
            if (condition == null) return InvalidConditionId;
            return condition.Id;
        }

        public Iterator IterateConditions(bool skipDisabled)
        {
            return new(this, skipDisabled);
        }

        public int GetNumberOfStacks(Condition condition)
        {
            // Non-power conditions cannot stack
            if (condition.CreatorPowerPrototype == null)
                return 0;

            return GetNumberOfStacks(condition.StackId);
        }

        public int GetNumberOfStacks(in StackId stackId)
        {
            if (stackId.PrototypeRef == PrototypeId.Invalid) return Logger.WarnReturn(0, "GetNumberOfStacks(): stackId.PrototypeRef == PrototypeId.Invalid");

            if (_stackCountCache.TryGetValue(stackId, out int stackCount) == false)
                return 0;

            return stackCount;
        }

        public int GetStackApplicationData(in StackId stackId, StackingBehaviorPrototype stackingBehaviorProto, int rankToApply, 
            out TimeSpan longestTimeRemaining, List<ulong> removeList = null, List<ulong> refreshList = null)
        {
            return GetStackApplicationData(stackId, stackingBehaviorProto, rankToApply, 0, out _, out longestTimeRemaining, removeList, refreshList);
        }

        public int GetStackApplicationData(in StackId stackId, StackingBehaviorPrototype stackingBehaviorProto, int rankToApply, ulong creatorId,
            out int existingStackCount, out TimeSpan longestTimeRemaining, List<ulong> removeList = null, List<ulong> refreshList = null)
        {
            existingStackCount = 0;
            longestTimeRemaining = TimeSpan.Zero;
            
            int numStacksToApply = 0;
            int maxNumStacksToApply = stackingBehaviorProto.NumStacksToApply;
            int numStacksAvailable = stackingBehaviorProto.MaxNumStacks;

            List<(int, ulong)> conditionsByRankList = ListPool<(int, ulong)>.Instance.Get();

            foreach (Condition condition in _currentConditions.Values)
            {
                if (condition.CanStackWith(stackId) == false)
                    continue;

                // Mark all used stack slots
                numStacksAvailable--;

                // Track lower rank conditions to remove if we run out of stack slots
                if (stackingBehaviorProto.StacksFromDifferentCreators)
                {
                    int rank = condition.Rank;
                    if (rank < rankToApply)
                        conditionsByRankList.Add((rank, condition.Id));
                }

                // Count the current number of stacks
                if (creatorId != Entity.InvalidId && condition.Id == creatorId)
                    existingStackCount++;

                // Determine the longest remaining duration
                TimeSpan timeRemaining = condition.TimeRemaining;
                if (timeRemaining > longestTimeRemaining)
                    longestTimeRemaining = timeRemaining;

                // Mark all conditions for removal if this stacking behavior recreates all stacks on each application
                if (removeList != null && stackingBehaviorProto.ApplicationStyle == StackingApplicationStyleType.Recreate)
                    removeList.Add(condition.Id);

                // Mark conditions with duration for refresh / duration extension if needed
                if (refreshList != null &&
                    (stackingBehaviorProto.ApplicationStyle == StackingApplicationStyleType.Refresh ||
                    stackingBehaviorProto.ApplicationStyle == StackingApplicationStyleType.SingleStackAddDuration ||
                    stackingBehaviorProto.ApplicationStyle == StackingApplicationStyleType.MultiStackAddDuration) &&
                    condition.Duration > TimeSpan.Zero)
                {
                    refreshList.Add(condition.Id);
                }
            }

            // The available number of stack slots should always be >= 0
            if (numStacksAvailable < 0)
            {
                Logger.Warn("GetStackingData(): numStacksAvailable < 0");
                goto end;
            }

            // Determine the number of stacks to apply based on the application style
            switch (stackingBehaviorProto.ApplicationStyle)
            {
                case StackingApplicationStyleType.DontRefresh:
                case StackingApplicationStyleType.Refresh:
                case StackingApplicationStyleType.MatchDuration:
                case StackingApplicationStyleType.MultiStackAddDuration:
                    numStacksToApply = Math.Min(numStacksAvailable, maxNumStacksToApply);

                    // Remove lower rank conditions until we run out of lower rank conditions or reach the required number of stacks
                    if (numStacksToApply < maxNumStacksToApply)
                    {
                        // Sort to remove the lowest rank conditions first
                        conditionsByRankList.Sort((left, right) => left.Item1.CompareTo(right.Item1));
                        foreach (var entry in conditionsByRankList)
                        {
                            if (numStacksToApply >= maxNumStacksToApply)
                                break;

                            removeList?.Add(entry.Item2);
                            numStacksToApply++;
                        }
                    }

                    break;

                case StackingApplicationStyleType.Recreate:
                    // Apply all the stacks if we are recreating
                    numStacksToApply = maxNumStacksToApply;
                    break;

                case StackingApplicationStyleType.SingleStackAddDuration:
                    // Only add a single stack if it's not present already
                    if (numStacksAvailable == 1)
                        numStacksToApply = 1;
                    break;

                default:
                    Logger.Warn($"GetStackingData(): Unsupported application style {stackingBehaviorProto.ApplicationStyle}");
                    goto end;
            }

            end:
            ListPool<(int, ulong)>.Instance.Return(conditionsByRankList);
            return numStacksToApply;
        }

        public static StackId MakeConditionStackId(PowerPrototype powerProto, ConditionPrototype conditionProto, ulong creatorEntityId,
            ulong creatorPlayerId, out StackingBehaviorPrototype stackingBehaviorProto)
        {
            bool useLegacyStackingBehavior = false;

            if (conditionProto?.StackingBehavior != null)
            {
                stackingBehaviorProto = conditionProto.StackingBehavior;
            }
            else
            {
                stackingBehaviorProto = powerProto.StackingBehaviorLEGACY;
                useLegacyStackingBehavior = true;
            }

            if (stackingBehaviorProto == null)
                return Logger.WarnReturn(StackId.Invalid, "MakeConditionStackId(): stackingBehaviorProto == null");

            ulong stackCreatorId = Entity.InvalidId;

            if (stackingBehaviorProto.StacksFromDifferentCreators == false)
            {
                stackCreatorId = creatorPlayerId != Entity.InvalidId ? creatorPlayerId : creatorEntityId;
                if (stackCreatorId == Entity.InvalidId)
                    return Logger.WarnReturn(StackId.Invalid, $"MakeConditionStackId(): Invalid creator id! creatorEntityId=[{creatorEntityId}] creatorPlayerId=[{creatorPlayerId}] power=[{powerProto}] condition=[{conditionProto}]");
            }

            if (stackingBehaviorProto.StacksByKeyword.HasValue())
            {
                // Keyword stacking
                PrototypeId keywordProtoRef = stackingBehaviorProto.StacksByKeyword[0];
                return new(keywordProtoRef, -1, stackCreatorId);
            }
            else if (stackingBehaviorProto.StacksWithOtherPower != PrototypeId.Invalid)
            {
                // Legacy stacking with other powers
                if (useLegacyStackingBehavior == false)
                    return Logger.WarnReturn(StackId.Invalid, "MakeConditionStackId(): Stacking with other powers is supported only for legacy stacking behavior");

                PrototypeId powerProtoRef = powerProto.DataRef < stackingBehaviorProto.StacksWithOtherPower ? powerProto.DataRef : stackingBehaviorProto.StacksWithOtherPower;
                return new(powerProtoRef, -1, stackCreatorId);
            }
            else if (conditionProto.DataRef != PrototypeId.Invalid)
            {
                // Standalone condition stacking
                if (useLegacyStackingBehavior)
                    return new(powerProto.DataRef, -1, stackCreatorId);

                return new(conditionProto.DataRef, -1, stackCreatorId);
            }

            // Power mixin condition stacking
            if (useLegacyStackingBehavior)
                return new(powerProto.DataRef, -1, stackCreatorId);

            return new(powerProto.DataRef, conditionProto.BlueprintCopyNum, stackCreatorId);
        }

        public bool AddCondition(Condition condition)
        {
            if (condition == null) return Logger.WarnReturn(false, "AddCondition(): condition == null");
            if (condition.IsInCollection) return Logger.WarnReturn(false, "AddCondition(): condition.IsInCollection");

            bool success = false;

            // Wrap this in a single iteration loop to be able to break out
            Handle handle = new(this, condition);

            do
            {
                // Do not add this condition if it's disabled by live tuning
                if (condition.ConditionPrototypeRef != PrototypeId.Invalid)
                {
                    ConditionPrototype conditionProto = condition.ConditionPrototype;
                    if (conditionProto != null && LiveTuningManager.GetLiveConditionTuningVar(conditionProto, ConditionTuningVar.eCTV_Enabled) == 0f)
                        break;
                }

                // Check stacking limits for power conditions
                int stackCount = 0;

                StackingBehaviorPrototype stackingBehaviorProto = condition.GetStackingBehaviorPrototype();
                if (stackingBehaviorProto != null)
                {
                    condition.CacheStackId();
                    stackCount = GetNumberOfStacks(condition);

                    // Do not add this condition if the maximum number of stacks has been reached
                    if (stackCount >= stackingBehaviorProto.MaxNumStacks)
                        break;
                }

                stackCount++;

                // Do the insertion
                if (InsertCondition(condition) == false)
                {
                    Logger.Warn("AddCondition(): InsertCondition(condition) == false");
                    break;
                }    

                condition.ResetStartTime();

                // Notify interested clients if any
                PlayerConnectionManager networkManager = _owner.Game.NetworkManager;

                List<PlayerConnection> interestedClientList = ListPool<PlayerConnection>.Instance.Get();
                if (networkManager.GetInterestedClients(interestedClientList, _owner))
                {
                    NetMessageAddCondition addConditionMessage = ArchiveMessageBuilder.BuildAddConditionMessage(_owner, condition);
                    networkManager.SendMessageToMultiple(interestedClientList, addConditionMessage);
                }

                ListPool<PlayerConnection>.Instance.Return(interestedClientList);

                OnInsertCondition(condition);

                // Make sure the condition still exists after all OnInsertCondition logic occurs
                condition = handle.Get();
                if (condition == null)
                    break;

                success = true;
                //Logger.Trace($"AddCondition(): {condition} - {condition.Duration.TotalMilliseconds} ms");

                condition.Properties.Bind(_owner, AOINetworkPolicyValues.AllChannels);

                // Trigger additional effects
                WorldEntity creator = _owner.Game.EntityManager.GetEntity<WorldEntity>(condition.CreatorId);
                PowerPrototype powerProto = condition.CreatorPowerPrototype;

                // Power Events
                if (creator != null && powerProto != null)
                {
                    Power power = creator.GetPower(powerProto.DataRef);
                    power?.HandleTriggerPowerEventOnStackCount(_owner, stackCount);
                }

                // Procs
                if (handle.Valid())
                    _owner.TryActivateOnConditionStackCountProcs(condition);

                if (handle.Valid() && stackCount == 1 && condition.IsANegativeStatusEffect())
                    _owner.OnNegativeStatusEffectApplied(condition.Id);

                // Check stacking behavior
                if (powerProto != null)
                {
                    if (stackingBehaviorProto == null)
                    {
                        Logger.Warn("AddCondition(): stackingBehaviorProto == null");
                        break;
                    }

                    if (stackingBehaviorProto.RemoveStackOnMaxNumStacksReached && stackCount >= stackingBehaviorProto.MaxNumStacks && handle.Valid())
                        RemoveStack(condition.StackId);
                }
            } while (false);

            if (handle.Valid() && condition.IsInCollection == false)
            {
                // Clean up the condition we failed to add
                WorldEntity creator = _owner.Game.EntityManager.GetEntity<WorldEntity>(condition.CreatorId);
                PrototypeId creatorPowerProtoRef = condition.CreatorPowerPrototypeRef;
                if (creator != null && creatorPowerProtoRef != PrototypeId.Invalid)
                {
                    Power power = creator.GetPower(creatorPowerProtoRef);
                    power?.UntrackCondition(_owner.Id, condition);
                }

                DeleteCondition(condition);
            }

            return success;
        }

        public bool RefreshCondition(ulong conditionId, ulong creatorId, TimeSpan durationDelta = default)
        {
            Condition condition = GetCondition(conditionId);
            if (condition == null) return Logger.WarnReturn(false, "RefreshCondition(): condition == null");

            Game game = _owner.Game;
            if (game == null) return Logger.WarnReturn(false, "RefreshCondition(): game == null");

            // Modify duration if needed
            if (durationDelta != default)
            {
                // Do not allow duration to be reduced to zero because that would make it infinite
                long newDurationMS = (long)(condition.TimeRemaining + durationDelta).TotalMilliseconds;
                condition.SetDuration(Math.Max(newDurationMS, 1));
            }

            // Do the refresh
            bool conditionIsActive = false;

            condition.ResetStartTime();
            condition.PauseTime = TimeSpan.Zero;

            if (condition.ShouldStartPaused(_owner.Region))
            {
                PauseCondition(condition, true);

                if (condition.IsPauseDurationCountdown() && condition.CreatorId != creatorId)
                {
                    WorldEntity creator = game.EntityManager.GetEntity<WorldEntity>(creatorId);
                    TryRestorePowerCondition(condition, creator);
                }
            }
            else
            {
                CancelScheduledConditionEnd(condition);
                Handle handle = new(this, condition);
                UpdateTicker(condition);
                conditionIsActive = handle.Valid() && ScheduleConditionEnd(condition);
            }

            // Notify the owner player if needed
            // NOTE: We don't need to notify other players because condition time is visual only client-side.
            Player player = _owner.GetOwnerOfType<Player>();
            if (player != null && player.InterestedInEntity(_owner, AOINetworkPolicyValues.AOIChannelOwner))
            {
                player.SendMessage(NetMessageChangeConditionDuration.CreateBuilder()
                    .SetIdEntity(_owner.Id)
                    .SetKey(condition.Id)
                    .SetDuration((long)condition.Duration.TotalMilliseconds)
                    .SetStartTime((ulong)condition.StartTime.TotalMilliseconds)
                    .Build());
            }

            return conditionIsActive;
        }

        public bool RemoveCondition(ulong conditionId)
        {
            if (_currentConditions.TryGetValue(conditionId, out Condition condition) == false)
                return false;

            return RemoveCondition(condition);
        }

        public bool ResetOrRemoveCondition(ulong conditionId)
        {
            if (_owner == null) return Logger.WarnReturn(false, "ResetOrRemoveCondition(): _owner == null");

            Condition condition = GetCondition(conditionId);
            if (condition == null) return false;

            return ResetOrRemoveCondition(condition);
        }

        public bool ResetOrRemoveCondition(Condition condition)
        {
            // TODO: reset

            // Removing by id also checks to make sure this condition is in this collection
            RemoveCondition(condition.Id);
            return true;
        }

        public bool RemoveAllConditions(bool removePersistToDB = true)
        {
            if (_owner == null) return Logger.WarnReturn(false, "RemoveAllConditions(): _owner == null");
            if (_owner.Game == null) return Logger.WarnReturn(false, "RemoveAllConditions(): _owner.Game == null");

            foreach (Condition condition in this)
            {
                if (removePersistToDB == false && condition.IsPersistToDB())
                    continue;
                
                RemoveCondition(condition);
            }

            return true;
        }

        public bool PauseCondition(Condition condition, bool notifyClient)
        {
            Logger.Debug($"PauseCondition(): {condition}");

            if (condition.IsPaused)
                return Logger.WarnReturn(false, $"PauseCondition(): Condition [{condition}] is already paused");

            CancelScheduledConditionEnd(condition);

            condition.PauseTime = Game.Current.CurrentTime;

            UpdateTicker(condition);

            if (notifyClient)
                SendConditionPauseTimeMessage(condition);

            return true;
        }

        public bool UnpauseCondition(Condition condition, bool notifyClient)
        {
            Logger.Debug($"UnpauseCondition(): {condition}");

            if (condition.IsPaused == false)    // return true to indicate that condition wasn't removed
                return Logger.WarnReturn(true, $"UnpauseCondition(): Condition [{condition}] is not paused");

            condition.ResetStartTimeFromPaused();
            condition.PauseTime = TimeSpan.Zero;

            if (ScheduleConditionEnd(condition) == false)
                return Logger.WarnReturn(false, $"UnpauseCondition(): Failed to reschedule paused condition [{condition}]");

            UpdateTicker(condition);

            if (notifyClient)
                SendConditionPauseTimeMessage(condition);

            return true;
        }

        /// <summary>
        /// Enables or disables all conditions created by the specified power.
        /// </summary>
        public bool EnablePowerConditions(PrototypeId powerProtoRef, bool enable)
        {
            bool success = true;

            foreach (Condition condition in this)
            {
                if (condition.CreatorPowerPrototypeRef != powerProtoRef)
                    continue;

                success &= EnableCondition(condition, enable);
            }

            return success;
        }

        /// <summary>
        /// Attempts to readd a condition to the <see cref="Power"/> that created it.
        /// Returns <see langword="false"/> if condition is no longer valid.
        /// </summary>
        public bool TryRestorePowerCondition(Condition condition, WorldEntity owner)
        {
            if (owner == null)
                return false;

            if (condition.CreatorPowerPrototypeRef == PrototypeId.Invalid)
                return false;

            // Restore tracking if the power is still assigned and can be used
            Power power = owner.PowerCollection?.GetPower(condition.CreatorPowerPrototypeRef);
            if (power != null)
            {
                if (power.CanBeUsedInRegion(owner.Region) == false)
                    return false;

                // Trying to figure out if this orphan condition bug is a data issue with a specific power (DiamondFormCondition) or a more broad issue
                if (power.IsToggled() && power.IsToggledOn() == false && power.PrototypeDataRef != (PrototypeId)17994345800984565974)
                    Logger.Warn($"TryRestorePowerCondition(): Toggled power is off, but has an active condition! power=[{power}]");

                if (power.IsTrackingCondition(owner.Id, condition) == false)
                    power.TrackCondition(owner.Id, condition);

                return true;
            }

            // Sometimes the condition should remain even if the power that created it is gone
            PowerPrototype powerProto = condition.CreatorPowerPrototype;
            if (powerProto == null) return Logger.WarnReturn(false, "TryRestorePowerCondition(): powerProto == null");

            // Consumable items that grant conditions (e.g. boosts)
            if (Power.IsItemPower(powerProto))
                return true;

            // Powers that do not cancel their conditions when they are gone
            if (powerProto.Activation != PowerActivationType.Passive && powerProto.CancelConditionsOnEnd == false && powerProto.CancelConditionsOnUnassign == false)
                return true;

            // Remove conditions in other cases
            return false;
        }

        public bool TransferConditionsFrom(ConditionCollection other)
        {
            if (other == null) return Logger.WarnReturn(false, "TransferConditionsFrom(): other == null");

            // Make sure both condition collections belong to the same player owner
            Player player = _owner.GetOwnerOfType<Player>();
            if (player == null) return Logger.WarnReturn(false, "TransferConditionsFrom(): player == null");

            Player otherPlayer = other._owner.GetOwnerOfType<Player>();
            if (otherPlayer == null) return Logger.WarnReturn(false, "TransferConditionsFrom(): otherPlayer == null");

            if (player != otherPlayer)
                return Logger.WarnReturn(false, $"TransferConditionsFrom(): Attempted to transfer conditions from [{otherPlayer}] to [{player}] ([{other._owner}] to [{_owner}])");

            // Transfer
            foreach (Condition condition in other)
            {
                if (condition.IsTransferToCurrentAvatar() == false)
                    continue;

                Condition conditionCopy = AllocateCondition();
                if (conditionCopy.InitializeFromOtherCondition(NextConditionId, condition, _owner))
                {
                    AddCondition(conditionCopy);
                }
                else
                {
                    Logger.Warn($"TransferConditionsFrom(): Failed to copy condition [{condition}] from [{other._owner}] to [{_owner}]");
                    DeleteCondition(conditionCopy);
                }

                other.RemoveCondition(condition.Id);
            }

            return true;
        }

        public bool HasANegativeStatusEffectCondition()
        {
            foreach (Condition condition in _currentConditions.Values)
            {
                if (condition != null && condition.IsANegativeStatusEffect())
                    return true;
            }

            return false;
        }

        public Iterator.Enumerator GetEnumerator()
        {
            return new Iterator(this, false).GetEnumerator();
        }

        public static Condition AllocateCondition()
        {
            return ConditionPool.Instance.Get();
        }

        public static bool DeleteCondition(Condition condition)
        {
            if (condition == null) return Logger.WarnReturn(false, "DeleteCondition(): condition == null");
            if (condition.IsInCollection) return Logger.WarnReturn(false, "DeleteCondition(): condition.IsInCollection");

            condition.Properties.Unbind();

            ConditionPool.Instance.Return(condition);

            return true;
        }

        public void OnOwnerEnteredWorld()
        {
            foreach (Condition condition in this)
            {
                StartTicker(condition);
                _owner?.UpdateProcEffectPowers(condition.Properties, true);
            }
        }

        public void OnOwnerExitedWorld()
        {
            foreach (Condition condition in this)
                StopTicker(condition);
        }

        public void OnOwnerSimulationStateChanged(bool isSimulated)
        {
            if (isSimulated)
            {
                foreach (Condition condition in this)
                    StartTicker(condition);
            }
            else
            {
                foreach (Condition condition in this)
                    StopTicker(condition);
            }
        }

        public void OnOwnerDeallocate()
        {
            _owner.Game.GameEventScheduler.CancelAllEvents(_pendingEvents);

            // We need to remove all conditions here to unbind their property collections.
            // If we don't do that, the garbage collector can't clean them and we end up with a memory leak.
            RemoveAllConditions();
        }

        private bool InsertCondition(Condition condition)
        {
            // NOTE: The client uses Handle here, but do we actually need it?

            if (condition == null) return Logger.WarnReturn(false, "InsertCondition(): condition == null");
            if (condition.Id == InvalidConditionId) return Logger.WarnReturn(false, "InsertCondition(): condition.Id == InvalidConditionId");
            if (condition.IsInCollection) return Logger.WarnReturn(false, "InsertCondition(): condition.IsInCollection");

            if (_currentConditions.TryAdd(condition.Id, condition) == false)
                return Logger.WarnReturn(false, $"InsertCondition(): Failed to insert condition id {condition.Id} for [{_owner}]");

            condition.Collection = this;
            return true;
        }

        private bool RemoveCondition(Condition condition)
        {
            if (_owner == null) return Logger.WarnReturn(false, "RemoveCondition(): _owner == null");
            if (condition == null) return false;

            //Logger.Trace($"RemoveCondition(): {condition}");

            CancelScheduledConditionEnd(condition);

            // Remove from the power if this is a power condition
            WorldEntity creator = _owner.Game.EntityManager.GetEntity<WorldEntity>(condition.CreatorId);
            PrototypeId creatorPowerProtoRef = condition.CreatorPowerPrototypeRef;
            if (creator != null && creatorPowerProtoRef != PrototypeId.Invalid)
            {
                Power power = creator.GetPower(creatorPowerProtoRef);
                power?.UntrackCondition(_owner.Id, condition);
            }

            // Trigger events
            _owner.OnConditionRemoved(condition);
            _owner.TryActivateOnConditionEndProcs(condition);

            Handle handle = new(this, condition);
            StopTicker(condition);

            // Check if stopping the ticker removed this condition
            if (handle.Valid() == false)
                return true;

            // Notify interested clients if any
            PlayerConnectionManager networkManager = _owner.Game.NetworkManager;

            List<PlayerConnection> interestedClientList = ListPool<PlayerConnection>.Instance.Get();
            if (networkManager.GetInterestedClients(interestedClientList, _owner))
            {
                var deleteConditionMessage = NetMessageDeleteCondition.CreateBuilder()
                    .SetIdEntity(_owner.Id)
                    .SetKey(condition.Id)
                    .Build();

                networkManager.SendMessageToMultiple(interestedClientList, deleteConditionMessage);
            }

            ListPool<PlayerConnection>.Instance.Return(interestedClientList);

            // Remove the condition
            if (condition.IsInCollection)
            {
                UnaccrueCondition(condition);

                // Early return if this condition was deleted by unaccruement
                if (handle.Valid() == false)
                    return true;

                if (_currentConditions.Remove(condition.Id) == false)
                    Logger.Warn($"RemoveCondition(): Failed to remove condition id {condition.Id}");

                condition.Collection = null;
                _version++;
            }

            DeleteCondition(condition);
            return true;
        }

        private bool OnInsertCondition(Condition condition)
        {
            if (condition == null) return Logger.WarnReturn(false, "OnInsertCondition(): condition == null");
            if (condition.Collection != this) return Logger.WarnReturn(false, "OnInsertCondition(): condition.Collection != this");

            Handle handle = new(this, condition);

            RebuildConditionKeywordsMask();

            OnPreAccrueCondition(condition);

            if (handle.Valid() == false)
                return true;

            condition.IsEnabled = true;
            condition.CacheStackId();

            if (_owner.UpdateProcEffectPowers(condition.Properties, true) == false)
                Logger.Warn($"OnInsertCondition(): UpdateProcEffectPowers failed when adding condition=[{condition}] owner=[{_owner}]");
            
            // Accrue properties from this condition on the owner (these are added and removed along with the condition)
            _owner.Properties.AddChildCollection(condition.Properties);

            if (handle.Valid() == false)
                return true;

            OnPostAccrueCondition(condition);
            return true;
        }

        private void OnPreAccrueCondition(Condition condition)
        {

        }

        private void OnPostAccrueCondition(Condition condition)
        {
            IncrementStackCountCache(condition);

            if (condition.ShouldStartPaused(_owner.Region))
            {
                // Pause straight away if it should be paused
                PauseCondition(condition, true);
            }
            else if (condition.IsPaused == false)
            {
                // Try to schedule this condition's end if it's not paused
                if (ScheduleConditionEnd(condition) == false)
                    return;
            }

            // Disable this condition if needed
            PrototypeId powerProtoRef = condition.CreatorPowerPrototypeRef;
            if (powerProtoRef != PrototypeId.Invalid && _owner.Properties[PropertyEnum.DisablePowerEffects, powerProtoRef])
                EnableCondition(condition, false);

            // Start the ticker if it wasn't disabled above
            if (condition.IsEnabled)
                StartTicker(condition);
        }

        private void UnaccrueCondition(Condition condition)
        {
            if (condition.Collection != this) Logger.Warn("UnaccrueCondition(): condition.Collection != this");

            Handle handle = new(this, condition);
            if (condition.IsEnabled)
            {
                // Disable the condition
                condition.IsEnabled = false;
                if (condition.Properties.RemoveFromParent(_owner.Properties) == false)
                    Logger.Warn($"RemoveFromParent FAILED owner=[{_owner}] condition=[{(handle.Valid() ? condition.ToString() : "INVALID")}]");
            }

            if (handle.Valid() == false)
                return;

            OnPostUnaccrueCondition(condition);

            if (_owner.IsDestroyed == false)
                _owner.RegisterForPendingPhysicsResolve();
        }

        private void OnPostUnaccrueCondition(Condition condition)
        {
            RebuildConditionKeywordsMask(condition.Id);
            DecrementStackCountCache(condition);
        }

        private bool EnableCondition(Condition condition, bool enable)
        {
            Logger.Debug($"EnableCondition(): {condition} = {enable} (owner={_owner})");

            PlayerConnectionManager networkManager = _owner.Game.NetworkManager;

            // Notify clients
            List<PlayerConnection> interestedClientList = ListPool<PlayerConnection>.Instance.Get();
            if (networkManager.GetInterestedClients(interestedClientList, _owner))
            {
                NetMessageEnableCondition enableConditionMessage = NetMessageEnableCondition.CreateBuilder()
                    .SetIdEntity(_owner.Id)
                    .SetKey(condition.Id)
                    .SetEnable(enable)
                    .Build();

                networkManager.SendMessageToMultiple(interestedClientList, enableConditionMessage);
            }

            ListPool<PlayerConnection>.Instance.Return(interestedClientList);

            // Enable/disable the condition
            if (enable && condition.IsEnabled == false)
            {
                condition.IsEnabled = true;

                if (_owner.Properties.AddChildCollection(condition.Properties) == false)
                    return Logger.WarnReturn(false, $"EnableCondition(): Failed to attach properties for condition=[{condition}], owner=[{_owner}])");

                StartTicker(condition);

            }
            else if (enable == false && condition.IsEnabled)
            {
                condition.IsEnabled = false;

                if (condition.Properties.RemoveFromParent(_owner.Properties) == false)
                    return Logger.WarnReturn(false, $"EnableCondition(): Failed to detach properties for condition=[{condition}], owner=[{_owner}])");

                StopTicker(condition);
            }

            return true;
        }

        private void StartTicker(Condition condition)
        {
            // TODO
            //Logger.Debug($"StartTicker(): {condition}");
        }

        private void StopTicker(Condition condition)
        {
            // TODO
            //Logger.Debug($"StopTicker(): {condition}");
        }

        private void UpdateTicker(Condition condition)
        {
            // TODO
            //Logger.Debug($"UpdateTicker(): {condition}");
        }

        private void SendConditionPauseTimeMessage(Condition condition)
        {
            // NOTE: We don't need to notify other players because condition time is visual only client-side.
            Player player = _owner.GetOwnerOfType<Player>();
            if (player != null && player.InterestedInEntity(_owner, AOINetworkPolicyValues.AOIChannelOwner))
            {
                player.SendMessage(NetMessageChangeConditionPauseTime.CreateBuilder()
                    .SetIdEntity(_owner.Id)
                    .SetKey(condition.Id)
                    .SetPauseTime((ulong)Game.GetTimeFromStart(condition.PauseTime))
                    .SetStartTime((ulong)condition.StartTime.TotalMilliseconds)
                    .Build());
            }
        }

        private bool ScheduleConditionEnd(Condition condition)
        {
            if (condition == null) return Logger.WarnReturn(false, "ScheduleConditionEnd(): condition == null");

            if (condition.Duration > TimeSpan.Zero)
            {
                TimeSpan timeRemaining = condition.TimeRemaining;
                if (timeRemaining <= TimeSpan.Zero)
                {
                    RemoveCondition(condition);
                    return false;
                }

                EventPointer<RemoveConditionEvent> removeEvent = new();
                condition.RemoveEvent = removeEvent;

                _owner.Game.GameEventScheduler.ScheduleEvent(removeEvent, timeRemaining, _pendingEvents);
                removeEvent.Get().Initialize(this, condition.Id);
            }

            return true;
        }

        private bool CancelScheduledConditionEnd(Condition condition)
        {
            if (condition == null) return Logger.WarnReturn(false, "CancelScheduledConditionEnd(): condition == null");

            EventPointer<RemoveConditionEvent> removeEvent = condition.RemoveEvent;

            if (removeEvent?.IsValid == true)
                _owner.Game.GameEventScheduler.CancelEvent(condition.RemoveEvent);

            return true;
        }

        private void RebuildConditionKeywordsMask(ulong conditionIdToSkip = InvalidConditionId)
        {
            ConditionKeywordsMask.Clear();
            foreach (Condition condition in _currentConditions.Values)
            {
                if (conditionIdToSkip != InvalidConditionId && condition.Id == conditionIdToSkip)
                    continue;

                GBitArray.Or(ConditionKeywordsMask, condition.GetKeywordsMask());
            }
        }

        private void IncrementStackCountCache(Condition condition)
        {
            // Non-power conditions cannot stack
            if (condition.CreatorPowerPrototype == null)
                return;

            StackId stackId = condition.StackId;

            if (_stackCountCache.TryGetValue(stackId, out int stackCount) == false)
                _stackCountCache.Add(stackId, 1);
            else
                _stackCountCache[stackId] = ++stackCount;
        }

        private void DecrementStackCountCache(Condition condition)
        {
            // Non-power conditions cannot stack
            if (condition.CreatorPowerPrototype == null)
                return;

            StackId stackId = condition.StackId;
            if (_stackCountCache.TryGetValue(stackId, out int stackCount) == false)
            {
                Logger.Warn($"DecrementStackCountCache(): Condition being removed but there is no cache entry for it! {stackId}");
                return;
            }

            stackCount--;
            
            if (stackCount <= 0)
            {
                if (stackCount < 0)
                    Logger.Warn("DecrementStackCountCache(): stackCount < 0");

                _stackCountCache.Remove(stackId);
            }
            else
            {
                _stackCountCache[stackId] = stackCount;
            }
        }

        /// <summary>
        /// Removes all <see cref="Condition"/> instances that match the provided <see cref="StackId"/> from this <see cref="ConditionCollection"/>.
        /// </summary>
        private void RemoveStack(in StackId stackId)
        {
            foreach (Condition condition in this)
            {
                if (condition.CanStackWith(stackId))
                    RemoveCondition(condition);
            }
        }

        private int GetPersistentConditionCount()
        {
            int count = 0;

            foreach (Condition condition in _currentConditions.Values)
            {
                if (condition.IsPersistToDB() == false)
                    continue;

                // Do some extra validation
                if (condition.ConditionPrototypeRef == PrototypeId.Invalid)
                    return Logger.WarnReturn(0, "GetPersistentConditionCount(): condition.ConditionPrototypeRef == PrototypeId.Invalid");

                if (condition.IsFinite == false)
                    return Logger.WarnReturn(0, "GetPersistentConditionCount(): condition.IsFinite == false");

                count++;
            }

            return count;
        }

        public readonly struct StackId : IEquatable<StackId>
        {
            public static readonly StackId Invalid = new(PrototypeId.Invalid, -1, Entity.InvalidId);

            public PrototypeId PrototypeRef { get; }    // ConditionPrototype or PowerPrototype
            public int CreatorPowerIndex { get; }
            public ulong CreatorId { get; }             // EntityId or PlayerGuid

            public StackId(PrototypeId prototypeRef, int creatorPowerIndex, ulong creatorId)
            {
                PrototypeRef = prototypeRef;
                CreatorPowerIndex = creatorPowerIndex;
                CreatorId = creatorId;
            }

            public override string ToString()
            {
                return $"PrototypeRef={PrototypeRef}, CreatorPowerIndex={CreatorPowerIndex}, CreatorId={CreatorId}";
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(PrototypeRef, CreatorPowerIndex, CreatorId);
            }

            public override bool Equals(object obj)
            {
                if (obj is not StackId other)
                    return false;

                return Equals(other);
            }

            public bool Equals(StackId other)
            {
                return PrototypeRef == other.PrototypeRef && CreatorPowerIndex == other.CreatorPowerIndex && CreatorId == other.CreatorId;
            }

            public static bool operator ==(StackId left, StackId right)
            {
                return left.Equals(right);
            }

            public static bool operator !=(StackId left, StackId right)
            {
                return !(left == right);
            }
        }

        /// <summary>
        /// Represents a <see cref="Powers.Conditions.Condition"/> stored in a <see cref="ConditionCollection"/>.
        /// </summary>
        public struct Handle
        {
            public ConditionCollection Collection;
            public Condition Condition;
            public uint CollectionVersion;
            public ulong ConditionId;

            public Handle(ConditionCollection collection, Condition condition)
            {
                Collection = collection;
                Condition = condition;
                CollectionVersion = collection._version;
                ConditionId = condition != null ? condition.Id : InvalidConditionId;
            }

            public bool Valid()
            {
                if (CollectionVersion != Collection._version)
                {
                    if (Collection._currentConditions.ContainsKey(ConditionId) == false)
                        return false;

                    CollectionVersion = Collection._version;
                }

                return true;
            }

            public Condition Get()
            {
                if (Collection == null) Logger.WarnReturn(false, "Handle.Get(): Collection == null");

                if (CollectionVersion != Collection._version)
                {
                    if (Collection._currentConditions.TryGetValue(ConditionId, out Condition condition) == false)
                        return null;

                    CollectionVersion = Collection._version;
                    Condition = condition;
                }

                return Condition;
            }
        }

        /// <summary>
        /// Iterates <see cref="Condition"/> instances contained in a <see cref="ConditionCollection"/>, skipping disabled ones if specified.
        /// Supports removing instances from the collection during iteration.
        /// </summary>
        public readonly struct Iterator
        {
            private readonly ConditionCollection _collection;
            private readonly bool _skipDisabled;

            public Iterator(ConditionCollection collection, bool skipDisabled)
            {
                _collection = collection;
                _skipDisabled = skipDisabled;
            }

            public Enumerator GetEnumerator()
            {
                return new(_collection, _skipDisabled);
            }

            public struct Enumerator : IEnumerator<Condition>
            {
                private readonly ConditionCollection _collection;
                private readonly bool _skipDisabled;

                private ulong _currentConditionId;
                private uint _currentCollectionVersion;
                private SortedDictionary<ulong, Condition>.ValueCollection.Enumerator _currentEnumerator;

                public Condition Current { get => GetCurrent(); }
                object IEnumerator.Current { get => Current; }

                public Enumerator(ConditionCollection collection, bool skipDisabled)
                {
                    _collection = collection;
                    _skipDisabled = skipDisabled;

                    _currentConditionId = InvalidConditionId;
                    _currentCollectionVersion = collection._version;
                    _currentEnumerator = collection._currentConditions.Values.GetEnumerator();
                }

                public bool MoveNext()
                {
                    // Reset the current enumerator if the collection we are iterating has changed
                    if (_currentCollectionVersion != _collection._version)
                    {
                        _currentEnumerator.Dispose();   // We don't really need this, but calling just in case something changes in the SortedDictionary
                        _currentEnumerator = UpperBound(_currentConditionId);
                        _currentCollectionVersion = _collection._version;
                    }
                    else
                    {
                        _currentEnumerator.MoveNext();
                    }

                    AdvanceToEnabledCondition();
                    return Current != null;
                }

                public void Reset()
                {
                    _currentConditionId = InvalidConditionId;
                    _currentCollectionVersion = _collection._version;
                    _currentEnumerator = _collection._currentConditions.Values.GetEnumerator();
                }

                public void Dispose()
                {
                    _currentEnumerator.Dispose();
                }

                private Condition GetCurrent()
                {
                    // Find the current condition again if the collection we are iterating has changed
                    if (_currentCollectionVersion != _collection._version)
                    {
                        _currentEnumerator.Dispose();   // We don't really need this, but calling just in case something changes in the SortedDictionary
                        _currentEnumerator = Find(_currentConditionId);
                        if (_currentEnumerator.Current != null)
                            _currentCollectionVersion = _collection._version;
                    }

                    return _currentEnumerator.Current;
                }

                private void AdvanceToEnabledCondition()
                {
                    if (_skipDisabled)
                    {
                        // If the current condition is not enabled, move until we find one or reach the end.
                        Condition condition = _currentEnumerator.Current;
                        while (condition != null)
                        {
                            if (condition.IsEnabled)
                            {
                                _currentConditionId = condition.Id;
                                return;
                            }

                            _currentEnumerator.MoveNext();
                            condition = _currentEnumerator.Current;
                        }
                    }
                    else
                    {
                        // Just record the id for the current condition if we are not skipping disabled ones
                        Condition condition = _currentEnumerator.Current;
                        if (condition != null)
                        {
                            _currentConditionId = condition.Id;
                            return;
                        }
                    }

                    // Clear the current condition id if we have reached the end
                    _currentConditionId = InvalidConditionId;
                }

                // Helper methods to imitate C++ iterator behavior

                /// <summary>
                /// Returns a new enumerator positioned at the specified condition, or <see langword="null"/> if it is no longer present in the collection.
                /// </summary>
                private readonly SortedDictionary<ulong, Condition>.ValueCollection.Enumerator Find(ulong conditionId)
                {
                    SortedDictionary<ulong, Condition>.ValueCollection.Enumerator enumerator = _collection._currentConditions.Values.GetEnumerator();

                    do
                    {
                        enumerator.MoveNext();
                    } while (enumerator.Current != null && enumerator.Current.Id != conditionId);

                    return enumerator;
                }

                /// <summary>
                /// Returns a new enumerator positioned at the condition after the specified one, or <see langword="null"/> if there are no more conditions.
                /// </summary>
                private readonly SortedDictionary<ulong, Condition>.ValueCollection.Enumerator UpperBound(ulong conditionId)
                {
                    SortedDictionary<ulong, Condition>.ValueCollection.Enumerator enumerator = _collection._currentConditions.Values.GetEnumerator();

                    do
                    {
                        enumerator.MoveNext();
                    } while (enumerator.Current != null && enumerator.Current.Id <= conditionId);

                    return enumerator;
                }
            }

        }

        public class RemoveConditionEvent : CallMethodEventParam1<ConditionCollection, ulong>
        {
            protected override CallbackDelegate GetCallback() => (t, p1) => t.RemoveCondition(p1);
        }
    }
}
