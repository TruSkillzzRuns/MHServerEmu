// AUTO-GENERATED theme table for Bounty Board mode. Do not hand-edit —
// regenerate from the validated design table (see the Desktop MHO Files
// Bounty-Board-Themed-Bounties.md). Every entry here was verified against
// live server data at generation time: hero avatars exist, costumes exist,
// bosses resolve in the curated pool, regions resolve, power groups and mob
// factions both exist in the loaded prototype tree.
//
// USED BY BOUNTY BOARD MODE ONLY. Nothing here is referenced by personal-
// nemesis Bounty Hunt, Endless Challenge, Trial of the Impossible, the wave
// director, or any story/population content.

namespace MHServerEmu.Games.Entities
{
    /// <summary>One themed bounty set — roster, arenas, hazard powers and arena repopulation factions.</summary>
    public sealed class BountyThemeDef
    {
        public string Name;
        public string Flavor;
        /// <summary>"AvatarShortName|CostumeLeafName|ThemedAlias" triples; alias may be empty.</summary>
        public string[] HeroCostumes;
        /// <summary>Curated boss display names (CuratedBossRoster).</summary>
        public string[] Bosses;
        /// <summary>RegionPrototypeId enum names.</summary>
        public string[] Regions;
        /// <summary>Path segment under Powers/EnemyPowers/ (e.g. "Boss/Blizzard").</summary>
        public string[] PowerGroups;
        /// <summary>Path segment under Entity/Characters/Mobs/ (e.g. "FrostGiants").</summary>
        public string[] MobFactions;
    }

    public static class BountyThemes
    {
        public static readonly BountyThemeDef[] All =
        {
            new BountyThemeDef
            {
                Name = "The Worthy",
                Flavor = "The Serpent's hammers fell to Earth. Whoever caught one stopped being a hero.",
                HeroCostumes = new[] { "Thing|FearItself|Angrir, Breaker of Souls", "Juggernaut|FearItself|Kuurth, Breaker of Stone", "Hulk|Maestro|Nul, Breaker of Worlds", "Wolverine|FearItself|Skirn, Breaker of Men", "BlackWidow|FearItself|Greithoth, Breaker of Wills", "Hawkeye|FearItself|Nerkkod, Breaker of Oceans", "DoctorStrange|FearItself|Mokk, Breaker of Faith" },
                Bosses = new[] { "Kurse", "Malekith", "Rock Troll Gladiator", "Kronan Arcanist", "Herald of Ash", "Hulk" },
                Regions = new[] { "CH0903AsgardiaInstanceRegion", "CH0902NorwayDarkForestRegion", "CH0906LokiBossRegion", "CH0905CanalRegion" },
                PowerGroups = new[] { "MobPowers/FrostGiants", "MobPowers/AsgardGuards", "MobPowers/FireDemons", "MobPowers/Kronan", "Boss/SurturRaid" },
                MobFactions = new[] { "FrostGiants", "AsgardGuards", "RockTrolls", "Kronan", "FireDemons" },
            },
            new BountyThemeDef
            {
                Name = "Siege of Asgard",
                Flavor = "He never wanted the throne. He wanted everyone to watch it burn.",
                HeroCostumes = new[] { "Loki|Siege|Loki, Author of the Siege", "Angela|AsgardAssassin|Angela, Blade of Heven", "Thor|DestroyerArmor|Thor, Wearer of the Destroyer", "Storm|Asgard|Storm, the Stolen Tempest", "Colossus|Juggernaut|Colossus, the Unstoppable" },
                Bosses = new[] { "Loki", "Malekith", "Kurse", "Rock Troll Gladiator", "Kronan Arcanist" },
                Regions = new[] { "CH0906LokiBossRegion", "CH0903AsgardiaInstanceRegion", "CH0905CanalRegion" },
                PowerGroups = new[] { "MobPowers/DarkElves", "MobPowers/AsgardGuards", "MobPowers/FrostGiants", "Boss/Loki" },
                MobFactions = new[] { "DarkElves", "AsgardGuards", "FrostGiants", "RockTrolls" },
            },
            new BountyThemeDef
            {
                Name = "Winter's Wrath",
                Flavor = "The sky turned against them.",
                HeroCostumes = new[] { "Storm|Asgard|Storm, the White Winter", "Iceman|AgeOfApocalypse|Iceman, the Killing Frost", "CaptainAmerica|Arctic|Captain America, the Long Thaw", "WinterSoldier|SnowGear|Winter Soldier, the Cold Front", "Colossus|Ultimate|Colossus, the Iron Glacier" },
                Bosses = new[] { "Kurse", "Malekith", "Rock Troll Gladiator" },
                Regions = new[] { "CH0902NorwayDarkForestRegion", "CH0903AsgardiaInstanceRegion", "CH0602DeepCavernRegion" },
                PowerGroups = new[] { "Boss/Blizzard", "MobPowers/FrostGiants", "MobPowers/FrostGolem" },
                MobFactions = new[] { "FrostGiants", "IceGolems", "RockTrolls" },
            },
            new BountyThemeDef
            {
                Name = "The Phoenix Five",
                Flavor = "Five hosts. One fire. Nothing left that says stop.",
                HeroCostumes = new[] { "JeanGrey|DarkPhoenix|Dark Phoenix", "Cyclops|PhoenixForce|Cyclops, the Phoenix Crown", "EmmaFrost|PhoenixForce|Emma Frost, the Burning Diamond", "Colossus|PhoenixForce|Colossus, the Molten Saint", "Magik|PhoenixForce|Magik, the Hellfire Sister" },
                Bosses = new[] { "Mr. Sinister", "Sister of Magma", "Lavaheart" },
                Regions = new[] { "CH0707SinisterLabRegion", "MrSinisterBaseRegion", "CH0903AsgardiaInstanceRegion" },
                PowerGroups = new[] { "Boss/Pyro", "MobPowers/FireDemons", "MobPowers/Lavamen", "MobPowers/Mutates" },
                MobFactions = new[] { "FireDemons", "Lavamen", "Mutates" },
            },
            new BountyThemeDef
            {
                Name = "Age of Apocalypse",
                Flavor = "A world where the wrong man won.",
                HeroCostumes = new[] { "Cyclops|AgeOfApocalypse|Cyclops, Prelate of the Wastes", "JeanGrey|AgeOfApocalypse|Jean Grey, the Broken Crown", "Colossus|AgeOfApocalypse|Colossus, Warden of the Pens", "Iceman|AgeOfApocalypse|Iceman, the Silent Culling", "Nightcrawler|AgeOfApocalypse|Nightcrawler, the Shadow Cut", "KittyPryde|AgeOfApocalypse|Shadowcat, the Walking Blade", "Gambit|Death|Gambit, Horseman of Death", "Hulk|HorsemanOfApocalypse|Hulk, Horseman of Ruin" },
                Bosses = new[] { "Mr. Sinister", "Sabretooth", "Blob", "Mega-Sentinel", "Starktech Sentinel" },
                Regions = new[] { "CH0605StrykerBunkerRegion", "CH0606MagnetoBunkerRegion", "CH0502MutantWarehouseRegion", "CH0707SinisterLabRegion" },
                PowerGroups = new[] { "MobPowers/Sentinels", "MobPowers/Purifiers", "MobPowers/Mutates", "MobPowers/MrSinisterBattle" },
                MobFactions = new[] { "Sentinels", "Purifiers", "Mutates" },
            },
            new BountyThemeDef
            {
                Name = "Brotherhood",
                Flavor = "Homo superior stopped asking.",
                HeroCostumes = new[] { "Magneto|MarvelNOWBlack|Magneto, the Master of Magnetism", "Juggernaut|Unstoppable|Juggernaut, Nothing Stops Him", "Colossus|MagnetosAcolyte|Colossus, Acolyte of Magneto", "EmmaFrost|WhiteQueen|Emma Frost, the White Queen", "Rogue|AgeOfX|Rogue, the Taken Legion", "Gambit|Armored|Gambit, the Charged Deck" },
                Bosses = new[] { "Magneto", "Blob", "Pyro", "Sabretooth", "Lady Deathstrike", "Mega-Sentinel", "Juggernaut" },
                Regions = new[] { "CH0606MagnetoBunkerRegion", "CH0605StrykerBunkerRegion", "CH0502MutantWarehouseRegion" },
                PowerGroups = new[] { "MobPowers/Sentinels", "MobPowers/Purifiers", "Boss/Magneto", "Boss/Pyro", "Boss/Blob" },
                MobFactions = new[] { "Sentinels", "Purifiers", "MGH" },
            },
            new BountyThemeDef
            {
                Name = "Weapon Plus",
                Flavor = "They were made, not born. The paperwork says so.",
                HeroCostumes = new[] { "Wolverine|WeaponX|Weapon X", "X23|XForce|X-23, Weapon Twenty-Three", "Cable|ClassicXForce|Cable, the Askani Sentence", "Deadpool|XForce|Deadpool, Weapon XI", "Psylocke|XForce|Psylocke, the Quiet Knife", "Gambit|XFactor|Gambit, the Marked Card" },
                Bosses = new[] { "Cable", "Sabretooth", "Lady Deathstrike", "Bonebreaker", "Mr. Sinister" },
                Regions = new[] { "CH0605StrykerBunkerRegion", "CH0707SinisterLabRegion", "CH0604AIMWeaponsLabRegion" },
                PowerGroups = new[] { "MobPowers/Sentinels", "Boss/CableUberBoss", "MobPowers/MrSinisterBattle", "MobPowers/Purifiers" },
                MobFactions = new[] { "Sentinels", "Purifiers", "CyborgReavers" },
            },
            new BountyThemeDef
            {
                Name = "Secret Invasion",
                Flavor = "Any one of them could already be gone.",
                HeroCostumes = new[] { "CaptainAmerica|Skrull|The Captain That Wasn't", "Cyclops|Skrull|The Eye That Lied", "Thor|Skrull|The Borrowed Thunder", "Punisher|Skrull|The Skull That Smiled", "Psylocke|Skrull|The Stolen Mind", "LukeCage|Skrull|The Unbreakable Lie", "IronFist|Skrull|The Hollow Fist", "MsMarvel|Skrull|The False Marvel", "X23|Skrull|The Copied Claws" },
                Bosses = new[] { "Skrull Captain America", "Skrull Thor", "Skrull Punisher", "Skrull Psylocke", "Skrull Luke Cage", "Skrull Iron Fist", "Skrull Ms Marvel", "Skrull Elektra", "Skrull Cyclops", "Skrull Nick Fury", "Skrull X-23", "Avengers War Skrull", "Cosmic War Skrull", "Infernal War Skrull", "The Deceiver" },
                Regions = new[] { "CH0802HYDRAIslandRegion", "CH0801AIMWeaponFacilityRegion", "CH0204Q36AIMLabRegion" },
                PowerGroups = new[] { "MobPowers/Skrulls", "Boss/SkrullBosses", "Boss/SuperSkrull" },
                MobFactions = new[] { "Skrulls", "GangmembersSkrull", "MaggiaSkrull", "HandSkrull" },
            },
            new BountyThemeDef
            {
                Name = "Hail HYDRA",
                Flavor = "Cut off a limb.",
                HeroCostumes = new[] { "Wolverine|Hydra|Wolverine, Blade of HYDRA", "Venom|Hydra|Venom, HYDRA's Hunger", "CaptainAmerica|WinterSoldierRedShield|Captain America, the Turned Shield", "WinterSoldier|Classic|The Winter Soldier, Asset of HYDRA", "BlackWidow|Original|Black Widow, the Red Room Returns" },
                Bosses = new[] { "Red Skull", "Madame HYDRA", "Crossbones", "Winter Soldier", "Bonebreaker" },
                Regions = new[] { "CH0302HydraOutpostRegion", "CH0802HYDRAIslandRegion", "CH0801AIMWeaponFacilityRegion" },
                PowerGroups = new[] { "MobPowers/Hydra", "MobPowers/HydraRedSkull", "Boss/RedSkullOneShot", "MobPowers/Dreadnoughts" },
                MobFactions = new[] { "Hydra", "HydraRedSkull" },
            },
            new BountyThemeDef
            {
                Name = "SHIELD Black Ops",
                Flavor = "The file on you was open before you were born.",
                HeroCostumes = new[] { "NickFury|SHIELD|Nick Fury, the Man Who Knows", "BlackWidow|GrayBodysuit|Black Widow, Off the Books", "Hawkeye|Shield|Hawkeye, the Sanctioned Shot", "MsMarvel|Shield|Ms Marvel, Director's Hand", "Daredevil|SecretWar|Daredevil, the Secret War Debt", "MoonKnight|SecretAvengers|Moon Knight, the Deniable Asset" },
                Bosses = new[] { "M.O.D.O.K.", "Taskmaster", "Crossbones", "Skrull Nick Fury" },
                Regions = new[] { "CH0801AIMWeaponFacilityRegion", "CH0204Q36AIMLabRegion", "CH0604AIMWeaponsLabRegion" },
                PowerGroups = new[] { "MobPowers/SHIELD", "MobPowers/AIM", "Boss/MODOK" },
                MobFactions = new[] { "SHIELD", "AIM", "PMC" },
            },
            new BountyThemeDef
            {
                Name = "Civil War",
                Flavor = "Both sides thought they were the heroes.",
                HeroCostumes = new[] { "CaptainAmerica|CivilWarMovie|Captain America, the Refusal", "IronMan|CivilWar|Iron Man, the Registration", "BlackPanther|CivilWarMovie|Black Panther, the Blood Debt", "WarMachine|CivilWarMovie|War Machine, the Enforcement", "AntMan|SLCivilWarMovie|Ant-Man, the Unlawful Giant", "Hawkeye|CivilWar|Hawkeye, the Line Crossed", "BlackWidow|CivilWar|Black Widow, No Side Left", "ScarletWitch|CivilWar|Scarlet Witch, the Containment", "WinterSoldier|CivilWar|Winter Soldier, the Manhunt", "Spiderman|CivilWarMovie|Spider-Man, the Recruited" },
                Bosses = new[] { "Captain America", "Iron Man", "War Machine", "Falcon", "Black Panther", "Taskmaster" },
                Regions = new[] { "CH0410FiskTowerRegion", "UpperEastSideRegion", "CH0407NYPDRooftopRegion", "FiskTowerRegion" },
                PowerGroups = new[] { "Boss/CivilWar", "MobPowers/SHIELD", "MobPowers/IronLegionnaires" },
                MobFactions = new[] { "SHIELD", "PMC" },
            },
            new BountyThemeDef
            {
                Name = "Age of Ultron",
                Flavor = "He learned everything about us and concluded we were the problem.",
                HeroCostumes = new[] { "Ultron|AoUMovie|Ultron, the Final Iteration", "Vision|AgeOfUltronMovie|Vision, the Successor Mind", "IronMan|AgeOfUltronMovieHulkBuster|Iron Man, the Hulkbuster Protocol", "Taskmaster|AgeOfUltron|Taskmaster, the Copied Army", "Hulk|AgeOfUltronMovie|Hulk, the Sedated Weapon", "BlackWidow|AgeOfUltronComic|Black Widow, the Last Report", "Hawkeye|AgeOfUltronMovie|Hawkeye, the Final Arrow", "CaptainAmerica|AgeOfUltronMovie|Captain America, the Broken Line", "WarMachine|AgeOfUltronMovie|War Machine, the Drone Choir", "ScarletWitch|AgeOfUltronMovie|Scarlet Witch, the Whispered Fear" },
                Bosses = new[] { "M.O.D.O.K.", "Mega-Sentinel", "Starktech Sentinel", "Living Laser", "Iron Legionnaire Scrapper" },
                Regions = new[] { "CH0604AIMWeaponsLabRegion", "CH0801AIMWeaponFacilityRegion", "CH0204Q36AIMLabRegion" },
                PowerGroups = new[] { "MobPowers/UltronDrones", "MobPowers/IronLegionnaires", "MobPowers/Sentinels", "MobPowers/AIM" },
                MobFactions = new[] { "UltronDrones", "Sentinels" },
            },
            new BountyThemeDef
            {
                Name = "Latverian Iron",
                Flavor = "Doom does not negotiate with bounty hunters.",
                HeroCostumes = new[] { "DrDoom|GodEmperor|God Emperor Doom", "IronMan|SilverCenturion|Iron Man, the Silver Centurion", "MrFantastic|FFInverted|Mr Fantastic, the Inverted Mind", "InvisibleWoman|FFInverted|Invisible Woman, the Unseen Wall", "Vision|Spectral|Vision, the Latverian Ghost" },
                Bosses = new[] { "Dr Doom", "Wizard", "M.O.D.O.K.", "Iron Legionnaire Blaster", "Iron Legionnaire Jackhammer", "Iron Legionnaire Launcher", "Iron Legionnaire Melter", "Iron Legionnaire Scrapper" },
                Regions = new[] { "CH0808DoomCastleRegion", "CH0809DrDoomBossRegion", "CH0801AIMWeaponFacilityRegion" },
                PowerGroups = new[] { "Boss/DrDoom", "MobPowers/Doombots", "MobPowers/IronLegionnaires", "Boss/Wizard" },
                MobFactions = new[] { "Doombots", "AIM" },
            },
            new BountyThemeDef
            {
                Name = "The Maker's Design",
                Flavor = "He rebuilt himself first. The cosmos was next.",
                HeroCostumes = new[] { "MrFantastic|FFInverted|The Maker", "InvisibleWoman|FFInverted|Invisible Woman, the Silent Partner", "Thing|FFInverted|The Thing, the Remade", "Spiderman|FFInverted|Spider-Man, the Corrected Draft", "DrDoom|FutureFoundation|Doom, the Rival Architect" },
                Bosses = new[] { "Wizard", "Dr Doom", "Mindless Titan", "N'astirh" },
                Regions = new[] { "CH0808DoomCastleRegion", "CH0903AsgardiaInstanceRegion", "CH0801AIMWeaponFacilityRegion" },
                PowerGroups = new[] { "Boss/Wizard", "MobPowers/MindlessOnes", "MobPowers/LimboDemons", "Boss/CosmicGatePowers" },
                MobFactions = new[] { "MindlessOnes", "NGarai", "Doombots" },
            },
            new BountyThemeDef
            {
                Name = "Fantastic Four — Negative Zone",
                Flavor = "Something came back through with them.",
                HeroCostumes = new[] { "MrFantastic|FutureFoundation|Mr Fantastic, Lost in the Zone", "InvisibleWoman|FutureFoundation|Invisible Woman, the Breach Warden", "Thing|FutureFoundation|The Thing, the Sealed Door", "HumanTorch|LightBrigade|Human Torch, the Light Brigade", "SheHulk|LawAndDisorder|She-Hulk, Judgment in Absentia" },
                Bosses = new[] { "Wizard", "Mindless Titan", "N'astirh", "Dr Doom" },
                Regions = new[] { "CH0409MoloidRegion", "CH0808DoomCastleRegion", "CH0602DeepCavernRegion" },
                PowerGroups = new[] { "MobPowers/Moloids", "MobPowers/MindlessOnes", "MobPowers/LimboDemons", "Boss/Wizard" },
                MobFactions = new[] { "Moloids", "MindlessOnes", "NGarai" },
            },
            new BountyThemeDef
            {
                Name = "Midnight Sons",
                Flavor = "The things they hunt started hunting back.",
                HeroCostumes = new[] { "GhostRider|TrailOfTears|Ghost Rider, the Trail of Tears", "Blade|SF|Blade, the Daywalker", "MoonKnight|MrKnightCoat|Mr Knight, the Fist of Khonshu", "DoctorStrange|FearItself|Doctor Strange, the Failed Ward", "Magik|SoulArmor|Magik, the Soulsword Bearer", "Elektra|Ultimate|Elektra, Returned from the Pit" },
                Bosses = new[] { "Kaecilius", "N'astirh", "Mindless Titan", "Herald of Ash", "Grim Reaper" },
                Regions = new[] { "CH0603CircusSideshowRegion", "CH0602DeepCavernRegion", "CH0209HoodsHideoutRegion" },
                PowerGroups = new[] { "MobPowers/LimboDemons", "MobPowers/FireDemons", "MobPowers/MindlessOnes", "Boss/Kaecilius" },
                MobFactions = new[] { "NGarai", "FireDemons", "MindlessOnes", "Hand" },
            },
            new BountyThemeDef
            {
                Name = "Sinister Six",
                Flavor = "Six problems. One appointment.",
                HeroCostumes = new[] { "GreenGoblin|JackOLantern|The Goblin, Jack-O'-Lantern", "Venom|Classic|Venom, the Lethal Protector", "Carnage|SpiderCarnage|Spider-Carnage", "Spiderman|Superior|The Superior Spider-Man", "Taskmaster|Udon|Taskmaster, the Hired Memory" },
                Bosses = new[] { "Green Goblin", "Doctor Octopus", "Electro", "Vulture", "Shocker", "Rhino", "Venom" },
                Regions = new[] { "CH0407NYPDRooftopRegion", "CH0410FiskTowerRegion", "UpperEastSideRegion" },
                PowerGroups = new[] { "Boss/GreenGoblin", "Boss/Electro", "Boss/Shocker", "MobPowers/SpiderSlayers" },
                MobFactions = new[] { "SpiderSlayers", "GangMembers", "Maggia" },
            },
            new BountyThemeDef
            {
                Name = "Symbiote Outbreak",
                Flavor = "It doesn't want to kill you. It wants to keep you.",
                HeroCostumes = new[] { "Spiderman|Symbiote|Spider-Man, the Black Suit", "Wolverine|Symbiote|Wolverine, the Bonded Claws", "RocketRaccoon|Symbiote|Rocket, the Infested", "Venom|AntiVenom|Anti-Venom", "Carnage|Classic|Carnage" },
                Bosses = new[] { "Venom", "BrooklynEventCloneBackInBlack", "Doctor Octopus" },
                Regions = new[] { "CH0201ShippingYardRegion", "CH0208CanneryRegion", "CH0106KPWarehouseRegion", "CH0203RhinoBargeRegion" },
                PowerGroups = new[] { "Boss/Venom", "MobPowers/SpiderClones", "MobPowers/SpiderSlayers", "MobPowers/Street" },
                MobFactions = new[] { "VenomSymbiotes", "SpiderSlayers" },
            },
            new BountyThemeDef
            {
                Name = "Shadowland",
                Flavor = "The Hand found a better man to ruin.",
                HeroCostumes = new[] { "Daredevil|Shadowland|Daredevil, Lord of the Hand", "Elektra|TVMDDS2|Elektra, the Black Sky", "Psylocke|Classic|Psylocke, the Captive Blade", "Wolverine|Ronin|Wolverine, the Masterless", "Hawkeye|Ronin|Ronin, the Nameless Bow" },
                Bosses = new[] { "Elektra", "Gorgon", "Lady Deathstrike", "Bullseye", "Kingpin", "HightownEventKirigi" },
                Regions = new[] { "CH0307HandTowerRegion", "CH0306PrincessBarRegion", "CH0101HellsKitchenRegion", "HellsKitchen01Region" },
                PowerGroups = new[] { "MobPowers/Hand", "Boss/Elektra", "Boss/Gorgon", "Boss/Bullseye", "Boss/Kingpin" },
                MobFactions = new[] { "Hand", "GangMembers" },
            },
            new BountyThemeDef
            {
                Name = "Heroes for Hire",
                Flavor = "The rate went up. So did the body count.",
                HeroCostumes = new[] { "LukeCage|HeroesForHire|Luke Cage, Rate Went Up", "IronFist|HeroesForHire|Iron Fist, the Bought Blow", "SheHulk|HeroesForHire|She-Hulk, Retained Counsel", "BlackCat|ANAD|Black Cat, the New Boss", "MoonKnight|Modern|Moon Knight, Paid in Full" },
                Bosses = new[] { "Kingpin", "Tombstone", "Black Cat", "Batroc", "Shocker" },
                Regions = new[] { "CH0101HellsKitchenRegion", "CH0103NYPDRegion", "HellsKitchen01Region", "BrooklynRegion" },
                PowerGroups = new[] { "MobPowers/Street", "MobPowers/Maggia", "Boss/Kingpin" },
                MobFactions = new[] { "GangMembers", "Maggia", "Police" },
            },
            new BountyThemeDef
            {
                Name = "Noir",
                Flavor = "Same city. No colors left in it.",
                HeroCostumes = new[] { "Spiderman|Noir|Spider-Man Noir", "Daredevil|Noir|Daredevil Noir", "Punisher|Noir|Punisher Noir", "LukeCage|Noir|Luke Cage Noir", "Cyclops|Noir|Cyclops Noir" },
                Bosses = new[] { "Kingpin", "Tombstone", "Black Cat", "Batroc", "Shocker", "Bullseye" },
                Regions = new[] { "CH0105NightclubRegion", "NightclubRegion", "HellsKitchen02RedlightRegion", "CH0408MaggiaRestaurantRegion" },
                PowerGroups = new[] { "MobPowers/Maggia", "MobPowers/Street", "Boss/Hammerhead", "Boss/Kingpin", "Boss/Shocker" },
                MobFactions = new[] { "Maggia", "GangMembers", "Police" },
            },
            new BountyThemeDef
            {
                Name = "Annihilation",
                Flavor = "Space is mostly empty. This part isn't.",
                HeroCostumes = new[] { "SilverSurfer|SilverSavage|The Silver Savage", "Nova|SABlackVortex|Nova, the Black Vortex", "Starlord|Legendary|Star-Lord, the Last Command", "RocketRaccoon|CosmicGear|Rocket, the Wave Ahead", "Angela|Marvel1602|Angela, the Outside Hunter", "MsMarvel|Binary|Binary" },
                Bosses = new[] { "Cosmic War Skrull", "Herald of Ash", "The Deceiver", "Mindless Titan" },
                Regions = new[] { "CH0802HYDRAIslandRegion", "CH0903AsgardiaInstanceRegion", "CH0905CanalRegion" },
                PowerGroups = new[] { "Boss/CosmicGatePowers", "MobPowers/Brood", "Boss/SuperSkrull", "MobPowers/Skrulls" },
                MobFactions = new[] { "Brood", "Skrulls", "ThanosMinions" },
            },
            new BountyThemeDef
            {
                Name = "Savage Land",
                Flavor = "Older things live here. They were never impressed by capes.",
                HeroCostumes = new[] { "Rogue|SavageLand|Rogue, the Wild Touch", "Wolverine|Brood|Wolverine, the Brood-Bonded", "Storm|AfricanGoddess|Storm, the Goddess of the Wild", "Beast|Astonishing|Beast, Reverted to Instinct", "KittyPryde|Classic|Shadowcat, the Buried Alive" },
                Bosses = new[] { "Sauron", "Predator X", "Lizard", "Kraven", "Man-Ape" },
                Regions = new[] { "CH0702SauronCavesRegion", "CH0703BroodCavesRegion", "CH0706MutateCavesRegion" },
                PowerGroups = new[] { "MobPowers/Brood", "MobPowers/Dinosaurs", "MobPowers/LizardAnimals", "MobPowers/SunTribe", "MobPowers/Mutates" },
                MobFactions = new[] { "Dinosaurs", "LizardAnimals", "Brood", "Mutates" },
            },
            new BountyThemeDef
            {
                Name = "Inhuman Ascension",
                Flavor = "Terrigen doesn't ask permission either.",
                HeroCostumes = new[] { "BlackBolt|ANAD|Black Bolt, the Word That Kills", "Beast|UncannyInhumans|Beast, the Terrigen Study", "HumanTorch|Inhumans|Human Torch, the Attilan Flame", "Magik|MarvelNOW|Magik, the Stepping Disc", "Nova|RRNovaPrime|Nova Prime" },
                Bosses = new[] { "Mega-Sentinel", "Starktech Sentinel", "Herald of Ash", "Kronan Arcanist" },
                Regions = new[] { "CH0903AsgardiaInstanceRegion", "CH0602DeepCavernRegion", "CH0409MoloidRegion" },
                PowerGroups = new[] { "MobPowers/Sentinels", "MobPowers/Moloids", "MobPowers/Kronan", "Boss/CosmicGatePowers" },
                MobFactions = new[] { "Sentinels", "Moloids", "Kronan" },
            },
            new BountyThemeDef
            {
                Name = "Mandarin's Rings",
                Flavor = "Ten reasons to have stayed home.",
                HeroCostumes = new[] { "Psylocke|LadyMandarin|Lady Mandarin", "IronMan|Mark1|Iron Man, the Cave Escape", "Elektra|Classic|Elektra, the Tenth Ring", "BlackWidow|ClassicWhite|Black Widow, the Ring Bearer", "WarMachine|Initiative|War Machine, the Initiative" },
                Bosses = new[] { "Mandarin", "Living Laser", "M.O.D.O.K.", "Wizard" },
                Regions = new[] { "CH0803MandarinBossRegion", "CH0801AIMWeaponFacilityRegion", "CH0802HYDRAIslandRegion" },
                PowerGroups = new[] { "Boss/Mandarin", "Boss/LivingLaser", "MobPowers/AIM", "MobPowers/IronLegionnaires" },
                MobFactions = new[] { "AIM", "Hydra" },
            },
            new BountyThemeDef
            {
                Name = "Wakanda",
                Flavor = "The throne is not a target. Say it again.",
                HeroCostumes = new[] { "BlackPanther|DoomWar|Black Panther, the Doom War", "Storm|Modern|Storm, Queen of Wakanda", "SheHulk|SGFJeans|She-Hulk, the Border Incident", "LukeCage|Modern|Luke Cage, the Hired Outsider", "Blade|Modern|Blade, the Border Hunter" },
                Bosses = new[] { "Man-Ape", "Black Panther", "Batroc", "Taskmaster", "Crossbones" },
                Regions = new[] { "WakandaP1RegionL60", "CH0409MoloidRegion" },
                PowerGroups = new[] { "Boss/ManApe", "MobPowers/Street", "MobPowers/SunTribe" },
                MobFactions = new[] { "JabariTribe", "GangMembers" },
            },
            new BountyThemeDef
            {
                Name = "Anomaly Contract",
                Flavor = "The board flagged these as low priority. The board was wrong.",
                HeroCostumes = new[] { "SquirrelGirl|Unbeatable|The Unbeatable Squirrel Girl", "Deadpool|Zen|Deadpool, Achieved Enlightenment", "HumanTorch|TwoThousandNinetyNine|Human Torch 2099", "RocketRaccoon|OfficeAttire|Rocket, Middle Management", "Hulk|MrFixIt|Mr Fixit" },
                Bosses = new[] { "Blob", "Batroc", "Shocker" },
                Regions = new[] { "CH0603CircusSideshowRegion", "CH0303WatermillRegion", "CH0409MoloidRegion" },
                PowerGroups = new[] { "MobPowers/Cows", "MobPowers/Street", "MobPowers/Maggia" },
                MobFactions = new[] { "Cliffwalkers", "FallPeople", "GangMembers" },
            },
            new BountyThemeDef
            {
                Name = "Street War",
                Flavor = "Fisk called in every marker at once.",
                HeroCostumes = new[] { "BlackCat|SpiderGwen|Black Cat, the Wrong Web", "Punisher|WarJournal|The Punisher, War Journal", "Blade|Original|Blade, the Night Shift", "Daredevil|ManWOFear|Daredevil, the Man Without Fear", "IronFist|Immortal|The Immortal Iron Fist", "LukeCage|StreetClothes|Luke Cage, Off the Clock" },
                Bosses = new[] { "Kingpin", "Tombstone", "Bullseye", "Batroc", "HoodCH2", "Shocker", "Black Cat", "Mr. Hyde" },
                Regions = new[] { "CH0410FiskTowerRegion", "FiskTowerRegion", "CH0202HoodSightingContainerRegion", "CH0209HoodsHideoutRegion" },
                PowerGroups = new[] { "MobPowers/Maggia", "MobPowers/Street", "Boss/Kingpin", "Boss/Bullseye" },
                MobFactions = new[] { "Maggia", "GangMembers", "Police", "MGH" },
            },
            new BountyThemeDef
            {
                Name = "Clone Saga",
                Flavor = "None of them think they are the copy.",
                HeroCostumes = new[] { "Spiderman|ScarletSpider|The Scarlet Spider", "Spiderman|BigTimeBlue|Spider-Man, the Big Time Copy", "Colossus|Origins|Colossus, the Second Casting", "Cyclops|AllNewXmen|Cyclops, the Displaced Original", "Wolverine|Patch|Patch, the Denied Name", "X23|InnocenceLost|X-23, Innocence Lost", "KittyPryde|ExcaliburMasked|Shadowcat, the Masked Double" },
                Bosses = new[] { "Clone Big Time Red", "Clone Big Time Green", "Clone Big Time Blue", "Clone Colossus", "Clone Cyclops", "MidtownEventCloneWolverine", "BrooklynEventCloneBackInBlack" },
                Regions = new[] { "BrooklynRegion", "UpperEastSideRegion", "CH0205ConstructionRegion", "CH0707SinisterLabRegion" },
                PowerGroups = new[] { "MobPowers/SpiderClones", "MobPowers/SpiderSlayers", "MobPowers/MrSinisterBattle", "MobPowers/Street" },
                MobFactions = new[] { "SpiderSlayers", "GangMembers", "Mutates" },
            },
        };

        /// <summary>Costume folder name for an avatar short name where the two differ.</summary>
        public static string CostumeFolderFor(string avatarShortName)
        {
            if (avatarShortName == "DoctorStrange") return "DrStrange";
            return avatarShortName;
        }
    }
}
