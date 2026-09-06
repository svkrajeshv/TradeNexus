using NexusApp.Interfaces;

namespace NexusApp.Services;

/// <summary>
/// Cached reader for the <c>Mcx.Enabled</c> master toggle.
/// <para>
/// The background pollers consult this on every loop iteration - as often as once
/// per second - so hitting the settings store each time would add pointless database
/// traffic. A short TTL keeps the value cheap to read while still picking up a change
/// made in Settings within a few seconds.
/// </para>
/// <para>
/// Reads fail closed: if the setting cannot be read, commodity trading is treated as
/// disabled so a transient fault can never silently widen trading behaviour.
/// </para>
/// </summary>
public sealed class McxToggle(IServiceProvider serviceProvider, ILogger<McxToggle> logger)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);

    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly ILogger<McxToggle> _logger = logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool _enabled;
    private DateTime _readAtUtc;

    /// <summary>True when commodity (MCX) trading is enabled.</summary>
    public async Task<bool> IsEnabledAsync(CancellationToken ct = default)
    {
        if (DateTime.UtcNow - _readAtUtc < CacheTtl)
            return _enabled;

        await _gate.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _readAtUtc < CacheTtl)
                return _enabled;

            using var scope = _serviceProvider.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            _enabled = await settings.GetSettingAsync<bool?>("Mcx.Enabled") ?? false;
            _readAtUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read Mcx.Enabled; treating commodity trading as disabled.");
            _enabled = false;
            _readAtUtc = DateTime.UtcNow;
        }
        finally
        {
            _gate.Release();
        }

        return _enabled;
    }

    /// <summary>
    /// Drops the cached value so the next read hits the settings store. Called after
    /// the toggle is saved so the change takes effect immediately.
    /// </summary>
    public void Invalidate() => _readAtUtc = default;
}
