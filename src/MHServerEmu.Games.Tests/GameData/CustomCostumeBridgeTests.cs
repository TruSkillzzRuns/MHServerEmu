using MHServerEmu.Games.GameData;

// Stand-in for MHCostumeMod's costume loader, so CustomCostumeBridge can be
// tested without the mod installed. The bridge finds a loader by scanning
// loaded assemblies for a type *named* CustomCostumeLoader, so this works from
// any namespace.
//
// It deliberately does NOT sit in MHServerEmu.Games.GameData, even though that
// is where the real one lives. On a server that has merged the mod, both types
// would then share a full name and every reference here would raise CS0436 --
// four warnings on a clean build, which reads as "something is wrong with the
// merge" to anyone following the setup guide. Nothing here ships to a server:
// it exists only inside the test assembly.
//
// Attribution: MHCostumeMod is a separate project by Mr.Gippy under its own
// licence. The type below is written for this test. It reproduces the public
// member NAMES and shapes the bridge reflects over -- the interface, which it
// has to match or the test would verify nothing -- and none of that project's
// implementation. The values are invented. The mod is not bundled with this
// fork; obtain it from its own project.
//
// Keep this in step with the real loader's public shape. If a future version
// renames a member, update this stand-in to match rather than leaving it
// pinned to the old name, or these tests will pass while the live integration
// quietly reports "no custom costumes".
namespace MHServerEmu.Games.Tests.Fakes
{
    public static class CustomCostumeLoader
    {
        public readonly struct CatalogCostume(string name, string token, int enumValue, ulong customId)
        {
            public readonly string Name = name;
            public readonly string Token = token;
            public readonly int Enum = enumValue;
            public readonly ulong CustomId = customId;
        }

        public readonly struct CatalogFxPack(string token, string displayName, string hero, int effects)
        {
            public readonly string Token = token;
            public readonly string DisplayName = displayName;
            public readonly string Hero = hero;
            public readonly int Effects = effects;
        }

        public const ulong SymbioteId = 0xDEADBEEF;
        public const ulong JadeId = 0xCAFEBABE;

        public static Dictionary<PrototypeId, (int enumValue, string displayName, string token)> CustomInfo { get; } = new()
        {
            [(PrototypeId)SymbioteId] = (100001, "Symbiote Spider-Man", "spiderman_symbiote"),
            [(PrototypeId)JadeId] = (100002, "Jade Giant", "shehulk_jade"),
        };

        public static CatalogCostume[] Catalog { get; } =
        {
            new("Symbiote Spider-Man", "spiderman_symbiote", 100001, SymbioteId),
            new("Jade Giant", "shehulk_jade", 100002, JadeId),
        };

        public static CatalogFxPack[] FxPackCatalog { get; } =
        {
            new("fx_web", "Web FX", "SpiderMan", 53),
        };
    }
}

namespace MHServerEmu.Games.Tests.GameData
{
    /// <summary>
    /// Tests for the reflection bridge to MHCostumeMod's costume loader.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bridge exists so neither the phantom spawn path nor the WebFrontend
    /// takes a build dependency on a mod almost no server has installed. That
    /// makes reflection the only contract between them, and reflection fails
    /// quietly: a renamed member or a mistyped dictionary key reports "no custom
    /// costumes" instead of throwing, which is indistinguishable from an
    /// unmodded server. These tests pin the member names and shapes the bridge
    /// depends on.
    /// </para>
    /// <para>
    /// On a server that has merged the mod, the real loader wins — the bridge
    /// resolves it by assembly-qualified name before it ever scans for the
    /// stand-in — and it holds no costumes during a test run. The value
    /// assertions are skipped in that case rather than failing, since what they
    /// describe is the stand-in's contents, not the bridge's behaviour.
    /// </para>
    /// </remarks>
    public class CustomCostumeBridgeTests
    {
        private const ulong SymbioteId = Fakes.CustomCostumeLoader.SymbioteId;
        private const ulong JadeId = Fakes.CustomCostumeLoader.JadeId;

        /// <summary>
        /// True when the bridge resolved the stand-in rather than a real loader.
        /// </summary>
        private static bool StandInResolved
            => CustomCostumeBridge.GetCatalog().Any(c => c.CustomId == SymbioteId);

        [Fact]
        public void DetectsAnInstalledLoader()
        {
            Assert.True(CustomCostumeBridge.IsAvailable);
        }

        [Fact]
        public void RecognisesACustomCostumeId()
        {
            if (StandInResolved == false) return;

            // The boxed-key lookup. If PrototypeId did not survive boxing into
            // IDictionary.Contains, this returns false and every custom costume
            // silently renders as its donor.
            Assert.True(CustomCostumeBridge.IsCustom((PrototypeId)SymbioteId));
            Assert.True(CustomCostumeBridge.IsCustom((PrototypeId)JadeId));
        }

        [Fact]
        public void LeavesOrdinaryCostumesAlone()
        {
            // A stock costume must not be re-asserted -- doing so would add a
            // visible donor-then-custom flicker to every ordinary spawn. True
            // whichever loader answered.
            Assert.False(CustomCostumeBridge.IsCustom((PrototypeId)0x1234));
            Assert.False(CustomCostumeBridge.IsCustom(PrototypeId.Invalid));
        }

        [Fact]
        public void ReadsTheCatalog()
        {
            var catalog = CustomCostumeBridge.GetCatalog();
            if (StandInResolved == false) return;

            Assert.Equal(2, catalog.Count);
            Assert.Equal("Symbiote Spider-Man", catalog[0].Name);
            Assert.Equal("spiderman_symbiote", catalog[0].Token);
            Assert.Equal(100001, catalog[0].Enum);
            Assert.Equal(SymbioteId, catalog[0].CustomId);
        }

        [Fact]
        public void ReportsNoHeroWhenTheIdIsNotAliased()
        {
            // GetHeroFor resolves the hero through the donor costume's record,
            // which needs loaded game data -- which a test run does not have,
            // and GameDatabase throws rather than returning null when it is
            // missing. Invalid is the correct answer either way: the catalog
            // then omits "hero" and the client falls back to matching names.
            // Pinned here because the alternative, letting that exception out,
            // turns one unresolvable costume into a 500 for the whole catalog.
            Assert.Equal(PrototypeId.Invalid, CustomCostumeBridge.GetHeroFor((PrototypeId)SymbioteId));
        }

        [Fact]
        public void ReportsNoHeroForAnInvalidId()
        {
            Assert.Equal(PrototypeId.Invalid, CustomCostumeBridge.GetHeroFor(PrototypeId.Invalid));
        }

        [Fact]
        public void ReadsFxPacks()
        {
            var packs = CustomCostumeBridge.GetFxPacks();
            if (StandInResolved == false) return;

            Assert.Single(packs);
            Assert.Equal("fx_web", packs[0].Token);
            Assert.Equal("SpiderMan", packs[0].Hero);
            // A count of packages, not a list of them -- the app's model got
            // this wrong once and failed to parse the whole response.
            Assert.Equal(53, packs[0].Effects);
        }
    }
}
