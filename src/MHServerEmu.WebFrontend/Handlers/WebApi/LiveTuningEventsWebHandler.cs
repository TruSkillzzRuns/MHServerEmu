// OmegaDev2 Live Events tool — force-activate one of MHO's real live-tuning
// events (OdinsBounty, WinterHoliday, etc.) on demand instead of waiting for
// the calendar-driven schedule. Pure wrapper over LiveTuningEventOverrideWriter
// — no reflection, no runtime prototype mutation.
//
//   GET  /webapi/livetuning/events           — { ok, knownEvents, activeToday, overrideActive, overrideEventName }
//   POST /webapi/livetuning/events/activate  — { eventName } -> force it on
//   POST /webapi/livetuning/events/clear     — clears any override, restores the normal schedule

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

            var (overrideActive, overrideEventName) = LiveTuningEventOverrideWriter.GetOverrideStatus();
            await context.SendJsonAsync(new
            {
                Ok = true,
                KnownEvents = LiveTuningEventOverrideWriter.ListKnownEvents().OrderBy(n => n).ToArray(),
                ActiveToday = LiveTuningEventOverrideWriter.ListActiveEventsToday(),
                OverrideActive = overrideActive,
                OverrideEventName = overrideEventName,
            });
        }
    }

    public class LiveTuningEventActivateWebHandler : WebHandler
    {
        protected override async Task Post(WebRequestContext context)
        {
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

            if (LiveTuningEventOverrideWriter.ForceActivateEvent(eventName, out string error) == false)
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

    public class LiveTuningEventClearWebHandler : WebHandler
    {
        protected override async Task Post(WebRequestContext context)
        {
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
