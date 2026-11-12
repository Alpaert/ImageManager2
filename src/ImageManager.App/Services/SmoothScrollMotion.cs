namespace ImageManager.App.Services;

/// <summary>Wheel offset and velocity driven by a monotonic caller-supplied clock.</summary>
public sealed class SmoothScrollMotion
{
    private const double ResponseRate = 32;
    private const double PositionTolerance = 0.5;
    private const double VelocityTolerance = 8;
    private double _appliedOffset;

    public double Position { get; private set; }
    public double Velocity { get; private set; }
    public double Target { get; private set; }
    public bool IsActive => Position != Target || Velocity != 0;
    public bool NeedsFrame => IsActive || Position != _appliedOffset;

    public void Reset(double offset)
    {
        Position = Target = _appliedOffset = offset;
        Velocity = 0;
    }

    public bool ObserveExternalOffset(double offset)
    {
        if (offset == _appliedOffset)
            return false;

        Reset(offset);
        return true;
    }

    public void RecordAppliedOffset(double offset) => _appliedOffset = offset;

    /// <summary>Changes the destination after advancing to the input's timestamp.</summary>
    public void ScrollBy(double distance, double maximumOffset)
    {
        if (distance == 0 || !double.IsFinite(distance))
            return;

        var basis = Math.Sign(Target - Position) == Math.Sign(distance) ? Target : Position;
        Target = Math.Clamp(basis + distance, 0, maximumOffset);
        if (Target == _appliedOffset && (Target == 0 || Target == maximumOffset))
        {
            // The opposite input can cancel a queued move before any frame
            // left the boundary. No visible momentum needs to be carried over.
            Reset(Target);
            return;
        }

        SettleIfClose();
    }

    public void Advance(TimeSpan elapsed, double maximumOffset)
    {
        Target = Math.Clamp(Target, 0, maximumOffset);
        ClampPosition(maximumOffset);
        if (!IsActive || elapsed <= TimeSpan.Zero)
            return;

        // Exact solution of the critically damped spring. Splitting the same
        // elapsed time into different frame intervals gives the same motion.
        var dt = elapsed.TotalSeconds;
        var error = Position - Target;
        var slope = Velocity + ResponseRate * error;
        var decay = Math.Exp(-ResponseRate * dt);
        var nextError = (error + slope * dt) * decay;
        if (error != 0 && Math.Sign(error) != Math.Sign(nextError))
        {
            Arrive();
            return;
        }

        Position = Target + nextError;
        Velocity = (Velocity - ResponseRate * slope * dt) * decay;
        ClampPosition(maximumOffset);
        SettleIfClose();
    }

    private void ClampPosition(double maximumOffset)
    {
        Position = Math.Clamp(Position, 0, maximumOffset);
        if (maximumOffset == 0 || (Position == 0 && Velocity < 0) ||
            (Position == maximumOffset && Velocity > 0))
            Velocity = 0;
    }

    private void SettleIfClose()
    {
        if (Math.Abs(Position - Target) < PositionTolerance && Math.Abs(Velocity) < VelocityTolerance)
            Arrive();
    }

    private void Arrive()
    {
        Position = Target;
        Velocity = 0;
    }
}
