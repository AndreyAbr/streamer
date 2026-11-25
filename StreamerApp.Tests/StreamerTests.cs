using System.Text.RegularExpressions;
using StreamerApp.Models;
using StreamerApp.Services;
using Xunit;

namespace StreamerApp.Tests;

public class StreamerTests
{
    [Fact]
    public void StreamSettings_GenerateRoomCode_MatchesExpectedFormat()
    {
        for (int i = 0; i < 10; i++)
        {
            var code = StreamSettings.GenerateRoomCode();
            Assert.Matches(@"^\d{3}-\d{3}$", code);
        }
    }

    [Theory]
    [InlineData("1280x720", 2560, 1440, 1280, 720)]
    [InlineData("1920x1080", 2560, 1440, 1920, 1080)]
    [InlineData("Original", 2560, 1440, 2560, 1440)]
    public void StreamSettings_GetTargetDimensions_ReturnsExpectedSize(
        string resolution, int srcW, int srcH, int expW, int expH)
    {
        var settings = new StreamSettings { Resolution = resolution };
        var (w, h) = settings.GetTargetDimensions(srcW, srcH);
        Assert.Equal(expW, w);
        Assert.Equal(expH, h);
    }

    [Fact]
    public void ConnectedViewer_RaisesPropertyChangedAndUpdatesStatusDisplay()
    {
        var viewer = new ConnectedViewer();
        var changed = new List<string?>();
        viewer.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal("Зритель", viewer.StatusDisplay);

        viewer.Nickname = "Alex";
        viewer.PingMs = 24;
        viewer.IsHandRaised = true;

        Assert.Equal("Поднята рука", viewer.StatusDisplay);
        Assert.Contains(nameof(ConnectedViewer.Nickname), changed);
        Assert.Contains(nameof(ConnectedViewer.PingMs), changed);
        Assert.Contains(nameof(ConnectedViewer.IsHandRaised), changed);
        Assert.Contains(nameof(ConnectedViewer.StatusDisplay), changed);
    }

    [Fact]
    public void BroadcastStateService_ManagesViewersChatAndBackend()
    {
        var svc = new BroadcastStateService();
        string? changedBackend = null;
        svc.OnBackendChanged += b => changedBackend = b;

        svc.AddOrUpdateViewer("conn-1", "UserOne", "192.168.1.10");
        Assert.Equal(1, svc.ViewersCount);
        Assert.Equal("UserOne", svc.GetViewer("conn-1")!.Nickname);

        svc.UpdatePing("conn-1", 15);
        svc.SetHandRaised("conn-1", true);
        var dto = svc.GetAllViewersDto().Single();
        Assert.True(dto.IsHandRaised);
        Assert.Equal(15, svc.GetViewer("conn-1")!.PingMs);

        svc.AddChatMessage("Host", "Добро пожаловать", isHost: true);
        var msgs = svc.GetRecentChatMessagesDto();
        Assert.Single(msgs);
        Assert.Equal("Host", msgs[0].Sender);
        Assert.True(msgs[0].IsHost);

        svc.SetActiveBackend("GDI (CPU)");
        Assert.Equal("GDI (CPU)", svc.ActiveBackendName);
        Assert.Equal("GDI (CPU)", changedBackend);

        svc.RemoveViewer("conn-1");
        Assert.Equal(0, svc.ViewersCount);
    }
}
