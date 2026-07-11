using System.Collections.Generic;
using Gazillion;
using MHServerEmu.Core.Logging;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.GameData;

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

        public IReadOnlyList<ulong> PhantomAvatarIds => _phantomAvatarIds;
        public IReadOnlyList<ulong> PhantomPlayerIds => _phantomPlayerIds;
        public int PhantomHeroCount => _phantomAvatarIds.Count;

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
            _phantomDescriptors[idx] = new PhantomIntent
            {
                AvatarRef = d.AvatarRef,
                Level = newLevel,
                Username = d.Username,
                LockLevel = d.LockLevel,
            };
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
                    Avatar av = mgr.GetEntity<Avatar>(avatarId);
                    if (av == null) continue;
                    if (av.IsInWorld) av.ExitWorld();
                    av.Destroy();
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
            if (_phantomDescriptors.Count == 0) return;
            var mig = PlayerConnection?.MigrationData;
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
            if (mig == null || mig.PhantomIntents.Count == 0 || caller == null) return 0;
            int spawned = 0;
            foreach (var intent in mig.PhantomIntents)
            {
                try
                {
                    // Force the caller to spawn each intent with its saved
                    // (avatarRef, level, username) rather than the default
                    // "random from deck / caller's level" path.
                    ulong id = caller.SpawnPhantomHeroFromIntent((PrototypeId)intent.AvatarRef, intent.Level, intent.Username, intent.LockLevel, out string error);
                    if (id != 0) spawned++;
                    else PhantomHostLogger.Warn($"[Phantom] restore intent {intent.Username} failed: {error}");
                }
                catch (System.Exception ex) { PhantomHostLogger.Warn($"[Phantom] restore intent {intent.Username} threw: {ex.Message}"); }
            }
            mig.PhantomIntents.Clear();
            PhantomHostLogger.Info($"[Phantom] restore from migration: {spawned} phantom(s) re-spawned");
            return spawned;
        }

        /// <summary>
        /// Auto-cleanup entry point wired from Player.ExitGame. Ensures a real
        /// player who logs out (or is teleported off-region during shutdown)
        /// doesn't leave stray phantoms behind.
        /// </summary>
        internal void PurgePhantomsOnExitGame()
        {
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
            // Only the human host synthesises a party. Phantom Players
            // (PlayerConnection == null) shouldn't recurse into this.
            if (PlayerConnection == null) return;

            var game = Game;
            if (game == null) return;

            ulong groupId = ComputeSyntheticGroupId();

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

            var partyInfoBuilder = PartyInfo.CreateBuilder()
                .SetGroupId(groupId)
                .SetType(GroupType.GroupType_Party)
                .SetLeaderDbId(DatabaseUniqueId)
                .SetDifficultyTierProtoId(0);

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
                partyInfoBuilder.AddMembers(PartyMemberInfo.CreateBuilder()
                    .SetPlayerDbId(phantom.DatabaseUniqueId)
                    .SetPlayerName(phantom.GetName())
                    .Build());
            }

            try
            {
                SendMessage(PartyInfoClientUpdate.CreateBuilder()
                    .SetGroupId(groupId)
                    .SetPartyInfo(partyInfoBuilder.Build())
                    .Build());
            }
            catch (System.Exception ex) { PhantomHostLogger.Warn($"[Phantom:Party] sync failed: {ex.Message}"); }
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
