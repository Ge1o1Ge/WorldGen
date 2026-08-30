using WorldGen.Core.Topology;

namespace WorldGen.Core.Simulation;

public sealed record WinterRules
{
    public int OverviewStride { get; init; } = 16;
    public double FreezeCoefficient { get; init; } = .00035;
    public double MeltMetersPerDegree { get; init; } = .012;
    public double SafeIceMeters { get; init; } = .18;
    public void Validate()
    {
        if (OverviewStride is < 8 or > 64 || !double.IsFinite(FreezeCoefficient) || FreezeCoefficient <= 0 || FreezeCoefficient > .01 ||
            !double.IsFinite(MeltMetersPerDegree) || MeltMetersPerDegree <= 0 || MeltMetersPerDegree > .1 ||
            !double.IsFinite(SafeIceMeters) || SafeIceMeters < .1 || SafeIceMeters > 1)
            throw new InvalidOperationException("Некорректные правила зимней поверхности");
    }
}

public sealed class WeatherSurfaceGrid
{
    public int Resolution { get; set; }
    public int LastDay { get; set; } = -1;
    public double[] Snow { get; set; } = [];
    public double[] Ice { get; set; } = [];
    public bool[] LeafOff { get; set; } = [];
    // A deliberately coarse, persistent climatology. It stores one spatial
    // average per simulated day, grouped into twelve calendar months. Keeping
    // it in the world state makes the graph observed history, not a forecast.
    public int ClimateResolution { get; set; }
    public int[] ClimateSampleDays { get; set; } = [];
    public double[] ClimateTemperatureSum { get; set; } = [];
    public double[] ClimateRainSum { get; set; } = [];
    public double[] ClimateWindSum { get; set; } = [];
}

public static class WinterWeather
{
    // Model thickness, not real-world ice safety advice. Snow insulates; thaw
    // removes thickness immediately. No retroactive ice is inferred on restore.
    public static double IceAfterDay(double ice, LocalWeather weather, WinterRules rules, bool flowing = false)
    {
        var freeze = Math.Max(0, -weather.TemperatureC) * rules.FreezeCoefficient / (1 + weather.Snow / 20) * (flowing ? .3 : 1);
        var melt = Math.Max(0, weather.TemperatureC) * rules.MeltMetersPerDegree + (weather.TemperatureC > 0 ? weather.RainMm * .0005 : 0);
        return Math.Clamp(Math.Sqrt(ice * ice + freeze) - melt, 0, 2);
    }
    public static bool Passable(double ice, double temperature, bool ocean, bool flowing, WinterRules rules) =>
        !ocean && !flowing && temperature <= 0 && ice >= rules.SafeIceMeters;
    public static double WalkingCost(LocalWeather weather) =>
        1 + Math.Max(0, weather.SoilWater - .65) * 1.5 + Math.Min(1.5, weather.Snow / 50) + weather.Fire * 8;

    public static UnitVector3 WindVector(AtmosphereState state, UnitVector3 p)
    {
        double x = -p.Z * .08, y = 0, z = p.X * .08;
        foreach (var s in state.Systems)
        {
            var dot = Math.Clamp(p.Dot(s.Center), -1, 1);
            var t = Math.Max(0, 1 - Math.Acos(dot) / s.Radius);
            var age = Math.Clamp(Math.Min(s.Age / 8d, (151 + s.Id * 3 - s.Age) / 8d), 0, 1);
            var amount = s.Strength * t * t * (3 - 2 * t) * age * (s.Center.Y >= 0 ? 1 : -1);
            // A smooth tangent vortex plus weak inflow; no longitude/pole branch.
            x += amount * (s.Center.Y * p.Z - s.Center.Z * p.Y + .2 * (s.Center.X - p.X * dot));
            y += amount * (s.Center.Z * p.X - s.Center.X * p.Z + .2 * (s.Center.Y - p.Y * dot));
            z += amount * (s.Center.X * p.Y - s.Center.Y * p.X + .2 * (s.Center.Z - p.Z * dot));
        }
        return new(x, y, z);
    }
}
