using MHServerEmu.Commands.Attributes;
using MHServerEmu.Core.Network;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Network;

namespace MHServerEmu.Commands.Implementations
{
    /// <summary>
    /// Player-facing toggle for the Gear Upgrade Alert (see
    /// Player.GearUpgradeAlert.cs). Off by default; persists across logout.
    ///
    /// Commands push an in-game banner as well as returning a string, so they
    /// confirm on screen when invoked from the OmegaDev2 web console (that
    /// path returns the result to the web caller, not to the game).
    /// </summary>
    [CommandGroup("gearalert")]
    [CommandGroupDescription("Flag drops that look better than what you have equipped.")]
    public class GearAlertCommands : CommandGroup
    {
        [DefaultCommand]
        [CommandDescription("Shows whether gear alerts are on, and how many fired this session.")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string Status(string[] @params, NetClient client)
        {
            Player player = ((PlayerConnection)client).Player;
            if (player == null) return "Player not found.";

            string msg = player.GearUpgradeAlertEnabled
                ? $"Gear alerts: ON | fired this session: {player.GearAlertSessionCount}"
                : "Gear alerts: OFF. Use 'gearalert on' to enable.";

            player.SendAutoStashBanner(msg);
            return msg;
        }

        [Command("on")]
        [CommandDescription("Announce drops that beat your equipped item in the same slot.")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string On(string[] @params, NetClient client)
        {
            Player player = ((PlayerConnection)client).Player;
            if (player == null) return "Player not found.";

            player.GearUpgradeAlertEnabled = true;

            string msg = "🟢 Gear alerts ON — drops that beat your equipped item will be called out. "
                       + "Ranking uses rarity and item level only, so treat it as 'worth a look', not a verdict.";
            player.SendAutoStashBanner(msg);
            return msg;
        }

        [Command("off")]
        [CommandDescription("Stop announcing gear upgrades.")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string Off(string[] @params, NetClient client)
        {
            Player player = ((PlayerConnection)client).Player;
            if (player == null) return "Player not found.";

            player.GearUpgradeAlertEnabled = false;

            string msg = "Gear alerts OFF.";
            player.SendAutoStashBanner(msg);
            return msg;
        }
    }
}
