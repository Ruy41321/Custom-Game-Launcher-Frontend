using System.Globalization;

namespace GameLauncher.Core.Models;

/// <summary>
/// Sizes as a person reads them. Shared rather than reimplemented per screen: a download that
/// says 4.7 GB on one page and 4,700 MB on another looks like two different downloads.
/// </summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>
    /// In the powers of 1024 a file manager uses, because a user comparing a download against
    /// their free disk space is comparing against that number.
    /// </summary>
    public static string Format(long bytes, IFormatProvider? culture = null)
    {
        double value = bytes;
        int unit = 0;

        while (Math.Abs(value) >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // The decimal separator follows the user's culture — a size is a number they read.
        return string.Create(culture ?? CultureInfo.CurrentCulture, $"{value:0.#} {Units[unit]}");
    }

    /// <summary>A transfer rate, which is a size with a unit of time attached.</summary>
    public static string FormatRate(double bytesPerSecond, IFormatProvider? culture = null) =>
        Format((long)Math.Round(bytesPerSecond), culture) + "/s";
}
