using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Metadata.Enums;

namespace Shoko.ImagePlanner;

public sealed record ProviderLookup(int SeriesId, IReadOnlyList<int> TvdbShowIds, IReadOnlyList<int> TmdbMovieIds);

public sealed record ProviderCandidate(
    string CandidateId,
    ImageEntityType ImageType,
    DataSource Source,
    string ResourceId,
    string Url,
    int? Width,
    int? Height,
    string? LanguageCode,
    double? Rating,
    int? RatingVotes,
    string Provider,
    int ProviderPriority);

public interface IImageProviderAdapter
{
    string Name { get; }
    DataSource Source { get; }
    Task<IReadOnlyList<ProviderCandidate>> GetCandidatesAsync(ProviderLookup lookup, CancellationToken cancellationToken);
}

public sealed class ProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IImageProviderAdapter> _providers;

    public ProviderRegistry(IEnumerable<IImageProviderAdapter> providers)
    {
        _providers = providers.ToDictionary(provider => provider.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<IImageProviderAdapter> Providers => _providers.Values.OrderBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase).ToArray();
}

public sealed class FanartTvAdapter : IImageProviderAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ImagePlannerOptions _options;
    private readonly ILogger<FanartTvAdapter> _logger;

    public FanartTvAdapter(IHttpClientFactory httpClientFactory, Microsoft.Extensions.Options.IOptions<ImagePlannerOptions> options, ILogger<FanartTvAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public string Name => "fanart.tv";
    public DataSource Source => DataSource.FanartTV;

    public async Task<IReadOnlyList<ProviderCandidate>> GetCandidatesAsync(ProviderLookup lookup, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.FanartTvApiKey))
            return [];

        var results = new List<ProviderCandidate>();
        foreach (var tvdbId in lookup.TvdbShowIds.Distinct().Order())
            await GetMediaCandidatesAsync($"tv/{tvdbId}", "tv", tvdbId.ToString(System.Globalization.CultureInfo.InvariantCulture), results, cancellationToken).ConfigureAwait(false);
        foreach (var tmdbId in lookup.TmdbMovieIds.Distinct().Order())
            await GetMediaCandidatesAsync($"movies/{tmdbId}", "movie", tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture), results, cancellationToken).ConfigureAwait(false);
        return results
            .GroupBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .Select(group => group.OrderBy(candidate => candidate.Url, StringComparer.Ordinal).First())
            .OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task GetMediaCandidatesAsync(string path, string mediaKind, string identity, ICollection<ProviderCandidate> output, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("image-planner-fanart");
        var response = await SendJsonAsync(client, path, cancellationToken).ConfigureAwait(false);
        if (response is null)
            return;
        using (response)
        {
            if (!HttpSafety.IsAllowedJsonContentType(response.Content.Headers.ContentType?.MediaType))
            {
                _logger.LogWarning("Fanart.tv returned a non-JSON content type ({ContentType}) for {Path}; skipping.", response.Content.Headers.ContentType?.MediaType ?? "none", path);
                return;
            }
            var bytes = await HttpSafety.ReadBoundedAsync(response.Content, _options.MaxJsonResponseBytes, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
                return;
            foreach (var candidate in FanartResponseMapper.Map(bytes, mediaKind, identity, _options.FanartTvPriority))
                output.Add(candidate);
        }
    }

    private async Task<HttpResponseMessage?> SendJsonAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://webservice.fanart.tv/v3/{path}"));
            request.Headers.TryAddWithoutValidation("api-key", _options.FanartTvApiKey);
            if (!string.IsNullOrWhiteSpace(_options.FanartTvClientKey))
                request.Headers.TryAddWithoutValidation("client-key", _options.FanartTvClientKey);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
            {
                var delay = HttpSafety.GetRetryAfter(response, TimeSpan.FromSeconds(30));
                response.Dispose();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                return null;
            }
            return response;
        }
        return null;
    }
}

public static class FanartResponseMapper
{
    private static readonly IReadOnlyDictionary<string, ImageEntityType> ImageTypes = new Dictionary<string, ImageEntityType>(StringComparer.OrdinalIgnoreCase)
    {
        ["tvposter"] = ImageEntityType.Primary,
        ["tvposterlarge"] = ImageEntityType.Primary,
        ["movieposter"] = ImageEntityType.Primary,
        ["movieposterlarge"] = ImageEntityType.Primary,
        ["showbackground"] = ImageEntityType.Backdrop,
        ["tvbackground"] = ImageEntityType.Backdrop,
        ["moviebackground"] = ImageEntityType.Backdrop,
    };

    public static IReadOnlyList<ProviderCandidate> Map(byte[] json, string mediaKind, string identity, int priority)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return [];
        var candidates = new List<ProviderCandidate>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!ImageTypes.TryGetValue(property.Name, out var imageType) || property.Value.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var item in property.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                var urlText = GetString(item, "url");
                if (urlText is null || !Uri.TryCreate(urlText, UriKind.Absolute, out var url) || !HttpSafety.IsAllowedFanartImageUri(url))
                    continue;
                var remoteId = GetString(item, "id") ?? Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url.AbsoluteUri))).ToLowerInvariant();
                var resourceId = $"{mediaKind}:{identity}:{property.Name}:{remoteId}";
                var candidateId = $"fanarttv:{resourceId}";
                candidates.Add(new ProviderCandidate(
                    candidateId,
                    imageType,
                    DataSource.FanartTV,
                    resourceId,
                    url.AbsoluteUri,
                    GetInt(item, "width"),
                    GetInt(item, "height"),
                    NormalizeLanguage(GetString(item, "lang")),
                    null,
                    GetInt(item, "likes"),
                    "fanart.tv",
                    priority));
            }
        }
        return candidates
            .GroupBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToArray();
    }

    private static string? GetString(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? GetInt(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out var number) && number > 0 ? number : null,
            JsonValueKind.String => ParsePositiveInt32(value.GetString()),
            _ => null,
        };
    }

    private static int? ParsePositiveInt32(string? text)
    {
        if (text is null || !decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            return null;
        if (parsed != decimal.Truncate(parsed) || parsed <= 0 || parsed > int.MaxValue)
            return null;
        return (int)parsed;
    }
    private static string? NormalizeLanguage(string? language) => string.IsNullOrWhiteSpace(language) || language == "00" ? null : language.Length > 5 ? language[..5] : language;
}

public static class HttpSafety
{
    private static readonly HashSet<string> AllowedImageHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "assets.fanart.tv",
        "fanart.tv",
        "www.fanart.tv",
    };
    private static readonly HashSet<string> AllowedMimeTypes = new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/webp", "image/gif" };

    public static bool IsAllowedFanartImageUri(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps && uri.Port is -1 or 443 && !uri.UserInfo.Contains('@') && AllowedImageHosts.Contains(uri.Host);

    public static async Task<byte[]?> ReadBoundedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long length && length > maxBytes)
            return null;
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (destination.Length + read > maxBytes)
                return null;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return destination.ToArray();
    }

    public static TimeSpan GetRetryAfter(HttpResponseMessage response, TimeSpan maximum)
    {
        var retryAfter = response.Headers.RetryAfter;
        var delay = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(1));
        return delay > TimeSpan.Zero && delay <= maximum ? delay : maximum;
    }

    public static bool IsAllowedJsonContentType(string? contentType)
    {
        // Absent content type: allow, the JSON parser is the validator.
        if (string.IsNullOrWhiteSpace(contentType))
            return true;
        var mediaType = contentType.Split(';', 2)[0].Trim();
        return mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("text/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAllowedImageContentType(string? contentType) => contentType is not null && AllowedMimeTypes.Contains(contentType.Split(';', 2)[0].Trim());
    public static string? DetectImageContentType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[..3].SequenceEqual(new byte[] { 0xFF, 0xD8, 0xFF })) return "image/jpeg";
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8.ToArray()) && bytes[8..12].SequenceEqual("WEBP"u8.ToArray())) return "image/webp";
        if (bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8))) return "image/gif";
        return null;
    }
}
