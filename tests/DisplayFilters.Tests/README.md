# Display filter regression checks

From the repository root:

```powershell
dotnet run --project tests/DisplayFilters.Tests --no-restore
$env:AVALONIA_TELEMETRY_OPTOUT='1'
dotnet run --project tests/DisplayFilterIntegration.Tests --no-restore -p:BuildInParallel=false -p:UseSharedCompilation=false
```

The first console harness links the production filter/model/type registry. It checks classification, type-first metadata access, orientations, unknown sizes, stable order, and cancellation. The second uses the real MainWindowViewModel with a fake metadata repository to check published snapshots, paging, source changes, search return, deterministic stale-request rejection, and deletion while a reset publication is pending. It does not launch the desktop application or use the user's database.

Manual desktop acceptance:

- On a mixed folder apply video + portrait; unknown dimensions are retained by default, and can be excluded.
- Change the draft then press Escape or click outside: committed criteria stay unchanged. Reset in the panel changes only the draft until Apply.
- Remove each chip; clear-all also exits search. Back from search retains type/direction criteria.
- Change folders, sort, toggle recursive browsing, and add/delete files. Check count, pages, selection and empty-result reset.
- Filter Tag and ranked similarity results; relevance order stays intact. Strict-match navigation skips filtered-out results.
- Resize the window; the toolbar wraps and the summary occupies its own row only while filtering.
- Folder context-menu operations continue to use the explicit dialog target, not the display filter.
