// Custom costume catalog — a bridge for MHCostumeMod (Mr.Gippy), which lets
// players add costumes the retail game never had.
//
//   GET /webapi/customcostumes/catalog
//     -> 200 { "ok": true, "costumes": [ { name, token, enum, customId, hero } ],
//                          "fxPacks":  [ { token, displayName, hero, effects } ] }
//     -> 404 on any server without the mod's costume loader
//
// Why this exists
// ---------------
// A custom costume has no entry anywhere in the game's prototype data. The
// mod's loader mints a prototype id at runtime and aliases it onto a real
// "donor" costume's data record, and the injected client DLL substitutes the
// art. Every catalog endpoint in this fork enumerates prototypes from the
// loaded client data, so none of them can ever see a custom costume, and
// OmegaDev2 therefore cannot offer one when picking a phantom's costume.
//
// The mod's own MHServerEmu fork publishes an equivalent endpoint, but only
// its WebFrontend does — and merging a WebFrontend is the awkward half of the
// merge, since that is where every route in here is registered. Anyone who
// merges only the Games-layer half gets working custom costumes in game and
// no way for the app to list them.
//
// The reflection that reaches the mod lives in CustomCostumeBridge, shared
// with the phantom spawn path. On a stock server the mod's type is absent, the
// bridge reports nothing, and this endpoint 404s.

using System.Net;
using System.Text.Json.Nodes;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.Games.GameData;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class CustomCostumesCatalogWebHandler : WebHandler
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        protected override async Task Get(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            if (CustomCostumeBridge.IsAvailable == false)
            {
                // Not an error: this is the answer for every server not
                // running the costume mod, which is nearly all of them.
                context.StatusCode = (int)HttpStatusCode.NotFound;
                await context.SendJsonAsync(new
                {
                    Ok = false,
                    Error = "This server does not have custom costume support installed."
                });
                return;
            }

            try
            {
                var costumes = new JsonArray();
                foreach (CustomCostumeInfo entry in CustomCostumeBridge.GetCatalog())
                {
                    var costume = new JsonObject
                    {
                        ["name"] = entry.Name,
                        ["token"] = entry.Token,
                        ["enum"] = entry.Enum,
                        // The mod formats its minted ids this way; matching it
                        // means a client never has to know which server it asked.
                        ["customId"] = $"0x{entry.CustomId:X16}",
                    };

                    // Not part of the mod's own catalog format, and the reason
                    // this is worth adding: without it a client has to guess
                    // the hero from the costume's name, which misfires on any
                    // hero whose name contains another's. Resolved from the
                    // donor's UsableBy, so it is exact. Omitted rather than
                    // sent as zero when it cannot be resolved, so a client can
                    // tell "unknown" from a real answer and fall back.
                    PrototypeId heroRef = CustomCostumeBridge.GetHeroFor((PrototypeId)entry.CustomId);
                    if (heroRef != PrototypeId.Invalid)
                        costume["hero"] = $"0x{(ulong)heroRef:X16}";

                    costumes.Add(costume);
                }

                var packs = new JsonArray();
                foreach (CustomCostumeFxPackInfo pack in CustomCostumeBridge.GetFxPacks())
                {
                    packs.Add(new JsonObject
                    {
                        ["token"] = pack.Token,
                        ["displayName"] = pack.DisplayName,
                        ["hero"] = pack.Hero,
                        // A count of effect packages, not a list of them.
                        ["effects"] = pack.Effects,
                    });
                }

                await context.SendAsync(
                    new JsonObject { ["ok"] = true, ["costumes"] = costumes, ["fxPacks"] = packs }.ToJsonString(),
                    "application/json");
            }
            catch (Exception e)
            {
                // An unexpected shape is worth a log line and a real error:
                // silently returning an empty catalog would read as "no custom
                // costumes installed", which is a different thing entirely.
                Logger.Warn($"CustomCostumesCatalog: failed to read the custom costume catalog: {e.Message}");
                context.StatusCode = (int)HttpStatusCode.InternalServerError;
                await context.SendJsonAsync(new { Ok = false, Error = "Failed to read the custom costume catalog." });
            }
        }
    }
}
