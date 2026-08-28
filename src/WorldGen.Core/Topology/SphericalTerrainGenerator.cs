using System.Security.Cryptography;

namespace WorldGen.Core.Topology;

public enum SphericalBiome : byte
{
    Ocean,
    Tundra,
    DryGrassland,
    Meadow,
    Forest,
    Wetland,
    Highlands
}

public readonly record struct TerrainCellSample(
    float ElevationMeters,
    float TemperatureC,
    float Moisture,
    float Fertility,
    float ForestCover,
    float TraversalCost,
    SphericalBiome Biome);

/// <summary>
/// Compact structure-of-arrays chunk. Mutable simulation deltas deliberately live outside
/// this immutable generated substrate so unloaded chunks do not require Territory objects.
/// </summary>
public sealed class TerrainChunk
{
    public TerrainChunk(ChunkAddress address, int width, int height)
    {
        Address = address;
        Width = width;
        Height = height;
        var length = checked(width * height);
        ElevationMeters = new float[length];
        TemperatureC = new float[length];
        Moisture = new float[length];
        Fertility = new float[length];
        ForestCover = new float[length];
        TraversalCost = new float[length];
        Biome = new SphericalBiome[length];
    }

    public ChunkAddress Address { get; }
    public int Width { get; }
    public int Height { get; }
    public int CellCount => Width * Height;
    public float[] ElevationMeters { get; }
    public float[] TemperatureC { get; }
    public float[] Moisture { get; }
    public float[] Fertility { get; }
    public float[] ForestCover { get; }
    public float[] TraversalCost { get; }
    public SphericalBiome[] Biome { get; }

    public int Index(int localX, int localY)
    {
        if ((uint)localX >= Width || (uint)localY >= Height) throw new ArgumentOutOfRangeException();
        return localY * Width + localX;
    }

    public long AllocatedDataBytes =>
        (long)(ElevationMeters.Length + TemperatureC.Length + Moisture.Length + Fertility.Length +
            ForestCover.Length + TraversalCost.Length) * sizeof(float) + Biome.LongLength;

    public string ContentHash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(ElevationMeters.AsSpan()));
        hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(TemperatureC.AsSpan()));
        hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(Moisture.AsSpan()));
        hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(Fertility.AsSpan()));
        hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(ForestCover.AsSpan()));
        hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(TraversalCost.AsSpan()));
        hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(Biome.AsSpan()));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

public sealed class SphericalTerrainGenerator
{
    private readonly SphericalWorldDefinition definition;
    private readonly CubeSphereTopology topology;
    private readonly ChunkLayout layout;

    public SphericalTerrainGenerator(SphericalWorldDefinition definition)
    {
        this.definition = definition ?? throw new ArgumentNullException(nameof(definition));
        topology = new CubeSphereTopology(definition.FaceSize);
        layout = new ChunkLayout(definition.FaceSize, definition.ChunkSize);
    }

    public TerrainChunk GenerateChunk(ChunkAddress address)
    {
        var (width, height) = layout.GetChunkDimensions(address);
        var chunk = new TerrainChunk(address, width, height);
        var originX = address.X * layout.ChunkSize;
        var originY = address.Y * layout.ChunkSize;

        for (var localY = 0; localY < height; localY++)
        {
            for (var localX = 0; localX < width; localX++)
            {
                var cell = new CellAddress(address.Face, originX + localX, originY + localY);
                WriteCell(chunk, chunk.Index(localX, localY), topology.ToUnitVector(cell));
            }
        }

        return chunk;
    }

    public TerrainCellSample GenerateCell(CellAddress cell)
    {
        if (!topology.Contains(cell)) throw new ArgumentOutOfRangeException(nameof(cell));
        return Sample(topology.ToUnitVector(cell));
    }

    public TerrainCellSample SampleSurface(UnitVector3 point) => Sample(point);

    public long EstimateResidentTerrainBytes() => definition.ZoneCount * (6 * sizeof(float) + sizeof(byte));

    private void WriteCell(TerrainChunk chunk, int index, UnitVector3 point)
    {
        var sample = Sample(point);
        chunk.ElevationMeters[index] = sample.ElevationMeters;
        chunk.TemperatureC[index] = sample.TemperatureC;
        chunk.Moisture[index] = sample.Moisture;
        chunk.Fertility[index] = sample.Fertility;
        chunk.ForestCover[index] = sample.ForestCover;
        chunk.TraversalCost[index] = sample.TraversalCost;
        chunk.Biome[index] = sample.Biome;
    }

    private TerrainCellSample Sample(UnitVector3 point)
    {
        var continent = FractalNoise(point, 1.15, 101);
        var detail = FractalNoise(point, 4.4, 503);
        var ridge = 1 - Math.Abs(FractalNoise(point, 8.8, 907) * 2 - 1);
        var terrain = definition.Terrain;
        var elevation = terrain.ElevationBaseMeters +
            (continent - 0.5) * terrain.ElevationRangeMeters * 1.85 +
            (detail - 0.5) * terrain.ElevationRangeMeters * terrain.Roughness * 0.52 +
            Math.Pow(ridge, 3) * terrain.ElevationRangeMeters * terrain.Roughness * 0.16;
        var aboveSea = elevation - terrain.SeaLevelMeters;
        var latitude = Math.Abs(Math.Asin(Math.Clamp(point.Y, -1, 1))) / (Math.PI * 0.5);
        var temperature = Lerp(definition.Climate.EquatorTemperatureC, definition.Climate.PoleTemperatureC, latitude) -
            Math.Max(0, aboveSea) * (definition.Climate.LapseRatePerMeter ?? 0.0065);
        var moistureNoise = FractalNoise(point, 3.1, 1301);
        var moisture = Clamp01(definition.Climate.MoistureBase + (moistureNoise - 0.5) * 0.75 - latitude * 0.08);
        var slopeSignal = Clamp01(Math.Abs(detail - 0.5) * 1.3 + Math.Max(0, aboveSea) / Math.Max(1, terrain.ElevationRangeMeters) * 0.36);
        var fertility = aboveSea <= 0 ? 0 : Clamp01(0.18 + moisture * 0.68 - slopeSignal * 0.42);
        var forest = aboveSea <= 0 || temperature < -3
            ? 0
            : Clamp01((moisture - terrain.ForestThreshold) * 2.6 + (detail - 0.5) * 0.28);
        var biome = ClassifyBiome(aboveSea, temperature, moisture, elevation, fertility, forest);
        var traversalCost = biome switch
        {
            SphericalBiome.Ocean => float.PositiveInfinity,
            SphericalBiome.Wetland => 3.4,
            SphericalBiome.Highlands => 2.8,
            SphericalBiome.Forest => 1.75 + forest * 0.8,
            SphericalBiome.Tundra => 1.7,
            _ => 1.0 + slopeSignal * 0.9
        };

        return new TerrainCellSample(
            (float)elevation,
            (float)temperature,
            (float)moisture,
            (float)fertility,
            (float)forest,
            (float)traversalCost,
            biome);
    }

    private SphericalBiome ClassifyBiome(
        double aboveSea, double temperature, double moisture, double elevation, double fertility, double forest)
    {
        if (aboveSea <= 0) return SphericalBiome.Ocean;
        if (temperature < -3) return SphericalBiome.Tundra;
        if (elevation > definition.Terrain.ElevationBaseMeters + definition.Terrain.ElevationRangeMeters * 0.42)
            return SphericalBiome.Highlands;
        if (moisture > 0.82 && fertility > 0.55) return SphericalBiome.Wetland;
        if (forest > 0.42) return SphericalBiome.Forest;
        if (moisture < 0.34) return SphericalBiome.DryGrassland;
        return SphericalBiome.Meadow;
    }

    private double FractalNoise(UnitVector3 point, double frequency, int salt) =>
        ValueNoise(point, frequency, salt) * 0.54 +
        ValueNoise(point, frequency * 2.03, salt + 101) * 0.29 +
        ValueNoise(point, frequency * 4.07, salt + 211) * 0.17;

    private double ValueNoise(UnitVector3 point, double frequency, int salt)
    {
        var x = point.X * frequency;
        var y = point.Y * frequency;
        var z = point.Z * frequency;
        var ix = (int)Math.Floor(x);
        var iy = (int)Math.Floor(y);
        var iz = (int)Math.Floor(z);
        var tx = Smoothstep(x - ix);
        var ty = Smoothstep(y - iy);
        var tz = Smoothstep(z - iz);
        var c00 = Lerp(Hash01(ix, iy, iz, salt), Hash01(ix + 1, iy, iz, salt), tx);
        var c10 = Lerp(Hash01(ix, iy + 1, iz, salt), Hash01(ix + 1, iy + 1, iz, salt), tx);
        var c01 = Lerp(Hash01(ix, iy, iz + 1, salt), Hash01(ix + 1, iy, iz + 1, salt), tx);
        var c11 = Lerp(Hash01(ix, iy + 1, iz + 1, salt), Hash01(ix + 1, iy + 1, iz + 1, salt), tx);
        return Lerp(Lerp(c00, c10, ty), Lerp(c01, c11, ty), tz);
    }

    private double Hash01(int x, int y, int z, int salt)
    {
        var value = definition.Seed ^
            unchecked((uint)(x + 1) * 0x9e3779b1u) ^
            unchecked((uint)(y + 1) * 0x85ebca77u) ^
            unchecked((uint)(z + 1) * 0xc2b2ae3du) ^
            unchecked((uint)salt);
        value ^= value >> 16;
        value = unchecked(value * 0x7feb352du);
        value ^= value >> 15;
        value = unchecked(value * 0x846ca68bu);
        value ^= value >> 16;
        return value / 4294967296d;
    }

    private static double Smoothstep(double value) => value * value * (3 - 2 * value);
    private static double Lerp(double left, double right, double amount) => left + (right - left) * amount;
    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);
}
