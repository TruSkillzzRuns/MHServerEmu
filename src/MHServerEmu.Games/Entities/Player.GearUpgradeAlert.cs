using MHServerEmu.Core.Logging;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Entities.Inventories;
using MHServerEmu.Games.Entities.Items;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Loot;
using MHServerEmu.Games.Properties;

namespace MHServerEmu.Games.Entities
{
    /// <summary>
    /// Gear Upgrade Alert — when a drop beats what you have equipped in the
    /// same slot, say so, so you don't have to open and compare tooltips on
    /// every item that lands.
    ///
    /// Ranking metric, and its honest limits: items are compared on
    /// (rarity tier, item level) only. RarityPrototype.Tier is derived at load
    /// by walking the DowngradeTo chain, so the ordering comes from the data
    /// rather than a hardcoded list and stays correct if rarities are patched.
    ///
    /// What this deliberately does NOT do is weigh affixes. Two same-rarity,
    /// same-level items can differ enormously in actual value, and an item
    /// with worse rarity can still roll better stats for a given build. So
    /// this is a "worth a look" flag, not a verdict — it is tuned to stay
    /// quiet rather than to catch every upgrade, because a noisy alert that
    /// cries wolf is worse than no alert.
    ///
    /// Only fires for items that resolve to a real equipment slot on the
    /// CURRENT avatar, so mats, currency, costumes and other-hero gear never
    /// trigger it.
    /// </summary>
    public partial class Player
    {
        private static readonly Logger GearAlertLogger = LogManager.CreateLogger();

        private bool _gearUpgradeAlertEnabled = false;

        public bool GearUpgradeAlertEnabled
        {
            get => _gearUpgradeAlertEnabled;
            set => _gearUpgradeAlertEnabled = value;
        }

        private int _gearAlertSessionCount = 0;

        public int GearAlertSessionCount { get => _gearAlertSessionCount; }

        /// <summary>
        /// Records the acquisition for the farm session tracker. Always on —
        /// it is a bounded in-memory tally with no player-visible output, so
        /// there is nothing to opt into, and the data has to already be there
        /// the moment someone asks "was that spot any good".
        /// </summary>
        private void RecordFarmDrop(Item item, string verdict)
        {
            if (item == null) return;

            PrototypeId regionRef = CurrentAvatar?.Region?.PrototypeDataRef ?? PrototypeId.Invalid;

            // Resolve the real in-game name — the prototype leaf is developer
            // shorthand ("Unique303") rather than what the player sees.
            string displayName = null;
            var locale = Locales.LocaleManager.Instance.CurrentLocale;
            if (locale != null && item.ItemPrototype is { DisplayName: var nameId } && nameId != LocaleStringId.Invalid)
            {
                displayName = locale.GetLocaleString(nameId);
                if (string.IsNullOrWhiteSpace(displayName)) displayName = null;
            }

            // Rarity's own display name too — "R4Epic" is the prototype leaf,
            // not what the player reads on the item.
            string rarityName = null;
            PrototypeId rarityRef = item.Properties[PropertyEnum.ItemRarity];
            if (locale != null && rarityRef != PrototypeId.Invalid)
            {
                var rarityProto = GameDatabase.GetPrototype<RarityPrototype>(rarityRef);
                if (rarityProto != null && rarityProto.DisplayNameText != LocaleStringId.Invalid)
                {
                    rarityName = locale.GetLocaleString(rarityProto.DisplayNameText);
                    if (string.IsNullOrWhiteSpace(rarityName)) rarityName = null;
                }
            }

            // Icon asset NAME only. The app resolves it against the user's own
            // installed client (via /webapi/portrait or /webapi/texbyname), so
            // no game art is ever stored in or shipped with the server.
            // HiRes first where the data has it — 1.48's ItemPrototype doesn't.
            string iconPath = null;
            if (item.ItemPrototype != null)
            {
#if GAME_VERSION_1_52 || GAME_VERSION_1_53
                AssetId iconAssetId = item.ItemPrototype.IconPathHiRes != 0
                    ? item.ItemPrototype.IconPathHiRes
                    : item.ItemPrototype.IconPath;
#else
                AssetId iconAssetId = item.ItemPrototype.IconPath;
#endif
                if (iconAssetId != 0)
                    iconPath = GameDatabase.GetAssetName(iconAssetId);
            }

            // Artifacts and legendaries carry a rarity ref that reads as
            // "Common" at runtime, which is worse than useless on the drop
            // list — it labels a cosmic artifact as trash. Neither type is
            // ranked by the rarity system in any meaningful way, so they get
            // their own label and colour instead of a rarity that misinforms.
            //
            // Type checks, not name or path matches, so this holds identically
            // on 1.48/1.52/1.53 with no per-version list to maintain.
            int specialTier = 0;
            if (item.ItemPrototype is ArtifactPrototype)
            {
                rarityName = "Artifact";
                specialTier = FarmTracker.ArtifactTier;
            }
            else if (item.ItemPrototype is LegendaryPrototype)
            {
                rarityName = "Legendary";
                specialTier = FarmTracker.LegendaryTier;
            }

            FarmTracker.RecordDrop(
                Id,
                GetName(),
                item.PrototypeDataRef,
                rarityRef,
                item.Properties[PropertyEnum.ItemLevel],
                regionRef,
                (long)Game.CurrentTime.TotalMilliseconds,
                displayName,
                rarityName,
                verdict,
                iconPath,
                specialTier);
        }

        /// <summary>
        /// Called after an item has been successfully acquired. Cheap-exits on
        /// anything that isn't equippable gear for the current avatar.
        /// </summary>
        /// <summary>
        /// Returns "" | "upgrade" | "sidegrade" so the Farm Session tool can
        /// mark the drop visually — a chat line alone tells you something
        /// dropped but not which row it was.
        /// </summary>
        private string CheckGearUpgrade(Item item)
        {
            if (item == null) return "";

            Avatar avatar = CurrentAvatar;
            if (avatar == null) return "";

            if (avatar.Prototype is not AvatarPrototype avatarProto) return "";

            ItemPrototype itemProto = item.ItemPrototype;
            if (itemProto == null) return "";

            // Slot resolution doubles as the "is this even gear for me"
            // test — non-equippable items and other heroes' exclusives come
            // back Invalid.
            EquipmentInvUISlot slot = itemProto.GetInventorySlotForAgent(avatarProto);
            if (slot == EquipmentInvUISlot.Invalid) return "";

            // Costumes are cosmetic; rarity/level ordering says nothing useful
            // about them.
            if (slot == EquipmentInvUISlot.Costume) return "";

            // Compare against the WEAKEST item in the same slot family, not the
            // one item whose slot enum happens to match. Artifacts have four
            // slots and gear has five; matching one exact slot compared a drop
            // against an arbitrary equipped item, so a drop that beat your worst
            // artifact and a drop that lost to your best both got the same
            // verdict. The weakest is the one you would actually replace.
            Item equipped = FindWeakestEquippedInSlotFamily(avatar, avatarProto, slot);

            int newTier = GetRarityTier(item);
            int newLevel = item.Properties[PropertyEnum.ItemLevel];

            // The item's real in-game name — the whole point of the alert is
            // knowing WHICH item, and the prototype leaf ("Unique303") doesn't
            // tell you that.
            string itemName = ResolveItemName(item);

            string message;
            string tag;

            if (equipped == null)
            {
                // An empty slot is unambiguous — always worth saying.
                message = $"🟢 UPGRADE — {itemName} (lv{newLevel}) · your {SlotName(slot)} slot is empty.";
                tag = "upgrade";
            }
            else
            {
                int oldTier = GetRarityTier(equipped);
                int oldLevel = equipped.Properties[PropertyEnum.ItemLevel];

                GearComparison cmp = CompareAffixStats(item, equipped);

                // A stat comparison is only meaningful when BOTH items actually
                // exposed stats. AccumulateAffixStats reads rolled affixes only,
                // so an item whose power is built into its prototype accumulates
                // nothing and reads as empty — which is how a lv1 artifact with
                // one rolled affix was reported as "better on 1, worse on none"
                // against a lv63 artifact. When either side is blank the affix
                // verdict is not evidence, so fall through to rarity/level.
                if (cmp.Comparable == false)
                {
                    if (newTier > oldTier || (newTier == oldTier && newLevel > oldLevel))
                    {
                        message = $"🟢 UPGRADE — {itemName} ({RarityName(item)} lv{newLevel}) for {SlotName(slot)} · "
                                + $"outranks {ResolveItemName(equipped)} ({RarityName(equipped)} lv{oldLevel}). "
                                + "Stats couldn't be compared — check the tooltip.";
                        tag = "upgrade";
                    }
                    else return "no";
                }
                else if (cmp.Dominates)
                {
                    // Strictly better in the real sense: every stat that
                    // changed moved up, none moved down.
                    message = $"🟢 UPGRADE — {itemName} (lv{newLevel}) for {SlotName(slot)} · up on {cmp.Better} stat(s), down on {cmp.Worse}. "
                            + $"Replaces {ResolveItemName(equipped)} (lv{oldLevel}).";
                    tag = "upgrade";
                }
                else if (cmp.Comparable && (cmp.Better > 0 || cmp.Worse > 0))
                {
                    // Compared cleanly and lost. No middle verdict by design.
                    return "no";
                }
                else if (cmp.Better == 0 && cmp.Worse == 0)
                {
                    // No affix data to separate them — fall back to the cheap
                    // ordering so an obviously better drop still gets flagged.
                    if (newTier > oldTier || (newTier == oldTier && newLevel > oldLevel))
                    {
                        message = $"🟢 UPGRADE — {itemName} ({RarityName(item)} lv{newLevel}) for {SlotName(slot)} · "
                                + $"beats {ResolveItemName(equipped)} ({RarityName(equipped)} lv{oldLevel}).";
                        tag = "upgrade";
                    }
                    else
                    {
                        // Equal or worse — no chat noise, but the row is still
                        // tagged so the tool can say "checked, not better"
                        // rather than looking the same as a crafting mat.
                        return "no";
                    }
                }
                else
                {
                    // Worse on every changed stat — quiet in chat, still tagged.
                    return "no";
                }
            }

            _gearAlertSessionCount++;

            // Chat is the live nudge; the tag is what lets the Farm Session
            // tool mark the row so you can still find it afterwards.
            if (_gearUpgradeAlertEnabled)
                SendAutoStashBanner(message);

            return tag;
        }

        /// <summary>The item's localized name, falling back to the prototype leaf.</summary>
        private static string ResolveItemName(Item item)
        {
            var locale = Locales.LocaleManager.Instance.CurrentLocale;
            if (locale != null && item?.ItemPrototype is { DisplayName: var nameId } && nameId != LocaleStringId.Invalid)
            {
                string name = locale.GetLocaleString(nameId);
                if (string.IsNullOrWhiteSpace(name) == false) return name;
            }

            return item?.PrototypeDataRef.GetNameFormatted() ?? "?";
        }

        /// <summary>
        /// Slots that hold interchangeable items, so a drop for one of them
        /// competes against every item in the family rather than against a
        /// single slot. Anything not listed is its own family.
        /// </summary>
        private static string SlotFamily(EquipmentInvUISlot slot)
        {
            return slot switch
            {
                // Artifacts are the ONLY interchangeable family: any artifact
                // can go in any of the four slots, so a drop competes with all
                // of them.
                //
                // Gear deliberately is NOT grouped. Gear01-05 are typed slots
                // (armor, helm, gloves, ...) and GetInventorySlotForAgent maps
                // each item to its one real slot, so a chest piece only ever
                // replaces the chest piece. Grouping them meant an armor drop
                // was judged against whichever gear piece happened to be
                // weakest, which is how a clearly worse item got called an
                // upgrade against an item it could never have replaced.
                EquipmentInvUISlot.Artifact01 or EquipmentInvUISlot.Artifact02
                    or EquipmentInvUISlot.Artifact03 or EquipmentInvUISlot.Artifact04 => "artifact",
                _ => slot.ToString(),
            };
        }

        /// <summary>
        /// Returns the WEAKEST equipped item in the same slot family, or
        /// <see langword="null"/> when the family has a free slot.
        ///
        /// A free slot returns null on purpose: if you have three artifacts in
        /// four slots, the next artifact costs you nothing, so it is an upgrade
        /// regardless of its stats.
        ///
        /// "Weakest" is (rarity tier, item level) — deliberately not the affix
        /// comparison, because that is not a total order (A can beat B, B beat
        /// C, and C beat A), so it cannot pick a minimum. This only chooses
        /// WHICH item to compare against; the real verdict is still the affix
        /// comparison against the item chosen here.
        /// </summary>
        private Item FindWeakestEquippedInSlotFamily(Avatar avatar, AvatarPrototype avatarProto, EquipmentInvUISlot slot)
        {
            if (avatarProto.EquipmentInventories == null) return null;

            string family = SlotFamily(slot);

            Item weakest = null;
            int weakestTier = int.MaxValue;
            int weakestLevel = int.MaxValue;
            int occupied = 0;

            foreach (AvatarEquipInventoryAssignmentPrototype assignment in avatarProto.EquipmentInventories)
            {
                if (assignment?.Inventory == null) continue;

                Inventory inv = avatar.GetInventoryByRef(assignment.Inventory.DataRef);
                if (inv == null) continue;

                foreach (var entry in inv)
                {
                    Item equipped = Game.EntityManager.GetEntity<Item>(entry.Id);
                    if (equipped == null) continue;

                    ItemPrototype equippedProto = equipped.ItemPrototype;
                    if (equippedProto == null) continue;

                    EquipmentInvUISlot equippedSlot = equippedProto.GetInventorySlotForAgent(avatarProto);
                    if (SlotFamily(equippedSlot) != family) continue;

                    occupied++;

                    int tier = GetRarityTier(equipped);
                    int level = equipped.Properties[PropertyEnum.ItemLevel];

                    if (tier < weakestTier || (tier == weakestTier && level < weakestLevel))
                    {
                        weakest = equipped;
                        weakestTier = tier;
                        weakestLevel = level;
                    }
                }
            }

            // Family has room — treat it as an empty slot.
            if (occupied < SlotFamilyCapacity(family)) return null;

            return weakest;
        }

        /// <summary>
        /// How many items a family holds. Only the multi-slot families need an
        /// entry; everything else is a single slot.
        /// </summary>
        private static int SlotFamilyCapacity(string family)
        {
            return family switch
            {
                "artifact" => 4,
                // Every other slot holds exactly one item, so it is "full" as
                // soon as anything is in it.
                _ => 1,
            };
        }

        private readonly struct GearComparison
        {
            public readonly int Better;
            public readonly int Worse;

            /// <summary>
            /// Both items exposed at least one stat, so the counts below mean
            /// something. False when either side accumulated nothing — see the
            /// note in CheckGearUpgrade about prototype-granted stats.
            /// </summary>
            public readonly bool Comparable;

            /// <summary>
            /// Net stat win. Deliberately "more stats up than down" rather than
            /// "up on everything": requiring a clean sweep meant almost every
            /// real roll landed in a middle bucket, which is not a verdict — an
            /// item either replaces what you're wearing or it doesn't.
            ///
            /// Honest limit: this counts stats, it can't weigh them, so a big
            /// gain on one stat against small losses on two reads as "no". That
            /// is a build-dependent judgement no server-side rule can make.
            /// </summary>
            public bool Dominates => Comparable && Better > Worse;

            public GearComparison(int better, int worse, bool comparable)
            {
                Better = better;
                Worse = worse;
                Comparable = comparable;
            }
        }

        /// <summary>
        /// Compares two items stat-by-stat over the UNION of properties either
        /// one grants, so nothing is missed because it only appears on one
        /// side (a stat absent from an item counts as 0 for that item).
        ///
        /// Deliberately weight-free: no attempt is made to decide that N
        /// damage is worth M health, because that depends on the build and any
        /// weighting would be a guess dressed up as a number. Instead this
        /// counts stats up vs stats down, which supports an objective
        /// "better on every changed stat" verdict.
        ///
        /// Known limit: this treats every property as higher-is-better, which
        /// holds for the beneficial affixes items actually roll but would
        /// misread a hypothetical penalty affix.
        /// </summary>
        private static GearComparison CompareAffixStats(Item newItem, Item equipped)
        {
            Dictionary<PropertyId, float> newStats = new();
            Dictionary<PropertyId, float> oldStats = new();

            newItem.AccumulateAffixStats(newStats);
            equipped.AccumulateAffixStats(oldStats);

            int better = 0;
            int worse = 0;

            HashSet<PropertyId> allKeys = new(newStats.Keys);
            allKeys.UnionWith(oldStats.Keys);

            foreach (PropertyId id in allKeys)
            {
                newStats.TryGetValue(id, out float newValue);
                oldStats.TryGetValue(id, out float oldValue);

                // Relative epsilon — percentage affixes are tiny absolute
                // numbers while flat stats are large, so a fixed threshold
                // would either ignore real percentage gains or treat float
                // noise on big numbers as a difference.
                float scale = Math.Max(Math.Abs(newValue), Math.Abs(oldValue));
                float epsilon = Math.Max(0.0001f, scale * 0.001f);

                if (newValue > oldValue + epsilon) better++;
                else if (newValue < oldValue - epsilon) worse++;
            }

            // An empty side means "no data", not "zero stats" — see GearComparison.
            bool comparable = newStats.Count > 0 && oldStats.Count > 0;

            return new GearComparison(better, worse, comparable);
        }

        private static int GetRarityTier(Item item)
        {
            RarityPrototype rarityProto = item.RarityPrototype;
            return rarityProto?.Tier ?? 0;
        }

        private static string RarityName(Item item)
        {
            RarityPrototype rarityProto = item.RarityPrototype;
            if (rarityProto == null) return "Unknown";
            return rarityProto.DataRef.GetNameFormatted();
        }

        private static string SlotName(EquipmentInvUISlot slot)
        {
            return slot switch
            {
                EquipmentInvUISlot.Gear01 or EquipmentInvUISlot.Gear02 or EquipmentInvUISlot.Gear03
                    or EquipmentInvUISlot.Gear04 or EquipmentInvUISlot.Gear05 => "gear",
                EquipmentInvUISlot.Artifact01 or EquipmentInvUISlot.Artifact02
                    or EquipmentInvUISlot.Artifact03 or EquipmentInvUISlot.Artifact04 => "artifact",
                EquipmentInvUISlot.Medal => "medal",
                EquipmentInvUISlot.Relic => "relic",
                EquipmentInvUISlot.Insignia => "insignia",
                EquipmentInvUISlot.Ring => "ring",
                EquipmentInvUISlot.Legendary => "legendary",
                EquipmentInvUISlot.UruForged => "uru-forged",
                _ => slot.ToString().ToLowerInvariant(),
            };
        }
    }
}
