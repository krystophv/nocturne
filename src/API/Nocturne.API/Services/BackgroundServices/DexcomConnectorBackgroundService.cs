using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Dexcom.Configurations;
using Nocturne.Connectors.Dexcom.Services;
using Nocturne.Core.Constants;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Polls Dexcom Share in step with the sensor rather than on a free-running timer. After each
/// successful sync the next one is scheduled just after the next reading should reach Share (see
/// <see cref="DexcomSyncAlignment"/>), with <c>SyncIntervalMinutes</c> still the longest it waits.
/// The tick is shortened so that schedule is met to within seconds rather than a minute.
/// </summary>
public class DexcomConnectorBackgroundService
    : ConnectorBackgroundService<DexcomConnectorService, DexcomConnectorConfiguration>
{
    /// <param name="serviceProvider">Service provider used to create a DI scope per sync cycle.</param>
    /// <param name="budget">The process-wide budget.</param>
    /// <param name="activeTenants">The active tenants every poller reads.</param>
    /// <param name="logger">Logger instance for this background service.</param>
    /// <param name="nudge">Delivers configuration writes for this connector.</param>
    /// <param name="metrics">Connector sync instruments.</param>
    public DexcomConnectorBackgroundService(
        IServiceProvider serviceProvider,
        ConnectorSyncBudget budget,
        ActiveTenantSnapshot activeTenants,
        ILogger<DexcomConnectorBackgroundService> logger,
        ConnectorPollerNudge? nudge = null,
        ConnectorSyncMetrics? metrics = null
    )
        : base(serviceProvider, budget, activeTenants, logger, nudge, metrics) { }

    /// <summary>
    /// A tick costs nothing for a tenant that is not due (the schedule is checked before any scope is
    /// opened) and the tenant list is cached, so a short tick only sharpens when a due sync starts.
    /// </summary>
    protected override TimeSpan PollInterval => TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    protected override async Task<DateTime?> GetAlignedSyncTimeAsync(
        IServiceProvider scopeProvider,
        DexcomConnectorConfiguration config,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var publisher = scopeProvider.GetService<IGlucosePublisher>();
        if (publisher is null)
            return null;

        var latest = await publisher.GetLatestSensorGlucoseTimestampAsync(
            DataSources.DexcomConnector, cancellationToken);
        if (latest is not { } reading)
            return null;

        reading = reading.Kind switch
        {
            DateTimeKind.Local => reading.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(reading, DateTimeKind.Utc),
            _ => reading,
        };

        return DexcomSyncAlignment.NextSyncAt(reading, now, Random.Shared.NextDouble());
    }
}
