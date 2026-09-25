namespace Nocturne.Core.Models;

/// <summary>
/// Carries an uploader's own lowercase <c>id</c> (Trio's CoreData UUID) between a
/// <see cref="Treatment"/> and the V4 records it decomposes into, so the projected treatment
/// still answers the <c>DELETE /api/v1/treatments?find[id][$eq]=…</c> Trio edits and deletes with.
/// </summary>
/// <remarks>
/// Legacy Nightscout keeps <c>id</c> as a plain, non-unique field, and so must we: it is never an
/// identity (<see cref="Treatment.Id"/>). Trio stamps every carb equivalent of one fat/protein entry
/// with the same <c>id</c>, so keying the upsert on it would collapse them into one record.
/// </remarks>
public static class TreatmentClientId
{
    public const string Field = "id";

    public static Dictionary<string, object?>? ToRecord(Treatment treatment) =>
        treatment.AdditionalProperties?.TryGetValue(Field, out var id) == true
            ? new() { [Field] = id }
            : null;

    public static Dictionary<string, object>? ToTreatment(IReadOnlyDictionary<string, object?>? record) =>
        record?.TryGetValue(Field, out var id) == true && id is not null
            ? new() { [Field] = id }
            : null;
}
