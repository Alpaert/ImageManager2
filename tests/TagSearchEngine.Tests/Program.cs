using System.Reflection;
using CommunityToolkit.Mvvm.Messaging;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Services;

var tests = new (string Name, Func<Task> Run)[]
{
    ("typed suggestions use active token, prefix priority, count order, and 300 limit", TypedSuggestions),
    ("typed suggestions return empty for blank input or counts", TypedSuggestionsEmpty),
    ("clearing search state preserves loaded autocomplete counts", ClearSearchPreservesCounts),
    ("co-tag suggestions exclude expression, pass filter, and limit results", CoTagSuggestions),
    ("co-tag suggestions skip repository for empty files", CoTagSuggestionsEmptyFiles),
    ("tag search does not implicitly query co-tags", SearchDoesNotImplicitlyQueryCoTags),
    ("selecting a typed suggestion waits for Enter", SelectSuggestionWaitsForEnter)
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

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} test(s) failed.");
    Environment.ExitCode = 1;
}
else
    Console.WriteLine($"All {tests.Length} TagSearchEngine regression tests passed.");

static TagSearchEngine CreateEngine(out RepositoryProxy repository)
{
    repository = new RepositoryProxy();
    RepositoryProxy.Current = repository;
    var proxy = DispatchProxy.Create<IImageMetaRepository, RepositoryProxy>();
    return new TagSearchEngine(proxy, WeakReferenceMessenger.Default);
}

static Task TypedSuggestions()
{
    var engine = CreateEngine(out _);
    var counts = Enumerable.Range(0, 305)
        .Select(i => new TagCount { Name = i == 0 ? "cat" : $"wildcat-{i:000}", Count = i == 0 ? 1 : 305 - i })
        .ToList();
    counts.Add(new TagCount { Name = "cater", Count = 999 });
    engine.SetAllTagCounts(counts);

    var results = engine.GetTypedSuggestions("cats a ca");
    Assert(results.Count == 300, "typed suggestions should be capped at 300");
    Assert(results[0].Name == "cater", "starts-with matches should precede contains matches");
    Assert(results[1].Name == "cat", "starts-with matches should be count ordered");
    Assert(results.All(t => t.Name.Contains("ca", StringComparison.OrdinalIgnoreCase)), "active token was not used");
    return Task.CompletedTask;
}

static Task TypedSuggestionsEmpty()
{
    var engine = CreateEngine(out _);
    engine.SetAllTagCounts(new List<TagCount> { new() { Name = "cat", Count = 1 } });
    Assert(engine.GetTypedSuggestions("").Count == 0, "blank keyword should return empty");
    engine.SetAllTagCounts(new List<TagCount>());
    Assert(engine.GetTypedSuggestions("cat").Count == 0, "empty counts should return empty");
    return Task.CompletedTask;
}

static Task ClearSearchPreservesCounts()
{
    var engine = CreateEngine(out _);
    engine.SetAllTagCounts(new List<TagCount> { new() { Name = "cat", Count = 1 } });
    engine.SearchResultFiles.Add("a.jpg");

    engine.ClearSearchResults();

    Assert(engine.SearchResultFiles.Count == 0, "search results should be cleared");
    Assert(engine.GetTypedSuggestions("cat").Count == 1, "loaded autocomplete counts should be retained");
    return Task.CompletedTask;
}

static async Task CoTagSuggestions()
{
    var engine = CreateEngine(out var repository);
    repository.Results = Enumerable.Range(0, 305)
        .Select(i => new TagCount { Name = $"co-{i:000}", Count = 305 - i })
        .ToList();

    var results = await engine.GetCoTagSuggestionsAsync(new[] { "a.jpg", "b.jpg" }, "cat a dog o bird", "co-");
    Assert(results.Count == 300, "co-tag suggestions should be capped at 300");
    Assert(repository.Calls == 1, "co-tag repository should be called once");
    Assert(repository.Paths.SequenceEqual(new[] { "a.jpg", "b.jpg" }), "file paths were not passed through");
    Assert(repository.Excludes.SequenceEqual(new[] { "cat", "dog", "bird" }), "expression tags were not excluded");
    Assert(repository.NameFilter == "co-", "name filter was not passed through");
}

static async Task CoTagSuggestionsEmptyFiles()
{
    var engine = CreateEngine(out var repository);
    var results = await engine.GetCoTagSuggestionsAsync(Array.Empty<string>(), "cat", "c");
    Assert(results.Count == 0, "empty files should return empty");
    Assert(repository.Calls == 0, "empty files should not query repository");
}

static async Task SearchDoesNotImplicitlyQueryCoTags()
{
    var engine = CreateEngine(out var repository);
    repository.SearchResults = new List<string> { "a.jpg" };

    await engine.SearchByTagAsync("cat", new List<string> { "a.jpg" }, false, _ => { });

    Assert(repository.SearchCalls == 1, "tag search should query matching files once");
    Assert(repository.Calls == 0, "tag search must not implicitly query co-tags");
}

static Task SelectSuggestionWaitsForEnter()
{
    var engine = CreateEngine(out _);
    var selectedText = string.Empty;
    var searched = false;
    engine.SelectSuggestion(
        new TagCount { Name = "cat", Count = 1 },
        "ca",
        text => selectedText = text,
        _ => { },
        () =>
        {
            searched = true;
            return Task.CompletedTask;
        });

    Assert(selectedText == "cat", "selecting a suggestion should replace the active token");
    Assert(!searched, "selecting a suggestion must not execute the image search");
    return Task.CompletedTask;
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

class RepositoryProxy : DispatchProxy
{
    public static RepositoryProxy Current { get; set; } = null!;
    public List<TagCount> Results { get; set; } = new();
    public List<string> Paths { get; private set; } = new();
    public List<string> Excludes { get; private set; } = new();
    public string? NameFilter { get; private set; }
    public int Calls { get; private set; }
    public int SearchCalls { get; private set; }
    public List<string> SearchResults { get; set; } = new();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IImageMetaRepository.GetCoOccurringTagsAsync))
        {
            Current.Calls++;
            Current.Paths = ((List<string>)args![0]!).ToList();
            Current.Excludes = ((List<string>?)args[1] ?? new()).ToList();
            Current.NameFilter = (string?)args[2];
            return Task.FromResult(Current.Results);
        }

        if (targetMethod?.Name == nameof(IImageMetaRepository.GetFilePathsByTagAsync))
        {
            Current.SearchCalls++;
            return Task.FromResult(Current.SearchResults);
        }

        throw new NotSupportedException($"Unexpected repository call: {targetMethod?.Name}");
    }
}
