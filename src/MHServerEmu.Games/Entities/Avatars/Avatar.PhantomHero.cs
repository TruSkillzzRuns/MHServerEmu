using System;
using System.Collections.Generic;
using MHServerEmu.Core.Collisions;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Memory;
using MHServerEmu.Core.Serialization;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.Common;
using MHServerEmu.Games.Entities.Inventories;
using MHServerEmu.Games.Entities.Locomotion;
using MHServerEmu.Games.Entities.PowerCollections;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.Events.Templates;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Powers;
using MHServerEmu.Games.Properties;
using MHServerEmu.Games.Regions;

namespace MHServerEmu.Games.Entities.Avatars
{
    /// <summary>
    /// Phantom-hero spawning: creates a Player entity in this Game (no
    /// PlayerConnection, no DB persistence) that owns a real AvatarPrototype
    /// entity from the actual playable roster. Bypasses the login pipeline —
    /// the Player exists only inside this Game's EntityManager.
    ///
    /// Why: the engine's Avatar.ApplyInitialReplicationState hard-requires the
    /// EntitySettings.InventoryLocation.ContainerId to resolve to a Player
    /// entity (Verify.IsNotNull at Avatar.cs:150). Without a Player owner the
    /// Avatar refuses to spawn. Hero-shaped Agent variants (CivilWar bosses,
    /// Skrull-hero variants, etc.) skip that check because they're Agents, not
    /// Avatars — which is why the old bot pool used them and why the names
    /// came out as "Skrull Luke Cage" etc. This gives us the real 64 heroes.
    /// </summary>
    public partial class Avatar
    {
        private static readonly Logger PhantomLogger = LogManager.CreateLogger();

        // No hardcoded roster — the pool is built at first spawn by iterating the
        // client's actual AvatarPrototype hierarchy (NoAbstractApprovedOnly, i.e.
        // concrete + shipping-approved entries). This makes the mod version-
        // agnostic: whatever heroes the currently-loaded client data ships with
        // become spawn candidates automatically. See EnsureResolvedPool().

        private static readonly object s_phantomDeckLock = new();
        private static readonly List<int> s_phantomDeck = new();
        private static int s_phantomDeckIdx;
        private static ulong s_phantomDbIdSeed = 0xB07_FADED_0000_0001UL;

        private readonly List<ulong> _phantomIds = new();
        private readonly List<ulong> _phantomPlayerIds = new();
        public int PhantomHeroCount => _phantomIds.Count;

        // Comic-book flavored random usernames. Kept short so nameplates fit.
        private static readonly string[] s_phantomAdjectives =
            { "Crimson", "Cosmic", "Silent", "Void", "Neon", "Feral", "Prime", "Shadow", "Solar", "Astral", "Rogue", "Onyx", "Phoenix", "Nova", "Iron", "Storm", "Cyber", "Ghost", "Wraith", "Phantom" };
        private static readonly string[] s_phantomNouns =
            { "Falcon", "Warden", "Reaper", "Nomad", "Sentinel", "Vector", "Specter", "Vanguard", "Sable", "Pulse", "Envoy", "Titan", "Arbiter", "Herald", "Blade", "Fury", "Strike", "Guard", "Reign", "Shade" };
        private static int s_phantomNameCounter;

        private static string NewPhantomUsername(MHServerEmu.Core.System.Random.GRandom rng)
        {
            string a = s_phantomAdjectives[rng.Next(0, s_phantomAdjectives.Length)];
            string n = s_phantomNouns[rng.Next(0, s_phantomNouns.Length)];
            int suffix = System.Threading.Interlocked.Increment(ref s_phantomNameCounter) % 1000;
            return $"{a}{n}{suffix:D3}";
        }

        // Follow-tick constants + scheduler state. Phantoms have no locomotion AI,
        // so they'd stay glued to their spawn spot. Every ~1s, if a phantom is more
        // than 1500u from the caller, we teleport it back with a random offset so
        // multiple phantoms spread out. Also fires a random offensive power at any
        // nearby hostile so they don't just stand around.
        private const float PhantomFollowMaxDistSq = 2500f * 2500f;
        private const float PhantomAttackRange = 1200f;
        private const float PhantomAttackRangeSq = PhantomAttackRange * PhantomAttackRange;
        // Wider search — phantom will walk to any hostile in this radius.
        private const float PhantomSearchRange = 3500f;
        private const float PhantomSearchRangeSq = PhantomSearchRange * PhantomSearchRange;
        // Tick twice as often so movement + attack feel snappy.
        private static readonly TimeSpan PhantomTickInterval = TimeSpan.FromMilliseconds(500);

        private readonly EventGroup _phantomPendingEvents = new();
        private readonly EventPointer<PhantomTickEvent> _phantomTick = new();

        private void SchedulePhantomTick()
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler == null) return;
            if (_phantomTick.IsValid) return;
            scheduler.ScheduleEvent(_phantomTick, PhantomTickInterval, _phantomPendingEvents);
            _phantomTick.Get().Initialize(this);
        }

        private void OnPhantomTick()
        {
            if (_phantomIds.Count == 0 || IsInWorld == false) return;

            Vector3 callerPos = RegionLocation.Position;
            var rng = Game.Random;
            List<ulong> stale = null;

            for (int i = 0; i < _phantomIds.Count; i++)
            {
                ulong id = _phantomIds[i];
                Avatar phantom = Game.EntityManager.GetEntity<Avatar>(id);
                if (phantom == null || phantom.IsDestroyed || phantom.IsInWorld == false)
                {
                    (stale ??= new List<ulong>()).Add(id);
                    continue;
                }

                // Leash: teleport back if stranded very far from caller.
                float distSq = Vector3.DistanceSquared2D(phantom.RegionLocation.Position, callerPos);
                if (distSq > PhantomFollowMaxDistSq)
                {
                    float angle = (float)(rng.NextDouble() * Math.PI * 2.0);
                    float radius = 200f + (float)(rng.NextDouble() * 600f);
                    Vector3 newPos = callerPos + new Vector3((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius, 0f);
                    Region r = phantom.Region;
                    if (r != null) newPos = RegionLocation.ProjectToFloor(r, newPos);
                    try
                    {
                        phantom.Locomotor?.Stop();
                        phantom.ChangeRegionPosition(newPos, null);
                    }
                    catch { /* keep ticking */ }
                }

                // Hunt: locomotor-walk toward the nearest hostile in a wider sweep,
                // then attack once in range. Locomotor.FollowEntity refreshes each
                // tick (250ms repath delay) so the phantom will keep advancing.
                try { UpdatePhantomHunt(phantom, rng); } catch { /* keep ticking */ }
            }

            if (stale != null)
                foreach (ulong id in stale) _phantomIds.Remove(id);

            if (_phantomIds.Count > 0)
                SchedulePhantomTick();
        }

        // One-time-per-phantom diagnostic set. Removed once attack is verified.
        private static readonly HashSet<ulong> s_phantomAttackLogged = new();

        // Revive-priority range — search a bit wider than combat range so
        // phantoms notice downed players from across a room.
        private const float PhantomReviveSearchRange = 4000f;
        private const float PhantomReviveSearchRangeSq = PhantomReviveSearchRange * PhantomReviveSearchRange;
        private const float PhantomReviveCastRange = 500f;
        private const float PhantomReviveCastRangeSq = PhantomReviveCastRange * PhantomReviveCastRange;

        private void UpdatePhantomHunt(Avatar phantom, MHServerEmu.Core.System.Random.GRandom rng)
        {
            Region region = phantom.Region;
            if (region == null || phantom.PowerCollection == null) return;

            Vector3 phantomPos = phantom.RegionLocation.Position;

            // Priority 1: revive any downed real Avatar within revive range. Real
            // avatars are still IsInWorld while downed (dead-but-revivable); we
            // filter to Avatar entities that are IsDead AND have a live
            // PlayerConnection (skips other phantoms). Nearest wins.
            Avatar downed = null;
            float downedDistSq = PhantomReviveSearchRangeSq;
            var reviveSphere = new Sphere(phantomPos, PhantomReviveSearchRange);
            var reviveCtx = new MHServerEmu.Games.Entities.EntityRegionSPContext(MHServerEmu.Games.Entities.EntityRegionSPContextFlags.PrimaryPartition);
            foreach (WorldEntity we in region.IterateEntitiesInVolume(reviveSphere, reviveCtx))
            {
                if (we is not Avatar candidate) continue;
                // Only skip the phantom itself — DO NOT skip the caller (this.Id).
                // The caller is the whole point: when the real player who spawned
                // the phantoms goes down, phantoms need to see them and rez.
                if (candidate.Id == phantom.Id) continue;
                if (candidate.IsDead == false) continue;
                // Real Avatar = has a live PlayerConnection. Phantoms don't
                // revive each other because their owner Player has PlayerConnection=null.
                Player candOwner = candidate.GetOwnerOfType<Player>();
                if (candOwner == null || candOwner.PlayerConnection == null) continue;
                float d = Vector3.DistanceSquared2D(candidate.RegionLocation.Position, phantomPos);
                if (d < downedDistSq) { downedDistSq = d; downed = candidate; }
            }
            if (downed != null)
            {
                // Walk to them if we're not in cast range yet.
                if (downedDistSq > PhantomReviveCastRangeSq)
                {
                    var reviveLoco = phantom.Locomotor;
                    if (reviveLoco != null)
                    {
                        var reviveOpts = new LocomotionOptions { RepathDelay = TimeSpan.FromMilliseconds(250) };
                        reviveLoco.FollowEntity(downed.Id, 50f, 50f, ref reviveOpts, false);
                    }
                }
                else
                {
                    // In cast range — fire the built-in resurrect-other power.
                    try { phantom.ResurrectOtherAvatar(downed); } catch { /* keep ticking */ }
                }
                return; // don't hunt while triaging a downed teammate
            }

            // Widest sweep so we start advancing on enemies before they're in
            // attack range. IterateEntitiesInVolume walks the region spatial
            // partition, cheap.
            var sweepSphere = new Sphere(phantomPos, PhantomSearchRange);
            var ctx = new MHServerEmu.Games.Entities.EntityRegionSPContext(MHServerEmu.Games.Entities.EntityRegionSPContextFlags.PrimaryPartition);

            WorldEntity nearest = null;
            float nearestDistSq = PhantomSearchRangeSq;
            foreach (WorldEntity we in region.IterateEntitiesInVolume(sweepSphere, ctx))
            {
                if (we == null || we.Id == phantom.Id || we.Id == Id) continue;
                if (we.IsDead || we.IsInWorld == false) continue;
                // Only Agents — filters out props/destructibles/spawner markers.
                if (we is not Agent) continue;
                if (phantom.IsHostileTo(we) == false) continue;
                float d = Vector3.DistanceSquared2D(we.RegionLocation.Position, phantomPos);
                if (d < nearestDistSq) { nearestDistSq = d; nearest = we; }
            }

            if (nearest == null)
            {
                phantom.Locomotor?.Stop();
                return;
            }

            // Always keep the Locomotor advancing toward the target — even when
            // we're inside attack range. Stopping while attacking was the reason
            // phantoms visually stood still: my previous tick called Stop() every
            // time nearestDistSq was in range, so they only ever ticked "stop,
            // cast, stop, cast" with no walking between. Now we walk in, stop only
            // if Locomotor reaches the target's radius, and fire the power
            // regardless — the engine cancels movement automatically while a
            // cast animation runs (Locomotor.Locomote respects ActivePower flags).
            var loco = phantom.Locomotor;
            if (loco != null)
            {
                var opts = new LocomotionOptions { RepathDelay = TimeSpan.FromMilliseconds(250) };
                // rangeEnd is the Locomotor's "close-enough" tolerance
                // (Locomotor.GetNextLocomotePosition line 669). Small → walks all
                // the way in. Real-player-style approach + cast.
                bool ok = loco.FollowEntity(nearest.Id, 50f, 50f, ref opts, false);
                if (s_phantomLocoLogged.Add(phantom.Id))
                {
                    PhantomLogger.Info($"[PhantomHero:Loco] {phantom} authoritative={phantom.IsMovementAuthoritative} simulated={phantom.IsSimulated} inWorld={phantom.IsInWorld} target={nearest.Id:X} dist={MathF.Sqrt(nearestDistSq):F0} FollowEntity returned={ok} locoEnabled={loco.IsEnabled} isMoving={loco.IsMoving} method={loco.Method} baseSpeed={loco.DefaultRunSpeed} hasPath={loco.HasPath} pathResult={loco.LastGeneratedPathResult} canMove={phantom.CanMove()}");
                }
            }

            // Fire an attack if we're within attack range, regardless of movement.
            if (nearestDistSq <= PhantomAttackRangeSq)
                TryPhantomAttack(phantom, nearest, nearestDistSq, rng);
        }

        private static readonly HashSet<ulong> s_phantomLocoLogged = new();

        private void TryPhantomAttack(Avatar phantom, WorldEntity target, float targetDistSq, MHServerEmu.Core.System.Random.GRandom rng)
        {
            if (target == null || phantom.PowerCollection == null) return;
            Vector3 phantomPos = phantom.RegionLocation.Position;

            // Refill Endurance so InsufficientEndurance doesn't gate every non-basic
            // power. Phantom has no resource regen wiring; we just keep the pool at
            // ceiling. Loop across every ManaType the avatar declares so multi-pool
            // heroes (Iron Man / Nova / Storm) all get topped up.
            foreach (PrimaryResourceManaBehaviorPrototype manaBehavior in phantom.GetPrimaryResourceManaBehaviors())
            {
                var manaType = manaBehavior.ManaType;
                float max = phantom.Properties[PropertyEnum.EnduranceMax, manaType];
                if (max > 0) phantom.Properties[PropertyEnum.Endurance, manaType] = max;
            }

            float targetDist = MathF.Sqrt(targetDistSq);

            // Build the candidate list with real prioritization instead of pure
            // reservoir sampling.
            //
            //   Filters (hard rejects):
            //     - is a Movement / Travel / Passive / Toggled power
            //     - is not NormalPower category
            //     - name ends in "Ultimate.prototype" (cinematic, returns FullscreenMovie)
            //     - power.GetRange() < target distance (would return OutOfPosition)
            //     - power is currently on cooldown
            //
            //   Score = cooldown duration in ms (used as a proxy for hit weight —
            //   powers with longer cooldowns are baked bigger, and it's the only
            //   universal numeric signal we can get without a per-hero damage table).
            //
            //   Pick strategy: sort survivors by score desc, weighted-random among
            //   the top 5. Favors real cooldown-worthy hits while still varying,
            //   and always fires the basic (0 cd) when nothing bigger is available.
            var candidates = ListPool<(PrototypeId, long)>.Instance.Get();
            try
            {
                foreach (var kvp in phantom.PowerCollection)
                {
                    PowerCollectionRecord rec = kvp.Value;
                    Power power = rec?.Power;
                    if (power == null) continue;
                    PowerPrototype pp = power.Prototype;
                    if (pp == null) continue;
                    if (pp is MovementPowerPrototype) continue;
                    if (pp.PowerCategory != PowerCategoryType.NormalPower) continue;
                    if (pp.Activation == PowerActivationType.Passive) continue;
                    if (pp.IsToggled) continue;
                    if (pp.IsTravelPower) continue;

                    string pName = pp.DataRef.GetName() ?? string.Empty;
                    if (pName.EndsWith("Ultimate.prototype", StringComparison.Ordinal)) continue;

                    float pRange = power.GetRange();
                    if (pRange > 0f && pRange + 50f < targetDist) continue;

                    if (power.IsOnCooldown()) continue;

                    long cdMs = (long)power.GetCooldownDuration().TotalMilliseconds;
                    candidates.Add((rec.PowerPrototypeRef, cdMs));
                }

                if (candidates.Count == 0) return;

                // Sort by cooldown desc — biggest hitter first.
                candidates.Sort(static (a, b) => b.Item2.CompareTo(a.Item2));

                // Take top 5 (or fewer). Weighted-random pick — weight = 1 + cooldownMs/1000
                // so a 5s power is ~6x more likely than a basic (0s) attack.
                int take = Math.Min(5, candidates.Count);
                long totalWeight = 0;
                for (int i = 0; i < take; i++) totalWeight += 1 + (candidates[i].Item2 / 1000);
                long roll = ((long)rng.NextDouble() * totalWeight * 1000L) % Math.Max(1, totalWeight);
                if (roll < 0) roll = -roll;

                PrototypeId chosenPower = candidates[0].Item1;
                long acc = 0;
                for (int i = 0; i < take; i++)
                {
                    long w = 1 + (candidates[i].Item2 / 1000);
                    acc += w;
                    if (roll < acc) { chosenPower = candidates[i].Item1; break; }
                }
                candidates.Clear();

                var settings = new PowerActivationSettings(target.Id, target.RegionLocation.Position, phantomPos)
                { Flags = PowerActivationSettingsFlags.NotifyOwner };
                var result = phantom.ActivatePower(chosenPower, ref settings);
                if (s_phantomAttackLogged.Add(phantom.Id))
                {
                    Player phantomOwner = phantom.GetOwnerOfType<Player>();
                    string ownerState = phantomOwner == null ? "OWNER=null" :
                        $"owner={phantomOwner} isMoviePlaying={phantomOwner.IsFullscreenMoviePlaying} isLoadingScreen={phantomOwner.IsOnLoadingScreen} isFullscreenObscured={phantomOwner.IsFullscreenObscured}";
                    PhantomLogger.Info($"[PhantomHero:Attack] {phantom} → target={target} power={chosenPower.GetName()} result={result} | {ownerState}");
                }
            }
            finally { ListPool<(PrototypeId, long)>.Instance.Return(candidates); }
        }

        private class PhantomTickEvent : CallMethodEvent<Avatar>
        {
            protected override CallbackDelegate GetCallback() => (avatar) => avatar.OnPhantomTick();
        }

        // Cached list of resolvable pool entries. Filled lazily on first call
        // so we can log which paths fail once and pull them out of the rotation.
        private static readonly object s_phantomResolvedLock = new();
        private static List<PrototypeId> s_phantomResolved;

        private static void EnsureResolvedPool()
        {
            lock (s_phantomResolvedLock)
            {
                if (s_phantomResolved != null) return;
                var resolved = new List<PrototypeId>(64);
                // Same iteration the login pipeline (PlayerConnection), the
                // equipment tables, and PowerCommands use to get "every real
                // playable hero for this client." Guarantees the pool tracks
                // the loaded client version exactly.
                foreach (PrototypeId avatarRef in DataDirectory.Instance
                    .IteratePrototypesInHierarchy<AvatarPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
                {
                    if (avatarRef == PrototypeId.Invalid) continue;
                    if (avatarRef.As<AvatarPrototype>() == null) continue;
                    resolved.Add(avatarRef);
                }
                s_phantomResolved = resolved;
                PhantomLogger.Info($"[PhantomHero] pool built from client data: {resolved.Count} playable avatars");
            }
        }

        private PrototypeId NextPhantomHeroRef()
        {
            EnsureResolvedPool();
            lock (s_phantomDeckLock)
            {
                if (s_phantomResolved.Count == 0) return PrototypeId.Invalid;
                if (s_phantomDeckIdx >= s_phantomDeck.Count)
                {
                    s_phantomDeck.Clear();
                    for (int i = 0; i < s_phantomResolved.Count; i++) s_phantomDeck.Add(i);
                    for (int i = s_phantomDeck.Count - 1; i > 0; i--)
                    {
                        int j = Game.Random.Next(0, i + 1);
                        (s_phantomDeck[i], s_phantomDeck[j]) = (s_phantomDeck[j], s_phantomDeck[i]);
                    }
                    s_phantomDeckIdx = 0;
                }
                int idx = s_phantomDeck[s_phantomDeckIdx++];
                return s_phantomResolved[idx];
            }
        }

        /// <summary>
        /// Spawns a phantom hero (real AvatarPrototype) at a random offset from
        /// this avatar. Returns the Avatar entity id, or 0 with a reason in
        /// <paramref name="error"/>.
        /// </summary>
        public ulong SpawnPhantomHero(int levelOverride, string username, out string error)
        {
            error = null;
            if (IsInWorld == false) { error = "avatar not in world"; return 0; }

            Region region = Region;
            if (region == null) { error = "no region"; return 0; }

            PrototypeId avatarRef = NextPhantomHeroRef();
            if (avatarRef == PrototypeId.Invalid) { error = "hero ref resolve failed"; return 0; }

            AvatarPrototype avatarProto = avatarRef.As<AvatarPrototype>();
            if (avatarProto == null) { error = "not an AvatarPrototype"; return 0; }

            // Step 1: phantom Player entity, no PlayerConnection. The Player
            // is created inside this Game's EntityManager so InventoryLocation
            // ContainerId lookups (used by Avatar.ApplyInitialReplicationState)
            // resolve locally without touching the login/DB path.
            ulong phantomDbId = System.Threading.Interlocked.Increment(ref s_phantomDbIdSeed);
            // If caller didn't supply a name, mint a comic-book-flavored one so
            // nameplates read "CrimsonFalcon042" instead of "Bot001".
            if (string.IsNullOrEmpty(username))
                username = NewPhantomUsername(Game.Random);
            Player phantomPlayer;
            using (var playerSettings = ObjectPoolManager.Instance.Get<EntitySettings>())
            {
                playerSettings.DbGuid = phantomDbId;
                playerSettings.EntityRef = GameDatabase.GlobalsPrototype.DefaultPlayer;
                playerSettings.OptionFlags = EntitySettingsOptionFlags.PopulateInventories;
                playerSettings.PlayerConnection = null; // Player.SendMessage is null-conditional; OK.
                playerSettings.PlayerName = username;
                playerSettings.ArchiveSerializeType = ArchiveSerializeType.Database;
                playerSettings.ArchiveData = null; // fresh account, triggers new-account init

                phantomPlayer = Game.EntityManager.CreateEntity(playerSettings) as Player;
            }
            if (phantomPlayer == null) { error = "phantom Player entity create failed"; return 0; }

            // Step 2: create the Avatar as a child of the phantom Player. Uses
            // the same Player.CreateAvatar helper the real login path calls
            // (PlayerConnection.LoadFromDBAccount:222).
            Avatar phantomAvatar = phantomPlayer.CreateAvatar(avatarRef);
            if (phantomAvatar == null) { error = $"CreateAvatar failed for {avatarRef.GetName()}"; DestroyPhantomPlayer(phantomPlayer); return 0; }

            // Step 3: move avatar from AvatarLibrary to AvatarInPlay so it can
            // enter the world. Same handshake the real client's SwitchAvatar
            // NetMessage triggers (PlayerConnection.LoadFromDBAccount:246-249).
            Inventory avatarInPlay = phantomPlayer.GetInventory(InventoryConvenienceLabel.AvatarInPlay);
            if (avatarInPlay == null) { error = "AvatarInPlay inventory missing"; DestroyPhantomPlayer(phantomPlayer); return 0; }
            InventoryResult moveResult = phantomAvatar.ChangeInventoryLocation(avatarInPlay);
            if (moveResult != InventoryResult.Success) { error = $"ChangeInventoryLocation failed: {moveResult}"; DestroyPhantomPlayer(phantomPlayer); return 0; }

            // Step 4: level + resources so the avatar has real stats.
            int effectiveLevel = levelOverride > 0 ? levelOverride : CharacterLevel;
            phantomAvatar.InitializeLevel(effectiveLevel);
            phantomAvatar.CombatLevel = effectiveLevel;
            phantomAvatar.ResetResources(false);

            // Step 5: pick a spawn point near the caller and enter the world.
            var rng = Game.Random;
            float ang = (float)(rng.NextDouble() * Math.PI * 2.0);
            float radius = 300f + (float)(rng.NextDouble() * 800f);
            Vector3 origin = RegionLocation.Position;
            Vector3 candidate = origin + new Vector3((float)Math.Cos(ang) * radius, (float)Math.Sin(ang) * radius, 0f);
            Vector3 spawnPos = RegionLocation.ProjectToFloor(region, candidate);
            Orientation spawnOri = RegionLocation.Orientation;

            // Mark phantom Player + Avatar as IsInGame so real clients' AOI
            // GetNewInterestPolicies passes the (entity.IsInGame == false) gate
            // and actually broadcasts them. Player.EnterGame cascades into
            // contained entities (avatar + inventories) via Entity.EnterGame.
            // Any downstream init that assumes a real client is trapped; the
            // essential IsInGame flag lands on entity-level first.
            try { phantomPlayer.EnterGame(); }
            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] phantomPlayer.EnterGame() partial: {ex.Message}"); }

            // Clear the loading-screen state that Player.Initialize (line 233 —
            // QueueLoadingScreen(Invalid)) sets unconditionally on every Player.
            // Real clients ack it and it clears; the phantom has no client to ack.
            // Left set, it makes IsFullscreenObscured=true → every power activation
            // returns PowerUseResult.FullscreenMovie via Agent.CanTriggerPower line 500.
            try { phantomPlayer.OnLoadingScreenFinished(); }
            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] OnLoadingScreenFinished failed: {ex.Message}"); }

            if (phantomAvatar.EnterWorld(region, spawnPos, spawnOri) == false)
            {
                error = "EnterWorld returned false";
                DestroyPhantomPlayer(phantomPlayer);
                return 0;
            }

            // Force AOI broadcast to every real client so they see the phantom.
            // Without this the phantom exists server-side but no NetMessage tells
            // clients about it — invisible bot.
            try
            {
                // Broadcast phantom Player first so client can resolve avatar owner.
                phantomPlayer.UpdateInterestPolicies(true, null);
                phantomAvatar.UpdateInterestPolicies(true, null);

                // Diagnostic: log what each real player's AOI decided for the phantom.
                foreach (Player realPlayer in new PlayerIterator(Game))
                {
                    if (realPlayer.PlayerConnection == null) continue;
                    var aoi = realPlayer.AOI;
                    if (aoi == null) { PhantomLogger.Info($"[PhantomHero:AOI] real={realPlayer} AOI=null"); continue; }
                    bool avatarInterested = aoi.InterestedInEntity(phantomAvatar.Id);
                    bool playerInterested = aoi.InterestedInEntity(phantomPlayer.Id);
                    Vector3 phantomPos = phantomAvatar.RegionLocation.Position;
                    Vector3 realPos = realPlayer.CurrentAvatar?.RegionLocation.Position ?? Vector3.Zero;
                    float dist = Vector3.Distance2D(phantomPos, realPos);
                    PhantomLogger.Info($"[PhantomHero:AOI] real={realPlayer.GetName()} sameRegion={realPlayer.GetRegion() == region} avatarInterested={avatarInterested} playerInterested={playerInterested} dist={dist:F0} phantomPos={phantomPos.ToStringNames()} realPos={realPos.ToStringNames()} inWorld={phantomAvatar.IsInWorld} cell={phantomAvatar.Cell?.Id.ToString() ?? "null"}");
                }
            }
            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] AOI broadcast failed: {ex.Message}"); }

            // Invulnerable so the phantom can't be downed by mob damage while
            // it's just standing there. Client-controlled Avatars have a revive
            // flow the phantom can't drive — a downed phantom would be dead
            // weight until manually cleared.
            phantomAvatar.Properties[PropertyEnum.Invulnerable] = true;

            // Full-BiS-omega-set-flavored damage scaling. Tuned down from the
            // first pass so bosses still take a moment. If you want more or less,
            // this is the whole knob.
            // - DamageMult: direct multiplier on outgoing damage.
            // - DamagePctBonus: percent bonus on top of that multiplier.
            // - DamageRating: feeds the combat-globals scaling curve
            //   (WorldEntity.cs line 2918); ~100 rating ≈ 10% damage.
            phantomAvatar.Properties[PropertyEnum.DamageMult] = 3f;
            phantomAvatar.Properties[PropertyEnum.DamagePctBonus] = 1.5f;
            phantomAvatar.Properties[PropertyEnum.DamageRating] = 5000f;

            // Server-authoritative movement — real avatars have IsMovementAuthoritative=false
            // because the client drives them. Phantoms have no client, so we must
            // flip it, or Locomotor.FollowEntity produces no visible walking on
            // the real client's screen.
            phantomAvatar.IsPhantomHero = true;

            // Book-keeping so DespawnAllPhantomHeroes can clean up.
            _phantomIds.Add(phantomAvatar.Id);
            _phantomPlayerIds.Add(phantomPlayer.Id);
            SchedulePhantomTick();

            PhantomLogger.Info($"[PhantomHero] {this} spawned '{avatarRef.GetName()}' (avatarId 0x{phantomAvatar.Id:X}, phantomPlayerId 0x{phantomPlayer.Id:X}) at {spawnPos.ToStringNames()} level {effectiveLevel}");
            return phantomAvatar.Id;
        }

        // ================================================================
        //  Off-thread entry point used by the WebFrontend HTTP handler.
        //  SpawnPhantomHero touches Game.Current (a thread-static) via
        //  Player.EnterGame → CheckMapDiscoveryDataExpiration, so calling it
        //  from a ThreadPool thread NREs on the first line. Schedule a
        //  zero-delay event on the game's own scheduler so the actual spawn
        //  runs inside the game tick.
        // ================================================================

        private sealed class WebSpawnEvent : CallMethodEventParam2<Avatar, int, int>
        {
            protected override CallbackDelegate GetCallback() => static (avatar, count, level) =>
            {
                for (int i = 0; i < count; i++)
                    avatar.SpawnPhantomHero(level, null, out _);
            };
        }

        private sealed class WebClearEvent : CallMethodEvent<Avatar>
        {
            protected override CallbackDelegate GetCallback() => static (avatar) => avatar.DespawnAllPhantomHeroes();
        }

        private readonly EventPointer<WebSpawnEvent> _webSpawnEvent = new();
        private readonly EventPointer<WebClearEvent> _webClearEvent = new();
        private readonly EventGroup _webEvents = new();

        /// <summary>
        /// Thread-safe web-facing spawn. Enqueues one game-thread event that
        /// spawns <paramref name="count"/> phantoms at <paramref name="level"/>.
        /// Returns immediately; poll <see cref="PhantomHeroCount"/> to observe.
        /// </summary>
        public void SpawnPhantomHeroesFromWeb(int count, int level)
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler == null) return;
            if (_webSpawnEvent.IsValid) scheduler.CancelEvent(_webSpawnEvent);
            scheduler.ScheduleEvent(_webSpawnEvent, TimeSpan.Zero, _webEvents);
            _webSpawnEvent.Get().Initialize(this, count, level);
        }

        /// <summary>Thread-safe clear.</summary>
        public void DespawnAllPhantomHeroesFromWeb()
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler == null) return;
            if (_webClearEvent.IsValid) scheduler.CancelEvent(_webClearEvent);
            scheduler.ScheduleEvent(_webClearEvent, TimeSpan.Zero, _webEvents);
            _webClearEvent.Get().Initialize(this);
        }

        /// <summary>Destroys every phantom hero this caller has spawned.</summary>
        public int DespawnAllPhantomHeroes()
        {
            int removed = 0;
            foreach (ulong avatarId in _phantomIds)
            {
                try
                {
                    Avatar av = Game.EntityManager.GetEntity<Avatar>(avatarId);
                    if (av == null) continue;
                    if (av.IsInWorld) av.ExitWorld();
                    av.Destroy();
                    removed++;
                }
                catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] despawn 0x{avatarId:X} failed: {ex.Message}"); }
            }
            foreach (ulong playerId in _phantomPlayerIds)
            {
                try
                {
                    Player p = Game.EntityManager.GetEntity<Player>(playerId);
                    if (p == null) continue;
                    // Player.Destroy walks GuildManager, MissionManager, etc. —
                    // all of which touch AOI/PlayerConnection state we don't have.
                    // ExitGame then base Destroy is enough to unregister without
                    // going through the guild/mission cleanup paths.
                    if (p.IsInGame) p.ExitGame();
                    p.Destroy();
                }
                catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] phantom-player 0x{playerId:X} destroy failed: {ex.Message}"); }
            }
            foreach (ulong id in _phantomIds) { s_phantomAttackLogged.Remove(id); s_phantomLocoLogged.Remove(id); }
            _phantomIds.Clear();
            _phantomPlayerIds.Clear();
            return removed;
        }

        private void DestroyPhantomPlayer(Player p)
        {
            if (p == null) return;
            try { p.Destroy(); } catch { /* best effort cleanup on partial init */ }
        }
    }
}
