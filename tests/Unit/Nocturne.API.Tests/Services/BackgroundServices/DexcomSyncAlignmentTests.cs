using FluentAssertions;
using Nocturne.API.Services.BackgroundServices;
using Xunit;

namespace Nocturne.API.Tests.Services.BackgroundServices;

public class DexcomSyncAlignmentTests
{
    private static readonly DateTime Reading = new(2026, 9, 25, 17, 4, 29, DateTimeKind.Utc);

    [Fact]
    public void BeforeTheNextReadingIsDue_PollsJustAfterItShouldReachShare()
    {
        var next = DexcomSyncAlignment.NextSyncAt(Reading, Reading.AddMinutes(2), jitterFraction: 0);

        next.Should().Be(Reading + DexcomSyncAlignment.ReadingCadence + DexcomSyncAlignment.PublishBuffer);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void Jitter_StaysInsideItsBound(double fraction)
    {
        var onTime = Reading + DexcomSyncAlignment.ReadingCadence + DexcomSyncAlignment.PublishBuffer;

        var next = DexcomSyncAlignment.NextSyncAt(Reading, Reading.AddMinutes(1), fraction);

        next.Should().BeOnOrAfter(onTime).And.BeOnOrBefore(onTime + DexcomSyncAlignment.MaxJitter);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(2.0)]
    public void Jitter_OutsideZeroToOne_IsClamped(double fraction)
    {
        var onTime = Reading + DexcomSyncAlignment.ReadingCadence + DexcomSyncAlignment.PublishBuffer;

        var next = DexcomSyncAlignment.NextSyncAt(Reading, Reading.AddMinutes(1), fraction);

        next.Should().BeOnOrAfter(onTime).And.BeOnOrBefore(onTime + DexcomSyncAlignment.MaxJitter);
    }

    /// <summary>
    /// The reading is due but Share does not have it yet, usually because the phone has not uploaded
    /// it. That is when a fixed timer loses a whole interval, so ask again soon.
    /// </summary>
    [Fact]
    public void WhenTheExpectedReadingIsLate_RetriesShortly()
    {
        var now = Reading + DexcomSyncAlignment.ReadingCadence + TimeSpan.FromSeconds(45);

        var next = DexcomSyncAlignment.NextSyncAt(Reading, now, jitterFraction: 0);

        next.Should().Be(now + DexcomSyncAlignment.LateRetryInterval);
    }

    /// <summary>
    /// Past the retry window the gap is real (signal loss, warm-up), so polling returns to the
    /// reading grid instead of asking every thirty seconds until the sensor comes back.
    /// </summary>
    [Fact]
    public void PastTheRetryWindow_ReturnsToTheReadingGrid()
    {
        var now = Reading + TimeSpan.FromMinutes(12);

        var next = DexcomSyncAlignment.NextSyncAt(Reading, now, jitterFraction: 0);

        next.Should().Be(Reading + TimeSpan.FromMinutes(15) + DexcomSyncAlignment.PublishBuffer);
    }

    [Fact]
    public void OnAGridSlotBoundary_NeverSchedulesInThePast()
    {
        var now = Reading + TimeSpan.FromMinutes(20) + DexcomSyncAlignment.PublishBuffer;

        var next = DexcomSyncAlignment.NextSyncAt(Reading, now, jitterFraction: 0);

        next.Should().BeAfter(now);
    }

    [Fact]
    public void AReadingOlderThanTheAlignableAge_LeavesTheIntervalInCharge()
    {
        var now = Reading + DexcomSyncAlignment.MaxAlignableAge + TimeSpan.FromMinutes(1);

        DexcomSyncAlignment.NextSyncAt(Reading, now, jitterFraction: 0).Should().BeNull();
    }

    /// <summary>
    /// Every answer is in the future, across a sweep of the cadence, the retry window and a long gap,
    /// so the poller can never be pointed at a time that has already passed.
    /// </summary>
    [Fact]
    public void EveryAnswer_IsInTheFuture()
    {
        for (var seconds = 0; seconds <= 3600; seconds += 7)
        {
            var now = Reading.AddSeconds(seconds);
            var next = DexcomSyncAlignment.NextSyncAt(Reading, now, jitterFraction: 0);

            if (next is { } at)
                at.Should().BeAfter(now, "at {0}s after the reading", seconds);
        }
    }
}
