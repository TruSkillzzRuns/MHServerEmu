using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Memory;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.Games.Missions.Actions
{
    public class MissionActionPlayKismetSeq : MissionAction
    {
        private static readonly Logger Logger = LogManager.CreateLogger();
        private MissionActionPlayKismetSeqPrototype _proto;
        public MissionActionPlayKismetSeq(IMissionActionOwner owner, MissionActionPrototype prototype) : base(owner, prototype)
        {
            // RaftNPEVenomKismetController
            _proto = prototype as MissionActionPlayKismetSeqPrototype;
        }

        public override void Run()
        {
            using var playersHandle = ListPool<Player>.Instance.Get(out List<Player> players);
            if (GetDistributors(_proto.SendTo, players))
            {
                // Phantom Hero fix (2026-07-20): our "phantoms follow into
                // queue/raid content" feature (Teleporter.BeginTeleportToQueueTarget
                // -> SnapshotPhantomsForTransfer) can leave a scripted cutscene
                // controller mission's participant list populated by client-less
                // phantom players while the real player who triggered the content
                // is absent. Raid cutscene controllers (e.g. AgeOfUltronKismetController)
                // are NON-open missions, so IsActiveForMission gates participation
                // purely on trigger-hotspot containment (FilterHotspots) -- and the
                // phantom respawn/positioning on entry can win that hotspot race,
                // so the phantoms become the mission's participants and the real
                // player never does. Result: the kismet cutscene is distributed
                // only to phantoms and the real player never sees it (confirmed
                // live 2026-07-20 -- the Ultron mansion-destruction cutscene). On
                // forks without our queue-follow feature the player enters solo
                // and the cutscene plays fine. QueuePlayKismetSeq is a guaranteed
                // no-op for phantoms (no PlayerConnection), so if any phantom
                // resolved as a recipient, also deliver to every real player
                // currently in the mission's region. Gated on phantom presence so
                // vanilla/solo distribution is byte-for-byte unchanged.
                bool anyPhantomRecipient = false;
                foreach (Player player in players)
                {
                    if (player.PlayerConnection == null)
                    {
                        anyPhantomRecipient = true;
                        break;
                    }
                }

                if (anyPhantomRecipient && Region != null)
                {
                    foreach (Player realPlayer in new PlayerIterator(Region))
                        if (players.Contains(realPlayer) == false)
                            players.Add(realPlayer);
                }

                foreach (Player player in players)
                    player.QueuePlayKismetSeq(_proto.KismetSeqPrototype);
            }

            if (MissionManager.Debug) Logger.Debug($"QueuePlayKismetSeq {Mission.PrototypeName} {_proto.KismetSeqPrototype.GetNameFormatted()}");
        }
    }
}
