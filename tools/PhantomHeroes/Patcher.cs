using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace PhantomHeroes;

public interface IPatcher { bool Install(); }

/// <summary>
/// Shared anchor-based file editing. Each edit finds an EXACT source anchor
/// (verbatim vanilla MHServerEmu text) and replaces it with a new block.
/// Refuses to patch if the anchor isn't found — that means the target file
/// has drifted from the version this installer knows.
/// </summary>
public abstract class PatcherBase
{
    protected readonly Detection Det;
    protected readonly Program.Options Opts;
    private const string BackupSuffix = ".phbak";

    protected PatcherBase(Detection det, Program.Options opts) { Det = det; Opts = opts; }

    protected bool ReplaceOnce(string filePath, string anchor, string replacement, string label)
    {
        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"✗ {label}: file missing: {filePath}");
            return false;
        }
        string src = File.ReadAllText(filePath);

        // Normalize line endings so a Windows CRLF anchor still matches a Unix
        // LF source file (and vice-versa). We match against the normalized copy
        // and translate the hit position back onto the raw source by walking
        // through it in step, so backups and rewrites keep the file's original
        // line-ending style intact everywhere except the patched span.
        string normSrc    = src.Replace("\r\n", "\n");
        string normAnchor = anchor.Replace("\r\n", "\n");

        // "Already patched" MUST be checked before the anchor search — because
        // after a successful patch the anchor text is gone from the file
        // (replaced by `replacement`), so re-running the installer would
        // otherwise print "anchor not found" and refuse to touch the file.
        if (src.Contains(replacement) || normSrc.Contains(replacement.Replace("\r\n", "\n")))
        {
            Console.WriteLine($"= {label}: already patched, skipping.");
            return true;
        }

        int normIdx = normSrc.IndexOf(normAnchor, StringComparison.Ordinal);
        if (normIdx < 0)
        {
            Console.Error.WriteLine($"✗ {label}: anchor not found in {filePath}. File has drifted from the vanilla text this installer knows. Aborting to protect your tree.");
            return false;
        }

        // Translate normalized index → raw-source index (raw is longer wherever CRLF appears).
        int rawIdx = 0, seen = 0;
        while (seen < normIdx && rawIdx < src.Length)
        {
            if (src[rawIdx] == '\r' && rawIdx + 1 < src.Length && src[rawIdx + 1] == '\n')
            { rawIdx += 2; seen += 1; }
            else
            { rawIdx += 1; seen += 1; }
        }
        // Same walk to skip the matched anchor length in raw coords.
        int rawEnd = rawIdx, seenAnchor = 0;
        while (seenAnchor < normAnchor.Length && rawEnd < src.Length)
        {
            if (src[rawEnd] == '\r' && rawEnd + 1 < src.Length && src[rawEnd + 1] == '\n')
            { rawEnd += 2; seenAnchor += 1; }
            else
            { rawEnd += 1; seenAnchor += 1; }
        }

        BackupOnce(filePath);
        string patched = src[..rawIdx] + replacement + src[rawEnd..];
        if (Opts.DryRun)
        {
            Console.WriteLine($"[dry-run] {label}: would rewrite {filePath}");
            return true;
        }
        File.WriteAllText(filePath, patched);
        Console.WriteLine($"✓ {label}: rewrote {filePath}");
        return true;
    }

    // Same as ReplaceOnce but accepts multiple (anchor, replacement) variants.
    // Tries each in order; first anchor that matches wins. Lets one anchor cover
    // multiple upstream styles (e.g. the older `Verify.IsNotNull(x)` null-check
    // vs the newer `if (x == null) { Logger.Warn(...); return; }` style) without
    // pinning the tool to a single upstream commit.
    protected bool ReplaceOnceAny(string filePath, string label, params (string anchor, string replacement)[] variants)
    {
        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"✗ {label}: file missing: {filePath}");
            return false;
        }
        string src = File.ReadAllText(filePath);
        string normSrc = src.Replace("\r\n", "\n");

        // Already-patched short-circuit: if ANY variant's replacement is present, we're done.
        foreach (var (_, replacement) in variants)
        {
            if (src.Contains(replacement) || normSrc.Contains(replacement.Replace("\r\n", "\n")))
            {
                Console.WriteLine($"= {label}: already patched, skipping.");
                return true;
            }
        }

        for (int v = 0; v < variants.Length; v++)
        {
            var (anchor, replacement) = variants[v];
            string normAnchor = anchor.Replace("\r\n", "\n");
            int normIdx = normSrc.IndexOf(normAnchor, StringComparison.Ordinal);
            if (normIdx < 0) continue;

            int rawIdx = 0, seen = 0;
            while (seen < normIdx && rawIdx < src.Length)
            {
                if (src[rawIdx] == '\r' && rawIdx + 1 < src.Length && src[rawIdx + 1] == '\n')
                { rawIdx += 2; seen += 1; }
                else
                { rawIdx += 1; seen += 1; }
            }
            int rawEnd = rawIdx, seenAnchor = 0;
            while (seenAnchor < normAnchor.Length && rawEnd < src.Length)
            {
                if (src[rawEnd] == '\r' && rawEnd + 1 < src.Length && src[rawEnd + 1] == '\n')
                { rawEnd += 2; seenAnchor += 1; }
                else
                { rawEnd += 1; seenAnchor += 1; }
            }

            BackupOnce(filePath);
            string patched = src[..rawIdx] + replacement + src[rawEnd..];
            if (Opts.DryRun)
            {
                Console.WriteLine($"[dry-run] {label}: would rewrite {filePath} (variant #{v + 1} of {variants.Length})");
                return true;
            }
            File.WriteAllText(filePath, patched);
            Console.WriteLine($"✓ {label}: rewrote {filePath} (variant #{v + 1} of {variants.Length})");
            return true;
        }

        Console.Error.WriteLine($"✗ {label}: none of the {variants.Length} known anchor variants matched in {filePath}. Upstream may have introduced a new style — please report this file so a variant can be added.");
        return false;
    }

    protected void WriteFile(string filePath, string content, string label)
    {
        if (Opts.DryRun)
        {
            Console.WriteLine($"[dry-run] {label}: would write {filePath} ({content.Length} chars)");
            return;
        }
        string dir = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(dir);
        // Don't overwrite user's local edits without a backup.
        if (File.Exists(filePath))
        {
            BackupOnce(filePath);
        }
        File.WriteAllText(filePath, content);
        Console.WriteLine($"✓ {label}: wrote {filePath} ({content.Length} chars)");
    }

    private void BackupOnce(string filePath)
    {
        if (Opts.NoBackup) return;
        string bak = filePath + BackupSuffix;
        if (File.Exists(bak)) return; // don't clobber the first backup
        if (Opts.DryRun)
        {
            Console.WriteLine($"[dry-run] backup: would copy {filePath} → {bak}");
            return;
        }
        File.Copy(filePath, bak);
        Console.WriteLine($"  backup: {bak}");
    }

    protected static string LoadEmbedded(string logicalName)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName)
                      ?? throw new InvalidOperationException($"Embedded resource missing: {logicalName}");
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }
}
