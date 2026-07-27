// OmegaDev2 "Region Events" tool — generic runtime prototype field editor,
// scoped in the app to MetaGame/MetaState prototypes (the machinery behind
// real events like X-Defense). Read-only discover/read here; write/clone
// (the actual mutation surface) live in PrototypeEditorWriteWebHandler.cs.
// Backed by RuntimePrototypeEditor — reflection-based, no .sip/client file
// ever touched, mutations are in-memory only (no persistence, no revert
// short of a server restart). See the app page's warning banner and the
// plan at PENDING_PUSH.md-adjacent notes for the full risk writeup.
//
//   GET /webapi/protoeditor/discover?baseType=MetaGame|MetaState&q=substring
//   GET /webapi/protoeditor/fields?protoRef=0x...

using System.Web;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.PatchManager;
using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class PrototypeEditorDiscoverWebHandler : WebHandler
    {
        protected override async Task Get(WebRequestContext context)
        {
            var qs = HttpUtility.ParseQueryString(context.QueryString ?? string.Empty);
            string baseType = (qs.Get("baseType") ?? "MetaGame").Trim();
            string query = (qs.Get("q") ?? string.Empty).Trim();
            int.TryParse(qs.Get("limit"), out int limit);
            if (limit <= 0 || limit > 5000) limit = 300;

            Type iterateType = baseType.Equals("MetaState", StringComparison.OrdinalIgnoreCase)
                ? typeof(MetaStatePrototype)
                : baseType.Equals("Agent", StringComparison.OrdinalIgnoreCase)
                    ? typeof(AgentPrototype)
                    : baseType.Equals("WorldEntity", StringComparison.OrdinalIgnoreCase)
                        ? typeof(WorldEntityPrototype)
                        : baseType.Equals("UIWidgetMissionText", StringComparison.OrdinalIgnoreCase)
                            ? typeof(UIWidgetMissionTextPrototype)
                            : baseType.Equals("Hotspot", StringComparison.OrdinalIgnoreCase)
                                ? typeof(HotspotPrototype)
                                : baseType.Equals("Rank", StringComparison.OrdinalIgnoreCase)
                                    ? typeof(RankPrototype)
                                    : typeof(MetaGamePrototype);

            var results = new List<object>();
            foreach (PrototypeId protoRef in DataDirectory.Instance.IteratePrototypesInHierarchy(iterateType, PrototypeIterateFlags.NoAbstract))
            {
                if (protoRef == PrototypeId.Invalid) continue;
                string path = GameDatabase.GetPrototypeName(protoRef);
                if (string.IsNullOrEmpty(path)) continue;
                if (query.Length > 0 && path.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;

                Prototype proto = protoRef.As<Prototype>();
                bool? visibleByDefault = (proto as WorldEntityPrototype)?.VisibleByDefault;
                results.Add(new
                {
                    ProtoRef = $"0x{(ulong)protoRef:X16}",
                    Name = ExtractLeaf(path),
                    Path = path,
                    ConcreteTypeName = proto?.GetType().Name ?? "Unknown",
                    VisibleByDefault = visibleByDefault,
                });
                if (results.Count >= limit) break;
            }

            await context.SendJsonAsync(new
            {
                Ok = true,
                BaseType = iterateType.Name,
                Query = query,
                Truncated = results.Count >= limit,
                Results = results,
            });
        }

        private static string ExtractLeaf(string path)
        {
            int slash = path.LastIndexOf('/');
            string leaf = slash >= 0 ? path[(slash + 1)..] : path;
            const string suffix = ".prototype";
            if (leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                leaf = leaf[..^suffix.Length];
            return leaf;
        }
    }

    public class PrototypeEditorReadWebHandler : WebHandler
    {
        protected override async Task Get(WebRequestContext context)
        {
            var qs = HttpUtility.ParseQueryString(context.QueryString ?? string.Empty);
            string protoRefStr = qs.Get("protoRef");
            PrototypeId protoRef = (PrototypeId)PhantomsWebUtil.ParseRef(protoRefStr);
            if (protoRef == PrototypeId.Invalid)
            {
                await context.SendJsonAsync(new { Ok = false, Error = "missing or invalid 'protoRef'" });
                return;
            }

            var fields = RuntimePrototypeEditor.ReadAllFields(protoRef);
            await context.SendJsonAsync(new
            {
                Ok = true,
                ProtoRef = protoRefStr,
                Name = GameDatabase.GetPrototypeName(protoRef),
                Fields = fields,
            });
        }
    }
}
