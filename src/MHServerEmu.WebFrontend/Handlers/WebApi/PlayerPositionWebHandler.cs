// Debug endpoint — GET /webapi/debug/position?player=
// Returns the requesting player's current avatar region + coordinates.
// Purpose-built to find exact in-world spawn positions (e.g. "stand next to
// the helicopter in Avengers Tower, tell me the numbers") without needing a
// dedicated in-game command — read-only, no state changes.

using MHServerEmu.Core.Network.Web;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.GameData;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class PlayerPositionWebHandler : WebHandler
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

            object result = await PhantomsWebUtil.RunOnGameThread(player, p =>
            {
                var avatar = p.CurrentAvatar;
                if (avatar == null || avatar.IsInWorld == false)
                    return new { Ok = false, Error = "avatar not in world" };

                var pos = avatar.RegionLocation.Position;
                var ori = avatar.RegionLocation.Orientation;
                return new
                {
                    Ok = true,
                    RegionRef = "0x" + ((ulong)avatar.Region.PrototypeDataRef).ToString("X"),
                    RegionName = avatar.Region.PrototypeDataRef.GetName(),
                    X = pos.X,
                    Y = pos.Y,
                    Z = pos.Z,
                    Yaw = ori.Yaw,
                };
            });
            await context.SendJsonAsync(result);
        }
    }
}
