// OmegaDev2 Command Console toggle panel.
//
//   GET  /webapi/toggles?player=*   — every on/off command and its state
//   POST /webapi/toggles            — {"name":"autostash","enabled":true}
//
// The list is NOT hardcoded: CommandManager.GetToggleableGroups() reflects
// over the registered command groups and returns those exposing both an "on"
// and an "off" subcommand, so a new toggle command appears in the panel as
// soon as it is written.
//
// Reached through the same reflection bridge ConsoleWebHandler uses, because
// MHServerEmu.WebFrontend is referenced BY MHServerEmu and so cannot take a
// compile-time dependency on the command system.
//
// Setting a toggle runs the real "<group> on|off" command rather than poking
// the property directly, so the command's own side effects and confirmation
// message happen exactly as if it had been typed.

using System.Reflection;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.Games.Entities;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class TogglesWebHandler : WebHandler
    {
        protected override async Task Get(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            string playerParam = PhantomsWebUtil.QueryParam(context, "player");
            Player player = PhantomsWebUtil.FindTargetPlayer(playerParam, null, out _);

            var toggles = new List<object>();

            foreach ((string name, string help) in GetToggleableGroups())
            {
                toggles.Add(new
                {
                    Name = name,
                    Help = help,
                    // null when the state couldn't be resolved — the UI shows
                    // the switch as indeterminate rather than lying about it.
                    Enabled = player != null ? ReadToggleState(player, name) : null,
                });
            }

            await context.SendJsonAsync(new
            {
                Ok = true,
                Player = player?.GetName(),
                Toggles = toggles,
            });
        }

        protected override async Task Post(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            string body = await context.ReadUtf8StringAsync();

            string name = null;
            string playerName = null;
            bool enabled = false;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(body) ? "{}" : body);

                if (doc.RootElement.TryGetProperty("name", out var n)) name = n.GetString();
                if (doc.RootElement.TryGetProperty("enabled", out var e)) enabled = e.GetBoolean();
                if (doc.RootElement.TryGetProperty("playerName", out var p)) playerName = p.GetString();
            }
            catch { /* fall through to the missing-name check */ }

            if (string.IsNullOrWhiteSpace(name))
            {
                await context.SendJsonAsync(new { Ok = false, Error = "missing 'name'" });
                return;
            }

            Player player = PhantomsWebUtil.FindTargetPlayer(playerName, null, out string findError);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = findError ?? "player not found" });
                return;
            }

            // A name may be nested ("phantom rogue"): the first token is the
            // command group and everything after it becomes leading arguments,
            // so "phantom rogue" + on => InvokeCommand("phantom", "rogue on").
            string commandName = name.Trim();
            string leading = "";

            int space = commandName.IndexOf(' ');
            if (space > 0)
            {
                leading = commandName[(space + 1)..].Trim() + " ";
                commandName = commandName[..space];
            }

            string parameters = leading + (enabled ? "on" : "off");

            // On the player's game thread, same as the console endpoint: these
            // commands touch player state the game loop also owns.
            object opResult = await PhantomsWebUtil.RunOnGameThread(player,
                p => (object)(ConsoleRuntime.InvokeCommand(commandName, parameters, p.PlayerConnection) ?? ""));

            await context.SendJsonAsync(new { Ok = true, Result = opResult as string ?? "" });
        }

        /// <summary>
        /// Calls CommandManager.GetToggleableGroups() across the assembly
        /// boundary. Returns empty rather than throwing if the shape changes.
        /// </summary>
        private static IEnumerable<(string Name, string Help)> GetToggleableGroups()
        {
            var mgrType = Type.GetType("MHServerEmu.Commands.CommandManager, MHServerEmu");
            object instance = mgrType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                                     ?.GetValue(null);
            if (instance == null) yield break;

            var method = mgrType.GetMethod("GetToggleableGroups", BindingFlags.Public | BindingFlags.Instance);
            if (method?.Invoke(instance, null) is not System.Collections.IEnumerable results) yield break;

            foreach (object entry in results)
            {
                Type t = entry.GetType();
                string name = t.GetField("Item1")?.GetValue(entry) as string;
                string help = t.GetField("Item2")?.GetValue(entry) as string;
                if (string.IsNullOrEmpty(name)) continue;

                yield return (name, help ?? "");
            }
        }

        /// <summary>
        /// Finds the bool property on Player backing a toggle group.
        ///
        /// Matched by convention instead of a hardcoded table so new toggles
        /// work without touching this file: "autostash" hits AutoStashEnabled
        /// exactly, and the prefix fallback catches names that don't line up
        /// literally (gearalert -> GearUpgradeAlertEnabled). Returns null when
        /// nothing matches, which the UI renders as unknown.
        /// </summary>
        private static bool? ReadToggleState(Player player, string groupName)
        {
            PropertyInfo[] props = typeof(Player).GetProperties(BindingFlags.Public | BindingFlags.Instance);

            // Nested names key off their LAST token: "phantom rogue" describes
            // the rogue toggle, not anything named phantom.
            int lastSpace = groupName.LastIndexOf(' ');
            if (lastSpace >= 0) groupName = groupName[(lastSpace + 1)..];

            string target = groupName.Replace("_", "").ToLowerInvariant() + "enabled";

            foreach (PropertyInfo p in props)
            {
                if (p.PropertyType != typeof(bool)) continue;
                if (p.Name.ToLowerInvariant() == target)
                    return (bool)p.GetValue(player);
            }

            // Prefix fallback: first four characters of the group name.
            string prefix = groupName.Length >= 4 ? groupName[..4].ToLowerInvariant() : groupName.ToLowerInvariant();

            foreach (PropertyInfo p in props)
            {
                if (p.PropertyType != typeof(bool)) continue;

                string lower = p.Name.ToLowerInvariant();
                if (lower.EndsWith("enabled") && lower.StartsWith(prefix))
                    return (bool)p.GetValue(player);
            }

            return null;
        }
    }
}
