using System.Collections.Generic;
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

        internal void RegisterPhantom(ulong avatarId, ulong phantomPlayerId, PhantomIntent descriptor)
        {
            _phantomAvatarIds.Add(avatarId);
            _phantomPlayerIds.Add(phantomPlayerId);
            _phantomDescriptors.Add(descriptor);
        }

        internal bool UnregisterPhantom(ulong avatarId)
        {
            int idx = _phantomAvatarIds.IndexOf(avatarId);
            if (idx < 0) return false;
            _phantomAvatarIds.RemoveAt(idx);
            _phantomPlayerIds.RemoveAt(idx);
            _phantomDescriptors.RemoveAt(idx);
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
                    ulong id = caller.SpawnPhantomHeroFromIntent((PrototypeId)intent.AvatarRef, intent.Level, intent.Username, out string error);
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
    }
}
