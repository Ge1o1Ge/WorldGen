using WorldGen.Core.Simulation;
using WorldGen.Core.Topology;

namespace WorldGen.Tests;

public sealed class WinterWeatherTests
{
    [Fact]
    public void IceRequiresAccumulatedColdAndThawClosesCrossingImmediately()
    {
        var rules = new WinterRules(); var cold = new LocalWeather(-8, 0, .1, 0, .5, 0, 0);
        var ice = WinterWeather.IceAfterDay(0, cold, rules);
        Assert.False(WinterWeather.Passable(ice, -8, false, false, rules));
        for (var day = 0; day < 20; day++) ice = WinterWeather.IceAfterDay(ice, cold, rules);
        Assert.True(WinterWeather.Passable(ice, -8, false, false, rules));
        Assert.False(WinterWeather.Passable(ice, .1, false, false, rules));
        Assert.False(WinterWeather.Passable(ice, -8, true, false, rules));
        Assert.False(WinterWeather.Passable(ice, -8, false, true, rules));
        Assert.True(WinterWeather.IceAfterDay(.1, cold with { Snow = 50 }, rules) < WinterWeather.IceAfterDay(.1, cold, rules));
        for (var day = 0; day < 30; day++) ice = WinterWeather.IceAfterDay(ice, cold with { TemperatureC = 12, RainMm = 4 }, rules);
        Assert.Equal(0, ice);
    }
    [Fact]
    public void SnowAndMudIncreaseWalkingCostWithoutUnboundedSnowPenalty()
    {
        var dry = new LocalWeather(10, 0, .1, 0, .5, 0, 0);
        Assert.Equal(1, WinterWeather.WalkingCost(dry));
        Assert.True(WinterWeather.WalkingCost(dry with { Snow = 10 }) > 1);
        Assert.InRange(WinterWeather.WalkingCost(dry with { Snow = 500, SoilWater = 1 }), 2.5, 3.1);
    }
    [Fact]
    public void WindIsTangentAndContinuousIncludingPoles()
    {
        var sky = new AtmosphereState();
        foreach (var point in new[] { new UnitVector3(0, 1, 0), new UnitVector3(0, -1, 0), UnitVector3.Normalize(1, .3, 1) })
            Assert.InRange(Math.Abs(point.Dot(WinterWeather.WindVector(sky, point))), 0, 1e-12);
        var a = WinterWeather.WindVector(sky, UnitVector3.Normalize(1 - 1e-8, .2, 1));
        var b = WinterWeather.WindVector(sky, UnitVector3.Normalize(1 + 1e-8, .2, 1));
        Assert.InRange(Math.Abs(a.X-b.X)+Math.Abs(a.Y-b.Y)+Math.Abs(a.Z-b.Z), 0, 1e-7);
    }
}
