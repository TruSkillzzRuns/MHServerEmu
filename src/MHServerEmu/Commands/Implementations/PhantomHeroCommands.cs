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
        [CommandDescription("Spawn phantom-hero NPCs near you. Args: [count=5] [level=60]")]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandUserLevel(AccountUserLevel.Admin)]
        public string Spawn(string[] @params, NetClient client)
        {
            int count = 5;
            int level = 60;
            if (@params.Length >= 1 && int.TryParse(@params[0], out int c)) count = System.Math.Clamp(c, 1, 50);
            if (@params.Length >= 2 && int.TryParse(@params[1], out int l)) level = System.Math.Clamp(l, 1, 60);

            var pc = (client as PlayerConnection) ?? throw new System.InvalidOperationException("Only clients can run !phantom spawn.");
            var avatar = pc.Player?.CurrentAvatar;
            if (avatar == null) return "No avatar in world.";

            // Reflection so this file compiles regardless of when Avatar.PhantomHero.cs lands.
            var method = typeof(Avatar).GetMethod("SpawnPhantomHero", BindingFlags.Public | BindingFlags.Instance);
            if (method == null) return "SpawnPhantomHero not present — is Avatar.PhantomHero.cs in the build?";

            int spawned = 0, failed = 0;
            var firstError = string.Empty;
            for (int i = 0; i < count; i++)
            {
                var args = new object?[] { level, null, null };
                var idBoxed = method.Invoke(avatar, args);
                ulong id = idBoxed is ulong u ? u : 0UL;
                if (id != 0) spawned++;
                else { failed++; if (firstError.Length == 0 && args[2] is string s) firstError = s; }
            }
            return firstError.Length > 0
                ? $"Phantoms: spawned={spawned} failed={failed}. First err: {firstError}"
                : $"Phantoms: spawned={spawned} failed={failed}.";
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
