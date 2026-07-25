// Debug endpoint — GET /webapi/debug/dialogtext?protoRef=0x...
// One-off diagnostic: reads a real MetaStateShutdownPrototype's embedded
// TeleportDialog (Text/Button1/Button2) and resolves each LocaleStringId to
// its actual client string via this process's own loaded Locale. Needed
// because LocaleStringId is NOT the same hash space as a generic
// PrototypeId — confirmed live: casting a prototype's own ProtoRef (e.g.
// Localization/Translations/Dialogs/Yes.prototype) to LocaleStringId
// resolved to "Invalid localeStringId" client-side instead of "Yes". This
// endpoint finds real, already-working IDs from an existing dialog instead.

using MHServerEmu.Core.Network.Web;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Locales;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class DialogTextDebugWebHandler : WebHandler
    {
        protected override Task Get(WebRequestContext context)
        {
            ulong refVal = PhantomsWebUtil.ParseRef(PhantomsWebUtil.QueryParam(context, "protoRef"));
            if (refVal == 0)
                return context.SendJsonAsync(new { Ok = false, Error = "missing/invalid protoRef" });

            var meta = GameDatabase.GetPrototype<MetaStateShutdownPrototype>((PrototypeId)refVal);
            if (meta == null)
                return context.SendJsonAsync(new { Ok = false, Error = "protoRef did not resolve to a MetaStateShutdownPrototype" });

            var dialog = meta.TeleportDialog;
            if (dialog == null)
                return context.SendJsonAsync(new { Ok = false, Error = "resolved, but TeleportDialog is not set on this instance" });

            var locale = LocaleManager.Instance.CurrentLocale;
            return context.SendJsonAsync(new
            {
                Ok = true,
                TextId = (long)dialog.Text,
                TextResolved = locale?.GetLocaleString(dialog.Text),
                Button1Id = (long)dialog.Button1,
                Button1Resolved = locale?.GetLocaleString(dialog.Button1),
                Button2Id = (long)dialog.Button2,
                Button2Resolved = locale?.GetLocaleString(dialog.Button2),
            });
        }
    }
}
