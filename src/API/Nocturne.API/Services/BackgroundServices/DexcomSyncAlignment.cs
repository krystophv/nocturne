namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// When to next ask Dexcom Share for data, given the newest reading already stored. A Dexcom sensor
/// reports every five minutes and the reading reaches Share some time after that, so a poll on a
/// fixed timer lands at an arbitrary phase of that cadence and can trail each reading by almost a
/// full interval. Aligning to the reading instead (as nightscout-connect's <c>align_to_glucose</c>
/// does) asks just after the next one should exist, and retries briefly when it is late.
/// </summary>
internal static class DexcomSyncAlignment
{
    /// <summary>How often the sensor produces a reading.</summary>
    public static readonly TimeSpan ReadingCadence = TimeSpan.FromMinutes(5);

    /// <summary>Allowance for the reading to reach Share after its sensor timestamp.</summary>
    public static readonly TimeSpan PublishBuffer = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Upper bound of the random spread added to an on-time poll, so many tenants aligned to
    /// readings taken at the same moment do not reach Dexcom in the same second.
    /// </summary>
    public static readonly TimeSpan MaxJitter = TimeSpan.FromSeconds(15);

    /// <summary>How soon to ask again while the expected reading is late.</summary>
    public static readonly TimeSpan LateRetryInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long past its due time a reading is still treated as merely delayed in Share. Beyond
    /// this the gap is a real one (signal loss, warm-up, sensor change) and polling returns to the
    /// five-minute grid rather than retrying every <see cref="LateRetryInterval"/>.
    /// </summary>
    public static readonly TimeSpan LateRetryWindow = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Past this age the newest reading says nothing useful about when the next one will arrive
    /// (the sensor is off or expired), and the connector's own interval is left in charge.
    /// </summary>
    public static readonly TimeSpan MaxAlignableAge = TimeSpan.FromHours(1);

    /// <summary>
    /// The time to next poll, or <c>null</c> when <paramref name="latestReading"/> is too old to
    /// align to.
    /// </summary>
    /// <param name="latestReading">UTC sensor timestamp of the newest stored reading.</param>
    /// <param name="now">The current UTC time.</param>
    /// <param name="jitterFraction">A value in [0, 1] choosing where in <see cref="MaxJitter"/> to land.</param>
    public static DateTime? NextSyncAt(DateTime latestReading, DateTime now, double jitterFraction)
    {
        var age = now - latestReading;
        if (age > MaxAlignableAge)
            return null;

        var jitter = MaxJitter * Math.Clamp(jitterFraction, 0d, 1d);
        var expected = latestReading + ReadingCadence;

        // The next reading is not due yet: ask just after it should have reached Share.
        if (now < expected + PublishBuffer)
            return expected + PublishBuffer + jitter;

        // Due but not in Share yet, which is usually the phone being slow to upload. Keep asking.
        if (now - expected < LateRetryWindow)
            return now + LateRetryInterval;

        // A real gap: go back to the reading grid, one slot at a time, instead of hammering.
        var slotsBehind = Math.Ceiling(age / ReadingCadence);
        var nextSlot = latestReading + ReadingCadence * slotsBehind;
        if (nextSlot + PublishBuffer <= now)
            nextSlot += ReadingCadence;

        return nextSlot + PublishBuffer + jitter;
    }
}
