using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// An uploader's lowercase <c>id</c> survives decomposition and projection so Trio's
/// <c>find[id][$eq]</c> delete reaches the record, without becoming its identity.
/// See <see cref="TreatmentClientId"/>.
/// </summary>
public class TreatmentClientIdRoundTripTests : IDisposable
{
    private const string TrioCarbId = "0B6F7E4A-1C2D-4E3F-8A9B-0C1D2E3F4A5B";
    private const string TrioFpuId = "7D3C2B1A-9E8F-4A6B-8C5D-4E3F2A1B0C9D";

    private readonly NocturneDbContext _context;
    private readonly CarbIntakeRepository _carbIntakeRepo;
    private readonly BolusRepository _bolusRepo;
    private readonly TreatmentDecomposer _decomposer;
    private readonly V4ToLegacyProjectionService _projection;

    public TreatmentClientIdRoundTripTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        _context.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var dedup = new Mock<IDeduplicationService>().Object;
        var audit = new Mock<IAuditContext>().Object;
        var ctxFactory = new TestTenantDbContextFactory(_context);
        _bolusRepo = new BolusRepository(ctxFactory, dedup, audit, NullLogger<BolusRepository>.Instance);
        _carbIntakeRepo = new CarbIntakeRepository(ctxFactory, dedup, audit, NullLogger<CarbIntakeRepository>.Instance);
        var bgCheckRepo = new BGCheckRepository(ctxFactory, dedup, audit, NullLogger<BGCheckRepository>.Instance);
        var noteRepo = new NoteRepository(ctxFactory, dedup, audit, NullLogger<NoteRepository>.Instance);
        var deviceEventRepo = new DeviceEventRepository(ctxFactory, dedup, audit, NullLogger<DeviceEventRepository>.Instance);
        var bolusCalcRepo = new BolusCalculationRepository(ctxFactory, dedup, audit, NullLogger<BolusCalculationRepository>.Instance);

        var tempBasalRepo = new Mock<ITempBasalRepository>();
        tempBasalRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var foods = new Mock<ITreatmentFoodService>();
        foods
            .Setup(s => s.GetByCarbIntakeIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var devices = new Mock<IDeviceService>();
        devices
            .Setup(s => s.ResolveAsync(
                It.IsAny<V4Models.DeviceCategory>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);

        _decomposer = new TreatmentDecomposer(
            _context,
            _bolusRepo, tempBasalRepo.Object,
            _carbIntakeRepo, bgCheckRepo, noteRepo, deviceEventRepo, bolusCalcRepo,
            Mock.Of<IStateSpanService>(),
            foods.Object,
            devices.Object,
            Mock.Of<IPatientDeviceStamper>(),
            Mock.Of<IProfileDecomposer>(),
            Mock.Of<IActiveProfileResolver>(),
            Mock.Of<IPatientInsulinRepository>(),
            audit,
            NullLogger<TreatmentDecomposer>.Instance);

        _projection = new V4ToLegacyProjectionService(
            Mock.Of<ISensorGlucoseRepository>(),
            _bolusRepo, _carbIntakeRepo, bgCheckRepo, noteRepo, deviceEventRepo,
            tempBasalRepo.Object, bolusCalcRepo,
            foods.Object,
            _context,
            NullLogger<V4ToLegacyProjectionService>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Treatment Upload(string json) => JsonSerializer.Deserialize<Treatment>(json)!;

    private static string TrioCarb(string id, int carbs, string createdAt) =>
        $$"""{"id":"{{id}}","enteredBy":"Trio","eventType":"Carb Correction","carbs":{{carbs}},"fat":0,"protein":0,"created_at":"{{createdAt}}"}""";

    private async Task<List<Treatment>> ProjectedAsync() =>
        (await _projection.GetProjectedTreatmentsAsync(null, null, 1000, nativeOnly: false)).ToList();

    /// <summary>
    /// What <c>DELETE /api/v1/treatments?find[id][$eq]=…</c> resolves: the projected treatments
    /// the find query matches, each deleted by the id it projects with.
    /// </summary>
    private async Task DeleteByTrioQueryAsync(string id)
    {
        var find = FindQuery.Parse($"find[id][$eq]={id}");
        foreach (var match in (await ProjectedAsync()).Where(find.Matches))
            await _carbIntakeRepo.DeleteAsync(Guid.Parse(match.Id!), WriteOrigin.Live);
    }

    [Fact]
    public async Task TrioCarbEdit_DeleteById_ThenReupload_LeavesOnlyTheReplacement()
    {
        await _decomposer.DecomposeAsync(
            Upload(TrioCarb(TrioCarbId, 30, "2026-01-01T12:00:00.000Z")), WriteOrigin.Live);

        await DeleteByTrioQueryAsync(TrioCarbId);

        const string replacementId = "C4B3A2F1-0E9D-4C8B-9A7F-6E5D4C3B2A10";
        await _decomposer.DecomposeAsync(
            Upload(TrioCarb(replacementId, 45, "2026-01-01T12:00:00.000Z")), WriteOrigin.Live);

        var carb = (await ProjectedAsync()).Should().ContainSingle().Subject;
        carb.Carbs.Should().Be(45);
        JsonSerializer.SerializeToElement(carb).GetProperty("id").GetString().Should().Be(replacementId);
    }

    [Fact]
    public async Task TrioFatProteinEquivalents_ShareOneId_StayDistinct_AndDeleteTogether()
    {
        await _decomposer.DecomposeAsync(
            Upload(TrioCarb(TrioFpuId, 10, "2026-01-01T13:00:00.000Z")), WriteOrigin.Live);
        await _decomposer.DecomposeAsync(
            Upload(TrioCarb(TrioFpuId, 10, "2026-01-01T13:30:00.000Z")), WriteOrigin.Live);
        await _decomposer.DecomposeAsync(
            Upload(TrioCarb(TrioCarbId, 30, "2026-01-01T12:00:00.000Z")), WriteOrigin.Live);

        (await ProjectedAsync()).Should().HaveCount(3);

        await DeleteByTrioQueryAsync(TrioFpuId);

        (await ProjectedAsync()).Should().ContainSingle().Which.Carbs.Should().Be(30);
    }

    [Fact]
    public async Task ClientId_IsCarried_ButIsNotTheLegacyId()
    {
        var result = await _decomposer.DecomposeAsync(
            Upload(TrioCarb(TrioCarbId, 30, "2026-01-01T12:00:00.000Z")), WriteOrigin.Live);

        var carb = result.CreatedRecords.OfType<V4Models.CarbIntake>().Single();
        carb.LegacyId.Should().StartWith("syn-");
        carb.AdditionalProperties.Should().ContainKey(TreatmentClientId.Field);
    }

    [Fact]
    public async Task ExplicitObjectId_StillWinsOverClientId()
    {
        const string objectId = "65a1b2c3d4e5f60718293a4b";
        var result = await _decomposer.DecomposeAsync(
            Upload($$"""{"_id":"{{objectId}}","id":"{{TrioCarbId}}","eventType":"Carb Correction","carbs":20,"created_at":"2026-01-01T12:00:00.000Z"}"""),
            WriteOrigin.Live);

        result.CreatedRecords.OfType<V4Models.CarbIntake>().Single().LegacyId.Should().Be(objectId);
    }

    [Fact]
    public async Task LoopSyncIdentifier_WithoutClientId_IsUnchanged()
    {
        const string syncIdentifier = "loop-sync-0001";
        var result = await _decomposer.DecomposeAsync(
            Upload($$"""{"syncIdentifier":"{{syncIdentifier}}","enteredBy":"Loop","eventType":"Correction Bolus","insulin":1.5,"created_at":"2026-01-01T12:00:00.000Z"}"""),
            WriteOrigin.Live);

        var bolus = result.CreatedRecords.OfType<V4Models.Bolus>().Single();
        bolus.LegacyId.Should().Be(syncIdentifier);
        bolus.AdditionalProperties.Should().BeNull();

        var projected = (await ProjectedAsync()).Should().ContainSingle().Subject;
        JsonSerializer.SerializeToElement(projected).TryGetProperty("id", out _).Should().BeFalse();
    }
}
