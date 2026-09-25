using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Connectors.Core.Interfaces;

public interface ITreatmentPublisher
{
    Task<bool> PublishTreatmentsAsync(
        IEnumerable<Treatment> treatments,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    Task<bool> PublishBolusesAsync(
        IEnumerable<Bolus> records,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    Task<bool> PublishCarbIntakesAsync(
        IEnumerable<CarbIntake> records,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    Task<bool> PublishBGChecksAsync(
        IEnumerable<BGCheck> records,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    Task<bool> PublishBolusCalculationsAsync(
        IEnumerable<BolusCalculation> records,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    Task<bool> PublishTempBasalsAsync(
        IEnumerable<TempBasal> records,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    Task<bool> PublishBasalInjectionsAsync(
        IEnumerable<BasalInjection> records,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    Task<DateTime?> GetLatestTreatmentTimestampAsync(
        string source,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The legacy ids of the treatment records <paramref name="source"/> has stored with an event
    /// time in [<paramref name="from"/>, <paramref name="to"/>].
    /// </summary>
    Task<IReadOnlySet<string>> GetStoredTreatmentIdsAsync(
        string source,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The subset of <paramref name="legacyIds"/> publishing again would find stored, or withheld
    /// because the user deleted it.
    /// </summary>
    Task<IReadOnlySet<string>> GetHeldTreatmentIdsAsync(
        IReadOnlySet<string> legacyIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deletes <paramref name="source"/>'s treatment records under <paramref name="legacyIds"/>
    /// as a system sweep, so the source can publish them again should they reappear.
    /// </summary>
    Task<int> DeleteTreatmentsAsync(
        string source,
        IReadOnlySet<string> legacyIds,
        CancellationToken cancellationToken = default);
}
