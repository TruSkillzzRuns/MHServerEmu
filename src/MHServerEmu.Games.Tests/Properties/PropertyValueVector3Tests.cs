using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.Properties;

namespace MHServerEmu.Games.Tests.Properties
{
    /// <summary>
    /// Round-trip tests for Vector3 stored in a PropertyValue (PropertyDataType
    /// Int21Vector3 — three signed 21-bit integers packed into one long).
    ///
    /// Regression for a live incident (2026-08-13): players teleporting back
    /// out of a town hung on an infinite loading screen. The bodyslider return
    /// position is stored in PropertyEnum.BodySliderRegionPos, and negative
    /// coordinates came back as garbage — an observed (-2031616, -2031616, 55)
    /// against a region only 1792 units across. 2031616 is 0x1F0000, i.e. bits
    /// 16-20, which is exactly the range the old sign handling corrupted:
    ///
    ///   - it used 0x10000 (bit 16) as the sign bit of a 21-bit field, where
    ///     the sign bit is 0x100000 (bit 20)
    ///   - the Z branch OR'd into x instead of z, and x had already been
    ///     shifted left 42 by then
    ///   - decode OR'd 0xFFE00000 onto the UNMASKED shifted value
    ///
    /// Any coordinate on a negative axis - i.e. most of any region, since
    /// regions are centred on the origin - could come back wrong.
    /// </summary>
    public class PropertyValueVector3Tests
    {
        private static Vector3 RoundTrip(Vector3 v)
        {
            PropertyValue value = new(v);
            return value.ToVector3();
        }

        [Fact]
        public void RoundTrip_Origin()
        {
            Assert.Equal(Vector3.Zero, RoundTrip(Vector3.Zero));
        }

        [Fact]
        public void RoundTrip_AllPositive()
        {
            Assert.Equal(new Vector3(100f, 250f, 55f), RoundTrip(new Vector3(100f, 250f, 55f)));
        }

        [Theory]
        // The live failure: a negative X/Y with a small positive Z.
        [InlineData(-31f, -31f, 55f)]
        [InlineData(-1f, -1f, -1f)]
        [InlineData(-1792f, -1792f, -1792f)]     // a region corner
        [InlineData(-1000f, 1000f, 0f)]          // mixed signs
        [InlineData(1000f, -1000f, 0f)]
        [InlineData(0f, 0f, -55f)]               // negative Z only — the branch that wrote to x
        public void RoundTrip_PreservesNegatives(float x, float y, float z)
        {
            Vector3 original = new(x, y, z);
            Assert.Equal(original, RoundTrip(original));
        }

        [Fact]
        public void RoundTrip_FieldsDoNotBleedIntoEachOther()
        {
            // A negative Z used to corrupt X, because the encoder OR'd the Z
            // sign flag into the x variable.
            Vector3 original = new(1234f, 0f, -1f);
            Vector3 result = RoundTrip(original);

            Assert.Equal(1234f, result.X);
            Assert.Equal(0f, result.Y);
            Assert.Equal(-1f, result.Z);
        }

        [Fact]
        public void RoundTrip_SignedRangeLimits()
        {
            // 21 bits signed: -1048576 .. 1048575. Region coordinates sit well
            // inside this, but the extremes must not wrap.
            Assert.Equal(new Vector3(1048575f, 1048575f, 1048575f),
                         RoundTrip(new Vector3(1048575f, 1048575f, 1048575f)));

            Assert.Equal(new Vector3(-1048576f, -1048576f, -1048576f),
                         RoundTrip(new Vector3(-1048576f, -1048576f, -1048576f)));
        }

        [Fact]
        public void RoundTrip_NeverProducesWildlyOutOfRangeValues()
        {
            // The shape of the live bug: a modest coordinate coming back as
            // hundreds of thousands of units away.
            for (float c = -2000f; c <= 2000f; c += 250f)
            {
                Vector3 result = RoundTrip(new Vector3(c, c, 55f));

                Assert.True(MathF.Abs(result.X) <= 2000f, $"X blew up: {c} -> {result.X}");
                Assert.True(MathF.Abs(result.Y) <= 2000f, $"Y blew up: {c} -> {result.Y}");
                Assert.Equal(55f, result.Z);
            }
        }
    }
}
