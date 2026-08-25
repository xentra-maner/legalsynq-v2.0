using CareConnect.Application.DTOs;

namespace CareConnect.Application.Interfaces;

public interface IReferralAttributionAccessCodeService
{
    Task<List<ReferralAttributionAccessCodeResponse>> ListAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>The single active code for this origination, or null if none exists.</summary>
    Task<ReferralAttributionAccessCodeResponse?> GetActiveByAttributionAsync(Guid tenantId, Guid referralAttributionId, CancellationToken ct = default);

    Task<GeneratedReferralAttributionAccessCodeResponse> GenerateAsync(
        Guid tenantId, Guid? actorUserId, string? actorName, CreateReferralAttributionAccessCodeRequest request, CancellationToken ct = default);

    Task<ReferralAttributionAccessCodeResponse> SetActiveAsync(
        Guid tenantId, Guid id, Guid? actorUserId, string? actorName, bool isActive, CancellationToken ct = default);

    /// <summary>
    /// Anonymous, stateless code check for the Representative Portal. Nothing is mutated —
    /// callers must call this again on every subsequent data request rather than caching
    /// an "authorized" result server-side.
    /// </summary>
    Task<VerifyReferralAttributionAccessCodeResponse> VerifyAsync(
        Guid tenantId, string code, CancellationToken ct = default);
}
