using BuildingBlocks.Exceptions;
using Liens.Application.DTOs;
using Liens.Application.Interfaces;
using Liens.Application.Repositories;
using Liens.Domain.Entities;
using Liens.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Liens.Application.Services;

public sealed class LienOfferService : ILienOfferService
{
    private readonly ILienOfferRepository _offerRepo;
    private readonly ILienRepository _lienRepo;
    private readonly IAuditPublisher _audit;
    private readonly INotificationPublisher _notifications;
    private readonly ISellingNotificationOutbox _notificationOutbox;
    private readonly ILogger<LienOfferService> _logger;

    private static readonly IReadOnlySet<string> OfferableStatuses = new HashSet<string>
    {
        LienStatus.Offered,
        LienStatus.UnderReview,
    };

    public LienOfferService(
        ILienOfferRepository offerRepo,
        ILienRepository lienRepo,
        IAuditPublisher audit,
        INotificationPublisher notifications,
        ISellingNotificationOutbox notificationOutbox,
        ILogger<LienOfferService> logger)
    {
        _offerRepo = offerRepo;
        _lienRepo = lienRepo;
        _audit = audit;
        _notifications = notifications;
        _notificationOutbox = notificationOutbox;
        _logger = logger;
    }

    public async Task<PaginatedResult<LienOfferResponse>> SearchAsync(
        Guid tenantId, Guid? lienId, string? status,
        Guid? buyerOrgId, Guid? sellerOrgId,
        int page, int pageSize,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var (items, totalCount) = await _offerRepo.SearchAsync(
            tenantId, lienId, status, buyerOrgId, sellerOrgId, page, pageSize, ct);

        return new PaginatedResult<LienOfferResponse>
        {
            Items = items.Select(MapToResponse).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
        };
    }

    public async Task<LienOfferResponse?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        var entity = await _offerRepo.GetByIdAsync(tenantId, id, ct);
        return entity is null ? null : MapToResponse(entity);
    }

    public async Task<List<LienOfferResponse>> GetByLienIdAsync(Guid tenantId, Guid lienId, CancellationToken ct = default)
    {
        var items = await _offerRepo.GetByLienIdAsync(tenantId, lienId, ct);
        return items.Select(MapToResponse).ToList();
    }

    public async Task<LienOfferResponse> CreateAsync(
        Guid tenantId, Guid buyerOrgId, Guid actingUserId,
        CreateLienOfferRequest request, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string[]>();

        if (request.LienId == Guid.Empty)
            errors.Add("lienId", ["Lien ID is required."]);
        if (request.OfferAmount <= 0)
            errors.Add("offerAmount", ["Offer amount must be positive."]);
        if (request.ExpiresAtUtc.HasValue && request.ExpiresAtUtc.Value <= DateTime.UtcNow)
            errors.Add("expiresAtUtc", ["Expiration must be in the future."]);
        if (errors.Count > 0)
            throw new ValidationException("One or more required fields are missing or invalid.", errors);

        var lien = await _lienRepo.GetByIdAsync(tenantId, request.LienId, ct)
            ?? throw new NotFoundException($"Lien '{request.LienId}' not found for tenant '{tenantId}'.");

        if (!OfferableStatuses.Contains(lien.Status))
            throw new InvalidOperationException(
                $"Lien '{request.LienId}' is in status '{lien.Status}' and cannot receive offers. " +
                $"Valid statuses: {string.Join(", ", OfferableStatuses)}.");

        var sellerOrgId = lien.SellingOrgId ?? lien.OrgId;

        if (buyerOrgId == sellerOrgId)
            throw new InvalidOperationException("Buyer organization cannot be the same as the seller organization.");

        if (await _offerRepo.HasActiveOfferAsync(tenantId, request.LienId, buyerOrgId, ct))
            throw new InvalidOperationException(
                $"Buyer organization '{buyerOrgId}' already has an active (Pending) offer on lien '{request.LienId}'.");

        var entity = LienOffer.Create(
            tenantId: tenantId,
            lienId: request.LienId,
            buyerOrgId: buyerOrgId,
            sellerOrgId: sellerOrgId,
            offerAmount: request.OfferAmount,
            createdByUserId: actingUserId,
            notes: request.Notes,
            expiresAtUtc: request.ExpiresAtUtc,
            submittedByPlatformUserId: actingUserId);

        _notificationOutbox.Enqueue(new NotificationInboxSendRequest(
            tenantId,
            actingUserId,
            BuildingBlocks.Notifications.NotificationTaxonomy.Liens.Events.OfferSubmitted,
            "lien",
            "Offer Submitted",
            $"Your offer for lien {lien.LienNumber} was submitted.",
            "Synq Selling",
            "SS",
            entity.OfferedAtUtc,
            $"selling:offer:{entity.Id:N}:submitted:{actingUserId:N}"));

        await _offerRepo.AddAsync(entity, ct);

        _logger.LogInformation(
            "LienOffer created: {OfferId} Lien={LienId} Buyer={BuyerOrgId} Amount={Amount} Tenant={TenantId}",
            entity.Id, entity.LienId, entity.BuyerOrgId, entity.OfferAmount, tenantId);

        _audit.Publish(
            eventType: "liens.offer.created",
            action: "create",
            description: $"Offer created on lien '{entity.LienId}' for amount {entity.OfferAmount}",
            tenantId: tenantId,
            actorUserId: actingUserId,
            entityType: "LienOffer",
            entityId: entity.Id.ToString());

        _ = _notifications.PublishAsync("lien.offer.submitted", tenantId, new Dictionary<string, string>
        {
            ["offerId"] = entity.Id.ToString(),
            ["lienId"] = entity.LienId.ToString(),
            ["lienNumber"] = lien.LienNumber,
            ["buyerOrgId"] = entity.BuyerOrgId.ToString(),
            ["sellerOrgId"] = entity.SellerOrgId.ToString(),
            ["offerAmount"] = entity.OfferAmount.ToString("F2"),
            ["originalLienAmount"] = lien.OriginalAmount.ToString("F2"),
            ["userId"] = actingUserId.ToString(),
        }, ct);

        return MapToResponse(entity);
    }

    private static LienOfferResponse MapToResponse(LienOffer entity)
    {
        return new LienOfferResponse
        {
            Id = entity.Id,
            LienId = entity.LienId,
            OfferAmount = entity.OfferAmount,
            Status = entity.Status,
            BuyerOrgId = entity.BuyerOrgId,
            SellerOrgId = entity.SellerOrgId,
            Notes = entity.Notes,
            ResponseNotes = entity.ResponseNotes,
            ExternalReference = entity.ExternalReference,
            OfferedAtUtc = entity.OfferedAtUtc,
            ExpiresAtUtc = entity.ExpiresAtUtc,
            RespondedAtUtc = entity.RespondedAtUtc,
            WithdrawnAtUtc = entity.WithdrawnAtUtc,
            IsExpired = entity.IsExpired,
            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc,
        };
    }
}
