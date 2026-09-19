using ImageManager.App.Services;

var tests = new (string Name, Action Run)[]
{
    ("same directory overlaps in every recursion combination", SameDirectoryOverlaps),
    ("parent and child overlap only when the relevant side is recursive", ParentChildRecursionIsDirectional),
    ("different directories and similar prefixes do not overlap", DifferentDirectoriesDoNotOverlap),
    ("folder and file scopes honor casing, trailing separators, and selection mode", FolderAndFileScopes),
    ("Windows drive root handles descendants", DriveRootHandlesDescendants),
    ("UNC share root handles descendants", UncRootHandlesDescendants)
};

var failures = new List<string>();
foreach (var (name, run) in tests)
{
    try
    {
        run();
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
{
    Console.WriteLine($"All {tests.Length} FolderOperationScope regression tests passed.");
}

static void SameDirectoryOverlaps()
{
    var path = @"C:\Pictures\Events";
    foreach (var leftRecursive in new[] { false, true })
    foreach (var rightRecursive in new[] { false, true })
        Assert(FolderOperationScope.ForFolder(path, leftRecursive).Overlaps(FolderOperationScope.ForFolder(path, rightRecursive)),
            $"same directory should overlap ({leftRecursive}, {rightRecursive})");
}

static void ParentChildRecursionIsDirectional()
{
    var parent = @"C:\Pictures\Events";
    var child = @"C:\Pictures\Events\2026";

    Assert(!FolderOperationScope.ForFolder(parent, false).Overlaps(FolderOperationScope.ForFolder(child, false)), "nonrecursive parent vs child");
    Assert(FolderOperationScope.ForFolder(parent, true).Overlaps(FolderOperationScope.ForFolder(child, false)), "recursive parent vs child");
    Assert(!FolderOperationScope.ForFolder(parent, false).Overlaps(FolderOperationScope.ForFolder(child, true)), "recursive child must not include its parent scope");
}

static void DifferentDirectoriesDoNotOverlap()
{
    var left = FolderOperationScope.ForFolder(@"C:\Pictures\Events", true);
    Assert(!left.Overlaps(FolderOperationScope.ForFolder(@"C:\Pictures\Event", true)), "similar prefix must not overlap");
    Assert(!left.Overlaps(FolderOperationScope.ForFolder(@"C:\Pictures\Events2", true)), "similar prefix must not overlap");
    Assert(!left.Overlaps(FolderOperationScope.ForFolder(@"D:\Pictures\Events", true)), "different drive must not overlap");
}

static void FolderAndFileScopes()
{
    var recursive = FolderOperationScope.ForFolder(@"C:\Pictures\Events\", true);
    var direct = FolderOperationScope.ForFolder(@"C:\Pictures\Events\", false);
    var directFile = FolderOperationScope.ForFiles(new[] { @"c:\pictures\events\cover.jpg", @"C:\Pictures\Events\COVER.jpg" });
    var nestedFile = FolderOperationScope.ForFiles(new[] { @"C:\Pictures\Events\2026\cover.jpg" });
    var outsideFile = FolderOperationScope.ForFiles(new[] { @"C:\Pictures\Event\cover.jpg" });

    Assert(recursive.Overlaps(directFile), "recursive folder should include direct selected file");
    Assert(recursive.Overlaps(nestedFile), "recursive folder should include nested selected file");
    Assert(direct.Overlaps(directFile), "nonrecursive folder should include direct selected file");
    Assert(!direct.Overlaps(nestedFile), "nonrecursive folder should exclude nested selected file");
    Assert(!recursive.Overlaps(outsideFile), "similar prefix file must not overlap");
    Assert(!directFile.Overlaps(nestedFile), "selected files only overlap by exact normalized path");
    Assert(FolderOperationScope.ForFiles(new[] { @"C:\Pictures\Events\cover.jpg" }).Overlaps(directFile), "file selection is case-insensitive and de-duplicates");
}

static void DriveRootHandlesDescendants()
{
    var root = Path.GetPathRoot(Environment.CurrentDirectory);
    if (string.IsNullOrEmpty(root) || !Path.IsPathRooted(root) || root.Length < 3 || root[1] != ':')
        throw new InvalidOperationException($"Expected a Windows drive root, got '{root ?? "<null>"}'.");

    var child = Path.Combine(root, "ImageManagerScopeRegression", "child");
    Assert(FolderOperationScope.ForFolder(root, true).Overlaps(FolderOperationScope.ForFolder(child, false)), "drive root recursive scope should include child");
}

static void UncRootHandlesDescendants()
{
    const string share = @"\\server\share\";
    var child = share + @"folder\child";
    Assert(FolderOperationScope.ForFolder(share, true).Overlaps(FolderOperationScope.ForFolder(child, false)), "UNC share root recursive scope should include child");
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
