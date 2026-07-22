using System;
using System.Collections.Generic;
using System.IO;
using MHServerEmu.Core.Collections;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Loot;

namespace MHServerEmu.Games.Entities.Avatars
{
    /// <summary>
    /// Loads the scraped best-in-slot gear table (Data/Game/PhantomHeroes/
    /// PhantomBiSGear.json) and resolves it to a per-avatar map of
    /// EquipmentInvUISlot -> item PrototypeId. Used by the nemesis loot
    /// system so rank-5 level-60 nemeses wear and drop the community BiS set
    /// for their hero. Names were pre-matched to prototype refs during the
    /// scrape, so this loader just parses hex refs and resolves the hero leaf
    /// name to its AvatarPrototype ref.
    /// </summary>
    public static class PhantomBiSData
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        // avatarRef -> (uiSlot -> itemRef). Loaded from the curated BiS
        // table at Data/Game/PhantomHeroes/PhantomBiSGear.json — a mapping of
        // community build recommendations from itembase.mhbugle.com (the
        // Marvel Heroes Omega "Item Base", by AlexBond), used with permission.
        // See NOTICE.md alongside the JSON for full attribution. If a hero
        // isn't in the file, we synthesize a loadout at runtime from the
        // loaded game data (see GenerateLoadout) — so this feature works with
        // or without the override file.
        private static Dictionary<PrototypeId, Dictionary<EquipmentInvUISlot, PrototypeId>> s_byAvatar;
        // Runtime-generated loadouts, cached per avatar so we only score the
        // item pools once per hero.
        private static readonly Dictionary<PrototypeId, Dictionary<EquipmentInvUISlot, PrototypeId>> s_generated = new();
        private static readonly object s_lock = new();
        private static readonly object s_genLock = new();

        // The equip slots we generate/serve loadouts for.
        private static readonly EquipmentInvUISlot[] BiSSlots =
        {
            EquipmentInvUISlot.Artifact01, EquipmentInvUISlot.Artifact02,
            EquipmentInvUISlot.Artifact03, EquipmentInvUISlot.Artifact04,
            EquipmentInvUISlot.Medal, EquipmentInvUISlot.Relic, EquipmentInvUISlot.UruForged,
            EquipmentInvUISlot.Legendary, EquipmentInvUISlot.Ring, EquipmentInvUISlot.Insignia,
            EquipmentInvUISlot.Gear01, EquipmentInvUISlot.Gear02, EquipmentInvUISlot.Gear03,
            EquipmentInvUISlot.Gear04, EquipmentInvUISlot.Gear05,
        };

        private static readonly Dictionary<string, EquipmentInvUISlot> SlotNameToUI = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Artifact1"] = EquipmentInvUISlot.Artifact01,
            ["Artifact2"] = EquipmentInvUISlot.Artifact02,
            ["Artifact3"] = EquipmentInvUISlot.Artifact03,
            ["Artifact4"] = EquipmentInvUISlot.Artifact04,
            ["Medal"]     = EquipmentInvUISlot.Medal,
            ["Relic"]     = EquipmentInvUISlot.Relic,
            ["UruForged"] = EquipmentInvUISlot.UruForged,
            ["Legendary"] = EquipmentInvUISlot.Legendary,
            ["Ring"]      = EquipmentInvUISlot.Ring,
            ["Insignia"]  = EquipmentInvUISlot.Insignia,
            ["Gear1"]     = EquipmentInvUISlot.Gear01,
            ["Gear2"]     = EquipmentInvUISlot.Gear02,
            ["Gear3"]     = EquipmentInvUISlot.Gear03,
            ["Gear4"]     = EquipmentInvUISlot.Gear04,
            ["Gear5"]     = EquipmentInvUISlot.Gear05,
        };

        // JSON shape: { "<AvatarLeaf>": { "slug":..., "display":..., "url":...,
        //   "slots": { "Artifact1": { "ref":"0x...", "name":..., "cat":... }, ... } } }
        private sealed class HeroEntry
        {
            public Dictionary<string, SlotEntry> slots { get; set; }
        }
        private sealed class SlotEntry
        {
            public string @ref { get; set; }
            public string name { get; set; }
        }

        public static void EnsureLoaded()
        {
            if (s_byAvatar != null) return;
            lock (s_lock)
            {
                if (s_byAvatar != null) return;
                s_byAvatar = Load();
            }
        }

        private static Dictionary<PrototypeId, Dictionary<EquipmentInvUISlot, PrototypeId>> Load()
        {
            var result = new Dictionary<PrototypeId, Dictionary<EquipmentInvUISlot, PrototypeId>>();
            string path = Path.Combine(FileHelper.DataDirectory, "Game", "PhantomHeroes", "PhantomBiSGear.json");
            if (File.Exists(path) == false)
            {
                Logger.Warn($"[PhantomBiS] data file not found: {path} — nemesis BiS loot disabled");
                return result;
            }

            Dictionary<string, HeroEntry> raw;
            try
            {
                raw = FileHelper.DeserializeJson<Dictionary<string, HeroEntry>>(path);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[PhantomBiS] parse failed: {ex.Message}");
                return result;
            }
            if (raw == null) return result;

            // Build avatar leaf -> ref index once.
            var leafToAvatarRef = new Dictionary<string, PrototypeId>(StringComparer.OrdinalIgnoreCase);
            foreach (PrototypeId avatarRef in DataDirectory.Instance
                .IteratePrototypesInHierarchy<AvatarPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                string name = GameDatabase.GetPrototypeName(avatarRef);
                if (string.IsNullOrEmpty(name)) continue;
                int slash = name.LastIndexOf('/');
                string leaf = slash >= 0 ? name[(slash + 1)..] : name;
                if (leaf.EndsWith(".prototype", StringComparison.OrdinalIgnoreCase))
                    leaf = leaf[..^".prototype".Length];
                leafToAvatarRef[leaf] = avatarRef;
            }

            int heroesResolved = 0, slotsResolved = 0, slotsMissed = 0;
            foreach (var kvp in raw)
            {
                string leaf = kvp.Key;
                if (leaf.StartsWith("_", StringComparison.Ordinal)) continue; // metadata keys (e.g. "_credit"), not a hero leaf
                if (leafToAvatarRef.TryGetValue(leaf, out PrototypeId avatarRef) == false)
                {
                    Logger.Warn($"[PhantomBiS] no avatar prototype for leaf '{leaf}'");
                    continue;
                }
                if (kvp.Value?.slots == null) continue;

                var slotMap = new Dictionary<EquipmentInvUISlot, PrototypeId>();
                foreach (var slotKvp in kvp.Value.slots)
                {
                    if (SlotNameToUI.TryGetValue(slotKvp.Key, out EquipmentInvUISlot uiSlot) == false)
                        continue;
                    PrototypeId itemRef = ParseHexRef(slotKvp.Value?.@ref);
                    if (itemRef == PrototypeId.Invalid || itemRef.As<ItemPrototype>() == null)
                    {
                        slotsMissed++;
                        continue;
                    }
                    slotMap[uiSlot] = itemRef;
                    slotsResolved++;
                }
                if (slotMap.Count > 0)
                {
                    result[avatarRef] = slotMap;
                    heroesResolved++;
                }
            }

            Logger.Info($"[PhantomBiS] loaded BiS gear for {heroesResolved} heroes ({slotsResolved} slots resolved, {slotsMissed} skipped)");
            return result;
        }

        private static PrototypeId ParseHexRef(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return PrototypeId.Invalid;
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
            return ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out ulong val)
                ? (PrototypeId)val : PrototypeId.Invalid;
        }

        /// <summary>
        /// Resolve a BiS loadout for the avatar. Prefers the optional private
        /// data file (if it has an entry); otherwise generates a clean loadout
        /// from the loaded game data. <paramref name="slots"/> maps each equip
        /// slot to the chosen item ref. Returns false only if neither source
        /// yields anything (e.g. no game data).
        /// </summary>
        public static bool TryGetLoadout(PrototypeId avatarRef, Game game, out Dictionary<EquipmentInvUISlot, PrototypeId> slots)
        {
            EnsureLoaded();
            if (s_byAvatar.TryGetValue(avatarRef, out slots))
                return true;

            slots = GetOrGenerate(avatarRef, game);
            return slots != null && slots.Count > 0;
        }

        private static Dictionary<EquipmentInvUISlot, PrototypeId> GetOrGenerate(PrototypeId avatarRef, Game game)
        {
            lock (s_genLock)
            {
                if (s_generated.TryGetValue(avatarRef, out var cached)) return cached;
                var gen = GenerateLoadout(avatarRef, game);
                s_generated[avatarRef] = gen; // cache even if empty so we don't rescore
                return gen;
            }
        }

        /// <summary>
        /// Build a best-in-slot loadout for a hero purely from the loaded game
        /// data — no external data. For each equip slot we take the hero's own
        /// candidate item pool (the same pool the loot roller uses) and pick
        /// the highest-value item: hero-signature Uniques win outright (they
        /// are the intended per-slot cores), with item level as the tiebreak.
        /// This yields a strong, defensible loadout of the hero's own top-tier
        /// gear without referencing any community data.
        /// </summary>
        private static Dictionary<EquipmentInvUISlot, PrototypeId> GenerateLoadout(PrototypeId avatarRef, Game game)
        {
            var result = new Dictionary<EquipmentInvUISlot, PrototypeId>();
            if (game == null) return result;
            AvatarPrototype avatarProto = avatarRef.As<AvatarPrototype>();
            if (avatarProto?.EquipmentInventories == null) return result;

            PrototypeId rarityUniqueRef = GameDatabase.LootGlobalsPrototype.RarityUnique;
            var picker = new Picker<Prototype>(game.Random);

            foreach (EquipmentInvUISlot uiSlot in BiSSlots)
            {
                picker.Clear();
                if (LootUtilities.BuildInventoryLootPicker(picker, avatarRef, uiSlot) == false)
                    continue;

                PrototypeId best = PrototypeId.Invalid;
                long bestScore = long.MinValue;
                while (picker.Empty() == false)
                {
                    if (picker.PickRemove(out Prototype proto) == false || proto == null) break;
                    if (proto is not ItemPrototype itemProto) continue;

                    string path = GameDatabase.GetPrototypeName(itemProto.DataRef);
                    bool isUnique = IsUniqueItem(itemProto, path, rarityUniqueRef);
                    // Signature Uniques dominate; a stable ref tiebreak orders
                    // the rest deterministically (same pick every run).
                    long score = isUnique ? 1_000_000L : 0L;
                    if (score > bestScore || (score == bestScore && (best == PrototypeId.Invalid || (ulong)itemProto.DataRef < (ulong)best)))
                    {
                        bestScore = score;
                        best = itemProto.DataRef;
                    }
                }

                if (best != PrototypeId.Invalid)
                    result[uiSlot] = best;
            }

            Logger.Info($"[PhantomBiS] generated loadout for '{avatarRef.GetName()}' ({result.Count} slots, from game data)");
            return result;
        }

        // Same unique detection the item catalog uses: items in the
        // .../UniquePrototypes/... tree, or whose loot restrictions pin rarity
        // to the engine's RarityUnique.
        private static bool IsUniqueItem(ItemPrototype itemProto, string path, PrototypeId rarityUniqueRef)
        {
            if (path != null && path.IndexOf("/UniquePrototypes/", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (rarityUniqueRef == PrototypeId.Invalid || itemProto.LootDropRestrictions == null) return false;
            foreach (DropRestrictionPrototype restriction in itemProto.LootDropRestrictions)
            {
                if (restriction is OutputRarityPrototype outputRarity && outputRarity.Value == rarityUniqueRef)
                    return true;
                if (restriction is RarityRestrictionPrototype rr && rr.AllowedRarities is { Length: > 0 })
                {
                    bool allUnique = true;
                    foreach (PrototypeId allowed in rr.AllowedRarities)
                        if (allowed != rarityUniqueRef) { allUnique = false; break; }
                    if (allUnique) return true;
                }
            }
            return false;
        }
    }
}
