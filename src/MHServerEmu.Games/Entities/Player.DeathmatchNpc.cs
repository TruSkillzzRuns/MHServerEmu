using System;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.Dialog;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Properties;
using MHServerEmu.Games.Regions;
using MHServerEmu.Games.UI;
using Gazillion;

namespace MHServerEmu.Games.Entities
{
    // Cloak — the in-game entry point for Deathmatch, standing in Avengers
    // Tower. Click him, pick a bracket, get warped straight into a match.
    //
    // Modelled directly on the Trial of the Impossible guide NPC
    // (Player.TrialOfImpossible.cs): EntityHelper.CreateAgent, AIStartsEnabled
    // off so he stands still, Interactable on so the click round-trips without
    // needing prototype-level interaction data, then a GameDialogManager dialog
    // driven off the region's PlayerInteractEvent.
    //
    // Dialog text comes from AchievementStringMap_95_Deathmatch.json — that file
    // is a general client string-table push (which is why ImportStringStream
    // takes allowOverrides), so it is how this repo adds custom UI text without
    // touching any .sip.
    public partial class Player
    {
        /// <summary>Entity/Characters/NPCs/Cloak.prototype</summary>
        private const ulong DeathmatchNpcHeroRef = 0x02D4831D26620F3A;

        /// <summary>Beside the Trial guide in Avengers Tower, offset so they do not overlap.</summary>
        private static readonly Vector3 s_deathmatchNpcPosition = new(1170.625f, 1256.375f, 376f);
        private static readonly Orientation s_deathmatchNpcOrientation = new(3.14f, 0f, 0f);

        // Custom strings (see AchievementStringMap_95_Deathmatch.json).
        private const ulong DmStrTitle = 9910000000000001;
        private const ulong DmStrBody = 9910000000000002;
        private const ulong DmStrSolos = 9910000000000003;
        private const ulong DmStrDuos = 9910000000000004;
        private const ulong DmStrVictory = 9910000000000010;
        private const ulong DmStrDefeat = 9910000000000011;

        private const ulong DmStrCancel = 9910000000000006;
        private const ulong DmStrOtherBracket = 9910000000000039;
        private const ulong DmStrQuickCheckbox = 9910000000000040;
        private const ulong DmStrTeamsBody = 9910000000000041;

        // Kill targets per bracket: [quick, standard]. Solos ends far sooner than
        // a 5v5 for the same number of kills — one player farming two opponents
        // versus ten combatants feeding a shared pool — so the ladders differ.
        // Standard is what the original flat menu always started.
        private static readonly int[] s_dmSolosKillTargets = { 5, 10 };
        private static readonly int[] s_dmTeamsKillTargets = { 15, 30 };

        // THE DIALOG SHAPE IS FORCED BY THE CLIENT, verified live:
        //   * only TWO buttons render, even though the protocol defines three
        //     (eGDR_Option1..3) and the server sends all three;
        //   * there is NO close affordance — no X, no click-away, no Escape — so a
        //     dialog whose every button is a choice traps the player.
        //
        // Together those mean one button per dialog must be an exit or a step to a
        // dialog that has one, leaving a single real choice per screen. That is
        // exactly the shape the Danger Room terminal already uses
        // (Player.DangerRoomEndlessTerminal.cs:924-930, "More Options..." drilling
        // into a second 2-button dialog).
        //
        // Match length therefore rides on the CHECKBOX rather than a third screen:
        // NetStructDialog carries a checkbox string and the tick comes back as
        // DialogResponse.CheckboxClicked (GameDialogManager.cs:30), so it is a
        // third input the client draws separately from the buttons.
        //
        //   Dialog 1:  [Solos - 1v1v1]  [Other bracket...]   + quick-match tick
        //   Dialog 2:  [Teams - 5v5]    [Not right now]      + quick-match tick
        //
        // Every screen is one click from an exit, and both brackets and both
        // lengths are reachable without a third dialog.

        /// <summary>
        /// How long the VICTORY / DEFEAT banner stays up. Sent as
        /// NetMessageBannerMessage.timeToLiveMS. The shipped banners run 4000-8000;
        /// the field is a uint with no server-side clamp, so this is simply how
        /// long the client is asked to hold it.
        /// </summary>
        private const int DeathmatchVerdictBannerMS = 20000;
        // The protocol defines three (eGDR_Option1..3), but the CLIENT renders
        // only two — reported live, a 3-button dialog showed 2. The Danger Room
        // terminal already works around this by chaining 2-button dialogs
        // (Player.DangerRoomEndlessTerminal.cs:924-930).
        // Historical note: this comment previously claimed three,
        // which is exactly the three brackets. Closing the dialog sends
        // eGDR_Closed and is handled as a cancel.

        // Bracket sizes. Teams are always three — that is an engine limit, not
        // a choice (see Player.DeathmatchTeams.cs).
        public const int DeathmatchBracketSolos = 1;
        public const int DeathmatchBracketDuos = 5;   // 5v5v5 — raised from 2 on request

        /// <summary>
        /// Midtown Patrol, the 5v5 arena — a big endgame map with room for a
        /// 10-combatant fight.
        ///
        /// A LIST, not a constant, because 1.53 renamed these regions. Verified
        /// by resolving both names on a running server of each version
        /// (!dmdata port, then !lookup region Midtown on 1.53):
        ///
        ///   1.48 / 1.52  XManhattanRegion60Cosmic       resolves
        ///   1.53         XManhattanRegion60Cosmic       MISSING
        ///                MidtownPatrolL1to60Region      resolves (0x8646E8B3B7F8DD99)
        ///
        /// First entry that resolves wins, so one binary works on all three.
        /// </summary>
        private static readonly string[] DeathmatchArenaMidtownCandidates =
        {
            "Regions/EndGame/TierX/PatrolMidtown/AltRegions/XManhattanRegion60Cosmic.prototype",   // 1.48 / 1.52
            "Regions/EndGame/TierX/PatrolMidtown/AltRegions/MidtownPatrolL1to60Region.prototype",  // 1.53
            "Regions/EndGame/TierX/PatrolMidtown/MidtownPatrolRegionBand.prototype",               // 1.53 fallback
        };

        /// <summary>The sewer suits the 3-player Solos duel. Same path on all three versions (verified).</summary>
        private const string DeathmatchArenaSewer = "Regions/Story/CH06FortStryker/Areas/ArmyBase/zzzArmyBaseInstances/SCSewer2Region.prototype";

        #region Arena rotation

        // Region pools, taken from the shortlists in
        // Desktop\MHO Files\Midtown Deathmatch\Midtown-Deathmatch-Design.md
        // (REVISION 2 and REVISION 3), with every path re-resolved against live
        // prototype data rather than trusted from the document.
        //
        // Nothing here is assumed to exist: PickDeathmatchArena drops any entry
        // that does not resolve on the running game version, AND any region whose
        // authored PlayerLimit cannot hold the match. PlayerLimit matters because
        // deathmatch phantoms are real synthetic Player entities, not agents — a
        // 5v5 needs ten slots, so a PlayerLimit=5 region genuinely cannot host it.
        // That is exactly why the design doc excluded those five regions from the
        // team bracket while keeping them for duels.

        private static readonly string[] DeathmatchSolosArenaPool =
        {
            "Regions/Story/CH06FortStryker/Areas/ArmyBase/zzzArmyBaseInstances/SCSewer2Region.prototype",      // limit 40, lvl 37
            "Regions/EndGame/DangerRoomMode/zzzDeprecatedAreas/Unused/Sewers/EDSewers1Region.prototype",       // limit 5
            "Regions/EndGame/DangerRoomMode/UniqueScenarios/StaticChallenges/CosmicDoop/DRRegionStaticChallengeCosmicDoopCosmic.prototype", // lvl 63, own start target, no kismet — see PlayerLimit note below

            // MOVED to the Teams pool 2026-08-06 on request:
            //   Regions/EndGame/DangerRoomMode/Marsh/Testing/DRMarshRegionTestingOnly
            // Its PlayerLimit=5 is not an obstacle — see the note on the filter in
            // PickDeathmatchArena.

            // REMOVED 2026-08-06 — the three ZZZUNUSED/OneShotMissions/ZooJungleInstance
            // entries (TREmployeeRegion, TRZooAquariumRegion, TRSeaWorldRegion).
            //
            // They do not load as themselves. Verified live: the picker chose
            // TRZooAquariumRegion and the player landed in
            // Regions/EndGame/OneShotMissions/NonChapterBound/BronxZoo/BronxZooRegionBand
            // — a full story region, not the small enclosed space the design doc
            // shortlisted. Consequences, all from that one log:
            //   * three areas "failed to generate" (ZooJungleArea1ver2 x3)
            //   * 824 native entities to sterilize instead of a handful
            //   * a mission fired MrHydeEntrance, a boss cutscene
            //   * spawn anchors ~30,000 units apart, because the region is enormous
            // The PlayerLimit=5 in the design doc belongs to the ZZZUNUSED stub;
            // what actually loads is something else entirely.
        };

        private static readonly string[] DeathmatchTeamsArenaPool =
        {
            "Regions/EndGame/TierX/PatrolMidtown/AltRegions/XManhattanRegion60Cosmic.prototype",   // Midtown Patrol, lvl 63 (1.48/1.52)
            "Regions/EndGame/TierX/PatrolMidtown/AltRegions/MidtownPatrolL1to60Region.prototype",  // same map, 1.53 name — only one of the two resolves per version
            "Regions/EndGame/Terminals/Green/AsgardInstance/AltRegions/DailyGAsgardINSTRegionL60.prototype", // Asgard, lvl 63
            "Regions/StoryRevamp/CH02JerseyDocks/CH0208CanneryRegion.prototype",                   // industrial interior
            "Regions/StoryRevamp/CH01HellsKitchen/CH0101HellsKitchenRegion.prototype",             // street layout, alleys
            "Regions/ZZZDemoBranch/PAX2013Demo/PAX2013SavageRegion.prototype",                     // open savage land
            "Regions/EndGame/DangerRoomMode/Marsh/Testing/DRMarshRegionTestingOnly.prototype",     // Danger Room marsh
            "Regions/EndGame/DangerRoomMode/AIMFacility/Testing/DRAIMBase1RegionTestingOnly.prototype", // Danger Room AIM base — limit 40, own start target, no kismet
            "Metagame/DefenderPvP/Regions/PvPDefenderRegion.prototype",                            // purpose-built PvP arena — limit 10, own start target, no kismet

            // REMOVED 2026-08-06 on request, played badly for 5v5:
            //   XManhattanRegion1to60          (level-banded twin of XManhattanRegion60Cosmic)
            //   CH0201ShippingYardRegion
            //   SCSewer2Region                 (kept in the Solos pool, too tight for ten)
            // REMOVED 2026-08-08 on request:
            //   CH0205ConstructionRegion
        };

        /// <summary>
        /// Picks a random arena that (a) resolves on this game version and
        /// (b) declares enough player capacity for the match. Falls back to the
        /// bracket's known-good default rather than warping into nothing.
        /// </summary>
        private string PickDeathmatchArena(bool solos, int combatantCount)
        {
            string[] pool = solos ? DeathmatchSolosArenaPool : DeathmatchTeamsArenaPool;

            List<string> usable = new();
            foreach (string path in pool)
            {
                PrototypeId regionRef = GameDatabase.GetPrototypeRefByName(path);
                if (regionRef == PrototypeId.Invalid) continue;

                var regionProto = regionRef.As<RegionPrototype>();
                if (regionProto == null) continue;

                // PlayerLimit is deliberately NOT checked against combatantCount.
                //
                // It constrains REAL CONNECTED CLIENTS only: the sole enforcement is
                // in MHServerEmu.PlayerManagement — RegionHandle.IsFull (line 79)
                // and RegionLoadBalancer (line 127), both counting PlayerHandles.
                // Deathmatch phantoms are synthetic Player entities built inside the
                // Game instance with no PlayerConnection, so they never take a slot.
                // A 5v5 therefore needs ONE real slot, not ten.
                //
                // The design doc's "PlayerLimit=5 regions cannot hold 6 players" is
                // correct for real PvP and does not apply here — filtering on it was
                // my error, and it was silently excluding perfectly good arenas such
                // as the Danger Room marsh.
                if (regionProto.PlayerLimit > 0 && regionProto.PlayerLimit < 1) continue;

                usable.Add(path);
            }

            if (usable.Count == 0)
            {
                string fallback = solos ? DeathmatchArenaSewer : GetDeathmatchArenaMidtown();
                DeathmatchLogger.Warn($"[Deathmatch] no arena in the {(solos ? "Solos" : "Teams")} pool resolves with capacity for {combatantCount} — falling back to {fallback}");
                return fallback;
            }

            // Deliberately NOT Game.Random. That is a GRandom built with the
            // parameterless constructor, which seeds to 0 (GRandom.cs:10-14), and
            // nothing ever reseeds it — so it replays the same sequence from every
            // server start. Reported live: two consecutive Solos matches both
            // landed in the sewer, which is the first entry of the pool.
            //
            // No-repeat: EXCLUDE the previous arena from the candidate list rather
            // than re-rolling. A single re-roll can land on the same entry again —
            // reported live as "getting the same maps back to back" — and with a
            // 3-entry pool that happens roughly one match in nine. Removing it
            // outright makes a back-to-back repeat impossible.
            string lastPicked = solos ? _dmLastSolosArena : _dmLastTeamsArena;

            if (usable.Count > 1 && lastPicked != null)
                usable.Remove(lastPicked);

            string picked = usable[s_dmArenaRng.Next(usable.Count)];

            // Remembered so arena setup can verify we actually arrived where we
            // asked — see the redirect check in StartDeathmatchTeams.
            _deathmatchRequestedArena = ((PrototypeId)GameDatabase.GetPrototypeRefByName(picked)).GetNameFormatted();

            if (solos) _dmLastSolosArena = picked;
            else _dmLastTeamsArena = picked;

            DeathmatchLogger.Info($"[Deathmatch] arena rotation: picked {picked} from {usable.Count}/{pool.Length} usable {(solos ? "Solos" : "Teams")} region(s) for {combatantCount} combatant(s)");
            return picked;
        }

        /// <summary>
        /// Arena picker RNG. Seeded from the wall clock so the pool actually
        /// rotates across server restarts — see the note in PickDeathmatchArena
        /// for why Game.Random cannot be used here.
        /// </summary>
        private static readonly System.Random s_dmArenaRng = new(Environment.TickCount);

        private string _dmLastSolosArena;
        private string _dmLastTeamsArena;

        /// <summary>Arena the picker asked for, checked against what actually loaded.</summary>
        private string _deathmatchRequestedArena;

        #endregion

        private static string s_deathmatchArenaMidtownResolved;

        /// <summary>First Midtown arena path that exists on the running game version.</summary>
        private static string GetDeathmatchArenaMidtown()
        {
            if (s_deathmatchArenaMidtownResolved != null) return s_deathmatchArenaMidtownResolved;

            foreach (string path in DeathmatchArenaMidtownCandidates)
            {
                if (GameDatabase.GetPrototypeRefByName(path) == PrototypeId.Invalid) continue;
                s_deathmatchArenaMidtownResolved = path;
                DeathmatchLogger.Info($"[Deathmatch] Midtown arena resolved to {path}");
                return path;
            }

            // Nothing resolved — fall back to the sewer rather than warping the
            // player into a region that does not exist.
            DeathmatchLogger.Warn("[Deathmatch] NO Midtown arena resolves on this game version — falling back to the sewer");
            s_deathmatchArenaMidtownResolved = DeathmatchArenaSewer;
            return s_deathmatchArenaMidtownResolved;
        }

        private ulong _deathmatchNpcId;
        private Region _deathmatchNpcRegion;
        private Event<PlayerInteractGameEvent>.Action _deathmatchInteractAction;

        /// <summary>Spawns Cloak when the player enters Avengers Tower. Called from the same place the Trial guide is spawned.</summary>
        internal void SpawnDeathmatchNpc(Region region, Avatar avatar)
        {
            if (region == null || avatar == null) return;
            if (region.PrototypeDataRef != (PrototypeId)RegionPrototypeId.NPEAvengersTowerHUBRegion) return;

            // Already alive in this region instance? Leave it and keep the
            // existing interact registration.
            if (_deathmatchNpcRegion == region && _deathmatchNpcId != 0)
            {
                var existing = Game.EntityManager.GetEntity<WorldEntity>(_deathmatchNpcId);
                if (existing != null && existing.IsDestroyed == false && existing.IsInWorld) return;
            }

            AgentPrototype agentProto = ((PrototypeId)DeathmatchNpcHeroRef).As<AgentPrototype>();
            if (agentProto == null)
            {
                DeathmatchLogger.Warn($"[Deathmatch] Cloak ref did not resolve to an AgentPrototype");
                return;
            }

            Agent npc = EntityHelper.CreateAgent(agentProto, avatar, s_deathmatchNpcPosition, s_deathmatchNpcOrientation);
            if (npc == null)
            {
                DeathmatchLogger.Warn($"[Deathmatch] failed to spawn Cloak (CreateAgent returned null)");
                return;
            }

            npc.Properties[PropertyEnum.AIStartsEnabled] = false;   // prop, not a combatant
            npc.Properties[PropertyEnum.Interactable] = true;       // makes the click round-trip succeed

            DetachDeathmatchNpc();
            _deathmatchNpcId = npc.Id;
            _deathmatchNpcRegion = region;
            _deathmatchInteractAction ??= OnDeathmatchNpcInteract;
            region.PlayerInteractEvent.AddActionBack(_deathmatchInteractAction);

            DeathmatchLogger.Info($"[Deathmatch] {GetName()}: Cloak spawned in Avengers Tower (id={npc.Id:X})");
        }

        internal void DetachDeathmatchNpc()
        {
            if (_deathmatchNpcRegion != null && _deathmatchInteractAction != null)
                _deathmatchNpcRegion.PlayerInteractEvent.RemoveAction(_deathmatchInteractAction);

            // Destroy the previous spawn, not just its event hook. Without this
            // every return to the Tower left another Cloak standing there.
            if (_deathmatchNpcId != 0
                && Game.EntityManager.GetEntity<WorldEntity>(_deathmatchNpcId) is WorldEntity old
                && old.IsDestroyed == false)
            {
                try { old.Destroy(); }
                catch (Exception ex) { DeathmatchLogger.Warn($"[Deathmatch] could not destroy old Cloak {_deathmatchNpcId}: {ex.Message}"); }
            }

            _deathmatchNpcRegion = null;
            _deathmatchNpcId = 0;
        }

        private void OnDeathmatchNpcInteract(in PlayerInteractGameEvent evt)
        {
            if (evt.Player != this) return;
            if (evt.InteractableObject == null) return;

            // Match on the PROTOTYPE, not a specific entity id. Verified live:
            // "interact ignored — clicked 432, Cloak is 820" — Avengers Tower
            // holds more than one Cloak (PatchDataCustom.json places a static one
            // in the cell marker set, and this file spawns another), so an id
            // comparison rejects whichever one the player actually clicked.
            if (evt.InteractableObject.PrototypeDataRef != (PrototypeId)DeathmatchNpcHeroRef)
            {
                DeathmatchLogger.Info($"[Deathmatch] interact ignored — clicked {evt.InteractableObject.Id} ({evt.InteractableObject.PrototypeDataRef.GetNameFormatted()}), not Cloak");
                return;
            }

            ShowDeathmatchBracketMenu("Cloak interact");
        }

        /// <summary>
        /// Dialog 1 — Solos, or step to the Teams screen. The quick-match tick
        /// rides along as the checkbox.
        /// </summary>
        private void ShowDeathmatchBracketMenu(string source)
        {
            var dialog = Game.GameDialogManager.CreateInstance(DatabaseUniqueId);
            dialog.Message.LocaleString = (LocaleStringId)DmStrBody;
            dialog.Checkbox.LocaleString = (LocaleStringId)DmStrQuickCheckbox;
            dialog.Options = DialogOptionEnum.MouseCenter;
            dialog.OnResponse = OnDeathmatchDialogResponse;
            dialog.AddButton(GameDialogResultEnum.eGDR_Option1, (LocaleStringId)DmStrSolos, ButtonStyle.Primary, false);
            dialog.AddButton(GameDialogResultEnum.eGDR_Option2, (LocaleStringId)DmStrOtherBracket, ButtonStyle.SecondaryPositive, false);
            Game.GameDialogManager.ShowDialog(dialog);

            DeathmatchLogger.Info($"[Deathmatch] {GetName()}: {source} — Solos screen shown");
        }

        /// <summary>Dialog 2 — Teams, or back out entirely.</summary>
        private void ShowDeathmatchTeamsMenu()
        {
            var dialog = Game.GameDialogManager.CreateInstance(DatabaseUniqueId);
            dialog.Message.LocaleString = (LocaleStringId)DmStrTeamsBody;
            dialog.Checkbox.LocaleString = (LocaleStringId)DmStrQuickCheckbox;
            dialog.Options = DialogOptionEnum.MouseCenter;
            dialog.OnResponse = OnDeathmatchTeamsDialogResponse;
            dialog.AddButton(GameDialogResultEnum.eGDR_Option1, (LocaleStringId)DmStrDuos, ButtonStyle.Primary, false);
            dialog.AddButton(GameDialogResultEnum.eGDR_Option2, (LocaleStringId)DmStrCancel, ButtonStyle.SecondaryNegative, false);
            Game.GameDialogManager.ShowDialog(dialog);

            DeathmatchLogger.Info($"[Deathmatch] {GetName()}: Teams screen shown");
        }

        private void OnDeathmatchDialogResponse(ulong playerGuid, DialogResponse response)
        {
            if (playerGuid != DatabaseUniqueId) return;

            if (response.ButtonIndex == GameDialogResultEnum.eGDR_Option2)
            {
                ShowDeathmatchTeamsMenu();
                return;
            }

            if (response.ButtonIndex != GameDialogResultEnum.eGDR_Option1)
            {
                DeathmatchLogger.Info($"[Deathmatch] {GetName()}: Solos screen dismissed");
                return;
            }

            StartDeathmatchFromMenu(DeathmatchBracketSolos, response.CheckboxClicked);
        }

        private void OnDeathmatchTeamsDialogResponse(ulong playerGuid, DialogResponse response)
        {
            if (playerGuid != DatabaseUniqueId) return;

            if (response.ButtonIndex != GameDialogResultEnum.eGDR_Option1)
            {
                DeathmatchLogger.Info($"[Deathmatch] {GetName()}: Teams screen dismissed");
                return;
            }

            StartDeathmatchFromMenu(DeathmatchBracketDuos, response.CheckboxClicked);
        }

        /// <summary>Starts the match for the chosen bracket and length.</summary>
        private void StartDeathmatchFromMenu(int teamSize, bool quick)
        {
            bool solos = teamSize == DeathmatchBracketSolos;
            int index = quick ? 0 : 1;
            int killTarget = solos ? s_dmSolosKillTargets[index] : s_dmTeamsKillTargets[index];

            // Solos is a three-way (1v1v1); the 5v5 bracket is two teams.
            int teamCount = solos ? 3 : 2;

            // Random arena from the bracket's pool, filtered to regions that both
            // resolve and can hold every combatant.
            string arena = PickDeathmatchArena(solos, teamSize * teamCount);

            string result = StartDeathmatchTeams(killTarget, teamSize, arena, null, teamCount);
            DeathmatchLogger.Info($"[Deathmatch] {GetName()}: {(solos ? "Solos 1v1v1" : "Teams 5v5")}, first to {killTarget} — {result}");
            SendBannerLines(result);
        }
    }
}
