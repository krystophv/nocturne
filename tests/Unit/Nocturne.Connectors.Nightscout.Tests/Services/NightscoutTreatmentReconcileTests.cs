using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Connectors.Nightscout.Tests.Services;

/// <summary>
/// The catch-up's re-read of recent treatments: it imports what the event-time cursor cannot see,
/// and deletes this connector's rows the source no longer has, but only on a positive answer.
/// </summary>
public class NightscoutTreatmentReconcileTests
{
    private const string Source = "nightscout-connector";
    private const string KeptId = "aaaaaaaaaaaaaaaaaaaaaaa1";
    private const string GoneId = "aaaaaaaaaaaaaaaaaaaaaaa2";
    private const string LateId = "aaaaaaaaaaaaaaaaaaaaaaa3";

    private static readonly DateTime Now = DateTime.UtcNow;

    [Fact]
    public async Task A_treatment_the_source_deleted_is_deleted_here()
    {
        var harness = new Harness
        {
            Window = Json(Carb(KeptId, Now.AddHours(-1))),
            Stored = [KeptId, GoneId],
            Held = [KeptId],
        };

        var result = await harness.SyncAsync();

        result.Success.Should().BeTrue();
        harness.Deleted.Should().ContainSingle().Which.Should().BeEquivalentTo([GoneId]);
        harness.LookedUp.Should().Equal(GoneId);
    }

    [Fact]
    public async Task A_failed_window_read_deletes_nothing()
    {
        var harness = new Harness
        {
            Window = Failure(),
            Stored = [KeptId, GoneId],
        };

        var result = await harness.SyncAsync();

        result.Success.Should().BeFalse();
        harness.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_row_the_id_lookup_still_finds_is_kept()
    {
        // Missing from the window read, as a record at a page boundary or with a created_at outside
        // the window would be, but the source still has it.
        var harness = new Harness
        {
            Window = Json(Carb(KeptId, Now.AddHours(-1))),
            Stored = [KeptId, GoneId],
            Held = [KeptId],
            Lookups = { [GoneId] = Json(Carb(GoneId, Now.AddDays(-3))) },
        };

        await harness.SyncAsync();

        harness.LookedUp.Should().Equal(GoneId);
        harness.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_id_lookup_deletes_nothing()
    {
        var harness = new Harness
        {
            Window = Json(Carb(KeptId, Now.AddHours(-1))),
            Stored = [KeptId, GoneId],
            Held = [KeptId],
            Lookups = { [GoneId] = Failure() },
        };

        var result = await harness.SyncAsync();

        result.Success.Should().BeFalse();
        harness.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_window_deletes_nothing()
    {
        var harness = new Harness
        {
            Window = Json(),
            Stored = [KeptId, GoneId],
        };

        var result = await harness.SyncAsync();

        result.Success.Should().BeTrue();
        harness.Deleted.Should().BeEmpty();
        harness.LookedUp.Should().BeEmpty();
    }

    [Fact]
    public async Task A_source_missing_most_of_the_window_deletes_nothing()
    {
        var missing = Enumerable.Range(0, 100)
            .Select(i => $"bbbbbbbbbbbbbbbbbbbb{i:0000}")
            .ToList();
        var harness = new Harness
        {
            Window = Json(Carb(KeptId, Now.AddHours(-1))),
            Stored = [KeptId, .. missing],
            Held = [KeptId],
        };

        var result = await harness.SyncAsync();

        result.Success.Should().BeTrue();
        harness.Deleted.Should().BeEmpty();
        harness.LookedUp.Should().BeEmpty();
    }

    [Fact]
    public async Task A_treatment_uploaded_after_a_newer_one_is_imported()
    {
        // Its event time sits well before the newest stored treatment, below the crawl's resume
        // point, as an edit re-uploaded under its original time or a back-dated entry does.
        var harness = new Harness
        {
            LatestStored = Now.AddMinutes(-2),
            Window = Json(Carb(KeptId, Now.AddMinutes(-2)), Carb(LateId, Now.AddMinutes(-40))),
            Stored = [KeptId],
            Held = [KeptId],
        };

        var result = await harness.SyncAsync();

        result.Success.Should().BeTrue();
        harness.Published.Select(t => t.Id).Should().Equal(LateId);
        harness.Published.Single().DataSource.Should().Be(Source);
        harness.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task Only_this_connectors_rows_are_asked_about_or_deleted()
    {
        var harness = new Harness
        {
            Window = Json(Carb(KeptId, Now.AddHours(-1))),
            Stored = [KeptId, GoneId],
            Held = [KeptId],
        };

        await harness.SyncAsync();

        harness.StoredAskedOf.Should().Equal(Source);
        harness.DeletedFrom.Should().Equal(Source);
    }

    [Fact]
    public async Task A_ranged_re_import_does_not_reconcile()
    {
        var harness = new Harness
        {
            Window = Json(Carb(KeptId, Now.AddHours(-1))),
            Stored = [KeptId, GoneId],
        };

        await harness.SyncAsync(new SyncRequest
        {
            From = Now.AddDays(-2),
            To = Now.AddDays(-1),
            DataTypes = [SyncDataType.CarbIntake],
        });

        harness.TreatmentReads.Should().Be(1);
        harness.Deleted.Should().BeEmpty();
    }

    private static string Carb(string id, DateTime at) =>
        $$"""{"_id":"{{id}}","eventType":"Carb Correction","carbs":20,"created_at":"{{at:o}}"}""";

    private static Func<HttpResponseMessage> Json(params string[] treatments) =>
        () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"[{string.Join(',', treatments)}]", Encoding.UTF8, "application/json"),
        };

    private static Func<HttpResponseMessage> Failure() =>
        () => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("") };

    private sealed class Harness : HttpMessageHandler
    {
        public DateTime LatestStored { get; init; } = Now.AddMinutes(-10);
        public Func<HttpResponseMessage> Window { get; init; } = Json();
        public HashSet<string> Stored { get; init; } = [];
        public HashSet<string> Held { get; init; } = [];
        public Dictionary<string, Func<HttpResponseMessage>> Lookups { get; } = [];

        public List<Treatment> Published { get; } = [];
        public List<IReadOnlySet<string>> Deleted { get; } = [];
        public List<string> DeletedFrom { get; } = [];
        public List<string> StoredAskedOf { get; } = [];
        public List<string> LookedUp { get; } = [];
        public int TreatmentReads { get; private set; }

        public Task<SyncResult> SyncAsync(SyncRequest? request = null)
        {
            var config = new NightscoutConnectorConfiguration { Url = "https://ns.example", ApiSecret = "secret" };
            return NewService().SyncDataAsync(
                request ?? new SyncRequest { From = Now.AddMinutes(-5), To = null, DataTypes = [SyncDataType.CarbIntake] },
                config,
                CancellationToken.None);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            if (!url.Contains("/api/v1/treatments.json", StringComparison.Ordinal))
                return Task.FromResult(Json()());

            const string idFilter = "&find[_id]=";
            var at = url.IndexOf(idFilter, StringComparison.Ordinal);
            if (at >= 0)
            {
                var id = url[(at + idFilter.Length)..];
                LookedUp.Add(id);
                return Task.FromResult(Lookups.TryGetValue(id, out var lookup) ? lookup() : Json()());
            }

            // The first read is the catch-up crawl, which here finds nothing new; the next is the
            // window re-read.
            return Task.FromResult(TreatmentReads++ == 0 ? Json()() : Window());
        }

        private NightscoutConnectorService NewService()
        {
            var treatments = new Mock<ITreatmentPublisher>();
            treatments
                .Setup(p => p.GetLatestTreatmentTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(LatestStored);
            treatments
                .Setup(p => p.PublishTreatmentsAsync(
                    It.IsAny<IEnumerable<Treatment>>(), It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<Treatment>, string, WriteOrigin, CancellationToken>((batch, _, _, _) => Published.AddRange(batch))
                .ReturnsAsync(true);
            treatments
                .Setup(p => p.GetHeldTreatmentIdsAsync(It.IsAny<IReadOnlySet<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlySet<string> ids, CancellationToken _) => ids.Where(Held.Contains).ToHashSet());
            treatments
                .Setup(p => p.GetStoredTreatmentIdsAsync(
                    It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .Callback<string, DateTime, DateTime, CancellationToken>((source, _, _, _) => StoredAskedOf.Add(source))
                .ReturnsAsync(() => Stored);
            treatments
                .Setup(p => p.DeleteTreatmentsAsync(
                    It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), It.IsAny<CancellationToken>()))
                .Callback<string, IReadOnlySet<string>, CancellationToken>((source, ids, _) =>
                {
                    DeletedFrom.Add(source);
                    Deleted.Add(ids);
                })
                .ReturnsAsync((string _, IReadOnlySet<string> ids, CancellationToken _) => ids.Count);

            var publisher = new Mock<IConnectorPublisher>();
            publisher.Setup(p => p.IsAvailable).Returns(true);
            publisher.Setup(p => p.Glucose).Returns(Mock.Of<IGlucosePublisher>());
            publisher.Setup(p => p.Treatments).Returns(treatments.Object);
            publisher.Setup(p => p.Device).Returns(Mock.Of<IDevicePublisher>());
            publisher.Setup(p => p.Metadata).Returns(Mock.Of<IMetadataPublisher>());

            var registration = new Mock<IConnectorRegistration<NightscoutConnectorConfiguration>>();
            registration.Setup(r => r.Defaults).Returns(new NightscoutConnectorConfiguration());

            return new NightscoutConnectorService(
                new HttpClient(this),
                Mock.Of<IConnectorServerResolver<NightscoutConnectorConfiguration>>(),
                NullLogger<NightscoutConnectorService>.Instance,
                Mock.Of<IRetryDelayStrategy>(),
                Mock.Of<IRateLimitingStrategy>(),
                registration.Object,
                publisher.Object);
        }
    }
}
