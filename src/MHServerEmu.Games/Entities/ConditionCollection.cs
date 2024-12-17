﻿using System.Collections;
using Gazillion;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Serialization;
using MHServerEmu.Games.Common;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.Events.Templates;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.LiveTuning;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Network;
using MHServerEmu.Games.Powers;

namespace MHServerEmu.Games.Entities
{
    public class ConditionCollection : ISerialize
    {
        public const int MaxConditions = 256;
        public const ulong InvalidConditionId = 0;

        private static readonly Logger Logger = LogManager.CreateLogger();

        private static int ConditionCount = 0;  // test variable to make sure we delete all conditions we allocate

        private readonly WorldEntity _owner;

        private readonly SortedDictionary<ulong, Condition> _currentConditions = new();
        private readonly EventGroup _pendingEvents = new();

        private uint _version = 0;
        private ulong _nextConditionId = 1;

        public ulong NextConditionId { get => _nextConditionId++; }

        public KeywordsMask ConditionKeywordsMask { get; internal set; }

        public ConditionCollection(WorldEntity owner)
        {
            _owner = owner;
        }

        public bool Serialize(Archive archive)
        {
            bool success = true;

            // TODO: Persistent serialization
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

            return success;
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
            throw new NotImplementedException();
        }

        public int GetNumberOfStacks(StackId stackId)
        {
            throw new NotImplementedException();
        }

        public bool AddCondition(Condition condition)
        {
            if (condition == null) return Logger.WarnReturn(false, "AddCondition(): condition == null");
            if (condition.IsInCollection) return Logger.WarnReturn(false, "AddCondition(): condition.IsInCollection");

            Logger.Debug($"AddCondition(): {condition.CreatorPowerPrototype} {condition.ConditionPrototype}");

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

                // TODO: Stacking

                if (InsertCondition(condition) == false)
                {
                    Logger.Warn("AddCondition(): InsertCondition(condition) == false");
                    break;
                }    

                condition.ResetStartTime();

                // Notify interested clients if any
                var networkManager = _owner.Game.NetworkManager;
                var interestedClients = networkManager.GetInterestedClients(_owner);
                if (interestedClients.Any())
                {
                    NetMessageAddCondition addConditionMessage = ArchiveMessageBuilder.BuildAddConditionMessage(_owner, condition);
                    networkManager.SendMessageToMultiple(interestedClients, addConditionMessage);
                }

                OnInsertCondition(condition);

                // Make sure the condition still exists after all OnInsertCondition logic occurs
                condition = handle.Get();
                if (condition == null)
                    break;

                success = true;

                condition.Properties.Bind(_owner, AOINetworkPolicyValues.AllChannels);

                // TODO: Additional effects

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
            Logger.Debug($"ResetOrRemoveCondition(): {condition}");

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

        public bool HasANegativeStatusEffectCondition()
        {
            foreach (Condition condition in _currentConditions.Values)
                if (condition != null && condition.IsANegativeStatusEffect()) return true;
            return false;
        }

        public Iterator.Enumerator GetEnumerator()
        {
            return new Iterator(this, false).GetEnumerator();
        }

        public Condition AllocateCondition()
        {
            // TODO: Pooling
            ConditionCount++;
            Logger.Debug($"AllocateCondition(): ConditionCount={ConditionCount}");
            return new();
        }

        public bool DeleteCondition(Condition condition)
        {
            if (condition == null) return Logger.WarnReturn(false, "DeleteCondition(): condition == null");
            if (condition.IsInCollection) return Logger.WarnReturn(false, "DeleteCondition(): condition.IsInCollection");

            // TODO: Pooling
            ConditionCount--;

            condition.Properties.Unbind();
            return true;
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

            Logger.Debug($"RemoveCondition(): {condition.CreatorPowerPrototype} {condition.ConditionPrototype}");

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

            // Notify interested clients if any
            var networkManager = _owner.Game.NetworkManager;
            var interestedClients = networkManager.GetInterestedClients(_owner);
            if (interestedClients.Any())
            {
                var deleteConditionMessage = NetMessageDeleteCondition.CreateBuilder()
                    .SetIdEntity(_owner.Id)
                    .SetKey(condition.Id)
                    .Build();

                networkManager.SendMessageToMultiple(interestedClients, deleteConditionMessage);
            }

            // Remove the condition
            if (condition.IsInCollection)
            {
                Handle handle = new(this, condition);
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

        private void OnInsertCondition(Condition condition)
        {
            // TODO

            OnPreAccrueCondition(condition);

            OnPostAccrueCondition(condition);
        }

        private void OnPreAccrueCondition(Condition condition)
        {

        }

        private void OnPostAccrueCondition(Condition condition)
        {
            if (ScheduleConditionEnd(condition) == false)
                return;
        }

        private void UnaccrueCondition(Condition condition)
        {
            // TODO
            OnPostUnaccrueCondition(condition);
        }

        private void OnPostUnaccrueCondition(Condition condition)
        {

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

        public readonly struct StackId
        {
            public PrototypeId PrototypeRef { get; }    // ConditionPrototype or PowerPrototype
            public int CreatorPowerIndex { get; }
            public ulong CreatorId { get; }             // EntityId or PlayerGuid

            public StackId(PrototypeId prototypeRef, int creatorPowerIndex, ulong creatorId)
            {
                // See ConditionCollection::MakeConditionStackId()
                PrototypeRef = prototypeRef;
                CreatorPowerIndex = creatorPowerIndex;
                CreatorId = creatorId;
            }
        }

        /// <summary>
        /// Represents a <see cref="Powers.Condition"/> stored in a <see cref="ConditionCollection"/>.
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
