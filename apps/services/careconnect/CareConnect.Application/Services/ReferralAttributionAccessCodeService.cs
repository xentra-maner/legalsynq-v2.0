using System.Security.Cryptography;
using System.Text;
using BuildingBlocks.Exceptions;
using CareConnect.Application.DTOs;
using CareConnect.Application.Interfaces;
using CareConnect.Application.Repositories;
using CareConnect.Domain;
using LegalSynq.AuditClient;
using LegalSynq.AuditClient.DTOs;
using LegalSynq.AuditClient.Enums;
using AuditVisibility = LegalSynq.AuditClient.Enums.VisibilityScope;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace CareConnect.Application.Services;

public class ReferralAttributionAccessCodeService : IReferralAttributionAccessCodeService
{
    // Crockford-style alphabet: excludes 0/O and 1/I so a hand-typed code can't be
    // misread. 32 symbols is a power of two, so a byte % 32 draw has no modulo bias.
    private const string CodeAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const int CodeLength = 10;

    private readonly IReferralAttributionAccessCodeRepository _accessCodes;
    private readonly IReferralAttributionRepository _attributions;
    private readonly IConfiguration _configuration;
    private readonly IAuditEventClient _auditClient;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ReferralAttributionAccessCodeService(
        IReferralAttributionAccessCodeRepository accessCodes,
        IReferralAttributionRepository attributions,
        IConfiguration configuration,
        IAuditEventClient auditClient,
        IHttpContextAccessor httpContextAccessor)
    {
        _accessCodes = accessCodes;
        _attributions = attributions;
        _configuration = configuration;
        _auditClient = auditClient;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<List<ReferralAttributionAccessCodeResponse>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        var records = await _accessCodes.ListByTenantAsync(tenantId, ct);
        return records.Select(r => ToResponse(r)).ToList();
    }

    public async Task<ReferralAttributionAccessCodeResponse?> GetActiveByAttributionAsync(
        Guid tenantId, Guid referralAttributionId, CancellationToken ct = default)
    {
        var record = await _accessCodes.GetActiveByAttributionAsync(tenantId, referralAttributionId, ct);
        return record is null ? null : ToResponse(record);
    }

    public async Task<GeneratedReferralAttributionAccessCodeResponse> GenerateAsync(
        Guid tenantId, Guid? actorUserId, string? actorName, CreateReferralAttributionAccessCodeRequest request, CancellationToken ct = default)
    {
        // Server-side tenant re-verification — the client supplies an attribution ID, but it
        // is never trusted at face value: it must belong to the same tenant as the code being
        // generated. This is what keeps cross-tenant code generation impossible even if a
        // malicious/buggy client sends an attribution ID from a different tenant.
        var attribution = await _attributions.GetByIdAsync(tenantId, request.ReferralAttributionId, ct)
            ?? throw new ValidationException("One or more validation errors occurred.",
                new() { ["referralAttributionId"] = ["Referral Origination was not found for this tenant."] });

        // Exactly one active code per origination — generating a replacement requires
        // revoking the existing one first (see SetActiveAsync). Prevents multiple
        // simultaneously-valid codes for the same source.
        if (await _accessCodes.CountActiveAsync(tenantId, attribution.Id, ct) > 0)
            throw new ConflictException(
                $"Origination '{attribution.Id}' already has an active access code.", "ACTIVE_CODE_EXISTS");

        string rawCode;
        string hash;
        var attempt = 0;
        do
        {
            if (++attempt > 5)
                throw new InvalidOperationException("Failed to generate a unique access code after several attempts.");

            rawCode = GenerateRawCode();
            hash = HashCode(rawCode);
        }
        while (await _accessCodes.GetByHashAsync(tenantId, hash, ct) is not null);

        var accessCode = ReferralAttributionAccessCode.Create(
            tenantId, attribution.Id, hash, request.AccessStartAtUtc, request.AccessEndAtUtc, actorUserId);

        await _accessCodes.AddAsync(accessCode, ct);

        EmitAudit("careconnect.referral_attribution_access_code.generated", "ReferralAttributionAccessCodeGenerated",
            tenantId, accessCode, actorUserId, actorName,
            $"Access code generated for origination '{attribution.FullName}'.",
            previousValue: null, newValue: ToAuditSnapshot(accessCode));

        var response = ToResponse(accessCode, attribution.FullName);
        return new GeneratedReferralAttributionAccessCodeResponse
        {
            Id = response.Id,
            TenantId = response.TenantId,
            ReferralAttributionId = response.ReferralAttributionId,
            ReferralAttributionFullName = response.ReferralAttributionFullName,
            IsActive = response.IsActive,
            AccessStartAtUtc = response.AccessStartAtUtc,
            AccessEndAtUtc = response.AccessEndAtUtc,
            CreatedAtUtc = response.CreatedAtUtc,
            UpdatedAtUtc = response.UpdatedAtUtc,
            Code = FormatForDisplay(rawCode),
        };
    }

    public async Task<ReferralAttributionAccessCodeResponse> SetActiveAsync(
        Guid tenantId, Guid id, Guid? actorUserId, string? actorName, bool isActive, CancellationToken ct = default)
    {
        var record = await RequireAsync(tenantId, id, ct);
        var previous = ToAuditSnapshot(record);
        record.SetActive(isActive, actorUserId);
        await _accessCodes.UpdateAsync(record, ct);

        EmitAudit(
            isActive ? "careconnect.referral_attribution_access_code.activated" : "careconnect.referral_attribution_access_code.deactivated",
            isActive ? "ReferralAttributionAccessCodeActivated" : "ReferralAttributionAccessCodeDeactivated",
            tenantId, record, actorUserId, actorName,
            $"Access code '{record.Id}' {(isActive ? "activated" : "deactivated")}.",
            previousValue: previous, newValue: ToAuditSnapshot(record));

        return ToResponse(record);
    }

    /// <summary>
    /// Anonymous, stateless verification for the Representative Portal — no login, nothing
    /// is mutated or recorded as "redeemed". Every representative-facing data request must
    /// call this again (see PublicRepresentativeEndpoints); a code that was valid a minute
    /// ago is re-checked from scratch, so revocation and origination deactivation take
    /// effect immediately on the very next request. Deliberately generic on failure — never
    /// distinguishes "wrong code" from "inactive" from "expired" from "origination
    /// deactivated" in what's returned, so a guessed code gets no signal about which of
    /// those states it's in.
    /// </summary>
    public async Task<VerifyReferralAttributionAccessCodeResponse> VerifyAsync(
        Guid tenantId, string code, CancellationToken ct = default)
    {
        var hash = HashCode(NormalizeInputCode(code));
        var record = await _accessCodes.GetByHashAsync(tenantId, hash, ct);
        if (record is null)
            return new VerifyReferralAttributionAccessCodeResponse { Ok = false };

        var attribution = await _attributions.GetByIdAsync(tenantId, record.ReferralAttributionId, ct);
        if (attribution is null || !record.IsValidAt(DateTime.UtcNow, attribution.IsActive))
            return new VerifyReferralAttributionAccessCodeResponse { Ok = false };

        return new VerifyReferralAttributionAccessCodeResponse
        {
            Ok = true,
            ReferralAttributionId = attribution.Id,
            ReferralAttributionFullName = attribution.FullName,
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<ReferralAttributionAccessCode> RequireAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        var record = await _accessCodes.GetByIdAsync(tenantId, id, ct);
        if (record is null)
            throw new NotFoundException($"Referral Origination access code '{id}' was not found.");
        return record;
    }

    private static string GenerateRawCode()
    {
        Span<byte> bytes = stackalloc byte[CodeLength];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[CodeLength];
        for (var i = 0; i < CodeLength; i++)
            chars[i] = CodeAlphabet[bytes[i] % CodeAlphabet.Length];
        return new string(chars);
    }

    private static string FormatForDisplay(string rawCode) =>
        $"{rawCode[..4]}-{rawCode[4..8]}-{rawCode[8..]}";

    private static string NormalizeInputCode(string code) =>
        new((code ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private string HashCode(string normalizedRawCode)
    {
        var pepper = _configuration["ReferralAttributionAccessCode:Pepper"] ?? string.Empty;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRawCode + pepper));
        return Convert.ToHexString(bytes);
    }

    private static ReferralAttributionAccessCodeResponse ToResponse(ReferralAttributionAccessCode a, string? attributionFullName = null) => new()
    {
        Id = a.Id,
        TenantId = a.TenantId,
        ReferralAttributionId = a.ReferralAttributionId,
        ReferralAttributionFullName = attributionFullName ?? a.ReferralAttribution?.FullName,
        IsActive = a.IsActive,
        AccessStartAtUtc = a.AccessStartAtUtc,
        AccessEndAtUtc = a.AccessEndAtUtc,
        CreatedAtUtc = a.CreatedAtUtc,
        UpdatedAtUtc = a.UpdatedAtUtc,
    };

    private static object ToAuditSnapshot(ReferralAttributionAccessCode a) => new
    {
        a.Id,
        a.ReferralAttributionId,
        a.IsActive,
        a.AccessStartAtUtc,
        a.AccessEndAtUtc,
    };

    private void EmitAudit(
        string eventType, string action, Guid tenantId, ReferralAttributionAccessCode accessCode,
        Guid? actorUserId, string? actorName, string description,
        object? previousValue, object? newValue)
    {
        var now = DateTimeOffset.UtcNow;
        _ = _auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType = eventType,
            EventCategory = EventCategory.Business,
            SourceSystem = "care-connect",
            SourceService = "referral-attribution-access-code-api",
            Visibility = AuditVisibility.Tenant,
            Severity = SeverityLevel.Info,
            OccurredAtUtc = now,
            Scope = new AuditEventScopeDto { ScopeType = ScopeType.Tenant, TenantId = tenantId.ToString() },
            Actor = new AuditEventActorDto
            {
                Id = actorUserId?.ToString(),
                Type = actorUserId.HasValue ? ActorType.User : ActorType.System,
                Name = actorName ?? actorUserId?.ToString() ?? "(system)",
            },
            // Never includes the plaintext or hashed code — the audit trail records that an
            // event happened to this record, not any value that could reconstruct or verify it.
            Entity = new AuditEventEntityDto { Type = "ReferralAttributionAccessCode", Id = accessCode.Id.ToString() },
            Action = action,
            Description = description,
            Outcome = "success",
            Metadata = JsonSerializer.Serialize(new { referralAttributionId = accessCode.ReferralAttributionId, previousValue, newValue }),
            CorrelationId = _httpContextAccessor.HttpContext?.Items["CorrelationId"]?.ToString(),
            RequestId = _httpContextAccessor.HttpContext?.TraceIdentifier,
            IdempotencyKey = IdempotencyKey.ForWithTimestamp(now, "care-connect", eventType, accessCode.Id.ToString()),
            Tags = ["referral-representative", "access-code"],
        });
    }
}
