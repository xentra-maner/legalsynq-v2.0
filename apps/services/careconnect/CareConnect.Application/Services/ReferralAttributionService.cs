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
using System.Text.Json;

namespace CareConnect.Application.Services;

public class ReferralAttributionService : IReferralAttributionService
{
    private readonly IReferralAttributionRepository _attributions;
    private readonly IReferralAttributionAccessCodeRepository _accessCodes;
    private readonly IAuditEventClient _auditClient;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ReferralAttributionService(
        IReferralAttributionRepository attributions,
        IReferralAttributionAccessCodeRepository accessCodes,
        IAuditEventClient auditClient,
        IHttpContextAccessor httpContextAccessor)
    {
        _attributions = attributions;
        _accessCodes = accessCodes;
        _auditClient = auditClient;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<List<ReferralAttributionResponse>> ListAsync(Guid tenantId, bool? activeOnly, CancellationToken ct = default)
    {
        var records = await _attributions.ListByTenantAsync(tenantId, activeOnly, ct);
        var responses = new List<ReferralAttributionResponse>(records.Count);
        foreach (var record in records)
            responses.Add(await ToResponseAsync(record, ct));
        return responses;
    }

    public async Task<ReferralAttributionResponse> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        var record = await RequireAsync(tenantId, id, ct);
        return await ToResponseAsync(record, ct);
    }

    public async Task<ReferralAttributionResponse> CreateAsync(
        Guid tenantId, Guid? actorUserId, string? actorName, CreateReferralAttributionRequest request, CancellationToken ct = default)
    {
        ValidateNameAndCode(request.FirstName, request.LastName, request.Code);

        var normalizedCode = ReferralAttribution.NormalizeCode(request.Code);
        var existing = await _attributions.GetByCodeAsync(tenantId, normalizedCode, ct);
        if (existing is not null)
            throw new ValidationException("One or more validation errors occurred.",
                new() { ["code"] = [$"An origination with code '{normalizedCode}' already exists for this tenant."] });

        var attribution = ReferralAttribution.Create(
            tenantId, request.FirstName, request.LastName, normalizedCode, request.Description,
            request.IsActive, request.DisplayOrder, actorUserId);

        await _attributions.AddAsync(attribution, ct);

        EmitAudit("careconnect.referral_attribution.created", "ReferralAttributionCreated",
            attribution.TenantId, attribution.Id, actorUserId, actorName,
            $"Referral Origination '{attribution.FullName}' ({attribution.Code}) created.",
            previousValue: null, newValue: ToAuditSnapshot(attribution));

        return await ToResponseAsync(attribution, ct);
    }

    public async Task<ReferralAttributionResponse> UpdateAsync(
        Guid tenantId, Guid id, Guid? actorUserId, string? actorName, UpdateReferralAttributionRequest request, CancellationToken ct = default)
    {
        var record = await RequireAsync(tenantId, id, ct);
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.FirstName))
            errors["firstName"] = ["FirstName is required."];
        if (string.IsNullOrWhiteSpace(request.LastName))
            errors["lastName"] = ["LastName is required."];
        if (errors.Count > 0)
            throw new ValidationException("One or more validation errors occurred.", errors);

        var previous = ToAuditSnapshot(record);
        record.Update(request.FirstName, request.LastName, request.Description, request.DisplayOrder, actorUserId);
        await _attributions.UpdateAsync(record, ct);

        EmitAudit("careconnect.referral_attribution.edited", "ReferralAttributionEdited",
            record.TenantId, record.Id, actorUserId, actorName,
            $"Referral Origination '{record.FullName}' ({record.Code}) edited.",
            previousValue: previous, newValue: ToAuditSnapshot(record));

        return await ToResponseAsync(record, ct);
    }

    public async Task<ReferralAttributionResponse> SetActiveAsync(
        Guid tenantId, Guid id, Guid? actorUserId, string? actorName, bool isActive, CancellationToken ct = default)
    {
        var record = await RequireAsync(tenantId, id, ct);
        var previous = ToAuditSnapshot(record);
        record.SetActive(isActive, actorUserId);
        await _attributions.UpdateAsync(record, ct);

        EmitAudit(
            isActive ? "careconnect.referral_attribution.activated" : "careconnect.referral_attribution.deactivated",
            isActive ? "ReferralAttributionActivated" : "ReferralAttributionDeactivated",
            record.TenantId, record.Id, actorUserId, actorName,
            $"Referral Origination '{record.FullName}' ({record.Code}) {(isActive ? "activated" : "deactivated")}.",
            previousValue: previous, newValue: ToAuditSnapshot(record));

        return await ToResponseAsync(record, ct);
    }

    public async Task SeedAsync(Guid tenantId, CreateReferralAttributionRequest request, CancellationToken ct = default)
    {
        var normalizedCode = ReferralAttribution.NormalizeCode(request.Code);
        var existing = await _attributions.GetByCodeAsync(tenantId, normalizedCode, ct);
        if (existing is not null)
            return; // idempotent — already seeded

        var attribution = ReferralAttribution.Create(
            tenantId, request.FirstName, request.LastName, normalizedCode, request.Description,
            request.IsActive, request.DisplayOrder, createdByUserId: null);

        await _attributions.AddAsync(attribution, ct);

        EmitAudit("careconnect.referral_attribution.created", "ReferralAttributionSeeded",
            attribution.TenantId, attribution.Id, actorUserId: null, actorName: "(seed)",
            $"Referral Origination '{attribution.FullName}' ({attribution.Code}) seeded.",
            previousValue: null, newValue: ToAuditSnapshot(attribution));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<ReferralAttribution> RequireAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        var record = await _attributions.GetByIdAsync(tenantId, id, ct);
        if (record is null)
            throw new NotFoundException($"Referral Origination '{id}' was not found.");
        return record;
    }

    private async Task<ReferralAttributionResponse> ToResponseAsync(ReferralAttribution a, CancellationToken ct)
    {
        var isUsed = await _attributions.IsUsedByAnyReferralAsync(a.TenantId, a.Id, ct);
        var activeAccessCodes = await _accessCodes.CountActiveAsync(a.TenantId, a.Id, ct);
        return new ReferralAttributionResponse
        {
            Id = a.Id,
            TenantId = a.TenantId,
            FirstName = a.FirstName,
            LastName = a.LastName,
            Code = a.Code,
            Description = a.Description,
            IsActive = a.IsActive,
            DisplayOrder = a.DisplayOrder,
            IsUsed = isUsed,
            ActiveAccessCodeCount = activeAccessCodes,
            CreatedAtUtc = a.CreatedAtUtc,
            UpdatedAtUtc = a.UpdatedAtUtc,
        };
    }

    private static void ValidateNameAndCode(string firstName, string lastName, string code)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(firstName))
            errors["firstName"] = ["FirstName is required."];
        if (string.IsNullOrWhiteSpace(lastName))
            errors["lastName"] = ["LastName is required."];
        if (string.IsNullOrWhiteSpace(code))
            errors["code"] = ["Code is required."];
        if (errors.Count > 0)
            throw new ValidationException("One or more validation errors occurred.", errors);
    }

    private static object ToAuditSnapshot(ReferralAttribution a) => new
    {
        a.Id,
        a.FirstName,
        a.LastName,
        a.Code,
        a.Description,
        a.IsActive,
        a.DisplayOrder,
    };

    private void EmitAudit(
        string eventType, string action, Guid tenantId, Guid attributionId,
        Guid? actorUserId, string? actorName, string description,
        object? previousValue, object? newValue)
    {
        var now = DateTimeOffset.UtcNow;
        // Fire-and-observe, consistent with every other audit call in this service —
        // never gates the configuration change itself.
        _ = _auditClient.IngestAsync(new IngestAuditEventRequest
        {
            EventType = eventType,
            EventCategory = EventCategory.Business,
            SourceSystem = "care-connect",
            SourceService = "referral-attribution-api",
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
            Entity = new AuditEventEntityDto { Type = "ReferralAttribution", Id = attributionId.ToString() },
            Action = action,
            Description = description,
            Outcome = "success",
            Metadata = JsonSerializer.Serialize(new { previousValue, newValue }),
            CorrelationId = _httpContextAccessor.HttpContext?.Items["CorrelationId"]?.ToString(),
            RequestId = _httpContextAccessor.HttpContext?.TraceIdentifier,
            IdempotencyKey = IdempotencyKey.ForWithTimestamp(now, "care-connect", eventType, attributionId.ToString()),
            Tags = ["referral-attribution", "configuration"],
        });
    }
}
