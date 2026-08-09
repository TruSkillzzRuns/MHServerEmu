using MHServerEmu.Commands.Attributes;
using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Network;

namespace MHServerEmu.Commands.Implementations
{
    // Debug entry point for the deathmatch vertical slice. The real entry is an
    // NPC in Avengers Tower (Cloak) going through the existing matchmaking queue;
    // this exists so the mode can be tested before any of that is built.
    [CommandGroup("dm")]
    [CommandGroupDescription("Deathmatch (testing). 1v1 against a phantom opponent on one fixed map.")]
    [CommandGroupUserLevel(AccountUserLevel.Admin)]
    public class DeathmatchCommands : CommandGroup
    {
        [Command("tdm")]
        [CommandDescription("Start a 5v5 Team Deathmatch. Optional: kill target, then a map name/alias (matched against the Teams pool) or a full region prototype path. e.g. !dm tdm 20 cannery — random map if omitted or \"random\".")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string Teams(string[] @params, NetClient client)
        {
            Player player = (client as PlayerConnection)?.Player;
            if (player == null) return "No player.";

            int target = Player.DeathmatchTeamKillTarget;
            string mapArg = null;
            if (@params != null && @params.Length > 0)
            {
                if (int.TryParse(@params[0], out int parsed) && parsed > 0)
                {
                    target = parsed;
                    if (@params.Length > 1) mapArg = @params[1];
                }
                else
                {
                    mapArg = @params[0];
                }
            }

            string regionPath = null;
            if (string.IsNullOrWhiteSpace(mapArg) == false && mapArg.Equals("random", System.StringComparison.OrdinalIgnoreCase) == false)
            {
                regionPath = player.ResolveDeathmatchArenaAlias(mapArg, solos: false, out string error);
                if (regionPath == null) return error;
            }

            return player.StartDeathmatchTeams(target, teamSize: 5, regionPath: regionPath, teamCount: 2);
        }

        [Command("solo")]
        [CommandDescription("Start a 1v1v1 Solos Deathmatch. Optional: kill target, then a map name/alias (matched against the Solos pool) or a full region prototype path. e.g. !dm solo 10 sewer — random map if omitted or \"random\".")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string Solo(string[] @params, NetClient client)
        {
            Player player = (client as PlayerConnection)?.Player;
            if (player == null) return "No player.";

            int target = 5;
            string mapArg = null;
            if (@params != null && @params.Length > 0)
            {
                if (int.TryParse(@params[0], out int parsed) && parsed > 0)
                {
                    target = parsed;
                    if (@params.Length > 1) mapArg = @params[1];
                }
                else
                {
                    mapArg = @params[0];
                }
            }

            string regionPath = null;
            if (string.IsNullOrWhiteSpace(mapArg) == false && mapArg.Equals("random", System.StringComparison.OrdinalIgnoreCase) == false)
            {
                regionPath = player.ResolveDeathmatchArenaAlias(mapArg, solos: true, out string error);
                if (regionPath == null) return error;
            }

            return player.StartDeathmatchTeams(target, teamSize: 1, regionPath: regionPath, teamCount: 3);
        }

        [Command("maps")]
        [CommandDescription("List usable map names for a bracket. e.g. !dm maps teams / !dm maps solo")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string Maps(string[] @params, NetClient client)
        {
            Player player = (client as PlayerConnection)?.Player;
            if (player == null) return "No player.";

            bool solos = @params != null && @params.Length > 0
                && (@params[0].Equals("solo", System.StringComparison.OrdinalIgnoreCase)
                 || @params[0].Equals("solos", System.StringComparison.OrdinalIgnoreCase));

            var names = player.ListDeathmatchArenas(solos);
            return $"{(solos ? "Solos" : "Teams")} maps ({names.Count}): {string.Join(", ", names)}";
        }

        [Command("quit")]
        [CommandDescription("Abandon the current deathmatch and return to Avengers Tower.")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string Quit(string[] @params, NetClient client)
        {
            Player player = (client as PlayerConnection)?.Player;
            if (player == null) return "No player.";

            return player.EndDeathmatch("abandoned", returnHome: true);
        }
    }
}
