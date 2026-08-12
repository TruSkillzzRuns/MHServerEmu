// OmegaDev2 Farm Session endpoints.
//
//   GET  /webapi/farm?player=*   — what dropped this session: totals, rate,
//                                  rarity split, per-region breakdown, and
//                                  the most recent drops
//   POST /webapi/farm/reset      — start a fresh session for that player
//
// The tracker itself (Games.Loot.FarmTracker) is lock-guarded and fed on the
// game thread from the item-acquisition path, so these handlers read it
// directly without game-thread marshaling — same arrangement as DpsWebHandler.

using MHServerEmu.Core.Network.Web;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Loot;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class FarmWebHandler : WebHandler
    {
        protected override async Task Get(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            string playerParam = PhantomsWebUtil.QueryParam(context, "player");

            Player player = PhantomsWebUtil.FindTargetPlayer(playerParam, null, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            long nowMs = (long)player.Game.CurrentTime.TotalMilliseconds;
            object snapshot = FarmTracker.GetSnapshot(player.Id, nowMs);

            if (snapshot == null)
            {
                // Distinct from an error: the player is here, nothing has
                // dropped yet. The dashboard should show an empty session,
                // not a failure.
                await context.SendJsonAsync(new
                {
                    Ok = true,
                    Empty = true,
                    Player = player.GetName(),
                    Message = "No drops recorded yet this session.",
                });
                return;
            }

            await context.SendJsonAsync(new { Ok = true, Empty = false, Session = snapshot });
        }
    }

    public class FarmResetWebHandler : WebHandler
    {
        protected override async Task Post(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            string playerParam = PhantomsWebUtil.QueryParam(context, "player");

            Player player = PhantomsWebUtil.FindTargetPlayer(playerParam, null, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            bool had = FarmTracker.Reset(player.Id);
            await context.SendJsonAsync(new { Ok = true, Cleared = had, Player = player.GetName() });
        }
    }
}
