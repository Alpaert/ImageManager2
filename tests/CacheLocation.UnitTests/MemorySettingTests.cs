using ImageManager.App.ViewModels;
using Xunit;

namespace CacheLocation.UnitTests;

public class MemorySettingTests
{
    private static MemorySettingViewModel Create(Func<bool, Task<bool>> save) => new(
        @"G:\ImageManagerCarche", "", 1, 75, 0.3, 0.35, true, 0.35, 1, 0.15, null, save);

    [Fact]
    public async Task SaveAwaitsCompletionAndRejectsDuplicateSubmission()
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var vm = Create(_ => { calls++; return result.Task; });
        var first = vm.SaveSettingsAsync();
        Assert.True(vm.IsSaving);
        Assert.False(first.IsCompleted);
        Assert.False(await vm.SaveSettingsAsync());
        Assert.Equal(1, calls);
        result.SetResult(true);
        Assert.True(await first);
        Assert.False(vm.IsSaving);
    }

    [Fact]
    public async Task FailedSaveRetainsChangedPathForRetryAndSuccessResetsIt()
    {
        var changes = new List<bool>();
        var vm = Create(changed => { changes.Add(changed); return Task.FromResult(changes.Count > 1); });
        vm.CachePath = @"G:\chosen";
        Assert.False(await vm.SaveSettingsAsync());
        Assert.False(vm.IsSaving);
        Assert.True(await vm.SaveSettingsAsync());
        Assert.True(await vm.SaveSettingsAsync());
        Assert.Equal(new[] { true, true, false }, changes);
    }

    [Fact]
    public async Task UnexpectedFailureReleasesSaveGuard()
    {
        var vm = Create(_ => throw new IOException("save failed"));
        await Assert.ThrowsAsync<IOException>(() => vm.SaveSettingsAsync());
        Assert.False(vm.IsSaving);
    }
}
