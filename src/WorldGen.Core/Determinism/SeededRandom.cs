namespace WorldGen.Core.Determinism;

public sealed class SeededRandom
{
    private const uint Increment = 0x6d2b79f5;
    private uint _state;

    public SeededRandom(uint seed, string streamName)
    {
        ArgumentNullException.ThrowIfNull(streamName);
        _state = HashStream(seed, streamName);
    }

    public uint State => _state;

    public void RestoreState(uint state) => _state = state;

    public double NextDouble()
    {
        unchecked
        {
            _state += Increment;
            var value = _state;
            value = (value ^ (value >> 15)) * (value | 1);
            value ^= value + (value ^ (value >> 7)) * (value | 61);
            return (value ^ (value >> 14)) / 4294967296d;
        }
    }

    public static IReadOnlyDictionary<string, SeededRandom> CreateStreams(uint seed, IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names
            .Order(StringComparer.Ordinal)
            .ToDictionary(name => name, name => new SeededRandom(seed, name), StringComparer.Ordinal);
    }

    private static uint HashStream(uint seed, string streamName)
    {
        unchecked
        {
            var hash = seed;
            foreach (var character in streamName)
            {
                hash ^= character;
                hash *= 0x01000193;
            }

            return hash == 0 ? Increment : hash;
        }
    }
}
