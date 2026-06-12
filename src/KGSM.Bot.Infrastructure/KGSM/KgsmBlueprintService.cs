using KGSM.Bot.Core.Common;

using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

// KGSM.Lib 1.1.0 added TheKrystalShip.KGSM.Core.Models.KgsmOptions, which collides with
// the bot's own config type; pin the unqualified name to the bot's.
using KgsmOptions = KGSM.Bot.Infrastructure.Configuration.KgsmOptions;

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
            var blueprints = await Task.Run(() => _kgsmClient.Blueprints.ListDetailed());

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
}
