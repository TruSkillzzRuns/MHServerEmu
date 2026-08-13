using MHServerEmu.Core.Logging;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Entities.Inventories;
using MHServerEmu.Games.Entities.Items;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Loot;
using MHServerEmu.Games.Properties;

namespace MHServerEmu.Games.Features
{
    /// <summary>
    /// Loot assistance: Auto-Stash, the Gear Upgrade Alert, and Farm Session
    /// recording. One class because they share a single entry point - every
    /// acquisition runs the gear comparison, records the drop with its verdict,
    /// then counts the auto-stash - and splitting them would mean threading that
    /// verdict between two objects.
    ///
    /// Extracted from Player (2026-08-12). It was 745 lines across two partial
    /// files sharing Player's state, so none of it could be tested without an
    /// Avatar, an EntityManager and a loaded GameDatabase - and the verdict
    /// logic reached live play wrong four times as a result.
    ///
    /// Player keeps thin delegating members, so every existing caller works
    /// unchanged - including the reflection-driven toggle panel, which looks for
    /// public bool properties named &lt;group&gt;Enabled on Player itself.
    /// </summary>
    public sealed class LootAssist
    {
        private readonly Player _player;

        public LootAssist(Player player)
        {
            _player = player;
        }


        private static readonly Logger AutoStashLogger = LogManager.CreateLogger();

        // Shared parent of every crafting ingredient in the shipped data.
        private const string CraftingIngredientDefaultsPath = "Entity/Items/Crafting/Ingredients/CraftingIngredient.defaults";

        // Resolved once per process. Invalid is a legitimate outcome on a game
        // version whose data lacks the prototype — the feature then simply
        // never matches, rather than throwing on every pickup.
        private static PrototypeId s_craftingIngredientDefaultsRef = PrototypeId.Invalid;
        private static bool s_craftingIngredientDefaultsResolved = false;

        private bool _autoStashEnabled = false;

        // Throttled feedback: stashing a mat per kill would flood the combat
        // log, so we count them and emit one banner at most every 15s.
        private int _autoStashPendingCount = 0;
        private long _autoStashNextBannerMs = 0;
        private const long AutoStashBannerIntervalMs = 15000;

        // Session diagnostics — how many items the rule actually moved, and how
        // many times it matched but every stash tab was full. Without these
        // there is no way to tell "not working" apart from "nothing matched".
        private int _autoStashSessionTotal = 0;
        private int _autoStashSessionFull = 0;

        public bool AutoStashEnabled
        {
            get => _autoStashEnabled;
            set => _autoStashEnabled = value;
        }

        private static PrototypeId GetCraftingIngredientDefaultsRef()
        {
            if (s_craftingIngredientDefaultsResolved == false)
            {
                s_craftingIngredientDefaultsResolved = true;
                s_craftingIngredientDefaultsRef = GameDatabase.GetPrototypeRefByName(CraftingIngredientDefaultsPath);

                if (s_craftingIngredientDefaultsRef == PrototypeId.Invalid)
                    AutoStashLogger.Warn($"[AutoStash] {CraftingIngredientDefaultsPath} not present on this game version — auto-stash will never match");
            }

            return s_craftingIngredientDefaultsRef;
        }

        /// <summary>
        /// Returns <see langword="true"/> and sets <paramref name="stashInvRef"/>
        /// when this item should be auto-stashed and a stash tab with room
        /// exists. Returns <see langword="false"/> for every other case so the
        /// caller keeps its normal behaviour.
        /// </summary>
        internal bool TryGetAutoStashInventory(Item item, out PrototypeId stashInvRef)
        {
            stashInvRef = PrototypeId.Invalid;

            if (_autoStashEnabled == false) return false;
            if (item == null) return false;

            PrototypeId ingredientRef = GetCraftingIngredientDefaultsRef();
            if (ingredientRef == PrototypeId.Invalid) return false;

            if (item.IsAPrototype(ingredientRef) == false) return false;

            if (IsAutoStashExcluded(item)) return false;

            // Only general stash tabs — avatar-specific and team-up-gear tabs
            // reject non-matching items, and a rejection here would send the
            // item to the delivery box instead of the bag.
            List<PrototypeId> unlockedStashRefs = new();
            if (_player.GetStashInventoryProtoRefs(unlockedStashRefs, getLocked: false, getUnlocked: true) == false)
                return false;

            foreach (PrototypeId invRef in unlockedStashRefs)
            {
                Inventory inventory = _player.GetInventoryByRef(invRef);
                if (inventory == null) continue;

                var stashProto = GameDatabase.GetPrototype<PlayerStashInventoryPrototype>(invRef);
                if (stashProto == null) continue;
                if (stashProto.Category != InventoryCategory.PlayerStashGeneral) continue;

                // Ask the inventory whether this specific item fits rather than
                // comparing counts — a stackable mat can merge into an existing
                // stack even when the tab has no free slot.
                if (inventory.IsSlotAvailableForEntity(item, allowStacking: true))
                {
                    stashInvRef = invRef;
                    return true;
                }
            }

            // Matched the rule but nothing had room — the item will fall
            // through to the bag. Worth counting so `autostash` can say so
            // instead of looking silently broken.
            _autoStashSessionFull++;
            return false;
        }


        // Prototype-path fragments for ingredients that should NOT be auto-stashed
        // even though they descend from CraftingIngredient.defaults.
        //
        // Mystical Energies is classed as an ingredient in the data but is
        // spent like a currency, so sweeping it into the stash on drop hides
        // income the player wants to see land. Matched on the prototype path
        // rather than the display name so it holds across game versions and
        // localizations.
        private static readonly string[] AutoStashExcludedPathFragments =
        {
            "MysticalEnergies",
        };

        /// <summary>
        /// True when an item matches the ingredient rule but is deliberately
        /// left alone. Only consulted after the ingredient test passes, so the
        /// string comparison never runs on ordinary drops.
        /// </summary>
        private static bool IsAutoStashExcluded(Item item)
        {
            string path = GameDatabase.GetPrototypeName(item.PrototypeDataRef);
            if (string.IsNullOrEmpty(path)) return false;

            foreach (string fragment in AutoStashExcludedPathFragments)
            {
                if (path.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Human-readable diagnostic: whether the rule can fire at all on this
        /// game version, how much stash room is available, and what it has done
        /// this session.
        /// </summary>
        public string GetAutoStashStatus()
        {
            System.Text.StringBuilder sb = new();

            sb.Append(_autoStashEnabled ? "Auto-stash: ON" : "Auto-stash: OFF");

            PrototypeId ingredientRef = GetCraftingIngredientDefaultsRef();
            if (ingredientRef == PrototypeId.Invalid)
            {
                sb.Append(" | ERROR: crafting-ingredient prototype missing on this game version — the rule can never match.");
                return sb.ToString();
            }

            int tabs = 0;
            int freeSlots = 0;
            List<PrototypeId> unlockedStashRefs = new();
            if (_player.GetStashInventoryProtoRefs(unlockedStashRefs, getLocked: false, getUnlocked: true))
            {
                foreach (PrototypeId invRef in unlockedStashRefs)
                {
                    Inventory inventory = _player.GetInventoryByRef(invRef);
                    if (inventory == null) continue;

                    var stashProto = GameDatabase.GetPrototype<PlayerStashInventoryPrototype>(invRef);
                    if (stashProto == null || stashProto.Category != InventoryCategory.PlayerStashGeneral) continue;

                    tabs++;
                    freeSlots += inventory.CapacityRemaining;
                }
            }

            sb.Append($" | {tabs} general stash tab(s), {freeSlots} free slot(s)");
            sb.Append($" | stashed this session: {_autoStashSessionTotal}");

            if (_autoStashSessionFull > 0)
                sb.Append($" | {_autoStashSessionFull} went to your bag because stash was full");

            if (tabs == 0)
                sb.Append(" | WARNING: no unlocked general stash tab — nothing can be auto-stashed.");

            return sb.ToString();
        }

        /// <summary>
        /// Picks the inventory a ground-picked-up item should land in.
        ///
        /// Ground pickup (_player.PlayerConnection.OnPickupInteraction) does NOT go
        /// through <see cref="AcquireItem"/> — it writes straight to the
        /// General inventory — so it needs its own entry point. That path is
        /// the common case for loot, so without this hook auto-stash, the gear
        /// alert and the farm tracker would only ever see given/mission/vendor
        /// items and look broken during normal play.
        /// </summary>
        public Inventory ResolvePickupInventory(Item item, out bool autoStashed)
        {
            autoStashed = false;

            if (TryGetAutoStashInventory(item, out PrototypeId stashInvRef))
            {
                Inventory stash = _player.GetInventoryByRef(stashInvRef);
                if (stash != null)
                {
                    autoStashed = true;
                    return stash;
                }
            }

            return _player.GetInventory(InventoryConvenienceLabel.General);
        }

        /// <summary>
        /// Pulls a just-spawned loot item straight into a stash tab, so matched
        /// materials never reach the ground and never have to be picked up.
        ///
        /// Called from LootManager.SpawnItemInternal, i.e. at DROP time. The
        /// pickup-path hook stays as a backstop for items that were already on
        /// the ground, or that dropped while the feature was off.
        ///
        /// Fail-open, same as the rest of auto-stash: any failure returns false
        /// and the item spawns on the ground exactly as it would have. This can
        /// never destroy or withhold a drop.
        /// </summary>
        public bool TryAutoCollectLoot(Item item)
        {
            if (item == null) return false;
            if (TryGetAutoStashInventory(item, out PrototypeId stashInvRef) == false) return false;

            Inventory stash = _player.GetInventoryByRef(stashInvRef);
            if (stash == null) return false;

            if (item.ChangeInventoryLocation(stash) != InventoryResult.Success)
                return false;

            // The item went straight to a container, so the instancing
            // restriction that gates ground pickup is now meaningless.
            item.Properties.RemoveProperty(PropertyEnum.RestrictedToPlayerGuid);
            item.SetRecentlyAdded(true);

            OnItemAcquired(item, true);
            return true;
        }

        /// <summary>
        /// Post-acquisition hooks shared by every path that gives a player an
        /// item, so a new acquisition path only has to call one thing.
        /// </summary>
        public void OnItemAcquired(Item item, bool autoStashed)
        {
            // Compare first: the verdict is stored on the drop so the Farm
            // Session tool can mark the row. A chat line alone tells you
            // something good dropped but not which one it was.
            string verdict = CheckGearUpgrade(item, out string replacesItemName);

            RecordFarmDrop(item, verdict, replacesItemName);

            if (autoStashed)
                OnItemAutoStashed();
        }

        /// <summary>
        /// Records one auto-stashed item and emits a throttled summary banner.
        /// </summary>
        private void OnItemAutoStashed()
        {
            _autoStashPendingCount++;
            _autoStashSessionTotal++;

            long nowMs = (long)_player.Game.CurrentTime.TotalMilliseconds;
            if (nowMs < _autoStashNextBannerMs) return;

            _autoStashNextBannerMs = nowMs + AutoStashBannerIntervalMs;

            int count = _autoStashPendingCount;
            _autoStashPendingCount = 0;

            string plural = count == 1 ? "material" : "materials";
            SendAutoStashBanner($"📦 Auto-stashed {count} crafting {plural}.");
        }

        /// <summary>
        /// Pushes a line into the player's in-game chat. Public so the command
        /// handler can confirm in-game even when the command was typed in the
        /// OmegaDev2 web console — that path returns its result string to the
        /// web caller, so without this the player sees nothing on screen.
        /// </summary>
        /// <summary>
        /// Single unadorned chat line. Delegates to Player's banner primitive
        /// rather than re-implementing the message plumbing — this used to be a
        /// second copy of Player.SendBannerLine.
        /// </summary>
        public void SendAutoStashBanner(string text) => _player.SendBannerLine(text);


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
        private void RecordFarmDrop(Item item, string verdict, string replacesItemName)
        {
            if (item == null) return;

            PrototypeId regionRef = _player.CurrentAvatar?.Region?.PrototypeDataRef ?? PrototypeId.Invalid;

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
                _player.Id,
                _player.GetName(),
                item.PrototypeDataRef,
                rarityRef,
                item.Properties[PropertyEnum.ItemLevel],
                regionRef,
                (long)_player.Game.CurrentTime.TotalMilliseconds,
                displayName,
                rarityName,
                verdict,
                iconPath,
                specialTier,
                replacesItemName);
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
        /// <param name="replacesItemName">
        /// Set to the equipped item's display name whenever the drop is an
        /// upgrade over something you're actually wearing (empty slot sets
        /// this to null instead — there's nothing to name). Lets the Farm
        /// Session tool say "replaces: X" instead of just "UPGRADE".
        /// </param>
        private string CheckGearUpgrade(Item item, out string replacesItemName)
        {
            replacesItemName = null;

            if (item == null) return "";

            Avatar avatar = _player.CurrentAvatar;
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
            Item equipped = FindWeakestEquippedInSlotFamily(item, avatar, avatarProto, slot);

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

                // Stat accumulation stays here (it needs live Item entities);
                // the decision itself lives in GearVerdict, where it can be
                // tested without a Player, an Avatar or a loaded GameDatabase.
                Dictionary<PropertyId, float> newStats = new();
                Dictionary<PropertyId, float> oldStats = new();
                item.AccumulateAffixStats(newStats);
                equipped.AccumulateAffixStats(oldStats);

                GearVerdict.Comparison cmp = GearVerdict.CompareStats(newStats, oldStats);
                GearVerdict.Kind verdict = GearVerdict.Decide(cmp, newTier, oldTier, newLevel, oldLevel);

                switch (verdict)
                {
                    case GearVerdict.Kind.UpgradeByStats:
                        message = $"🟢 UPGRADE — {itemName} (lv{newLevel}) for {SlotName(slot)} · up on {cmp.Better} stat(s), down on {cmp.Worse}. "
                                + $"Replaces {ResolveItemName(equipped)} (lv{oldLevel}).";
                        tag = "upgrade";
                        replacesItemName = ResolveItemName(equipped);
                        break;

                    case GearVerdict.Kind.UpgradeByRank:
                        message = $"🟢 UPGRADE — {itemName} ({RarityName(item)} lv{newLevel}) for {SlotName(slot)} · "
                                + $"outranks {ResolveItemName(equipped)} ({RarityName(equipped)} lv{oldLevel}). "
                                + "Stats couldn't be compared — check the tooltip.";
                        tag = "upgrade";
                        replacesItemName = ResolveItemName(equipped);
                        break;

                    default:
                        // Compared and lost. No middle verdict by design.
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
        /// <see langword="null"/> when the family has an UNLOCKED free slot.
        ///
        /// A free slot returns null on purpose: if you have three artifacts
        /// unlocked and two equipped, the next artifact costs you nothing, so
        /// it is an upgrade regardless of its stats. Capacity is read live per
        /// assignment (Inventory.GetCapacity(), same call the equip path
        /// itself uses) rather than a hardcoded "4 artifact slots" — artifact
        /// (and some gear) slots unlock progressively via
        /// AvatarEquipInventoryAssignmentPrototype.UnlocksAtCharacterLevel,
        /// same check Avatar.GetEquipmentInventoryAvailableStatus() uses. A
        /// slot you haven't unlocked yet doesn't exist for this character —
        /// counting it as "empty and ready" made a drop read as a free
        /// upgrade into a slot you literally cannot equip anything into
        /// yet, while leveling.
        ///
        /// "Weakest" is (rarity tier, item level) — deliberately not the affix
        /// comparison, because that is not a total order (A can beat B, B beat
        /// C, and C beat A), so it cannot pick a minimum. This only chooses
        /// WHICH item to compare against; the real verdict is still the affix
        /// comparison against the item chosen here.
        /// </summary>
        private Item FindWeakestEquippedInSlotFamily(Item item, Avatar avatar, AvatarPrototype avatarProto, EquipmentInvUISlot slot)
        {
            if (avatarProto.EquipmentInventories == null) return null;

            string family = SlotFamily(slot);
            bool isArtifact = family == "artifact";

            Item weakest = null;
            int weakestTier = int.MaxValue;
            int weakestLevel = int.MaxValue;
            int occupied = 0;
            int unlockedCapacity = 0;

            // An equipped item the drop legally CANNOT sit alongside. If one
            // exists it is the only thing this drop could ever replace, so it
            // wins over both the free-slot shortcut and the weakest-item pick.
            Item blocker = null;

            foreach (AvatarEquipInventoryAssignmentPrototype assignment in avatarProto.EquipmentInventories)
            {
                if (assignment?.Inventory == null) continue;
                if (SlotFamily(assignment.UISlot) != family) continue;

                // Still leveling — this slot isn't available yet, so it does
                // NOT count as free capacity. Skip it entirely rather than
                // reading its (empty) inventory.
                if (avatar.CharacterLevel < assignment.UnlocksAtCharacterLevel) continue;

                Inventory inv = avatar.GetInventoryByRef(assignment.Inventory.DataRef);
                if (inv == null) continue;

                unlockedCapacity += inv.GetCapacity();

                foreach (var entry in inv)
                {
                    Item equipped = _player.Game.EntityManager.GetEntity<Item>(entry.Id);
                    if (equipped == null) continue;

                    occupied++;

                    // Mirrors Avatar.ValidateEquipmentChange(): two of the SAME
                    // artifact can never be worn together (InvalidTwoOfSameArtifact,
                    // matched on PrototypeDataRef), and keyword-conflicting items
                    // are rejected by CanBeEquippedWithItem(). In either case a
                    // free slot elsewhere is irrelevant — the drop can only get
                    // equipped by displacing THIS item, so it has to beat it.
                    if (blocker == null)
                    {
                        bool sameArtifact = isArtifact && item.PrototypeDataRef == equipped.PrototypeDataRef;
                        if (sameArtifact || item.CanBeEquippedWithItem(equipped) == false)
                            blocker = equipped;
                    }

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

            // Can't be worn next to something already equipped — that item is
            // the forced comparison target, empty slots notwithstanding.
            if (blocker != null) return blocker;

            // Room in an unlocked slot — treat it as an empty slot.
            if (occupied < unlockedCapacity) return null;

            return weakest;
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
