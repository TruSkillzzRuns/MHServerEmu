// Cross-account moderation for the "everyone's Trial of the Impossible runs"
// board (see TrialLeaderboardGlobalWebHandler). No role check — this whole
// WebApi surface is already trusted-operator-only (OmegaDev2), per explicit
// user request to drop the extra admin-account gate that was here.
//
//   POST /webapi/leaderboard/trial-global/delete  { targetPlayerName, id }
//   POST /webapi/leaderboard/trial-global/clear   {}  — wipes every account's TrialOfImpossible entries

using System.Text.Json;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.DatabaseAccess;
using MHServerEmu.Games.Entities;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class TrialLeaderboardAdminDeleteWebHandler : WebHandler
    {
        protected override async Task Post(WebRequestContext context)
        {
            string body = await context.ReadUtf8StringAsync();

            string targetPlayerName = null, id = null;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                if (root.TryGetProperty("targetPlayerName", out var t)) targetPlayerName = t.GetString();
                if (root.TryGetProperty("id", out var idEl)) id = idEl.GetString();
            }
            catch (Exception ex)
            {
                await context.SendJsonAsync(new { Ok = false, Error = $"bad request: {ex.Message}" });
                return;
            }

            if (string.IsNullOrWhiteSpace(targetPlayerName) || string.IsNullOrWhiteSpace(id))
            {
                await context.SendJsonAsync(new { Ok = false, Error = "targetPlayerName and id required" });
                return;
            }

            if (IDBManager.Instance.TryGetPlayerDbIdByName(targetPlayerName, out ulong targetId, out _) == false)
            {
                await context.SendJsonAsync(new { Ok = false, Error = $"no account named '{targetPlayerName}'" });
                return;
            }

            var entries = Player.LoadLeaderboardFileForAccount(targetId);
            int removed = entries.RemoveAll(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                await context.SendJsonAsync(new { Ok = false, Error = "no matching entry found" });
                return;
            }

            Player.SaveLeaderboardFileForAccount(targetId, entries);
            await context.SendJsonAsync(new { Ok = true, Message = $"Deleted entry for {targetPlayerName}." });
        }
    }

    public class TrialLeaderboardAdminClearWebHandler : WebHandler
    {
        protected override async Task Post(WebRequestContext context)
        {
            var playerNames = new Dictionary<ulong, string>();
            IDBManager.Instance.GetPlayerNames(playerNames);

            int totalRemoved = 0;
            foreach (ulong accountId in playerNames.Keys)
            {
                var entries = Player.LoadLeaderboardFileForAccount(accountId);
                int removed = entries.RemoveAll(e => e.Kind == Player.LeaderboardKind.TrialOfImpossible);
                if (removed == 0) continue;
                Player.SaveLeaderboardFileForAccount(accountId, entries);
                totalRemoved += removed;
            }

            await context.SendJsonAsync(new { Ok = true, Message = $"Cleared {totalRemoved} Trial of the Impossible entr{(totalRemoved == 1 ? "y" : "ies")} across every account." });
        }
    }
}
