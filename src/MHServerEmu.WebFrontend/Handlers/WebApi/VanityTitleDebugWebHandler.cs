// Debug endpoint — GET /webapi/debug/vanitytitle?protoRef=0x...
// One-off diagnostic: reads a real VanityTitlePrototype's Text LocaleStringId
// and resolves it through this process's own loaded Locale — same targeted
// approach as DialogTextDebugWebHandler (the generic RuntimePrototypeEditor
// field reader has a pre-existing JSON serialization bug unrelated to this).

using MHServerEmu.Core.Network.Web;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Locales;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class VanityTitleDebugWebHandler : WebHandler
    {
        protected override Task Get(WebRequestContext context)
        {
            ulong refVal = PhantomsWebUtil.ParseRef(PhantomsWebUtil.QueryParam(context, "protoRef"));
            if (refVal == 0)
                return context.SendJsonAsync(new { Ok = false, Error = "missing/invalid protoRef" });

            var titleProto = GameDatabase.GetPrototype<VanityTitlePrototype>((PrototypeId)refVal);
            if (titleProto == null)
                return context.SendJsonAsync(new { Ok = false, Error = "protoRef did not resolve to a VanityTitlePrototype" });

            var locale = LocaleManager.Instance.CurrentLocale;
            return context.SendJsonAsync(new
            {
                Ok = true,
                TextId = (long)titleProto.Text,
                TextResolved = locale?.GetLocaleString(titleProto.Text),
            });
        }
    }
}
