using System;
using System.Collections.Generic;
using Gazillion;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.Properties;
using MHServerEmu.Games.Regions;

namespace MHServerEmu.Games.Entities
{
    // Phantom-hero ownership lives on the human Player, not on their current
    // Avatar. Reason: when the player changes Avatar (hero swap) or crosses
    // region boundaries, the old Avatar shell is torn down. If the phantom
    // list were on the Avatar it would vanish with the shell, orphaning the
    // phantom entities in the world with no way to reach them from `!phantom
    // clear` — the exact symptom users reported ("only server restart
    // despawns them"). Anchoring to the Player keeps the list stable across
    // Avatar swaps and region hops so cleanup + tick reattachment always
    // finds them.
    public partial class Player
    {
        private static readonly Logger PhantomHostLogger = LogManager.CreateLogger();

        // Three parallel lists indexed together:
        //   _phantomAvatarIds[i]  = runtime Avatar entity id of the phantom
        //   _phantomPlayerIds[i]  = runtime Player entity id owning that Avatar
        //   _phantomDescriptors[i] = respawn recipe (avatar ref, level, name)
        // The descriptor is what we serialize to MigrationData when the human
        // crosses a region boundary — the runtime IDs are useless on the other
        // side because that Game instance is new.
        private readonly List<ulong> _phantomAvatarIds = new();
        private readonly List<ulong> _phantomPlayerIds = new();
        private readonly List<PhantomIntent> _phantomDescriptors = new();

        // Keyed by phantom runtime id, value is the DbGuid the client's
        // party UI currently has for that member. Used by SyncPhantomParty
        // to emit an explicit PartyMemberInfoClientUpdate/ePME_Remove when
        // a phantom drops off the roster — without this the client keeps
        // the removed member visible with stale/zero HP forever, and
        // subsequent spawn/clear cycles accumulate ghost members past the
        // client's 5-member cap. Adapted from lordunborn's fork
        // (github.com/lordunborn/MHServerEmu, commit 79514463).
        private readonly Dictionary<ulong, ulong> _syncedPhantomMemberDbIds = new();

#if !GAME_VERSION_1_52 && !GAME_VERSION_1_53
        // 1.48-only: whether we've already sent ClientCreateGroup for the
        // current phantom-party "session" (reset on full teardown so a later
        // respawn creates the group again). See SyncPhantomParty's 1.48 branch.
        private bool _hasSyncedPhantomGroup48;
#endif

        public IReadOnlyList<ulong> PhantomAvatarIds => _phantomAvatarIds;
        public IReadOnlyList<ulong> PhantomPlayerIds => _phantomPlayerIds;
        public int PhantomHeroCount => _phantomAvatarIds.Count;

        // Effective party/raid cap (including the human's own slot) in the
        // player's current region — see Avatar.GetPhantomPartyCap. int.MaxValue
        // means uncapped (Town/PublicCombatZone/MatchPlay, or no avatar in world).
        public int PhantomPartyCap => Avatar.GetPhantomPartyCap(CurrentAvatar?.Region);

        // ================================================================
        //  Cross-Area phantom relocation retry (see Player.OnCellLoaded in
        //  Player.cs and Avatar.BringPhantomsToPosition). A phantom moved
        //  via ChangeRegionPosition into an Area/Cell the client hasn't
        //  finished loading yet silently fails its AOI proximity check and
        //  is never retried on its own — record the last attempted target
        //  here so OnCellLoaded can retry once the destination cell is
        //  actually confirmed loaded.
        // ================================================================
        private Vector3? _lastPhantomRelocationPos;
        private Region _lastPhantomRelocationRegion;

        internal void RecordPhantomRelocationTarget(Vector3 pos, Region region)
        {
            _lastPhantomRelocationPos = pos;
            _lastPhantomRelocationRegion = region;
        }

        internal void RetryPendingPhantomRelocation()
        {
            if (_lastPhantomRelocationPos == null) return;
            Vector3 pos = _lastPhantomRelocationPos.Value;
            Region region = _lastPhantomRelocationRegion;
            _lastPhantomRelocationPos = null;
            _lastPhantomRelocationRegion = null;
            CurrentAvatar?.BringPhantomsToPosition(pos, region);
        }

        /// <summary>
        /// Set on phantom-hero synthetic Players at spawn time; points at the
        /// human Player entity that created them. Non-phantom (real) Players
        /// always report 0. Used by kill-attribution paths to substitute the
        /// synthetic phantom Player with the actual human when awarding
        /// mission credit / loot / XP: a phantom's tag or kill would
        /// otherwise fail IsMissionPlayer checks and the human would get
        /// nothing for the mob their bot cleared.
        /// </summary>
        public ulong PhantomCreatorId { get; internal set; }

        /// <summary>
        /// Returns the human Player who should receive credit for anything
        /// <paramref name="raw"/> did — either <paramref name="raw"/> itself
        /// if it's a real player, or the phantom's creator if this is a
        /// phantom synthetic Player. Null if the creator has already left
        /// the game.
        /// </summary>
        public static Player ResolveCreditPlayer(Player raw)
        {
            if (raw == null) return null;
            ulong creatorId = raw.PhantomCreatorId;
            if (creatorId == 0) return raw;
            Player creator = raw.Game?.EntityManager?.GetEntity<Player>(creatorId);
            return creator ?? raw;
        }

        internal void RegisterPhantom(ulong avatarId, ulong phantomPlayerId, PhantomIntent descriptor)
        {
            _phantomAvatarIds.Add(avatarId);
            _phantomPlayerIds.Add(phantomPlayerId);
            _phantomDescriptors.Add(descriptor);
            SyncPhantomParty();
        }

        /// <summary>
        /// True if the phantom with this avatar id was spawned with an
        /// explicit level lock (`!phantom spawn N L`) and should NOT be
        /// auto-levelled by the tick loop.
        /// </summary>
        internal bool IsPhantomLevelLocked(ulong avatarId)
        {
            int idx = _phantomAvatarIds.IndexOf(avatarId);
            if (idx < 0) return false;
            return _phantomDescriptors[idx].LockLevel;
        }

        /// <summary>
        /// Update the stored spawn-level for a live phantom so a subsequent
        /// cross-region transfer re-spawns it at the caller's current level
        /// rather than the (potentially stale) level at first spawn. Called
        /// by the tick loop's level-sync block when the human has levelled
        /// past the phantom.
        /// </summary>
        internal void UpdatePhantomLevel(ulong avatarId, int newLevel)
        {
            int idx = _phantomAvatarIds.IndexOf(avatarId);
            if (idx < 0) return;
            var d = _phantomDescriptors[idx];
            d.Level = newLevel;
            _phantomDescriptors[idx] = d;
        }

        /// <summary>
        /// Update the stored costume for a live phantom so squad saves and
        /// cross-region transfers reproduce a costume applied after spawn
        /// via the costume command.
        /// </summary>
        internal void UpdatePhantomCostume(ulong avatarId, ulong costumeRef)
        {
            int idx = _phantomAvatarIds.IndexOf(avatarId);
            if (idx < 0) return;
            var d = _phantomDescriptors[idx];
            d.CostumeRef = costumeRef;
            _phantomDescriptors[idx] = d;
        }

        /// <summary>
        /// Update the stored gear list for a live phantom (post-spawn
        /// re-roll via the gear command).
        /// </summary>
        internal void UpdatePhantomGear(ulong avatarId, List<ulong> gearRefs)
        {
            int idx = _phantomAvatarIds.IndexOf(avatarId);
            if (idx < 0) return;
            var d = _phantomDescriptors[idx];
            d.GearRefs = gearRefs;
            _phantomDescriptors[idx] = d;
        }

        internal bool UnregisterPhantom(ulong avatarId)
        {
            int idx = _phantomAvatarIds.IndexOf(avatarId);
            if (idx < 0) return false;
            _phantomAvatarIds.RemoveAt(idx);
            _phantomPlayerIds.RemoveAt(idx);
            _phantomDescriptors.RemoveAt(idx);
            SyncPhantomParty();
            return true;
        }

        /// <summary>
        /// Look up the stored descriptor for a live friendly phantom. Used by
        /// the tick loop's team-up death-respawn hook so it can capture the
        /// (avatarRef, level, username) recipe BEFORE the phantom is unregistered.
        /// Returns default(PhantomIntent) if the id isn't tracked.
        /// </summary>
        internal PhantomIntent GetPhantomDescriptor(ulong avatarId)
        {
            int idx = _phantomAvatarIds.IndexOf(avatarId);
            if (idx < 0) return default;
            return _phantomDescriptors[idx];
        }

        // ---- Team-up death respawn queue ----
        // When a friendly team-up phantom dies, its (descriptor, dueAtMs) is
        // enqueued here. The tick loop drains this list and re-spawns each
        // team-up on its due time — 90s after death, so the fight has time to
        // resolve without the team-up popping back mid-fight.
        internal sealed class TeamUpRespawnEntry
        {
            public PhantomIntent Descriptor;
            public long DueAtMs;
        }
        private readonly List<TeamUpRespawnEntry> _teamUpRespawnQueue = new();

        internal void EnqueueTeamUpRespawn(PhantomIntent descriptor, long dueAtMs)
        {
            _teamUpRespawnQueue.Add(new TeamUpRespawnEntry { Descriptor = descriptor, DueAtMs = dueAtMs });
        }

        /// <summary>
        /// Non-zero if there are pending team-up respawns waiting to fire — the
        /// tick loop uses this to keep itself alive even when every live phantom
        /// has been cleared so the respawn actually happens on schedule.
        /// </summary>
        internal int TeamUpRespawnQueueCount => _teamUpRespawnQueue.Count;

        internal List<TeamUpRespawnEntry> DrainTeamUpRespawnsDue(long nowMs)
        {
            List<TeamUpRespawnEntry> ready = null;
            for (int i = _teamUpRespawnQueue.Count - 1; i >= 0; i--)
            {
                if (_teamUpRespawnQueue[i].DueAtMs <= nowMs)
                {
                    (ready ??= new List<TeamUpRespawnEntry>()).Add(_teamUpRespawnQueue[i]);
                    _teamUpRespawnQueue.RemoveAt(i);
                }
            }
            return ready;
        }

        /// <summary>
        /// Destroys every phantom (avatar + owning phantom-Player) currently
        /// tracked and clears the list. Callable from any Avatar the human is
        /// controlling — `!phantom clear` routes here.
        /// </summary>
        public int PurgePhantoms()
        {
            if (_phantomAvatarIds.Count == 0) return 0;
            var mgr = Game?.EntityManager;
            if (mgr == null) { _phantomAvatarIds.Clear(); _phantomPlayerIds.Clear(); return 0; }

            int removed = 0;
            foreach (ulong avatarId in _phantomAvatarIds)
            {
                try
                {
                    // GetEntity<Agent>, NOT <Avatar> — this is the root cause of
                    // the "second spawn is broken" bug (traced 2026-08-09).
                    //
                    // RegisterPhantom (line ~119) puts EVERY phantom kind into
                    // _phantomAvatarIds: avatar phantoms (Avatar), team-up
                    // phantoms (Agent) and boss phantoms (Agent). But
                    // EntityManager.GetEntity<T> ends in "GetEntity(...) as T",
                    // so asking for <Avatar> returns NULL for a team-up or boss
                    // — the `continue` below then skipped them and they were
                    // NEVER ExitWorld'd or Destroy'd. Immediately after this
                    // loop, _phantomAvatarIds.Clear() dropped the only
                    // reference to them, and the loop below destroyed the
                    // phantom Player that owned them.
                    //
                    // Net effect: every despawn left live, untracked, ownerless
                    // boss/team-up entities behind in the world. Nothing drove
                    // their animation or AI any more (the phantom tick iterates
                    // the now-cleared list), which is exactly the reported
                    // T-pose — and the next spawn then layered fresh phantoms
                    // on top of those orphans. It also explains precisely why a
                    // FIRST spawn into a clean world always looked correct and
                    // only later spawns misbehaved.
                    //
                    // Avatar derives from Agent, so <Agent> covers all three
                    // kinds. Matches how OnPhantomTick was already widened.
                    Agent av = mgr.GetEntity<Agent>(avatarId);
                    if (av == null) continue;

                    // [BossDiag] state before/after teardown, plus whether the
                    // real client still holds AOI interest afterwards.
                    Avatar.BossDiagTeardown(av, Game, "before-exit");
                    if (av.IsInWorld) av.ExitWorld();
                    Avatar.BossDiagTeardown(av, Game, "after-exitworld");
                    av.Destroy();
                    Avatar.BossDiagTeardown(av, Game, "after-destroy");
                    removed++;
                }
                catch (System.Exception ex) { PhantomHostLogger.Warn($"[Phantom] purge avatar 0x{avatarId:X} failed: {ex.Message}"); }
            }
            foreach (ulong phantomPlayerId in _phantomPlayerIds)
            {
                try
                {
                    Player p = mgr.GetEntity<Player>(phantomPlayerId);
                    if (p == null) continue;
                    // Same path DespawnAllPhantomHeroes used — avoid Player.Destroy's
                    // GuildManager / MissionManager teardown that assumes a real
                    // PlayerConnection.
                    if (p.IsInGame) p.ExitGame();
                    p.Destroy();
                }
                catch (System.Exception ex) { PhantomHostLogger.Warn($"[Phantom] purge phantom-player 0x{phantomPlayerId:X} failed: {ex.Message}"); }
            }

            _phantomAvatarIds.Clear();
            _phantomPlayerIds.Clear();
            _phantomDescriptors.Clear();
            SyncPhantomParty();

            // [BossDiag] Scan AFTER the lists are cleared: anything the purge
            // failed to destroy is now untracked and invisible to every other
            // probe, so this is the only place it can be observed.
            Avatar.BossDiagOrphanScan(CurrentAvatar, this, $"after-purge(destroyed={removed})");

            return removed;
        }

        // ================================================================
        //  Enemy phantoms — hostile AI heroes. Deliberately a SEPARATE
        //  registry from the friendly squad:
        //    * never added to the synthetic party HUD
        //    * never migrated across regions (they're an encounter, not a
        //      companion)
        //    * no PhantomCreatorId, so kill/loot credit is never remapped
        //      to the human who spawned them
        // ================================================================

        private readonly List<ulong> _enemyPhantomAvatarIds = new();
        private readonly List<ulong> _enemyPhantomPlayerIds = new();

        public IReadOnlyList<ulong> EnemyPhantomAvatarIds => _enemyPhantomAvatarIds;
        public int EnemyPhantomCount => _enemyPhantomAvatarIds.Count;

        internal void RegisterEnemyPhantom(ulong avatarId, ulong phantomPlayerId)
        {
            _enemyPhantomAvatarIds.Add(avatarId);
            _enemyPhantomPlayerIds.Add(phantomPlayerId);
        }

        internal bool UnregisterEnemyPhantom(ulong avatarId)
        {
            int idx = _enemyPhantomAvatarIds.IndexOf(avatarId);
            if (idx < 0) return false;

            // Destroy the paired synthetic Player too — Unregister is called
            // by the tick's dead-cleanup, which only destroys the Avatar.
            ulong phantomPlayerId = _enemyPhantomPlayerIds[idx];
            _enemyPhantomAvatarIds.RemoveAt(idx);
            _enemyPhantomPlayerIds.RemoveAt(idx);

            try
            {
                Player p = Game?.EntityManager?.GetEntity<Player>(phantomPlayerId);
                if (p != null)
                {
                    if (p.IsInGame) p.ExitGame();
                    p.Destroy();
                }
            }
            catch (System.Exception ex) { PhantomHostLogger.Warn($"[Phantom:Enemy] unregister player 0x{phantomPlayerId:X} failed: {ex.Message}"); }

            return true;
        }

        /// <summary>Destroys exactly one of this player's enemy phantoms, by avatar id.</summary>
        public bool DespawnOneEnemyPhantom(ulong avatarId)
        {
            int idx = _enemyPhantomAvatarIds.IndexOf(avatarId);
            if (idx < 0) return false;

            var mgr = Game?.EntityManager;
            ulong phantomPlayerId = _enemyPhantomPlayerIds[idx];
            _enemyPhantomAvatarIds.RemoveAt(idx);
            _enemyPhantomPlayerIds.RemoveAt(idx);

            try
            {
                // <Agent>, not <Avatar> — same leak as PurgePhantoms above:
                // enemy phantoms can be team-up Agents, which <Avatar> silently
                // returns null for, leaving them alive and untracked.
                Agent av = mgr?.GetEntity<Agent>(avatarId);
                if (av != null)
                {
                    if (av.IsInWorld) av.ExitWorld();
                    av.Destroy();
                }
            }
            catch (System.Exception ex) { PhantomHostLogger.Warn($"[Phantom:Enemy] despawn one avatar 0x{avatarId:X} failed: {ex.Message}"); }

            try
            {
                Player p = mgr?.GetEntity<Player>(phantomPlayerId);
                if (p != null)
                {
                    if (p.IsInGame) p.ExitGame();
                    p.Destroy();
                }
            }
            catch (System.Exception ex) { PhantomHostLogger.Warn($"[Phantom:Enemy] despawn one player 0x{phantomPlayerId:X} failed: {ex.Message}"); }

            return true;
        }

        /// <summary>Destroys every enemy phantom this player has spawned.</summary>
        public int PurgeEnemyPhantoms()
        {
            if (_enemyPhantomAvatarIds.Count == 0) return 0;
            var mgr = Game?.EntityManager;
            if (mgr == null) { _enemyPhantomAvatarIds.Clear(); _enemyPhantomPlayerIds.Clear(); return 0; }

            int removed = 0;
            foreach (ulong avatarId in _enemyPhantomAvatarIds)
            {
                try
                {
                    // <Agent>, not <Avatar> — same leak as PurgePhantoms above.
                    Agent av = mgr.GetEntity<Agent>(avatarId);
                    if (av == null) continue;
                    if (av.IsInWorld) av.ExitWorld();
                    av.Destroy();
                    removed++;
                }
                catch (System.Exception ex) { PhantomHostLogger.Warn($"[Phantom:Enemy] purge avatar 0x{avatarId:X} failed: {ex.Message}"); }
            }
            foreach (ulong phantomPlayerId in _enemyPhantomPlayerIds)
            {
                try
                {
                    Player p = mgr.GetEntity<Player>(phantomPlayerId);
                    if (p == null) continue;
                    if (p.IsInGame) p.ExitGame();
                    p.Destroy();
                }
                catch (System.Exception ex) { PhantomHostLogger.Warn($"[Phantom:Enemy] purge player 0x{phantomPlayerId:X} failed: {ex.Message}"); }
            }

            _enemyPhantomAvatarIds.Clear();
            _enemyPhantomPlayerIds.Clear();
            return removed;
        }

        /// <summary>
        /// Copy current phantom recipes into MigrationData so the human's
        /// next Game instance (after the region transfer completes) can
        /// respawn them via RestorePhantomsFromMigration. THEN purge the
        /// live entities — the old Game instance is going away anyway and
        /// leaving them alive would break the reattach heuristic in the
        /// new region.
        /// </summary>
        internal void SnapshotPhantomsForTransfer()
        {
            // Enemy phantoms never migrate — they die with the region.
            PurgeEnemyPhantoms();

            var mig = PlayerConnection?.MigrationData;

            // Rogue Encounter is a per-player setting, not a per-phantom
            // one — persist it whether or not there are phantoms to snapshot
            // so the toggle survives every region hop.
            if (mig != null)
            {
                mig.RogueEncounterEnabled = _rogueEncounterEnabled;
                SnapshotNemesesForTransfer(mig);
                SnapshotBountyBoardForTransfer(mig);
                SnapshotPreferredPowersForTransfer(mig);
                SnapshotCombatRangePrefsForTransfer(mig);
            }

            if (_phantomDescriptors.Count == 0) return;
            if (mig == null)
            {
                // No migration bus — treat as ExitGame-style cleanup.
                PurgePhantomsOnExitGame();
                return;
            }
            mig.PhantomIntents.Clear();
            foreach (var d in _phantomDescriptors)
            {
                mig.PhantomIntents.Add(new PhantomIntent
                {
                    AvatarRef = d.AvatarRef,
                    Level = d.Level,
                    Username = d.Username,
                    LockLevel = d.LockLevel,
                    CostumeRef = d.CostumeRef,
                    GearRefs = d.GearRefs != null ? new List<ulong>(d.GearRefs) : null,
                    Invincible = d.Invincible,
                    BypassCap = d.BypassCap,
                });
            }
            int n = PurgePhantoms();
            PhantomHostLogger.Info($"[Phantom] snapshot for transfer: {mig.PhantomIntents.Count} intent(s), purged {n} live phantom(s)");
        }

        /// <summary>
        /// Read MigrationData.PhantomIntents (populated by the previous
        /// Game's SnapshotPhantomsForTransfer) and respawn each phantom in
        /// the current region via the caller Avatar. Called from
        /// Avatar.OnEnteredWorld after the reattach step.
        /// </summary>
        internal int RestorePhantomsFromMigration(Avatar caller)
        {
            var mig = PlayerConnection?.MigrationData;
            if (mig == null || caller == null) return 0;

            // Rogue Encounter opt-in survives region hops. The setter
            // reschedules the tick automatically when flipped on.
            if (mig.RogueEncounterEnabled && RogueEncounterEnabled == false)
                RogueEncounterEnabled = true;

            RestoreNemesesFromMigration(mig);
            RestoreBountyBoardFromMigration(mig);
            RestorePreferredPowersFromMigration(mig);
            RestoreCombatRangePrefsFromMigration(mig);

            if (mig.PhantomIntents.Count == 0) return 0;

            // Trial of the Impossible is solo-only. StartTrialGauntlet already
            // purges any live phantoms on entry (Player.TrialOfImpossible.cs),
            // but this method runs afterward from Avatar.OnEnteredWorld and
            // would otherwise immediately respawn the same intents from
            // MigrationData, undoing the purge. Drop the queued intents
            // instead of retrying them mid-trial.
            if (IsTrialGauntletActive)
            {
                mig.PhantomIntents.Clear();
                return 0;
            }

            int spawned = 0;
            // Confirmed live 2026-07-26 — this used to Clear() the whole
            // list unconditionally after the loop, regardless of whether
            // each intent actually succeeded. A phantom that failed to
            // restore (e.g. rejected by the destination region's cap) had
            // its intent thrown away right here — silently and permanently
            // "kicked from the party," not just left behind for that one
            // region. Only remove intents that actually succeeded; a failed
            // one stays queued and gets retried on the next region
            // transfer/restore instead of vanishing forever.
            var failedIntents = new List<PhantomIntent>();
            foreach (var intent in mig.PhantomIntents)
            {
                try
                {
                    // Force the caller to spawn each intent with its saved
                    // (avatarRef, level, username) rather than the default
                    // "random from deck / caller's level" path. BypassCap
                    // rides along too — otherwise a squad spawned over the
                    // party/raid cap gets silently truncated back down the
                    // moment it's re-spawned fresh in a new region instance
                    // (e.g. entering a terminal).
                    ulong id = caller.SpawnPhantomHeroFromIntent((PrototypeId)intent.AvatarRef, intent.Level, intent.Username, intent.LockLevel, intent.CostumeRef, out string error, intent.GearRefs, intent.Invincible, intent.BypassCap);
                    if (id != 0) spawned++;
                    else
                    {
                        failedIntents.Add(intent);
                        PhantomHostLogger.Warn($"[Phantom] restore intent {intent.Username} failed (kept queued for retry): {error}");
                    }
                }
                catch (System.Exception ex)
                {
                    // Full stack trace — the previous "threw: NRE" one-liner
                    // swallowed the location that would tell us which line
                    // in SpawnPhantomHeroCore failed.
                    failedIntents.Add(intent);
                    PhantomHostLogger.Warn($"[Phantom] restore intent {intent.Username} threw ({ex.GetType().Name}) (kept queued for retry): {ex.Message}\n" +
                        $"  avatarRef=0x{intent.AvatarRef:X} level={intent.Level} costumeRef=0x{intent.CostumeRef:X} " +
                        $"gearCount={(intent.GearRefs?.Count ?? 0)} invincible={intent.Invincible} lockLevel={intent.LockLevel}\n" +
                        $"{ex.StackTrace}");
                }
            }
            mig.PhantomIntents.Clear();
            mig.PhantomIntents.AddRange(failedIntents);
            PhantomHostLogger.Info($"[Phantom] restore from migration: {spawned} phantom(s) re-spawned, {failedIntents.Count} kept queued for retry");
            return spawned;
        }

        /// <summary>
        /// Auto-cleanup entry point wired from Player.ExitGame. Ensures a real
        /// player who logs out (or is teleported off-region during shutdown)
        /// doesn't leave stray phantoms behind.
        /// </summary>
        internal void PurgePhantomsOnExitGame()
        {
            int e = PurgeEnemyPhantoms();
            if (e > 0) PhantomHostLogger.Info($"[Phantom:Enemy] ExitGame purge for {this}: destroyed {e} enemy phantom(s)");

            if (_phantomAvatarIds.Count == 0) return;
            int n = PurgePhantoms();
            if (n > 0) PhantomHostLogger.Info($"[Phantom] ExitGame purge for {this}: destroyed {n} phantom(s)");
        }

        // ================================================================
        //  Party HUD integration
        //
        //  Every phantom-list mutation calls SyncPhantomParty(), which
        //  synthesises a Gazillion.PartyInfo protobuf with the human as
        //  leader + every live phantom Player as a member and sends it
        //  DIRECTLY to the human's client as a PartyInfoClientUpdate. The
        //  client renders the party HUD from that message alone —
        //  nameplates, health bars, portraits, mission-progress icons.
        //
        //  We deliberately do NOT go through PartyManager.OnPartyInfo-
        //  ServerUpdate: that path calls Party.AddMember which fires
        //  Player.OnAddedToParty on every member, including phantoms,
        //  setting their _partyId. When those phantoms get destroyed,
        //  Player.Destroy calls UpdatePartyAOI(GetParty()) which iterates
        //  members and dereferences partyMember.AOI — and phantoms have
        //  no AOI (PlayerConnection == null), so the whole game instance
        //  NREs and shuts down. Bypassing server-side party state keeps
        //  every real subsystem completely unaware of the synthetic
        //  group.
        //
        //  The synthetic GroupId is derived from the human's DbGuid with
        //  a fixed high-nibble tag (see ComputeSyntheticGroupId) so it's
        //  stable across spawn/despawn and can't collide with a real
        //  PlayerManager-minted party id.
        // ================================================================

        /// <summary>
        /// Rebuild + push the synthetic party info to reflect the current
        /// phantom list. Called from every list mutation (Register,
        /// Unregister, Purge, Restore).
        /// </summary>
        private void SyncPhantomParty()
        {
#if GAME_VERSION_1_52 || GAME_VERSION_1_53
            // Only the human host synthesises a party. Phantom Players
            // (PlayerConnection == null) shouldn't recurse into this.
            if (PlayerConnection == null) return;

            var game = Game;
            if (game == null) return;

            ulong groupId = ComputeSyntheticGroupId();

            // Anyone tracked as synced last time but no longer in the live
            // roster needs an explicit per-member Remove event. The client's
            // party roster is keyed incrementally (see PartyManager.cs
            // OnPartyMemberInfoServerUpdate) and does NOT drop stale members
            // just because a later PartyInfoClientUpdate snapshot happens to
            // omit them. Without this, despawned phantoms leave "ghost"
            // members that accumulate past the 5-member cap on repeated
            // spawn/clear cycles.
            List<ulong> staleRuntimeIds = null;
            foreach (var kvp in _syncedPhantomMemberDbIds)
            {
                if (_phantomPlayerIds.Contains(kvp.Key)) continue;
                (staleRuntimeIds ??= new List<ulong>()).Add(kvp.Key);
            }
            if (staleRuntimeIds != null)
            {
                foreach (ulong runtimeId in staleRuntimeIds)
                {
                    ulong staleMemberDbId = _syncedPhantomMemberDbIds[runtimeId];
                    try
                    {
                        SendMessage(PartyMemberInfoClientUpdate.CreateBuilder()
                            .SetGroupId(groupId)
                            .SetMemberDbGuid(staleMemberDbId)
                            .SetMemberEvent(PartyMemberEvent.ePME_Remove)
                            .Build());
                    }
                    catch (System.Exception ex) { PhantomHostLogger.Warn($"[Phantom:Party] member-remove failed: {ex.Message}"); }

                    // Also drop the community party-circle registration so
                    // the AvatarSlotInfo cache stays clean across
                    // spawn/clear cycles.
                    try { Community?.RemoveMember(staleMemberDbId, MHServerEmu.Games.Social.Communities.CircleId.__Party); }
                    catch (System.Exception ex) { PhantomHostLogger.Warn($"[Phantom:Party] community remove failed: {ex.Message}"); }

                    _syncedPhantomMemberDbIds.Remove(runtimeId);
                }
            }

            // Empty list = teardown. Send a client update with a null
            // PartyInfo to hide the group HUD on the client.
            if (_phantomAvatarIds.Count == 0)
            {
                try
                {
                    SendMessage(PartyInfoClientUpdate.CreateBuilder()
                        .SetGroupId(groupId)
                        .Build());
                }
                catch (System.Exception ex) { PhantomHostLogger.Warn($"[Phantom:Party] teardown failed: {ex.Message}"); }
                return;
            }

            // Carry the human's actual difficulty preference. The required
            // difficultyTierProtoId field was originally 0 (invalid), which
            // made the client treat the party's difficulty state as broken
            // and lock the difficulty selector entirely while phantoms were
            // active. Server-side GetParty() is null here, so
            // GetDifficultyTierPreference() falls through to the avatar's
            // DifficultyTierPreference property — the same value a solo
            // player's selector uses.
            ulong difficultyTierProtoId = (ulong)GetDifficultyTierPreference();

            var partyInfoBuilder = PartyInfo.CreateBuilder()
                .SetGroupId(groupId)
                .SetType(GroupType.GroupType_Party)
                .SetLeaderDbId(DatabaseUniqueId)
                .SetDifficultyTierProtoId(difficultyTierProtoId);

            // Human = leader / first member.
            partyInfoBuilder.AddMembers(PartyMemberInfo.CreateBuilder()
                .SetPlayerDbId(DatabaseUniqueId)
                .SetPlayerName(GetName())
                .Build());

            // One PartyMemberInfo per live phantom. Skip any whose Player
            // entity has already been destroyed (mid-teardown race).
            var mgr = game.EntityManager;
            for (int i = 0; i < _phantomPlayerIds.Count; i++)
            {
                Player phantom = mgr.GetEntity<Player>(_phantomPlayerIds[i]);
                if (phantom == null) continue;
                var phantomMemberInfo = PartyMemberInfo.CreateBuilder()
                    .SetPlayerDbId(phantom.DatabaseUniqueId)
                    .SetPlayerName(phantom.GetName())
                    .Build();
                partyInfoBuilder.AddMembers(phantomMemberInfo);

                // First-time sync for this phantom: (1) explicit Add event
                // for the party HUD, and (2) register the phantom in the
                // human's Community.__Party circle + broadcast its
                // AvatarSlotInfo. The party UI's HP-bar wiring resolves
                // party members via CommunityMember.GetAvatarSlotInfo (see
                // Player.OnPartyCircleChanged) — without the community
                // registration + broadcast, member.GetAvatarSlotInfo
                // returns null for phantoms and the party UI shows a
                // nameplate but no HP bar.
                if (_syncedPhantomMemberDbIds.ContainsKey(_phantomPlayerIds[i]) == false)
                {
                    try
                    {
                        SendMessage(PartyMemberInfoClientUpdate.CreateBuilder()
                            .SetGroupId(groupId)
                            .SetMemberDbGuid(phantom.DatabaseUniqueId)
                            .SetMemberEvent(PartyMemberEvent.ePME_Add)
                            .SetMemberInfo(phantomMemberInfo)
                            .Build());
                    }
                    catch (System.Exception ex) { PhantomHostLogger.Warn($"[Phantom:Party] member-add failed: {ex.Message}"); }

                    // Community party-circle registration + broadcast.
                    // For avatar phantoms the standard RequestLocalBroadcast
                    // (which internally calls phantom.BuildCommunityBroadcast)
                    // works — it reads CurrentAvatar. Team-up phantoms have
                    // no CurrentAvatar, so we build the broadcast manually
                    // with the team-up's own proto ref + level; without this
                    // the party HUD shows the nameplate but no HP bar for
                    // team-up phantoms.
                    try
                    {
                        if (Community != null)
                        {
                            bool added = Community.AddMember(phantom.DatabaseUniqueId, phantom.GetName(), MHServerEmu.Games.Social.Communities.CircleId.__Party);
                            var member = Community.GetMember(phantom.DatabaseUniqueId);
                            bool broadcast = false;
                            if (member != null)
                            {
                                var teamUpAgent = mgr.GetEntity<Agent>(_phantomAvatarIds[i]);
                                // Boss phantoms need the exact same manual broadcast as
                                // team-ups, for the exact same reason: no CurrentAvatar,
                                // so Community.RequestLocalBroadcast (which reads it) has
                                // nothing to report and the client shows "?" instead of a
                                // real icon/HP bar in the party frame (reported live
                                // 2026-08-08). GetRawBossCandidatePool is the same real-
                                // boss-only check SpawnBossPhantomHero validates against.
                                bool isBossPhantom = teamUpAgent != null
                                    && teamUpAgent.IsTeamUpAgent == false
                                    && GetRawBossCandidatePool().Contains(teamUpAgent.PrototypeDataRef);
                                if (teamUpAgent != null && (teamUpAgent.IsTeamUpAgent || isBossPhantom))
                                {
                                    // CurrentDifficultyRefId was missing here — the real,
                                    // working broadcast (Player.BuildCommunityBroadcast,
                                    // used by ordinary avatar-type phantoms) always sets
                                    // it alongside the region ref. Found by diffing this
                                    // manual broadcast against that method field-by-field
                                    // after a live report (2026-08-09) that team-up
                                    // phantoms ALSO never show a party icon/HP bar, not
                                    // just boss phantoms — ruling out "the client doesn't
                                    // recognize this prototype type" and pointing at a gap
                                    // in the broadcast itself instead.
                                    var teamUpBroadcast = Gazillion.CommunityMemberBroadcast.CreateBuilder()
                                        .SetMemberPlayerDbId(phantom.DatabaseUniqueId)
                                        .SetCurrentRegionRefId((ulong)(teamUpAgent.Region?.PrototypeDataRef ?? MHServerEmu.Games.GameData.PrototypeId.Invalid))
                                        .SetCurrentDifficultyRefId((ulong)(teamUpAgent.Region?.DifficultyTierRef ?? MHServerEmu.Games.GameData.PrototypeId.Invalid))
                                        .AddSlots(Gazillion.CommunityMemberAvatarSlot.CreateBuilder()
                                            .SetAvatarRefId((ulong)teamUpAgent.PrototypeDataRef)
                                            .SetCostumeRefId(0)
                                            .SetLevel((uint)teamUpAgent.CharacterLevel)
                                            .SetPrestigeLevel(0))
                                        .SetCurrentPlayerName(phantom.GetName())
                                        .SetIsOnline(1)
                                        .Build();
                                    Community.ReceiveMemberBroadcast(teamUpBroadcast);
                                    broadcast = true;
                                }
                                else
                                {
                                    broadcast = Community.RequestLocalBroadcast(member);
                                }
                            }
                            PhantomHostLogger.Info($"[Phantom:Party] community register '{phantom.GetName()}' 0x{phantom.DatabaseUniqueId:X}: added={added} memberFound={member != null} broadcasted={broadcast}");
                        }
                        else
                        {
                            PhantomHostLogger.Warn($"[Phantom:Party] community register skipped — Community is null on {GetName()}");
                        }
                    }
                    catch (System.Exception ex) { PhantomHostLogger.Warn($"[Phantom:Party] community register failed: {ex.Message}"); }
                }

                // Record what the client now knows about, keyed by the
                // phantom's runtime id so the next SyncPhantomParty can
                // emit a Remove for this DbGuid even after the entity is
                // gone. See the stale-cleanup block above.
                _syncedPhantomMemberDbIds[_phantomPlayerIds[i]] = phantom.DatabaseUniqueId;
            }

            try
            {
                SendMessage(PartyInfoClientUpdate.CreateBuilder()
                    .SetGroupId(groupId)
                    .SetPartyInfo(partyInfoBuilder.Build())
                    .Build());
            }
            catch (System.Exception ex) { PhantomHostLogger.Warn($"[Phantom:Party] sync failed: {ex.Message}"); }
#else
            // 1.48 delivers party state over the GroupingManager protocol (mux
            // channel 2: CurrentPartyInfo/PlayerJoinedGroup/PlayerLeftGroup/
            // ClientCreateGroup/ClientBootedFromGroup) instead of 1.52/1.53's
            // PartyInfo/PartyMemberInfo family, which doesn't exist in 1.48's
            // protocol at all (verified: zero matches for those class names in
            // src/Gazillion/1.48.0.1712). No code in this fork or upstream had
            // ever built or exercised this 1.48 message path before (2026-07-28)
            // -- there's no known-working reference to copy, so this is a
            // first attempt built from the real message field requirements
            // (each message's IsInitialized list) rather than a guess. The
            // community/HP-bar half (Community.AddMember/RequestLocalBroadcast/
            // ReceiveMemberBroadcast, CommunityMember's 1.48 AvatarRef/
            // CostumeRef fields) is NOT version-gated and already works
            // identically to the 1.52 path below -- reused as-is.
            if (PlayerConnection == null) return;

            var game = Game;
            if (game == null) return;

            ulong groupId = ComputeSyntheticGroupId();

            // Stale-member cleanup: mirrors the 1.52 path's PartyMemberEvent
            // Remove, using PlayerLeftGroup instead.
            List<ulong> staleRuntimeIds = null;
            foreach (var kvp in _syncedPhantomMemberDbIds)
            {
                if (_phantomPlayerIds.Contains(kvp.Key)) continue;
                (staleRuntimeIds ??= new List<ulong>()).Add(kvp.Key);
            }
            if (staleRuntimeIds != null)
            {
                foreach (ulong runtimeId in staleRuntimeIds)
                {
                    ulong staleMemberDbId = _syncedPhantomMemberDbIds[runtimeId];
                    try
                    {
                        SendGroupingMessage(PlayerLeftGroup.CreateBuilder()
                            .SetLeaverName(string.Empty) // entity is already gone -- name unknown at this point
                            .SetPlayerSessionId(staleMemberDbId)
                            .SetGroupId(groupId)
                            .SetLeaveReason(GroupLeaveReason.GROUP_LEAVE_REASON_LEFT)
                            .Build());
                    }
                    catch (System.Exception ex) { PhantomHostLogger.Warn($"[Phantom:Party48] member-leave failed: {ex.Message}"); }

                    try { Community?.RemoveMember(staleMemberDbId, MHServerEmu.Games.Social.Communities.CircleId.__Party); }
                    catch (System.Exception ex) { PhantomHostLogger.Warn($"[Phantom:Party48] community remove failed: {ex.Message}"); }

                    _syncedPhantomMemberDbIds.Remove(runtimeId);
                }
            }

            // Empty list = teardown. Tell the human's own client they've left
            // the group so the HUD hides (best-effort interpretation -- there's
            // no dedicated "disband" message on 1.48's client-facing side).
            if (_phantomAvatarIds.Count == 0)
            {
                if (_hasSyncedPhantomGroup48)
                {
                    try
                    {
                        SendGroupingMessage(ClientBootedFromGroup.CreateBuilder()
                            .SetGroupId(groupId)
                            .SetLeaveReason(GroupLeaveReason.GROUP_LEAVE_REASON_LEFT)
                            .Build());
                    }
                    catch (System.Exception ex) { PhantomHostLogger.Warn($"[Phantom:Party48] teardown failed: {ex.Message}"); }
                    _hasSyncedPhantomGroup48 = false;
                }
                return;
            }

            // First phantom of this session: establish the group before
            // anything else. ClientCreateGroup requires groupId/groupType/
            // leaderSessionId/leaderName.
            if (_hasSyncedPhantomGroup48 == false)
            {
                try
                {
                    SendGroupingMessage(ClientCreateGroup.CreateBuilder()
                        .SetGroupId(groupId)
                        .SetGroupType(GroupType.GroupType_Party)
                        .SetLeaderSessionId(DatabaseUniqueId)
                        .SetLeaderName(GetName())
                        .Build());
                }
                catch (System.Exception ex) { PhantomHostLogger.Warn($"[Phantom:Party48] create-group failed: {ex.Message}"); }
                _hasSyncedPhantomGroup48 = true;
            }

            var mgr = game.EntityManager;

            // PerPlayerInfo.playerSessionId is a required field with no
            // equivalent concept for a phantom (no real login session) -- use
            // DatabaseUniqueId as a stable, non-zero stand-in. This send path
            // goes straight to this player's own connection, bypassing the
            // Grouping service's session-id-keyed lookup dictionaries
            // entirely, so nothing else in the server ever needs this value
            // to correspond to a real session.
            var currentPartyInfoBuilder = CurrentPartyInfo.CreateBuilder()
                .SetGroupId(groupId)
                .SetGroupType(GroupType.GroupType_Party)
                .SetLeader(PerPlayerInfo.CreateBuilder()
                    .SetPlayerName(GetName())
                    .SetPlayerSessionId(DatabaseUniqueId)
                    .SetPlayerDbId(DatabaseUniqueId)
                    .Build());

            currentPartyInfoBuilder.AddMembers(PerPlayerInfo.CreateBuilder()
                .SetPlayerName(GetName())
                .SetPlayerSessionId(DatabaseUniqueId)
                .SetPlayerDbId(DatabaseUniqueId)
                .Build());

            for (int i = 0; i < _phantomPlayerIds.Count; i++)
            {
                Player phantom = mgr.GetEntity<Player>(_phantomPlayerIds[i]);
                if (phantom == null) continue;

                currentPartyInfoBuilder.AddMembers(PerPlayerInfo.CreateBuilder()
                    .SetPlayerName(phantom.GetName())
                    .SetPlayerSessionId(phantom.DatabaseUniqueId)
                    .SetPlayerDbId(phantom.DatabaseUniqueId)
                    .Build());

                if (_syncedPhantomMemberDbIds.ContainsKey(_phantomPlayerIds[i]) == false)
                {
                    try
                    {
                        SendGroupingMessage(PlayerJoinedGroup.CreateBuilder()
                            .SetJoiningPlayerName(phantom.GetName())
                            .SetPlayerSessionId(phantom.DatabaseUniqueId)
                            .SetGroupId(groupId)
                            .Build());
                    }
                    catch (System.Exception ex) { PhantomHostLogger.Warn($"[Phantom:Party48] member-join failed: {ex.Message}"); }

                    // Community registration -- same ungated machinery the
                    // 1.52 path above uses (Community.cs/CommunityMember.cs
                    // have zero GAME_VERSION gating); the HP bar resolves via
                    // CommunityMember's 1.48 AvatarRef/CostumeRef fields
                    // instead of 1.52's GetAvatarSlotInfo()/slots[], reached
                    // through Player.BuildCommunityBroadcast(), which already
                    // has its own 1.48 branch.
                    try
                    {
                        if (Community != null)
                        {
                            bool added = Community.AddMember(phantom.DatabaseUniqueId, phantom.GetName(), MHServerEmu.Games.Social.Communities.CircleId.__Party);
                            var member = Community.GetMember(phantom.DatabaseUniqueId);
                            bool broadcast = false;
                            if (member != null)
                            {
                                var teamUpAgent = mgr.GetEntity<Agent>(_phantomAvatarIds[i]);
                                // Boss phantoms need the exact same manual broadcast as
                                // team-ups, for the exact same reason: no CurrentAvatar,
                                // so Community.RequestLocalBroadcast (which reads it) has
                                // nothing to report and the client shows "?" instead of a
                                // real icon/HP bar in the party frame (reported live
                                // 2026-08-08). GetRawBossCandidatePool is the same real-
                                // boss-only check SpawnBossPhantomHero validates against.
                                bool isBossPhantom = teamUpAgent != null
                                    && teamUpAgent.IsTeamUpAgent == false
                                    && GetRawBossCandidatePool().Contains(teamUpAgent.PrototypeDataRef);
                                if (teamUpAgent != null && (teamUpAgent.IsTeamUpAgent || isBossPhantom))
                                {
                                    var teamUpBroadcast = Gazillion.CommunityMemberBroadcast.CreateBuilder()
                                        .SetMemberPlayerDbId(phantom.DatabaseUniqueId)
                                        .SetCurrentRegionRefId((ulong)(teamUpAgent.Region?.PrototypeDataRef ?? MHServerEmu.Games.GameData.PrototypeId.Invalid))
                                        .SetCurrentAvatarRefId((ulong)teamUpAgent.PrototypeDataRef)
                                        .SetCurrentCostumeRefId(0)
                                        .SetCurrentCharacterLevel((ulong)teamUpAgent.CharacterLevel)
                                        .SetCurrentPrestigeLevel(0)
                                        .SetCurrentPlayerName(phantom.GetName())
                                        .SetIsOnline(1)
                                        .Build();
                                    Community.ReceiveMemberBroadcast(teamUpBroadcast);
                                    broadcast = true;
                                }
                                else
                                {
                                    broadcast = Community.RequestLocalBroadcast(member);
                                }
                            }
                            PhantomHostLogger.Info($"[Phantom:Party48] community register '{phantom.GetName()}' 0x{phantom.DatabaseUniqueId:X}: added={added} memberFound={member != null} broadcasted={broadcast}");
                        }
                        else
                        {
                            PhantomHostLogger.Warn($"[Phantom:Party48] community register skipped — Community is null on {GetName()}");
                        }
                    }
                    catch (System.Exception ex) { PhantomHostLogger.Warn($"[Phantom:Party48] community register failed: {ex.Message}"); }
                }

                _syncedPhantomMemberDbIds[_phantomPlayerIds[i]] = phantom.DatabaseUniqueId;
            }

            // Full snapshot every sync, in case the 1.48 client needs this to
            // (re)render the roster rather than relying on the incremental
            // Create/Join/Leave events alone -- unverified either way (see
            // class-level note), so send both.
            try
            {
                SendGroupingMessage(currentPartyInfoBuilder.Build());
            }
            catch (System.Exception ex) { PhantomHostLogger.Warn($"[Phantom:Party48] snapshot failed: {ex.Message}"); }
#endif
        }

        /// <summary>
        /// True when this Player has live phantoms and therefore a synthetic
        /// client-side party (which has no server-side Party object).
        /// </summary>
        public bool HasPhantomParty => PhantomHeroCount > 0 && PartyId == 0;

        /// <summary>
        /// Re-push the synthetic party info to the client. Public entry
        /// point for systems outside the phantom module that change state
        /// reflected in the party HUD (e.g. difficulty tier changes).
        /// </summary>
        public void ResyncPhantomParty() => SyncPhantomParty();

        // ================================================================
        //  Saved squads
        //
        //  Per-account phantom lineups, persisted as JSON under
        //  <ServerRoot>/Data/PhantomSquads/0x<AccountDbGuid>.json — runtime
        //  data next to the exe, same territory as Account.db. Squad names
        //  and rosters are user data: they are typed by the player in chat
        //  and stored per account, so nothing team- or hero-specific ever
        //  enters this source tree.
        // ================================================================

        private const int PhantomSquadMaxCount = 20;

        private sealed class PhantomSquadMember
        {
            public ulong AvatarRef { get; set; }
            public int Level { get; set; }
            public string Username { get; set; }
            public bool LockLevel { get; set; }
            public ulong CostumeRef { get; set; }
            public List<ulong> GearRefs { get; set; }
            public bool Invincible { get; set; }
        }

        private string GetPhantomSquadFilePath()
            => System.IO.Path.Combine(MHServerEmu.Core.Helpers.FileHelper.DataDirectory, "PhantomSquads", $"0x{DatabaseUniqueId:X}.json");

        private Dictionary<string, List<PhantomSquadMember>> LoadPhantomSquadFile()
        {
            try
            {
                string path = GetPhantomSquadFilePath();
                if (System.IO.File.Exists(path) == false)
                    return new(StringComparer.OrdinalIgnoreCase);
                var loaded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<PhantomSquadMember>>>(System.IO.File.ReadAllText(path));
                return loaded != null ? new(loaded, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
            }
            catch (System.Exception ex)
            {
                PhantomHostLogger.Warn($"[Phantom:Squad] load failed for {this}: {ex.Message}");
                return new(StringComparer.OrdinalIgnoreCase);
            }
        }

        private bool SavePhantomSquadFile(Dictionary<string, List<PhantomSquadMember>> squads)
        {
            try
            {
                string path = GetPhantomSquadFilePath();
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(squads,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return true;
            }
            catch (System.Exception ex)
            {
                PhantomHostLogger.Warn($"[Phantom:Squad] save failed for {this}: {ex.Message}");
                return false;
            }
        }

        private static bool IsValidSquadName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 32) return false;
            foreach (char c in name)
                if (char.IsLetterOrDigit(c) == false && c != '_' && c != '-') return false;
            return true;
        }

        /// <summary>Snapshot the current phantom lineup under a name.</summary>
        public string SavePhantomSquad(string squadName)
        {
            if (IsValidSquadName(squadName) == false)
                return "Squad names must be 1-32 letters, digits, _ or -.";
            if (_phantomDescriptors.Count == 0)
                return "No phantoms active — spawn the lineup you want to save first.";

            var squads = LoadPhantomSquadFile();
            if (squads.ContainsKey(squadName) == false && squads.Count >= PhantomSquadMaxCount)
                return $"Squad limit reached ({PhantomSquadMaxCount}). Delete one first.";

            var members = new List<PhantomSquadMember>(_phantomDescriptors.Count);
            foreach (var d in _phantomDescriptors)
                members.Add(new PhantomSquadMember { AvatarRef = d.AvatarRef, Level = d.Level, Username = d.Username, LockLevel = d.LockLevel, CostumeRef = d.CostumeRef, GearRefs = d.GearRefs != null ? new List<ulong>(d.GearRefs) : null, Invincible = d.Invincible });

            squads[squadName] = members;
            if (SavePhantomSquadFile(squads) == false)
                return "Failed to write squad file — check server log.";
            return $"Squad '{squadName}' saved ({members.Count} phantom(s)).";
        }

        /// <summary>Replace the current phantoms with a saved squad.</summary>
        public string SpawnPhantomSquad(string squadName, Avatar caller, bool bypassCap = false)
        {
            if (caller == null || caller.IsInWorld == false)
                return "No avatar in world.";
            if (IsTrialGauntletActive)
                return "Trial of the Impossible is solo-only — no phantom summons.";

            var squads = LoadPhantomSquadFile();
            if (squads.TryGetValue(squadName, out List<PhantomSquadMember> members) == false || members == null || members.Count == 0)
                return $"No squad named '{squadName}'. Use: phantom squad list";

            PurgePhantoms();

            int spawned = 0;
            string firstError = null;
            foreach (var m in members)
            {
                // LockLevel squads respawn at their stored level; auto-level
                // squads respawn at the caller's current level (level 0 =
                // "match caller" inside SpawnPhantomHeroCore).
                ulong id = caller.SpawnPhantomHeroFromIntent((PrototypeId)m.AvatarRef, m.LockLevel ? m.Level : 0, m.Username, m.LockLevel, m.CostumeRef, out string error, m.GearRefs, m.Invincible, bypassCap);
                if (id != 0) spawned++;
                else firstError ??= error;
            }

            return firstError == null
                ? $"Squad '{squadName}': spawned {spawned}/{members.Count}."
                : $"Squad '{squadName}': spawned {spawned}/{members.Count}. First error: {firstError}";
        }

        /// <summary>List saved squad names.</summary>
        public string ListPhantomSquads()
        {
            var squads = LoadPhantomSquadFile();
            if (squads.Count == 0) return "No saved squads. Use: phantom squad save [name]";
            var sb = new System.Text.StringBuilder("Saved squads: ");
            bool first = true;
            foreach (var kvp in squads)
            {
                if (first == false) sb.Append(", ");
                sb.Append($"{kvp.Key} ({kvp.Value.Count})");
                first = false;
            }
            return sb.ToString();
        }

        // ================================================================
        //  Costume commands. Same legal posture as squads: costume names
        //  are matched at runtime against the client's own data, and the
        //  applied ref is user data stored per phantom.
        // ================================================================

        /// <summary>
        /// Find active phantoms whose hero short-name or username matches
        /// the query.
        /// </summary>
        private List<Avatar> FindActivePhantoms(string query)
        {
            var results = new List<Avatar>();
            var mgr = Game?.EntityManager;
            if (mgr == null || string.IsNullOrWhiteSpace(query)) return results;

            foreach (ulong avatarId in _phantomAvatarIds)
            {
                Avatar phantom = mgr.GetEntity<Avatar>(avatarId);
                if (phantom == null || phantom.IsInWorld == false) continue;

                string heroName = phantom.PrototypeDataRef.GetName();
                int slash = heroName.LastIndexOf('/');
                if (slash >= 0) heroName = heroName[(slash + 1)..];
                if (heroName.EndsWith(".prototype", StringComparison.OrdinalIgnoreCase))
                    heroName = heroName[..^".prototype".Length];

                string username = phantom.GetOwnerOfType<Player>()?.GetName() ?? string.Empty;

                if (heroName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    username.Contains(query, StringComparison.OrdinalIgnoreCase))
                    results.Add(phantom);
            }

            return results;
        }

        /// <summary>
        /// Same lookup as <see cref="FindActivePhantoms"/> but typed to
        /// <see cref="Agent"/> so it also matches team-up phantoms — used by
        /// gear commands (team-ups have equipment slots) rather than costume
        /// commands (team-ups have no costume system).
        /// </summary>
        private List<Agent> FindActivePhantomAgents(string query)
        {
            var results = new List<Agent>();
            var mgr = Game?.EntityManager;
            if (mgr == null || string.IsNullOrWhiteSpace(query)) return results;

            foreach (ulong avatarId in _phantomAvatarIds)
            {
                Agent phantom = mgr.GetEntity<Agent>(avatarId);
                if (phantom == null || phantom.IsInWorld == false) continue;

                string heroName = phantom.PrototypeDataRef.GetName();
                int slash = heroName.LastIndexOf('/');
                if (slash >= 0) heroName = heroName[(slash + 1)..];
                if (heroName.EndsWith(".prototype", StringComparison.OrdinalIgnoreCase))
                    heroName = heroName[..^".prototype".Length];

                string username = phantom.GetOwnerOfType<Player>()?.GetName() ?? string.Empty;

                if (heroName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    username.Contains(query, StringComparison.OrdinalIgnoreCase))
                    results.Add(phantom);
            }

            return results;
        }

        /// <summary>
        /// Forces an already-in-world avatar to re-enter the world in place
        /// (same region/position/orientation). ChangeCostume() only writes
        /// the CostumeCurrent property — for a BRAND NEW phantom that's
        /// applied before EnterWorld so the initial entity-creation packet
        /// already carries the right look, but for an ALREADY-spawned
        /// phantom (re-costuming from the app's dropdown or !phantom
        /// costume) there's no equivalent trigger, so already-connected
        /// clients keep rendering the old mesh even though the server-side
        /// property (and thus Inspect, which reads it directly) is correct.
        /// Exit+re-enter forces a fresh entity-creation packet, the same
        /// path already confirmed to render costumes correctly on spawn.
        /// </summary>
        private static void RefreshPhantomVisual(Avatar phantom)
        {
            if (phantom == null || phantom.IsInWorld == false) return;

            Region region = phantom.Region;
            Vector3 position = phantom.RegionLocation.Position;
            Orientation orientation = phantom.Orientation;
            if (region == null) return;

            phantom.ExitWorld();
            phantom.EnterWorld(region, position, orientation);
        }

        /// <summary>Give every active phantom a random costume.</summary>
        public string RandomizePhantomCostumes()
        {
            if (_phantomAvatarIds.Count == 0) return "No phantoms active.";
            var mgr = Game?.EntityManager;
            if (mgr == null) return "No game.";

            int changed = 0;
            foreach (ulong avatarId in _phantomAvatarIds)
            {
                Avatar phantom = mgr.GetEntity<Avatar>(avatarId);
                if (phantom == null || phantom.IsInWorld == false) continue;
                PrototypeId costumeRef = Avatar.PickRandomCostume(phantom.PrototypeDataRef, Game.Random);
                if (costumeRef == PrototypeId.Invalid) continue;
                if (phantom.ChangeCostume(costumeRef))
                {
                    UpdatePhantomCostume(avatarId, (ulong)costumeRef);
                    RefreshPhantomVisual(phantom);
                    changed++;
                }
            }
            return $"Randomized costumes on {changed} phantom(s).";
        }

        /// <summary>
        /// Set a specific (or random) costume on the phantom matching
        /// <paramref name="phantomQuery"/>. costumeQuery "random" rolls.
        /// </summary>
        public string SetPhantomCostume(string phantomQuery, string costumeQuery)
        {
            var matches = FindActivePhantoms(phantomQuery);
            if (matches.Count == 0) return $"No active phantom matching '{phantomQuery}'.";
            if (matches.Count > 1) return $"Multiple phantoms match '{phantomQuery}' — use their username to disambiguate.";

            Avatar phantom = matches[0];
            PrototypeId costumeRef;

            if (costumeQuery.Equals("random", StringComparison.OrdinalIgnoreCase))
            {
                costumeRef = Avatar.PickRandomCostume(phantom.PrototypeDataRef, Game.Random);
                if (costumeRef == PrototypeId.Invalid) return "This hero has no approved costumes in the loaded data.";
            }
            else
            {
                var costumes = Avatar.FindCostumeRefs(phantom.PrototypeDataRef, costumeQuery);
                if (costumes.Count == 0) return $"No costume matching '{costumeQuery}'. Use: phantom costume list [hero]";
                if (costumes.Count > 1)
                {
                    var sb = new System.Text.StringBuilder("Multiple matches: ");
                    for (int i = 0; i < costumes.Count && i < 8; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(costumes[i].ShortName);
                    }
                    if (costumes.Count > 8) sb.Append(", ...");
                    return sb.ToString();
                }
                costumeRef = costumes[0].CostumeRef;
            }

            if (phantom.ChangeCostume(costumeRef) == false)
                return "ChangeCostume failed — check server log.";

            UpdatePhantomCostume(phantom.Id, (ulong)costumeRef);
            RefreshPhantomVisual(phantom);
            return $"Costume applied.";
        }

        /// <summary>
        /// Force-equips a costume directly on THIS player's own live avatar,
        /// bypassing the item/store/closet flow entirely — same mechanism
        /// as the phantom costume commands above. Also grants CostumeUnlock
        /// first (real ownership, same property a store purchase would set)
        /// so we can isolate whether the client's block on low-DesignState
        /// costumes is an ownership/anti-spoof check (this should get past
        /// it) versus a hard DesignState wall baked into rendering itself
        /// (this won't help, and would confirm a client-data patch is the
        /// only route).
        /// </summary>
        public string ForceEquipCostumeOnCurrentAvatar(PrototypeId costumeRef)
        {
            Avatar avatar = CurrentAvatar;
            if (avatar == null) return "No current avatar in world.";

#if GAME_VERSION_1_52 || GAME_VERSION_1_53
            UnlockCostume(costumeRef); // No CostumeUnlock property on 1.48.
#endif

            if (avatar.ChangeCostume(costumeRef) == false)
                return "ChangeCostume failed — check server log.";

            RefreshPhantomVisual(avatar);
            return $"Applied {costumeRef.GetName()} to {avatar.PrototypeDataRef.GetName()}.";
        }

        /// <summary>
        /// Re-roll gear on all active phantoms, or on the one matching
        /// <paramref name="phantomQuery"/>. Strips current equipment
        /// (except the costume slot) and rolls a fresh level-appropriate
        /// set per slot — or, if <paramref name="toBiS"/> is true, equips
        /// each phantom's community best-in-slot loadout directly instead
        /// of a random roll (falls back to random for any slot the BiS data
        /// doesn't cover).
        /// </summary>
        public string RerollPhantomGear(string phantomQuery = null, bool toBiS = false)
        {
            var mgr = Game?.EntityManager;
            if (mgr == null) return "No game.";

            // Agent, not Avatar — team-up phantoms are Agent and carry their
            // own 4-slot EquipmentInventories, so the lookup has to be able
            // to resolve both kinds (costume commands stay Avatar-only via
            // FindActivePhantoms since team-ups have no costume system).
            List<Agent> targets;
            if (string.IsNullOrWhiteSpace(phantomQuery))
            {
                targets = new List<Agent>();
                foreach (ulong avatarId in _phantomAvatarIds)
                {
                    Agent phantom = mgr.GetEntity<Agent>(avatarId);
                    if (phantom != null && phantom.IsInWorld) targets.Add(phantom);
                }
                if (targets.Count == 0) return "No phantoms active.";
            }
            else
            {
                targets = FindActivePhantomAgents(phantomQuery);
                if (targets.Count == 0) return $"No active phantom matching '{phantomQuery}'.";
                if (targets.Count > 1) return $"Multiple phantoms match '{phantomQuery}' — use their username to disambiguate.";
            }

            int rerolled = 0;
            int noBiSData = 0;
            foreach (Agent phantom in targets)
            {
                Player phantomOwner = phantom.GetOwnerOfType<Player>();
                PrototypeId phantomRef = phantom.PrototypeDataRef;
                var equipmentInventories = phantom is Avatar phantomAvatar
                    ? phantomAvatar.AvatarPrototype?.EquipmentInventories
                    : phantomRef.As<GameData.Prototypes.AgentTeamUpPrototype>()?.EquipmentInventories;
                if (phantomOwner == null || equipmentInventories == null) continue;

                IReadOnlyDictionary<Loot.EquipmentInvUISlot, PrototypeId> bisLoadout = null;
                if (toBiS)
                {
                    if (PhantomBiSData.TryGetLoadout(phantomRef, Game, out var loadout) && loadout.Count > 0)
                        bisLoadout = loadout;
                    else
                        noBiSData++; // falls through to a normal random roll below — team-ups have no BiS data at all today
                }

                // Strip current gear (costume slot untouched — that belongs
                // to the costume system, and team-ups have no costume slot
                // in the first place).
                foreach (var assignment in equipmentInventories)
                {
                    var invProto = assignment.Inventory;
                    if (invProto == null || invProto.ConvenienceLabel == Inventories.InventoryConvenienceLabel.Costume) continue;
                    phantom.GetInventoryByRef(assignment.Inventory.DataRef)?.DestroyContained();
                }

                List<ulong> applied = Avatar.ApplyPhantomGear(phantomOwner, phantom, phantom.CharacterLevel, null, bisLoadout);
                UpdatePhantomGear(phantom.Id, applied);
                rerolled++;
            }

            if (toBiS == false)
                return $"Re-rolled gear on {rerolled} phantom(s).";
            return noBiSData == 0
                ? $"Equipped BiS gear on {rerolled} phantom(s)."
                : $"Equipped BiS gear on {rerolled} phantom(s) ({noBiSData} had no BiS data — rolled random instead).";
        }

        /// <summary>List available costumes for the phantom matching the query.</summary>
        public string ListPhantomCostumes(string phantomQuery)
        {
            var matches = FindActivePhantoms(phantomQuery);
            if (matches.Count == 0) return $"No active phantom matching '{phantomQuery}'.";
            Avatar phantom = matches[0];

            var costumes = Avatar.GetCostumesForAvatar(phantom.PrototypeDataRef);
            if (costumes.Count == 0) return "This hero has no approved costumes in the loaded data.";

            var sb = new System.Text.StringBuilder($"{costumes.Count} costume(s): ");
            for (int i = 0; i < costumes.Count && i < 15; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(costumes[i].ShortName);
            }
            if (costumes.Count > 15) sb.Append(", ...");
            return sb.ToString();
        }

        /// <summary>Delete a saved squad.</summary>
        public string DeletePhantomSquad(string squadName)
        {
            var squads = LoadPhantomSquadFile();
            if (squads.Remove(squadName) == false)
                return $"No squad named '{squadName}'.";
            if (SavePhantomSquadFile(squads) == false)
                return "Failed to write squad file — check server log.";
            // The deleted squad can't stay the default — clear it silently if it was.
            if (string.Equals(GetDefaultSquadName(), squadName, StringComparison.OrdinalIgnoreCase))
                ClearDefaultSquadFile();
            return $"Squad '{squadName}' deleted.";
        }

        // ================================================================
        //  Default squad — auto-spawns once per login session. Stored as a
        //  tiny sibling text file next to the squads JSON so the existing
        //  squad-file format never has to change shape.
        // ================================================================

        // Session-scoped, not persisted: guards TryAutoSpawnDefaultSquad so
        // it only ever fires once per login, not on every region hop /
        // hero swap that re-enters Avatar.OnEnteredWorld.
        private bool _defaultSquadAutoSpawnAttempted;

        private string GetDefaultSquadFilePath()
            => System.IO.Path.Combine(MHServerEmu.Core.Helpers.FileHelper.DataDirectory, "PhantomSquads", $"0x{DatabaseUniqueId:X}.default");

        private void ClearDefaultSquadFile()
        {
            try
            {
                string path = GetDefaultSquadFilePath();
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
            catch (System.Exception ex) { PhantomHostLogger.Warn($"[Phantom:Squad] clear default failed for {this}: {ex.Message}"); }
        }

        /// <summary>Name of this account's default (auto-spawn-on-login) squad, or null if none set.</summary>
        public string GetDefaultSquadName()
        {
            try
            {
                string path = GetDefaultSquadFilePath();
                if (System.IO.File.Exists(path) == false) return null;
                string name = System.IO.File.ReadAllText(path).Trim();
                return name.Length > 0 ? name : null;
            }
            catch (System.Exception ex)
            {
                PhantomHostLogger.Warn($"[Phantom:Squad] read default failed for {this}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Marks <paramref name="squadName"/> as the account's default squad
        /// (auto-spawned once on the next login), or clears the default if
        /// <paramref name="squadName"/> is null/"clear"/"none".
        /// </summary>
        public string SetDefaultSquad(string squadName)
        {
            if (string.IsNullOrWhiteSpace(squadName)
                || squadName.Equals("clear", StringComparison.OrdinalIgnoreCase)
                || squadName.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                ClearDefaultSquadFile();
                return "Default squad cleared.";
            }

            var squads = LoadPhantomSquadFile();
            if (squads.ContainsKey(squadName) == false)
                return $"No squad named '{squadName}'. Save it first with: phantom squad save {squadName}";

            try
            {
                string path = GetDefaultSquadFilePath();
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.IO.File.WriteAllText(path, squadName);
            }
            catch (System.Exception ex)
            {
                PhantomHostLogger.Warn($"[Phantom:Squad] set default failed for {this}: {ex.Message}");
                return "Failed to write default-squad file — check server log.";
            }
            return $"'{squadName}' will auto-spawn on login.";
        }

        /// <summary>
        /// Fires once per login session on the first Avatar.OnEnteredWorld —
        /// if the account has a default squad set and no phantoms are
        /// already active (e.g. from a mid-session migration restore), spawn
        /// it automatically so the squad is there without a manual command.
        /// </summary>
        internal void TryAutoSpawnDefaultSquad(Avatar caller)
        {
            if (_defaultSquadAutoSpawnAttempted) return;
            _defaultSquadAutoSpawnAttempted = true;

            if (PhantomHeroCount > 0) return; // already populated (migration restore, etc.)

            string defaultSquad = GetDefaultSquadName();
            if (defaultSquad == null) return;

            string result = SpawnPhantomSquad(defaultSquad, caller);
            PhantomHostLogger.Info($"[Phantom:Squad] auto-spawn default '{defaultSquad}' for {this}: {result}");
        }

        /// <summary>
        /// Deterministic group id derived from the human's DbGuid. The
        /// high nibble is set to a distinct tag (0xFACE_0BAD…) so we can
        /// tell synthetic parties apart from any PlayerManager-assigned
        /// group id at a glance in the logs, and so the two id spaces
        /// can't collide.
        /// </summary>
        private ulong ComputeSyntheticGroupId()
        {
            const ulong PhantomPartyTag = 0xFACE_0BAD_0000_0000UL;
            return PhantomPartyTag | (DatabaseUniqueId & 0x0000_0000_FFFF_FFFFUL);
        }
    }
}
