using System.Diagnostics;
using ImageManager.App.Controls;

namespace ImageManager.App.Services;

/// <summary>Collects fixed-size callback statistics and logs a single line when motion ends.</summary>
internal sealed class WheelAnimationDiagnostics
{
    private long _start;
    private long _lastFrame;
    private double _firstFrameDelay;
    private double _maxFrameInterval;
    private double _totalFrameIntervals;
    private double _maxStep;
    private double _totalDistance;
    private double _inputDistance;
    private int _inputs;
    private int _callbacks;
    private int _offsetUpdates;
    private int _intervalsOver20;
    private int _intervalsOver33;
    private int _directionChanges;
    private int _lastDirection;

    public static WheelAnimationDiagnostics? CreateIfEnabled() =>
        Environment.GetEnvironmentVariable("IM_SCROLL_FRAME_DIAGNOSTICS") == "1" ? new() : null;

    public void RecordInput(long timestamp, double distance)
    {
        if (_inputs == 0)
            _start = timestamp;
        _inputs++;
        _inputDistance += distance;
        var direction = Math.Sign(distance);
        if (_lastDirection != 0 && _lastDirection != direction)
            _directionChanges++;
        _lastDirection = direction;
    }

    public void RecordFrame(long timestamp, double displacement)
    {
        if (_callbacks == 0)
        {
            _firstFrameDelay = Stopwatch.GetElapsedTime(_start, timestamp).TotalMilliseconds;
        }
        else
        {
            var interval = Stopwatch.GetElapsedTime(_lastFrame, timestamp).TotalMilliseconds;
            _totalFrameIntervals += interval;
            _maxFrameInterval = Math.Max(_maxFrameInterval, interval);
            if (interval > 20)
                _intervalsOver20++;
            if (interval > 33)
                _intervalsOver33++;
        }

        _lastFrame = timestamp;
        _callbacks++;
        if (displacement != 0)
            _offsetUpdates++;
        _maxStep = Math.Max(_maxStep, Math.Abs(displacement));
        _totalDistance += Math.Abs(displacement);
    }

    public void Complete(string reason, double offset)
    {
        if (_inputs == 0)
            return;

        var meanInterval = _callbacks > 1 ? _totalFrameIntervals / (_callbacks - 1) : 0;
        ScrollDiagnosticsLogger.Log(
            $"WheelMotion.End reason={reason} elapsedMs={Stopwatch.GetElapsedTime(_start).TotalMilliseconds:F1} " +
            $"inputs={_inputs} reversals={_directionChanges} requestedDip={_inputDistance:F1} " +
            $"callbacks={_callbacks} offsetUpdates={_offsetUpdates} firstCallbackMs={_firstFrameDelay:F1} " +
            $"meanCallbackGapMs={meanInterval:F1} maxCallbackGapMs={_maxFrameInterval:F1} " +
            $"gapsOver20ms={_intervalsOver20} gapsOver33ms={_intervalsOver33} " +
            $"maxStepDip={_maxStep:F1} traveledDip={_totalDistance:F1} offsetDip={offset:F1}");
        _inputs = _callbacks = _offsetUpdates = _intervalsOver20 = _intervalsOver33 = _directionChanges = _lastDirection = 0;
        _firstFrameDelay = _maxFrameInterval = _totalFrameIntervals = _maxStep = _totalDistance = _inputDistance = 0;
    }
}
