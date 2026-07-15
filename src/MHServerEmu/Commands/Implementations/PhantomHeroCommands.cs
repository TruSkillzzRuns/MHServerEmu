using MHServerEmu.Commands.Attributes;
using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
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
            int level = PhantomCommandUtil.ParseLevelClamp(@params, 1);

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
                    return PhantomCommandUtil.FormatMultipleMatches(matches);

                ulong heroId = avatar.SpawnPhantomHeroFromIntent(matches[0].AvatarRef, level, null, level > 0, 0, out string heroError);
                return heroId != 0
                    ? $"Spawned {matches[0].ShortName}."
                    : $"Failed to spawn {matches[0].ShortName}: {heroError}";
            }

            // Numeric path: spawn N random heroes.
            count = PhantomCommandUtil.ClampCount(count, 5, 50);

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
        [CommandDescription("Manage saved phantom squads. Usage: squad save [name] | squad spawn [name] | squad list | squad delete [name] | squad default [name|clear]")]
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

                case "default":
                    if (name == null)
                    {
                        string current = player.GetDefaultSquadName();
                        return current != null ? $"Default squad: '{current}'." : "No default squad set.";
                    }
                    return player.SetDefaultSquad(name);

                default:
                    return $"Unknown squad operation '{op}'. Use save, spawn, list, delete or default.";
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
        [CommandDescription("Re-roll phantom gear. Usage: gear (all, random) | gear [hero] (one, random) | gear bis (all, best-in-slot) | gear [hero] bis (one, best-in-slot).")]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandUserLevel(AccountUserLevel.Admin)]
        public string Gear(string[] @params, NetClient client)
        {
            var pc = (client as PlayerConnection) ?? throw new System.InvalidOperationException("Only clients can run !phantom gear.");
            var player = pc.Player;
            if (player == null || player.CurrentAvatar == null) return "No avatar in world.";

            // "bis" can appear as the only arg (all phantoms) or after a hero
            // name (that one phantom) — strip it out to find the hero query.
            bool toBiS = false;
            string heroQuery = null;
            foreach (string p in @params)
            {
                if (p.Equals("bis", System.StringComparison.OrdinalIgnoreCase)) toBiS = true;
                else heroQuery = p;
            }

            return player.RerollPhantomGear(heroQuery, toBiS);
        }

        [Command("enemy")]
        [CommandDescription("Spawn HOSTILE phantom heroes that hunt you. Args: [count=1] [level=your level], or [heroname] [level].")]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandUserLevel(AccountUserLevel.Admin)]
        public string Enemy(string[] @params, NetClient client)
        {
            var pc = (client as PlayerConnection) ?? throw new System.InvalidOperationException("Only clients can run !phantom enemy.");
            var avatar = pc.Player?.CurrentAvatar;
            if (avatar == null) return "No avatar in world.";

            int level = PhantomCommandUtil.ParseLevelClamp(@params, 1);

            int count = 0;
            if (@params.Length >= 1 && int.TryParse(@params[0], out count) == false)
            {
                var matches = Avatar.FindPhantomHeroRefs(@params[0]);
                if (matches.Count == 0) return $"No hero matching '{@params[0]}'.";
                if (matches.Count > 1) return PhantomCommandUtil.FormatMultipleMatches(matches);

                ulong heroId = avatar.SpawnEnemyPhantomHero(matches[0].AvatarRef, level, out string heroError);
                return heroId != 0
                    ? $"Enemy {matches[0].ShortName} inbound. Good luck."
                    : $"Failed: {heroError}";
            }

            count = PhantomCommandUtil.ClampCount(count, 1, 20);
            int spawned = 0, failed = 0;
            string firstError = null;
            for (int i = 0; i < count; i++)
            {
                ulong id = avatar.SpawnEnemyPhantomHero(MHServerEmu.Games.GameData.PrototypeId.Invalid, level, out string error);
                if (id != 0) spawned++;
                else { failed++; firstError ??= error; }
            }
            return firstError == null
                ? $"Enemy phantoms inbound: {spawned}. Good luck."
                : $"Enemy phantoms: spawned={spawned} failed={failed}. First err: {firstError}";
        }

        [Command("teamup")]
        [CommandDescription("Spawn a team-up as a phantom hero. Args: [teamupname] [level=your level] [enemy?]. e.g. !phantom teamup rocket 60 enemy")]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandUserLevel(AccountUserLevel.Admin)]
        public string TeamUp(string[] @params, NetClient client)
        {
            var pc = (client as PlayerConnection) ?? throw new System.InvalidOperationException("Only clients can run !phantom teamup.");
            var avatar = pc.Player?.CurrentAvatar;
            if (avatar == null) return "No avatar in world.";
            if (@params.Length < 1) return "Usage: !phantom teamup [name] [level] [enemy]";

            var all = Avatar.GetAllPhantomTeamUpRefs();
            var matches = new System.Collections.Generic.List<(MHServerEmu.Games.GameData.PrototypeId Ref, string ShortName)>();
            string query = @params[0];
            foreach (var (r, n) in all)
            {
                if (n.Equals(query, System.StringComparison.OrdinalIgnoreCase)) { matches.Clear(); matches.Add((r, n)); break; }
                if (n.Contains(query, System.StringComparison.OrdinalIgnoreCase)) matches.Add((r, n));
            }
            if (matches.Count == 0) return $"No team-up matching '{query}'.";
            if (matches.Count > 1)
                return PhantomCommandUtil.FormatMultipleMatches(matches);

            int level = PhantomCommandUtil.ParseLevelClamp(@params, 1);
            bool enemy = @params.Length >= 3 && @params[2].Equals("enemy", System.StringComparison.OrdinalIgnoreCase);

            ulong id = avatar.SpawnTeamUpPhantomHero(matches[0].Ref, level, out string err, enemy: enemy);
            return id != 0
                ? $"Team-up {matches[0].ShortName} {(enemy ? "inbound as HOSTILE" : "joined your side")}."
                : $"Failed to spawn team-up {matches[0].ShortName}: {err}";
        }

        [Command("rogue")]
        [CommandDescription("Rogue Encounter — spontaneous ambushes by hostile heroes while roaming. Args: on | off | status | trigger.")]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandUserLevel(AccountUserLevel.Admin)]
        public string Rogue(string[] @params, NetClient client)
        {
            var pc = (client as PlayerConnection) ?? throw new System.InvalidOperationException("Only clients can run !phantom rogue.");
            var player = pc.Player;
            if (player == null) return "No player.";

            string op = @params.Length >= 1 ? @params[0].ToLowerInvariant() : "status";
            switch (op)
            {
                case "on":
                    player.RogueEncounterEnabled = true;
                    return "Rogue Encounter enabled. Watch your back.";
                case "off":
                    player.RogueEncounterEnabled = false;
                    return "Rogue Encounter disabled.";
                case "trigger":
                    return player.TriggerRogueEncounterNow();
                case "status":
                    long cd = player.RogueEncounterCooldownRemainingMs / 1000;
                    return $"Rogue Encounter: {(player.RogueEncounterEnabled ? "ON" : "off")}. Cooldown: {cd}s.";
                default:
                    return "Usage: phantom rogue on | off | status | trigger";
            }
        }

        [Command("testnemesis")]
        [CommandDescription("Spawn a hostile nemesis phantom at a specific rank + level for testing loot tiers. Args: [heroname] [rank 1-5] [level 1-60].")]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandUserLevel(AccountUserLevel.Admin)]
        public string TestNemesis(string[] @params, NetClient client)
        {
            var pc = (client as PlayerConnection) ?? throw new System.InvalidOperationException("Only clients can run !phantom testnemesis.");
            var avatar = pc.Player?.CurrentAvatar;
            if (avatar == null) return "No avatar in world.";
            if (@params.Length < 1) return "Usage: phantom testnemesis [heroname] [rank 1-5] [level 1-60]";

            var matches = Avatar.FindPhantomHeroRefs(@params[0]);
            if (matches.Count == 0) return $"No hero matching '{@params[0]}'.";
            if (matches.Count > 1)
                return PhantomCommandUtil.FormatMultipleMatches(matches);

            int rank = PhantomCommandUtil.ParseRankClamp(@params, 1, 5);
            int level = @params.Length >= 3 && int.TryParse(@params[2], out int l) ? System.Math.Clamp(l, PhantomCommandUtil.MinLevel, PhantomCommandUtil.MaxLevel) : 60;

            string display = $"★{rank} TEST {matches[0].ShortName}";
            ulong id = avatar.SpawnNemesisPhantomHero(matches[0].AvatarRef, level, display, rank, out string error);
            return id != 0
                ? $"Spawned rank-{rank} level-{level} nemesis {matches[0].ShortName}. Kill it to test the loot tier."
                : $"Failed: {error}";
        }

        [Command("enemyclear")]
        [CommandDescription("Despawn every enemy phantom.")]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandUserLevel(AccountUserLevel.Admin)]
        public string EnemyClear(string[] @params, NetClient client)
        {
            var pc = (client as PlayerConnection) ?? throw new System.InvalidOperationException("Only clients can run !phantom enemyclear.");
            var avatar = pc.Player?.CurrentAvatar;
            if (avatar == null) return "No avatar in world.";
            return $"Enemy phantoms despawned: {avatar.DespawnAllEnemyPhantoms()}.";
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

            int removed = avatar.DespawnAllPhantomHeroes();
            return $"Phantoms despawned: {removed}.";
        }

        [Command("caps")]
        [CommandDescription("Show the game's actual party/raid size caps (from loaded client data) and the effective cap in your current region.")]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandUserLevel(AccountUserLevel.Admin)]
        public string Caps(string[] @params, NetClient client)
        {
            var pc = (client as PlayerConnection) ?? throw new System.InvalidOperationException("Only clients can run !phantom caps.");
            var avatar = pc.Player?.CurrentAvatar;
            if (avatar == null) return "No avatar in world.";

            var globals = GameDatabase.GlobalsPrototype;
            if (globals == null) return "GlobalsPrototype not loaded.";

            var region = avatar.Region;
            var proto = region?.Prototype;

            string regionInfo = proto == null
                ? "no region"
                : $"{proto.DataRef.GetName()} behavior={proto.Behavior} playerLimit={proto.PlayerLimit}";

            return $"PlayerPartyMaxSize={globals.PlayerPartyMaxSize} PlayerRaidMaxSize={globals.PlayerRaidMaxSize} | current region: {regionInfo}";
        }
    }
}
