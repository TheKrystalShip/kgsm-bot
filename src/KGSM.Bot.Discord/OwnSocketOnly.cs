using System.Net.Sockets;

using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;

namespace KGSM.Bot.Discord;

/// <summary>
/// Keeps a route on this bot's own unix sockets, off the member wire.
/// </summary>
/// <remarks>
/// <para>
/// This bot listens on three things: an address other members of its cluster reach it at, and two unix
/// sockets of its own. Kestrel applies one endpoint map to all of them, so a route meant for this host
/// would otherwise answer whoever can reach the member wire.
/// </para>
/// <para>
/// <b>The socket's filesystem permissions are the whole boundary</b> for what this bot says about
/// itself and about its gateway, which is only true while those routes are reachable on the socket and
/// nowhere else. A request arriving anywhere else is answered as no route rather than as refused: what
/// is being stated is that this is not served there, and a 403 would be an admission that it is.
/// </para>
/// </remarks>
internal sealed class OwnSocketOnly : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        ArrivedOnAUnixSocket(context.HttpContext)
            ? next(context)
            : ValueTask.FromResult<object?>(Results.NotFound());

    /// <summary>
    /// Whether this request came in on a unix socket, read off the accepted socket itself.
    /// </summary>
    /// <remarks>
    /// The transport's own view of what it accepted, rather than anything derived from the request:
    /// a header, a host or a local address can be shaped by whoever is calling, and what is being
    /// decided here is which listener answered.
    /// </remarks>
    private static bool ArrivedOnAUnixSocket(HttpContext http) =>
        http.Features.Get<IConnectionSocketFeature>()?.Socket.LocalEndPoint is UnixDomainSocketEndPoint;
}
