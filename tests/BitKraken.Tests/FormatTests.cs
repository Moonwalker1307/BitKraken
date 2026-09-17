using Xunit;

namespace BitKraken.Tests;

/// <summary>
/// Every row in the torrent list renders through <see cref="Format"/>, so these pin the unit
/// boundaries and the rounding rather than just the happy path.
/// </summary>
public class FormatTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(1023, "1023 B")]          // last value before the unit rolls over
    [InlineData(1024, "1 KB")]            // ...and the first after it
    [InlineData(1536, "1.5 KB")]
    [InlineData(1500, "1.5 KB")]          // 1.4648 -> one decimal place
    [InlineData(1048576, "1 MB")]
    [InlineData(1073741824, "1 GB")]
    [InlineData(1099511627776, "1 TB")]
    [InlineData(1125899906842624, "1 PB")]
    public void Bytes_formats_each_unit(long bytes, string expected) => Assert.Equal(expected, Format.Bytes(bytes));

    [Fact]
    public void Bytes_renders_whole_bytes_without_a_decimal_place()
    {
        // The B branch is formatted "0", every larger unit "0.#".
        Assert.Equal("512 B", Format.Bytes(512));
        Assert.DoesNotContain(".", Format.Bytes(512));
    }

    [Fact]
    public void Bytes_returns_a_dash_for_a_negative_size() => Assert.Equal("—", Format.Bytes(-1));

    [Fact]
    public void Bytes_stops_at_the_largest_known_unit()
    {
        // Must not walk off the end of the unit table.
        Assert.EndsWith(" PB", Format.Bytes(long.MaxValue));
    }

    [Theory]
    [InlineData(0, "0 B/s")]
    [InlineData(-1, "0 B/s")]             // a negative rate reads as idle, never as "—/s"
    [InlineData(1, "1 B/s")]
    [InlineData(2048, "2 KB/s")]
    public void Speed_formats_a_rate(long bytesPerSecond, string expected) =>
        Assert.Equal(expected, Format.Speed(bytesPerSecond));

    [Fact]
    public void Eta_is_infinite_when_unknown() => Assert.Equal("∞", Format.Eta(null));

    [Fact]
    public void Eta_is_infinite_beyond_thirty_days()
    {
        Assert.Equal("∞", Format.Eta(TimeSpan.FromDays(30)));
        Assert.Equal("∞", Format.Eta(TimeSpan.FromDays(365)));
        Assert.Equal("29d 12h", Format.Eta(TimeSpan.FromDays(29.5)));   // just inside the cutoff
    }

    [Theory]
    [InlineData(0.5, "0s")]               // sub-second rounds down to zero, not to ""
    [InlineData(1, "1s")]
    [InlineData(45, "45s")]
    [InlineData(59, "59s")]
    [InlineData(60, "1m 0s")]
    [InlineData(90, "1m 30s")]
    [InlineData(3599, "59m 59s")]
    [InlineData(3600, "1h 0m")]
    [InlineData(5400, "1h 30m")]
    public void Eta_formats_below_a_day(double seconds, string expected) =>
        Assert.Equal(expected, Format.Eta(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Eta_carries_hours_into_days()
    {
        // The day branch prints TotalDays with the *remainder* hours; 47h is 1d 23h, never 1d 47h.
        Assert.Equal("1d 23h", Format.Eta(TimeSpan.FromHours(47)));
        Assert.Equal("1d 0h", Format.Eta(TimeSpan.FromDays(1)));
        Assert.Equal("2d 12h", Format.Eta(TimeSpan.FromHours(60)));
    }

    [Theory]
    [InlineData(0, "0%")]
    [InlineData(50, "50%")]
    [InlineData(100, "100%")]
    [InlineData(-5, "0%")]                // clamped, so a stray negative never renders
    [InlineData(150, "100%")]
    [InlineData(33.333, "33.3%")]
    public void Percent_clamps_and_rounds(double value, string expected) =>
        Assert.Equal(expected, Format.Percent(value));

    [Theory]
    [InlineData(0, 0, "0.00")]            // nothing moved yet
    [InlineData(1, 2, "0.50")]
    [InlineData(3, 2, "1.50")]
    [InlineData(2, 2, "1.00")]
    public void Ratio_is_uploaded_over_downloaded(long uploaded, long downloaded, string expected) =>
        Assert.Equal(expected, Format.Ratio(uploaded, downloaded));

    [Fact]
    public void Ratio_is_infinite_when_something_was_seeded_without_downloading()
    {
        Assert.Equal("∞", Format.Ratio(uploaded: 1024, downloaded: 0));
    }

    [Theory]
    [InlineData(0, "0.00")]
    [InlineData(1.5, "1.50")]
    [InlineData(-1, "0.00")]              // a ratio can't be negative; don't render one that is
    [InlineData(double.PositiveInfinity, "∞")]
    [InlineData(double.NaN, "∞")]
    public void A_ratio_already_worked_out_renders_the_same_way(double ratio, string expected) =>
        Assert.Equal(expected, Format.Ratio(ratio));

    [Theory]
    [InlineData(0, "0m")]                 // "0m", not "0s": this is a stretch of time, not a countdown
    [InlineData(-30, "0m")]
    [InlineData(45, "45s")]
    [InlineData(90, "1m")]
    [InlineData(3600, "1h 0m")]
    [InlineData(5430, "1h 30m")]
    [InlineData(90000, "1d 1h")]
    public void Duration_reads_as_time_already_spent(int seconds, string expected) =>
        Assert.Equal(expected, Format.Duration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Duration_never_gives_up_the_way_an_eta_does()
    {
        // Eta calls anything past a month "∞" because it is a guess. Time already seeded is not.
        Assert.Equal("∞", Format.Eta(TimeSpan.FromDays(60)));
        Assert.Equal("60d 0h", Format.Duration(TimeSpan.FromDays(60)));
    }
}
