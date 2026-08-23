namespace KGSM.Bot.Core.Interfaces;

/// <summary>
/// Looks up how a server should be written down, given only its id.
/// </summary>
/// <remarks>
/// Most surfaces already hold the instance and compose the label themselves
/// (<see cref="Models.ServerLabel"/>). This is for the ones that hold an id and nothing else — an
/// engine event names the server it is about by id, and a channel binding stores one — so a message
/// about a server can still call it what the person reading it calls it.
/// <para>
/// It reads the cached inventory, so a lookup costs no kgsm process. <b>A server the inventory does
/// not know is its id</b>, which is also the answer for one that has just been uninstalled and for
/// a read that failed: a missing label is never invented, and never left blank.
/// </para>
/// </remarks>
public interface IServerLabels
{
    /// <summary>The server's display name, or its id when it has none or is unknown here.</summary>
    Task<string> LabelAsync(string instanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The display name with the id beside it (<c>Fixed Name (factorio-42)</c>), or the bare id when
    /// the server carries no label of its own.
    /// </summary>
    Task<string> DescribeAsync(string instanceId, CancellationToken cancellationToken = default);
}
