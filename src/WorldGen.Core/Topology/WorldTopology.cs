namespace WorldGen.Core.Topology;

public enum CubeFace : byte
{
    PositiveX,
    NegativeX,
    PositiveY,
    NegativeY,
    PositiveZ,
    NegativeZ
}

public enum CardinalDirection : byte
{
    West,
    East,
    North,
    South
}

public enum TriangleHalf : byte
{
    First,
    Second
}

public readonly record struct CellAddress(CubeFace Face, int X, int Y);

public readonly record struct TriangleAddress(CellAddress Cell, TriangleHalf Half);

public readonly record struct ChunkAddress(CubeFace Face, int X, int Y);

public readonly record struct ChunkCellAddress(ChunkAddress Chunk, int LocalX, int LocalY);

public readonly record struct UnitVector3(double X, double Y, double Z)
{
    public static UnitVector3 Normalize(double x, double y, double z)
    {
        var length = Math.Sqrt(x * x + y * y + z * z);
        if (length == 0) throw new ArgumentException("Нулевой вектор невозможно спроецировать на сферу");
        return new UnitVector3(x / length, y / length, z / length);
    }

    public double Dot(UnitVector3 other) => X * other.X + Y * other.Y + Z * other.Z;
}

public interface IWorldTopology
{
    long CellCount { get; }
    bool Contains(CellAddress cell);
    CellAddress GetNeighbor(CellAddress cell, CardinalDirection direction);
    IReadOnlyList<CellAddress> GetNeighbors(CellAddress cell);
    UnitVector3 ToUnitVector(CellAddress cell);
}

public sealed class ChunkLayout
{
    public ChunkLayout(int faceSize, int chunkSize)
    {
        if (faceSize < 1) throw new ArgumentOutOfRangeException(nameof(faceSize));
        if (chunkSize < 1) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        FaceSize = faceSize;
        ChunkSize = chunkSize;
        ChunksPerFaceAxis = (faceSize + chunkSize - 1) / chunkSize;
    }

    public int FaceSize { get; }
    public int ChunkSize { get; }
    public int ChunksPerFaceAxis { get; }
    public int ChunkCount => 6 * ChunksPerFaceAxis * ChunksPerFaceAxis;

    public ChunkCellAddress Locate(CellAddress cell)
    {
        if ((uint)cell.X >= FaceSize || (uint)cell.Y >= FaceSize)
        {
            throw new ArgumentOutOfRangeException(nameof(cell));
        }

        var chunk = new ChunkAddress(cell.Face, cell.X / ChunkSize, cell.Y / ChunkSize);
        return new ChunkCellAddress(chunk, cell.X % ChunkSize, cell.Y % ChunkSize);
    }

    public (int Width, int Height) GetChunkDimensions(ChunkAddress chunk)
    {
        ValidateChunk(chunk);
        return (
            Math.Min(ChunkSize, FaceSize - chunk.X * ChunkSize),
            Math.Min(ChunkSize, FaceSize - chunk.Y * ChunkSize));
    }

    public IEnumerable<ChunkAddress> EnumerateChunks()
    {
        foreach (var face in Enum.GetValues<CubeFace>())
        {
            for (var y = 0; y < ChunksPerFaceAxis; y++)
            {
                for (var x = 0; x < ChunksPerFaceAxis; x++)
                {
                    yield return new ChunkAddress(face, x, y);
                }
            }
        }
    }

    private void ValidateChunk(ChunkAddress chunk)
    {
        if ((uint)chunk.X >= ChunksPerFaceAxis || (uint)chunk.Y >= ChunksPerFaceAxis)
        {
            throw new ArgumentOutOfRangeException(nameof(chunk));
        }
    }
}
