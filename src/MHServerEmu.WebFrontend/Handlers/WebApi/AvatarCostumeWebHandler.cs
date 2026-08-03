// OmegaDev2 Gear Picker — "force equip" costume test endpoint.
//
//   POST /webapi/avatar/costume  — body: { "playerName": "...", "playerDbId": "0x...",
//                                          "costumeProtoRef": "0x..." }
//                                  Force-equips a costume directly on the target
//                                  player's CURRENT live avatar via
//                                  Avatar.ChangeCostume() + a visual refresh —
//                                  the same mechanism used for phantom NPCs.
//                                  Deliberately bypasses the item/store/closet
//                                  flow entirely: no item is created, no
//                                  inventory is touched, CostumeUnlock is not
//                                  granted. Built to test whether a costume's
//                                  DesignState (checked by the retail Closet
//                                  UI against its own client-local Calligraphy
//                                  copy) blocks avatar rendering itself, or
//                                  only blocks the Closet UI's purchase list.

using System.Text.Json;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.GameData;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class AvatarCostumeWebHandler : WebHandler
    {
        protected override async Task Post(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            string body = await context.ReadUtf8StringAsync();

            string playerName = null, playerDbId = null, costumeProtoRefRaw = null;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                if (root.TryGetProperty("playerName", out var pn)) playerName = pn.GetString();
                if (root.TryGetProperty("playerDbId", out var pd)) playerDbId = pd.GetString();
                if (root.TryGetProperty("costumeProtoRef", out var cr)) costumeProtoRefRaw = cr.GetString();
            }
            catch (System.Exception ex)
            {
                await context.SendJsonAsync(new { Ok = false, Error = $"bad request: {ex.Message}" });
                return;
            }

            ulong costumeRaw = PhantomsWebUtil.ParseRef(costumeProtoRefRaw);
            var costumeRef = (PrototypeId)costumeRaw;
            if (costumeRef == PrototypeId.Invalid || costumeRef.As<Games.GameData.Prototypes.CostumePrototype>() == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = "costumeProtoRef is not a costume prototype ref" });
                return;
            }

            Player player = PhantomsWebUtil.FindTargetPlayer(playerName, playerDbId, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player,
                p => new { Ok = true, Message = p.ForceEquipCostumeOnCurrentAvatar(costumeRef) });
            await context.SendJsonAsync(result);
        }
    }
}
