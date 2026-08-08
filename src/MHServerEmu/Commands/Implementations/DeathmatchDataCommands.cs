using System.Text;
using MHServerEmu.Commands.Attributes;
using MHServerEmu.Core.Extensions;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Calligraphy;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.GameData.Prototypes.Markers;
using MHServerEmu.Games.Locales;
using MHServerEmu.Games.Loot;
using MHServerEmu.Games.Properties;

namespace MHServerEmu.Commands.Implementations
{
    /// <summary>
    /// Read-only dump of the shipped PvP / metagame / widget data.
    ///
    /// Exists because every deathmatch upgrade that "reuses shipped data" needs
    /// the REAL field values first — which VO assets a PvP prototype actually
    /// carries, which modes actually have timed banners, which entity-icon
    /// widgets actually exist. Guessing those has cost several test runs, so
    /// this prints them instead.
    ///
    /// Output goes to the server log (it is far too long for chat).
    /// </summary>
    [CommandGroup("dmdata")]
    [CommandGroupDescription("Dump shipped PvP/metagame/widget prototype data to the server log (read-only).")]
    [CommandGroupUserLevel(AccountUserLevel.Admin)]
    public class DeathmatchDataCommands : CommandGroup
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        [Command("pvp")]
        [CommandDescription("Dump every PvPPrototype: VO assets, damage curves, schemas.")]
        public string DumpPvP(string[] @params, NetClient client)
        {
            StringBuilder sb = new();
            sb.AppendLine("=== PvPPrototype dump ===");

            int count = 0;
            foreach (PrototypeId protoRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<PvPPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                PvPPrototype proto = protoRef.As<PvPPrototype>();
                if (proto == null) continue;
                count++;

                sb.AppendLine($"--- {GameDatabase.GetPrototypeName(protoRef)} (0x{(ulong)protoRef:X})");
                sb.AppendLine($"    IsPvP={proto.IsPvP} RespawnCooldown={proto.RespawnCooldown} StartingScore={proto.StartingScore} RecordPlayerDeaths={proto.RecordPlayerDeaths}");
                sb.AppendLine($"    ScoreSchemaPlayer={Ref(proto.ScoreSchemaPlayer)}");
                sb.AppendLine($"    ScoreSchemaRegion={Ref(proto.ScoreSchemaRegion)}");
                sb.AppendLine($"    MiniMapFilter={Ref(proto.MiniMapFilter)}");
                sb.AppendLine($"    AvatarKilledLootTable={Ref(proto.AvatarKilledLootTable)}");
                sb.AppendLine($"    Teams={(proto.Teams.IsNullOrEmpty() ? "<none>" : string.Join(", ", proto.Teams.Select(t => GameDatabase.GetPrototypeName(t))))}");
                sb.AppendLine($"    GameModes={(proto.GameModes.IsNullOrEmpty() ? "<none>" : string.Join(", ", proto.GameModes.Select(m => GameDatabase.GetPrototypeName(m))))}");

                sb.AppendLine($"    VOFirstKill={Asset(proto.VOFirstKill)}");
                sb.AppendLine($"    VOKillSpreeShutdown={Asset(proto.VOKillSpreeShutdown)}");
                sb.AppendLine($"    VORevenge={Asset(proto.VORevenge)}");
                sb.AppendLine($"    VOTeammateKilled={Asset(proto.VOTeammateKilled)}");
                sb.AppendLine($"    VOEnemyTeamWiped={Asset(proto.VOEnemyTeamWiped)}");
                if (proto.VOKillSpreeList.IsNullOrEmpty())
                {
                    sb.AppendLine($"    VOKillSpreeList=<empty>");
                }
                else
                {
                    sb.AppendLine($"    VOKillSpreeList[{proto.VOKillSpreeList.Length}]:");
                    for (int i = 0; i < proto.VOKillSpreeList.Length; i++)
                        sb.AppendLine($"        [{i}] {Asset(proto.VOKillSpreeList[i])}");
                }

                sb.AppendLine($"    DamageBoostForKDPct={Curve(proto.DamageBoostForKDPct)} DamageReductionForKDPct={Curve(proto.DamageReductionForKDPct)}");
                sb.AppendLine($"    DamageBoostForWinPct={Curve(proto.DamageBoostForWinPct)} DamageReductionForWinPct={Curve(proto.DamageReductionForWinPct)}");
                sb.AppendLine($"    DamageBoostForNoobs={Curve(proto.DamageBoostForNoobs)} DamageReductionForNoobs={Curve(proto.DamageReductionForNoobs)}");
            }

            sb.AppendLine($"=== {count} PvPPrototype(s) ===");
            Logger.Info(sb.ToString());
            return $"Dumped {count} PvPPrototype(s) to the server log.";
        }

        [Command("pvpdamage")]
        [CommandDescription("Dump the DifficultyGlobals PvP damage knobs and the level curve, so PvP scaling can be tuned from real numbers.")]
        public string DumpPvPDamage(string[] @params, NetClient client)
        {
            StringBuilder sb = new();
            sb.AppendLine("=== PvP damage scaling ===");

            DifficultyGlobalsPrototype dg = GameDatabase.DifficultyGlobalsPrototype;
            if (dg == null)
            {
                Logger.Info("DifficultyGlobalsPrototype is null");
                return "DifficultyGlobalsPrototype is null.";
            }

            sb.AppendLine($"PvPDamageMultiplier     = {dg.PvPDamageMultiplier}");
            sb.AppendLine($"PvPCritDamageMultiplier = {dg.PvPCritDamageMultiplier}");
            sb.AppendLine($"PvPDamageScalarFromLevelCurve = {Curve(dg.PvPDamageScalarFromLevelCurve)}");

            var curve = dg.PvPDamageScalarFromLevelCurve.AsCurve();
            if (curve == null)
            {
                sb.AppendLine("    <curve does not resolve>");
            }
            else
            {
                sb.AppendLine($"    curve range {curve.MinPosition}..{curve.MaxPosition}");
                foreach (int lvl in new[] { 1, 10, 20, 30, 40, 50, 55, 58, 59, 60 })
                {
                    if (lvl < curve.MinPosition || lvl > curve.MaxPosition) continue;
                    float v = curve.GetAt(lvl);
                    sb.AppendLine($"    level {lvl,3} => {v:G6}   (effective vs PvPDamageMultiplier: {v * dg.PvPDamageMultiplier:G6})");
                }
            }

            Logger.Info(sb.ToString());
            return "Dumped PvP damage scaling to the server log.";
        }

        [Command("modes")]
        [CommandDescription("Dump MetaGameMode prototypes that carry timed banners, audio themes or defeat banner text.")]
        public string DumpModes(string[] @params, NetClient client)
        {
            StringBuilder sb = new();
            sb.AppendLine("=== MetaGameModePrototype dump (only modes with banner/audio data) ===");

            int total = 0, shown = 0;
            foreach (PrototypeId protoRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<MetaGameModePrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                MetaGameModePrototype proto = protoRef.As<MetaGameModePrototype>();
                if (proto == null) continue;
                total++;

                bool hasBanners = proto.UITimedBannersOnActivate.IsNullOrEmpty() == false;
                bool hasAudio = proto.PlayerEnterAudioTheme != AssetId.Invalid;
                var defender = proto as PvPDefenderGameModePrototype;
                bool hasDefeatText = defender != null;

                if (hasBanners == false && hasAudio == false && hasDefeatText == false) continue;
                shown++;

                sb.AppendLine($"--- {GameDatabase.GetPrototypeName(protoRef)} (0x{(ulong)protoRef:X}) [{proto.GetType().Name}]");
                sb.AppendLine($"    ShowScoreboard={proto.ShowScoreboard} PlayerEnterAudioTheme={Asset(proto.PlayerEnterAudioTheme)}");

                if (hasBanners)
                {
                    sb.AppendLine($"    UITimedBannersOnActivate[{proto.UITimedBannersOnActivate.Length}]:");
                    foreach (var banner in proto.UITimedBannersOnActivate)
                    {
                        if (banner == null) continue;
                        sb.AppendLine($"        TimerValueMS={banner.TimerValueMS} TimerModeType={banner.TimerModeType} BannerText={Loc(banner.BannerText)}");
                    }
                }

                if (defender != null)
                {
                    sb.AppendLine($"    BannerMsgPlayerDefeatOther={Loc(defender.BannerMsgPlayerDefeatOther)}");
                    sb.AppendLine($"    BannerMsgPlayerDefeatAttacker={Loc(defender.BannerMsgPlayerDefeatAttacker)}");
                    sb.AppendLine($"    BannerMsgPlayerDefeatDefender={Loc(defender.BannerMsgPlayerDefeatDefender)}");
                    sb.AppendLine($"    ChatMessagePlayerDefeatedPlayer={Loc(defender.ChatMessagePlayerDefeatedPlayer)}");
                    sb.AppendLine($"    DeathTimerText={Loc(defender.DeathTimerText)}");
                    sb.AppendLine($"    DefenderInvinciblePower={Ref(defender.DefenderInvinciblePower)}");
                }
            }

            sb.AppendLine($"=== {shown} shown of {total} MetaGameModePrototype(s) ===");
            Logger.Info(sb.ToString());
            return $"Dumped {shown} of {total} MetaGameModePrototype(s) to the server log.";
        }

        [Command("widgets")]
        [CommandDescription("Dump every UIWidgetEntityIcons prototype and its entry types.")]
        public string DumpWidgets(string[] @params, NetClient client)
        {
            StringBuilder sb = new();
            sb.AppendLine("=== UIWidgetEntityIconsPrototype dump ===");

            int count = 0;
            foreach (PrototypeId protoRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<UIWidgetEntityIconsPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                UIWidgetEntityIconsPrototype proto = protoRef.As<UIWidgetEntityIconsPrototype>();
                if (proto == null) continue;
                count++;

                sb.AppendLine($"--- {GameDatabase.GetPrototypeName(protoRef)} (0x{(ulong)protoRef:X}) [{proto.GetType().Name}]");
                if (proto.Entities.IsNullOrEmpty())
                {
                    sb.AppendLine("    Entities=<none>");
                    continue;
                }

                for (int i = 0; i < proto.Entities.Length; i++)
                {
                    var entry = proto.Entities[i];
                    if (entry == null) continue;
                    sb.AppendLine($"    [{i}] {entry.GetType().Name} Count={entry.Count} TreatUnknownAs={entry.TreatUnknownAs} Descriptor={Ref(entry.Descriptor)} Filter={(entry.Filter == null ? "<null>" : entry.Filter.GetType().Name)}");
                }
            }

            sb.AppendLine($"=== {count} UIWidgetEntityIconsPrototype(s) ===");
            Logger.Info(sb.ToString());
            return $"Dumped {count} UIWidgetEntityIconsPrototype(s) to the server log.";
        }

        [Command("audio")]
        [CommandDescription("List every asset in the same asset type as PvP_Welcome — i.e. every usable UI sound theme.")]
        public string DumpAudio(string[] @params, NetClient client)
        {
            // PvP_Welcome, the theme DefenderPvP's Staging mode plays on entry.
            const AssetId Welcome = (AssetId)2980640826322588369;

            StringBuilder sb = new();
            sb.AppendLine("=== UI sound theme dump ===");

            AssetType owningType = null;
            foreach (AssetType type in GameDatabase.DataDirectory.IterateAssetTypes())
            {
                for (int i = 0; i <= type.MaxEnumValue; i++)
                {
                    if (type.GetAssetRefFromEnum(i) != Welcome) continue;
                    owningType = type;
                    break;
                }
                if (owningType != null) break;
            }

            if (owningType == null)
            {
                Logger.Info(sb.AppendLine("PvP_Welcome not found in any asset type").ToString());
                return "PvP_Welcome not found in any asset type.";
            }

            sb.AppendLine($"Asset type: {GameDatabase.GetAssetTypeName(owningType.AssetTypeRef)} (0x{(ulong)owningType.AssetTypeRef:X}) MaxEnumValue={owningType.MaxEnumValue}");

            int count = 0;
            for (int i = 0; i <= owningType.MaxEnumValue; i++)
            {
                AssetId assetId = owningType.GetAssetRefFromEnum(i);
                if (assetId == AssetId.Invalid) continue;
                count++;
                sb.AppendLine($"    [{i,4}] {GameDatabase.GetAssetName(assetId),-52} 0x{(ulong)assetId:X}  ({(ulong)assetId})");
            }

            sb.AppendLine($"=== {count} asset(s) ===");
            Logger.Info(sb.ToString());
            return $"Dumped {count} sound theme asset(s) to the server log.";
        }

        [Command("audiotypes")]
        [CommandDescription("List every asset type that looks audio-related, with member counts and samples.")]
        public string DumpAudioTypes(string[] @params, NetClient client)
        {
            StringBuilder sb = new();
            sb.AppendLine("=== audio-ish asset types ===");

            int shown = 0, total = 0;
            foreach (AssetType type in GameDatabase.DataDirectory.IterateAssetTypes())
            {
                total++;
                string name = GameDatabase.GetAssetTypeName(type.AssetTypeRef) ?? string.Empty;
                if (name.Contains("Audio", StringComparison.OrdinalIgnoreCase) == false
                    && name.Contains("Banter", StringComparison.OrdinalIgnoreCase) == false
                    && name.Contains("VO", StringComparison.Ordinal) == false
                    && name.Contains("Sound", StringComparison.OrdinalIgnoreCase) == false
                    && name.Contains("Speech", StringComparison.OrdinalIgnoreCase) == false)
                    continue;

                // Pass a substring as a parameter to get the FULL member list
                // for the matching type(s) instead of a sample.
                string filter = (@params != null && @params.Length > 0) ? @params[0] : null;
                bool full = filter != null && name.Contains(filter, StringComparison.OrdinalIgnoreCase);

                shown++;
                int members = 0;
                StringBuilder samples = new();
                StringBuilder all = new();
                for (int i = 0; i <= type.MaxEnumValue; i++)
                {
                    AssetId assetId = type.GetAssetRefFromEnum(i);
                    if (assetId == AssetId.Invalid) continue;
                    members++;
                    if (members <= 8) samples.Append($"{GameDatabase.GetAssetName(assetId)}, ");
                    if (full) all.AppendLine($"        {GameDatabase.GetAssetName(assetId),-56} 0x{(ulong)assetId:X}  ({(ulong)assetId})");
                }

                sb.AppendLine($"--- {name} (0x{(ulong)type.AssetTypeRef:X}) members={members}");
                if (full) sb.Append(all); else sb.AppendLine($"    e.g. {samples}");
            }

            sb.AppendLine($"=== {shown} shown of {total} asset type(s) ===");
            Logger.Info(sb.ToString());
            return $"Dumped {shown} of {total} asset type(s) to the server log.";
        }

        [Command("banter")]
        [CommandDescription("Sample the assets actually used by story-banter and VO-trigger prototypes, and name their asset types.")]
        public string DumpBanter(string[] @params, NetClient client)
        {
            StringBuilder sb = new();
            sb.AppendLine("=== story banter / VO trigger asset sample ===");

            // Which asset TYPE does a given asset belong to? Build the reverse
            // map once so the sample below can name it.
            Dictionary<AssetId, string> typeOfAsset = new();
            foreach (AssetType type in GameDatabase.DataDirectory.IterateAssetTypes())
            {
                string typeName = GameDatabase.GetAssetTypeName(type.AssetTypeRef);
                for (int i = 0; i <= type.MaxEnumValue; i++)
                {
                    AssetId assetId = type.GetAssetRefFromEnum(i);
                    if (assetId != AssetId.Invalid) typeOfAsset[assetId] = typeName;
                }
            }

            HashSet<AssetId> banterAssets = new();
            foreach (PrototypeId protoRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<MissionActionPlayBanterPrototype>(PrototypeIterateFlags.None))
            {
                var proto = protoRef.As<MissionActionPlayBanterPrototype>();
                if (proto != null && proto.BanterAsset != AssetId.Invalid) banterAssets.Add(proto.BanterAsset);
            }

            HashSet<AssetId> voAssets = new();
            foreach (PrototypeId protoRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<StoryNotificationPrototype>(PrototypeIterateFlags.None))
            {
                var proto = protoRef.As<StoryNotificationPrototype>();
                if (proto != null && proto.VOTrigger != AssetId.Invalid) voAssets.Add(proto.VOTrigger);
            }

            void Dump(string label, HashSet<AssetId> set)
            {
                sb.AppendLine($"--- {label}: {set.Count} distinct asset(s)");
                int n = 0;
                foreach (AssetId assetId in set)
                {
                    if (++n > 40) { sb.AppendLine($"    ... and {set.Count - 40} more"); break; }
                    string typeName = typeOfAsset.TryGetValue(assetId, out string t) ? t : "<type unknown>";
                    sb.AppendLine($"    {GameDatabase.GetAssetName(assetId),-56} 0x{(ulong)assetId:X}  ({(ulong)assetId})   [{typeName}]");
                }
            }

            Dump("MissionActionPlayBanter.BanterAsset", banterAssets);
            Dump("StoryNotificationPrototype.VOTrigger", voAssets);

            Logger.Info(sb.ToString());
            return $"Dumped {banterAssets.Count} banter and {voAssets.Count} VO-trigger asset(s) to the server log.";
        }

        [Command("port")]
        [CommandDescription("Resolve every ref the deathmatch mode needs BY PATH on the running game version, for building that version's patch data.")]
        public string DumpPort(string[] @params, NetClient client)
        {
            // Prototype ids and asset ids are NOT stable between game versions —
            // they are content hashes. PATHS are stable. So the only safe way to
            // build a per-version PatchDataDeathmatch.json is to resolve every
            // ref by path on that version and read the numbers back. Anything
            // printed as MISSING does not exist on this version and needs a
            // different answer, not a copied v52 number.

            string[] protoPaths =
            {
                "Metagame/PvPTrainingRoom.prototype",
                "Metagame/PvPScoreSchema.prototype",
                "Metagame/FactionPvP/Teams/FactionTeam1Tier1.prototype",
                "Metagame/FactionPvP/Teams/FactionTeam2Tier1.prototype",
                "Metagame/FactionPvP/Teams/FactionTeam3Tier1.prototype",
                "Metagame/PvPMiniMapIcons.defaults",
                "Metagame/PvEWaveBase/PvEWaveShared/Modes/PvEGameFade.prototype",
                "Metagame/DefenderPvP/Modes/MainTier5.prototype",
                "Metagame/DefenderPvP/DefenderPvPTier5.prototype",
                "Entity/Alliances/PVPTeam1RED.prototype",
                "Entity/Alliances/PVPTeam2WHITE.prototype",
                "Entity/Alliances/PVPTeam3BLUE.prototype",
                "Entity/Characters/NPCs/Cloak.prototype",
                "Regions/EndGame/TierX/PatrolMidtown/AltRegions/XManhattanRegion60Cosmic.prototype",
                "Regions/EndGame/TierX/PatrolMidtown/XManhattanRegionBand.prototype",
                "Regions/EndGame/TierX/PatrolMidtown/XManhattanRegion1to60.prototype",
                "Regions/Story/CH06FortStryker/Areas/ArmyBase/zzzArmyBaseInstances/SCSewer2Region.prototype",
                "Loot/Tables/Mob/Bosses/EndgameDailies/Subtables/SharedEndgameDailiesCosmicBUFFED.prototype",
            };

            string[] audioNames =
            {
                "PvP_MatchStart", "PvP_Welcome", "PvP_FirstKill", "PvP_Revenge", "PvP_TeammateDeath",
                "PvP_Shutdown", "PvP_NoVO",
                "PvP_KillSpree2", "PvP_KillSpree3", "PvP_KillSpree4", "PvP_KillSpree5",
                "PvP_KillSpree6", "PvP_KillSpree7", "PvP_KillSpree8",
            };

            StringBuilder sb = new();
            sb.AppendLine("=== deathmatch ref resolution (by path) ===");

            int missing = 0;
            sb.AppendLine("--- prototypes");
            foreach (string path in protoPaths)
            {
                PrototypeId id = GameDatabase.GetPrototypeRefByName(path);
                if (id == PrototypeId.Invalid) { missing++; sb.AppendLine($"    MISSING  {path}"); continue; }
                sb.AppendLine($"    OK  {(ulong)id,-22} 0x{(ulong)id:X16}  {path}");
            }

            sb.AppendLine("--- MetaGameTheme audio assets");
            foreach (string assetName in audioNames)
            {
                AssetId found = AssetId.Invalid;
                foreach (AssetType type in GameDatabase.DataDirectory.IterateAssetTypes())
                {
                    AssetId candidate = type.FindAssetByName(assetName, true);
                    if (candidate != AssetId.Invalid) { found = candidate; break; }
                }
                if (found == AssetId.Invalid) { missing++; sb.AppendLine($"    MISSING  {assetName}"); continue; }
                sb.AppendLine($"    OK  {(ulong)found,-22} 0x{(ulong)found:X16}  {assetName}");
            }

            // Values that only exist as FIELDS on a shipped prototype — read
            // them off the live object rather than guessing a path.
            sb.AppendLine("--- fields read off MainTier5 / PvPTrainingRoom");
            var mainTier5 = GameDatabase.GetPrototypeRefByName("Metagame/DefenderPvP/Modes/MainTier5.prototype").As<PvPDefenderGameModePrototype>();
            if (mainTier5 == null) { missing++; sb.AppendLine("    MISSING  MainTier5 does not resolve as PvPDefenderGameModePrototype"); }
            else
            {
                sb.AppendLine($"    EventHandler                  {(ulong)mainTier5.EventHandler,-22} {Ref(mainTier5.EventHandler)}");
                sb.AppendLine($"    AvatarOnKilledInfoOverride    {(ulong)mainTier5.AvatarOnKilledInfoOverride,-22} {Ref(mainTier5.AvatarOnKilledInfoOverride)}");
                sb.AppendLine($"    BannerMsgPlayerDefeatAttacker {(ulong)mainTier5.BannerMsgPlayerDefeatAttacker,-22} {Loc(mainTier5.BannerMsgPlayerDefeatAttacker)}");
                sb.AppendLine($"    BannerMsgPlayerDefeatDefender {(ulong)mainTier5.BannerMsgPlayerDefeatDefender,-22} {Loc(mainTier5.BannerMsgPlayerDefeatDefender)}");
                sb.AppendLine($"    BannerMsgPlayerDefeatOther    {(ulong)mainTier5.BannerMsgPlayerDefeatOther,-22} {Loc(mainTier5.BannerMsgPlayerDefeatOther)}");
            }

            var trainingRoom = GameDatabase.GetPrototypeRefByName("Metagame/PvPTrainingRoom.prototype").As<PvPPrototype>();
            if (trainingRoom == null) { missing++; sb.AppendLine("    MISSING  PvPTrainingRoom does not resolve as PvPPrototype"); }
            else
            {
                sb.AppendLine("--- PvPTrainingRoom current state");
                sb.AppendLine($"    IsPvP={trainingRoom.IsPvP} ScoreSchemaPlayer={Ref(trainingRoom.ScoreSchemaPlayer)}");
                sb.AppendLine($"    Teams={(trainingRoom.Teams.IsNullOrEmpty() ? "<none>" : string.Join(", ", trainingRoom.Teams.Select(t => GameDatabase.GetPrototypeName(t))))}");
                sb.AppendLine($"    GameModes={(trainingRoom.GameModes.IsNullOrEmpty() ? "<none>" : string.Join(", ", trainingRoom.GameModes.Select(m => GameDatabase.GetPrototypeName(m))))}");
                sb.AppendLine($"    VOKillSpreeList={(trainingRoom.VOKillSpreeList.IsNullOrEmpty() ? "<empty>" : trainingRoom.VOKillSpreeList.Length.ToString())}");
            }

            sb.AppendLine($"=== {missing} MISSING ===");
            Logger.Info(sb.ToString());
            return $"Resolved deathmatch refs; {missing} missing. See server log.";
        }

        [Command("loottables")]
        [CommandDescription("List loot tables whose path matches a substring. Arg = filter, default 'Bosses'.")]
        public string DumpLootTables(string[] @params, NetClient client)
        {
            string filter = (@params != null && @params.Length > 0) ? @params[0] : "Bosses";

            StringBuilder sb = new();
            sb.AppendLine($"=== loot tables matching \"{filter}\" ===");

            int total = 0, shown = 0;
            foreach (PrototypeId protoRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<LootTablePrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                total++;
                string name = GameDatabase.GetPrototypeName(protoRef);
                if (name == null || name.Contains(filter, StringComparison.OrdinalIgnoreCase)) { } else continue;
                shown++;
                sb.AppendLine($"    {(ulong)protoRef,-22} 0x{(ulong)protoRef:X16}  {name}");
            }

            sb.AppendLine($"=== {shown} of {total} loot table(s) ===");
            Logger.Info(sb.ToString());
            return $"Dumped {shown} of {total} loot table(s) matching \"{filter}\". See server log.";
        }

        [Command("regionloot")]
        [CommandDescription("Dump the LootTables assignments of every region whose path matches a substring. Arg = filter, default 'Terminal'.")]
        public string DumpRegionLoot(string[] @params, NetClient client)
        {
            string filter = (@params != null && @params.Length > 0) ? @params[0] : "Terminal";

            StringBuilder sb = new();
            sb.AppendLine($"=== region loot-table assignments matching \"{filter}\" ===");
            sb.AppendLine("A region declares which table each loot SOURCE rolls (RegionPrototype.LootTables,");
            sb.AppendLine("keyed by AssetId Name). This is the authoritative answer to \"what can a boss in");
            sb.AppendLine("this region actually drop\" — no guessing from table names.");

            // Tally which tables show up most across the matched regions, so the
            // shared endgame-boss table is obvious rather than inferred.
            Dictionary<PrototypeId, int> tally = new();
            int total = 0, shown = 0;

            foreach (PrototypeId protoRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<RegionPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                total++;
                string name = GameDatabase.GetPrototypeName(protoRef);
                if (name == null || name.Contains(filter, StringComparison.OrdinalIgnoreCase) == false) continue;

                RegionPrototype proto = protoRef.As<RegionPrototype>();
                if (proto == null || proto.LootTables.IsNullOrEmpty()) continue;
                shown++;

                sb.AppendLine($"--- {name}");
                foreach (LootTableAssignmentPrototype assign in proto.LootTables)
                {
                    if (assign == null) continue;
                    sb.AppendLine($"    source={Asset(assign.Name),-28} event={assign.Event,-24} table={Ref(assign.Table)}");
                    if (assign.Table != PrototypeId.Invalid)
                        tally[assign.Table] = tally.GetValueOrDefault(assign.Table) + 1;
                }
            }

            sb.AppendLine($"=== {shown} of {total} region(s) had loot assignments ===");
            sb.AppendLine("--- tables by how many matched regions use them (most-shared first) ---");
            foreach (var kvp in tally.OrderByDescending(k => k.Value))
                sb.AppendLine($"    {kvp.Value,4}x  0x{(ulong)kvp.Key:X16}  {GameDatabase.GetPrototypeName(kvp.Key)}");

            Logger.Info(sb.ToString());
            return $"Dumped loot assignments for {shown} region(s) matching \"{filter}\". See server log.";
        }

        [Command("dmgloghere")]
        [CommandDescription("Toggle per-hit PvP damage logging inside deathmatch. Arg = on|off.")]
        public string ToggleDamageLog(string[] @params, NetClient client)
        {
            bool on = @params != null && @params.Length > 0
                   && (@params[0].Equals("on", StringComparison.OrdinalIgnoreCase) || @params[0] == "1");

            MHServerEmu.Games.Powers.PowerPayload.DeathmatchDamageLogging = on;
            Logger.Info($"[TDM:DMG] per-hit PvP damage logging {(on ? "ENABLED" : "disabled")}");
            return $"Deathmatch damage logging {(on ? "ENABLED" : "disabled")}.";
        }

                        [Command("testheroes")]
        [CommandDescription("TEMPORARY: force every deathmatch phantom to spawn as Nick Fury or Magik, for reproducing their immunity bugs. Arg = on|off.")]
        public string ToggleTestHeroes(string[] @params, NetClient client)
        {
            bool on = @params != null && @params.Length > 0
                   && (@params[0].Equals("on", StringComparison.OrdinalIgnoreCase) || @params[0] == "1");

            MHServerEmu.Games.Entities.Player.DeathmatchTestHeroLock = on;
            Logger.Info($"[TDM] test hero lock {(on ? "ENABLED — phantoms will be NickFury / Magik only" : "disabled — normal random roster")}");
            return $"Deathmatch test hero lock {(on ? "ENABLED (NickFury / Magik only)" : "disabled")}.";
        }

        [Command("doors")]
        [CommandDescription("List every entity in YOUR CURRENT region that carries an EntityState — doors, gates, switchable props — with state details.")]
        public string DumpDoors(string[] @params, NetClient client)
        {
            if (client is not MHServerEmu.Games.Network.PlayerConnection conn || conn.Player?.CurrentAvatar?.Region == null)
                return "Needs a player in a region — run with a playerName.";

            var region = conn.Player.CurrentAvatar.Region;

            StringBuilder sb = new();
            sb.AppendLine($"=== stateful entities in {region.PrototypeName} ===");
            sb.AppendLine("Answers whether a region's gates are ENTITY-STATE doors (openable server-side");
            sb.AppendLine("via SetState, the same call MissionActionEntitySetState uses) or baked cell");
            sb.AppendLine("geometry (nothing server-side can open). Doors show as DoorEntityStatePrototype");
            sb.AppendLine("with IsOpen.");

            // "near" mode: EVERY entity within 2000u of the avatar, closest
            // first, with the flags that matter for a blocker — because a gate
            // without an EntityState set is invisible to the default filter
            // (verified live in BroodShip: a blocking membrane, yet only the two
            // Transitions carried a state).
            bool nearMode = @params != null && @params.Length > 0 && @params[0].Equals("near", StringComparison.OrdinalIgnoreCase);

            int total = 0, stateful = 0;
            var avatarPos = conn.Player.CurrentAvatar.RegionLocation.Position;
            var aabb = region.Aabb;
            var sphere = nearMode
                ? new MHServerEmu.Core.Collisions.Sphere(avatarPos, 2000f)
                : new MHServerEmu.Core.Collisions.Sphere(aabb.Center, MathF.Max(aabb.Width, aabb.Length));
            var ctx = new MHServerEmu.Games.Entities.EntityRegionSPContext(MHServerEmu.Games.Entities.EntityRegionSPContextFlags.AllPartitions);

            var rows = new List<(float dist, string text)>();
            foreach (MHServerEmu.Games.Entities.WorldEntity we in region.IterateEntitiesInVolume(sphere, ctx))
            {
                if (we == null) continue;
                total++;

                PrototypeId stateRef = we.Properties[PropertyEnum.EntityState];
                if (stateRef != PrototypeId.Invalid) stateful++;

                if (nearMode)
                {
                    float dist = MHServerEmu.Core.VectorMath.Vector3.Distance2D(avatarPos, we.RegionLocation.Position);
                    string state = stateRef != PrototypeId.Invalid ? $" state={GameDatabase.GetPrototypeName(stateRef).Split('/')[^1]}" : "";
                    rows.Add((dist, $"{dist,6:F0}u  {we.GetType().Name,-14} id={we.Id,-5} {we.PrototypeName}{state}" +
                                    $"  [dormant={we.IsDormant} untargetable={we.IsUntargetable} interactableProp={(int)we.Properties[PropertyEnum.Interactable]} collides={we.Bounds.CollisionType}]"));
                    continue;
                }

                if (stateRef == PrototypeId.Invalid) continue;
                var stateProto = GameDatabase.GetPrototype<EntityStatePrototype>(stateRef);
                string door = stateProto is DoorEntityStatePrototype d ? $" DOOR IsOpen={d.IsOpen}" : "";
                sb.AppendLine($"--- {we.PrototypeName}  (id={we.Id}, {we.GetType().Name})");
                sb.AppendLine($"    state={GameDatabase.GetPrototypeName(stateRef)}{door}  pos={we.RegionLocation.Position.ToStringNames()}");
            }

            if (nearMode)
            {
                rows.Sort((a, b) => a.dist.CompareTo(b.dist));
                foreach (var r in rows) sb.AppendLine(r.text);
            }

            sb.AppendLine($"=== {stateful} stateful of {total} entit(ies){(nearMode ? " (near mode, 2000u)" : "")} ===");
            Logger.Info(sb.ToString());
            return $"{stateful} stateful entit(ies) of {total} in {region.PrototypeName}. See server log.";
        }

        [Command("regionkismet")]
        [CommandDescription("Report which cutscene (kismet) sources a region can fire. Arg = region name substring.")]
        public string DumpRegionKismet(string[] @params, NetClient client)
        {
            string filter = (@params != null && @params.Length > 0) ? @params[0] : "Region";

            StringBuilder sb = new();
            sb.AppendLine($"=== kismet sources for regions matching \"{filter}\" ===");
            sb.AppendLine("Five prototype fields reference a kismet, but only TWO ever reach the client:");
            sb.AppendLine("  RegionConnectionTarget.IntroKismetSeq / Hotspot.KismetSeq / MissionActionPlayKismetSeq");
            sb.AppendLine("      -> Player.PlayKismetSeq");
            sb.AppendLine("  MetaGameMode.KismetSequenceOnActivate");
            sb.AppendLine("      -> MetaGameMode.SendPlayKismetSeq");
            sb.AppendLine("KismetSequenceEntityPrototype is never played server-side (InteractionManager.cs:528).");
            sb.AppendLine("Both paths are gated for deathmatch; this lists what WOULD have fired.");
            sb.AppendLine();

            int shown = 0, withKismet = 0;
            foreach (PrototypeId protoRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<RegionPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                string name = GameDatabase.GetPrototypeName(protoRef);
                if (name == null || name.Contains(filter, StringComparison.OrdinalIgnoreCase) == false) continue;

                RegionPrototype proto = protoRef.As<RegionPrototype>();
                if (proto == null) continue;
                shown++;

                List<string> sources = new();

                // 1. Region start target intro cutscene.
                var startTarget = proto.StartTarget.As<RegionConnectionTargetPrototype>();
                if (startTarget != null && startTarget.IntroKismetSeq != PrototypeId.Invalid)
                    sources.Add($"IntroKismetSeq={GameDatabase.GetPrototypeName(startTarget.IntroKismetSeq)}");

                // 2. Any metagame mode that plays one on activate.
                if (proto.MetaGames != null)
                {
                    foreach (PrototypeId metaRef in proto.MetaGames)
                    {
                        var metaProto = metaRef.As<MetaGamePrototype>();
                        if (metaProto?.GameModes == null) continue;

                        foreach (PrototypeId modeRef in metaProto.GameModes)
                        {
                            // KismetSequenceOnActivate lives on MetaGameModeIdlePrototype,
                            // not the base MetaGameModePrototype — the Idle mode is the
                            // only one that plays a cutscene when it activates, which
                            // matches MetaGameModeIdle.cs:53 being the sole caller.
                            var modeProto = modeRef.As<MetaGameModeIdlePrototype>();
                            if (modeProto == null || modeProto.KismetSequenceOnActivate == PrototypeId.Invalid) continue;
                            sources.Add($"{GameDatabase.GetPrototypeName(modeRef).Split('/')[^1]}.KismetSequenceOnActivate={GameDatabase.GetPrototypeName(modeProto.KismetSequenceOnActivate).Split('/')[^1]}");
                        }
                    }
                }

                sb.AppendLine($"--- {name}");
                sb.AppendLine($"    PlayerLimit={proto.PlayerLimit} Level={proto.Level} StartTarget={Ref(proto.StartTarget)}");

                // Attached metagames matter as much as cutscenes for an arena: a
                // region carrying its own PvP metagame runs its own game modes,
                // spawns defenders/turrets, and can apply PvPDefenderGameMode's
                // player lock (SystemImmobilized + Invulnerable + PowerLock,
                // PvPDefenderGameMode.cs:549) — which would freeze a deathmatch.
                sb.AppendLine($"    MetaGames={(proto.MetaGames.IsNullOrEmpty() ? "<none>" : string.Join(", ", proto.MetaGames.Select(m => GameDatabase.GetPrototypeName(m).Split('/')[^1])))}");

                if (sources.Count == 0) { sb.AppendLine("    kismet: <none>"); continue; }
                withKismet++;
                foreach (string s in sources) sb.AppendLine($"    {s}");
            }

            sb.AppendLine($"=== {withKismet} of {shown} matched region(s) can fire a cutscene ===");
            Logger.Info(sb.ToString());
            return $"{withKismet} of {shown} region(s) matching \"{filter}\" can fire a cutscene. See server log.";
        }

        [Command("heroes")]
        [CommandDescription("List AvatarPrototypes (the phantom spawn pool) whose path matches a substring. Arg = filter.")]
        public string DumpHeroes(string[] @params, NetClient client)
        {
            string filter = (@params != null && @params.Length > 0) ? @params[0] : null;

            StringBuilder sb = new();
            sb.AppendLine($"=== AvatarPrototypes matching \"{filter ?? "<all>"}\" ===");

            int total = 0, shown = 0;
            foreach (PrototypeId protoRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<AvatarPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                total++;
                string name = GameDatabase.GetPrototypeName(protoRef);
                if (name == null) continue;
                if (filter != null && name.Contains(filter, StringComparison.OrdinalIgnoreCase) == false) continue;
                shown++;
                sb.AppendLine($"    0x{(ulong)protoRef:X16}  {name}");
            }

            sb.AppendLine($"=== {shown} of {total} avatar(s) ===");
            Logger.Info(sb.ToString());
            return $"Dumped {shown} of {total} avatar(s). See server log.";
        }

        [Command("alliances")]
        [CommandDescription("Dump AlliancePrototypes with their HostileTo/FriendlyTo lists, and every MetaGameTeam. Arg = optional name filter.")]
        public string DumpAlliances(string[] @params, NetClient client)
        {
            string filter = (@params != null && @params.Length > 0) ? @params[0] : null;

            StringBuilder sb = new();
            sb.AppendLine("=== AlliancePrototype dump ===");
            sb.AppendLine("Deathmatch needs N alliances that are MUTUALLY hostile. No alliance is");
            sb.AppendLine("hostile to itself, so the number of usable free-for-all sides equals the");
            sb.AppendLine("size of the largest mutually-hostile set in the CLIENT's data.");

            List<AlliancePrototype> all = new();
            foreach (PrototypeId protoRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<AlliancePrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                AlliancePrototype proto = protoRef.As<AlliancePrototype>();
                if (proto == null) continue;
                all.Add(proto);

                string name = GameDatabase.GetPrototypeName(protoRef);
                if (filter != null && (name == null || name.Contains(filter, StringComparison.OrdinalIgnoreCase) == false)) continue;

                sb.AppendLine($"--- {name} (0x{(ulong)protoRef:X16})");
                bool selfHostile = proto.HostileTo != null && proto.HostileTo.Contains(protoRef);
                sb.AppendLine($"    SELF-HOSTILE = {selfHostile}   WhileConfused = {(proto.WhileConfused != null ? GameDatabase.GetPrototypeName(proto.WhileConfused.DataRef) : "<none>")}   WhileControlled = {(proto.WhileControlled != null ? GameDatabase.GetPrototypeName(proto.WhileControlled.DataRef) : "<none>")}");
                sb.AppendLine($"    HostileTo  = {(proto.HostileTo.IsNullOrEmpty() ? "<none>" : string.Join(", ", proto.HostileTo.Select(r => GameDatabase.GetPrototypeName(r))))}");
                sb.AppendLine($"    FriendlyTo = {(proto.FriendlyTo.IsNullOrEmpty() ? "<none>" : string.Join(", ", proto.FriendlyTo.Select(r => GameDatabase.GetPrototypeName(r))))}");
            }

            sb.AppendLine($"=== {all.Count} alliance(s) total ===");

            // Which alliances are mutually hostile with EVERY other member of the
            // set? Brute force over the resolved table, not the raw arrays, because
            // IsHostileTo is what the game actually asks.
            sb.AppendLine("--- mutually-hostile groups (greedy, largest first) ---");
            List<AlliancePrototype> pvpish = all.Where(a =>
            {
                string n = GameDatabase.GetPrototypeName(a.DataRef);
                return n != null && (n.Contains("PVP", StringComparison.OrdinalIgnoreCase) || n.Contains("PvP", StringComparison.Ordinal));
            }).ToList();

            sb.AppendLine($"PvP-named alliances: {pvpish.Count}");
            foreach (var a in pvpish)
            {
                List<string> hostile = new();
                foreach (var b in pvpish)
                {
                    if (a == b) continue;
                    if (a.IsHostileTo(b) && b.IsHostileTo(a)) hostile.Add(GameDatabase.GetPrototypeName(b.DataRef).Split('/')[^1]);
                }
                sb.AppendLine($"    {GameDatabase.GetPrototypeName(a.DataRef).Split('/')[^1],-40} mutually hostile with {hostile.Count}: {string.Join(", ", hostile)}");
            }

            sb.AppendLine("--- MetaGameTeamPrototypes ---");
            int teams = 0;
            foreach (PrototypeId protoRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<MetaGameTeamPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                MetaGameTeamPrototype proto = protoRef.As<MetaGameTeamPrototype>();
                if (proto == null) continue;
                teams++;
                string alliance = proto is PvPTeamPrototype pvpTeam && pvpTeam.Alliance != null
                    ? GameDatabase.GetPrototypeName(pvpTeam.Alliance.DataRef)
                    : "<not a PvPTeam>";
                sb.AppendLine($"    {GameDatabase.GetPrototypeName(protoRef)}  MaxPlayers={proto.MaxPlayers} Alliance={alliance}");
            }
            sb.AppendLine($"=== {teams} MetaGameTeam(s) ===");

            Logger.Info(sb.ToString());
            return $"Dumped {all.Count} alliance(s) and {teams} team(s). See server log.";
        }

        [Command("bossloot")]
        [CommandDescription("Dump the LootTablePrototype properties declared on entity prototypes matching a substring. Arg = filter, e.g. a boss name or 'Terminals'.")]
        public string DumpBossLoot(string[] @params, NetClient client)
        {
            if (@params == null || @params.Length == 0)
                return "Usage: !dmdata bossloot <entity name substring>";

            string filter = @params[0];

            StringBuilder sb = new();
            sb.AppendLine($"=== entity loot tables matching \"{filter}\" ===");
            sb.AppendLine("A mob's own LootTablePrototype property IS its drop table. The region");
            sb.AppendLine("LootTables map is only an OVERRIDE, applied when the entity carries a");
            sb.AppendLine("LootTableSource asset (WorldEntity.cs:4255-4258) — chests, mostly. So this");
            sb.AppendLine("is where a boss's real drops live.");

            Dictionary<PrototypeId, int> tally = new();
            int shown = 0;

            foreach (PrototypeId protoRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<WorldEntityPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                string name = GameDatabase.GetPrototypeName(protoRef);
                if (name == null || name.Contains(filter, StringComparison.OrdinalIgnoreCase) == false) continue;

                WorldEntityPrototype proto = protoRef.As<WorldEntityPrototype>();
                if (proto?.Properties == null) continue;

                List<string> lines = new();
                foreach (var kvp in proto.Properties.IteratePropertyRange(PropertyEnum.LootTablePrototype))
                {
                    PrototypeId tableRef = kvp.Value;
                    if (tableRef == PrototypeId.Invalid) continue;

                    Property.FromParam(kvp.Key, 0, out int lootEventValue);
                    lines.Add($"    event={(LootDropEventType)lootEventValue,-22} table={Ref(tableRef)}");
                    tally[tableRef] = tally.GetValueOrDefault(tableRef) + 1;
                }

                if (lines.Count == 0) continue;
                shown++;

                sb.AppendLine($"--- {name}  [rank={(proto.Rank != null ? GameDatabase.GetPrototypeName(proto.Rank.DataRef) : "<none>")}]");
                foreach (string line in lines) sb.AppendLine(line);
            }

            sb.AppendLine($"=== {shown} entity prototype(s) with loot tables ===");
            sb.AppendLine("--- tables by how many matched entities use them ---");
            foreach (var kvp in tally.OrderByDescending(k => k.Value).Take(40))
                sb.AppendLine($"    {kvp.Value,4}x  0x{(ulong)kvp.Key:X16}  {GameDatabase.GetPrototypeName(kvp.Key)}");

            Logger.Info(sb.ToString());
            return $"Dumped loot tables for {shown} entity prototype(s) matching \"{filter}\". See server log.";
        }

        [Command("tabletree")]
        [CommandDescription("Walk a loot table and report whether it can actually drop artifacts. Arg = table name substring or 0xHEX id.")]
        public string DumpTableTree(string[] @params, NetClient client)
        {
            if (@params == null || @params.Length == 0)
                return "Usage: !dmdata tabletree <name substring | 0xHEX>";

            string arg = @params[0];
            List<PrototypeId> targets = new();

            if (arg.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && ulong.TryParse(arg.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out ulong raw))
            {
                targets.Add((PrototypeId)raw);
            }
            else
            {
                foreach (PrototypeId protoRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<LootTablePrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
                {
                    string name = GameDatabase.GetPrototypeName(protoRef);
                    if (name != null && name.Contains(arg, StringComparison.OrdinalIgnoreCase))
                        targets.Add(protoRef);
                }
            }

            if (targets.Count == 0) return $"No loot table matched \"{arg}\".";

            StringBuilder sb = new();
            sb.AppendLine($"=== loot table tree for \"{arg}\" ({targets.Count} match(es)) ===");

            foreach (PrototypeId target in targets.Take(12))
            {
                LootTablePrototype proto = target.As<LootTablePrototype>();
                if (proto == null)
                {
                    sb.AppendLine($"--- 0x{(ulong)target:X16} does not resolve as a LootTablePrototype");
                    continue;
                }

                // NumMin < 1 makes a table a no-op: LootTablePrototype.Roll returns
                // NoRoll immediately (LootTablePrototype.cs:289). A table meant to be
                // pulled in as a sub-node of a parent often has NumMin=0 and only
                // works when the parent supplies the count — rolling it directly
                // then yields nothing at all.
                sb.AppendLine($"--- {GameDatabase.GetPrototypeName(target)} (0x{(ulong)target:X16})");
                sb.AppendLine($"    NumMin={proto.NumMin} NumMax={proto.NumMax} NoDropPercent={proto.NoDropPercent} PickMethod={proto.PickMethod}" +
                              $"{(proto.NumMin < 1 ? "   <== NumMin<1: ROLLS NOTHING ON ITS OWN" : "")}");
                HashSet<LootTablePrototype> seen = new();
                bool artifacts = false;
                WalkLootTable(proto, sb, seen, 1, ref artifacts);
                sb.AppendLine($"    >>> CAN DROP ARTIFACTS: {artifacts}");
            }

            Logger.Info(sb.ToString());
            return $"Walked {Math.Min(targets.Count, 12)} loot table(s). See server log.";
        }

        /// <summary>
        /// Recursively prints a loot table's Choices. Artifacts surface two ways:
        /// an explicit LootDropItemPrototype whose Item is an ArtifactPrototype,
        /// or — far more common — a LootDropItemFilterPrototype whose UISlot is
        /// one of Artifact01..Artifact04, which rolls whatever artifact fits.
        /// </summary>
        private static void WalkLootTable(LootTablePrototype table, StringBuilder sb,
            HashSet<LootTablePrototype> seen, int depth, ref bool artifacts)
        {
            if (table == null || depth > 8) return;
            if (seen.Add(table) == false)
            {
                sb.AppendLine($"{new string(' ', depth * 4)}(already expanded) {GameDatabase.GetPrototypeName(table.DataRef)}");
                return;
            }

            if (table.Choices.IsNullOrEmpty()) return;
            string pad = new(' ', depth * 4);

            foreach (LootNodePrototype node in table.Choices)
            {
                if (node == null) continue;

                switch (node)
                {
                    case LootTablePrototype nested:
                        sb.AppendLine($"{pad}[table w={nested.Weight}] {GameDatabase.GetPrototypeName(nested.DataRef)}");
                        WalkLootTable(nested, sb, seen, depth + 1, ref artifacts);
                        break;

                    case LootDropItemFilterPrototype filter:
                        bool isArtifactSlot = filter.UISlot == EquipmentInvUISlot.Artifact01
                                           || filter.UISlot == EquipmentInvUISlot.Artifact02
                                           || filter.UISlot == EquipmentInvUISlot.Artifact03
                                           || filter.UISlot == EquipmentInvUISlot.Artifact04;
                        if (isArtifactSlot) artifacts = true;
                        sb.AppendLine($"{pad}[filter w={filter.Weight}] slot={filter.UISlot} rank={filter.ItemRank} num={filter.NumMin}-{filter.NumMax}{(isArtifactSlot ? "   <== ARTIFACT" : "")}");
                        break;

                    case LootDropItemPrototype item:
                        bool isArtifactItem = item.Item is ArtifactPrototype;
                        if (isArtifactItem) artifacts = true;
                        sb.AppendLine($"{pad}[item w={item.Weight}] {(item.Item != null ? GameDatabase.GetPrototypeName(item.Item.DataRef) : "<null>")}{(isArtifactItem ? "   <== ARTIFACT" : "")}");
                        break;

                    default:
                        sb.AppendLine($"{pad}[{node.GetType().Name} w={node.Weight}]");
                        break;
                }
            }
        }

        [Command("curves")]
        [CommandDescription("Dump the actual sampled values of the PvP damage-scaling curves.")]
        public string DumpCurves(string[] @params, NetClient client)
        {
            // PvP.cs:117-119 carries a comment claiming these tables are flat
            // no-ops. That is a comment, not evidence — print the real numbers.
            (string Name, CurveId Id)[] curves =
            {
                ("DamageBoostForKDPct",      (CurveId)0xBB961D26909A16D5),
                ("DamageReductionForKDPct",  (CurveId)0xB319F425F003187B),
                ("DamageBoostForWinPct",     (CurveId)0x0C6F29C7A8D81754),
                ("DamageReductionForWinPct", (CurveId)0x956EF79409F618FA),
                ("DamageBoostForNoobs",      (CurveId)0x4855F923911116E0),
                ("DamageReductionForNoobs",  (CurveId)0x40DA1020F07A1886),
            };

            StringBuilder sb = new();
            sb.AppendLine("=== PvP damage curve dump ===");

            foreach ((string name, CurveId curveId) in curves)
            {
                var curve = GameDatabase.GetCurve(curveId);
                if (curve == null)
                {
                    sb.AppendLine($"--- {name}: <curve not found>");
                    continue;
                }

                sb.AppendLine($"--- {name} ({GameDatabase.GetCurveName(curveId)}) Min={curve.MinPosition} Max={curve.MaxPosition} IsCurveZero={curve.IsCurveZero}");

                // Sample rather than print hundreds of points.
                StringBuilder samples = new();
                int span = curve.MaxPosition - curve.MinPosition;
                int step = Math.Max(1, span / 10);
                for (int pos = curve.MinPosition; pos <= curve.MaxPosition; pos += step)
                    samples.Append($"[{pos}]={curve.GetAt(pos):0.####} ");
                sb.AppendLine($"    {samples}");
            }

            Logger.Info(sb.ToString());
            return "Dumped PvP damage curves to the server log.";
        }

        private static string Ref(PrototypeId protoRef)
            => protoRef == PrototypeId.Invalid ? "<none>" : $"{GameDatabase.GetPrototypeName(protoRef)} (0x{(ulong)protoRef:X})";

        private static string Asset(AssetId assetId)
            => assetId == AssetId.Invalid ? "<none>" : $"{GameDatabase.GetAssetName(assetId)} (0x{(ulong)assetId:X})";

        private static string Curve(CurveId curveId)
            => curveId == CurveId.Invalid ? "<none>" : $"{GameDatabase.GetCurveName(curveId)} (0x{(ulong)curveId:X})";

        private static string Loc(LocaleStringId stringId)
        {
            if (stringId == LocaleStringId.Blank) return "<blank>";
            string text = LocaleManager.Instance.CurrentLocale?.GetLocaleString(stringId);
            return $"\"{text}\" (0x{(ulong)stringId:X})";
        }
    }
}

