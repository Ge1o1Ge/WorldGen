using WorldGen.Core.Topology;

namespace WorldGen.Core.Simulation;

public sealed partial class SettlementSimulation
{
    private const int ClimateMonths = 12;
    private const int ClimateMaximumResolution = 8;
    private UnitVector3[] weatherPoints = [];
    private double[] weatherMeans = [];
    private LocalWeather[] surfaceWeather = [];
    private UnitVector3[] surfaceWind = [];
    private double[] climateDayTemperature = [];
    private double[] climateDayRain = [];
    private double[] climateDayWind = [];
    private int[] climateDayCounts = [];
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
        InitializeClimateHistory(grid, resolution);
        var mesh = new CubeSphereTopology(resolution);
        weatherPoints = new UnitVector3[count]; weatherMeans = new double[count];
        surfaceWeather = new LocalWeather[count]; surfaceWind = new UnitVector3[count];
        var climateCells = 6 * grid.ClimateResolution * grid.ClimateResolution;
        climateDayTemperature = new double[climateCells]; climateDayRain = new double[climateCells];
        climateDayWind = new double[climateCells]; climateDayCounts = new int[climateCells];
        for (var i = 0; i < count; i++)
        {
            weatherPoints[i] = mesh.ToUnitVector(new((CubeFace)(i / (resolution * resolution)), i % resolution, i / resolution % resolution));
            weatherMeans[i] = planetTerrain!.SampleSurface(weatherPoints[i]).TemperatureC;
        }
        SampleWeatherSurface(advance: false);
    }
    private static void InitializeClimateHistory(WeatherSurfaceGrid grid, int weatherResolution)
    {
        var resolution = Math.Min(ClimateMaximumResolution, weatherResolution);
        var length = ClimateMonths * 6 * resolution * resolution;
        if (grid.ClimateResolution == 0 && grid.ClimateSampleDays.Length == 0 &&
            grid.ClimateTemperatureSum.Length == 0 && grid.ClimateRainSum.Length == 0 && grid.ClimateWindSum.Length == 0)
        {
            grid.ClimateResolution = resolution;
            grid.ClimateSampleDays = new int[ClimateMonths];
            grid.ClimateTemperatureSum = new double[length];
            grid.ClimateRainSum = new double[length];
            grid.ClimateWindSum = new double[length];
            return;
        }
        if (grid.ClimateResolution != resolution || grid.ClimateSampleDays.Length != ClimateMonths ||
            grid.ClimateTemperatureSum.Length != length || grid.ClimateRainSum.Length != length || grid.ClimateWindSum.Length != length ||
            grid.ClimateSampleDays.Any(n => n < 0) ||
            grid.ClimateTemperatureSum.Concat(grid.ClimateRainSum).Concat(grid.ClimateWindSum).Any(v => !double.IsFinite(v)))
            throw new InvalidOperationException("Некорректная история климата в снимке");
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
        if (advance)
        {
            Array.Clear(climateDayTemperature); Array.Clear(climateDayRain);
            Array.Clear(climateDayWind); Array.Clear(climateDayCounts);
        }
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
            if (advance) AccumulateClimateCell(grid, i, surfaceWeather[i], surfaceWind[i]);
        }
        if (advance) FinishClimateDay(grid, sky.LastDay);
        cachedWeatherMap = null;
    }
    private void AccumulateClimateCell(WeatherSurfaceGrid grid, int source, LocalWeather weather, UnitVector3 wind)
    {
        var sourceResolution = grid.Resolution;
        var resolution = grid.ClimateResolution;
        var face=source/(sourceResolution*sourceResolution);var local=source%(sourceResolution*sourceResolution);
        var x=local%sourceResolution;var y=local/sourceResolution;
        var target=(face*resolution+Math.Min(resolution-1,y*resolution/sourceResolution))*resolution+Math.Min(resolution-1,x*resolution/sourceResolution);
        climateDayTemperature[target]+=weather.TemperatureC;climateDayRain[target]+=weather.RainMm;
        climateDayWind[target]+=Math.Sqrt(wind.X*wind.X+wind.Y*wind.Y+wind.Z*wind.Z);climateDayCounts[target]++;
    }
    private void FinishClimateDay(WeatherSurfaceGrid grid, int day)
    {
        var cellCount = climateDayCounts.Length;
        var yearDay = ((day % world.Calendar.DaysPerYear) + world.Calendar.DaysPerYear) % world.Calendar.DaysPerYear;
        var month = Math.Min(ClimateMonths - 1, yearDay * ClimateMonths / world.Calendar.DaysPerYear);
        var offset = month * cellCount;
        for (var i = 0; i < cellCount; i++)
        {
            grid.ClimateTemperatureSum[offset + i] += climateDayTemperature[i] / climateDayCounts[i];
            grid.ClimateRainSum[offset + i] += climateDayRain[i] / climateDayCounts[i];
            grid.ClimateWindSum[offset + i] += climateDayWind[i] / climateDayCounts[i];
        }
        grid.ClimateSampleDays[month]++;
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
            climate = new
            {
                resolution = grid.ClimateResolution,
                months = ClimateMonths,
                sampleDays = grid.ClimateSampleDays,
                // Missing months use a sentinel instead of pretending that a
                // seasonal model is historical observation.
                temperature = ClimateValues(grid.ClimateTemperatureSum, grid.ClimateSampleDays, grid.ClimateResolution, 10),
                rain = ClimateValues(grid.ClimateRainSum, grid.ClimateSampleDays, grid.ClimateResolution, 10),
                wind = ClimateValues(grid.ClimateWindSum, grid.ClimateSampleDays, grid.ClimateResolution, 100)
            },
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
    private static int[] ClimateValues(double[] sums, int[] days, int resolution, double scale)
    {
        const int missing = int.MinValue;
        var cellCount = 6 * resolution * resolution;
        var result = new int[sums.Length];
        for (var month = 0; month < ClimateMonths; month++)
        for (var cell = 0; cell < cellCount; cell++)
            result[month * cellCount + cell] = days[month] == 0 ? missing :
                (int)Math.Round(sums[month * cellCount + cell] / days[month] * scale);
        return result;
    }
}
