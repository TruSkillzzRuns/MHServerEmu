using System;
using System.Collections.Generic;
using Gazillion;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Memory;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.Events.Templates;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Properties;
using MHServerEmu.Games.Regions;
using MHServerEmu.Games.UI;

namespace MHServerEmu.Games.Entities
{
    // Danger Room Endless Terminal — a stationary "Agent Coulson" NPC spawned
    // in the Danger Room hub (DangerRoomHubRegion). Interacting shows a real
    // native Yes/No-style dialog (same GameDialogManager plumbing as Trial of
    // the Impossible); confirming warps the player into a dedicated, always-
    // sterilized training arena (DRRegionUniqueTutorialFight) where a second
    // stationary interactable (a FuryCommandConsole prop, a real physical
    // console model) starts an Endless Challenge run on interact — the
    // same Player.WaveDirector.cs machinery the OmegaDev2 app's Wave Director
    // page already drives over HTTP, just triggered in-world instead.
    public partial class Player
    {
        private static readonly Logger DrEndlessLogger = LogManager.CreateLogger();

        // Positions/refs captured live via /webapi/debug/position and
        // /webapi/protoeditor/discover (2026-07-25).
        private const ulong DrEndlessHubRegionRef = 0xB8881DBE371719B8;    // Regions/EndGame/DangerRoomMode/HUB/DangerRoomHubRegion.prototype
        private const ulong DrEndlessArenaRegionRef = 0xC315DE42E03825FF;  // Regions/EndGame/DangerRoomMode/UniqueScenarios/Tutorial/DRRegionUniqueTutorialFight.prototype

        private const ulong DrEndlessGuideHeroRef = 0x336D92F0229014E9;    // Entity/Characters/NPCs/SHIELD/AgentCoulson.prototype
        private static readonly Vector3 s_drEndlessGuidePosition = new(105.875f, -498.875f, 311f);
        private static readonly Orientation s_drEndlessGuideOrientation = new(1.890625f, 0f, 0f);

        // Three genuine static console/terminal PROPS were tried and ruled
        // out (2026-07-25): EventTerminalVendor (VisibleByDefault=false),
        // FuryCommandConsole (Entity/Props/Missions/…, likely gated by a
        // MissionVisibilityOption tied to its native mission), and
        // ComputerTerminal (passed every server+client check — correct
        // position/region/cell, VisibilityStatus=true, not hidden per
        // in-game `!entity near` — yet still never rendered). All three
        // failures point at static props relying on being placed via the
        // region's own baked population/encounter data rather than a raw
        // runtime CreateEntity call. Reverted to an Agent-type NPC
        // (SHIELDTechnician), the one category confirmed to render
        // correctly both times it was tried.
        private const ulong DrEndlessTerminalHeroRef = 0x50D735A977C81666; // Entity/Characters/NPCs/SHIELD/SHIELDTechnician.prototype
        private static readonly Vector3 s_drEndlessTerminalPosition = new(-81.625f, 19f, 312f);
        // Rotated 180° from the originally captured yaw (-2.546875) per user
        // request (2026-07-25) — was facing the wrong way.
        private static readonly Orientation s_drEndlessTerminalOrientation = new(0.594718f, 0f, 0f);

        // Loot break stash box — the REAL player-Stash-access object used in
        // real hub content (Avengers Tower), confirmed VisibleByDefault=true
        // via /webapi/debug/visiblebydefault (2026-07-26). Interacting with
        // any entity carrying PropertyEnum.OpenPlayerStash=true opens the
        // player's actual Stash inventory (Dialog\StashOption.cs:25) — no
        // special prototype logic needed beyond that one property.
        private const ulong DrEndlessStashRef = 0x53EB98704E8E15C6; // Entity/Characters/NPCs/Objects/AvengersStash.prototype
        // 80f matches the spacing SpawnOneEndlessChest already uses to keep
        // multiple same-milestone chests from touching each other -- reused
        // here for the same reason, between the terminal/stash/portal cluster.
        private static readonly Vector3 s_drEndlessStashOffset = new(80f, 0f, 0f); // relative to the terminal's position

        // Loot-break return portal — a real base-game "return to Danger Room"
        // Transition prototype, Type=ReturnToLastTown. Spawning it as a real
        // Transition entity needs no manual destination wiring at all:
        // Avatar.cs's generic OnPlayerInteracted already special-cases any
        // interacted Transition entity and calls UseTransition() on it
        // automatically, which for this type resolves to
        // Transition.UseTransitionReturnToLastTown -> Teleporter.TeleportToLastTown().
        //
        // No single asset covers all three client versions (confirmed live,
        // 2026-07-28, via /webapi/protoeditor against all three running
        // servers) — each entry here resolves on a different subset:
        //   ReturnToLastBaseDR            -> 1.48, 1.52 (not 1.53)
        //   ReturnToLastBaseHolosimVisible -> 1.53 only
        // SpawnDrPortal tries them in order and uses whichever resolves on
        // this server, same self-healing-pool pattern as
        // Player.TrialOfImpossible.cs's GetValidTrialArenaPool.
        private static readonly ulong[] s_drEndlessReturnPortalRefCandidates =
        {
            0x65013AB9D36D1394, // Entity/Transitions/ReturnToLastBaseDR.prototype
            0x68F74D36E02D18A7, // Entity/Transitions/ReturnToLastBaseHolosimVisible.prototype
        };
        private static readonly Vector3 s_drEndlessPortalOffset = new(-80f, 0f, 0f); // relative to the terminal's position, opposite side from the stash (160f from the stash itself)

        // Custom dialog strings — see Data/Game/Achievements/AchievementStringMap_99_DangerRoomEndless.json.
        // TrialDialogYesStringId ("Continue") is reused as-is from Player.TrialOfImpossible.cs (same partial class).
        private const ulong DrEndlessGuideDialogMessageStringId = 1234567890123456790;   // "Enter the Endless Wave training arena?"
        private const ulong DrEndlessTerminalDialogMessageStringId = 1234567890123456791; // "Start Endless Wave training?"
        private const ulong DrEndlessDifficultyPromptStringId = 1234567890123456793;      // "Choose your difficulty:"
        private const ulong DrEndlessDifficultyRecruitStringId = 1234567890123456794;     // "Recruit"
        private const ulong DrEndlessDifficultyVeteranStringId = 1234567890123456795;     // "Veteran"
        private const ulong DrEndlessDifficultyOmegaStringId = 1234567890123456796;       // "Omega-Level"
        private const ulong DrEndlessDifficultyMoreOptionsStringId = 1234567890123456797; // "More Options..."
        private const ulong DrEndlessDifficultyAdvancedPromptStringId = 1234567890123456798; // "Choose your difficulty (advanced):"

        // Default Endless Challenge parameters for the in-world terminal —
        // no in-game UI to configure per-run knobs, so this mirrors a
        // reasonable OmegaDev2 Wave Director default: one random enemy
        // phantom hero per wave, rank climbs automatically (see
        // Player.WaveDirector.cs's EndlessRankBumpEveryNWaves), gentle
        // per-wave count/level scaling. Reuses the same validated finale
        // loot table Trial of the Impossible uses for its lootsplosion.
        private const float DrEndlessCountScalePerWave = 0.15f;
        private const int DrEndlessLevelBumpPerWave = 1;
        private const ulong DrEndlessRewardLootTableRef = 0x0520D1A142CA23CD; // Loot/Tables/Mob/Bosses/EndgameDailies/Subtables/SharedEndgameDailiesCosmicBUFFED.prototype

        private ulong _drGuideNpcId;
        private Region _drGuideRegion;
        private Event<PlayerInteractGameEvent>.Action _drGuideInteractAction;

        private ulong _drTerminalNpcId;
        private Region _drTerminalRegion;
        private Event<PlayerInteractGameEvent>.Action _drTerminalInteractAction;

        private ulong _drStashNpcId;
        private ulong _drPortalNpcId;
        private Region _drPortalRegion;
        private Event<PlayerInteractGameEvent>.Action _drPortalInteractAction;
        private readonly EventPointer<DrPortalLastTownRestoreEvent> _drPortalLastTownRestoreTick = new();

        // How long after using the portal to restore the player's real
        // LastTownRegionForAccount (see OnDrPortalInteract) -- long enough
        // that the teleport this triggers has definitely already resolved,
        // short enough nothing else reads the temporarily-overridden value
        // in between.
        private const int DrPortalLastTownRestoreMs = 5000;

        // True from the moment the player confirms the Coulson dialog until
        // BeginRegionTransfer snapshots it onto MigrationData — same shape as
        // Player.TrialOfImpossible.cs's _trialWarpPending.
        private bool _drWarpPending;

        private readonly EventGroup _drEndlessEvents = new();
        private readonly EventPointer<DrEndlessArenaSettleTickEvent> _drArenaSettleTick = new();

        // 4-player co-op groundwork (Phase 2) — DRRegionUniqueTutorialFight
        // is native Danger Room tutorial content, built for exactly one
        // player. Party members joining via the game's own existing
        // "teleport to party member" feature (Player.cs's
        // TeleportToPartyMember -> Teleporter.TeleportToPlayer) land in the
        // SAME region instance as whoever they're following with zero new
        // code needed for that part — it's the same mechanism already used
        // to follow a friend into any private story instance. The only
        // blocker is this specific region's own native PlayerLimit/
        // PartyFormationAllowed data, sized for solo Danger Room training.
        // Patched in-memory only (RuntimePrototypeEditor-style reflection
        // write, same mechanism src\...\PrototypeEditorWriteWebHandler.cs
        // already exposes over HTTP) — no .sip/client file is ever touched,
        // and nothing else in the game references this specific unique-
        // scenario region, so it can't affect any other content.
        private static bool s_drEndlessArenaPartyPatchApplied;

        private static void EnsureDrEndlessArenaAllowsParty()
        {
            if (s_drEndlessArenaPartyPatchApplied) return;
            s_drEndlessArenaPartyPatchApplied = true;

            try
            {
                var regionProto = ((PrototypeId)DrEndlessArenaRegionRef).As<RegionPrototype>();
                if (regionProto == null)
                {
                    DrEndlessLogger.Warn("[DangerRoomEndless] arena region ref did not resolve to a RegionPrototype — party patch skipped");
                    return;
                }

                typeof(RegionPrototype).GetProperty(nameof(RegionPrototype.PlayerLimit))
                    ?.SetValue(regionProto, 4);
                typeof(RegionPrototype).GetProperty(nameof(RegionPrototype.PartyFormationAllowed))
                    ?.SetValue(regionProto, true);

                DrEndlessLogger.Info("[DangerRoomEndless] arena region patched in-memory: PlayerLimit=4, PartyFormationAllowed=true");
            }
            catch (Exception ex)
            {
                DrEndlessLogger.Warn($"[DangerRoomEndless] arena party patch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Called from Avatar.OnEnteredWorld on every region entry for a real
        /// (non-phantom) player avatar — mirrors OnAvatarEnteredRegionForTrial's call site.
        /// </summary>
        internal void OnAvatarEnteredRegionForDangerRoomEndless(Region region, Avatar avatar)
        {
            if (region == null || avatar == null) return;

            EnsureDrEndlessArenaAllowsParty();

            var mig = PlayerConnection?.MigrationData;
            bool freshWarp = false;
            if (mig != null && mig.PendingDangerRoomEndlessWarp)
            {
                mig.PendingDangerRoomEndlessWarp = false;
                freshWarp = true;
            }

            PrototypeId regionRef = region.PrototypeDataRef;

            // Leaving the arena by ANY means other than a normal death/
            // Extract (bodyslide confirmed live 2026-07-26, probably also
            // an admin warp or disconnect) never told WaveDirector the run
            // ended — StartEndlessChallenge/StopWaveRun only clean up when
            // EndEndlessChallenge runs, and nothing was calling that on a
            // plain region exit. Left unhandled: the wave tick kept firing
            // and spawning enemies in whatever region the player ended up
            // in (confirmed: nemeses spawned in the Danger Room HUB), the
            // wave-count widget was never cleared (stale "Wave N" banner
            // persisted into the hub), and _endlessCycle never reset for
            // the next run (restarting read "Wave 4" instead of "Wave 1").
            // Force-end it here the moment we detect we're anywhere but the
            // arena — this covers every exit path in one place.
            if (regionRef != (PrototypeId)DrEndlessArenaRegionRef && IsEndlessChallengeActive)
            {
                DrEndlessLogger.Info($"[DangerRoomEndless] {GetName()}: left the arena mid-run — force-ending Endless Challenge");
                ExtractEndlessChallenge();
            }

            if (regionRef == (PrototypeId)DrEndlessHubRegionRef)
            {
                DetachDrTerminalNpc();

                if (_drGuideRegion == region && _drGuideNpcId != 0)
                {
                    var existingNpc = Game.EntityManager.GetEntity<WorldEntity>(_drGuideNpcId);
                    if (existingNpc != null && existingNpc.IsDestroyed == false && existingNpc.IsInWorld)
                        return; // already set up for this region instance
                }

                SpawnDrGuideNpc(region, avatar);
                return;
            }

            if (regionRef == (PrototypeId)DrEndlessArenaRegionRef)
            {
                DetachDrGuideNpc();

                // Only sterilize/suspend on the actual warp-in from Coulson's
                // dialog — NOT on every subsequent entry (e.g. reconnecting
                // mid-run would otherwise wipe an active Endless Challenge
                // session). Deferred past ArenaSettleMs — this is a Danger
                // Room scenario room, and its own tutorial mission/kismet
                // content trickles in over the first couple of seconds after
                // the client's load screen ends (same trickle Player.
                // WaveDirector.cs's ArenaSettleMs already accounts for);
                // acting immediately on OnEnteredWorld missed it and left the
                // native tutorial script running (confirmed live 2026-07-25).
                if (freshWarp)
                {
                    // Suspend whatever mission(s) already exist THE INSTANT
                    // we arrive — the tutorial's "Welcome to the Danger Room"
                    // intro dialogue fires immediately on region entry,
                    // faster than the ArenaSettleMs delay below.
                    //
                    // Root cause of the "phantoms stopped taking damage"
                    // regression this caused (confirmed live 2026-07-26):
                    // NOT a mission/combat interaction at all — the tutorial
                    // mission's own OnStart actions include
                    // MissionActionShowHUDTutorial, which calls
                    // Player.ShowHUDTutorial -> Avatar.SetTutorialProps and
                    // sets PropertyEnum.TutorialPowerLock on the REAL
                    // PLAYER'S OWN avatar (Avatar.cs:277) when the tutorial
                    // step disallows power usage. Normally a later
                    // MissionActionHideHUDTutorial clears it via
                    // Avatar.ResetTutorialProps(). Suspending the mission
                    // before that later step ever runs left the lock stuck
                    // ON — the player literally couldn't activate powers,
                    // which reads exactly like "phantoms are invulnerable"
                    // but is actually the player's own attacks never firing.
                    // Fix: clear it ourselves unconditionally right after
                    // suspending, so it can never get stuck regardless of
                    // whether the native mission's own cleanup step ran.
                    region.MissionManager?.SuspendAllMissions();
                    avatar.ResetTutorialProps();
                    ScheduleDrArenaSettle();
                    return;
                }

                // 4-line HUD dashboard init call DISABLED — see
                // Player.WaveDirector.cs's UpdateEndlessHudWidgets doc
                // comment (confirmed live 2026-07-26 the widget slots it
                // used don't render standalone text the way "Wave N" does).

                if (_drTerminalRegion == region && _drTerminalNpcId != 0)
                {
                    var existingNpc = Game.EntityManager.GetEntity<WorldEntity>(_drTerminalNpcId);
                    if (existingNpc != null && existingNpc.IsDestroyed == false && existingNpc.IsInWorld)
                        return; // already set up for this region instance
                }

                SpawnDrTerminalNpc(region, avatar);
                return;
            }

            DetachDrGuideNpc();
            DetachDrTerminalNpc();
        }

        /// <summary>
        /// Snapshot the pending Danger Room Endless warp confirmation onto
        /// MigrationData so it survives the cross-region Game-instance
        /// destroy/recreate. Called from PlayerConnection.BeginRegionTransfer,
        /// right next to SnapshotTrialWarpForTransfer — same problem, same fix shape.
        /// </summary>
        internal void SnapshotDangerRoomEndlessWarpForTransfer()
        {
            if (_drWarpPending == false) return;
            var mig = PlayerConnection?.MigrationData;
            if (mig == null) return;
            mig.PendingDangerRoomEndlessWarp = true;
            _drWarpPending = false; // this Game instance is going away
        }

        // ---------------- Guide NPC (Danger Room hub) ----------------

        private void SpawnDrGuideNpc(Region region, Avatar avatar)
        {
            // Sweep the region for any leftover/live guide NPC BEFORE
            // spawning a fresh one — cannot rely on _drGuideNpcId/
            // _drGuideRegion (a Player-instance field) here: confirmed live
            // 2026-07-26 that a cross-region round trip (hub -> arena -> hub)
            // destroys the whole Game instance, so the Player object active
            // when this runs again has never even SEEN the previous guide's
            // id — it's not "0 vs stale", it's a brand-new Player that never
            // tracked one at all. DetachDrGuideNpc's per-instance bookkeeping
            // can't survive that; scanning the live region directly can.
            //
            // 4-player co-op groundwork (Phase 2): this same scan doubles as
            // multi-player dedup — a second real player joining this exact
            // region instance (via the game's own "teleport to party member"
            // feature) hits this too, and must ADOPT the already-live guide
            // NPC the first player spawned rather than sweep-destroy it and
            // spawn a "new" one, which would orphan the first player's own
            // tracking and interact registration.
            var sweepSphere = new MHServerEmu.Core.Collisions.Sphere(s_drEndlessGuidePosition, 50f);
            var sweepCtx = new EntityRegionSPContext(EntityRegionSPContextFlags.PrimaryPartition);
            WorldEntity existingLive = null;
            var staleGuides = new List<WorldEntity>();
            foreach (WorldEntity existing in region.IterateEntitiesInVolume(sweepSphere, sweepCtx))
            {
                if (existing == null || existing.IsDestroyed) continue;
                if (existing.PrototypeDataRef != (PrototypeId)DrEndlessGuideHeroRef) continue;
                if (existing.IsInWorld && existingLive == null) existingLive = existing;
                else staleGuides.Add(existing);
            }

            if (existingLive != null)
            {
                DetachDrGuideNpc();
                _drGuideNpcId = existingLive.Id;
                _drGuideRegion = region;
                _drGuideInteractAction ??= OnDrGuideInteract;
                region.PlayerInteractEvent.AddActionBack(_drGuideInteractAction);
                DrEndlessLogger.Info($"[DangerRoomEndless] {GetName()}: adopted existing guide NPC (id={existingLive.Id:X}) already present in this region instance");
                return;
            }

            foreach (WorldEntity existing in staleGuides)
            {
                if (existing.IsInWorld) existing.ExitWorld();
                existing.Destroy();
            }
            if (staleGuides.Count > 0)
                DrEndlessLogger.Info($"[DangerRoomEndless] {GetName()}: removed {staleGuides.Count} stale guide NPC(s) before respawn");

            AgentPrototype agentProto = ((PrototypeId)DrEndlessGuideHeroRef).As<AgentPrototype>();
            if (agentProto == null)
            {
                DrEndlessLogger.Warn($"[DangerRoomEndless] {GetName()}: guide hero ref did not resolve to an AgentPrototype");
                return;
            }

            Agent npc = EntityHelper.CreateAgent(agentProto, avatar, s_drEndlessGuidePosition, s_drEndlessGuideOrientation);
            if (npc == null)
            {
                DrEndlessLogger.Warn($"[DangerRoomEndless] {GetName()}: failed to spawn guide NPC (CreateAgent returned null)");
                return;
            }

            npc.Properties[PropertyEnum.AIStartsEnabled] = false;
            npc.Properties[PropertyEnum.Interactable] = true;

            DetachDrGuideNpc();
            _drGuideNpcId = npc.Id;
            _drGuideRegion = region;
            _drGuideInteractAction ??= OnDrGuideInteract;
            region.PlayerInteractEvent.AddActionBack(_drGuideInteractAction);

            DrEndlessLogger.Info($"[DangerRoomEndless] {GetName()}: guide NPC spawned in Danger Room hub (id={npc.Id:X})");
        }

        private void DetachDrGuideNpc()
        {
            if (_drGuideRegion != null && _drGuideInteractAction != null)
                _drGuideRegion.PlayerInteractEvent.RemoveAction(_drGuideInteractAction);
            _drGuideRegion = null;
            _drGuideNpcId = 0;
        }

        private void OnDrGuideInteract(in PlayerInteractGameEvent evt)
        {
            if (evt.Player != this) return;
            if (evt.InteractableObject == null || evt.InteractableObject.Id != _drGuideNpcId) return;

            var dialog = Game.GameDialogManager.CreateInstance(DatabaseUniqueId);
            dialog.Message.LocaleString = (LocaleStringId)DrEndlessGuideDialogMessageStringId;
            dialog.Options = DialogOptionEnum.ScreenBottom;
            dialog.OnResponse = OnDrGuideDialogResponse;
            dialog.AddButton(GameDialogResultEnum.eGDR_Option1, (LocaleStringId)TrialDialogYesStringId, ButtonStyle.Primary, false);
            Game.GameDialogManager.ShowDialog(dialog);
        }

        private void OnDrGuideDialogResponse(ulong playerGuid, DialogResponse response)
        {
            if (playerGuid != DatabaseUniqueId) return;
            if (response.ButtonIndex != GameDialogResultEnum.eGDR_Option1) return;

            Avatar avatar = CurrentAvatar;
            if (avatar == null || avatar.IsInWorld == false) return;

            _drWarpPending = true;
            avatar.TeleportToRegionFromWeb(DrEndlessArenaRegionRef);
            DrEndlessLogger.Info($"[DangerRoomEndless] {GetName()}: confirmed — warping to Endless Wave training arena");
        }

        // ---------------- Arena settle (sterilize + suspend native tutorial mission) ----------------

        /// <summary>
        /// Beat between arriving in the arena and actually sterilizing it —
        /// gives the region's own native content (including its tutorial
        /// mission) time to finish spawning before we sweep it and suspend
        /// its scripting. Reuses Player.WaveDirector.cs's ArenaSettleMs (same
        /// partial class, same tuned value for this exact class of region).
        /// </summary>
        private void ScheduleDrArenaSettle()
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler == null) return;
            if (_drArenaSettleTick.IsValid) scheduler.CancelEvent(_drArenaSettleTick);
            scheduler.ScheduleEvent(_drArenaSettleTick, TimeSpan.FromMilliseconds(ArenaSettleMs), _drEndlessEvents);
            _drArenaSettleTick.Get().Initialize(this);
        }

        private void OnDrArenaSettleTick()
        {
            Avatar avatar = CurrentAvatar;
            if (avatar == null || avatar.IsInWorld == false) return;

            Region region = avatar.Region;
            if (region == null || region.PrototypeDataRef != (PrototypeId)DrEndlessArenaRegionRef)
                return; // left before the settle tick fired

            // Suspend the region's own tutorial mission BEFORE sterilizing —
            // SetSuspendedState(true) unregisters its event hooks so its
            // scripted logic (dialogue, forced spawns, the timed return to
            // the Danger Room hub) stops ticking entirely. ClearArena alone
            // only removes entities; it can't stop a mission's own scripting.
            int suspended = region.MissionManager?.SuspendAllMissions() ?? 0;
            avatar.ResetTutorialProps(); // safety net — see the freshWarp branch above for why this must never be left stuck
            int removed = ClearArena(avatar);
            DrEndlessLogger.Info($"[DangerRoomEndless] {GetName()}: arena settled — suspended {suspended} mission(s), removed {removed} native entity(ies)");

            // Bodyslide out of this arena should land back in the Danger
            // Room hub, not wherever the player's account last considered
            // its "town" (Bodyslider.GetBodyslideTargetRef checks
            // PropertyEnum.RegionBodysliderTargetOverride on the CURRENT
            // region before falling back to LastTownRegionForAccount — see
            // Regions/Bodyslider.cs). Set it once per arena instance.
            PrototypeId hubStartTarget = ((PrototypeId)DrEndlessHubRegionRef).As<RegionPrototype>()?.StartTarget ?? PrototypeId.Invalid;
            if (hubStartTarget != PrototypeId.Invalid)
            {
                PrototypeId metaGameTeamBase = GameDatabase.GlobalsPrototype.MetaGameTeamBase;
                region.Properties[PropertyEnum.RegionBodysliderTargetOverride, metaGameTeamBase] = hubStartTarget;
            }

            SpawnDrTerminalNpc(region, avatar);
        }

        private void CancelDrArenaSettle()
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler != null && _drArenaSettleTick.IsValid) scheduler.CancelEvent(_drArenaSettleTick);
        }

        private sealed class DrEndlessArenaSettleTickEvent : CallMethodEvent<Player>
        {
            protected override CallbackDelegate GetCallback() => static (player) => player.OnDrArenaSettleTick();
        }

        // ---------------- Terminal (training arena) ----------------

        private void SpawnDrTerminalNpc(Region region, Avatar avatar)
        {
            // 4-player co-op groundwork (Phase 2) — same adopt-if-live
            // pattern as SpawnDrGuideNpc: a second real player joining this
            // region instance should reuse the first player's already-spawned
            // terminal instead of creating a duplicate prop alongside it.
            var sweepSphere = new MHServerEmu.Core.Collisions.Sphere(s_drEndlessTerminalPosition, 50f);
            var sweepCtx = new EntityRegionSPContext(EntityRegionSPContextFlags.PrimaryPartition);
            foreach (WorldEntity existing in region.IterateEntitiesInVolume(sweepSphere, sweepCtx))
            {
                if (existing == null || existing.IsDestroyed || existing.IsInWorld == false) continue;
                if (existing.PrototypeDataRef != (PrototypeId)DrEndlessTerminalHeroRef) continue;

                DetachDrTerminalNpc();
                _drTerminalNpcId = existing.Id;
                _drTerminalRegion = region;
                _drTerminalInteractAction ??= OnDrTerminalInteract;
                region.PlayerInteractEvent.AddActionBack(_drTerminalInteractAction);
                DrEndlessLogger.Info($"[DangerRoomEndless] {GetName()}: adopted existing terminal (id={existing.Id:X}) already present in this region instance");
                return;
            }

            AgentPrototype agentProto = ((PrototypeId)DrEndlessTerminalHeroRef).As<AgentPrototype>();
            if (agentProto == null)
            {
                DrEndlessLogger.Warn($"[DangerRoomEndless] {GetName()}: terminal ref did not resolve to an AgentPrototype");
                return;
            }

            Agent npc = EntityHelper.CreateAgent(agentProto, avatar, s_drEndlessTerminalPosition, s_drEndlessTerminalOrientation);
            if (npc == null)
            {
                DrEndlessLogger.Warn($"[DangerRoomEndless] {GetName()}: failed to spawn terminal (CreateAgent returned null)");
                return;
            }

            npc.Properties[PropertyEnum.AIStartsEnabled] = false;
            npc.Properties[PropertyEnum.Interactable] = true;

            DetachDrTerminalNpc();
            _drTerminalNpcId = npc.Id;
            _drTerminalRegion = region;
            _drTerminalInteractAction ??= OnDrTerminalInteract;
            region.PlayerInteractEvent.AddActionBack(_drTerminalInteractAction);

            DrEndlessLogger.Info($"[DangerRoomEndless] {GetName()}: terminal spawned in training arena (id={npc.Id:X})");
        }

        private void DetachDrTerminalNpc()
        {
            if (_drTerminalRegion != null && _drTerminalInteractAction != null)
                _drTerminalRegion.PlayerInteractEvent.RemoveAction(_drTerminalInteractAction);
            _drTerminalRegion = null;
            _drTerminalNpcId = 0;
        }

        /// <summary>
        /// Remove the terminal NPC from the world entirely (not just stop
        /// tracking it) so it can't be interacted with again mid-run — called
        /// once Endless Challenge actually starts. Re-spawned by
        /// RespawnDrTerminalAfterEndlessChallenge once the run ends.
        /// </summary>
        private void DespawnDrTerminalNpc()
        {
            if (_drTerminalNpcId != 0)
            {
                var existingNpc = Game.EntityManager.GetEntity<WorldEntity>(_drTerminalNpcId);
                if (existingNpc != null && existingNpc.IsDestroyed == false)
                {
                    if (existingNpc.IsInWorld) existingNpc.ExitWorld();
                    existingNpc.Destroy();
                    DrEndlessLogger.Info($"[DangerRoomEndless] {GetName()}: terminal despawned (Endless Challenge started)");
                }
            }
            DetachDrTerminalNpc();
        }

        /// <summary>
        /// Called from Player.WaveDirector.cs's EndEndlessChallenge (same
        /// partial class) once a run ends (extract or wipe) — brings the
        /// terminal back so another run can be started, as long as the
        /// player is still standing in the training arena.
        /// </summary>
        internal void RespawnDrTerminalAfterEndlessChallenge()
        {
            Avatar avatar = CurrentAvatar;
            if (avatar == null || avatar.IsInWorld == false) return;
            Region region = avatar.Region;
            if (region == null || region.PrototypeDataRef != (PrototypeId)DrEndlessArenaRegionRef) return;
            if (_drTerminalNpcId != 0) return; // already present

            SpawnDrTerminalNpc(region, avatar);
        }

        // ---------------- Loot break (every EndlessLootBreakEveryNWaves waves) ----------------

        /// <summary>
        /// Called from Player.WaveDirector.cs's AdvanceAfterWaveCleared (same
        /// partial class) every EndlessLootBreakEveryNWaves cleared waves —
        /// pauses the run and brings the terminal + a real Stash box back so
        /// the player can safely bank loot before continuing. No-op outside
        /// the arena (same region-gate RespawnDrTerminalAfterEndlessChallenge
        /// already uses).
        /// </summary>
        internal void TriggerEndlessLootBreak()
        {
            Avatar avatar = CurrentAvatar;
            if (avatar == null || avatar.IsInWorld == false) return;
            Region region = avatar.Region;
            if (region == null || region.PrototypeDataRef != (PrototypeId)DrEndlessArenaRegionRef) return;

            PauseWaveRun(true);
            if (_drTerminalNpcId == 0) SpawnDrTerminalNpc(region, avatar);
            SpawnDrStashBox(region, avatar);
            SpawnDrPortal(region, avatar);

            BroadcastEndlessBannerLines("💰 LOOT BREAK — bank your gear at the stash, then talk to the technician to continue, or step into the portal to return to the Danger Room hub.");
            DrEndlessLogger.Info($"[DangerRoomEndless] {GetName()}: loot break triggered at wave {_endlessCycle}");
        }

        private void SpawnDrStashBox(Region region, Avatar avatar)
        {
            var stashProto = ((PrototypeId)DrEndlessStashRef).As<AgentPrototype>();
            if (stashProto == null)
            {
                DrEndlessLogger.Warn($"[DangerRoomEndless] {GetName()}: stash ref did not resolve to an AgentPrototype");
                return;
            }

            DespawnDrStashBox();

            Vector3 stashPos = s_drEndlessTerminalPosition + s_drEndlessStashOffset;
            Agent stash = EntityHelper.CreateAgent(stashProto, avatar, stashPos, s_drEndlessTerminalOrientation);
            if (stash == null)
            {
                DrEndlessLogger.Warn($"[DangerRoomEndless] {GetName()}: failed to spawn stash box (CreateAgent returned null)");
                return;
            }

            stash.Properties[PropertyEnum.AIStartsEnabled] = false;
            stash.Properties[PropertyEnum.Interactable] = true;
            stash.Properties[PropertyEnum.OpenPlayerStash] = true;

            _drStashNpcId = stash.Id;
            DrEndlessLogger.Info($"[DangerRoomEndless] {GetName()}: stash box spawned in training arena (id={stash.Id:X})");
        }

        private void DespawnDrStashBox()
        {
            if (_drStashNpcId != 0)
            {
                var existing = Game.EntityManager.GetEntity<WorldEntity>(_drStashNpcId);
                if (existing != null && existing.IsDestroyed == false)
                {
                    if (existing.IsInWorld) existing.ExitWorld();
                    existing.Destroy();
                }
            }
            _drStashNpcId = 0;
        }

        /// <summary>
        /// Spawns the loot-break "leave now" return portal alongside the
        /// terminal/stash. A real Transition entity (not an Agent prop like
        /// the stash) -- Avatar.cs's generic interact handler already knows
        /// how to use any Transition it's interacted with, so no custom
        /// interact/teleport code is needed here at all; the prototype's own
        /// ReturnToLastTown type handles it natively, same as every other
        /// return-to-town portal in the real game.
        /// </summary>
        // Cached after the first resolution attempt -- which candidate
        // resolves never changes for the lifetime of a running server, so
        // there's no need to re-check every loot break.
        private static PrototypeId? s_drEndlessReturnPortalRefResolved;

        private static PrototypeId GetValidDrEndlessReturnPortalRef()
        {
            if (s_drEndlessReturnPortalRefResolved != null)
                return s_drEndlessReturnPortalRefResolved.Value;

            foreach (ulong candidate in s_drEndlessReturnPortalRefCandidates)
            {
                if (GameDatabase.PrototypeExists((PrototypeId)candidate))
                {
                    s_drEndlessReturnPortalRefResolved = (PrototypeId)candidate;
                    return s_drEndlessReturnPortalRefResolved.Value;
                }
            }

            DrEndlessLogger.Warn("[DangerRoomEndless] none of the known return-portal prototypes resolve on this server");
            s_drEndlessReturnPortalRefResolved = PrototypeId.Invalid;
            return PrototypeId.Invalid;
        }

        private void SpawnDrPortal(Region region, Avatar avatar)
        {
            DespawnDrPortal();

            PrototypeId portalRef = GetValidDrEndlessReturnPortalRef();
            if (portalRef == PrototypeId.Invalid)
                return; // already logged in GetValidDrEndlessReturnPortalRef

            using EntitySettings entitySettings = ObjectPoolManager.Instance.Get<EntitySettings>();
            entitySettings.EntityRef = portalRef;
            entitySettings.Position = s_drEndlessTerminalPosition + s_drEndlessPortalOffset;
            entitySettings.Orientation = s_drEndlessTerminalOrientation;
            entitySettings.RegionId = region.Id;

            WorldEntity portal = Game.EntityManager.CreateEntity(entitySettings) as WorldEntity;
            if (portal == null)
            {
                DrEndlessLogger.Warn($"[DangerRoomEndless] {GetName()}: failed to spawn return portal (CreateEntity returned null)");
                return;
            }

            portal.Properties[PropertyEnum.Interactable] = true;

            _drPortalNpcId = portal.Id;
            _drPortalRegion = region;
            _drPortalInteractAction ??= OnDrPortalInteract;
            region.PlayerInteractEvent.AddActionBack(_drPortalInteractAction);
            DrEndlessLogger.Info($"[DangerRoomEndless] {GetName()}: return portal spawned in training arena (id={portal.Id:X})");
        }

        /// <summary>
        /// Avatar.cs's generic OnPlayerInteracted always calls UseTransition()
        /// on any interacted Transition entity right after invoking
        /// PlayerInteractEvent, which for this prototype's type resolves to
        /// Teleporter.TeleportToLastTown() -> PropertyEnum.LastTownRegionForAccount.
        /// DangerRoomHubRegion has Behavior=PrivateNonStory (confirmed live,
        /// 2026-07-29), not Town, so it's never naturally tracked as a "last
        /// town" -- without this, the portal would send the player to
        /// whatever their REAL last town actually was (Avengers Tower,
        /// wherever), not back here. Point it at the hub just-in-time, right
        /// before the engine's own UseTransition() call reads it (this
        /// handler runs first, synchronously, as part of the same
        /// PlayerInteractEvent.Invoke() the engine calls before UseTransition),
        /// then restore the player's real value shortly after so normal
        /// Bodyslide-to-town behavior elsewhere is never affected.
        /// </summary>
        private void OnDrPortalInteract(in PlayerInteractGameEvent evt)
        {
            if (evt.Player != this) return;
            if (evt.InteractableObject == null || evt.InteractableObject.Id != _drPortalNpcId) return;

            PrototypeId previousLastTown = Properties[PropertyEnum.LastTownRegionForAccount];
            Properties[PropertyEnum.LastTownRegionForAccount] = (PrototypeId)DrEndlessHubRegionRef;

            var scheduler = Game?.GameEventScheduler;
            if (scheduler != null)
            {
                if (_drPortalLastTownRestoreTick.IsValid) scheduler.CancelEvent(_drPortalLastTownRestoreTick);
                scheduler.ScheduleEvent(_drPortalLastTownRestoreTick, TimeSpan.FromMilliseconds(DrPortalLastTownRestoreMs), _drEndlessEvents);
                _drPortalLastTownRestoreTick.Get().Initialize(this, previousLastTown);
            }

            DrEndlessLogger.Info($"[DangerRoomEndless] {GetName()}: return portal used — LastTownRegionForAccount pointed at hub, will restore in {DrPortalLastTownRestoreMs}ms");
        }

        private void RestoreLastTownAfterPortal(PrototypeId previousLastTown)
        {
            Properties[PropertyEnum.LastTownRegionForAccount] = previousLastTown;
            DrEndlessLogger.Info($"[DangerRoomEndless] {GetName()}: restored real LastTownRegionForAccount after portal use");
        }

        private sealed class DrPortalLastTownRestoreEvent : CallMethodEventParam1<Player, PrototypeId>
        {
            protected override CallbackDelegate GetCallback() => static (player, previousLastTown) => player.RestoreLastTownAfterPortal(previousLastTown);
        }

        private void DetachDrPortalNpc()
        {
            if (_drPortalRegion != null && _drPortalInteractAction != null)
                _drPortalRegion.PlayerInteractEvent.RemoveAction(_drPortalInteractAction);
            _drPortalRegion = null;
        }

        private void DespawnDrPortal()
        {
            DetachDrPortalNpc();

            if (_drPortalNpcId != 0)
            {
                var existing = Game.EntityManager.GetEntity<WorldEntity>(_drPortalNpcId);
                if (existing != null && existing.IsDestroyed == false)
                {
                    if (existing.IsInWorld) existing.ExitWorld();
                    existing.Destroy();
                }
            }
            _drPortalNpcId = 0;
        }

        private void OnDrTerminalInteract(in PlayerInteractGameEvent evt)
        {
            if (evt.Player != this) return;
            if (evt.InteractableObject == null || evt.InteractableObject.Id != _drTerminalNpcId) return;

            var dialog = Game.GameDialogManager.CreateInstance(DatabaseUniqueId);
            dialog.OnResponse = OnDrTerminalDialogResponse;

            // Difficulty is only ever asked the FIRST time a run is started
            // (or by whoever hosts a fresh shared run) — every later
            // interaction with the terminal while a run is active/paused
            // (loot break, or a non-host teammate resuming the host's run)
            // is just "continue", never re-prompted. See
            // ShouldShowEndlessDifficultyPicker.
            if (ShouldShowEndlessDifficultyPicker())
            {
                // Confirmed live 2026-07-27 — a 3-button dialog only ever
                // renders 2 buttons client-side (tried both ScreenBottom and
                // MouseCenter; every OTHER dialog anywhere in the codebase
                // only ever used 1-2 buttons too, so this looks like a real
                // 2-button cap on the client's dialog widget, not something
                // fixable via DialogOptionEnum). Split into two chained
                // 2-button dialogs instead: this one offers the single-click
                // "just start" (Recruit, the easiest/most beginner-friendly
                // tier) plus "More Options..." for Veteran/Omega-Level,
                // handled by OnDrTerminalDifficultyAdvancedResponse below.
                dialog.Options = DialogOptionEnum.MouseCenter;
                dialog.Message.LocaleString = (LocaleStringId)DrEndlessDifficultyPromptStringId;
                dialog.AddButton(GameDialogResultEnum.eGDR_Option1, (LocaleStringId)DrEndlessDifficultyRecruitStringId, ButtonStyle.Primary, false);
                dialog.AddButton(GameDialogResultEnum.eGDR_Option2, (LocaleStringId)DrEndlessDifficultyMoreOptionsStringId, ButtonStyle.Primary, false);
            }
            else
            {
                dialog.Options = DialogOptionEnum.ScreenBottom;
                dialog.Message.LocaleString = (LocaleStringId)DrEndlessTerminalDialogMessageStringId;
                dialog.AddButton(GameDialogResultEnum.eGDR_Option1, (LocaleStringId)TrialDialogYesStringId, ButtonStyle.Primary, false);
            }

            Game.GameDialogManager.ShowDialog(dialog);
        }

        /// <summary>
        /// True only when this interaction would actually START a fresh run
        /// — never when continuing/resuming (own run paused for a loot
        /// break, or a non-host teammate resuming the host's already-active
        /// shared run). Mirrors the exact same state checks
        /// OnDrTerminalDialogResponse falls through before calling
        /// StartEndlessChallenge, kept in one place so the prompt shown here
        /// can never drift out of sync with what the response handler
        /// actually does with the button that gets clicked.
        /// </summary>
        private bool ShouldShowEndlessDifficultyPicker()
        {
            Region region = CurrentAvatar?.Region;
            ulong hostDbId = region?.EndlessHostPlayerDbId ?? 0;
            if (hostDbId != 0 && hostDbId != DatabaseUniqueId)
            {
                Player host = Game.EntityManager.GetEntityByDbGuid<Player>(hostDbId);
                if (host != null && host.IsEndlessChallengeActive)
                    return false; // continuing/resuming someone else's shared run
            }

            return IsEndlessChallengeActive == false;
        }

        private void OnDrTerminalDialogResponse(ulong playerGuid, DialogResponse response)
        {
            if (playerGuid != DatabaseUniqueId) return;
            if (response.ButtonIndex != GameDialogResultEnum.eGDR_Option1
                && response.ButtonIndex != GameDialogResultEnum.eGDR_Option2
                && response.ButtonIndex != GameDialogResultEnum.eGDR_Option3)
                return;

            // 4-player co-op groundwork (Phase 2) — if another real player
            // already owns an active shared run in this region instance,
            // route to THEIR WaveDirector state instead of starting/
            // duplicating our own independent one. All of the run's actual
            // state (waves, hazards, bosses, chests) lives on the host
            // Player instance; a non-host interaction here only ever
            // pauses/resumes THAT run, never starts a second one.
            Region region = CurrentAvatar?.Region;
            ulong hostDbId = region?.EndlessHostPlayerDbId ?? 0;
            if (hostDbId != 0 && hostDbId != DatabaseUniqueId)
            {
                Player host = Game.EntityManager.GetEntityByDbGuid<Player>(hostDbId);
                if (host != null && host.IsEndlessChallengeActive)
                {
                    if (host.IsWaveRunPaused)
                    {
                        host.PauseWaveRun(false);
                        host.DespawnDrTerminalNpc();
                        host.DespawnDrStashBox();
                        host.DespawnDrPortal();
                        host.BroadcastEndlessBannerLines("⚔ Endless Wave resumes!");
                        DrEndlessLogger.Info($"[DangerRoomEndless] {GetName()}: resumed {host.GetName()}'s shared run via terminal");
                    }
                    else
                    {
                        DrEndlessLogger.Info($"[DangerRoomEndless] {GetName()}: terminal interact ignored — {host.GetName()}'s Endless Challenge already active");
                    }
                    return;
                }

                // Host is gone or their run already ended without clearing
                // the marker (shouldn't normally happen — EndEndlessChallenge
                // clears it — but don't strand every future interaction on a
                // stale reference if it does).
                if (region != null) region.EndlessHostPlayerDbId = 0;
            }

            if (IsEndlessChallengeActive)
            {
                if (IsWaveRunPaused)
                {
                    // Loot break — resume instead of trying (and failing) to
                    // start a second run. Terminal + stash disappear again
                    // once combat resumes, matching the "gone while a run is
                    // active" convention DespawnDrTerminalNpc already sets.
                    PauseWaveRun(false);
                    DespawnDrTerminalNpc();
                    DespawnDrStashBox();
                    DespawnDrPortal();
                    BroadcastEndlessBannerLines("⚔ Endless Wave resumes!");
                    DrEndlessLogger.Info($"[DangerRoomEndless] {GetName()}: loot break ended — run resumed");
                    return;
                }

                DrEndlessLogger.Info($"[DangerRoomEndless] {GetName()}: terminal interact ignored — Endless Challenge already active");
                return;
            }

            // First dialog: Option1=Recruit (single-click, easiest tier),
            // Option2="More Options..." drills into a second 2-button dialog
            // instead of starting anything yet.
            if (response.ButtonIndex == GameDialogResultEnum.eGDR_Option2)
            {
                ShowEndlessDifficultyAdvancedDialog();
                return;
            }

            StartFreshEndlessRun(EndlessDifficulty.Recruit);
        }

        /// <summary>Second-tier difficulty dialog — only reachable via "More Options..." on the first prompt. Offers Veteran (today's original tuning) and Omega-Level.</summary>
        private void ShowEndlessDifficultyAdvancedDialog()
        {
            var dialog = Game.GameDialogManager.CreateInstance(DatabaseUniqueId);
            dialog.Options = DialogOptionEnum.MouseCenter;
            dialog.OnResponse = OnDrTerminalDifficultyAdvancedResponse;
            dialog.Message.LocaleString = (LocaleStringId)DrEndlessDifficultyAdvancedPromptStringId;
            dialog.AddButton(GameDialogResultEnum.eGDR_Option1, (LocaleStringId)DrEndlessDifficultyVeteranStringId, ButtonStyle.Primary, false);
            dialog.AddButton(GameDialogResultEnum.eGDR_Option2, (LocaleStringId)DrEndlessDifficultyOmegaStringId, ButtonStyle.Primary, false);
            Game.GameDialogManager.ShowDialog(dialog);
        }

        private void OnDrTerminalDifficultyAdvancedResponse(ulong playerGuid, DialogResponse response)
        {
            if (playerGuid != DatabaseUniqueId) return;
            if (response.ButtonIndex != GameDialogResultEnum.eGDR_Option1 && response.ButtonIndex != GameDialogResultEnum.eGDR_Option2)
                return;

            StartFreshEndlessRun(response.ButtonIndex == GameDialogResultEnum.eGDR_Option1 ? EndlessDifficulty.Veteran : EndlessDifficulty.OmegaLevel);
        }

        /// <summary>Shared tail for both dialog chains once a difficulty has actually been chosen.</summary>
        private void StartFreshEndlessRun(EndlessDifficulty difficulty)
        {
            var baseEntry = new WaveEntryDef
            {
                IsEnemyPhantom = true,
                HeroRef = 0, // random hero each spawn
                Count = 1,
                Level = 0,   // match player
                Rank = 1,
            };

            // Already standing in the sterilized arena — no additional warp
            // or clear needed (arenaRegionRef=0, clearArena=false).
            string result = StartEndlessChallenge(baseEntry, 5000, 0, false,
                DrEndlessCountScalePerWave, DrEndlessLevelBumpPerWave, DrEndlessRewardLootTableRef, difficulty);
            BroadcastEndlessBannerLines($"🎯 Difficulty: {difficulty}");
            DrEndlessLogger.Info($"[DangerRoomEndless] {GetName()}: terminal confirmed — difficulty={difficulty} — {result}");

            // Only remove the terminal once the run actually started —
            // IsEndlessChallengeActive is set by StartEndlessChallenge on
            // success, so this can't strand the player with no way to
            // interact with anything if the start attempt failed.
            if (IsEndlessChallengeActive)
                DespawnDrTerminalNpc();
        }

        /// <summary>Called from Player.OnDeallocate so dangling event subscriptions can't outlive the player (logout/disconnect).</summary>
        internal void UnsubscribeDangerRoomEndlessTracking()
        {
            CancelDrArenaSettle();
            DetachDrGuideNpc();
            DetachDrTerminalNpc();
            DespawnDrStashBox();
            DespawnDrPortal();

            var scheduler = Game?.GameEventScheduler;
            if (scheduler != null && _drPortalLastTownRestoreTick.IsValid)
                scheduler.CancelEvent(_drPortalLastTownRestoreTick);
        }
    }
}
