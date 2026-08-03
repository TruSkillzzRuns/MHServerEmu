// OmegaDev2 DPS Meter endpoints.
//
//   GET  /webapi/dps?player=*      — per-combatant damage snapshot for the
//                                    player's hero + their phantoms (sorted
//                                    by total damage)
//   POST /webapi/dps/reset         — clear the meter
//
// The meter itself (Games.Powers.DpsMeter) is lock-guarded and fed on the
// game thread from the damage-application path, so these handlers can read
// it directly without game-thread marshaling.

using MHServerEmu.Core.Network.Web;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Powers;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class DpsWebHandler : WebHandler
    {
        protected override async Task Get(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            string playerParam = PhantomsWebUtil.QueryParam(context, "player");

            ulong ownerFilter = 0;
            string playerName = null;
            if (string.IsNullOrWhiteSpace(playerParam) == false && playerParam != "all")
            {
                Player player = PhantomsWebUtil.FindTargetPlayer(playerParam, null, out string error);
                if (player == null)
                {
                    await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                    return;
                }
                ownerFilter = player.Id;
                playerName = player.GetName();
            }

            var combatants = DpsMeter.GetSnapshots(ownerFilter);
            await context.SendJsonAsync(new
            {
                Ok = true,
                Player = playerName,
                SecondsSinceReset = DpsMeter.SecondsSinceReset,
                Combatants = combatants,
            });
        }
    }

    public class DpsResetWebHandler : WebHandler
    {
        protected override async Task Post(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            DpsMeter.Reset();
            await context.SendJsonAsync(new { Ok = true, Message = "DPS meter reset." });
        }
    }
}
