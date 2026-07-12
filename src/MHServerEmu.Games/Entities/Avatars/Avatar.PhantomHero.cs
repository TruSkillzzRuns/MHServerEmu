using System;
using System.Collections.Generic;
using MHServerEmu.Core.Collisions;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Memory;
using MHServerEmu.Core.Serialization;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.Common;
using MHServerEmu.Games.Entities.Inventories;
using MHServerEmu.Games.Entities.Items;
using MHServerEmu.Games.Entities.Locomotion;
using MHServerEmu.Games.Entities.PowerCollections;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.Events.Templates;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Loot;
using MHServerEmu.Games.Navi;
using MHServerEmu.Games.Network;
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

        // Ownership lists moved to Player (see Player.PhantomHero.cs). The
        // Avatar shell no longer owns anything — every operation delegates to
        // GetOwnerOfType<Player>() so avatar swaps and region hops don't
        // orphan phantoms. Left in place for source-compat: PhantomHeroCount
        // + the Ids reader now pull straight from the Player's list.
        private Player PhantomHost => GetOwnerOfType<Player>();
        public int PhantomHeroCount => PhantomHost?.PhantomHeroCount ?? 0;
        private IReadOnlyList<ulong> PhantomIds => PhantomHost?.PhantomAvatarIds ?? (IReadOnlyList<ulong>)System.Array.Empty<ulong>();

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
        // Tighter than the old 2500u — phantoms should read as "with you"
        // not "vaguely nearby." 1500u ≈ two-thirds of a screen at default
        // zoom; if they wander beyond that the leash snaps them back.
        private const float PhantomFollowMaxDistSq = 1500f * 1500f;
        // Stuck detection: if the phantom's position barely changes across
        // this many ticks (500ms each) they're either wall-clipped or
        // pathed into an out-of-bounds corner — force a teleport back to
        // caller.
        private const int PhantomStuckTickThreshold = 4;      // 2 seconds
        private const float PhantomStuckMoveEpsilonSq = 40f * 40f;
        private static readonly Dictionary<ulong, (Vector3 lastPos, int stuckTicks)> s_phantomStuckTrack = new();
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
            Player host = PhantomHost;
            if (host == null || host.PhantomHeroCount == 0 || IsInWorld == false) return;

            Vector3 callerPos = RegionLocation.Position;
            var rng = Game.Random;
            List<ulong> stale = null;
            var ids = host.PhantomAvatarIds; // snapshot count for stable iteration

            int callerLevel = CharacterLevel;
            for (int i = 0; i < ids.Count; i++)
            {
                ulong id = ids[i];
                Avatar phantom = Game.EntityManager.GetEntity<Avatar>(id);
                if (phantom == null || phantom.IsDestroyed || phantom.IsInWorld == false)
                {
                    (stale ??= new List<ulong>()).Add(id);
                    continue;
                }

                // Level sync: if the human has levelled since the last tick,
                // bring phantoms up to match so a lvl-15 hero doesn't drag
                // lvl-15 phantoms into a lvl-60 mission. Runs every 500ms;
                // InitializeLevel is a no-op internally when the new level
                // equals the current level, so this is cheap on stable
                // ticks. Only levels UP — we don't downlevel phantoms when
                // the human hero-swaps to a lower-level character.
                //
                // Phantoms spawned with an explicit level lock
                // (`!phantom spawn N L`) are skipped — the user asked for
                // a specific level and we honour it forever.
                if (callerLevel > 0 && phantom.CharacterLevel < callerLevel && host.IsPhantomLevelLocked(phantom.Id) == false)
                {
                    try
                    {
                        phantom.InitializeLevel(callerLevel);
                        phantom.CombatLevel = callerLevel;
                        // Rescale damage buffs so a level-1 phantom that
                        // just autolevelled to lvl-15 stops hitting like
                        // lvl-1 (or overshoots — the anchor curve tracks
                        // level, not spawn-time snapshot).
                        ApplyPhantomDamageScaling(phantom, callerLevel);
                        // Refresh the stored descriptor so cross-region
                        // migration re-spawns at the new level, not the
                        // stale spawn-time value.
                        host.UpdatePhantomLevel(phantom.Id, callerLevel);
                    }
                    catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] level sync {phantom.Id:X} → {callerLevel} failed: {ex.Message}"); }
                }

                // Stuck-power watchdog: a channeled power (or channel-style
                // ultimate) never ends for a phantom — no client exists to
                // release the button — and while ActivePowerRef is set,
                // every attack and revive returns PowerInProgress. Force-end
                // any power that's been active for 10 consecutive ticks (5s).
                PrototypeId activePowerRef = phantom.ActivePowerRef;
                if (activePowerRef != PrototypeId.Invalid)
                {
                    if (s_phantomActivePowerTrack.TryGetValue(phantom.Id, out var powerTrack) && powerTrack.powerRef == activePowerRef)
                    {
                        int ticks = powerTrack.ticks + 1;
                        if (ticks >= PhantomStuckPowerTicks)
                        {
                            try
                            {
                                Power stuckPower = phantom.PowerCollection?.GetPower(activePowerRef);
                                stuckPower?.EndPower(EndPowerFlags.ExplicitCancel | EndPowerFlags.Force);
                                PhantomLogger.Info($"[PhantomHero:Watchdog] force-ended stuck power {activePowerRef.GetName()} on {phantom.Id:X} after {ticks * 500}ms");
                            }
                            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Watchdog] EndPower failed on {phantom.Id:X}: {ex.Message}"); }
                            s_phantomActivePowerTrack.Remove(phantom.Id);
                        }
                        else s_phantomActivePowerTrack[phantom.Id] = (activePowerRef, ticks);
                    }
                    else s_phantomActivePowerTrack[phantom.Id] = (activePowerRef, 1);
                }
                else s_phantomActivePowerTrack.Remove(phantom.Id);

                // Stuck detection: if the phantom's position barely moved
                // this tick despite the Locomotor being set to move, count
                // it. After N consecutive stuck ticks assume they're
                // wall-clipped or pathed out of bounds and force-leash.
                Vector3 curPos = phantom.RegionLocation.Position;
                bool forceLeash = false;
                if (s_phantomStuckTrack.TryGetValue(phantom.Id, out var stuckState))
                {
                    float movedSq = Vector3.DistanceSquared2D(curPos, stuckState.lastPos);
                    bool wantsToMove = phantom.Locomotor != null && phantom.Locomotor.IsMoving;
                    int newStuck = (wantsToMove && movedSq < PhantomStuckMoveEpsilonSq) ? stuckState.stuckTicks + 1 : 0;
                    if (newStuck >= PhantomStuckTickThreshold) forceLeash = true;
                    s_phantomStuckTrack[phantom.Id] = (curPos, forceLeash ? 0 : newStuck);
                }
                else s_phantomStuckTrack[phantom.Id] = (curPos, 0);

                // Leash: teleport back if stranded far or wall-stuck.
                float distSq = Vector3.DistanceSquared2D(curPos, callerPos);
                if (distSq > PhantomFollowMaxDistSq || forceLeash)
                {
                    Region r = phantom.Region;
                    Vector3 leashPos = ChoosePhantomLeashPos(r, callerPos, rng, phantom.Bounds.Radius);
                    try
                    {
                        phantom.Locomotor?.Stop();
                        phantom.ChangeRegionPosition(leashPos, null);
                        s_phantomStuckTrack[phantom.Id] = (leashPos, 0);
                    }
                    catch { /* keep ticking */ }
                }

                // Hunt: locomotor-walk toward the nearest hostile in a wider sweep,
                // then attack once in range. Locomotor.FollowEntity refreshes each
                // tick (250ms repath delay) so the phantom will keep advancing.
                try { UpdatePhantomHunt(phantom, rng); } catch { /* keep ticking */ }
            }

            if (stale != null)
                foreach (ulong id in stale) host.UnregisterPhantom(id);

            if (host.PhantomHeroCount > 0)
                SchedulePhantomTick();
        }

        // One-time-per-phantom diagnostic set. Removed once attack is verified.
        private static readonly HashSet<ulong> s_phantomAttackLogged = new();
        // One-time-per-(phantom,target) diagnostic set for the Attack log so a
        // new boss gets its state dumped even after this phantom has already
        // logged an attack on a mob.
        private static readonly HashSet<ulong> s_phantomAttackTargetLogged = new();

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

            // Mid-cast: stand still, like a real player. Without this the
            // tick kept re-issuing FollowEntity every 500ms while a power
            // was executing, so phantoms slid across the ground through
            // their cast animations. Any new attack would return
            // PowerInProgress anyway, and the stuck-power watchdog (in
            // OnPhantomTick, which runs before this) still force-ends
            // channels that never finish — so skipping the whole hunt for
            // the duration of a cast is safe.
            if (phantom.IsExecutingPower)
            {
                var castLoco = phantom.Locomotor;
                if (castLoco != null && castLoco.IsMoving)
                    castLoco.Stop();
                return;
            }

            Vector3 phantomPos = phantom.RegionLocation.Position;

            // Priority 1: revive any downed real Avatar within revive range. Real
            // avatars are still IsInWorld while downed (dead-but-revivable); we
            // filter to Avatar entities that are IsDead AND have a live
            // PlayerConnection (skips other phantoms). Nearest wins.
            Avatar downed = null;
            float downedDistSq = PhantomReviveSearchRangeSq;
            var reviveSphere = new Sphere(phantomPos, PhantomReviveSearchRange);
            var reviveCtx = new MHServerEmu.Games.Entities.EntityRegionSPContext(MHServerEmu.Games.Entities.EntityRegionSPContextFlags.PrimaryPartition);

            // Direct check on the caller first — this covers the case where the
            // human died far from the phantom (out of the 4000u sphere) or
            // during a scripted death animation where the AOI doesn't return
            // them from IterateEntitiesInVolume. The caller is the phantom's
            // owner, so we always know exactly who to look for.
            if (this.IsDead && this.IsInWorld && this.Region == region)
            {
                downed = this;
                downedDistSq = Vector3.DistanceSquared2D(this.RegionLocation.Position, phantomPos);
            }
            else foreach (WorldEntity we in region.IterateEntitiesInVolume(reviveSphere, reviveCtx))
            {
                if (we is not Avatar candidate) continue;
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

            // Build a full sorted candidate list of hostile Agents instead of just
            // "the nearest one." Some encounters (dramatic-entrance bosses, mission
            // untargetable phases, out-of-line-of-sight bosses on elevated
            // platforms) leave the nearest hostile in a state where
            // Power.IsValidTarget silently rejects — and if that's the only entity
            // we track, the phantom locks onto it, ActivatePower burns the
            // cooldown returning BadTarget, and the phantom stands still for the
            // whole fight. With a list we fall through to the next-nearest until
            // one accepts the attack.
            var candidates = new List<(WorldEntity we, float distSq)>();
            List<(WorldEntity we, float distSq, string reason)> diagRejected = null;
            bool diagWant = ShouldEmitPhantomDiag(phantom.Id);
            long nowMsSweep = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
            foreach (WorldEntity we in region.IterateEntitiesInVolume(sweepSphere, ctx))
            {
                if (we == null || we.Id == phantom.Id || we.Id == Id) continue;
                if (we.IsDead || we.IsInWorld == false) continue;
                // Only Agents — filters out props/destructibles/spawner markers.
                if (we is not Agent) continue;
                if (phantom.IsHostileTo(we) == false) continue;
                float d = Vector3.DistanceSquared2D(we.RegionLocation.Position, phantomPos);
                // Skip anything the engine won't accept as a valid target yet.
                // Dramatic-entrance bosses (Doom, Loki, terminal bosses...) spawn
                // with IsDormant=true until their intro cutscene wakes them
                // (Agent.cs:97 + WakeEndCallback line 3119). While dormant,
                // IsAffectedByPowersInternal returns false so
                // Power.IsValidTarget rejects the attack (Power.Validation.cs
                // line 313).
                if (we.IsDormant || we.IsUntargetable || we.IsUnaffectable)
                {
                    if (diagWant) (diagRejected ??= new()).Add((we, d,
                        we.IsDormant ? "dormant" : we.IsUntargetable ? "untargetable" : "unaffectable"));
                    continue;
                }
                // Per-phantom blacklist: if we tried this target recently and
                // ActivatePower returned non-Success, skip for the blacklist window.
                // Lets phantoms rotate through other hostiles while a cutscene
                // boss finishes waking up, and lets the boss get picked up again
                // on the next tick after the blacklist expires.
                if (IsTargetBlacklisted(phantom.Id, we.Id, nowMsSweep))
                {
                    if (diagWant) (diagRejected ??= new()).Add((we, d, "blacklist"));
                    continue;
                }
                candidates.Add((we, d));
            }
            candidates.Sort(static (a, b) => a.distSq.CompareTo(b.distSq));

            if (diagWant && (candidates.Count == 0 || diagRejected != null))
                DumpPhantomHuntDiag(phantom, phantomPos,
                    candidates.Count > 0 ? candidates[0].we : null,
                    candidates.Count > 0 ? candidates[0].distSq : 0f,
                    diagRejected, region, sweepSphere, ctx);

            if (candidates.Count == 0)
            {
                phantom.Locomotor?.Stop();
                return;
            }

            // Advance toward the closest survivor for movement, but for the
            // attack try each in order — the closest might be a Living Laser
            // waiting on his cutscene entry that rejects power activation for a
            // few seconds, while the actual boss is right behind him and
            // attackable now. Without the fallback the phantom stood on the
            // first target and never fired.
            WorldEntity nearest = candidates[0].we;
            float nearestDistSq = candidates[0].distSq;

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
                // Follow only as close as the phantom's widest usable power's
                // range. Ranged heroes (Storm, Iron Man, Rocket) stop at
                // projectile range and start casting; melee heroes (Thing,
                // Colossus) keep walking in to 50u. Without this every
                // phantom sprinted into point-blank on every target — visually
                // wrong for ranged kits, and left the phantom stuck at 50u
                // firing projectiles the client had to render at melee.
                float followStopDist = ComputePhantomFollowStopDist(phantom, nearest);
                bool ok = loco.FollowEntity(nearest.Id, followStopDist, followStopDist, ref opts, false);
                if (s_phantomLocoLogged.Add(phantom.Id))
                {
                    PhantomLogger.Info($"[PhantomHero:Loco] {phantom} authoritative={phantom.IsMovementAuthoritative} simulated={phantom.IsSimulated} inWorld={phantom.IsInWorld} target={nearest.Id:X} dist={MathF.Sqrt(nearestDistSq):F0} FollowEntity returned={ok} locoEnabled={loco.IsEnabled} isMoving={loco.IsMoving} method={loco.Method} baseSpeed={loco.DefaultRunSpeed} hasPath={loco.HasPath} pathResult={loco.LastGeneratedPathResult} canMove={phantom.CanMove()}");
                }
            }

            // Only fire an attack when the phantom is settled — either the
            // Locomotor has arrived (or is close enough that the last step
            // is trivial), or the target is inside melee range. Firing while
            // FollowEntity is mid-path produces the "skating" look: the
            // cast animation cancels walking mid-stride but position keeps
            // advancing, so the character glides without a walk cycle.
            const float PhantomMeleeSq = 400f * 400f;
            bool arrived = loco == null || loco.IsMoving == false;
            bool inMelee = nearestDistSq <= PhantomMeleeSq;
            if ((arrived || inMelee) && nearestDistSq <= PhantomAttackRangeSq)
            {
                // Per-phantom attack cooldown — prevents the 2 Hz tick from
                // burst-firing 2 attacks per second. Real players average
                // closer to 1 attack per 800-1200 ms after animation locks.
                long now = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
                if (s_phantomNextAttackMs.TryGetValue(phantom.Id, out long nextAt) == false || now >= nextAt)
                {
                    // Try candidates in distance order. First one that
                    // ActivatePower accepts wins. Others get blacklisted only
                    // when they actually get an activate attempt — we don't
                    // pre-check IsValidTarget because that would double the
                    // per-tick work for the common case where the nearest is
                    // fine.
                    bool fired = false;
                    int maxTries = Math.Min(5, candidates.Count);
                    for (int i = 0; i < maxTries; i++)
                    {
                        WorldEntity tryTarget = candidates[i].we;
                        float tryDistSq = candidates[i].distSq;
                        if (tryDistSq > PhantomAttackRangeSq) break; // rest are out of range
                        PowerUseResult r = TryPhantomAttack(phantom, tryTarget, tryDistSq, rng);
                        if (r == PowerUseResult.Success)
                        {
                            fired = true;
                            // Successful hit — make sure this target isn't
                            // blacklisted from a stale prior tick.
                            ClearTargetBlacklist(phantom.Id, tryTarget.Id);
                            break;
                        }
                        // BadTarget / InsufficientEndurance / TargetIsMissing /
                        // OutOfPosition / FullscreenMovie — blacklist this
                        // target for 3 seconds so the sweep skips it while
                        // whatever transient state clears.
                        BlacklistTarget(phantom.Id, tryTarget.Id, nowMsSweep);
                    }
                    if (!fired && diagWant)
                        PhantomLogger.Info($"[PhantomHero:Attack] {phantom} all {maxTries} candidates rejected the attack — sweep found {candidates.Count} hostile(s), first={candidates[0].we} dist={MathF.Sqrt(candidates[0].distSq):F0}");
                    // 800 ms floor + 400 ms jitter so 3 phantoms don't fire in
                    // lockstep.
                    s_phantomNextAttackMs[phantom.Id] = now + 800 + (long)(rng.NextDouble() * 400);
                }
            }
        }

        // Per-phantom next-attack timestamp (ms). Enforces at least ~800ms
        // between casts so the tick doesn't spam-fire.
        private static readonly Dictionary<ulong, long> s_phantomNextAttackMs = new();

        // Per-phantom next-ultimate timestamp (ms). Ultimates fire on any
        // target once available, then rest for 20 minutes regardless of
        // what the power data's own cooldown says.
        private const long PhantomUltimateCooldownMs = 20 * 60 * 1000;
        private static readonly Dictionary<ulong, long> s_phantomNextUltimateMs = new();

        // Per-(phantom, power) blacklist. Some powers fail for reasons that
        // won't clear on their own — RestrictiveCondition (unmet condition
        // requirement, e.g. transform-state powers), WeaponMissing (needs an
        // equipped item the phantom doesn't have). Without this, a broken
        // power with a big cooldown weight gets picked every tick against
        // every target (per-TARGET blacklist doesn't help) and the phantom
        // never lands a hit. 10-minute expiry in case the blocking state is
        // situational.
        private const long PhantomPowerBlacklistMs = 10 * 60 * 1000;
        private static readonly Dictionary<(ulong phantomId, PrototypeId powerRef), long> s_phantomPowerBlacklist = new();
        private static bool IsPhantomPowerBlacklisted(ulong phantomId, PrototypeId powerRef, long nowMs)
            => s_phantomPowerBlacklist.TryGetValue((phantomId, powerRef), out long expiresAt) && nowMs < expiresAt;
        private static void PrunePowerBlacklistFor(ulong phantomId)
        {
            List<(ulong, PrototypeId)> toRemove = null;
            foreach (var key in s_phantomPowerBlacklist.Keys)
                if (key.phantomId == phantomId) (toRemove ??= new()).Add(key);
            if (toRemove != null)
                foreach (var k in toRemove) s_phantomPowerBlacklist.Remove(k);
        }

        // Stuck-power watchdog. Channeled / recurring powers (beam channels
        // and some ultimates) never end on their own for phantoms — a real
        // player ends them by releasing the button, which the phantom can't
        // do. A stuck ActivePowerRef rejects every subsequent attack AND
        // revive with PowerInProgress, soft-locking the phantom forever.
        // If the same power stays active for this many consecutive ticks
        // (500ms each), force-end it.
        private const int PhantomStuckPowerTicks = 10; // 5 seconds
        private static readonly Dictionary<ulong, (PrototypeId powerRef, int ticks)> s_phantomActivePowerTrack = new();

        // Per-(phantom,target) blacklist expiry. Populated when ActivatePower
        // returns non-Success, so the sweep skips that target for
        // PhantomBlacklistDurationMs. Lets phantoms rotate to hittable targets
        // during scripted encounters (cutscene bosses, mid-transition mission
        // NPCs, temporary Invulnerable phases) instead of glueing to the
        // first-picked hostile forever.
        private const long PhantomBlacklistDurationMs = 3000;
        private static readonly Dictionary<(ulong phantomId, ulong targetId), long> s_phantomTargetBlacklist = new();
        private static bool IsTargetBlacklisted(ulong phantomId, ulong targetId, long nowMs)
            => s_phantomTargetBlacklist.TryGetValue((phantomId, targetId), out long expiresAt) && nowMs < expiresAt;
        private static void BlacklistTarget(ulong phantomId, ulong targetId, long nowMs)
            => s_phantomTargetBlacklist[(phantomId, targetId)] = nowMs + PhantomBlacklistDurationMs;
        private static void ClearTargetBlacklist(ulong phantomId, ulong targetId)
            => s_phantomTargetBlacklist.Remove((phantomId, targetId));
        private static void PruneBlacklistFor(ulong phantomId)
        {
            List<(ulong, ulong)> toRemove = null;
            foreach (var key in s_phantomTargetBlacklist.Keys)
                if (key.phantomId == phantomId) (toRemove ??= new()).Add(key);
            if (toRemove != null)
                foreach (var k in toRemove) s_phantomTargetBlacklist.Remove(k);
        }

        /// <summary>
        /// Pick a leash-teleport position near the caller that lands on the
        /// walkable navi mesh. Retries up to 6 times with fresh random
        /// angles/radii; falls back to caller position if nothing validates.
        /// Fixes the "phantom leashes into a wall/out-of-bounds corner and
        /// stays there" case that only server-restart used to unstick.
        /// </summary>
        private static Vector3 ChoosePhantomLeashPos(Region region, Vector3 callerPos, MHServerEmu.Core.System.Random.GRandom rng, float avatarRadius)
        {
            if (region == null) return callerPos;
            var walkCheck = new DefaultContainsPathFlagsCheck(PathFlags.Walk);
            for (int attempt = 0; attempt < 6; attempt++)
            {
                float angle = (float)(rng.NextDouble() * Math.PI * 2.0);
                // Tighter than the old 200-800 range so leashed phantoms
                // land right next to the caller instead of "somewhere on
                // this screen."
                float radius = 150f + (float)(rng.NextDouble() * 250f);
                Vector3 candidate = callerPos + new Vector3((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius, 0f);
                candidate = RegionLocation.ProjectToFloor(region, candidate);
                if (region.NaviMesh.Contains(candidate, MathF.Max(20f, avatarRadius), walkCheck))
                    return candidate;
            }
            // Fallback: caller's exact position. Guaranteed walkable since
            // the caller is standing on it.
            return callerPos;
        }

        private static readonly HashSet<ulong> s_phantomLocoLogged = new();

        // ================================================================
        //  Phantom damage-scaling curve
        //
        //  Real avatars pick up damage the same way from levels 1 -> 60:
        //  a level curve on the base power damage (already baked into
        //  each PowerPrototype) PLUS gear-scaling from DamageRating.
        //  Phantoms have no gear, so we synthesise the "gear" side by
        //  interpolating three properties along the level track:
        //
        //    DamageMult      1.5   ->  3.0   (final-damage multiplier)
        //    DamagePctBonus  0.2   ->  1.5   (percent bonus)
        //    DamageRating    0     ->  5000  (feeds combat-globals curve;
        //                                     ~100 rating ≈ 10% damage,
        //                                     5000 ≈ a fully-BiS endgame
        //                                     avatar)
        //
        //  Tuning runs on t = ((level - 1) / 59)^2 — QUADRATIC, not linear.
        //  Playtesting showed linear scaling made phantoms hit too hard
        //  through the story levels (1-30): at level 30 linear-t was 0.49,
        //  handing out half the endgame bonus while mobs still have
        //  story-tier health pools. Squaring t keeps the ramp shallow
        //  early (t=0.24 at level 30, t=0.06 at level 15) and steep into
        //  endgame, where mob health scales up to meet it. Level 1 and
        //  level 60 anchors are unaffected.
        //
        //  Clamped to [0,1] so a level-lock override (e.g. `!phantom
        //  spawn 4 45`) still gets the level-45 damage anchors and
        //  doesn't stay at spawn-time values while the human levels past
        //  it.
        //
        //  If you want phantoms to hit harder / softer, adjust the six
        //  anchor constants — the interpolation and call sites don't
        //  need to change.
        // ================================================================
        // Rebalanced after gear landed: rolled equipment now provides real
        // affix stats, so the synthetic curve only needs to cover the gap
        // between "AI that never dodges or optimizes" and a live player —
        // not simulate an entire BiS loadout. The old anchors (up to 3.0x /
        // +150% / 5000 rating) double-dipped with gear affixes and made
        // phantoms shred everything from level 1 to 60.
        private const float PhantomDmgMultLvl1  = 1.0f;
        private const float PhantomDmgMultLvl60 = 1.6f;
        private const float PhantomDmgPctBonusLvl1  = 0.0f;
        private const float PhantomDmgPctBonusLvl60 = 0.4f;
        private const float PhantomDmgRatingLvl1  = 0f;
        private const float PhantomDmgRatingLvl60 = 1200f;

        // Follow-stop bounds. 50u = "on top of the target" (old behaviour),
        // 1000u = a comfortable ranged-cast distance well inside the widest
        // player-attack ranges (~1400u for artillery-tier abilities). If a
        // phantom's collection has no usable ranged option we fall back to
        // PhantomFollowStopMin — melee heroes get closed distance the same
        // way real players do.
        private const float PhantomFollowStopMin = 50f;
        private const float PhantomFollowStopMax = 1000f;
        // Margin subtracted from the picked power's range so the phantom
        // stops just inside effective range rather than exactly at the edge
        // (where the target moving away one tick would kick the shot out).
        private const float PhantomFollowRangeMargin = 100f;

        private static float ComputePhantomFollowStopDist(Avatar phantom, WorldEntity target)
        {
            var pc = phantom.PowerCollection;
            if (pc == null) return PhantomFollowStopMin;

            float bestRange = 0f;
            foreach (var kvp in pc)
            {
                Power power = kvp.Value?.Power;
                if (power == null) continue;
                PowerPrototype pp = power.Prototype;
                if (pp == null) continue;
                if (pp is MovementPowerPrototype) continue;
                if (pp.PowerCategory != PowerCategoryType.NormalPower) continue;
                if (pp.Activation == PowerActivationType.Passive) continue;
                if (pp.IsToggled) continue;
                if (pp.IsTravelPower) continue;
                if (power.IsOnCooldown()) continue;

                float r = power.GetRange();
                if (r > bestRange) bestRange = r;
            }

            if (bestRange <= 0f) return PhantomFollowStopMin;

            float dist = Math.Clamp(bestRange - PhantomFollowRangeMargin,
                PhantomFollowStopMin, PhantomFollowStopMax);
            return dist;
        }

        // ================================================================
        //  Phantom gear
        //
        //  Rolls one level-appropriate item per equip slot using the same
        //  data the loot system uses for real drops:
        //  AvatarPrototype.EquipmentInventories declares the slots, and
        //  LootUtilities.BuildInventoryLootPicker resolves every concrete
        //  item prototype that fits a given (avatar, slot) pair from the
        //  loaded client data. Nothing item-specific lives in source.
        //
        //  Besides stats, this un-breaks weapon-gated powers: powers that
        //  returned WeaponMissing (e.g. shield-throw style kits) work once
        //  the hero-specific weapon slot is filled.
        // ================================================================

        // ----------------------------------------------------------------
        //  Gear rarity bands (see PickPhantomGearRarity for the data-
        //  reality notes on the top tiers):
        //    levels  1-10  → tier 1                       (white)
        //    levels 11-19  → tiers 2-3                    (green/blue)
        //    levels 20-30  → tier 4                       (purple)
        //    levels 31-50  → tier 4 + RarityCosmic        (purple/yellow)
        //    levels 51-60  → RarityCosmic + RarityUnique  (yellow/orange)
        // ----------------------------------------------------------------
        private static readonly object s_rarityTierLock = new();
        private static Dictionary<int, PrototypeId> s_rarityByTier;

        private static void EnsureRarityTiers()
        {
            lock (s_rarityTierLock)
            {
                if (s_rarityByTier != null) return;
                var map = new Dictionary<int, PrototypeId>();
                foreach (PrototypeId rarityRef in DataDirectory.Instance
                    .IteratePrototypesInHierarchy<RarityPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
                {
                    RarityPrototype rarityProto = rarityRef.As<RarityPrototype>();
                    if (rarityProto == null) continue;
                    // First proto wins per tier; the core ladder is a single
                    // DowngradeTo chain so collisions shouldn't happen.
                    map.TryAdd(rarityProto.Tier, rarityRef);
                }
                s_rarityByTier = map;
                // One-time dump of the resolved ladder so band issues are
                // diagnosable from the log (e.g. off-ladder special
                // rarities in data that shouldn't be rolled).
                var sb = new System.Text.StringBuilder($"[PhantomHero:Gear] rarity tier map built: {map.Count} tiers |");
                foreach (var kvp in map)
                    sb.Append($" T{kvp.Key}={kvp.Value.GetName()}");
                PhantomLogger.Info(sb.ToString());
            }
        }

        /// <summary>
        /// The rarities a phantom's gear is ALLOWED to end up at for a
        /// given level. This is both the roll pool and the acceptance
        /// filter: some item prototypes carry their own rarity
        /// restrictions (red "Ultimate" items, Runeword items), and
        /// MakeRestrictionsDroppable silently overrides whatever rarity we
        /// request to satisfy them — so forcing the rarity up front is not
        /// enough, the FINAL spec rarity must be validated against this
        /// list and off-band items re-picked.
        ///
        /// 1.52 data reality: the DowngradeTo tier chain covers
        /// Common(1) → Uncommon(2) → Rare(3) → Epic(4), but yellow Cosmic
        /// and orange Unique are not on that chain — they're anchored
        /// directly by the engine's LootGlobalsPrototype refs. The chain
        /// above Epic holds the red special rarities we must never roll.
        /// </summary>
        private static List<PrototypeId> GetPhantomGearAllowedRarities(int level)
        {
            EnsureRarityTiers();
            var lootGlobals = GameDatabase.LootGlobalsPrototype;
            var allowed = new List<PrototypeId>(2);

            void AddTier(int tier)
            {
                if (s_rarityByTier.TryGetValue(tier, out PrototypeId r) && r != PrototypeId.Invalid)
                    allowed.Add(r);
            }

            if (level <= 10) AddTier(1);
            else if (level <= 19) { AddTier(2); AddTier(3); }
            else if (level <= 30) AddTier(4);
            else if (level <= 50)
            {
                AddTier(4);
                if (lootGlobals.RarityCosmic != PrototypeId.Invalid) allowed.Add(lootGlobals.RarityCosmic);
            }
            else
            {
                if (lootGlobals.RarityCosmic != PrototypeId.Invalid) allowed.Add(lootGlobals.RarityCosmic);
                if (lootGlobals.RarityUnique != PrototypeId.Invalid) allowed.Add(lootGlobals.RarityUnique);
            }

            return allowed;
        }

        /// <summary>
        /// Equip the phantom. If <paramref name="gearOverride"/> is
        /// non-empty, those exact item protos are recreated at the
        /// phantom's level (squad/migration restore); otherwise one random
        /// valid item is rolled per unlocked equip slot. Rarity follows the
        /// level band table above. Returns the applied item proto refs for
        /// descriptor storage.
        /// </summary>
        internal static List<ulong> ApplyPhantomGear(Player phantomPlayer, Avatar phantomAvatar, int level, List<ulong> gearOverride)
        {
            var applied = new List<ulong>();
            AvatarPrototype avatarProto = phantomAvatar.AvatarPrototype;
            if (avatarProto?.EquipmentInventories == null) return applied;

            Game game = phantomAvatar.Game;
            var lootManager = game.LootManager;
            var rng = game.Random;

            bool useOverride = gearOverride != null && gearOverride.Count > 0;
            int overrideIdx = 0;

            List<PrototypeId> allowedRarities = GetPhantomGearAllowedRarities(level);

            // Red "Ultimate" tier — banned from every slot, core or special.
            EnsureRarityTiers();
            s_rarityByTier.TryGetValue(5, out PrototypeId bannedUltimateRef);

            foreach (AvatarEquipInventoryAssignmentPrototype assignment in avatarProto.EquipmentInventories)
            {
                if (assignment.UnlocksAtCharacterLevel > level) continue;

                InventoryPrototype invProto = assignment.Inventory.As<InventoryPrototype>();
                if (invProto == null) continue;
                // The costume slot is driven by the phantom costume system —
                // equipping a rolled costume item here would clobber it.
                if (invProto.ConvenienceLabel == InventoryConvenienceLabel.Costume) continue;

                // Slot policy:
                //  - Core armor (Gear01-05): rarity strictly follows the
                //    level band table — this is what colors the paper doll.
                //  - Special classes (artifacts, medal, relic, insignia,
                //    ring, legendary, uru-forged): these item families have
                //    their own natural rarity ranges, and forcing the band
                //    on them excluded their ENTIRE pools (empty artifact /
                //    rune / legendary slots on the level-60 paper doll).
                //    They roll their natural rarity instead — red Ultimate
                //    stays banned everywhere.
                //  - Anything else (crafting, consumables, misc): skipped.
                EquipmentInvUISlot uiSlot = assignment.UISlot;
                bool isCoreGear = uiSlot >= EquipmentInvUISlot.Gear01 && uiSlot <= EquipmentInvUISlot.Gear05;
                bool isSpecial = uiSlot == EquipmentInvUISlot.Artifact01 || uiSlot == EquipmentInvUISlot.Artifact02 ||
                                 uiSlot == EquipmentInvUISlot.Artifact03 || uiSlot == EquipmentInvUISlot.Artifact04 ||
                                 uiSlot == EquipmentInvUISlot.Medal      || uiSlot == EquipmentInvUISlot.Relic      ||
                                 uiSlot == EquipmentInvUISlot.Insignia   || uiSlot == EquipmentInvUISlot.Ring       ||
                                 uiSlot == EquipmentInvUISlot.Legendary  || uiSlot == EquipmentInvUISlot.UruForged;
                if (isCoreGear == false && isSpecial == false) continue;

                Inventory equipInventory = phantomAvatar.GetInventoryByRef(assignment.Inventory);
                if (equipInventory == null) continue;

                // Stored ref on restore (consumed even if it fails, to keep
                // slot alignment); random picks otherwise.
                PrototypeId overrideItemRef = PrototypeId.Invalid;
                if (useOverride && overrideIdx < gearOverride.Count)
                    overrideItemRef = (PrototypeId)gearOverride[overrideIdx++];

                var picker = new MHServerEmu.Core.Collections.Picker<Prototype>(rng);
                LootUtilities.BuildInventoryLootPicker(picker, avatarProto.DataRef, assignment.UISlot);

                try
                {
                    // An item prototype can carry its own rarity restriction
                    // (red Ultimate / Runeword items live in the same slot
                    // pools as normal gear) and the spec builder overrides
                    // our requested rarity to match it — so the FINAL spec
                    // rarity is what gets validated. The pool is DRAINED via
                    // PickRemove rather than sampled: every rejected item is
                    // removed and never retried, and every remaining item is
                    // eventually tried at EVERY allowed rarity. If any item
                    // in the pool can exist in-band (and every hero has
                    // Uniques/Cosmics per slot), it is guaranteed to be
                    // found — red can never come out of this loop.
                    ItemSpec acceptedSpec = null;
                    PrototypeId acceptedItemRef = PrototypeId.Invalid;

                    bool TryBuildInBandSpec(PrototypeId itemProtoRef)
                    {
                        if (itemProtoRef == PrototypeId.Invalid) return false;

                        if (isCoreGear)
                        {
                            // Core armor: try every banded rarity, random
                            // start for variety. A Unique-class item may
                            // only build at Unique while a normal piece
                            // only reaches Cosmic — one random rarity per
                            // item would wrongly discard valid items.
                            int rarityCount = Math.Max(1, allowedRarities.Count);
                            int start = rng.Next(0, rarityCount);
                            for (int i = 0; i < rarityCount; i++)
                            {
                                PrototypeId rarityRef = allowedRarities.Count > 0
                                    ? allowedRarities[(start + i) % allowedRarities.Count]
                                    : PrototypeId.Invalid;

                                ItemSpec spec = lootManager.CreateItemSpec(itemProtoRef, LootContext.Drop, phantomPlayer, level, rarityRef);
                                if (spec == null) continue;
                                if (allowedRarities.Count > 0 && allowedRarities.Contains(spec.RarityProtoRef) == false) continue;

                                acceptedSpec = spec;
                                acceptedItemRef = itemProtoRef;
                                return true;
                            }
                            return false;
                        }

                        // Special slots: natural (level-based) rarity roll —
                        // artifacts, medals, runewords, legendaries etc. own
                        // their rarity ranges. Only red Ultimate is banned.
                        ItemSpec naturalSpec = lootManager.CreateItemSpec(itemProtoRef, LootContext.Drop, phantomPlayer, level);
                        if (naturalSpec == null) return false;
                        if (bannedUltimateRef != PrototypeId.Invalid && naturalSpec.RarityProtoRef == bannedUltimateRef) return false;

                        acceptedSpec = naturalSpec;
                        acceptedItemRef = itemProtoRef;
                        return true;
                    }

                    // Stored override item first (squad/migration restore)...
                    if (overrideItemRef != PrototypeId.Invalid)
                        TryBuildInBandSpec(overrideItemRef);

                    // ...then drain the slot pool until something lands in-band.
                    while (acceptedSpec == null && picker.Empty() == false)
                    {
                        if (picker.PickRemove(out Prototype pickedProto) == false || pickedProto == null) break;
                        TryBuildInBandSpec(pickedProto.DataRef);
                    }

                    if (acceptedSpec == null)
                    {
                        PhantomLogger.Warn($"[PhantomHero:Gear] slot pool for {assignment.UISlot} on {avatarProto.DataRef.GetName()} has NO item usable at the level-{level} band rarities — slot left empty");
                        continue;
                    }

                    Item item;
                    using (var itemSettings = ObjectPoolManager.Instance.Get<EntitySettings>())
                    {
                        itemSettings.EntityRef = acceptedItemRef;
                        itemSettings.ItemSpec = acceptedSpec;
                        item = game.EntityManager.CreateEntity(itemSettings) as Item;
                    }
                    if (item == null) continue;

                    if (item.ChangeInventoryLocation(equipInventory) != InventoryResult.Success)
                    {
                        item.Destroy();
                        continue;
                    }

                    applied.Add((ulong)acceptedItemRef);
                }
                catch (Exception ex)
                {
                    PhantomLogger.Warn($"[PhantomHero:Gear] equip roll for slot {assignment.UISlot} on {phantomAvatar.Id:X} failed: {ex.Message}");
                }
            }

            return applied;
        }

        private static void ApplyPhantomDamageScaling(Avatar phantom, int level)
        {
            float t = Math.Clamp((level - 1) / 59f, 0f, 1f);
            t *= t; // quadratic — shallow through story levels, steep into endgame
            float dmgMult   = PhantomDmgMultLvl1     + t * (PhantomDmgMultLvl60     - PhantomDmgMultLvl1);
            float pctBonus  = PhantomDmgPctBonusLvl1 + t * (PhantomDmgPctBonusLvl60 - PhantomDmgPctBonusLvl1);
            float dmgRating = PhantomDmgRatingLvl1   + t * (PhantomDmgRatingLvl60   - PhantomDmgRatingLvl1);

            phantom.Properties[PropertyEnum.DamageMult]     = dmgMult;
            phantom.Properties[PropertyEnum.DamagePctBonus] = pctBonus;
            phantom.Properties[PropertyEnum.DamageRating]   = dmgRating;
        }

        // Rate-limit the "why isn't my phantom attacking" dump to at most one
        // per phantom every 5 seconds so a 500ms tick doesn't spam the log.
        private static readonly Dictionary<ulong, long> s_phantomNextDiagMs = new();
        private const long PhantomDiagIntervalMs = 5000;

        private bool ShouldEmitPhantomDiag(ulong phantomId)
        {
            long now = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
            if (s_phantomNextDiagMs.TryGetValue(phantomId, out long nextAt) && now < nextAt) return false;
            s_phantomNextDiagMs[phantomId] = now + PhantomDiagIntervalMs;
            return true;
        }

        private static void DumpPhantomHuntDiag(Avatar phantom, Vector3 phantomPos, WorldEntity picked,
            float pickedDistSq, List<(WorldEntity we, float distSq, string reason)> rejected,
            Region region, Sphere sweepSphere, MHServerEmu.Games.Entities.EntityRegionSPContext ctx)
        {
            // Full state dump of every hostile Agent in the sweep sphere so we
            // can identify exactly which flag is stopping the attack on a
            // cutscene boss. Runs at most once per 5s per phantom.
            var sb = new System.Text.StringBuilder();
            sb.Append($"[PhantomHero:Diag] {phantom} pos={phantomPos.ToStringNames()} ");
            if (picked != null)
                sb.Append($"picked={picked} dist={MathF.Sqrt(pickedDistSq):F0}");
            else
                sb.Append("picked=<none>");

            int n = 0;
            foreach (WorldEntity we in region.IterateEntitiesInVolume(sweepSphere, ctx))
            {
                if (we == null || we.Id == phantom.Id) continue;
                if (we is not Agent) continue;
                if (we.IsDead) continue;
                if (phantom.IsHostileTo(we) == false) continue;
                if (n++ >= 8) { sb.Append(" ...(more truncated)"); break; }
                float d = Vector3.Distance2D(we.RegionLocation.Position, phantomPos);
                string allianceRef = we.Alliance != null ? we.Alliance.DataRef.GetName() : "<null>";
                sb.Append($" | {we} dist={d:F0} dormant={we.IsDormant} untargetable={we.IsUntargetable} unaffectable={we.IsUnaffectable} affectedByPowers={we.IsAffectedByPowers()} sim={we.IsSimulated} inWorld={we.IsInWorld} alliance={allianceRef}");
            }
            if (rejected != null)
            {
                sb.Append(" | rejected=[");
                for (int i = 0; i < rejected.Count && i < 5; i++)
                    sb.Append($"{rejected[i].we}({rejected[i].reason},{MathF.Sqrt(rejected[i].distSq):F0}) ");
                sb.Append(']');
            }
            PhantomLogger.Info(sb.ToString());
        }

        private PowerUseResult TryPhantomAttack(Avatar phantom, WorldEntity target, float targetDistSq, MHServerEmu.Core.System.Random.GRandom rng)
        {
            if (target == null || phantom.PowerCollection == null) return PowerUseResult.GenericError;
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
            //     - power.GetRange() < target distance (would return OutOfPosition)
            //     - power is currently on cooldown
            //     - ultimates: additionally gated behind a 20-minute
            //       per-phantom timer (see s_phantomNextUltimateMs)
            //
            //   Score = cooldown duration in ms (used as a proxy for hit weight —
            //   powers with longer cooldowns are baked bigger, and it's the only
            //   universal numeric signal we can get without a per-hero damage table).
            //
            //   Pick strategy: a ready ultimate wins outright — 20 minutes
            //   apart it should never lose a coin flip to a basic attack.
            //   Otherwise sort survivors by score desc, weighted-random among
            //   the top 5. Favors real cooldown-worthy hits while still varying,
            //   and always fires the basic (0 cd) when nothing bigger is available.
            var candidates = ListPool<(PrototypeId, long)>.Instance.Get();
            try
            {
                long nowMs = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
                PrototypeId readyUltimate = PrototypeId.Invalid;

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

                    float pRange = power.GetRange();
                    if (pRange > 0f && pRange + 50f < targetDist) continue;

                    if (power.IsOnCooldown()) continue;

                    // Skip powers that recently failed with power-specific
                    // errors (RestrictiveCondition / WeaponMissing) — they
                    // won't start working by themselves, and their big
                    // cooldown weights would otherwise get them picked
                    // every single tick.
                    if (IsPhantomPowerBlacklisted(phantom.Id, rec.PowerPrototypeRef, nowMs)) continue;

                    // Ultimates fire on any target, but at most once per
                    // 20 minutes per phantom (on top of whatever cooldown
                    // the power data itself carries). The original
                    // FullscreenMovie failure that got them blanket-banned
                    // was the phantom-Player fullscreen-state bug, fixed in
                    // Player.PlayKismetSeq.
                    string pName = pp.DataRef.GetName() ?? string.Empty;
                    if (pName.EndsWith("Ultimate.prototype", StringComparison.Ordinal))
                    {
                        if (s_phantomNextUltimateMs.TryGetValue(phantom.Id, out long ultReadyAt) && nowMs < ultReadyAt)
                            continue;
                        readyUltimate = rec.PowerPrototypeRef;
                        continue; // not part of the weighted pool — it wins outright below
                    }

                    long cdMs = (long)power.GetCooldownDuration().TotalMilliseconds;
                    candidates.Add((rec.PowerPrototypeRef, cdMs));
                }

                if (candidates.Count == 0 && readyUltimate == PrototypeId.Invalid)
                    return PowerUseResult.OutOfPosition;

                PrototypeId chosenPower;
                bool chosenIsUltimate = readyUltimate != PrototypeId.Invalid;
                if (chosenIsUltimate)
                {
                    chosenPower = readyUltimate;
                }
                else
                {
                    // Sort by cooldown desc — biggest hitter first.
                    candidates.Sort(static (a, b) => b.Item2.CompareTo(a.Item2));

                    // Take top 5 (or fewer). Weighted-random pick — weight = 1 + cooldownMs/1000
                    // so a 5s power is ~6x more likely than a basic (0s) attack.
                    int take = Math.Min(5, candidates.Count);
                    long totalWeight = 0;
                    for (int i = 0; i < take; i++) totalWeight += 1 + (candidates[i].Item2 / 1000);
                    long roll = ((long)rng.NextDouble() * totalWeight * 1000L) % Math.Max(1, totalWeight);
                    if (roll < 0) roll = -roll;

                    chosenPower = candidates[0].Item1;
                    long acc = 0;
                    for (int i = 0; i < take; i++)
                    {
                        long w = 1 + (candidates[i].Item2 / 1000);
                        acc += w;
                        if (roll < acc) { chosenPower = candidates[i].Item1; break; }
                    }
                }
                candidates.Clear();

                // Pre-generate FXRandomSeed BEFORE the call so it's stored
                // in settings.FXRandomSeed. Without this the seed is 0,
                // ArchiveMessageBuilder auto-generates a random one for
                // the outbound NetMessageActivatePower (line 357), but
                // that generated seed never gets back into the
                // PowerApplication / PowerPayload / PowerResult. Client
                // sees ActivatePower(fxSeed=N) then PowerResult(fxSeed=0)
                // — mismatched. Body-emitter particle systems that vary
                // by seed treat 0 as "no effect" or drop it because the
                // cast doesn't correlate to the hit. Missile / projectile
                // FX works even with seed=0 because those spawn from a
                // separate Missile entity.
                //
                // ServerCombo forces the broadcast path (Power.cs line
                // 3680) to include the owner client and skip the combo-
                // effect early-out.
                int fxSeed = rng.Next(1, 10000);
                var settings = new PowerActivationSettings(target.Id, target.RegionLocation.Position, phantomPos)
                {
                    Flags = PowerActivationSettingsFlags.NotifyOwner | PowerActivationSettingsFlags.ServerCombo,
                    FXRandomSeed = fxSeed,
                    PowerRandomSeed = fxSeed,
                };
                var result = phantom.ActivatePower(chosenPower, ref settings);

                // Charge-and-release powers: the first activation only
                // STARTS the charge and waits for a button-release message
                // that will never come (no client). Release immediately —
                // ReleaseVariableActivation schedules the actual firing at
                // the power's MinReleaseTimeMS on the game scheduler, so
                // the shot still charges the minimum time and then goes
                // off on its own. Without this, phantoms wound up holding
                // the charge until the stuck-power watchdog cancelled it
                // 5 seconds later, and the power never fired at all.
                if (result == PowerUseResult.Success)
                {
                    Power chosenPowerInstance = phantom.PowerCollection?.GetPower(chosenPower);
                    if (chosenPowerInstance?.Prototype?.ExtraActivation is SecondaryActivateOnReleasePrototype)
                    {
                        try { chosenPowerInstance.ReleaseVariableActivation(ref settings); }
                        catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] ReleaseVariableActivation({chosenPower.GetName()}) failed on {phantom.Id:X}: {ex.Message}"); }
                    }
                }

                // Start the 20-minute ultimate timer only on a successful
                // cast — a rejected attempt (target died mid-windup etc.)
                // shouldn't burn the ult for the next 20 minutes.
                if (chosenIsUltimate && result == PowerUseResult.Success)
                    s_phantomNextUltimateMs[phantom.Id] = nowMs + PhantomUltimateCooldownMs;

                // Power-specific failures won't clear by retrying with a
                // different target — park the power so the picker falls
                // back to ones that actually work (see s_phantomPowerBlacklist).
                if (result == PowerUseResult.RestrictiveCondition || result == PowerUseResult.WeaponMissing)
                    s_phantomPowerBlacklist[(phantom.Id, chosenPower)] = nowMs + PhantomPowerBlacklistMs;

                // Log every failed activation so we can see WHY a cutscene boss
                // rejects the phantom's power (Dormant/Unaffectable/etc). Log
                // successes only once per (phantom, target) pair to avoid spam.
                bool logThis = result != PowerUseResult.Success;
                ulong key = phantom.Id ^ (target.Id * 0x9E3779B97F4A7C15UL);
                if (!logThis && s_phantomAttackTargetLogged.Add(key)) logThis = true;
                if (logThis)
                {
                    Power probePower = phantom.PowerCollection?.GetPower(chosenPower);
                    bool isValid = probePower != null && probePower.IsValidTarget(target);
                    string allianceRef = target.Alliance != null ? target.Alliance.DataRef.GetName() : "<null>";
                    PhantomLogger.Info($"[PhantomHero:Attack] {phantom} → target={target} power={chosenPower.GetName()} result={result} isValidTarget={isValid} tgtDormant={target.IsDormant} tgtUntargetable={target.IsUntargetable} tgtUnaffectable={target.IsUnaffectable} tgtAffectedByPowers={target.IsAffectedByPowers()} tgtSim={target.IsSimulated} tgtInWorld={target.IsInWorld} tgtAlliance={allianceRef} phantomAlliance={(phantom.Alliance?.DataRef.GetName() ?? "<null>")}");
                }
                return result;
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

        /// <summary>
        /// The full playable-avatar pool, resolved from the loaded client
        /// data. Used by the OmegaDev2 phantom tool's hero roster.
        /// </summary>
        public static List<(PrototypeId AvatarRef, string ShortName)> GetAllPhantomHeroRefs()
        {
            var results = new List<(PrototypeId, string)>();
            EnsureResolvedPool();
            lock (s_phantomResolvedLock)
            {
                foreach (PrototypeId avatarRef in s_phantomResolved)
                    results.Add((avatarRef, ExtractPrototypeShortName(avatarRef.GetName())));
            }
            results.Sort((a, b) => string.CompareOrdinal(a.Item2, b.Item2));
            return results;
        }

        /// <summary>
        /// Match a user-typed hero name against the playable-avatar pool
        /// resolved from the loaded client data. Matching is entirely
        /// runtime — hero names come from the user's own data files and
        /// their chat input, never from this source tree. Exact short-name
        /// match (case-insensitive) wins immediately; otherwise all
        /// substring matches are returned so the caller can ask the user
        /// to be more specific.
        /// </summary>
        public static List<(PrototypeId AvatarRef, string ShortName)> FindPhantomHeroRefs(string query)
        {
            var results = new List<(PrototypeId, string)>();
            if (string.IsNullOrWhiteSpace(query)) return results;
            EnsureResolvedPool();

            lock (s_phantomResolvedLock)
            {
                foreach (PrototypeId avatarRef in s_phantomResolved)
                {
                    string shortName = ExtractPrototypeShortName(avatarRef.GetName());
                    if (shortName.Equals(query, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Clear();
                        results.Add((avatarRef, shortName));
                        return results;
                    }
                    if (shortName.Contains(query, StringComparison.OrdinalIgnoreCase))
                        results.Add((avatarRef, shortName));
                }
            }

            return results;
        }

        /// <summary>
        /// All approved costumes usable by the given avatar, resolved from
        /// the loaded client data (CostumePrototype.UsableBy). Like the
        /// hero pool, this is entirely runtime — no costume names live in
        /// server source.
        /// </summary>
        public static List<(PrototypeId CostumeRef, string ShortName)> GetCostumesForAvatar(PrototypeId avatarRef)
        {
            var results = new List<(PrototypeId, string)>();
            if (avatarRef == PrototypeId.Invalid) return results;

            foreach (PrototypeId costumeRef in DataDirectory.Instance
                .IteratePrototypesInHierarchy<CostumePrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                CostumePrototype costumeProto = costumeRef.As<CostumePrototype>();
                if (costumeProto == null) continue;
                if (costumeProto.UsableBy != avatarRef) continue;
                if (costumeProto.CostumeUnrealClass == AssetId.Invalid) continue;
                results.Add((costumeRef, ExtractPrototypeShortName(costumeRef.GetName())));
            }

            return results;
        }

        /// <summary>
        /// Match a user-typed costume name against the avatar's costume
        /// pool. Exact short-name match wins; otherwise all substring
        /// matches are returned.
        /// </summary>
        public static List<(PrototypeId CostumeRef, string ShortName)> FindCostumeRefs(PrototypeId avatarRef, string query)
        {
            var all = GetCostumesForAvatar(avatarRef);
            var results = new List<(PrototypeId, string)>();
            if (string.IsNullOrWhiteSpace(query)) return results;

            foreach (var entry in all)
            {
                if (entry.ShortName.Equals(query, StringComparison.OrdinalIgnoreCase))
                {
                    results.Clear();
                    results.Add(entry);
                    return results;
                }
                if (entry.ShortName.Contains(query, StringComparison.OrdinalIgnoreCase))
                    results.Add(entry);
            }

            return results;
        }

        /// <summary>Pick a random costume for the avatar, or Invalid if it has none.</summary>
        public static PrototypeId PickRandomCostume(PrototypeId avatarRef, MHServerEmu.Core.System.Random.GRandom rng)
        {
            var pool = GetCostumesForAvatar(avatarRef);
            if (pool.Count == 0) return PrototypeId.Invalid;
            return pool[rng.Next(0, pool.Count)].CostumeRef;
        }

        /// <summary>
        /// "Entity/Characters/Avatars/Shipping/SomeHero.prototype" → "SomeHero".
        /// </summary>
        private static string ExtractPrototypeShortName(string prototypePath)
        {
            if (string.IsNullOrEmpty(prototypePath)) return string.Empty;
            int slash = prototypePath.LastIndexOf('/');
            string fileName = slash >= 0 ? prototypePath[(slash + 1)..] : prototypePath;
            const string suffix = ".prototype";
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                fileName = fileName[..^suffix.Length];
            return fileName;
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
            // A non-zero levelOverride from the chat command means the user
            // explicitly asked for a specific level (e.g. `!phantom spawn 4 45`).
            // Lock that level in — the tick loop will not auto-level these
            // phantoms as the caller gains XP. Costume 0 = roll random,
            // gear null = roll random per slot.
            => SpawnPhantomHeroCore(PrototypeId.Invalid, levelOverride, username, levelOverride > 0, 0, null, out error);

        /// <summary>
        /// Respawns a phantom from a MigrationData intent — same avatarRef +
        /// level + username + LockLevel + costume as the pre-transfer state.
        /// Used by Player.RestorePhantomsFromMigration after cross-region
        /// travel and by saved-squad spawns.
        /// </summary>
        public ulong SpawnPhantomHeroFromIntent(PrototypeId avatarRefOverride, int level, string username, bool lockLevel, ulong costumeRef, out string error, List<ulong> gearRefs = null)
            => SpawnPhantomHeroCore(avatarRefOverride, level, username, lockLevel, costumeRef, gearRefs, out error);

        private ulong SpawnPhantomHeroCore(PrototypeId avatarRefOverride, int levelOverride, string username, bool lockLevel, ulong costumeRef, List<ulong> gearRefs, out string error)
        {
            error = null;
            if (IsInWorld == false) { error = "avatar not in world"; return 0; }

            Region region = Region;
            if (region == null) { error = "no region"; return 0; }

            PrototypeId avatarRef = avatarRefOverride != PrototypeId.Invalid ? avatarRefOverride : NextPhantomHeroRef();
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

            // Stamp the human's Player entity id on the phantom so kill /
            // damage-tag paths can substitute the phantom out and credit the
            // real player. Without this, mission counters and loot rolls skip
            // phantom kills because Player.IsMissionPlayer on the synthetic
            // phantom Player always returns false.
            Player humanHost = PhantomHost;
            if (humanHost != null) phantomPlayer.PhantomCreatorId = humanHost.Id;

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

            // Step 4b: costume. Explicit ref (squad restore / migration /
            // command) wins; otherwise roll a random one from the avatar's
            // costume pool so phantom crowds don't all wear the default.
            // Applied before EnterWorld so the initial replication already
            // carries the final look. The ACTUAL applied ref is stored in
            // the descriptor below, so saves/transfers reproduce this
            // costume instead of re-rolling.
            PrototypeId appliedCostumeRef = costumeRef != 0 ? (PrototypeId)costumeRef : PickRandomCostume(avatarRef, Game.Random);
            if (appliedCostumeRef != PrototypeId.Invalid)
            {
                if (phantomAvatar.ChangeCostume(appliedCostumeRef) == false)
                {
                    PhantomLogger.Warn($"[PhantomHero] ChangeCostume({appliedCostumeRef.GetName()}) failed for {avatarRef.GetName()}");
                    appliedCostumeRef = PrototypeId.Invalid;
                }
            }

            // Step 4c: gear — one level-appropriate item per unlocked equip
            // slot (or the stored set on squad/migration restore). Fills
            // hero-specific weapon slots too, which un-breaks WeaponMissing
            // powers. The applied refs go on the descriptor below.
            List<ulong> appliedGearRefs = ApplyPhantomGear(phantomPlayer, phantomAvatar, effectiveLevel, gearRefs);

            // Step 5: pick a spawn point close to the caller and enter the
            // world. Two goals:
            //   * Close — old range (300-1100u) put phantoms half a screen
            //     away; tightened to 200-400u so they land within visible
            //     radius and read as "with you" instead of "over there".
            //   * No stacking — reject candidates within PhantomMinSpacing of
            //     any already-alive phantom. Up to 8 tries; last try
            //     accepted regardless so we never fail-to-spawn on a crowd.
            var rng = Game.Random;
            Vector3 origin = RegionLocation.Position;
            Vector3 candidate = origin;
            const float minRadius = 150f;
            const float maxRadius = 320f;
            const float PhantomMinSpacing = 130f;              // ≈ 1.4 avatar widths
            const float PhantomMinSpacingSq = PhantomMinSpacing * PhantomMinSpacing;
            Player spacingHost = PhantomHost;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                float ang = (float)(rng.NextDouble() * Math.PI * 2.0);
                float radius = minRadius + (float)(rng.NextDouble() * (maxRadius - minRadius));
                candidate = origin + new Vector3((float)Math.Cos(ang) * radius, (float)Math.Sin(ang) * radius, 0f);
                if (spacingHost == null || spacingHost.PhantomHeroCount == 0) break;

                bool tooClose = false;
                for (int i = 0; i < spacingHost.PhantomAvatarIds.Count; i++)
                {
                    Avatar existing = Game.EntityManager.GetEntity<Avatar>(spacingHost.PhantomAvatarIds[i]);
                    if (existing == null || existing.IsInWorld == false) continue;
                    if (Vector3.DistanceSquared2D(existing.RegionLocation.Position, candidate) < PhantomMinSpacingSq) { tooClose = true; break; }
                }
                if (tooClose == false) break;
                // On the last attempt, accept whatever we've got — better a
                // slight overlap than no spawn.
            }
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

                // Re-broadcast the phantom's PowerCollection to every real
                // player that now has it in AOI. PowerCollection.AssignPower
                // only ships NetMessagePowerCollectionAssignPower to
                // clients when _owner.IsInGame is true (PowerCollection.cs
                // line 339) — but phantom powers get assigned during
                // CreateAvatar / InitializeLevel BEFORE phantomPlayer.
                // EnterGame() runs. That means the initial assign batch
                // never reaches anyone, and the client's PowerCollection
                // for this phantom stays empty. Empty collection ->
                // NetMessageActivatePower arrives referencing a power the
                // client doesn't know the phantom has -> the cast
                // animation never plays -> no VFX. Sending the whole
                // collection here fixes both: cast animations play and
                // VFX renders for every phantom power.
                if (phantomAvatar.PowerCollection != null)
                {
                    int collectionSize = 0;
                    foreach (var _ in phantomAvatar.PowerCollection) collectionSize++;
                    foreach (Player realPlayer in new PlayerIterator(Game))
                    {
                        if (realPlayer.PlayerConnection == null) continue;
                        var aoi = realPlayer.AOI;
                        if (aoi == null) continue;
                        bool interested = aoi.InterestedInEntity(phantomAvatar.Id, AOINetworkPolicyValues.AOIChannelProximity);
                        if (!interested)
                        {
                            PhantomLogger.Info($"[PhantomHero:PowerSync] SKIP {realPlayer.GetName()} — not interested in phantom {phantomAvatar.Id:X} (proximity=false). collectionSize={collectionSize}");
                            continue;
                        }
                        bool sent = phantomAvatar.PowerCollection.SendEntireCollection(realPlayer);
                        PhantomLogger.Info($"[PhantomHero:PowerSync] {realPlayer.GetName()} ← phantom {phantomAvatar.Id:X} collection ({collectionSize} powers) sent={sent}");
                    }
                }
                else
                {
                    PhantomLogger.Warn($"[PhantomHero:PowerSync] phantom {phantomAvatar.Id:X} has no PowerCollection at spawn — client can't render any VFX");
                }

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

            // Damage scaling — see ApplyPhantomDamageScaling for the level
            // curve. Anchored at "helpful but not obliterating" for level 1
            // and "full-BiS-omega teammate" for level 60, linear between.
            ApplyPhantomDamageScaling(phantomAvatar, effectiveLevel);

            // Server-authoritative movement — real avatars have IsMovementAuthoritative=false
            // because the client drives them. Phantoms have no client, so we must
            // flip it, or Locomotor.FollowEntity produces no visible walking on
            // the real client's screen.
            phantomAvatar.IsPhantomHero = true;

            // Force simulation on. WorldEntity.SetSimulated adds the entity to
            // EntityCollection.Locomotion which is what actually steps
            // Locomotor path progress per tick AND broadcasts LocomotionState
            // changes to interested clients. Without this, the tick still
            // updates position but the client receives only raw position
            // snaps — no walk animation, hence the "sliding" look. Real
            // players get flipped simulated=true when a peer's AOI notices
            // them; phantoms may not go through that path reliably.
            try { phantomAvatar.SetSimulated(true); }
            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] SetSimulated(true) failed: {ex.Message}"); }

            // Book-keeping goes on the human Player (source of truth) — not on
            // this Avatar shell — so `!phantom clear` and tick reattachment
            // still find these entries after hero swaps or region hops.
            Player host = PhantomHost;
            if (host == null)
            {
                error = "no Player host to register phantom against";
                try { if (phantomAvatar.IsInWorld) phantomAvatar.ExitWorld(); phantomAvatar.Destroy(); } catch { }
                DestroyPhantomPlayer(phantomPlayer);
                return 0;
            }
            var descriptor = new MHServerEmu.DatabaseAccess.Models.PhantomIntent
            {
                AvatarRef = (ulong)avatarRef,
                Level = effectiveLevel,
                Username = username,
                LockLevel = lockLevel,
                CostumeRef = (ulong)appliedCostumeRef,
                GearRefs = appliedGearRefs,
            };
            host.RegisterPhantom(phantomAvatar.Id, phantomPlayer.Id, descriptor);
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
            Player host = PhantomHost;
            if (host == null) return 0;
            // Snapshot ids for the diagnostic-cache scrub — Player.PurgePhantoms
            // clears its own list, so we need the ids before it runs.
            var ids = new List<ulong>(host.PhantomAvatarIds);
            int removed = host.PurgePhantoms();
            foreach (ulong id in ids) { s_phantomAttackLogged.Remove(id); s_phantomLocoLogged.Remove(id); s_phantomNextAttackMs.Remove(id); s_phantomStuckTrack.Remove(id); s_phantomNextDiagMs.Remove(id); s_phantomNextUltimateMs.Remove(id); s_phantomActivePowerTrack.Remove(id); PruneBlacklistFor(id); PrunePowerBlacklistFor(id); }
            return removed;
        }

        /// <summary>
        /// Called from Avatar.OnEnteredWorld. Prunes phantoms that don't
        /// belong to this Avatar's region (they were left behind by an old
        /// Avatar in another region and can't be seen anyway) and restarts
        /// the tick on survivors so their AI resumes. This is the core of
        /// Option B: same-region avatar swaps keep phantoms alive; cross-
        /// region hops clean them up automatically.
        /// </summary>
        internal void ReattachPhantomTick()
        {
            Player host = PhantomHost;
            if (host == null || host.PhantomHeroCount == 0) return;
            Region myRegion = Region;
            if (myRegion == null) return;

            var mgr = Game?.EntityManager;
            if (mgr == null) return;

            var stale = new List<ulong>();
            int alive = 0;
            for (int i = 0; i < host.PhantomAvatarIds.Count; i++)
            {
                ulong id = host.PhantomAvatarIds[i];
                Avatar phantom = mgr.GetEntity<Avatar>(id);
                if (phantom == null || phantom.IsDestroyed) { stale.Add(id); continue; }
                // Different region OR not in world = can't be driven from
                // here; destroy so the count is honest and !phantom clear
                // stays accurate.
                if (phantom.IsInWorld == false || phantom.Region != myRegion) { stale.Add(id); continue; }
                alive++;
            }

            if (stale.Count > 0)
            {
                foreach (ulong id in stale)
                {
                    int idx = -1;
                    for (int j = 0; j < host.PhantomAvatarIds.Count; j++)
                        if (host.PhantomAvatarIds[j] == id) { idx = j; break; }
                    ulong playerId = idx >= 0 && idx < host.PhantomPlayerIds.Count ? host.PhantomPlayerIds[idx] : 0;
                    try
                    {
                        Avatar av = mgr.GetEntity<Avatar>(id);
                        if (av != null)
                        {
                            if (av.IsInWorld) av.ExitWorld();
                            av.Destroy();
                        }
                    }
                    catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] stale-region cleanup avatar 0x{id:X} failed: {ex.Message}"); }
                    if (playerId != 0)
                    {
                        try
                        {
                            Player p = mgr.GetEntity<Player>(playerId);
                            if (p != null) { if (p.IsInGame) p.ExitGame(); p.Destroy(); }
                        }
                        catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] stale-region cleanup phantom-player 0x{playerId:X} failed: {ex.Message}"); }
                    }
                    host.UnregisterPhantom(id);
                    s_phantomAttackLogged.Remove(id);
                    s_phantomLocoLogged.Remove(id);
                    s_phantomNextAttackMs.Remove(id); s_phantomStuckTrack.Remove(id);
                    s_phantomNextDiagMs.Remove(id);
                    s_phantomNextUltimateMs.Remove(id);
                    s_phantomActivePowerTrack.Remove(id);
                    PruneBlacklistFor(id);
                    PrunePowerBlacklistFor(id);
                }
                PhantomLogger.Info($"[PhantomHero] {this} reattach: pruned {stale.Count} stale, {alive} alive");
            }

            if (alive > 0)
                SchedulePhantomTick();
        }

        private void DestroyPhantomPlayer(Player p)
        {
            if (p == null) return;
            try { p.Destroy(); } catch { /* best effort cleanup on partial init */ }
        }
    }
}
