using BuildingBlocks.Domain;
using Liens.Domain.Enums;

namespace Liens.Domain.Entities;

public class LienOffer : AuditableEntity
{
    public Guid Id            { get; private set; }
    public Guid TenantId      { get; private set; }
    public Guid LienId        { get; private set; }

    public Guid BuyerOrgId    { get; private set; }
    public Guid? BuyerCompanyId { get; private set; }
    public Guid SellerOrgId   { get; private set; }

    public decimal OfferAmount { get; private set; }
    public string Status       { get; private set; } = OfferStatus.Pending;

    public string? Notes             { get; private set; }
    public string? ResponseNotes     { get; private set; }
    public string? ExternalReference { get; private set; }
    public Guid? SubmittedByPlatformUserId { get; private set; }

    public DateTime OfferedAtUtc      { get; private set; }
    public DateTime? ExpiresAtUtc     { get; private set; }
    public DateTime? RespondedAtUtc   { get; private set; }
    public DateTime? WithdrawnAtUtc   { get; private set; }

    private LienOffer() { }

    public void LinkCanonicalBuyer(Guid? companyId)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Canonical buyer company id cannot be empty.", nameof(companyId));
        BuyerCompanyId = companyId;
    }

    public void ReassignCanonicalBuyerCompany(
        Guid sourceCompanyId, Guid targetCompanyId, Guid updatedByUserId)
    {
        if (sourceCompanyId == Guid.Empty) throw new ArgumentException("Source company id is required.", nameof(sourceCompanyId));
        if (targetCompanyId == Guid.Empty) throw new ArgumentException("Target company id is required.", nameof(targetCompanyId));
        if (sourceCompanyId == targetCompanyId) throw new ArgumentException("Source and target company ids must differ.", nameof(targetCompanyId));
        if (updatedByUserId == Guid.Empty) throw new ArgumentException("UpdatedByUserId is required.", nameof(updatedByUserId));
        if (BuyerCompanyId != sourceCompanyId) return;
        BuyerCompanyId = targetCompanyId;
        UpdatedByUserId = updatedByUserId;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public static LienOffer Create(
        Guid tenantId,
        Guid lienId,
        Guid buyerOrgId,
        Guid sellerOrgId,
        decimal offerAmount,
        Guid createdByUserId,
        string? notes = null,
        string? externalReference = null,
        DateTime? expiresAtUtc = null,
        Guid? submittedByPlatformUserId = null)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("TenantId is required.", nameof(tenantId));
        if (lienId == Guid.Empty) throw new ArgumentException("LienId is required.", nameof(lienId));
        if (buyerOrgId == Guid.Empty) throw new ArgumentException("BuyerOrgId is required.", nameof(buyerOrgId));
        if (sellerOrgId == Guid.Empty) throw new ArgumentException("SellerOrgId is required.", nameof(sellerOrgId));
        if (createdByUserId == Guid.Empty) throw new ArgumentException("CreatedByUserId is required.", nameof(createdByUserId));

        if (offerAmount <= 0)
            throw new ArgumentOutOfRangeException(nameof(offerAmount), "Offer amount must be positive.");

        if (expiresAtUtc.HasValue && expiresAtUtc.Value <= DateTime.UtcNow)
            throw new ArgumentException("Expiration must be in the future.", nameof(expiresAtUtc));

        var now = DateTime.UtcNow;
        return new LienOffer
        {
            Id                = Guid.CreateVersion7(),
            TenantId          = tenantId,
            LienId            = lienId,
            BuyerOrgId        = buyerOrgId,
            SellerOrgId       = sellerOrgId,
            OfferAmount       = offerAmount,
            Status            = OfferStatus.Pending,
            Notes             = notes?.Trim(),
            ExternalReference = externalReference?.Trim(),
            SubmittedByPlatformUserId = submittedByPlatformUserId,
            OfferedAtUtc      = now,
            ExpiresAtUtc      = expiresAtUtc,
            CreatedByUserId   = createdByUserId,
            UpdatedByUserId   = createdByUserId,
            CreatedAtUtc      = now,
            UpdatedAtUtc      = now,
        };
    }

    public void UpdatePending(
        decimal offerAmount,
        Guid updatedByUserId,
        string? notes = null,
        DateTime? expiresAtUtc = null)
    {
        EnsurePendingAndNotExpired();

        if (offerAmount <= 0)
            throw new ArgumentOutOfRangeException(nameof(offerAmount), "Offer amount must be positive.");

        if (expiresAtUtc.HasValue && expiresAtUtc.Value <= DateTime.UtcNow)
            throw new ArgumentException("Expiration must be in the future.", nameof(expiresAtUtc));

        OfferAmount     = offerAmount;
        Notes           = notes?.Trim();
        ExpiresAtUtc    = expiresAtUtc;
        UpdatedByUserId = updatedByUserId;
        UpdatedAtUtc    = DateTime.UtcNow;
    }

    public void Accept(Guid respondedByUserId, string? responseNotes = null)
    {
        EnsurePendingAndNotExpired();

        Status          = OfferStatus.Accepted;
        ResponseNotes   = responseNotes?.Trim();
        RespondedAtUtc  = DateTime.UtcNow;
        UpdatedByUserId = respondedByUserId;
        UpdatedAtUtc    = DateTime.UtcNow;
    }

    public void Reject(Guid respondedByUserId, string? responseNotes = null)
    {
        EnsurePendingAndNotExpired();

        Status          = OfferStatus.Rejected;
        ResponseNotes   = responseNotes?.Trim();
        RespondedAtUtc  = DateTime.UtcNow;
        UpdatedByUserId = respondedByUserId;
        UpdatedAtUtc    = DateTime.UtcNow;
    }

    public void Withdraw(Guid withdrawnByUserId)
    {
        EnsurePendingAndNotExpired();

        Status          = OfferStatus.Withdrawn;
        WithdrawnAtUtc  = DateTime.UtcNow;
        UpdatedByUserId = withdrawnByUserId;
        UpdatedAtUtc    = DateTime.UtcNow;
    }

    public void Expire(Guid? expiredByUserId = null)
    {
        if (Status != OfferStatus.Pending)
            throw new InvalidOperationException($"Only pending offers can be expired. Current status: '{Status}'.");

        Status          = OfferStatus.Expired;
        UpdatedByUserId = expiredByUserId;
        UpdatedAtUtc    = DateTime.UtcNow;
    }

    public bool IsExpired => Status == OfferStatus.Expired
        || (Status == OfferStatus.Pending && ExpiresAtUtc.HasValue && ExpiresAtUtc.Value <= DateTime.UtcNow);

    private void EnsurePendingAndNotExpired()
    {
        if (Status != OfferStatus.Pending)
            throw new InvalidOperationException($"Only pending offers can be acted on. Current status: '{Status}'.");

        if (ExpiresAtUtc.HasValue && ExpiresAtUtc.Value <= DateTime.UtcNow)
            throw new InvalidOperationException("This offer has expired and cannot be acted on.");
    }
}
