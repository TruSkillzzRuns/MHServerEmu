using System;
using System.Collections.Generic;
using MHServerEmu.Core.Logging;
using MHServerEmu.Games.GameData;

namespace MHServerEmu.Games.Entities
{
    // Wave Director run history — one JSON file per account, same shape as
    // Player.PhantomHero.cs's PhantomSquads file. Purely informational (past
    // runs shown in the app); never read back into an active run.
    public partial class Player
    {
        private static readonly Logger WaveHistoryLogger = LogManager.CreateLogger();

        private const int WaveHistoryMaxCount = 50;

        public sealed class WaveRunHistoryEntry
        {
            public long StartedAtMs { get; set; }
            public long EndedAtMs { get; set; }
            public int WavesCompleted { get; set; }
            public int TotalWaves { get; set; }
            public int Kills { get; set; }
            public int SpawnedTotal { get; set; }
            public bool Completed { get; set; }
            public string ArenaRegionRef { get; set; }
        }

        private string GetWaveHistoryFilePath()
            => System.IO.Path.Combine(MHServerEmu.Core.Helpers.FileHelper.DataDirectory, "WaveHistory", $"0x{DatabaseUniqueId:X}.json");

        private List<WaveRunHistoryEntry> LoadWaveHistoryFile()
        {
            try
            {
                string path = GetWaveHistoryFilePath();
                if (System.IO.File.Exists(path) == false) return new();
                var loaded = System.Text.Json.JsonSerializer.Deserialize<List<WaveRunHistoryEntry>>(System.IO.File.ReadAllText(path));
                return loaded ?? new();
            }
            catch (Exception ex)
            {
                WaveHistoryLogger.Warn($"[WaveDirector:History] load failed for {this}: {ex.Message}");
                return new();
            }
        }

        private bool SaveWaveHistoryFile(List<WaveRunHistoryEntry> entries)
        {
            try
            {
                string path = GetWaveHistoryFilePath();
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(entries,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return true;
            }
            catch (Exception ex)
            {
                WaveHistoryLogger.Warn($"[WaveDirector:History] save failed for {this}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Append one run's result, evicting the oldest past the cap. Called from Player.WaveDirector.cs.</summary>
        private void LogWaveRunHistory(bool completed)
        {
            try
            {
                var entry = new WaveRunHistoryEntry
                {
                    StartedAtMs = _waveRunStartMs,
                    EndedAtMs = WaveNowMs,
                    WavesCompleted = completed ? _waveDefs.Count : _waveIndex,
                    TotalWaves = _waveDefs.Count,
                    Kills = _waveKills,
                    SpawnedTotal = _waveSpawnedTotal,
                    Completed = completed,
                    ArenaRegionRef = _waveArenaRegionRef == PrototypeId.Invalid ? null : _waveArenaRegionRef.GetName(),
                };

                var entries = LoadWaveHistoryFile();
                entries.Add(entry);
                while (entries.Count > WaveHistoryMaxCount)
                    entries.RemoveAt(0);
                SaveWaveHistoryFile(entries);
                _waveHistoryLogged = true;
            }
            catch (Exception ex)
            {
                WaveHistoryLogger.Warn($"[WaveDirector:History] log failed for {this}: {ex.Message}");
            }
        }

        /// <summary>Most recent runs first, capped at WaveHistoryMaxCount.</summary>
        public List<WaveRunHistoryEntry> GetWaveHistoryForWeb()
        {
            var entries = LoadWaveHistoryFile();
            entries.Reverse();
            return entries;
        }
    }
}
