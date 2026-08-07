using MHServerEmu.Commands.Attributes;
using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.GameData.Tables;
using MHServerEmu.Games.Network;
using MHServerEmu.Games.Properties;

namespace MHServerEmu.Commands.Implementations
{
    // TEMPORARY DIAGNOSTIC — Midtown Deathmatch investigation.
    //
    // The question this answers: can more than 3 mutually hostile parties exist?
    //
    // Hostility comes from AllianceTable, a matrix built once at startup from
    // prototype data. Every alliance is automatically friendly to ITSELF, and
    // stock data contains only three mutually hostile PvP alliances
    // (PVPTeam1RED / PVPTeam2WHITE / PVPTeam3BLUE) — verified live, none of them
    // hostile to itself. So an 8-player free-for-all cannot be expressed through
    // alliance data alone.
    //
    // The only remaining route is for the SERVER to declare hostility the client
    // does not know about. The client builds the same table from the same data,
    // so the two would disagree. Whether that works depends entirely on whether
    // the client independently blocks attacks on entities its own data calls
    // friendly, or defers to the server.
    //
    // These commands set up exactly that disagreement so it can be observed
    // in-game. Delete this file once the answer is recorded.
    [CommandGroup("pvptest")]
    [CommandGroupDescription("TEMPORARY diagnostics for the Midtown Deathmatch alliance investigation.")]
    [CommandGroupUserLevel(AccountUserLevel.Admin)]
    public class PvPTestCommands : CommandGroup
    {
        private const string RedPath = "Entity/Alliances/PVPTeam1RED.prototype";
        private const string WhitePath = "Entity/Alliances/PVPTeam2WHITE.prototype";

        private static AlliancePrototype GetAlliance(string path)
            => GameDatabase.GetPrototypeRefByName(path).As<AlliancePrototype>();

        /// <summary>
        /// CONTROL CASE. Player on RED, phantom on WHITE — mutually hostile in
        /// the real data, so client and server agree. If this does NOT work,
        /// nothing about PvP-in-Midtown works and the deathmatch design is wrong
        /// for a reason that has nothing to do with free-for-all.
        /// </summary>
        [Command("control")]
        [CommandDescription("Control case: you=RED, phantom=WHITE (genuinely hostile in data). You SHOULD be able to attack it.")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string Control(string[] @params, NetClient client)
        {
            var pc = client as PlayerConnection;
            var avatar = pc?.Player?.CurrentAvatar;
            if (avatar == null) return "No avatar in world.";

            AlliancePrototype red = GetAlliance(RedPath);
            AlliancePrototype white = GetAlliance(WhitePath);
            if (red == null || white == null) return "PvP alliance prototypes not found in this game version.";

            pc.Player.SetAllianceOverride(red);

            ulong id = avatar.SpawnEnemyPhantomHero(PrototypeId.Invalid, avatar.CharacterLevel, out string error);
            if (id == 0) return $"phantom spawn failed: {error}";

            if (avatar.Game.EntityManager.GetEntity<Agent>(id) is not Agent phantom)
                return "phantom spawned but could not be resolved.";

            phantom.Properties[PropertyEnum.AllianceOverride] = white.DataRef;

            bool serverSaysHostile = avatar.IsHostileTo(phantom);
            return $"CONTROL: you=RED, phantom=WHITE. server hostile={serverSaysHostile}. " +
                   "Try to attack it — this is the case that SHOULD work.";
        }

        /// <summary>
        /// THE ACTUAL QUESTION. Player and phantom BOTH on RED — friendly in the
        /// real data — with the server's RED-vs-RED bit forced hostile. Client
        /// data still says friendly.
        /// </summary>
        [Command("sameteam")]
        [CommandDescription("The real test: you AND the phantom both on RED, with the server forced to treat RED as hostile to itself. Client data still says friendly.")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string SameTeam(string[] @params, NetClient client)
        {
            var pc = client as PlayerConnection;
            var avatar = pc?.Player?.CurrentAvatar;
            if (avatar == null) return "No avatar in world.";

            AlliancePrototype red = GetAlliance(RedPath);
            if (red == null) return "PVPTeam1RED not found in this game version.";

            // Server-side only. The client's own table is untouched.
            if (GameDataTables.Instance.AllianceTable.ForceHostileForTesting(red, red) == false)
                return "failed to force RED-vs-RED hostility server-side.";

            pc.Player.SetAllianceOverride(red);

            ulong id = avatar.SpawnEnemyPhantomHero(PrototypeId.Invalid, avatar.CharacterLevel, out string error);
            if (id == 0) return $"phantom spawn failed: {error}";

            if (avatar.Game.EntityManager.GetEntity<Agent>(id) is not Agent phantom)
                return "phantom spawned but could not be resolved.";

            phantom.Properties[PropertyEnum.AllianceOverride] = red.DataRef;

            bool serverSaysHostile = avatar.IsHostileTo(phantom);
            return $"SAMETEAM: you AND phantom both RED. server hostile={serverSaysHostile}, client data says FRIENDLY. " +
                   "Try to attack it. Can you target it? Does damage land?";
        }

        /// <summary>Puts the player back to normal so they are not left stuck on a PvP alliance.</summary>
        [Command("reset")]
        [CommandDescription("Clear your alliance override and undo the forced RED-vs-RED hostility.")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string Reset(string[] @params, NetClient client)
        {
            var pc = client as PlayerConnection;
            if (pc?.Player == null) return "No player.";

            pc.Player.SetAllianceOverride(null);

            AlliancePrototype red = GetAlliance(RedPath);
            if (red != null)
                GameDataTables.Instance.AllianceTable.ForceHostileForTesting(red, red, false);

            return "alliance override cleared and RED-vs-RED hostility undone.";
        }
    }
}
