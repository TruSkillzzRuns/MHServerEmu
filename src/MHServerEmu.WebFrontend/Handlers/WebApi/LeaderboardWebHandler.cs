// OmegaDev2 Leaderboard endpoints.
//
//   GET  /webapi/leaderboard?player=&kind=&hero=   — best-first list of saved
//                                                     DPS parses and/or
//                                                     terminal run times
//   POST /webapi/leaderboard/commit-dps            body: { playerName, heroName, dpsValue }
//                                                     — explicitly save the
//                                                     current top DPS parse
//   POST /webapi/leaderboard/delete                 body: { playerName, id }
//   POST /webapi/leaderboard/clear                  body: { playerName, kind? } — kind omitted = clear everything
//
// Terminal runs are logged automatically server-side (Avatar.OnEnteredWorld →
// Player.OnAvatarEnteredRegion) — there is no "commit" endpoint for those.

using System.Text.Json;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.Games.Entities;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class LeaderboardWebHandler : WebHandler
    {
        protected override async Task Get(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            string kind = PhantomsWebUtil.QueryParam(context, "kind");
            string hero = PhantomsWebUtil.QueryParam(context, "hero");

            Player player = PhantomsWebUtil.FindTargetPlayer(PhantomsWebUtil.QueryParam(context, "player"), null, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player,
                p => new { Ok = true, Entries = p.GetLeaderboardForWeb(kind, hero) });
            await context.SendJsonAsync(result);
        }
    }

    public class LeaderboardCommitDpsWebHandler : WebHandler
    {
        protected override async Task Post(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            string body = await context.ReadUtf8StringAsync();

            string playerName = null, heroName = null;
            double dpsValue = 0;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                if (root.TryGetProperty("playerName", out var pn)) playerName = pn.GetString();
                if (root.TryGetProperty("heroName", out var hn)) heroName = hn.GetString();
                if (root.TryGetProperty("dpsValue", out var dv)) dpsValue = dv.GetDouble();
            }
            catch (System.Exception ex)
            {
                await context.SendJsonAsync(new { Ok = false, Error = $"bad request: {ex.Message}" });
                return;
            }

            Player player = PhantomsWebUtil.FindTargetPlayer(playerName, null, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player,
                p => new { Ok = true, Message = p.CommitDpsToLeaderboard(heroName, dpsValue) });
            await context.SendJsonAsync(result);
        }
    }

    public class LeaderboardDeleteWebHandler : WebHandler
    {
        protected override async Task Post(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            string body = await context.ReadUtf8StringAsync();

            string playerName = null, id = null;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                if (root.TryGetProperty("playerName", out var pn)) playerName = pn.GetString();
                if (root.TryGetProperty("id", out var idEl)) id = idEl.GetString();
            }
            catch (System.Exception ex)
            {
                await context.SendJsonAsync(new { Ok = false, Error = $"bad request: {ex.Message}" });
                return;
            }

            Player player = PhantomsWebUtil.FindTargetPlayer(playerName, null, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player,
                p => new { Ok = true, Message = p.DeleteLeaderboardEntry(id) });
            await context.SendJsonAsync(result);
        }
    }

    public class LeaderboardClearWebHandler : WebHandler
    {
        protected override async Task Post(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            string body = await context.ReadUtf8StringAsync();

            string playerName = null, kind = null;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                if (root.TryGetProperty("playerName", out var pn)) playerName = pn.GetString();
                if (root.TryGetProperty("kind", out var kindEl)) kind = kindEl.GetString();
            }
            catch (System.Exception ex)
            {
                await context.SendJsonAsync(new { Ok = false, Error = $"bad request: {ex.Message}" });
                return;
            }

            Player player = PhantomsWebUtil.FindTargetPlayer(playerName, null, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player,
                p => new { Ok = true, Message = p.ClearLeaderboard(kind) });
            await context.SendJsonAsync(result);
        }
    }
}
