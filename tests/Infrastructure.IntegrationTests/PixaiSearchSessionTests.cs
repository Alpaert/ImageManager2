using ImageManager.Infrastructure.Services;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class PixaiSearchSessionTests
{
    [Fact]
    public void SearchSessionExtendsBoundedIdleTimeWithoutLoadingAndRestoresAfterLastLease()
    {
        using var service = new ObservablePixai();
        Assert.Equal(TimeSpan.FromMinutes(1), service.IdleLimit);
        var first = service.BeginSearchSession();
        var second = service.BeginSearchSession();
        Assert.False(service.IsModelLoaded);
        Assert.Equal(TimeSpan.FromMinutes(3), service.IdleLimit);
        first.Dispose();
        first.Dispose();
        Assert.Equal(TimeSpan.FromMinutes(3), service.IdleLimit);
        second.Dispose();
        Assert.Equal(TimeSpan.FromMinutes(1), service.IdleLimit);
        Assert.False(service.IsModelLoaded);
    }

    private sealed class ObservablePixai : PixaiTagService
    {
        public TimeSpan IdleLimit => ModelIdleTimeout;
    }
}
