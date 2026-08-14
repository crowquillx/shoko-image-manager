using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Xunit;

namespace Shoko.ImagePlanner.Tests;

public sealed class ImagePlannerControllerTests
{
    [Fact]
    public async Task ReconcileUsesTheReconcileServiceMethod()
    {
        var service = new RecordingPlannerService();
        var controller = CreateController(service);
        controller.HttpContext.Request.Headers["Idempotency-Key"] = "reconcile-key";

        var result = await controller.Reconcile(new ReconcileRequestDto(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("reconcile", service.Operation);
        Assert.Equal("reconcile-key", service.IdempotencyKey);
    }

    [Fact]
    public async Task ApplyUsesTheApplyServiceMethod()
    {
        var service = new RecordingPlannerService();
        var controller = CreateController(service);
        controller.HttpContext.Request.Headers["Idempotency-Key"] = "apply-key";

        var result = await controller.Apply(new ApplyRequestDto(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("apply", service.Operation);
        Assert.Equal("apply-key", service.IdempotencyKey);
    }

    [Fact]
    public async Task IngestPlanRequiresAnIdempotencyKey()
    {
        var service = new RecordingPlannerService();
        var controller = CreateController(service);

        var result = await controller.Plan(new PlanRequestDto(Ingest: true), CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Null(service.Operation);
    }

    [Fact]
    public async Task ReadOnlyPlanDoesNotRequireAnIdempotencyKey()
    {
        var service = new RecordingPlannerService();
        var controller = CreateController(service);

        var result = await controller.Plan(new PlanRequestDto(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("plan", service.Operation);
        Assert.Null(service.IdempotencyKey);
    }

    private static ImagePlannerController CreateController(RecordingPlannerService service)
    {
        var controller = new ImagePlannerController(
            service,
            new ProviderRegistry(Array.Empty<IImageProviderAdapter>()),
            Options.Create(new ImagePlannerOptions()));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private sealed class RecordingPlannerService : IImagePlannerService
    {
        public string? Operation { get; private set; }
        public string? IdempotencyKey { get; private set; }

        public Task<PlannerReport> PlanAsync(PlannerRequest request, CancellationToken cancellationToken, string? idempotencyKey = null)
        {
            Operation = "plan";
            IdempotencyKey = idempotencyKey;
            return Task.FromResult(Report());
        }

        public Task<PlannerReport> ApplyAsync(PlannerRequest request, string idempotencyKey, CancellationToken cancellationToken)
        {
            Operation = "apply";
            IdempotencyKey = idempotencyKey;
            return Task.FromResult(Report());
        }

        public Task<PlannerReport> ReconcileAsync(PlannerRequest request, string idempotencyKey, CancellationToken cancellationToken)
        {
            Operation = "reconcile";
            IdempotencyKey = idempotencyKey;
            return Task.FromResult(Report());
        }

        private static PlannerReport Report() => new(DateTimeOffset.UtcNow, [], false);
    }
}
