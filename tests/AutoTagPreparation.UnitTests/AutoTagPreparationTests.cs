using ImageManager.Core.Models;
using ImageManager.Infrastructure.Services;
using Xunit;

namespace AutoTagPreparation.UnitTests;

public sealed class AutoTagPreparationTests
{
    [Fact]
    public async Task PreparationHonorsCancellationBeforeRepositoryAccess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var preparation = new AutoTagPreparationService(null!);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            preparation.PrepareAsync(1, new[] { "C:/images/photo.png" }, ct: cancellation.Token));
    }

    [Fact]
    public void RegistrationPreservesMovedImageMetadata()
    {
        var metadata = new ImageMeta
        {
            Id = 42,
            FilePath = "C:/images/new.png",
            FileHash = "abc123",
            FolderId = 7
        };

        var registration = new AutoTagFileRegistration(metadata, 19, "C:/images/old.png");

        Assert.Equal(metadata, registration.Metadata);
        Assert.Equal(19, registration.MovedImageId);
        Assert.Equal("C:/images/old.png", registration.PreviousPath);
    }
}
