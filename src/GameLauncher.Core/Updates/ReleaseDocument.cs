using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace GameLauncher.Core.Updates;

/// <summary>
/// What a launcher release says about itself, and what the signature covers.
///
/// <b>The document is signed, never the artifact</b>, and the difference is the security
/// argument rather than a detail. A signature over the bytes of a zip says only that somebody
/// with the key once produced that zip: it says nothing about which version it is, which
/// channel it belongs to, or which platform it runs on. Binding all of it together in one
/// signed document is what stops a genuine, genuinely signed artifact from being served as
/// something it is not — last year's build as the newest one, or the Linux build to a Windows
/// launcher.
///
/// <see cref="TryParse"/> is only ever called on bytes whose signature has already been
/// checked. It does <b>not</b> rebuild a canonical form to compare against: that would put a
/// second definition of a wire contract in a second language, and the two would drift. The
/// server does that check once, at publish time, where the operator can act on it.
/// </summary>
public sealed record ReleaseDocument
{
    /// <summary>The only schema this launcher knows how to read.</summary>
    public const int KnownSchema = 1;

    /// <summary>
    /// The server's own ceiling on an artifact, repeated here so a mistyped size cannot make
    /// the launcher wait for bytes no disk holds.
    /// </summary>
    public const long MaxArtifactBytes = 4L * 1024 * 1024 * 1024;

    private const int MaxNotesLength = 4096;

    public string Channel { get; init; } = ReleaseTargets.StableChannel;

    public ReleaseVersion Version { get; init; }

    public string Platform { get; init; } = string.Empty;

    public string Arch { get; init; } = string.Empty;

    /// <summary>
    /// Content address of the artifact. Bytes that do not hash to it are refused, so a tampered
    /// download fails even when the document and its signature are untouched.
    /// </summary>
    public string Sha256 { get; init; } = string.Empty;

    public long Size { get; init; }

    /// <summary><c>YYYY-MM-DDTHH:MM:SSZ</c>, kept as text because that is what was signed.</summary>
    public string ReleasedAt { get; init; } = string.Empty;

    /// <summary>A paragraph shown in the launcher, not a changelog file.</summary>
    public string Notes { get; init; } = string.Empty;

    /// <summary>
    /// Parses the exact bytes that arrived. Returns false with a reason rather than throwing:
    /// a document this launcher cannot read is a check that did not happen, never a start-up
    /// failure.
    /// </summary>
    public static bool TryParse(
        ReadOnlySpan<byte> utf8,
        [NotNullWhen(true)] out ReleaseDocument? document,
        out string problem)
    {
        document = null;

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(utf8.ToArray());
        }
        catch (JsonException exception)
        {
            problem = exception.Message;
            return false;
        }

        using (parsed)
        {
            JsonElement root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                problem = "the release document is not a JSON object";
                return false;
            }

            if (!Number(root, "schema", out long schema) || schema != KnownSchema)
            {
                problem = string.Create(
                    CultureInfo.InvariantCulture, $"schema must be {KnownSchema}");
                return false;
            }

            if (!Text(root, "channel", out string channel) ||
                channel is not (ReleaseTargets.StableChannel or ReleaseTargets.BetaChannel))
            {
                problem = "channel must be 'stable' or 'beta'";
                return false;
            }

            if (!Text(root, "version", out string versionText) ||
                !ReleaseVersion.TryParse(versionText, out ReleaseVersion version))
            {
                problem = "version must be written as major.minor.patch";
                return false;
            }

            if (!Text(root, "platform", out string platform) ||
                platform is not ("windows" or "linux" or "macos"))
            {
                problem = "platform must be 'windows', 'linux' or 'macos'";
                return false;
            }

            if (!Text(root, "arch", out string arch) || arch is not ("x64" or "arm64"))
            {
                problem = "arch must be 'x64' or 'arm64'";
                return false;
            }

            if (!Text(root, "sha256", out string sha256) || !IsSha256Hex(sha256))
            {
                problem = "sha256 must be 64 lowercase hexadecimal characters";
                return false;
            }

            if (!Number(root, "size", out long size) || size <= 0 || size > MaxArtifactBytes)
            {
                problem = string.Create(
                    CultureInfo.InvariantCulture, $"size must be between 1 and {MaxArtifactBytes}");
                return false;
            }

            if (!Text(root, "releasedAt", out string releasedAt) || !IsUtcInstant(releasedAt))
            {
                problem = "releasedAt must be spelled YYYY-MM-DDTHH:MM:SSZ";
                return false;
            }

            // Absent notes are legitimate — a release does not have to say anything — but a
            // present one that is not a string means this is not the document it claims to be.
            string notes = string.Empty;
            if (root.TryGetProperty("notes", out JsonElement notesElement))
            {
                if (notesElement.ValueKind != JsonValueKind.String)
                {
                    problem = "notes must be a string";
                    return false;
                }

                notes = notesElement.GetString() ?? string.Empty;
                if (notes.Length > MaxNotesLength)
                {
                    problem = "notes are longer than this launcher will display";
                    return false;
                }
            }

            document = new ReleaseDocument
            {
                Channel = channel,
                Version = version,
                Platform = platform,
                Arch = arch,
                Sha256 = sha256,
                Size = size,
                ReleasedAt = releasedAt,
                Notes = notes,
            };

            problem = string.Empty;
            return true;
        }
    }

    /// <summary>
    /// Whether this document describes the launcher that is asking. The route is asked for one
    /// channel, platform and architecture, but what makes the answer trustworthy is that the
    /// <i>signed</i> document says the same three things — otherwise a server holding real
    /// signed releases could hand a Windows launcher the Linux one, which is precisely what
    /// signing the document instead of the artifact exists to prevent.
    /// </summary>
    public bool Describes(string channel, string platform, string arch) =>
        string.Equals(Channel, channel, StringComparison.Ordinal)
        && string.Equals(Platform, platform, StringComparison.Ordinal)
        && string.Equals(Arch, arch, StringComparison.Ordinal);

    private static bool Text(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out JsonElement element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return true;
    }

    private static bool Number(JsonElement root, string name, out long value)
    {
        value = 0;
        return root.TryGetProperty(name, out JsonElement element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out value);
    }

    private static bool IsSha256Hex(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (char character in value)
        {
            // Lowercase only: uppercase would be a second content address for one file.
            if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// One spelling, exactly. A signature covers bytes, so an instant that can be written
    /// several ways is several documents.
    /// </summary>
    private static bool IsUtcInstant(string value) =>
        DateTimeOffset.TryParseExact(
            value,
            "yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out _);
}
