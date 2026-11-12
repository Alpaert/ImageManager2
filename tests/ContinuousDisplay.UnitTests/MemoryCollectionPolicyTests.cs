using ImageManager.Common.Helpers;
using Xunit;
using Level = ImageManager.Common.Helpers.MemoryPressureMonitor.PressureLevel;

namespace ContinuousDisplay.UnitTests;

public class MemoryCollectionPolicyTests
{
    [Fact]
    public void RepeatedNormalIdleTicksNeverForceFullCollection()
    {
        var policy = new MemoryCollectionPolicy();
        for (var time = 0; time < 600_000; time += 15_000)
        {
            Assert.False(policy.ShouldCollect(Level.Low, time, 0));
            Assert.False(policy.ShouldCollect(Level.Medium, time, 0));
        }
    }

    [Fact]
    public void HighPressureRequiresSustainedPressureIdleAndCooldown()
    {
        var policy = new MemoryCollectionPolicy();
        Assert.False(policy.ShouldCollect(Level.High, 10_000, 0));
        Assert.False(policy.ShouldCollect(Level.High, 39_999, 0));
        Assert.False(policy.ShouldCollect(Level.High, 40_000, 39_900));
        Assert.True(policy.ShouldCollect(Level.High, 45_000, 40_000));
        policy.RecordCollection(45_000);
        Assert.False(policy.ShouldCollect(Level.High, 164_999, 0));
        Assert.True(policy.ShouldCollect(Level.High, 165_000, 0));
    }

    [Fact]
    public void FallingPressureResetsTimerAndCriticalStillProtectsMemory()
    {
        var policy = new MemoryCollectionPolicy();
        Assert.False(policy.ShouldCollect(Level.High, 0, 0));
        policy.ObservePressure(Level.Low, 29_000);
        Assert.False(policy.ShouldCollect(Level.High, 31_000, 0));
        Assert.True(policy.ShouldCollect(Level.Critical, 32_000, 32_000));
        policy.RecordCollection(32_000);
        Assert.False(policy.ShouldCollect(Level.Critical, 40_000, 40_000));
        Assert.True(policy.ShouldCollect(Level.Critical, 62_000, 62_000));
    }
}
