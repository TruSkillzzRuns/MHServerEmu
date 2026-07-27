// Debug endpoint — GET /webapi/debug/findasset?typeFromAssetId=0x...&name=...
// Given a known-good AssetId (e.g. another entity's working UnrealClass
// value), resolves its AssetType, then searches that SAME asset type for a
// different asset by name — used to check whether an alternate/known-good
// mesh package (e.g. one confirmed to load in an external mesh tool) is
// actually registered as a valid UnrealClass-type asset the engine knows
// about, so it could be used as a real Runtime prototype patch target
// instead of the current (broken) reference. Read-only, no state changes.

using System.Linq;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Calligraphy;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class FindAssetWebHandler : WebHandler
    {
        protected override async Task Get(WebRequestContext context)
        {
            string typeFromAssetIdStr = PhantomsWebUtil.QueryParam(context, "typeFromAssetId");
            string name = PhantomsWebUtil.QueryParam(context, "name");

            if (string.IsNullOrWhiteSpace(name))
            {
                await context.SendJsonAsync(new { Ok = false, Error = "missing 'name'" });
                return;
            }

            ulong rawAssetId = PhantomsWebUtil.ParseRef(typeFromAssetIdStr);
            if (rawAssetId == 0)
            {
                await context.SendJsonAsync(new { Ok = false, Error = "missing or invalid 'typeFromAssetId'" });
                return;
            }

            AssetId knownGoodAssetId = (AssetId)rawAssetId;
            AssetType assetType = AssetDirectory.Instance.GetAssetType(knownGoodAssetId);
            if (assetType == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = "typeFromAssetId did not resolve to a known AssetType" });
                return;
            }

            AssetId exactMatch = assetType.FindAssetByName(name, true);
            List<AssetId> containsMatches = assetType.FindAssetsByNameContains(name, true);

            await context.SendJsonAsync(new
            {
                Ok = true,
                AssetTypeRef = $"0x{(ulong)assetType.AssetTypeRef:X16}",
                AssetTypeName = GameDatabase.GetAssetTypeName(assetType.AssetTypeRef),
                SearchedName = name,
                ExactMatch = exactMatch != AssetId.Invalid ? $"0x{(ulong)exactMatch:X16}" : null,
                ContainsMatches = containsMatches.Select(id => new
                {
                    AssetId = $"0x{(ulong)id:X16}",
                    Name = GameDatabase.GetAssetName(id),
                }).ToList(),
            });
        }
    }
}
