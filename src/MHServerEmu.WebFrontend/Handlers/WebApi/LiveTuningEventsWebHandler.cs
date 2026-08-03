// OmegaDev2 Live Events tool — force on/off any of MHO's real live-tuning
// events (OdinsBounty, WinterHoliday, etc.) independently of each other and
// of the calendar-driven schedule. Pure wrapper over LiveTuningEventOverrideWriter
// — no reflection, no runtime prototype mutation.
//
//   GET  /webapi/livetuning/events            — { ok, knownEvents, activeToday, overrideActive }
//   POST /webapi/livetuning/events/activate   — { eventName } -> force it on (in addition to any others already forced on)
//   POST /webapi/livetuning/events/deactivate — { eventName } -> turn it back off, leaving other forced events alone
//   POST /webapi/livetuning/events/clear      — clears every override, restores the normal calendar schedule

using System.Text.Json;
using MHServerEmu.Core.Config;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.LiveTuning;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class LiveTuningEventsListWebHandler : WebHandler
    {
        protected override async Task Get(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            GameDataConfig config = ConfigManager.Instance.GetConfig<GameDataConfig>();
            if (config.EnableLiveTuningEvents == false)
            {
                await context.SendJsonAsync(new
                {
                    Ok = false,
                    Error = "EnableLiveTuningEvents is false in Config.ini — live tuning events are disabled server-wide",
                });
                return;
            }

            await context.SendJsonAsync(new
            {
                Ok = true,
                KnownEvents = LiveTuningEventOverrideWriter.ListKnownEvents().OrderBy(n => n).ToArray(),
                ActiveToday = LiveTuningEventOverrideWriter.ListActiveEventsToday(),
                OverrideActive = LiveTuningEventOverrideWriter.IsOverrideActive(),
            });
        }
    }

    public class LiveTuningEventActivateWebHandler : WebHandler
    {
        protected override async Task Post(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            string body = await context.ReadUtf8StringAsync();
            string eventName = null;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (doc.RootElement.TryGetProperty("eventName", out var e)) eventName = e.GetString();
            }
            catch (Exception ex)
            {
                await context.SendJsonAsync(new { Ok = false, Error = $"bad request: {ex.Message}" });
                return;
            }

            if (LiveTuningEventOverrideWriter.ActivateEvent(eventName, out string error) == false)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error });
                return;
            }

            await context.SendJsonAsync(new
            {
                Ok = true,
                Message = $"'{eventName}' forced on",
                ActiveToday = LiveTuningEventOverrideWriter.ListActiveEventsToday(),
            });
        }
    }

    public class LiveTuningEventDeactivateWebHandler : WebHandler
    {
        protected override async Task Post(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            string body = await context.ReadUtf8StringAsync();
            string eventName = null;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (doc.RootElement.TryGetProperty("eventName", out var e)) eventName = e.GetString();
            }
            catch (Exception ex)
            {
                await context.SendJsonAsync(new { Ok = false, Error = $"bad request: {ex.Message}" });
                return;
            }

            if (LiveTuningEventOverrideWriter.DeactivateEvent(eventName, out string error) == false)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error });
                return;
            }

            await context.SendJsonAsync(new
            {
                Ok = true,
                Message = $"'{eventName}' turned off",
                ActiveToday = LiveTuningEventOverrideWriter.ListActiveEventsToday(),
            });
        }
    }

    public class LiveTuningEventClearWebHandler : WebHandler
    {
        protected override async Task Post(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            if (LiveTuningEventOverrideWriter.ClearOverride(out string error) == false)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error });
                return;
            }

            await context.SendJsonAsync(new
            {
                Ok = true,
                Message = "override cleared, normal schedule restored",
                ActiveToday = LiveTuningEventOverrideWriter.ListActiveEventsToday(),
            });
        }
    }
}
