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
            "Wolverine Clone", "Wizard", "Winter Soldier", "War X-Skrull", "War Machine", "Vulture",
            "Very Tenacious Skrull Cmdr.", "Very Protective Skrull Cmdr.", "Very Dangerous Skrull Cmdr.",
            "Very Cold Skrull Cmdr.", "Very Bloodthirsty Skrull Cmdr.", "Very Agressive Skrull Cmdr.",
            "Venom", "Ultron Prime", "Ulrik of Myrkvidr", "Tombstone", "Toad", "The Hood", "The Deceiver",
            "The \"Business\"", "Tenacious Skrull Commander", "Taskmaster", "Superior Spider-Clone",
            "Stark Sentinel", "Skrull X-23", "Skrull Thor", "Skrull Punisher", "Skrull Psylocke",
            "Skrull Nick Fury", "Skrull Ms Marvel", "Skrull Luke Cage", "Skrull Iron Fist", "Skrull Elektra",
            "Skrull Cyclops", "Skrull Captain America", "Sister of Magma", "Shooter McPackin", "Shocker",
            "Sauron", "Sabretooth", "Run-Gun", "Rock Troll Gladiator", "Rhino", "Red Skull", "Red Hjalmrun",
            "Pyro", "Protective Skrull Commander", "Predator X", "Overseer Pismis", "Overseer Orionis",
            "Overseer Cephei", "Njordlaugur Nightaim", "N'astirh", "Mr. Sinister", "Mr. Hyde",
            // "Mole Man" removed 2026-07-26 — confirmed live doesn't render.
            "Mindless Titan", "MGH Cook", "Megadactyl", "Mega-Sentinel", "Mandarin", "Man-Ape", "Malekith",
            "Magneto", "Madame HYDRA", "M.O.D.O.K.", "Loki", "Lizard", "Living Laser", "Lavaheart",
            "Lady Deathstrike", "Kurse", "Krong the Mighty", "Kronan Arcanist", "Kraven", "Kirigi the Undying",
            "Kingpin", "Kaecilius", "Juggernaut", "Iron Man", "Iron Legionnaire 05", "Iron Legionnaire 04",
            "Iron Legionnaire 03", "Iron Legionnaire 02", "Iron Legionnaire 01", "Invading Mindless Titan",
            "Infernal War Skrull", "Hybrid Doctor Schramm", "Hybrid Agent Stephen Gay", "Hybrid Agent Shiue",
            "Hybrid Agent Donais", "Hulk", "High Commander Brevik", "Herald of Ash", "Grim Reaper",
            "Green Goblin", "Gorgon", "General Kl'rt", "Future Foundation Clone", "Frost Giant Glacial Lord",
            "Fist of N'astirh, Limbo Demon", "Falcon", "Extra-Dimensional Warlord", "Extra-Dimensional Overseer",
            "Extra-Dimensional Harbinger", "Extra-Dimensional Beast", "Ends of the Earth Clone", "Elektra",
            "Electro", "Einvarr of the Hallows", "Doombot", "Doctor Octopus", "Doctor Doom",
            "Dangerous Skrull Commander", "Cyclops Clone", "Crossbones", "Cosmic War Skrull", "Cosmic Doop",
            "Colossus Clone", "Charlie Foxtrot", "Captain Torog", "Captain Sumac", "Captain Bolger",
            "Captain America", "Cable", "Bullseye", "Brood Ship Commander", "Bonebreaker", "Blob",
            "Black Panther", "Black Cat", "Big Time Spider-Clone (Red)", "Big Time Spider-Clone (Green)",
            "Big Time Spider-Clone (Blue)", "Batroc", "Back-in-Black Spider-Clone", "Avengers War Skrull",
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
        public static List<T> SelectCanonical<T>(List<T> candidates, Func<T, string> leafSelector)
        {
            var best = new Dictionary<string, (int LeafLength, T Item)>();
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

                if (best.TryGetValue(bestCuratedMatch, out var existing) == false || leaf.Length < existing.LeafLength)
                    best[bestCuratedMatch] = (leaf.Length, candidate);
            }
            return best.Values.Select(v => v.Item).ToList();
        }
    }
}
