using KGSM.Bot.Core.Common;

using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace KGSM.Bot.Infrastructure.KGSM;

/// <summary>
/// Implementation of IBlueprintService using KGSM-Lib
/// </summary>
public class KgsmBlueprintService : Core.Interfaces.IBlueprintService
{
    private readonly IKgsmClient _kgsmClient;
    private readonly KgsmOptions _options;
    private readonly ILogger<KgsmBlueprintService> _logger;

    public KgsmBlueprintService(
        IKgsmClient kgsmClient,
        IOptions<KgsmOptions> options,
        ILogger<KgsmBlueprintService> logger)
    {
        _kgsmClient = kgsmClient;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyDictionary<string, Blueprint>>> GetAllAsync()
    {
        try
        {
            _logger.LogInformation("Getting all blueprints");

            // KGSM-Lib operates synchronously, but we'll maintain async signature for consistency
            var blueprints = await Task.Run(() => _kgsmClient.Blueprints.GetAll());

            var result = new Dictionary<string, Blueprint>();

            foreach (var (name, kgsmBlueprint) in blueprints)
            {
                string? onlineTrigger = null;

                if (_options.Blueprints.TryGetValue(name, out var blueprintConfig))
                {
                    onlineTrigger = blueprintConfig.OnlineTrigger;
                }

                result[name] = new Blueprint
                {
                    Name = name,
                    Ports = kgsmBlueprint.Ports,
                    ExecutableFile = kgsmBlueprint.ExecutableFile,
                    SteamAppId = kgsmBlueprint.SteamAppId,
                    IsSteamAccountRequired = kgsmBlueprint.IsSteamAccountRequired,
                };
            }

            _logger.LogInformation("Retrieved {Count} blueprints", result.Count);
            return Result.Success<IReadOnlyDictionary<string, Blueprint>>(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting blueprints");
            return Result.Failure<IReadOnlyDictionary<string, Blueprint>>(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<Result> CreateAsync(Blueprint blueprint)
    {
        try
        {
            _logger.LogInformation("Creating blueprint {BlueprintName}", blueprint.Name);

            // Convert to KGSM Blueprint
            var kgsmBlueprint = new Blueprint
            {
                Name = blueprint.Name,
                Ports = blueprint.Ports,
                ExecutableFile = blueprint.ExecutableFile,
                SteamAppId = blueprint.SteamAppId,
                IsSteamAccountRequired = blueprint.IsSteamAccountRequired
            };

            // KGSM-Lib operates synchronously, but we'll maintain async signature for consistency
            await Task.Run(() => _kgsmClient.Blueprints.Create(kgsmBlueprint));

            _logger.LogInformation("Created blueprint {BlueprintName}", blueprint.Name);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating blueprint {BlueprintName}", blueprint.Name);
            return Result.Failure(ex.Message);
        }
    }
}
