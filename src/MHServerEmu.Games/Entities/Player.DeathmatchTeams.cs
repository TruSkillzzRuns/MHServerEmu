using System;
using System.Collections.Generic;
using Gazillion;
using MHServerEmu.Core.Memory;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.Events.Templates;
using MHServerEmu.Games.MetaGames;
using MHServerEmu.Games.Properties;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.Regions;
using MHServerEmu.Games.UI.Widgets;

namespace MHServerEmu.Games.Entities
{
    // 3-Duo Team Deathmatch — three duos, one per PvP alliance, racing to a
    // shared kill total.
    //
    // WHY THREE: hostility comes from AllianceTable, a matrix built at startup
    // from prototype data. No alliance is hostile to itself, and only three
    // mutually hostile PvP alliances exist (PVPTeam1RED / 2WHITE / 3BLUE). Live
    // testing confirmed the client independently refuses to target anything its
    // own data calls friendly, even when the server says otherwise — so three
    // factions is a hard ceiling, not a design preference. Players sharing a
    // team can never damage each other, which is exactly what makes duos work.
    //
    // Teams come from FactionTeam1/2/3Tier1 (RED/WHITE/BLUE), attached to the
    // otherwise-unused PvPTrainingRoom metagame by PatchDataDeathmatch.json —
    // runtime prototype patching, no .sip edits.
    public partial class Player
    {
        /// <summary>Shared kill target per duo. Both members contribute to one pool.</summary>
        public const int DeathmatchTeamKillTarget = 50;

        /// <summary>Default per-team size. Duos, so two. Overridable per match for testing.</summary>
        public const int DeathmatchTeamSizeDefault = 2;

        /// <summary>Upper bound on per-team size. FactionTeam*Tier1 declares MaxPlayers=15, so that is the data-imposed ceiling.</summary>
        public const int DeathmatchTeamSizeMax = 15;

        /// <summary>Per-team size for the CURRENT match.</summary>
        private int _tdmTeamSize = DeathmatchTeamSizeDefault;

        public int DeathmatchTeamSize => _tdmTeamSize;

        /// <summary>
        /// Explicit rosters, one list per team, each holding avatar prototype
        /// refs. Set from the OmegaDev2 panel so a match can be hand-built
        /// ("Colossus and Thing on RED, Magneto and Juggernaut on BLUE") instead
        /// of rolling random heroes. A null/short list falls back to random for
        /// the remaining slots, so partial rosters are fine.
        /// </summary>
        private readonly List<ulong>[] _tdmTeamRosters =
        {
            new List<ulong>(), new List<ulong>(), new List<ulong>()
        };   // sized to DeathmatchTeamCountMax

        /// <summary>Live combatant id -> the slot it occupies, so a respawn refills the same slot.</summary>
        private readonly Dictionary<ulong, ulong> _tdmCombatantHero = new();

        /// <summary>
        /// (team, slot) -> the avatar prototype that slot is playing.
        ///
        /// Locked in the FIRST time a slot spawns and reused for every respawn,
        /// so a combatant keeps its identity all match. Without this a slot rolls
        /// a fresh random hero on each death — which only looked correct in 1v1,
        /// where an explicit roster happened to be set.
        /// </summary>
        private readonly Dictionary<(int team, int slot), ulong> _tdmSlotHero = new();

        internal void SetDeathmatchTeamRoster(int team, IEnumerable<ulong> heroRefs)
        {
            if (team < 0 || team >= DeathmatchTeamCountMax) return;
            _tdmTeamRosters[team].Clear();
            if (heroRefs != null) _tdmTeamRosters[team].AddRange(heroRefs);
        }

        internal void ClearDeathmatchTeamRosters()
        {
            foreach (var r in _tdmTeamRosters) r.Clear();
        }

        /// <summary>
        /// The hero a given slot on a team should use. Returns Invalid when the
        /// roster does not cover that slot, which the spawn path treats as
        /// "roll a random hero".
        /// </summary>
        private PrototypeId GetRosterHero(int team, int slotIndex)
        {
            if (team < 0 || team >= DeathmatchTeamCountMax) return PrototypeId.Invalid;
            var roster = _tdmTeamRosters[team];
            if (slotIndex < 0 || slotIndex >= roster.Count) return PrototypeId.Invalid;
            return (PrototypeId)roster[slotIndex];
        }

        /// <summary>
        /// Upper bound on teams. THREE is the engine ceiling (only three mutually
        /// hostile PvP alliances exist and none is hostile to itself) — it is not
        /// a requirement. A match may run with two.
        /// </summary>
        public const int DeathmatchTeamCountMax = 3;

        /// <summary>Teams in the CURRENT match: 2 or 3.</summary>
        private int _tdmTeamCount = DeathmatchTeamCountMax;

        public int DeathmatchTeamCount => _tdmTeamCount;

        private bool _tdmActive;
        private int _tdmKillTarget;

        /// <summary>Team index (0-2) this player is on.</summary>
        private int _tdmMyTeam = -1;

        /// <summary>Shared score per team, indexed by team.</summary>
        private readonly int[] _tdmTeamScores = new int[DeathmatchTeamCountMax];

        /// <summary>Which team each combatant belongs to, so a kill can be credited. Keyed by entity id.</summary>
        private readonly Dictionary<ulong, int> _tdmCombatantTeam = new();

        /// <summary>Per-team spawn anchor, spread across the arena so duos do not start on top of each other.</summary>
        private readonly Vector3[] _tdmTeamAnchors = new Vector3[DeathmatchTeamCountMax];

        private readonly EventPointer<DeathmatchPlayerPlacementEvent> _tdmPlayerPlacement = new();
        private readonly EventPointer<DeathmatchSterilizeEvent> _tdmSterilize = new();

        /// <summary>How many more sterilize passes to run, and how far apart.</summary>
        private int _tdmSterilizePassesLeft;
        private const int DeathmatchSterilizePasses = 4;
        private const int DeathmatchSterilizeIntervalMs = 1500;

        /// <summary>How far a team's members scatter around their own anchor. Small on purpose — a team starts together.</summary>
        private const float DeathmatchDuoSpread = 350f;

        #region TEMPORARY test hero lock

        /// <summary>
        /// Toggled by !dmdata testheroes. While on, every phantom spawns as Magik
        /// or Nick Fury, alternating — the two heroes with reported damage-immunity
        /// bugs. Purely a diagnostic aid so the bugs can be reproduced on demand
        /// instead of waiting for a 64-hero random roster to roll them.
        ///
        /// Both refs resolved from live data with !dmdata heroes (there are no
        /// Skrull avatar variants in the pool — the roster is the 64 real playable
        /// avatars, so "Nick Fury Skrull" is this NickFury wearing a costume).
        /// </summary>
        public static bool DeathmatchTestHeroLock { get; set; } = false;

        private static readonly string[] DeathmatchTestHeroPaths =
        {
            "Entity/Characters/Avatars/Shipping/NickFury.prototype",
            "Entity/Characters/Avatars/Shipping/Magik.prototype",
        };

        private int _tdmTestHeroFlip;

        private static PrototypeId GetDeathmatchTestHero(int index)
        {
            string path = DeathmatchTestHeroPaths[Math.Abs(index) % DeathmatchTestHeroPaths.Length];
            PrototypeId heroRef = GameDatabase.GetPrototypeRefByName(path);
            if (heroRef == PrototypeId.Invalid)
                DeathmatchLogger.Warn($"[TDM] test hero {path} does not resolve on this game version");
            return heroRef;
        }

        #endregion

        #region Anti-spawn-camping

        // Three independent defences. Any one alone is beatable; together a
        // camper standing on an enemy anchor gets no free kills.
        //
        // The problem, measured live 2026-08-05 from the 1v1v1 log: the kill and
        // the replacement's placement were 5ms apart
        //   22:00:36.940  released phantom Player 412
        //   22:00:36.945  team 1 phantom 466 placed at x:944 y:-818
        // because RespawnDeathmatchCombatant called SpawnDeathmatchTeammate
        // synchronously, and that method places purely from _tdmTeamAnchors[team]
        // with no regard for where any hostile is standing. Every replacement
        // landed within DeathmatchDuoSpread of the same anchor the camper was
        // parked on.

        /// <summary>Delay before a fallen combatant is replaced. Solo already uses 5s (DeathmatchOpponentRespawnMs); teams stay snappier.</summary>
        private const int DeathmatchTeamRespawnDelayMs = 3_000;

        /// <summary>How long a fresh combatant is untouchable AND unable to act.</summary>
        private const int DeathmatchSpawnProtectionMs = 2_500;

        /// <summary>A spawn point wants no hostile within this range.</summary>
        private const float DeathmatchSpawnSafeRadius = 1_000f;

        /// <summary>Scattered placements to score before taking the best one.</summary>
        private const int DeathmatchSpawnPlacementTries = 10;

        private readonly List<EventPointer<DeathmatchTeamRespawnEvent>> _tdmRespawnEvents = new();
        private readonly List<EventPointer<DeathmatchSpawnProtectEndEvent>> _tdmProtectEvents = new();

        #endregion

        /// <summary>
        /// Floor on the distance between team anchors, for small regions. Real
        /// separation comes from the region's own bounds, so large maps spread
        /// the teams much further than this. Only used where a map has no
        /// hand-picked spawn points.
        /// </summary>
        private const float DeathmatchMinTeamSeparation = 3000f;

        /// <summary>
        /// The three Midtown spawn points, walked to and captured in game.
        /// Shared by every Midtown region ref because a warp aimed at an alt
        /// region (Cosmic / 1-60) resolves into the band, and the lookup keys off
        /// whichever region actually instantiates.
        ///
        /// Separation: RED-WHITE 8824u, RED-BLUE 10990u, WHITE-BLUE 11666u.
        /// </summary>
        private static readonly Vector3[] MidtownSpawns =
        {
            new Vector3(12114f,    7020f,     55f),   // RED
            new Vector3(5747.99f,  909.79f,   55f),   // WHITE
            new Vector3(2353.25f,  12070.88f, 61f),   // BLUE
        };

        // NOTE: declared ABOVE the dictionary below on purpose. Static field
        // initializers run in declaration order, so a dictionary that referenced
        // this array while it was still declared further down captured null and
        // threw a NullReferenceException on the first lookup.

        /// <summary>
        /// Hand-picked team spawn points, per region. Walked to in-game and
        /// captured, so they land on real ground in sensible places rather than
        /// wherever a generated ring happens to fall.
        ///
        /// Index order is RED, WHITE, BLUE — matching the team order everywhere
        /// else. A region with no entry here falls back to the generated ring.
        /// </summary>
        private static readonly Dictionary<ulong, Vector3[]> DeathmatchFixedSpawns = new()
        {
            // Midtown Patrol band (0x33878A4E74CE1AC2). Separation: RED-WHITE
            // 8824u, RED-BLUE 10990u, WHITE-BLUE 11666u — far enough apart that
            // no team starts on top of another.
            [0x33878A4E74CE1AC2] = MidtownSpawns,   // XManhattanRegionBand      (1.48 / 1.52)
            [0xD0C8F44B2B722077] = MidtownSpawns,   // XManhattanRegion60Cosmic  (1.48 / 1.52)
            [0xE86F0BF4CA0B1F0D] = MidtownSpawns,   // XManhattanRegion1to60     (1.52 only — MISSING on 1.48/1.53)

            // 1.53 renamed the Midtown Patrol regions outright — none of the
            // XManhattan* paths above resolve there. Ids resolved on a running
            // 1.53 server (!lookup region Midtown), not converted or guessed.
            //
            // The coordinates are REUSED as-is on the assumption the map layout
            // is unchanged between the XManhattan and MidtownPatrol versions of
            // this region. That assumption is NOT verified — if 1.53 spawns land
            // off-mesh or stacked, these three entries are what to re-capture.
            [0xBE4FACDACAB01C22] = MidtownSpawns,   // MidtownPatrolRegionBand   (1.53)
            [0x86522A2A4C5920D9] = MidtownSpawns,   // MidtownPatrolL1to60Region (1.53)
            [0xED769F08CAF71C28] = MidtownSpawns,   // MidtownPatrolRegionBase   (1.53)
        };



        internal bool IsDeathmatchTeamsActive => _tdmActive;

        /// <summary>How many combatants are currently alive on a team (the player counts toward their own).</summary>
        private int CountDeathmatchTeam(int team)
        {
            int n = 0;
            foreach (var kvp in _tdmCombatantTeam)
                if (kvp.Value == team) n++;
            return n;
        }

        /// <summary>True while a TDM is running and this entity is one of its combatants.</summary>
        internal bool IsDeathmatchTeamCombatant(ulong entityId)
            => _tdmActive && _tdmCombatantTeam.ContainsKey(entityId);

        /// <summary>
        /// Sets up three teams on the deathmatch MetaGame, puts the player on one,
        /// and fills the rest with phantoms.
        ///
        /// Phantom fill is not a testing shortcut: most servers running this have
        /// nobody else online, so a lobby needing six players would never fill and
        /// the mode would be dead on arrival. Real players take these slots as
        /// they queue.
        /// </summary>
        private bool SetupDeathmatchTeams(Region region, Avatar avatar, int killTarget, int teamSize, int teamCount)
        {
            var pvp = GetDeathmatchMetaGame();
            if (pvp == null) return false;

            _tdmTeamCount = Math.Clamp(teamCount > 0 ? teamCount : DeathmatchTeamCountMax, 2, DeathmatchTeamCountMax);

            if (pvp.Teams.Count < _tdmTeamCount)
            {
                DeathmatchLogger.Warn($"[TDM] MetaGame has only {pvp.Teams.Count} teams, need {_tdmTeamCount} — is PatchDataDeathmatch.json applied?");
                return false;
            }

            _tdmActive = true;
            _tdmKillTarget = killTarget > 0 ? killTarget : DeathmatchTeamKillTarget;
            _tdmTeamSize = Math.Clamp(teamSize > 0 ? teamSize : DeathmatchTeamSizeDefault, 1, DeathmatchTeamSizeMax);

            // An explicit roster defines that team's size. The largest roster
            // sets the match size so every team is filled to the same strength,
            // with random heroes covering any slot a roster does not name.
            int largestRoster = 0;
            foreach (var roster in _tdmTeamRosters)
                if (roster.Count > largestRoster) largestRoster = roster.Count;
            if (largestRoster > 0)
                _tdmTeamSize = Math.Clamp(Math.Max(_tdmTeamSize, largestRoster), 1, DeathmatchTeamSizeMax);
            Array.Clear(_tdmTeamScores);
            _tdmCombatantTeam.Clear();

            // Player takes team 0 (RED). PvPTeam.AddPlayer applies the alliance.
            _tdmMyTeam = 0;
            if (pvp.Teams[0] is not PvPTeam myTeam || myTeam.AddPlayer(this) == false)
            {
                DeathmatchLogger.Warn("[TDM] could not join team 0");
                return false;
            }
            pvp.AddPlayer(this);   // creates the ScoreTable row
            if (avatar != null) _tdmCombatantTeam[avatar.Id] = 0;

            // Spread the three duos around the arena. The player's own team anchors
            // where they already stand; the rivals get points far enough away that
            // a match opens with three separate fights rather than one scrum on the
            // spawn point. Every anchor is navi-mesh validated, so nobody starts
            // out of bounds or stuck in geometry.
            ChooseDeathmatchTeamAnchors(region, avatar);

            // Repeated sterilize passes. Scheduled HERE, not inside
            // ChooseDeathmatchTeamAnchors — that method returns early for maps
            // with hand-picked spawns, which would have silently skipped all mob
            // clearing on exactly the maps we most want it on.
            //
            // One pass at arrival is not enough in a PublicCombatZone like
            // Midtown: the population manager had not spawned anything yet when
            // the arena was set up (the log showed "cleared 0"), and it keeps
            // spawning afterwards. Each pass destroys live spawns, tears down the
            // spawn schedulers so they stop coming back, and shuts off the
            // region's own event metagame.
            _tdmSterilizePassesLeft = DeathmatchSterilizePasses;
            ScheduleDeathmatchSterilize();

            // One-shot self-check a few seconds later — see Player.DeathmatchDiag.cs.
            ScheduleDeathmatchDiagnostics();
            UpdateDeathmatchTeamWidget();

            // The player already occupies one slot on their own team, so their duo
            // needs teamSize-1 phantoms; rivals need the full teamSize.
            // Slot 0 of the player's own team is the player, so their roster
            // entries start at index 1 and line up with the phantom slots.
            for (int slot = 1; slot < _tdmTeamSize; slot++)
                SpawnDeathmatchTeammate(avatar, pvp, _tdmMyTeam, slot);

            for (int team = 0; team < _tdmTeamCount; team++)
            {
                if (team == _tdmMyTeam) continue;
                for (int slot = 0; slot < _tdmTeamSize; slot++)
                    SpawnDeathmatchTeammate(avatar, pvp, team, slot);
            }

            for (int t = 0; t < pvp.Teams.Count; t++)
            {
                var pt = pvp.Teams[t] as PvPTeam;
                DeathmatchLogger.Info($"[TDM] teams[{t}] = {pvp.Teams[t].ProtoRef.GetNameFormatted()} alliance={(pt?.Alliance?.DataRef.GetNameFormatted() ?? "<none>")}");
            }

            DeathmatchLogger.Info($"[TDM] {GetName()}: {DescribeDeathmatchBracket()} ready in {region?.PrototypeName} — first team to {_tdmKillTarget} shared kills, {_tdmCombatantTeam.Count} combatants");

            // A pool entry does not always load as itself. Verified live 2026-08-06:
            // the picker chose TRZooAquariumRegion and the player arrived in
            // BronxZooRegionBand — a different, far larger region carrying full
            // story content, broken area generation and a boss cutscene. Nothing in
            // the prototype data announces that redirect, so the only reliable
            // detection is comparing what we asked for against what we got.
            if (_deathmatchRequestedArena != null && region != null)
            {
                string actual = region.PrototypeName ?? "<null>";
                if (actual.Equals(_deathmatchRequestedArena, StringComparison.OrdinalIgnoreCase) == false)
                {
                    DeathmatchLogger.Warn($"[TDM] ARENA REDIRECT — asked for {_deathmatchRequestedArena}, landed in {actual}. " +
                                          "This pool entry does not load as itself and should be removed.");
                }
            }

            // Name the arena in chat. The region is now picked at random from a
            // pool (PickDeathmatchArena), so "where am I" is a real question that
            // the fixed-arena version never had to answer.
            //
            // Chat, not a banner: the countdown owns the banner channel for the
            // next three seconds and SendBannerMessage(doNotQueue:true,
            // showImmediately:true) would be stomped by the "3" a beat later.
            SendBannerLines($"Arena — {region?.PrototypeName ?? "unknown"}");

            // Rubber-band setup, then the match-start sting.
            ApplyDeathmatchWinLossRubberBand();
            StartDeathmatchCountdown();

            // No match-start banner — the HUD counter carries the score and the
            // kill lines carry the rest. Requested: keep the top of the screen clear.
            return true;
        }

        /// <summary>
        /// Picks the <paramref name="count"/> most widely separated standable
        /// positions the region actually contains, and writes them into
        /// _tdmTeamAnchors. Returns false if the region cannot supply enough
        /// verified positions, in which case the caller falls back to the ring.
        ///
        /// Every candidate is proven walkable before it is considered — same two
        /// checks the phantom placement code uses (NaviMesh.Contains with
        /// PathFlags.Walk, plus GetCellAtPosition to reject points that pass the
        /// mesh test but have no backing cell). That is the difference from the
        /// geometric ring, which invents points and hopes they land on the map.
        /// </summary>
        private bool TryPickDispersedAnchors(Region region, int count, float avatarRadius)
        {
            if (region == null || count <= 0) return false;

            try
            {
                var walkCheck = new Navi.DefaultContainsPathFlagsCheck(Navi.PathFlags.Walk);
                float probeRadius = MathF.Max(20f, avatarRadius);
                List<Vector3> candidates = new();

                // Cell centres are the natural sample points: every cell is a
                // real, generated piece of the map. Sample a few offsets inside
                // each one too so a large cell can contribute more than a single
                // position and the spread is not quantised to cell size.
                foreach (Cell cell in region.Cells)
                {
                    Vector3 cellCentre = cell.RegionBounds.Center;
                    float half = MathF.Max(0f, cell.RegionBounds.Width * 0.25f);

                    TryAddAnchorCandidate(region, cellCentre, probeRadius, walkCheck, candidates);
                    if (half > 0f)
                    {
                        TryAddAnchorCandidate(region, cellCentre + new Vector3(half, half, 0f), probeRadius, walkCheck, candidates);
                        TryAddAnchorCandidate(region, cellCentre + new Vector3(-half, half, 0f), probeRadius, walkCheck, candidates);
                        TryAddAnchorCandidate(region, cellCentre + new Vector3(half, -half, 0f), probeRadius, walkCheck, candidates);
                        TryAddAnchorCandidate(region, cellCentre + new Vector3(-half, -half, 0f), probeRadius, walkCheck, candidates);
                    }
                }

                if (candidates.Count < count)
                {
                    DeathmatchLogger.Warn($"[TDM] only {candidates.Count} walkable candidate(s) in {region.PrototypeName}, need {count} — falling back to the generated ring");
                    return false;
                }

                // Farthest-point selection: seed with the two furthest apart,
                // then repeatedly take whichever candidate is furthest from
                // everything already chosen. That maximises the SMALLEST gap
                // between teams, which is what "as far apart as the map allows"
                // actually means — maximising total distance would happily put
                // two teams next to each other.
                List<Vector3> chosen = new(count);

                int bestA = 0, bestB = 0;
                float bestDist = -1f;
                for (int i = 0; i < candidates.Count; i++)
                {
                    for (int j = i + 1; j < candidates.Count; j++)
                    {
                        float d = Vector3.DistanceSquared2D(candidates[i], candidates[j]);
                        if (d <= bestDist) continue;
                        bestDist = d; bestA = i; bestB = j;
                    }
                }

                chosen.Add(candidates[bestA]);
                if (count > 1) chosen.Add(candidates[bestB]);

                while (chosen.Count < count)
                {
                    int bestIdx = -1;
                    float bestMin = -1f;
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        float minDist = float.MaxValue;
                        foreach (Vector3 taken in chosen)
                            minDist = MathF.Min(minDist, Vector3.DistanceSquared2D(candidates[i], taken));

                        if (minDist <= bestMin) continue;
                        bestMin = minDist; bestIdx = i;
                    }

                    if (bestIdx < 0) break;
                    chosen.Add(candidates[bestIdx]);
                }

                if (chosen.Count < count) return false;

                float smallestGap = float.MaxValue;
                for (int i = 0; i < chosen.Count; i++)
                {
                    _tdmTeamAnchors[i] = chosen[i];
                    for (int j = i + 1; j < chosen.Count; j++)
                        smallestGap = MathF.Min(smallestGap, Vector3.Distance2D(chosen[i], chosen[j]));
                }

                DeathmatchLogger.Info($"[TDM] dispersed spawns for {region.PrototypeName}: {candidates.Count} walkable candidate(s), closest pair {smallestGap:F0}u apart");
                for (int i = 0; i < count; i++)
                    DeathmatchLogger.Info($"[TDM] team {i} anchor {_tdmTeamAnchors[i].ToStringNames()}");

                return true;
            }
            catch (Exception ex)
            {
                DeathmatchLogger.Warn($"[TDM] dispersed anchor pick failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Adds a position to the candidate pool only if it is genuinely standable.</summary>
        private static void TryAddAnchorCandidate(Region region, Vector3 position, float probeRadius,
            in Navi.DefaultContainsPathFlagsCheck walkCheck, List<Vector3> candidates)
        {
            Vector3 onFloor = RegionLocation.ProjectToFloor(region, position);
            if (region.NaviMesh.Contains(onFloor, probeRadius, walkCheck) == false) return;
            if (region.GetCellAtPosition(onFloor) == null) return;
            candidates.Add(onFloor);
        }

        /// <summary>
        /// Picks one spawn anchor per team, spread apart and validated against the
        /// navi mesh. The player's team keeps the player's own position.
        /// </summary>
        private void ChooseDeathmatchTeamAnchors(Region region, Avatar avatar)
        {
            var rng = Game.Random;
            float radius = avatar?.Bounds.Radius ?? 40f;

            // NOTE: this runs from Avatar.OnEnteredWorld, BEFORE the avatar has
            // its final position — RegionLocation.Position reads (0,0) at this
            // point. It must therefore never be used as an anchor or a fallback:
            // doing so put all three anchors on (0,0) and stacked every team on
            // one spot. Everything below derives from the REGION's bounds only.
            Vector3 playerPos = avatar?.RegionLocation.Position ?? Vector3.Zero;

            // Hand-picked points win outright where a map has them — no ring
            // generation, no navi-mesh search, because these were captured by
            // standing on the spot in game.
            if (region != null
                && DeathmatchFixedSpawns.TryGetValue((ulong)region.PrototypeDataRef, out Vector3[] fixedSpawns)
                && fixedSpawns != null
                && fixedSpawns.Length >= _tdmTeamCount)
            {
                for (int team = 0; team < _tdmTeamCount; team++)
                    _tdmTeamAnchors[team] = fixedSpawns[team];

                DeathmatchLogger.Info($"[TDM] using hand-picked spawns for {region.PrototypeName}");
                for (int i = 0; i < _tdmTeamCount; i++)
                    DeathmatchLogger.Info($"[TDM] team {i} anchor {_tdmTeamAnchors[i].ToStringNames()}");

                ScheduleDeathmatchPlayerPlacement();
                return;
            }

            // No hand-picked points for this map — pick the most widely
            // separated spots the region ACTUALLY has, from real walkable
            // geometry rather than a geometric ring.
            //
            // The ring below is kept only as a last resort. It fails badly on
            // maps whose AABB is much larger than their playable space, which is
            // exactly the 1v1v1 sewer: confirmed live 2026-08-05, all three ring
            // anchors came out at z:0 off the navi mesh
            //   [TDM] team 0 anchor x:-2941.49 y:-589.60 z:0
            // ChooseScatteredArenaPos only searches within ring*0.35 of its
            // ideal point, so all 24 attempts missed the walkable area and it
            // returned the unvalidated fallback. ChangeRegionPosition then
            // refused the move and every phantom stayed where it spawned — 230u
            // from the player:
            //   [TDM] team 1 phantom 680 DID NOT MOVE — wanted x:1981.35
            //   y:-2252.60 z:0, is at x:227.08 y:35.39 z:1 (2883u off)
            if (TryPickDispersedAnchors(region, _tdmTeamCount, radius))
            {
                ScheduleDeathmatchPlayerPlacement();
                return;
            }

            // Spread the teams around the CENTRE of the region, 120 degrees
            // apart, at a radius taken from the region's own bounds. Anchoring
            // off the player (what this did before) kept every team within a
            // couple of thousand units of wherever they happened to stand, which
            // is why teams started on top of each other.
            Vector3 centre;
            float ring;
            try
            {
                var aabb = region.Aabb;
                centre = aabb.Center;
                float span = MathF.Min(aabb.Width, aabb.Length);
                ring = MathF.Max(DeathmatchMinTeamSeparation, span * 0.33f);
            }
            catch (Exception ex)
            {
                DeathmatchLogger.Warn($"[TDM] region bounds unavailable: {ex.Message}");
                return;
            }

            float baseAngle = (float)(rng.NextDouble() * Math.PI * 2.0);

            for (int team = 0; team < _tdmTeamCount; team++)
            {
                float angle = baseAngle + (float)(team * (Math.PI * 2.0 / _tdmTeamCount));
                Vector3 ideal = centre + new Vector3(MathF.Cos(angle) * ring, MathF.Sin(angle) * ring, 0f);

                // Validated onto the navi mesh, searching outward from the ideal
                // point; falls back to the player's position (always walkable)
                // rather than dropping anyone out of bounds.
                // Search outward from the ideal point. The fallback is the ideal
                // point itself projected to the floor — NOT the player's position,
                // which is (0,0) this early and would collapse all three anchors
                // onto the same spot.
                Vector3 fallback = RegionLocation.ProjectToFloor(region, ideal);
                Vector3 chosen = Avatar.ChooseScatteredArenaPos(
                    region, ideal, fallback, rng, radius, 0f, ring * 0.35f);

                _tdmTeamAnchors[team] = chosen;
            }

            // Start the player at their own anchor too, so all three teams begin
            // equally placed instead of two being moved and one not.
            //
            // SCHEDULED, not immediate: this whole setup runs from
            // Avatar.OnEnteredWorld BEFORE base.OnEnteredWorld has finished, and
            // repositioning the avatar mid-entry invalidates its Region — the
            // base call then dereferences a null Region and crashes the game
            // instance, dumping the player back to the login screen. Phantoms are
            // safe to move here because they are separate, fully-entered entities;
            // only the arriving avatar itself must wait.
            ScheduleDeathmatchPlayerPlacement();


            for (int i = 0; i < _tdmTeamCount; i++)
                DeathmatchLogger.Info($"[TDM] team {i} anchor {_tdmTeamAnchors[i].ToStringNames()}");

            // Anchors landing on top of each other is the failure mode that
            // stacks every team on one spot — surface it rather than let it look
            // like a spawn bug.
            for (int a = 0; a < _tdmTeamCount; a++)
                for (int b = a + 1; b < _tdmTeamCount; b++)
                {
                    float sep = Vector3.Distance2D(_tdmTeamAnchors[a], _tdmTeamAnchors[b]);
                    if (sep < 1000f)
                        DeathmatchLogger.Warn($"[TDM] anchors {a} and {b} are only {sep:F0}u apart — teams will start on top of each other");
                }
        }

        private void ScheduleDeathmatchSterilize()
        {
            if (_tdmSterilizePassesLeft <= 0 || _tdmSterilize.IsValid) return;
            Game.GameEventScheduler?.ScheduleEvent(_tdmSterilize,
                TimeSpan.FromMilliseconds(DeathmatchSterilizeIntervalMs), _deathmatchEvents);
            _tdmSterilize.Get()?.Initialize(this);
        }

        /// <summary>
        /// Empties the arena of everything that is not a match combatant, and
        /// stops it refilling. A deathmatch arena must contain only the three
        /// teams — leftover mobs also read as FRIENDLY to a player wearing a PvP
        /// team alliance (mob alliances are not hostile to PVPTeam*), so they
        /// cannot be fought back against even though their AI still attacks.
        /// </summary>
        private void DoDeathmatchSterilize()
        {
            if (_tdmActive == false) return;

            Avatar avatar = CurrentAvatar;
            Region region = avatar?.Region;
            if (region == null) return;

            int removed = 0;
            try { removed = ClearArena(avatar); }
            catch (Exception ex) { DeathmatchLogger.Warn($"[TDM] sterilize ClearArena threw: {ex.Message}"); }

            // Stop the region refilling itself. Deallocate destroys every live
            // spawn spec and cancels the pending spawn events, which is what
            // stops mobs coming straight back after a pass.
            try { region.PopulationManager?.Deallocate(); }
            catch (Exception ex) { DeathmatchLogger.Warn($"[TDM] population deallocate threw: {ex.Message}"); }

            // Shut down the region's OWN metagame (Midtown's patrol event, etc).
            // Ours is excluded by id — destroying it would take the teams and the
            // scoreboard with it.
            try
            {
                foreach (ulong metaId in new List<ulong>(region.MetaGames))
                {
                    if (metaId == _deathmatchMetaGameId) continue;
                    if (Game.EntityManager.GetEntity<MetaGame>(metaId) is MetaGame mg)
                    {
                        // Tell the client to tear down this metagame's UI BEFORE
                        // destroying it. Verified in MetaGameStateMode.cs:77-78 —
                        // PatrolMidtown's mode sends NetMessageStartPvPTimer (the
                        // "Next Event" countdown, StatePickIntervalMS=180000) and
                        // sets a UIWidgetGenericFraction. Destroying the metagame
                        // sends neither a stop nor a widget delete, so the client
                        // keeps drawing the bar with nothing behind it.
                        try
                        {
                            SendMessage(NetMessageStopPvPTimer.CreateBuilder().SetMetaGameId(mg.Id).Build());

                            if (mg.Prototype is MetaGamePrototype mgProto && mgProto.GameModes != null)
                            {
                                foreach (PrototypeId modeRef in mgProto.GameModes)
                                {
                                    if (modeRef.As<MetaGameStateModePrototype>() is not MetaGameStateModePrototype stateMode) continue;
                                    if (stateMode.UIStatePickIntervalWidget == PrototypeId.Invalid) continue;

                                    mg.ResetUIWidgetGenericFraction(stateMode.UIStatePickIntervalWidget);
                                    DeathmatchLogger.Info($"[TDM] cleared widget {stateMode.UIStatePickIntervalWidget.GetNameFormatted()} from {mg.PrototypeDataRef.GetNameFormatted()}");
                                }
                            }
                        }
                        catch (Exception ex) { DeathmatchLogger.Warn($"[TDM] metagame UI teardown threw: {ex.Message}"); }

                        mg.Destroy();
                        DeathmatchLogger.Info($"[TDM] destroyed region metagame {metaId} ({mg.PrototypeDataRef.GetNameFormatted()})");
                    }
                }
            }
            catch (Exception ex) { DeathmatchLogger.Warn($"[TDM] metagame teardown threw: {ex.Message}"); }

            _tdmSterilizePassesLeft--;
            DeathmatchLogger.Info($"[TDM] sterilize pass: removed {removed}, {_tdmSterilizePassesLeft} pass(es) left");
            ScheduleDeathmatchSterilize();
        }

        private sealed class DeathmatchSterilizeEvent : CallMethodEvent<Player>
        {
            protected override CallbackDelegate GetCallback() => static (player) => player.DoDeathmatchSterilize();
        }

        /// <summary>Moves the player onto their team's anchor once region entry has finished.</summary>
        private void ScheduleDeathmatchPlayerPlacement()
        {
            if (_tdmPlayerPlacement.IsValid) return;
            Game.GameEventScheduler?.ScheduleEvent(_tdmPlayerPlacement, TimeSpan.FromMilliseconds(250), _deathmatchEvents);
            _tdmPlayerPlacement.Get()?.Initialize(this);
        }

        private void DoDeathmatchPlayerPlacement()
        {
            if (_tdmActive == false || _tdmMyTeam < 0) return;

            Avatar avatar = CurrentAvatar;
            if (avatar == null || avatar.IsInWorld == false) return;

            try
            {
                avatar.Locomotor?.Stop();
                avatar.ChangeRegionPosition(_tdmTeamAnchors[_tdmMyTeam], null);
                DeathmatchLogger.Info($"[TDM] {GetName()}: placed at team {_tdmMyTeam} anchor");
            }
            catch (Exception ex) { DeathmatchLogger.Warn($"[TDM] player placement failed: {ex.Message}"); }
        }

        private sealed class DeathmatchPlayerPlacementEvent : CallMethodEvent<Player>
        {
            protected override CallbackDelegate GetCallback() => static (player) => player.DoDeathmatchPlayerPlacement();
        }

        /// <summary>
        /// Where the player respawns: their own team's anchor, so a death returns
        /// them to their start rather than a checkpoint elsewhere on the map.
        /// </summary>
        internal bool TryGetDeathmatchRespawnPos(out Vector3 pos)
        {
            pos = Vector3.Zero;
            if (_tdmActive == false || _tdmMyTeam < 0) return false;
            pos = _tdmTeamAnchors[_tdmMyTeam];
            return true;
        }

        /// <summary>
        /// Spawns one phantom onto the given team and records its allegiance.
        ///
        /// The player's OWN duo partner is spawned through the FRIENDLY path so it
        /// joins the party properly (visible in the party UI) and runs ally AI.
        /// Spawning it as an enemy phantom and merely re-colouring its alliance —
        /// which is what this did at first — produced a "teammate" that was in no
        /// party and fought on enemy logic. Rival duos are genuine enemy phantoms.
        /// </summary>
        private void SpawnDeathmatchTeammate(Avatar avatar, PvP pvp, int team, int slotIndex = -1)
        {
            if (avatar == null || avatar.IsInWorld == false) return;

            // Hard per-team limit, enforced here rather than at any single call
            // site, so NO path can ever put a third body on a team. A duo is two.
            int onTeam = CountDeathmatchTeam(team);
            if (onTeam >= _tdmTeamSize)
            {
                DeathmatchLogger.Warn($"[TDM] team {team} already has {onTeam}/{_tdmTeamSize} — spawn refused");
                return;
            }

            bool isMyDuo = team == _tdmMyTeam;

            // Hand-picked hero for this slot if the caller supplied a roster;
            // otherwise whatever this slot has been playing all match. Only a
            // brand-new slot rolls at random.
            PrototypeId heroRef = GetRosterHero(team, slotIndex);
            if (heroRef == PrototypeId.Invalid && slotIndex >= 0
                && _tdmSlotHero.TryGetValue((team, slotIndex), out ulong lockedHero))
                heroRef = (PrototypeId)lockedHero;

            // TEMPORARY test lock — !dmdata testheroes on. Forces every phantom to
            // be Magik or Nick Fury so their reported damage-immunity bugs can be
            // reproduced without waiting for a random roster to roll them. Off by
            // default; remove this block once both are diagnosed.
            if (DeathmatchTestHeroLock)
            {
                PrototypeId forced = GetDeathmatchTestHero(_tdmTestHeroFlip++);
                if (forced != PrototypeId.Invalid)
                {
                    heroRef = forced;
                    DeathmatchLogger.Info($"[TDM] TEST HERO LOCK — forcing {forced.GetNameFormatted()} for team {team} slot {slotIndex}");
                }
            }

            string error;
            ulong id = isMyDuo
                ? avatar.SpawnPhantomHeroFromIntent(heroRef, avatar.CharacterLevel, GetName(), false, 0, out error, bypassCap: true)
                : avatar.SpawnEnemyPhantomHero(heroRef, avatar.CharacterLevel, out error);

            if (id == 0)
            {
                DeathmatchLogger.Warn($"[TDM] team {team} phantom spawn failed: {error}");
                return;
            }

            if (Game.EntityManager.GetEntity<Agent>(id) is not Agent phantom) return;

            // A phantom is an Agent, not a Player, so it cannot join a PvPTeam
            // (which takes Players). Apply the team's alliance directly instead —
            // same end result: teammates are friendly, rivals are hostile.
            //
            // The player is on this same alliance for their own duo, so an ally
            // phantom stays friendly to the player while becoming hostile to the
            // other two duos.
            if (pvp.Teams[team] is PvPTeam pvpTeam && pvpTeam.Alliance != null)
            {
                phantom.Properties[PropertyEnum.AllianceOverride] = pvpTeam.Alliance.DataRef;

                // Read the alliance back. Setting the property is not proof the
                // entity resolves to it — Avatar.Alliance may resolve through the
                // owning Player rather than the entity's own override, and a
                // phantom's owner is a synthetic Player we do not touch here.
                string wanted = pvpTeam.Alliance.DataRef.GetNameFormatted();
                string got = phantom.Alliance?.DataRef.GetNameFormatted() ?? "<null>";
                if (wanted != got)
                    DeathmatchLogger.Warn($"[TDM] team {team} phantom {id} ALLIANCE MISMATCH — set {wanted}, resolves as {got}");
                else
                    DeathmatchLogger.Info($"[TDM] team {team} phantom {id} alliance {got}");
            }
            else
            {
                DeathmatchLogger.Warn($"[TDM] team {team} has no PvPTeam/alliance — phantom {id} keeps its default alliance");
            }

            // Register the phantom's Player with the MetaGame and its PvPTeam.
            //
            // The old comment here said a phantom "is an Agent, not a Player, so
            // it cannot join a PvPTeam" — that was wrong. SpawnPhantomHeroCore
            // creates a real synthetic Player entity and calls
            // Player.CreateAvatar on it, the same path a human login uses
            // (Avatar.PhantomHero.cs:5910-5927). The Agent handed back IS that
            // Player's avatar, so GetOwnerOfType<Player> resolves.
            //
            // This matters for more than tidiness:
            //   * ScoreTable keys its rows by Player (ScoreTable.cs:30), so
            //     without this the scoreboard can only ever hold ONE row - the
            //     human. With it, every combatant has a row.
            //   * PvPScoreEventHandler.OnEntityDead needs the killer to be a
            //     Player and the victim an Avatar (MetaGameEventHandler.cs:51-107).
            //     Both now hold, so kills / deaths / assists / killing-spree
            //     counters populate from the shipped handler.
            RegisterDeathmatchPhantomWithTeam(phantom, pvp, team);

            // Move it to its team's area. Phantoms spawn next to the player by
            // default, which would put all six on one spot.
            if (isMyDuo == false || _tdmCombatantTeam.Count > 0)
            {
                try
                {
                    Vector3 anchor = _tdmTeamAnchors[team];
                    Vector3 pos = PickDeathmatchSafeSpawnPos(phantom, team, anchor);
                    phantom.Locomotor?.Stop();
                    phantom.ChangeRegionPosition(pos, null);

                    // Verify the move actually landed. ChangeRegionPosition can
                    // silently no-op (documented in ChoosePhantomLeashPos: a point
                    // can pass NaviMesh.Contains yet have no backing Cell), and it
                    // throws nothing when it does — so "no exception" is not proof
                    // the entity moved. Compare against where we asked it to go.
                    Vector3 actual = phantom.RegionLocation.Position;
                    float off = Vector3.Distance2D(actual, pos);
                    if (off > 200f)
                        DeathmatchLogger.Warn($"[TDM] team {team} phantom {id} DID NOT MOVE — wanted {pos.ToStringNames()}, is at {actual.ToStringNames()} ({off:F0}u off)");
                    else
                        DeathmatchLogger.Info($"[TDM] team {team} phantom {id} placed at {actual.ToStringNames()}");

                    // Where it ACTUALLY ended up is what matters. ChangeRegionPosition
                    // can settle an entity somewhere other than the requested point,
                    // so re-test the landing spot and pull the phantom back to the
                    // validated anchor if it is not standable. Without this a phantom
                    // can sit in geometry it cannot path out of for the whole match.
                    var landCheck = new Navi.DefaultContainsPathFlagsCheck(Navi.PathFlags.Walk);
                    float landRadius = MathF.Max(phantom.Bounds.Radius, 40f);
                    bool standable = phantom.Region.NaviMesh.Contains(actual, landRadius, landCheck)
                                     && phantom.Region.GetCellAtPosition(actual) != null;

                    if (standable == false)
                    {
                        Vector3 rescue = _tdmTeamAnchors[team];
                        DeathmatchLogger.Warn($"[TDM] team {team} phantom {id} landed on non-walkable ground at {actual.ToStringNames()} — moving to the validated anchor {rescue.ToStringNames()}");
                        phantom.Locomotor?.Stop();
                        phantom.ChangeRegionPosition(rescue, null);
                    }
                }
                catch (Exception ex) { DeathmatchLogger.Warn($"[TDM] team {team} placement failed: {ex.Message}"); }
            }

            _tdmCombatantTeam[id] = team;
            if (slotIndex >= 0)
            {
                _tdmCombatantHero[id] = (ulong)slotIndex;
                // Lock this slot's hero on first spawn so respawns keep it.
                if (_tdmSlotHero.ContainsKey((team, slotIndex)) == false)
                    _tdmSlotHero[(team, slotIndex)] = (ulong)phantom.PrototypeDataRef;
            }
            _deathmatchSuppressedPhantomIds.Add(id);   // fixed payout, no gear lootsplosion

            ApplyDeathmatchSpawnProtection(phantom);

            DeathmatchLogger.Info($"[TDM] team {team} phantom {id} ({phantom.PrototypeDataRef.GetNameFormatted()}) spawned as {(isMyDuo ? "ALLY (party)" : "enemy")}");
        }

        #region Anti-spawn-camping implementation

        /// <summary>
        /// Picks the scattered spawn point that sits FARTHEST from the nearest
        /// hostile combatant, rather than the first walkable one near the anchor.
        ///
        /// Hostiles are read from _tdmCombatantTeam (plus the player's own avatar),
        /// so this needs no spatial query and cannot miss a combatant that a
        /// partition filter would have hidden.
        /// </summary>
        private Vector3 PickDeathmatchSafeSpawnPos(Agent phantom, int team, Vector3 anchor)
        {
            Region region = phantom.Region;
            float radius = phantom.Bounds.Radius;

            // Everyone hostile to this team, by position.
            List<Vector3> hostiles = new();
            foreach (var kvp in _tdmCombatantTeam)
            {
                if (kvp.Value == team) continue;
                if (Game.EntityManager.GetEntity<WorldEntity>(kvp.Key) is WorldEntity foe && foe.IsInWorld)
                    hostiles.Add(foe.RegionLocation.Position);
            }
            if (_tdmMyTeam != team && CurrentAvatar?.IsInWorld == true)
                hostiles.Add(CurrentAvatar.RegionLocation.Position);

            // Build a pool of candidates that are VERIFIED standable, not merely
            // whatever ChooseScatteredArenaPos handed back.
            //
            // ChooseScatteredArenaPos does its own checks, but the pattern already
            // documented in ChoosePhantomLeashPos holds here too: a point can pass
            // NaviMesh.Contains and still have no backing Cell, and the placement
            // verification below only catches a position the entity failed to REACH
            // — not one it reached and then cannot path out of. That is how a
            // phantom ends up standing in geometry it can never walk out of.
            //
            // TryAddAnchorCandidate is the same three-check validation used to pick
            // the match's starting anchors (ProjectToFloor + NaviMesh.Contains +
            // GetCellAtPosition), so respawns now get exactly the same guarantee the
            // initial spawns already had.
            var walkCheck = new Navi.DefaultContainsPathFlagsCheck(Navi.PathFlags.Walk);
            float probeRadius = MathF.Max(radius, 40f);

            List<Vector3> candidates = new(DeathmatchSpawnPlacementTries);
            for (int i = 0; i < DeathmatchSpawnPlacementTries; i++)
            {
                float spread = DeathmatchDuoSpread * (1f + i * 0.5f);
                Vector3 raw = Avatar.ChooseScatteredArenaPos(region, anchor, anchor, Game.Random, radius, 60f, spread);
                TryAddAnchorCandidate(region, raw, probeRadius, walkCheck, candidates);
            }

            if (candidates.Count == 0)
            {
                // The anchor itself was validated when the match started, so it is
                // the safest possible fallback — better a contested spawn than one
                // the phantom cannot move out of.
                DeathmatchLogger.Warn($"[TDM] team {team} — no walkable scatter point passed validation; falling back to the validated team anchor");
                return anchor;
            }

            if (hostiles.Count == 0)
                return candidates[0];

            Vector3 best = candidates[0];
            float bestScore = NearestHostileDistance(best, hostiles);

            for (int i = 1; i < candidates.Count; i++)
            {
                float score = NearestHostileDistance(candidates[i], hostiles);
                if (score > bestScore) { bestScore = score; best = candidates[i]; }
            }

            if (bestScore < DeathmatchSpawnSafeRadius)
                DeathmatchLogger.Info($"[TDM] team {team} spawn is contested — best of {candidates.Count} walkable point(s) has a hostile {bestScore:F0}u away (wanted {DeathmatchSpawnSafeRadius:F0}u); spawn protection covers it");

            return best;
        }

        private static float NearestHostileDistance(Vector3 pos, List<Vector3> hostiles)
        {
            float nearest = float.MaxValue;
            foreach (Vector3 h in hostiles)
            {
                float d = Vector3.Distance2D(pos, h);
                if (d < nearest) nearest = d;
            }
            return nearest;
        }

        /// <summary>
        /// Brief untouchable-and-inert window on spawn.
        ///
        /// Invulnerable is the real damage gate — WorldEntity.cs:2205 zeroes
        /// healthDelta for any hostile result while it is set — and Combat.cs:117
        /// also drops the entity out of CheckInvulnerable target selection, so AI
        /// stops swinging at it. PowerLock rides along so the protection cannot be
        /// used offensively: a protected combatant cannot act either. That pairing
        /// is exactly what the shipped PvP mode uses for its own locked state
        /// (PvPDefenderGameMode.cs:564-566).
        ///
        /// The id must be registered in the deliberately-invincible set or
        /// PhantomSharedMaintenance's stuck-invulnerability watchdog force-clears
        /// both properties within a tick (Avatar.PhantomHero.cs:787-792).
        /// </summary>
        private void ApplyDeathmatchSpawnProtection(Agent phantom)
        {
            if (phantom == null) return;

            var scheduler = Game?.GameEventScheduler;
            if (scheduler == null) return;

            try
            {
                phantom.Properties[PropertyEnum.Invulnerable] = true;
                phantom.Properties[PropertyEnum.PowerLock] = true;

                // Timed, not permanent — the watchdog lifts it if the event below
                // never fires. A small grace past the scheduled lift keeps the two
                // from racing on the same tick.
                long untilMs = (Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond) + DeathmatchSpawnProtectionMs + 500;
                Avatar.MarkPhantomTimedInvincible(phantom.Id, untilMs);

                EventPointer<DeathmatchSpawnProtectEndEvent> ptr = new();
                _tdmProtectEvents.Add(ptr);
                scheduler.ScheduleEvent(ptr, TimeSpan.FromMilliseconds(DeathmatchSpawnProtectionMs), _deathmatchEvents);
                ptr.Get()?.Initialize(this, phantom.Id);
            }
            catch (Exception ex) { DeathmatchLogger.Warn($"[TDM] spawn protection failed for {phantom.Id}: {ex.Message}"); }
        }

        private void EndDeathmatchSpawnProtection(ulong phantomId)
        {
            _tdmProtectEvents.RemoveAll(p => p.IsValid == false);

            if (Game.EntityManager.GetEntity<Agent>(phantomId) is not Agent phantom) return;

            try
            {
                phantom.Properties[PropertyEnum.Invulnerable] = false;
                phantom.Properties[PropertyEnum.PowerLock] = false;
                Avatar.ClearPhantomTimedInvincible(phantomId);
            }
            catch (Exception ex) { DeathmatchLogger.Warn($"[TDM] could not lift spawn protection on {phantomId}: {ex.Message}"); }
        }

        private sealed class DeathmatchSpawnProtectEndEvent : CallMethodEventParam1<Player, ulong>
        {
            protected override CallbackDelegate GetCallback() => static (player, id) => player.EndDeathmatchSpawnProtection(id);
        }

        #endregion

        /// <summary>
        /// Puts a phantom's synthetic Player into the MetaGame and its PvPTeam,
        /// so it counts as a real combatant to every shipped PvP system.
        /// </summary>
        private void RegisterDeathmatchPhantomWithTeam(Agent phantom, PvP pvp, int team)
        {
            try
            {
                Player phantomPlayer = phantom.GetOwnerOfType<Player>();
                if (phantomPlayer == null)
                {
                    DeathmatchLogger.Warn($"[TDM] phantom {phantom.Id} has no owning Player — not registered with team {team}");
                    return;
                }

                // The phantom Player is the human's own Player if something went
                // wrong upstream; never put ourselves on an enemy team.
                if (phantomPlayer == this)
                {
                    DeathmatchLogger.Warn($"[TDM] phantom {phantom.Id} resolved to the HUMAN player — not registered with team {team}");
                    return;
                }

                if (pvp.Teams[team] is not PvPTeam pvpTeam)
                {
                    DeathmatchLogger.Warn($"[TDM] team {team} is not a PvPTeam — phantom {phantom.Id} not registered");
                    return;
                }

                // Hard cap, independent of the release-on-death path below. A
                // bloated team is what reached the client as an oversized PvP
                // roster and score table, so this must be impossible even if a
                // release is ever missed. The human occupies one slot on their
                // own team, and the log confirms they are already counted
                // ("team size now 2" for the first ally phantom), so the same
                // limit applies to every team.
                if (pvpTeam.TeamSize >= _tdmTeamSize)
                {
                    DeathmatchLogger.Warn($"[TDM] team {team} already at {pvpTeam.TeamSize}/{_tdmTeamSize} Players — phantom {phantom.Id} NOT added (a release was missed)");
                    return;
                }

                pvp.AddPlayer(phantomPlayer);

                // AddPlayer here also calls SetAllianceOverride on the phantom's
                // Player. The entity-level AllianceOverride set above is kept as
                // well — it is what has been verified working all along, and the
                // read-back check above is the proof. Belt and braces.
                bool added = pvpTeam.AddPlayer(phantomPlayer);
                _tdmPhantomPlayers[phantom.Id] = phantomPlayer.Id;

                DeathmatchLogger.Info($"[TDM] team {team} phantom {phantom.Id} registered as Player {phantomPlayer.Id} \"{phantomPlayer.GetName()}\" (teamAdd={added}, team size now {pvpTeam.TeamSize})");
            }
            catch (Exception ex)
            {
                DeathmatchLogger.Warn($"[TDM] phantom team registration threw: {ex.Message}");
            }
        }

        /// <summary>Phantom avatar id -> its synthetic Player's entity id, for scoreboard and banner lookups.</summary>
        private readonly Dictionary<ulong, ulong> _tdmPhantomPlayers = new();

        /// <summary>
        /// Removes one phantom's synthetic Player from its PvPTeam. Called the
        /// moment that phantom dies, because the replacement is a whole new
        /// Player entity rather than a revival of this one.
        /// </summary>
        private void ReleaseDeathmatchPhantomPlayer(ulong phantomAvatarId)
        {
            if (_tdmPhantomPlayers.TryGetValue(phantomAvatarId, out ulong playerId) == false) return;
            _tdmPhantomPlayers.Remove(phantomAvatarId);

            try
            {
                if (_deathmatchMetaGameId == 0) return;
                if (Game.EntityManager.GetEntity<PvP>(_deathmatchMetaGameId) is not PvP pvp) return;
                if (Game.EntityManager.GetEntity<Player>(playerId) is not Player phantomPlayer) return;

                // MetaGame.RemovePlayer walks every team and removes on the
                // first match, and PvPTeam.RemovePlayer clears the alliance
                // override on the way out.
                bool removed = pvp.RemovePlayer(phantomPlayer);
                DeathmatchLogger.Info($"[TDM] released phantom Player {playerId} (avatar {phantomAvatarId}) from its team — removed={removed}");
            }
            catch (Exception ex)
            {
                DeathmatchLogger.Warn($"[TDM] phantom Player release threw: {ex.Message}");
            }
        }

        /// <summary>
        /// Credits a kill to the killer's TEAM. Returns true if the match ended.
        /// Both duo members feed one shared pool, which is the whole point of the
        /// format — 50 shared, not 50 each.
        /// </summary>
        private bool CreditDeathmatchTeamKill(ulong killerId, ulong victimId)
        {
            if (_tdmActive == false) return false;

            // Every rejection is logged. Kills silently not counting was the
            // single hardest thing to diagnose in this mode — "no score" looks
            // identical whether the event never fired, the victim was untracked,
            // or the killer could not be resolved.
            if (_tdmCombatantTeam.TryGetValue(victimId, out int victimTeam) == false)
            {
                // Almost always a summon/pet dying rather than a combatant, so
                // this is normal — not logged, it drowns the log at 10 combatants.
                return false;
            }

            if (_tdmCombatantTeam.TryGetValue(killerId, out int killerTeam) == false)
            {
                // The killer is very often a SUMMON (turret, pet, missile owner)
                // rather than the combatant itself, so a straight roster lookup
                // misses and the kill scores for nobody. Walk up to whoever is
                // actually responsible — the same resolver the game uses for its
                // own kill credit.
                ulong resolved = 0;
                if (Game.EntityManager.GetEntity<WorldEntity>(killerId) is WorldEntity killerEntity)
                    resolved = killerEntity.GetMostResponsiblePowerUser<Avatar>()?.Id ?? 0;

                if (resolved == 0 || _tdmCombatantTeam.TryGetValue(resolved, out killerTeam) == false)
                {
                    // Identify WHAT the killer is. "not tracked" alone cannot
                    // distinguish a hazard from a summon from an untracked
                    // combatant, and that ambiguity has cost several test runs.
                    string what = "missing-from-world";
                    if (Game.EntityManager.GetEntity<WorldEntity>(killerId) is WorldEntity ke)
                    {
                        string owner = ke.GetOwnerOfType<Player>()?.GetName() ?? "<no player owner>";
                        what = $"{ke.GetType().Name}/{ke.PrototypeDataRef.GetNameFormatted()} alliance={ke.Alliance?.DataRef.GetNameFormatted() ?? "null"} owner={owner} powerUserOverride={ke.PowerUserOverrideId}";
                    }
                    DeathmatchLogger.Info($"[TDM:KILL] killer {killerId} untracked (owner {resolved}) killed team {victimTeam} — killer is {what}");
                    return false;
                }

                DeathmatchLogger.Info($"[TDM:KILL] killer {killerId} is a summon of {resolved} (team {killerTeam})");
            }

            if (killerTeam == victimTeam)
            {
                DeathmatchLogger.Info($"[TDM:KILL] team kill on team {killerTeam} — no credit");
                return false;
            }

            _tdmTeamScores[killerTeam]++;
            AnnounceDeathmatchKill(killerId, victimId, killerTeam, victimTeam);
            DeathmatchLogger.Info($"[TDM] team {killerTeam} scores — {string.Join(" / ", _tdmTeamScores)} (target {_tdmKillTarget})");

            // HUD first — your score on the fraction widget, rivals on the text
            // widget. Chat carries the same information as a persistent backup,
            // and is the only place the third team is guaranteed to appear if the
            // text widget's second slot turns out not to render.
            UpdateDeathmatchTeamWidget();
            SendDeathmatchScoreboard();

            if (killerTeam == _tdmMyTeam) PushDeathmatchScoreToTable();

            if (_tdmTeamScores[killerTeam] >= _tdmKillTarget)
            {
                bool won = killerTeam == _tdmMyTeam;
                string verdict = won
                    ? $"VICTORY — {_tdmTeamScores[killerTeam]} to {Others(killerTeam)}"
                    : $"DEFEAT — {DeathmatchTeamName(killerTeam)} wins, your team scored {_tdmTeamScores[_tdmMyTeam]}";

                // On-screen banner, not just chat. SendBannerLines only writes
                // to the Metagame chat channel — that is why the previous build
                // showed nothing centre-screen. NetMessageBannerMessage is the
                // real banner, and it takes a LocaleStringId, so the words come
                // from AchievementStringMap_95_Deathmatch.json (same custom
                // string-table push the Cloak dialog uses). The score cannot go
                // in the banner for that reason — it stays in chat below.
                try
                {
                    SendBannerMessage(
                        (LocaleStringId)(won ? DmStrVictory : DmStrDefeat),
                        won ? TextStylePrototype.BannerMessageAlert : TextStylePrototype.BannerMessageError,
                        timeToLiveMS: DeathmatchVerdictBannerMS,
                        messageStyle: BannerMessageStyle.FlyIn,
                        doNotQueue: true,
                        showImmediately: true);
                }
                catch (Exception ex) { DeathmatchLogger.Warn($"[TDM] verdict banner failed: {ex.Message}"); }

                SendBannerLines(verdict);

                // Feeds the next match's rubber band (see Player.DeathmatchPolish.cs).
                RecordDeathmatchResult(won);

                // The match's ONLY ground loot, dropped before EndDeathmatch
                // tears the arena down (which destroys every remaining
                // combatant, including the body we want to drop on).
                //
                // Winners only. Losing paid out exactly the same burst, which
                // made the match result meaningless as a reward gate.
                if (won)
                    SpawnDeathmatchFinaleLoot(victimId);
                else
                    DeathmatchLogger.Info("[TDM] no finale loot — match lost");

                EndDeathmatch(verdict, returnHome: true);
                return true;
            }

            // Score lives in the HUD fraction widget, not chat. SetModeText and
            // UIWidgetMissionText both take a LocaleStringId, so neither can show
            // a live number — the fraction widget is the only one that takes ints.
            UpdateDeathmatchTeamWidget();
            return false;
        }

        // The original PvP kill banner strings. These live on
        // PvPDefenderGameModePrototype, so a non-defender mode cannot reach them
        // through the prototype — but the LocaleStringIds themselves are just
        // numbers, and NetMessageMetaGameBanner takes a raw id, so they are
        // usable directly. Dumped from MainTier5 with !dmdata modes:
        //
        //   0xB65F787124AD0506  "You defeated $playertarget$"
        //   0x98A950F724BF0508  "$playersource$ defeated you"
        //   0xC54501FB24E3050C  "$playersource$ defeated $playertarget$"
        //
        // $playersource$ binds to playerName1 and $playertarget$ to playerName2 —
        // confirmed by the shipped call site, PvPDefenderGameMode.cs:406-417,
        // which passes (attacker.GetName(), defender.GetName()) in that order to
        // all three variants.
        private const ulong DmBannerYouDefeated = 0xB65F787124AD0506;
        private const ulong DmBannerDefeatedYou = 0x98A950F724BF0508;
        private const ulong DmBannerOtherDefeat = 0xC54501FB24E3050C;

        /// <summary>
        /// "X defeated Y" centre-screen, using the game's own PvP banner strings
        /// and its own name substitution — not a chat line.
        /// </summary>
        private void AnnounceDeathmatchKill(ulong killerId, ulong victimId, int killerTeam, int victimTeam)
        {
            try
            {
                string killer = DescribeDeathmatchCombatant(killerId);
                string victim = DescribeDeathmatchCombatant(victimId);
                ulong myAvatarId = CurrentAvatar?.Id ?? 0;

                ulong bannerText = victimId == myAvatarId ? DmBannerDefeatedYou
                                 : killerId == myAvatarId ? DmBannerYouDefeated
                                 : DmBannerOtherDefeat;

                SendDeathmatchMetaGameBanner(bannerText, killer, victim);

                // Chat keeps the team colours, which the shipped banner strings
                // have no placeholder for.
                SendBannerLines($"{DeathmatchTeamName(killerTeam)} {killer} defeated {DeathmatchTeamName(victimTeam)} {victim}");

                AnnounceDeathmatchKillVO(killerId, victimId, killerTeam, victimTeam);
            }
            catch (Exception ex) { DeathmatchLogger.Warn($"[TDM] kill announce failed: {ex.Message}"); }
        }

        /// <summary>
        /// Sends NetMessageMetaGameBanner to this player. MetaGameMode has its own
        /// SendMetaGameBanner but it is protected and needs a mode instance with
        /// the right prototype fields, which our mode does not have.
        /// </summary>
        private void SendDeathmatchMetaGameBanner(ulong bannerTextId, string playerName1, string playerName2, params long[] intArgs)
        {
            if (PlayerConnection == null) return;

            var message = NetMessageMetaGameBanner.CreateBuilder()
                .SetMessageStringId(bannerTextId)
                .SetPlayerName1(playerName1 ?? string.Empty)
                .SetPlayerName2(playerName2 ?? string.Empty);

            if (intArgs != null && intArgs.Length > 0)
                message.AddRangeIntArgs(intArgs);

            SendMessage(message.Build());
        }

        // Voice-over assets, read off the metagame prototype rather than
        // hardcoded, so PatchDataDeathmatch.json stays the single source of
        // truth. PvPTrainingRoom shipped with all of these unset; the patch file
        // copies DefenderPvPTier5's values in.
        //
        // The killing-spree VO is NOT sent here — the shipped
        // PvPScoreEventHandler already does it (MetaGameEventHandler.cs:98-106),
        // and now that phantoms are registered Players that handler actually
        // runs. Sending it here as well would double up.
        private void AnnounceDeathmatchKillVO(ulong killerId, ulong victimId, int killerTeam, int victimTeam)
        {
            if (_deathmatchMetaGameId == 0) return;
            if (Game.EntityManager.GetEntity<PvP>(_deathmatchMetaGameId) is not PvP pvp) return;

            var pvpProto = pvp.PvPPrototype;
            var mode = pvp.CurrentMode;
            if (pvpProto == null || mode == null) return;

            ulong myAvatarId = CurrentAvatar?.Id ?? 0;
            AssetId vo = AssetId.Invalid;

            if (killerId == myAvatarId)
            {
                // Revenge beats first-blood if both apply — it is the rarer event.
                if (_tdmLastKilledMe != 0 && _tdmLastKilledMe == victimId)
                    vo = pvpProto.VORevenge;
                else if (_tdmFirstKillAnnounced == false)
                    vo = pvpProto.VOFirstKill;
            }
            else if (victimTeam == _tdmMyTeam && victimId != myAvatarId)
            {
                vo = pvpProto.VOTeammateKilled;
            }

            if (victimId == myAvatarId) _tdmLastKilledMe = killerId;
            if (_tdmFirstKillAnnounced == false && killerId == myAvatarId) _tdmFirstKillAnnounced = true;

            if (vo != AssetId.Invalid)
                mode.SendPlayUISoundTheme(vo, this);
        }

        /// <summary>Who killed this player last, for the revenge VO.</summary>
        private ulong _tdmLastKilledMe;
        private bool _tdmFirstKillAnnounced;

        /// <summary>"5v5" for two teams, "5v5v5" for three.</summary>
        internal string DescribeDeathmatchBracket()
            => string.Join("v", System.Linq.Enumerable.Repeat(_tdmTeamSize.ToString(), _tdmTeamCount));

        /// <summary>Drives the HUD counter with this player's TEAM score toward the target.</summary>
        // HUD scoreboard string table — AchievementStringMap_97_DeathmatchScores.json
        // bakes "RED 0".."BLUE 60" as one LocaleStringId per (team, score) pair.
        //
        // This is the only way to get a live number into a text widget: SetText
        // takes LocaleStringIds, not free text, so there is no format template.
        // Same technique the Endless HUD uses for "Wave 1".."Wave 300"
        // (Player.WaveDirector.cs:1948-1955), and the reason the table is bounded
        // at 60 — the largest kill target the menu offers is 30.
        private const ulong DmScoreStringBase = 9910000001000000;
        private const int DmScoreStringMax = 60;

        /// <summary>
        /// UI/MetaGame/MissionName.prototype — the ONLY UIWidgetMissionText slot
        /// that renders a pushed string standalone. Confirmed live 2026-07-26: the
        /// other six (ObjectiveNameLeft/Right/Center/LeftB/LeftC, MissionObjectiveName)
        /// rendered raw "$MissionObjectiveName$" placeholders stacked on each other,
        /// which is why Player.WaveDirector's EndlessHud widgets are disabled.
        /// </summary>
        private const ulong DmScoreTextWidgetRef = 0x636EA5AADD1D0D53;

        private static LocaleStringId DmScoreString(int team, int score)
        {
            if (team < 0 || team > 2) return LocaleStringId.Blank;
            int clamped = Math.Clamp(score, 0, DmScoreStringMax);
            return (LocaleStringId)(DmScoreStringBase + (ulong)(team * 1000) + (ulong)clamped);
        }

        private void UpdateDeathmatchTeamWidget()
        {
            if (_tdmMyTeam < 0) return;
            try
            {
                // Your own team stays on the fraction widget — it is the only
                // widget that takes live ints, and score-against-target is exactly
                // the shape it was built for.
                PrototypeId widgetRef = GetEndlessWaveWidgetRef();
                if (widgetRef != PrototypeId.Invalid)
                {
                    CurrentAvatar?.Region?.UIDataProvider?.GetWidget<UIWidgetGenericFraction>(widgetRef)
                        ?.SetCount(_tdmTeamScores[_tdmMyTeam], _tdmKillTarget);
                }

                // The rival teams go in the text widget's two slots, highest first,
                // so the team closest to winning is always the one you see.
                //
                // NOTE: slot 2 (missionObjectiveName) is UNPROVEN — the Endless HUD
                // only ever passes LocaleStringId.Blank there, so nothing in this
                // fork has established whether the client renders it. If it does
                // not, slot 1 still carries the leading rival, which is the piece
                // that matters; the third team simply stays chat-only.
                var rivals = new List<int>(2);
                for (int i = 0; i < _tdmTeamCount; i++)
                    if (i != _tdmMyTeam) rivals.Add(i);
                rivals.Sort((a, b) => _tdmTeamScores[b].CompareTo(_tdmTeamScores[a]));

                LocaleStringId line1 = rivals.Count > 0 ? DmScoreString(rivals[0], _tdmTeamScores[rivals[0]]) : LocaleStringId.Blank;
                LocaleStringId line2 = rivals.Count > 1 ? DmScoreString(rivals[1], _tdmTeamScores[rivals[1]]) : LocaleStringId.Blank;

                CurrentAvatar?.Region?.UIDataProvider?.GetWidget<UIWidgetMissionText>((PrototypeId)DmScoreTextWidgetRef)
                    ?.SetText(line1, line2);
            }
            catch (Exception ex) { DeathmatchLogger.Warn($"[TDM] score widget failed: {ex.Message}"); }
        }

        /// <summary>
        /// Chat scoreboard: every team's score, the player's own marked, leader
        /// flagged. Only the teams actually in this match are listed — a 5v5 runs
        /// two teams, so printing a third would invent a side that does not exist.
        /// </summary>
        private void SendDeathmatchScoreboard()
        {
            try
            {
                int best = -1;
                for (int i = 0; i < _tdmTeamCount; i++)
                    if (_tdmTeamScores[i] > best) best = _tdmTeamScores[i];

                // A tie for the lead is not a lead — do not crown anyone.
                int leaders = 0;
                for (int i = 0; i < _tdmTeamCount; i++)
                    if (_tdmTeamScores[i] == best) leaders++;

                var parts = new List<string>(_tdmTeamCount);
                for (int i = 0; i < _tdmTeamCount; i++)
                {
                    string mark = i == _tdmMyTeam ? " (you)" : "";
                    string lead = (leaders == 1 && _tdmTeamScores[i] == best && best > 0) ? " <" : "";
                    parts.Add($"{DeathmatchTeamName(i)} {_tdmTeamScores[i]}{mark}{lead}");
                }

                SendBannerLines($"{string.Join("   |   ", parts)}   —  first to {_tdmKillTarget}");
            }
            catch (Exception ex) { DeathmatchLogger.Warn($"[TDM] scoreboard failed: {ex.Message}"); }
        }

        private static string DeathmatchTeamName(int team) => team switch
        {
            0 => "RED",
            1 => "WHITE",
            2 => "BLUE",
            _ => "?",
        };

        /// <summary>Readable name for a combatant — the player's own name, or the phantom's hero.</summary>
        private string DescribeDeathmatchCombatant(ulong entityId)
        {
            if (entityId == CurrentAvatar?.Id) return GetName();

            if (Game.EntityManager.GetEntity<Agent>(entityId) is Agent agent)
            {
                string leaf = agent.PrototypeDataRef.GetNameFormatted();
                int slash = leaf.LastIndexOf('/');
                if (slash >= 0 && slash + 1 < leaf.Length) leaf = leaf[(slash + 1)..];
                return leaf;
            }
            return "someone";
        }

        private string Others(int team)
        {
            var parts = new List<string>();
            for (int i = 0; i < _tdmTeamCount; i++)
                if (i != team) parts.Add(_tdmTeamScores[i].ToString());
            return string.Join("-", parts);
        }

        /// <summary>
        /// Replaces a fallen combatant on the same team, keeping each duo at two.
        ///
        /// The fallen one must be DESTROYED first, not merely forgotten. A downed
        /// phantom is still in the world and still revivable — so without this the
        /// roster grew without bound (a fresh replacement each death while the
        /// corpse lingered), and the friendly revive logic would bring downed
        /// bodies back and pull them into the player's party. That is the
        /// "2v2v2 turned into 10v-whatever, and revived phantoms joined my party"
        /// behaviour.
        /// </summary>
        private void RespawnDeathmatchCombatant(ulong deadId)
        {
            if (_tdmActive == false) return;
            if (_tdmCombatantTeam.TryGetValue(deadId, out int team) == false) return;
            if (deadId == CurrentAvatar?.Id) return;   // the player uses the normal death/release flow

            _tdmCombatantTeam.Remove(deadId);
            _deathmatchSuppressedPhantomIds.Remove(deadId);
            int slotIndex = _tdmCombatantHero.TryGetValue(deadId, out ulong slot) ? (int)slot : -1;
            _tdmCombatantHero.Remove(deadId);

            // Take the dead phantom's synthetic Player off its PvPTeam. Each
            // respawn builds a BRAND NEW Player entity, so without this the team
            // accumulates one dead member per death — observed live 2026-08-04:
            // a 5-man team logged "team size now 6 / 7 / 8" within seconds, and
            // every one of those Players also held a ScoreTable row.
            ReleaseDeathmatchPhantomPlayer(deadId);

            // Remove the body so it cannot be revived back into the match.
            if (Game.EntityManager.GetEntity<Agent>(deadId) is Agent fallen)
            {
                try { fallen.Destroy(); }
                catch (Exception ex) { DeathmatchLogger.Warn($"[TDM] could not destroy fallen {deadId}: {ex.Message}"); }
            }

            // Replacement arrives on a timer, not in the same tick as the kill.
            // An instant replacement is the core of the spawn-camping exploit:
            // the camper never leaves the anchor and the arena feeds them.
            var scheduler = Game?.GameEventScheduler;
            if (scheduler == null) return;

            EventPointer<DeathmatchTeamRespawnEvent> ptr = new();
            _tdmRespawnEvents.Add(ptr);
            scheduler.ScheduleEvent(ptr, TimeSpan.FromMilliseconds(DeathmatchTeamRespawnDelayMs), _deathmatchEvents);
            ptr.Get()?.Initialize(this, team, slotIndex);
        }

        /// <summary>Fires DeathmatchTeamRespawnDelayMs after a combatant fell.</summary>
        private void DoDeathmatchTeamRespawn(int team, int slotIndex)
        {
            _tdmRespawnEvents.RemoveAll(p => p.IsValid == false);

            if (_tdmActive == false) return;

            var pvp = GetDeathmatchMetaGame();
            if (pvp != null) SpawnDeathmatchTeammate(CurrentAvatar, pvp, team, slotIndex);
        }

        private sealed class DeathmatchTeamRespawnEvent : CallMethodEventParam2<Player, int, int>
        {
            protected override CallbackDelegate GetCallback() => static (player, team, slot) => player.DoDeathmatchTeamRespawn(team, slot);
        }

        private void ClearDeathmatchTeams()
        {
            // Remove every combatant, or they survive the match and follow the
            // player out of the arena.
            foreach (ulong id in _tdmCombatantTeam.Keys)
            {
                if (id == CurrentAvatar?.Id) continue;

                // Teardown cancels _deathmatchEvents, so any spawn-protection
                // lift still pending will never fire. Drop the watchdog exemption
                // here or the id leaks into a static set for the process lifetime.
                Avatar.ClearPhantomTimedInvincible(id);

                if (Game.EntityManager.GetEntity<Agent>(id) is Agent combatant)
                {
                    try { combatant.Destroy(); }
                    catch { /* best effort during teardown */ }
                }
            }

            _tdmRespawnEvents.Clear();
            _tdmProtectEvents.Clear();

            // Drop any queued phantom-restore intents. Friendly phantoms are
            // restored from MigrationData on the next region entry, so without
            // this the player's TDM team follows them out of the arena — which
            // is exactly what happens on a bodyslide.
            var mig = PlayerConnection?.MigrationData;
            if (mig != null && mig.PhantomIntents != null)
            {
                int dropped = mig.PhantomIntents.Count;
                if (dropped > 0)
                {
                    mig.PhantomIntents.Clear();
                    DeathmatchLogger.Info($"[TDM] dropped {dropped} phantom restore intent(s) so the squad does not follow out");
                }
            }

            // Take the phantom Players back off their PvPTeams before the
            // MetaGame is destroyed, so RemovePlayer can clear each one's
            // alliance override the same way it does for a human leaving.
            if (_tdmPhantomPlayers.Count > 0)
            {
                if (_deathmatchMetaGameId != 0
                    && Game.EntityManager.GetEntity<PvP>(_deathmatchMetaGameId) is PvP pvp)
                {
                    foreach (ulong playerId in _tdmPhantomPlayers.Values)
                    {
                        if (Game.EntityManager.GetEntity<Player>(playerId) is not Player phantomPlayer) continue;
                        try { pvp.RemovePlayer(phantomPlayer); }
                        catch (Exception ex) { DeathmatchLogger.Warn($"[TDM] phantom Player {playerId} removal threw: {ex.Message}"); }
                    }
                }
                DeathmatchLogger.Info($"[TDM] released {_tdmPhantomPlayers.Count} phantom Player(s) from their teams");
                _tdmPhantomPlayers.Clear();
            }

            _tdmActive = false;
            _tdmMyTeam = -1;
            _tdmLastKilledMe = 0;
            _tdmFirstKillAnnounced = false;
            Array.Clear(_tdmTeamScores);
            _tdmCombatantTeam.Clear();
            _tdmCombatantHero.Clear();
            _tdmSlotHero.Clear();
            ClearDeathmatchTeamRosters();
        }
    }
}
