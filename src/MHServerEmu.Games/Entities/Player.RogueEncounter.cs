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
        // Per-slot chance a rogue phantom is drawn from the team-up pool
        // instead of the avatar pool. Kept low so avatar phantoms remain
        // the "canonical" rogue face but team-up cameos happen occasionally.
        private const double RogueEncounterTeamUpChance = 0.15;
        // Per-slot chance a NON-roster slot spawns a random hero as a full
        // rank-5 nemesis boss — a rare "surprise boss" ambush even when you
        // have no active nemeses. At level 60 it wears+drops the BiS jackpot.
        private const double RogueEncounterSurpriseRank5Chance = 0.08;

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

            // Terminals / boss chambers cap at 1 — those regions already
            // have a boss encounter running with its own dense mob spawns,
            // and stacking 2-3 hero-tier phantoms + ultimate VFX on top of
            // the fight overwhelms the MHO client's render path (12+ year
            // old single-threaded renderer). One nasty rogue phantom in a
            // terminal still feels like a Rogue Encounter without OOM'ing
            // the client.
            if (IsTerminalRegion(avatar.Region))
                count = 1;

            // Nemesis roll — each spawn slot rolls independently against
            // the roster. Same hero can't appear twice in one encounter
            // (usedNemesisRefs guards it), but different active nemeses can
            // all show up together in the same ambush if the rolls land.
            int spawned = 0;
            int nemesisSpawnedCount = 0;
            string firstNemesisName = null;
            string firstError = null;
            var usedNemesisRefs = new System.Collections.Generic.HashSet<ulong>();
            for (int i = 0; i < count; i++)
            {
                ulong id;
                string err;
                var nemesis = TryPickNemesisForRogue(rng);
                if (nemesis != null && usedNemesisRefs.Add(nemesis.HeroRef))
                {
                    string killerBase = string.IsNullOrEmpty(nemesis.LastKillerName) ? "Phantom" : nemesis.LastKillerName;
                    string suffix = NemesisSuffixForRank(nemesis.Rank);
                    // Star-prefix by rank so the nameplate reads e.g.
                    // "★★★ NovaStrike001 the Slayer" for rank 3. Immediately
                    // legible from across the screen without needing to
                    // squint at rank text.
                    string stars = new string('★', System.Math.Clamp(nemesis.Rank, 1, NemesisMaxRank));
                    string displayName = string.IsNullOrEmpty(suffix)
                        ? $"{stars} {killerBase}"
                        : $"{stars} {killerBase} {suffix}";
                    id = avatar.SpawnNemesisPhantomHero((PrototypeId)nemesis.HeroRef, 0, displayName, nemesis.Rank, out err);
                    if (id != 0)
                    {
                        nemesisSpawnedCount++;
                        firstNemesisName ??= displayName;
                    }
                }
                else if (rng.NextDouble() < RogueEncounterSurpriseRank5Chance)
                {
                    // Surprise boss: a random hero (Invalid ref -> the spawn
                    // path rolls one) ambushes as a full rank-5 nemesis even
                    // though it isn't on the roster. At level 60 the rank-5
                    // spawn path makes it wear + drop the BiS jackpot.
                    string stars = new string('★', NemesisMaxRank);
                    string suffix = NemesisSuffixForRank(NemesisMaxRank);
                    string displayName = $"{stars} Phantom {suffix}";
                    id = avatar.SpawnNemesisPhantomHero(PrototypeId.Invalid, 0, displayName, NemesisMaxRank, out err);
                    if (id != 0)
                    {
                        nemesisSpawnedCount++;
                        firstNemesisName ??= displayName;
                    }
                }
                else
                {
                    // Team-up cameo: small per-slot chance to draw from the
                    // team-up pool instead of the avatar pool. SpawnEnemyPhantomHero
                    // detects a team-up ref and dispatches to the team-up path.
                    PrototypeId roll = rng.NextDouble() < RogueEncounterTeamUpChance
                        ? PickRandomTeamUpRef(rng)
                        : PrototypeId.Invalid;
                    id = avatar.SpawnEnemyPhantomHero(roll, 0, out err);
                }
                if (id != 0) spawned++;
                else firstError ??= err;
            }
            bool nemesisSpawned = nemesisSpawnedCount > 0;
            string nemesisName = firstNemesisName;

            RogueEncounterLogger.Info($"[RogueEncounter] {GetName()}: spawned {spawned}/{count} hostile(s) in {avatar.Region?.PrototypeDataRef.GetName()}" + (nemesisSpawned ? $" (nemesis: {nemesisName})" : string.Empty));

            if (spawned == 0)
            {
                RogueEncounterLogger.Warn($"[RogueEncounter] {GetName()}: spawn failed — {firstError}");
                return;
            }

            // In-game announcement — banner-style so the player actually
            // sees it in the middle of a fight. Uses the same grouping-
            // manager channel as [System] chat but with prominent emoji
            // framing on separate lines so it stands out from mission
            // spam and damage numbers.
            //
            // Text templates chosen for tone:
            //   Regular:       ⚔ ROGUE ENCOUNTER — Hostile heroes have located you!
            //   Nemesis (1):   ☠ YOUR NEMESIS RETURNS — {Name} has come for you!
            //   Nemesis (2+):  ☠ NEMESIS SWARM — {N} of your killers have united!
            //   Terminal alt:  ☠ NEMESIS BOSS — {Name} has cornered you in the terminal!
            //                  ⚔ ROGUE INTRUSION — A hostile hero has invaded your mission!
            try
            {
                if (PlayerConnection != null)
                {
                    bool inTerminal = IsTerminalRegion(avatar.Region);
                    string bannerText;
                    if (nemesisSpawnedCount > 1)
                    {
                        bannerText = $"☠ NEMESIS SWARM — {nemesisSpawnedCount} of your killers have united!";
                    }
                    else if (nemesisSpawned)
                    {
                        bannerText = inTerminal
                            ? $"☠ NEMESIS BOSS — {nemesisName} has cornered you in the terminal!"
                            : $"☠ YOUR NEMESIS RETURNS — {nemesisName} has come for you!";
                    }
                    else if (inTerminal)
                    {
                        bannerText = "⚔ ROGUE INTRUSION — A hostile hero has invaded your mission!";
                    }
                    else
                    {
                        bannerText = spawned == 1
                            ? "⚔ ROGUE ENCOUNTER — A hostile hero has located you!"
                            : $"⚔ ROGUE ENCOUNTER — {spawned} hostile heroes have located you!";
                    }

                    SendBannerLines(bannerText);
                }
            }
            catch (Exception ex) { RogueEncounterLogger.Warn($"[RogueEncounter] chat notify failed: {ex.Message}"); }
        }

        /// <summary>
        /// Send a banner-style multi-line notification through the grouping
        /// manager chat channel. A blank spacer line above the banner and
        /// a divider line below help it stand out in the combat log.
        /// </summary>
        private void SendBannerLines(string text)
        {
            if (PlayerConnection == null) return;
            const string divider = "━━━━━━━━━━━━━━━━━━━━";
            SendBannerLine(divider);
            SendBannerLine(text);
            SendBannerLine(divider);
        }

        private void SendBannerLine(string line)
        {
            var msg = new MHServerEmu.Core.Network.ServiceMessage.GroupingManagerMetagameMessage(
                PlayerConnection.PlayerDbId, line, showSender: false);
            MHServerEmu.Core.Network.ServerManager.Instance.SendMessageToService(
                MHServerEmu.Core.Network.GameServiceType.GroupingManager, msg);
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

        /// <summary>
        /// Terminal / boss-chamber / cosmic-terminal detection. There isn't
        /// a dedicated RegionBehavior for these, so we match on the region
        /// prototype path — terminals all live under Regions/EndGame/
        /// Terminals/ (and cosmic variants under Regions/EndGame/Cosmic/).
        /// Used to cap Rogue Encounter spawn count so the fight doesn't OOM
        /// the game client on top of the boss encounter.
        /// </summary>
        private static bool IsTerminalRegion(Regions.Region region)
        {
            if (region == null) return false;
            string path = region.PrototypeDataRef.GetName();
            if (string.IsNullOrEmpty(path)) return false;
            return path.Contains("/Terminals/", System.StringComparison.OrdinalIgnoreCase)
                || path.Contains("/Cosmic/", System.StringComparison.OrdinalIgnoreCase)
                || path.Contains("BossChamber", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Uniformly pick a random team-up ref from the resolved pool. Returns
        /// PrototypeId.Invalid if the pool is empty (which routes the caller
        /// back to the default avatar path).
        /// </summary>
        private static PrototypeId PickRandomTeamUpRef(MHServerEmu.Core.System.Random.GRandom rng)
        {
            var pool = Avatar.GetAllPhantomTeamUpRefs();
            if (pool.Count == 0) return PrototypeId.Invalid;
            return pool[rng.Next(0, pool.Count)].TeamUpRef;
        }

        private sealed class RogueEncounterCheckEvent : CallMethodEvent<Player>
        {
            protected override CallbackDelegate GetCallback() => static (player) => player.OnRogueEncounterCheck();
        }
    }
}
