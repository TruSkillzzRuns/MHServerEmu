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

        /// <summary>True if an override file is currently in effect (one or more events forced on).</summary>
        public static bool IsOverrideActive() => File.Exists(EventScheduleOverrideFilePath);

        private static List<LiveTuningEventRule> ReadOverrideRules()
        {
            if (!File.Exists(EventScheduleOverrideFilePath))
                return new();

            try
            {
                LiveTuningEventRule[] rules = JsonSerializer.Deserialize<LiveTuningEventRule[]>(
                    File.ReadAllText(EventScheduleOverrideFilePath), LiveTuningEvent.JsonOptions.Default);
                return rules?.ToList() ?? new();
            }
            catch (Exception ex)
            {
                Logger.Warn($"ReadOverrideRules(): failed to read override file: {ex.Message}");
                return new();
            }
        }

        /// <summary>
        /// The rule set to mutate for an activate/deactivate call. If an override is already in
        /// effect, that's just its rules. Otherwise (still on the calendar-driven schedule) this
        /// SEEDS an explicit rule set from whatever's naturally active today, one AlwaysOn rule per
        /// event, so the very first toggle click deterministically takes over from the calendar
        /// instead of being a no-op against an override that doesn't exist yet. This is what makes
        /// "Turn Off" actually suppress an event that's currently active only because a WeeklyRotation/
        /// SpecialDate calendar rule says so today, not because it was ever force-activated.
        /// </summary>
        private static List<LiveTuningEventRule> GetEffectiveRules()
        {
            if (File.Exists(EventScheduleOverrideFilePath))
                return ReadOverrideRules();

            return ListActiveEventsToday()
                .Select(name => new LiveTuningEventRule
                {
                    Name = $"OmegaDev2Force_{name}",
                    IsEnabled = true,
                    Type = LiveTuningEventRuleType.AlwaysOn,
                    Events = new[] { name },
                })
                .ToList();
        }

        /// <summary>
        /// Forces the given event on unconditionally (an AlwaysOn rule), adding it to whatever other
        /// events are currently active (seeding from the calendar schedule on the first call — see
        /// <see cref="GetEffectiveRules"/>). Writes the override schedule file and triggers a full
        /// live-tuning reload.
        /// </summary>
        public static bool ActivateEvent(string eventName, out string error)
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

            List<LiveTuningEventRule> rules = GetEffectiveRules();
            if (rules.Any(r => string.Equals(r.Events?.FirstOrDefault(), eventName, StringComparison.OrdinalIgnoreCase)) == false)
            {
                rules.Add(new LiveTuningEventRule
                {
                    Name = $"OmegaDev2Force_{eventName}",
                    IsEnabled = true,
                    Type = LiveTuningEventRuleType.AlwaysOn,
                    Events = new[] { eventName },
                });
            }

            if (!WriteOverrideRules(rules, out error))
                return false;

            LiveTuningEventScheduler.Instance.RefreshEventIndex();
            LiveTuningManager.Instance.LoadLiveTuningData(true);
            Logger.Info($"[LiveTuningEvents] force-activated '{eventName}' via override");
            return true;
        }

        /// <summary>
        /// Turns off a single event, leaving every other currently-active event untouched — whether
        /// it was force-activated or just naturally active via the calendar (see
        /// <see cref="GetEffectiveRules"/>, which seeds from the calendar on the first call). Unlike
        /// the old behavior, an empty resulting rule set is still WRITTEN (not deleted), so "turn
        /// everything off" means zero active events, not a silent fallback to the calendar schedule.
        /// Only <see cref="ClearOverride"/> restores the calendar-driven schedule.
        /// </summary>
        public static bool DeactivateEvent(string eventName, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(eventName))
            {
                error = "eventName is required";
                return false;
            }

            List<LiveTuningEventRule> rules = GetEffectiveRules();
            rules.RemoveAll(r => string.Equals(r.Events?.FirstOrDefault(), eventName, StringComparison.OrdinalIgnoreCase));

            if (!WriteOverrideRules(rules, out error))
                return false;

            LiveTuningEventScheduler.Instance.RefreshEventIndex();
            LiveTuningManager.Instance.LoadLiveTuningData(true);
            Logger.Info($"[LiveTuningEvents] force-deactivated '{eventName}' via override");
            return true;
        }

        private static bool WriteOverrideRules(List<LiveTuningEventRule> rules, out string error)
        {
            error = null;
            try
            {
                string json = JsonSerializer.Serialize(rules, LiveTuningEvent.JsonOptions.Default);
                Directory.CreateDirectory(LiveTuningManager.LiveTuningDataDirectory);
                File.WriteAllText(EventScheduleOverrideFilePath, json);
                return true;
            }
            catch (Exception ex)
            {
                error = $"failed to write override file: {ex.Message}";
                return false;
            }
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

            LiveTuningEventScheduler.Instance.RefreshEventIndex();
            LiveTuningManager.Instance.LoadLiveTuningData(true);
            Logger.Info("[LiveTuningEvents] cleared override, restored calendar-driven schedule");
            return true;
        }
    }
}
