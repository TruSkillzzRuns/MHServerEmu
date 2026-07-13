using System;
using MHServerEmu.Core.Logging;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.Events.Templates;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.Games.Entities
{
    // Rogue Encounter — spontaneous ambushes by hostile phantom heroes while
    // the player is out in the world. Opt-in per player (default off). Each
    // encounter drops 1-3 hostile heroes near the player with a warning
    // shout in chat, then respects a cooldown so they aren't stacked.
    //
    // NEVER triggers in hubs (RegionBehavior.Town) or while the player is
    // dead/loading. NEVER triggers if the player already has active enemy
    // phantoms out (avoids drowning the player in a compound event).
    public partial class Player
    {
        private static readonly Logger RogueEncounterLogger = LogManager.CreateLogger();

        // Sixty seconds between eligibility rolls keeps the load low —
        // roughly one dice-roll per minute of active play.
        private const long RogueEncounterCheckIntervalMs = 60_000;
        // Five minutes between successful encounters is the tightest window
        // that still lets the previous fight fully resolve.
        private const long RogueEncounterCooldownMs = 5 * 60_000;
        // 25% roll chance per eligibility check → expected value ~1 encounter
        // every 4 minutes of active play, floored by the cooldown above.
        private const double RogueEncounterRollChance = 0.25;

        private bool _rogueEncounterEnabled;
        private long _rogueEncounterLastMs;

        private readonly EventGroup _rogueEncounterEvents = new();
        private readonly EventPointer<RogueEncounterCheckEvent> _rogueEncounterCheckEvent = new();

        public bool RogueEncounterEnabled
        {
            get => _rogueEncounterEnabled;
            set
            {
                bool changed = _rogueEncounterEnabled != value;
                _rogueEncounterEnabled = value;
                if (changed && value) ScheduleRogueEncounterCheck();
            }
        }

        public long RogueEncounterCooldownRemainingMs
        {
            get
            {
                if (_rogueEncounterEnabled == false) return 0;
                long nowMs = Game?.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond ?? 0;
                long since = nowMs - _rogueEncounterLastMs;
                return Math.Max(0, RogueEncounterCooldownMs - since);
            }
        }

        internal void ScheduleRogueEncounterCheck()
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler == null) return;
            if (_rogueEncounterCheckEvent.IsValid) return;
            scheduler.ScheduleEvent(_rogueEncounterCheckEvent, TimeSpan.FromMilliseconds(RogueEncounterCheckIntervalMs), _rogueEncounterEvents);
            _rogueEncounterCheckEvent.Get().Initialize(this);
        }

        private void OnRogueEncounterCheck()
        {
            try
            {
                if (_rogueEncounterEnabled == false) return;

                Avatar avatar = CurrentAvatar;
                if (avatar == null || avatar.IsInWorld == false || avatar.IsDead) return;

                // Cooldown gate. Silent — the next scheduled check will roll again.
                long nowMs = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
                if (nowMs - _rogueEncounterLastMs < RogueEncounterCooldownMs) return;

                // Hub check — never ambush in towns / social spaces.
                if (IsHubRegion(avatar.Region)) return;

                // Don't stack encounters if the player already has enemy
                // phantoms out (from a previous encounter still in progress
                // or a manual spawn from the app).
                if (EnemyPhantomCount > 0) return;

                var rng = Game.Random;
                if (rng.NextDouble() >= RogueEncounterRollChance) return;

                TriggerRogueEncounter(avatar, rng);
                _rogueEncounterLastMs = nowMs;
            }
            finally
            {
                if (_rogueEncounterEnabled) ScheduleRogueEncounterCheck();
            }
        }

        /// <summary>
        /// Force-fire an encounter right now, bypassing the roll but still
        /// respecting the hub check. Used by the "test now" web endpoint /
        /// chat command.
        /// </summary>
        public string TriggerRogueEncounterNow()
        {
            Avatar avatar = CurrentAvatar;
            if (avatar == null || avatar.IsInWorld == false) return "not in world";
            if (IsHubRegion(avatar.Region)) return "cannot trigger in a hub";

            long nowMs = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
            TriggerRogueEncounter(avatar, Game.Random);
            _rogueEncounterLastMs = nowMs;
            return "encounter triggered";
        }

        private void TriggerRogueEncounter(Avatar avatar, MHServerEmu.Core.System.Random.GRandom rng)
        {
            // Weighted count: 1 hostile is the common case (60%), 2 is the
            // "oh no" case (30%), 3 is the "you kicked the anthill" case (10%).
            double r = rng.NextDouble();
            int count = r <= 0.6 ? 1 : r <= 0.9 ? 2 : 3;

            // Nemesis roll — the FIRST spawn of the encounter can come from
            // the player's revenge roster (rank-weighted). Only one nemesis
            // per encounter so a rank-5 doesn't triple up.
            var nemesis = TryPickNemesisForRogue(rng);
            bool nemesisSpawned = false;
            string nemesisName = null;

            int spawned = 0;
            string firstError = null;
            for (int i = 0; i < count; i++)
            {
                ulong id;
                string err;
                if (i == 0 && nemesis != null)
                {
                    string killerBase = string.IsNullOrEmpty(nemesis.LastKillerName) ? "Phantom" : nemesis.LastKillerName;
                    string suffix = NemesisSuffixForRank(nemesis.Rank);
                    string displayName = string.IsNullOrEmpty(suffix) ? killerBase : $"{killerBase} {suffix}";
                    id = avatar.SpawnNemesisPhantomHero((PrototypeId)nemesis.HeroRef, 0, displayName, nemesis.Rank, out err);
                    if (id != 0) { nemesisSpawned = true; nemesisName = displayName; }
                }
                else
                {
                    id = avatar.SpawnEnemyPhantomHero(PrototypeId.Invalid, 0, out err);
                }
                if (id != 0) spawned++;
                else firstError ??= err;
            }

            RogueEncounterLogger.Info($"[RogueEncounter] {GetName()}: spawned {spawned}/{count} hostile(s) in {avatar.Region?.PrototypeDataRef.GetName()}" + (nemesisSpawned ? $" (nemesis: {nemesisName})" : string.Empty));

            if (spawned == 0)
            {
                RogueEncounterLogger.Warn($"[RogueEncounter] {GetName()}: spawn failed — {firstError}");
                return;
            }

            // In-game chat warning — feels like a real event notification
            // rather than mobs silently appearing. Routed to the same
            // grouping-manager service that carries command output back
            // to players (uses the [System] name prefix when showSender
            // is off).
            try
            {
                if (PlayerConnection != null)
                {
                    string text = nemesisSpawned
                        ? $"⚔ Rogue Encounter — {nemesisName} has returned for you."
                        : spawned == 1
                            ? "⚠ Rogue Encounter — a hostile hero is closing on your position."
                            : $"⚠ Rogue Encounter — {spawned} hostile heroes have found you.";
                    var msg = new MHServerEmu.Core.Network.ServiceMessage.GroupingManagerMetagameMessage(
                        PlayerConnection.PlayerDbId, text, showSender: false);
                    MHServerEmu.Core.Network.ServerManager.Instance.SendMessageToService(
                        MHServerEmu.Core.Network.GameServiceType.GroupingManager, msg);
                }
            }
            catch (Exception ex) { RogueEncounterLogger.Warn($"[RogueEncounter] chat notify failed: {ex.Message}"); }
        }

        /// <summary>
        /// Hub check — RegionBehavior.Town covers every hub / social /
        /// waypoint space in the loaded data (data-driven, not path-string
        /// dependent). Also treated as a hub: null / mid-transition regions,
        /// so we don't accidentally spawn during a load screen.
        /// </summary>
        private static bool IsHubRegion(Regions.Region region)
        {
            if (region == null) return true;
            var proto = region.PrototypeDataRef.As<RegionPrototype>();
            if (proto == null) return true;
            return proto.Behavior == RegionBehavior.Town;
        }

        private sealed class RogueEncounterCheckEvent : CallMethodEvent<Player>
        {
            protected override CallbackDelegate GetCallback() => static (player) => player.OnRogueEncounterCheck();
        }
    }
}
