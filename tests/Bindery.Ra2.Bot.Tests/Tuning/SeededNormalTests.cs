// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Tuning;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Tuning;

/// <summary>
/// The part of the tuner's generator that is bit-exact on every platform: the SplitMix64 integer stream, checked
/// against values computed independently of .NET. (The normals go through the platform's Log/Sin/Cos and are
/// reproducible only per platform; see <see cref="SeededNormal"/>.)
/// </summary>
public sealed class SeededNormalTests
{
    [Fact]
    public void The_integer_stream_is_the_reference_splitmix64()
    {
        SeededNormal rng = new(42);

        Assert.Equal(13679457532755275413UL, rng.NextUInt64());
        Assert.Equal(2949826092126892291UL, rng.NextUInt64());
        Assert.Equal(5139283748462763858UL, rng.NextUInt64());
    }

    [Fact]
    public void Uniforms_are_open_on_both_ends_and_normals_are_finite()
    {
        SeededNormal rng = new(7);
        for (int i = 0; i < 10_000; i++)
        {
            double u = rng.NextUnit();
            Assert.True(u > 0 && u < 1);
            Assert.True(double.IsFinite(rng.NextGaussian()));
        }
    }
}
