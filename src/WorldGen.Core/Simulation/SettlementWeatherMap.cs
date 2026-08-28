using WorldGen.Core.Topology;

namespace WorldGen.Core.Simulation;

public sealed partial class SettlementSimulation
{
    private UnitVector3[] weatherPoints = [];
    private double[] weatherMeans = [];
    private LocalWeather[] surfaceWeather = [];
    private UnitVector3[] surfaceWind = [];
    private object? cachedWeatherMap;

    private void InitializeWeatherSurface()
    {
        if (Rules.Primitive?.Winter is not { } rules || State.Atmosphere is not { } sky) return;
        var resolution = Math.Clamp(topology.FaceSize / rules.OverviewStride, 2, 32);
        var count = 6 * resolution * resolution;
        sky.Surface ??= new WeatherSurfaceGrid { Resolution = resolution, LastDay = sky.LastDay,
            Snow = new double[count], Ice = new double[count], LeafOff = new bool[count] };
        var grid = sky.Surface;
        if (grid.Resolution != resolution || grid.Snow.Length != count || grid.Ice.Length != count || grid.LeafOff.Length != count ||
            grid.Snow.Any(v => !double.IsFinite(v) || v < 0 || v > 500) || grid.Ice.Any(v => !double.IsFinite(v) || v < 0 || v > 2))
            throw new InvalidOperationException("Некорректная зимняя поверхность в снимке");
        var mesh = new CubeSphereTopology(resolution);
        weatherPoints = new UnitVector3[count]; weatherMeans = new double[count];
        surfaceWeather = new LocalWeather[count]; surfaceWind = new UnitVector3[count];
        for (var i = 0; i < count; i++)
        {
            weatherPoints[i] = mesh.ToUnitVector(new((CubeFace)(i / (resolution * resolution)), i % resolution, i / resolution % resolution));
            weatherMeans[i] = planetTerrain!.SampleSurface(weatherPoints[i]).TemperatureC;
        }
        SampleWeatherSurface(advance: false);
    }
    private void AdvanceWeatherSurface()
    {
        if (State.Atmosphere?.Surface is not { } grid || grid.LastDay == State.Atmosphere.LastDay) return;
        SampleWeatherSurface(advance: true);
        grid.LastDay = State.Atmosphere.LastDay;
    }
    private void SampleWeatherSurface(bool advance)
    {
        var sky = State.Atmosphere!; var grid = sky.Surface!; var rules = Rules.Primitive!;
        for (var i = 0; i < weatherPoints.Length; i++)
        {
            var w = SphericalWeather.Sample(sky, rules, weatherPoints[i], weatherMeans[i], Math.Max(0, sky.LastDay), world.Calendar.DaysPerYear);
            if (advance)
            {
                grid.Ice[i] = WinterWeather.IceAfterDay(grid.Ice[i], w with { Snow = grid.Snow[i] }, rules.Winter!);
                grid.Snow[i] = Math.Clamp(grid.Snow[i] + (w.TemperatureC <= 0 ? w.RainMm : 0) - Math.Max(0, w.TemperatureC) * 1.5, 0, 500);
                if (w.TemperatureC < 3) grid.LeafOff[i] = true;
                else if (w.TemperatureC > 8) grid.LeafOff[i] = false;
            }
            surfaceWeather[i] = w with { Snow = grid.Snow[i] };
            surfaceWind[i] = WinterWeather.WindVector(sky, weatherPoints[i]);
        }
        cachedWeatherMap = null;
    }
    public object? WeatherMap()
    {
        if (State.Atmosphere?.Surface is not { } grid) return null;
        if (cachedWeatherMap is not null) return cachedWeatherMap;
        static int Q(double value, double scale) => (int)Math.Round(value * scale);
        var local = terrain.OrderBy(p => SphericalSimulation.ZoneId(p.Key), StringComparer.Ordinal).ToArray();
        cachedWeatherMap = new
        {
            revision = State.Atmosphere.LastDay,
            grid.Resolution,
            safeIceMeters = Rules.Primitive!.Winter!.SafeIceMeters,
            temperature = surfaceWeather.Select(w => Q(w.TemperatureC, 10)).ToArray(),
            rain = surfaceWeather.Select(w => Q(w.RainMm, 10)).ToArray(),
            snow = grid.Snow.Select(v => Q(v, 10)).ToArray(),
            ice = grid.Ice.Select(v => Q(v, 1000)).ToArray(),
            leafOff = grid.LeafOff.Select(v => v ? 1 : 0).ToArray(),
            windX = surfaceWind.Select(v => Q(v.X, 10000)).ToArray(),
            windY = surfaceWind.Select(v => Q(v.Y, 10000)).ToArray(),
            windZ = surfaceWind.Select(v => Q(v.Z, 10000)).ToArray(),
            local = new
            {
                indices = local.Select(p => ((int)p.Key.Face * topology.FaceSize + p.Key.Y) * topology.FaceSize + p.Key.X).ToArray(),
                temperature = local.Select(p => Q(dailyWeather[p.Key].TemperatureC, 10)).ToArray(),
                snow = local.Select(p => Q(State.Atmosphere.Ground[p.Value.Id].Snow, 10)).ToArray(),
                ice = local.Select(p => Q(State.Atmosphere.Ground[p.Value.Id].IceMeters, 1000)).ToArray(),
                // Negative = water closed; positive = actual local walking cost.
                walking = local.Select(p => p.Value.Terrain == "water" && !IcePassable(p.Key) ? -1 : Q(WeatherWalking(p.Key) * (p.Value.Terrain == "water" ? 1.2 : 1), 100)).ToArray()
            }
        };
        return cachedWeatherMap;
    }
}
