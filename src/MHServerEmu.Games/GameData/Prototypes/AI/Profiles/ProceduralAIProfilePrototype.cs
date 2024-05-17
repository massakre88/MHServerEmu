﻿using MHServerEmu.Core.Collections;
using MHServerEmu.Core.Collisions;
using MHServerEmu.Core.Extensions;
using MHServerEmu.Core.System.Random;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.Behavior;
using MHServerEmu.Games.Behavior.ProceduralAI;
using MHServerEmu.Games.Behavior.StaticAI;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Entities.Locomotion;
using MHServerEmu.Games.Entities.PowerCollections;
using MHServerEmu.Games.Generators;
using MHServerEmu.Games.Generators.Population;
using MHServerEmu.Games.Navi;
using MHServerEmu.Games.Properties;
using MHServerEmu.Games.Regions;

namespace MHServerEmu.Games.GameData.Prototypes
{
    public class ProceduralAIProfilePrototype : BrainPrototype
    {
        public static StaticBehaviorReturnType HandleContext<TContextProto>(ProceduralAI proceduralAI, AIController ownerController,
            TContextProto contextProto, ProceduralContextPrototype proceduralContext = null)
            where TContextProto : Prototype
        {
            (IAIState instance, IStateContext context) = IStateContext.Create(ownerController, contextProto);
            return proceduralAI.HandleContext(instance, ref context, proceduralContext);
        }

        public static bool HandleMovementContext<TContextProto>(ProceduralAI proceduralAI, AIController ownerController, 
            Locomotor locomotor, TContextProto contextProto, bool checkPower, out StaticBehaviorReturnType movementResult, ProceduralContextPrototype proceduralContext = null)
             where TContextProto : Prototype
        {
            movementResult = StaticBehaviorReturnType.None;
            if (locomotor == null)
            {
                ProceduralAI.Logger.Warn($"Can't move without a locomotor! {locomotor}");
                return false;
            }
            (IAIState instance, IStateContext context) = IStateContext.Create(ownerController, contextProto);
            movementResult = proceduralAI.HandleContext(instance, ref context, proceduralContext);
            if (ResetTargetAndStateIfPathFails(proceduralAI, ownerController, locomotor, ref context, checkPower))
                return false;
            return true;
        }

        protected virtual StaticBehaviorReturnType HandleUsePowerContext(AIController ownerController, ProceduralAI proceduralAI, GRandom random,
            long currentTime, UsePowerContextPrototype powerContext, ProceduralContextPrototype proceduralContext = null)
        {
            return HandleContext(proceduralAI, ownerController, powerContext, proceduralContext);
        }

        private static bool ResetTargetAndStateIfPathFails(ProceduralAI proceduralAI, AIController ownerController, Locomotor locomotor, 
            ref IStateContext context, bool checkPower)
        {
            Agent owner = ownerController.Owner;
            if (owner == null) return false;
            if (locomotor == null)
            {
                ProceduralAI.Logger.Warn($"Agent [{owner}] doesn't have a locomotor and should not be calling this function");
                return false;
            }

            if (locomotor.LastGeneratedPathResult == NaviPathResult.FailedNoPathFound)
            {
                bool resetTarget = true;
                if (checkPower) resetTarget = proceduralAI.LastPowerResult == StaticBehaviorReturnType.Failed;
                if (resetTarget) ownerController.ResetCurrentTargetState();
                proceduralAI.SwitchProceduralState(null, context, StaticBehaviorReturnType.Failed);
                return true;
            }

            return false;
        }

        public static bool ValidateContext(ProceduralAI proceduralAI, AIController ownerController, UsePowerContextPrototype contextProto)
        {
            IStateContext context = new UsePowerContext(ownerController, contextProto);
            return proceduralAI.ValidateContext(UsePower.Instance, ref context);
        }

        protected static bool ValidateUsePowerContext(AIController ownerController, ProceduralAI proceduralAI, UsePowerContextPrototype powerContext)
        {
            return ValidateContext(proceduralAI, ownerController, powerContext);
        }

        public bool HandleOverrideBehavior(AIController ownerController)
        {
            ProceduralAI proceduralAI = ownerController.Brain;
            if (proceduralAI == null) return false;

            ProceduralAIProfilePrototype fullOverrideBehavior = proceduralAI.FullOverrideBehavior;
            if (fullOverrideBehavior != null && fullOverrideBehavior.GetType() != GetType())
            {
                fullOverrideBehavior.Think(ownerController);
                if (ownerController.IsOwnerValid() == false) return true;
                return proceduralAI.FullOverrideBehavior != null;
            }
            return false;
        }

        protected static void HandleEnticerBehaviorResultStatus(Game game, BehaviorBlackboard blackboard, bool completed)
        {
            ulong enticerId = blackboard.PropertyCollection[PropertyEnum.AIEnticedToID];
            Agent enticer = game.EntityManager.GetEntity<Agent>(enticerId);
            if (enticer != null)
            {
                AIController enticerController = enticer.AIController;
                if (enticerController == null) return;
                if (enticerController.Brain is not ProceduralAI enticersBrain) return;
                if (enticersBrain.Behavior is not ProceduralProfileEnticerPrototype enticersProfile) return;
                enticersProfile.HandleEnticementBehaviorCompletion(enticerController, completed);
            }
            blackboard.PropertyCollection.RemoveProperty(PropertyEnum.AIFullOverride);
            blackboard.PropertyCollection.RemoveProperty(PropertyEnum.AIEnticedToID);
        }

        public virtual void Think(AIController ownerController)
        {
            ProceduralAI.Logger.Error("ProceduralAIProfilePrototype.THINK() - BASE CLASS SHOULD NOT BE CALLED");
        }

        public virtual void Init(Agent agent){ }

        protected static void InitPowers(Agent agent, ProceduralUsePowerContextPrototype[] proceduralPowers)
        {
            if (proceduralPowers.HasValue())
                foreach(var proceduralPower in proceduralPowers)
                    InitPower(agent, proceduralPower);
        }

        protected static void InitPowers(Agent agent, PrototypeId[] powers)
        {
            if (powers.HasValue())
                foreach (var power in powers)
                    InitPower(agent, power);
        }

        protected static void InitPower(Agent agent, ProceduralUsePowerContextPrototype proceduralPower)
        {
            InitPower(agent, proceduralPower?.PowerContext);
        }

        protected static void InitPower(Agent agent, UsePowerContextPrototype powerContext)
        {
            if (powerContext == null) return;
            InitPower(agent, powerContext.Power);
        }

        protected static void InitPower(Agent agent, PrototypeId power)
        {
            if (power == PrototypeId.Invalid) return;
            if (agent.HasPowerInPowerCollection(power) == false)
            {
                var indexPowerProps = new PowerIndexProperties(agent.CharacterLevel, agent.CombatLevel, agent.Properties[PropertyEnum.PowerRank]);
                // TODO PropertyEnum.AILOSMaxPowerRadius
                agent.AssignPower(power, indexPowerProps);
            }
        }

        public virtual void OnOwnerExitWorld(AIController ownerController) { }
        public virtual void OnOwnerKilled(AIController ownerController) { }
        public virtual void ProcessInterrupts(AIController ownerController, BehaviorInterruptType interrupt) { }
    }

    public class ProceduralProfileEnticerPrototype : ProceduralAIProfilePrototype
    {
        public int CooldownMinMS { get; protected set; }
        public int CooldownMaxMS { get; protected set; }
        public int EnticeeEnticerCooldownMaxMS { get; protected set; }
        public int EnticeeEnticerCooldownMinMS { get; protected set; }
        public int EnticeeGlobalEnticerCDMaxMS { get; protected set; }
        public int EnticeeGlobalEnticerCDMinMS { get; protected set; }
        public int MaxSubscriptions { get; protected set; }
        public int MaxSubscriptionsPerActivation { get; protected set; }
        public float Radius { get; protected set; }
        public AIEntityAttributePrototype[] EnticeeAttributes { get; protected set; }
        public PrototypeId EnticedBehavior { get; protected set; }

        public override void Think(AIController ownerController)
        {
            ProceduralAI proceduralAI = ownerController.Brain;
            if (proceduralAI == null) return;
            Agent agent = ownerController.Owner;
            if (agent == null) return;
            Game game = agent.Game;
            if (game == null) return;
            long currentTime = (long)game.GetCurrentTime().TotalMilliseconds;

            BehaviorBlackboard blackboard = ownerController.Blackboard;
            GRandom random = game.Random;

            int subscriptions = blackboard.PropertyCollection[PropertyEnum.AICustomStateVal1];
            int availableSubscriptions = Math.Min(MaxSubscriptionsPerActivation, MaxSubscriptions - subscriptions);
            int subscribed = 0;

            List<WorldEntity> potentialTargets = Combat.GetTargetsInRange(agent, Radius, 0.0f, CombatTargetType.Ally, CombatTargetFlags.IgnoreHostile, EnticeeAttributes);
            foreach (WorldEntity potentialTarget in potentialTargets)
            {
                if (potentialTarget == null) return;
                if (Subscribe(potentialTarget, agent, random, currentTime))
                {
                    availableSubscriptions--;
                    subscribed++;
                    if (availableSubscriptions <= 0)
                        break;
                }
            }

            blackboard.PropertyCollection.AdjustProperty(subscribed, PropertyEnum.AICustomStateVal1);

            if (MaxSubscriptions > 0 && (subscriptions + subscribed) >= MaxSubscriptions)
            {
                ownerController.SetIsEnabled(false);
                return;
            }

            if (subscribed > 0)
            {
                ownerController.ClearScheduledThinkEvent();
                ownerController.ScheduleAIThinkEvent(TimeSpan.FromMilliseconds(random.Next(CooldownMinMS, CooldownMaxMS)));
            }
        }

        public void HandleEnticementBehaviorCompletion(AIController enticerController, bool completed)
        {
            if (completed == false)
            {
                enticerController.Blackboard.PropertyCollection.AdjustProperty(-1, PropertyEnum.AICustomStateVal1);
                if (enticerController.IsEnabled == false)
                    enticerController.SetIsEnabled(true);
            }
        }

        private bool Subscribe(WorldEntity subscriber, Agent enticed, GRandom random, long currentTime)
        {
            if (subscriber is not Agent subscriberAgent || subscriberAgent.IsExecutingPower) return false;

            AIController controller = subscriberAgent.AIController;
            if (controller == null) return false;
                
            var collection = controller.Blackboard.PropertyCollection;
            if (collection.HasProperty(PropertyEnum.AIEnticedToID)) return false;
            
            long globalNextAvailableTime = collection[PropertyEnum.AIEnticerGlobalNextAvailableTime];
            if (globalNextAvailableTime > 0 && currentTime < globalNextAvailableTime) return false;
            
            long nextAvailableTime = collection[PropertyEnum.AIEnticerTypeNextAvailableTime, enticed.PrototypeDataRef];
            if (nextAvailableTime > 0 && currentTime < nextAvailableTime) return false;
            
            collection[PropertyEnum.AIEnticedToID] = enticed.Id;
            collection[PropertyEnum.AIFullOverride] = EnticedBehavior;

            globalNextAvailableTime = currentTime + random.Next(EnticeeGlobalEnticerCDMinMS, EnticeeGlobalEnticerCDMaxMS);
            collection[PropertyEnum.AIEnticerGlobalNextAvailableTime] = globalNextAvailableTime;

            nextAvailableTime = currentTime + random.Next(EnticeeEnticerCooldownMinMS, EnticeeEnticerCooldownMaxMS);
            collection[PropertyEnum.AIEnticerTypeNextAvailableTime, enticed.PrototypeDataRef] = nextAvailableTime;
            return true;
        }
    }

    public class ProceduralProfileEnticedBehaviorPrototype : ProceduralAIProfilePrototype
    {
        public FlankContextPrototype FlankToEnticer { get; protected set; }
        public MoveToContextPrototype MoveToEnticer { get; protected set; }
        public PrototypeId DynamicBehavior { get; protected set; }
        public bool OrientToEnticerOrientation { get; protected set; }

        public override void Think(AIController ownerController)
        {
            ProceduralAI proceduralAI = ownerController.Brain;
            if (proceduralAI == null) return;
            Agent agent = ownerController.Owner;
            if (agent == null) return;
            Game game = agent.Game;
            if (game == null) return;

            bool notCompleted = false;
            if (MoveToEnticer != null)
            {
                StaticBehaviorReturnType interactResult = HandleContext(proceduralAI, ownerController, MoveToEnticer);
                if (interactResult == StaticBehaviorReturnType.Running) return;
                notCompleted = interactResult != StaticBehaviorReturnType.Completed;
            } 
            else if (FlankToEnticer != null)
            {
                StaticBehaviorReturnType interactResult = HandleContext(proceduralAI, ownerController, FlankToEnticer);
                if (interactResult == StaticBehaviorReturnType.Running) return;
                notCompleted = interactResult != StaticBehaviorReturnType.Completed;
            }

            BehaviorBlackboard blackboard = ownerController.Blackboard;
            if (notCompleted)
                HandleEnticerBehaviorResultStatus(game, blackboard, false);
            else
            {
                if (OrientToEnticerOrientation && agent.CanRotate)
                {
                    Locomotor locomotor = agent.Locomotor;
                    if (locomotor != null)
                    {
                        ulong enticerId = blackboard.PropertyCollection[PropertyEnum.AIEnticedToID];
                        WorldEntity enticer = game.EntityManager.GetEntity<WorldEntity>(enticerId);
                        if (enticer == null) return;
                        locomotor.LookAt(enticer.Forward + agent.RegionLocation.Position);
                    }
                }
                blackboard.PropertyCollection[PropertyEnum.AIFullOverride] = DynamicBehavior;
            }
        }
    }

    public class ProceduralProfileInteractEnticerOverridePrototype : ProceduralAIProfilePrototype
    {
        public InteractContextPrototype Interact { get; protected set; }

        public override void Think(AIController ownerController)
        {
            ProceduralAI proceduralAI = ownerController.Brain;
            if (proceduralAI == null) return;
            Agent agent = ownerController.Owner;
            if (agent == null) return;
            Game game = agent.Game;
            if (game == null) return;

            if (Interact == null) return;
            StaticBehaviorReturnType interactResult = HandleContext(proceduralAI, ownerController, Interact);
            if (interactResult == StaticBehaviorReturnType.Running) return;

            HandleEnticerBehaviorResultStatus(game, ownerController.Blackboard, interactResult == StaticBehaviorReturnType.Completed);
        }
    }

    public class ProceduralProfileUsePowerEnticerOverridePrototype : ProceduralProfileWithTargetPrototype
    {
        public UsePowerContextPrototype Power { get; protected set; }
        public new SelectEntityContextPrototype SelectTarget { get; protected set; }

        public override void Think(AIController ownerController)
        {
            ProceduralAI proceduralAI = ownerController.Brain;
            if (proceduralAI == null) return;
            Agent agent = ownerController.Owner;
            if (agent == null) return;
            Game game = agent.Game;
            if (game == null) return;

            if (Power == null) return;
            if (SelectTarget != null)
            {
                CombatTargetFlags flags = CombatTargetFlags.IgnoreHostile;
                WorldEntity target = ownerController.TargetEntity;
                SelectTargetEntity(agent, ref target, ownerController, proceduralAI, SelectTarget, CombatTargetType.Hostile, SelectTargetFlags.None, flags);
            }

            StaticBehaviorReturnType interactResult = HandleContext(proceduralAI, ownerController, Power);
            if (interactResult == StaticBehaviorReturnType.Running) return;

            HandleEnticerBehaviorResultStatus(game, ownerController.Blackboard, interactResult == StaticBehaviorReturnType.Completed);
        }
    }

    public class ProceduralProfileSenseOnlyPrototype : ProceduralAIProfilePrototype
    {
        public AIEntityAttributePrototype[] AttributeList { get; protected set; }
        public PrototypeId AllianceOverride { get; protected set; }

        public override void Think(AIController ownerController)
        {
            ProceduralAI proceduralAI = ownerController.Brain;
            if (proceduralAI == null) return;
            Agent agent = ownerController.Owner;
            if (agent == null) return;
            Game game = agent.Game;
            if (game == null) return;
            long currentTime = (long)game.GetCurrentTime().TotalMilliseconds;

            if (HandleOverrideBehavior(ownerController)) return;

            BehaviorBlackboard blackboard = ownerController.Blackboard;
            long thinkTime = blackboard.PropertyCollection[PropertyEnum.AICustomTimeVal1];
            if (thinkTime == 0)
            {
                blackboard.PropertyCollection[PropertyEnum.AICustomTimeVal1] = currentTime;
                return;
            }

            var allianceOverrideProto = AllianceOverride.As<AlliancePrototype>();
            AlliancePrototype alliance = allianceOverrideProto ?? agent.GetAlliancePrototype();

            float proximityRangeOverride = blackboard.PropertyCollection[PropertyEnum.AIProximityRangeOverride];
            float aggroRangeHostile = ownerController.AggroRangeHostile;
            float aggroRangeAlly = ownerController.AggroRangeAlly;

            bool enemyDetect = agent.CanEntityActionTrigger(EntitySelectorActionEventType.OnDetectedEnemy);
            bool enemyProximity = (proximityRangeOverride > aggroRangeHostile) && agent.CanEntityActionTrigger(EntitySelectorActionEventType.OnEnemyProximity);
            bool playerDetect = agent.CanEntityActionTrigger(EntitySelectorActionEventType.OnDetectedPlayer);
            bool playerProximity = (proximityRangeOverride > aggroRangeAlly) && agent.CanEntityActionTrigger(EntitySelectorActionEventType.OnPlayerProximity);
            bool friendDetect = agent.CanEntityActionTrigger(EntitySelectorActionEventType.OnDetectedFriend);

            float aggroRange = 0.0f;
            if (playerDetect || enemyDetect)
                aggroRange = Math.Max(aggroRange, aggroRangeHostile);
            if (playerDetect || friendDetect)
                aggroRange = Math.Max(aggroRange, aggroRangeAlly);

            float maxRange = Math.Max(aggroRange, proximityRangeOverride);
            if (maxRange == 0.0f) return;

            Region region = agent.Region;
            if (region == null || game.EntityManager == null) return;

            Vector3 position = agent.RegionLocation.Position;

            bool foundEnemy = false;
            bool foundEnemyProximity = false;
            bool foundPlayer = false;
            bool foundPlayerProximity = false;
            bool foundFriendlyEntity = false;

            var volume = new Sphere(position, maxRange);

            if ((playerDetect || playerProximity) 
                && enemyDetect == false && enemyProximity == false && friendDetect == false)
            {
                foreach (Avatar worldEntity in region.IterateAvatarsInVolume(volume))
                {
                    if (worldEntity == null || worldEntity.IsInWorld == false) continue;

                    if (playerDetect
                        && Combat.ValidTarget(game, agent, worldEntity, CombatTargetType.Ally, false, CombatTargetFlags.None, alliance, aggroRange)
                        && CheckAttributes(ownerController, AttributeList, worldEntity))
                        {
                            foundPlayer = true;
                            foundPlayerProximity = true;
                            break;
                        }

                    if (playerProximity && foundPlayerProximity == false
                        && Combat.ValidTarget(game, agent, worldEntity, CombatTargetType.Ally, false, CombatTargetFlags.None, alliance, proximityRangeOverride)
                        && CheckAttributes(ownerController, AttributeList, worldEntity))
                            foundPlayerProximity = true;
                }
            }
            else
            {
                foreach (WorldEntity worldEntity in region.IterateEntitiesInVolume(volume, new(EntityRegionSPContextFlags.ActivePartition)))
                {
                    if (worldEntity == null) continue;

                    if (enemyDetect && foundEnemy == false                    
                        && Combat.ValidTarget(game, agent, worldEntity, CombatTargetType.Hostile, false, CombatTargetFlags.CheckAgent, alliance, aggroRangeHostile)
                        && CheckAttributes(ownerController, AttributeList, worldEntity))
                        {
                            foundEnemy = true;
                            foundEnemyProximity = true;
                            break;
                        }                    

                    if (enemyProximity && foundEnemyProximity == false
                        && Combat.ValidTarget(game, agent, worldEntity, CombatTargetType.Hostile, false, CombatTargetFlags.CheckAgent, alliance, proximityRangeOverride)
                        && CheckAttributes(ownerController, AttributeList, worldEntity))
                            foundEnemyProximity = true;

                    if (foundEnemy == false && foundEnemyProximity == false)
                    {
                        if ((playerDetect || friendDetect) 
                            && (foundPlayer == false || foundFriendlyEntity == false)
                            && Combat.ValidTarget(game, agent, worldEntity, CombatTargetType.Ally, false, CombatTargetFlags.CheckAgent, alliance, aggroRangeAlly)
                            && CheckAttributes(ownerController, AttributeList, worldEntity))
                            {
                                if (worldEntity is Avatar)
                                {
                                    foundPlayer = true;
                                    foundPlayerProximity = true;
                                }
                                else
                                {
                                    foundFriendlyEntity = true;
                                }
                            }

                        if (playerProximity && foundPlayerProximity == false
                            && worldEntity is Avatar
                            && Combat.ValidTarget(game, agent, worldEntity, CombatTargetType.Ally, false, CombatTargetFlags.CheckAgent, alliance, proximityRangeOverride)
                            && CheckAttributes(ownerController, AttributeList, worldEntity))
                                foundPlayerProximity = true;
                    }
                }
            }

            if (foundEnemy)
                agent.TriggerEntityActionEvent(EntitySelectorActionEventType.OnDetectedEnemy);

            if (foundEnemyProximity)
                agent.TriggerEntityActionEvent(EntitySelectorActionEventType.OnEnemyProximity);

            if (foundEnemy == false && foundEnemyProximity == false)
            {
                if (foundPlayer)
                {
                    agent.TriggerEntityActionEvent(EntitySelectorActionEventType.OnDetectedPlayer);
                    SpawnGroup spawnGroup = agent.SpawnGroup;
                    if (spawnGroup != null && alliance != null)
                    {
                        var filterFlag = SpawnGroupEntityQueryFilterFlags.Allies | SpawnGroupEntityQueryFilterFlags.NotDeadDestroyedControlled;
                        if (spawnGroup.GetEntities(out List <WorldEntity> allies, filterFlag, agent.GetAlliancePrototype()))                        
                            foreach (var ally in allies)
                                if (ally != agent)
                                    ally.TriggerEntityActionEvent(EntitySelectorActionEventType.OnAllyDetectedPlayer);                        
                    }
                }

                if (foundPlayer == false && foundPlayerProximity)
                    agent.TriggerEntityActionEvent(EntitySelectorActionEventType.OnPlayerProximity);

                if (foundFriendlyEntity)
                    agent.TriggerEntityActionEvent(EntitySelectorActionEventType.OnDetectedFriend);
            }
        }

        private static bool CheckAttributes(AIController ownerController, AIEntityAttributePrototype[] attributeList, WorldEntity target)
        {
            Agent ownerAgent = ownerController.Owner;
            if (ownerAgent == null || target.IsInWorld == false) return false;

            if (attributeList.HasValue())
                foreach (AIEntityAttributePrototype attrib in attributeList)
                {
                    if (attrib == null) return false;
                    if (attrib.Check(ownerAgent, target)) return true;
                }

            return false;
        }
    }

    public class ProceduralProfileLeashOverridePrototype : ProceduralAIProfilePrototype
    {
        public PrototypeId LeashReturnHeal { get; protected set; }
        public PrototypeId LeashReturnImmunity { get; protected set; }
        public MoveToContextPrototype MoveToSpawn { get; protected set; }
        public TeleportContextPrototype TeleportToSpawn { get; protected set; }
        public PrototypeId LeashReturnTeleport { get; protected set; }
        public PrototypeId LeashReturnInvulnerability { get; protected set; }

        public override void Init(Agent agent)
        {
            base.Init(agent);
            InitPower(agent, LeashReturnHeal);
            InitPower(agent, LeashReturnImmunity);
            InitPower(agent, LeashReturnTeleport);
            InitPower(agent, LeashReturnInvulnerability);
        }

        private enum State
        {
            Default = 0,
            Move = 1,
            Teleport = 2
        }

        public override void Think(AIController ownerController)
        {
            ProceduralAI proceduralAI = ownerController.Brain;
            if (proceduralAI == null) return;
            Agent agent = ownerController.Owner;
            if (agent == null) return;

            if (agent.CanMove == false || agent.IsExecutingPower) return;

            var collection = ownerController.Blackboard.PropertyCollection;

            State state = (State)(int)collection[PropertyEnum.AICustomOverrideStateVal1];
            if (state == State.Default)
            {
                if (LeashReturnHeal != PrototypeId.Invalid 
                    && !ownerController.AttemptActivatePower(LeashReturnHeal, agent.Id, agent.RegionLocation.Position)) 
                    return;
                if (LeashReturnImmunity != PrototypeId.Invalid
                    && !ownerController.AttemptActivatePower(LeashReturnImmunity, agent.Id, agent.RegionLocation.Position)) 
                    return;
                if (LeashReturnInvulnerability != PrototypeId.Invalid
                    && !ownerController.AttemptActivatePower(LeashReturnInvulnerability, agent.Id, agent.RegionLocation.Position)) 
                    return;
            }

            if (state == State.Default || state == State.Move)
            {
                if (MoveToSpawn != null)
                {
                    StaticBehaviorReturnType moveResult = HandleContext(proceduralAI, ownerController, MoveToSpawn, null);
                    if (moveResult == StaticBehaviorReturnType.Running)
                    {
                        collection[PropertyEnum.AICustomOverrideStateVal1] = (int)State.Move;
                        return;
                    }
                    else if (moveResult == StaticBehaviorReturnType.Completed)
                    {
                        if (LeashReturnHeal != PrototypeId.Invalid
                            && !ownerController.AttemptActivatePower(LeashReturnHeal, agent.Id, agent.RegionLocation.Position))
                            return;

                        if (LeashReturnImmunity != PrototypeId.Invalid
                            && !ownerController.AttemptActivatePower(LeashReturnImmunity, agent.Id, agent.RegionLocation.Position))
                            return;

                        collection[PropertyEnum.AIIsLeashing] = false;
                        return;
                    }
                }

                if (LeashReturnTeleport != PrototypeId.Invalid)
                {
                    if (ownerController.AttemptActivatePower(LeashReturnTeleport, agent.Id, agent.RegionLocation.Position) == false)
                        return;

                    collection[PropertyEnum.AICustomOverrideStateVal1] = (int)State.Teleport;
                    return;
                }
            }

            if (state == State.Teleport && agent.ActivePowerRef == LeashReturnTeleport) return;

            Region agentsRegion = agent.Region;
            if (agentsRegion == null) return;

            if (LeashReturnHeal != PrototypeId.Invalid
                && !ownerController.AttemptActivatePower(LeashReturnHeal, agent.Id, agent.RegionLocation.Position))
                return;

            if (LeashReturnImmunity != PrototypeId.Invalid
                && !ownerController.AttemptActivatePower(LeashReturnImmunity, agent.Id, agent.RegionLocation.Position))
                return;

            if (LeashReturnInvulnerability != PrototypeId.Invalid
                && !ownerController.AttemptActivatePower(LeashReturnInvulnerability, agent.Id, agent.RegionLocation.Position))
                return;

            HandleContext(proceduralAI, ownerController, TeleportToSpawn, null);

            collection[PropertyEnum.AIIsLeashing] = false;
        }

    }

    public class ProceduralProfileRunToExitAndDespawnOverridePrototype : ProceduralAIProfilePrototype
    {
        public MoveToContextPrototype RunToExit { get; protected set; }
        public int NumberOfWandersBeforeDestroy { get; protected set; }
        public DelayContextPrototype DelayBeforeRunToExit { get; protected set; }
        public SelectEntityContextPrototype SelectPortalToExitFrom { get; protected set; }
        public DelayContextPrototype DelayBeforeDestroyOnMoveExitFail { get; protected set; }
        public bool VanishesIfMoveToExitFails { get; protected set; }

        public override void Think(AIController ownerController)
        {
            ProceduralAI proceduralAI = ownerController.Brain;
            if (proceduralAI == null) return;
            Agent agent = ownerController.Owner;
            if (agent == null) return;

            WorldEntity target = ownerController.TargetEntity;
            if (target == null || target is not Transition)
            {
                var selectionContext = new SelectEntity.SelectEntityContext(ownerController, SelectPortalToExitFrom);
                selectionContext.StaticEntities = true;
                WorldEntity selectedEntity = SelectEntity.DoSelectEntity(ref selectionContext);
                if (selectedEntity != null)
                    SelectEntity.RegisterSelectedEntity(ownerController, selectedEntity, selectionContext.SelectionType);
            }

            BehaviorBlackboard blackboard = ownerController.Blackboard;
            if (DelayBeforeRunToExit != null && blackboard.PropertyCollection[PropertyEnum.AIRunToExitDelayFired] == false)
            {
                StaticBehaviorReturnType delayResult = HandleContext(proceduralAI, ownerController, DelayBeforeRunToExit);
                if (delayResult == StaticBehaviorReturnType.Running) return;
                else if (delayResult == StaticBehaviorReturnType.Completed)
                    blackboard.PropertyCollection[PropertyEnum.AIRunToExitDelayFired] = true;
            }

            HandleMovementContext(proceduralAI, ownerController, agent.Locomotor, RunToExit, false, out var movementResult);
            if (movementResult == StaticBehaviorReturnType.Running) return;
            else if (movementResult == StaticBehaviorReturnType.Completed)
            {
                agent.Destroy();
                return;
            }

            if (VanishesIfMoveToExitFails)
            {
                StaticBehaviorReturnType delayResult = HandleContext(proceduralAI, ownerController, DelayBeforeDestroyOnMoveExitFail);
                if (delayResult == StaticBehaviorReturnType.Completed)
                    agent.Destroy();
            }
        }
    }

    public class ProceduralProfileRotatingTurretPrototype : ProceduralAIProfilePrototype
    {
        public UsePowerContextPrototype Power { get; protected set; }
        public RotateContextPrototype Rotate { get; protected set; }

        public override void Init(Agent agent)
        {
            base.Init(agent);
            InitPower(agent, Power);
        }

        public override void Think(AIController ownerController)
        {
            ProceduralAI proceduralAI = ownerController.Brain;
            if (proceduralAI == null) return;
            Agent agent = ownerController.Owner;
            if (agent == null) return;
            Game game = agent.Game;
            if (game == null) return;

            if (HandleOverrideBehavior(ownerController)) return;

            BehaviorSensorySystem senses = ownerController.Senses;
            if (senses.ShouldSense())
                senses.UpdateAvatarSensory();

            if (agent.IsDormant == false)
                if (HandleContext(proceduralAI, ownerController, Power) == StaticBehaviorReturnType.Running)
                {
                    proceduralAI.PushSubstate();
                    HandleContext(proceduralAI, ownerController, Rotate);
                    proceduralAI.PopSubstate();
                }
        }
    }

    public class ProceduralProfileWanderNoPowerPrototype : ProceduralAIProfilePrototype
    {
        public WanderContextPrototype WanderMovement { get; protected set; }

        public override void Think(AIController ownerController)
        {
            ProceduralAI proceduralAI = ownerController.Brain;
            if (proceduralAI == null) return;
            Agent agent = ownerController.Owner;
            if (agent == null) return;
            Game game = agent.Game;
            if (game == null) return;

            HandleMovementContext(proceduralAI, ownerController, agent.Locomotor, WanderMovement, false, out _);
        }
    }

    public class ProceduralProfilePetFidgetPrototype : ProceduralProfilePetPrototype
    {
        public ProceduralUsePowerContextPrototype Fidget { get; protected set; }

        public override void Init(Agent agent)
        {
            base.Init(agent);
            InitPower(agent, Fidget);
        }

        public override void PopulatePowerPicker(AIController ownerController, Picker<ProceduralUsePowerContextPrototype> powerPicker)
        {
            var powerContext = Fidget?.PowerContext;
            if (powerContext == null || powerContext.Power == PrototypeId.Invalid) return;

            BehaviorBlackboard blackboard = ownerController.Blackboard;
            if (blackboard.PropertyCollection.HasProperty(new PropertyId(PropertyEnum.AIPowerStarted, powerContext.Power)))
                ownerController.AddPowersToPicker(powerPicker, Fidget);
            else
                base.PopulatePowerPicker(ownerController, powerPicker);
        }
    }

    public class ProceduralProfileRollingGrenadesPrototype : ProceduralAIProfilePrototype
    {
        public int MaxSpeedDegreeUpdateIntervalMS { get; protected set; }
        public int MinSpeedDegreeUpdateIntervalMS { get; protected set; }
        public int MovementSpeedVariance { get; protected set; }
        public int RandomDegreeFromForward { get; protected set; }
    }

    public class ProceduralProfileFrozenOrbPrototype : ProceduralAIProfilePrototype
    {
        public int ShardBurstsPerSecond { get; protected set; }
        public int ShardsPerBurst { get; protected set; }
        public int ShardRotationSpeed { get; protected set; }
        public PrototypeId ShardPower { get; protected set; }

        public override void Init(Agent agent)
        {
            base.Init(agent);
            InitPower(agent, ShardPower);
        }
    }

    public class ProceduralProfilePetDirectedPrototype : ProceduralProfilePetPrototype
    {
        public ProceduralUsePowerContextPrototype[] DirectedPowers { get; protected set; }

        public override void Init(Agent agent)
        {
            base.Init(agent);
            InitPowers(agent, DirectedPowers);
        }
    }
 
    public class ProceduralProfileSyncAttackPrototype : ProceduralProfileBasicMeleePrototype
    {
        public ProceduralSyncAttackContextPrototype[] SyncAttacks { get; protected set; }

        private const int IDPropertiesLength = 4;
        private readonly PropertyEnum[] IDProperties = new PropertyEnum[IDPropertiesLength]
        {
            PropertyEnum.AICustomEntityId1, 
            PropertyEnum.AICustomEntityId2, 
            PropertyEnum.AICustomEntityId3, 
            PropertyEnum.AICustomEntityId4
        };

        public override void PopulatePowerPicker(AIController ownerController, Picker<ProceduralUsePowerContextPrototype> powerPicker)
        {
            base.PopulatePowerPicker(ownerController, powerPicker);
            PrototypeId startedPowerRef = ownerController.ActivePowerRef;
            if (startedPowerRef != PrototypeId.Invalid)
                foreach (var syncAttack in SyncAttacks)
                {
                    var powerContext = syncAttack?.LeaderPower?.PowerContext;
                    if (powerContext != null && powerContext.Power == startedPowerRef)
                    {
                        ownerController.AddPowersToPicker(powerPicker, syncAttack.LeaderPower);
                        return;
                    }
                }

            Agent leader = ownerController.Owner;
            if (leader == null) return;
            Game game = leader.Game;
            if (game == null) return;
            var blackboard = ownerController.Blackboard;

            int syncAttackIndex = GetRandomSyncAttackIndex(blackboard, game);
            if (syncAttackIndex < 0 || syncAttackIndex >= IDPropertiesLength) return;

            ulong targetId = blackboard.PropertyCollection[IDProperties[syncAttackIndex]];            
            var target = game.EntityManager.GetEntity<Agent>(targetId);
            if (target == null) return;

            var syncAttackProto = SyncAttacks[syncAttackIndex];
            if (syncAttackProto == null) return;

            var targetController = target.AIController;
            if (targetController == null) return;

            var targetBlackboard = targetController.Blackboard;
            if (targetController.Brain is not ProceduralAI targetAI) return;

            ulong tempEntityId = targetBlackboard.PropertyCollection[PropertyEnum.AIRawTargetEntityID];
            targetBlackboard.PropertyCollection[PropertyEnum.AIRawTargetEntityID] = leader.Id;

            var targetEntityPowerProto = syncAttackProto.TargetEntityPower.As<ProceduralUsePowerContextPrototype>();
            if (ValidateUsePowerContext(targetController, targetAI, targetEntityPowerProto.PowerContext))
            {
                blackboard.PropertyCollection[PropertyEnum.AICustomStateVal1] = syncAttackIndex;
                ownerController.AddPowersToPicker(powerPicker, syncAttackProto.LeaderPower);
            }
            targetBlackboard.PropertyCollection[PropertyEnum.AIRawTargetEntityID] = tempEntityId;
        }

        private int GetRandomSyncAttackIndex(BehaviorBlackboard blackboard, Game game)
        {
            if (SyncAttacks.IsNullOrEmpty()) return -1;

            if (IDPropertiesLength < SyncAttacks.Length)
            {
                ProceduralAI.Logger.Warn($"AI has more SyncAttacks than supported! Max supported is {IDPropertiesLength}! AI: {ToString()}");
                return -1;
            }

            List<int> syncAttackIndices = new ();
            for (int i = 0; i < IDPropertiesLength && i < SyncAttacks.Length; i++)
            {
                ulong targetId = blackboard.PropertyCollection[IDProperties[i]];
                Agent target = game.EntityManager.GetEntity<Agent>(targetId);
                if (target != null && target.IsDead == false)
                    syncAttackIndices.Add(i);
            }
            if (syncAttackIndices.Count == 0) return -1;

            int randomIndex = game.Random.Next(0, syncAttackIndices.Count);
            return syncAttackIndices[randomIndex];
        }

    }

    public class ProceduralProfileLOSRangedPrototype : ProceduralProfileBasicRangePrototype
    {
        public ProceduralUsePowerContextPrototype LOSChannelPower { get; protected set; }

        public override void Init(Agent agent)
        {
            base.Init(agent);
            InitPower(agent, LOSChannelPower);
        }

        public override void PopulatePowerPicker(AIController ownerController, Picker<ProceduralUsePowerContextPrototype> powerPicker)
        {
            ownerController.AddPowersToPicker(powerPicker, LOSChannelPower);
            base.PopulatePowerPicker(ownerController, powerPicker);
        }
    }

    public class ProcProfileSpikeDanceControllerPrototype : ProceduralAIProfilePrototype
    {
        public PrototypeId Onslaught { get; protected set; }
        public PrototypeId SpikeDanceMob { get; protected set; }
        public int MaxSpikeDanceActivations { get; protected set; }
        public float SpikeDanceMobSearchRadius { get; protected set; }
    }

    public class ProceduralProfileMeleeRevengePrototype : ProceduralProfileBasicMeleePrototype
    {
        public ProceduralUsePowerContextPrototype RevengePower { get; protected set; }
        public PrototypeId RevengeSupport { get; protected set; }

        public override void Init(Agent agent)
        {
            base.Init(agent);
            InitPower(agent, RevengePower);
        }

        public override void PopulatePowerPicker(AIController ownerController, Picker<ProceduralUsePowerContextPrototype> powerPicker)
        {
            base.PopulatePowerPicker(ownerController, powerPicker);
            int stateVal = ownerController.Blackboard.PropertyCollection[PropertyEnum.AICustomStateVal1];
            if (stateVal == 1)
                ownerController.AddPowersToPicker(powerPicker, RevengePower);
        }
    }

    public class ProceduralProfileRangedRevengePrototype : ProceduralProfileBasicRangePrototype
    {
        public ProceduralUsePowerContextPrototype RevengePower { get; protected set; }
        public PrototypeId RevengeSupport { get; protected set; }

        public override void Init(Agent agent)
        {
            base.Init(agent);
            InitPower(agent, RevengePower);
        }

        public override void PopulatePowerPicker(AIController ownerController, Picker<ProceduralUsePowerContextPrototype> powerPicker)
        {
            base.PopulatePowerPicker(ownerController, powerPicker);
            int stateVal = ownerController.Blackboard.PropertyCollection[PropertyEnum.AICustomStateVal1];
            if (stateVal == 1)
                ownerController.AddPowersToPicker(powerPicker, RevengePower);
        }
    }

}
