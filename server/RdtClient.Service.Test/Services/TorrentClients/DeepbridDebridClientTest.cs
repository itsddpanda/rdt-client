using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Moq;
using RdtClient.Data.Enums;
using RdtClient.Data.Models.Data;
using RdtClient.Data.Models.DebridClient;
using RdtClient.Data.Models.Internal;
using RdtClient.Service.Helpers;
using RdtClient.Service.Services;
using RdtClient.Service.Services.DebridClients;

namespace RdtClient.Service.Test.Services.TorrentClients;

public class DeepbridDebridClientTest
{
    private readonly Mock<IDownloadableFileFilter> _fileFilterMock;
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<ILogger<DeepbridDebridClient>> _loggerMock;
    private readonly TestSettings _settings;

    public DeepbridDebridClientTest()
    {
        _loggerMock = new();
        _httpClientFactoryMock = new();
        _fileFilterMock = new();
        _settings = new();
        _settings.Current.Provider.ApiKey = "test-deepbrid-api-key";
    }

    [Fact]
    public async Task GetUser_WhenPremiumUser_ReturnsUserWithExpiration()
    {
        // Arrange
        var json = """
        {
            "username": "john_doe",
            "email": "john@example.com",
            "type": "premium",
            "expiration": "2026-12-31 23:59:59",
            "maxDownloads": 5,
            "maxConnections": 1
        }
        """;
        var handler = new RecordingHttpMessageHandler(_ => JsonResponse(json));
        var client = CreateClient(handler);

        // Act
        var user = await client.GetUser();

        // Assert
        Assert.Equal("john_doe", user.Username);
        Assert.NotNull(user.Expiration);
        Assert.Equal(2026, user.Expiration.Value.Year);
        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal("https://www.deepbrid.com/api/v1/user", handler.Request.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("test-deepbrid-api-key", handler.Request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task GetUser_WhenFreeUser_ReturnsNullExpiration()
    {
        // Arrange
        var json = """
        {
            "username": "free_user",
            "email": "free@example.com",
            "type": "free",
            "expiration": null
        }
        """;
        var handler = new RecordingHttpMessageHandler(_ => JsonResponse(json));
        var client = CreateClient(handler);

        // Act
        var user = await client.GetUser();

        // Assert
        Assert.Equal("free_user", user.Username);
        Assert.Null(user.Expiration);
    }

    [Fact]
    public async Task GetUser_WhenApiError_ThrowsException()
    {
        // Arrange
        var json = """{"error": 401, "message": "Authentication required."}""";
        var handler = new RecordingHttpMessageHandler(_ => JsonResponse(json, HttpStatusCode.Unauthorized));
        var client = CreateClient(handler);

        // Act & Assert
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetUser());
    }

    [Fact]
    public async Task AddTorrentMagnet_ValidMagnet_ReturnsTorrentId()
    {
        // Arrange
        var json = """{"error": 0, "message": "OK", "id": "14648", "filename": "test.torrent", "progress": 0}""";
        var handler = new RecordingHttpMessageHandler(_ => JsonResponse(json));
        var client = CreateClient(handler);

        // Act
        var result = await client.AddTorrentMagnet("magnet:?xt=urn:btih:abcdef1234567890");

        // Assert
        Assert.Equal("14648", result);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://www.deepbrid.com/api/v1/torrents/add", handler.Request.RequestUri!.ToString());
        Assert.Equal("application/x-www-form-urlencoded", handler.Request.Content!.Headers.ContentType!.MediaType);
        Assert.Contains("magnet=magnet%3A%3Fxt%3Durn%3Abtih%3Aabcdef1234567890", handler.RequestBody);
    }

    [Fact]
    public async Task AddTorrentMagnet_WhenRateLimited_ThrowsRateLimitException()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ => JsonResponse("Too many requests", HttpStatusCode.TooManyRequests));
        var client = CreateClient(handler);

        // Act & Assert
        await Assert.ThrowsAsync<RateLimitException>(() => client.AddTorrentMagnet("magnet:?xt=urn:btih:abc"));
    }

    [Fact]
    public async Task AddTorrentFile_ValidBytes_ReturnsTorrentId()
    {
        // Arrange
        var json = """{"error": 0, "message": "OK", "id": 14650, "filename": "file.mkv"}""";
        var handler = new RecordingHttpMessageHandler(_ => JsonResponse(json));
        var client = CreateClient(handler);

        // Act
        var result = await client.AddTorrentFile(Encoding.UTF8.GetBytes("fake torrent content"));

        // Assert
        Assert.Equal("14650", result);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://www.deepbrid.com/api/v1/torrents/add", handler.Request.RequestUri!.ToString());
        Assert.Equal("multipart/form-data", handler.Request.Content!.Headers.ContentType!.MediaType);
        Assert.Contains("torrent_file", handler.RequestBody);
        Assert.Contains("fake torrent content", handler.RequestBody);
    }

    [Fact]
    public async Task AddNzbLink_ValidUrl_ReturnsNzbId()
    {
        // Arrange
        var json = """{"error": 0, "message": "OK", "id": "1024"}""";
        var handler = new RecordingHttpMessageHandler(_ => JsonResponse(json));
        var client = CreateClient(handler);

        // Act
        var result = await client.AddNzbLink("https://example.com/test.nzb");

        // Assert
        Assert.Equal("1024", result);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://www.deepbrid.com/api/v1/usenet/add", handler.Request.RequestUri!.ToString());
        Assert.Contains("nzb_url=https%3A%2F%2Fexample.com%2Ftest.nzb", handler.RequestBody);
    }

    [Fact]
    public async Task AddNzbFile_ValidBytes_ReturnsNzbId()
    {
        // Arrange
        var json = """{"error": 0, "message": "OK", "id": 1025}""";
        var handler = new RecordingHttpMessageHandler(_ => JsonResponse(json));
        var client = CreateClient(handler);

        // Act
        var result = await client.AddNzbFile(Encoding.UTF8.GetBytes("fake nzb content"), "movie.nzb");

        // Assert
        Assert.Equal("1025", result);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://www.deepbrid.com/api/v1/usenet/add", handler.Request.RequestUri!.ToString());
        Assert.Equal("multipart/form-data", handler.Request.Content!.Headers.ContentType!.MediaType);
        Assert.Contains("nzb_file", handler.RequestBody);
        Assert.Contains("movie.nzb", handler.RequestBody);
    }

    [Fact]
    public async Task GetDownloads_CombinesTorrentsAndUsenet()
    {
        // Arrange
        var torrentsJson = """
        {
            "1": {
                "id": "14648",
                "filename": "ubuntu.iso",
                "progress": 100,
                "seeders": 10,
                "speed": "0.00 MB/s",
                "links": ["https://www.deepbrid.com/mytorrents?torrent=14648&file=abc"]
            },
            "2": {
                "id": "14649",
                "filename": "movie.mkv",
                "progress": 50,
                "seeders": 4,
                "speed": "2.5 MB/s",
                "links": []
            }
        }
        """;
        var usenetJson = """
        {
            "error": 0,
            "count": 1,
            "items": [
                {
                    "id": 1024,
                    "title": "sample.nzb",
                    "hash": "hash123",
                    "added_at": "2026-06-05 18:22:10"
                }
            ]
        }
        """;

        var handler = new RecordingHttpMessageHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("/torrents/info"))
            {
                return JsonResponse(torrentsJson);
            }
            if (req.RequestUri!.ToString().Contains("/usenet/uploads"))
            {
                return JsonResponse(usenetJson);
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var client = CreateClient(handler);

        // Act
        var downloads = await client.GetDownloads();

        // Assert
        Assert.Equal(3, downloads.Count);
        var ubuntu = downloads.First(d => d.Id == "14648");
        Assert.Equal("ubuntu.iso", ubuntu.Filename);
        Assert.Equal(100, ubuntu.Progress);
        Assert.Equal("downloaded", ubuntu.Status);

        var nzb = downloads.First(d => d.Id == "1024");
        Assert.Equal("sample.nzb", nzb.Filename);
        Assert.Equal("hash123", nzb.Hash);
    }

    [Fact]
    public async Task GetDownloadInfos_WhenTorrentFinished_ReturnsDownloadLinks()
    {
        // Arrange
        var json = """
        {
            "error": 0,
            "id": "14648",
            "filename": "ubuntu.iso",
            "progress": 100,
            "seeders": 12,
            "speed": "0.00 MB/s",
            "links": [
                "https://www.deepbrid.com/mytorrents?torrent=14648&file=ubuntu.iso"
            ]
        }
        """;
        var handler = new RecordingHttpMessageHandler(_ => JsonResponse(json));
        var client = CreateClient(handler);

        var torrent = new Torrent
        {
            RdId = "14648",
            Type = DownloadType.Torrent,
            RdName = "ubuntu.iso"
        };

        // Act
        var infos = await client.GetDownloadInfos(torrent);

        // Assert
        Assert.NotNull(infos);
        Assert.Single(infos);
        Assert.Equal("https://www.deepbrid.com/mytorrents?torrent=14648&file=ubuntu.iso", infos[0].RestrictedLink);
    }

    [Fact]
    public async Task GetDownloadInfos_WhenTorrentDownloading_ReturnsNull()
    {
        // Arrange
        var json = """
        {
            "error": 0,
            "id": "14649",
            "filename": "movie.mkv",
            "progress": 42,
            "seeders": 3,
            "speed": "1.5 MB/s",
            "links": []
        }
        """;
        var handler = new RecordingHttpMessageHandler(_ => JsonResponse(json));
        var client = CreateClient(handler);

        var torrent = new Torrent
        {
            RdId = "14649",
            Type = DownloadType.Torrent,
            RdName = "movie.mkv"
        };

        // Act
        var infos = await client.GetDownloadInfos(torrent);

        // Assert
        Assert.Null(infos);
    }

    [Fact]
    public async Task GetDownloadInfos_WhenNzbFinished_ReturnsFileLinks()
    {
        // Arrange
        var json = """
        {
            "error": 0,
            "id": 1024,
            "title": "My.NZB",
            "files": [
                {
                    "name": "sample.mkv",
                    "size": 104857600,
                    "link": "https://premium-dl.deepbrid.com/d/file1"
                },
                {
                    "name": "sample.nfo",
                    "size": 1024,
                    "link": "https://premium-dl.deepbrid.com/d/file2"
                }
            ]
        }
        """;
        var handler = new RecordingHttpMessageHandler(_ => JsonResponse(json));
        var client = CreateClient(handler);

        var torrent = new Torrent
        {
            RdId = "1024",
            Type = DownloadType.Nzb,
            RdName = "My.NZB"
        };

        // Act
        var infos = await client.GetDownloadInfos(torrent);

        // Assert
        Assert.NotNull(infos);
        Assert.Equal(2, infos.Count);
        Assert.Equal("https://premium-dl.deepbrid.com/d/file1", infos[0].RestrictedLink);
        Assert.Equal("sample.mkv", infos[0].FileName);
    }

    [Fact]
    public async Task Unrestrict_WhenDeepbridTorrentLink_ResolvesRedirectLocation()
    {
        // Arrange
        var redirectUrl = "https://n.myfast.link/n/dl/torrent/d26a4b0045/The.Invite.2026.1080p.WEBRip.x264.AAC5.1-YTS.GG+-+YTS.BZ.mp4";
        var redirectResponse = new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri(redirectUrl) }
        };
        var handler = new RecordingHttpMessageHandler(_ => redirectResponse);
        var client = CreateClient(handler);

        var torrent = new Torrent
        {
            RdId = "40910"
        };

        // Act
        var result = await client.Unrestrict(torrent, "https://www.deepbrid.com/mytorrents?torrent=40910&file=blNlLL&opt=.jdeatme");

        // Assert
        Assert.Equal(redirectUrl, result);
        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal("https://www.deepbrid.com/mytorrents?torrent=40910&file=blNlLL&opt=.jdeatme", handler.Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Unrestrict_GeneratesPremiumLink_ReturnsDirectLink()
    {
        // Arrange
        var json = """
        {
            "error": 0,
            "message": "OK",
            "filename": "video.mp4",
            "link": "https://premium-dl.deepbrid.com/d/abcdef123456"
        }
        """;
        var handler = new RecordingHttpMessageHandler(_ => JsonResponse(json));
        var client = CreateClient(handler);

        var torrent = new Torrent
        {
            RdId = "123"
        };

        // Act
        var result = await client.Unrestrict(torrent, "https://rapidgator.net/file/123/video.mp4");

        // Assert
        Assert.Equal("https://premium-dl.deepbrid.com/d/abcdef123456", result);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://www.deepbrid.com/api/v1/generate/link", handler.Request.RequestUri!.ToString());
        Assert.Contains("link=https%3A%2F%2Frapidgator.net%2Ffile%2F123%2Fvideo.mp4", handler.RequestBody);
    }

    [Fact]
    public async Task UpdateData_UpdatesTorrentProperties()
    {
        // Arrange
        var json = """
        {
            "error": 0,
            "id": "14648",
            "filename": "ubuntu-24.04.iso",
            "progress": 100,
            "seeders": 15,
            "speed": "0.00 MB/s",
            "links": ["https://www.deepbrid.com/mytorrents?torrent=14648&file=ubuntu-24.04.iso"]
        }
        """;
        var handler = new RecordingHttpMessageHandler(_ => JsonResponse(json));
        var client = CreateClient(handler);

        var torrent = new Torrent
        {
            RdId = "14648",
            Type = DownloadType.Torrent
        };

        // Act
        var updated = await client.UpdateData(torrent, null);

        // Assert
        Assert.Equal("ubuntu-24.04.iso", updated.RdName);
        Assert.Equal(100, updated.RdProgress);
        Assert.Equal(Provider.Deepbrid, updated.ClientKind);
        Assert.Equal(TorrentStatus.Finished, updated.RdStatus);
        Assert.Equal(15, updated.RdSeeders);
    }

    [Fact]
    public async Task GetFileName_ExtractsFromQueryParamOrPath()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ => JsonResponse("{}"));
        var client = CreateClient(handler);

        var downloadWithParam = new Download
        {
            Link = "https://www.deepbrid.com/mytorrents?torrent=14648&file=test-movie.mkv&opt=.jdeatme"
        };
        var downloadWithPath = new Download
        {
            Link = "https://premium-dl.deepbrid.com/d/abc123xyz/sample-video.mp4"
        };
        var downloadWithToken = new Download
        {
            Link = "https://www.deepbrid.com/mytorrents?torrent=14648&file=blNlLL&opt=.jdeatme"
        };

        // Act
        var filename1 = await client.GetFileName(downloadWithParam);
        var filename2 = await client.GetFileName(downloadWithPath);
        var filename3 = await client.GetFileName(downloadWithToken);

        // Assert
        Assert.Equal("test-movie.mkv", filename1);
        Assert.Equal("sample-video.mp4", filename2);
        Assert.Equal("mytorrents", filename3); // Not the raw token without extension
    }

    [Fact]
    public async Task SelectFiles_ReturnsFileCount()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ => JsonResponse("{}"));
        var client = CreateClient(handler);

        var files = new List<DebridClientFile>
        {
            new() { Path = "file1.mkv" },
            new() { Path = "file2.nfo" }
        };

        var torrent = new Torrent
        {
            RdFiles = Newtonsoft.Json.JsonConvert.SerializeObject(files)
        };

        _fileFilterMock.Setup(f => f.IsDownloadable(It.IsAny<Torrent>(), It.IsAny<String>(), It.IsAny<Int64>())).Returns(true);

        // Act
        var count = await client.SelectFiles(torrent);

        // Assert
        Assert.Equal(2, count);
    }

    private DeepbridDebridClient CreateClient(RecordingHttpMessageHandler handler)
    {
        _httpClientFactoryMock.Setup(m => m.CreateClient(It.IsAny<String>())).Returns(() => new HttpClient(handler, disposeHandler: false));

        return new(_loggerMock.Object, _httpClientFactoryMock.Object, _fileFilterMock.Object, _settings);
    }

    private static HttpResponseMessage JsonResponse(String json, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private class RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public String? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            return handler(request);
        }
    }
}
