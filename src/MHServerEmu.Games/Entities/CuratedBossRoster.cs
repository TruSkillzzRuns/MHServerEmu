using System;
using System.Collections.Generic;
using System.Linq;

namespace MHServerEmu.Games.Entities
{
    /// <summary>
    /// Curated list of real, recognizable boss/villain names — cross-referenced
    /// against itembase.mhbugle.com's Villains list (156 entries, 2026-07-26,
    /// user-authorized one-off manual pull) minus 11 Raid-location entries
    /// (Sisters of Magma, Surtur, Stark Sentinels, Red Skull Onslaught,
    /// Overseer Slag, Monolith, Lord Brimstone, Lady Hellfire — excluded per
    /// user request, "don't include bosses from raids") and 3 training-dummy
    /// entries (not real bosses). 142 names.
    ///
    /// Used to narrow the raw "everything under /Bosses/ with a combat brain"
    /// pool (687 entries, mostly per-event/per-chapter reskins like
    /// "BrooklynEventKraven"/"BrooklynEventMODOKWalkerV2") down to the actual
    /// recognizable villain roster players expect, for the OmegaDev2 Boss
    /// Roster panel and the Endless Wave periodic-boss feature.
    ///
    /// Matching is by normalized name (letters/digits only, lowercased) since
    /// this engine's internal PrototypeId leaf names are PascalCase/no-spaces
    /// ("DoctorDoom") while the site's display names have spaces/punctuation
    /// ("Doctor Doom", "M.O.D.O.K."). Some entries may not resolve if the
    /// internal leaf name differs more substantially from the display name
    /// (e.g. numbered/versioned variants) — not verified exhaustively.
    /// </summary>
    public static class CuratedBossRoster
    {
        private static readonly string[] s_rawNames =
        {
            // "Apocalypse" added 2026-07-30 then removed same day -- his real
            // prototype (Entity/Characters/Bosses/Apocolypse/Apocalypse.prototype)
            // has a Radius 400/HeightFromCenter 400 bounding capsule (~5x a normal
            // large boss like Doctor Doom's 80/70), so standalone spawns fail with
            // "no space found" in anything but a very large open region. Not
            // practical for the Boss Roster/Endless Wave spawn tools as-is.
            // Real prototype is "MidtownEventCloneWolverine" -- word order reversed
            // vs. the display name. Same reversed-order issue affects every
            // "X Clone" entry below (Cyclops/Colossus/Big Time/Back-in-Black).
            "Clone Wolverine", "Wizard", "Winter Soldier", "War X-Skrull", "War Machine", "Vulture",
            "Very Tenacious Skrull Cmdr.", "Very Protective Skrull Cmdr.", "Very Dangerous Skrull Cmdr.",
            "Very Cold Skrull Cmdr.", "Very Bloodthirsty Skrull Cmdr.", "Very Agressive Skrull Cmdr.",
            // Real prototype is "HoodCH2"/"HoodCH8" etc. -- no "The" prefix internally.
            // 2026-07-30: SelectCanonical now prefers non-/PVEInstances/ candidates
            // (see its doc comment), so "Venom"/"Sabretooth" below no longer silently
            // resolve to the broken EG01Venom/EG01Sabretooth variants -- they now
            // pick VenomCH01/SabretoothOMCH7 and similar working candidates instead.
            "Venom", "Ultron Prime", "Ulrik of Myrkvidr", "Tombstone", "Hood", "The Deceiver",
#if GAME_VERSION_1_48
            // Re-added 2026-07-30, 1.48-only, same reasoning as "Mole Man" above:
            // 1.48 has a clean root-level Entity/Characters/Bosses/ToadOMCH6.prototype,
            // not just the confirmed-broken PVEInstances/EG01Toad.prototype. Report back
            // if it still doesn't render and this gets reverted.
            "Toad",
#endif
            // "Toad" removed 2026-07-27 on 1.52/1.53 — confirmed live doesn't render there.
            "The \"Business\"", "Tenacious Skrull Commander", "Taskmaster", "Superior Spider-Clone",
            // Real prototypes are "StarkTechSentinelBossA/B" -- "Tech" breaks the
            // "Stark Sentinel" substring match.
            "Starktech Sentinel", "Skrull X-23", "Skrull Thor", "Skrull Punisher", "Skrull Psylocke",
            "Skrull Nick Fury", "Skrull Ms Marvel", "Skrull Luke Cage", "Skrull Iron Fist", "Skrull Elektra",
            "Skrull Cyclops", "Skrull Captain America", "Sister of Magma", "Shooter McPackin", "Shocker",
            "Sauron", "Sabretooth", "Run-Gun", "Rock Troll Gladiator", "Rhino", "Red Skull", "Red Hjalmrun",
            "Pyro", "Protective Skrull Commander", "Predator X", "Overseer Pismis", "Overseer Orionis",
            "Overseer Cephei", "Njordlaugur Nightaim", "N'astirh", "Mr. Sinister", "Mr. Hyde",
#if GAME_VERSION_1_48
            // Re-added 2026-07-30, 1.48-only. The 2026-07-26 exclusion below
            // was based on Entity/Characters/Bosses/MoleMan.prototype not
            // rendering when spawned standalone -- that test was on 1.52/1.53.
            // On 1.48 this same-named prototype is a clean root-level /Bosses/
            // entry (not under /PVEInstances/ like the confirmed-broken Toad
            // variant), so it's kept enabled here pending live confirmation;
            // report back if it still doesn't render and this gets reverted.
            "Mole Man",
#endif
            // "Mole Man" removed 2026-07-26 on 1.52/1.53 — confirmed live doesn't render there.
            "Mindless Titan", "MGH Cook", "Megadactyl", "Mega-Sentinel", "Mandarin", "Man-Ape", "Malekith",
            "Magneto", "Madame HYDRA", "M.O.D.O.K.", "Loki", "Lizard", "Living Laser", "Lavaheart",
            // Real prototype is "HightownEventKirigi" -- "the Undying" suffix breaks the match.
            "Lady Deathstrike", "Kurse", "Krong the Mighty", "Kronan Arcanist", "Kraven", "Kirigi",
            // Real prototypes name Iron Legionnaires by weapon type, not a 01-05
            // numbering scheme: IronLegionnaireBlasterBase/JackhammerBase/
            // LauncherBase/MelterBase/ScrapperBase (5 distinct real bosses).
            "Kingpin", "Kaecilius", "Juggernaut", "Iron Man",
            "Iron Legionnaire Blaster", "Iron Legionnaire Jackhammer",
            "Iron Legionnaire Launcher", "Iron Legionnaire Melter", "Iron Legionnaire Scrapper",
            "Invading Mindless Titan",
            "Infernal War Skrull", "Hybrid Doctor Schramm", "Hybrid Agent Stephen Gay", "Hybrid Agent Shiue",
            "Hybrid Agent Donais", "Hulk", "High Commander Brevik", "Herald of Ash", "Grim Reaper",
            "Green Goblin", "Gorgon", "General Kl'rt", "Future Foundation Clone", "Frost Giant Glacial Lord",
            "Fist of N'astirh, Limbo Demon", "Falcon", "Extra-Dimensional Warlord", "Extra-Dimensional Overseer",
            "Extra-Dimensional Harbinger", "Extra-Dimensional Beast", "Ends of the Earth Clone", "Elektra",
            "Electro", "Einvarr of the Hallows", "Doombot", "Doctor Octopus",
            // Real prototypes abbreviate to "Dr" (DrDoomPhase1/2/3), not "Doctor" --
            // "Doctor Doom" never matched on any version. Confirmed via live
            // /webapi/bossroster/catalog: 0 Doom entries resolved on 1.48 or 1.53
            // before this fix.
            "Dr Doom",
            // Reversed word order vs. real prototypes (MidtownEventCloneCyclops /
            // MidtownEventCloneColossus) -- same issue as "Clone Wolverine" above.
            "Dangerous Skrull Commander", "Clone Cyclops", "Crossbones", "Cosmic War Skrull", "Cosmic Doop",
            "Clone Colossus", "Charlie Foxtrot", "Captain Torog", "Captain Sumac", "Captain Bolger",
            "Captain America", "Cable", "Bullseye", "Brood Ship Commander", "Bonebreaker", "Blob",
            // Real prototypes are "BrooklynEventCloneBigTimeRed/Green/Blue" and
            // "BrooklynEventCloneBackInBlack" -- reversed order and no "Spider" segment.
            "Black Panther", "Black Cat", "Clone Big Time Red", "Clone Big Time Green",
            "Clone Big Time Blue", "Batroc", "Clone Back In Black", "Avengers War Skrull",
            "Ata-Boy", "Anglaugur Skulleater", "All-Father Brevik", "Agressive Skrull Commander",
        };

        private static readonly List<string> s_normalized = s_rawNames.Select(Normalize).ToList();

        private static string Normalize(string s)
            => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        /// <summary>
        /// Given every /Bosses/ candidate that already passed the base
        /// filters (icon, combat brain, not test/debug/raid), picks exactly
        /// ONE representative per curated name — real internal leaf names
        /// wrap each villain in chapter/difficulty-tier prefixes
        /// ("DrDoomPhase1", "EGD09CSBSabretooth", "GreenGoblinCH00"), so
        /// matching is by substring containment (normalized curated name
        /// found inside the normalized leaf name), not exact equality —
        /// confirmed live 2026-07-26 that exact matching only found 30/142
        /// curated names, while the underlying content clearly exists under
        /// prefixed/suffixed internal names. Substring matching alone would
        /// bring back the "too many near-duplicate variants" problem this
        /// whole curation exists to solve (every chapter/difficulty variant
        /// would qualify), so for each curated name only the SHORTEST
        /// matching leaf is kept — shorter leaf names consistently turned
        /// out to be the plain/base version rather than a specific
        /// phase/difficulty/event reskin in spot checks (KravenBase,
        /// GreenGoblinBase, DangerRoomMagneto, DrDoomPhase1, etc.).
        /// </summary>
        /// <param name="pathSelector">
        /// Optional full-path accessor. When provided, candidates under
        /// /PVEInstances/ are only picked as a last resort — confirmed live
        /// (Toad, Mole Man) that content in that subtree depends on scripted
        /// mission dressing and doesn't render when spawned standalone.
        /// Without this the shortest-leaf rule alone can silently pick a
        /// broken instance-locked variant just because its name happens to be
        /// short (confirmed: "Sabretooth"/"Venom" were resolving to
        /// EG01Sabretooth/EG01Venom over the working SabretoothOMCH7/VenomCH01
        /// candidates for exactly this reason).
        /// </param>
        public static List<T> SelectCanonical<T>(List<T> candidates, Func<T, string> leafSelector, Func<T, string> pathSelector = null)
        {
            var best = new Dictionary<string, (int LeafLength, bool InstanceLocked, T Item)>();
            foreach (T candidate in candidates)
            {
                string leaf = leafSelector(candidate);
                string normLeaf = Normalize(leaf);

                string bestCuratedMatch = null;
                foreach (string curated in s_normalized)
                {
                    if (normLeaf.Contains(curated) && (bestCuratedMatch == null || curated.Length > bestCuratedMatch.Length))
                        bestCuratedMatch = curated;
                }
                if (bestCuratedMatch == null) continue;

                string path = pathSelector?.Invoke(candidate);
                bool instanceLocked = path != null && path.IndexOf("/PVEInstances/", StringComparison.OrdinalIgnoreCase) >= 0;

                bool better;
                if (best.TryGetValue(bestCuratedMatch, out var existing) == false)
                    better = true;
                else if (existing.InstanceLocked != instanceLocked)
                    better = existing.InstanceLocked; // a non-instance-locked candidate always beats a locked one
                else
                    better = leaf.Length < existing.LeafLength;

                if (better)
                    best[bestCuratedMatch] = (leaf.Length, instanceLocked, candidate);
            }
            return best.Values.Select(v => v.Item).ToList();
        }
    }
}
