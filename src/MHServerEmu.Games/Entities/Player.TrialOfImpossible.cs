using System;
using System.Collections.Generic;
using Gazillion;
using MHServerEmu.Core.Extensions;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.Events.Templates;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Loot;
using MHServerEmu.Games.Properties;
using MHServerEmu.Games.Regions;
using MHServerEmu.Games.UI;
using MHServerEmu.Games.UI.Widgets;

namespace MHServerEmu.Games.Entities
{
    // Trial of the Impossible — a stationary "Trial Guide" NPC (a Nick Fury
    // phantom, cosmetic only) spawned in Avengers Tower. Clicking it shows a
    // real native Yes/No dialog (GameDialogManager — confirmed working via
    // MetaStateShutdown's teleport-confirm dialog); confirming warps the
    // player to a random known-safe boss arena and starts a full escalating
    // gauntlet: one nemesis phantom per stage, rank climbing 1->5 over the
    // first 4 stages, then stage 5 onward fighting two concurrent phantoms
    // at rank 5, while cycling through every real playable hero exactly
    // once — until the roster runs out, at which point the final stage is a
    // heavily buffed mirror of the player's own hero with periodic hazard
    // adds and a lootsplosion on death (the only stage that drops loot at
    // all — regular stages drop nothing). Outcome is
    // tracked onto the account leaderboard the same way Player.Leaderboard.cs
    // already tracks TerminalRun attempts (region-entry start, EntityDeadEvent
    // completion, "left without killing = aborted").
    public partial class Player
    {
        private static readonly Logger TrialLogger = LogManager.CreateLogger();

        // Same proven-safe arena pool the app's Wave Director / Endless
        // Challenge / Boss Rush curation already validated via live testing
        // (see their s_arenaPathMatches comments) — mirrored here as real
        // RegionPrototypeId values so a random pick can happen server-side
        // without the app in the loop.
        private static readonly RegionPrototypeId[] s_trialArenaPool =
        {
            RegionPrototypeId.CH0207TaskmasterRegion,
            RegionPrototypeId.CH0803MandarinBossRegion,
            RegionPrototypeId.CH0809DrDoomBossRegion,
            RegionPrototypeId.CH0906LokiBossRegion,
            RegionPrototypeId.MrSinisterBaseRegion,
            RegionPrototypeId.CH0206TaskmasterVHSTapeConstructionRegion,
            RegionPrototypeId.CH0409MoloidRegion,
            RegionPrototypeId.CH0503SupervillainRecCenterRegion,
            RegionPrototypeId.CH0605StrykerBunkerRegion,
            RegionPrototypeId.CH0606MagnetoBunkerRegion,
            RegionPrototypeId.CH0705SabretoothShowdownRegion,
            RegionPrototypeId.CH0707SinisterLabRegion,
            RegionPrototypeId.CH0808DoomCastleRegion,
            RegionPrototypeId.CH0903AsgardiaInstanceRegion,
            RegionPrototypeId.CH0101HellsKitchenRegion,
            RegionPrototypeId.CH0102PowerPlantRegion,
            RegionPrototypeId.CH0103NYPDRegion,
            RegionPrototypeId.CH0104SubwayRegion,
            RegionPrototypeId.CH0105NightclubRegion,
            RegionPrototypeId.CH0106KPWarehouseRegion,
            RegionPrototypeId.CH0201ShippingYardRegion,
            RegionPrototypeId.CH0202HoodSightingContainerRegion,
            RegionPrototypeId.CH0203RhinoBargeRegion,
            RegionPrototypeId.CH0204Q36AIMLabRegion,
            RegionPrototypeId.CH0205ConstructionRegion,
            RegionPrototypeId.CH0208CanneryRegion,
            RegionPrototypeId.CH0209HoodsHideoutRegion,
            RegionPrototypeId.CH0302HydraOutpostRegion,
            RegionPrototypeId.CH0303WatermillRegion,
            RegionPrototypeId.CH0304PoisonGladeRegion,
            RegionPrototypeId.CH0305ReconPostRegion,
            RegionPrototypeId.CH0306PrincessBarRegion,
            RegionPrototypeId.CH0307HandTowerRegion,
            RegionPrototypeId.CH0403MGHStorageRegion,
            RegionPrototypeId.CH0404MGHFactoryRegion,
            RegionPrototypeId.CH0405WaxMuseumRegion,
            RegionPrototypeId.CH0406SubwayRegion,
            RegionPrototypeId.CH0407NYPDRooftopRegion,
            RegionPrototypeId.CH0408MaggiaRestaurantRegion,
            RegionPrototypeId.CH0410FiskTowerRegion,
            RegionPrototypeId.CH0502MutantWarehouseRegion,
            RegionPrototypeId.CH0504PurifierChurchRegion,
            RegionPrototypeId.CH0602DeepCavernRegion,
            RegionPrototypeId.CH0603CircusSideshowRegion,
            RegionPrototypeId.CH0604AIMWeaponsLabRegion,
            RegionPrototypeId.CH0702SauronCavesRegion,
            RegionPrototypeId.CH0703BroodCavesRegion,
            RegionPrototypeId.CH0706MutateCavesRegion,
            RegionPrototypeId.CH0801AIMWeaponFacilityRegion,
            RegionPrototypeId.CH0802HYDRAIslandRegion,
            RegionPrototypeId.CH0902NorwayDarkForestRegion,
            RegionPrototypeId.CH0905CanalRegion,
            RegionPrototypeId.HellsKitchen01Region,
            RegionPrototypeId.NightclubRegion,
            RegionPrototypeId.BrooklynRegion,
            RegionPrototypeId.HellsKitchen02RedlightRegion,
            RegionPrototypeId.UpperEastSideRegion,
            RegionPrototypeId.FiskTowerRegion,
            RegionPrototypeId.WakandaP1RegionL60,
        };

        // s_trialArenaPool was captured against the 1.52 client and mixes
        // "StoryRevamp" region variants with older "Story" ones. Verified
        // live 2026-07-28 against all three running servers via
        // /webapi/protoeditor/fields: 51 of the 59 StoryRevamp entries do
        // NOT exist on 1.48 at all (that content postdates the 1.48 client),
        // and WakandaP1RegionL60 doesn't exist on 1.53. A raw random pick
        // from the full pool would try to warp into a nonexistent region
        // roughly 86% of the time on 1.48. Filter to whatever actually
        // resolves on THIS server before picking, cached after first use.
        private static RegionPrototypeId[] s_validTrialArenaPool;

        private static RegionPrototypeId[] GetValidTrialArenaPool()
        {
            if (s_validTrialArenaPool != null) return s_validTrialArenaPool;

            var valid = new List<RegionPrototypeId>();
            foreach (RegionPrototypeId regionId in s_trialArenaPool)
            {
                if (GameDatabase.GetPrototype<RegionPrototype>((PrototypeId)(ulong)regionId) != null)
                    valid.Add(regionId);
            }

            // Never worse than the pre-fix behavior even in the
            // (shouldn't-happen) case where nothing resolved at all.
            s_validTrialArenaPool = valid.Count > 0 ? valid.ToArray() : s_trialArenaPool;
            return s_validTrialArenaPool;
        }

        // A Nick Fury look used purely as a stationary, interactable prop —
        // spawned as a plain Agent (EntityHelper.CreateAgent), NOT via the
        // phantom-hero pipeline. SpawnPhantomHeroFromIntent creates a full
        // synthetic Player+Avatar (the same machinery real squad members
        // use), which the client renders as an actual party member —
        // confirmed live (2026-07-23): the Trial Guide showed up in the
        // player's party frame instead of standing there as a prop. A plain
        // Agent has no Player/party semantics at all.
        //
        // MUST be a genuine NPC-type Agent prototype, not the playable-hero
        // Avatar one — confirmed live (2026-07-23): the Avatar prototype
        // (Entity/Characters/Avatars/Shipping/NickFury.prototype) silently
        // failed inside EntityHelper.CreateAgent (Avatar.ApplyInitialReplicationState
        // throws "Verify failed: player" — it expects a real Player owner
        // that a plain Agent spawn never has). Entity/Characters/NPCs/SHIELD/
        // NickFury.prototype is the real ambient NPC variant, found live via
        // /webapi/prototypes/search.
        private const ulong TrialGuideHeroRef = 0x32CA9743D2251342; // Entity/Characters/NPCs/SHIELD/NickFury.prototype

        // Fixed spot next to the helicopter in Avengers Tower (NPEAvengersTowerHUBRegion) —
        // captured live via /webapi/debug/position (2026-07-23).
        private static readonly Vector3 s_trialGuidePosition = new(1170.625f, 1056.375f, 376f);
        private static readonly Orientation s_trialGuideOrientation = new(-2.625f, 0f, 0f);

        // LocaleStringId is NOT the same hash space as a generic PrototypeId
        // (confirmed live 2026-07-23: casting Localization/Translations/Dialogs/
        // Yes.prototype's ProtoRef to LocaleStringId rendered "Invalid
        // localeStringId" client-side). The button text below was instead
        // read off a real, working DangerRoom shutdown dialog
        // (MetaStateShutdownPrototype.TeleportDialog) via a one-off
        // /webapi/debug/dialogtext diagnostic — confirmed to render
        // correctly ("Continue"). Only one real button exists on that
        // source dialog (no "No"), so the Trial dialog is single-button:
        // closing/dismissing without clicking naturally resolves to
        // GameDialogResultEnum.eGDR_Closed, which OnTrialDialogResponse
        // already treats as "not confirmed."
        //
        // The message text uses a custom string instead — Data/Game/
        // Achievements/AchievementStringMap_99_TrialOfImpossible.json defines
        // a brand-new LocaleStringId ("1234567890123456789", picked to avoid
        // any real hash collision) mapped to "Trial of the Impossible".
        // AchievementStringMap is a general client string-table push (sent
        // via NetMessageAchievementDatabaseDump at login), not scoped to the
        // achievements panel — same technique already confirmed working
        // elsewhere for area names/NPC dialogue. Untested for THIS specific
        // use (a GameDialogInstance message) until verified live.
        private const ulong TrialDialogMessageStringId = 1234567890123456789; // "Trial of the Impossible" (custom)
        private const ulong TrialDialogYesStringId = 0xB7113937247E0504;      // "Continue" (real, from DangerRoom's dialog)

        // Generic high-tier endgame boss loot table — found live via
        // /webapi/prototypes/search, used only for the finale's lootsplosion
        // (regular stages drop nothing; they're not the point).
        private const ulong TrialFinaleLootTableRef = 0x0520D1A142CA23CD; // Loot/Tables/Mob/Bosses/EndgameDailies/Subtables/SharedEndgameDailiesCosmicBUFFED.prototype
        private const int TrialFinaleLootRolls = 10;
        private const int TrialFinaleGrudgeScale = 80; // bigger than Kaiju Mode's (40) or the old single-nemesis Trial's (60) — "a lot more HP", "damage increase"

        private const int TrialHazardMinDelayMs = 15_000;
        private const int TrialHazardMaxDelayMs = 30_000;

        // Pause between clearing a stage and the next one spawning — gives a
        // beat before the next phantom(s) appear instead of an instant swap.
        private const int TrialStageAdvanceDelayMs = 5_000;

        // Solo-only, limited-life run: 2 respawns allowed (the player can die
        // twice and keep going); the 3rd own-death ends the run and sends
        // them back to Avengers Tower — see OnTrialOwnDeath/DoDeathRelease.
        private const int TrialMaxDeaths = 3;

        // The finale is meant to read as a genuinely higher tier than any
        // regular stage — regular stages cap at real nemesis rank 5
        // (NemesisMaxRank), so the finale is spawned at that same rank (its
        // gear/BiS/loot-tier gating all key off NemesisMaxRank) and then
        // manually pushed further on top, display-labeled as its own
        // "rank 6" instead of reusing rank 5's stars/suffix.
        private const int TrialFinaleDisplayRank = 6;
        private const float TrialFinaleExtraHealthMult = 1.5f;   // on top of rank 5's own 32x
        private const float TrialFinaleExtraDamageMult = 1.35f;  // on top of rank 5's own DamageMult/DamagePctBonus

        // Real orb item prototypes (see EntityHelper.TestOrb) — a small
        // sustain reward per phantom kill during the gauntlet's downtime,
        // distinct from the finale's own lootsplosion (which stays as the
        // only real gear/loot payout).
        private const ulong TrialHealOrbRef = 925659119519994384;       // HealOrbItem
        private const ulong TrialEnduranceOrbRef = 9607833165236212779; // EnduranceOrbItem
        private const int TrialOrbLifespanSec = 20;

        private ulong _trialGuideNpcId;
        private Region _trialGuideRegion;
        private Event<PlayerInteractGameEvent>.Action _trialInteractAction;

        // True from the moment the player confirms the dialog until
        // BeginRegionTransfer snapshots it onto MigrationData (both happen on
        // THIS Player object, before the cross-region transfer destroys it —
        // see SnapshotTrialWarpForTransfer).
        private bool _trialWarpPending;

        private Region _trialArenaRegion;
        private string _trialHeroName;
        private long _trialStartMs;
        private Event<EntityDeadGameEvent>.Action _trialStageDeadAction;

        // Stage progression — one nemesis-hero per roster slot, drawn without
        // repeats until exhausted (see BuildTrialRoster), at which point the
        // next stage is the finale instead of drawing another hero.
        private int _trialStageNumber;
        private readonly List<ulong> _trialStageAliveIds = new();
        private List<PrototypeId> _trialRoster;
        private int _trialRosterIndex;
        private bool _trialIsFinaleStage;

        // Own-death counter (real player only, see Avatar.OnKilled) and a
        // running kill tally driving the HUD widget below.
        private int _trialDeathCount;
        private int _trialKillCount;
        private int _trialTotalEnemies;

        private readonly EventGroup _trialHazardEvents = new();
        private readonly EventPointer<TrialHazardTickEvent> _trialHazardTick = new();
        private readonly EventPointer<TrialStageAdvanceTickEvent> _trialStageAdvanceTick = new();

        /// <summary>
        /// True for the whole gauntlet, every stage including the finale —
        /// suppresses Avatar.PhantomHero.cs's automatic per-kill
        /// DropPhantomGear (same guard shape as IsEndlessChallengeActive) so
        /// regular stages drop nothing and the finale drops ONLY the
        /// explicit DropTrialFinaleLoot lootsplosion, not that PLUS its own
        /// natural worn-gear roll.
        /// </summary>
        public bool IsTrialGauntletActive => _trialArenaRegion != null;

        /// <summary>
        /// Called from Avatar.OnEnteredWorld on every region entry for a real
        /// (non-phantom) player avatar — mirrors Player.Leaderboard.cs's
        /// OnAvatarEnteredRegion call site.
        /// </summary>
        internal void OnAvatarEnteredRegionForTrial(Region region, Avatar avatar)
        {
            if (region == null || avatar == null) return;

            // Read off MigrationData, not a local field — a cross-region
            // transfer destroys the Game instance the confirming Player
            // object lived on (confirmed live 2026-07-23: the warp
            // succeeded but the nemesis never spawned because _trialWarpPending
            // was a plain field on the now-destroyed old Player). See
            // SnapshotTrialWarpForTransfer/MigrationData.PendingTrialWarp.
            var mig = PlayerConnection?.MigrationData;
            if (mig != null && mig.PendingTrialWarp)
            {
                mig.PendingTrialWarp = false;
                StartTrialGauntlet(region, avatar);
                return;
            }

            // Left the arena without clearing it (gave up, died and got sent
            // elsewhere, etc.) — commit as an aborted attempt.
            if (_trialArenaRegion != null && region != _trialArenaRegion)
                EndTrialRun(completed: false);

            PrototypeId regionRef = region.PrototypeDataRef;
            bool isTower = regionRef == (PrototypeId)RegionPrototypeId.AvengersTowerHUBRegion
                        || regionRef == (PrototypeId)RegionPrototypeId.NPEAvengersTowerHUBRegion;

            if (isTower == false)
            {
                DetachTrialGuideNpc();
                return;
            }

            if (_trialGuideRegion == region && _trialGuideNpcId != 0)
            {
                var existingNpc = Game.EntityManager.GetEntity<WorldEntity>(_trialGuideNpcId);
                if (existingNpc != null && existingNpc.IsDestroyed == false && existingNpc.IsInWorld)
                    return; // already set up for this region instance
            }

            SpawnTrialGuideNpc(region, avatar);
        }

        /// <summary>
        /// Snapshot the pending trial-warp confirmation onto MigrationData so
        /// it survives the cross-region Game-instance destroy/recreate.
        /// Called from PlayerConnection.BeginRegionTransfer, right next to
        /// SnapshotWaveRunForTransfer — same problem, same fix shape.
        /// </summary>
        internal void SnapshotTrialWarpForTransfer()
        {
            if (_trialWarpPending == false) return;
            var mig = PlayerConnection?.MigrationData;
            if (mig == null) return;
            mig.PendingTrialWarp = true;
            _trialWarpPending = false; // this Game instance is going away
        }

        private void SpawnTrialGuideNpc(Region region, Avatar avatar)
        {
            AgentPrototype agentProto = ((PrototypeId)TrialGuideHeroRef).As<AgentPrototype>();
            if (agentProto == null)
            {
                TrialLogger.Warn($"[TrialOfImpossible] {GetName()}: Trial Guide hero ref did not resolve to an AgentPrototype");
                return;
            }

            Agent npc = EntityHelper.CreateAgent(agentProto, avatar, s_trialGuidePosition, s_trialGuideOrientation);
            if (npc == null)
            {
                TrialLogger.Warn($"[TrialOfImpossible] {GetName()}: failed to spawn Trial Guide NPC (CreateAgent returned null)");
                return;
            }

            // Stand still — cosmetic prop only, no combat/wander behavior.
            npc.Properties[PropertyEnum.AIStartsEnabled] = false;
            // Legacy per-instance override — makes the click round-trip
            // succeed (CanInteract) without needing prototype-level
            // interaction data, since this NPC isn't a real vendor/trainer.
            npc.Properties[PropertyEnum.Interactable] = true;

            DetachTrialGuideNpc();
            _trialGuideNpcId = npc.Id;
            _trialGuideRegion = region;
            _trialInteractAction ??= OnTrialGuideInteract;
            region.PlayerInteractEvent.AddActionBack(_trialInteractAction);

            TrialLogger.Info($"[TrialOfImpossible] {GetName()}: Trial Guide NPC spawned in Avengers Tower (id={npc.Id:X})");
        }

        private void DetachTrialGuideNpc()
        {
            if (_trialGuideRegion != null && _trialInteractAction != null)
                _trialGuideRegion.PlayerInteractEvent.RemoveAction(_trialInteractAction);
            _trialGuideRegion = null;
            _trialGuideNpcId = 0;
        }

        private void OnTrialGuideInteract(in PlayerInteractGameEvent evt)
        {
            if (evt.Player != this) return;
            if (evt.InteractableObject == null || evt.InteractableObject.Id != _trialGuideNpcId)
            {
                TrialLogger.Info($"[TrialOfImpossible] {GetName()}: interact event ignored (target={evt.InteractableObject?.Id:X}, expected={_trialGuideNpcId:X})");
                return;
            }
            TrialLogger.Info($"[TrialOfImpossible] {GetName()}: Trial Guide interact — showing dialog");

            var dialog = Game.GameDialogManager.CreateInstance(DatabaseUniqueId);
            dialog.Message.LocaleString = (LocaleStringId)TrialDialogMessageStringId;
            dialog.Options = DialogOptionEnum.ScreenBottom;
            dialog.OnResponse = OnTrialDialogResponse;
            dialog.AddButton(GameDialogResultEnum.eGDR_Option1, (LocaleStringId)TrialDialogYesStringId, ButtonStyle.Primary, false);
            Game.GameDialogManager.ShowDialog(dialog);
        }

        private void OnTrialDialogResponse(ulong playerGuid, DialogResponse response)
        {
            if (playerGuid != DatabaseUniqueId) return;
            if (response.ButtonIndex != GameDialogResultEnum.eGDR_Option1) return; // "No" or dismissed

            Avatar avatar = CurrentAvatar;
            if (avatar == null || avatar.IsInWorld == false) return;

            RegionPrototypeId[] validPool = GetValidTrialArenaPool();
            RegionPrototypeId chosen = validPool[Game.Random.Next(validPool.Length)];

            _trialWarpPending = true;
            avatar.TeleportToRegionFromWeb((ulong)chosen);
            TrialLogger.Info($"[TrialOfImpossible] {GetName()}: confirmed — warping to {chosen}");
        }

        // ---------------- Gauntlet progression ----------------

        /// <summary>Arrival in the arena — sterilize it, build the hero roster for this run, and kick off stage 1.</summary>
        private void StartTrialGauntlet(Region region, Avatar avatar)
        {
            // Sterilize first — reuses Player.WaveDirector.cs's ClearArena
            // (same partial class), which destroys every native
            // Agent/Spawner/Transition in the region so the gauntlet isn't
            // fighting alongside (or getting lost among) the arena's own
            // native encounter. Confirmed live 2026-07-23: without this,
            // whatever was already in the arena stayed put.
            int removed = ClearArena(avatar);
            if (removed > 0)
                TrialLogger.Info($"[TrialOfImpossible] {GetName()}: arena sterilized — {removed} native entity(ies) removed");

            // Solo-only — dismiss any existing phantom squad / team-up so
            // nothing but the player fights the gauntlet.
            int purged = PurgePhantoms();
            if (purged > 0)
                TrialLogger.Info($"[TrialOfImpossible] {GetName()}: dismissed {purged} existing phantom(s) for solo trial");
            avatar.DismissTeamUpAgent(true);

            _trialArenaRegion = region;
            _trialStartMs = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
            _trialHeroName = GetFriendlyHeroName(avatar);
            _trialStageNumber = 0;
            _trialRosterIndex = 0;
            _trialRoster = BuildTrialRoster(avatar.PrototypeDataRef);
            _trialDeathCount = 0;
            _trialKillCount = 0;
            _trialTotalEnemies = _trialRoster.Count + 1; // + the finale mirror

            _trialStageDeadAction ??= OnTrialStageEntityDead;
            region.EntityDeadEvent.AddActionBack(_trialStageDeadAction);

            UpdateTrialKillWidget(avatar);

            try { SendBannerLines("☠ THE IMPOSSIBLE TRIAL BEGINS — face every phantom, then face yourself"); } catch { }
            TrialLogger.Info($"[TrialOfImpossible] {GetName()}: gauntlet started, {_trialRoster.Count} hero(es) in the roster");

            AdvanceTrialStage(avatar);
        }

        /// <summary>Every real playable hero except the player's own (reserved for the finale), shuffled once per run.</summary>
        private List<PrototypeId> BuildTrialRoster(PrototypeId excludeHeroRef)
        {
            var roster = new List<PrototypeId>(Avatar.GetAllPlayableHeroRefs());
            roster.RemoveAll(r => r == excludeHeroRef);

            for (int i = roster.Count - 1; i > 0; i--)
            {
                int j = Game.Random.Next(i + 1);
                (roster[i], roster[j]) = (roster[j], roster[i]);
            }
            return roster;
        }

        /// <summary>
        /// Rank climbs 1->5 over stages 1-4 (capped at 5 thereafter); stages
        /// 1-4 fight one phantom at a time. From stage 5 on, rank stays at 5
        /// but the CONCURRENT COUNT keeps escalating instead of sitting flat
        /// forever — scaled by how far through the roster the run is
        /// (not a fixed stage number), so it adapts automatically whether
        /// the roster is 62 heroes or 128+ heroes-and-team-ups: 2 phantoms
        /// through the first half of the roster, 3 through the next 30%,
        /// 4 for the final stretch before the finale. Once the roster can't
        /// cover the next stage's count, that stage becomes the finale.
        /// </summary>
        private void AdvanceTrialStage(Avatar avatar)
        {
            _trialStageAliveIds.Clear();
            _trialStageNumber++;
            int rank = Math.Min(_trialStageNumber, NemesisMaxRank);

            int count;
            if (_trialStageNumber < 5) count = 1;
            else
            {
                double progress = _trialRoster.Count > 0 ? _trialRosterIndex / (double)_trialRoster.Count : 0;
                count = progress < 0.50 ? 2 : progress < 0.80 ? 3 : 4;
            }

            int remaining = _trialRoster.Count - _trialRosterIndex;

            if (remaining < count)
            {
                _trialIsFinaleStage = true;
                SpawnTrialFinale(avatar);
                return;
            }

            _trialIsFinaleStage = false;
            int spawned = 0;
            for (int i = 0; i < count; i++)
            {
                PrototypeId heroRef = _trialRoster[_trialRosterIndex++];
                string display = $"{new string('★', rank)} {FriendlyNameFromRef(heroRef)} {NemesisSuffixForRank(rank)}";
                ulong id = avatar.SpawnNemesisPhantomHero(heroRef, 0, display, rank, out string err);
                if (id != 0) { _trialStageAliveIds.Add(id); spawned++; }
                else TrialLogger.Warn($"[TrialOfImpossible] {GetName()}: stage {_trialStageNumber} spawn failed for {heroRef.GetName()}: {err}");
            }

            if (spawned == 0)
            {
                TrialLogger.Warn($"[TrialOfImpossible] {GetName()}: stage {_trialStageNumber} — every spawn failed, aborting run");
                EndTrialRun(completed: false);
                return;
            }

            try { SendBannerLines($"⚔ Trial Stage {_trialStageNumber} — {spawned} nemesis phantom(s), rank {rank}"); } catch { }
            TrialLogger.Info($"[TrialOfImpossible] {GetName()}: stage {_trialStageNumber} — {spawned}/{count} spawned, rank {rank}, {remaining - count} hero(es) left in roster");
        }

        /// <summary>The finale: a heavily buffed mirror of the player's own hero, periodic hazard adds, lootsplosion on death.</summary>
        private void SpawnTrialFinale(Avatar avatar)
        {
            PrototypeId ownHeroRef = avatar.PrototypeDataRef;
            string display = $"{new string('★', TrialFinaleDisplayRank)} YOUR REFLECTION — THE IMPOSSIBLE";
            ulong id = avatar.SpawnNemesisPhantomHero(ownHeroRef, avatar.CharacterLevel, display,
                NemesisMaxRank, out string err, escapeCount: 0, grudgeScore: TrialFinaleGrudgeScale);
            if (id == 0)
            {
                TrialLogger.Warn($"[TrialOfImpossible] {GetName()}: finale spawn failed: {err}");
                EndTrialRun(completed: false);
                return;
            }

            // Spawned at real nemesis rank 5 (NemesisMaxRank) so its gear/
            // BiS/loot-tier gating all key off the tested, existing rank-5
            // path — then manually pushed further on top so it reads as a
            // genuinely higher tier than any regular stage, not just another
            // rank-5 fight. Multiplicative on whatever the rank-5 spawn
            // already set, not a replacement.
            Avatar finaleAvatar = Game.EntityManager.GetEntity<Avatar>(id);
            if (finaleAvatar != null)
            {
                finaleAvatar.Properties[PropertyEnum.HealthMaxMult] =
                    (float)finaleAvatar.Properties[PropertyEnum.HealthMaxMult] * TrialFinaleExtraHealthMult;
                finaleAvatar.Properties[PropertyEnum.DamageMult] =
                    (float)finaleAvatar.Properties[PropertyEnum.DamageMult] * TrialFinaleExtraDamageMult;
                finaleAvatar.ResetResources(false);
            }

            _trialStageAliveIds.Add(id);
            ScheduleTrialHazardTick();

            try { SendBannerLines("☠ THE FINAL TRIAL — YOUR OWN REFLECTION AWAITS"); } catch { }
            TrialLogger.Info($"[TrialOfImpossible] {GetName()}: finale spawned (id={id:X})");
        }

        /// <summary>Fires on every kill in the arena region — advances the gauntlet or, on the finale, ends the run.</summary>
        private void OnTrialStageEntityDead(in EntityDeadGameEvent evt)
        {
            if (evt.Defender == null) return;
            if (_trialStageAliveIds.Remove(evt.Defender.Id) == false) return; // not one of ours

            _trialKillCount++;
            Avatar killWidgetAvatar = CurrentAvatar;
            if (killWidgetAvatar != null) UpdateTrialKillWidget(killWidgetAvatar);

            // Small sustain reward per regular-stage kill — the finale kill
            // skips this since it already gets the real lootsplosion below.
            if (_trialIsFinaleStage == false && _trialArenaRegion != null)
                DropTrialStageOrbs(evt.Defender.RegionLocation.Position, _trialArenaRegion);

            if (_trialStageAliveIds.Count > 0) return; // more still alive this stage

            if (_trialIsFinaleStage)
            {
                CancelTrialHazardTick();
                Avatar avatar = CurrentAvatar;
                if (avatar != null && avatar.IsInWorld)
                    DropTrialFinaleLoot(avatar);
                TrialLogger.Info($"[TrialOfImpossible] {GetName()}: finale defeated — trial complete");
                EndTrialRun(completed: true);
                return;
            }

            Avatar liveAvatar = CurrentAvatar;
            if (liveAvatar == null || liveAvatar.IsInWorld == false) return; // can't advance without a live avatar
            ScheduleAdvanceTrialStage();
        }

        /// <summary>Huge multi-roll drop at the player's position — only the finale kill triggers this.</summary>
        private void DropTrialFinaleLoot(Avatar avatar)
        {
            try
            {
                using LootInputSettings inputSettings = MHServerEmu.Core.Memory.ObjectPoolManager.Instance.Get<LootInputSettings>();
                inputSettings.Initialize(LootContext.Drop, this, avatar);
                for (int i = 0; i < TrialFinaleLootRolls; i++)
                    Game.LootManager.SpawnLootFromTable((PrototypeId)TrialFinaleLootTableRef, inputSettings, 1);
                try { SendBannerLines($"💰 LOOTSPLOSION — {TrialFinaleLootRolls} rolls!"); } catch { }
                TrialLogger.Info($"[TrialOfImpossible] {GetName()}: finale lootsplosion — {TrialFinaleLootRolls} roll(s)");
            }
            catch (Exception ex)
            {
                TrialLogger.Warn($"[TrialOfImpossible] {GetName()}: finale loot drop failed: {ex.Message}");
            }
        }

        /// <summary>Health + Endurance(mana) orb at a defeated phantom's position — a small breather reward for regular-stage kills, distinct from the finale's own lootsplosion.</summary>
        private void DropTrialStageOrbs(Vector3 position, Region region)
        {
            try
            {
                SpawnTrialOrb((PrototypeId)TrialHealOrbRef, position, region);
                SpawnTrialOrb((PrototypeId)TrialEnduranceOrbRef, position, region);
            }
            catch (Exception ex)
            {
                TrialLogger.Warn($"[TrialOfImpossible] {GetName()}: orb drop failed: {ex.Message}");
            }
        }

        private void SpawnTrialOrb(PrototypeId orbRef, Vector3 position, Region region)
        {
            using EntitySettings settings = MHServerEmu.Core.Memory.ObjectPoolManager.Instance.Get<EntitySettings>();
            settings.EntityRef = orbRef;
            settings.Position = position;
            settings.Orientation = new(3.14f, 0f, 0f);
            settings.RegionId = region.Id;
            settings.Lifespan = TimeSpan.FromSeconds(TrialOrbLifespanSec);

            using PropertyCollection properties = MHServerEmu.Core.Memory.ObjectPoolManager.Instance.Get<PropertyCollection>();
            properties[PropertyEnum.AIStartsEnabled] = false;
            properties[PropertyEnum.NoEntityCollide] = true;
            settings.Properties = properties;

            Game.EntityManager.CreateEntity(settings);
        }

        // ---------------- Stage transition delay ----------------

        /// <summary>Beat between clearing a stage and the next one spawning, instead of an instant swap.</summary>
        private void ScheduleAdvanceTrialStage()
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler == null) return;
            if (_trialStageAdvanceTick.IsValid) scheduler.CancelEvent(_trialStageAdvanceTick);
            scheduler.ScheduleEvent(_trialStageAdvanceTick, TimeSpan.FromMilliseconds(TrialStageAdvanceDelayMs), _trialHazardEvents);
            _trialStageAdvanceTick.Get().Initialize(this);
        }

        private void OnTrialStageAdvanceTick()
        {
            if (_trialArenaRegion == null) return; // run already ended/aborted
            Avatar avatar = CurrentAvatar;
            if (avatar == null || avatar.IsInWorld == false) return;
            AdvanceTrialStage(avatar);
        }

        private void CancelTrialStageAdvance()
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler != null && _trialStageAdvanceTick.IsValid) scheduler.CancelEvent(_trialStageAdvanceTick);
        }

        private sealed class TrialStageAdvanceTickEvent : CallMethodEvent<Player>
        {
            protected override CallbackDelegate GetCallback() => static (player) => player.OnTrialStageAdvanceTick();
        }

        // ---------------- Finale hazards ----------------

        /// <summary>Random extra ambush adds during the finale — pressure, not a stage gate (they don't block advancement).</summary>
        private void ScheduleTrialHazardTick()
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler == null || _trialIsFinaleStage == false) return;
            if (_trialHazardTick.IsValid) return;
            int delayMs = Game.Random.Next(TrialHazardMinDelayMs, TrialHazardMaxDelayMs);
            scheduler.ScheduleEvent(_trialHazardTick, TimeSpan.FromMilliseconds(delayMs), _trialHazardEvents);
            _trialHazardTick.Get().Initialize(this);
        }

        private void OnTrialHazardTick()
        {
            try
            {
                if (_trialIsFinaleStage == false) return; // finale ended/cancelled
                Avatar avatar = CurrentAvatar;
                if (avatar != null && avatar.IsInWorld && _trialRoster != null && _trialRoster.Count > 0)
                {
                    PrototypeId hazardHeroRef = _trialRoster[Game.Random.Next(_trialRoster.Count)];
                    ulong id = avatar.SpawnEnemyPhantomHero(hazardHeroRef, 0, out _, ambush: true);
                    if (id != 0)
                    {
                        try { SendBannerLines("⚠ A hazard emerges to aid your reflection!"); } catch { }
                        TrialLogger.Info($"[TrialOfImpossible] {GetName()}: finale hazard spawned ({FriendlyNameFromRef(hazardHeroRef)})");
                    }
                }
            }
            finally
            {
                ScheduleTrialHazardTick();
            }
        }

        private void CancelTrialHazardTick()
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler != null && _trialHazardTick.IsValid) scheduler.CancelEvent(_trialHazardTick);
        }

        private sealed class TrialHazardTickEvent : CallMethodEvent<Player>
        {
            protected override CallbackDelegate GetCallback() => static (player) => player.OnTrialHazardTick();
        }

        // ---------------- Shared helpers ----------------

        /// <summary>"Powers/Player/NickFury/..." style refs aren't hero names — this expects an avatar ref like "Entity/Characters/Avatars/Shipping/Thor.prototype" -> "Thor".</summary>
        private static string FriendlyNameFromRef(PrototypeId heroRef)
        {
            string path = heroRef.GetName();
            if (string.IsNullOrEmpty(path)) return path;
            int slash = path.LastIndexOf('/');
            string leaf = slash >= 0 ? path[(slash + 1)..] : path;
            const string suffix = ".prototype";
            if (leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                leaf = leaf[..^suffix.Length];
            return leaf;
        }

        /// <summary>Drive a native HUD fraction widget with "phantoms defeated / total". Reuses the same cached widget prototype Player.WaveDirector.cs's Endless Challenge wave counter already found (same partial class, same process-wide cache).</summary>
        private void UpdateTrialKillWidget(Avatar avatar)
        {
            try
            {
                PrototypeId widgetRef = GetEndlessWaveWidgetRef();
                if (widgetRef == PrototypeId.Invalid) return;

                var widget = avatar.Region?.UIDataProvider?.GetWidget<UIWidgetGenericFraction>(widgetRef);
                widget?.SetCount(_trialKillCount, _trialTotalEnemies);
            }
            catch (Exception ex)
            {
                TrialLogger.Warn($"[TrialOfImpossible] {GetName()}: kill widget update failed: {ex.Message}");
            }
        }

        private void ClearTrialKillWidget(Avatar avatar)
        {
            try
            {
                PrototypeId widgetRef = GetEndlessWaveWidgetRef();
                if (widgetRef == PrototypeId.Invalid) return;
                avatar?.Region?.UIDataProvider?.DeleteWidget(widgetRef);
            }
            catch { /* best effort */ }
        }

        /// <summary>Called from Avatar.OnKilled for the real player's own death (never a phantom's). No-op outside an active run.</summary>
        internal void OnTrialOwnDeath()
        {
            if (IsTrialGauntletActive == false) return;
            _trialDeathCount++;
            TrialLogger.Info($"[TrialOfImpossible] {GetName()}: own death #{_trialDeathCount}/{TrialMaxDeaths}");
        }

        internal bool IsTrialDeathLimitReached => _trialDeathCount >= TrialMaxDeaths;

        /// <summary>Called from Avatar.DoDeathRelease instead of the normal checkpoint/corpse release once the death limit is hit.</summary>
        internal void EndTrialRunFromDeathLimit(Avatar avatar)
        {
            try { SendBannerLines("💀 Three defeats — the Trial ends. Return to the Trial Guide to try again."); } catch { }
            EndTrialRun(completed: false, failReason: "Defeated");
            avatar.TeleportToRegionFromWeb((ulong)RegionPrototypeId.NPEAvengersTowerHUBRegion);
            TrialLogger.Info($"[TrialOfImpossible] {GetName()}: death limit reached — run ended, returning to Avengers Tower");
        }

        private void EndTrialRun(bool completed, string failReason = null)
        {
            if (_trialArenaRegion == null) return;

            long nowMs = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
            long elapsedMs = nowMs - _trialStartMs;
            CommitTrialToLeaderboard(_trialHeroName, elapsedMs, completed, _trialKillCount, _trialDeathCount, failReason);

            CancelTrialHazardTick();
            CancelTrialStageAdvance();

            Avatar avatar = CurrentAvatar;
            if (avatar != null) ClearTrialKillWidget(avatar);

            if (_trialStageDeadAction != null)
                _trialArenaRegion.EntityDeadEvent.RemoveAction(_trialStageDeadAction);

            _trialArenaRegion = null;
            _trialStageAliveIds.Clear();
            _trialIsFinaleStage = false;
            _trialRoster = null;

            TrialLogger.Info($"[TrialOfImpossible] {GetName()}: run ended (completed={completed}, elapsed={elapsedMs}ms)");
        }

        /// <summary>Called from Player.OnDeallocate so dangling event subscriptions can't outlive the player (logout/disconnect mid-run).</summary>
        internal void UnsubscribeTrialTracking()
        {
            DetachTrialGuideNpc();
            CancelTrialHazardTick();
            CancelTrialStageAdvance();

            if (_trialArenaRegion != null && _trialStageDeadAction != null)
                _trialArenaRegion.EntityDeadEvent.RemoveAction(_trialStageDeadAction);
            _trialArenaRegion = null;
            _trialStageAliveIds.Clear();
        }
    }
}
