using ImageManager.App.Services;
using ImageManager.Core.Models;

var tests = new (string Name, Func<Task> Run)[]
{
    ("classifies extensions case-insensitively and excludes unsupported files", ClassifiesExtensions),
    ("type-only filtering does not load dimensions or image headers", TypeOnlySkipsMetadata),
    ("type is applied before dimensions are requested", TypeIsAppliedBeforeDimensions),
    ("orientation intersection preserves source order", OrientationIntersectionPreservesOrder),
    ("square orientation is distinct", SquareIsDistinct),
    ("dimensions require positive width and height", DimensionsMustBePositive),
    ("image fallback is used but video fallback is not", ImageFallbackAndVideoNoFallback),
    ("unknown dimensions honor include and exclude", UnknownDimensionsHonorOption),
    ("cancellation before and during metadata is deterministic", CancellationBeforeAndDuringMetadata),
    ("cancellation during image fallback is observed", CancellationDuringFallback),
    ("empty source returns an empty result", EmptySource)
};

var failures = new List<string>();
foreach (var (name, run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"FAIL {name}: {ex.Message}");
        Console.WriteLine(failures[^1]);
    }
}

if (failures.Count != 0)
{
    Console.Error.WriteLine($"{failures.Count} test(s) failed.");
    Environment.ExitCode = 1;
}
else
    Console.WriteLine($"All {tests.Length} DisplayFileFilter regression tests passed.");

static async Task<DisplayFilterResult> Apply(
    IReadOnlyList<string> source,
    DisplayFilterOptions options,
    Func<List<string>, CancellationToken, Task<Dictionary<string, (int Width, int Height)>>>? load = null,
    Func<string, (int Width, int Height)>? fallback = null,
    CancellationToken token = default)
    => await DisplayFileFilter.ApplyAsync(source, options,
        load ?? ((_, _) => Task.FromResult(new Dictionary<string, (int, int)>())),
        fallback ?? (_ => throw new InvalidOperationException("unexpected image header read")), token);

static async Task ClassifiesExtensions()
{
    var source = new[] { "A.JPG", "b.Mp4", "c.txt", "d.PNG", "e.bin" };
    var result = await Apply(source, new());
    AssertSequence(result.Files, "A.JPG", "b.Mp4", "d.PNG");
    Assert(result.UnknownDimensionsCount == 0, "type-only filtering has no unknown count");
}

static async Task TypeOnlySkipsMetadata()
{
    var loadCalls = 0;
    var fallbackCalls = 0;
    var result = await Apply(new[] { "a.jpg", "b.mp4" }, new("image"),
        (_, _) => { loadCalls++; throw new InvalidOperationException("metadata should not be read"); },
        _ => { fallbackCalls++; throw new InvalidOperationException("header should not be read"); });
    AssertSequence(result.Files, "a.jpg");
    Assert(loadCalls == 0 && fallbackCalls == 0, "type-only path performed metadata I/O");
}

static async Task TypeIsAppliedBeforeDimensions()
{
    List<string>? requested = null;
    var result = await Apply(new[] { "video.mp4", "image.jpg", "other.png" }, new("image", MediaOrientation.Landscape),
        (paths, _) => { requested = paths; return Task.FromResult(new Dictionary<string, (int, int)> { ["image.jpg"] = (4, 2), ["other.png"] = (2, 4) }); });
    AssertSequence(result.Files, "image.jpg");
    AssertSequence(requested!, "image.jpg", "other.png");
}

static async Task OrientationIntersectionPreservesOrder()
{
    var source = new[] { "p.jpg", "l.mp4", "l2.jpg", "u.png", "p2.jpg" };
    var result = await Apply(source, new(null, MediaOrientation.Landscape, false),
        (paths, _) => Task.FromResult(new Dictionary<string, (int, int)>
        {
            ["p.jpg"] = (2, 4), ["l.mp4"] = (9, 3), ["l2.jpg"] = (8, 2), ["u.png"] = (0, 0), ["p2.jpg"] = (3, 7)
        }));
    AssertSequence(result.Files, "l.mp4", "l2.jpg");
    Assert(result.UnknownDimensionsCount == 1, "unknown dimensions count");
}

static async Task SquareIsDistinct()
{
    var result = await Apply(new[] { "land.jpg", "square.png", "port.mp4" }, new(null, MediaOrientation.Square),
        (paths, _) => Task.FromResult(new Dictionary<string, (int, int)>
        { ["land.jpg"] = (3, 2), ["square.png"] = (5, 5), ["port.mp4"] = (2, 3) }));
    AssertSequence(result.Files, "square.png");
}

static async Task DimensionsMustBePositive()
{
    var result = await Apply(new[] { "zero.jpg", "neg.mp4", "valid.png" }, new(null, MediaOrientation.Portrait, false),
        (paths, _) => Task.FromResult(new Dictionary<string, (int, int)>
        { ["zero.jpg"] = (0, 2), ["neg.mp4"] = (-1, 4), ["valid.png"] = (2, 4) }),
        _ => throw new InvalidOperationException("no image header available"));
    AssertSequence(result.Files, "valid.png");
    Assert(result.UnknownDimensionsCount == 2, "non-positive dimensions should be unknown");
}

static async Task ImageFallbackAndVideoNoFallback()
{
    var fallbackPaths = new List<string>();
    var result = await Apply(new[] { "unknown.jpg", "unknown.mp4", "known.png" }, new(null, MediaOrientation.Landscape, false),
        (_, _) => Task.FromResult(new Dictionary<string, (int, int)>
        { ["unknown.jpg"] = (0, 0), ["unknown.mp4"] = (0, 0), ["known.png"] = (4, 2) }),
        path => { fallbackPaths.Add(path); return path == "unknown.jpg" ? (8, 2) : throw new InvalidOperationException(); });
    AssertSequence(result.Files, "unknown.jpg", "known.png");
    AssertSequence(fallbackPaths, "unknown.jpg");
    Assert(result.UnknownDimensionsCount == 1, "video remains unknown without image fallback");
}

static async Task UnknownDimensionsHonorOption()
{
    var source = new[] { "unknown.jpg", "known.jpg" };
    var load = (List<string> _, CancellationToken _) => Task.FromResult(new Dictionary<string, (int, int)> { ["unknown.jpg"] = (0, 0), ["known.jpg"] = (2, 4) });
    var included = await Apply(source, new(null, MediaOrientation.Portrait, true), load, _ => (0, 0));
    var excluded = await Apply(source, new(null, MediaOrientation.Portrait, false), load, _ => (0, 0));
    AssertSequence(included.Files, "unknown.jpg", "known.jpg");
    AssertSequence(excluded.Files, "known.jpg");
    Assert(included.UnknownDimensionsCount == 1 && excluded.UnknownDimensionsCount == 1, "unknown count is reported either way");
}

static async Task CancellationBeforeAndDuringMetadata()
{
    using var before = new CancellationTokenSource();
    before.Cancel();
    await AssertCanceled(Apply(new[] { "a.jpg" }, new("image"), token: before.Token));

    using var during = new CancellationTokenSource();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var pending = Apply(new[] { "a.jpg" }, new(null, MediaOrientation.Landscape), async (_, token) =>
    {
        entered.SetResult();
        await release.Task.WaitAsync(token);
        return new Dictionary<string, (int, int)> { ["a.jpg"] = (2, 1) };
    }, token: during.Token);
    await entered.Task;
    during.Cancel();
    release.SetResult();
    await AssertCanceled(pending);
}

static async Task CancellationDuringFallback()
{
    using var cts = new CancellationTokenSource();
    var result = Apply(new[] { "a.jpg", "b.jpg" }, new(null, MediaOrientation.Landscape),
        (_, _) => Task.FromResult(new Dictionary<string, (int, int)> { ["a.jpg"] = (0, 0), ["b.jpg"] = (0, 0) }),
        _ => { cts.Cancel(); throw new InvalidOperationException("fallback failed"); }, cts.Token);
    await AssertCanceled(result);
}

static async Task EmptySource()
{
    var result = await Apply(Array.Empty<string>(), new(null, MediaOrientation.Portrait));
    Assert(result.Files.Count == 0 && result.UnknownDimensionsCount == 0, "empty source result");
}

static async Task AssertCanceled(Task task)
{
    try { await task; }
    catch (OperationCanceledException) { return; }
    throw new InvalidOperationException("expected OperationCanceledException");
}

static void AssertSequence(IReadOnlyList<string> actual, params string[] expected)
{
    if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        throw new InvalidOperationException($"expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
