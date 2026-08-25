using CareConnect.Application.DTOs;

namespace CareConnect.Application.Interfaces;

public interface IReferralAttributionService
{
    Task<List<ReferralAttributionResponse>> ListAsync(Guid tenantId, bool? activeOnly, CancellationToken ct = default);
    Task<ReferralAttributionResponse> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct = default);
    Task<ReferralAttributionResponse> CreateAsync(Guid tenantId, Guid? actorUserId, string? actorName, CreateReferralAttributionRequest request, CancellationToken ct = default);
    Task<ReferralAttributionResponse> UpdateAsync(Guid tenantId, Guid id, Guid? actorUserId, string? actorName, UpdateReferralAttributionRequest request, CancellationToken ct = default);
    Task<ReferralAttributionResponse> SetActiveAsync(Guid tenantId, Guid id, Guid? actorUserId, string? actorName, bool isActive, CancellationToken ct = default);

    /// <summary>
    /// Idempotent seed used for the initial Cam-Perry-style origination (or any future
    /// pre-configured origination). Check-then-create on (TenantId, Code) — safe to call
    /// on every startup.
    /// </summary>
    Task SeedAsync(Guid tenantId, CreateReferralAttributionRequest request, CancellationToken ct = default);
}
