
using KGSM.Bot.Core.Interfaces;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Events;

namespace KGSM.Bot.Infrastructure.KGSM;

/// <summary>
/// Implementation of IServerEventHandler using KGSM-Lib
/// </summary>
public class KgsmServerEventHandler : IServerEventHandler
{
    private readonly IKgsmClient _kgsmClient;
    private readonly ILogger<KgsmServerEventHandler> _logger;
    private readonly List<Func<string, string, Task>> _instanceInstalledHandlers = new();
    private readonly List<Func<string, Task>> _instanceStartedHandlers = new();
    private readonly List<Func<string, Task>> _instanceStoppedHandlers = new();
    private readonly List<Func<string, Task>> _instanceUninstalledHandlers = new();
    private readonly List<Func<string, InstanceStatus, Task>> _runningStatusUpdatedHandlers = new();

    public KgsmServerEventHandler(
        IKgsmClient kgsmClient,
        ILogger<KgsmServerEventHandler> logger)
    {
        _kgsmClient = kgsmClient;
        _logger = logger;
    }

    // Second guard, independent of the coordinator's: Initialize() starts the journal reader, and
    // a second call would run a second read loop over one reader's cursor state. Public entry
    // point, so it defends itself rather than trusting every caller.
    private bool _initialized;

    /// <inheritdoc />
    public void Initialize()
    {
        if (_initialized)
        {
            _logger.LogDebug("KGSM event handlers already initialized — skipping");
            return;
        }
        _initialized = true;

        _logger.LogInformation("Initializing KGSM event handlers");

        // Register KGSM event handlers
        _kgsmClient.Events.RegisterHandler<InstanceInstalledData>(OnInstanceInstalledAsync);
        _kgsmClient.Events.RegisterHandler<InstanceStartedData>(OnInstanceStartedAsync);
        _kgsmClient.Events.RegisterHandler<InstanceStoppedData>(OnInstanceStoppedAsync);
        _kgsmClient.Events.RegisterHandler<InstanceUninstalledData>(OnInstanceUninstalledAsync);

        // Initialize KGSM event listening
        _kgsmClient.Events.Initialize();

        _logger.LogInformation("KGSM event handlers initialized");
    }

    /// <inheritdoc />
    public void RegisterInstanceInstalledHandler(Func<string, string, Task> handler)
    {
        _instanceInstalledHandlers.Add(handler);
    }

    /// <inheritdoc />
    public void RegisterInstanceStartedHandler(Func<string, Task> handler)
    {
        _instanceStartedHandlers.Add(handler);
    }

    /// <inheritdoc />
    public void RegisterInstanceStoppedHandler(Func<string, Task> handler)
    {
        _instanceStoppedHandlers.Add(handler);
    }

    /// <inheritdoc />
    public void RegisterInstanceUninstalledHandler(Func<string, Task> handler)
    {
        _instanceUninstalledHandlers.Add(handler);
    }

    /// <inheritdoc />
    public void RegisterRunningStatusUpdatedHandler(Func<string, InstanceStatus, Task> handler)
    {
        _runningStatusUpdatedHandlers.Add(handler);
    }

    private async Task OnInstanceInstalledAsync(InstanceInstalledData data)
    {
        _logger.LogInformation("Instance installed event: {InstanceName} using blueprint {BlueprintName}",
            data.InstanceName, data.Blueprint);

        foreach (var handler in _instanceInstalledHandlers)
        {
            try
            {
                await handler(data.Blueprint, data.InstanceName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling instance installed event for {InstanceName}",
                    data.InstanceName);
            }
        }
    }

    private async Task OnInstanceStartedAsync(InstanceStartedData data)
    {
        _logger.LogInformation("Instance started event: {InstanceName}", data.InstanceName);

        // Call instance started handlers
        foreach (var handler in _instanceStartedHandlers)
        {
            try
            {
                await handler(data.InstanceName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling instance started event for {InstanceName}",
                    data.InstanceName);
            }
        }

        // Call running status updated handlers
        foreach (var handler in _runningStatusUpdatedHandlers)
        {
            try
            {
                await handler(data.InstanceName, InstanceStatus.Active);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling running status updated event for {InstanceName}",
                    data.InstanceName);
            }
        }
    }

    private async Task OnInstanceStoppedAsync(InstanceStoppedData data)
    {
        _logger.LogInformation("Instance stopped event: {InstanceName}", data.InstanceName);

        // Call instance stopped handlers
        foreach (var handler in _instanceStoppedHandlers)
        {
            try
            {
                await handler(data.InstanceName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling instance stopped event for {InstanceName}",
                    data.InstanceName);
            }
        }

        // Call running status updated handlers
        foreach (var handler in _runningStatusUpdatedHandlers)
        {
            try
            {
                await handler(data.InstanceName, InstanceStatus.Inactive);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling running status updated event for {InstanceName}",
                    data.InstanceName);
            }
        }
    }

    private async Task OnInstanceUninstalledAsync(InstanceUninstalledData data)
    {
        _logger.LogInformation("Instance uninstalled event: {InstanceName}", data.InstanceName);

        // Call instance uninstalled handlers
        foreach (var handler in _instanceUninstalledHandlers)
        {
            try
            {
                await handler(data.InstanceName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling instance uninstalled event for {InstanceName}",
                    data.InstanceName);
            }
        }

        // Call running status updated handlers
        foreach (var handler in _runningStatusUpdatedHandlers)
        {
            try
            {
                await handler(data.InstanceName, InstanceStatus.Inactive);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling running status updated event for {InstanceName}",
                    data.InstanceName);
            }
        }
    }
}
