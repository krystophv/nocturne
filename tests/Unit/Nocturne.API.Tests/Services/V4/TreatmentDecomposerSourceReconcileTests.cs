using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// The reads and the delete a connector reconciles its recent treatments through, over a real
/// schema: only the named source's rows are touched, and the event stays visible when another
/// source holds a copy of it.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentDecomposerSourceReconcileTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private const string Connector = "nightscout-connector";
    private const string Other = "other-connector";

    private static readonly DateTime At = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly TreatmentDecomposer _decomposer;

    public TreatmentDecomposerSourceReconcileTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);
        _context = _db.CreateContext();

        var deduplication = new DeduplicationService(
            _context, Mock.Of<IServiceScopeFactory>(), NullLogger<DeduplicationService>.Instance);

        _decomposer = new TreatmentDecomposer(
            _context,
            Mock.Of<IBolusRepository>(), Mock.Of<ITempBasalRepository>(),
            Mock.Of<ICarbIntakeRepository>(), Mock.Of<IBGCheckRepository>(), Mock.Of<INoteRepository>(),
            Mock.Of<IDeviceEventRepository>(), Mock.Of<IBolusCalculationRepository>(),
            Mock.Of<IStateSpanService>(),
            Mock.Of<ITreatmentFoodService>(),
            Mock.Of<IDeviceService>(),
            Mock.Of<IPatientDeviceStamper>(),
            Mock.Of<IProfileDecomposer>(),
            Mock.Of<IActiveProfileResolver>(),
            Mock.Of<IPatientInsulinRepository>(),
            new AuditContext { IsSystem = true },
            deduplication,
            NullLogger<TreatmentDecomposer>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Deleting_from_a_source_leaves_other_sources_and_unnamed_ids_alone()
    {
        var named = await AddCarbAsync("t-1", Connector);
        var otherSource = await AddCarbAsync("t-2", Other);
        var unnamed = await AddCarbAsync("t-3", Connector);
        var namedBolus = await AddBolusAsync("t-1", Connector);

        var deleted = await _decomposer.DeleteFromSourceAsync(Connector, new HashSet<string> { "t-1", "t-2" });

        deleted.Should().Be(2);
        (await DeletedAtAsync<CarbIntakeEntity>(named)).Should().NotBeNull();
        (await DeletedAtAsync<BolusEntity>(namedBolus)).Should().NotBeNull("a treatment's decomposed rows go together");
        (await DeletedAtAsync<CarbIntakeEntity>(otherSource)).Should().BeNull();
        (await DeletedAtAsync<CarbIntakeEntity>(unnamed)).Should().BeNull();
    }

    [Fact]
    public async Task A_sweep_delete_does_not_block_the_source_publishing_it_again()
    {
        var named = await AddCarbAsync("t-1", Connector);

        await _decomposer.DeleteFromSourceAsync(Connector, new HashSet<string> { "t-1" });

        var row = await _context.CarbIntakes.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == named);
        _context.Entry(row).Property<bool>("DeletedByUser").CurrentValue.Should().BeFalse();
        (await _decomposer.GetHeldLegacyIdsAsync(new HashSet<string> { "t-1" })).Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_a_groups_primary_hands_the_flag_to_the_other_sources_copy()
    {
        var connectorCopy = await AddCarbAsync("t-1", Connector);
        var otherCopy = await AddCarbAsync("t-9", Other, At.AddSeconds(30));
        var canonical = Guid.CreateVersion7();
        AddLink(canonical, connectorCopy, At, Connector, isPrimary: true);
        AddLink(canonical, otherCopy, At.AddSeconds(30), Other, isPrimary: false);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await _decomposer.DeleteFromSourceAsync(Connector, new HashSet<string> { "t-1" });

        var primary = await _context.LinkedRecords.AsNoTracking().SingleAsync(l => l.IsPrimary);
        primary.RecordId.Should().Be(otherCopy);
    }

    [Fact]
    public async Task Stored_ids_are_this_sources_live_rows_in_the_window()
    {
        await AddCarbAsync("in-window", Connector);
        await AddCarbAsync("before-window", Connector, At.AddHours(-3));
        await AddCarbAsync("other-source", Other);
        var gone = await AddCarbAsync("deleted", Connector);
        await SoftDeleteAsync<CarbIntakeEntity>(gone, byUser: false);
        await AddTempBasalAsync("temp-basal", Connector);

        var ids = await _decomposer.GetLegacyIdsFromSourceAsync(Connector, At.AddHours(-1), At.AddHours(1));

        ids.Should().BeEquivalentTo(["in-window", "temp-basal"]);
    }

    [Fact]
    public async Task Held_ids_are_stored_rows_of_any_source_and_user_deletions()
    {
        await AddCarbAsync("live", Other);
        var userDeleted = await AddCarbAsync("user-deleted", Connector);
        await SoftDeleteAsync<CarbIntakeEntity>(userDeleted, byUser: true);
        var swept = await AddCarbAsync("swept", Connector);
        await SoftDeleteAsync<CarbIntakeEntity>(swept, byUser: false);
        _context.StateSpans.Add(new StateSpanEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, Category = nameof(StateSpanCategory.Override),
            State = "Custom", StartTimestamp = At, OriginalId = "override",
        });
        await _context.SaveChangesAsync();

        var held = await _decomposer.GetHeldLegacyIdsAsync(
            new HashSet<string> { "live", "user-deleted", "swept", "override", "new" });

        held.Should().BeEquivalentTo(["live", "user-deleted", "override"]);
    }

    private async Task<Guid> AddCarbAsync(string legacyId, string source, DateTime? at = null)
    {
        var id = Guid.CreateVersion7();
        _context.CarbIntakes.Add(new CarbIntakeEntity
        {
            Id = id, TenantId = TenantId, LegacyId = legacyId, DataSource = source,
            Carbs = 20, Timestamp = at ?? At,
        });
        await _context.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> AddBolusAsync(string legacyId, string source)
    {
        var id = Guid.CreateVersion7();
        _context.Boluses.Add(new BolusEntity
        {
            Id = id, TenantId = TenantId, LegacyId = legacyId, DataSource = source,
            Insulin = 1, Timestamp = At,
        });
        await _context.SaveChangesAsync();
        return id;
    }

    private async Task AddTempBasalAsync(string legacyId, string source)
    {
        _context.TempBasals.Add(new TempBasalEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, LegacyId = legacyId, DataSource = source,
            Rate = 1, StartTimestamp = At, Origin = "Algorithm",
        });
        await _context.SaveChangesAsync();
    }

    private void AddLink(Guid canonical, Guid recordId, DateTime at, string source, bool isPrimary) =>
        _context.LinkedRecords.Add(new LinkedRecordEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, CanonicalId = canonical,
            RecordType = "carbintake", RecordId = recordId,
            SourceTimestamp = new DateTimeOffset(at).ToUnixTimeMilliseconds(),
            DataSource = source, IsPrimary = isPrimary, SysCreatedAt = DateTime.UtcNow,
        });

    private async Task SoftDeleteAsync<T>(Guid id, bool byUser) where T : class, IV4Entity
    {
        var row = await _context.Set<T>().SingleAsync(e => e.Id == id);
        row.DeletedAt = DateTime.UtcNow;
        _context.Entry(row).Property<bool>("DeletedByUser").CurrentValue = byUser;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private Task<DateTime?> DeletedAtAsync<T>(Guid id) where T : class, IV4Entity =>
        _context.Set<T>().IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Id == id).Select(e => e.DeletedAt).SingleAsync();
}
