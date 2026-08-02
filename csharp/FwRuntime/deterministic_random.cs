using System.Security.Cryptography;

namespace Fw.Rt.Randomness;

public readonly record struct RandomStreamState(int Seed, long Step);

public sealed class DeterministicRandomStream
{
    private const ulong GoldenGamma = 0x9E3779B97F4A7C15UL;

    public DeterministicRandomStream(int seed, long step = 0)
    {
        Seed = NormalizeSeed(seed);
        Step = Math.Max(0, step);
    }

    public int Seed { get; }
    public long Step { get; private set; }
    public RandomStreamState State => new(Seed, Step);

    public static int CreateRuntimeSeed()
    {
        return RandomNumberGenerator.GetInt32(1, int.MaxValue);
    }

    public int Next(int maxExclusive)
    {
        if (maxExclusive <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive));
        }

        var bound = (uint)maxExclusive;
        var threshold = unchecked(0u - bound) % bound;
        uint value;
        do
        {
            value = NextUInt32();
        }
        while (value < threshold);

        return (int)(value % bound);
    }

    public int Next(int minInclusive, int maxExclusive)
    {
        if (minInclusive >= maxExclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive));
        }

        return (int)NextInt64(minInclusive, maxExclusive);
    }

    public long NextInt64(long maxExclusive)
    {
        if (maxExclusive <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive));
        }
        return (long)NextBounded((ulong)maxExclusive);
    }

    public long NextInt64(long minInclusive, long maxExclusive)
    {
        if (minInclusive >= maxExclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive));
        }
        ulong range = unchecked((ulong)maxExclusive - (ulong)minInclusive);
        return unchecked((long)((ulong)minInclusive + NextBounded(range)));
    }

    public double NextDouble()
    {
        return (NextUInt64() >> 11) * (1.0 / (1UL << 53));
    }

    public DeterministicRandomStream Fork(string channel, string? key = null)
    {
        var channelHash = StableHash(channel ?? string.Empty);
        var value = Mix64(
            unchecked((uint)Seed)
            ^ channelHash
            ^ unchecked((ulong)Step * GoldenGamma)
        );
        if (!string.IsNullOrEmpty(key))
        {
            value = Mix64(value ^ RotateLeft(StableHash(key), 23));
        }
        return new DeterministicRandomStream(NormalizeSeed((int)(value & 0x7FFFFFFF)));
    }

    private uint NextUInt32()
    {
        EnsureCanAdvance(1);
        var sequence = unchecked((uint)Seed) + unchecked((ulong)(Step + 1) * GoldenGamma);
        Step += 1;
        return (uint)(Mix64(sequence) >> 32);
    }

    private ulong NextUInt64()
    {
        EnsureCanAdvance(2);
        return ((ulong)NextUInt32() << 32) | NextUInt32();
    }

    private ulong NextBounded(ulong bound)
    {
        var threshold = unchecked(0UL - bound) % bound;
        ulong value;
        do
        {
            value = NextUInt64();
        }
        while (value < threshold);

        return value % bound;
    }

    private void EnsureCanAdvance(long count)
    {
        if (Step > long.MaxValue - count)
        {
            throw new InvalidOperationException("Deterministic random stream exhausted its step range.");
        }
    }

    private static int NormalizeSeed(int seed)
    {
        var normalized = seed & 0x7FFFFFFF;
        return normalized == 0 ? 1 : normalized;
    }

    private static ulong StableHash(string value)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= prime;
        }
        return hash;
    }

    private static ulong RotateLeft(ulong value, int bits)
    {
        return (value << bits) | (value >> (64 - bits));
    }

    private static ulong Mix64(ulong value)
    {
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }
}

public static class RandomPicker
{
    public static T? PickWeighted<T>(
        IReadOnlyList<T> items,
        DeterministicRandomStream random,
        Func<T, long> weightOf,
        Func<T, bool>? predicate = null
    )
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(random);
        ArgumentNullException.ThrowIfNull(weightOf);

        long totalWeight = 0;
        foreach (var item in items)
        {
            if (predicate != null && !predicate(item))
            {
                continue;
            }
            totalWeight = checked(totalWeight + Math.Max(0, weightOf(item)));
        }
        if (totalWeight <= 0)
        {
            return default;
        }

        var roll = NextLong(random, totalWeight);
        foreach (var item in items)
        {
            if (predicate != null && !predicate(item))
            {
                continue;
            }
            var weight = Math.Max(0, weightOf(item));
            if (roll < weight)
            {
                return item;
            }
            roll -= weight;
        }
        return default;
    }

    public static void Shuffle<T>(IList<T> values, DeterministicRandomStream random)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(random);
        for (var index = values.Count - 1; index > 0; index--)
        {
            var other = random.Next(index + 1);
            (values[index], values[other]) = (values[other], values[index]);
        }
    }

    private static long NextLong(DeterministicRandomStream random, long maxExclusive)
    {
        return random.NextInt64(maxExclusive);
    }
}
