using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.DatabaseAccess.Models;

namespace MHServerEmu.Games.Entities
{
    // Cross-session persistence for our phantom-heroes fork's per-player
    // state: nemesis roster, Bounty Board, preferred-power map, and the
    // Rogue Encounter toggle. Stored as a JSON sidecar per DbGuid under
    // Data/PhantomPersist so we don't have to touch the SQLite schema
    // (which would need a migration for every fork user).
    //
    // Save fires on Player.ExitGame; load fires on Player.OnLoadingScreenFinished
    // once the DbGuid is known. Idempotent — safe to call multiple times.
    public partial class Player
    {
        private static readonly Logger PersistLogger = LogManager.CreateLogger();
        // IncludeFields is REQUIRED here — NemesisEntry/BountyBoardEntry are
        // plain public-field classes (no properties), and System.Text.Json
        // only handles properties by default. Without this, every entry
        // silently round-trips as "{}" (Serialize writes nothing, Deserialize
        // gives back a blank default-constructed instance) — confirmed live
        // 2026-08-02: a saved-then-reloaded board came back as 6 slots with
        // HeroRef=0/Rank=0, since a region transfer (not just a full
        // disconnect) also runs a Save+Load cycle via Player.ExitGame/
        // OnLoadingScreenFinished.
        private static readonly JsonSerializerOptions s_persistJsonOptions = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            IncludeFields = true,
        };

        private bool _phantomPersistLoaded;

        private static string PhantomPersistDir()
        {
            string dir = Path.Combine(FileHelper.DataDirectory, "PhantomPersist");
            try { if (Directory.Exists(dir) == false) Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        private static string PhantomPersistPath(ulong dbGuid)
            => Path.Combine(PhantomPersistDir(), $"{dbGuid:X}.json");

        /// <summary>
        /// Called from Player.OnLoadingScreenFinished. Idempotent — subsequent
        /// calls are no-ops. Rehydrates the nemesis roster, preferred-power
        /// map, and the Rogue Encounter toggle from the JSON sidecar.
        /// </summary>
        internal void LoadPhantomPersist()
        {
            if (_phantomPersistLoaded) return;
            _phantomPersistLoaded = true;

            ulong dbGuid = DatabaseUniqueId;
            if (dbGuid == 0) return;

            string path = PhantomPersistPath(dbGuid);
            if (File.Exists(path) == false) return;

            try
            {
                string json = File.ReadAllText(path);
                var blob = JsonSerializer.Deserialize<PhantomPersistBlob>(json, s_persistJsonOptions);
                if (blob == null) return;

                // Nemesis roster
                _nemeses.Clear();
                if (blob.Nemeses != null)
                {
                    foreach (var n in blob.Nemeses) _nemeses.Add(n);
                }

                // Bounty Board — same "must survive a real logout, not just
                // a same-session region hop" requirement as Nemeses. It
                // also rides MigrationData.BountyBoard for region-transfer
                // continuity (Player.BountyBoard.cs's Snapshot/Restore), but
                // that's a separate, session-only relay — this JSON sidecar
                // is what actually makes it outlive a disconnect.
                _bountyBoard.Clear();
                if (blob.BountyBoard != null)
                {
                    foreach (var b in blob.BountyBoard) _bountyBoard.Add(b);
                }
                // Theme the saved board was rolled under (Bounty Board mode
                // only) — without this a board restored after a full logout
                // would keep its themed roster but lose the arena/costume/
                // power/repopulation theming that goes with it.
                _bountyThemeIndex = blob.BountyThemeIndex;

                // Preferred powers
                _preferredPowers.Clear();
                if (blob.PreferredPowers != null)
                {
                    foreach (var kvp in blob.PreferredPowers) _preferredPowers[kvp.Key] = kvp.Value;
                }

                // Rogue Encounter toggle — use the field directly so the
                // setter's scheduling side effect fires only if we're
                // enabling and the caller isn't yet scheduled.
                if (blob.RogueEncounterEnabled)
                    RogueEncounterEnabled = true;

                // Auto-Stash toggle — plain field, no side effects on set.
                AutoStashEnabled = blob.AutoStashEnabled;
                GearUpgradeAlertEnabled = blob.GearUpgradeAlertEnabled;

                PersistLogger.Info($"[PhantomPersist] {GetName()}: loaded {_nemeses.Count} nemesis entries, {_bountyBoard.Count} bounty board slots, {_preferredPowers.Count} preferred powers");
            }
            catch (Exception ex)
            {
                PersistLogger.Warn($"[PhantomPersist] load failed for 0x{dbGuid:X}: {ex.Message}");
            }
        }

        /// <summary>
        /// Called from Player.ExitGame. Writes the sidecar synchronously so
        /// the file is on disk before we return control to the logout path.
        /// </summary>
        internal void SavePhantomPersist()
        {
            ulong dbGuid = DatabaseUniqueId;
            if (dbGuid == 0) return;

            // Only write if there's actually something worth persisting — no
            // point littering the folder with empty stubs for players who
            // never engaged with the phantom systems.
            bool hasData = _nemeses.Count > 0
                        || _bountyBoard.Count > 0
                        || _preferredPowers.Count > 0
                        || _rogueEncounterEnabled
                        // QoL toggles must count as data in their own right —
                        // otherwise a player who only ever turns these on has
                        // hasData == false, the sidecar is deleted on logout,
                        // and the setting silently reverts every session.
                        || _autoStashEnabled
                        || _gearUpgradeAlertEnabled;
            string path = PhantomPersistPath(dbGuid);
            if (hasData == false)
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                return;
            }

            try
            {
                var blob = new PhantomPersistBlob
                {
                    Version = 1,
                    Nemeses = new List<NemesisEntry>(_nemeses),
                    BountyBoard = new List<BountyBoardEntry>(_bountyBoard),
                    BountyThemeIndex = _bountyThemeIndex,
                    PreferredPowers = new Dictionary<ulong, ulong>(_preferredPowers),
                    RogueEncounterEnabled = _rogueEncounterEnabled,
                    AutoStashEnabled = _autoStashEnabled,
                    GearUpgradeAlertEnabled = _gearUpgradeAlertEnabled,
                };
                string json = JsonSerializer.Serialize(blob, s_persistJsonOptions);
                // Write to a temp file + move so a crash mid-write can't
                // corrupt the sidecar and lose the whole roster.
                string tempPath = path + ".tmp";
                File.WriteAllText(tempPath, json);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tempPath, path);

                PersistLogger.Info($"[PhantomPersist] {GetName()}: saved {_nemeses.Count} nemesis entries, {_bountyBoard.Count} bounty board slots, {_preferredPowers.Count} preferred powers");
            }
            catch (Exception ex)
            {
                PersistLogger.Warn($"[PhantomPersist] save failed for 0x{dbGuid:X}: {ex.Message}");
            }
        }

        private sealed class PhantomPersistBlob
        {
            public int Version { get; set; }
            public List<NemesisEntry> Nemeses { get; set; }
            public List<BountyBoardEntry> BountyBoard { get; set; }
            public int BountyThemeIndex { get; set; } = -1;
            public Dictionary<ulong, ulong> PreferredPowers { get; set; }
            public bool RogueEncounterEnabled { get; set; }
            public bool AutoStashEnabled { get; set; }
            public bool GearUpgradeAlertEnabled { get; set; }
        }
    }
}
