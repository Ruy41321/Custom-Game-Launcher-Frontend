using GameLauncher.Core.Models;

namespace GameLauncher.Core.Api;

/// <summary>
/// What the server will accept. The one catalog-adjacent route that carries no bearer token:
/// the launcher asks before it has a session, and nothing in the answer depends on who asks.
/// </summary>
public interface ICapabilitiesApi
{
    /// <summary>
    /// Throws <see cref="ApiException"/> like every other call — including
    /// <see cref="ApiErrorCode.NotFound"/> from a server older than the route. Deciding what to
    /// do about that is <see cref="IServerCapabilityProvider"/>'s job, not every caller's.
    /// </summary>
    Task<ServerCapabilities> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The launcher's answer to "what may I send this server", cached and never failing.
///
/// Separate from <see cref="ICapabilitiesApi"/> because the two answer different questions: the
/// client says what the server replied, and this says what to do — which for an unreachable or
/// older server is to carry on with <see cref="ServerCapabilities.Fallback"/> rather than to
/// refuse to publish. A launcher that could not upload because it could not read a document
/// about uploading would be worse than one that guesses, which is what it did before.
/// </summary>
public interface IServerCapabilityProvider
{
    Task<ServerCapabilities> GetAsync(CancellationToken cancellationToken = default);
}
