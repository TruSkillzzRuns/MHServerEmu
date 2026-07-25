// Debug endpoint — GET /webapi/debug/uiwidgets
// One-off diagnostic: lists every real UIWidgetGenericFractionPrototype the
// loaded client data ships, with its native Descriptor string resolved.
// Player.WaveDirector.cs / Player.TrialOfImpossible.cs both drive their
// kill/wave counters by grabbing "the first one found" — confirmed live that
// this can pick up an instance whose baked Descriptor belongs to a real
// quest ("Defeat Mindless Titan"), which is misleading even though the
// fraction counts themselves are correct. This lists every candidate so a
// better (blank/generic) one can be picked deliberately instead.

using MHServerEmu.Core.Network.Web;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Locales;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class UiWidgetScanDebugWebHandler : WebHandler
    {
        protected override Task Get(WebRequestContext context)
        {
            var locale = LocaleManager.Instance.CurrentLocale;
            var results = new List<object>();

            foreach (PrototypeId protoRef in DataDirectory.Instance
                .IteratePrototypesInHierarchy<UIWidgetGenericFractionPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                var proto = protoRef.As<UIWidgetGenericFractionPrototype>();
                if (proto == null) continue;

                string descriptorText = proto.Descriptor != LocaleStringId.Invalid
                    ? locale?.GetLocaleString(proto.Descriptor)
                    : null;

                results.Add(new
                {
                    ProtoRef = $"0x{(ulong)protoRef:X16}",
                    Path = protoRef.GetName(),
                    DescriptorId = (long)proto.Descriptor,
                    DescriptorText = descriptorText,
                    IsBlankOrInvalid = proto.Descriptor == LocaleStringId.Invalid || string.IsNullOrWhiteSpace(descriptorText),
                });
            }

            return context.SendJsonAsync(new { Ok = true, Count = results.Count, Widgets = results });
        }
    }
}
