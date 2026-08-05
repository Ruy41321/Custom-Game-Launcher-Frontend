using GameLauncher.Core.Api;
using GameLauncher.Core.Models;

namespace GameLauncher.Infrastructure.Tests;

/// <summary>
/// A server that has already answered. Every publishing test needs one, because the packager
/// and the publisher both ask what the limits are before they do anything.
/// </summary>
internal sealed class FixedCapabilities(ServerCapabilities? capabilities = null)
    : IServerCapabilityProvider
{
    public ServerCapabilities Capabilities { get; set; } =
        capabilities ?? ServerCapabilities.Fallback;

    /// <summary>How many times it was asked. The provider is meant to make this cheap.</summary>
    public int Calls { get; private set; }

    public Task<ServerCapabilities> GetAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(Capabilities);
    }

    /// <summary>The same server, but with a different chunk ceiling.</summary>
    public static FixedCapabilities WithChunkBytes(long chunkBytes) =>
        new(ServerCapabilities.Fallback with
        {
            Uploads = ServerCapabilities.Fallback.Uploads with { MaxChunkBytes = chunkBytes },
        });
}
