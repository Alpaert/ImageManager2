using System.Diagnostics;
using CommunityToolkit.Mvvm.Messaging;
using ImageManager.Common.Helpers;
using ImageManager.Core.Messages;
using ImageManager.Core.Services;
using ImageManager.Core.Models;
using ImageManager.Infrastructure.Data;
using ImageManager.Infrastructure.Data.Repositories;
using ImageManager.Infrastructure.Services;
using Microsoft.Data.Sqlite;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Database initialization is idempotent and NOCASE lookup uses its index", DatabaseInitializationIsIdempotentAsync),
    ("Prepare skips 20,000 tagged missing files without opening them and reports NOCASE lookup timing", PrepareSkipsTaggedMissingFilesAsync),
    ("Repository treats path casing and duplicate registration as one image", CaseInsensitiveAndDuplicateRegistrationAsync),
    ("Repository persists known content ratings without accepting unknown", SystemRatingPersistenceAsync),
    ("Database migration backfills unambiguous legacy rating tags only", LegacyRatingMigrationAsync),
    ("Prepare registers new files and records disappeared files as failures", PrepareRegistersAndSkipsDisappearedFilesAsync),
    ("Prepare moves matching missing metadata without losing identity, status, or tags", PrepareMovesExistingMetadataAsync),
    ("Concurrent registration preserves existing metadata and does not duplicate paths", ConcurrentRegistrationDoesNotDuplicateOrOverwriteAsync),
    ("Orchestration skips models, supports preparation cancel and rejects overlapping runs", OrchestrationPreparationAsync),
    ("Cancellation is honored by lookup, preparation, registration, and database lock retry", CancellationIsHonoredAsync)
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
        failures.Add($"{name}: {ex}");
        Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

return 0;

static async Task DatabaseInitializationIsIdempotentAsync()
{
    await using var scope = await TestScope.CreateAsync();
    using var conn = scope.Factory.CreateConnection();
    await ExecuteAsync(conn, "DROP INDEX idx_imagemeta_filepath_nocase;");
    DatabaseInitializer.Initialize(conn);
    DatabaseInitializer.Initialize(conn);

    await ExecuteAsync(conn, "INSERT INTO ImageMeta (FilePath, AutoTagStatus) VALUES ('C:/Index/Photo.PNG', 1);");
    using var plan = conn.CreateCommand();
    plan.CommandText = "EXPLAIN QUERY PLAN SELECT Id, FilePath, AutoTagStatus FROM ImageMeta WHERE FilePath COLLATE NOCASE IN ('c:/index/photo.png')";
    using var reader = await plan.ExecuteReaderAsync();
    var details = new List<string>();
    while (await reader.ReadAsync()) details.Add(reader.GetString(3));
    Assert(details.Any(d => d.Contains("SEARCH", StringComparison.OrdinalIgnoreCase) &&
                            d.Contains("idx_imagemeta_filepath_nocase", StringComparison.OrdinalIgnoreCase)),
        $"Expected NOCASE index search, got: {string.Join(" | ", details)}");
}

static async Task PrepareSkipsTaggedMissingFilesAsync()
{
    await using var scope = await TestScope.CreateAsync();
    const int count = 20_000;
    const int libraryCount = 140_000;
    var paths = Enumerable.Range(0, count).Select(i => $"Z:/not-present/tagged-{i:D5}.png").ToList();
    using (var conn = scope.Factory.CreateConnection())
    using (var txn = conn.BeginTransaction())
    {
        for (var i = 0; i < libraryCount; i++)
        {
            using var command = conn.CreateCommand();
            command.Transaction = txn;
            command.CommandText = "INSERT INTO ImageMeta (FilePath, AutoTagStatus) VALUES (@path, 1);";
            command.Parameters.AddWithValue("@path", $"Z:/not-present/tagged-{i:D5}.png");
            await command.ExecuteNonQueryAsync();
        }
        txn.Commit();
    }

    using (var conn = scope.Factory.CreateConnection())
        await ExecuteAsync(conn, "DROP INDEX idx_imagemeta_filepath_nocase;");
    var noIndexWatch = Stopwatch.StartNew();
    var noIndexStatuses = await scope.Repository.GetStatusMapByPathsAsync(paths);
    noIndexWatch.Stop();
    Assert(noIndexStatuses.Count == count, "Unindexed status lookup did not return all tagged paths.");

    using (var conn = scope.Factory.CreateConnection())
        await ExecuteAsync(conn, "CREATE INDEX idx_imagemeta_filepath_nocase ON ImageMeta(FilePath COLLATE NOCASE);");
    var indexedWatch = Stopwatch.StartNew();
    var indexedStatuses = await scope.Repository.GetStatusMapByPathsAsync(paths);
    indexedWatch.Stop();
    Assert(indexedStatuses.Count == count, "Indexed status lookup did not return all tagged paths.");
    Console.WriteLine($"INFO GetStatusMapByPathsAsync 20k paths / 140k library: no-NOCASE-index={noIndexWatch.ElapsedMilliseconds}ms, indexed={indexedWatch.ElapsedMilliseconds}ms");

    var reported = new List<int>();
    var prepareWatch = Stopwatch.StartNew();
    var result = await new AutoTagPreparationService(scope.Repository).PrepareAsync(1, paths,
        progress: p => { if (p.Phase == "Filter") reported.Add(p.Processed); });
    Assert(result.Total == count && result.Skipped == count && result.Images.Count == 0 && result.Failed == 0,
        $"Unexpected result: total={result.Total}, skipped={result.Skipped}, images={result.Images.Count}, failed={result.Failed}");
    Assert(reported.Contains(count), "Filter progress did not reach the full 20,000-path batch.");
    Console.WriteLine($"INFO Full preparation: {prepareWatch.ElapsedMilliseconds}ms, skipped={result.Skipped}");
}

static async Task OrchestrationPreparationAsync()
{
    await using var scope = await TestScope.CreateAsync();
    var path = "Z:/does-not-exist/already-tagged.png";
    await scope.Repository.RegisterAutoTagFilesAsync(new[] { Registration(path, "tagged") });
    await scope.Repository.SetAutoTagStatusByPathAsync(path, 1);
    var messenger = new StrongReferenceMessenger();
    var trap = new ModelTrap();
    using var pipeline = new AutoTagPipelineService(scope.Repository, trap, null!, null!, null!);
    using var orchestrator = new AutoTagOrchestrator(pipeline, scope.Repository, null!, trap,
        null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, messenger);
    var recipient = new object();
    var phases = new List<string>();
    var cancel = false;
    var overlapping = false;
    var pauseAtFilter = false;
    using var releaseFilter = new ManualResetEventSlim();
    Task? overlap = null;
    messenger.Register<AutoTagProgressMessage>(recipient, (_, message) =>
    {
        Assert(AutoTagRuntimeState.IsRunning, "Run must cover preparation, including progress publication.");
        phases.Add(message.Phase);
        if (message.Phase == "Filter" && message.Processed == 0)
        {
            if (pauseAtFilter) releaseFilter.Wait(TimeSpan.FromSeconds(5));
            if (cancel) orchestrator.CancelAsync().GetAwaiter().GetResult();
            if (overlapping)
            {
                try { overlap = orchestrator.RunSelectedImagesAsync(new() { path }); }
                catch (InvalidOperationException ex) { overlap = Task.FromException(ex); }
            }
        }
    });
    await orchestrator.RunSelectedImagesAsync(new() { path, path.ToUpperInvariant() });
    Assert(orchestrator.LastSkippedCount == 1 && orchestrator.LastProcessedPaths.Count == 0, "Tagged duplicate input should only be skipped once.");
    Assert(phases.Last() == "Done" && !phases.Contains("Model"), "All-tagged run must not load models.");
    Assert(!AutoTagRuntimeState.IsRunning, "Runtime state leaked after all-tagged run.");
    cancel = true;
    await orchestrator.RunPipelineAsync(0, "", new() { path }, "Start");
    Assert(orchestrator.LastRunCancelled && phases.Last() == "Stopped", "Preparation cancellation did not stop the run.");
    Assert(!AutoTagRuntimeState.IsRunning, "Runtime state leaked after cancellation.");
    cancel = false;
    pauseAtFilter = true;
    var immediateRun = orchestrator.RunSelectedImagesAsync(new() { path });
    await orchestrator.CancelAsync();
    releaseFilter.Set();
    await immediateRun;
    Assert(orchestrator.LastRunCancelled, "Stopping immediately after dispatch was lost.");
    pauseAtFilter = false;
    overlapping = true;
    await orchestrator.RunPipelineAsync(0, "", new() { path }, "Start");
    Assert(overlap is not null, "Overlap test did not run.");
    try { await overlap!; throw new Exception("Overlapping run should be rejected."); }
    catch (InvalidOperationException) { }
    Assert(!AutoTagRuntimeState.IsRunning && orchestrator.LastSkippedCount == 1, "Overlap damaged the original run.");
    messenger.UnregisterAll(recipient);
}

static async Task CaseInsensitiveAndDuplicateRegistrationAsync()
{
    await using var scope = await TestScope.CreateAsync();
    var first = Registration("C:/Photos/Case.PNG", "case-hash", folderId: 9);
    var second = Registration("c:/photos/case.png", "different-hash", folderId: 99);
    var result = await scope.Repository.RegisterAutoTagFilesAsync(new[] { first, second });
    Assert(result.Count == 1, "Duplicate case variants should produce one result entry.");
    var rows = await QueryRowsAsync(scope, "SELECT Id, FileHash, FolderId, AutoTagStatus FROM ImageMeta");
    Assert(rows.Count == 1, "Case variants created more than one metadata row.");
    Assert((string)rows[0]["FileHash"]! == "case-hash" && Convert.ToInt64(rows[0]["FolderId"]) == 9,
        "Existing metadata was overwritten by duplicate registration.");
}

static async Task SystemRatingPersistenceAsync()
{
    await using var scope = await TestScope.CreateAsync();
    const string path = "C:/Ratings/Example.PNG";
    await scope.Repository.RegisterAutoTagFilesAsync(new[] { Registration(path, "rating-hash") });

    await scope.Repository.SetSystemRatingByPathAsync("c:/ratings/example.png", 3);
    var ratings = await scope.Repository.GetSystemRatingsByPathsAsync(new List<string> { path });
    Assert(ratings.TryGetValue(path, out var rating) && rating == 3,
        "Known rating was not persisted with a case-insensitive path match.");

    try
    {
        await scope.Repository.SetSystemRatingByPathAsync(path, -1);
        throw new InvalidOperationException("Unknown rating must not be persisted through the known-rating API.");
    }
    catch (ArgumentOutOfRangeException) { }

    ratings = await scope.Repository.GetSystemRatingsByPathsAsync(new List<string> { path });
    Assert(ratings[path] == 3, "Rejected unknown rating overwrote the existing known rating.");
}

static async Task LegacyRatingMigrationAsync()
{
    await using var scope = await TestScope.CreateAsync();
    var generalId = await scope.Repository.UpsertAsync(new ImageMeta { FilePath = "C:/Ratings/general.png" });
    var explicitId = await scope.Repository.UpsertAsync(new ImageMeta { FilePath = "C:/Ratings/explicit.png" });
    var conflictId = await scope.Repository.UpsertAsync(new ImageMeta { FilePath = "C:/Ratings/conflict.png" });
    var preservedId = await scope.Repository.UpsertAsync(new ImageMeta { FilePath = "C:/Ratings/preserved.png" });
    var unknownId = await scope.Repository.UpsertAsync(new ImageMeta { FilePath = "C:/Ratings/unknown.png" });

    await scope.Repository.SetTagsAsync(generalId, new List<string> { "全年龄", "general" });
    await scope.Repository.SetTagsAsync(explicitId, new List<string> { "R-18" });
    await scope.Repository.SetTagsAsync(conflictId, new List<string> { "敏感", "大尺度" });
    await scope.Repository.SetTagsAsync(preservedId, new List<string> { "全年龄" });
    await scope.Repository.SetSystemRatingByPathAsync("C:/Ratings/preserved.png", 3);

    using (var conn = scope.Factory.CreateConnection())
    {
        DatabaseInitializer.Initialize(conn);
        DatabaseInitializer.Initialize(conn);
    }

    var rows = await QueryRowsAsync(scope, "SELECT FilePath, SystemRating FROM ImageMeta ORDER BY FilePath");
    var ratings = rows.ToDictionary(
        row => (string)row["FilePath"]!,
        row => Convert.ToInt32(row["SystemRating"]),
        StringComparer.OrdinalIgnoreCase);
    Assert(ratings["C:/Ratings/general.png"] == 0, "General legacy tags were not backfilled.");
    Assert(ratings["C:/Ratings/explicit.png"] == 3, "Explicit legacy tag was not backfilled.");
    Assert(ratings["C:/Ratings/conflict.png"] == -1, "Conflicting legacy rating tags must remain unknown.");
    Assert(ratings["C:/Ratings/preserved.png"] == 3, "Existing SystemRating must not be overwritten.");
    Assert(ratings["C:/Ratings/unknown.png"] == -1, "Images without legacy rating tags must remain unknown.");
}

static async Task PrepareRegistersAndSkipsDisappearedFilesAsync()
{
    await using var scope = await TestScope.CreateAsync();
    var newFile = scope.CreateFile("new-image.png", new byte[] { 1, 2, 3, 4 });
    var goneFile = scope.CreateFile("gone-image.png", new byte[] { 5, 6, 7 });
    File.Delete(goneFile);

    var result = await new AutoTagPreparationService(scope.Repository).PrepareAsync(42, new[] { newFile, goneFile });
    Assert(result.Total == 2 && result.Failed == 1 && result.Images.Count == 1,
        $"Unexpected prepare outcome: total={result.Total}, failed={result.Failed}, images={result.Images.Count}");
    var meta = await scope.Repository.GetByPathAsync(newFile);
    Assert(meta is { FolderId: 42, AutoTagStatus: 0 } && meta.FileHash?.Length == 32,
        "New image was not registered with prepared metadata.");
}

static async Task PrepareMovesExistingMetadataAsync()
{
    await using var scope = await TestScope.CreateAsync();
    var oldPath = scope.CreateFile("old.png", new byte[] { 8, 9, 10 });
    var hash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(await File.ReadAllBytesAsync(oldPath))).ToLowerInvariant();
    var oldId = await scope.Repository.UpsertAsync(new ImageMeta { FilePath = oldPath, FileHash = hash, FolderId = 3 });
    await scope.Repository.SetAutoTagStatusByPathAsync(oldPath, 1);
    await scope.Repository.SetTagsAsync(oldId, new List<string> { "preserved-tag" });
    var newPath = Path.Combine(scope.Directory, "moved.png");
    File.Move(oldPath, newPath);

    var result = await new AutoTagPreparationService(scope.Repository).PrepareAsync(77, new[] { newPath });
    Assert(result.Images.Count == 0 && result.Skipped == 1, "Moved tagged image should remain skipped.");
    var moved = await scope.Repository.GetByPathAsync(newPath);
    Assert(moved is not null && moved.Id == oldId && moved.AutoTagStatus == 1 && moved.FolderId == 77,
        "Move did not preserve Id, tagging status, and new folder link.");
    Assert(moved!.Tags.Any(t => t.Name == "preserved-tag"), "Move lost image tag associations.");
    Assert(await scope.Repository.GetByPathAsync(oldPath) is null, "Old path remains after move.");
}

static async Task ConcurrentRegistrationDoesNotDuplicateOrOverwriteAsync()
{
    await using var scope = await TestScope.CreateAsync();
    var path = "C:/Concurrent/one.png";
    var first = Registration(path, "first", folderId: 5);
    var second = Registration("c:/concurrent/ONE.PNG", "second", folderId: 6);
    await Task.WhenAll(
        Task.Run(() => scope.Repository.RegisterAutoTagFilesAsync(new[] { first })),
        Task.Run(() => new ImageMetaRepository(scope.Factory).RegisterAutoTagFilesAsync(new[] { second })));
    var rows = await QueryRowsAsync(scope, "SELECT Id, FileHash, FolderId FROM ImageMeta");
    Assert(rows.Count == 1, "Concurrent registration produced duplicate paths.");
    Assert(new[] { "first", "second" }.Contains((string)rows[0]["FileHash"]!), "Unexpected stored file hash.");
    Assert(Convert.ToInt64(rows[0]["FolderId"]) is 5 or 6, "Unexpected stored folder ID.");
}

static async Task CancellationIsHonoredAsync()
{
    await using var scope = await TestScope.CreateAsync();
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    await AssertCanceled(() => scope.Repository.GetStatusMapByPathsAsync(new List<string> { "C:/x.png" }, cancelled.Token));
    await AssertCanceled(() => new AutoTagPreparationService(scope.Repository).PrepareAsync(1, new[] { "C:/x.png" }, ct: cancelled.Token));
    await AssertCanceled(() => scope.Repository.RegisterAutoTagFilesAsync(new[] { Registration("C:/x.png", "x") }, cancelled.Token));

    using var lockConn = scope.Factory.CreateConnection();
    await ExecuteAsync(lockConn, "BEGIN EXCLUSIVE;");
    using var lockCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
    var cancellationWatch = Stopwatch.StartNew();
    try
    {
        await AssertCanceled(() => scope.Repository.RegisterAutoTagFilesAsync(new[] { Registration("C:/locked.png", "locked") }, lockCancellation.Token));
        Assert(cancellationWatch.Elapsed < TimeSpan.FromSeconds(5), "Cancellation waited for a long SQLite lock timeout.");
    }
    finally
    {
        await ExecuteAsync(lockConn, "ROLLBACK;");
    }
}

static AutoTagFileRegistration Registration(string path, string hash, long? folderId = null) =>
    new(new ImageMeta { FilePath = path, FileHash = hash, FileSize = 1, LastWriteTicks = 1, FolderId = folderId });

static async Task<List<Dictionary<string, object>>> QueryRowsAsync(TestScope scope, string sql)
{
    using var conn = scope.Factory.CreateConnection();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    using var reader = await cmd.ExecuteReaderAsync();
    var rows = new List<Dictionary<string, object>>();
    while (await reader.ReadAsync())
    {
        var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.GetValue(i);
        rows.Add(row);
    }
    return rows;
}

static async Task ExecuteAsync(SqliteConnection conn, string sql)
{
    using var command = conn.CreateCommand();
    command.CommandText = sql;
    await command.ExecuteNonQueryAsync();
}

static async Task AssertCanceled(Func<Task> action)
{
    try { await action(); }
    catch (OperationCanceledException) { return; }
    throw new InvalidOperationException("Expected OperationCanceledException.");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class TestScope : IAsyncDisposable
{
    private TestScope(string directory, DbContextFactory factory, ImageMetaRepository repository)
    {
        Directory = directory;
        Factory = factory;
        Repository = repository;
    }

    public string Directory { get; }
    public DbContextFactory Factory { get; }
    public ImageMetaRepository Repository { get; }

    public static Task<TestScope> CreateAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ImageManager2-AutoTagPreparation-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var factory = new DbContextFactory(Path.Combine(directory, "test.db"));
        using (var conn = factory.CreateConnection()) DatabaseInitializer.Initialize(conn);
        return Task.FromResult(new TestScope(directory, factory, new ImageMetaRepository(factory)));
    }

    public string CreateFile(string fileName, byte[] content)
    {
        var path = Path.Combine(Directory, fileName);
        File.WriteAllBytes(path, content);
        return path;
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        var resolved = Path.GetFullPath(Directory);
        if (!resolved.StartsWith(Path.Combine(Path.GetTempPath(), "ImageManager2-AutoTagPreparation-"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing cleanup outside the test directory.");
        try { System.IO.Directory.Delete(Directory, recursive: true); }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }
}

sealed class ModelTrap : IEnsembleTagService
{
    public event Action<AutoTagProgress>? ProgressChanged { add { } remove { } }
    public bool IsModelLoaded => throw new Exception("Model was accessed for a skip-only run.");
    public TagMode Mode => TagMode.Ensemble;
    public Task LoadModelAsync(string modelPath, CancellationToken ct = default) => throw new Exception("Unexpected model load.");
    public Task<List<TagPrediction>> PredictAsync(string imagePath, CancellationToken ct = default) => throw new Exception("Unexpected inference.");
    public Task<EnsembleResult> PredictWithSourcesAsync(string imagePath, CancellationToken ct = default) => throw new Exception("Unexpected inference.");
    public Task<SystemRating> PredictRatingAsync(string imagePath, CancellationToken ct = default) => throw new Exception("Unexpected inference.");
    public IReadOnlyList<ModelStatus> GetModelStatuses() => Array.Empty<ModelStatus>();
}
