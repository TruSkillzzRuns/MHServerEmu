// OmegaDev2 Arena endpoints — Enemy Phantoms and the Wave Director.
//
//   POST /webapi/arena/enemyphantoms/spawn  { playerName, heroes:[{avatarRef, level, count}] }
//   POST /webapi/arena/enemyphantoms/clear  { playerName }
//   GET  /webapi/arena/enemyphantoms/status?player=
//
//   POST /webapi/arena/waves/start  { playerName, intermissionMs, loop, countScalePerWave,
//                                     levelBumpPerWave, rewardMode, rewardLootTableRef, arenaRegionRef,
//                                     clearArena, waves:[{intermissionMsOverride,
//                                     entries:[{agentRef | heroRef+enemyPhantom, count, level}]}] }
//   POST /webapi/arena/waves/stop   { playerName, cleanup }
//   POST /webapi/arena/waves/pause  { playerName, paused }
//   POST /webapi/arena/waves/skip   { playerName }
//   GET  /webapi/arena/waves/status?player=
//   GET  /webapi/arena/waves/history?player=
//   POST /webapi/arena/waves/plans  { playerName, op: save|start|delete|list, name, plan? }
//
// Everything runs on the target player's game thread via Player.RunPhantomWebOp.

using System.Text.Json;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.GameData;
using static MHServerEmu.Games.Features.BossPool;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class EnemyPhantomsSpawnWebHandler : WebHandler
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private sealed class SpawnEntry
        {
            public ulong AvatarRef;   // 0 = random hero
            public int Level;         // 0 = match player
            public int Count = 1;
            public int Rank;          // 0 = plain hostile; 1-5 = spawn as a nemesis-rank hostile
        }

        private static string LeafHeroName(PrototypeId avatarRef)
        {
            string path = GameDatabase.GetPrototypeName(avatarRef);
            if (string.IsNullOrEmpty(path)) return "Hostile";
            string leaf = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
            const string suffix = ".prototype";
            if (leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                leaf = leaf[..^suffix.Length];
            return leaf;
        }

        protected override async Task Post(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;
            string body = await context.ReadUtf8StringAsync();

            string playerName = null;
            var entries = new List<SpawnEntry>();
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                if (root.TryGetProperty("playerName", out var pn)) playerName = pn.GetString();
                if (root.TryGetProperty("heroes", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        var e = new SpawnEntry();
                        if (el.TryGetProperty("avatarRef", out var ar)) e.AvatarRef = PhantomsWebUtil.ParseRef(ar.GetString());
                        if (el.TryGetProperty("level", out var lv)) e.Level = lv.GetInt32();
                        if (el.TryGetProperty("count", out var cn)) e.Count = cn.GetInt32();
                        if (el.TryGetProperty("rank", out var rk)) e.Rank = rk.GetInt32();
                        entries.Add(e);
                    }
                }
            }
            catch (Exception ex)
            {
                await context.SendJsonAsync(new { Ok = false, Error = $"bad request: {ex.Message}" });
                return;
            }

            if (entries.Count == 0)
            {
                await context.SendJsonAsync(new { Ok = false, Error = "no heroes in request" });
                return;
            }

            Player player = PhantomsWebUtil.FindTargetPlayer(playerName, null, out string findError);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = findError ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player, p =>
            {
                var avatar = p.CurrentAvatar;
                if (avatar == null || avatar.IsInWorld == false)
                    return new { Ok = false, Error = "player has no avatar in world", Spawned = 0, Failed = 0, FirstError = (string)null };

                int spawned = 0, failed = 0;
                string firstError = null;

                foreach (var e in entries)
                {
                    int level = Math.Clamp(e.Level, 0, 60);
                    int rank = Math.Clamp(e.Rank, 0, 5);

                    // A ranked spawn is always exactly one nemesis-style hostile —
                    // Count only applies to the plain (rank 0) random/named path,
                    // same convention Phantom Heroes' own quick-spawn already uses
                    // ("specific hero = one spawn per click").
                    if (rank > 0)
                    {
                        string display = $"★{rank} {LeafHeroName((PrototypeId)e.AvatarRef)}";
                        ulong id = avatar.SpawnNemesisPhantomHero((PrototypeId)e.AvatarRef, level, display, rank, out string err);
                        if (id != 0) spawned++;
                        else { failed++; firstError ??= err; }
                        continue;
                    }

                    int count = Math.Clamp(e.Count, 1, 20);
                    for (int i = 0; i < count; i++)
                    {
                        ulong id = avatar.SpawnEnemyPhantomHero((PrototypeId)e.AvatarRef, level, out string err);
                        if (id != 0) spawned++;
                        else { failed++; firstError ??= err; }
                    }
                }

                return new { Ok = spawned > 0 || failed == 0, Error = (string)null, Spawned = spawned, Failed = failed, FirstError = firstError };
            });

            Logger.Info($"[Arena:EnemyPhantoms] spawn for {player.GetName()}: {JsonSerializer.Serialize(result)}");
            await context.SendJsonAsync(result);
        }
    }

    public class EnemyPhantomsClearWebHandler : WebHandler
    {
        protected override async Task Post(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;
            string body = await context.ReadUtf8StringAsync();
            PhantomsWebUtil.ParseTarget(body, out string playerName, out string playerDbId);

            // Optional targetId: despawn just that one hostile instead of
            // clearing the whole roster. Absent/0 = existing clear-all behavior.
            ulong targetId = 0;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (doc.RootElement.TryGetProperty("targetId", out var t) && t.ValueKind == JsonValueKind.String)
                    ulong.TryParse(t.GetString(), out targetId);
            }
            catch { /* malformed targetId just falls back to clear-all */ }

            Player player = PhantomsWebUtil.FindTargetPlayer(playerName, playerDbId, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player, p =>
            {
                var avatar = p.CurrentAvatar;
                if (targetId != 0)
                {
                    bool ok = avatar != null ? avatar.DespawnOneEnemyPhantom(targetId) : p.DespawnOneEnemyPhantom(targetId);
                    return new { Ok = ok, Removed = ok ? 1 : 0 };
                }
                int removed = avatar != null ? avatar.DespawnAllEnemyPhantoms() : p.PurgeEnemyPhantoms();
                return new { Ok = true, Removed = removed };
            });
            await context.SendJsonAsync(result);
        }
    }

    public class EnemyPhantomsStatusWebHandler : WebHandler
    {
        protected override async Task Get(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;
            Player player = PhantomsWebUtil.FindTargetPlayer(PhantomsWebUtil.QueryParam(context, "player"), null, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found", Enemies = new List<object>() });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player, p => new
            {
                Ok = true,
                Count = p.EnemyPhantomCount,
                Enemies = p.GetEnemyPhantomInfosForWeb(),
            });
            await context.SendJsonAsync(result);
        }
    }

    public class WavesStartWebHandler : WebHandler
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        protected override async Task Post(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;
            string body = await context.ReadUtf8StringAsync();

            string playerName = null;
            int intermissionMs = 5000;
            ulong arenaRegionRef = 0;
            bool clearArena = false;
            bool loop = false;
            float countScalePerWave = 0f;
            int levelBumpPerWave = 0;
            Player.WaveRewardMode rewardMode = Player.WaveRewardMode.None;
            ulong rewardLootTableRef = 0;
            var waves = new List<Player.WaveDef>();
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                if (root.TryGetProperty("playerName", out var pn)) playerName = pn.GetString();
                if (root.TryGetProperty("intermissionMs", out var im)) intermissionMs = im.GetInt32();
                if (root.TryGetProperty("arenaRegionRef", out var arr2)) arenaRegionRef = PhantomsWebUtil.ParseRef(arr2.GetString());
                if (root.TryGetProperty("clearArena", out var ca)) clearArena = ca.GetBoolean();
                if (root.TryGetProperty("loop", out var lp)) loop = lp.GetBoolean();
                if (root.TryGetProperty("countScalePerWave", out var csw)) countScalePerWave = csw.GetSingle();
                if (root.TryGetProperty("levelBumpPerWave", out var lbw)) levelBumpPerWave = lbw.GetInt32();
                if (root.TryGetProperty("rewardMode", out var rm) && Enum.TryParse<Player.WaveRewardMode>(rm.GetString(), true, out var parsedMode))
                    rewardMode = parsedMode;
                if (root.TryGetProperty("rewardLootTableRef", out var rltr)) rewardLootTableRef = PhantomsWebUtil.ParseRef(rltr.GetString());
                if (root.TryGetProperty("waves", out var wavesEl) && wavesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var waveEl in wavesEl.EnumerateArray())
                    {
                        var wave = new Player.WaveDef();
                        if (waveEl.TryGetProperty("intermissionMsOverride", out var imo) && imo.ValueKind != JsonValueKind.Null)
                            wave.IntermissionMsOverride = imo.GetInt32();
                        if (waveEl.TryGetProperty("entries", out var entriesEl) && entriesEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var el in entriesEl.EnumerateArray())
                            {
                                var entry = new Player.WaveEntryDef();
                                if (el.TryGetProperty("agentRef", out var ar)) entry.AgentRef = PhantomsWebUtil.ParseRef(ar.GetString());
                                if (el.TryGetProperty("heroRef", out var hr)) entry.HeroRef = PhantomsWebUtil.ParseRef(hr.GetString());
                                if (el.TryGetProperty("enemyPhantom", out var ep)) entry.IsEnemyPhantom = ep.GetBoolean();
                                if (el.TryGetProperty("count", out var cn)) entry.Count = cn.GetInt32();
                                if (el.TryGetProperty("level", out var lv)) entry.Level = lv.GetInt32();
                                if (el.TryGetProperty("rank", out var rk)) entry.Rank = rk.GetInt32();
                                if (entry.AgentRef != 0 || entry.IsEnemyPhantom)
                                    wave.Entries.Add(entry);
                            }
                        }
                        if (wave.Entries.Count > 0) waves.Add(wave);
                    }
                }
            }
            catch (Exception ex)
            {
                await context.SendJsonAsync(new { Ok = false, Error = $"bad request: {ex.Message}" });
                return;
            }

            if (waves.Count == 0)
            {
                await context.SendJsonAsync(new { Ok = false, Error = "no valid waves in request" });
                return;
            }

            Player player = PhantomsWebUtil.FindTargetPlayer(playerName, null, out string findError);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = findError ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player,
                p => new { Ok = true, Message = p.StartWaveRun(waves, intermissionMs, arenaRegionRef, clearArena,
                    loop, countScalePerWave, levelBumpPerWave, rewardMode, rewardLootTableRef) });

            Logger.Info($"[Arena:Waves] start for {player.GetName()}: {waves.Count} wave(s)");
            await context.SendJsonAsync(result);
        }
    }

    public class WavesStopWebHandler : WebHandler
    {
        protected override async Task Post(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;
            string body = await context.ReadUtf8StringAsync();

            string playerName = null;
            bool cleanup = true;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                if (root.TryGetProperty("playerName", out var pn)) playerName = pn.GetString();
                if (root.TryGetProperty("cleanup", out var cl)) cleanup = cl.GetBoolean();
            }
            catch { /* defaults */ }

            Player player = PhantomsWebUtil.FindTargetPlayer(playerName, null, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player,
                p => new { Ok = true, Message = p.StopWaveRun(cleanup) });
            await context.SendJsonAsync(result);
        }
    }

    public class WavesStatusWebHandler : WebHandler
    {
        protected override async Task Get(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;
            Player player = PhantomsWebUtil.FindTargetPlayer(PhantomsWebUtil.QueryParam(context, "player"), null, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player,
                p => new { Ok = true, Status = p.GetWaveStatusForWeb() });
            await context.SendJsonAsync(result);
        }
    }

    public class WavesPauseWebHandler : WebHandler
    {
        protected override async Task Post(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;
            string body = await context.ReadUtf8StringAsync();

            string playerName = null;
            bool paused = true;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                if (root.TryGetProperty("playerName", out var pn)) playerName = pn.GetString();
                if (root.TryGetProperty("paused", out var pa)) paused = pa.GetBoolean();
            }
            catch { /* defaults */ }

            Player player = PhantomsWebUtil.FindTargetPlayer(playerName, null, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player,
                p => new { Ok = true, Message = p.PauseWaveRun(paused) });
            await context.SendJsonAsync(result);
        }
    }

    public class WavesSkipWebHandler : WebHandler
    {
        protected override async Task Post(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;
            string body = await context.ReadUtf8StringAsync();
            PhantomsWebUtil.ParseTarget(body, out string playerName, out string playerDbId);

            Player player = PhantomsWebUtil.FindTargetPlayer(playerName, playerDbId, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player,
                p => new { Ok = true, Message = p.SkipWaveRun() });
            await context.SendJsonAsync(result);
        }
    }

    public class WavesHistoryWebHandler : WebHandler
    {
        protected override async Task Get(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;
            Player player = PhantomsWebUtil.FindTargetPlayer(PhantomsWebUtil.QueryParam(context, "player"), null, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found", Entries = new List<object>() });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player,
                p => new { Ok = true, Entries = p.GetWaveHistoryForWeb() });
            await context.SendJsonAsync(result);
        }
    }

    public class WavePlansWebHandler : WebHandler
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        protected override async Task Post(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;
            string body = await context.ReadUtf8StringAsync();

            string playerName = null, op = null, name = null;
            Player.WavePlanRecord record = null;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                if (root.TryGetProperty("playerName", out var pn)) playerName = pn.GetString();
                if (root.TryGetProperty("op", out var opEl)) op = opEl.GetString();
                if (root.TryGetProperty("name", out var nm)) name = nm.GetString();

                if (op == "save" && root.TryGetProperty("plan", out var planEl))
                {
                    record = new Player.WavePlanRecord();
                    if (planEl.TryGetProperty("intermissionMs", out var im)) record.IntermissionMs = im.GetInt32();
                    if (planEl.TryGetProperty("arenaRegionRef", out var arr2)) record.ArenaRegionRef = PhantomsWebUtil.ParseRef(arr2.GetString());
                    if (planEl.TryGetProperty("clearArena", out var ca)) record.ClearArena = ca.GetBoolean();
                    if (planEl.TryGetProperty("loop", out var lp)) record.Loop = lp.GetBoolean();
                    if (planEl.TryGetProperty("countScalePerWave", out var csw)) record.CountScalePerWave = csw.GetSingle();
                    if (planEl.TryGetProperty("levelBumpPerWave", out var lbw)) record.LevelBumpPerWave = lbw.GetInt32();
                    if (planEl.TryGetProperty("rewardMode", out var rm) && Enum.TryParse<Player.WaveRewardMode>(rm.GetString(), true, out var parsedMode))
                        record.RewardMode = parsedMode;
                    if (planEl.TryGetProperty("rewardLootTableRef", out var rltr)) record.RewardLootTableRef = PhantomsWebUtil.ParseRef(rltr.GetString());
                    if (planEl.TryGetProperty("waves", out var wavesEl) && wavesEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var waveEl in wavesEl.EnumerateArray())
                        {
                            var wave = new Player.WaveDef();
                            if (waveEl.TryGetProperty("intermissionMsOverride", out var imo) && imo.ValueKind != JsonValueKind.Null)
                                wave.IntermissionMsOverride = imo.GetInt32();
                            if (waveEl.TryGetProperty("entries", out var entriesEl) && entriesEl.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var el in entriesEl.EnumerateArray())
                                {
                                    var entry = new Player.WaveEntryDef();
                                    if (el.TryGetProperty("agentRef", out var ar)) entry.AgentRef = PhantomsWebUtil.ParseRef(ar.GetString());
                                    if (el.TryGetProperty("heroRef", out var hr)) entry.HeroRef = PhantomsWebUtil.ParseRef(hr.GetString());
                                    if (el.TryGetProperty("enemyPhantom", out var ep)) entry.IsEnemyPhantom = ep.GetBoolean();
                                    if (el.TryGetProperty("count", out var cn)) entry.Count = cn.GetInt32();
                                    if (el.TryGetProperty("level", out var lv)) entry.Level = lv.GetInt32();
                                    if (el.TryGetProperty("rank", out var rk)) entry.Rank = rk.GetInt32();
                                    if (entry.AgentRef != 0 || entry.IsEnemyPhantom)
                                        wave.Entries.Add(entry);
                                }
                            }
                            if (wave.Entries.Count > 0) record.Waves.Add(wave);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                await context.SendJsonAsync(new { Ok = false, Error = $"bad request: {ex.Message}" });
                return;
            }

            if (string.IsNullOrEmpty(op))
            {
                await context.SendJsonAsync(new { Ok = false, Error = "missing op" });
                return;
            }

            Player player = PhantomsWebUtil.FindTargetPlayer(playerName, null, out string findError);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = findError ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player, p =>
            {
                switch (op)
                {
                    case "save":
                        return new { Ok = true, Message = p.SaveWavePlan(name, record), Plans = (List<string>)null };
                    case "start":
                        return new { Ok = true, Message = p.StartWavePlan(name), Plans = (List<string>)null };
                    case "delete":
                        return new { Ok = true, Message = p.DeleteWavePlan(name), Plans = (List<string>)null };
                    case "list":
                        return new { Ok = true, Message = (string)null, Plans = p.ListWavePlans() };
                    default:
                        return new { Ok = false, Message = $"unknown op '{op}'", Plans = (List<string>)null };
                }
            });

            Logger.Info($"[Arena:Waves:Plans] {op} for {player.GetName()}: {name}");
            await context.SendJsonAsync(result);
        }
    }

    /// <summary>
    /// POST /webapi/arena/endless/start — { playerName, entry:{agentRef|heroRef,
    /// enemyPhantom, count, level, rank}, intermissionMs, arenaRegionRef,
    /// clearArena, countScalePerWave, levelBumpPerWave, rewardLootTableRef }.
    /// One WaveEntryDef that repeats forever, escalating rank/count/level as
    /// waves clear — see Player.WaveDirector.cs's StartEndlessChallenge.
    /// </summary>
    public class EndlessStartWebHandler : WebHandler
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        protected override async Task Post(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;
            string body = await context.ReadUtf8StringAsync();

            string playerName = null;
            int intermissionMs = 5000;
            ulong arenaRegionRef = 0;
            bool clearArena = false;
            float countScalePerWave = 0f;
            int levelBumpPerWave = 0;
            ulong rewardLootTableRef = 0;
            Player.WaveEntryDef entry = null;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                if (root.TryGetProperty("playerName", out var pn)) playerName = pn.GetString();
                if (root.TryGetProperty("intermissionMs", out var im)) intermissionMs = im.GetInt32();
                if (root.TryGetProperty("arenaRegionRef", out var arr2)) arenaRegionRef = PhantomsWebUtil.ParseRef(arr2.GetString());
                if (root.TryGetProperty("clearArena", out var ca)) clearArena = ca.GetBoolean();
                if (root.TryGetProperty("countScalePerWave", out var csw)) countScalePerWave = csw.GetSingle();
                if (root.TryGetProperty("levelBumpPerWave", out var lbw)) levelBumpPerWave = lbw.GetInt32();
                if (root.TryGetProperty("rewardLootTableRef", out var rltr)) rewardLootTableRef = PhantomsWebUtil.ParseRef(rltr.GetString());
                if (root.TryGetProperty("entry", out var el))
                {
                    entry = new Player.WaveEntryDef();
                    if (el.TryGetProperty("agentRef", out var ar)) entry.AgentRef = PhantomsWebUtil.ParseRef(ar.GetString());
                    if (el.TryGetProperty("heroRef", out var hr)) entry.HeroRef = PhantomsWebUtil.ParseRef(hr.GetString());
                    if (el.TryGetProperty("enemyPhantom", out var ep)) entry.IsEnemyPhantom = ep.GetBoolean();
                    if (el.TryGetProperty("count", out var cn)) entry.Count = cn.GetInt32();
                    if (el.TryGetProperty("level", out var lv)) entry.Level = lv.GetInt32();
                    if (el.TryGetProperty("rank", out var rk)) entry.Rank = rk.GetInt32();
                }
            }
            catch (Exception ex)
            {
                await context.SendJsonAsync(new { Ok = false, Error = $"bad request: {ex.Message}" });
                return;
            }

            if (entry == null || (entry.AgentRef == 0 && entry.IsEnemyPhantom == false))
            {
                await context.SendJsonAsync(new { Ok = false, Error = "no valid wave entry in request" });
                return;
            }

            Player player = PhantomsWebUtil.FindTargetPlayer(playerName, null, out string findError);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = findError ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player,
                p => new { Ok = true, Message = p.StartEndlessChallenge(entry, intermissionMs, arenaRegionRef, clearArena,
                    countScalePerWave, levelBumpPerWave, rewardLootTableRef) });

            Logger.Info($"[Arena:Endless] start for {player.GetName()}");
            await context.SendJsonAsync(result);
        }
    }

    /// <summary>POST /webapi/arena/endless/extract — { playerName }. Bail safely, banking the current wave count.</summary>
    public class EndlessExtractWebHandler : WebHandler
    {
        protected override async Task Post(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;
            string body = await context.ReadUtf8StringAsync();
            PhantomsWebUtil.ParseTarget(body, out string playerName, out string playerDbId);

            Player player = PhantomsWebUtil.FindTargetPlayer(playerName, playerDbId, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player,
                p => new { Ok = true, Message = p.ExtractEndlessChallenge() });
            await context.SendJsonAsync(result);
        }
    }

    /// <summary>GET /webapi/arena/endless/status?player= — live Endless Challenge status.</summary>
    public class EndlessStatusWebHandler : WebHandler
    {
        protected override async Task Get(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;
            Player player = PhantomsWebUtil.FindTargetPlayer(PhantomsWebUtil.QueryParam(context, "player"), null, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player,
                p => new { Ok = true, Status = p.GetEndlessStatusForWeb() });
            await context.SendJsonAsync(result);
        }
    }
}
