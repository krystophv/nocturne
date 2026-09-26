namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A row decomposed from a legacy treatment, carrying the fingerprint of the upstream document a
/// connector last wrote it from. Storage only: no domain model or DTO carries it, so it never
/// reaches the wire. Written through <see cref="UpstreamFingerprintScope"/>.
/// </summary>
public interface IUpstreamFingerprinted : IV4Entity
{
    string? UpstreamFingerprint { get; set; }
}
