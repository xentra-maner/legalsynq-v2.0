using BuildingBlocks.Exceptions;
using Liens.Application.DTOs;
using Liens.Application.Interfaces;
using Liens.Application.Repositories;
using Liens.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Liens.Application.Services;

public sealed class LienSaleService : ILienSaleService
{
    private readonly ILienRepository _lienRepo;
    private readonly ILienOfferRepository _offerRepo;
    private readonly IBillOfSaleRepository _bosRepo;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IBillOfSaleDocumentService _docService;
    private readonly IAuditPublisher _audit;
    private readonly INotificationPublisher _notifications;
    private readonly ISellingNotificationOutbox _notificationOutbox;
    private readonly ILogger<LienSaleService> _logger;

    public LienSaleService(
        ILienRepository lienRepo,
        ILienOfferRepository offerRepo,
        IBillOfSaleRepository bosRepo,
        IUnitOfWork unitOfWork,
        IBillOfSaleDocumentService docService,
        IAuditPublisher audit,
        INotificationPublisher notifications,
        ISellingNotificationOutbox notificationOutbox,
        ILogger<LienSaleService> logger)
    {
        _lienRepo   = lienRepo;
        _offerRepo  = offerRepo;
        _bosRepo    = bosRepo;
        _unitOfWork = unitOfWork;
        _docService = docService;
        _audit      = audit;
        _notifications = notifications;
        _notificationOutbox = notificationOutbox;
        _logger     = logger;
    }

    public async Task<SaleFinalizationResult> AcceptOfferAsync(
        Guid tenantId,
        Guid lienOfferId,
        Guid actingUserId,
        CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string[]>();
        if (tenantId == Guid.Empty)
            errors.Add("tenantId", ["TenantId is required."]);
        if (lienOfferId == Guid.Empty)
            errors.Add("lienOfferId", ["LienOfferId is required."]);
        if (actingUserId == Guid.Empty)
            errors.Add("actingUserId", ["ActingUserId is required."]);
        if (errors.Count > 0)
            throw new ValidationException("One or more input parameters are invalid.", errors);

        _logger.LogInformation(
            "SaleFinalization: starting for Offer={OfferId} Tenant={TenantId} Actor={ActorId}",
            lienOfferId, tenantId, actingUserId);

        var offer = await _offerRepo.GetByIdAsync(tenantId, lienOfferId, ct)
            ?? throw new NotFoundException($"Offer '{lienOfferId}' not found for tenant '{tenantId}'.");

        if (offer.TenantId != tenantId)
            throw new NotFoundException($"Offer '{lienOfferId}' not found for tenant '{tenantId}'.");

        if (offer.Status == OfferStatus.Accepted)
        {
            var existingBos = await _bosRepo.GetByLienOfferIdAsync(tenantId, lienOfferId, ct);
            if (existingBos != null)
            {
                _logger.LogInformation(
                    "SaleFinalization: idempotent return — Offer={OfferId} already accepted, BOS={BosId}",
                    lienOfferId, existingBos.Id);

                var lienForIdempotent = await _lienRepo.GetByIdAsync(tenantId, offer.LienId, ct);
                return new SaleFinalizationResult
                {
                    AcceptedOfferId      = offer.Id,
                    AcceptedOfferStatus  = offer.Status,
                    LienId               = offer.LienId,
                    FinalLienStatus      = lienForIdempotent?.Status ?? LienStatus.Sold,
                    BillOfSaleId         = existingBos.Id,
                    BillOfSaleNumber     = existingBos.BillOfSaleNumber,
                    BillOfSaleStatus     = existingBos.Status,
                    PurchaseAmount       = existingBos.PurchaseAmount,
                    OriginalLienAmount   = existingBos.OriginalLienAmount,
                    DiscountPercent      = existingBos.DiscountPercent,
                    DocumentId           = existingBos.DocumentId,
                    CompetingOffersRejected = 0,
                    FinalizedAtUtc       = existingBos.IssuedAtUtc,
                };
            }
        }

        if (offer.Status != OfferStatus.Pending)
            throw new ConflictException(
                $"Offer '{lienOfferId}' is in status '{offer.Status}' and cannot be accepted.",
                "OFFER_NOT_ACTIONABLE");

        if (offer.IsExpired)
            throw new ConflictException(
                $"Offer '{lienOfferId}' has expired and cannot be accepted.",
                "OFFER_EXPIRED");

        var lien = await _lienRepo.GetByIdAsync(tenantId, offer.LienId, ct)
            ?? throw new NotFoundException($"Lien '{offer.LienId}' not found for tenant '{tenantId}'.");

        if (lien.TenantId != tenantId)
            throw new NotFoundException($"Lien '{offer.LienId}' not found for tenant '{tenantId}'.");

        if (lien.Status != LienStatus.Offered && lien.Status != LienStatus.Accepted && lien.Status != LienStatus.UnderReview)
            throw new ConflictException(
                $"Lien '{lien.Id}' is in status '{lien.Status}' and cannot accept an offer. " +
                "Only liens in 'Offered', 'Accepted', or 'UnderReview' status can finalize a sale.",
                "LIEN_NOT_SELLABLE");

        var existingBosForLien = await _bosRepo.GetByLienIdAsync(tenantId, lien.Id, ct);
        var activeBos = existingBosForLien.FirstOrDefault(
            b => !BillOfSaleStatus.Terminal.Contains(b.Status));
        if (activeBos != null)
            throw new ConflictException(
                $"Lien '{lien.Id}' already has an active Bill of Sale '{activeBos.Id}'.",
                "LIEN_ALREADY_HAS_BOS");

        Domain.Entities.BillOfSale bos;
        int rejectedCount = 0;
        var rejectedOfferIds = new List<(Guid OfferId, Guid BuyerOrgId)>();

        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);

        try
        {
            offer.Accept(actingUserId, "Accepted via sale finalization workflow");
            await _offerRepo.UpdateAsync(offer, ct);

            if (offer.SubmittedByPlatformUserId is { } acceptedRecipient)
            {
                _notificationOutbox.Enqueue(new NotificationInboxSendRequest(
                    tenantId,
                    acceptedRecipient,
                    BuildingBlocks.Notifications.NotificationTaxonomy.Liens.Events.OfferAccepted,
                    "lien",
                    "Offer Accepted",
                    $"Your offer for lien {lien.LienNumber} was accepted.",
                    "Synq Selling",
                    "SS",
                    offer.RespondedAtUtc ?? DateTime.UtcNow,
                    $"selling:offer:{offer.Id:N}:accepted:{acceptedRecipient:N}"));
            }

            var billOfSaleNumber = GenerateBillOfSaleNumber(lien.LienNumber);

            bos = Domain.Entities.BillOfSale.CreateFromAcceptedOffer(
                tenantId:           tenantId,
                lienId:             lien.Id,
                lienOfferId:        offer.Id,
                billOfSaleNumber:   billOfSaleNumber,
                sellerOrgId:        offer.SellerOrgId,
                buyerOrgId:         offer.BuyerOrgId,
                purchaseAmount:     offer.OfferAmount,
                originalLienAmount: lien.OriginalAmount,
                createdByUserId:    actingUserId);

            await _bosRepo.AddAsync(bos, ct);

            var competingOffers = await _offerRepo.GetByLienIdAsync(tenantId, lien.Id, ct);

            foreach (var competing in competingOffers)
            {
                if (competing.Id == offer.Id)
                    continue;

                if (competing.Status != OfferStatus.Pending)
                    continue;

                competing.Reject(actingUserId, $"Rejected: competing offer superseded by accepted offer '{offer.Id}'");
                await _offerRepo.UpdateAsync(competing, ct);
                if (competing.SubmittedByPlatformUserId is { } rejectedRecipient)
                {
                    _notificationOutbox.Enqueue(new NotificationInboxSendRequest(
                        tenantId,
                        rejectedRecipient,
                        BuildingBlocks.Notifications.NotificationTaxonomy.Liens.Events.OfferRejected,
                        "lien",
                        "Offer Declined",
                        $"Your offer for lien {lien.LienNumber} was declined.",
                        "Synq Selling",
                        "SS",
                        competing.RespondedAtUtc ?? DateTime.UtcNow,
                        $"selling:offer:{competing.Id:N}:rejected:{rejectedRecipient:N}"));
                }
                rejectedOfferIds.Add((competing.Id, competing.BuyerOrgId));
                rejectedCount++;
            }

            lien.MarkSold(offer.OfferAmount, offer.BuyerOrgId, actingUserId);
            await _lienRepo.UpdateAsync(lien, ct);

            await transaction.CommitAsync(ct);

            _logger.LogInformation(
                "SaleFinalization: committed — Offer={OfferId} Lien={LienId} BOS={BosId} " +
                "Rejected={RejectedCount} PurchaseAmount={Amount}",
                offer.Id, lien.Id, bos.Id, rejectedCount, offer.OfferAmount);

            _audit.Publish(
                eventType: "liens.sale.finalized",
                action: "finalize",
                description: $"Sale finalized: Offer '{offer.Id}' accepted, BOS '{bos.BillOfSaleNumber}' created, " +
                             $"{rejectedCount} competing offer(s) rejected, amount={offer.OfferAmount}",
                tenantId: tenantId,
                actorUserId: actingUserId,
                entityType: "BillOfSale",
                entityId: bos.Id.ToString(),
                metadata: $"{{\"lienId\":\"{lien.Id}\",\"offerId\":\"{offer.Id}\",\"rejectedOffers\":{rejectedCount}}}");

            var saleData = new Dictionary<string, string>
            {
                ["lienId"] = lien.Id.ToString(),
                ["lienNumber"] = lien.LienNumber,
                ["offerId"] = offer.Id.ToString(),
                ["billOfSaleId"] = bos.Id.ToString(),
                ["billOfSaleNumber"] = bos.BillOfSaleNumber,
                ["buyerOrgId"] = offer.BuyerOrgId.ToString(),
                ["sellerOrgId"] = offer.SellerOrgId.ToString(),
                ["purchaseAmount"] = bos.PurchaseAmount.ToString("F2"),
                ["originalLienAmount"] = bos.OriginalLienAmount.ToString("F2"),
                ["discountPercent"] = bos.DiscountPercent?.ToString("F2") ?? "0.00",
                ["userId"] = actingUserId.ToString(),
            };

            _ = _notifications.PublishAsync("lien.offer.accepted", tenantId, saleData, ct);
            _ = _notifications.PublishAsync("lien.sale.finalized", tenantId, saleData, ct);

            foreach (var (rejectedOfferId, rejectedBuyerOrgId) in rejectedOfferIds)
            {
                _ = _notifications.PublishAsync("lien.offer.rejected", tenantId, new Dictionary<string, string>
                {
                    ["offerId"] = rejectedOfferId.ToString(),
                    ["lienId"] = lien.Id.ToString(),
                    ["lienNumber"] = lien.LienNumber,
                    ["buyerOrgId"] = rejectedBuyerOrgId.ToString(),
                    ["sellerOrgId"] = offer.SellerOrgId.ToString(),
                    ["acceptedOfferId"] = offer.Id.ToString(),
                    ["userId"] = actingUserId.ToString(),
                }, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "SaleFinalization: rolling back — Offer={OfferId} Lien={LienId} Error={Error}",
                offer.Id, lien.Id, ex.Message);

            await transaction.RollbackAsync(ct);
            throw;
        }

        try
        {
            var documentId = await _docService.GenerateAndStoreAsync(bos, actingUserId, ct);
            if (documentId.HasValue)
            {
                bos.AttachDocument(documentId.Value, actingUserId);
                await _bosRepo.UpdateAsync(bos, ct);

                _logger.LogInformation(
                    "SaleFinalization: document attached — BOS={BosId} DocumentId={DocumentId}",
                    bos.Id, documentId.Value);

                _ = _notifications.PublishAsync("lien.sale.document.generated", tenantId, new Dictionary<string, string>
                {
                    ["billOfSaleId"] = bos.Id.ToString(),
                    ["billOfSaleNumber"] = bos.BillOfSaleNumber,
                    ["documentId"] = documentId.Value.ToString(),
                    ["lienId"] = lien.Id.ToString(),
                    ["lienNumber"] = lien.LienNumber,
                    ["buyerOrgId"] = offer.BuyerOrgId.ToString(),
                    ["sellerOrgId"] = offer.SellerOrgId.ToString(),
                    ["userId"] = actingUserId.ToString(),
                }, ct);
            }
            else
            {
                _logger.LogWarning(
                    "SaleFinalization: document generation/storage failed (recoverable) — BOS={BosId} Tenant={TenantId}",
                    bos.Id, tenantId);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "SaleFinalization: post-commit document step failed (recoverable) — BOS={BosId} Tenant={TenantId}",
                bos.Id, tenantId);
        }

        return new SaleFinalizationResult
        {
            AcceptedOfferId      = offer.Id,
            AcceptedOfferStatus  = offer.Status,
            LienId               = lien.Id,
            FinalLienStatus      = lien.Status,
            BillOfSaleId         = bos.Id,
            BillOfSaleNumber     = bos.BillOfSaleNumber,
            BillOfSaleStatus     = bos.Status,
            PurchaseAmount       = bos.PurchaseAmount,
            OriginalLienAmount   = bos.OriginalLienAmount,
            DiscountPercent      = bos.DiscountPercent,
            DocumentId           = bos.DocumentId,
            CompetingOffersRejected = rejectedCount,
            FinalizedAtUtc       = bos.IssuedAtUtc,
        };
    }

    private static string GenerateBillOfSaleNumber(string lienNumber)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        return $"BOS-{lienNumber}-{timestamp}";
    }
}
