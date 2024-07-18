﻿using MHServerEmu.Core.Collisions;
using MHServerEmu.Core.Extensions;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.Games.GameData.Calligraphy;
using MHServerEmu.Games.Loot;

namespace MHServerEmu.Games.GameData.Prototypes
{
    public class DropRestrictionPrototype : Prototype
    {
        public virtual bool Adjust(DropFilterArguments filterArgs, ref RestrictionTestFlags outputRestrictionFlags, RestrictionTestFlags restrictionFlags)
        {
            return restrictionFlags.HasFlag(RestrictionTestFlags.Output) || Allow(filterArgs, restrictionFlags);
        }

        public virtual bool Allow(DropFilterArguments filterArgs, RestrictionTestFlags restrictionFlags)
        {
            return (filterArgs.LootContext & LootContext.CashShop) == filterArgs.LootContext;
        }

        public virtual bool AllowAsCraftingInput(LootCloneRecord lootCloneRecord, RestrictionTestFlags restrictionFlags)
        {
            return Allow(lootCloneRecord, restrictionFlags);
        }
    }

    public class ConditionalRestrictionPrototype : DropRestrictionPrototype
    {
        private LootContext _lootContextFlags = LootContext.None;

        public DropRestrictionPrototype[] Apply { get; protected set; }
        public LootContext[] ApplyFor { get; protected set; }
        public DropRestrictionPrototype[] Else { get; protected set; }

        public override void PostProcess()
        {
            base.PostProcess();

            if (ApplyFor.IsNullOrEmpty())
                return;

            foreach (LootContext context in ApplyFor)
                _lootContextFlags |= context;
        }

        public override bool Adjust(DropFilterArguments filterArgs, ref RestrictionTestFlags outputRestrictionFlags, RestrictionTestFlags restrictionFlags)
        {
            if ((filterArgs.LootContext & _lootContextFlags) == filterArgs.LootContext)
            {
                if (Apply.IsNullOrEmpty())
                    return true;

                foreach (DropRestrictionPrototype restrictionProto in Apply)
                {
                    if (restrictionProto.Adjust(filterArgs, ref outputRestrictionFlags, restrictionFlags) == false)
                        return false;
                }
            }
            else
            {
                if (Else.IsNullOrEmpty())
                    return true;

                foreach (DropRestrictionPrototype restrictionProto in Else)
                {
                    if (restrictionProto.Adjust(filterArgs, ref outputRestrictionFlags, restrictionFlags) == false)
                        return false;
                }
            }

            return true;
        }

        public override bool Allow(DropFilterArguments filterArgs, RestrictionTestFlags restrictionFlags)
        {
            if ((filterArgs.LootContext & _lootContextFlags) == filterArgs.LootContext)
            {
                if (Apply.IsNullOrEmpty())
                    return true;

                foreach (DropRestrictionPrototype restrictionProto in Apply)
                {
                    if (restrictionProto.Allow(filterArgs, restrictionFlags) == false)
                        return false;
                }
            }
            else
            {
                if (Else.IsNullOrEmpty())
                    return true;

                foreach (DropRestrictionPrototype restrictionProto in Else)
                {
                    if (restrictionProto.Allow(filterArgs, restrictionFlags) == false)
                        return false;
                }
            }

            return true;
        }
    }

    public class ContextRestrictionPrototype : DropRestrictionPrototype
    {
        private LootContext _lootContextFlags = LootContext.None;

        public LootContext[] UsableFor { get; protected set; }

        public override void PostProcess()
        {
            base.PostProcess();

            if (UsableFor.IsNullOrEmpty())
                return;

            foreach (LootContext context in UsableFor)
                _lootContextFlags |= context;
        }

        public override bool Allow(DropFilterArguments filterArgs, RestrictionTestFlags restrictionFlags)
        {
            return (filterArgs.LootContext & _lootContextFlags) == filterArgs.LootContext;
        }
    }

    public class ItemTypeRestrictionPrototype : DropRestrictionPrototype
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        public PrototypeId[] AllowedTypes { get; protected set; }

        public override bool Allow(DropFilterArguments filterArgs, RestrictionTestFlags restrictionFlags)
        {
            if (AllowedTypes.IsNullOrEmpty())
                return false;

            if (restrictionFlags.HasFlag(RestrictionTestFlags.ItemType) == false)
                return true;

            Prototype itemProto = filterArgs.ItemProto;
            if (itemProto == null) return Logger.WarnReturn(false, "Allow(): itemProto == null");

            DataDirectory dataDirectory = DataDirectory.Instance;
            foreach (PrototypeId allowedTypeRef in AllowedTypes)
            {
                Blueprint blueprint = dataDirectory.GetBlueprint((BlueprintId)allowedTypeRef);
                if (blueprint == null)
                {
                    Logger.Warn("Allow(): blueprint == null");
                    continue;
                }

                if (dataDirectory.PrototypeIsChildOfBlueprint(itemProto.DataRef, blueprint.Id))
                    return true;
            }

            return false;
        }
    }

    public class ItemParentRestrictionPrototype : DropRestrictionPrototype
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        public PrototypeId[] AllowedParents { get; protected set; }

        public override bool Allow(DropFilterArguments filterArgs, RestrictionTestFlags restrictionFlags)
        {
            if (AllowedParents.IsNullOrEmpty())
                return false;

            if (restrictionFlags.HasFlag(RestrictionTestFlags.ItemParent) == false)
                return true;

            Prototype itemProto = filterArgs.ItemProto;
            if (itemProto == null) return Logger.WarnReturn(false, "Allow(): itemProto == null");

            DataDirectory dataDirectory = DataDirectory.Instance;
            foreach (PrototypeId allowedTypeRef in AllowedParents)
            {
                if (dataDirectory.PrototypeIsAPrototype(itemProto.DataRef, allowedTypeRef))
                    return true;
            }

            return false;
        }
    }

    public class HasAffixInPositionRestrictionPrototype : DropRestrictionPrototype
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        public AffixPosition Position { get; protected set; }

        public override bool Allow(DropFilterArguments filterArgs, RestrictionTestFlags restrictionFlags)
        {
            return Logger.WarnReturn(false, "Allow(): HasAffixInPosition DropRestriction is only supported in CraftingInput right-hand structs!");
        }

        public override bool AllowAsCraftingInput(LootCloneRecord lootCloneRecord, RestrictionTestFlags restrictionTestFlags)
        {
            // TODO
            return false;
        }
    }

    public class HasVisualAffixRestrictionPrototype : DropRestrictionPrototype
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        public bool MustHaveNoVisualAffixes { get; protected set; }
        public bool MustHaveVisualAffix { get; protected set; }

        public override bool Allow(DropFilterArguments filterArgs, RestrictionTestFlags restrictionFlags)
        {
            return Logger.WarnReturn(false, "Allow(): HasVisualAffix DropRestriction is only supported in CraftingInput right-hand structs!");
        }

        public override bool AllowAsCraftingInput(LootCloneRecord lootCloneRecord, RestrictionTestFlags restrictionTestFlags)
        {
            // TODO
            return false;
        }
    }

    public class LevelRestrictionPrototype : DropRestrictionPrototype
    {
        public int LevelMin { get; protected set; }
        public int LevelRange { get; protected set; }

        public override void PostProcess()
        {
            base.PostProcess();

            LevelMin = Math.Max(LevelMin, 1);
            LevelRange = Math.Max(LevelRange, -1);
        }

        public override bool Adjust(DropFilterArguments filterArgs, ref RestrictionTestFlags outputRestrictionFlags, RestrictionTestFlags restrictionFlags)
        {
            if (Allow(filterArgs, restrictionFlags))
                return true;

            if (outputRestrictionFlags.HasFlag(RestrictionTestFlags.OutputLevel) || restrictionFlags.HasFlag(RestrictionTestFlags.Output))
                return true;

            if (restrictionFlags.HasFlag(RestrictionTestFlags.Level) == false)
                return false;

            filterArgs.Level = Math.Max(filterArgs.Level, LevelMin);

            if (LevelRange >= 0)
                filterArgs.Level = Math.Min(filterArgs.Level, LevelMin + LevelRange);

            outputRestrictionFlags |= RestrictionTestFlags.Level;
            
            return true;
        }

        public override bool Allow(DropFilterArguments filterArgs, RestrictionTestFlags restrictionFlags)
        {
            if (restrictionFlags.HasFlag(RestrictionTestFlags.Level) == false)
                return true;

            int level = filterArgs.Level;

            return level >= LevelMin && (LevelRange < 0 || level <= LevelMin + LevelRange);
        }
    }

    public class OutputLevelPrototype : DropRestrictionPrototype
    {
        public int Value { get; protected set; }
        public bool UseAsFilter { get; protected set; }

        public override void PostProcess()
        {
            base.PostProcess();
        }

        public override bool Adjust(DropFilterArguments filterArgs, ref RestrictionTestFlags outputRestrictionFlags, RestrictionTestFlags restrictionFlags)
        {
            if (restrictionFlags.HasFlag(RestrictionTestFlags.Level))
            {
                outputRestrictionFlags |= RestrictionTestFlags.OutputLevel;

                if (filterArgs.Level != Value)
                {
                    outputRestrictionFlags |= RestrictionTestFlags.Level;
                    filterArgs.Level = Value;
                }
            }

            return true;
        }

        public override bool Allow(DropFilterArguments filterArgs, RestrictionTestFlags restrictionFlags)
        {
            return UseAsFilter == false || restrictionFlags.HasFlag(RestrictionTestFlags.Level) == false || filterArgs.Level == Value;
        }
    }

    public class OutputRankPrototype : DropRestrictionPrototype
    {
        public int Value { get; protected set; }
        public bool UseAsFilter { get; protected set; }

        public override bool Adjust(DropFilterArguments filterArgs, ref RestrictionTestFlags outputRestrictionFlags, RestrictionTestFlags restrictionFlags)
        {
            if (restrictionFlags.HasFlag(RestrictionTestFlags.Rank))
            {
                outputRestrictionFlags |= RestrictionTestFlags.OutputLevel;

                if (filterArgs.Level != Value)
                {
                    outputRestrictionFlags |= RestrictionTestFlags.Level;
                    filterArgs.Level = Value;
                }
            }

            return true;
        }

        public override bool Allow(DropFilterArguments filterArgs, RestrictionTestFlags restrictionFlags)
        {
            return UseAsFilter == false || restrictionFlags.HasFlag(RestrictionTestFlags.Rank) == false ||
                MathHelper.BitTestAll(Value, filterArgs.Rank);
        }
    }

    public class OutputRarityPrototype : DropRestrictionPrototype
    {
        public PrototypeId Value { get; protected set; }
        public bool UseAsFilter { get; protected set; }

        public override bool Adjust(DropFilterArguments filterArgs, ref RestrictionTestFlags outputRestrictionFlags, RestrictionTestFlags restrictionFlags)
        {
            if (restrictionFlags.HasFlag(RestrictionTestFlags.Rarity))
            {
                outputRestrictionFlags |= RestrictionTestFlags.OutputRarity;

                if (filterArgs.Rarity != Value)
                {
                    outputRestrictionFlags |= RestrictionTestFlags.Rarity;
                    filterArgs.Rarity = Value;
                }
            }

            return true;
        }

        public override bool Allow(DropFilterArguments filterArgs, RestrictionTestFlags restrictionFlags)
        {
            return UseAsFilter == false || restrictionFlags.HasFlag(RestrictionTestFlags.Rarity) == false || filterArgs.Rarity == Value;
        }
    }

    public class RarityRestrictionPrototype : DropRestrictionPrototype
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        public PrototypeId[] AllowedRarities { get; protected set; }

        public override void PostProcess()
        {
            base.PostProcess();
            // TODO
        }

        public override bool Adjust(DropFilterArguments dropFilterArgs, ref RestrictionTestFlags outputRestrictionFlags, RestrictionTestFlags restrictionFlags)
        {
            // TODO
            Logger.Warn("Adjust(): Not implemented");
            return true;
        }

        public override bool Allow(DropFilterArguments filterArgs, RestrictionTestFlags restrictionFlags)
        {
            if (restrictionFlags.HasFlag(RestrictionTestFlags.Rarity) == false)
                return true;

            return AllowedRarities != null && AllowedRarities.Contains(filterArgs.Rarity);
        }
    }

    public class RankRestrictionPrototype : DropRestrictionPrototype
    {
        public int AllowedRanks { get; protected set; }

        public override bool Adjust(DropFilterArguments filterArgs, ref RestrictionTestFlags outputRestrictionFlags, RestrictionTestFlags restrictionFlags)
        {
            if (Allow(filterArgs, restrictionFlags) == false || (restrictionFlags.HasFlag(RestrictionTestFlags.Rank) && filterArgs.Rank == 0))
            {
                if (outputRestrictionFlags.HasFlag(RestrictionTestFlags.OutputRank) || restrictionFlags.HasFlag(RestrictionTestFlags.Output))
                    return true;

                if (restrictionFlags.HasFlag(RestrictionTestFlags.Rank) == false)
                    return false;

                outputRestrictionFlags |= RestrictionTestFlags.Rank;
                filterArgs.Rank = MathHelper.BitfieldGetLS1B(AllowedRanks);
            }

            return true;
        }

        public override bool Allow(DropFilterArguments filterArgs, RestrictionTestFlags restrictionFlags)
        {
            return restrictionFlags.HasFlag(RestrictionTestFlags.Rank) == false || MathHelper.BitTestAll(AllowedRanks, filterArgs.Rank);
        }
    }

    public class RestrictionListPrototype : DropRestrictionPrototype
    {
        public DropRestrictionPrototype[] Children { get; protected set; }

        public override bool Adjust(DropFilterArguments filterArgs, ref RestrictionTestFlags outputRestrictionFlags, RestrictionTestFlags restrictionFlags)
        {
            if (Children.IsNullOrEmpty())
                return false;

            foreach (DropRestrictionPrototype dropRestrictionProto in Children)
            {
                if (dropRestrictionProto.Adjust(filterArgs, ref outputRestrictionFlags, restrictionFlags) == false)
                    return false;
            }

            return true;
        }

        public override bool Allow(DropFilterArguments filterArgs, RestrictionTestFlags restrictionFlags)
        {
            if (Children.IsNullOrEmpty())
                return false;

            foreach (DropRestrictionPrototype dropRestrictionProto in Children)
            {
                if (dropRestrictionProto.Allow(filterArgs, restrictionFlags) == false)
                    return false;
            }

            return true;
        }
    }

    public class SlotRestrictionPrototype : DropRestrictionPrototype
    {
        public EquipmentInvUISlot[] AllowedSlots { get; protected set; }

        public override bool Adjust(DropFilterArguments filterArgs, ref RestrictionTestFlags outputRestrictionFlags, RestrictionTestFlags restrictionFlags)
        {
            if (Allow(filterArgs, restrictionFlags) == false)
            {
                if (restrictionFlags.HasFlag(RestrictionTestFlags.Output))
                    return true;

                if (restrictionFlags.HasFlag(RestrictionTestFlags.Slot) == false || AllowedSlots.IsNullOrEmpty())
                    return false;

                outputRestrictionFlags |= RestrictionTestFlags.Slot;
                filterArgs.Slot = AllowedSlots[0];
            }

            return true;
        }

        public override bool Allow(DropFilterArguments filterArgs, RestrictionTestFlags restrictionFlags)
        {
            if (AllowedSlots.IsNullOrEmpty())
                return false;

            return restrictionFlags.HasFlag(RestrictionTestFlags.Slot) == false || AllowedSlots.Contains(filterArgs.Slot);
        }
    }

    public class UsableByRestrictionPrototype : DropRestrictionPrototype
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        public PrototypeId[] Avatars { get; protected set; }

        public override bool Allow(DropFilterArguments filterArgs, RestrictionTestFlags restrictionFlags)
        {
            if (Avatars.IsNullOrEmpty())
                return false;

            if (restrictionFlags.HasFlag(RestrictionTestFlags.UsableBy) == false)
                return true;

            if (filterArgs.LootContext == LootContext.Crafting)
            {
                if (filterArgs.RollFor != PrototypeId.Invalid && Avatars.Contains(filterArgs.RollFor) == false)
                    return false;
            }
            else
            {
                if (filterArgs.RollFor == PrototypeId.Invalid)
                    Logger.Warn($"Allow(): RollFor is invalid, but context is not Crafting! RestrictionTestFlags=[{restrictionFlags}] Args=[{filterArgs}]");
            }

            ItemPrototype itemProto = filterArgs.ItemProto as ItemPrototype;
            if (itemProto == null) return Logger.WarnReturn(false, "Allow(): itemProto == null");

            foreach (PrototypeId avatarProtoRef in Avatars)
            {
                AgentPrototype agentProto = avatarProtoRef.As<AgentPrototype>();
                if (itemProto.IsUsableByAgent(agentProto))
                    return true;
            }

            return false;
        }
    }

    public class DistanceRestrictionPrototype : DropRestrictionPrototype
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        public override bool Allow(DropFilterArguments filterArgs, RestrictionTestFlags restrictionFlags)
        {
            Logger.Debug("Allow()");

            if (Segment.IsNearZero(filterArgs.DropDistanceSq) == false)
            {
                LootGlobalsPrototype lootGlobalsProto = GameDatabase.LootGlobalsPrototype;
                if (lootGlobalsProto == null) return Logger.WarnReturn(false, "Allow(): lootGlobalsProto == null");

                float dropDistanceThresholdSq = lootGlobalsProto.DropDistanceThreshold * lootGlobalsProto.DropDistanceThreshold;
                return dropDistanceThresholdSq > filterArgs.DropDistanceSq;
            }

            return true;
        }
    }
}
