using ImageManager.App.Services;
using Xunit;

namespace ContinuousDisplay.UnitTests;

public sealed class SmoothScrollMotionTests
{
    private const double Maximum = 5000;

    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(144)]
    public void FrameCadenceProducesTheSameOffsetAndVelocity(int hertz)
    {
        var actual = new SmoothScrollMotion();
        var expected = new SmoothScrollMotion();
        actual.ScrollBy(400, Maximum);
        expected.ScrollBy(400, Maximum);

        var duration = TimeSpan.FromMilliseconds(100);
        var frameInterval = TimeSpan.FromSeconds(1d / hertz);
        var remaining = duration;
        while (remaining > TimeSpan.Zero)
        {
            var step = remaining < frameInterval ? remaining : frameInterval;
            actual.Advance(step, Maximum);
            remaining -= step;
        }
        expected.Advance(duration, Maximum);

        Assert.Equal(expected.Position, actual.Position, precision: 8);
        Assert.Equal(expected.Velocity, actual.Velocity, precision: 8);
        var previous = actual.Position;
        for (var frame = 0; frame < hertz && actual.IsActive; frame++)
        {
            actual.Advance(frameInterval, Maximum);
            Assert.InRange(actual.Position, previous, 400);
            previous = actual.Position;
        }
        Assert.Equal(400, actual.Position);
        Assert.Equal(0, actual.Velocity);
        Assert.False(actual.IsActive);
    }

    [Fact]
    public void SameFrameInputAccumulatesAndZeroDeltaLeavesMotionAlone()
    {
        var motion = new SmoothScrollMotion();
        motion.ScrollBy(400, Maximum);
        motion.ScrollBy(400, Maximum);
        motion.ScrollBy(0, Maximum);

        Assert.Equal(800, motion.Target);
        Assert.Equal(0, motion.Position);
        Assert.Equal(0, motion.Velocity);
        motion.Advance(TimeSpan.FromSeconds(1), Maximum);
        Assert.Equal(800, motion.Position);
        Assert.False(motion.IsActive);
    }

    [Fact]
    public void IrregularFrameStepsAndMidFrameInputPreserveMotion()
    {
        var sampled = new SmoothScrollMotion();
        var reference = new SmoothScrollMotion();
        sampled.ScrollBy(400, Maximum);
        reference.ScrollBy(400, Maximum);

        Advance(sampled, 5, 29, 26);
        Advance(reference, 60);
        var positionAtInput = sampled.Position;
        var velocityAtInput = sampled.Velocity;
        sampled.ScrollBy(400, Maximum);
        reference.ScrollBy(400, Maximum);
        Assert.Equal(800, sampled.Target);
        Assert.Equal(positionAtInput, sampled.Position);
        Assert.Equal(velocityAtInput, sampled.Velocity);

        Advance(sampled, 8, 27, 2, 11, 9);
        Advance(reference, 57);
        Assert.Equal(reference.Position, sampled.Position, precision: 8);
        Assert.Equal(reference.Velocity, sampled.Velocity, precision: 8);
    }

    [Fact]
    public void ReversingRetargetsFromCurrentPositionAndBrakesBeforeTurning()
    {
        var motion = new SmoothScrollMotion();
        motion.Reset(1000);
        motion.ScrollBy(400, Maximum);
        Advance(motion, 60);

        var positionAtInput = motion.Position;
        var velocityAtInput = motion.Velocity;
        motion.ScrollBy(-400, Maximum);
        Assert.Equal(positionAtInput - 400, motion.Target);
        Assert.Equal(positionAtInput, motion.Position);
        Assert.Equal(velocityAtInput, motion.Velocity);

        Advance(motion, 1);
        Assert.True(motion.Velocity > 0 && motion.Velocity < velocityAtInput);
        Advance(motion, 15);
        Assert.True(motion.Velocity < 0);
        Assert.InRange(motion.Position, motion.Target, positionAtInput + 15);
        Advance(motion, 1000);
        Assert.Equal(motion.Target, motion.Position);
        Assert.False(motion.IsActive);
    }

    [Fact]
    public void BoundaryCancelsUnpresentedOppositeMoveAndClearsOutwardVelocity()
    {
        var motion = new SmoothScrollMotion();
        motion.ScrollBy(400, Maximum);
        Advance(motion, 1);
        motion.ScrollBy(-400, Maximum);
        Assert.Equal(0, motion.Position);
        Assert.Equal(0, motion.Velocity);
        Assert.False(motion.NeedsFrame);

        motion.Reset(4500);
        motion.ScrollBy(400, Maximum);
        Advance(motion, 20);
        motion.ScrollBy(400, Maximum);
        Advance(motion, 20);
        var currentLimit = motion.Position - 1;
        motion.Advance(TimeSpan.Zero, currentLimit);
        Assert.Equal(currentLimit, motion.Position);
        Assert.Equal(currentLimit, motion.Target);
        Assert.Equal(0, motion.Velocity);
    }

    [Fact]
    public void ExternalOffsetCancelsMotionAndTheNextInputUsesThatOffset()
    {
        var motion = new SmoothScrollMotion();
        motion.ScrollBy(400, Maximum);
        Advance(motion, 40);
        motion.RecordAppliedOffset(motion.Position);
        Assert.False(motion.ObserveExternalOffset(motion.Position));
        Assert.True(motion.IsActive);

        Assert.True(motion.ObserveExternalOffset(750));
        Assert.False(motion.NeedsFrame);
        Assert.Equal(0, motion.Velocity);
        Advance(motion, 40);
        Assert.Equal(750, motion.Position);
        motion.ScrollBy(400, Maximum);
        Assert.Equal(1150, motion.Target);
    }

    private static void Advance(SmoothScrollMotion motion, params int[] intervals)
    {
        foreach (var milliseconds in intervals)
            motion.Advance(TimeSpan.FromMilliseconds(milliseconds), Maximum);
    }
}
