// Debug endpoint — GET /webapi/debug/fractionwidget?protoRef=0x...
// Reports the live, in-memory state of a UIWidgetGenericFractionPrototype's
// Descriptor + icon fields — used to confirm whether Player.WaveDirector.cs's
// runtime patch (GetEndlessWaveOnlyWidgetRef) actually applied, and whether
// icon assets (not just Descriptor text) are what makes a widget render as
// icon-pips instead of plain text. Read-only, no state changes.

using MHServerEmu.Core.Network.Web;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Locales;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class FractionWidgetDebugWebHandler : WebHandler
    {
        protected override async Task Get(WebRequestContext context)
        {
            string protoRefStr = PhantomsWebUtil.QueryParam(context, "protoRef");
            PrototypeId protoRef = (PrototypeId)PhantomsWebUtil.ParseRef(protoRefStr);
            if (protoRef == PrototypeId.Invalid)
            {
                await context.SendJsonAsync(new { Ok = false, Error = "missing or invalid 'protoRef'" });
                return;
            }

            var proto = protoRef.As<UIWidgetGenericFractionPrototype>();
            if (proto == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = "protoRef did not resolve to a UIWidgetGenericFractionPrototype" });
                return;
            }

            var locale = LocaleManager.Instance.CurrentLocale;
            string descriptorText = proto.Descriptor != LocaleStringId.Invalid ? locale?.GetLocaleString(proto.Descriptor) : null;

            await context.SendJsonAsync(new
            {
                Ok = true,
                ProtoRef = protoRefStr,
                Path = GameDatabase.GetPrototypeName(protoRef),
                DescriptorId = (long)proto.Descriptor,
                DescriptorText = descriptorText,
                IconComplete = (long)proto.IconComplete,
                IconIncomplete = (long)proto.IconIncomplete,
#if GAME_VERSION_1_52 || GAME_VERSION_1_53
                IconCompleteHiRes = (long)proto.IconCompleteHiRes,
                IconIncompleteHiRes = (long)proto.IconIncompleteHiRes,
#else
                IconCompleteHiRes = 0L,
                IconIncompleteHiRes = 0L,
#endif
                proto.IconSpacing,
            });
        }
    }
}
