using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Infrastructure.Data;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Tests.Services.V4;

public class ActivityDecomposerBatchTests : IDisposable
{
    private readonly NocturneDbContext _context;
    private readonly Mock<IStateSpanRepository> _stateSpanRepoMock;
    private readonly ActivityDecomposer _decomposer;

    public ActivityDecomposerBatchTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        _context.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        _stateSpanRepoMock = new Mock<IStateSpanRepository>();
        _stateSpanRepoMock
            .Setup(x => x.CreateActivitiesAsStateSpansAsync(
                It.IsAny<IEnumerable<StateSpan>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<StateSpan> spans, CancellationToken _) => spans);

        _decomposer = new ActivityDecomposer(
            _context,
            _stateSpanRepoMock.Object,
            NullLogger<ActivityDecomposer>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task DecomposeBatchAsync_RoutesHeartRateToRepo()
    {
        // Arrange
        var activities = new List<Activity>
        {
            CreateHeartRateActivity("hr1", 72),
            CreateHeartRateActivity("hr2", 85),
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync(activities, WriteOrigin.Live);

        // Assert - heart rates stored via DbContext
        _context.HeartRates.Should().HaveCount(2);
        result.CreatedRecords.Should().HaveCount(2);
        result.CorrelationId.Should().NotBeNull();

        _stateSpanRepoMock.Verify(
            x => x.CreateActivitiesAsStateSpansAsync(
                It.IsAny<IEnumerable<StateSpan>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DecomposeBatchAsync_RoutesStepCountToRepo()
    {
        // Arrange
        var activities = new List<Activity>
        {
            CreateStepCountActivity("sc1", 1500),
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync(activities, WriteOrigin.Live);

        // Assert - step counts stored via DbContext
        _context.StepCounts.Should().HaveCount(1);
        result.CreatedRecords.Should().HaveCount(1);

        _stateSpanRepoMock.Verify(
            x => x.CreateActivitiesAsStateSpansAsync(
                It.IsAny<IEnumerable<StateSpan>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DecomposeBatchAsync_XDripUploads_RoutesToSensorTablesAtTimeStamp()
    {
        const long stepsAt = 1_780_000_000_123;
        const long bpmAt = stepsAt + 60_000;
        var activities = System.Text.Json.JsonSerializer.Deserialize<List<Activity>>($$"""
            [
              {"_id":"steps1","type":"steps-total","timeStamp":{{stepsAt}},"created_at":"2026-05-28T20:26:40Z","steps":1000},
              {"_id":"hr1","type":"hr-bpm","timeStamp":{{bpmAt}},"created_at":"2026-05-28T20:27:40Z","bpm":60}
            ]
            """)!;

        await _decomposer.DecomposeBatchAsync(activities, WriteOrigin.Backfill);

        var steps = _context.StepCounts.Should().ContainSingle().Subject;
        steps.Metric.Should().Be(1000);
        steps.Timestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(stepsAt).UtcDateTime);

        _context.HeartRates.Should().ContainSingle().Which.Timestamp
            .Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(bpmAt).UtcDateTime);

        _stateSpanRepoMock.Verify(
            x => x.CreateActivitiesAsStateSpansAsync(
                It.IsAny<IEnumerable<StateSpan>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DecomposeBatchAsync_RoutesRegularActivityToStateSpans()
    {
        // Arrange
        var activities = new List<Activity>
        {
            CreateRegularActivity("ex1", "exercise"),
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync(activities, WriteOrigin.Live);

        // Assert
        _stateSpanRepoMock.Verify(
            x => x.CreateActivitiesAsStateSpansAsync(
                It.Is<IEnumerable<StateSpan>>(spans => spans.Count() == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _context.HeartRates.Should().BeEmpty();
        _context.StepCounts.Should().BeEmpty();
        result.CreatedRecords.Should().HaveCount(1);
    }

    [Fact]
    public async Task DecomposeBatchAsync_EmptyBatch_NoRepositoryCalls()
    {
        // Act
        var result = await _decomposer.DecomposeBatchAsync([], WriteOrigin.Live);

        // Assert
        _context.HeartRates.Should().BeEmpty();
        _context.StepCounts.Should().BeEmpty();
        _stateSpanRepoMock.Verify(
            x => x.CreateActivitiesAsStateSpansAsync(
                It.IsAny<IEnumerable<StateSpan>>(), It.IsAny<CancellationToken>()),
            Times.Never);

        result.CreatedRecords.Should().BeEmpty();
        result.CorrelationId.Should().BeNull();
    }

    [Fact]
    public async Task DecomposeBatchAsync_MixedTypes()
    {
        // Arrange - one of each type
        var activities = new List<Activity>
        {
            CreateHeartRateActivity("hr1", 72),
            CreateStepCountActivity("sc1", 3000),
            CreateRegularActivity("ex1", "exercise"),
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync(activities, WriteOrigin.Live);

        // Assert - correct routing
        _context.HeartRates.Should().HaveCount(1);
        _context.StepCounts.Should().HaveCount(1);

        _stateSpanRepoMock.Verify(
            x => x.CreateActivitiesAsStateSpansAsync(
                It.Is<IEnumerable<StateSpan>>(spans => spans.Count() == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);

        result.CreatedRecords.Should().HaveCount(3);

        // All records produced in one decompose share a single non-empty correlation id
        result.CorrelationId.Should().NotBeNull().And.NotBe(Guid.Empty);
    }

    #region NormalizeMills

    [Fact]
    public void NormalizeMills_OnlyCreatedAt_UsesCreatedAt()
    {
        var activity = new Activity { Type = "exercise", CreatedAt = "2026-05-28T21:00:00Z" };

        ActivityDecomposer.NormalizeMills(activity);

        activity.Mills.Should().Be(DateTimeOffset.Parse("2026-05-28T21:00:00Z").ToUnixTimeMilliseconds());
    }

    [Fact]
    public void NormalizeMills_MillsSet_KeepsMills()
    {
        var activity = new Activity { Mills = 1_780_000_000_000, CreatedAt = "2026-05-28T21:00:00Z" };

        ActivityDecomposer.NormalizeMills(activity);

        activity.Mills.Should().Be(1_780_000_000_000);
    }

    #endregion

    #region IsStepCount

    [Theory]
    [InlineData("steps-total", true)]
    [InlineData("walk", false)]
    [InlineData(null, false)]
    public void IsStepCount_StepsKey_TrueOnlyForStepsTotalType(string? type, bool expected)
    {
        var activity = new Activity
        {
            Type = type,
            AdditionalProperties = new Dictionary<string, object> { ["steps"] = 1000 },
        };

        _decomposer.IsStepCount(activity).Should().Be(expected);
    }

    #endregion

    #region RequiredWriteScope

    [Fact]
    public void RequiredWriteScope_HeartRate_ReturnsHeartRateReadWrite()
    {
        _decomposer.RequiredWriteScope(CreateHeartRateActivity("hr", 72))
            .Should().Be(Scope.HeartRateReadWrite);
    }

    [Fact]
    public void RequiredWriteScope_StepCount_ReturnsStepCountReadWrite()
    {
        _decomposer.RequiredWriteScope(CreateStepCountActivity("sc", 1500))
            .Should().Be(Scope.StepCountReadWrite);
    }

    [Theory]
    [InlineData("sleep")]
    [InlineData("nap")]
    [InlineData("Sleep")]
    public void RequiredWriteScope_SleepType_ReturnsSleepReadWrite(string type)
    {
        _decomposer.RequiredWriteScope(CreateRegularActivity("s", type))
            .Should().Be(Scope.SleepReadWrite);
    }

    [Theory]
    [InlineData("exercise")]
    [InlineData("running")]
    [InlineData("illness")]
    [InlineData("travel")]
    [InlineData("restaurant")] // contains "rest" but is not an exact sleep type
    public void RequiredWriteScope_RegularActivity_ReturnsNull(string type)
    {
        _decomposer.RequiredWriteScope(CreateRegularActivity("r", type))
            .Should().BeNull();
    }

    #endregion

    #region RequiredReadScope

    [Fact]
    public void RequiredReadScope_HeartRate_ReturnsHeartRateRead()
    {
        _decomposer.RequiredReadScope(CreateHeartRateActivity("hr", 72))
            .Should().Be(Scope.HeartRateRead);
    }

    [Fact]
    public void RequiredReadScope_StepCount_ReturnsStepCountRead()
    {
        _decomposer.RequiredReadScope(CreateStepCountActivity("sc", 1500))
            .Should().Be(Scope.StepCountRead);
    }

    [Theory]
    [InlineData("sleep")]
    [InlineData("nap")]
    [InlineData("Sleep")]
    public void RequiredReadScope_SleepType_ReturnsSleepRead(string type)
    {
        _decomposer.RequiredReadScope(CreateRegularActivity("s", type))
            .Should().Be(Scope.SleepRead);
    }

    /// <summary>
    /// Regular activities route to StateSpans, which the merged read serves under treatments. Unlike
    /// the write scope this is never null: every record in the merged response needs a category to
    /// be filtered on, so "no category" would mean "visible to anyone admitted".
    /// </summary>
    [Theory]
    [InlineData("exercise")]
    [InlineData("running")]
    [InlineData("illness")]
    [InlineData("travel")]
    [InlineData("restaurant")]
    public void RequiredReadScope_RegularActivity_ReturnsTreatmentsRead(string type)
    {
        _decomposer.RequiredReadScope(CreateRegularActivity("r", type))
            .Should().Be(Scope.TreatmentsRead);
    }

    /// <summary>
    /// The read scope must be the read counterpart of the write scope for the same record, so the
    /// read gate and the write gate cannot classify a record into different categories.
    /// </summary>
    [Fact]
    public void RequiredReadScope_IsTheReadCounterpartOfRequiredWriteScope()
    {
        foreach (var activity in new[]
                 {
                     CreateHeartRateActivity("hr", 72),
                     CreateStepCountActivity("sc", 1500),
                     CreateRegularActivity("s", "sleep"),
                 })
        {
            var writeScope = _decomposer.RequiredWriteScope(activity);
            _decomposer.RequiredReadScope(activity)
                .Should().Be(Scope.ImpliedReadScope(writeScope!));
        }
    }

    #endregion

    #region Helpers

    private static Activity CreateHeartRateActivity(string id, int bpm)
    {
        return new Activity
        {
            Id = id,
            Mills = 1700000000000,
            EnteredBy = "test",
            AdditionalProperties = new Dictionary<string, object>
            {
                ["bpm"] = bpm,
                ["accuracy"] = 1,
            },
        };
    }

    private static Activity CreateStepCountActivity(string id, int metric)
    {
        return new Activity
        {
            Id = id,
            Mills = 1700000000000,
            EnteredBy = "test",
            AdditionalProperties = new Dictionary<string, object>
            {
                ["metric"] = metric,
                ["source"] = 1,
            },
        };
    }

    private static Activity CreateRegularActivity(string id, string type)
    {
        return new Activity
        {
            Id = id,
            Mills = 1700000000000,
            Type = type,
            EnteredBy = "test",
        };
    }

    #endregion
}
