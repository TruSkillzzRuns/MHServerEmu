using System;
using System.Collections.Generic;
using MHServerEmu.Core.Logging;

namespace MHServerEmu.Games.Entities
{
    // Wave Director saved plans — one JSON file per account, same shape as
    // Player.PhantomHero.cs's PhantomSquads file. A plan is the full run
    // recipe (waves + all run settings), so "Start" reproduces the run
    // exactly as it was saved.
    public partial class Player
    {
        private static readonly Logger WavePlanLogger = LogManager.CreateLogger();

        private const int WavePlanMaxCount = 20;

        public sealed class WavePlanRecord
        {
            public List<WaveDef> Waves { get; set; } = new();
            public int IntermissionMs { get; set; } = 5000;
            public ulong ArenaRegionRef { get; set; }
            public bool ClearArena { get; set; }
            public bool Loop { get; set; }
            public float CountScalePerWave { get; set; }
            public int LevelBumpPerWave { get; set; }
            public WaveRewardMode RewardMode { get; set; } = WaveRewardMode.None;
            public ulong RewardLootTableRef { get; set; }
        }

        private string GetWavePlanFilePath()
            => System.IO.Path.Combine(MHServerEmu.Core.Helpers.FileHelper.DataDirectory, "WavePlans", $"0x{DatabaseUniqueId:X}.json");

        // WaveDef/WaveEntryDef use public fields (not properties) — everything
        // else in this codebase's JSON files uses property-based DTOs, so the
        // wave-plan file needs IncludeFields explicitly rather than changing
        // those shared classes.
        private static readonly System.Text.Json.JsonSerializerOptions s_wavePlanJsonOptions = new()
        {
            WriteIndented = true,
            IncludeFields = true,
        };

        private Dictionary<string, WavePlanRecord> LoadWavePlanFile()
        {
            try
            {
                string path = GetWavePlanFilePath();
                if (System.IO.File.Exists(path) == false) return new(StringComparer.OrdinalIgnoreCase);
                var loaded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, WavePlanRecord>>(
                    System.IO.File.ReadAllText(path), s_wavePlanJsonOptions);
                return loaded != null ? new(loaded, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                WavePlanLogger.Warn($"[WaveDirector:Plan] load failed for {this}: {ex.Message}");
                return new(StringComparer.OrdinalIgnoreCase);
            }
        }

        private bool SaveWavePlanFile(Dictionary<string, WavePlanRecord> plans)
        {
            try
            {
                string path = GetWavePlanFilePath();
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(plans, s_wavePlanJsonOptions));
                return true;
            }
            catch (Exception ex)
            {
                WavePlanLogger.Warn($"[WaveDirector:Plan] save failed for {this}: {ex.Message}");
                return false;
            }
        }

        private static bool IsValidWavePlanName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 32) return false;
            foreach (char c in name)
                if (char.IsLetterOrDigit(c) == false && c != '_' && c != '-') return false;
            return true;
        }

        public string SaveWavePlan(string name, WavePlanRecord record)
        {
            if (IsValidWavePlanName(name) == false)
                return "Plan names must be 1-32 letters, digits, _ or -.";
            if (record == null || record.Waves == null || record.Waves.Count == 0)
                return "Plan has no waves.";

            var plans = LoadWavePlanFile();
            if (plans.ContainsKey(name) == false && plans.Count >= WavePlanMaxCount)
                return $"Plan limit reached ({WavePlanMaxCount}). Delete one first.";

            plans[name] = record;
            if (SaveWavePlanFile(plans) == false)
                return "Failed to write wave plan file — check server log.";
            return $"Plan '{name}' saved ({record.Waves.Count} wave(s)).";
        }

        public string DeleteWavePlan(string name)
        {
            var plans = LoadWavePlanFile();
            if (plans.Remove(name) == false)
                return $"No plan named '{name}'.";
            return SaveWavePlanFile(plans) ? $"Plan '{name}' deleted." : "Failed to write wave plan file — check server log.";
        }

        public List<string> ListWavePlans()
        {
            var plans = LoadWavePlanFile();
            var names = new List<string>(plans.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>Start a run using a saved plan's full recipe.</summary>
        public string StartWavePlan(string name)
        {
            var plans = LoadWavePlanFile();
            if (plans.TryGetValue(name, out WavePlanRecord record) == false || record == null)
                return $"No plan named '{name}'.";

            return StartWaveRun(record.Waves, record.IntermissionMs, record.ArenaRegionRef, record.ClearArena,
                record.Loop, record.CountScalePerWave, record.LevelBumpPerWave, record.RewardMode, record.RewardLootTableRef);
        }
    }
}
