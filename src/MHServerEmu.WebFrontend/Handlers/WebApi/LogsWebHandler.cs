// Log Tail WebAPI — installed for OmegaDev2's Log Tail tool.
//
//   GET /webapi/logs/tail?lines=200
//
// Returns the last N lines from the newest MHServerEmu.log file in the
// Logs/ folder next to the running exe. Lightweight — no long-lived
// connection or SSE, the client polls every 2s.

using System.Text.Json;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network.Web;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class LogsTailWebHandler : WebHandler
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        protected override async Task Get(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            var qs = System.Web.HttpUtility.ParseQueryString(context.QueryString ?? string.Empty);
            int lines = int.TryParse(qs["lines"], out int requested)
                ? System.Math.Clamp(requested, 50, 5000)
                : 200;

            try
            {
                string logsDir = Path.Combine(AppContext.BaseDirectory, "Logs");
                if (!Directory.Exists(logsDir))
                {
                    await context.SendJsonAsync(new { ok = false, error = $"Logs directory not found at {logsDir}" });
                    return;
                }

                var newest = new DirectoryInfo(logsDir)
                    .EnumerateFiles("*.log", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();

                if (newest == null)
                {
                    await context.SendJsonAsync(new { ok = true, path = logsDir, count = 0, lines = System.Array.Empty<string>() });
                    return;
                }

                // Read shared-write so we don't lock the live logger out.
                var last = ReadLastLines(newest.FullName, lines);

                await context.SendJsonAsync(new
                {
                    ok = true,
                    file = newest.Name,
                    fullPath = newest.FullName,
                    count = last.Count,
                    lines = last,
                });
            }
            catch (System.Exception ex)
            {
                await context.SendJsonAsync(new { ok = false, error = $"{ex.GetType().Name}: {ex.Message}" });
            }
        }

        private static List<string> ReadLastLines(string path, int maxLines)
        {
            var buf = new List<string>(maxLines);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                buf.Add(line);
                if (buf.Count > maxLines) buf.RemoveAt(0);
            }
            return buf;
        }
    }
}
