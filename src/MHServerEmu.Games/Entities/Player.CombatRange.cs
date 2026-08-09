using System;
using System.Collections.Generic;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Games.GameData;

namespace MHServerEmu.Games.Entities
{
    /// <summary>
    /// Per-hero combat range preference for phantom heroes.
    /// </summary>
    /// <remarks>
    /// Phantom movement decides how close to close in via
    /// Avatar.PhantomHero.cs's ComputePhantomFollowStopDist, whose default rule
    /// is "if the kit has ANY ready melee power, walk all the way in." That rule
    /// exists because mixed-kit melee heroes otherwise parked at ranged distance
    /// and could structurally never satisfy the melee range gate (fixed
    /// 2026-07-19).
    ///
    /// The side effect is that ranged heroes carrying a couple of melee options
    /// — Iron Man has JetThrustPunch/BasicDentingPunch at range 90 alongside
    /// UniBeam at 1000 — get classified as brawlers and hug enemies.
    ///
    /// Auto-detecting this from game data was investigated and rejected: melee/
    /// ranged power counts (Thing 4/10 vs Iron Man 3/12), max range (Colossus
    /// reaches 1000 via gap-closers, further than Thing), and base damage (every
    /// hero reads 3024 for both at level 60) all fail to separate them, and
    /// AvatarPrototype carries no role/archetype field. Rather than ship a rule
    /// that fixes one hero by misclassifying another, the choice is explicit.
    ///
    /// Auto (the default) preserves the existing behavior exactly, so nothing
    /// changes for any hero until the operator opts in.
    ///
    /// Persistence via MigrationData.CombatRangePrefs so preferences survive
    /// region hops, same as Skill Rotation.
    /// </remarks>
    public enum PhantomCombatRangePref
    {
        Auto = 0,
        Melee = 1,
        Ranged = 2,
    }

    public partial class Player
    {
        private readonly Dictionary<ulong, int> _combatRangePrefs = new();

        /// <summary>Read-only view for the web endpoint.</summary>
        public IReadOnlyDictionary<ulong, int> CombatRangePrefs => _combatRangePrefs;

        /// <summary>
        /// Set the combat range preference for a hero. Pass Auto to clear it and
        /// fall back to the default kit-derived behavior.
        /// </summary>
        public void SetCombatRangePref(ulong heroRef, int pref)
        {
            if (heroRef == 0) return;
            if (pref == (int)PhantomCombatRangePref.Auto) _combatRangePrefs.Remove(heroRef);
            else _combatRangePrefs[heroRef] = pref;
        }

        /// <summary>
        /// Lookup used by the phantom movement logic. Checks this player's own
        /// explicit override first (set via OmegaDev2 / SetCombatRangePref),
        /// then falls back to the server-wide per-hero AI profile file (Data/
        /// Game/PhantomHeroes/AIProfiles/&lt;Hero&gt;.json — see
        /// Player.PhantomAIProfiles.cs), then finally the kit-derived Auto
        /// heuristic if neither is set.
        /// </summary>
        public PhantomCombatRangePref GetCombatRangePref(PrototypeId heroRef)
        {
            if (heroRef == PrototypeId.Invalid) return PhantomCombatRangePref.Auto;

            if (_combatRangePrefs.TryGetValue((ulong)heroRef, out int p))
                return (PhantomCombatRangePref)p;

            PhantomHeroAIProfile fileProfile = GetPhantomAIProfile(heroRef);
            if (fileProfile != null && Enum.TryParse(fileProfile.CombatRangePref, true, out PhantomCombatRangePref filePref))
                return filePref;

            return PhantomCombatRangePref.Auto;
        }

        internal void SnapshotCombatRangePrefsForTransfer(MigrationData mig)
        {
            if (mig == null) return;
            mig.CombatRangePrefs.Clear();
            foreach (var kvp in _combatRangePrefs) mig.CombatRangePrefs[kvp.Key] = kvp.Value;
        }

        internal void RestoreCombatRangePrefsFromMigration(MigrationData mig)
        {
            if (mig == null || mig.CombatRangePrefs.Count == 0) return;
            _combatRangePrefs.Clear();
            foreach (var kvp in mig.CombatRangePrefs) _combatRangePrefs[kvp.Key] = kvp.Value;
        }
    }
}
