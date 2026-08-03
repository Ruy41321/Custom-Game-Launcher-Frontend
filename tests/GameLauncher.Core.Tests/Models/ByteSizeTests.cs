using System.Globalization;
using GameLauncher.Core.Models;

namespace GameLauncher.Core.Tests.Models;

public sealed class ByteSizeTests
{
    // Powers of 1024, because a user comparing against free disk space compares against what
    // their file manager shows.
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(999, "999 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1_048_576, "1 MB")]
    [InlineData(5_368_709_120, "5 GB")]
    public void SizesAreFormattedTheWayAFileManagerWould(long bytes, string expected)
    {
        // Pinned to the invariant culture: the separator deliberately follows the user's,
        // and this test is about the unit and the rounding, not about where the comma goes.
        Assert.Equal(expected, ByteSize.Format(bytes, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ARateIsASizeWithAUnitOfTimeOnIt()
    {
        Assert.Equal("3.5 MB/s", ByteSize.FormatRate(3_670_016, CultureInfo.InvariantCulture));
    }
}
