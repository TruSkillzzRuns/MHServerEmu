using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.Games.Loot
{
    /// <summary>
    /// Farm session tracker — records what dropped, where, and when, so a
    /// player can tell whether a farming spot is actually paying out instead
    /// of going on feel.
    ///
    /// Modelled on <see cref="Powers.DpsMeter"/>: a lock-guarded static fed
    /// from the game thread on the item-acquisition path, so web handlers can
    /// read snapshots directly without marshaling back onto the game thread.
    ///
    /// Session-scoped and in-memory by design — this answers "is this spot
    /// worth it right now", not "what have I ever looted". Nothing is written
    /// to disk, so it costs nothing when unused and disappears on restart.
    /// </summary>
    public static class FarmTracker
    {
        private static readonly object s_lock = new();
        private static readonly Dictionary<ulong, Session> s_sessions = new();

        private sealed class Session
        {
            public string PlayerName;
            public long StartMs;
            public long LastDropMs;
            public int TotalItems;

            // Keyed by rarity prototype -> count.
            public readonly Dictionary<PrototypeId, int> ByRarity = new();

            // Keyed by region prototype -> per-region tally.
            public readonly Dictionary<PrototypeId, RegionTally> ByRegion = new();

            // Most recent drops, newest last. Bounded so a long session can't
            // grow without limit.
            public readonly Queue<DropRecord> Recent = new();
        }

        private sealed class RegionTally
        {
            public int Items;
            public long FirstMs;
            public long LastMs;
            public readonly Dictionary<PrototypeId, int> ByRarity = new();
        }

        private sealed class DropRecord
        {
            public string ItemName;
            public string RarityName;
            /// <summary>Rarity ordering, so the UI can colour without knowing rarity names.</summary>
            public int RarityTier;
            public int ItemLevel;
            public string RegionName;
            public long Ms;
            /// <summary>"" | "upgrade" | "sidegrade" | "no" — from the gear alert comparison.</summary>
            public string Verdict;
            /// <summary>
            /// Icon asset name, for the client to fetch from its OWN local game
            /// files via /webapi/portrait or /webapi/texbyname. Only the name
            /// travels — no image data is stored or shipped here.
            /// </summary>
            public string IconPath;
        }

        private const int MaxRecent = 100;

        /// <summary>
        /// Sentinel tiers for item types that sit OUTSIDE the rarity ordering.
        /// Deliberately above every real rarity tier so they can never collide
        /// with one the data adds later. These are labels, not ranks — nothing
        /// should compare them against a rarity tier.
        /// </summary>
        public const int ArtifactTier = 20;
        public const int LegendaryTier = 21;

        /// <summary>
        /// Called on the game thread when a player acquires an item.
        /// </summary>
        /// <param name="itemDisplayName">
        /// The item's real in-game name. Passed in rather than derived from the
        /// prototype path: the leaf name is developer shorthand ("Unique303"),
        /// not what the player sees on the item.
        /// </param>
        public static void RecordDrop(ulong playerId, string playerName, PrototypeId itemProtoRef,
            PrototypeId rarityProtoRef, int itemLevel, PrototypeId regionProtoRef, long nowMs,
            string itemDisplayName = null, string rarityDisplayName = null, string verdict = null,
            string iconPath = null, int specialTier = 0)
        {
            if (playerId == 0 || itemProtoRef == PrototypeId.Invalid)
                return;

            lock (s_lock)
            {
                if (s_sessions.TryGetValue(playerId, out Session session) == false)
                {
                    session = new Session { PlayerName = playerName, StartMs = nowMs };
                    s_sessions[playerId] = session;
                }

                session.PlayerName = playerName;
                session.LastDropMs = nowMs;
                session.TotalItems++;

                if (rarityProtoRef != PrototypeId.Invalid)
                {
                    session.ByRarity.TryGetValue(rarityProtoRef, out int rarityCount);
                    session.ByRarity[rarityProtoRef] = rarityCount + 1;
                }

                if (regionProtoRef != PrototypeId.Invalid)
                {
                    if (session.ByRegion.TryGetValue(regionProtoRef, out RegionTally tally) == false)
                    {
                        tally = new RegionTally { FirstMs = nowMs };
                        session.ByRegion[regionProtoRef] = tally;
                    }

                    tally.Items++;
                    tally.LastMs = nowMs;

                    if (rarityProtoRef != PrototypeId.Invalid)
                    {
                        tally.ByRarity.TryGetValue(rarityProtoRef, out int c);
                        tally.ByRarity[rarityProtoRef] = c + 1;
                    }
                }

                session.Recent.Enqueue(new DropRecord
                {
                    // Fall back to the prototype leaf only when the item has no
                    // localized name, so a drop is never nameless.
                    ItemName = string.IsNullOrWhiteSpace(itemDisplayName) ? Name(itemProtoRef) : itemDisplayName,
                    RarityName = string.IsNullOrWhiteSpace(rarityDisplayName) ? Name(rarityProtoRef) : rarityDisplayName,
                    // ArtifactTier is a sentinel, not a rank: artifacts sit
                    // outside the rarity ordering entirely, so the UI colours
                    // them on their own instead of pretending they are Common.
                    RarityTier = specialTier != 0
                        ? specialTier
                        : (rarityProtoRef != PrototypeId.Invalid
                            ? (GameDatabase.GetPrototype<RarityPrototype>(rarityProtoRef)?.Tier ?? 0)
                            : 0),
                    ItemLevel = itemLevel,
                    RegionName = RegionName(regionProtoRef),
                    Ms = nowMs,
                    Verdict = verdict ?? "",
                    IconPath = iconPath ?? "",
                });

                while (session.Recent.Count > MaxRecent)
                    session.Recent.Dequeue();
            }
        }

        /// <summary>
        /// Immutable view for the web layer. Returns <see langword="null"/>
        /// when the player has no session yet.
        /// </summary>
        public static object GetSnapshot(ulong playerId, long nowMs)
        {
            lock (s_lock)
            {
                if (s_sessions.TryGetValue(playerId, out Session session) == false)
                    return null;

                long elapsedMs = Math.Max(1, nowMs - session.StartMs);
                double hours = elapsedMs / 3_600_000.0;

                // Sort the tallies themselves, then project — sorting anonymous
                // objects would mean reflecting on their properties.
                var regionTallies = new List<KeyValuePair<PrototypeId, RegionTally>>(session.ByRegion);
                regionTallies.Sort((a, b) => b.Value.Items.CompareTo(a.Value.Items));

                var regions = new List<object>();
                foreach (var kvp in regionTallies)
                {
                    RegionTally tally = kvp.Value;
                    long regionMs = Math.Max(1, tally.LastMs - tally.FirstMs);
                    double regionHours = regionMs / 3_600_000.0;

                    regions.Add(new
                    {
                        Region = RegionName(kvp.Key),
                        Items = tally.Items,
                        // Only meaningful once there's enough time to divide
                        // by; a single drop would otherwise read as a wild
                        // per-hour rate.
                        ItemsPerHour = regionMs < 60_000 ? (double?)null : Math.Round(tally.Items / regionHours, 1),
                        ByRarity = ToRarityList(tally.ByRarity),
                    });
                }

                var recent = new List<object>();
                foreach (DropRecord rec in session.Recent)
                {
                    recent.Add(new
                    {
                        rec.ItemName,
                        rec.RarityName,
                        rec.RarityTier,
                        rec.ItemLevel,
                        rec.RegionName,
                        rec.Verdict,
                        rec.IconPath,
                        AgeSeconds = Math.Max(0, (nowMs - rec.Ms) / 1000),
                    });
                }

                recent.Reverse();   // newest first for display

                return new
                {
                    Player = session.PlayerName,
                    SessionMinutes = Math.Round(elapsedMs / 60_000.0, 1),
                    TotalItems = session.TotalItems,
                    ItemsPerHour = elapsedMs < 60_000 ? (double?)null : Math.Round(session.TotalItems / hours, 1),
                    ByRarity = ToRarityList(session.ByRarity),
                    Regions = regions,
                    Recent = recent,
                };
            }
        }

        public static bool Reset(ulong playerId)
        {
            lock (s_lock)
                return s_sessions.Remove(playerId);
        }

        private static List<object> ToRarityList(Dictionary<PrototypeId, int> byRarity)
        {
            var pairs = new List<KeyValuePair<PrototypeId, int>>(byRarity);
            pairs.Sort((a, b) => b.Value.CompareTo(a.Value));

            var list = new List<object>(pairs.Count);
            foreach (var kvp in pairs)
                list.Add(new { Rarity = Name(kvp.Key), Count = kvp.Value });

            return list;
        }

        /// <summary>
        /// A region's real in-game name. The prototype leaf is developer
        /// shorthand ("DailyGAsgardINSTRegionL60"), which is not what the
        /// player sees on their map. Falls back to the leaf so a region is
        /// never nameless.
        /// </summary>
        private static string RegionName(PrototypeId regionProtoRef)
        {
            if (regionProtoRef == PrototypeId.Invalid) return "Unknown";

            var locale = Locales.LocaleManager.Instance.CurrentLocale;
            var regionProto = GameDatabase.GetPrototype<RegionPrototype>(regionProtoRef);

            if (locale != null && regionProto != null && regionProto.RegionName != LocaleStringId.Invalid)
            {
                string name = locale.GetLocaleString(regionProto.RegionName);
                if (string.IsNullOrWhiteSpace(name) == false) return name;
            }

            return Name(regionProtoRef);
        }

        private static string Name(PrototypeId protoRef)
        {
            if (protoRef == PrototypeId.Invalid) return "Unknown";

            string full = GameDatabase.GetPrototypeName(protoRef);
            if (string.IsNullOrEmpty(full)) return "Unknown";

            // Prototype paths are long; the leaf is what a player recognises.
            int slash = full.LastIndexOf('/');
            string leaf = slash >= 0 ? full[(slash + 1)..] : full;

            if (leaf.EndsWith(".prototype", StringComparison.OrdinalIgnoreCase))
                leaf = leaf[..^".prototype".Length];

            return leaf;
        }
    }
}
