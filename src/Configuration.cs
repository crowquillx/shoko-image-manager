using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shoko.Abstractions.Config.Attributes;
using ShokoConfiguration = Shoko.Abstractions.Config.IConfiguration;

namespace Shoko.ImagePlanner;

public sealed class ImagePlannerOptions : ShokoConfiguration
{
    public const string SectionName = "Shoko:Plugins:ImagePlanner";

    // The plugin copies these values into its in-memory options singleton once at startup and does
    // not reload them on save, so every setting below only takes effect after a Shoko restart.

    [EnvironmentVariable("SHOKO__PLUGINS__IMAGEPLANNER__ENABLED")]
    [RequiresRestart]
    public bool Enabled { get; set; } = true;
    [EnvironmentVariable("SHOKO__PLUGINS__IMAGEPLANNER__FANARTTVAPIKEY")]
    [RequiresRestart]
    public string? FanartTvApiKey { get; set; }
    [EnvironmentVariable("SHOKO__PLUGINS__IMAGEPLANNER__FANARTTVCLIENTKEY")]
    [RequiresRestart]
    public string? FanartTvClientKey { get; set; }
    [Range(1, 120)]
    [RequiresRestart]
    public int RequestTimeoutSeconds { get; set; } = 20;
    [Range(1024, 10_485_760)]
    [RequiresRestart]
    public int MaxJsonResponseBytes { get; set; } = 1_048_576;
    [Range(1024, 52_428_800)]
    [RequiresRestart]
    public int MaxImageBytes { get; set; } = 20 * 1024 * 1024;
    [Range(1, 10_080)]
    [RequiresRestart]
    public int ReconciliationIntervalMinutes { get; set; } = 1_440;
    [Range(1, 365)]
    [RequiresRestart]
    public int IdempotencyReceiptRetentionDays { get; set; } = 30;
    [RequiresRestart]
    public bool RecurringReconciliationEnabled { get; set; }
    [MaxLength(5)]
    [RequiresRestart]
    public string PreferredLanguage { get; set; } = "en";
    [RequiresRestart]
    public int FanartTvPriority { get; set; } = 10;

    public void CopyFrom(ImagePlannerOptions source)
    {
        Enabled = source.Enabled;
        FanartTvApiKey = source.FanartTvApiKey;
        FanartTvClientKey = source.FanartTvClientKey;
        RequestTimeoutSeconds = source.RequestTimeoutSeconds;
        MaxJsonResponseBytes = source.MaxJsonResponseBytes;
        MaxImageBytes = source.MaxImageBytes;
        ReconciliationIntervalMinutes = source.ReconciliationIntervalMinutes;
        IdempotencyReceiptRetentionDays = source.IdempotencyReceiptRetentionDays;
        RecurringReconciliationEnabled = source.RecurringReconciliationEnabled;
        PreferredLanguage = source.PreferredLanguage;
        FanartTvPriority = source.FanartTvPriority;
    }
}

public sealed class PluginState
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, ProviderImageState> ProviderImages { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, AssignmentLedgerEntry> Assignments { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, MutationReceipt> Idempotency { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ProviderImageState
{
    public required string CandidateId { get; set; }
    public required string Provider { get; set; }
    public required string ResourceId { get; set; }
    public required string ImageId { get; set; }
    public string? ContentHash { get; set; }
    public string? ImageType { get; set; }
    public DateTimeOffset IngestedAt { get; set; }
}

public sealed class AssignmentLedgerEntry
{
    public required int SeriesId { get; set; }
    public required string ImageType { get; set; }
    public required string CandidateId { get; set; }
    public required string ImageId { get; set; }
    public DateTimeOffset AppliedAt { get; set; }
}

public sealed class MutationReceipt
{
    public required string Operation { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public int Changed { get; set; }
    public PlannerReport? Report { get; set; }
}

public interface IPluginStateStore
{
    PluginState Load();
    void Save(PluginState state);
}

public sealed class AtomicPluginStateStore : IPluginStateStore
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly string _path;
    private readonly object _gate = new();

    public AtomicPluginStateStore(Shoko.Abstractions.Plugin.IApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.DataPath);
        _path = Path.Combine(paths.DataPath, "shoko-image-planner-state.json");
    }

    public PluginState Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
                return new PluginState();
            try
            {
                using var stream = File.OpenRead(_path);
                return System.Text.Json.JsonSerializer.Deserialize<PluginState>(stream, JsonOptions) ?? new PluginState();
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The image planner state file is invalid.", exception);
            }
            catch (IOException exception)
            {
                throw new IOException("The image planner state file could not be read.", exception);
            }
        }
    }

    public void Save(PluginState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.WriteThrough))
                {
                    System.Text.Json.JsonSerializer.Serialize(stream, state, JsonOptions);
                    stream.Flush(true);
                }
                File.Move(temporaryPath, _path, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
    }
}
