// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Sim;

/// <summary>
/// A small, fast, fully deterministic PRNG (xorshift64*) used in place of
/// <see cref="System.Random"/> so simulator determinism does not depend on
/// the BCL's RNG implementation staying stable across runtimes. Never shared
/// across threads: each <see cref="SkirmishSimulation"/> owns one instance.
/// </summary>
public sealed class Xorshift
{
    private ulong state;

    public Xorshift(int seed)
    {
        // Avoid the all-zero state, which is a fixed point for xorshift.
        state = unchecked((ulong)seed) ^ 0x9E3779B97F4A7C15UL;
        if (state == 0) state = 0x9E3779B97F4A7C15UL;
    }

    public ulong NextUInt64()
    {
        ulong x = state;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        state = x;
        return x * 0x2545F4914F6CDD1DUL;
    }

    /// <summary>Uniform double in [0, 1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Uniform integer in [0, exclusiveMax).</summary>
    public int NextInt(int exclusiveMax)
    {
        if (exclusiveMax <= 0) throw new ArgumentOutOfRangeException(nameof(exclusiveMax));
        return (int)(NextDouble() * exclusiveMax);
    }
}
