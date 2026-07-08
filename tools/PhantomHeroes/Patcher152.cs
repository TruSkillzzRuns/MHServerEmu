using System;
using System.IO;

namespace PhantomHeroes;

/// <summary>
/// 1.52-generation patcher. Writes the render-override template as a new
/// partial next to Avatar.cs, and prints the steps the user must complete
/// against their fork's exact API surface. Does NOT auto-edit vanilla files
/// because 1.52 community forks vary (namespace, property vs helper naming,
/// AI-controller wiring).
/// </summary>
public sealed class Patcher152 : PatcherBase, IPatcher
{
    public Patcher152(Detection det, Program.Options opts) : base(det, opts) { }

    public bool Install()
    {
        Console.WriteLine("→ 1.52 render-override recipe: writes a template partial, no engine changes needed.");
        Console.WriteLine();

        // 1. Write the template partial.
        string templatePath = Path.Combine(Path.GetDirectoryName(Det.AvatarCs)!, "Avatar.PhantomHeroRender.cs");
        string body = LoadEmbedded("Templates.152.Agent.PhantomHeroRender.cs");
        WriteFile(templatePath, body, "Avatar.PhantomHeroRender.cs (1.52 template)");

        Console.WriteLine();
        Console.WriteLine("→ Manual step required — the render-override API name varies across 1.52 forks:");
        Console.WriteLine();
        Console.WriteLine("  1. Open the file above.");
        Console.WriteLine("  2. Find the line marked   // TODO_1_52: uncomment ONE of these depending on your branch");
        Console.WriteLine("  3. Uncomment whichever of these compiles against your Agent class:");
        Console.WriteLine("       bot.ClientPrototypeRefOverride = renderRef;");
        Console.WriteLine("       bot.SetClientPrototypeOverride(renderRef);");
        Console.WriteLine("       bot.SetSpoofRender(renderRef);");
        Console.WriteLine("  4. Add a chat command or webapi endpoint that calls SpawnPhantomHeroRendered(...) on your player's avatar.");
        Console.WriteLine();
        Console.WriteLine("→ Rebuild:");
        Console.WriteLine("    dotnet build src/MHServerEmu/MHServerEmu.csproj -c Release");
        Console.WriteLine();
        Console.WriteLine("Once one of those lines compiles, the recipe is complete. No null guards, no phantom Player, no other file edits.");
        return true;
    }
}
