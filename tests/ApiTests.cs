using System.Reflection;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Shoko.Abstractions.Metadata.Enums;
using Xunit;

namespace Shoko.ImagePlanner.Tests;

public sealed class ImagePlannerControllerTests
{
    [Fact]
    public void ControllerHasPublicVersionedAdminOnlyMetadata()
    {
        var controllerType = typeof(ImagePlannerController);
        var route = Assert.Single(controllerType.GetCustomAttributes<RouteAttribute>());
        var apiVersion = Assert.Single(controllerType.GetCustomAttributes<ApiVersionAttribute>());
        var authorization = Assert.Single(controllerType.GetCustomAttributes<AuthorizeAttribute>());

        Assert.Equal("/api/v{version:apiVersion}/Plugin/ImagePlanner", route.Template);
        Assert.Equal("3.0", apiVersion.Versions.Single().ToString());
        Assert.Equal("admin", authorization.Roles);
        Assert.Null(authorization.Policy);
        Assert.DoesNotContain(controllerType.Assembly.GetReferencedAssemblies(), assembly => assembly.Name == "Shoko.Server");
    }

    [Fact]
    public void UiPageIsEmbeddedAndAnonymousWithoutConfigurationData()
    {
        var plugin = new ImagePlannerPlugin();
        var page = Assert.Single(plugin.GetPages());
        var resourceNames = typeof(ImagePlannerPlugin).Assembly.GetManifestResourceNames();

        Assert.Equal("Image Planner", page.Name);
        Assert.Equal("/api/v3/Plugin/ImagePlanner/ui", page.Url);
        Assert.StartsWith("/", page.Url, StringComparison.Ordinal);
        Assert.True(page.CanEmbed);
        Assert.Contains("Shoko.ImagePlanner.Ui.image-planner.html", resourceNames);
        Assert.Contains("Shoko.ImagePlanner.Ui.image-planner.css", resourceNames);
        Assert.Contains("Shoko.ImagePlanner.Ui.image-planner.js", resourceNames);
    }

    [Fact]
    public void StaticUiResourcesAreAnonymousAndDataApisAreAdminOnly()
    {
        var controllerType = typeof(ImagePlannerController);
        foreach (var methodName in new[] { nameof(ImagePlannerController.GetUiPage), nameof(ImagePlannerController.GetUiStyles), nameof(ImagePlannerController.GetUiScript) })
        {
            var method = controllerType.GetMethod(methodName);
            Assert.NotNull(method);
            Assert.NotNull(method!.GetCustomAttribute<AllowAnonymousAttribute>());
        }

        foreach (var methodName in new[] { nameof(ImagePlannerController.GetStatus), nameof(ImagePlannerController.GetCapabilities), nameof(ImagePlannerController.GetProviders), nameof(ImagePlannerController.GetGroups), nameof(ImagePlannerController.Plan), nameof(ImagePlannerController.Apply), nameof(ImagePlannerController.Reconcile) })
        {
            var method = controllerType.GetMethod(methodName);
            Assert.NotNull(method);
            Assert.Null(method!.GetCustomAttribute<AllowAnonymousAttribute>());
        }

        var pageMethod = controllerType.GetMethod(nameof(ImagePlannerController.GetUiPage));
        Assert.Contains("text/html", pageMethod!.GetCustomAttribute<ProducesAttribute>()!.ContentTypes);
    }

    [Fact]
    public void UiResourceUsesSafeDomAndARestrictiveCsp()
    {
        const string scriptResource = "Shoko.ImagePlanner.Ui.image-planner.js";
        using var stream = typeof(ImagePlannerPlugin).Assembly.GetManifestResourceStream(scriptResource);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var script = reader.ReadToEnd();

        Assert.DoesNotContain("innerHTML", script, StringComparison.Ordinal);
        Assert.Contains("sessionStorage", script, StringComparison.Ordinal);
        Assert.Contains("localStorage", script, StringComparison.Ordinal);
        Assert.Contains("headers.set('apikey'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("field.value = state.secrets", script, StringComparison.Ordinal);

        var controller = CreateController(new RecordingPlannerService());
        var result = Assert.IsType<ContentResult>(controller.GetUiPage());
        Assert.Contains("frame-ancestors 'self'", controller.Response.Headers.ContentSecurityPolicy.ToString(), StringComparison.Ordinal);
        Assert.Contains("connect-src 'self'", controller.Response.Headers.ContentSecurityPolicy.ToString(), StringComparison.Ordinal);
        Assert.Contains("text/html", result.ContentType, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"", result.Content!, StringComparison.Ordinal);
        Assert.DoesNotContain("apiKey\":\"", result.Content!, StringComparison.OrdinalIgnoreCase);
    }

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

    [Fact]
    public async Task PlanResponseIncludesOptionalAssignmentPreviewMetadata()
    {
        var imageId = Guid.NewGuid();
        var service = new RecordingPlannerService
        {
            ReportValue = new PlannerReport(DateTimeOffset.UtcNow,
            [new PlannerGroup(1, "Group", [new PlannedSeries(2, "Series",
                [new PlannerAssignment(2, ImageEntityType.Primary, "local", true, false, 100, null,
                    new PlannerAssignmentPreview(imageId, null, 1000, 1500, DataSource.User, "en"))], false, null)], false)], false),
        };
        var controller = CreateController(service);

        var result = await controller.Plan(new PlanRequestDto(), CancellationToken.None);

        var response = Assert.IsType<PlanResponseDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        var preview = Assert.Single(Assert.Single(Assert.Single(response.Groups).Series).Assignments).Preview;
        Assert.NotNull(preview);
        Assert.Equal(imageId.ToString("D"), preview.ImageId);
        Assert.Null(preview.DownloadUrl);
        Assert.Equal("User", preview.Source);
        Assert.Equal("en", preview.Language);
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
        public PlannerReport ReportValue { get; set; } = Report();

        public Task<PlannerReport> PlanAsync(PlannerRequest request, CancellationToken cancellationToken, string? idempotencyKey = null)
        {
            Operation = "plan";
            IdempotencyKey = idempotencyKey;
            return Task.FromResult(ReportValue);
        }

        public Task<PlannerReport> ApplyAsync(PlannerRequest request, string idempotencyKey, CancellationToken cancellationToken)
        {
            Operation = "apply";
            IdempotencyKey = idempotencyKey;
            return Task.FromResult(ReportValue);
        }

        public Task<PlannerReport> ReconcileAsync(PlannerRequest request, string idempotencyKey, CancellationToken cancellationToken)
        {
            Operation = "reconcile";
            IdempotencyKey = idempotencyKey;
            return Task.FromResult(ReportValue);
        }

        private static PlannerReport Report() => new(DateTimeOffset.UtcNow, [], false);
    }
}
