using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;

namespace GameLauncher.App.Services;

/// <summary>
/// Plays one video at a time, inside the launcher. Behind an interface for the reason
/// <see cref="IImageProvider"/> is: what is underneath needs a **native** library loaded and a
/// window to draw into, and a view model that could not be constructed without those is a view
/// model nobody tests.
///
/// <para>
/// The interface is deliberately a state machine and nothing more — play this URL, stop, is it
/// available, did it fail. Whether a picture actually appears is the one thing no test here can
/// answer, so what is tested is everything around it and the picture is checked by hand in the
/// window.
/// </para>
///
/// <para>
/// <see cref="Player"/> is typed as <see cref="object"/> on purpose. The view binds it to
/// <c>VideoView.MediaPlayer</c>, but nothing above the view has any business naming a
/// LibVLCSharp type, and a substitute in a test has nothing to hand back if the property
/// demands one.
/// </para>
/// </summary>
public interface IVideoPlayback : IDisposable
{
    /// <summary>
    /// Whether this machine can play anything at all. <b>False is an ordinary outcome, not an
    /// error</b>: on Linux there is no NuGet package carrying libvlc, so playback depends on the
    /// VLC the distribution installed, and a launcher on a machine without it must show the rest
    /// of the page rather than refuse to draw it.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>The object a <c>VideoView</c> binds to. Null where nothing was initialised.</summary>
    object? Player { get; }

    /// <summary>Starts <paramref name="url"/>, replacing whatever was playing.</summary>
    /// <returns>False when nothing could be started, so a caller can say so.</returns>
    bool Play(string url);

    /// <summary>
    /// Stops. Safe to call when nothing is playing, which is most of the time.
    /// Not called <c>Stop</c> because that is a reserved word in some languages and the
    /// analyzer refuses it on an interface member (CA1716).
    /// </summary>
    void StopPlayback();
}

/// <summary>
/// The LibVLC implementation. The first native dependency this repository has, which is why two
/// of its properties are about not being there.
///
/// <para>
/// Initialisation is <b>lazy and forgiving</b>: <c>Core.Initialize()</c> throws where the native
/// library is missing, and that is a machine without VLC rather than a bug — so it is caught,
/// logged once, and turned into <see cref="IsAvailable"/> being false. Doing it lazily also
/// keeps a ~100 MB native library out of start-up for the majority of sessions that never open a
/// game page with a trailer on it.
/// </para>
/// </summary>
public sealed class LibVlcVideoPlayback(ILogger<LibVlcVideoPlayback> logger) : IVideoPlayback
{
    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private bool _initialised;
    private bool _disposed;

    public bool IsAvailable
    {
        get
        {
            Initialise();
            return _player is not null;
        }
    }

    public object? Player
    {
        get
        {
            Initialise();
            return _player;
        }
    }

    public bool Play(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !IsAvailable || _libVlc is null || _player is null)
        {
            return false;
        }

        try
        {
            // The URL is the server's own public media URL, http(s) and unsigned (D35). VLC
            // fetches it itself rather than the launcher downloading it first, which is what
            // makes seeking a Range request instead of a wait for the whole file.
            using Media media = new(_libVlc, new Uri(url));
            return _player.Play(media);
        }
        catch (Exception exception) when (exception is UriFormatException or VLCException)
        {
            logger.LogDebug(exception, "Could not play {Url}", url);
            return false;
        }
    }

    public void StopPlayback()
    {
        if (_player is { IsPlaying: true })
        {
            _player.Stop();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _player?.Dispose();
        _libVlc?.Dispose();
        _player = null;
        _libVlc = null;
    }

    private void Initialise()
    {
        if (_initialised || _disposed)
        {
            return;
        }

        _initialised = true;

        try
        {
            LibVLCSharp.Shared.Core.Initialize();
            _libVlc = new LibVLC();
            _player = new MediaPlayer(_libVlc);
        }
        catch (Exception exception)
        {
            // Deliberately broad. What comes back from a missing native library depends on the
            // platform — a DllNotFoundException here, a TypeInitializationException there, a
            // VLCException where the library is present and unusable — and every one of them
            // means the same thing to this launcher: no playback on this machine.
            logger.LogInformation(
                exception, "Video playback is unavailable: LibVLC could not be initialised");
            _libVlc = null;
            _player = null;
        }
    }
}
