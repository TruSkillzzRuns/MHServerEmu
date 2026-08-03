// Debug endpoint — GET /webapi/debug/visiblebydefault?protoRef=0x...
// Returns just WorldEntityPrototype.VisibleByDefault for a given prototype
// ref. The generic /webapi/protoeditor/fields dump crashes on every
// prototype (base Prototype.ClassType field isn't JSON-serializable) — this
// is a narrow, targeted read to check ONE specific rendering-gating flag
// (see AreaOfInterest.cs:951 / InteractionManager.GetVisibilityStatus)
// without touching that broken generic path. Read-only, no state changes.

using MHServerEmu.Core.Network.Web;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class VisibleByDefaultWebHandler : WebHandler
    {
        protected override async Task Get(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            string protoRefStr = PhantomsWebUtil.QueryParam(context, "protoRef");
            PrototypeId protoRef = (PrototypeId)PhantomsWebUtil.ParseRef(protoRefStr);
            if (protoRef == PrototypeId.Invalid)
            {
                await context.SendJsonAsync(new { Ok = false, Error = "missing or invalid 'protoRef'" });
                return;
            }

            var proto = protoRef.As<WorldEntityPrototype>();
            if (proto == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = "protoRef did not resolve to a WorldEntityPrototype" });
                return;
            }

            await context.SendJsonAsync(new
            {
                Ok = true,
                ProtoRef = protoRefStr,
                Path = GameDatabase.GetPrototypeName(protoRef),
                proto.VisibleByDefault,
                proto.SnapToFloorOnSpawn,
                IsLiveTuningVisible = proto.IsLiveTuningVisible(),
                Bounds = proto.Bounds != null ? proto.Bounds.GetType().Name : "<null>",
            });
        }
    }
}
