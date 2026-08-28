namespace WorldGen.Core.Simulation;

internal static class SimulationMath
{
    public static StringComparer LocaleComparer { get; } = StringComparer.InvariantCulture;
    public static double Quantize(double value) => Round(value, 1_000_000);
    public static double Round(double value, double factor) => Math.Floor(value * factor + 0.5) / factor;
    public static double Clamp(double value, double minimum, double maximum) => Math.Max(minimum, Math.Min(maximum, value));
}
