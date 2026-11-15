using System.Diagnostics;
using System.Reflection;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using ImageManager.App.Helpers;
using ImageManager.App.Services;
using ImageManager.App.ViewModels;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Caching;
using ImageManager.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

internal static class RegionSearchResponsivenessChecks
{
    public static void Run()
    {
        var repository = DispatchProxy.Create<IImageMetaRepository, MetadataProxy>();
        var messenger = new WeakReferenceMessenger();
        using var cache = new ThumbnailCacheService(new MediaProcessorFactory(null!),
            Path.Combine(Path.GetTempPath(), "ImageManager-RegionSearch-Responsiveness"));
        var page = new PageManager(cache, null!, metaRepo: repository);
        using var search = new SlowCandidateReader();
        using var services = new ServiceCollection().AddSingleton<IRegionSearchService>(search).BuildServiceProvider();
        var servicesProperty = typeof(ImageManager.App.App).GetProperty("Services")!;
        var previous = servicesProperty.GetValue(null);
        servicesProperty.SetValue(null, services);
        try
        {
            var vm = new MainWindowViewModel(null!, null!, repository, null!, null!, null!, null!, cache,
                page, new TagSearchEngine(repository, messenger, ImagePaging.Default), null!, null!, null!,
                messenger, new AvaloniaDispatcher(), regionSearchService: search);
            var watch = Stopwatch.StartNew();
            var request = vm.SearchRegionAsync("fixture.png", new ImageRegion(0, 0, 8, 8), RegionSearchMode.SimilarContent);
            var returnedPromptly = watch.Elapsed < TimeSpan.FromMilliseconds(500);
            var entered = search.Entered.Wait(TimeSpan.FromSeconds(3));
            try
            {
                if (!entered || !returnedPromptly || search.RanOnUi)
                    throw new InvalidOperationException("Similar content search blocked the Avalonia UI during candidate loading.");

                Dispatcher.UIThread.Post(() => vm.StopSearchCommand.Execute(null));
                PumpUntil(() => request.IsCompleted, TimeSpan.FromMilliseconds(700));
                if (!request.IsCompletedSuccessfully || search.WorkerReturned.IsSet || vm.StatusText != "搜索已取消")
                    throw new InvalidOperationException("Stop search did not complete before the uncancelable worker returned.");
            }
            finally
            {
                search.Release.Set();
                PumpUntil(() => search.WorkerReturned.IsSet, TimeSpan.FromSeconds(3));
            }

            // Give any late publication queued by the worker time to run.
            PumpUntil(() => vm.HasSearchResults, TimeSpan.FromMilliseconds(250));
            if (!search.WorkerReturned.IsSet || !search.SawCancellation || vm.HasSearchResults ||
                vm.IsShowingSearchResult || vm.StatusText != "搜索已取消")
                throw new InvalidOperationException("Late successful results changed the canceled search state.");
            Console.WriteLine("Region search remains responsive and handles cancellation.");
        }
        finally
        {
            servicesProperty.SetValue(null, previous);
        }
    }

    private static void PumpUntil(Func<bool> completed, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!completed() && watch.Elapsed < timeout)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class SlowCandidateReader : IRegionSearchService, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public ManualResetEventSlim WorkerReturned { get; } = new();
        public bool RanOnUi { get; private set; }
        public bool SawCancellation { get; private set; }

        public Task<RegionSearchResponse> SearchAsync(string path, ImageRegion? region,
            IReadOnlyCollection<string> candidates, RegionSearchMode mode, int limit, CancellationToken ct = default)
        {
            RanOnUi = Dispatcher.UIThread.CheckAccess();
            Entered.Set();
            Release.Wait();
            SawCancellation = ct.IsCancellationRequested;
            var response = Task.FromResult(new RegionSearchResponse(
                [new SimilaritySearchResult("late-result.png", 1)], 1, 1));
            WorkerReturned.Set();
            return response;
        }

        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
            WorkerReturned.Dispose();
        }
    }
}
