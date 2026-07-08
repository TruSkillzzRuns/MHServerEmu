using System;
using System.IO;

namespace PhantomHeroes;

/// <summary>
/// Shared installer plumbing used by the WPF UI. Not an entry point — App.xaml
/// is the WPF startup URI. The types here are exposed so MainWindow can call
/// the same install / uninstall paths a CLI would use.
/// </summary>
public static class Program
{
    public const string BackupSuffix = ".phbak";

    /// <summary>Options used by the patchers. Public so the UI can construct it.</summary>
    public sealed class Options
    {
        public string Command = "";
        public string SourcePath = "";
        public string Version = "auto";
        public bool DryRun;
        public bool NoBackup;
    }

    /// <summary>
    /// Undo an install by restoring every .phbak sidecar back over its target and
    /// deleting the phantom source files the installer wrote. Writes progress to
    /// Console.Out so the UI's console-capture picks it up.
    /// </summary>
    public static void RunUninstall(Options o)
    {
        Console.WriteLine($"→ Uninstalling from: {o.SourcePath}");

        int restored = 0;
        int deleted = 0;

        foreach (var bak in Directory.EnumerateFiles(o.SourcePath, "*" + BackupSuffix, SearchOption.AllDirectories))
        {
            string target = bak[..^BackupSuffix.Length];
            if (o.DryRun) { Console.WriteLine($"[dry-run] restore {target}"); continue; }
            File.Copy(bak, target, overwrite: true);
            File.Delete(bak);
            Console.WriteLine($"restored {target}");
            restored++;
        }

        foreach (string rel in new[]
        {
            @"src\MHServerEmu.Games\Entities\Avatars\Avatar.PhantomHero.cs",
            @"src\MHServerEmu.Games\Entities\Avatars\Avatar.PhantomHeroRender.cs",
        })
        {
            string p = Path.Combine(o.SourcePath, rel);
            if (!File.Exists(p)) continue;
            if (o.DryRun) { Console.WriteLine($"[dry-run] delete {p}"); continue; }
            File.Delete(p);
            Console.WriteLine($"deleted  {p}");
            deleted++;
        }

        Console.WriteLine();
        Console.WriteLine($"Done. Restored {restored} file(s), deleted {deleted} phantom source file(s).");
    }
}
