// OmegaDev2 (or any REST caller) uses this entry point to teleport the player
// via the /webapi/regions/teleport endpoint. Design matches Avatar.PhantomHero's
// SpawnPhantomHeroesFromWeb — schedule a zero-delay game-thread event so the
// off-thread WebAPI caller never touches thread-static Game state directly.

using MHServerEmu.Core.Logging;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.Events.Templates;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Regions;

namespace MHServerEmu.Games.Entities.Avatars
{
    public partial class Avatar
    {
        private static readonly Logger WebTeleportLogger = LogManager.CreateLogger();

        private readonly EventPointer<WebTeleportEvent> _webTeleportEvent = new();

        /// <summary>
        /// Called from an off-thread WebAPI handler. Schedules the actual
        /// teleport as a zero-delay game-thread event, then returns immediately.
        /// </summary>
        public void TeleportToRegionFromWeb(ulong regionProtoRefId)
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler == null)
            {
                WebTeleportLogger.Warn("[WebTeleport] no scheduler on this Avatar's Game");
                return;
            }
            if (_webTeleportEvent.IsValid) scheduler.CancelEvent(_webTeleportEvent);
            scheduler.ScheduleEvent(_webTeleportEvent, System.TimeSpan.Zero, _webEvents);
            _webTeleportEvent.Get().Initialize(this, regionProtoRefId);
        }

        private void OnWebTeleportTick(ulong regionProtoRefId)
        {
            PrototypeId regionRef = (PrototypeId)regionProtoRefId;
            RegionPrototype regionProto = regionRef.As<RegionPrototype>();
            if (regionProto == null)
            {
                WebTeleportLogger.Warn($"[WebTeleport] region prototype not found for ref {regionProtoRefId}");
                return;
            }

            var player = GetOwnerOfType<Player>();
            if (player == null)
            {
                WebTeleportLogger.Warn("[WebTeleport] avatar has no player owner");
                return;
            }

            Teleporter.DebugTeleportToTarget(player, regionProto.StartTarget);
            WebTeleportLogger.Info($"[WebTeleport] {player} → {GameDatabase.GetPrototypeName(regionRef)}");
        }

        private class WebTeleportEvent : CallMethodEventParam1<Avatar, ulong>
        {
            protected override CallbackDelegate GetCallback() => (avatar, regionRefId) => avatar.OnWebTeleportTick(regionRefId);
        }
    }
}
