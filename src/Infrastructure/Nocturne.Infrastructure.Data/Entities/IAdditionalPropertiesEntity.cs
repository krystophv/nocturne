namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// An entity carrying the uploader fields no column holds, as a JSON object. Implementers map it as
/// an ordinary EF column so generic queries translate the interface-member access.
/// </summary>
public interface IAdditionalPropertiesEntity
{
    string? AdditionalPropertiesJson { get; set; }
}
