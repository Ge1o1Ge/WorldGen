namespace WorldGen.Core.Topology;

/// <summary>
/// Six square faces projected onto a sphere. Each square cell is still split into two
/// simulation triangles, while the cube seams provide a closed surface without poles.
/// </summary>
public sealed class CubeSphereTopology : IWorldTopology
{
    private static readonly CardinalDirection[] Directions = Enum.GetValues<CardinalDirection>();

    public CubeSphereTopology(int faceSize)
    {
        if (faceSize < 2) throw new ArgumentOutOfRangeException(nameof(faceSize), "Грань должна иметь хотя бы 2 клетки");
        FaceSize = faceSize;
    }

    public int FaceSize { get; }
    public long CellCount => 6L * FaceSize * FaceSize;
    public long TriangleCount => CellCount * 2;

    public bool Contains(CellAddress cell) =>
        Enum.IsDefined(cell.Face) && (uint)cell.X < FaceSize && (uint)cell.Y < FaceSize;

    public CellAddress GetNeighbor(CellAddress cell, CardinalDirection direction)
    {
        Validate(cell);
        var (dx, dy) = direction switch
        {
            CardinalDirection.West => (-1, 0),
            CardinalDirection.East => (1, 0),
            CardinalDirection.North => (0, -1),
            CardinalDirection.South => (0, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(direction))
        };
        var x = cell.X + dx;
        var y = cell.Y + dy;
        if ((uint)x < FaceSize && (uint)y < FaceSize) return new CellAddress(cell.Face, x, y);

        var step = 2d / FaceSize;
        var u = -1 + (cell.X + 0.5) * step + dx * step;
        var v = -1 + (cell.Y + 0.5) * step + dy * step;
        var cube = ToCube(cell.Face, u, v);
        return FromCube(cube.X, cube.Y, cube.Z);
    }

    public IReadOnlyList<CellAddress> GetNeighbors(CellAddress cell) =>
        Directions.Select(direction => GetNeighbor(cell, direction)).ToArray();

    public UnitVector3 ToUnitVector(CellAddress cell)
    {
        Validate(cell);
        var step = 2d / FaceSize;
        return ProjectFacePoint(
            cell.Face,
            -1 + (cell.X + 0.5) * step,
            -1 + (cell.Y + 0.5) * step);
    }

    public static UnitVector3 ProjectFacePoint(CubeFace face, double u, double v)
    {
        var cube = ToCube(face, u, v);
        return UnitVector3.Normalize(cube.X, cube.Y, cube.Z);
    }

    public CellAddress Locate(UnitVector3 point) => FromCube(point.X, point.Y, point.Z);

    private CellAddress FromCube(double x, double y, double z)
    {
        var ax = Math.Abs(x);
        var ay = Math.Abs(y);
        var az = Math.Abs(z);
        CubeFace face;
        double u;
        double v;

        if (ax >= ay && ax >= az)
        {
            if (x >= 0)
            {
                face = CubeFace.PositiveX;
                u = -z / ax;
                v = y / ax;
            }
            else
            {
                face = CubeFace.NegativeX;
                u = z / ax;
                v = y / ax;
            }
        }
        else if (ay >= az)
        {
            if (y >= 0)
            {
                face = CubeFace.PositiveY;
                u = x / ay;
                v = -z / ay;
            }
            else
            {
                face = CubeFace.NegativeY;
                u = x / ay;
                v = z / ay;
            }
        }
        else if (z >= 0)
        {
            face = CubeFace.PositiveZ;
            u = x / az;
            v = y / az;
        }
        else
        {
            face = CubeFace.NegativeZ;
            u = -x / az;
            v = y / az;
        }

        return new CellAddress(face, ToCellCoordinate(u), ToCellCoordinate(v));
    }

    private int ToCellCoordinate(double value)
    {
        var coordinate = (int)Math.Floor((value + 1) * 0.5 * FaceSize);
        return Math.Clamp(coordinate, 0, FaceSize - 1);
    }

    private static (double X, double Y, double Z) ToCube(CubeFace face, double u, double v) => face switch
    {
        CubeFace.PositiveX => (1, v, -u),
        CubeFace.NegativeX => (-1, v, u),
        CubeFace.PositiveY => (u, 1, -v),
        CubeFace.NegativeY => (u, -1, v),
        CubeFace.PositiveZ => (u, v, 1),
        CubeFace.NegativeZ => (-u, v, -1),
        _ => throw new ArgumentOutOfRangeException(nameof(face))
    };

    private void Validate(CellAddress cell)
    {
        if (!Contains(cell)) throw new ArgumentOutOfRangeException(nameof(cell));
    }
}
