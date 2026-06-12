using KGSM.Bot.Core.Common;

using TheKrystalShip.KGSM.Core.Models;

namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// Interface for managing game server blueprints
/// </summary>
public interface IBlueprintService
{
    /// <summary>
    /// Gets all available blueprints
    /// </summary>
    /// <returns>Collection of blueprints</returns>
    Task<Result<IReadOnlyDictionary<string, Blueprint>>> GetAllAsync();
}
