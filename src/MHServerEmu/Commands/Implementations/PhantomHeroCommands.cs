using System.Reflection;
using MHServerEmu.Commands.Attributes;
using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Network;

namespace MHServerEmu.Commands.Implementations
{
    /// <summary>
    /// Phantom Heroes — chat commands.
    ///   !phantom spawn [count] [level]   — spawn N phantom-hero NPCs near you
    ///   !phantom clear                   — despawn every phantom you've spawned
    /// Installed by the standalone PhantomHeroes tool.
    /// </summary>
    [CommandGroup("phantom")]
    [CommandGroupDescription("Spawn / clear phantom-hero server-side NPC bots.")]
    public class PhantomHeroCommands : CommandGroup
    {
        [Command("spawn")]
        [CommandDescription("Spawn phantom-hero NPCs near you. Args: [count=5] [level=your level], or [heroname] [level] to spawn a specific hero. Phantoms auto-level with you unless an explicit level is given.")]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandUserLevel(AccountUserLevel.Admin)]
        public string Spawn(string[] @params, NetClient client)
        {
            var pc = (client as PlayerConnection) ?? throw new System.InvalidOperationException("Only clients can run !phantom spawn.");
            var avatar = pc.Player?.CurrentAvatar;
            if (avatar == null) return "No avatar in world.";

            // 0 = "match caller's CharacterLevel" (handled inside
            // SpawnPhantomHeroCore). The tick loop then keeps them in sync
            // if the human levels up — see OnPhantomTick's level-sync block.
            int level = 0;
            if (@params.Length >= 2 && int.TryParse(@params[1], out int l)) level = System.Math.Clamp(l, 1, 60);

            // Non-numeric first arg = spawn a specific hero by name. The
            // name is matched at runtime against the playable-avatar pool
            // from the loaded client data — no hero names live in server
            // source.
            int count = 0;
            if (@params.Length >= 1 && int.TryParse(@params[0], out count) == false)
            {
                var matches = Avatar.FindPhantomHeroRefs(@params[0]);
                if (matches.Count == 0)
                    return $"No hero matching '{@params[0]}'.";
                if (matches.Count > 1)
                {
                    var names = new System.Text.StringBuilder();
                    for (int i = 0; i < matches.Count && i < 8; i++)
                    {
                        if (i > 0) names.Append(", ");
                        names.Append(matches[i].ShortName);
                    }
                    if (matches.Count > 8) names.Append(", ...");
                    return $"Multiple matches: {names}. Be more specific.";
                }

                ulong heroId = avatar.SpawnPhantomHeroFromIntent(matches[0].AvatarRef, level, null, level > 0, 0, out string heroError);
                return heroId != 0
                    ? $"Spawned {matches[0].ShortName}."
                    : $"Failed to spawn {matches[0].ShortName}: {heroError}";
            }

            // Numeric path: spawn N random heroes.
            count = @params.Length >= 1 ? System.Math.Clamp(count, 1, 50) : 5;

            int spawned = 0, failed = 0;
            var firstError = string.Empty;
            for (int i = 0; i < count; i++)
            {
                ulong id = avatar.SpawnPhantomHero(level, null, out string error);
                if (id != 0) spawned++;
                else { failed++; if (firstError.Length == 0 && error != null) firstError = error; }
            }
            return firstError.Length > 0
                ? $"Phantoms: spawned={spawned} failed={failed}. First err: {firstError}"
                : $"Phantoms: spawned={spawned} failed={failed}.";
        }

        [Command("squad")]
        [CommandDescription("Manage saved phantom squads. Usage: squad save [name] | squad spawn [name] | squad list | squad delete [name]")]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandUserLevel(AccountUserLevel.Admin)]
        public string Squad(string[] @params, NetClient client)
        {
            var pc = (client as PlayerConnection) ?? throw new System.InvalidOperationException("Only clients can run !phantom squad.");
            var player = pc.Player;
            var avatar = player?.CurrentAvatar;
            if (player == null || avatar == null) return "No avatar in world.";

            if (@params.Length == 0)
                return "Usage: phantom squad save [name] | spawn [name] | list | delete [name]";

            string op = @params[0].ToLowerInvariant();
            string name = @params.Length >= 2 ? @params[1] : null;

            switch (op)
            {
                case "save":
                    if (name == null) return "Usage: phantom squad save [name]";
                    return player.SavePhantomSquad(name);

                case "spawn":
                case "load":
                    if (name == null) return "Usage: phantom squad spawn [name]";
                    return player.SpawnPhantomSquad(name, avatar);

                case "list":
                    return player.ListPhantomSquads();

                case "delete":
                    if (name == null) return "Usage: phantom squad delete [name]";
                    return player.DeletePhantomSquad(name);

                default:
                    return $"Unknown squad operation '{op}'. Use save, spawn, list or delete.";
            }
        }

        [Command("costume")]
        [CommandDescription("Phantom costumes. Usage: costume random | costume [hero] random | costume [hero] [costumename] | costume list [hero]")]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandUserLevel(AccountUserLevel.Admin)]
        public string Costume(string[] @params, NetClient client)
        {
            var pc = (client as PlayerConnection) ?? throw new System.InvalidOperationException("Only clients can run !phantom costume.");
            var player = pc.Player;
            if (player == null || player.CurrentAvatar == null) return "No avatar in world.";

            if (@params.Length == 0)
                return "Usage: phantom costume random | costume [hero] random | costume [hero] [costumename] | costume list [hero]";

            // costume random — every active phantom re-rolls.
            if (@params.Length == 1 && @params[0].Equals("random", System.StringComparison.OrdinalIgnoreCase))
                return player.RandomizePhantomCostumes();

            // costume list <hero>
            if (@params[0].Equals("list", System.StringComparison.OrdinalIgnoreCase))
            {
                if (@params.Length < 2) return "Usage: phantom costume list [hero]";
                return player.ListPhantomCostumes(@params[1]);
            }

            // costume <hero> <random|costumename>
            if (@params.Length < 2)
                return "Usage: phantom costume [hero] random | costume [hero] [costumename]";
            return player.SetPhantomCostume(@params[0], @params[1]);
        }

        [Command("gear")]
        [CommandDescription("Re-roll phantom gear. Usage: gear (all phantoms) | gear [hero] (one phantom). Gear rolls at each phantom's current level.")]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandUserLevel(AccountUserLevel.Admin)]
        public string Gear(string[] @params, NetClient client)
        {
            var pc = (client as PlayerConnection) ?? throw new System.InvalidOperationException("Only clients can run !phantom gear.");
            var player = pc.Player;
            if (player == null || player.CurrentAvatar == null) return "No avatar in world.";

            return player.RerollPhantomGear(@params.Length >= 1 ? @params[0] : null);
        }

        [Command("clear")]
        [CommandDescription("Despawn every phantom you've spawned.")]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandUserLevel(AccountUserLevel.Admin)]
        public string Clear(string[] @params, NetClient client)
        {
            var pc = (client as PlayerConnection) ?? throw new System.InvalidOperationException("Only clients can run !phantom clear.");
            var avatar = pc.Player?.CurrentAvatar;
            if (avatar == null) return "No avatar in world.";

            var method = typeof(Avatar).GetMethod("DespawnAllPhantomHeroes", BindingFlags.Public | BindingFlags.Instance);
            if (method == null) return "DespawnAllPhantomHeroes not present.";

            var removed = method.Invoke(avatar, System.Array.Empty<object>());
            return $"Phantoms despawned: {removed}.";
        }
    }
}
