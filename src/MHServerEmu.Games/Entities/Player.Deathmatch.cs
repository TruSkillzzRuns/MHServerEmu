using System;
using System.Collections.Generic;
using MHServerEmu.Core.Logging;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.Events.Templates;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.GameData.Tables;
using MHServerEmu.Games.Properties;
using MHServerEmu.Games.MetaGames;
using MHServerEmu.Core.Memory;
using MHServerEmu.Games.Regions;
using MHServerEmu.Games.UI.Widgets;
using Gazillion;

namespace MHServerEmu.Games.Entities
{
    // Deathmatch — first vertical slice: 1v1 against a phantom opponent.
    //
    // Scope of this pass is deliberately narrow: prove the loop end to end
    // (warp -> hostile opponent -> kill race -> match end -> return home) on one
    // map, against a phantom, driven by a debug command. The queue NPC, real
    // player opponents, 3-duo teams, hazards and the map rotation all come later
    // and sit on top of this.
    //
    // WHY 1v1 FIRST: hostility comes from AllianceTable, a matrix built at
    // startup from prototype data. Every alliance is friendly to itself, no
    // alliance is hostile to itself, and only three mutually hostile PvP
    // alliances exist (PVPTeam1RED / 2WHITE / 3BLUE). Live testing confirmed the
    // client independently refuses to target anything its own data calls
    // friendly, even when the server says otherwise — so free-for-all is
    // impossible and three factions is the hard ceiling. 1v1 needs only two,
    // making it the smallest thing that can prove the mode works.
    //
    // See Desktop\MHO Files\Midtown Deathmatch\Midtown-Deathmatch-Design.md.
    public partial class Player
    {
        private static readonly Logger DeathmatchLogger = LogManager.CreateLogger();

        /// <summary>Test arena. One map for now — see the design doc's shortlist for the rest.</summary>
        private const string DeathmatchTestRegionPath = "Regions/Story/CH06FortStryker/Areas/ArmyBase/zzzArmyBaseInstances/SCSewer2Region.prototype";

        private const string DeathmatchTeamAPath = "Entity/Alliances/PVPTeam1RED.prototype";
        private const string DeathmatchTeamBPath = "Entity/Alliances/PVPTeam2WHITE.prototype";

        /// <summary>
        /// The MetaGame that backs the real PvP scoreboard. Every score message
        /// carries SetPvpEntityId(MetaGame.Id), so without a live MetaGame entity
        /// the client simply ignores them — verified live: NetMessageShowPvPScoreboard
        /// was sent with no MetaGame and the panel never opened.
        ///
        /// PvPTrainingRoom is used deliberately: it is IsPvP=true, has ZERO
        /// GameModes (so PvP.OnPostInit's automatic ActivateGameMode(0) does
        /// nothing and none of the defender turret/base machinery runs), and no
        /// region references it — both verified live. PatchDataDeathmatch.json
        /// gives it the score schema and the two PvP teams it lacks, at runtime,
        /// with no .sip edits.
        /// </summary>
        private const string DeathmatchMetaGamePath = "Metagame/PvPTrainingRoom.prototype";

        private ulong _deathmatchMetaGameId;

        /// <summary>True when the pending/active match is 3-duo TDM rather than 1v1.</summary>
        private bool _deathmatchIsTeams;
        private int _deathmatchTeamSize = DeathmatchTeamSizeDefault;
        private int _deathmatchTeamCount = DeathmatchTeamCountMax;
        private PrototypeId _deathmatchRegionOverride = PrototypeId.Invalid;

        /// <summary>Arena maps the tooling may pick from. Every one verified to hold the players it needs.</summary>
        public static readonly (string Name, string Path)[] DeathmatchArenas =
        {
            ("Midtown Patrol (Cosmic)",  "Regions/EndGame/TierX/PatrolMidtown/AltRegions/XManhattanRegion60Cosmic.prototype"),
            ("Midtown Patrol (1-60)",    "Regions/EndGame/TierX/PatrolMidtown/AltRegions/XManhattanRegion1to60.prototype"),
            ("Asgard Instance L60",      "Regions/EndGame/Terminals/Green/AsgardInstance/AltRegions/DailyGAsgardINSTRegionL60.prototype"),
            ("Fort Stryker Sewer",       "Regions/Story/CH06FortStryker/Areas/ArmyBase/zzzArmyBaseInstances/SCSewer2Region.prototype"),
            ("Hidden AIM Lab",           "Regions/ReusableInstances/ScienceLabs/HiddenAIMLab/AIMLabRegion.prototype"),
            ("Danger Room: AIM Base",    "Regions/EndGame/DangerRoomMode/AIMFacility/Testing/DRAIMBase1RegionTestingOnly.prototype"),
            ("Danger Room: Bamboo",      "Regions/EndGame/DangerRoomMode/BambooForest/Testing/DRBambooForestRegionTestingOnly.prototype"),
            ("Danger Room: Trainyard",   "Regions/EndGame/DangerRoomMode/Trainyard/zzzTesting/DRTrainyardRegionTestingOnly.prototype"),
            ("Danger Room: Subway",      "Regions/EndGame/DangerRoomMode/Subway/Testing/DRSubwayRegionTestingOnly.prototype"),
            ("PAX2013 Savage",           "Regions/ZZZDemoBranch/PAX2013Demo/PAX2013SavageRegion.prototype"),
        };

        /// <summary>Testing default. The real targets are 25 (1v1/FFA) and 50 shared (duos).</summary>
        public const int DeathmatchDefaultKillTarget = 15;

        /// <summary>How long after a death before the opponent is replaced.</summary>
        private const int DeathmatchOpponentRespawnMs = 5_000;

        // Per-kill payout. Deathmatch only — nothing else grants on a per-kill
        // basis, so these do not touch any other mode's economy.
        private const int DeathmatchCreditsPerKill = 20_000;
        private const int DeathmatchSplintersPerKill = 10;

        private bool _deathmatchWarpPending;
        private bool _deathmatchActive;
        private int _deathmatchKillTarget;
        private int _deathmatchPlayerScore;
        private int _deathmatchOpponentScore;
        private ulong _deathmatchOpponentId;

        /// <summary>
        /// The opponent's hero, picked once at match start and reused for every
        /// respawn. This is PvP, not a wave mode — you are fighting one
        /// character for the whole match, not a new hero each time it dies.
        /// </summary>
        private PrototypeId _deathmatchOpponentAvatarRef = PrototypeId.Invalid;

        /// <summary>
        /// Opponent ids that must never produce the automatic per-kill gear drop
        /// in Avatar.PhantomHero.cs. Deathmatch pays a fixed credits + splinters
        /// reward instead, so a gear lootsplosion on every kill is both wrong for
        /// the mode and absurd at this kill rate.
        ///
        /// Tracked BY ID rather than off the live _deathmatchActive flag, for the
        /// reason spelled out at that drop site: the corpse-cleanup tick runs
        /// after the kill handler, and the match-winning kill clears
        /// _deathmatchActive synchronously — so a live-flag check would let the
        /// final opponent's gear drop through. Same fix Trial already uses.
        /// </summary>
        private readonly HashSet<ulong> _deathmatchSuppressedPhantomIds = new();

        internal bool IsDeathmatchSuppressedPhantom(ulong id) => _deathmatchSuppressedPhantomIds.Contains(id);
        private Region _deathmatchRegion;
        private Event<EntityDeadGameEvent>.Action _deathmatchDeadAction;

        private readonly EventGroup _deathmatchEvents = new();

        // Deliberately NOT cancelled by EndDeathmatch. The finale lootsplosion is
        // queued during the win/loss handler and drip-feeds its rolls over the next
        // ~1.4s, so putting it in _deathmatchEvents would have teardown kill every
        // roll before the first tick. Cleared at match START instead.
        private readonly EventGroup _deathmatchFinaleEvents = new();

        private readonly EventPointer<DeathmatchRespawnEvent> _deathmatchRespawn = new();

        internal bool IsDeathmatchActive => _deathmatchActive;

        private static AlliancePrototype GetDeathmatchAlliance(string path)
            => GameDatabase.GetPrototypeRefByName(path).As<AlliancePrototype>();

        /// <summary>
        /// Start a 1v1 deathmatch. Validates, then warps — the arena itself is
        /// built on arrival (see OnAvatarEnteredRegionForDeathmatch), because the
        /// transfer destroys this Game instance.
        /// </summary>
        /// <summary>Start a 3-duo Team Deathmatch. Same warp path as 1v1; the arena is built on arrival.</summary>
        public string StartDeathmatchTeams(int killTarget, int teamSize = 0, string regionPath = null, IReadOnlyList<IReadOnlyList<ulong>> teamRosters = null, int teamCount = 0)
        {
            PrototypeId regionOverride = PrototypeId.Invalid;
            if (string.IsNullOrWhiteSpace(regionPath) == false)
            {
                regionOverride = GameDatabase.GetPrototypeRefByName(regionPath);
                if (regionOverride == PrototypeId.Invalid) return $"region not found: {regionPath}";
            }

            _deathmatchRegionOverride = regionOverride;

            // Hand-built rosters, if the caller supplied any. Applied before the
            // warp so they survive into the arrival handler with the rest of the
            // match state.
            ClearDeathmatchTeamRosters();
            if (teamRosters != null)
            {
                for (int team = 0; team < teamRosters.Count && team < DeathmatchTeamCount; team++)
                    SetDeathmatchTeamRoster(team, teamRosters[team]);
            }

            string result = StartDeathmatch1v1(killTarget > 0 ? killTarget : DeathmatchTeamKillTarget);
            if (_deathmatchWarpPending)
            {
                _deathmatchIsTeams = true;
                _deathmatchTeamSize = Math.Clamp(teamSize > 0 ? teamSize : DeathmatchTeamSizeDefault, 1, DeathmatchTeamSizeMax);
                _deathmatchTeamCount = Math.Clamp(teamCount > 0 ? teamCount : DeathmatchTeamCountMax, 2, DeathmatchTeamCountMax);
                string bracket = string.Join("v", System.Linq.Enumerable.Repeat(_deathmatchTeamSize.ToString(), _deathmatchTeamCount));
                return $"TDM started — {bracket}, first to {(killTarget > 0 ? killTarget : DeathmatchTeamKillTarget)}. Warping...";
            }
            return result;
        }

        public string StartDeathmatch1v1(int killTarget)
        {
            Avatar avatar = CurrentAvatar;
            if (avatar == null || avatar.IsInWorld == false) return "no avatar in world";
            if (_deathmatchActive) return "you're already in a deathmatch — finish it or use !dm quit";

            PrototypeId regionRef = _deathmatchRegionOverride != PrototypeId.Invalid
                ? _deathmatchRegionOverride
                : GameDatabase.GetPrototypeRefByName(DeathmatchTestRegionPath);
            if (regionRef == PrototypeId.Invalid) return "arena not found in this game version";

            if (GetDeathmatchAlliance(DeathmatchTeamAPath) == null || GetDeathmatchAlliance(DeathmatchTeamBPath) == null)
                return "PvP alliances not found in this game version";

            if (killTarget <= 0) killTarget = DeathmatchDefaultKillTarget;

            _deathmatchWarpPending = true;
            _deathmatchIsTeams = false;
            _deathmatchTeamSize = DeathmatchTeamSizeDefault;
            _deathmatchTeamCount = DeathmatchTeamCountMax;
            _deathmatchKillTarget = killTarget;

            avatar.TeleportToRegionFromWeb((ulong)regionRef);

            DeathmatchLogger.Info($"[Deathmatch] {GetName()}: match starting (first to {killTarget}), warping to {regionRef.GetNameFormatted()}");
            return $"deathmatch started — first to {killTarget} kills. Warping...";
        }

        /// <summary>Carry the pending warp across the Game-instance boundary. Called from PlayerConnection.BeginRegionTransfer.</summary>
        internal void SnapshotDeathmatchForTransfer()
        {
            var mig = PlayerConnection?.MigrationData;
            if (mig == null) return;

            // Leaving the arena mid-match ends it. Simplest correct behaviour for
            // now — no reconnect/rejoin handling in this slice.
            if (_deathmatchActive)
                EndDeathmatch("you left the arena", returnHome: false);

            if (_deathmatchWarpPending == false) return;
            mig.PendingDeathmatchWarp = true;
            mig.DeathmatchKillTarget = _deathmatchKillTarget;
            mig.DeathmatchIsTeams = _deathmatchIsTeams;
            mig.DeathmatchTeamSizeOverride = _deathmatchTeamSize;
            mig.DeathmatchTeamCountOverride = _deathmatchTeamCount;
            _deathmatchWarpPending = false;
        }

        /// <summary>Called from Avatar.OnEnteredWorld on every region entry for a real player avatar.</summary>
        internal void OnAvatarEnteredRegionForDeathmatch(Region region, Avatar avatar)
        {
            if (region == null || avatar == null) return;

            var mig = PlayerConnection?.MigrationData;
            if (mig == null || mig.PendingDeathmatchWarp == false) return;

            mig.PendingDeathmatchWarp = false;
            _deathmatchKillTarget = mig.DeathmatchKillTarget > 0 ? mig.DeathmatchKillTarget : DeathmatchDefaultKillTarget;
            _deathmatchIsTeams = mig.DeathmatchIsTeams;
            _deathmatchTeamSize = mig.DeathmatchTeamSizeOverride > 0 ? mig.DeathmatchTeamSizeOverride : DeathmatchTeamSizeDefault;
            _deathmatchTeamCount = mig.DeathmatchTeamCountOverride > 0 ? mig.DeathmatchTeamCountOverride : DeathmatchTeamCountMax;
            _deathmatchRegion = region;
            _deathmatchPlayerScore = 0;
            _deathmatchOpponentScore = 0;
            _deathmatchOpponentAvatarRef = PrototypeId.Invalid;   // re-picked on first spawn
            // Kill any finale rolls still drip-feeding from the previous match.
            Game.GameEventScheduler?.CancelAllEvents(_deathmatchFinaleEvents);
            _tdmFinaleGearRollsLeft = 0;
            _tdmFinaleArtifactRollsLeft = 0;

            _deathmatchActive = true;

            // Empty the arena — a deathmatch is between the two fighters, not a
            // PvE zone. Same helper the wave director and bounty hunts use.
            int removed = 0;
            try { removed = ClearArena(avatar); }
            catch (Exception ex) { DeathmatchLogger.Warn($"[Deathmatch] ClearArena threw: {ex.Message}"); }

            // Stand up the PvP MetaGame that backs the scoreboard, and join a
            // team through it. PvPTeam.AddPlayer applies the team's alliance
            // override itself, so this replaces the manual SetAllianceOverride
            // this used to do — same result, but through the real PvP system.
            if (TryCreateDeathmatchMetaGame(region) && _deathmatchIsTeams == false)
            {
                // 1v1: player on team 0 (RED). PvPTeam.AddPlayer applies the alliance.
                var pvp1v1 = GetDeathmatchMetaGame();
                if (pvp1v1?.Teams.Count > 0 && pvp1v1.Teams[0] is PvPTeam t0 && t0.AddPlayer(this))
                    pvp1v1.AddPlayer(this);   // creates the ScoreTable row
                else
                    SetAllianceOverride(GetDeathmatchAlliance(DeathmatchTeamAPath));
            }
            else if (_deathmatchMetaGameId == 0)
            {
                // Fall back to the manual alliance so the match is still
                // playable without a scoreboard, rather than failing outright.
                SetAllianceOverride(GetDeathmatchAlliance(DeathmatchTeamAPath));
                DeathmatchLogger.Warn($"[Deathmatch] {GetName()}: no MetaGame — falling back to manual alliance, no scoreboard");
            }

            _deathmatchDeadAction = OnDeathmatchEntityDead;
            region.EntityDeadEvent.AddActionBack(_deathmatchDeadAction);

            if (_deathmatchIsTeams)
            {
                if (SetupDeathmatchTeams(region, avatar, _deathmatchKillTarget, _deathmatchTeamSize, _deathmatchTeamCount) == false)
                {
                    DeathmatchLogger.Warn($"[Deathmatch] {GetName()}: TDM setup failed — falling back to 1v1");
                    _deathmatchIsTeams = false;
                    SpawnDeathmatchOpponent(avatar);
                }
            }
            else
            {
                SpawnDeathmatchOpponent(avatar);
            }

            DeathmatchLogger.Info($"[Deathmatch] {GetName()}: arena ready in {region.PrototypeName} (cleared {removed}), first to {_deathmatchKillTarget}");
            SendBannerLines($"Deathmatch — first to {_deathmatchKillTarget} kills. Opponent incoming.");
            UpdateDeathmatchWidget();

            // EXPERIMENT: this message carries no fields, unlike every score-row
            // message (which needs a real MetaGame entity id). Sending it tells
            // us whether the client will even open the PvP scoreboard panel
            // without a MetaGame behind it. If the panel appears but stays
            // empty, the panel is reachable and only the rows need a MetaGame.
            try
            {
                SendMessage(NetMessageShowPvPScoreboard.DefaultInstance);
                DeathmatchLogger.Info($"[Deathmatch] {GetName()}: sent NetMessageShowPvPScoreboard (MetaGame {_deathmatchMetaGameId} exists)");
            }
            catch (Exception ex) { DeathmatchLogger.Warn($"[Deathmatch] ShowPvPScoreboard failed: {ex.Message}"); }
        }

        /// <summary>
        /// Creates the PvP MetaGame in the arena and joins the player to a team.
        /// Returns false if anything is missing, in which case the caller falls
        /// back to a plain alliance override.
        /// </summary>
        private bool TryCreateDeathmatchMetaGame(Region region)
        {
            try
            {
                PrototypeId metaGameRef = GameDatabase.GetPrototypeRefByName(DeathmatchMetaGamePath);
                if (metaGameRef == PrototypeId.Invalid) return false;

                using EntitySettings settings = ObjectPoolManager.Instance.Get<EntitySettings>();
                settings.RegionId = region.Id;
                settings.EntityRef = metaGameRef;

                if (Game.EntityManager.CreateEntity(settings) is not PvP pvp)
                    return false;

                _deathmatchMetaGameId = pvp.Id;

                if (pvp.Teams.Count == 0) return false;

                // Deliberately does NOT join a team here — the mode does that, so
                // 1v1 and TDM can each pick their own. Joining here as well was a
                // real bug: MetaGameTeam.AddPlayer rejects a duplicate, so TDM's
                // own join failed and silently fell back to 1v1.
                DeathmatchLogger.Info($"[Deathmatch] {GetName()}: PvP MetaGame {pvp.Id} created, "
                    + $"scoreTable={(pvp.PvPScore != null ? "yes" : "NULL")}, teams={pvp.Teams.Count}, "
                    + $"regionMatch={(pvp.GetRegion() == region ? "yes" : "NO")}");
                return true;
            }
            catch (Exception ex)
            {
                DeathmatchLogger.Warn($"[Deathmatch] MetaGame creation failed: {ex.Message}");
                return false;
            }
        }

        private PvP GetDeathmatchMetaGame()
            => _deathmatchMetaGameId != 0 ? Game.EntityManager.GetEntity<PvP>(_deathmatchMetaGameId) : null;

        /// <summary>Spawns the opponent on the opposing alliance. Phantom for now; a real player takes this slot later.</summary>
        private void SpawnDeathmatchOpponent(Avatar avatar)
        {
            if (_deathmatchActive == false || avatar == null || avatar.IsInWorld == false) return;

            // First spawn picks the hero at random; every later spawn reuses it,
            // so the player faces the same opponent for the whole match.
            ulong id = avatar.SpawnEnemyPhantomHero(_deathmatchOpponentAvatarRef, avatar.CharacterLevel, out string error);
            if (id == 0)
            {
                DeathmatchLogger.Warn($"[Deathmatch] opponent spawn failed: {error}");
                return;
            }

            _deathmatchOpponentId = id;
            _deathmatchSuppressedPhantomIds.Add(id);   // no automatic gear drop for this one

            // WHITE, so the two are mutually hostile in real alliance data — both
            // client and server agree, which is what makes them targetable.
            if (Game.EntityManager.GetEntity<Agent>(id) is Agent opponent)
            {
                opponent.Properties[PropertyEnum.AllianceOverride] = GetDeathmatchAlliance(DeathmatchTeamBPath).DataRef;

                // Lock the hero in on the first spawn so respawns match.
                if (_deathmatchOpponentAvatarRef == PrototypeId.Invalid)
                    _deathmatchOpponentAvatarRef = opponent.PrototypeDataRef;
            }

            DeathmatchLogger.Info($"[Deathmatch] {GetName()}: opponent {id} ({_deathmatchOpponentAvatarRef.GetNameFormatted()}) spawned on WHITE");
        }

        private void OnDeathmatchEntityDead(in EntityDeadGameEvent evt)
        {
            if (_deathmatchActive == false || evt.Defender == null) return;

            if (_deathmatchIsTeams)
            {
                // EntityDeadGameEvent.Killer is a PLAYER, already resolved by the
                // game from whatever actually landed the blow. The roster is keyed
                // by AVATAR id, so passing the Player's id here never matched and
                // no kill ever scored. Resolve to the avatar; fall back to the raw
                // attacker's responsible power user for anything the game could
                // not attribute to a player.
                ulong killerId = evt.Killer?.CurrentAvatar?.Id ?? 0;
                if (killerId == 0 && evt.Attacker != null)
                    killerId = evt.Attacker.GetMostResponsiblePowerUser<Avatar>()?.Id ?? evt.Attacker.Id;
                if (CreditDeathmatchTeamKill(killerId, evt.Defender.Id)) return;   // match ended

                // Player kills still pay out, same as 1v1.
                if (killerId == CurrentAvatar?.Id) GrantDeathmatchKillReward();

                RespawnDeathmatchCombatant(evt.Defender.Id);
                return;
            }

            // Opponent died -> the player scores.
            if (evt.Defender.Id == _deathmatchOpponentId)
            {
                _deathmatchOpponentId = 0;
                _deathmatchPlayerScore++;
                GrantDeathmatchKillReward();
                DeathmatchLogger.Info($"[Deathmatch] {GetName()}: score {_deathmatchPlayerScore}-{_deathmatchOpponentScore}");

                if (_deathmatchPlayerScore >= _deathmatchKillTarget)
                {
                    EndDeathmatch($"you win {_deathmatchPlayerScore}-{_deathmatchOpponentScore}", returnHome: true);
                    return;
                }

                AnnounceDeathmatchScore();
                ScheduleDeathmatchRespawn();
                return;
            }

            // The player died -> the opponent scores. The player's own respawn is
            // the normal death/release flow; only the score is ours to track.
            if (evt.Defender.Id == CurrentAvatar?.Id)
            {
                _deathmatchOpponentScore++;
                DeathmatchLogger.Info($"[Deathmatch] {GetName()}: score {_deathmatchPlayerScore}-{_deathmatchOpponentScore}");

                if (_deathmatchOpponentScore >= _deathmatchKillTarget)
                {
                    EndDeathmatch($"you lose {_deathmatchPlayerScore}-{_deathmatchOpponentScore}", returnHome: true);
                    return;
                }

                AnnounceDeathmatchScore();
            }
        }

        /// <summary>
        /// Per-kill payout, granted directly rather than as a world drop so it
        /// cannot be missed mid-fight or lost when the arena tears down.
        /// </summary>
        private void GrantDeathmatchKillReward()
        {
            try
            {
                var currencyGlobals = GameDatabase.CurrencyGlobalsPrototype;
                if (currencyGlobals == null) return;

                Properties.AdjustProperty(DeathmatchCreditsPerKill, new(PropertyEnum.Currency, currencyGlobals.Credits));
                Properties.AdjustProperty(DeathmatchSplintersPerKill, new(PropertyEnum.Currency, currencyGlobals.EternitySplinters));

                DeathmatchLogger.Info($"[Deathmatch] {GetName()}: kill reward +{DeathmatchCreditsPerKill} credits, +{DeathmatchSplintersPerKill} splinters");
            }
            catch (Exception ex) { DeathmatchLogger.Warn($"[Deathmatch] kill reward failed: {ex.Message}"); }
        }

        private void AnnounceDeathmatchScore()
        {
            PushDeathmatchScoreToTable();
            SendBannerLines($"{_deathmatchPlayerScore} - {_deathmatchOpponentScore}  (first to {_deathmatchKillTarget})  +{DeathmatchCreditsPerKill:N0} credits, +{DeathmatchSplintersPerKill} splinters");
            UpdateDeathmatchWidget();
        }

        /// <summary>
        /// Drives the native HUD fraction widget with "your kills / target" —
        /// the same widget Trial of the Impossible and Endless Challenge already
        /// use for their counters.
        ///
        /// NOTE: this is NOT the real PvP scoreboard. That one is driven entirely
        /// by a MetaGame + ScoreTable with a prototype-defined schema
        /// (MetaGameMode.OnActivate -> NetMessageShowPvPScoreboard, and
        /// ScoreTable -> NetMessagePvPScorePlayerUpdate). Standing up a real PvP
        /// MetaGame is a much bigger change — see the design doc.
        /// </summary>
        /// <summary>
        /// Writes the running score into the MetaGame's ScoreTable, which is what
        /// emits NetMessagePvPScorePlayerUpdate with a valid PvpEntityId. Category
        /// 0 is the first entry in the schema (DefenderPvP's own player schema).
        /// </summary>
        private void PushDeathmatchScoreToTable()
        {
            try
            {
                var pvp = GetDeathmatchMetaGame();
                if (pvp == null) { DeathmatchLogger.Warn("[Deathmatch] score push: MetaGame entity is gone"); return; }
                var score = pvp.PvPScore;
                if (score == null) { DeathmatchLogger.Warn("[Deathmatch] score push: PvPScore is null (schema patch not applied?)"); return; }

                score.SetPlayerScoreValue(this, _deathmatchPlayerScore, 0);
                bool readBack = score.TryGetPlayerScore(this, 0, out int stored);
                DeathmatchLogger.Info($"[Deathmatch] score push: category0={_deathmatchPlayerScore}, readBack={(readBack ? stored.ToString() : "NO ROW")}");
            }
            catch (Exception ex) { DeathmatchLogger.Warn($"[Deathmatch] score table update failed: {ex.Message}"); }
        }

        private void UpdateDeathmatchWidget()
        {
            try
            {
                PrototypeId widgetRef = GetEndlessWaveWidgetRef();
                if (widgetRef == PrototypeId.Invalid) return;
                CurrentAvatar?.Region?.UIDataProvider?.GetWidget<UIWidgetGenericFraction>(widgetRef)
                    ?.SetCount(_deathmatchPlayerScore, _deathmatchKillTarget);
            }
            catch (Exception ex) { DeathmatchLogger.Warn($"[Deathmatch] widget update failed: {ex.Message}"); }
        }

        private void ClearDeathmatchWidget()
        {
            try
            {
                PrototypeId widgetRef = GetEndlessWaveWidgetRef();
                if (widgetRef == PrototypeId.Invalid) return;
                CurrentAvatar?.Region?.UIDataProvider?.DeleteWidget(widgetRef);
            }
            catch { /* best effort */ }
        }

        private void ScheduleDeathmatchRespawn()
        {
            if (_deathmatchActive == false) return;
            if (_deathmatchRespawn.IsValid) return;

            Game.GameEventScheduler?.ScheduleEvent(_deathmatchRespawn,
                TimeSpan.FromMilliseconds(DeathmatchOpponentRespawnMs), _deathmatchEvents);
            _deathmatchRespawn.Get()?.Initialize(this);
        }

        private void DoDeathmatchRespawn()
        {
            if (_deathmatchActive == false) return;
            SpawnDeathmatchOpponent(CurrentAvatar);
        }

        /// <summary>Ends the match, cleans up, and (optionally) sends the player home.</summary>
        public string EndDeathmatch(string reason, bool returnHome)
        {
            if (_deathmatchActive == false) return "you're not in a deathmatch";

            _deathmatchActive = false;

            if (_deathmatchRegion != null && _deathmatchDeadAction != null)
                _deathmatchRegion.EntityDeadEvent.RemoveAction(_deathmatchDeadAction);
            _deathmatchDeadAction = null;
            _deathmatchRegion = null;

            Game.GameEventScheduler?.CancelAllEvents(_deathmatchEvents);
            ClearDeathmatchWidget();
            _deathmatchSuppressedPhantomIds.Clear();
            ClearDeathmatchTeams();
            _deathmatchIsTeams = false;

            // Destroy the MetaGame with the match. RemovePlayer clears the team's
            // alliance override; SetAllianceOverride(null) below is the belt-and-
            // braces for the fallback path where no MetaGame existed.
            if (_deathmatchMetaGameId != 0)
            {
                if (Game.EntityManager.GetEntity<PvP>(_deathmatchMetaGameId) is PvP pvp)
                {
                    try { pvp.RemovePlayer(this); pvp.Destroy(); }
                    catch (Exception ex) { DeathmatchLogger.Warn($"[Deathmatch] MetaGame teardown threw: {ex.Message}"); }
                }
                _deathmatchMetaGameId = 0;
            }

            // Critical: drop the PvP alliance. Leaving it on would make the
            // player hostile to everything for the rest of the session
            // (PvPDefenderGameMode:97 does the same on leave).
            SetAllianceOverride(null);

            // Remove the opponent so it does not linger in the region.
            if (_deathmatchOpponentId != 0)
            {
                if (Game.EntityManager.GetEntity<Agent>(_deathmatchOpponentId) is Agent opponent)
                {
                    try { opponent.Destroy(); }
                    catch (Exception ex) { DeathmatchLogger.Warn($"[Deathmatch] opponent destroy threw: {ex.Message}"); }
                }
                _deathmatchOpponentId = 0;
            }

            DeathmatchLogger.Info($"[Deathmatch] {GetName()}: ended — {reason}");

            SendBannerLines($"Deathmatch over — {reason}.");

            if (returnHome)
            {
                Avatar avatar = CurrentAvatar;
                if (avatar != null && avatar.IsInWorld)
                {
                    // Drop a portal rather than yanking the player out the instant
                    // the last kill lands. An instant warp gives no time to see the
                    // result, and it dragged the phantom squad along with it.
                    Properties[PropertyEnum.LastTownRegionForAccount] = (PrototypeId)(ulong)RegionPrototypeId.NPEAvengersTowerHUBRegion;
                    try
                    {
                        SpawnBountyReturnPortal(avatar.Region, avatar.RegionLocation.Position);
                        SendBannerLines("Portal open — step through when you are ready.");
                    }
                    catch (Exception ex)
                    {
                        DeathmatchLogger.Warn($"[Deathmatch] portal spawn failed, falling back to warp: {ex.Message}");
                        avatar.TeleportToRegionFromWeb((ulong)RegionPrototypeId.NPEAvengersTowerHUBRegion);
                    }
                }
            }

            return $"deathmatch ended — {reason}";
        }

        private sealed class DeathmatchRespawnEvent : CallMethodEvent<Player>
        {
            protected override CallbackDelegate GetCallback() => static (player) => player.DoDeathmatchRespawn();
        }
    }
}
