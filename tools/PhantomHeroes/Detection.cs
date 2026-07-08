using System;
using System.IO;

namespace PhantomHeroes;

/// <summary>
/// Sniffs a user-supplied MHServerEmu source tree to figure out which patcher
/// generation applies. Prints a human-readable report either way.
/// </summary>
public sealed class Detection
{
    public string SourceRoot { get; }
    public string SrcDir     => Path.Combine(SourceRoot, "src");
    public string GamesProject   => Path.Combine(SrcDir, "MHServerEmu.Games", "MHServerEmu.Games.csproj");
    public string HostProject    => Path.Combine(SrcDir, "MHServerEmu",       "MHServerEmu.csproj");
    public string AvatarCs       => Path.Combine(SrcDir, "MHServerEmu.Games", "Entities", "Avatars", "Avatar.cs");
    public string PlayerCs       => Path.Combine(SrcDir, "MHServerEmu.Games", "Entities", "Player.cs");
    public string EntityCs       => Path.Combine(SrcDir, "MHServerEmu.Games", "Entities", "Entity.cs");
    public string AgentCs        => Path.Combine(SrcDir, "MHServerEmu.Games", "Entities", "Agent.cs");
    public string GameSvcMailbox => Path.Combine(SrcDir, "MHServerEmu.Games", "Network", "GameServiceMailbox.cs");
    public string WebFrontendService => Path.Combine(SrcDir, "MHServerEmu.WebFrontend", "WebFrontendService.cs");
    public string WebApiFolder      => Path.Combine(SrcDir, "MHServerEmu.WebFrontend", "Handlers", "WebApi");

    public bool HasSrcDir { get; private set; }
    public bool HasGamesProject { get; private set; }
    public bool HasHostProject { get; private set; }
    public bool HasAvatarCs { get; private set; }
    public bool HasPlayerCs { get; private set; }
    public bool HasEntityCs { get; private set; }
    public bool HasAgentCs { get; private set; }
    public bool HasGameSvcMailbox { get; private set; }
    public bool AlreadyInstalled153 { get; private set; }
    public bool AgentHasClientPrototypeRefOverride { get; private set; }
    public bool AvatarHasIsMovementAuthoritative { get; private set; }

    public string DetectedVersion { get; private set; } = "unknown";
    public bool Ok => HasSrcDir && HasGamesProject && HasAvatarCs && HasPlayerCs && HasEntityCs && HasGameSvcMailbox && DetectedVersion != "unknown";

    /// <summary>
    /// Best-guess diagnosis of *why* detection failed, so the UI can tell the
    /// user something more useful than a wall of red pills.
    /// </summary>
    public string Diagnosis
    {
        get
        {
            if (Ok)
            {
                string gen = DetectedVersion switch
                {
                    "153" => "modern (phantom-Player) codebase",
                    "152" => "legacy (render-override) codebase",
                    _     => DetectedVersion
                };
                return $"OK — {gen}. Ready to install.";
            }
            if (!Directory.Exists(SourceRoot)) return "Path does not exist.";

            bool looksLikeInstall = File.Exists(Path.Combine(SourceRoot, "MHServerEmu.exe"))
                                 || File.Exists(Path.Combine(SourceRoot, "MHServerEmu.dll"));

            if (looksLikeInstall && !HasSrcDir)
                return "This looks like a compiled server install (has MHServerEmu.exe/.dll but no src/ folder). "
                     + "Phantom Heroes patches source code, not binaries. Point at the source tree — the folder that contains 'src/'.";

            if (!HasSrcDir)
                return "No 'src/' subfolder found. Point at the root of the MHServerEmu source tree "
                     + "(the folder that contains 'src/', 'tools/', and 'MHServerEmu.sln').";

            if (!HasGamesProject)
                return "'src/MHServerEmu.Games/MHServerEmu.Games.csproj' missing. "
                     + "This tree is incomplete or a very different fork.";

            var missing = new System.Collections.Generic.List<string>();
            if (!HasAvatarCs)       missing.Add("Avatar.cs");
            if (!HasPlayerCs)       missing.Add("Player.cs");
            if (!HasEntityCs)       missing.Add("Entity.cs");
            if (!HasGameSvcMailbox) missing.Add("GameServiceMailbox.cs");
            if (missing.Count > 0)
                return "Source tree found but missing expected files: " + string.Join(", ", missing) + ".";

            if (DetectedVersion == "unknown")
                return "Source tree looks valid but I can't identify the server generation. "
                     + "Neither 'IsMovementAuthoritative' on Avatar (modern) nor 'ClientPrototypeRefOverride' on Agent (legacy) was found. "
                     + "This is likely a fork with different naming — either patch by hand from the docs, or force the generation with --version 153 or 152 from CLI.";

            return "Detection failed for an unclassified reason.";
        }
    }

    public Detection(string sourceRoot)
    {
        SourceRoot = sourceRoot;
        HasSrcDir         = Directory.Exists(SrcDir);
        HasGamesProject   = File.Exists(GamesProject);
        HasHostProject    = File.Exists(HostProject);
        HasAvatarCs       = File.Exists(AvatarCs);
        HasPlayerCs       = File.Exists(PlayerCs);
        HasEntityCs       = File.Exists(EntityCs);
        HasAgentCs        = File.Exists(AgentCs);
        HasGameSvcMailbox = File.Exists(GameSvcMailbox);

        if (HasAvatarCs)
        {
            string avatarSrc = File.ReadAllText(AvatarCs);
            AvatarHasIsMovementAuthoritative = avatarSrc.Contains("IsMovementAuthoritative");
            AlreadyInstalled153 = avatarSrc.Contains("public bool IsPhantomHero") ||
                                  File.Exists(Path.Combine(Path.GetDirectoryName(AvatarCs)!, "Avatar.PhantomHero.cs"));
        }
        if (HasAgentCs)
        {
            string agentSrc = File.ReadAllText(AgentCs);
            AgentHasClientPrototypeRefOverride = agentSrc.Contains("ClientPrototypeRefOverride");
        }

        DetectedVersion = InferVersion();
    }

    private string InferVersion()
    {
        // Signal 1: Agent.ClientPrototypeRefOverride only exists in 1.52-generation trees.
        if (AgentHasClientPrototypeRefOverride)
            return "152";

        // Signal 2: Avatar.IsMovementAuthoritative override with the false default is
        // a 1.53-generation signature. Also require the 1.53 files we plan to patch
        // exist so we don't try to patch a partial or heavily-forked tree.
        if (HasAvatarCs && HasPlayerCs && HasEntityCs && HasGameSvcMailbox && AvatarHasIsMovementAuthoritative)
            return "153";

        return "unknown";
    }

    public void PrintReport(TextWriter w)
    {
        w.WriteLine($"→ Source root: {SourceRoot}");
        w.WriteLine($"    src/                          {Mark(HasSrcDir)}");
        w.WriteLine($"    src/MHServerEmu.Games/*.csproj{Mark(HasGamesProject)}");
        w.WriteLine($"    src/MHServerEmu/*.csproj      {Mark(HasHostProject)}");
        w.WriteLine($"    Avatar.cs                     {Mark(HasAvatarCs)}");
        w.WriteLine($"    Player.cs                     {Mark(HasPlayerCs)}");
        w.WriteLine($"    Entity.cs                     {Mark(HasEntityCs)}");
        w.WriteLine($"    Agent.cs                      {Mark(HasAgentCs)}");
        w.WriteLine($"    GameServiceMailbox.cs         {Mark(HasGameSvcMailbox)}");
        w.WriteLine();
        w.WriteLine($"→ Version detection: {DetectedVersion}");
        w.WriteLine($"    Agent has ClientPrototypeRefOverride (1.52 signal): {AgentHasClientPrototypeRefOverride}");
        w.WriteLine($"    Avatar has IsMovementAuthoritative (1.53 signal):   {AvatarHasIsMovementAuthoritative}");
        w.WriteLine($"    Already installed (1.53):                           {AlreadyInstalled153}");
        if (DetectedVersion == "unknown")
        {
            w.WriteLine();
            w.WriteLine("⚠ Could not auto-detect the server generation. If you know it is 1.52 or 1.53,");
            w.WriteLine("  rerun with --version 152 or --version 153.");
        }
    }

    private static string Mark(bool ok) => ok ? "  ok" : "  MISSING";
}
