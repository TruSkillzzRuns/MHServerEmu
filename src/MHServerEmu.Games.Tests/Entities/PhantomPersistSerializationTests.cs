using System.Text.Json;
using System.Text.Json.Serialization;
using MHServerEmu.DatabaseAccess.Models;

namespace MHServerEmu.Games.Tests.Entities
{
    // Regression test for a real incident (2026-08-02): NemesisEntry and
    // BountyBoardEntry are plain public-FIELD classes (no properties), but
    // System.Text.Json only serializes properties by default. Player.
    // PhantomPersist.cs's save/load sidecar silently wrote "{}" per entry
    // and read back default-constructed garbage (HeroRef=0, Rank=0) until
    // IncludeFields=true was added to its JsonSerializerOptions. The bug
    // never threw — it just quietly lost data — which is why it went
    // unnoticed until a live repro (a Bounty Board reroll to all-zero
    // slots) surfaced it.
    //
    // These options intentionally mirror Player.PhantomPersist.cs's
    // s_persistJsonOptions. If that ever changes, update this to match —
    // don't just delete the test, the whole point is catching a future
    // regression of the same shape (a new field-only DTO added to the
    // persist blob without IncludeFields, or IncludeFields getting dropped
    // in a refactor).
    public class PhantomPersistSerializationTests
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            IncludeFields = true,
        };

        [Fact]
        public void NemesisEntry_RoundTrip_PreservesFieldValues()
        {
            var original = new NemesisEntry
            {
                HeroRef = 0x1234567890ABCDEF,
                IsBoss = true,
                Rank = 4,
                Kills = 7,
                RevengeKills = 2,
                Defeated = true,
                LastKillerName = "TestPhantom",
                LastKillMs = 123456789,
                EscapeCount = 1,
                MercyCount = 0,
            };

            string json = JsonSerializer.Serialize(original, Options);
            var roundTripped = JsonSerializer.Deserialize<NemesisEntry>(json, Options);

            Assert.NotNull(roundTripped);
            Assert.Equal(original.HeroRef, roundTripped.HeroRef);
            Assert.Equal(original.IsBoss, roundTripped.IsBoss);
            Assert.Equal(original.Rank, roundTripped.Rank);
            Assert.Equal(original.Kills, roundTripped.Kills);
            Assert.Equal(original.RevengeKills, roundTripped.RevengeKills);
            Assert.Equal(original.Defeated, roundTripped.Defeated);
            Assert.Equal(original.LastKillerName, roundTripped.LastKillerName);
            Assert.Equal(original.LastKillMs, roundTripped.LastKillMs);
            Assert.Equal(original.EscapeCount, roundTripped.EscapeCount);
            Assert.Equal(original.MercyCount, roundTripped.MercyCount);
        }

        [Fact]
        public void BountyBoardEntry_RoundTrip_PreservesFieldValues()
        {
            var original = new BountyBoardEntry
            {
                HeroRef = 0xFEDCBA0987654321,
                IsBoss = false,
                Rank = 3,
                LossCount = 2,
                Defeated = true,
                Fled = false,
                RewardCollected = true,
                LastKillerName = "Deadpool",
            };

            string json = JsonSerializer.Serialize(original, Options);
            var roundTripped = JsonSerializer.Deserialize<BountyBoardEntry>(json, Options);

            Assert.NotNull(roundTripped);
            Assert.Equal(original.HeroRef, roundTripped.HeroRef);
            Assert.Equal(original.IsBoss, roundTripped.IsBoss);
            Assert.Equal(original.Rank, roundTripped.Rank);
            Assert.Equal(original.LossCount, roundTripped.LossCount);
            Assert.Equal(original.Defeated, roundTripped.Defeated);
            Assert.Equal(original.Fled, roundTripped.Fled);
            Assert.Equal(original.RewardCollected, roundTripped.RewardCollected);
            Assert.Equal(original.LastKillerName, roundTripped.LastKillerName);
        }

        // The actual failure mode wasn't an exception, it was silent data
        // loss — this is the most direct guard against that shape of bug:
        // without IncludeFields, HeroRef/Rank come back as their zero
        // defaults instead of the test throwing, exactly like the live
        // incident (a board with real bounties came back as "0x0"/Rank 0
        // on every slot, no error anywhere in the log).
        [Fact]
        public void BountyBoardEntry_NonDefaultValues_SurviveRoundTrip()
        {
            var original = new BountyBoardEntry { HeroRef = 0xABCDEF, Rank = 5 };

            string json = JsonSerializer.Serialize(original, Options);
            var roundTripped = JsonSerializer.Deserialize<BountyBoardEntry>(json, Options);

            Assert.NotEqual(0ul, roundTripped.HeroRef);
            Assert.NotEqual(0, roundTripped.Rank);
        }
    }
}
