using System;
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
                    CostumeRef = d.CostumeRef,
                    GearRefs = d.GearRefs != null ? new List<ulong>(d.GearRefs) : null,
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
                    ulong id = caller.SpawnPhantomHeroFromIntent((PrototypeId)intent.AvatarRef, intent.Level, intent.Username, intent.LockLevel, intent.CostumeRef, out string error, intent.GearRefs);
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
                members.Add(new PhantomSquadMember { AvatarRef = d.AvatarRef, Level = d.Level, Username = d.Username, LockLevel = d.LockLevel, CostumeRef = d.CostumeRef, GearRefs = d.GearRefs != null ? new List<ulong>(d.GearRefs) : null });

            squads[squadName] = members;
            if (SavePhantomSquadFile(squads) == false)
                return "Failed to write squad file — check server log.";
            return $"Squad '{squadName}' saved ({members.Count} phantom(s)).";
        }

        /// <summary>Replace the current phantoms with a saved squad.</summary>
        public string SpawnPhantomSquad(string squadName, Avatar caller)
        {
            if (caller == null || caller.IsInWorld == false)
                return "No avatar in world.";

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
                ulong id = caller.SpawnPhantomHeroFromIntent((PrototypeId)m.AvatarRef, m.LockLevel ? m.Level : 0, m.Username, m.LockLevel, m.CostumeRef, out string error, m.GearRefs);
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
            return $"Costume applied.";
        }

        /// <summary>
        /// Re-roll gear on all active phantoms, or on the one matching
        /// <paramref name="phantomQuery"/>. Strips current equipment
        /// (except the costume slot) and rolls a fresh level-appropriate
        /// set per slot.
        /// </summary>
        public string RerollPhantomGear(string phantomQuery = null)
        {
            var mgr = Game?.EntityManager;
            if (mgr == null) return "No game.";

            List<Avatar> targets;
            if (string.IsNullOrWhiteSpace(phantomQuery))
            {
                targets = new List<Avatar>();
                foreach (ulong avatarId in _phantomAvatarIds)
                {
                    Avatar phantom = mgr.GetEntity<Avatar>(avatarId);
                    if (phantom != null && phantom.IsInWorld) targets.Add(phantom);
                }
                if (targets.Count == 0) return "No phantoms active.";
            }
            else
            {
                targets = FindActivePhantoms(phantomQuery);
                if (targets.Count == 0) return $"No active phantom matching '{phantomQuery}'.";
                if (targets.Count > 1) return $"Multiple phantoms match '{phantomQuery}' — use their username to disambiguate.";
            }

            int rerolled = 0;
            foreach (Avatar phantom in targets)
            {
                Player phantomOwner = phantom.GetOwnerOfType<Player>();
                var avatarProto = phantom.AvatarPrototype;
                if (phantomOwner == null || avatarProto?.EquipmentInventories == null) continue;

                // Strip current gear (costume slot untouched — that belongs
                // to the costume system).
                foreach (var assignment in avatarProto.EquipmentInventories)
                {
                    var invProto = assignment.Inventory.As<GameData.Prototypes.InventoryPrototype>();
                    if (invProto == null || invProto.ConvenienceLabel == Inventories.InventoryConvenienceLabel.Costume) continue;
                    phantom.GetInventoryByRef(assignment.Inventory)?.DestroyContained();
                }

                List<ulong> applied = Avatar.ApplyPhantomGear(phantomOwner, phantom, phantom.CharacterLevel, null);
                UpdatePhantomGear(phantom.Id, applied);
                rerolled++;
            }

            return $"Re-rolled gear on {rerolled} phantom(s).";
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
            return $"Squad '{squadName}' deleted.";
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
