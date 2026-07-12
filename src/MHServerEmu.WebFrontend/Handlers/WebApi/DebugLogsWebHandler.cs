// Debug Console WebAPI — matches the schema OmegaDev's DebugConsolePage expects.
//
//   GET /webapi/debug/logs?since={seq}&max={count}&min={level}
//
// Returns:
//   { "latestSeq": long, "entries": [ { seq, ts, level, logger, message } ] }
//
// Parses the newest MHServerEmu.log file line-by-line. Each line's index in
// the file is used as its `seq` — the client polls with the last seq it saw
// and gets only newer entries. `min` filters below the given level using the
// standard NLog ranking (Trace<Debug<Info<Warn<Error).

using System.Text.Json;
using System.Text.RegularExpressions;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network.Web;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class DebugLogsWebHandler : WebHandler
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        // Matches log lines like: [2026.07.08 20:34:56.789] [ Info] [ServerApp] Message
        private static readonly Regex s_lineRegex = new(
            @"^\[(?<ts>[^\]]+)\]\s*\[\s*(?<level>Trace|Debug|Info|Warn|Error|Fatal)\s*\]\s*\[(?<logger>[^\]]+)\]\s*(?<msg>.*)$",
            RegexOptions.Compiled);

        private static readonly Dictionary<string, int> s_levelRank = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Trace", 0 }, { "Debug", 1 }, { "Info", 2 }, { "Warn", 3 }, { "Error", 4 }, { "Fatal", 5 },
        };

        protected override Task Get(WebRequestContext context)
        {
            try
            {
                // Parse query params from the raw path. WebRequestContext doesn't expose
                // Request.QueryString directly, so pull off the ?... suffix by reflection
                // where possible, otherwise fall back to defaults.
                long since = -1;
                int max = 500;
                string minLevel = "Trace";
                foreach (var (k, v) in ExtractQuery(context))
                {
                    if (k.Equals("since", StringComparison.OrdinalIgnoreCase)) long.TryParse(v, out since);
                    else if (k.Equals("max", StringComparison.OrdinalIgnoreCase)) int.TryParse(v, out max);
                    else if (k.Equals("min", StringComparison.OrdinalIgnoreCase)) minLevel = v;
                }
                max = Math.Clamp(max, 1, 2000);
                int minRank = s_levelRank.TryGetValue(minLevel, out var r) ? r : 0;

                string logsDir = Path.Combine(AppContext.BaseDirectory, "Logs");
                if (!Directory.Exists(logsDir))
                    return context.SendJsonAsync(new { latestSeq = -1L, entries = System.Array.Empty<object>() });

                var newest = new DirectoryInfo(logsDir)
                    .EnumerateFiles("*.log", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();

                if (newest == null)
                    return context.SendJsonAsync(new { latestSeq = -1L, entries = System.Array.Empty<object>() });

                var entries = new List<object>();
                long latestSeq = -1;
                long seq = 0;
                using (var fs = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs))
                {
                    string? line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        latestSeq = seq;
                        if (seq > since)
                        {
                            var m = s_lineRegex.Match(line);
                            string level, logger, msg, ts;
                            if (m.Success)
                            {
                                level = m.Groups["level"].Value;
                                ts = m.Groups["ts"].Value;
                                logger = m.Groups["logger"].Value;
                                msg = m.Groups["msg"].Value;
                            }
                            else
                            {
                                level = "Info";
                                ts = "";
                                logger = "raw";
                                msg = line;
                            }
                            int rank = s_levelRank.TryGetValue(level, out var lr) ? lr : 0;
                            if (rank >= minRank)
                            {
                                entries.Add(new { seq, ts, level, logger, message = msg });
                                if (entries.Count >= max) { /* keep reading to update latestSeq */ }
                            }
                        }
                        seq++;
                    }
                }

                // If more entries matched than max, trim to the newest `max`
                if (entries.Count > max) entries = entries.GetRange(entries.Count - max, max);

                return context.SendJsonAsync(new { latestSeq, entries });
            }
            catch (Exception ex)
            {
                return context.SendJsonAsync(new { latestSeq = -1L, entries = System.Array.Empty<object>(), error = ex.Message });
            }
        }

        private static IEnumerable<(string Key, string Value)> ExtractQuery(WebRequestContext context)
        {
            var qs = System.Web.HttpUtility.ParseQueryString(context.QueryString ?? string.Empty);
            foreach (string key in qs.AllKeys)
            {
                if (key == null) continue;
                yield return (key, qs[key]);
            }
        }
    }
}
