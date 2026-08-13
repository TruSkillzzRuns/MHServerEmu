using MHServerEmu.Games.Loot;
using MHServerEmu.Games.Properties;

namespace MHServerEmu.Games.Tests.Loot
{
    /// <summary>
    /// Regression tests for the Gear Upgrade Alert's decision core.
    ///
    /// Every case here is a bug that reached live play (2026-08-11/12) and was
    /// only caught by a player noticing a wrong verdict on screen. They are
    /// written against GearVerdict rather than Player.CheckGearUpgrade because
    /// the latter needs an Avatar, an EntityManager and a loaded GameDatabase —
    /// which is precisely why none of this was covered when it broke.
    ///
    /// The stat dictionaries stand in for what Item.AccumulateAffixStats
    /// produces. An EMPTY dictionary means "this item exposed no stats", which
    /// is not the same as "all its stats are zero" — see Comparable.
    /// </summary>
    public class GearVerdictTests
    {
        // Arbitrary distinct property ids. The verdict logic never interprets
        // them, it only matches keys across the two sides.
        private static PropertyId Stat(int n) => new((PropertyEnum)n);

        private static Dictionary<PropertyId, float> Stats(params (int Id, float Value)[] entries)
        {
            Dictionary<PropertyId, float> dict = new();
            foreach ((int id, float value) in entries)
                dict[Stat(id)] = value;
            return dict;
        }

        // ------------------------------------------------------------------
        // Comparison
        // ------------------------------------------------------------------

        [Fact]
        public void CompareStats_CountsBothDirections()
        {
            var cmp = GearVerdict.CompareStats(
                Stats((1, 100f), (2, 10f)),
                Stats((1, 50f), (2, 20f)));

            Assert.Equal(1, cmp.Better);
            Assert.Equal(1, cmp.Worse);
            Assert.True(cmp.Comparable);
        }

        [Fact]
        public void CompareStats_StatOnOnlyOneSide_CountsAsZeroForTheOther()
        {
            // The equipped item grants a stat the drop doesn't have at all.
            // That must count against the drop, not be ignored.
            var cmp = GearVerdict.CompareStats(
                Stats((1, 100f)),
                Stats((1, 50f), (2, 25f)));

            Assert.Equal(1, cmp.Better);
            Assert.Equal(1, cmp.Worse);
        }

        [Fact]
        public void CompareStats_EmptySide_IsNotComparable()
        {
            // Items whose power is built into their prototype accumulate
            // nothing. Treating that as a real zero is the lv1-artifact bug.
            var cmp = GearVerdict.CompareStats(Stats((1, 100f)), Stats());

            Assert.False(cmp.Comparable);
        }

        [Fact]
        public void CompareStats_IdenticalStats_AreComparableButTied()
        {
            var cmp = GearVerdict.CompareStats(Stats((1, 100f)), Stats((1, 100f)));

            Assert.True(cmp.Comparable);
            Assert.Equal(0, cmp.Better);
            Assert.Equal(0, cmp.Worse);
        }

        [Fact]
        public void CompareStats_TinyPercentageGain_IsNotLostToEpsilon()
        {
            // Percentage affixes are small absolute numbers; a fixed epsilon
            // would swallow a real gain here.
            var cmp = GearVerdict.CompareStats(Stats((1, 0.08f)), Stats((1, 0.05f)));

            Assert.Equal(1, cmp.Better);
            Assert.Equal(0, cmp.Worse);
        }

        [Fact]
        public void CompareStats_FloatNoiseOnLargeStat_IsNotADifference()
        {
            var cmp = GearVerdict.CompareStats(Stats((1, 100000f)), Stats((1, 100000.5f)));

            Assert.Equal(0, cmp.Better);
            Assert.Equal(0, cmp.Worse);
        }

        // ------------------------------------------------------------------
        // Verdict — the four live bugs
        // ------------------------------------------------------------------

        [Fact]
        public void Decide_Lv1ItemAgainstLv63WithNoAccumulatedStats_IsNotAnUpgrade()
        {
            // LIVE BUG: "Midtown Manhattan Patrol Visual - Silver (lv1)" was
            // reported as an upgrade over "The Doomsaw" (lv63). The Doomsaw's
            // stats are prototype-granted, so it accumulated nothing and read
            // as an empty item that any single affix beat.
            var cmp = GearVerdict.CompareStats(Stats((1, 5f)), Stats());

            var verdict = GearVerdict.Decide(cmp, newTier: 6, oldTier: 6, newLevel: 1, oldLevel: 63);

            Assert.Equal(GearVerdict.Kind.NotBetter, verdict);
        }

        [Fact]
        public void Decide_BetterOnOneWorseOnNine_IsNotAnUpgrade()
        {
            // LIVE BUG: a Battle Armor drop was called an upgrade over
            // "Portal's Darkhawk Armor", which beat it on nine stats. The real
            // cause was comparing against the wrong slot, but the verdict rule
            // must reject this pairing on its own merits too.
            var cmp = new GearVerdict.Comparison(better: 1, worse: 9, comparable: true);

            var verdict = GearVerdict.Decide(cmp, newTier: 5, oldTier: 6, newLevel: 63, oldLevel: 69);

            Assert.Equal(GearVerdict.Kind.NotBetter, verdict);
        }

        [Fact]
        public void Decide_NetStatWin_IsAnUpgrade()
        {
            // LIVE BUG: requiring a clean sweep (worse == 0) meant almost every
            // real roll landed in a "sidegrade" bucket, which describes a roll
            // instead of deciding it. A net win is an upgrade.
            var cmp = new GearVerdict.Comparison(better: 3, worse: 2, comparable: true);

            var verdict = GearVerdict.Decide(cmp, newTier: 5, oldTier: 5, newLevel: 60, oldLevel: 60);

            Assert.Equal(GearVerdict.Kind.UpgradeByStats, verdict);
        }

        [Fact]
        public void Decide_EqualStatCounts_IsNotAnUpgrade()
        {
            // Ties go to the incumbent — swapping gear for no net gain is noise.
            var cmp = new GearVerdict.Comparison(better: 2, worse: 2, comparable: true);

            Assert.Equal(GearVerdict.Kind.NotBetter,
                GearVerdict.Decide(cmp, 5, 5, 60, 60));
        }

        [Fact]
        public void Decide_NoUsableStats_FallsBackToRank()
        {
            var cmp = new GearVerdict.Comparison(better: 0, worse: 0, comparable: false);

            Assert.Equal(GearVerdict.Kind.UpgradeByRank,
                GearVerdict.Decide(cmp, newTier: 6, oldTier: 5, newLevel: 60, oldLevel: 60));

            Assert.Equal(GearVerdict.Kind.NotBetter,
                GearVerdict.Decide(cmp, newTier: 4, oldTier: 5, newLevel: 60, oldLevel: 60));
        }

        [Fact]
        public void Decide_IdenticalStats_FallsBackToRank()
        {
            // Comparable, but the counts can't separate them.
            var cmp = new GearVerdict.Comparison(better: 0, worse: 0, comparable: true);

            Assert.Equal(GearVerdict.Kind.UpgradeByRank,
                GearVerdict.Decide(cmp, newTier: 5, oldTier: 5, newLevel: 70, oldLevel: 60));
        }

        // ------------------------------------------------------------------
        // Rank ordering
        // ------------------------------------------------------------------

        [Fact]
        public void OutranksByRank_TierBeatsLevel()
        {
            // A higher tier wins even at a much lower level...
            Assert.True(GearVerdict.OutranksByRank(newTier: 6, oldTier: 5, newLevel: 1, oldLevel: 60));

            // ...and a lower tier loses even at a much higher level.
            Assert.False(GearVerdict.OutranksByRank(newTier: 4, oldTier: 5, newLevel: 99, oldLevel: 1));
        }

        [Fact]
        public void OutranksByRank_SameTier_UsesLevel()
        {
            Assert.True(GearVerdict.OutranksByRank(5, 5, 61, 60));
            Assert.False(GearVerdict.OutranksByRank(5, 5, 60, 60));
            Assert.False(GearVerdict.OutranksByRank(5, 5, 59, 60));
        }
    }
}
