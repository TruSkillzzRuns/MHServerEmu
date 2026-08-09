using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MHServerEmu.Core.Logging;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Powers;

namespace MHServerEmu.Games.Entities
{
    /// <summary>
    /// One power entry in a generated <see cref="PhantomHeroAIProfile"/> — real
    /// data pulled from the hero's own PowerProgressionTables/PowerProgression,
    /// not guessed. IsMelee is TargetingReachPrototype.Melee (the same flag
    /// Power.IsMelee reads at combat time); Range is Power.GetRange's static
    /// overload evaluated with no owner-specific modifiers (base value).
    /// </summary>
    public class PhantomPowerEntry
    {
        public string Name { get; set; }
        public string Ref { get; set; }
        public bool IsMelee { get; set; }
        public float Range { get; set; }
        public bool IsUltimate { get; set; }
    }

    /// <summary>
    /// Per-hero (or per-team-up) editable AI profile. One file per hero under
    /// Data/Game/PhantomHeroes/AIProfiles/. The generator (see
    /// GeneratePhantomAIProfiles) writes a starting file for every real hero
    /// from their actual power data and NEVER overwrites a file that already
    /// exists, so hand edits persist across regenerations.
    ///
    /// CombatRangePref defaults to "Auto" for every generated file — per
    /// Player.CombatRange.cs's own documented investigation, melee/ranged
    /// power counts, max range, and damage all fail to reliably separate a
    /// true brawler with a couple of ranged options (Thing) from a true
    /// ranged hero with a couple of real melee options (Iron Man). The
    /// Powers list below exists so a human looking at this file has the real
    /// facts to make that call per hero, instead of the AI guessing.
    /// </summary>
    public class PhantomHeroAIProfile
    {
        public string HeroName { get; set; }
        public string HeroRef { get; set; }
        public bool IsTeamUp { get; set; }

        /// <summary>"Auto" | "Melee" | "Ranged" — see PhantomCombatRangePref. Hand-edit this per hero.</summary>
        public string CombatRangePref { get; set; } = "Auto";

        /// <summary>
        /// "Auto" | "Aggressive" | "Defensive" | "Supportive" | "Reckless" | "Smart".
        /// Overrides the random personality roll in Avatar.PhantomHero.cs's
        /// GetPhantomPersonality when set to anything but Auto.
        /// </summary>
        public string PersonalityOverride { get; set; } = "Auto";

        public float DetectedBestRange { get; set; }
        public bool DetectedHasMelee { get; set; }

        public string Notes { get; set; } = "";

        public List<PhantomPowerEntry> Powers { get; set; } = new();
    }

    public partial class Player
    {
        private static readonly Logger PhantomAIProfileLogger = LogManager.CreateLogger();

        private const string PhantomAIProfileDir = "Data/Game/PhantomHeroes/AIProfiles";

        private static readonly Dictionary<PrototypeId, PhantomHeroAIProfile> s_phantomAIProfileCache = new();
        private static readonly object s_phantomAIProfileLock = new();
        private static readonly JsonSerializerOptions s_phantomAIProfileJsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        /// <summary>File-safe name for a hero — same sanitization used elsewhere for per-hero data files.</summary>
        private static string SanitizeAIProfileFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        /// <summary>
        /// Builds the AI profile filename from the FULL prototype path, not
        /// just the leaf name. Confirmed live 2026-08-08: using leaf name
        /// alone collided for 7 heroes/team-ups out of 130 on 1.52 alone
        /// (e.g. multiple costume/variant prototypes sharing a base name in
        /// different folders) — whichever one the iterator reached first
        /// silently claimed the file, and the rest were skipped with no
        /// profile of their own at all. Prototype paths are unique by
        /// definition, so building the filename from the whole path (minus
        /// the .prototype suffix, slashes replaced) instead of just its
        /// last segment guarantees no two different heroes can ever
        /// collide on the same file again.
        /// </summary>
        private static string GetAIProfileFileName(string fullPrototypeName)
        {
            string stem = fullPrototypeName;
            if (stem.EndsWith(".prototype")) stem = stem[..^".prototype".Length];
            stem = stem.Replace('/', '_');
            return SanitizeAIProfileFileName(stem) + ".json";
        }

        /// <summary>
        /// Loads (and caches) the AI profile file for a hero, if one exists.
        /// Returns null if no file has been generated for this hero yet —
        /// callers should fall back to the existing Auto-detection behavior.
        /// </summary>
        internal static PhantomHeroAIProfile GetPhantomAIProfile(PrototypeId heroRef)
        {
            if (heroRef == PrototypeId.Invalid) return null;

            lock (s_phantomAIProfileLock)
            {
                if (s_phantomAIProfileCache.TryGetValue(heroRef, out PhantomHeroAIProfile cached))
                    return cached;

                string name = GameDatabase.GetPrototypeName(heroRef);
                if (string.IsNullOrEmpty(name)) return null;

                string path = Path.Combine(PhantomAIProfileDir, GetAIProfileFileName(name));
                if (!File.Exists(path))
                {
                    s_phantomAIProfileCache[heroRef] = null;
                    return null;
                }

                try
                {
                    string json = File.ReadAllText(path);
                    PhantomHeroAIProfile profile = JsonSerializer.Deserialize<PhantomHeroAIProfile>(json, s_phantomAIProfileJsonOptions);
                    s_phantomAIProfileCache[heroRef] = profile;
                    return profile;
                }
                catch (Exception ex)
                {
                    PhantomAIProfileLogger.Warn($"[PhantomAIProfile] failed to load {path}: {ex.Message}");
                    s_phantomAIProfileCache[heroRef] = null;
                    return null;
                }
            }
        }

        /// <summary>
        /// One-time (per server run) generator — walks every real playable
        /// AvatarPrototype and AgentTeamUpPrototype, pulls their ACTUAL granted
        /// powers from PowerProgressionTables/PowerProgression, and writes one
        /// starter JSON file per hero. Existing files are never overwritten, so
        /// this is safe to re-run after adding new heroes without clobbering
        /// hand edits. Triggered by the !phantom aiprofiles generate command.
        /// </summary>
        public static (int written, int skipped, int total) GeneratePhantomAIProfiles()
        {
            Directory.CreateDirectory(PhantomAIProfileDir);

            int written = 0, skipped = 0, total = 0;

            foreach (PrototypeId avatarRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<AvatarPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                AvatarPrototype avatarProto = avatarRef.As<AvatarPrototype>();
                if (avatarProto == null) continue;

                total++;
                if (TryWritePhantomAIProfile(avatarRef, avatarProto.PowerProgressionTables, isTeamUp: false))
                    written++;
                else
                    skipped++;
            }

            foreach (PrototypeId teamUpRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<AgentTeamUpPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                AgentTeamUpPrototype teamUpProto = teamUpRef.As<AgentTeamUpPrototype>();
                if (teamUpProto == null) continue;

                total++;
                if (TryWritePhantomAIProfileTeamUp(teamUpRef, teamUpProto.PowerProgression))
                    written++;
                else
                    skipped++;
            }

            PhantomAIProfileLogger.Info($"[PhantomAIProfile] generation done: {written} written, {skipped} skipped (already existed), {total} total heroes/team-ups");
            return (written, skipped, total);
        }

        private static bool TryWritePhantomAIProfile(PrototypeId heroRef, PowerProgressionTablePrototype[] tables, bool isTeamUp)
        {
            string name = GameDatabase.GetPrototypeName(heroRef);
            if (string.IsNullOrEmpty(name)) return false;

            string leaf = GetPowerLeafName(heroRef);

            string path = Path.Combine(PhantomAIProfileDir, GetAIProfileFileName(name));
            if (File.Exists(path)) return false; // never clobber a hand-edited or already-generated file

            var profile = new PhantomHeroAIProfile
            {
                HeroName = leaf,
                HeroRef = name,
                IsTeamUp = isTeamUp,
            };

            float bestRange = 0f;
            bool hasMelee = false;

            if (tables != null)
            {
                foreach (PowerProgressionTablePrototype table in tables)
                {
                    if (table?.PowerProgressionEntries == null) continue;
                    foreach (PowerProgressionEntryPrototype entry in table.PowerProgressionEntries)
                    {
                        PrototypeId powerRef = entry?.PowerAssignment?.Ability ?? PrototypeId.Invalid;
                        if (powerRef == PrototypeId.Invalid) continue;

                        AddPowerEntryToProfile(profile, powerRef, ref bestRange, ref hasMelee);
                    }
                }
            }

            profile.DetectedBestRange = bestRange;
            profile.DetectedHasMelee = hasMelee;

            try
            {
                string json = JsonSerializer.Serialize(profile, s_phantomAIProfileJsonOptions);
                File.WriteAllText(path, json);
                return true;
            }
            catch (Exception ex)
            {
                PhantomAIProfileLogger.Warn($"[PhantomAIProfile] failed to write {path}: {ex.Message}");
                return false;
            }
        }

        private static bool TryWritePhantomAIProfileTeamUp(PrototypeId teamUpRef, TeamUpPowerProgressionEntryPrototype[] entries)
        {
            string name = GameDatabase.GetPrototypeName(teamUpRef);
            if (string.IsNullOrEmpty(name)) return false;

            string leaf = GetPowerLeafName(teamUpRef);

            string path = Path.Combine(PhantomAIProfileDir, GetAIProfileFileName(name));
            if (File.Exists(path)) return false;

            var profile = new PhantomHeroAIProfile
            {
                HeroName = leaf,
                HeroRef = name,
                IsTeamUp = true,
            };

            float bestRange = 0f;
            bool hasMelee = false;

            if (entries != null)
            {
                foreach (TeamUpPowerProgressionEntryPrototype entry in entries)
                {
                    PrototypeId powerRef = entry?.Power ?? PrototypeId.Invalid;
                    if (powerRef == PrototypeId.Invalid) continue;

                    AddPowerEntryToProfile(profile, powerRef, ref bestRange, ref hasMelee);
                }
            }

            profile.DetectedBestRange = bestRange;
            profile.DetectedHasMelee = hasMelee;

            try
            {
                string json = JsonSerializer.Serialize(profile, s_phantomAIProfileJsonOptions);
                File.WriteAllText(path, json);
                return true;
            }
            catch (Exception ex)
            {
                PhantomAIProfileLogger.Warn($"[PhantomAIProfile] failed to write {path}: {ex.Message}");
                return false;
            }
        }

        private static void AddPowerEntryToProfile(PhantomHeroAIProfile profile, PrototypeId powerRef, ref float bestRange, ref bool hasMelee)
        {
            PowerPrototype powerProto = powerRef.As<PowerPrototype>();
            if (powerProto == null) return;

            // Same real, data-driven checks the combat code itself uses —
            // Power.IsMelee reads TargetingReachPrototype.Melee; the static
            // Power.GetRange overload evaluates the power's Range field/curve
            // with no owner-specific properties, giving the base value.
            bool isMelee = Power.IsMelee(powerProto);
            float range = Power.GetRange(powerProto, null, null);

            if (isMelee) hasMelee = true;
            else if (range > bestRange) bestRange = range;

            profile.Powers.Add(new PhantomPowerEntry
            {
                Name = GetPowerLeafName(powerRef),
                Ref = GameDatabase.GetPrototypeName(powerRef),
                IsMelee = isMelee,
                Range = range,
                IsUltimate = powerProto.IsUltimate,
            });
        }

        private static string GetPowerLeafName(PrototypeId powerRef)
        {
            string name = GameDatabase.GetPrototypeName(powerRef);
            if (string.IsNullOrEmpty(name)) return name;
            int slash = name.LastIndexOf('/');
            if (slash >= 0) name = name[(slash + 1)..];
            if (name.EndsWith(".prototype")) name = name[..^".prototype".Length];
            return name;
        }
    }
}
