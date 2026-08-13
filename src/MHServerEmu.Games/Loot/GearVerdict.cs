using MHServerEmu.Games.Properties;

namespace MHServerEmu.Games.Loot
{
    /// <summary>
    /// The decision core of the Gear Upgrade Alert: given two items' stats and
    /// their rarity/level, is the drop an upgrade?
    ///
    /// Deliberately separate from <see cref="Entities.Player"/> and free of any
    /// game state — it takes plain numbers and dictionaries. The verdict logic
    /// got four separate things wrong in live play (a lv1 artifact beating a
    /// lv63, an armour drop judged against boots, every roll landing in a
    /// useless middle bucket, and prototype-granted stats reading as an empty
    /// item), and none of it was testable while it lived inside a method that
    /// needed an Avatar, an EntityManager and a loaded GameDatabase.
    ///
    /// Player.GearUpgradeAlert.cs still does the game-state work — resolving the
    /// slot, finding the equipped item, accumulating affixes — then calls in
    /// here for the actual decision.
    /// </summary>
    public static class GearVerdict
    {
        /// <summary>
        /// Result of comparing two items stat by stat.
        /// </summary>
        public readonly struct Comparison
        {
            /// <summary>Stats where the new item is higher.</summary>
            public readonly int Better;

            /// <summary>Stats where the new item is lower.</summary>
            public readonly int Worse;

            /// <summary>
            /// Both items exposed at least one stat, so the counts above mean
            /// something.
            ///
            /// False when either side accumulated nothing. That is NOT the same
            /// as "zero stats": items whose power is built into their prototype
            /// rather than rolled as affixes accumulate nothing, and treating
            /// that as a real zero is how a lv1 artifact with one rolled affix
            /// was reported as beating a lv63 artifact on every stat.
            /// </summary>
            public readonly bool Comparable;

            public Comparison(int better, int worse, bool comparable)
            {
                Better = better;
                Worse = worse;
                Comparable = comparable;
            }
        }

        public enum Kind
        {
            /// <summary>Not better. There is no middle verdict by design.</summary>
            NotBetter,

            /// <summary>Wins on the stat comparison.</summary>
            UpgradeByStats,

            /// <summary>
            /// Stats couldn't separate them, but it outranks on rarity/level.
            /// </summary>
            UpgradeByRank,
        }

        /// <summary>
        /// Compares two items over the UNION of stats either one grants, so
        /// nothing is missed by appearing on only one side (absent counts as 0
        /// for that item).
        ///
        /// Weight-free on purpose: no attempt is made to decide that N damage
        /// is worth M health, because that depends on the build and any
        /// weighting would be a guess dressed up as a number.
        ///
        /// Known limit: every property is treated as higher-is-better, which
        /// holds for the beneficial affixes items actually roll but would
        /// misread a penalty affix.
        /// </summary>
        public static Comparison CompareStats(
            IReadOnlyDictionary<PropertyId, float> newStats,
            IReadOnlyDictionary<PropertyId, float> oldStats)
        {
            if (newStats == null || oldStats == null)
                return new Comparison(0, 0, false);

            int better = 0;
            int worse = 0;

            HashSet<PropertyId> allKeys = new(newStats.Keys);
            allKeys.UnionWith(oldStats.Keys);

            foreach (PropertyId id in allKeys)
            {
                newStats.TryGetValue(id, out float newValue);
                oldStats.TryGetValue(id, out float oldValue);

                // Relative epsilon — percentage affixes are tiny absolute
                // numbers while flat stats are large, so a fixed threshold
                // would either ignore real percentage gains or treat float
                // noise on big numbers as a difference.
                float scale = Math.Max(Math.Abs(newValue), Math.Abs(oldValue));
                float epsilon = Math.Max(0.0001f, scale * 0.001f);

                if (newValue > oldValue + epsilon) better++;
                else if (newValue < oldValue - epsilon) worse++;
            }

            bool comparable = newStats.Count > 0 && oldStats.Count > 0;

            return new Comparison(better, worse, comparable);
        }

        /// <summary>
        /// The verdict. Binary by design: an item either replaces what you are
        /// wearing or it does not. Requiring a clean sweep (better on something,
        /// worse on nothing) put almost every real roll into a middle bucket,
        /// which describes the roll instead of deciding it.
        ///
        /// Two rules:
        ///   1. Stats usable  -> more stats up than down wins.
        ///   2. Stats unusable (no data, or identical) -> fall back to
        ///      rarity tier, then item level.
        ///
        /// Honest limit: this COUNTS stats, it cannot WEIGH them. A large gain
        /// on one stat against small losses on two reads as NotBetter. Whether
        /// that trade is good is build-dependent and not knowable here.
        /// </summary>
        public static Kind Decide(Comparison cmp, int newTier, int oldTier, int newLevel, int oldLevel)
        {
            bool statsUsable = cmp.Comparable && (cmp.Better > 0 || cmp.Worse > 0);

            if (statsUsable)
                return cmp.Better > cmp.Worse ? Kind.UpgradeByStats : Kind.NotBetter;

            return OutranksByRank(newTier, oldTier, newLevel, oldLevel)
                ? Kind.UpgradeByRank
                : Kind.NotBetter;
        }

        /// <summary>
        /// Rarity tier first, then item level as the tie-break. Tier comes from
        /// RarityPrototype.Tier, derived by walking the DowngradeTo chain, so
        /// the ordering is the data's own and survives rarities being patched.
        /// </summary>
        public static bool OutranksByRank(int newTier, int oldTier, int newLevel, int oldLevel)
        {
            if (newTier != oldTier) return newTier > oldTier;
            return newLevel > oldLevel;
        }
    }
}
