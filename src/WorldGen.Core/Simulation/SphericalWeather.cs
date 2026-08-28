using WorldGen.Core.Topology;

namespace WorldGen.Core.Simulation;

public sealed class AtmosphereState
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public WeatherSurfaceGrid? Surface { get; set; }
    public int LastDay { get; set; } = -1;
    public List<WeatherSystemState> Systems { get; set; } = [];
    public Dictionary<string, GroundWeatherState> Ground { get; set; } = new(StringComparer.Ordinal);
    public long Ignitions { get; set; }
    public double BurnedTimber { get; set; }
}
public sealed class WeatherSystemState
{
    public int Id { get; set; }
    public int Generation { get; set; }
    public int Age { get; set; }
    public UnitVector3 Center { get; set; }
    public double Moisture { get; set; }
    public double Strength { get; set; }
    public double Radius { get; set; }
}
public sealed class GroundWeatherState
{
    public double IceMeters { get; set; }
    public double SoilWater { get; set; } = .5;
    public double Snow { get; set; }
    public double Fire { get; set; }
    public double BurnedTimber { get; set; }
}
public sealed record LocalWeather(double TemperatureC, double RainMm, double Wind, double Storm,
    double SoilWater, double Snow, double Fire);

/// <summary>Coherent spherical fields, not independent per-cell weather dice.
/// Weather is sampled on unit vectors, so it cannot see cube seams.</summary>
public static class SphericalWeather
{
    public static double Random(uint seed, int day, int index)
    {
        var x = unchecked(seed ^ (uint)day * 747796405u ^ (uint)index * 2891336453u);
        x ^= x >> 16; x *= 2246822519u; x ^= x >> 13; x *= 3266489917u; x ^= x >> 16;
        return x / 4294967296d;
    }
    public static double SeasonalTemperature(double mean, UnitVector3 point, double amplitude, double day, int yearDays) =>
        mean + amplitude * point.Y * Math.Cos(2 * Math.PI * (day / yearDays - .47));

    public static AtmosphereState Create(PrimitiveWorldRules rules, uint seed)
    {
        var state = new AtmosphereState();
        for (var id = 0; id < rules.AtmosphericSystems; id++)
        {
            var system = new WeatherSystemState { Id = id };
            Spawn(system, seed, rules.AtmosphericSystems); system.Age = id * 9;
            state.Systems.Add(system);
        }
        return state;
    }
    private static void Spawn(WeatherSystemState s, uint seed, int count)
    {
        // Stratified latitude bands avoid accidentally leaving an entire hemisphere without weather.
        var y = -1 + 2 * (s.Id + .25 + Random(seed, s.Generation, s.Id * 7) * .5) / count;
        var angle = Random(seed, s.Generation, s.Id * 7 + 1) * Math.PI * 2;
        var r = Math.Sqrt(1 - y * y);
        s.Center = new(r * Math.Cos(angle), y, r * Math.Sin(angle));
        s.Moisture = .4 + Random(seed, s.Generation, s.Id * 7 + 2) * .4;
        s.Strength = .35 + Random(seed, s.Generation, s.Id * 7 + 3) * .65;
        s.Radius = .35 + Random(seed, s.Generation, s.Id * 7 + 4) * .3;
        s.Age = 0;
    }
    public static void Advance(AtmosphereState state, PrimitiveWorldRules rules, uint seed, int day,
        Func<UnitVector3, bool> isOcean)
    {
        if (day <= state.LastDay) throw new InvalidOperationException("Погода за этот день уже рассчитана");
        for (var d = state.LastDay + 1; d <= day; d++)
        foreach (var s in state.Systems.OrderBy(s => s.Id))
        {
            if (++s.Age > 150 + s.Id * 3) { s.Generation++; Spawn(s, seed, rules.AtmosphericSystems); }
            var angle = rules.WeatherSpeedRadians * (.6 + s.Strength) * (s.Id % 3 == 0 ? -1 : 1);
            var p = s.Center;
            s.Center = UnitVector3.Normalize(p.X * Math.Cos(angle) - p.Z * Math.Sin(angle),
                p.Y + Math.Sin((d + s.Id * 19) * .025) * .0015, p.X * Math.Sin(angle) + p.Z * Math.Cos(angle));
            // Ocean evaporation replenishes the system; precipitation and dissipation remove moisture.
            s.Moisture = Math.Clamp(s.Moisture + (isOcean(s.Center) ? .022 : .003) - .012 * s.Strength * s.Moisture, 0, 1);
        }
        state.LastDay = day;
    }
    public static LocalWeather Sample(AtmosphereState state, PrimitiveWorldRules rules, UnitVector3 point,
        double meanTemperature, int day, int yearDays, GroundWeatherState? ground = null)
    {
        double rain = 0, wind = .08, storm = 0, anomaly = 0;
        foreach (var s in state.Systems)
        {
            var distance = Math.Acos(Math.Clamp(point.Dot(s.Center), -1, 1));
            var x = Math.Max(0, 1 - distance / s.Radius);
            var weight = x * x * (3 - 2 * x); // C1 edge; no hard chunk boundary.
            var ageFactor = Math.Clamp(Math.Min(s.Age / 8d, (151 + s.Id * 3 - s.Age) / 8d), 0, 1);
            var strength = s.Strength * weight * ageFactor;
            rain += strength * s.Moisture * 14;
            wind += strength * .7;
            var outer = Math.Max(0, 1 - distance / (s.Radius * 1.7));
            storm = Math.Max(storm, s.Strength * s.Moisture * outer * outer * (3 - 2 * outer) * ageFactor);
            anomaly -= strength * 3;
        }
        return new(SeasonalTemperature(meanTemperature, point, rules.SeasonalAmplitudeC, day, yearDays) + anomaly,
            Math.Min(35, rain), Math.Min(1, wind), storm, ground?.SoilWater ?? .5, ground?.Snow ?? 0, ground?.Fire ?? 0);
    }
    public static void WetGround(GroundWeatherState ground, LocalWeather weather)
    {
        var snowfall = weather.TemperatureC <= 0 ? weather.RainMm : 0;
        var melt = Math.Min(ground.Snow, Math.Max(0, weather.TemperatureC) * 1.5);
        ground.Snow = Math.Clamp(ground.Snow + snowfall - melt, 0, 500);
        var evaporation = .0015 + Math.Max(0, weather.TemperatureC) * .00025 + weather.Wind * .002;
        ground.SoilWater = Math.Clamp(ground.SoilWater + (weather.RainMm - snowfall + melt) * .009 - evaporation - ground.SoilWater * .002, 0, 1);
    }
    public static double Growth(LocalWeather w) => Math.Clamp((w.TemperatureC + 3) / 18, .02, 1) *
        Math.Clamp(w.SoilWater * 2, .08, 1) * Math.Clamp(1 - w.Snow / 60, .02, 1) * (1 - w.Fire);

    public static double CalendarReserve(PrimitiveWorldRules rules, double mean, UnitVector3 point, int day, int yearDays)
    {
        // Climatological cycle only: no future weather state is inspected.
        double weightedCold = 0;
        for (var ahead = 0; ahead <= (int)rules.PreparationDays; ahead++)
        {
            var temperature = SeasonalTemperature(mean, point, rules.SeasonalAmplitudeC, day + ahead, yearDays);
            weightedCold = Math.Max(weightedCold, Math.Clamp((10 - temperature) / 12, 0, 1) * (1 - ahead / (rules.PreparationDays + 1)));
        }
        return rules.WinterReserveDays * weightedCold;
    }
}
