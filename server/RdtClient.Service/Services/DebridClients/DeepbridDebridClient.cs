using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using RdtClient.Data.Enums;
using RdtClient.Data.Models.Data;
using RdtClient.Data.Models.DebridClient;
using RdtClient.Data.Models.Internal;
using RdtClient.Service.Helpers;
using Download = RdtClient.Data.Models.Data.Download;
using JsonSerializer = System.Text.Json.JsonSerializer;
using Torrent = RdtClient.Data.Models.Data.Torrent;

namespace RdtClient.Service.Services.DebridClients;

public class DeepbridDebridClient(
    ILogger<DeepbridDebridClient> logger,
    IHttpClientFactory httpClientFactory,
    IDownloadableFileFilter fileFilter,
    ISettings settings)
    : IDebridClient
{
    private const String BaseUrl = "https://www.deepbrid.com/api/v1/";

    public async Task<IList<DebridClientTorrent>> GetDownloads()
    {
        var results = new List<DebridClientTorrent>();

        try
        {
            var torrents = await GetAllTorrents();
            if (torrents != null)
            {
                results.AddRange(torrents);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get torrents from Deepbrid: {Message}", ex.Message);
        }

        try
        {
            var nzbs = await GetAllUsenetUploads();
            if (nzbs != null)
            {
                results.AddRange(nzbs);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get Usenet uploads from Deepbrid: {Message}", ex.Message);
        }

        return results;
    }

    public async Task<DebridClientUser> GetUser()
    {
        var json = await SendRequestAsync(HttpMethod.Get, "/user");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        CheckForApiError(root);

        var username = root.TryGetProperty("username", out var u) ? u.GetString() : null;
        if (String.IsNullOrWhiteSpace(username) && root.TryGetProperty("email", out var e))
        {
            username = e.GetString();
        }

        DateTimeOffset? expiration = null;
        if (root.TryGetProperty("expiration", out var expProp) && expProp.ValueKind == JsonValueKind.String)
        {
            var expStr = expProp.GetString();
            if (!String.IsNullOrWhiteSpace(expStr) && DateTimeOffset.TryParse(expStr, out var parsedExp))
            {
                expiration = parsedExp;
            }
        }

        var isPremium = false;
        if (root.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String)
        {
            isPremium = String.Equals(typeProp.GetString(), "premium", StringComparison.OrdinalIgnoreCase);
        }

        return new()
        {
            Username = username ?? "Deepbrid User",
            Expiration = isPremium ? expiration ?? DateTimeOffset.MaxValue : null
        };
    }

    public async Task<String> AddTorrentMagnet(String magnetLink)
    {
        var form = new Dictionary<String, String>
        {
            { "magnet", magnetLink }
        };

        var json = await SendRequestAsync(HttpMethod.Post, "/torrents/add", new FormUrlEncodedContent(form));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        CheckForApiError(root);

        var id = GetIdProperty(root);
        if (String.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("Deepbrid API did not return torrent ID.");
        }

        return id;
    }

    public async Task<String> AddTorrentFile(Byte[] bytes)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-bittorrent");
        content.Add(fileContent, "torrent_file", "file.torrent");

        var json = await SendRequestAsync(HttpMethod.Post, "/torrents/add", content);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        CheckForApiError(root);

        var id = GetIdProperty(root);
        if (String.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("Deepbrid API did not return torrent ID.");
        }

        return id;
    }

    public async Task<String> AddNzbLink(String nzbLink)
    {
        var form = new Dictionary<String, String>
        {
            { "nzb_url", nzbLink }
        };

        var json = await SendRequestAsync(HttpMethod.Post, "/usenet/add", new FormUrlEncodedContent(form));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        CheckForApiError(root);

        var id = GetIdProperty(root);
        if (String.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("Deepbrid API did not return NZB ID.");
        }

        return id;
    }

    public async Task<String> AddNzbFile(Byte[] bytes, String? name)
    {
        var fileName = GetNzbFileName(name);

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-nzb");
        content.Add(fileContent, "nzb_file", fileName);

        var json = await SendRequestAsync(HttpMethod.Post, "/usenet/add", content);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        CheckForApiError(root);

        var id = GetIdProperty(root);
        if (String.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("Deepbrid API did not return NZB ID.");
        }

        return id;
    }

    public Task<IList<DebridClientAvailableFile>> GetAvailableFiles(String hash)
    {
        return Task.FromResult<IList<DebridClientAvailableFile>>([]);
    }

    public Task<Int32?> SelectFiles(Torrent torrent)
    {
        var files = torrent.Files.Where(f => fileFilter.IsDownloadable(torrent, f.Path, f.Bytes)).ToList();
        var count = files.Count > 0 ? files.Count : (torrent.Files.Count > 0 ? torrent.Files.Count : 1);
        return Task.FromResult<Int32?>(count);
    }

    public Task Delete(Torrent torrent)
    {
        Log("Deepbrid API does not support remote torrent/NZB deletion; skipping delete", torrent);
        return Task.CompletedTask;
    }

    public async Task<String> Unrestrict(Torrent torrent, String link)
    {
        try
        {
            var form = new Dictionary<String, String>
            {
                { "link", link }
            };

            var json = await SendRequestAsync(HttpMethod.Post, "/generate/link", new FormUrlEncodedContent(form));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            CheckForApiError(root);

            if (root.TryGetProperty("link", out var linkProp) && linkProp.ValueKind == JsonValueKind.String)
            {
                var directLink = linkProp.GetString();
                if (!String.IsNullOrWhiteSpace(directLink))
                {
                    return directLink;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Unrestrict failed for link {Link}, falling back to raw link: {Message}", link, ex.Message);
        }

        return link;
    }

    public async Task<Torrent> UpdateData(Torrent torrent, DebridClientTorrent? torrentClientTorrent)
    {
        try
        {
            if (torrent.RdId == null)
            {
                return torrent;
            }

            if (torrentClientTorrent == null)
            {
                torrentClientTorrent = await GetSingleDownload(torrent.RdId, torrent.Type);
            }

            if (torrentClientTorrent == null)
            {
                throw new("Resource not found");
            }

            if (!String.IsNullOrWhiteSpace(torrentClientTorrent.Filename))
            {
                torrent.RdName = torrentClientTorrent.Filename;
            }

            if (!String.IsNullOrWhiteSpace(torrentClientTorrent.OriginalFilename))
            {
                torrent.RdName = torrentClientTorrent.OriginalFilename;
            }

            if (torrentClientTorrent.Bytes > 0)
            {
                torrent.RdSize = torrentClientTorrent.Bytes;
            }
            else if (torrentClientTorrent.OriginalBytes > 0)
            {
                torrent.RdSize = torrentClientTorrent.OriginalBytes;
            }

            if (torrentClientTorrent.Files != null && torrentClientTorrent.Files.Count > 0)
            {
                torrent.RdFiles = JsonConvert.SerializeObject(torrentClientTorrent.Files);
            }

            torrent.ClientKind = Provider.Deepbrid;
            torrent.RdHost = torrentClientTorrent.Host;
            torrent.RdSplit = torrentClientTorrent.Split;
            torrent.RdProgress = torrentClientTorrent.Progress;
            torrent.RdAdded = torrentClientTorrent.Added;
            torrent.RdEnded = torrentClientTorrent.Ended;
            torrent.RdSpeed = torrentClientTorrent.Speed;
            torrent.RdSeeders = torrentClientTorrent.Seeders;
            torrent.RdStatusRaw = torrentClientTorrent.Status;

            torrent.RdStatus = torrentClientTorrent.Status switch
            {
                "downloaded" => TorrentStatus.Finished,
                "finished" => TorrentStatus.Finished,
                "completed" => TorrentStatus.Finished,
                "downloading" => TorrentStatus.Downloading,
                "processing" => TorrentStatus.Processing,
                "queued" => TorrentStatus.Downloading,
                "error" => TorrentStatus.Error,
                _ => (torrentClientTorrent.Progress >= 100 ? TorrentStatus.Finished : TorrentStatus.Downloading)
            };
        }
        catch (Exception ex)
        {
            if (ex.Message == "Resource not found")
            {
                torrent.RdStatusRaw = "deleted";
            }
            else
            {
                throw;
            }
        }

        return torrent;
    }

    public async Task<IList<DownloadInfo>?> GetDownloadInfos(Torrent torrent)
    {
        if (torrent.RdId == null)
        {
            return null;
        }

        var item = await GetSingleDownload(torrent.RdId, torrent.Type);
        if (item == null)
        {
            return null;
        }

        // For torrents: check if progress is complete and links are available
        if (torrent.Type == DownloadType.Torrent)
        {
            if (item.Progress < 100 && (item.Links == null || item.Links.Count == 0))
            {
                return null;
            }

            if (item.Links == null || item.Links.Count == 0)
            {
                return null;
            }

            var links = item.Links
                            .Where(m => !String.IsNullOrWhiteSpace(m))
                            .Select(l => new DownloadInfo
                            {
                                RestrictedLink = l,
                                FileName = null
                            })
                            .ToList();

            Log($"Found {links.Count} download links for torrent {torrent.RdName}", torrent);
            return links;
        }

        // For NZB: check if files are available
        if (item.Files != null && item.Files.Count > 0)
        {
            var links = new List<DownloadInfo>();
            foreach (var f in item.Files)
            {
                if (item.Links != null && item.Links.Count == item.Files.Count)
                {
                    var index = item.Files.IndexOf(f);
                    links.Add(new()
                    {
                        RestrictedLink = item.Links[index],
                        FileName = f.Path
                    });
                }
                else if (!String.IsNullOrWhiteSpace(f.Path))
                {
                    links.Add(new()
                    {
                        RestrictedLink = f.Path,
                        FileName = Path.GetFileName(f.Path)
                    });
                }
            }

            if (links.Count > 0)
            {
                Log($"Found {links.Count} files for NZB {torrent.RdName}", torrent);
                return links;
            }
        }

        if (item.Links != null && item.Links.Count > 0)
        {
            return item.Links
                       .Where(m => !String.IsNullOrWhiteSpace(m))
                       .Select(l => new DownloadInfo
                       {
                           RestrictedLink = l,
                           FileName = null
                       })
                       .ToList();
        }

        return null;
    }

    public Task<String> GetFileName(Download download)
    {
        if (!String.IsNullOrWhiteSpace(download.FileName))
        {
            return Task.FromResult(download.FileName);
        }

        if (String.IsNullOrWhiteSpace(download.Link))
        {
            return Task.FromResult("");
        }

        try
        {
            var uri = new Uri(download.Link);
            var query = HttpUtility.ParseQueryString(uri.Query);

            if (!String.IsNullOrWhiteSpace(query["file"]))
            {
                return Task.FromResult(query["file"]!);
            }

            var segment = HttpUtility.UrlDecode(uri.Segments.LastOrDefault()?.TrimEnd('/') ?? "");
            return Task.FromResult(segment);
        }
        catch
        {
            return Task.FromResult(Path.GetFileName(download.Link));
        }
    }

    private async Task<IList<DebridClientTorrent>?> GetAllTorrents()
    {
        var json = await SendRequestAsync(HttpMethod.Get, "/torrents/info");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        CheckForApiError(root);

        var list = new List<DebridClientTorrent>();

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var elem in root.EnumerateArray())
            {
                list.Add(MapTorrent(elem));
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object &&
                    (prop.Value.TryGetProperty("id", out _) || prop.Value.TryGetProperty("filename", out _)))
                {
                    list.Add(MapTorrent(prop.Value));
                }
                else if (prop.Name == "id" && root.TryGetProperty("filename", out _))
                {
                    list.Add(MapTorrent(root));
                    break;
                }
            }
        }

        return list;
    }

    private async Task<IList<DebridClientTorrent>?> GetAllUsenetUploads()
    {
        var json = await SendRequestAsync(HttpMethod.Get, "/usenet/uploads");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        CheckForApiError(root);

        var list = new List<DebridClientTorrent>();

        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                list.Add(MapUsenet(item));
            }
        }

        return list;
    }

    private async Task<DebridClientTorrent?> GetSingleDownload(String id, DownloadType type)
    {
        if (type == DownloadType.Nzb)
        {
            var json = await SendRequestAsync(HttpMethod.Get, $"/usenet/uploads/info?id={Uri.EscapeDataString(id)}");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            CheckForApiError(root);
            return MapUsenetInfo(root);
        }
        else
        {
            var json = await SendRequestAsync(HttpMethod.Get, $"/torrents/info?id={Uri.EscapeDataString(id)}");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            CheckForApiError(root);
            return MapTorrent(root);
        }
    }

    private static DebridClientTorrent MapTorrent(JsonElement elem)
    {
        var id = GetIdProperty(elem) ?? "";
        var filename = elem.TryGetProperty("filename", out var fn) ? fn.GetString() : null;
        var progress = elem.TryGetProperty("progress", out var pr) && pr.TryGetInt64(out var pVal) ? pVal : 0;
        var seeders = elem.TryGetProperty("seeders", out var s) && s.TryGetInt32(out var sVal) ? sVal : (Int32?)null;

        Int64? speed = null;
        if (elem.TryGetProperty("speed", out var spProp) && spProp.ValueKind == JsonValueKind.String)
        {
            speed = ParseSpeed(spProp.GetString());
        }

        var links = new List<String>();
        if (elem.TryGetProperty("links", out var linksProp) && linksProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in linksProp.EnumerateArray())
            {
                var lStr = l.GetString();
                if (!String.IsNullOrWhiteSpace(lStr))
                {
                    links.Add(lStr);
                }
            }
        }

        var status = progress >= 100 ? "downloaded" : "downloading";
        if (elem.TryGetProperty("status", out var stProp) && stProp.ValueKind == JsonValueKind.String)
        {
            status = stProp.GetString() ?? status;
        }

        return new()
        {
            Id = id,
            Filename = filename ?? id,
            OriginalFilename = filename ?? id,
            Progress = progress,
            Speed = speed,
            Seeders = seeders,
            Status = status,
            Links = links,
            Added = DateTimeOffset.UtcNow,
            Ended = progress >= 100 ? DateTimeOffset.UtcNow : null
        };
    }

    private static DebridClientTorrent MapUsenet(JsonElement elem)
    {
        var id = GetIdProperty(elem) ?? "";
        var title = elem.TryGetProperty("title", out var t) ? t.GetString() : null;
        var hash = elem.TryGetProperty("hash", out var h) ? h.GetString() : null;

        DateTimeOffset added = DateTimeOffset.UtcNow;
        if (elem.TryGetProperty("added_at", out var addedProp) && addedProp.ValueKind == JsonValueKind.String)
        {
            if (DateTimeOffset.TryParse(addedProp.GetString(), out var parsedAdded))
            {
                added = parsedAdded;
            }
        }

        return new()
        {
            Id = id,
            Filename = title ?? id,
            OriginalFilename = title ?? id,
            Hash = hash ?? "",
            Progress = 100,
            Status = "downloaded",
            Added = added,
            Ended = added
        };
    }

    private static DebridClientTorrent MapUsenetInfo(JsonElement elem)
    {
        var id = GetIdProperty(elem) ?? "";
        var title = elem.TryGetProperty("title", out var t) ? t.GetString() : null;

        var files = new List<DebridClientFile>();
        var links = new List<String>();
        Int64 totalBytes = 0;

        if (elem.TryGetProperty("files", out var filesProp) && filesProp.ValueKind == JsonValueKind.Array)
        {
            var idx = 1;
            foreach (var f in filesProp.EnumerateArray())
            {
                var name = f.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var size = f.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
                var link = f.TryGetProperty("link", out var l) ? l.GetString() : null;

                totalBytes += size;
                files.Add(new()
                {
                    Id = idx++,
                    Path = name,
                    Bytes = size,
                    Selected = true
                });

                if (!String.IsNullOrWhiteSpace(link))
                {
                    links.Add(link);
                }
            }
        }

        return new()
        {
            Id = id,
            Filename = title ?? id,
            OriginalFilename = title ?? id,
            Bytes = totalBytes,
            OriginalBytes = totalBytes,
            Progress = 100,
            Status = "downloaded",
            Files = files,
            Links = links,
            Added = DateTimeOffset.UtcNow,
            Ended = DateTimeOffset.UtcNow
        };
    }

    private static String? GetIdProperty(JsonElement elem)
    {
        if (elem.TryGetProperty("id", out var idProp))
        {
            return idProp.ValueKind switch
            {
                JsonValueKind.String => idProp.GetString(),
                JsonValueKind.Number => idProp.GetInt64().ToString(),
                _ => idProp.ToString()
            };
        }

        return null;
    }

    private static void CheckForApiError(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var errorProp))
        {
            var errorCode = errorProp.ValueKind switch
            {
                JsonValueKind.Number => errorProp.GetInt32(),
                JsonValueKind.String when Int32.TryParse(errorProp.GetString(), out var c) => c,
                _ => 0
            };

            if (errorCode != 0)
            {
                var message = root.TryGetProperty("message", out var mProp) ? mProp.GetString() : $"Deepbrid error code {errorCode}";
                if (errorCode == 429)
                {
                    throw new RateLimitException(message ?? "Deepbrid rate limit exceeded", TimeSpan.FromMinutes(2));
                }

                throw new InvalidOperationException($"Deepbrid API error {errorCode}: {message}");
            }
        }
    }

    private static Int64? ParseSpeed(String? speedStr)
    {
        if (String.IsNullOrWhiteSpace(speedStr))
        {
            return null;
        }

        try
        {
            var parts = speedStr.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1 && Double.TryParse(parts[0], out var speedVal))
            {
                var unit = parts.Length > 1 ? parts[1].ToUpperInvariant() : "B/S";
                if (unit.Contains("MB"))
                {
                    return (Int64)(speedVal * 1024 * 1024);
                }
                if (unit.Contains("KB"))
                {
                    return (Int64)(speedVal * 1024);
                }
                if (unit.Contains("GB"))
                {
                    return (Int64)(speedVal * 1024 * 1024 * 1024);
                }
                return (Int64)speedVal;
            }
        }
        catch
        {
            // Ignore parse errors
        }

        return null;
    }

    private static String GetNzbFileName(String? name)
    {
        var fileName = String.IsNullOrWhiteSpace(name) ? "upload.nzb" : name.Trim();
        if (!fileName.EndsWith(".nzb", StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".nzb";
        }

        return fileName;
    }

    private HttpClient CreateHttpClient()
    {
        var apiKey = settings.Current.Provider.ApiKey;
        if (String.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Deepbrid API Key not set in the settings");
        }

        var client = httpClientFactory.CreateClient(DiConfig.DEEPBRID_CLIENT);
        if (client.BaseAddress == null)
        {
            client.BaseAddress = new(BaseUrl);
        }
        client.Timeout = TimeSpan.FromSeconds(settings.Current.Provider.Timeout > 0 ? settings.Current.Provider.Timeout : 10);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    private async Task<String> SendRequestAsync(HttpMethod method, String endpoint, HttpContent? content = null)
    {
        var client = CreateHttpClient();
        using var request = new HttpRequestMessage(method, endpoint.TrimStart('/'))
        {
            Content = content
        };

        var response = await client.SendAsync(request);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new RateLimitException("Deepbrid rate limit exceeded", TimeSpan.FromMinutes(2));
        }

        var responseString = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            try
            {
                using var doc = JsonDocument.Parse(responseString);
                CheckForApiError(doc.RootElement);
            }
            catch (RateLimitException)
            {
                throw;
            }
            catch
            {
                // Fallthrough to generic error
            }

            throw new HttpRequestException($"Deepbrid API request failed with status {(Int32)response.StatusCode} ({response.StatusCode}): {responseString}");
        }

        return responseString;
    }

    private void Log(String message, Torrent? torrent = null)
    {
        if (torrent != null)
        {
            message = $"{message} {torrent.ToLog()}";
        }

        logger.LogDebug(message);
    }
}
