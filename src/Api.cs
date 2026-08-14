using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Shoko.ImagePlanner;

public sealed record CapabilityDto(string Name, bool Enabled, string? Detail = null);
public sealed record StatusDto(string ApiVersion, string PluginVersion, string MinimumShokoAbstractionsVersion, bool Enabled, IReadOnlyList<CapabilityDto> Capabilities);
public sealed record ProviderDto(string Name, string Source, bool Configured, int Priority);
public sealed record GroupDto(int Id, string Name, int SeriesCount);
public sealed record PlanRequestDto(int ApiVersion = 1, IReadOnlyList<int>? GroupIds = null, bool Ingest = false, bool Force = false);
public sealed record ApplyRequestDto(int ApiVersion = 1, IReadOnlyList<int>? GroupIds = null, bool Force = false);
public sealed record ReconcileRequestDto(int ApiVersion = 1, IReadOnlyList<int>? GroupIds = null, bool Force = false);
public sealed record AssignmentDto(int SeriesId, string CandidateId, string ImageType, bool IsUnique, bool IsFallback, long Score, string? Reason);
public sealed record SeriesPlanDto(int SeriesId, string Name, IReadOnlyList<AssignmentDto> Assignments, bool Protected, string? ProtectedReason);
public sealed record GroupPlanDto(int GroupId, string Name, IReadOnlyList<SeriesPlanDto> Series, bool HasInsufficientUniqueCandidates);
public sealed record PlanResponseDto(int ApiVersion, DateTimeOffset CreatedAt, IReadOnlyList<GroupPlanDto> Groups, bool DownloadsPerformed, int ChangedCount);

[ApiController]
[Authorize(Policy = "admin")]
[Route("api/v3/Plugin/ImagePlanner")]
public sealed class ImagePlannerController : ControllerBase
{
    private readonly IImagePlannerService _service;
    private readonly ProviderRegistry _providers;
    private readonly ImagePlannerOptions _options;

    public ImagePlannerController(IImagePlannerService service, ProviderRegistry providers, Microsoft.Extensions.Options.IOptions<ImagePlannerOptions> options)
    {
        _service = service;
        _providers = providers;
        _options = options.Value;
    }

    [HttpGet("status")]
    public ActionResult<StatusDto> GetStatus()
        => Ok(new StatusDto("1", typeof(ImagePlannerPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0", "6.0.0-alpha.77", _options.Enabled,
        [new CapabilityDto("plan", _options.Enabled), new CapabilityDto("apply", _options.Enabled), new CapabilityDto("reconcile", _options.Enabled), new CapabilityDto("recurring-reconciliation", _options.Enabled && _options.RecurringReconciliationEnabled)]));

    [HttpGet("capabilities")]
    public ActionResult<StatusDto> GetCapabilities() => GetStatus();

    [HttpGet("providers")]
    public ActionResult<IReadOnlyList<ProviderDto>> GetProviders()
        => Ok(_providers.Providers.Select(provider => new ProviderDto(provider.Name, provider.Source.ToString(), provider is FanartTvAdapter && !string.IsNullOrWhiteSpace(_options.FanartTvApiKey), _options.FanartTvPriority)).ToArray());

    [HttpGet("groups")]
    public ActionResult<IReadOnlyList<GroupDto>> GetGroups([FromServices] Shoko.Abstractions.Metadata.Services.IShokoGroupManager groups)
        => Ok(groups.GetAllGroups().Where(group => group.ID == group.TopLevelGroupID).OrderBy(group => group.ID).Select(group => new GroupDto(group.ID, group.Title, group.AllSeries.Count)).ToArray());

    [HttpPost("plan")]
    public async Task<ActionResult<PlanResponseDto>> Plan([FromBody] PlanRequestDto request, CancellationToken cancellationToken)
    {
        if (request.ApiVersion != 1)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unsupported API version", detail: "Use apiVersion 1.");
        var idempotencyKey = request.Ingest ? Request.Headers["Idempotency-Key"].FirstOrDefault() : null;
        if (request.Ingest && string.IsNullOrWhiteSpace(idempotencyKey))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Missing idempotency key", detail: "Send an Idempotency-Key header when ingest is enabled.");
        try
        {
            var report = await _service.PlanAsync(new PlannerRequest(request.GroupIds, request.Ingest, request.Force), cancellationToken, idempotencyKey).ConfigureAwait(false);
            return Ok(ToResponse(report));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Problem(statusCode: StatusCodes.Status499ClientClosedRequest, title: "Request cancelled");
        }
        catch (ArgumentException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request", detail: exception.Message);
        }
    }

    [HttpPost("apply")]
    public async Task<ActionResult<PlanResponseDto>> Apply([FromBody] ApplyRequestDto request, CancellationToken cancellationToken)
        => await MutateAsync(request.ApiVersion, request.GroupIds, request.Force, _service.ApplyAsync, cancellationToken).ConfigureAwait(false);

    [HttpPost("reconcile")]
    public async Task<ActionResult<PlanResponseDto>> Reconcile([FromBody] ReconcileRequestDto request, CancellationToken cancellationToken)
        => await MutateAsync(request.ApiVersion, request.GroupIds, request.Force, _service.ReconcileAsync, cancellationToken).ConfigureAwait(false);

    private async Task<ActionResult<PlanResponseDto>> MutateAsync(int apiVersion, IReadOnlyList<int>? groupIds, bool force, Func<PlannerRequest, string, CancellationToken, Task<PlannerReport>> mutation, CancellationToken cancellationToken)
    {
        if (apiVersion != 1)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unsupported API version", detail: "Use apiVersion 1.");
        if (!Request.Headers.TryGetValue("Idempotency-Key", out var values) || string.IsNullOrWhiteSpace(values.FirstOrDefault()))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Missing idempotency key", detail: "Send an Idempotency-Key header for mutations.");
        try
        {
            var report = await mutation(new PlannerRequest(groupIds, true, force), values.First()!, cancellationToken).ConfigureAwait(false);
            return Ok(ToResponse(report));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Problem(statusCode: StatusCodes.Status499ClientClosedRequest, title: "Request cancelled");
        }
        catch (ArgumentException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request", detail: exception.Message);
        }
    }

    private static PlanResponseDto ToResponse(PlannerReport report)
        => new(1, report.CreatedAt, report.Groups.Select(group => new GroupPlanDto(group.GroupId, group.Name,
            group.Series.Select(series => new SeriesPlanDto(series.SeriesId, series.Name, series.Assignments.Select(assignment => new AssignmentDto(assignment.SeriesId, assignment.CandidateId, assignment.ImageType.ToString(), assignment.IsUnique, assignment.IsFallback, assignment.Score, assignment.Reason)).ToArray(), series.Protected, series.ProtectedReason)).ToArray(), group.HasInsufficientUniqueCandidates)).ToArray(), report.DownloadsPerformed, report.ChangedCount);
}
