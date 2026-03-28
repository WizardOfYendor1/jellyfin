using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.Configuration;
using Jellyfin.LiveTv.Guide;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.TunerHosts;

/// <inheritdoc />
public class TunerHostManager : ITunerHostManager
{
    private const int TunerDiscoveryDurationMs = 3000;

    private readonly ILogger<TunerHostManager> _logger;
    private readonly IConfigurationManager _config;
    private readonly ITaskManager _taskManager;
    private readonly ITunerHost[] _tunerHosts;

    /// <summary>
    /// Initializes a new instance of the <see cref="TunerHostManager"/> class.
    /// </summary>
    /// <param name="logger">The <see cref="ILogger{T}"/>.</param>
    /// <param name="config">The <see cref="IConfigurationManager"/>.</param>
    /// <param name="taskManager">The <see cref="ITaskManager"/>.</param>
    /// <param name="tunerHosts">The <see cref="IEnumerable{T}"/>.</param>
    public TunerHostManager(
        ILogger<TunerHostManager> logger,
        IConfigurationManager config,
        ITaskManager taskManager,
        IEnumerable<ITunerHost> tunerHosts)
    {
        _logger = logger;
        _config = config;
        _taskManager = taskManager;
        _tunerHosts = tunerHosts.Where(t => t.IsSupported).ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyList<ITunerHost> TunerHosts => _tunerHosts;

    /// <inheritdoc />
    public IEnumerable<NameIdPair> GetTunerHostTypes()
        => _tunerHosts.OrderBy(i => i.Name).Select(i => new NameIdPair
        {
            Name = i.Name,
            Id = i.Type
        });

    /// <inheritdoc />
    public async Task<TunerHostInfo> SaveTunerHost(TunerHostInfo info, bool dataSourceChanged = true)
    {
        info = JsonSerializer.Deserialize<TunerHostInfo>(JsonSerializer.SerializeToUtf8Bytes(info))!;

        var provider = _tunerHosts.FirstOrDefault(i => string.Equals(info.Type, i.Type, StringComparison.OrdinalIgnoreCase));

        if (provider is null)
        {
            throw new ResourceNotFoundException();
        }

        if (provider is IConfigurableTunerHost configurable)
        {
            await configurable.Validate(info).ConfigureAwait(false);
        }

        var config = _config.GetLiveTvConfiguration();

        var list = config.TunerHosts;
        var index = Array.FindIndex(list, i => string.Equals(i.Id, info.Id, StringComparison.OrdinalIgnoreCase));

        if (index == -1 || string.IsNullOrWhiteSpace(info.Id))
        {
            info.Id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            config.TunerHosts = [..list, info];
        }
        else
        {
            // Preserve advanced FFmpeg properties that the web UI doesn't know about.
            // Without this, saving a tuner from the UI overwrites these with null.
            // Sending -1 explicitly clears a value back to null (system default).
            var existing = config.TunerHosts[index];
            info.AnalyzeDurationMs = PreserveOrClear(info.AnalyzeDurationMs, existing.AnalyzeDurationMs);
            info.ProbeSizeBytes = PreserveOrClear(info.ProbeSizeBytes, existing.ProbeSizeBytes);
            info.MaxDelayUs = PreserveOrClear(info.MaxDelayUs, existing.MaxDelayUs);
            info.SegmentLength = PreserveOrClear(info.SegmentLength, existing.SegmentLength);
            info.MinSegments = PreserveOrClear(info.MinSegments, existing.MinSegments);
            info.HlsInitTimeSec = PreserveOrClear(info.HlsInitTimeSec, existing.HlsInitTimeSec);
            if (info.OutputFFlags is null)
            {
                info.OutputFFlags = existing.OutputFFlags;
            }
            else if (info.OutputFFlags.Length == 0)
            {
                info.OutputFFlags = null;
            }

            config.TunerHosts[index] = info;
        }

        _config.SaveConfiguration("livetv", config);

        if (dataSourceChanged)
        {
            _taskManager.CancelIfRunningAndQueue<RefreshGuideScheduledTask>();
        }

        return info;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TunerHostInfo> DiscoverTuners(bool newDevicesOnly)
    {
        var configuredDeviceIds = _config.GetLiveTvConfiguration().TunerHosts
            .Where(i => !string.IsNullOrWhiteSpace(i.DeviceId))
            .Select(i => i.DeviceId)
            .ToList();

        foreach (var host in _tunerHosts)
        {
            var discoveredDevices = await DiscoverDevices(host, TunerDiscoveryDurationMs, CancellationToken.None).ConfigureAwait(false);
            foreach (var tuner in discoveredDevices)
            {
                if (!newDevicesOnly || !configuredDeviceIds.Contains(tuner.DeviceId, StringComparer.OrdinalIgnoreCase))
                {
                    yield return tuner;
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task ScanForTunerDeviceChanges(CancellationToken cancellationToken)
    {
        foreach (var host in _tunerHosts)
        {
            await ScanForTunerDeviceChanges(host, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ScanForTunerDeviceChanges(ITunerHost host, CancellationToken cancellationToken)
    {
        var discoveredDevices = await DiscoverDevices(host, TunerDiscoveryDurationMs, cancellationToken).ConfigureAwait(false);

        var configuredDevices = _config.GetLiveTvConfiguration().TunerHosts
            .Where(i => string.Equals(i.Type, host.Type, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var device in discoveredDevices)
        {
            var configuredDevice = configuredDevices.FirstOrDefault(i => string.Equals(i.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase));

            if (configuredDevice is not null && !string.Equals(device.Url, configuredDevice.Url, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Tuner url has changed from {PreviousUrl} to {NewUrl}", configuredDevice.Url, device.Url);

                configuredDevice.Url = device.Url;
                await SaveTunerHost(configuredDevice).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Preserves an existing value when the incoming value is null (UI didn't send it),
    /// or clears to null when the incoming value is -1 (explicit reset to default).
    /// </summary>
    private static int? PreserveOrClear(int? incoming, int? existing)
    {
        if (incoming is null)
        {
            return existing;
        }

        return incoming == -1 ? null : incoming;
    }

    private async Task<IList<TunerHostInfo>> DiscoverDevices(ITunerHost host, int discoveryDurationMs, CancellationToken cancellationToken)
    {
        try
        {
            var discoveredDevices = await host.DiscoverDevices(discoveryDurationMs, cancellationToken).ConfigureAwait(false);

            foreach (var device in discoveredDevices)
            {
                _logger.LogInformation("Discovered tuner device {0} at {1}", host.Name, device.Url);
            }

            return discoveredDevices;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error discovering tuner devices");

            return Array.Empty<TunerHostInfo>();
        }
    }
}
