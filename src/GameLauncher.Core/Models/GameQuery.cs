namespace GameLauncher.Core.Models;

/// <summary>
/// Sort orders Explore accepts. An unknown value falls back to the default server-side rather
/// than failing, so this enum can grow without breaking against an older server.
/// </summary>
public enum GameSort
{
    ReleaseDate,
    Title,
    Recent,
}

/// <summary>What Explore is being asked for. Every field maps to one query parameter.</summary>
public sealed record GameQuery
{
    /// <summary>Server-side page size ceiling; asking for more is clamped, not rejected.</summary>
    public const int MaxPageSize = 100;

    public const int DefaultPageSize = 20;

    /// <summary>Case-insensitive substring of the title. Null or blank lists everything.</summary>
    public string? Search { get; init; }

    public GameSort Sort { get; init; } = GameSort.ReleaseDate;

    /// <summary>1-based, because that is what the UI shows.</summary>
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = DefaultPageSize;

    /// <summary>
    /// The query string this asks for, without the leading <c>?</c>. Defaults are omitted so a
    /// plain listing is a bare URL, which keeps logs and cache keys readable.
    /// </summary>
    public string ToQueryString()
    {
        List<string> parameters = [];

        if (!string.IsNullOrWhiteSpace(Search))
        {
            parameters.Add("search=" + Uri.EscapeDataString(Search.Trim()));
        }

        if (Sort != GameSort.ReleaseDate)
        {
            parameters.Add("sort=" + (Sort == GameSort.Title ? "title" : "recent"));
        }

        if (Page > 1)
        {
            parameters.Add("page=" + Math.Max(1, Page).ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        }

        if (PageSize != DefaultPageSize)
        {
            parameters.Add("pageSize=" + Math.Clamp(PageSize, 1, MaxPageSize).ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        }

        return string.Join('&', parameters);
    }
}
