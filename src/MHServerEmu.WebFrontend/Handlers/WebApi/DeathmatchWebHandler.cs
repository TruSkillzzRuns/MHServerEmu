// Deathmatch WebAPI — drives the OmegaDev2 testing panel.
//
//   GET  /webapi/deathmatch/arenas  -> the map list plus the size limits
//   POST /webapi/deathmatch/start    body: {playerName, mode:"tdm"|"1v1", teamSize, killTarget, regionPath}
//   POST /webapi/deathmatch/stop     body: {playerName}
//
// Exists so match settings can be changed without a server rebuild between
// runs. The in-game entry point stays the Cloak NPC; this is tooling.

using System.Text.Json;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.Games.Entities;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class DeathmatchArenasWebHandler : WebHandler
    {
        protected override async Task Get(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            var arenas = new List<object>();
            foreach (var (name, path) in Player.DeathmatchArenas)
                arenas.Add(new { name, path });

            await context.SendJsonAsync(new
            {
                Ok = true,
                Arenas = arenas,
                TeamCountMax = Player.DeathmatchTeamCountMax,
                TeamSizeDefault = Player.DeathmatchTeamSizeDefault,
                TeamSizeMax = Player.DeathmatchTeamSizeMax,
                KillTargetDefault = Player.DeathmatchTeamKillTarget,
            });
        }
    }

    public class DeathmatchStartWebHandler : WebHandler
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        protected override async Task Post(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            string body = await context.ReadUtf8StringAsync();
            PhantomsWebUtil.ParseTarget(body, out string playerName, out string playerDbId);

            string mode = "tdm";
            string regionPath = null;
            int teamSize = 0;
            int killTarget = 0;
            int teamCount = 0;
            List<List<ulong>> rosters = null;

            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                if (root.TryGetProperty("mode", out var m)) mode = m.GetString() ?? "tdm";
                if (root.TryGetProperty("regionPath", out var r)) regionPath = r.GetString();
                if (root.TryGetProperty("teamSize", out var ts) && ts.TryGetInt32(out int tsv)) teamSize = tsv;
                if (root.TryGetProperty("teamCount", out var tc) && tc.TryGetInt32(out int tcv)) teamCount = tcv;
                if (root.TryGetProperty("killTarget", out var kt) && kt.TryGetInt32(out int ktv)) killTarget = ktv;

                // teamRosters: [[heroRef, ...], [...], [...]] — one list per team,
                // each a hand-picked avatar prototype ref. Short/absent lists just
                // mean "fill the rest randomly".
                if (root.TryGetProperty("teamRosters", out var tr) && tr.ValueKind == JsonValueKind.Array)
                {
                    rosters = new List<List<ulong>>();
                    foreach (var teamEl in tr.EnumerateArray())
                    {
                        var list = new List<ulong>();
                        if (teamEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var heroEl in teamEl.EnumerateArray())
                            {
                                if (heroEl.ValueKind == JsonValueKind.Number && heroEl.TryGetUInt64(out ulong hn)) list.Add(hn);
                                else if (heroEl.ValueKind == JsonValueKind.String)
                                {
                                    string hs = heroEl.GetString();
                                    if (string.IsNullOrWhiteSpace(hs)) continue;
                                    if (hs.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (ulong.TryParse(hs.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out ulong hx)) list.Add(hx);
                                    }
                                    else if (ulong.TryParse(hs, out ulong hd)) list.Add(hd);
                                }
                            }
                        }
                        rosters.Add(list);
                    }
                }
            }
            catch { /* defaults below are all valid */ }

            Player player = PhantomsWebUtil.FindTargetPlayer(playerName, playerDbId, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            bool isTeams = string.Equals(mode, "tdm", StringComparison.OrdinalIgnoreCase);

            object result = await PhantomsWebUtil.RunOnGameThread(player, p =>
            {
                string message = isTeams
                    ? p.StartDeathmatchTeams(killTarget, teamSize, regionPath, rosters, teamCount)
                    : p.StartDeathmatch1v1(killTarget);
                return new { Ok = true, Message = message };
            });

            Logger.Info($"[Deathmatch] web start: mode={mode} teams={teamCount} teamSize={teamSize} killTarget={killTarget} region={regionPath ?? "(default)"}");
            await context.SendJsonAsync(result);
        }
    }

    public class DeathmatchStopWebHandler : WebHandler
    {
        protected override async Task Post(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            string body = await context.ReadUtf8StringAsync();
            PhantomsWebUtil.ParseTarget(body, out string playerName, out string playerDbId);

            Player player = PhantomsWebUtil.FindTargetPlayer(playerName, playerDbId, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player,
                p => new { Ok = true, Message = p.EndDeathmatch("stopped from OmegaDev2", returnHome: true) });

            await context.SendJsonAsync(result);
        }
    }
}
