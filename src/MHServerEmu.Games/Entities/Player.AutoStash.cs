using MHServerEmu.Games.Entities.Inventories;
using MHServerEmu.Games.Entities.Items;
using MHServerEmu.Games.Features;

namespace MHServerEmu.Games.Entities
{
    /// <summary>
    /// Delegation surface for loot assistance — Auto-Stash, the Gear Upgrade
    /// Alert, and Farm Session recording. The implementation lives in
    /// <see cref="LootAssist"/>; this file only forwards to it.
    ///
    /// The members below are kept ON Player deliberately rather than asking
    /// callers to reach through a feature object:
    ///
    ///   - AutoStashEnabled / GearUpgradeAlertEnabled are found by REFLECTION.
    ///     TogglesWebHandler builds the OmegaDev2 console toggle panel by
    ///     scanning Player for public bool properties named &lt;group&gt;Enabled.
    ///     Moving them off Player breaks that panel silently — it would report
    ///     an unknown state rather than fail.
    ///   - Player.PhantomPersist.cs saves and restores them.
    ///   - The !autostash and !gearalert commands read and write them.
    ///
    /// Everything else — the ingredient classification, stash selection, gear
    /// comparison, drop recording — is private to LootAssist.
    /// </summary>
    public partial class Player
    {
        private LootAssist _lootAssist;

        /// <summary>
        /// Created on first use rather than in a constructor: Player has several
        /// construction paths and this keeps the extraction from touching any
        /// of them.
        /// </summary>
        private LootAssist LootAssist { get => _lootAssist ??= new LootAssist(this); }

        public bool AutoStashEnabled
        {
            get => LootAssist.AutoStashEnabled;
            set => LootAssist.AutoStashEnabled = value;
        }

        public bool GearUpgradeAlertEnabled
        {
            get => LootAssist.GearUpgradeAlertEnabled;
            set => LootAssist.GearUpgradeAlertEnabled = value;
        }

        public int GearAlertSessionCount { get => LootAssist.GearAlertSessionCount; }

        /// <summary>Diagnostic for the !autostash command.</summary>
        public string GetAutoStashStatus() => LootAssist.GetAutoStashStatus();

        /// <summary>Pushes a line into this player's in-game chat.</summary>
        public void SendAutoStashBanner(string text) => LootAssist.SendAutoStashBanner(text);

        /// <summary>
        /// Whether this item should be auto-stashed, and into which tab.
        /// Used by AcquireItem, which selects the destination itself rather
        /// than going through ResolvePickupInventory.
        /// </summary>
        internal bool TryGetAutoStashInventory(Item item, out GameData.PrototypeId stashInvRef)
            => LootAssist.TryGetAutoStashInventory(item, out stashInvRef);

        /// <summary>
        /// Picks the inventory a ground-picked-up item should land in.
        /// Called from PlayerConnection.OnPickupInteraction.
        /// </summary>
        internal Inventory ResolvePickupInventory(Item item, out bool autoStashed)
            => LootAssist.ResolvePickupInventory(item, out autoStashed);

        /// <summary>
        /// Pulls a just-spawned loot item straight into a stash tab, so matched
        /// materials never reach the ground. Called from
        /// LootManager.SpawnItemInternal at drop time.
        /// </summary>
        public bool TryAutoCollectLoot(Item item) => LootAssist.TryAutoCollectLoot(item);

        /// <summary>
        /// Post-acquisition hooks shared by every path that gives a player an
        /// item, so a new acquisition path only has to call one thing.
        /// </summary>
        internal void OnItemAcquired(Item item, bool autoStashed)
            => LootAssist.OnItemAcquired(item, autoStashed);
    }
}
