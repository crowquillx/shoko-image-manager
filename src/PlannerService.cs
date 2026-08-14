using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Image;
using Shoko.Abstractions.Metadata.Image.CrossReferences;
using Shoko.Abstractions.Metadata.Image.Exceptions;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;

namespace Shoko.ImagePlanner;

public sealed record PlannerRequest(IReadOnlyList<int>? GroupIds = null, bool Ingest = false, bool Force = false);
public sealed record PlannedSeries(int SeriesId, string Name, IReadOnlyList<PlannerAssignment> Assignments, bool Protected, string? ProtectedReason);
public sealed record PlannerGroup(int GroupId, string Name, IReadOnlyList<PlannedSeries> Series, bool HasInsufficientUniqueCandidates);
public sealed record PlannerReport(DateTimeOffset CreatedAt, IReadOnlyList<PlannerGroup> Groups, bool DownloadsPerformed, int ChangedCount = 0);

public interface IImagePlannerService
{
    Task<PlannerReport> PlanAsync(PlannerRequest request, CancellationToken cancellationToken, string? idempotencyKey = null);
    Task<PlannerReport> ApplyAsync(PlannerRequest request, string idempotencyKey, CancellationToken cancellationToken);
    Task<PlannerReport> ReconcileAsync(PlannerRequest request, string idempotencyKey, CancellationToken cancellationToken);
}

public sealed class ImagePlannerService : IImagePlannerService
{
    private readonly IShokoGroupManager _groupManager;
    private readonly IImageManager _imageManager;
    private readonly ProviderRegistry _providerRegistry;
    private readonly ImagePlannerOptions _options;
    private readonly IPluginStateStore _stateStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GlobalAssignmentPlanner _assignmentPlanner;
    private readonly ILogger<ImagePlannerService> _logger;
    private readonly SemaphoreSlim _mutationLock = new(1, 1);

    public ImagePlannerService(
        IShokoGroupManager groupManager,
        IImageManager imageManager,
        ProviderRegistry providerRegistry,
        IOptions<ImagePlannerOptions> options,
        IPluginStateStore stateStore,
        IHttpClientFactory httpClientFactory,
        GlobalAssignmentPlanner assignmentPlanner,
        ILogger<ImagePlannerService> logger)
    {
        _groupManager = groupManager;
        _imageManager = imageManager;
        _providerRegistry = providerRegistry;
        _options = options.Value;
        _stateStore = stateStore;
        _httpClientFactory = httpClientFactory;
        _assignmentPlanner = assignmentPlanner;
        _logger = logger;
    }

    public async Task<PlannerReport> PlanAsync(PlannerRequest request, CancellationToken cancellationToken, string? idempotencyKey = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Ingest)
            return await PlanCoreAsync(request, _stateStore.Load(), cancellationToken).ConfigureAwait(false);

        ValidateIdempotencyKey(idempotencyKey);
        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _stateStore.Load();
            var receiptKey = ReceiptKey("plan-ingest", idempotencyKey!);
            if (state.Idempotency.TryGetValue(receiptKey, out var receipt))
                return receipt.Report ?? new PlannerReport(receipt.CompletedAt, [], false, receipt.Changed);

            var report = await PlanCoreAsync(request with { Ingest = true }, state, cancellationToken).ConfigureAwait(false);
            var completedAt = DateTimeOffset.UtcNow;
            state.Idempotency[receiptKey] = new MutationReceipt
            {
                Operation = "plan-ingest",
                CompletedAt = completedAt,
                Changed = 0,
                Report = report,
            };
            PruneReceipts(state, completedAt);
            _stateStore.Save(state);
            return report;
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    private async Task<PlannerReport> PlanCoreAsync(PlannerRequest request, PluginState state, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return new PlannerReport(DateTimeOffset.UtcNow, [], false);
        var groups = SelectGroups(request.GroupIds);
        var reports = new List<PlannerGroup>();
        var selectedCandidates = new List<(IShokoSeries Series, PlannerCandidate Candidate)>();
        foreach (var group in groups)
        {
            var series = group.AllSeries.OrderBy(item => item.ID).ToArray();
            var contexts = await BuildSeriesContextsAsync(series, state, cancellationToken).ConfigureAwait(false);
            var assignments = new Dictionary<int, List<PlannerAssignment>>();
            var protectedChoices = new Dictionary<(int SeriesId, ImageEntityType Type), (PlannerCandidate Candidate, string Reason)>();
            foreach (var type in new[] { ImageEntityType.Primary, ImageEntityType.Backdrop })
            {
                var typeRows = contexts.Select(context =>
                {
                    if (context.Protected.TryGetValue(type, out var protectedChoice))
                    {
                        protectedChoices[(context.Series.ID, type)] = (protectedChoice.Candidate, protectedChoice.Reason);
                        return new PlannerSeries(context.Series.ID, context.Series.Title, [protectedChoice.Candidate], protectedChoice.Candidate);
                    }
                    return new PlannerSeries(context.Series.ID, context.Series.Title, context.Candidates.Where(candidate => candidate.ImageType == type).ToArray());
                }).ToArray();
                var result = _assignmentPlanner.Assign(type, typeRows, _options.PreferredLanguage);
                foreach (var assignment in result.Assignments)
                {
                    if (!assignments.TryGetValue(assignment.SeriesId, out var list))
                        assignments[assignment.SeriesId] = list = [];
                    list.Add(assignment);
                }
            }

            var plannedSeries = contexts.Select(context =>
            {
                var seriesAssignments = assignments.GetValueOrDefault(context.Series.ID, []);
                foreach (var assignment in seriesAssignments.Where(item => !string.IsNullOrEmpty(item.CandidateId)))
                {
                    var candidate = context.Candidates.FirstOrDefault(item => item.CandidateId == assignment.CandidateId);
                    if (candidate is not null)
                        selectedCandidates.Add((context.Series, candidate));
                }
                var protections = protectedChoices.Where(item => item.Key.SeriesId == context.Series.ID).Select(item => item.Value.Reason).ToArray();
                return new PlannedSeries(context.Series.ID, context.Series.Title, seriesAssignments, protections.Length > 0, protections.Length == 0 ? null : string.Join(" ", protections.Distinct(StringComparer.Ordinal)));
            }).ToArray();
            reports.Add(new PlannerGroup(group.ID, group.Title, plannedSeries, HasInsufficient(plannedSeries)));
        }
        var downloads = request.Ingest ? await IngestCandidatesAsync(selectedCandidates, state, cancellationToken).ConfigureAwait(false) : false;
        return new PlannerReport(DateTimeOffset.UtcNow, reports, downloads);
    }

    public Task<PlannerReport> ApplyAsync(PlannerRequest request, string idempotencyKey, CancellationToken cancellationToken)
        => ApplyCoreAsync(request, idempotencyKey, "apply", cancellationToken);

    public Task<PlannerReport> ReconcileAsync(PlannerRequest request, string idempotencyKey, CancellationToken cancellationToken)
        => ApplyCoreAsync(request, idempotencyKey, "reconcile", cancellationToken);

    private async Task<PlannerReport> ApplyCoreAsync(PlannerRequest request, string idempotencyKey, string operation, CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(idempotencyKey);
        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _stateStore.Load();
            var receiptKey = ReceiptKey(operation, idempotencyKey);
            if (state.Idempotency.TryGetValue(receiptKey, out var receipt))
                return receipt.Report ?? new PlannerReport(receipt.CompletedAt, [], false, receipt.Changed);

            if (!_options.Enabled)
                return new PlannerReport(DateTimeOffset.UtcNow, [], false);
            var report = await PlanCoreAsync(request with { Ingest = true }, state, cancellationToken).ConfigureAwait(false);
            var downloads = report.DownloadsPerformed;
            // A series can appear in both a top-level group and its nested child groups, so dedupe
            // before keying by ID or ToDictionary throws on the first duplicate.
            var seriesById = _groupManager.GetAllGroups()
                .SelectMany(item => item.AllSeries)
                .DistinctBy(item => item.ID)
                .ToDictionary(item => item.ID);
            var changed = 0;
            foreach (var group in report.Groups)
                foreach (var seriesPlan in group.Series)
                {
                    if (!seriesById.TryGetValue(seriesPlan.SeriesId, out var series))
                        continue;
                    foreach (var assignment in seriesPlan.Assignments.Where(item => !string.IsNullOrEmpty(item.CandidateId)))
                    {
                        var contextCandidate = EnsureCandidate(series, assignment.CandidateId, state);
                        if (contextCandidate is not { Image: { } image })
                            continue;
                        var resolved = contextCandidate.Value;
                        var type = resolved.Candidate.ImageType;
                        var current = _imageManager.GetImageCrossReferencesForEntity(series).FirstOrDefault(xref => xref.ImageType == type && xref.IsPreferred);
                        var ledgerKey = LedgerKey(series.ID, type);
                        var ownedImageId = state.Assignments.TryGetValue(ledgerKey, out var ledger) && Guid.TryParse(ledger.ImageId, out var parsedImageId) ? parsedImageId : (Guid?)null;
                        if (!PreferenceProtection.CanReplace(current?.ImageID, ownedImageId, request.Force))
                            continue;
                        _imageManager.SetPreferredImageForEntity(series, type, image);
                        state.Assignments[ledgerKey] = new AssignmentLedgerEntry
                        {
                            SeriesId = series.ID,
                            ImageType = type.ToString(),
                            CandidateId = resolved.Candidate.CandidateId,
                            ImageId = image.ID.ToString("D"),
                            AppliedAt = DateTimeOffset.UtcNow,
                        };
                        changed++;
                    }
                }
            var completedAt = DateTimeOffset.UtcNow;
            var completedReport = report with { DownloadsPerformed = downloads, ChangedCount = changed };
            state.Idempotency[receiptKey] = new MutationReceipt { Operation = operation, CompletedAt = completedAt, Changed = changed, Report = completedReport };
            PruneReceipts(state, completedAt);
            _stateStore.Save(state);
            return completedReport;
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    private IReadOnlyList<IShokoGroup> SelectGroups(IReadOnlyList<int>? ids)
    {
        var groups = _groupManager.GetAllGroups()
            .Where(group => group.TopLevelGroupID == group.ID)
            .OrderBy(group => group.ID)
            .ToArray();
        return ids is null or { Count: 0 } ? groups : groups.Where(group => ids.Contains(group.ID)).ToArray();
    }

    private async Task<IReadOnlyList<SeriesContext>> BuildSeriesContextsAsync(IReadOnlyList<IShokoSeries> series, PluginState state, CancellationToken cancellationToken)
    {
        var contexts = new List<SeriesContext>(series.Count);
        foreach (var item in series)
        {
            var candidates = new List<PlannerCandidate>();
            var crossReferences = _imageManager.GetImageCrossReferencesForEntity(item);
            foreach (var xref in crossReferences.Where(xref => xref.ImageType is ImageEntityType.Primary or ImageEntityType.Backdrop))
            {
                var image = xref.GetImage();
                if (image is null)
                    continue;
                candidates.Add(new PlannerCandidate(
                    $"shoko:{image.ID:D}", xref.ImageType, image.Source, image.ID, image.ResourceID, image.Width, image.Height,
                    image.LanguageCode, xref.Rating, xref.RatingVotes, xref.IsPreferred, xref.Source == DataSource.User,
                    image.ID.ToString("N"), 0, null, image.IsAvailable));
            }

            var lookup = new ProviderLookup(item.ID,
                item.TmdbShows.Select(show => show.TvdbShowID).Where(id => id.HasValue).Select(id => id!.Value).Distinct().Order().ToArray(),
                item.TmdbMovies.Select(movie => movie.ID).Distinct().Order().ToArray());
            foreach (var provider in _providerRegistry.Providers)
            {
                try
                {
                    var providerCandidates = await provider.GetCandidatesAsync(lookup, cancellationToken).ConfigureAwait(false);
                    candidates.AddRange(providerCandidates.Select(candidate => FromProvider(candidate, state)));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException exception)
                {
                    // The caller is not cancelling, so this is a provider-internal timeout
                    // (for example the linked per-request timeout token firing). Isolate it
                    // so one slow provider cannot abort the whole plan.
                    _logger.LogWarning(exception, "Image planner provider {Provider} timed out for series {SeriesId}.", provider.Name, item.ID);
                }
                catch (JsonException exception)
                {
                    _logger.LogWarning(exception, "Image planner provider {Provider} returned an invalid JSON response for series {SeriesId}.", provider.Name, item.ID);
                }
                catch (HttpRequestException exception)
                {
                    _logger.LogWarning(exception, "Image planner provider {Provider} failed for series {SeriesId}.", provider.Name, item.ID);
                }
            }
            var protectedChoices = new Dictionary<ImageEntityType, (PlannerCandidate Candidate, string Reason)>();
            foreach (var type in new[] { ImageEntityType.Primary, ImageEntityType.Backdrop })
            {
                var current = crossReferences.FirstOrDefault(xref => xref.ImageType == type && xref.IsPreferred);
                if (current?.GetImage() is not { } currentImage)
                    continue;
                var ledgerKey = LedgerKey(item.ID, type);
                var pluginOwns = state.Assignments.TryGetValue(ledgerKey, out var ledger) && ledger.ImageId.Equals(currentImage.ID.ToString("D"), StringComparison.OrdinalIgnoreCase);
                if (!pluginOwns)
                {
                    var currentCandidate = candidates.FirstOrDefault(candidate => candidate.ImageId == currentImage.ID && candidate.ImageType == type)
                        ?? new PlannerCandidate($"shoko:{currentImage.ID:D}", type, currentImage.Source, currentImage.ID, currentImage.ResourceID, currentImage.Width, currentImage.Height, currentImage.LanguageCode, current.Rating, current.RatingVotes, true, current.Source == DataSource.User, currentImage.ID.ToString("N"), 100, null, currentImage.IsAvailable);
                    protectedChoices[type] = (currentCandidate with { IsPreferredHint = true }, "A direct series preference is not owned by this plugin.");
                }
            }
            contexts.Add(new SeriesContext(item, candidates, protectedChoices));
        }
        return contexts;
    }

    private static PlannerCandidate FromProvider(ProviderCandidate candidate, PluginState state)
    {
        state.ProviderImages.TryGetValue(candidate.CandidateId, out var saved);
        var imageId = saved is not null && Guid.TryParse(saved.ImageId, out var parsedImageId) ? parsedImageId : (Guid?)null;
        return new PlannerCandidate(candidate.CandidateId, candidate.ImageType, candidate.Source, imageId, candidate.ResourceId,
            candidate.Width, candidate.Height, candidate.LanguageCode, candidate.Rating, candidate.RatingVotes, false, false, saved?.ContentHash,
            candidate.ProviderPriority, candidate.Url, saved is not null);
    }

    private async Task<bool> IngestCandidatesAsync(IEnumerable<(IShokoSeries Series, PlannerCandidate Candidate)> selectedCandidates, PluginState state, CancellationToken cancellationToken)
    {
        var downloaded = false;
        foreach (var (series, candidate) in selectedCandidates
            .Where(item => item.Candidate.DownloadUrl is not null && !item.Candidate.IsAvailable)
            .DistinctBy(item => (item.Series.ID, item.Candidate.CandidateId))
            .OrderBy(item => item.Candidate.CandidateId, StringComparer.Ordinal))
        {
            var image = await DownloadAndUploadAsync(candidate.DownloadUrl!, cancellationToken).ConfigureAwait(false);
            if (image is null)
                continue;
            var hash = Convert.ToHexString(SHA256.HashData(image.Bytes)).ToLowerInvariant();
            var existingState = state.ProviderImages.Values.FirstOrDefault(saved => string.Equals(saved.ContentHash, hash, StringComparison.OrdinalIgnoreCase) && Guid.TryParse(saved.ImageId, out _));
            var uploaded = existingState is not null && Guid.TryParse(existingState.ImageId, out var existingImageId)
                ? _imageManager.GetImageByID(existingImageId)
                : null;
            uploaded ??= _imageManager.UploadImage(image.Bytes, image.ContentType, false);
            try
            {
                _imageManager.AddImageCrossReference(series, uploaded, new ImageCrossReferenceData { ImageType = candidate.ImageType, Source = candidate.Source, IsEnabled = true, IsDesired = true });
            }
            catch (ImageCrossReferenceExistsException)
            {
            }
            state.ProviderImages[candidate.CandidateId] = new ProviderImageState
            {
                CandidateId = candidate.CandidateId,
                Provider = candidate.Source.ToString(),
                ResourceId = candidate.RemoteResourceId ?? candidate.CandidateId,
                ImageId = uploaded.ID.ToString("D"),
                ContentHash = hash,
                ImageType = candidate.ImageType.ToString(),
                IngestedAt = DateTimeOffset.UtcNow,
            };
            downloaded = true;
        }
        return downloaded;
    }

    private (PlannerCandidate Candidate, IImage? Image)? EnsureCandidate(IShokoSeries series, string candidateId, PluginState state)
    {
        var xref = _imageManager.GetImageCrossReferencesForEntity(series).FirstOrDefault(item => item.GetImage() is { } image && $"shoko:{image.ID:D}".Equals(candidateId, StringComparison.Ordinal));
        if (xref?.GetImage() is { } existing)
            return (new PlannerCandidate(candidateId, xref.ImageType, existing.Source, existing.ID, existing.ResourceID, existing.Width, existing.Height, existing.LanguageCode, xref.Rating, xref.RatingVotes, xref.IsPreferred, xref.Source == DataSource.User, existing.ID.ToString("N"), 0, null, existing.IsAvailable), existing);
        if (!state.ProviderImages.TryGetValue(candidateId, out var saved) || !Guid.TryParse(saved.ImageId, out var imageId))
            return null;
        var image = _imageManager.GetImageByID(imageId);
        if (image is null)
            return null;
        if (!Enum.TryParse<ImageEntityType>(saved.ImageType, out var candidateType))
            return null;
        var source = Enum.TryParse<DataSource>(saved.Provider, out var parsedSource) ? parsedSource : DataSource.FanartTV;
        return (new PlannerCandidate(candidateId, candidateType, source, image.ID, saved.ResourceId, image.Width, image.Height, image.LanguageCode, null, null, false, false, saved.ContentHash, 10, null, true), image);
    }

    private async Task<DownloadedImage?> DownloadAndUploadAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !HttpSafety.IsAllowedFanartImageUri(uri))
            return null;
        var client = _httpClientFactory.CreateClient("image-planner-fanart-images");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
            {
                var delay = HttpSafety.GetRetryAfter(response, TimeSpan.FromSeconds(30));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                return null;
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!HttpSafety.IsAllowedImageContentType(contentType))
                return null;
            var bytes = await HttpSafety.ReadBoundedAsync(response.Content, _options.MaxImageBytes, cancellationToken).ConfigureAwait(false);
            if (bytes is null || HttpSafety.DetectImageContentType(bytes) is not { } detected || !string.Equals(detected, contentType, StringComparison.OrdinalIgnoreCase))
                return null;
            return new DownloadedImage(bytes, detected);
        }
        return null;
    }

    private void PruneReceipts(PluginState state, DateTimeOffset now)
    {
        var cutoff = now.AddDays(-_options.IdempotencyReceiptRetentionDays);
        foreach (var key in state.Idempotency.Where(item => item.Value.CompletedAt < cutoff).Select(item => item.Key).ToArray())
            state.Idempotency.Remove(key);
    }

    private static bool HasInsufficient(IEnumerable<PlannedSeries> series) => series.Any(item => item.Assignments.Any(assignment => assignment.IsFallback));

    private static void ValidateIdempotencyKey(string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
            throw new ArgumentException("A valid idempotency key is required.", nameof(idempotencyKey));
    }

    private static string ReceiptKey(string operation, string idempotencyKey) => $"{operation}:{idempotencyKey}";
    private static string LedgerKey(int seriesId, ImageEntityType type) => $"{seriesId}:{type}";

    private sealed record SeriesContext(IShokoSeries Series, IReadOnlyList<PlannerCandidate> Candidates, IReadOnlyDictionary<ImageEntityType, (PlannerCandidate Candidate, string Reason)> Protected);
    private sealed record DownloadedImage(byte[] Bytes, string ContentType);
}
