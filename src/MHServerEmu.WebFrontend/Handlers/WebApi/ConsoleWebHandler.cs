// Debug Console WebAPI — installed for OmegaDev2's Debug Console tool.
//
//   POST /webapi/console/exec   body: {"command": "region warp AvengersTower"}
//
// Runs the given text through CommandManager.InvokeCommand (the same
// dispatcher chat commands use). Returns the raw string result the command
// handler produced so the tool can display it verbatim.
//
// Commands prefixed with '!' or unprefixed both work — the '!' is stripped
// so users can paste chat-style or type bare.
//
// Reflection is used because CommandManager.InvokeCommand is private and the
// vanilla WebFrontend project has no reference to MHServerEmu (the host exe
// project). This keeps project graph edits at zero.

using System.Reflection;
using System.Text.Json;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network.Web;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class ConsoleExecWebHandler : WebHandler
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        protected override async Task Post(WebRequestContext context)
        {
            string body = await context.ReadUtf8StringAsync();
            string? cmdLine = null;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (doc.RootElement.TryGetProperty("command", out var c)) cmdLine = c.GetString();
            }
            catch { /* fall through */ }

            if (string.IsNullOrWhiteSpace(cmdLine))
            {
                await context.SendJsonAsync(new { ok = false, error = "missing 'command'" });
                return;
            }

            cmdLine = cmdLine.Trim();
            if (cmdLine.StartsWith('!')) cmdLine = cmdLine[1..];

            int spaceIdx = cmdLine.IndexOf(' ');
            string command = spaceIdx > 0 ? cmdLine[..spaceIdx] : cmdLine;
            string parameters = spaceIdx > 0 ? cmdLine[(spaceIdx + 1)..] : "";

            string result;
            try
            {
                result = ConsoleRuntime.InvokeCommand(command, parameters);
            }
            catch (System.Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                await context.SendJsonAsync(new { ok = false, error = $"{inner.GetType().Name}: {inner.Message}" });
                return;
            }

            Logger.Info($"[Console:Web] {cmdLine}");
            await context.SendJsonAsync(new { ok = true, command = cmdLine, output = result ?? "" });
        }
    }

    internal static class ConsoleRuntime
    {
        public static string InvokeCommand(string command, string parameters)
        {
            // Locate the singleton MHServerEmu.Commands.CommandManager instance.
            var mgrType = System.Type.GetType("MHServerEmu.Commands.CommandManager, MHServerEmu");
            if (mgrType == null) return "CommandManager type not reachable";

            var instance = mgrType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (instance == null) return "CommandManager.Instance is null";

            var invoke = mgrType.GetMethod("InvokeCommand", BindingFlags.NonPublic | BindingFlags.Instance);
            if (invoke == null) return "CommandManager.InvokeCommand not found";

            // Third parameter is NetClient — pass null to run in "server console" mode.
            // Commands requiring a client will report the requirement in their output.
            object? result = invoke.Invoke(instance, new object?[] { command, parameters, null });
            return result as string ?? "";
        }
    }
}
