using System.Security.Cryptography;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;
using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Generates and verifies single-use recovery codes for break-glass account access.
/// Each code consists of two 5-character segments from a reduced unambiguous alphabet,
/// separated by a hyphen. Codes are stored as per-code salted PBKDF2-HMAC-SHA256 hashes,
/// so they stay valid when any instance secret is rotated.
/// </summary>
/// <seealso cref="IRecoveryCodeService"/>
public class RecoveryCodeService : IRecoveryCodeService
{
    private const int CodeCount = 8;
    private const int SegmentLength = 5;
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 100_000;
    private const string HashAlgorithm = "pbkdf2-sha256";

    // A code is checked against every slot so an attempt costs the same whether the subject
    // exists and holds codes or not; the padding salt is what the empty slots derive against.
    private static readonly byte[] DummySalt = RandomNumberGenerator.GetBytes(SaltSize);

    private readonly NocturneDbContext _dbContext;

    /// <summary>
    /// Initialises a new <see cref="RecoveryCodeService"/>.
    /// </summary>
    /// <param name="dbContext">Database context for reading and writing recovery code records.</param>
    public RecoveryCodeService(NocturneDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<List<string>> GenerateCodesAsync(Guid subjectId)
    {
        var existing = await _dbContext.RecoveryCodes
            .Where(r => r.SubjectId == subjectId)
            .ToListAsync();

        if (existing.Count > 0)
        {
            _dbContext.RecoveryCodes.RemoveRange(existing);
        }

        var codes = new List<string>(CodeCount);

        for (var i = 0; i < CodeCount; i++)
        {
            var code = GenerateCode();
            codes.Add(code);

            _dbContext.RecoveryCodes.Add(new RecoveryCodeEntity
            {
                Id = Guid.CreateVersion7(),
                SubjectId = subjectId,
                CodeHash = Hash(NormalizeCode(code)),
                CreatedAt = DateTime.UtcNow,
            });
        }

        await _dbContext.SaveChangesAsync();

        return codes;
    }

    /// <inheritdoc />
    public async Task<bool> VerifyAndConsumeAsync(Guid? subjectId, string code)
    {
        var live = subjectId is null
            ? []
            : await _dbContext.RecoveryCodes
                .Where(r => r.SubjectId == subjectId && r.UsedAt == null && r.InvalidatedAt == null)
                .ToListAsync();

        var normalized = NormalizeCode(code);

        Guid? matchedId = null;

        for (var i = 0; i < CodeCount; i++)
        {
            var candidate = i < live.Count ? TryParseHash(live[i].CodeHash) : null;
            var salt = candidate?.Salt ?? DummySalt;
            var iterations = candidate?.Iterations ?? Iterations;

            var derived = KeyDerivation.Pbkdf2(
                password: normalized,
                salt: salt,
                prf: KeyDerivationPrf.HMACSHA256,
                iterationCount: iterations,
                numBytesRequested: HashSize
            );

            if (candidate is not null && CryptographicOperations.FixedTimeEquals(derived, candidate.Hash))
            {
                matchedId = live[i].Id;
            }
        }

        if (matchedId is null)
        {
            return false;
        }

        var consumed = await _dbContext.RecoveryCodes
            .Where(r => r.Id == matchedId && r.UsedAt == null && r.InvalidatedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.UsedAt, DateTime.UtcNow));

        return consumed == 1;
    }

    /// <inheritdoc />
    public async Task<int> GetRemainingCountAsync(Guid subjectId)
    {
        return await _dbContext.RecoveryCodes
            .CountAsync(r => r.SubjectId == subjectId && r.UsedAt == null && r.InvalidatedAt == null);
    }

    /// <inheritdoc />
    public async Task<bool> HasCodesAsync(Guid subjectId)
    {
        return await _dbContext.RecoveryCodes
            .AnyAsync(r => r.SubjectId == subjectId && r.InvalidatedAt == null);
    }

    /// <inheritdoc />
    public async Task<bool> WereCodesResetAsync(Guid subjectId)
    {
        var hasInvalidated = await _dbContext.RecoveryCodes
            .AnyAsync(r => r.SubjectId == subjectId && r.InvalidatedAt != null);

        if (!hasInvalidated)
        {
            return false;
        }

        return !await _dbContext.RecoveryCodes
            .AnyAsync(r => r.SubjectId == subjectId && r.UsedAt == null && r.InvalidatedAt == null);
    }

    /// <summary>
    /// Generates a single recovery code in <c>XXXXX-XXXXX</c> format using <see cref="Alphabet"/>.
    /// </summary>
    /// <returns>A formatted recovery code string.</returns>
    private static string GenerateCode()
    {
        var segment1 = GenerateSegment();
        var segment2 = GenerateSegment();
        return $"{segment1}-{segment2}";
    }

    /// <summary>
    /// Generates a single segment of <see cref="SegmentLength"/> characters from <see cref="Alphabet"/>
    /// using a cryptographically secure RNG.
    /// </summary>
    /// <returns>A random character segment string.</returns>
    private static string GenerateSegment()
    {
        var chars = new char[SegmentLength];
        for (var i = 0; i < SegmentLength; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }
        return new string(chars);
    }

    /// <summary>
    /// Normalises a code by converting to uppercase and stripping hyphens,
    /// ensuring consistent hashing regardless of user input formatting.
    /// </summary>
    /// <param name="code">The raw recovery code string entered by the user.</param>
    /// <returns>The normalised form suitable for hashing.</returns>
    private static string NormalizeCode(string code)
    {
        return code.ToUpperInvariant().Replace("-", "");
    }

    /// <summary>
    /// Hashes a normalised code with a fresh random salt, returning the self-describing
    /// <c>pbkdf2-sha256$iterations$salt$hash</c> string stored in the database.
    /// </summary>
    private static string Hash(string normalizedCode)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);

        var hash = KeyDerivation.Pbkdf2(
            password: normalizedCode,
            salt: salt,
            prf: KeyDerivationPrf.HMACSHA256,
            iterationCount: Iterations,
            numBytesRequested: HashSize
        );

        return $"{HashAlgorithm}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static ParsedHash? TryParseHash(string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != HashAlgorithm)
        {
            return null;
        }

        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0)
        {
            return null;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var hash = Convert.FromBase64String(parts[3]);
            if (salt.Length != SaltSize || hash.Length != HashSize)
            {
                return null;
            }

            return new ParsedHash(iterations, salt, hash);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed record ParsedHash(int Iterations, byte[] Salt, byte[] Hash);
}
