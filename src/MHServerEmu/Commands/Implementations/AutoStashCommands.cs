using System.Text;
using MHServerEmu.Commands.Attributes;
using MHServerEmu.Core.Network;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Calligraphy;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Network;

namespace MHServerEmu.Commands.Implementations
{
    /// <summary>
    /// Player-facing toggle for Auto-Stash (see Player.AutoStash.cs).
    /// Off by default so it never surprises anyone; the setting persists
    /// across logout via the phantom persist sidecar.
    ///
    /// Every command also pushes an in-game banner rather than relying on the
    /// returned string: when invoked from the OmegaDev2 web console the return
    /// value goes back to the web caller, so the player would otherwise see
    /// nothing on screen.
    /// </summary>
    [CommandGroup("autostash")]
    [CommandGroupDescription("Send crafting materials straight to your stash instead of your bag.")]
    public class AutoStashCommands : CommandGroup
    {
        [DefaultCommand]
        [CommandDescription("Shows auto-stash state, stash room, and what it has moved this session.")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string Status(string[] @params, NetClient client)
        {
            Player player = ((PlayerConnection)client).Player;
            if (player == null) return "Player not found.";

            string status = player.GetAutoStashStatus();
            player.SendAutoStashBanner(status);
            return status;
        }

        [Command("on")]
        [CommandDescription("Route crafting materials to your stash on pickup.")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string On(string[] @params, NetClient client)
        {
            Player player = ((PlayerConnection)client).Player;
            if (player == null) return "Player not found.";

            player.AutoStashEnabled = true;

            string msg = "📦 Auto-stash ON — crafting materials will go to your stash.";
            player.SendAutoStashBanner(msg);
            player.SendAutoStashBanner(player.GetAutoStashStatus());
            return msg;
        }

        [Command("off")]
        [CommandDescription("Send crafting materials to your bag again.")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string Off(string[] @params, NetClient client)
        {
            Player player = ((PlayerConnection)client).Player;
            if (player == null) return "Player not found.";

            player.AutoStashEnabled = false;

            string msg = "📦 Auto-stash OFF — crafting materials will go to your bag.";
            player.SendAutoStashBanner(msg);
            return msg;
        }

        /// <summary>
        /// Data-only check of the classifier — needs no logged-in player, so it
        /// can confirm on every game version that the rule will actually match
        /// crafting materials. Answers the question "is it broken, or did
        /// nothing droppable match?" before anyone goes farming.
        /// </summary>
        [Command("check")]
        [CommandDescription("Verify the crafting-material classifier against this version's data (no player needed).")]
        public string Check(string[] @params, NetClient client)
        {
            PrototypeId ingredientRef = GameDatabase.GetPrototypeRefByName(
                "Entity/Items/Crafting/Ingredients/CraftingIngredient.defaults");

            if (ingredientRef == PrototypeId.Invalid)
                return "FAIL: CraftingIngredient.defaults not found on this game version — auto-stash can never match.";

            int items = 0;
            int matched = 0;
            StringBuilder samples = new();

            foreach (PrototypeId protoRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<ItemPrototype>(
                         PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                items++;

                if (GameDatabase.DataDirectory.PrototypeIsAPrototype(protoRef, ingredientRef) == false)
                    continue;

                matched++;
                if (matched <= 5)
                    samples.Append($"\n    {GameDatabase.GetPrototypeName(protoRef)}");
            }

            if (matched == 0)
                return $"FAIL: prototype resolved, but 0 of {items} items classify as crafting materials — the rule would never fire.";

            return $"OK: {matched} of {items} item prototypes classify as crafting materials.{samples}";
        }
    }
}
