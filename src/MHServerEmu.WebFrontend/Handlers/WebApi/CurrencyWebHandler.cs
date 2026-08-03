// OmegaDev2 Currency Editor — view a player's balance for every real
// currency the client knows about, and set any one of them directly.
// Same PropertyEnum.Currency mechanism the `!player givecurrency` chat
// command uses (which only ever grants ALL currencies at once) — this
// exposes per-currency, absolute-value control instead.
//
//   GET  /webapi/currency/list?player=*        — { ok, player, currencies: [{protoRef, name, amount, maxAmount}] }
//   POST /webapi/currency/set                  — { playerName, currencyProtoRef, amount } -> { ok, previousAmount, newAmount }

using System.Text.Json;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Properties;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class CurrencyListWebHandler : WebHandler
    {
        protected override async Task Get(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            string playerName = PhantomsWebUtil.QueryParam(context, "player");
            Player player = PhantomsWebUtil.FindTargetPlayer(playerName, null, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player, p =>
            {
                var currencies = new List<object>();
                foreach (PrototypeId currencyRef in DataDirectory.Instance.IteratePrototypesInHierarchy<CurrencyPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
                {
                    CurrencyPrototype proto = currencyRef.As<CurrencyPrototype>();
                    if (proto == null) continue;

                    long amount = p.Properties[PropertyEnum.Currency, currencyRef];
                    currencies.Add(new
                    {
                        ProtoRef = $"0x{(ulong)currencyRef:X16}",
                        Name = LeafName(GameDatabase.GetPrototypeName(currencyRef)),
                        Amount = amount,
                        MaxAmount = proto.MaxAmount,
                    });
                }
                return new { Ok = true, Player = p.GetName(), Currencies = currencies };
            });

            await context.SendJsonAsync(result);
        }

        private static string LeafName(string path)
        {
            if (string.IsNullOrEmpty(path)) return path ?? string.Empty;
            int slash = path.LastIndexOf('/');
            string leaf = slash >= 0 ? path[(slash + 1)..] : path;
            const string suffix = ".prototype";
            if (leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                leaf = leaf[..^suffix.Length];
            return leaf;
        }
    }

    public class CurrencySetWebHandler : WebHandler
    {
        protected override async Task Post(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            string body = await context.ReadUtf8StringAsync();

            string playerName = null, currencyRefStr = null;
            long amount = 0;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                if (root.TryGetProperty("playerName", out var pn)) playerName = pn.GetString();
                if (root.TryGetProperty("currencyProtoRef", out var cr)) currencyRefStr = cr.GetString();
                if (root.TryGetProperty("amount", out var am)) amount = am.GetInt64();
            }
            catch (Exception ex)
            {
                await context.SendJsonAsync(new { Ok = false, Error = $"bad request: {ex.Message}" });
                return;
            }

            PrototypeId currencyRef = (PrototypeId)PhantomsWebUtil.ParseRef(currencyRefStr);
            if (currencyRef == PrototypeId.Invalid)
            {
                await context.SendJsonAsync(new { Ok = false, Error = "missing or invalid 'currencyProtoRef'" });
                return;
            }

            Player player = PhantomsWebUtil.FindTargetPlayer(playerName, null, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            long clampedAmount = Math.Max(0, amount);

            object result = await PhantomsWebUtil.RunOnGameThread(player, p =>
            {
                CurrencyPrototype proto = currencyRef.As<CurrencyPrototype>();
                if (proto == null)
                    return new { Ok = false, Error = "not a CurrencyPrototype" };

                long clamped = proto.MaxAmount > 0 ? Math.Min(clampedAmount, proto.MaxAmount) : clampedAmount;
                long previous = p.Properties[PropertyEnum.Currency, currencyRef];
                p.Properties[PropertyEnum.Currency, currencyRef] = clamped;
                return new { Ok = true, PreviousAmount = previous, NewAmount = clamped };
            });

            await context.SendJsonAsync(result);
        }
    }
}
