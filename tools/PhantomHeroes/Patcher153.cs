using System;
using System.IO;

namespace PhantomHeroes;

/// <summary>
/// 1.53-generation patcher. Applies the six one-line null guards + writes the
/// Avatar.PhantomHero.cs partial embedded in this installer. Every edit is
/// anchor-matched against verbatim vanilla text — if any anchor is missing the
/// installer refuses to touch the file, so a drifted / forked tree is safe.
/// </summary>
public sealed class Patcher153 : PatcherBase, IPatcher
{
    public Patcher153(Detection det, Program.Options opts) : base(det, opts) { }

    public bool Install()
    {
        Console.WriteLine("→ 1.53 phantom-Player recipe: 6 null-guard edits + 1 new partial + 1 mailbox case.");
        Console.WriteLine();

        bool ok = true;

        // 1. Player.AOI getter — null-safe.
        ok &= ReplaceOnce(Det.PlayerCs,
            "public AreaOfInterest AOI { get => PlayerConnection.AOI; }",
            "public AreaOfInterest AOI { get => PlayerConnection?.AOI; }",
            "Player.AOI null-safe");

        // 2. Player.GetRegion — null-safe.
        ok &= ReplaceOnce(Det.PlayerCs,
@"public Region GetRegion()
        {
            // This shouldn't need any null checks, at least for now
            return AOI.Region;
        }",
@"public Region GetRegion()
        {
            // Phantom Players (Avatar.SpawnPhantomHero) have no PlayerConnection.
            // AOI is null in that case — return null and let PlayerIterator skip us.
            return PlayerConnection?.AOI?.Region;
        }",
            "Player.GetRegion null-safe");

        // 3. Player.ExitGame — null-safe AOI.SetRegion.
        ok &= ReplaceOnce(Det.PlayerCs,
            "SendMessage(NetMessageBeginExitGame.DefaultInstance);\r\n            AOI.SetRegion(0, true);",
            "SendMessage(NetMessageBeginExitGame.DefaultInstance);\r\n            AOI?.SetRegion(0, true);",
            "Player.ExitGame null-safe AOI");

        // 4. Entity.UpdateInterestPolicies — null-safe iteration over PlayerIterator.
        ok &= ReplaceOnce(Det.EntityCs,
@"                foreach (Player player in new PlayerIterator(Game))
                    player.AOI.ConsiderEntity(this, settings);",
@"                foreach (Player player in new PlayerIterator(Game))
                {
                    if (player == null) continue;
                    // Phantom Players have PlayerConnection=null, so AOI is null.
                    // Skip them cleanly; do NOT crash the interest sweep.
                    try { player.AOI?.ConsiderEntity(this, settings); }
                    catch (NullReferenceException) { /* half-initialized phantom */ }
                }",
            "Entity.UpdateInterestPolicies null-safe");

        // 5. Avatar.ChangeRegionPosition — phantom fast-path.
        //    Two upstream variants: older uses Verify.IsNotNull(), newer uses
        //    (player == null) + Logger.WarnReturn(). Both accepted.
        ok &= ReplaceOnceAny(Det.AvatarCs,
            "Avatar.ChangeRegionPosition phantom fast-path",
            (
@"            // Get player for AOI update
            Player player = GetOwnerOfType<Player>();
            if (!Verify.IsNotNull(player)) return ChangePositionResult.NotChanged;

            ChangePositionResult result;

            if (player.AOI.ContainsPosition(position.Value))",
@"            // Get player for AOI update
            Player player = GetOwnerOfType<Player>();
            if (!Verify.IsNotNull(player)) return ChangePositionResult.NotChanged;

            // Phantom-hero owner (SpawnPhantomHero) has no PlayerConnection so AOI is null.
            // Skip AOI-gated teleport/exit logic and do a plain base position change.
            if (player.AOI == null)
            {
                if (flags.HasFlag(ChangePositionFlags.EnterWorld))
                    AvatarWorldInstanceId++;
                return base.ChangeRegionPosition(position, orientation, flags);
            }

            ChangePositionResult result;

            if (player.AOI.ContainsPosition(position.Value))"
            ),
            (
@"            // Get player for AOI update
            Player player = GetOwnerOfType<Player>();
            if (player == null) return Logger.WarnReturn(ChangePositionResult.NotChanged, ""ChangeRegionPosition(): player == null"");

            ChangePositionResult result;

            if (player.AOI.ContainsPosition(position.Value))",
@"            // Get player for AOI update
            Player player = GetOwnerOfType<Player>();
            if (player == null) return Logger.WarnReturn(ChangePositionResult.NotChanged, ""ChangeRegionPosition(): player == null"");

            // Phantom-hero owner (SpawnPhantomHero) has no PlayerConnection so AOI is null.
            // Skip AOI-gated teleport/exit logic and do a plain base position change.
            if (player.AOI == null)
            {
                if (flags.HasFlag(ChangePositionFlags.EnterWorld))
                    AvatarWorldInstanceId++;
                return base.ChangeRegionPosition(position, orientation, flags);
            }

            ChangePositionResult result;

            if (player.AOI.ContainsPosition(position.Value))"
            )
        );

        // 6. Avatar.OnEnteredWorld — phantom fast-path.
        //    Two upstream variants: older uses Verify.IsNotNull(), newer uses
        //    (player == null) { Logger.Warn(...); return; } blocks. Both accepted.
        ok &= ReplaceOnceAny(Det.AvatarCs,
            "Avatar.OnEnteredWorld phantom fast-path",
            (
@"        public override void OnEnteredWorld(EntitySettings settings)
        {
            Player player = GetOwnerOfType<Player>();
            if (!Verify.IsNotNull(player)) return;

            Region region = Region;
            if (!Verify.IsNotNull(region)) return;

            player.UpdateScoringEventContext();",
@"        public override void OnEnteredWorld(EntitySettings settings)
        {
            Player player = GetOwnerOfType<Player>();
            if (!Verify.IsNotNull(player)) return;

            Region region = Region;
            if (!Verify.IsNotNull(region)) return;

            // Phantom-hero owner: no client to receive scoring/leaderboard/party
            // updates. Run only base + endurance regen + power init and bail.
            if (player.PlayerConnection == null)
            {
                base.OnEnteredWorld(settings);
                Properties[PropertyEnum.AvatarTimePlayedStart] = Game.CurrentTime;
                foreach (PrimaryResourceManaBehaviorPrototype primaryManaBehaviorProto in GetPrimaryResourceManaBehaviors())
                    EnableEnduranceRegen(primaryManaBehaviorProto.ManaType);
                InitializePowers();
                return;
            }

            player.UpdateScoringEventContext();"
            ),
            (
@"        public override void OnEnteredWorld(EntitySettings settings)
        {
            Player player = GetOwnerOfType<Player>();
            if (player == null)
            {
                Logger.Warn(""OnEnteredWorld(): player == null"");
                return;
            }

            Region region = Region;
            if (region == null)
            {
                Logger.Warn(""OnEnteredWorld(): region == null"");
                return;
            }

            player.UpdateScoringEventContext();",
@"        public override void OnEnteredWorld(EntitySettings settings)
        {
            Player player = GetOwnerOfType<Player>();
            if (player == null)
            {
                Logger.Warn(""OnEnteredWorld(): player == null"");
                return;
            }

            Region region = Region;
            if (region == null)
            {
                Logger.Warn(""OnEnteredWorld(): region == null"");
                return;
            }

            // Phantom-hero owner: no client to receive scoring/leaderboard/party
            // updates. Run only base + endurance regen + power init and bail.
            if (player.PlayerConnection == null)
            {
                base.OnEnteredWorld(settings);
                Properties[PropertyEnum.AvatarTimePlayedStart] = Game.CurrentTime;
                foreach (PrimaryResourceManaBehaviorPrototype primaryManaBehaviorProto in GetPrimaryResourceManaBehaviors())
                    EnableEnduranceRegen(primaryManaBehaviorProto.ManaType);
                InitializePowers();
                return;
            }

            player.UpdateScoringEventContext();"
            )
        );

        // 6b. Avatar.RefreshStatsPower — silence "statsPower verify failed" spam on phantom spawn.
        ok &= ReplaceOnce(Det.AvatarCs,
@"        private bool RefreshStatsPower()
        {
            if (IsInWorld == false)
                return false;

            Power statsPower = GetPower(AvatarPrototype.StatsPower);",
@"        private bool RefreshStatsPower()
        {
            if (IsInWorld == false)
                return false;

            // Phantom heroes (Avatar.SpawnPhantomHero) don't need StatsPower —
            // skip the lookup so we don't spam the log with 'statsPower verify
            // failed' on every phantom spawn.
            if (IsPhantomHero) return false;

            Power statsPower = GetPower(AvatarPrototype.StatsPower);",
            "Avatar.RefreshStatsPower phantom bail");

        // 6c. CommunityRegistry: skip broadcasts for phantom dbids to silence
        //     "No player found for dbid" spam on every phantom AOI update.
        string communityRegistryCs = Path.Combine(Det.SrcDir, "MHServerEmu.PlayerManagement", "Social", "CommunityRegistry.cs");
        if (File.Exists(communityRegistryCs))
        {
            var saved = Det.PlayerCs; // reuse ReplaceOnce for a file other than PlayerCs
            ok &= ReplaceOnce(communityRegistryCs,
@"            ulong playerDbId = broadcast.MemberPlayerDbId;

            // We should be receiving broadcasts only from online players",
@"            ulong playerDbId = broadcast.MemberPlayerDbId;

            // Phantom heroes (Avatar.SpawnPhantomHero) mint synthetic DbGuids in
            // the 0xB07F_ADED_XXXX_XXXX range that are never registered with the
            // community system. Silently ignore their broadcasts so the log
            // stays clean.
            if ((playerDbId & 0xFFFF_FFFF_0000_0000UL) == 0xB07F_ADED_0000_0000UL)
                return false;

            // We should be receiving broadcasts only from online players",
                "CommunityRegistry.ReceiveMemberBroadcast phantom dbid skip");
        }

        // 7. Avatar: IsPhantomHero + IsMovementAuthoritative override.
        ok &= ReplaceOnce(Det.AvatarCs,
            "public override bool IsMovementAuthoritative => false;",
@"// Phantom heroes (SpawnPhantomHero) have no client — the server MUST be
        // authoritative for their movement, or Locomotor.FollowEntity produces no
        // visible walking on the real client's screen. Real avatars keep the
        // default false so the client stays in charge.
        public bool IsPhantomHero { get; internal set; }
        public override bool IsMovementAuthoritative => IsPhantomHero;",
            "Avatar.IsMovementAuthoritative phantom flip");

        // 8. Write Avatar.PhantomHero.cs from the embedded template.
        string phantomPath = Path.Combine(Path.GetDirectoryName(Det.AvatarCs)!, "Avatar.PhantomHero.cs");
        string phantomBody = LoadEmbedded("Templates.153.Avatar.PhantomHero.cs");
        WriteFile(phantomPath, phantomBody, "Avatar.PhantomHero.cs");

        // 9. Write the PhantomHero WebHandler + register its routes.
        ok &= InstallWebHandler();

        // 10. Wire an entry point. Two candidates:
        //    - Modded trees (Downloads copy) have a "botfarm" case in
        //      GameServiceMailbox.cs — leave that alone if present.
        //    - Vanilla upstream trees have a [CommandGroup] chat-command system —
        //      drop a PhantomHeroCommands.cs so users get `!phantom spawn N`.
        ok &= WirePhantomEntryPoint();

        Console.WriteLine();
        Console.WriteLine(ok
            ? "✓ Install complete. Rebuild:\n    dotnet build src/MHServerEmu/MHServerEmu.csproj -c Release"
            : "✗ Install did not complete cleanly. See errors above; run `uninstall` to roll back.");
        return ok;
    }

    private bool InstallWebHandler()
    {
        if (!Directory.Exists(Det.WebApiFolder))
        {
            Console.WriteLine($"⚠ WebApi folder not found ({Det.WebApiFolder}) — skipping runtime web endpoint. Only the chat command will work.");
            return true; // non-fatal
        }

        string handlerPath = Path.Combine(Det.WebApiFolder, "PhantomHeroWebHandler.cs");
        string body = LoadEmbedded("Templates.153.PhantomHeroWebHandler.cs");
        WriteFile(handlerPath, body, "PhantomHeroWebHandler.cs");

        // Register the three routes by anchor-appending after ServerStatus registration.
        if (!File.Exists(Det.WebFrontendService))
        {
            Console.WriteLine($"⚠ WebFrontendService.cs missing at {Det.WebFrontendService} — routes not registered.");
            return true;
        }
        string wsSrc = File.ReadAllText(Det.WebFrontendService);
        if (wsSrc.Contains("PhantomHeroSpawnWebHandler"))
        {
            Console.WriteLine("= WebFrontendService routes already registered, skipping.");
            return true;
        }
        return ReplaceOnce(Det.WebFrontendService,
            "_webService.RegisterHandler(\"/ServerStatus\", new ServerStatusWebHandler());",
@"_webService.RegisterHandler(""/ServerStatus"", new ServerStatusWebHandler());

            // PhantomHeroes runtime endpoints.
            _webService.RegisterHandler(""/webapi/phantom/spawn"",  new MHServerEmu.WebFrontend.Handlers.WebApi.PhantomHeroSpawnWebHandler());
            _webService.RegisterHandler(""/webapi/phantom/clear"",  new MHServerEmu.WebFrontend.Handlers.WebApi.PhantomHeroClearWebHandler());
            _webService.RegisterHandler(""/webapi/phantom/status"", new MHServerEmu.WebFrontend.Handlers.WebApi.PhantomHeroStatusWebHandler());",
            "WebFrontendService: register /webapi/phantom/*");
    }

    private bool WirePhantomEntryPoint()
    {
        // Modded (Downloads) tree already has a botfarm case; leave it.
        if (File.Exists(Det.GameSvcMailbox))
        {
            string mbSrc = File.ReadAllText(Det.GameSvcMailbox);
            if (mbSrc.Contains("SpawnPhantomHero"))
            {
                Console.WriteLine("= Mailbox already calls SpawnPhantomHero — skipping chat-command drop.");
                return true;
            }
            if (mbSrc.Contains("case \"botfarm\":"))
            {
                Console.WriteLine("⚠ Mailbox has a \"botfarm\" case but doesn't call SpawnPhantomHero. Change the per-bot call to");
                Console.WriteLine("    avatar.SpawnPhantomHero(level, null, out string err)");
                Console.WriteLine("  This installer won't auto-rewrite that case — the shape varies between forks.");
                Console.WriteLine("  (Chat-command file will be dropped anyway as a fallback.)");
            }
        }

        // Vanilla upstream: drop a chat-command file. Users get !phantom spawn N.
        string commandsDir = Path.Combine(Det.SourceRoot, "src", "MHServerEmu", "Commands", "Implementations");
        if (!Directory.Exists(commandsDir))
        {
            Console.Error.WriteLine($"✗ No Commands/Implementations folder at {commandsDir} — cannot install chat command. Install SpawnPhantomHero triggers manually per PHANTOM_HERO_MOD.md.");
            return true; // non-fatal — the mod code is still installed
        }
        string cmdPath = Path.Combine(commandsDir, "PhantomHeroCommands.cs");
        WriteFile(cmdPath, PhantomHeroCommandsSource, "PhantomHeroCommands.cs");
        Console.WriteLine("  Once the server is rebuilt, users can spawn phantoms in-game with:");
        Console.WriteLine("    !phantom spawn 10");
        Console.WriteLine("    !phantom clear");
        return true;
    }

    /// <summary>
    /// Vanilla-upstream chat-command file. Drops into the standard
    /// Commands/Implementations folder next to DebugCommands.cs, EntityCommands.cs, etc.
    /// Reflection is used to reach SpawnPhantomHero / DespawnAllPhantomHeroes so
    /// the command file compiles even if the phantom partial was written after
    /// this file (we don't need a source-level reference).
    /// </summary>
    private const string PhantomHeroCommandsSource = @"using System.Reflection;
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
    [CommandGroup(""phantom"")]
    [CommandGroupDescription(""Spawn / clear phantom-hero server-side NPC bots."")]
    public class PhantomHeroCommands : CommandGroup
    {
        [Command(""spawn"")]
        [CommandDescription(""Spawn phantom-hero NPCs near you. Args: [count=5] [level=60]"")]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandUserLevel(AccountUserLevel.Admin)]
        public string Spawn(string[] @params, NetClient client)
        {
            int count = 5;
            int level = 60;
            if (@params.Length >= 1 && int.TryParse(@params[0], out int c)) count = System.Math.Clamp(c, 1, 50);
            if (@params.Length >= 2 && int.TryParse(@params[1], out int l)) level = System.Math.Clamp(l, 1, 60);

            var pc = (client as PlayerConnection) ?? throw new System.InvalidOperationException(""Only clients can run !phantom spawn."");
            var avatar = pc.Player?.CurrentAvatar;
            if (avatar == null) return ""No avatar in world."";

            // Reflection so this file compiles regardless of when Avatar.PhantomHero.cs lands.
            var method = typeof(Avatar).GetMethod(""SpawnPhantomHero"", BindingFlags.Public | BindingFlags.Instance);
            if (method == null) return ""SpawnPhantomHero not present — is Avatar.PhantomHero.cs in the build?"";

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
                ? $""Phantoms: spawned={spawned} failed={failed}. First err: {firstError}""
                : $""Phantoms: spawned={spawned} failed={failed}."";
        }

        [Command(""clear"")]
        [CommandDescription(""Despawn every phantom you've spawned."")]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandUserLevel(AccountUserLevel.Admin)]
        public string Clear(string[] @params, NetClient client)
        {
            var pc = (client as PlayerConnection) ?? throw new System.InvalidOperationException(""Only clients can run !phantom clear."");
            var avatar = pc.Player?.CurrentAvatar;
            if (avatar == null) return ""No avatar in world."";

            var method = typeof(Avatar).GetMethod(""DespawnAllPhantomHeroes"", BindingFlags.Public | BindingFlags.Instance);
            if (method == null) return ""DespawnAllPhantomHeroes not present."";

            var removed = method.Invoke(avatar, System.Array.Empty<object>());
            return $""Phantoms despawned: {removed}."";
        }
    }
}
";
}
