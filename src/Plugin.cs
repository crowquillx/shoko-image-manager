using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shoko.Abstractions.Config.Services;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Plugin.Models;

namespace Shoko.ImagePlanner;

public sealed class ImagePlannerPlugin : IPlugin, IPluginServiceRegistration, IPluginApplicationRegistration
{
    public Guid ID => Guid.Parse("7c5f8f4d-7b1d-4e07-9d4c-5d8bd6f581a2");
    public string Name => "Shoko Image Planner";
    public string? Description => "Assigns distinct provider and Shoko images within each top-level group.";

    public IReadOnlyList<PluginPage> GetPages() =>
    [
        new PluginPage
        {
            Name = "Image Planner",
            Url = "/api/v3/Plugin/ImagePlanner/ui",
            CanEmbed = true,
        },
    ];

    public static void RegisterServices(IServiceCollection services, IApplicationPaths applicationPaths)
    {
        services.AddOptions<ImagePlannerOptions>().ValidateDataAnnotations();
        services.AddSingleton(applicationPaths);
        services.AddSingleton<IPluginStateStore, AtomicPluginStateStore>();
        services.AddSingleton<GlobalAssignmentPlanner>();
        services.AddSingleton<ProviderRegistry>();
        services.AddSingleton<IImageProviderAdapter, FanartTvAdapter>();
        services.AddSingleton<IImagePlannerService, ImagePlannerService>();
        services.AddHttpClient("image-planner-fanart", client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient("image-planner-fanart-images", client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHostedService<ReconciliationHostedService>();
    }

    public static void RegisterServices(IApplicationBuilder application, IApplicationPaths applicationPaths)
    {
        var configurationService = application.ApplicationServices.GetRequiredService<IConfigurationService>();
        configurationService.AddParts([typeof(ImagePlannerOptions)]);
        var provider = configurationService.CreateProvider<ImagePlannerOptions>();
        var options = provider.Load();
        application.ApplicationServices.GetRequiredService<IOptions<ImagePlannerOptions>>().Value.CopyFrom(options);
    }
}

public sealed class ReconciliationHostedService : BackgroundService
{
    private readonly IImagePlannerService _planner;
    private readonly IOptions<ImagePlannerOptions> _options;
    private readonly ILogger<ReconciliationHostedService> _logger;
    private readonly SemaphoreSlim _singleFlight = new(1, 1);

    public ReconciliationHostedService(IImagePlannerService planner, IOptions<ImagePlannerOptions> options, ILogger<ReconciliationHostedService> logger)
    {
        _planner = planner;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.RecurringReconciliationEnabled || !_options.Value.Enabled)
            return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.Value.ReconciliationIntervalMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!await _singleFlight.WaitAsync(0, stoppingToken).ConfigureAwait(false))
                continue;
            try
            {
                await _planner.ReconcileAsync(new PlannerRequest(), "recurring:" + DateTimeOffset.UtcNow.ToString("yyyy-MM-dd-HH", System.Globalization.CultureInfo.InvariantCulture), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Image planner reconciliation failed.");
            }
            finally
            {
                _singleFlight.Release();
            }
        }
    }
}
