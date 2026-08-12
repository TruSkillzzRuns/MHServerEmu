using System.Text;
using MHServerEmu.Commands.Attributes;
using MHServerEmu.Core.Extensions;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Calligraphy;
using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.Commands.Implementations
{
    /// <summary>
    /// Read-only dump of the shipped TransformMode data.
    ///
    /// Exists to answer one question before any "play as another character" work
    /// starts: which transform modes does the CLIENT actually have, and what do
    /// they turn you into. That matters because the client resolves the
    /// transformed model from its own copy of the prototype — the server never
    /// sends it (see Avatar.GetEntityWorldAsset, which is only used server-side
    /// for animation timing and for stamping asset refs onto conditions and
    /// summons). So the usable set is bounded by shipped data, and editing
    /// UnrealClass server-side would desync rather than help.
    ///
    /// Output goes to the server log (it is far too long for chat).
    /// </summary>
    [CommandGroup("xformdata")]
    [CommandGroupDescription("Dump shipped TransformMode prototype data to the server log (read-only).")]
    [CommandGroupUserLevel(AccountUserLevel.Admin)]
    public class TransformModeDataCommands : CommandGroup
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        [Command("modes")]
        [CommandDescription("Dump every TransformModePrototype: model, powers, entry/exit, duration.")]
        public string DumpModes(string[] @params, NetClient client)
        {
            StringBuilder sb = new();
            sb.AppendLine("=== TransformModePrototype dump ===");

            int count = 0;
            int withModel = 0;

            foreach (PrototypeId protoRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<TransformModePrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                TransformModePrototype proto = protoRef.As<TransformModePrototype>();
                if (proto == null) continue;
                count++;
                if (proto.UnrealClass != AssetId.Invalid) withModel++;

                sb.AppendLine($"--- {GameDatabase.GetPrototypeName(protoRef)} (0x{(ulong)protoRef:X})");
                sb.AppendLine($"    UnrealClass={Asset(proto.UnrealClass)}");
                sb.AppendLine($"    EnterPower={Ref(proto.EnterTransformModePower)}");
                sb.AppendLine($"    ExitPower={Ref(proto.ExitTransformModePower)}");
                sb.AppendLine($"    PowersAreSlottable={proto.PowersAreSlottable} UseRankOfPower={Ref(proto.UseRankOfPower)}");
                sb.AppendLine($"    DurationMSEval={(proto.DurationMSEval == null ? "<none> (permanent until exited)" : "<eval present — timed>")}");

                if (proto.DefaultEquippedAbilities.IsNullOrEmpty())
                {
                    sb.AppendLine("    DefaultEquippedAbilities=<none>");
                }
                else
                {
                    sb.AppendLine($"    DefaultEquippedAbilities[{proto.DefaultEquippedAbilities.Length}]:");
                    foreach (AbilityAssignmentPrototype ability in proto.DefaultEquippedAbilities)
                        sb.AppendLine($"        {Ref(ability.Ability)}");
                }

                if (proto.HiddenPassivePowers.IsNullOrEmpty())
                    sb.AppendLine("    HiddenPassivePowers=<none>");
                else
                    sb.AppendLine($"    HiddenPassivePowers[{proto.HiddenPassivePowers.Length}]: {string.Join(", ", proto.HiddenPassivePowers.Select(p => GameDatabase.GetPrototypeName(p)))}");

                if (proto.UnrealClassOverrides.IsNullOrEmpty())
                {
                    sb.AppendLine("    UnrealClassOverrides=<none>");
                }
                else
                {
                    sb.AppendLine($"    UnrealClassOverrides[{proto.UnrealClassOverrides.Length}] (per-costume model swaps):");
                    foreach (TransformModeUnrealOverridePrototype ovr in proto.UnrealClassOverrides)
                        sb.AppendLine($"        {Asset(ovr.IncomingUnrealClass)} -> {Asset(ovr.TransformedUnrealClass)}");
                }
            }

            sb.AppendLine($"=== {count} transform modes, {withModel} with a non-empty UnrealClass ===");

            Logger.Info(sb.ToString());
            return $"Dumped {count} transform modes ({withModel} with a model) to the server log.";
        }

        [Command("owners")]
        [CommandDescription("Dump which avatars author which transform modes, and the powers allowed in each.")]
        public string DumpOwners(string[] @params, NetClient client)
        {
            StringBuilder sb = new();
            sb.AppendLine("=== Avatars that author TransformModes ===");

            int avatarsTotal = 0;
            int avatarsWithModes = 0;
            int entries = 0;

            foreach (PrototypeId protoRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<AvatarPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                AvatarPrototype proto = protoRef.As<AvatarPrototype>();
                if (proto == null) continue;
                avatarsTotal++;

                if (proto.TransformModes.IsNullOrEmpty()) continue;
                avatarsWithModes++;

                sb.AppendLine($"--- {GameDatabase.GetPrototypeName(protoRef)}");
                foreach (TransformModeEntryPrototype entry in proto.TransformModes)
                {
                    entries++;
                    sb.AppendLine($"    mode={Ref(entry.TransformMode)}");
                    if (entry.AllowedPowers.IsNullOrEmpty())
                        sb.AppendLine("        AllowedPowers=<none>");
                    else
                        sb.AppendLine($"        AllowedPowers[{entry.AllowedPowers.Length}]: {string.Join(", ", entry.AllowedPowers.Select(p => GameDatabase.GetPrototypeName(p)))}");
                }
            }

            sb.AppendLine($"=== {avatarsWithModes} of {avatarsTotal} avatars author transform modes, {entries} entries total ===");

            Logger.Info(sb.ToString());
            return $"Dumped transform mode owners ({avatarsWithModes}/{avatarsTotal} avatars) to the server log.";
        }

        [Command("teamups")]
        [CommandDescription("Dump every team-up's model asset, to compare against available transform mode models.")]
        public string DumpTeamUps(string[] @params, NetClient client)
        {
            StringBuilder sb = new();
            sb.AppendLine("=== AgentTeamUpPrototype models ===");

            int count = 0;
            foreach (PrototypeId protoRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<AgentTeamUpPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                AgentTeamUpPrototype proto = protoRef.As<AgentTeamUpPrototype>();
                if (proto == null) continue;
                count++;

                sb.AppendLine($"--- {GameDatabase.GetPrototypeName(protoRef)} (0x{(ulong)protoRef:X})");
                sb.AppendLine($"    UnrealClass={Asset(proto.UnrealClass)}");
            }

            sb.AppendLine($"=== {count} team-ups ===");

            Logger.Info(sb.ToString());
            return $"Dumped {count} team-up models to the server log.";
        }

        private static string Ref(PrototypeId protoRef)
            => protoRef == PrototypeId.Invalid ? "<none>" : $"{GameDatabase.GetPrototypeName(protoRef)} (0x{(ulong)protoRef:X})";

        private static string Asset(AssetId assetId)
            => assetId == AssetId.Invalid ? "<none>" : $"{GameDatabase.GetAssetName(assetId)} (0x{(ulong)assetId:X})";
    }
}
