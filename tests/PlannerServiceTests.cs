using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shoko.Abstractions.Metadata.Image.CrossReferences;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Metadata.Tmdb;
using Xunit;

namespace Shoko.ImagePlanner.Tests;

public sealed class ImagePlannerServiceTests
{
    private static IShokoSeries Series(int id) => DynamicFake.Create<IShokoSeries>(fake => fake
        .WithValue("ID", id)
        .WithValue("Title", $"Series {id}")
        .WithValue("TmdbShows", Array.Empty<ITmdbShow>())
        .WithValue("TmdbMovies", Array.Empty<ITmdbMovie>()));

    private static IShokoGroup Group(int id, int topLevelGroupId, string title, params IShokoSeries[] series)
        => DynamicFake.Create<IShokoGroup>(fake => fake
            .WithValue("ID", id)
            .WithValue("TopLevelGroupID", topLevelGroupId)
            .WithValue("Title", title)
            .WithValue("AllSeries", (IReadOnlyList<IShokoSeries>)series));

    private static IShokoGroupManager GroupManager(params IShokoGroup[] groups)
        => DynamicFake.Create<IShokoGroupManager>(fake => fake
            .WithBehavior("GetAllGroups", _ => (IEnumerable<IShokoGroup>)groups));

    private static IImageManager ImageManager()
        => DynamicFake.Create<IImageManager>(fake => fake
            .WithBehavior("GetImageCrossReferencesForEntity", _ => Array.Empty<IImageCrossReference>()));

    private static IHttpClientFactory HttpClientFactory()
        => DynamicFake.Create<IHttpClientFactory>(fake => fake
            .WithBehavior("CreateClient", _ => new HttpClient()));

    private static ImagePlannerService CreateService(
        IShokoGroupManager groupManager,
        IImageManager imageManager,
        IReadOnlyList<IImageProviderAdapter> providers,
        IPluginStateStore stateStore,
        ILogger<ImagePlannerService> logger)
        => new(groupManager, imageManager, new ProviderRegistry(providers), Options.Create(new ImagePlannerOptions()), stateStore, HttpClientFactory(), new GlobalAssignmentPlanner(), logger);

    [Fact]
    public async Task ApplyAndReconcileHandleSeriesSharedAcrossNestedGroups()
    {
        // The same series is returned by both a top-level group and its nested child group, which
        // must not crash the series lookup in the apply/reconcile path.
        var shared = Series(100);
        var groups = new[]
        {
            Group(10, 10, "Top", shared, Series(200)),
            Group(11, 10, "Child of 10", shared, Series(300)),
        };
        var service = CreateService(GroupManager(groups), ImageManager(), [], new InMemoryStateStore(), NullLogger<ImagePlannerService>.Instance);

        var apply = await service.ApplyAsync(new PlannerRequest(), "apply-key", CancellationToken.None);
        Assert.Single(apply.Groups);
        Assert.Equal(2, apply.Groups[0].Series.Count);
        Assert.Equal(0, apply.ChangedCount);

        var reconcile = await service.ReconcileAsync(new PlannerRequest(), "reconcile-key", CancellationToken.None);
        Assert.Single(reconcile.Groups);
    }

    [Fact]
    public async Task IngestPlanRequiresAnIdempotencyKeyAndReplaysItsReceipt()
    {
        var state = new InMemoryStateStore();
        var service = CreateService(
            GroupManager(Group(10, 10, "Top")),
            ImageManager(),
            [],
            state,
            NullLogger<ImagePlannerService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => service.PlanAsync(new PlannerRequest(Ingest: true), CancellationToken.None));

        var first = await service.PlanAsync(new PlannerRequest(Ingest: true), CancellationToken.None, "ingest-key");
        var replay = await service.PlanAsync(new PlannerRequest(Ingest: true), CancellationToken.None, "ingest-key");

        Assert.Equal(first.CreatedAt, replay.CreatedAt);
        var receipt = Assert.Single(state.Load().Idempotency);
        Assert.Equal("plan-ingest:ingest-key", receipt.Key);
        Assert.Equal("plan-ingest", receipt.Value.Operation);
    }

    [Fact]
    public async Task ProviderTimeoutIsIsolatedAndLoggedWhenCallerIsNotCancelling()
    {
        var logger = new RecordingLogger<ImagePlannerService>();
        var service = CreateService(
            GroupManager(Group(10, 10, "Top", Series(100))),
            ImageManager(),
            [new FakeProvider { Name = "slow", Exception = new TaskCanceledException("provider timed out") }],
            new InMemoryStateStore(),
            logger);

        var report = await service.PlanAsync(new PlannerRequest(), CancellationToken.None);

        Assert.Single(report.Groups);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Exception is OperationCanceledException);
    }

    [Fact]
    public async Task ProviderCancellationIsRethrownWhenCallerTokenIsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var service = CreateService(
            GroupManager(Group(10, 10, "Top", Series(100))),
            ImageManager(),
            [new FakeProvider { Name = "slow", Exception = new OperationCanceledException(cts.Token) }],
            new InMemoryStateStore(),
            NullLogger<ImagePlannerService>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PlanAsync(new PlannerRequest(), cts.Token));
    }

    [Fact]
    public async Task MalformedJsonFromProviderIsIsolatedAndLogged()
    {
        var logger = new RecordingLogger<ImagePlannerService>();
        var service = CreateService(
            GroupManager(Group(10, 10, "Top", Series(100))),
            ImageManager(),
            [new FakeProvider { Name = "bad-json", Exception = new JsonException("invalid json") }],
            new InMemoryStateStore(),
            logger);

        var report = await service.PlanAsync(new PlannerRequest(), CancellationToken.None);

        Assert.Single(report.Groups);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Exception is JsonException);
    }
}

public sealed class FanartTvAdapterTests
{
    private static FanartTvAdapter Adapter(HttpResponseMessage response)
    {
        var httpFactory = DynamicFake.Create<IHttpClientFactory>(fake => fake
            .WithBehavior("CreateClient", _ => new HttpClient(new StubHttpMessageHandler(response))));
        return new FanartTvAdapter(httpFactory, Options.Create(new ImagePlannerOptions { FanartTvApiKey = "api-key" }), NullLogger<FanartTvAdapter>.Instance);
    }

    [Fact]
    public async Task SkipsNonJsonContentTypeBeforeParsing()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>not json</html>", Encoding.UTF8, "text/html"),
        };
        var adapter = Adapter(response);

        var candidates = await adapter.GetCandidatesAsync(new ProviderLookup(1, [42], []), CancellationToken.None);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task MalformedJsonBodiesSurfaceAsJsonExceptionForThePlannerToIsolate()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"tvposter\": [", Encoding.UTF8, "application/json"),
        };
        var adapter = Adapter(response);

        await Assert.ThrowsAnyAsync<JsonException>(() => adapter.GetCandidatesAsync(new ProviderLookup(1, [42], []), CancellationToken.None));
    }
}

public sealed class JsonContentTypeTests
{
    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/problem+json")]
    [InlineData("text/json")]
    public void AcceptsJsonContentTypes(string contentType) => Assert.True(HttpSafety.IsAllowedJsonContentType(contentType));

    [Theory]
    [InlineData("text/html")]
    [InlineData("image/png")]
    [InlineData("application/xml")]
    public void RejectsNonJsonContentTypes(string contentType) => Assert.False(HttpSafety.IsAllowedJsonContentType(contentType));

    [Fact]
    public void AllowsMissingContentTypeAndLetsTheParserValidate()
    {
        Assert.True(HttpSafety.IsAllowedJsonContentType(null));
        Assert.True(HttpSafety.IsAllowedJsonContentType(""));
        Assert.True(HttpSafety.IsAllowedJsonContentType("   "));
    }
}
