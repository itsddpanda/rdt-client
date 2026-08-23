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

    private static readonly SocketsHttpHandler NonRedirectingHandler = new()
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = delegate { return true; }
        }
    };

    private static readonly HttpMessageInvoker NonRedirectingInvoker = new(NonRedirectingHandler);

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

        try
        {
            var json = await SendRequestAsync(HttpMethod.Post, "/torrents/add", new FormUrlEncodedContent(form));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            CheckForApiError(root);

            var id = GetIdProperty(root);
            if (!String.IsNullOrWhiteSpace(id))
            {
                return id;
            }
        }
        catch (RateLimitException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "AddTorrentMagnet direct add failed: {Message}. Attempting duplicate recovery from existing torrents list.", ex.Message);
        }

        // Duplicate recovery: search existing torrents on Deepbrid
        var existingTorrents = await GetAllTorrents();
        if (existingTorrents != null && existingTorrents.Count > 0)
        {
            var hash = ExtractHashFromMagnet(magnetLink);
            if (!String.IsNullOrWhiteSpace(hash))
            {
                var match = existingTorrents.FirstOrDefault(t =>
                    String.Equals(t.Hash, hash, StringComparison.OrdinalIgnoreCase) ||
                    (!String.IsNullOrWhiteSpace(t.Filename) && t.Filename.Contains(hash, StringComparison.OrdinalIgnoreCase)));

                if (match != null && !String.IsNullOrWhiteSpace(match.Id))
                {
                    logger.LogInformation("Found existing torrent on Deepbrid with ID {Id} matching hash {Hash}", match.Id, hash);
                    return match.Id;
                }
            }

            var dn = ExtractDisplayNameFromMagnet(magnetLink);
            if (!String.IsNullOrWhiteSpace(dn))
            {
                var match = existingTorrents.FirstOrDefault(t => IsMatchingTorrentName(t.Filename, dn));

                if (match != null && !String.IsNullOrWhiteSpace(match.Id))
                {
                    logger.LogInformation("Found existing torrent on Deepbrid with ID {Id} matching name {Name} ({Filename})", match.Id, dn, match.Filename);
                    return match.Id;
                }
            }
        }

        throw new InvalidOperationException("Deepbrid API did not return torrent ID.");
    }

    public async Task<String> AddTorrentFile(Byte[] bytes)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-bittorrent");
        content.Add(fileContent, "torrent_file", "file.torrent");

        try
        {
            var json = await SendRequestAsync(HttpMethod.Post, "/torrents/add", content);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            CheckForApiError(root);

            var id = GetIdProperty(root);
            if (!String.IsNullOrWhiteSpace(id))
            {
                return id;
            }
        }
        catch (RateLimitException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "AddTorrentFile direct add failed: {Message}. Attempting duplicate recovery from existing torrents list.", ex.Message);
        }

        var existingTorrents = await GetAllTorrents();
        if (existingTorrents != null && existingTorrents.Count > 0)
        {
            var latest = existingTorrents.OrderByDescending(t => t.Added).FirstOrDefault();
            if (latest != null && !String.IsNullOrWhiteSpace(latest.Id))
            {
                logger.LogInformation("Recovered latest torrent on Deepbrid with ID {Id} ({Filename})", latest.Id, latest.Filename);
                return latest.Id;
            }
        }

        throw new InvalidOperationException("Deepbrid API did not return torrent ID.");
    }

    public async Task<String> AddNzbLink(String nzbLink)
    {
        var form = new Dictionary<String, String>
        {
            { "nzb_url", nzbLink }
        };

        try
        {
            var json = await SendRequestAsync(HttpMethod.Post, "/usenet/add", new FormUrlEncodedContent(form));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            CheckForApiError(root);

            var id = GetIdProperty(root);
            if (!String.IsNullOrWhiteSpace(id))
            {
                return id;
            }
        }
        catch (RateLimitException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "AddNzbLink direct add failed: {Message}. Attempting duplicate recovery from existing NZB list.", ex.Message);
        }

        var existingUploads = await GetAllUsenetUploads();
        if (existingUploads != null && existingUploads.Count > 0)
        {
            var latest = existingUploads.OrderByDescending(u => u.Added).FirstOrDefault();
            if (latest != null && !String.IsNullOrWhiteSpace(latest.Id))
            {
                return latest.Id;
            }
        }

        throw new InvalidOperationException("Deepbrid API did not return NZB ID.");
    }

    public async Task<String> AddNzbFile(Byte[] bytes, String? name)
    {
        var fileName = GetNzbFileName(name);

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-nzb");
        content.Add(fileContent, "nzb_file", fileName);

        try
        {
            var json = await SendRequestAsync(HttpMethod.Post, "/usenet/add", content);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            CheckForApiError(root);

            var id = GetIdProperty(root);
            if (!String.IsNullOrWhiteSpace(id))
            {
                return id;
            }
        }
        catch (RateLimitException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "AddNzbFile direct add failed: {Message}. Attempting duplicate recovery from existing NZB list.", ex.Message);
        }

        var existingUploads = await GetAllUsenetUploads();
        if (existingUploads != null && existingUploads.Count > 0)
        {
            var match = existingUploads.FirstOrDefault(u =>
                !String.IsNullOrWhiteSpace(u.Filename) &&
                (String.Equals(u.Filename, fileName, StringComparison.OrdinalIgnoreCase) ||
                 String.Equals(u.Filename, name, StringComparison.OrdinalIgnoreCase)));

            if (match != null && !String.IsNullOrWhiteSpace(match.Id))
            {
                return match.Id;
            }

            var latest = existingUploads.OrderByDescending(u => u.Added).FirstOrDefault();
            if (latest != null && !String.IsNullOrWhiteSpace(latest.Id))
            {
                return latest.Id;
            }
        }

        throw new InvalidOperationException("Deepbrid API did not return NZB ID.");
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

    public async Task Delete(Torrent torrent)
    {
        if (String.IsNullOrWhiteSpace(torrent.RdId))
        {
            return;
        }

        try
        {
            var endpoint = torrent.Type == DownloadType.Nzb
                ? $"/usenet/uploads/delete/{Uri.EscapeDataString(torrent.RdId)}"
                : $"/torrents/delete/{Uri.EscapeDataString(torrent.RdId)}";

            var json = await SendRequestAsync(HttpMethod.Delete, endpoint);
            Log($"Deleted Deepbrid remote {torrent.Type} {torrent.RdId}: {json}", torrent);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to delete remote Deepbrid {Type} {Id}: {Message}", torrent.Type, torrent.RdId, ex.Message);
        }
    }

    public async Task<String> Unrestrict(Torrent torrent, String link)
    {
        if (String.IsNullOrWhiteSpace(link))
        {
            return link;
        }

        var apiKey = settings.Current.Provider.ApiKey;

        try
        {
            if (link.Contains("deepbrid.com/mytorrents", StringComparison.OrdinalIgnoreCase))
            {
                var directLink = await ResolveDeepbridTorrentLink(link);
                if (!String.IsNullOrWhiteSpace(directLink) && !directLink.Contains("deepbrid.com/mytorrents", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"Resolved Deepbrid torrent link to direct download URL: {directLink}", torrent);
                    return directLink;
                }

                throw new InvalidOperationException($"Unable to resolve direct download link from Deepbrid for {link}");
            }

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

        if (!String.IsNullOrWhiteSpace(apiKey) &&
            link.Contains("deepbrid.com", StringComparison.OrdinalIgnoreCase) &&
            !link.Contains("apikey=", StringComparison.OrdinalIgnoreCase))
        {
            var separator = link.Contains('?') ? "&" : "?";
            return $"{link}{separator}apikey={Uri.EscapeDataString(apiKey)}";
        }

        return link;
    }

    private async Task<String?> ResolveDeepbridTorrentLink(String link)
    {
        var apiKey = settings.Current.Provider.ApiKey;
        if (String.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var authenticatedLink = link;
        if (!link.Contains("apikey=", StringComparison.OrdinalIgnoreCase))
        {
            var separator = link.Contains('?') ? "&" : "?";
            authenticatedLink = $"{link}{separator}apikey={Uri.EscapeDataString(apiKey)}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, authenticatedLink);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await NonRedirectingInvoker.SendAsync(request, CancellationToken.None);

        if (response.Headers.Location != null)
        {
            var loc = response.Headers.Location;
            var resolvedUri = loc.IsAbsoluteUri ? loc.ToString() : new Uri(new Uri(authenticatedLink), loc).ToString();
            if (!resolvedUri.Contains("deepbrid.com/mytorrents", StringComparison.OrdinalIgnoreCase))
            {
                return resolvedUri;
            }
        }

        if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or (HttpStatusCode)308)
        {
            var loc = response.Headers.Location;
            if (loc != null)
            {
                var resolvedUri = loc.IsAbsoluteUri ? loc.ToString() : new Uri(new Uri(authenticatedLink), loc).ToString();
                if (!resolvedUri.Contains("deepbrid.com/mytorrents", StringComparison.OrdinalIgnoreCase))
                {
                    return resolvedUri;
                }
            }
        }

        return null;
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

            var validRawLinks = item.Links.Where(m => !String.IsNullOrWhiteSpace(m)).ToList();
            var links = new List<DownloadInfo>();

            var tasks = validRawLinks.Select(async rawLink =>
            {
                try
                {
                    var resolved = await ResolveDeepbridTorrentLink(rawLink);
                    if (String.IsNullOrWhiteSpace(resolved))
                    {
                        return null;
                    }

                    var decodedPath = HttpUtility.UrlDecode(new Uri(resolved).LocalPath);
                    var fileName = Path.GetFileName(decodedPath);
                    if (String.IsNullOrWhiteSpace(fileName) || fileName.Equals("mytorrents", StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }

                    if (!fileFilter.IsDownloadable(torrent, fileName, Int64.MaxValue))
                    {
                        Log($"Excluded Deepbrid link {fileName} by filter rules", torrent);
                        return null;
                    }

                    return new DownloadInfo
                    {
                        RestrictedLink = rawLink,
                        FileName = fileName
                    };
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Skipping unresolved Deepbrid link {Link}: {Message}", rawLink, ex.Message);
                    return null;
                }
            });

            var resolvedList = await Task.WhenAll(tasks);
            links.AddRange(resolvedList.Where(d => d != null)!);

            if (links.Count == 0 && validRawLinks.Count > 0)
            {
                links = validRawLinks.Select(l => new DownloadInfo
                {
                    RestrictedLink = l,
                    FileName = null
                }).ToList();
            }

            Log($"Found {links.Count} downloadable files (out of {validRawLinks.Count} provider links) for torrent {torrent.RdName}", torrent);
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

            // 1. If it's a resolved direct link, extract the filename from the last path segment
            var segment = HttpUtility.UrlDecode(uri.Segments.LastOrDefault()?.TrimEnd('/') ?? "");
            if (!String.IsNullOrWhiteSpace(segment) && segment.Contains('.'))
            {
                return Task.FromResult(FileHelper.RemoveInvalidFileNameChars(segment));
            }

            // 2. If query param 'file' contains an actual file name with extension
            var query = HttpUtility.ParseQueryString(uri.Query);
            var fileParam = query["file"];
            if (!String.IsNullOrWhiteSpace(fileParam) && fileParam.Contains('.'))
            {
                return Task.FromResult(FileHelper.RemoveInvalidFileNameChars(fileParam));
            }

            if (!String.IsNullOrWhiteSpace(segment))
            {
                return Task.FromResult(FileHelper.RemoveInvalidFileNameChars(segment));
            }

            return Task.FromResult("");
        }
        catch
        {
            return Task.FromResult(Path.GetFileName(download.Link));
        }
    }

    private async Task<IList<DebridClientTorrent>?> GetAllTorrents()
    {
        var list = new List<DebridClientTorrent>();

        try
        {
            var json = await SendRequestAsync(HttpMethod.Get, "/torrents/info");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var errProp))
            {
                var code = errProp.ValueKind switch
                {
                    JsonValueKind.Number => errProp.GetInt32(),
                    JsonValueKind.String when Int32.TryParse(errProp.GetString(), out var c) => c,
                    _ => 0
                };

                if (code == 1)
                {
                    return list;
                }
            }

            CheckForApiError(root);

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
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get torrents from Deepbrid: {Message}", ex.Message);
        }

        return list;
    }

    private async Task<IList<DebridClientTorrent>?> GetAllUsenetUploads()
    {
        var list = new List<DebridClientTorrent>();

        try
        {
            var json = await SendRequestAsync(HttpMethod.Get, "/usenet/uploads");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var errProp))
            {
                var code = errProp.ValueKind switch
                {
                    JsonValueKind.Number => errProp.GetInt32(),
                    JsonValueKind.String when Int32.TryParse(errProp.GetString(), out var c) => c,
                    _ => 0
                };

                if (code == 1)
                {
                    return list;
                }
            }

            CheckForApiError(root);

            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    list.Add(MapUsenet(item));
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get Usenet uploads from Deepbrid: {Message}", ex.Message);
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
        if (elem.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "id", "torrent_id", "torrentId", "nzb_id", "nzbId", "torrent" })
            {
                if (elem.TryGetProperty(name, out var prop))
                {
                    var val = prop.ValueKind switch
                    {
                        JsonValueKind.String => prop.GetString(),
                        JsonValueKind.Number => prop.GetInt64().ToString(),
                        _ => prop.ToString()
                    };

                    if (!String.IsNullOrWhiteSpace(val))
                    {
                        return val;
                    }
                }
            }

            foreach (var container in new[] { "data", "item", "result" })
            {
                if (elem.TryGetProperty(container, out var containerProp) && containerProp.ValueKind == JsonValueKind.Object)
                {
                    var nestedId = GetIdProperty(containerProp);
                    if (!String.IsNullOrWhiteSpace(nestedId))
                    {
                        return nestedId;
                    }
                }
            }
        }
        else if (elem.ValueKind == JsonValueKind.String)
        {
            return elem.GetString();
        }
        else if (elem.ValueKind == JsonValueKind.Number)
        {
            return elem.GetInt64().ToString();
        }

        return null;
    }

    private static String? ExtractHashFromMagnet(String magnetLink)
    {
        if (String.IsNullOrWhiteSpace(magnetLink))
        {
            return null;
        }

        try
        {
            var match = System.Text.RegularExpressions.Regex.Match(magnetLink, @"xt=urn:btih:([a-zA-Z0-9]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }
        catch
        {
            // Ignore parse errors
        }

        return null;
    }

    private static String? ExtractDisplayNameFromMagnet(String magnetLink)
    {
        if (String.IsNullOrWhiteSpace(magnetLink))
        {
            return null;
        }

        try
        {
            var match = System.Text.RegularExpressions.Regex.Match(magnetLink, @"dn=([^&]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var raw = match.Groups[1].Value.Replace('+', ' ');
                return HttpUtility.UrlDecode(raw);
            }
        }
        catch
        {
            // Ignore parse errors
        }

        return null;
    }

    private static Boolean IsMatchingTorrentName(String? name1, String? name2)
    {
        if (String.IsNullOrWhiteSpace(name1) || String.IsNullOrWhiteSpace(name2))
        {
            return false;
        }

        var clean1 = System.Text.RegularExpressions.Regex.Replace(name1.ToLowerInvariant(), @"[^a-z0-9]", "");
        var clean2 = System.Text.RegularExpressions.Regex.Replace(name2.ToLowerInvariant(), @"[^a-z0-9]", "");

        if (clean1.Length > 3 && clean2.Length > 3)
        {
            if (clean1.Contains(clean2) || clean2.Contains(clean1))
            {
                return true;
            }
        }

        var tokens1 = System.Text.RegularExpressions.Regex.Split(name1.ToLowerInvariant(), @"[^a-z0-9]+")
                                                          .Where(t => t.Length > 1)
                                                          .ToHashSet();
        var tokens2 = System.Text.RegularExpressions.Regex.Split(name2.ToLowerInvariant(), @"[^a-z0-9]+")
                                                          .Where(t => t.Length > 1)
                                                          .ToHashSet();

        if (tokens1.Count > 0 && tokens2.Count > 0)
        {
            var intersection = tokens1.Intersect(tokens2).Count();
            var minCount = Math.Min(tokens1.Count, tokens2.Count);
            if (minCount > 0 && (Double)intersection / minCount >= 0.7)
            {
                return true;
            }
        }

        return false;
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
