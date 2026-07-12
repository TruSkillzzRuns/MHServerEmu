// OmegaDev2 Phantom Heroes endpoints — the full phantom feature set over
// WebAPI, mirroring the !phantom chat commands.
//
//   GET  /webapi/phantoms/catalog             — playable-hero roster (+ portraits)
//   GET  /webapi/phantoms/catalog?hero=0x...  — costumes for one hero
//   GET  /webapi/phantoms/status?player=      — live phantom list for a player
//   POST /webapi/phantoms/spawn               — { playerName, count, level } random
//                                               or { playerName, heroes:[{avatarRef,
//                                               level, lockLevel, costumeRef}] }
//   POST /webapi/phantoms/clear               — { playerName }
//   POST /webapi/phantoms/costume             — { playerName, randomizeAll:true } or
//                                               { playerName, phantomQuery, costume }
//   POST /webapi/phantoms/gear                — { playerName, phantomQuery? } re-roll
//   GET  /webapi/phantoms/squads?player=      — saved squads (structured)
//   POST /webapi/phantoms/squads              — { playerName, op:"save|spawn|delete", name }
//
// All operations marshal onto the target player's game thread via
// Player.RunPhantomWebOp. Hero / costume identity is resolved from the loaded
// client data at runtime — nothing hero-specific lives in server source.

using System.Reflection;
using System.Text.Json;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.Games;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class PhantomsCatalogWebHandler : WebHandler
    {
        private static object _cachedHeroes;
        private static readonly object _buildLock = new();

        protected override async Task Get(WebRequestContext context)
        {
            string heroParam = PhantomsWebUtil.QueryParam(context, "hero");
            if (string.IsNullOrWhiteSpace(heroParam) == false)
            {
                await context.SendJsonAsync(BuildCostumes(heroParam));
                return;
            }

            await context.SendJsonAsync(GetOrBuildHeroes());
        }

        private static object GetOrBuildHeroes()
        {
            if (_cachedHeroes != null) return _cachedHeroes;
            lock (_buildLock)
            {
                if (_cachedHeroes != null) return _cachedHeroes;

                var locale = MHServerEmu.Games.Locales.LocaleManager.Instance.CurrentLocale;
                var heroes = new List<object>(80);
                foreach (var (avatarRef, shortName) in Avatar.GetAllPhantomHeroRefs())
                {
                    var avatarProto = avatarRef.As<AvatarPrototype>();
                    if (avatarProto == null) continue;

                    string displayName = null;
                    if (avatarProto.DisplayName != LocaleStringId.Invalid && locale != null)
                    {
                        displayName = locale.GetLocaleString(avatarProto.DisplayName);
                        if (string.IsNullOrWhiteSpace(displayName)) displayName = null;
                    }

                    // Several icon assets exist per avatar and not all of them
                    // live in the TFC stream the portrait endpoint extracts
                    // from — send every candidate so the client can fall
                    // through to one that renders.
                    var portraitCandidates = new List<string>(4);
                    void AddCandidate(AssetId assetId)
                    {
                        if (assetId == 0) return;
                        string assetName = GameDatabase.GetAssetName(assetId);
                        if (string.IsNullOrEmpty(assetName) == false && portraitCandidates.Contains(assetName) == false)
                            portraitCandidates.Add(assetName);
                    }
                    AddCandidate(avatarProto.PortraitPath);
                    AddCandidate(avatarProto.CharacterSelectIconPortraitSmall);
                    AddCandidate(avatarProto.SocialIconPath);
                    AddCandidate(avatarProto.CharacterSelectIconPath);

                    heroes.Add(new
                    {
                        ProtoRef = $"0x{(ulong)avatarRef:X16}",
                        Name = shortName,
                        DisplayName = displayName,
                        PortraitPath = portraitCandidates.Count > 0 ? portraitCandidates[0] : null,
                        PortraitCandidates = portraitCandidates,
                    });
                }

                _cachedHeroes = new { TotalHeroes = heroes.Count, Heroes = heroes };
                return _cachedHeroes;
            }
        }

        private static object BuildCostumes(string heroParam)
        {
            ulong heroRaw = PhantomsWebUtil.ParseRef(heroParam);
            var avatarRef = (PrototypeId)heroRaw;
            if (avatarRef == PrototypeId.Invalid || avatarRef.As<AvatarPrototype>() == null)
                return new { Ok = false, Error = "hero param is not an avatar prototype ref" };

            var locale = MHServerEmu.Games.Locales.LocaleManager.Instance.CurrentLocale;
            var costumes = new List<object>();
            foreach (var (costumeRef, shortName) in Avatar.GetCostumesForAvatar(avatarRef))
            {
                string displayName = null;
                var costumeProto = costumeRef.As<CostumePrototype>();
                if (costumeProto != null && costumeProto.DisplayName != LocaleStringId.Invalid && locale != null)
                {
                    displayName = locale.GetLocaleString(costumeProto.DisplayName);
                    if (string.IsNullOrWhiteSpace(displayName)) displayName = null;
                }

                costumes.Add(new
                {
                    ProtoRef = $"0x{(ulong)costumeRef:X16}",
                    Name = shortName,
                    DisplayName = displayName,
                });
            }

            return new { Ok = true, TotalCostumes = costumes.Count, Costumes = costumes };
        }
    }

    public class PhantomsStatusWebHandler : WebHandler
    {
        protected override async Task Get(WebRequestContext context)
        {
            Player player = PhantomsWebUtil.FindTargetPlayer(PhantomsWebUtil.QueryParam(context, "player"), null, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found", Phantoms = new List<object>() });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player, p => new
            {
                Ok = true,
                Player = p.GetName(),
                Count = p.PhantomHeroCount,
                Phantoms = p.GetPhantomInfosForWeb(),
            });
            await context.SendJsonAsync(result);
        }
    }

    public class PhantomsSpawnWebHandler : WebHandler
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private sealed class SpawnHeroEntry
        {
            public ulong AvatarRef;
            public int Level;
            public bool LockLevel;
            public ulong CostumeRef;
        }

        protected override async Task Post(WebRequestContext context)
        {
            string body = await context.ReadUtf8StringAsync();

            string playerName = null, playerDbId = null;
            int count = 0, level = 0;
            bool lockLevel = false;
            var heroes = new List<SpawnHeroEntry>();
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                if (root.TryGetProperty("playerName", out var pn)) playerName = pn.GetString();
                if (root.TryGetProperty("playerDbId", out var pd)) playerDbId = pd.GetString();
                if (root.TryGetProperty("count", out var cn)) count = cn.GetInt32();
                if (root.TryGetProperty("level", out var lv)) level = lv.GetInt32();
                if (root.TryGetProperty("lockLevel", out var ll)) lockLevel = ll.GetBoolean();
                if (root.TryGetProperty("heroes", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        var entry = new SpawnHeroEntry();
                        if (el.TryGetProperty("avatarRef", out var ar)) entry.AvatarRef = PhantomsWebUtil.ParseRef(ar.GetString());
                        if (el.TryGetProperty("level", out var hl)) entry.Level = hl.GetInt32();
                        if (el.TryGetProperty("lockLevel", out var hll)) entry.LockLevel = hll.GetBoolean();
                        if (el.TryGetProperty("costumeRef", out var cr)) entry.CostumeRef = PhantomsWebUtil.ParseRef(cr.GetString());
                        if (entry.AvatarRef != 0) heroes.Add(entry);
                    }
                }
            }
            catch (Exception ex)
            {
                await context.SendJsonAsync(new { Ok = false, Error = $"bad request: {ex.Message}" });
                return;
            }

            Player player = PhantomsWebUtil.FindTargetPlayer(playerName, playerDbId, out string findError);
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

                if (heroes.Count > 0)
                {
                    foreach (var h in heroes)
                    {
                        int lvl = Math.Clamp(h.Level, 0, 60);
                        ulong id = avatar.SpawnPhantomHeroFromIntent((PrototypeId)h.AvatarRef, lvl, null,
                            h.LockLevel && lvl > 0, h.CostumeRef, out string err);
                        if (id != 0) spawned++;
                        else { failed++; firstError ??= err; }
                    }
                }
                else
                {
                    int n = Math.Clamp(count > 0 ? count : 5, 1, 50);
                    int lvl = Math.Clamp(level, 0, 60);
                    for (int i = 0; i < n; i++)
                    {
                        // Match the chat command's semantics: an explicit level
                        // is a locked level unless lockLevel is explicitly false.
                        ulong id = lvl > 0 && lockLevel == false
                            ? avatar.SpawnPhantomHeroFromIntent(PrototypeId.Invalid, lvl, null, false, 0, out string err)
                            : avatar.SpawnPhantomHero(lvl, null, out err);
                        if (id != 0) spawned++;
                        else { failed++; firstError ??= err; }
                    }
                }

                return new { Ok = failed == 0 || spawned > 0, Error = (string)null, Spawned = spawned, Failed = failed, FirstError = firstError };
            });

            Logger.Info($"[Phantoms:Web] spawn for {player.GetName()}: {JsonSerializer.Serialize(result)}");
            await context.SendJsonAsync(result);
        }
    }

    public class PhantomsClearWebHandler : WebHandler
    {
        protected override async Task Post(WebRequestContext context)
        {
            string body = await context.ReadUtf8StringAsync();
            PhantomsWebUtil.ParseTarget(body, out string playerName, out string playerDbId);

            Player player = PhantomsWebUtil.FindTargetPlayer(playerName, playerDbId, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player, p => new { Ok = true, Removed = p.PurgePhantoms() });
            await context.SendJsonAsync(result);
        }
    }

    public class PhantomsCostumeWebHandler : WebHandler
    {
        protected override async Task Post(WebRequestContext context)
        {
            string body = await context.ReadUtf8StringAsync();

            string playerName = null, playerDbId = null, phantomQuery = null, costume = null;
            bool randomizeAll = false;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                if (root.TryGetProperty("playerName", out var pn)) playerName = pn.GetString();
                if (root.TryGetProperty("playerDbId", out var pd)) playerDbId = pd.GetString();
                if (root.TryGetProperty("phantomQuery", out var pq)) phantomQuery = pq.GetString();
                if (root.TryGetProperty("costume", out var cs)) costume = cs.GetString();
                if (root.TryGetProperty("randomizeAll", out var ra)) randomizeAll = ra.GetBoolean();
            }
            catch (Exception ex)
            {
                await context.SendJsonAsync(new { Ok = false, Error = $"bad request: {ex.Message}" });
                return;
            }

            Player player = PhantomsWebUtil.FindTargetPlayer(playerName, playerDbId, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player, p =>
            {
                string message = randomizeAll
                    ? p.RandomizePhantomCostumes()
                    : p.SetPhantomCostume(phantomQuery ?? "", costume ?? "random");
                return new { Ok = true, Message = message };
            });
            await context.SendJsonAsync(result);
        }
    }

    public class PhantomsGearWebHandler : WebHandler
    {
        protected override async Task Post(WebRequestContext context)
        {
            string body = await context.ReadUtf8StringAsync();

            string playerName = null, playerDbId = null, phantomQuery = null;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                if (root.TryGetProperty("playerName", out var pn)) playerName = pn.GetString();
                if (root.TryGetProperty("playerDbId", out var pd)) playerDbId = pd.GetString();
                if (root.TryGetProperty("phantomQuery", out var pq)) phantomQuery = pq.GetString();
            }
            catch (Exception ex)
            {
                await context.SendJsonAsync(new { Ok = false, Error = $"bad request: {ex.Message}" });
                return;
            }

            Player player = PhantomsWebUtil.FindTargetPlayer(playerName, playerDbId, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player,
                p => new { Ok = true, Message = p.RerollPhantomGear(string.IsNullOrWhiteSpace(phantomQuery) ? null : phantomQuery) });
            await context.SendJsonAsync(result);
        }
    }

    public class PhantomsSquadsWebHandler : WebHandler
    {
        protected override async Task Get(WebRequestContext context)
        {
            Player player = PhantomsWebUtil.FindTargetPlayer(PhantomsWebUtil.QueryParam(context, "player"), null, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found", Squads = new List<object>() });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player,
                p => new { Ok = true, Squads = p.GetPhantomSquadsForWeb() });
            await context.SendJsonAsync(result);
        }

        protected override async Task Post(WebRequestContext context)
        {
            string body = await context.ReadUtf8StringAsync();

            string playerName = null, playerDbId = null, op = null, name = null;
            var members = new List<Player.WebSquadSaveMember>();
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                if (root.TryGetProperty("playerName", out var pn)) playerName = pn.GetString();
                if (root.TryGetProperty("playerDbId", out var pd)) playerDbId = pd.GetString();
                if (root.TryGetProperty("op", out var opEl)) op = opEl.GetString();
                if (root.TryGetProperty("name", out var nm)) name = nm.GetString();
                if (root.TryGetProperty("members", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        var m = new Player.WebSquadSaveMember();
                        if (el.TryGetProperty("avatarRef", out var ar)) m.AvatarRef = PhantomsWebUtil.ParseRef(ar.GetString());
                        if (el.TryGetProperty("level", out var lv)) m.Level = lv.GetInt32();
                        if (el.TryGetProperty("lockLevel", out var ll)) m.LockLevel = ll.GetBoolean();
                        if (el.TryGetProperty("costumeRef", out var cr)) m.CostumeRef = PhantomsWebUtil.ParseRef(cr.GetString());
                        if (m.AvatarRef != 0) members.Add(m);
                    }
                }
            }
            catch (Exception ex)
            {
                await context.SendJsonAsync(new { Ok = false, Error = $"bad request: {ex.Message}" });
                return;
            }

            if (string.IsNullOrWhiteSpace(op) || string.IsNullOrWhiteSpace(name))
            {
                await context.SendJsonAsync(new { Ok = false, Error = "op and name are required" });
                return;
            }

            Player player = PhantomsWebUtil.FindTargetPlayer(playerName, playerDbId, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player, p =>
            {
                string message = op.ToLowerInvariant() switch
                {
                    "save" => p.SavePhantomSquad(name),
                    "savelist" => p.SavePhantomSquadFromList(name, members),
                    "spawn" or "load" => p.SpawnPhantomSquad(name, p.CurrentAvatar),
                    "delete" => p.DeletePhantomSquad(name),
                    _ => $"unknown op '{op}' — use save, savelist, spawn or delete",
                };
                return new { Ok = true, Message = message };
            });
            await context.SendJsonAsync(result);
        }
    }

    /// <summary>
    /// Shared plumbing for the phantom endpoints: target-player lookup (same
    /// reflection route as ItemGiveWebHandler) and game-thread marshaling.
    /// </summary>
    internal static class PhantomsWebUtil
    {
        public static string QueryParam(WebRequestContext context, string key)
        {
            var qs = System.Web.HttpUtility.ParseQueryString(context.QueryString ?? string.Empty);
            return qs[key];
        }

        public static async Task<object> RunOnGameThread(Player player, Func<Player, object> op)
        {
            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            player.RunPhantomWebOp(op, tcs);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            if (completed != tcs.Task)
                return new { Ok = false, Error = "timed out waiting for the game thread" };
            return tcs.Task.Result;
        }

        public static void ParseTarget(string body, out string playerName, out string playerDbId)
        {
            playerName = null;
            playerDbId = null;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                if (root.TryGetProperty("playerName", out var pn)) playerName = pn.GetString();
                if (root.TryGetProperty("playerDbId", out var pd)) playerDbId = pd.GetString();
            }
            catch { /* defaults */ }
        }

        public static ulong ParseRef(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
            return ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out ulong v) ? v : 0;
        }

        public static Player FindTargetPlayer(string playerName, string playerDbId, out string error)
        {
            error = null;

            var smType = Type.GetType("MHServerEmu.Core.Network.ServerManager, MHServerEmu.Core");
            var smInstance = smType?.GetProperty("Instance")?.GetValue(null);
            if (smInstance == null) { error = "ServerManager.Instance missing"; return null; }

            var services = smType.GetField("_services", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(smInstance) as System.Collections.IEnumerable;
            if (services == null) { error = "ServerManager._services missing"; return null; }

            object gameManager = null;
            foreach (var svc in services)
            {
                if (svc == null) continue;
                var prop = svc.GetType().GetProperty("GameManager", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop != null) { gameManager = prop.GetValue(svc); if (gameManager != null) break; }
            }
            if (gameManager == null) { error = "GameManager not reachable"; return null; }

            var gameDict = gameManager.GetType().GetField("_gameDict", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(gameManager) as System.Collections.IDictionary;
            if (gameDict == null || gameDict.Count == 0) { error = "no games running"; return null; }

            ulong dbId = ParseRef(playerDbId);

            foreach (var gameObj in gameDict.Values)
            {
                if (gameObj is not Game game) continue;

                if (dbId != 0)
                {
                    Player byDbId = game.EntityManager.GetEntityByDbGuid<Player>(dbId);
                    if (byDbId != null) return byDbId;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(playerName) == false && playerName != "*")
                {
                    Player byName = game.EntityManager.GetPlayerByName(playerName);
                    if (byName != null && byName.PlayerConnection != null) return byName;
                    continue;
                }

                foreach (Player candidate in new PlayerIterator(game))
                {
                    if (candidate?.PlayerConnection != null) return candidate;
                }
            }

            error = "player not found in any running game";
            return null;
        }
    }
}
