using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using MHServerEmu.Core.Logging;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Regions;

namespace MHServerEmu.Games.Entities
{
    // Leaderboard — one JSON file per account, same shape as Player.WaveHistory.cs.
    // Unlike WaveHistory (keeps the most RECENT runs), this keeps the BEST
    // scores: past the cap, the worst entry is evicted rather than the oldest.
    public partial class Player
    {
        private static readonly Logger LeaderboardLogger = LogManager.CreateLogger();

        private const int LeaderboardMaxCount = 200;

        // JsonConverter on the enum itself (not just the file-save options)
        // so it serializes as a string ("DpsParse"/"TerminalRun") everywhere,
        // including the webapi response — WebRequestContext.SendJsonAsync
        // uses plain default JsonSerializer options with no enum converter,
        // so without this the app (whose DTO expects a string) fails to
        // deserialize a raw numeric enum value.
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public enum LeaderboardKind { DpsParse, TerminalRun, EndlessChallenge }

        public sealed class LeaderboardEntry
        {
            public string Id { get; set; } = Guid.NewGuid().ToString("N");
            public LeaderboardKind Kind { get; set; }
            public string HeroName { get; set; }
            // Terminal runs only; null for DPS parses.
            public string RegionName { get; set; }
            public string DifficultyTier { get; set; }
            // DpsParse: damage/sec (higher is better). TerminalRun: elapsed ms
            // (lower is better). EndlessChallenge: waves survived (higher is better).
            public double Value { get; set; }
            public long TimestampMs { get; set; }
            // TerminalRun only: true = boss actually killed, false = left/died before finishing.
            // EndlessChallenge: true = extracted safely, false = squad wiped.
            // Always true for DpsParse (there's no "aborted" concept there).
            public bool Completed { get; set; } = true;
            // Computed fresh on every GetLeaderboardForWeb call, not a stored
            // fact — whether this is currently the best entry in its group
            // (DpsParse: per HeroName; TerminalRun: per HeroName+Region+Tier,
            // completed runs only). Recomputing on read means a new personal
            // best is reflected immediately without any backfill/migration.
            public bool IsPersonalBest { get; set; }
        }

        private string GetLeaderboardFilePath()
            => System.IO.Path.Combine(MHServerEmu.Core.Helpers.FileHelper.DataDirectory, "Leaderboard", $"0x{DatabaseUniqueId:X}.json");

        private List<LeaderboardEntry> LoadLeaderboardFile()
        {
            try
            {
                string path = GetLeaderboardFilePath();
                if (System.IO.File.Exists(path) == false) return new();
                var loaded = System.Text.Json.JsonSerializer.Deserialize<List<LeaderboardEntry>>(System.IO.File.ReadAllText(path));
                return loaded ?? new();
            }
            catch (Exception ex)
            {
                LeaderboardLogger.Warn($"[Leaderboard] load failed for {this}: {ex.Message}");
                return new();
            }
        }

        private bool SaveLeaderboardFile(List<LeaderboardEntry> entries)
        {
            try
            {
                string path = GetLeaderboardFilePath();
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(entries,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return true;
            }
            catch (Exception ex)
            {
                LeaderboardLogger.Warn($"[Leaderboard] save failed for {this}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Add one entry, keeping only the best LeaderboardMaxCount (best-per-Kind, not most-recent).</summary>
        private void AppendLeaderboardEntry(LeaderboardEntry entry)
        {
            try
            {
                var entries = LoadLeaderboardFile();
                entries.Add(entry);

                // DpsParse: higher Value is better. TerminalRun: lower Value (faster) is better.
                // Sort each kind's entries by its own "better" direction, then trim
                // globally by re-interleaving worst-last so the cap drops the worst
                // entries regardless of kind.
                var ranked = entries
                    .Select(e => (Entry: e, Rank: RankWithinKind(e, entries)))
                    .OrderBy(x => x.Rank)
                    .Select(x => x.Entry)
                    .ToList();

                if (ranked.Count > LeaderboardMaxCount)
                    ranked = ranked.Take(LeaderboardMaxCount).ToList();

                SaveLeaderboardFile(ranked);
                LeaderboardLogger.Info($"[Leaderboard] {this}: recorded {entry.Kind} entry for {entry.HeroName} (value={entry.Value})");
            }
            catch (Exception ex)
            {
                LeaderboardLogger.Warn($"[Leaderboard] append failed for {this}: {ex.Message}");
            }
        }

        // Lower rank = better. Comparable only within the same Kind (DPS and
        // terminal-run values are different units), but a single global sort
        // key is fine here since we only use it to decide trim order.
        // Completed is the primary key for TerminalRun — an aborted run's
        // short elapsed time must never outrank a real clear.
        private static double RankWithinKind(LeaderboardEntry e, List<LeaderboardEntry> all)
        {
            var sameKind = all.Where(x => x.Kind == e.Kind)
                .OrderByDescending(x => x.Kind == LeaderboardKind.TerminalRun ? (x.Completed ? 1 : 0) : 1)
                .ThenBy(x => LowerIsBetter(x.Kind) ? x.Value : -x.Value)
                .ToList();
            return sameKind.IndexOf(e);
        }

        // TerminalRun: elapsed time, lower (faster) is better. Everything
        // else (DpsParse, EndlessChallenge) is higher-is-better.
        private static bool LowerIsBetter(LeaderboardKind kind) => kind == LeaderboardKind.TerminalRun;

        /// <summary>Record a saved DPS parse. Called explicitly by the app ("Save to Leaderboard"), not automatically on every reset.</summary>
        public string CommitDpsToLeaderboard(string heroName, double dpsValue)
        {
            if (string.IsNullOrWhiteSpace(heroName) || dpsValue <= 0)
                return "Nothing to save — no valid DPS value.";

            AppendLeaderboardEntry(new LeaderboardEntry
            {
                Kind = LeaderboardKind.DpsParse,
                HeroName = heroName,
                Value = dpsValue,
                // Game.CurrentTime is the simulated per-Game-instance clock
                // (starts at ~1ms, not wall time) — using it here made every
                // leaderboard entry's "when" column show a bogus date near
                // the Unix epoch instead of the real save time. DateTimeOffset
                // is real system wall-clock time, which is what the app's
                // WhenText display (LeaderboardPage.xaml.cs) actually expects.
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            return $"Saved {heroName}: {dpsValue:N0} DPS.";
        }

        /// <summary>Record an Endless Challenge run — Value is waves survived. Completed = extracted safely, false = squad wiped.</summary>
        internal void CommitEndlessChallengeToLeaderboard(string heroName, int wavesSurvived, bool completed)
        {
            AppendLeaderboardEntry(new LeaderboardEntry
            {
                Kind = LeaderboardKind.EndlessChallenge,
                HeroName = heroName,
                Value = wavesSurvived,
                Completed = completed,
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
        }

        /// <summary>Record a completed (boss killed) or aborted (left/died first) terminal run.</summary>
        private void CommitTerminalRunToLeaderboard(string heroName, string regionName, string difficultyTier, long elapsedMs, bool completed)
        {
            AppendLeaderboardEntry(new LeaderboardEntry
            {
                Kind = LeaderboardKind.TerminalRun,
                HeroName = heroName,
                RegionName = regionName,
                DifficultyTier = difficultyTier,
                Value = elapsedMs,
                Completed = completed,
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
        }

        // Curated allowlist of endgame "Terminal" regions, mapped to their
        // real in-game names — no dedicated RegionBehavior/keyword flag or
        // display-name resolution exists for this content server-side
        // (RegionPrototype.RegionName is a LocaleStringId with no string-table
        // resolver anywhere in this codebase), so these are the actual names
        // already documented as comments on the matching entries in
        // RegionEnums.cs's "// Terminals" block.
        private static readonly Dictionary<PrototypeId, string> s_terminalRegionNames = new()
        {
            [(PrototypeId)RegionPrototypeId.OpDailyBugleRegionL11To60] = "Daily Bugle",
            [(PrototypeId)RegionPrototypeId.DailyGTimesSquareRegionL60] = "Times Square",
            [(PrototypeId)RegionPrototypeId.DailyGShockerSubwayRegionL60] = "Abandoned Subway",
            [(PrototypeId)RegionPrototypeId.DailyGKPWarehouseRegionL60] = "Kingpin's Warehouse",
            [(PrototypeId)RegionPrototypeId.DailyGTaskmasterRegionL60] = "Taskmaster Institute",
            [(PrototypeId)RegionPrototypeId.DailyGHoodsShipRegionL60] = "The Hood's Hideout",
            [(PrototypeId)RegionPrototypeId.DailyGFiskTowerRegionL60] = "Fisk Tower",
            [(PrototypeId)RegionPrototypeId.DailyGPurifierChurchRegionL60] = "Church of Purification",
            [(PrototypeId)RegionPrototypeId.DailyGStrykerBunkerRegionL60] = "Stryker Command Bunker",
            [(PrototypeId)RegionPrototypeId.DailyGSinisterLabRegionL60] = "Sinister Lab",
            [(PrototypeId)RegionPrototypeId.DailyGAIMFacilityRegionL60] = "A.I.M. Weapon Facility",
            [(PrototypeId)RegionPrototypeId.DailyGHYDRAIslandRegionL60] = "Hydra Island",
            [(PrototypeId)RegionPrototypeId.DailyGDoomCastleRegionL60] = "Castle Doom",
            [(PrototypeId)RegionPrototypeId.DailyGAsgardINSTRegionL60] = "Odin's Palace",
            [(PrototypeId)RegionPrototypeId.DailyGHighTownInvasionRegionL60] = "Skrull Invasion",
            [(PrototypeId)RegionPrototypeId.DrStrangeTimesSquareRegionL60] = "Dimensions Collide",
        };

        private Region _terminalRunRegion;
        private PrototypeId _terminalRunRegionRef = PrototypeId.Invalid;
        private long _terminalRunStartMs;
        private string _terminalRunTier;
        private string _terminalRunHeroName;
        private string _terminalRunFriendlyName;
        private Event<EntityDeadGameEvent>.Action _terminalBossDeadAction;

        /// <summary>
        /// Called from Avatar.OnEnteredWorld on every region entry for a real
        /// (non-phantom) player avatar. Starts/stops terminal-run tracking.
        /// Completion is normally detected the instant the boss dies
        /// (OnTerminalBossDead, via Region.EntityDeadEvent) — this method only
        /// has to handle the "left without killing the boss" (aborted) case,
        /// since a real kill already commits and clears tracking first.
        /// </summary>
        internal void OnAvatarEnteredRegion(Region region, Avatar avatar)
        {
            if (region == null || avatar == null) return;
            PrototypeId newRegionRef = region.PrototypeDataRef;

            if (_terminalRunRegionRef != PrototypeId.Invalid && newRegionRef != _terminalRunRegionRef)
            {
                LeaderboardLogger.Info($"[Leaderboard:Terminal] {this}: left {_terminalRunFriendlyName} without killing the boss — committing as aborted");
                CompleteTerminalRun(completed: false);
            }

            if (s_terminalRegionNames.TryGetValue(newRegionRef, out string friendlyName))
            {
                if (newRegionRef != _terminalRunRegionRef)
                {
                    _terminalRunRegion = region;
                    _terminalRunRegionRef = newRegionRef;
                    _terminalRunFriendlyName = friendlyName;
                    _terminalRunStartMs = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
                    _terminalRunTier = GetDifficultyTierName(region);
                    _terminalRunHeroName = GetFriendlyHeroName(avatar);

                    _terminalBossDeadAction ??= OnTerminalBossDead;
                    region.EntityDeadEvent.AddActionBack(_terminalBossDeadAction);

                    LeaderboardLogger.Info($"[Leaderboard:Terminal] {this}: entered {friendlyName} (tier={_terminalRunTier}) — tracking started");
                }
            }
            else
            {
                LeaderboardLogger.Info($"[Leaderboard:Terminal] {this}: entered {newRegionRef.GetName()} — not a recognized terminal, no tracking");
            }
        }

        /// <summary>Fires on every kill in a tracked terminal region — commits the run the instant the boss dies.</summary>
        private void OnTerminalBossDead(in EntityDeadGameEvent evt)
        {
            if (_terminalRunRegionRef == PrototypeId.Invalid) return;
            if (evt.Defender?.GetRankPrototype()?.IsRankBoss != true) return;
            if (evt.Killer != null && evt.Killer.Id != Id) return; // don't credit another player's kill in a shared instance

            LeaderboardLogger.Info($"[Leaderboard:Terminal] {this}: boss killed in {_terminalRunFriendlyName} — committing as completed");
            CompleteTerminalRun(completed: true);
        }

        /// <summary>Called from Player.OnDeallocate so a dangling event subscription can't outlive the player (logout/disconnect mid-run).</summary>
        internal void UnsubscribeTerminalRunTracking()
        {
            if (_terminalRunRegion != null && _terminalBossDeadAction != null)
                _terminalRunRegion.EntityDeadEvent.RemoveAction(_terminalBossDeadAction);
            _terminalRunRegion = null;
            _terminalRunRegionRef = PrototypeId.Invalid;
        }

        private void CompleteTerminalRun(bool completed)
        {
            long nowMs = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
            long elapsedMs = nowMs - _terminalRunStartMs;
            CommitTerminalRunToLeaderboard(_terminalRunHeroName, _terminalRunFriendlyName, _terminalRunTier, elapsedMs, completed);

            if (_terminalRunRegion != null && _terminalBossDeadAction != null)
                _terminalRunRegion.EntityDeadEvent.RemoveAction(_terminalBossDeadAction);

            _terminalRunRegion = null;
            _terminalRunRegionRef = PrototypeId.Invalid;
        }

        /// <summary>Real "Green"/"Red"/"Cosmic" name — DifficultyTierPrototype.Tier is a plain C# enum, not a LocaleStringId, so this is reliable (unlike region display names).</summary>
        private static string GetDifficultyTierName(Region region)
        {
            var tierProto = region.DifficultyTierRef.As<DifficultyTierPrototype>();
            return tierProto != null ? tierProto.Tier.ToString() : "Unknown";
        }

        /// <summary>"Entity/Characters/Avatars/Shipping/Thor.prototype" -> "Thor".</summary>
        private static string GetFriendlyHeroName(Avatar avatar)
        {
            string path = avatar.PrototypeDataRef.GetName();
            if (string.IsNullOrEmpty(path)) return path;
            int slash = path.LastIndexOf('/');
            string leaf = slash >= 0 ? path[(slash + 1)..] : path;
            const string suffix = ".prototype";
            if (leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                leaf = leaf[..^suffix.Length];
            return leaf;
        }

        /// <summary>Delete a single entry by id.</summary>
        public string DeleteLeaderboardEntry(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "No entry id given.";

            var entries = LoadLeaderboardFile();
            int removed = entries.RemoveAll(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return "No matching entry found.";

            SaveLeaderboardFile(entries);
            LeaderboardLogger.Info($"[Leaderboard] {this}: deleted entry {id}");
            return "Entry deleted.";
        }

        /// <summary>Wipe the entire leaderboard, optionally scoped to one Kind.</summary>
        public string ClearLeaderboard(string kind)
        {
            if (string.IsNullOrWhiteSpace(kind) == false && Enum.TryParse<LeaderboardKind>(kind, true, out var parsedKind))
            {
                var entries = LoadLeaderboardFile();
                int removed = entries.RemoveAll(e => e.Kind == parsedKind);
                SaveLeaderboardFile(entries);
                LeaderboardLogger.Info($"[Leaderboard] {this}: cleared {removed} {parsedKind} entr{(removed == 1 ? "y" : "ies")}");
                return $"Cleared {removed} entr{(removed == 1 ? "y" : "ies")}.";
            }

            SaveLeaderboardFile(new List<LeaderboardEntry>());
            LeaderboardLogger.Info($"[Leaderboard] {this}: cleared entire leaderboard");
            return "Leaderboard cleared.";
        }

        /// <summary>Best-first list, optionally filtered by kind/hero.</summary>
        public List<LeaderboardEntry> GetLeaderboardForWeb(string kind, string heroName)
        {
            var entries = LoadLeaderboardFile();

            if (string.IsNullOrWhiteSpace(kind) == false && Enum.TryParse<LeaderboardKind>(kind, true, out var parsedKind))
                entries = entries.Where(e => e.Kind == parsedKind).ToList();
            if (string.IsNullOrWhiteSpace(heroName) == false)
                entries = entries.Where(e => string.Equals(e.HeroName, heroName, StringComparison.OrdinalIgnoreCase)).ToList();

            entries.Sort((a, b) =>
            {
                if (a.Kind != b.Kind) return a.Kind.CompareTo(b.Kind);
                if (a.Kind == LeaderboardKind.TerminalRun)
                {
                    // Completed runs always rank above aborted ones — a short
                    // aborted run must never look like a fast clear.
                    if (a.Completed != b.Completed) return a.Completed ? -1 : 1;
                    return a.Value.CompareTo(b.Value);
                }
                // DpsParse and EndlessChallenge: higher Value always wins —
                // unlike TerminalRun, Completed (extracted vs. wiped) doesn't
                // invalidate the metric. Surviving 50 waves before dying is a
                // real, honest result — better than extracting safely at 10.
                return b.Value.CompareTo(a.Value);
            });

            MarkPersonalBests(entries);
            return entries;
        }

        /// <summary>
        /// Flags each entry's IsPersonalBest against everything ELSE currently
        /// on the board (not just the filtered/returned subset) — grouped by
        /// HeroName for DpsParse, and by HeroName+Region+Tier (completed runs
        /// only) for TerminalRun, since an aborted run should never be able
        /// to "win" a personal best.
        /// </summary>
        private void MarkPersonalBests(List<LeaderboardEntry> entries)
        {
            // Always compare against the FULL board, not whatever subset the
            // caller filtered down to — best-status must not change just
            // because a kind/hero filter was applied to what's displayed.
            var allEntries = LoadLeaderboardFile();

            var bestDps = allEntries.Where(e => e.Kind == LeaderboardKind.DpsParse)
                .GroupBy(e => e.HeroName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Max(e => e.Value), StringComparer.OrdinalIgnoreCase);

            var bestTerminal = allEntries.Where(e => e.Kind == LeaderboardKind.TerminalRun && e.Completed)
                .GroupBy(e => (e.HeroName, e.RegionName, e.DifficultyTier))
                .ToDictionary(g => g.Key, g => g.Min(e => e.Value));

            // Per-hero highest waves survived, regardless of Completed
            // (extracted vs. wiped) — see the sort comment above for why.
            var bestEndless = allEntries.Where(e => e.Kind == LeaderboardKind.EndlessChallenge)
                .GroupBy(e => e.HeroName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Max(e => e.Value), StringComparer.OrdinalIgnoreCase);

            foreach (var e in entries)
            {
                e.IsPersonalBest = e.Kind switch
                {
                    LeaderboardKind.DpsParse => bestDps.TryGetValue(e.HeroName, out double bestDpsValue) && e.Value >= bestDpsValue,
                    LeaderboardKind.EndlessChallenge => bestEndless.TryGetValue(e.HeroName, out double bestEndlessValue) && e.Value >= bestEndlessValue,
                    _ => e.Completed && bestTerminal.TryGetValue((e.HeroName, e.RegionName, e.DifficultyTier), out double bestMs) && e.Value <= bestMs,
                };
            }
        }
    }
}
