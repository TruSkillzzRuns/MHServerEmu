// Phantom Heroes runtime WebAPI — installed by the standalone PhantomHeroes tool.
//
// Adds two POST endpoints to the vanilla WebFrontend so PhantomHeroes.exe (and
// any other REST client) can spawn / clear phantom hero NPCs on the running
// server without opening the client chat window.
//
//   POST /webapi/phantom/spawn   body: {"count": 10, "level": 60}
//   POST /webapi/phantom/clear
//
// The handler iterates the first live Game instance, picks its first connected
// Player, and calls Avatar.SpawnPhantomHero / DespawnAllPhantomHeroes directly.
// This is designed for the single-player local-server case — 99% of the way
// PhantomHeroes is used. Multi-player servers should route via the chat
// command (!phantom spawn) which naturally runs on the game thread.

using System.Reflection;
using System.Text.Json;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network;
using MHServerEmu.Core.Network.Web;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class PhantomHeroSpawnWebHandler : WebHandler
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        protected override async Task Post(WebRequestContext context)
        {
            string body = await context.ReadUtf8StringAsync();
            int count = 5, level = 60;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (doc.RootElement.TryGetProperty("count", out var c)) count = c.GetInt32();
                if (doc.RootElement.TryGetProperty("level", out var l)) level = l.GetInt32();
            }
            catch { /* accept empty / malformed body — use defaults */ }
            count = MHServerEmu.Games.Entities.Avatars.PhantomCommandUtil.ClampCount(count, 5, 50);
            level = System.Math.Clamp(level, MHServerEmu.Games.Entities.Avatars.PhantomCommandUtil.MinLevel, MHServerEmu.Games.Entities.Avatars.PhantomCommandUtil.MaxLevel);

            var (avatar, err) = PhantomHeroRuntime.FindAnyPlayerAvatar();
            if (avatar == null)
            {
                await context.SendJsonAsync(new { ok = false, error = err ?? "no player online" });
                return;
            }

            // Marshal onto the game thread — Player.EnterGame reads
            // Game.Current (thread-static) and NREs when called off-thread.
            // SpawnPhantomHeroesFromWeb enqueues a zero-delay event on the
            // caller's game scheduler and returns immediately.
            var scheduleMethod = avatar.GetType().GetMethod("SpawnPhantomHeroesFromWeb",
                BindingFlags.Public | BindingFlags.Instance);
            if (scheduleMethod == null)
            {
                await context.SendJsonAsync(new { ok = false, error = "SpawnPhantomHeroesFromWeb missing — is Avatar.PhantomHero.cs current?" });
                return;
            }
            try { scheduleMethod.Invoke(avatar, new object[] { count, level }); }
            catch (System.Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                await context.SendJsonAsync(new { ok = false, error = $"{inner.GetType().Name}: {inner.Message}" });
                return;
            }

            Logger.Info($"[PhantomHero:Web] queued spawn count={count} level={level}");
            await context.SendJsonAsync(new { ok = true, queued = count, level, note = "Spawn queued on the game thread. Poll /webapi/phantom/status to observe the count." });
        }
    }

    public class PhantomHeroClearWebHandler : WebHandler
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        protected override async Task Post(WebRequestContext context)
        {
            var (avatar, err) = PhantomHeroRuntime.FindAnyPlayerAvatar();
            if (avatar == null)
            {
                await context.SendJsonAsync(new { ok = false, error = err ?? "no player online" });
                return;
            }

            var method = avatar.GetType().GetMethod("DespawnAllPhantomHeroesFromWeb", BindingFlags.Public | BindingFlags.Instance);
            if (method == null)
            {
                await context.SendJsonAsync(new { ok = false, error = "DespawnAllPhantomHeroesFromWeb missing — is Avatar.PhantomHero.cs current?" });
                return;
            }
            try { method.Invoke(avatar, System.Array.Empty<object>()); }
            catch (System.Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                await context.SendJsonAsync(new { ok = false, error = $"{inner.GetType().Name}: {inner.Message}" });
                return;
            }
            Logger.Info("[PhantomHero:Web] queued clear");
            await context.SendJsonAsync(new { ok = true, note = "Clear queued on the game thread." });
        }
    }

    public class PhantomHeroStatusWebHandler : WebHandler
    {
        protected override async Task Get(WebRequestContext context)
        {
            var (avatar, err) = PhantomHeroRuntime.FindAnyPlayerAvatar();
            if (avatar == null)
            {
                await context.SendJsonAsync(new { ok = false, error = err ?? "no player online", online = false });
                return;
            }

            var prop = avatar.GetType().GetProperty("PhantomHeroCount");
            int alive = prop != null ? (int)(prop.GetValue(avatar) ?? 0) : -1;
            await context.SendJsonAsync(new { ok = true, online = true, alive });
        }
    }

    /// <summary>
    /// Shared helper: reach into MHServerEmu.Games via reflection so this handler
    /// file has no compile-time dependency on internal Games types (keeps the
    /// vanilla-WebFrontend project references untouched).
    /// </summary>
    internal static class PhantomHeroRuntime
    {
        public static (object? avatar, string? error) FindAnyPlayerAvatar()
        {
            // 1. Locate GameInstanceService.Instance via ServerManager.
            var smType = System.Type.GetType("MHServerEmu.Core.Network.ServerManager, MHServerEmu.Core");
            var smInstance = smType?.GetProperty("Instance")?.GetValue(null);
            if (smInstance == null) return (null, "ServerManager.Instance missing");

            // 2. Walk services (IGameService[]) to find GameInstanceService — has a GameManager.
            var services = smType!.GetField("_services", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(smInstance) as System.Collections.IEnumerable;
            if (services == null) return (null, "ServerManager _services field missing");

            object? gameManager = null;
            foreach (var v in services)
            {
                if (v == null) continue;
                var prop = v.GetType().GetProperty("GameManager", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop != null) { gameManager = prop.GetValue(v); if (gameManager != null) break; }
            }
            if (gameManager == null) return (null, "GameManager not reachable from any registered service");

            // 3. Grab any Game with at least one player.
            var gameDictField = gameManager.GetType().GetField("_gameDict", BindingFlags.NonPublic | BindingFlags.Instance);
            var gameDict = gameDictField?.GetValue(gameManager) as System.Collections.IDictionary;
            if (gameDict == null || gameDict.Count == 0) return (null, "no games running");

            foreach (var game in gameDict.Values)
            {
                var entityManager = game?.GetType().GetProperty("EntityManager")?.GetValue(game);
                var playersSet = entityManager?.GetType().GetProperty("Players")?.GetValue(entityManager) as System.Collections.IEnumerable;
                if (playersSet == null) continue;
                foreach (var player in playersSet)
                {
                    var avatarProp = player?.GetType().GetProperty("CurrentAvatar");
                    var avatar = avatarProp?.GetValue(player);
                    if (avatar != null) return (avatar, null);
                }
            }
            return (null, "no online avatars in any game");
        }
    }
}
