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
        [CommandDescription("Start a 3-duo Team Deathmatch (you + a phantom vs two rival duos). Optional shared kill target, e.g. !dm tdm 20")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string Teams(string[] @params, NetClient client)
        {
            Player player = (client as PlayerConnection)?.Player;
            if (player == null) return "No player.";

            int target = Player.DeathmatchTeamKillTarget;
            if (@params != null && @params.Length > 0 && int.TryParse(@params[0], out int parsed) && parsed > 0)
                target = parsed;

            return player.StartDeathmatchTeams(target);
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
