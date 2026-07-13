using System.Collections.Generic;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Games.GameData;

namespace MHServerEmu.Games.Entities
{
    // Skill Rotation — per-hero preferred power. When a phantom hero picks
    // its next attack, the candidate loop in Avatar.PhantomHero.cs asks the
    // hosting player which power (if any) it should reach for first for the
    // avatar's heroRef. If the preferred power is in the candidate list and
    // ready, it wins outright — same tier as an ultimate. Otherwise the
    // default cooldown-weighted selection applies.
    //
    // Persistence via MigrationData.PreferredPowers so preferences survive
    // region hops just like the nemesis roster does.
    public partial class Player
    {
        private readonly Dictionary<ulong, ulong> _preferredPowers = new();

        /// <summary>Read-only view for the web endpoint.</summary>
        public IReadOnlyDictionary<ulong, ulong> PreferredPowers => _preferredPowers;

        /// <summary>
        /// Set the preferred power for a hero. Pass 0 for powerRef to clear
        /// the preference and fall back to default weighted selection.
        /// </summary>
        public void SetPreferredPower(ulong heroRef, ulong powerRef)
        {
            if (heroRef == 0) return;
            if (powerRef == 0) _preferredPowers.Remove(heroRef);
            else _preferredPowers[heroRef] = powerRef;
        }

        /// <summary>Lookup used by the phantom AI candidate loop.</summary>
        public PrototypeId GetPreferredPower(PrototypeId heroRef)
        {
            if (heroRef == PrototypeId.Invalid) return PrototypeId.Invalid;
            return _preferredPowers.TryGetValue((ulong)heroRef, out ulong p)
                ? (PrototypeId)p
                : PrototypeId.Invalid;
        }

        internal void SnapshotPreferredPowersForTransfer(MigrationData mig)
        {
            if (mig == null) return;
            mig.PreferredPowers.Clear();
            foreach (var kvp in _preferredPowers) mig.PreferredPowers[kvp.Key] = kvp.Value;
        }

        internal void RestorePreferredPowersFromMigration(MigrationData mig)
        {
            if (mig == null || mig.PreferredPowers.Count == 0) return;
            _preferredPowers.Clear();
            foreach (var kvp in mig.PreferredPowers) _preferredPowers[kvp.Key] = kvp.Value;
        }
    }
}
