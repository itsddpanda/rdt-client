using Microsoft.Extensions.Logging;
using Moq;
using RdtClient.Data.Data;
using RdtClient.Data.Enums;
using RdtClient.Data.Models.Data;
using RdtClient.Service.Helpers;
using RdtClient.Service.Services;

namespace RdtClient.Service.Test.Services;

public class TorrentRunnerTest
{
    [Fact]
    public async Task Tick_ShouldNotRequeueCompletedErrorTorrent()
    {
        var testSettings = new TestSettings();
        var runnerState = new TorrentRunnerState();

        testSettings.Current.Provider.ApiKey = "test-api-key";
        testSettings.Current.Provider.Provider = Provider.RealDebrid;
        testSettings.Current.Provider.MaxParallelDownloads = 1;
        testSettings.Current.DownloadClient.DownloadPath = "/downloads";

        var erroredTorrent = new Torrent
        {
            TorrentId = Guid.NewGuid(),
            Hash = "hash-1",
            RdName = "Torrent 1",
            Payload = new()
            {
                Content = "magnet:?xt=urn:btih:hash-1"
            },
            Type = DownloadType.Torrent,
            RdStatus = TorrentStatus.Queued,
            DeleteOnError = 10,
            Error = "Could not add to provider: Infringing file",
            Completed = DateTimeOffset.UtcNow.AddMinutes(-5),
            Downloads = new List<Download>()
        };

        var torrentDataMock = new Mock<ITorrentData>(MockBehavior.Strict);

        torrentDataMock.Setup(m => m.Get())
                       .ReturnsAsync(new List<Torrent>
                       {
                           erroredTorrent
                       });

        var torrents = new Torrents(Mock.Of<ILogger<Torrents>>(),
                                    torrentDataMock.Object,
                                    Mock.Of<IDownloads>(),
                                    null!,
                                    null!,
                                    null!,
                                    null!,
                                    null!,
                                    null!,
                                    null!,
                                    null!,
                                    null!,
                                    testSettings,
                                    runnerState);

        var torrentRunner = new TorrentRunner(Mock.Of<ILogger<TorrentRunner>>(),
                                              torrents,
                                              new(null!),
                                              new(null!, torrents),
                                              Mock.Of<IHttpClientFactory>(),
                                              new RateLimitCoordinator(),
                                              testSettings,
                                              runnerState);

        await torrentRunner.Tick();

        torrentDataMock.Verify(m => m.UpdateComplete(It.IsAny<Guid>(),
                                                     It.IsAny<String?>(),
                                                     It.IsAny<DateTimeOffset?>(),
                                                     It.IsAny<Boolean>()),
                               Times.Never);
    }

    [Fact]
    public async Task Tick_WhenTorrentInErrorPastDeleteOnError_ShouldDeleteTorrent()
    {
        var testSettings = new TestSettings();
        var runnerState = new TorrentRunnerState();

        testSettings.Current.Provider.ApiKey = "test-api-key";
        testSettings.Current.Provider.Provider = Provider.RealDebrid;
        testSettings.Current.Provider.MaxParallelDownloads = 1;
        testSettings.Current.DownloadClient.DownloadPath = "/downloads";

        var torrentId = Guid.NewGuid();
        var erroredTorrent = new Torrent
        {
            TorrentId = torrentId,
            Hash = "hash-err",
            RdName = "Torrent Error",
            Payload = new()
            {
                Content = "magnet:?xt=urn:btih:hash-err"
            },
            Type = DownloadType.Torrent,
            RdStatus = TorrentStatus.Finished,
            DeleteOnError = 1,
            Error = "1/1 downloads failed with errors",
            Completed = DateTimeOffset.UtcNow.AddMinutes(-2),
            Downloads = new List<Download>()
        };

        var torrentDataMock = new Mock<ITorrentData>(MockBehavior.Strict);

        torrentDataMock.Setup(m => m.Get())
                       .ReturnsAsync(new List<Torrent>
                       {
                           erroredTorrent
                       });

        torrentDataMock.Setup(m => m.GetById(torrentId))
                       .ReturnsAsync(erroredTorrent);

        torrentDataMock.Setup(m => m.UpdateComplete(torrentId, "Torrent deleted", It.IsAny<DateTimeOffset?>(), false))
                       .Returns(Task.CompletedTask);

        torrentDataMock.Setup(m => m.Delete(torrentId))
                       .Returns(Task.CompletedTask);

        var downloadsMock = new Mock<IDownloads>(MockBehavior.Strict);
        downloadsMock.Setup(m => m.GetForTorrent(torrentId))
                     .ReturnsAsync(new List<Download>());

        var torrents = new Torrents(Mock.Of<ILogger<Torrents>>(),
                                    torrentDataMock.Object,
                                    downloadsMock.Object,
                                    null!,
                                    null!,
                                    null!,
                                    null!,
                                    null!,
                                    null!,
                                    null!,
                                    null!,
                                    null!,
                                    testSettings,
                                    runnerState);

        var torrentRunner = new TorrentRunner(Mock.Of<ILogger<TorrentRunner>>(),
                                              torrents,
                                              new(null!),
                                              new(null!, torrents),
                                              Mock.Of<IHttpClientFactory>(),
                                              new RateLimitCoordinator(),
                                              testSettings,
                                              runnerState);

        await torrentRunner.Tick();

        torrentDataMock.Verify(m => m.Delete(torrentId), Times.Once);
    }
}
