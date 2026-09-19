# Incremental auto-tag regression harness

Run from the repository root:

```powershell
dotnet run --project tests/AutoTagPreparation.Tests/AutoTagPreparation.Tests.csproj -p:UseSharedCompilation=false -p:BuildInParallel=false
```

This console harness uses disposable SQLite databases and no GPU/model files. A failed assertion returns a nonzero exit code. Cases cover index migration/query plans, skipping 20,000 tagged paths in a 140,000-row library, path casing, duplicates, new/missing/moved files, concurrent registration, early cancellation, overlapping runs, model-free skip behavior and bounded SQLite lock cancellation.

The benchmark prints unindexed/indexed lookup and full preparation times on identical synthetic records. It does not predict cold-disk, network-directory, model-loading or image-hashing performance.

Manual desktop checks: tag an already tagged folder (should show skipped count without model loading); tag a folder with new images (scan/filter/prepare/register/model/inference progress); stop during scanning or preparation; run while background hash maintenance is active; check moved files retain their labels.
