using MHServerEmu.Core.Logging;
using MHServerEmu.Games.Entities.Inventories;
using MHServerEmu.Games.Entities.Items;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Properties;

namespace MHServerEmu.Games.Entities
{
    /// <summary>
    /// Auto-Stash on pickup — routes crafting materials straight into a stash
    /// tab instead of the general bag.
    ///
    /// Why this hooks <see cref="Player.AcquireItem"/> rather than a pickup
    /// event: AcquireItem is the single funnel every acquisition path goes
    /// through (ground pickup, mission reward, vendor purchase, delivery box
    /// drain), so one hook covers all of them and there is no second path that
    /// can bypass the rule.
    ///
    /// Classification is hierarchy-based, not path-string based: every
    /// craftable ingredient in the shipped data descends from
    /// CraftingIngredient.defaults, so <see cref="Entity.IsAPrototype"/>
    /// catches ingredients added by prototype patches too. Verified live on
    /// 1.52 via `lookup item Ingredient` — all hits sit under
    /// Entity/Items/Crafting/Ingredients/ with that shared defaults parent.
    ///
    /// Fail-open by design: if the feature is off, nothing matches, no stash
    /// tab is unlocked, or every tab is full, the item falls through to the
    /// caller's normal inventory selection. Auto-stash can never cause an item
    /// to be lost or refused.
    /// </summary>
    public partial class Player
    {
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
        private bool TryGetAutoStashInventory(Item item, out PrototypeId stashInvRef)
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
            if (GetStashInventoryProtoRefs(unlockedStashRefs, getLocked: false, getUnlocked: true) == false)
                return false;

            foreach (PrototypeId invRef in unlockedStashRefs)
            {
                Inventory inventory = GetInventoryByRef(invRef);
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
            if (GetStashInventoryProtoRefs(unlockedStashRefs, getLocked: false, getUnlocked: true))
            {
                foreach (PrototypeId invRef in unlockedStashRefs)
                {
                    Inventory inventory = GetInventoryByRef(invRef);
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
        /// Ground pickup (PlayerConnection.OnPickupInteraction) does NOT go
        /// through <see cref="AcquireItem"/> — it writes straight to the
        /// General inventory — so it needs its own entry point. That path is
        /// the common case for loot, so without this hook auto-stash, the gear
        /// alert and the farm tracker would only ever see given/mission/vendor
        /// items and look broken during normal play.
        /// </summary>
        internal Inventory ResolvePickupInventory(Item item, out bool autoStashed)
        {
            autoStashed = false;

            if (TryGetAutoStashInventory(item, out PrototypeId stashInvRef))
            {
                Inventory stash = GetInventoryByRef(stashInvRef);
                if (stash != null)
                {
                    autoStashed = true;
                    return stash;
                }
            }

            return GetInventory(InventoryConvenienceLabel.General);
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

            Inventory stash = GetInventoryByRef(stashInvRef);
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
        internal void OnItemAcquired(Item item, bool autoStashed)
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

            long nowMs = (long)Game.CurrentTime.TotalMilliseconds;
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
        public void SendAutoStashBanner(string text)
        {
            if (PlayerConnection == null) return;

            var msg = new Core.Network.ServiceMessage.GroupingManagerMetagameMessage(
                PlayerConnection.PlayerDbId, text, showSender: false);
            Core.Network.ServerManager.Instance.SendMessageToService(
                Core.Network.GameServiceType.GroupingManager, msg);
        }
    }
}
