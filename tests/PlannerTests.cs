using System.Text;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Plugin;
using Xunit;

namespace Shoko.ImagePlanner.Tests;

public sealed class PlannerTests
{
    private static PlannerCandidate Candidate(string id, ImageEntityType type = ImageEntityType.Primary, int priority = 0, ulong? hash = null)
        => new(id, type, DataSource.FanartTV, null, id, 1000, 1500, "en", 8, 20, false, false, null, priority, "https://assets.fanart.tv/image.jpg");

    [Fact]
    public void AssignsGloballyUniqueImagesWithinOneGroup()
    {
        var planner = new GlobalAssignmentPlanner();
        var result = planner.Assign(ImageEntityType.Primary, [
            new PlannerSeries(2, "Second", [Candidate("a"), Candidate("b")]),
            new PlannerSeries(1, "First", [Candidate("a", priority: 1), Candidate("b")]),
        ], "en");

        Assert.Equal(new[] { "a", "b" }, result.Assignments.OrderBy(item => item.SeriesId).Select(item => item.CandidateId));
        Assert.All(result.Assignments, item => Assert.True(item.IsUnique));
    }

    [Fact]
    public void UsesHungarianAssignmentForLargeGroups()
    {
        var planner = new GlobalAssignmentPlanner();
        var series = Enumerable.Range(1, 21)
            .Select(id => new PlannerSeries(id, $"Series {id}", [Candidate($"candidate-{id}")]))
            .ToArray();

        var result = planner.Assign(ImageEntityType.Primary, series, "en");

        Assert.Equal(21, result.Assignments.Count);
        Assert.Equal(21, result.Assignments.Select(item => item.CandidateId).Distinct().Count());
        Assert.All(result.Assignments, item => Assert.True(item.IsUnique));
    }

    [Fact]
    public void UsesStableTieBreaks()
    {
        var planner = new GlobalAssignmentPlanner();
        var input = new[]
        {
            new PlannerSeries(1, "A", [Candidate("z"), Candidate("a")]),
            new PlannerSeries(2, "B", [Candidate("z"), Candidate("a")]),
        };
        Assert.NotEqual(Candidate("a").ExactKey, Candidate("z").ExactKey);
        var first = planner.Assign(ImageEntityType.Primary, input, "en");
        var second = planner.Assign(ImageEntityType.Primary, input, "en");
        Assert.Equal(first.Assignments.Select(item => item.CandidateId), second.Assignments.Select(item => item.CandidateId));
        Assert.Equal(2, first.UniqueCandidateCount);
        Assert.Equal(new[] { "a", "z" }, first.Assignments.OrderBy(item => item.SeriesId).Select(item => item.CandidateId));
    }

    [Fact]
    public void CarriesSelectedLocalImagePreviewIntoAssignment()
    {
        var imageId = Guid.NewGuid();
        var candidate = new PlannerCandidate("local", ImageEntityType.Primary, DataSource.User, imageId, "resource", 1000, 1500, "en", null, null, true, true, null, 0, null, true);

        var result = new GlobalAssignmentPlanner().Assign(ImageEntityType.Primary, [new PlannerSeries(1, "A", [candidate])], "en");

        var preview = Assert.Single(result.Assignments).Preview;
        Assert.NotNull(preview);
        Assert.Equal(imageId, preview.ImageId);
        Assert.Null(preview.DownloadUrl);
        Assert.Equal(1000, preview.Width);
        Assert.Equal(1500, preview.Height);
        Assert.Equal(DataSource.User, preview.Source);
        Assert.Equal("en", preview.Language);
    }

    [Fact]
    public void OmitsUnsafeProviderPreviewUrlWithoutDroppingOtherMetadata()
    {
        var candidate = Candidate("provider") with { DownloadUrl = "https://assets.fanart.tv/image.jpg?api-key=secret" };

        var preview = PlannerAssignmentPreview.FromCandidate(candidate);

        Assert.Null(preview.DownloadUrl);
        Assert.Equal(candidate.Source, preview.Source);
        Assert.Equal(candidate.Width, preview.Width);
        Assert.Equal(candidate.Height, preview.Height);
        Assert.Equal(candidate.LanguageCode, preview.Language);
    }

    [Fact]
    public void DoesNotTreatDifferentImagesAsPerceptualDuplicates()
    {
        var planner = new GlobalAssignmentPlanner();
        var result = planner.Assign(ImageEntityType.Primary, [
            new PlannerSeries(1, "A", [Candidate("a")]),
            new PlannerSeries(2, "B", [Candidate("b")]),
        ], "en");
        Assert.False(result.HasInsufficientUniqueCandidates);
        Assert.All(result.Assignments, item => Assert.True(item.IsUnique));
    }

    [Fact]
    public void ProtectsManualPreferencesUntilForceIsUsed()
    {
        var imageId = Guid.NewGuid();
        Assert.False(PreferenceProtection.CanReplace(imageId, null, false));
        Assert.True(PreferenceProtection.CanReplace(imageId, imageId, false));
        Assert.True(PreferenceProtection.CanReplace(imageId, null, true));
    }

    [Fact]
    public void ReportsInsufficientCandidatesAndKeepsBestFallback()
    {
        var planner = new GlobalAssignmentPlanner();
        var result = planner.Assign(ImageEntityType.Backdrop, [
            new PlannerSeries(1, "A", [Candidate("one", ImageEntityType.Backdrop)]),
            new PlannerSeries(2, "B", [Candidate("one", ImageEntityType.Backdrop)]),
        ], "en");
        Assert.True(result.HasInsufficientUniqueCandidates);
        Assert.Equal(2, result.Assignments.Count);
        Assert.Single(result.Assignments, item => item.IsUnique);
        Assert.Single(result.Assignments, item => item.IsFallback);
    }
}

public sealed class ProviderSafetyTests
{
    [Fact]
    public void MapsFanartResponseToStableCandidatesWithoutGuessing()
    {
        var json = Encoding.UTF8.GetBytes("""
            {"tvposter":[{"id":"77","url":"https://assets.fanart.tv/poster.jpg","lang":"en","likes":4,"width":1000,"height":1500}],"showbackground":[{"id":"88","url":"https://assets.fanart.tv/background.jpg"}]}
            """);
        var candidates = FanartResponseMapper.Map(json, "tv", "123", 10);
        Assert.Equal(new[] { "fanarttv:tv:123:showbackground:88", "fanarttv:tv:123:tvposter:77" }, candidates.Select(item => item.CandidateId));
        Assert.All(candidates, item => Assert.Equal(DataSource.FanartTV, item.Source));
    }

    [Fact]
    public void RejectsUnsafeImageUrisAndUnknownContentTypes()
    {
        Assert.False(HttpSafety.IsAllowedFanartImageUri(new Uri("http://assets.fanart.tv/image.jpg")));
        Assert.False(HttpSafety.IsAllowedFanartImageUri(new Uri("https://evil.example/image.jpg")));
        Assert.False(HttpSafety.IsAllowedImageContentType("text/html"));
        Assert.Equal("image/png", HttpSafety.DetectImageContentType(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
    }

    [Fact]
    public async Task BoundedReadsRejectOversizedBodies()
    {
        using var content = new ByteArrayContent(new byte[32]);
        Assert.Null(await HttpSafety.ReadBoundedAsync(content, 16, CancellationToken.None));
    }
}

public sealed class StateStoreTests
{
    [Fact]
    public void SavesStateByReplacingTheFileAtomically()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var store = new AtomicPluginStateStore(new TestApplicationPaths(directory.FullName));
            store.Save(new PluginState
            {
                ProviderImages = new Dictionary<string, ProviderImageState>
                {
                    ["candidate"] = new ProviderImageState { CandidateId = "candidate", Provider = "fanart.tv", ResourceId = "tv:1:poster:2", ImageId = Guid.NewGuid().ToString(), IngestedAt = DateTimeOffset.UtcNow },
                }
            });
            var loaded = store.Load();
            Assert.Contains("candidate", loaded.ProviderImages.Keys);
            loaded.Idempotency["request-1"] = new MutationReceipt { Operation = "apply", CompletedAt = DateTimeOffset.UtcNow, Changed = 1, Report = new PlannerReport(DateTimeOffset.UtcNow, [], false, 1) };
            store.Save(loaded);
            Assert.Equal(1, store.Load().Idempotency["request-1"].Changed);
            Assert.DoesNotContain("api", File.ReadAllText(Path.Combine(directory.FullName, "shoko-image-planner-state.json")), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private sealed class TestApplicationPaths(string dataPath) : IApplicationPaths
    {
        public string ApplicationPath => dataPath;
        public string WebPath => dataPath;
        public string DataPath => dataPath;
        public string ImagesPath => dataPath;
        public string PluginsPath => dataPath;
        public string ThemesPath => dataPath;
        public string ConfigurationsPath => dataPath;
        public string LogsPath => dataPath;
    }
}
