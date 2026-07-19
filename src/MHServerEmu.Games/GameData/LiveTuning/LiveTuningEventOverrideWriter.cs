using System.Text.Json;
using MHServerEmu.Core.Logging;

namespace MHServerEmu.Games.GameData.LiveTuning
{
    /// <summary>
    /// OmegaDev2 "Live Events" tool support — lets an operator force-activate one of MHO's real
    /// live-tuning events (the same OdinsBounty/WinterHoliday/etc. system that ships with the
    /// client, driven by Events.json + EventSchedule.json) on demand, instead of waiting for the
    /// calendar-driven schedule. Writes a single-rule EventScheduleOverride.json (already a
    /// supported override path — see LiveTuningEventScheduler.GetBaseFileOrOverride) and reloads
    /// via the existing LiveTuningManager.LoadLiveTuningData entrypoint. No new subsystem, no
    /// reflection, no runtime prototype mutation — purely additive against already-working code.
    /// </summary>
    public static class LiveTuningEventOverrideWriter
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private static readonly string EventScheduleOverrideFilePath =
            Path.Combine(LiveTuningManager.LiveTuningDataDirectory, "EventScheduleOverride.json");

        /// <summary>Every event name the scheduler knows about, regardless of whether it's active today.</summary>
        public static IEnumerable<string> ListKnownEvents() => LiveTuningEventScheduler.Instance.GetKnownEventNames();

        /// <summary>Event names actually active right now under the current schedule/override.</summary>
        public static List<string> ListActiveEventsToday() => LiveTuningEventScheduler.Instance.GetActiveEventNamesForToday();

        /// <summary>True if an override file is currently in effect, and which event it forces on (if any).</summary>
        public static (bool active, string eventName) GetOverrideStatus()
        {
            if (!File.Exists(EventScheduleOverrideFilePath))
                return (false, null);

            try
            {
                LiveTuningEventRule[] rules = JsonSerializer.Deserialize<LiveTuningEventRule[]>(
                    File.ReadAllText(EventScheduleOverrideFilePath), LiveTuningEvent.JsonOptions.Default);
                string name = rules is { Length: > 0 } ? rules[0].Events?.FirstOrDefault() : null;
                return (name != null, name);
            }
            catch (Exception ex)
            {
                Logger.Warn($"GetOverrideStatus(): failed to read override file: {ex.Message}");
                return (false, null);
            }
        }

        /// <summary>
        /// Forces exactly one event on unconditionally (an AlwaysOn rule) by writing the override
        /// schedule file and triggering a full live-tuning reload. Overwrites any previous override.
        /// </summary>
        public static bool ForceActivateEvent(string eventName, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(eventName))
            {
                error = "eventName is required";
                return false;
            }

            if (!ListKnownEvents().Any(n => string.Equals(n, eventName, StringComparison.OrdinalIgnoreCase)))
            {
                error = $"unknown event '{eventName}' — not present in Events.json/EventsOverride.json";
                return false;
            }

            var rule = new LiveTuningEventRule
            {
                Name = $"OmegaDev2ForceActivate_{eventName}",
                IsEnabled = true,
                Type = LiveTuningEventRuleType.AlwaysOn,
                Events = new[] { eventName },
            };

            try
            {
                string json = JsonSerializer.Serialize(new[] { rule }, LiveTuningEvent.JsonOptions.Default);
                Directory.CreateDirectory(LiveTuningManager.LiveTuningDataDirectory);
                File.WriteAllText(EventScheduleOverrideFilePath, json);
            }
            catch (Exception ex)
            {
                error = $"failed to write override file: {ex.Message}";
                return false;
            }

            LiveTuningManager.Instance.LoadLiveTuningData(true);
            Logger.Info($"[LiveTuningEvents] force-activated '{eventName}' via override");
            return true;
        }

        /// <summary>Removes the override file (if present) and reloads, restoring the normal calendar-driven schedule.</summary>
        public static bool ClearOverride(out string error)
        {
            error = null;
            try
            {
                if (File.Exists(EventScheduleOverrideFilePath))
                    File.Delete(EventScheduleOverrideFilePath);
            }
            catch (Exception ex)
            {
                error = $"failed to delete override file: {ex.Message}";
                return false;
            }

            LiveTuningManager.Instance.LoadLiveTuningData(true);
            Logger.Info("[LiveTuningEvents] cleared override, restored calendar-driven schedule");
            return true;
        }
    }
}
