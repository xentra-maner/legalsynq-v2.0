using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BuildingBlocks.Authentication.ServiceTokens;
using BuildingBlocks.Notifications;
using Liens.Api;
using Liens.Api.Serialization;
using Liens.Application.Interfaces;
using Liens.Application.Services;
using Liens.Domain.Entities;
using Liens.Domain.Enums;
using Liens.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Liens.Api.Endpoints;

public static class SellingPublicEndpoints
{
    private const int MaxPublicMessageLength = 400;
    private const int MaxPublicMessageAttachmentCount = 10;
    private const string SynqLienBuyerLoginReturnTo = "/funding/dashboard";
    private const string SynqLienBuyerActivationReason = "synqlien-buyer-activation";
    private const string DocumentsServiceAudience = "documents-service";
    private static readonly Guid SellingMessageAttachmentDocumentTypeId =
        Guid.Parse("10000000-0000-0000-0000-000000000005");
    private static readonly string[] SellingMessageAttachmentAllowedExtensions =
    [
        ".pdf",
        ".jpg",
        ".jpeg",
        ".png",
        ".docx",
        ".xlsx",
        ".xls",
        ".csv",
    ];
    private const string LegalSynqBrandIconContentId = "legalsynq-brand-icon";
    private const string LegalSynqBrandIconPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAHgAAAB4CAYAAAA5ZDbSAAAFH0lEQVR42u2d23HiMBSGU4JLcAmOSWZ4pIR0sDxsAYLdfQ4dLB2QDrYEtgOX4AZCVILXwmJyWRsdCev+/zPnFbA+jq7Wf+7uIAiCvIqzVcG39fq0XRzeNnXbR0cI/rZZHE/bmnG2LNGKocLd1s8DLBLUyRB/DoAOKmuXZZ+Bza1gv2a1yGi0bhBwyV2xdoheAa2cKNz3LhuZ7EViYmQb7qW7xpjsWK/9TNkRXBmLI1rdbfY2bgH34zF7rNDy7sZe9djJ6p2qa+WsKjg79wYtYSzeofUdqAfHVDBee2h6f5qKMGFDN+0G8KbeK5Y2B7Oe4WGlmmyh9QOYPZsua0R3rR6HqwIEPAMWM2zzz1YCxnIJgCEAhgA4610ssSadils2JAA4+d4BgAEYAmAIgPNS13VFH099sD52DuLJBHCvveJzxe9fiecB1QHsuo9j514HQ8A6Es+1zhWs+Je3nT+5AHyReM4yJ7jPnX+5BHzRcw5wD10Y8gE4bciBZK5vwEIs1TG3A+CzeHJjsucJVWiAzzPs1JZCOksL1Voz1HXwXnPJt0oF8JEI1nu3NcdOlngOYo+1T2WHSqUmta1KIuQ2l8lVmRpgjWcvYge8jmmyMfdhg5wxX1MVO2BmMptNCHCT9ERLzi5zBnwE4LQBV3IsnooCgCMGnMPhAgADMAADMADnA/hsdsaWpY1QAv75sLL13apwAoz/eqwGJ7ma/Xej4MfiyQVg9/4cQcXZlU+0wWzQpT2g0kVu7OI1AFuN9mYfLx17QAD2CFo3m0XW6vpQAbBvyMRLdxJuY2D7B8AxZLK04u0AOMZQOAHd0ngAHEaIpdy1hX4LwIlmMcEn6oOX8uLwNfiI+VhsO1khS+yiCfc+mu3iyI4bZewVJmQ6XlEAbAO0ugcb9SlRzZxNHOQA2NaeucIMrk/EsfGXGw/eAOxUKr/O0WS0cZKSO2B5qX19JYze6FBNNAHYHWAr72QBMAADMAADMAADMABfXHSmggFwxIBzly3ARmUNbGx0ALAdwKpDodEKMeo3OBYNkPkFLN+POxgdGarS/nIUhTJv9gGLN1TF26sf47R9+E08zuUT/w7ycWE3HEyIjM8rXN0unPtcXqObzvxtiRgAX/uNelkMwKEBJpXfo741AMBhAT5t7l/oC+nN4gVA4wGsBReZHBVgLg7/jaf51NKrAOwcMJfvx82zZB1AY4btCnA/RO7H3lwd6kKJK6wWi2SKOzDDl/gJ9dBx/9fWd4eyVZm0jE5SEtuLBmAABmAABmAABuDMAfu4XQjAbh9wnTngJnfAPFXA0vVdpej9oikPyRIFrKwTlcSkiOB6znX+yTEA7p/nWzaldWS5GVLZN0r9hpABSw9oavm+dSqAZ696ZgI4sMJcaVUk7R/mBYA/aZfU5oSsn9QCcCL1kiYauCJMuFIHnFbXbAtypIDThvtlbdxmBvjY5VTm/cM+dZs4YJ76FVnqduYfHdiBA27lWnh1B0124ZPRfK+YIeDSdoDeDIrpNAkCYAiAARiAAZimsZv1nwLuBnEDVl3PGfVhhgAYAmAIgAEYgHMAPOkTRQJ8/eI13P4cSNxPJrjNMP3PPRfihF9nCKJURBWVUynrVmkP+JtSGxAt70g028V3MIq43UEOmrubrsrQ/Tkgt1l8U5Ac5KDZs7hwZPGEsddzV20TcouuOVnI+vbBkM0xeT7rRWEPiDE31Gw2dOXjQ8bWzKqLHDQ38GV53b1OuPbhEB+CoND1D6mLXlFVwRdjAAAAAElFTkSuQmCC";

    private static readonly IReadOnlyList<NotificationEmailInlineAttachment> PublicResponseInlineAttachments =
    [
        new(
            LegalSynqBrandIconContentId,
            "legalsynq-brand-icon.png",
            "image/png",
            LegalSynqBrandIconPngBase64),
    ];

    public static void MapSellingPublicEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/liens/selling/public")
            .AllowAnonymous();

        group.MapGet("/{token}", GetTemporaryBuyerPortal)
            .AllowAnonymous();

        group.MapGet("/{token}/documents/{documentId:guid}/view", ViewTemporaryBuyerPortalDocument)
            .AllowAnonymous();

        group.MapGet("/{token}/documents/{documentId:guid}/download", DownloadTemporaryBuyerPortalDocument)
            .AllowAnonymous();

        group.MapPost("/{token}/messages", PostTemporaryBuyerPortalMessage)
            .AllowAnonymous()
            .DisableAntiforgery();

        group.MapGet("/{token}/message-attachments/{attachmentId:guid}/view", ViewTemporaryBuyerPortalMessageAttachment)
            .AllowAnonymous();

        group.MapGet("/{token}/message-attachments/{attachmentId:guid}/download", DownloadTemporaryBuyerPortalMessageAttachment)
            .AllowAnonymous();

        group.MapPost("/{token}/accept", AcceptTemporaryBuyerPortal)
            .AllowAnonymous();

        group.MapPost("/{token}/offers", SubmitPublicBuyerOffer)
            .AllowAnonymous();

        group.MapPost("/{token}/decline", DeclineTemporaryBuyerPortal)
            .AllowAnonymous();

        group.MapPost("/{token}/activate-account", ActivateBuyerAccount)
            .AllowAnonymous();
    }

    private static async Task<IResult> GetTemporaryBuyerPortal(
        string token,
        HttpResponse response,
        IPublicBuyerAccountProvisioningService buyerAccountService,
        ISellerOrganizationDisplayResolver sellerDisplayResolver,
        LiensDbContext db,
        CancellationToken ct = default)
    {
        SetNoReferrerHeader(response);
        var resolved = await ResolvePublicAccessLinkAsync(token, db, ct);
        if (resolved.Error is not null)
            return resolved.Error;

        var requireActionable =
            string.IsNullOrWhiteSpace(resolved.AccessLink!.ResponseStatus) &&
            !string.Equals(
                resolved.AccessLink.Purpose,
                SellingAccessLinkPurposes.ConfirmSaleSellerView,
                StringComparison.Ordinal);
        var view = await BuildPublicViewAsync(db, sellerDisplayResolver, resolved.AccessLink, ct, requireActionable);
        if (view is null)
        {
            return PublicLinkState(
                "unavailable",
                "Lien offer unavailable",
                "The lien offer data could not be resolved.",
                StatusCodes.Status404NotFound);
        }

        var account = await ResolvePublicBuyerAccountAsync(view, buyerAccountService, ct);
        resolved.AccessLink!.MarkAccessed();
        await db.SaveChangesAsync(ct);

        return Results.Ok(MapPublicPortalResponse(view, account, token));
    }

    private static Task<IResult> ViewTemporaryBuyerPortalDocument(
        string token,
        Guid documentId,
        HttpResponse response,
        LiensDbContext db,
        IHttpClientFactory httpClientFactory,
        IServiceTokenIssuer serviceTokenIssuer,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
        => RedirectTemporaryBuyerPortalDocument(
            token,
            documentId,
            "view",
            response,
            db,
            httpClientFactory,
            serviceTokenIssuer,
            loggerFactory,
            ct);

    private static Task<IResult> DownloadTemporaryBuyerPortalDocument(
        string token,
        Guid documentId,
        HttpResponse response,
        LiensDbContext db,
        IHttpClientFactory httpClientFactory,
        IServiceTokenIssuer serviceTokenIssuer,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
        => RedirectTemporaryBuyerPortalDocument(
            token,
            documentId,
            "download",
            response,
            db,
            httpClientFactory,
            serviceTokenIssuer,
            loggerFactory,
            ct);

    private static Task<IResult> ViewTemporaryBuyerPortalMessageAttachment(
        string token,
        Guid attachmentId,
        HttpResponse response,
        LiensDbContext db,
        IHttpClientFactory httpClientFactory,
        IServiceTokenIssuer serviceTokenIssuer,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
        => RedirectTemporaryBuyerPortalMessageAttachment(
            token,
            attachmentId,
            "view",
            response,
            db,
            httpClientFactory,
            serviceTokenIssuer,
            loggerFactory,
            ct);

    private static Task<IResult> DownloadTemporaryBuyerPortalMessageAttachment(
        string token,
        Guid attachmentId,
        HttpResponse response,
        LiensDbContext db,
        IHttpClientFactory httpClientFactory,
        IServiceTokenIssuer serviceTokenIssuer,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
        => RedirectTemporaryBuyerPortalMessageAttachment(
            token,
            attachmentId,
            "download",
            response,
            db,
            httpClientFactory,
            serviceTokenIssuer,
            loggerFactory,
            ct);

    private static async Task<IResult> RedirectTemporaryBuyerPortalMessageAttachment(
        string token,
        Guid attachmentId,
        string accessType,
        HttpResponse response,
        LiensDbContext db,
        IHttpClientFactory httpClientFactory,
        IServiceTokenIssuer serviceTokenIssuer,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        SetNoReferrerHeader(response);
        if (attachmentId == Guid.Empty)
        {
            return PublicLinkState(
                "attachment-required",
                "Attachment unavailable",
                "A valid attachment id is required.",
                StatusCodes.Status400BadRequest);
        }

        var resolved = await ResolvePublicAccessLinkAsync(token, db, ct);
        if (resolved.Error is not null)
            return resolved.Error;

        var accessLink = resolved.AccessLink!;
        var attachment = await ResolvePublicMessageAttachmentAsync(db, accessLink, attachmentId, ct);
        if (attachment is null)
        {
            return PublicLinkState(
                "attachment-not-found",
                "Attachment unavailable",
                "This attachment is not part of the lien offer message thread.",
                StatusCodes.Status404NotFound);
        }

        var redeemUrl = await IssueDocumentsAccessUrlAsync(
            httpClientFactory,
            serviceTokenIssuer,
            loggerFactory,
            accessLink,
            attachment.DocumentId,
            accessType,
            ct);
        if (string.IsNullOrWhiteSpace(redeemUrl))
        {
            return PublicLinkState(
                "attachment-unavailable",
                "Attachment unavailable",
                "The attachment could not be opened right now.",
                StatusCodes.Status502BadGateway);
        }

        accessLink.MarkAccessed();
        await db.SaveChangesAsync(ct);

        return Results.Redirect(redeemUrl, permanent: false, preserveMethod: false);
    }

    private static async Task<IResult> RedirectTemporaryBuyerPortalDocument(
        string token,
        Guid documentId,
        string accessType,
        HttpResponse response,
        LiensDbContext db,
        IHttpClientFactory httpClientFactory,
        IServiceTokenIssuer serviceTokenIssuer,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        SetNoReferrerHeader(response);
        if (documentId == Guid.Empty)
        {
            return PublicLinkState(
                "document-required",
                "Document unavailable",
                "A valid document id is required.",
                StatusCodes.Status400BadRequest);
        }

        var resolved = await ResolvePublicAccessLinkAsync(token, db, ct);
        if (resolved.Error is not null)
            return resolved.Error;

        var accessLink = resolved.AccessLink!;
        if (!IsSupportedPublicPurpose(accessLink.Purpose))
        {
            return PublicLinkState(
                "unsupported-link",
                "Lien offer link unavailable",
                "This secure link cannot access lien documents.",
                StatusCodes.Status404NotFound);
        }

        var documentReference = await ResolvePublicDocumentReferenceAsync(
            db,
            accessLink.TenantId,
            accessLink.LienId,
            documentId,
            ct);
        if (documentReference is null)
        {
            return PublicLinkState(
                "document-not-found",
                "Document unavailable",
                "This document is not attached to the lien offer.",
                StatusCodes.Status404NotFound);
        }

        var redeemUrl = await IssueDocumentsAccessUrlAsync(
            httpClientFactory,
            serviceTokenIssuer,
            loggerFactory,
            accessLink,
            documentReference.Value,
            accessType,
            ct);
        if (string.IsNullOrWhiteSpace(redeemUrl))
        {
            return PublicLinkState(
                "document-unavailable",
                "Document unavailable",
                "The document could not be opened right now.",
                StatusCodes.Status502BadGateway);
        }

        accessLink.MarkAccessed();
        await db.SaveChangesAsync(ct);

        return Results.Redirect(redeemUrl, permanent: false, preserveMethod: false);
    }

    internal static async Task<IResult> PostTemporaryBuyerPortalMessage(
        string token,
        HttpContext httpContext,
        INotificationPublisher notifications,
        ISellingBuyerAccessLinkService accessLinks,
        ILoggerFactory loggerFactory,
        IConfiguration configuration,
        ISellerOrganizationDisplayResolver sellerDisplayResolver,
        LiensDbContext db,
        ILegacyDocumentUploadClient uploadClient,
        CancellationToken ct = default)
    {
        var parsedRequest = await ReadPortalMessageRequestAsync(httpContext.Request, ct);
        if (parsedRequest.Error is not null)
            return parsedRequest.Error;

        var resolved = await ResolvePublicAccessLinkAsync(token, db, ct);
        if (resolved.Error is not null)
            return resolved.Error;

        try
        {
            return await PostResolvedBuyerPortalMessage(
                resolved.AccessLink!,
                token,
                parsedRequest.Request,
                httpContext,
                notifications,
                accessLinks,
                loggerFactory,
                configuration,
                sellerDisplayResolver,
                db,
                uploadClient,
                parsedRequest.Attachments,
                ct);
        }
        finally
        {
            DisposePortalMessageAttachmentUploads(parsedRequest.Attachments);
        }
    }

    internal static async Task<IResult> PostResolvedBuyerPortalMessage(
        SellingBuyerAccessLink accessLink,
        string? currentToken,
        PublicPortalMessageRequest? request,
        HttpContext httpContext,
        INotificationPublisher notifications,
        ISellingBuyerAccessLinkService accessLinks,
        ILoggerFactory loggerFactory,
        IConfiguration configuration,
        ISellerOrganizationDisplayResolver sellerDisplayResolver,
        LiensDbContext db,
        ILegacyDocumentUploadClient? uploadClient = null,
        IReadOnlyList<SellingPortalMessageAttachmentUpload>? attachmentUploads = null,
        CancellationToken ct = default,
        string? currentBuyerAccountEmail = null,
        Func<SellingPortalMessageAttachment, string, string?>? attachmentUrlBuilder = null)
        => await PostResolvedPortalMessage(
            accessLink,
            currentToken,
            request,
            httpContext,
            notifications,
            accessLinks,
            loggerFactory,
            configuration,
            sellerDisplayResolver,
            db,
            ct,
            uploadClient: uploadClient,
            attachmentUploads: attachmentUploads,
            attachmentUrlBuilder: attachmentUrlBuilder,
            senderTypeOverride: null,
            currentBuyerAccountEmail: currentBuyerAccountEmail);

    internal static async Task<IResult> PostResolvedSellerPortalMessage(
        SellingBuyerAccessLink accessLink,
        PublicPortalMessageRequest? request,
        HttpContext httpContext,
        INotificationPublisher notifications,
        ISellingBuyerAccessLinkService accessLinks,
        ILoggerFactory loggerFactory,
        IConfiguration configuration,
        ISellerOrganizationDisplayResolver sellerDisplayResolver,
        LiensDbContext db,
        ILegacyDocumentUploadClient? uploadClient = null,
        IReadOnlyList<SellingPortalMessageAttachmentUpload>? attachmentUploads = null,
        Func<SellingPortalMessageAttachment, string, string?>? attachmentUrlBuilder = null,
        CancellationToken ct = default)
        => await PostResolvedPortalMessage(
            accessLink,
            null,
            request,
            httpContext,
            notifications,
            accessLinks,
            loggerFactory,
            configuration,
            sellerDisplayResolver,
            db,
            ct,
            senderTypeOverride: SellingPortalMessageSenderType.Seller,
            currentBuyerAccountEmail: null,
            uploadClient: uploadClient,
            attachmentUploads: attachmentUploads,
            attachmentUrlBuilder: attachmentUrlBuilder);

    private static async Task<IResult> PostResolvedPortalMessage(
        SellingBuyerAccessLink accessLink,
        string? currentToken,
        PublicPortalMessageRequest? request,
        HttpContext httpContext,
        INotificationPublisher notifications,
        ISellingBuyerAccessLinkService accessLinks,
        ILoggerFactory loggerFactory,
        IConfiguration configuration,
        ISellerOrganizationDisplayResolver sellerDisplayResolver,
        LiensDbContext db,
        CancellationToken ct = default,
        string? senderTypeOverride = null,
        string? currentBuyerAccountEmail = null,
        ILegacyDocumentUploadClient? uploadClient = null,
        IReadOnlyList<SellingPortalMessageAttachmentUpload>? attachmentUploads = null,
        Func<SellingPortalMessageAttachment, string, string?>? attachmentUrlBuilder = null)
    {
        var messageText = request?.Message?.Trim() ?? string.Empty;
        var uploads = attachmentUploads ?? [];
        if (messageText.Length == 0 && uploads.Count == 0)
        {
            return PublicLinkState(
                "message-required",
                "Message could not be sent",
                "Enter a message or attach at least one file before sending.",
                StatusCodes.Status400BadRequest);
        }

        if (messageText.Length > MaxPublicMessageLength)
        {
            return PublicLinkState(
                "message-too-long",
                "Message could not be sent",
                $"Message must be {MaxPublicMessageLength} characters or fewer.",
                StatusCodes.Status400BadRequest);
        }

        var view = await BuildPublicViewAsync(
            db,
            sellerDisplayResolver,
            accessLink,
            ct,
            requireActionable: false,
            currentBuyerAccountEmail: currentBuyerAccountEmail);
        if (view is null)
        {
            return PublicLinkState(
                "unavailable",
                "Lien offer unavailable",
                "The lien offer data could not be resolved.",
                StatusCodes.Status404NotFound);
        }

        var senderType = senderTypeOverride ?? ResolvePublicAudience(view.AccessLink);
        var sender = ResolvePublicMessageSender(view, senderType);
        var publicMessage = SellingPortalMessage.Create(
            view.AccessLink.TenantId,
            view.AccessLink.LienId,
            view.AccessLink.SellerOrgId,
            view.AccessLink.BuyerOrgId,
            view.AccessLink.BuyerContactId,
            view.AccessLink.Id,
            senderType,
            sender.Name,
            sender.Email,
            messageText,
            ResolvePublicMessageActorId(view.AccessLink, senderType));

        view.AccessLink.MarkAccessed();
        db.SellingPortalMessages.Add(publicMessage);
        var attachments = await UploadSellingPortalMessageAttachmentsAsync(
            publicMessage,
            uploads,
            uploadClient,
            ct);
        if (attachments.Count > 0)
            db.SellingPortalMessageAttachments.AddRange(attachments);

        var inboxRecipient = string.Equals(senderType, SellingPortalMessageSenderType.Buyer, StringComparison.Ordinal)
            ? view.AccessLink.CreatedByUserId
            : await db.SellingBuyerAccessLinks
                .AsNoTracking()
                .Where(link =>
                    link.TenantId == view.AccessLink.TenantId &&
                    link.LienId == view.AccessLink.LienId &&
                    link.BuyerOrgId == view.AccessLink.BuyerOrgId &&
                    link.BuyerContactId == view.AccessLink.BuyerContactId &&
                    link.AccountActivatedUserId != null)
                .OrderByDescending(link => link.AccountActivatedAtUtc)
                .ThenByDescending(link => link.Id)
                .Select(link => link.AccountActivatedUserId)
                .FirstOrDefaultAsync(ct);
        if (inboxRecipient is { } recipientUserId && recipientUserId != Guid.Empty)
        {
            EnqueueInbox(
                db,
                view.AccessLink.TenantId,
                recipientUserId,
                NotificationTaxonomy.Liens.Events.OfferMessageCreated,
                "message",
                "New Message",
                $"{sender.Name} sent a new message regarding lien {ResolveLienCode(view.Lien)}.",
                publicMessage.CreatedAtUtc,
                $"selling:message:{publicMessage.Id:N}:{recipientUserId:N}",
                sender.Name);
        }
        await db.SaveChangesAsync(ct);

        await SendPublicMessageNotificationAsync(
            notifications,
            loggerFactory,
            configuration,
            httpContext,
            view,
            publicMessage,
            accessLinks,
            currentToken,
            ct);

        var location = currentToken is null
            ? $"/api/liens/selling/buyer/liens/{accessLink.Id}/messages/{publicMessage.Id}"
            : $"/api/liens/selling/public/{Uri.EscapeDataString(currentToken)}/messages/{publicMessage.Id}";

        return Results.Created(
            location,
            MapPublicMessage(publicMessage, attachments, currentToken, attachmentUrlBuilder));
    }

    internal static async Task<IResult> AcceptTemporaryBuyerPortal(
        string token,
        PublicBuyerAcceptLienRequest? request,
        HttpContext httpContext,
        INotificationPublisher notifications,
        ILoggerFactory loggerFactory,
        ISellerOrganizationDisplayResolver sellerDisplayResolver,
        LiensDbContext db,
        CancellationToken ct = default)
    {
        SetNoReferrerHeader(httpContext.Response);
        if (!SellingIdempotency.TryGetKey(httpContext.Request, out var idempotencyKey, out var idempotencyError))
            return idempotencyError!;
        var resolved = await ResolvePublicAccessLinkAsync(token, db, ct);
        if (resolved.Error is not null)
            return resolved.Error;

        return await AcceptResolvedBuyerPortal(
            resolved.AccessLink!,
            request,
            httpContext,
            notifications,
            loggerFactory,
            sellerDisplayResolver,
            db,
            ct);
    }

    internal static async Task<IResult> AcceptResolvedBuyerPortal(
        SellingBuyerAccessLink accessLink,
        PublicBuyerAcceptLienRequest? request,
        HttpContext httpContext,
        INotificationPublisher notifications,
        ILoggerFactory loggerFactory,
        ISellerOrganizationDisplayResolver sellerDisplayResolver,
        LiensDbContext db,
        CancellationToken ct = default,
        string? currentBuyerAccountEmail = null)
    {
        SetNoReferrerHeader(httpContext.Response);
        if (!SellingIdempotency.TryGetKey(httpContext.Request, out var idempotencyKey, out var idempotencyError))
            return idempotencyError!;
        if (EnsureBuyerResponseLink(accessLink) is { } readOnlyError)
            return readOnlyError;

        var replay = await SellingIdempotency.GetReplayAsync(
            db,
            accessLink.TenantId,
            "BuyerAccessLink",
            accessLink.Id,
            "/api/liens/selling/public/{token}/accept",
            "Lien",
            accessLink.LienId.ToString(),
            idempotencyKey!,
            request,
            ct);
        if (replay is not null)
            return replay;

        if (await HandleExistingPublicResponseAsync(
                accessLink,
                SellingBuyerResponseStatus.Accepted,
                notifications,
                loggerFactory,
                sellerDisplayResolver,
                db,
                ct,
                currentBuyerAccountEmail: currentBuyerAccountEmail) is { } existingResponse)
            return existingResponse;

        var view = await BuildPublicViewAsync(
            db,
            sellerDisplayResolver,
            accessLink,
            ct,
            currentBuyerAccountEmail: currentBuyerAccountEmail);
        if (view is null)
        {
            return PublicLinkState(
                "unavailable",
                "Lien offer unavailable",
                "The lien offer data could not be resolved.",
                StatusCodes.Status404NotFound);
        }

        var responseAmount = view.Lien.AskAmount;
        if (!responseAmount.HasValue || responseAmount.Value <= 0m)
        {
            return PublicLinkState(
                "ask-unavailable",
                "Lien offer unavailable",
                "This lien does not have a valid ask amount.",
                StatusCodes.Status409Conflict);
        }

        if (!string.IsNullOrWhiteSpace(view.AccessLink.ResponseStatus))
        {
            return PublicLinkState(
                "response-conflict",
                "Lien response already recorded",
                "A buyer response has already been recorded for this secure link.",
                StatusCodes.Status409Conflict);
        }

        var lienTransition = await SellingIdempotency.TryBeginAsync(
            db,
            view.AccessLink.TenantId,
            "LienStateTransition",
            view.Lien.Id,
            "/api/liens/selling/liens/{lienId}/state-transition",
            "Lien",
            view.Lien.Id.ToString(),
            "lien-state-transition-v1",
            request: null,
            ct: ct);
        if (lienTransition.Result is not null)
        {
            return PublicLinkState(
                "not-actionable",
                "Lien offer unavailable",
                "This lien is changing state and cannot accept a buyer response.",
                StatusCodes.Status409Conflict);
        }

        var responseTransition = await SellingIdempotency.TryBeginAsync(
            db,
            view.AccessLink.TenantId,
            "BuyerLinkResponseTransition",
            view.AccessLink.Id,
            "/api/liens/selling/public/{token}/response",
            "BuyerAccessLink",
            view.AccessLink.Id.ToString(),
            "buyer-response-transition-v1",
            request: null,
            ct: ct);
        if (responseTransition.Result is not null)
        {
            db.SellingIdempotencyRecords.Remove(lienTransition.Record!);
            await db.SaveChangesAsync(ct);
            return PublicLinkState(
                "response-conflict",
                "Lien response already recorded",
                "A buyer response is already being recorded for this secure link.",
                StatusCodes.Status409Conflict);
        }

        var started = await SellingIdempotency.TryBeginAsync(
            db,
            view.AccessLink.TenantId,
            "BuyerAccessLink",
            view.AccessLink.Id,
            "/api/liens/selling/public/{token}/accept",
            "Lien",
            view.Lien.Id.ToString(),
            idempotencyKey!,
            request,
            ct);
        if (started.Result is not null)
        {
            db.SellingIdempotencyRecords.Remove(responseTransition.Record!);
            db.SellingIdempotencyRecords.Remove(lienTransition.Record!);
            await db.SaveChangesAsync(ct);
            return started.Result;
        }

        view.AccessLink.MarkAccessed();
        view.AccessLink.RecordResponse(
            SellingBuyerResponseStatus.Accepted,
            responseAmount.Value,
            FirstNonEmpty(request?.Notes, request?.Message));
        await ApplyPublicResponseToLienAsync(db, view, SellingBuyerResponseStatus.Accepted, ct);
        EnqueueAccessLinkResponseInbox(db, view, accepted: true);
        await db.SaveChangesAsync(ct);

        var persistedLien = await db.Liens.AsNoTracking().FirstAsync(
            lien => lien.TenantId == view.AccessLink.TenantId && lien.Id == view.Lien.Id,
            ct);
        // Accepted links are intentionally no longer actionable, so the public
        // projection builder returns null. Use the post-transition lien for the
        // immediate response rather than leaking the stale Offered state.
        var updatedView = await BuildPublicViewAsync(
                              db,
                              sellerDisplayResolver,
                              view.AccessLink,
                              ct,
                              currentBuyerAccountEmail: currentBuyerAccountEmail)
                          ?? view with { Lien = persistedLien };
        await SendPublicResponseNotificationsAsync(
            notifications,
            loggerFactory,
            updatedView,
            SellingBuyerResponseStatus.Accepted,
            updatedView.AccessLink.ResponseNotes,
            ct);
        var completed = await SellingIdempotency.CompleteAsync(
            db,
            started.Record!,
            ResolvePublicResponseActorId(view.AccessLink),
            StatusCodes.Status200OK,
            MapPublicPortalResponse(updatedView),
            ct);
        await SellingIdempotency.CompleteAsync(
            db,
            responseTransition.Record!,
            ResolvePublicResponseActorId(view.AccessLink),
            StatusCodes.Status200OK,
            MapPublicPortalResponse(updatedView),
            ct);
        await SellingIdempotency.CompleteAsync(
            db,
            lienTransition.Record!,
            ResolvePublicResponseActorId(view.AccessLink),
            StatusCodes.Status200OK,
            MapPublicPortalResponse(updatedView),
            ct);
        return completed;
    }

    internal static async Task<IResult> DeclineTemporaryBuyerPortal(
        string token,
        PublicBuyerDeclineLienRequest? request,
        HttpContext httpContext,
        INotificationPublisher notifications,
        ILoggerFactory loggerFactory,
        ISellerOrganizationDisplayResolver sellerDisplayResolver,
        LiensDbContext db,
        CancellationToken ct = default)
    {
        SetNoReferrerHeader(httpContext.Response);
        if (!SellingIdempotency.TryGetKey(httpContext.Request, out var idempotencyKey, out var idempotencyError))
            return idempotencyError!;
        var resolved = await ResolvePublicAccessLinkAsync(token, db, ct);
        if (resolved.Error is not null)
            return resolved.Error;

        return await DeclineResolvedBuyerPortal(
            resolved.AccessLink!,
            request,
            httpContext,
            notifications,
            loggerFactory,
            sellerDisplayResolver,
            db,
            ct);
    }

    internal static async Task<IResult> DeclineResolvedBuyerPortal(
        SellingBuyerAccessLink accessLink,
        PublicBuyerDeclineLienRequest? request,
        HttpContext httpContext,
        INotificationPublisher notifications,
        ILoggerFactory loggerFactory,
        ISellerOrganizationDisplayResolver sellerDisplayResolver,
        LiensDbContext db,
        CancellationToken ct = default,
        string? currentBuyerAccountEmail = null)
    {
        SetNoReferrerHeader(httpContext.Response);
        if (!SellingIdempotency.TryGetKey(httpContext.Request, out var idempotencyKey, out var idempotencyError))
            return idempotencyError!;
        if (EnsureBuyerResponseLink(accessLink) is { } readOnlyError)
            return readOnlyError;

        var replay = await SellingIdempotency.GetReplayAsync(
            db,
            accessLink.TenantId,
            "BuyerAccessLink",
            accessLink.Id,
            "/api/liens/selling/public/{token}/decline",
            "Lien",
            accessLink.LienId.ToString(),
            idempotencyKey!,
            request,
            ct);
        if (replay is not null)
            return replay;

        if (await HandleExistingPublicResponseAsync(
                accessLink,
                SellingBuyerResponseStatus.Declined,
                notifications,
                loggerFactory,
                sellerDisplayResolver,
                db,
                ct,
                currentBuyerAccountEmail: currentBuyerAccountEmail) is { } existingResponse)
            return existingResponse;

        var view = await BuildPublicViewAsync(
            db,
            sellerDisplayResolver,
            accessLink,
            ct,
            currentBuyerAccountEmail: currentBuyerAccountEmail);
        if (view is null)
        {
            return PublicLinkState(
                "unavailable",
                "Lien offer unavailable",
                "The lien offer data could not be resolved.",
                StatusCodes.Status404NotFound);
        }

        if (!IsActionableLien(view.Lien))
        {
            return PublicLinkState(
                "not-actionable",
                "Lien offer unavailable",
                "This lien is no longer accepting buyer responses.",
                StatusCodes.Status409Conflict);
        }

        var responseTransition = await SellingIdempotency.TryBeginAsync(
            db,
            view.AccessLink.TenantId,
            "BuyerLinkResponseTransition",
            view.AccessLink.Id,
            "/api/liens/selling/public/{token}/response",
            "BuyerAccessLink",
            view.AccessLink.Id.ToString(),
            "buyer-response-transition-v1",
            request: null,
            ct: ct);
        if (responseTransition.Result is not null)
        {
            return PublicLinkState(
                "response-conflict",
                "Lien response already recorded",
                "A buyer response is already being recorded for this secure link.",
                StatusCodes.Status409Conflict);
        }

        var started = await SellingIdempotency.TryBeginAsync(
            db,
            view.AccessLink.TenantId,
            "BuyerAccessLink",
            view.AccessLink.Id,
            "/api/liens/selling/public/{token}/decline",
            "Lien",
            view.Lien.Id.ToString(),
            idempotencyKey!,
            request,
            ct);
        if (started.Result is not null)
        {
            db.SellingIdempotencyRecords.Remove(responseTransition.Record!);
            await db.SaveChangesAsync(ct);
            return started.Result;
        }
        view.AccessLink.MarkAccessed();
        view.AccessLink.RecordResponse(SellingBuyerResponseStatus.Declined, null, request?.Reason);
        await ApplyPublicResponseToLienAsync(db, view, SellingBuyerResponseStatus.Declined, ct);
        EnqueueAccessLinkResponseInbox(db, view, accepted: false);
        await db.SaveChangesAsync(ct);
        var persistedLien = await db.Liens.AsNoTracking().FirstAsync(
            lien => lien.TenantId == view.AccessLink.TenantId && lien.Id == view.Lien.Id,
            ct);
        var updatedView = await BuildPublicViewAsync(
                              db,
                              sellerDisplayResolver,
                              view.AccessLink,
                              ct,
                              currentBuyerAccountEmail: currentBuyerAccountEmail)
                          ?? view with { Lien = persistedLien };
        await SendPublicResponseNotificationsAsync(
            notifications,
            loggerFactory,
            updatedView,
            SellingBuyerResponseStatus.Declined,
            updatedView.AccessLink.ResponseNotes,
            ct);
        var completed = await SellingIdempotency.CompleteAsync(
            db,
            started.Record!,
            view.AccessLink.BuyerContactId,
            StatusCodes.Status200OK,
            MapPublicPortalResponse(updatedView),
            ct);
        await SellingIdempotency.CompleteAsync(
            db,
            responseTransition.Record!,
            view.AccessLink.BuyerContactId,
            StatusCodes.Status200OK,
            MapPublicPortalResponse(updatedView),
            ct);
        return completed;
    }

    private static async Task<IResult> SubmitPublicBuyerOffer(
        string token,
        PublicBuyerOfferRequest? request,
        HttpRequest httpRequest,
        HttpResponse response,
        ISellerOrganizationDisplayResolver sellerDisplayResolver,
        LiensDbContext db,
        CancellationToken ct = default)
    {
        SetNoReferrerHeader(response);
        if (!SellingIdempotency.TryGetKey(httpRequest, out var idempotencyKey, out var idempotencyError))
            return idempotencyError!;
        if (request is null || request.OfferAmount <= 0m)
        {
            return PublicLinkState(
                "invalid-offer",
                "Lien offer unavailable",
                "offerAmount must be positive.",
                StatusCodes.Status400BadRequest);
        }

        var resolved = await ResolvePublicAccessLinkAsync(token, db, ct);
        if (resolved.Error is not null)
            return resolved.Error;

        var replay = await SellingIdempotency.GetReplayAsync(
            db,
            resolved.AccessLink!.TenantId,
            "BuyerAccessLink",
            resolved.AccessLink.Id,
            "/api/liens/selling/public/{token}/offers",
            "Lien",
            resolved.AccessLink.LienId.ToString(),
            idempotencyKey!,
            request,
            ct);
        if (replay is not null)
            return replay;

        if (!string.IsNullOrWhiteSpace(resolved.AccessLink.ResponseStatus))
        {
            return PublicLinkState(
                "response-conflict",
                "Lien response already recorded",
                "A buyer response has already been recorded for this secure link.",
                StatusCodes.Status409Conflict);
        }

        var view = await BuildPublicViewAsync(db, sellerDisplayResolver, resolved.AccessLink!, ct);
        if (view is null || !IsActionableLien(view.Lien))
        {
            return PublicLinkState(
                "not-actionable",
                "Lien offer unavailable",
                "This lien is no longer accepting buyer offers.",
                StatusCodes.Status409Conflict);
        }

        var activeOfferExists = await db.LienOffers.AnyAsync(offer =>
            offer.TenantId == view.AccessLink.TenantId &&
            offer.LienId == view.Lien.Id &&
            offer.BuyerOrgId == view.AccessLink.BuyerOrgId &&
            offer.Status == OfferStatus.Pending &&
            (!offer.ExpiresAtUtc.HasValue || offer.ExpiresAtUtc > DateTime.UtcNow),
            ct);
        if (activeOfferExists)
        {
            return PublicLinkState(
                "active-offer-exists",
                "Lien offer unavailable",
                "An active offer has already been submitted by this buyer organization.",
                StatusCodes.Status409Conflict);
        }

        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct)
            : null;
        try
        {
            var started = await SellingIdempotency.TryBeginAsync(
                db,
                view.AccessLink.TenantId,
                "BuyerAccessLink",
                view.AccessLink.Id,
                "/api/liens/selling/public/{token}/offers",
                "Lien",
                view.Lien.Id.ToString(),
                idempotencyKey!,
                request,
                ct);
            if (started.Result is not null)
                return started.Result;

            // Repeat the predicate inside a serializable transaction after the
            // idempotency row is reserved. This closes the different-key race
            // that could otherwise create two active offers for one buyer/lien.
            activeOfferExists = await db.LienOffers.AnyAsync(offer =>
                offer.TenantId == view.AccessLink.TenantId &&
                offer.LienId == view.Lien.Id &&
                offer.BuyerOrgId == view.AccessLink.BuyerOrgId &&
                offer.Status == OfferStatus.Pending &&
                (!offer.ExpiresAtUtc.HasValue || offer.ExpiresAtUtc > DateTime.UtcNow),
                ct);
            if (activeOfferExists)
            {
                var conflict = await SellingIdempotency.CompleteAsync(
                    db,
                    started.Record!,
                    view.AccessLink.BuyerContactId,
                    StatusCodes.Status409Conflict,
                    new { error = new { code = "active_offer_exists", message = "An active offer has already been submitted by this buyer organization." } },
                    ct);
                if (transaction is not null) await transaction.CommitAsync(ct);
                return conflict;
            }

            var offer = LienOffer.Create(
                view.AccessLink.TenantId,
                view.Lien.Id,
                view.AccessLink.BuyerOrgId,
                view.AccessLink.SellerOrgId,
                request.OfferAmount,
                view.AccessLink.BuyerContactId,
                request.Message,
                submittedByPlatformUserId: view.AccessLink.AccountActivatedUserId);
            db.LienOffers.Add(offer);
            if (offer.SubmittedByPlatformUserId is { } submittingUserId)
            {
                EnqueueInbox(
                    db,
                    view.AccessLink.TenantId,
                    submittingUserId,
                    NotificationTaxonomy.Liens.Events.OfferSubmitted,
                    "lien",
                    "Offer Submitted",
                    $"Your offer for lien {ResolveLienCode(view.Lien)} was submitted.",
                    offer.OfferedAtUtc,
                    $"selling:offer:{offer.Id:N}:submitted:{submittingUserId:N}");
            }
            view.AccessLink.MarkAccessed();
            await db.SaveChangesAsync(ct);
            var completed = await SellingIdempotency.CompleteAsync(db, started.Record!, view.AccessLink.BuyerContactId, StatusCodes.Status201Created, new
            {
                offer.Id,
                offer.LienId,
                offer.OfferAmount,
                offer.Status,
                offer.OfferedAtUtc,
            }, ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return completed;
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private static async Task<IResult> ActivateBuyerAccount(
        string token,
        PublicBuyerActivateAccountRequest? request,
        IPublicBuyerAccountProvisioningService provisioningService,
        HttpResponse response,
        ISellerOrganizationDisplayResolver sellerDisplayResolver,
        LiensDbContext db,
        CancellationToken ct = default)
    {
        SetNoReferrerHeader(response);
        if (request is null)
        {
            return PublicLinkState(
                "invalid-activation-request",
                "Account activation failed",
                "The account activation request is missing.",
                StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(request.Password))
        {
            return PublicLinkState(
                "password-required",
                "Account activation failed",
                "Password is required.",
                StatusCodes.Status400BadRequest);
        }

        var resolved = await ResolvePublicAccessLinkAsync(token, db, ct);
        if (resolved.Error is not null)
            return resolved.Error;
        if (EnsureBuyerResponseLink(resolved.AccessLink!) is { } readOnlyError)
            return readOnlyError;

        var view = await BuildPublicViewAsync(
            db,
            sellerDisplayResolver,
            resolved.AccessLink!,
            ct,
            requireActionable: false);
        if (view is null)
        {
            return PublicLinkState(
                "unavailable",
                "Lien offer unavailable",
                "The lien offer data could not be resolved.",
                StatusCodes.Status404NotFound);
        }

        var email = FirstNonEmpty(view.BuyerContact?.Email, request.Email);
        if (string.IsNullOrWhiteSpace(email))
        {
            return PublicLinkState(
                "buyer-email-required",
                "Account activation failed",
                "This lien offer does not have a buyer email address.",
                StatusCodes.Status409Conflict);
        }

        var buyerCompanyName = FirstNonEmpty(view.BuyerContact?.Organization, request.CompanyName);
        if (string.IsNullOrWhiteSpace(buyerCompanyName))
        {
            return PublicLinkState(
                "buyer-company-required",
                "Account activation failed",
                "Company name is required to activate a buyer account.",
                StatusCodes.Status400BadRequest);
        }

        var nameParts = SplitName(view.BuyerContact?.DisplayName);
        var firstName = FirstNonEmpty(view.BuyerContact?.FirstName, nameParts.FirstName, request.FirstName);
        if (string.IsNullOrWhiteSpace(firstName))
        {
            return PublicLinkState(
                "first-name-required",
                "Account activation failed",
                "First name is required.",
                StatusCodes.Status400BadRequest);
        }

        var result = await provisioningService.ProvisionBuyerAccountAsync(
            new PublicBuyerAccountProvisioningRequest(
                view.AccessLink.TenantId,
                view.AccessLink.BuyerOrgId,
                buyerCompanyName,
                email,
                request.Password.Trim(),
                firstName,
                FirstNonEmpty(view.BuyerContact?.LastName, nameParts.LastName, request.LastName),
                NormalizePhoneForIdentity(FirstNonEmpty(view.BuyerContact?.Phone, request.Phone))),
            ct);

        if (!result.Success)
        {
            return PublicLinkState(
                FirstNonEmpty(result.ErrorCode, "activation-failed")!,
                "Account activation failed",
                FirstNonEmpty(result.ErrorMessage, "Account activation could not be completed.")!,
                result.StatusCode.GetValueOrDefault(StatusCodes.Status503ServiceUnavailable));
        }

        view.AccessLink.RecordAccountActivation(result.UserId!.Value, email);
        view.AccessLink.MarkAccessed();
        await db.SaveChangesAsync(ct);

        return Results.Ok(new PublicBuyerAccountActivationResponse(
            result.UserId!.Value,
            result.IsNew,
            BuildSynqLienBuyerLoginUrl(view.AccessLink.TenantId)));
    }

    private static async Task<(SellingBuyerAccessLink? AccessLink, IResult? Error)> ResolvePublicAccessLinkAsync(
        string token,
        LiensDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return (null, PublicLinkState(
                "missing-token",
                "Lien offer link unavailable",
                "The secure link is missing from this request.",
                StatusCodes.Status404NotFound));
        }

        var tokenHash = SellingBuyerAccessLink.ComputeTokenHash(token);
        var accessLink = await db.SellingBuyerAccessLinks
            .FirstOrDefaultAsync(link => link.TokenHash == tokenHash, ct);

        if (accessLink is null)
        {
            return (null, PublicLinkState(
                "not-found",
                "Lien offer link unavailable",
                "The secure link could not be found.",
                StatusCodes.Status404NotFound));
        }

        if (accessLink.RevokedAtUtc.HasValue)
        {
            return (null, PublicLinkState(
                "revoked",
                "Lien offer link revoked",
                "This secure link is no longer active.",
                StatusCodes.Status410Gone));
        }

        if (accessLink.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return (null, PublicLinkState(
                "expired",
                "Lien offer link expired",
                "This secure link has expired.",
                StatusCodes.Status410Gone));
        }

        if (!IsSupportedPublicPurpose(accessLink.Purpose))
        {
            return (null, PublicLinkState(
                "not-found",
                "Lien offer link unavailable",
                "The secure link could not be found.",
                StatusCodes.Status404NotFound));
        }

        return (accessLink, null);
    }

    private static IResult? EnsureBuyerResponseLink(SellingBuyerAccessLink accessLink)
        => IsBuyerResponseLink(accessLink)
            ? null
            : PublicLinkState(
                "read-only-link",
                "Lien offer is read-only",
                "This secure link is for viewing lien details and cannot record buyer responses.",
                StatusCodes.Status403Forbidden);

    private static async Task<IResult?> HandleExistingPublicResponseAsync(
        SellingBuyerAccessLink accessLink,
        string requestedStatus,
        INotificationPublisher notifications,
        ILoggerFactory loggerFactory,
        ISellerOrganizationDisplayResolver sellerDisplayResolver,
        LiensDbContext db,
        CancellationToken ct,
        string? currentBuyerAccountEmail = null)
    {
        if (string.IsNullOrWhiteSpace(accessLink.ResponseStatus))
            return null;

        if (!string.Equals(accessLink.ResponseStatus, requestedStatus, StringComparison.Ordinal))
        {
            return PublicLinkState(
                "response-conflict",
                "Lien response already recorded",
                "A different response has already been securely recorded for this lien offer.",
                StatusCodes.Status409Conflict);
        }

        var view = await BuildPublicViewAsync(
            db,
            sellerDisplayResolver,
            accessLink,
            ct,
            requireActionable: false,
            currentBuyerAccountEmail: currentBuyerAccountEmail);
        if (view is null)
        {
            return PublicLinkState(
                "unavailable",
                "Lien offer unavailable",
                "The lien offer data could not be resolved.",
                StatusCodes.Status404NotFound);
        }

        await SendPublicResponseNotificationsAsync(
            notifications,
            loggerFactory,
            view,
            requestedStatus,
            view.AccessLink.ResponseNotes,
            ct);

        return Results.Ok(MapPublicPortalResponse(view));
    }

    private static async Task<IResult> RecordPublicResponseAsync(
        LiensDbContext db,
        ISellerOrganizationDisplayResolver sellerDisplayResolver,
        PublicPortalView view,
        string responseStatus,
        decimal? responseAmount,
        string? responseNotes,
        INotificationPublisher notifications,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(view.AccessLink.ResponseStatus))
        {
            if (string.Equals(view.AccessLink.ResponseStatus, responseStatus, StringComparison.Ordinal))
            {
                await ApplyPublicResponseToLienAsync(db, view, responseStatus, ct);
                await db.SaveChangesAsync(ct);

                var reconciledView = await BuildPublicViewAsync(db, sellerDisplayResolver, view.AccessLink, ct) ?? view;
                await SendPublicResponseNotificationsAsync(
                    notifications,
                    loggerFactory,
                    reconciledView,
                    responseStatus,
                    reconciledView.AccessLink.ResponseNotes,
                    ct);

                return Results.Ok(MapPublicPortalResponse(reconciledView));
            }

            return PublicLinkState(
                "response-conflict",
                "Lien response already recorded",
                "A different response has already been securely recorded for this lien offer.",
                StatusCodes.Status409Conflict);
        }

        if (!IsActionableBuyerOffer(view.Lien.Status, view.Lien.SellerStatus))
        {
            return PublicLinkState(
                "not-actionable",
                "Lien offer unavailable",
                "This lien is no longer accepting buyer responses.",
                StatusCodes.Status409Conflict);
        }

        view.AccessLink.MarkAccessed();
        view.AccessLink.RecordResponse(
            responseStatus,
            responseAmount,
            responseNotes);

        await ApplyPublicResponseToLienAsync(db, view, responseStatus, ct);
        await db.SaveChangesAsync(ct);

        var updatedView = await BuildPublicViewAsync(db, sellerDisplayResolver, view.AccessLink, ct) ?? view;
        await SendPublicResponseNotificationsAsync(
            notifications,
            loggerFactory,
            updatedView,
            responseStatus,
            responseNotes,
            ct);

        return Results.Ok(MapPublicPortalResponse(updatedView));
    }

    private static async Task SendPublicResponseNotificationsAsync(
        INotificationPublisher notifications,
        ILoggerFactory loggerFactory,
        PublicPortalView view,
        string responseStatus,
        string? responseNotes,
        CancellationToken ct)
    {
        var eventKey = string.Equals(responseStatus, SellingBuyerResponseStatus.Accepted, StringComparison.Ordinal)
            ? NotificationTaxonomy.Liens.Events.OfferAccepted
            : NotificationTaxonomy.Liens.Events.OfferRejected;
        var statusLabel = string.Equals(responseStatus, SellingBuyerResponseStatus.Accepted, StringComparison.Ordinal)
            ? "Accepted"
            : "Declined";
        var responseVerb = string.Equals(responseStatus, SellingBuyerResponseStatus.Accepted, StringComparison.Ordinal)
            ? "accepted"
            : "declined";
        var lienCode = ResolveLienCode(view.Lien);
        var subject = $"Lien Offer {statusLabel}";
        var respondedAtUtc = view.AccessLink.RespondedAtUtc?.ToString("O", CultureInfo.InvariantCulture)
                             ?? DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        var buyerName = FirstNonEmpty(view.BuyerContact?.DisplayName, view.BuyerContact?.Email, "Buyer")!;
        var buyerCompany = FirstNonEmpty(view.BuyerContact?.Organization, "Funding company")!;
        var sellerName = FirstNonEmpty(view.SellerDisplay.Name, view.SellerDisplay.Company, "Seller")!;
        var sellerCompany = FirstNonEmpty(view.SellerDisplay.Company, view.SellerDisplay.Name, "Seller company")!;

        var commonMetadata = new Dictionary<string, string>
        {
            ["tenantId"] = view.AccessLink.TenantId.ToString(),
            ["lienId"] = view.AccessLink.LienId.ToString(),
            ["lienCode"] = lienCode,
            ["buyerContactId"] = view.AccessLink.BuyerContactId.ToString(),
            ["buyerOrgId"] = view.AccessLink.BuyerOrgId.ToString(),
            ["sellerOrgId"] = view.AccessLink.SellerOrgId.ToString(),
            ["buyerAccessLinkId"] = view.AccessLink.Id.ToString(),
            ["responseStatus"] = responseStatus,
            ["respondedAtUtc"] = respondedAtUtc,
        };

        await SendPublicResponseNotificationAsync(
            notifications,
            loggerFactory,
            eventKey,
            view.AccessLink.TenantId,
            FirstNonEmpty(view.BuyerAccountEmail, view.BuyerContact?.Email),
            subject,
            BuildPublicResponseEmailBody(
                recipientRole: "buyer",
                responseVerb,
                lienCode,
                buyerName,
                buyerCompany,
                sellerCompany,
                responseNotes),
            BuildPublicResponseEmailHtmlBody(
                recipientRole: "buyer",
                statusLabel,
                responseVerb,
                lienCode,
                buyerName,
                buyerCompany,
                sellerCompany,
                responseNotes),
            commonMetadata,
            recipientRole: "buyer",
            idempotencyKey: BuildPublicResponseNotificationIdempotencyKey(view.AccessLink, responseStatus, "buyer"),
            requestedBy: ResolvePublicResponseActorId(view.AccessLink).ToString(),
            ct: ct);

        await SendPublicResponseNotificationAsync(
            notifications,
            loggerFactory,
            eventKey,
            view.AccessLink.TenantId,
            FirstNonEmpty(view.SellerDisplay.Email),
            subject,
            BuildPublicResponseEmailBody(
                recipientRole: "seller",
                responseVerb,
                lienCode,
                buyerName,
                buyerCompany,
                sellerCompany,
                responseNotes),
            BuildPublicResponseEmailHtmlBody(
                recipientRole: "seller",
                statusLabel,
                responseVerb,
                lienCode,
                buyerName,
                buyerCompany,
                sellerCompany,
                responseNotes),
            commonMetadata,
            recipientRole: "seller",
            idempotencyKey: BuildPublicResponseNotificationIdempotencyKey(view.AccessLink, responseStatus, "seller"),
            requestedBy: ResolvePublicResponseActorId(view.AccessLink).ToString(),
            ct: ct);
    }

    private static async Task SendPublicResponseNotificationAsync(
        INotificationPublisher notifications,
        ILoggerFactory loggerFactory,
        string eventKey,
        Guid tenantId,
        string? recipientEmail,
        string subject,
        string body,
        string htmlBody,
        IReadOnlyDictionary<string, string> commonMetadata,
        string recipientRole,
        string idempotencyKey,
        string requestedBy,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(recipientEmail))
            return;

        var metadata = new Dictionary<string, string>(commonMetadata)
        {
            ["recipientRole"] = recipientRole,
        };

        try
        {
            var result = await notifications.SendEmailAsync(
                eventKey,
                tenantId,
                recipientEmail.Trim(),
                subject,
                body,
                metadata,
                ct,
                new NotificationEmailSendOptions(
                    IdempotencyKey: idempotencyKey,
                    RequestedBy: requestedBy,
                    HtmlBody: htmlBody,
                    TextBody: body,
                    InlineAttachments: PublicResponseInlineAttachments));

            if (!IsNotificationSubmittedStatus(result.Status) || result.BlockedByPolicy || !string.IsNullOrWhiteSpace(result.FailureCategory))
            {
                loggerFactory
                    .CreateLogger("Liens.Api.Endpoints.SellingPublicEndpoints")
                    .LogWarning(
                        "Public lien response notification was not submitted: Tenant={TenantId} Event={EventKey} Role={RecipientRole} Status={Status} FailureCategory={FailureCategory}",
                        tenantId,
                        eventKey,
                        recipientRole,
                        result.Status,
                        result.FailureCategory);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loggerFactory
                .CreateLogger("Liens.Api.Endpoints.SellingPublicEndpoints")
                .LogWarning(
                    ex,
                    "Public lien response notification failed: Tenant={TenantId} Event={EventKey} Role={RecipientRole}",
                    tenantId,
                    eventKey,
                    recipientRole);
        }
    }

    private static async Task SendPublicMessageNotificationAsync(
        INotificationPublisher notifications,
        ILoggerFactory loggerFactory,
        IConfiguration configuration,
        HttpContext httpContext,
        PublicPortalView view,
        SellingPortalMessage message,
        ISellingBuyerAccessLinkService accessLinks,
        string? currentToken,
        CancellationToken ct)
    {
        var recipientRole = message.SenderType == SellingPortalMessageSenderType.Buyer
            ? SellingPortalMessageSenderType.Seller
            : SellingPortalMessageSenderType.Buyer;
        var recipient = ResolvePublicMessageRecipient(view, recipientRole);
        if (string.IsNullOrWhiteSpace(recipient.Email))
        {
            loggerFactory
                .CreateLogger("Liens.Api.Endpoints.SellingPublicEndpoints")
                .LogWarning(
                    "Public lien message notification skipped because recipient email is missing: Tenant={TenantId} Lien={LienId} MessageId={MessageId} Role={RecipientRole} RecipientName={RecipientName}",
                    view.AccessLink.TenantId,
                    view.AccessLink.LienId,
                    message.Id,
                    recipientRole,
                    recipient.Name);
            return;
        }

        string? portalUrl = null;
        try
        {
            portalUrl = await BuildPublicMessageRecipientPortalUrlAsync(
                accessLinks,
                view.AccessLink,
                recipientRole,
                message,
                ct);
            if (string.IsNullOrWhiteSpace(portalUrl) &&
                !string.IsNullOrWhiteSpace(currentToken) &&
                string.Equals(ResolvePublicAudience(view.AccessLink), recipientRole, StringComparison.Ordinal))
            {
                portalUrl = BuildPublicPortalUrl(configuration, httpContext, currentToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loggerFactory
                .CreateLogger("Liens.Api.Endpoints.SellingPublicEndpoints")
                .LogWarning(
                    ex,
                    "Public lien message notification link creation failed: Tenant={TenantId} MessageId={MessageId} Role={RecipientRole}",
                    view.AccessLink.TenantId,
                    message.Id,
                    recipientRole);
        }

        var lienCode = ResolveLienCode(view.Lien);
        var subject = "New message on lien offer";
        var body = BuildPublicMessageEmailBody(message, lienCode, portalUrl);
        var htmlBody = BuildPublicMessageEmailHtmlBody(message, lienCode, portalUrl);
        var messageSentAt = FormatPublicMessageEmailTimestamp(message.CreatedAtUtc);
        var metadata = new Dictionary<string, string>
        {
            ["tenantId"] = view.AccessLink.TenantId.ToString(),
            ["lienId"] = view.AccessLink.LienId.ToString(),
            ["lienCode"] = lienCode,
            ["buyerContactId"] = view.AccessLink.BuyerContactId.ToString(),
            ["buyerOrgId"] = view.AccessLink.BuyerOrgId.ToString(),
            ["sellerOrgId"] = view.AccessLink.SellerOrgId.ToString(),
            ["accessLinkId"] = view.AccessLink.Id.ToString(),
            ["messageId"] = message.Id.ToString(),
            ["messageCreatedAtUtc"] = DateTime.SpecifyKind(message.CreatedAtUtc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture),
            ["messageSentAt"] = messageSentAt,
            ["senderType"] = message.SenderType,
            ["recipientRole"] = recipientRole,
        };

        try
        {
            var result = await notifications.SendEmailAsync(
                NotificationTaxonomy.Liens.Events.OfferMessageCreated,
                view.AccessLink.TenantId,
                recipient.Email.Trim(),
                subject,
                body,
                metadata,
                ct,
                new NotificationEmailSendOptions(
                    IdempotencyKey: BuildPublicMessageNotificationIdempotencyKey(message, recipientRole),
                    RequestedBy: ResolvePublicMessageActorId(view.AccessLink, message.SenderType).ToString(),
                    HtmlBody: htmlBody,
                    TextBody: body,
                    InlineAttachments: PublicResponseInlineAttachments,
                    DisableClickTracking: true));

            if (!IsNotificationSubmittedStatus(result.Status) || result.BlockedByPolicy || !string.IsNullOrWhiteSpace(result.FailureCategory))
            {
                loggerFactory
                    .CreateLogger("Liens.Api.Endpoints.SellingPublicEndpoints")
                    .LogWarning(
                        "Public lien message notification was not submitted: Tenant={TenantId} MessageId={MessageId} Role={RecipientRole} Status={Status} FailureCategory={FailureCategory}",
                        view.AccessLink.TenantId,
                        message.Id,
                        recipientRole,
                        result.Status,
                        result.FailureCategory);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loggerFactory
                .CreateLogger("Liens.Api.Endpoints.SellingPublicEndpoints")
                .LogWarning(
                    ex,
                    "Public lien message notification failed: Tenant={TenantId} MessageId={MessageId} Role={RecipientRole}",
                    view.AccessLink.TenantId,
                    message.Id,
                    recipientRole);
        }
    }

    private static Task<SellingBuyerAccessLinkResult> BuildPublicMessageRecipientAccessLinkAsync(
        ISellingBuyerAccessLinkService accessLinks,
        SellingBuyerAccessLink currentAccessLink,
        string recipientRole,
        SellingPortalMessage message,
        CancellationToken ct)
    {
        var actingUserId = currentAccessLink.CreatedByUserId.GetValueOrDefault(currentAccessLink.BuyerContactId);
        var idempotencyKey = BuildPublicMessageRecipientAccessLinkIdempotencyKey(message, recipientRole);

        return recipientRole == SellingPortalMessageSenderType.Seller
            ? accessLinks.CreateOrGetForConfirmSaleSellerViewAsync(
                currentAccessLink.TenantId,
                currentAccessLink.LienId,
                currentAccessLink.SellerOrgId,
                currentAccessLink.BuyerOrgId,
                currentAccessLink.BuyerContactId,
                currentAccessLink.BuyerCompanyId,
                currentAccessLink.BuyerCompanyContactPersonId,
                actingUserId,
                idempotencyKey,
                TimeSpan.FromDays(30),
                ct)
            : accessLinks.CreateOrGetForConfirmSaleAsync(
                currentAccessLink.TenantId,
                currentAccessLink.LienId,
                currentAccessLink.SellerOrgId,
                currentAccessLink.BuyerOrgId,
                currentAccessLink.BuyerContactId,
                currentAccessLink.BuyerCompanyId,
                currentAccessLink.BuyerCompanyContactPersonId,
                actingUserId,
                idempotencyKey,
                TimeSpan.FromDays(30),
                ct);
    }

    private static async Task<string?> BuildPublicMessageRecipientPortalUrlAsync(
        ISellingBuyerAccessLinkService accessLinks,
        SellingBuyerAccessLink currentAccessLink,
        string recipientRole,
        SellingPortalMessage message,
        CancellationToken ct)
    {
        var recipientAccessLink = await BuildPublicMessageRecipientAccessLinkAsync(
            accessLinks,
            currentAccessLink,
            recipientRole,
            message,
            ct);

        return string.IsNullOrWhiteSpace(recipientAccessLink.Token)
            ? null
            : recipientAccessLink.PublicPortalUrl;
    }

    private static (string Name, string? Email) ResolvePublicMessageSender(
        PublicPortalView view,
        string senderType)
        => senderType == SellingPortalMessageSenderType.Seller
            ? (
                FirstNonEmpty(view.SellerDisplay.Name, view.SellerDisplay.Company, "Seller")!,
                FirstNonEmpty(view.SellerDisplay.Email))
            : (
                FirstNonEmpty(view.BuyerContact?.DisplayName, view.BuyerContact?.Organization, view.BuyerAccountEmail, "Buyer")!,
                FirstNonEmpty(view.BuyerAccountEmail));

    private static (string Name, string? Email) ResolvePublicMessageRecipient(
        PublicPortalView view,
        string recipientRole)
        => recipientRole == SellingPortalMessageSenderType.Seller
            ? (
                FirstNonEmpty(view.SellerDisplay.Name, view.SellerDisplay.Company, "Seller")!,
                FirstNonEmpty(view.SellerDisplay.Email))
            : (
                FirstNonEmpty(view.BuyerContact?.DisplayName, view.BuyerContact?.Organization, view.BuyerAccountEmail, "Buyer")!,
                FirstNonEmpty(
                    view.BuyerAccountEmail,
                    ResolveLatestBuyerMessageSenderEmail(view.Messages),
                    view.BuyerContact?.Email));

    private static string? ResolveLatestBuyerMessageSenderEmail(IReadOnlyList<SellingPortalMessage> messages)
        => messages
            .Where(message =>
                string.Equals(message.SenderType, SellingPortalMessageSenderType.Buyer, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(message.SenderEmail))
            .OrderByDescending(message => message.CreatedAtUtc)
            .ThenByDescending(message => message.Id)
            .Select(message => message.SenderEmail)
            .FirstOrDefault();

    private static Guid ResolvePublicMessageActorId(SellingBuyerAccessLink accessLink, string senderType)
        => senderType == SellingPortalMessageSenderType.Buyer
            ? accessLink.BuyerContactId
            : accessLink.CreatedByUserId.GetValueOrDefault(accessLink.BuyerContactId);

    private static string BuildPublicMessageEmailBody(
        SellingPortalMessage message,
        string lienCode,
        string? portalUrl)
    {
        var messageSentAt = FormatPublicMessageEmailTimestamp(message.CreatedAtUtc);
        var body = new List<string>
        {
            "LegalSynq",
            "New message on lien offer",
            string.Empty,
            $"{message.SenderName} sent a message regarding lien offer {lienCode}.",
            $"Message sent: {messageSentAt}",
            string.Empty,
            message.Message,
        };

        if (!string.IsNullOrWhiteSpace(portalUrl))
        {
            body.Add(string.Empty);
            body.Add($"View Lien: {portalUrl}");
        }

        return string.Join(Environment.NewLine, body);
    }

    private static string BuildPublicMessageEmailHtmlBody(
        SellingPortalMessage message,
        string lienCode,
        string? portalUrl)
    {
        var messageSentAt = FormatPublicMessageEmailTimestamp(message.CreatedAtUtc);
        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"en\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.AppendLine("<title>New message on lien offer</title>");
        html.AppendLine("</head>");
        html.AppendLine("<body style=\"margin:0;padding:0;background-color:#f4f5f7;color:#111827;font-family:Arial,Helvetica,sans-serif;\">");
        html.AppendLine("<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" bgcolor=\"#f4f5f7\" style=\"width:100%;border-collapse:collapse;background-color:#f4f5f7;\">");
        html.AppendLine("<tr><td align=\"center\" style=\"padding:28px 14px;\">");
        html.AppendLine("<table role=\"presentation\" width=\"560\" cellspacing=\"0\" cellpadding=\"0\" bgcolor=\"#ffffff\" style=\"width:100%;max-width:560px;border-collapse:separate;border-spacing:0;background-color:#ffffff;border-radius:10px;overflow:hidden;\">");
        html.AppendLine("<tr><td bgcolor=\"#071b31\" style=\"background-color:#071b31;padding:28px 30px;\">");
        AppendPublicResponseEmailBrand(html);
        html.AppendLine("<h1 style=\"margin:24px 0 10px 0;color:#ffffff;font-size:24px;line-height:1.25;font-weight:700;letter-spacing:0;\">New message on lien offer</h1>");
        html.Append("<p style=\"margin:0;color:#ffffff;font-size:16px;line-height:1.55;font-weight:400;opacity:.92;\">")
            .Append(Html(message.SenderName))
            .Append(" sent a message regarding lien offer ")
            .Append(Html(lienCode))
            .AppendLine(".</p>");
        html.Append("<p style=\"margin:10px 0 0 0;color:#ffffff;font-size:14px;line-height:1.45;font-weight:400;opacity:.82;\">Message sent: ")
            .Append(Html(messageSentAt))
            .AppendLine("</p>");
        html.AppendLine("</td></tr>");
        html.AppendLine("<tr><td bgcolor=\"#ffffff\" style=\"background-color:#ffffff;color:#111827;border:1px solid #e5e5e5;border-top:0;border-radius:0 0 10px 10px;padding:24px 24px 28px;\">");
        html.Append("<p style=\"margin:0 0 20px 0;color:#111827;font-size:15px;line-height:1.6;white-space:pre-wrap;\">")
            .Append(Html(message.Message))
            .AppendLine("</p>");
        if (!string.IsNullOrWhiteSpace(portalUrl))
        {
            html.Append("<a href=\"")
                .Append(Html(portalUrl))
                .AppendLine("\" style=\"display:inline-block;background:#ee7132;color:#ffffff;padding:12px 22px;border-radius:8px;text-decoration:none;font-weight:700;font-size:14px;line-height:1.2;\">View Lien</a>");
        }
        html.AppendLine("</td></tr>");
        html.AppendLine("</table>");
        html.AppendLine("</td></tr>");
        html.AppendLine("</table>");
        html.AppendLine("</body></html>");

        return html.ToString();
    }

    private static string FormatPublicMessageEmailTimestamp(DateTime createdAtUtc)
        => $"{PacificTimeHelper.FormatTimestamp(createdAtUtc)} PT";

    private static string BuildPublicMessageNotificationIdempotencyKey(
        SellingPortalMessage message,
        string recipientRole)
    {
        var key = string.Join(":", new[]
        {
            "liens.public-message.email",
            message.TenantId.ToString("N"),
            message.Id.ToString("N"),
            recipientRole.Trim().ToLowerInvariant(),
        });

        return key.Length > 280 ? key[..280] : key;
    }

    private static string BuildPublicMessageRecipientAccessLinkIdempotencyKey(
        SellingPortalMessage message,
        string recipientRole)
    {
        var key = string.Join(":", new[]
        {
            "liens.public-message.access-link",
            message.TenantId.ToString("N"),
            message.Id.ToString("N"),
            recipientRole.Trim().ToLowerInvariant(),
        });

        return key.Length > 280 ? key[..280] : key;
    }

    private static string? BuildPublicPortalUrl(
        IConfiguration configuration,
        HttpContext httpContext,
        string token)
    {
        var baseUrl = ResolveConfiguredBuyerPortalBaseUrl(configuration);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            var host = httpContext.Request.Headers["x-legal-synq-public-host"].FirstOrDefault()
                       ?? httpContext.Request.Host.Value;
            if (string.IsNullOrWhiteSpace(host))
                return null;

            var proto = httpContext.Request.Headers["x-legal-synq-public-proto"].FirstOrDefault()
                        ?? httpContext.Request.Headers["x-forwarded-proto"].FirstOrDefault()
                        ?? (httpContext.Request.IsHttps ? Uri.UriSchemeHttps : Uri.UriSchemeHttp);
            baseUrl = $"{proto.Replace(":", string.Empty, StringComparison.Ordinal)}://{host}/selling/public";
        }

        return BuildPublicPortalUrl(baseUrl, token);
    }

    private static string? ResolveConfiguredBuyerPortalBaseUrl(IConfiguration configuration)
    {
        var value = configuration["Liens:Selling:BuyerPortalBaseUrl"]?.Trim();
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        var portalHostname = configuration["SYNQLIEN_COMMON_PORTAL_HOSTNAME"]?.Trim();
        if (string.IsNullOrWhiteSpace(portalHostname))
            return null;

        var scheme = portalHostname.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            ? Uri.UriSchemeHttp
            : Uri.UriSchemeHttps;
        var port = portalHostname.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            ? ":5000"
            : string.Empty;
        return $"{scheme}://{portalHostname.TrimEnd('/')}{port}/selling/public";
    }

    private static string BuildPublicPortalUrl(string portalBaseUrl, string token)
    {
        if (portalBaseUrl.Contains("{token}", StringComparison.Ordinal))
            return portalBaseUrl.Replace("{token}", Uri.EscapeDataString(token), StringComparison.Ordinal);

        return $"{portalBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(token)}";
    }

    private static string BuildPublicResponseEmailBody(
        string recipientRole,
        string responseVerb,
        string lienCode,
        string buyerName,
        string buyerCompany,
        string sellerCompany,
        string? responseNotes)
    {
        var body = new List<string>
        {
            "LegalSynq",
            $"Lien Offer {CultureInfo.InvariantCulture.TextInfo.ToTitleCase(responseVerb)}",
            string.Empty,
            recipientRole == "buyer"
                ? $"This confirms that your company, {buyerCompany}, {responseVerb} lien offer {lienCode}."
                : $"{buyerName} from {buyerCompany} {responseVerb} lien offer {lienCode}.",
            $"Seller: {sellerCompany}",
        };

        if (!string.IsNullOrWhiteSpace(responseNotes))
            body.Add($"Response notes: {responseNotes.Trim()}");

        return string.Join(Environment.NewLine, body);
    }

    private static string BuildPublicResponseEmailHtmlBody(
        string recipientRole,
        string statusLabel,
        string responseVerb,
        string lienCode,
        string buyerName,
        string buyerCompany,
        string sellerCompany,
        string? responseNotes)
    {
        var title = $"Lien Offer {statusLabel}";
        var isAccepted = string.Equals(statusLabel, "Accepted", StringComparison.Ordinal);
        var badgeBackground = isAccepted ? "#d1fae5" : "#fee2e2";
        var badgeColor = isAccepted ? "#047857" : "#b91c1c";
        var summary = recipientRole == "buyer"
            ? $"This confirms that your company, {buyerCompany}, {responseVerb} lien offer {lienCode}."
            : $"{buyerName} from {buyerCompany} {responseVerb} lien offer {lienCode}.";

        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"en\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.AppendLine("<title>").Append(Html(title)).AppendLine("</title>");
        html.AppendLine("</head>");
        html.AppendLine("<body style=\"margin:0;padding:0;background-color:#f4f5f7;color:#111827;font-family:Arial,Helvetica,sans-serif;\">");
        html.AppendLine("<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" bgcolor=\"#f4f5f7\" style=\"width:100%;border-collapse:collapse;background-color:#f4f5f7;\">");
        html.AppendLine("<tr><td align=\"center\" style=\"padding:28px 14px;\">");
        html.AppendLine("<table role=\"presentation\" width=\"560\" cellspacing=\"0\" cellpadding=\"0\" bgcolor=\"#ffffff\" style=\"width:100%;max-width:560px;border-collapse:separate;border-spacing:0;background-color:#ffffff;border-radius:10px;overflow:hidden;\">");
        html.AppendLine("<tr><td bgcolor=\"#071b31\" style=\"background-color:#071b31;padding:28px 30px;\">");
        html.AppendLine("<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" style=\"width:100%;border-collapse:collapse;margin:0 0 28px 0;\"><tr>");
        html.AppendLine("<td align=\"left\" style=\"vertical-align:middle;padding:0;\">");
        AppendPublicResponseEmailBrand(html);
        html.AppendLine("</td>");
        html.Append("<td align=\"right\" style=\"vertical-align:middle;padding:0;\"><span style=\"display:inline-block;background-color:")
            .Append(badgeBackground)
            .Append(";color:")
            .Append(badgeColor)
            .Append(";border-radius:999px;padding:6px 12px;font-size:12px;font-weight:700;line-height:1.1;white-space:nowrap;\">")
            .Append(Html(statusLabel))
            .AppendLine("</span></td>");
        html.AppendLine("</tr></table>");
        html.Append("<h1 style=\"margin:0 0 10px 0;color:#ffffff;font-size:24px;line-height:1.25;font-weight:700;letter-spacing:0;\">")
            .Append(Html(title))
            .AppendLine("</h1>");
        html.Append("<p style=\"margin:0;color:#ffffff;font-size:16px;line-height:1.55;font-weight:400;opacity:.92;\">")
            .Append(Html(summary))
            .AppendLine("</p>");
        html.AppendLine("</td></tr>");
        html.AppendLine("<tr><td bgcolor=\"#ffffff\" style=\"background-color:#ffffff;color:#111827;border:1px solid #e5e5e5;border-top:0;border-radius:0 0 10px 10px;padding:24px 24px 28px;\">");
        html.AppendLine("<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" style=\"border-collapse:separate;border-spacing:0;margin:0 0 20px 0;\">");
        AppendPublicResponseEmailRow(html, "Lien Number", lienCode, isFirstRow: true, isLastRow: false);
        AppendPublicResponseEmailRow(html, "Buyer", buyerName, isFirstRow: false, isLastRow: false);
        AppendPublicResponseEmailRow(html, "Funding Company", buyerCompany, isFirstRow: false, isLastRow: false);
        AppendPublicResponseEmailRow(html, "Seller", sellerCompany, isFirstRow: false, isLastRow: string.IsNullOrWhiteSpace(responseNotes));
        if (!string.IsNullOrWhiteSpace(responseNotes))
            AppendPublicResponseEmailRow(html, "Response Notes", responseNotes.Trim(), isFirstRow: false, isLastRow: true);
        html.AppendLine("</table>");
        html.AppendLine("</td></tr>");
        html.AppendLine("</table>");
        html.AppendLine("</td></tr>");
        html.AppendLine("</table>");
        html.AppendLine("</body></html>");

        return html.ToString();
    }

    private static void AppendPublicResponseEmailBrand(StringBuilder html)
    {
        html.Append("<table role=\"presentation\" cellspacing=\"0\" cellpadding=\"0\" aria-label=\"LegalSynq\" style=\"border-collapse:collapse;\"><tr><td width=\"36\" style=\"width:36px;padding:0 6px 0 0;vertical-align:middle;\"><img src=\"cid:")
            .Append(LegalSynqBrandIconContentId)
            .AppendLine("\" width=\"36\" height=\"36\" alt=\"\" role=\"presentation\" style=\"display:block;width:36px;height:36px;border:0;outline:none;text-decoration:none;\"></td><td style=\"padding:0;vertical-align:middle;white-space:nowrap;\"><span style=\"color:#ffffff !important;-webkit-text-fill-color:#ffffff;font-size:22px;line-height:1;font-weight:700;letter-spacing:0;\">Legal</span><span style=\"color:#f26a2e !important;-webkit-text-fill-color:#f26a2e;font-size:22px;line-height:1;font-weight:700;letter-spacing:0;\">Synq</span></td></tr></table>");
    }

    private static void AppendPublicResponseEmailRow(
        StringBuilder html,
        string label,
        string value,
        bool isFirstRow,
        bool isLastRow)
    {
        var border = isFirstRow ? "border-top:1px solid #e5e5e5;" : string.Empty;
        var radiusLeft = isFirstRow ? "border-top-left-radius:10px;" : isLastRow ? "border-bottom-left-radius:10px;" : string.Empty;
        var radiusRight = isFirstRow ? "border-top-right-radius:10px;" : isLastRow ? "border-bottom-right-radius:10px;" : string.Empty;

        html.Append("<tr><td style=\"width:42%;padding:14px 14px;color:#6f6f6f;font-size:13px;line-height:1.35;border-left:1px solid #e5e5e5;border-bottom:1px solid #e5e5e5;")
            .Append(border)
            .Append(radiusLeft)
            .Append("\">")
            .Append(Html(label))
            .Append("</td><td align=\"right\" style=\"padding:14px 14px;color:#111111;font-size:15px;line-height:1.35;font-weight:600;border-right:1px solid #e5e5e5;border-bottom:1px solid #e5e5e5;")
            .Append(border)
            .Append(radiusRight)
            .Append("\">")
            .Append(Html(value))
            .AppendLine("</td></tr>");
    }

    private static async Task ApplyPublicResponseToLienAsync(
        LiensDbContext db,
        PublicPortalView view,
        string responseStatus,
        CancellationToken ct)
    {
        var lien = await db.Liens
            .FirstOrDefaultAsync(l =>
                l.TenantId == view.AccessLink.TenantId &&
                l.Id == view.AccessLink.LienId,
                ct);

        if (lien is null)
            return;

        var updatedByUserId = ResolvePublicResponseActorId(view.AccessLink);
        var previousStatus = ResolveActivityLienStatus(lien);
        if (string.Equals(responseStatus, SellingBuyerResponseStatus.Accepted, StringComparison.Ordinal))
        {
            if (IsActionableLienStatus(lien.Status))
                lien.TransitionStatus(LienStatus.Accepted, updatedByUserId);

            if (IsActionableBuyerOffer(view.Lien.Status, view.Lien.SellerStatus) &&
                !string.Equals(lien.SellerStatus, SellingLienStatus.Accepted, StringComparison.Ordinal))
                lien.UpdateSellingAnalyticsFields(updatedByUserId, sellerStatus: SellingLienStatus.Accepted);

            var acceptedAtUtc = view.AccessLink.RespondedAtUtc ?? DateTime.UtcNow;
            lien.SetPurchaseDate(DateOnly.FromDateTime(acceptedAtUtc), updatedByUserId);
        }
        else if (string.Equals(responseStatus, SellingBuyerResponseStatus.Declined, StringComparison.Ordinal))
        {
            if (IsActionableBuyerOffer(view.Lien.Status, view.Lien.SellerStatus) &&
                (string.Equals(lien.Status, LienStatus.Offered, StringComparison.Ordinal) ||
                 string.Equals(lien.Status, LienStatus.UnderReview, StringComparison.Ordinal)))
                lien.ReturnToSellingPending(updatedByUserId);
        }

        var currentStatus = ResolveActivityLienStatus(lien);
        if (!string.Equals(previousStatus, currentStatus, StringComparison.Ordinal))
        {
            db.ChangeTracker.DetectChanges();
            var changes = db.Entry(lien).Properties
                .Where(property =>
                    property.IsModified &&
                    property.Metadata.Name != nameof(Lien.UpdatedAtUtc) &&
                    property.Metadata.Name != nameof(Lien.UpdatedByUserId))
                .Select(property => new LienFieldChange(
                    LienUpdateHistoryFormatter.DisplayFieldName(property.Metadata.Name),
                    property.OriginalValue,
                    property.CurrentValue))
                .ToList();

            var description = LienUpdateHistoryFormatter.BuildSingleDescription(
                $"Lien Status: {currentStatus}. Buyer response recorded as {responseStatus}",
                changes);
            db.LienStatusHistories.Add(LienStatusHistory.Create(
                lien.TenantId,
                lien.Id,
                lien.CaseId,
                description,
                updatedByUserId));
        }
    }

    private static string ResolveActivityLienStatus(Lien lien)
        => string.IsNullOrWhiteSpace(lien.SellerStatus) ? lien.Status : lien.SellerStatus;

    private static Guid ResolvePublicResponseActorId(SellingBuyerAccessLink accessLink)
        => accessLink.CreatedByUserId.GetValueOrDefault(accessLink.BuyerContactId);

    private static async Task<PublicPortalView?> BuildPublicViewAsync(
        LiensDbContext db,
        ISellerOrganizationDisplayResolver sellerDisplayResolver,
        SellingBuyerAccessLink accessLink,
        CancellationToken ct,
        bool requireActionable = true,
        string? currentBuyerAccountEmail = null)
    {
        var lien = await db.Liens
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.TenantId == accessLink.TenantId && l.Id == accessLink.LienId, ct);

        if (lien is null)
            return null;

        if (requireActionable && !IsActionableLien(lien))
            return null;

        var caseEntity = lien.CaseId.HasValue
            ? await db.Cases
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.TenantId == accessLink.TenantId && c.Id == lien.CaseId.Value, ct)
            : null;

        PublicBuyerContact? buyerContact;
        if (accessLink.BuyerCompanyId.HasValue && accessLink.BuyerCompanyContactPersonId.HasValue)
        {
            var canonicalContact = await db.CompanyContactPersons
                .AsNoTracking()
                .Include(contact => contact.Company)
                .FirstOrDefaultAsync(contact =>
                    contact.TenantId == accessLink.TenantId &&
                    contact.Id == accessLink.BuyerCompanyContactPersonId.Value &&
                    contact.CompanyId == accessLink.BuyerCompanyId.Value &&
                    contact.Company != null &&
                    contact.Company.OrgId == accessLink.SellerOrgId,
                    ct);
            buyerContact = canonicalContact?.Company is null
                ? null
                : new PublicBuyerContact(
                    canonicalContact.Id,
                    canonicalContact.Company.Id,
                    $"{canonicalContact.FirstName} {canonicalContact.LastName}".Trim(),
                    canonicalContact.Company.Name,
                    canonicalContact.Email,
                    canonicalContact.Phone,
                    canonicalContact.FirstName,
                    canonicalContact.LastName);
        }
        else
        {
            var legacyContact = await db.Contacts
                .AsNoTracking()
                .FirstOrDefaultAsync(c =>
                    c.TenantId == accessLink.TenantId &&
                    c.Id == accessLink.BuyerContactId &&
                    c.OrgId == accessLink.BuyerOrgId,
                    ct);
            buyerContact = legacyContact is null
                ? null
                : new PublicBuyerContact(
                    legacyContact.Id,
                    legacyContact.OrgId,
                    legacyContact.DisplayName,
                    legacyContact.Organization,
                    legacyContact.Email,
                    legacyContact.Phone,
                    legacyContact.FirstName,
                    legacyContact.LastName);
        }

        var sellerContacts = await db.CompanyContactPersons
            .AsNoTracking()
            .Include(c => c.Company)
                .ThenInclude(c => c!.CompanyType)
            .Include(c => c.ContactPersonType)
            .Where(c => c.TenantId == accessLink.TenantId &&
                c.Company!.TenantId == accessLink.TenantId &&
                c.Company.OrgId == accessLink.SellerOrgId &&
                c.Company.IsActive &&
                c.IsActive)
            .ToListAsync(ct);

        var sellerContact = SelectSellerContact(sellerContacts);
        var sellerDisplay = await sellerDisplayResolver.ResolveAsync(
            accessLink.TenantId,
            accessLink.SellerOrgId,
            sellerContacts,
            sellerUserId: accessLink.CreatedByUserId,
            fallbackEmail: null,
            includeIdentityOwnerEmailFallback: true,
            ct: ct);
        var caseParties = await ResolvePublicCasePartiesAsync(db, accessLink.TenantId, caseEntity, ct);
        var documents = await ResolveDocumentsAsync(db, accessLink.TenantId, lien, ct);
        var messages = await ResolveMessagesAsync(db, accessLink, ct);
        var messageAttachments = await ResolveMessageAttachmentsAsync(db, accessLink, messages, ct);
        var buyerResponseAccessLink = await ResolveBuyerResponseAccessLinkAsync(db, accessLink, ct);
        var buyerAccountEmail = FirstNonEmpty(currentBuyerAccountEmail, accessLink.AccountActivatedEmail);

        return new PublicPortalView(
            accessLink,
            lien,
            caseEntity,
            buyerContact,
            sellerContact,
            sellerDisplay,
            caseParties,
            documents,
            messages,
            messageAttachments,
            buyerResponseAccessLink,
            buyerAccountEmail);
    }

    private static async Task<PublicBuyerAccountResponse?> ResolvePublicBuyerAccountAsync(
        PublicPortalView view,
        IPublicBuyerAccountProvisioningService buyerAccountService,
        CancellationToken ct)
    {
        if (!IsBuyerResponseLink(view.AccessLink))
            return null;

        if (view.AccessLink.AccountActivatedAtUtc.HasValue)
            return new PublicBuyerAccountResponse(true, BuildSynqLienBuyerLoginUrl(view.AccessLink.TenantId));

        var email = view.BuyerContact?.Email?.Trim();
        if (string.IsNullOrWhiteSpace(email))
            return new PublicBuyerAccountResponse(false, BuildSynqLienBuyerLoginUrl(view.AccessLink.TenantId));

        var status = await buyerAccountService.GetBuyerAccountStatusAsync(
            new PublicBuyerAccountStatusRequest(view.AccessLink.TenantId, email),
            ct);

        return new PublicBuyerAccountResponse(
            status.Success && status.AccountExists,
            BuildSynqLienBuyerLoginUrl(view.AccessLink.TenantId));
    }

    private static string BuildSynqLienBuyerLoginUrl(Guid tenantId)
    {
        var query = new Dictionary<string, string>
        {
            ["returnTo"] = SynqLienBuyerLoginReturnTo,
            ["reason"] = SynqLienBuyerActivationReason,
            ["tenantId"] = tenantId.ToString("D"),
        };

        return "/login?" + string.Join(
            '&',
            query.Select(item =>
                $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"));
    }

    private static async Task<IReadOnlyList<SellingPortalMessage>> ResolveMessagesAsync(
        LiensDbContext db,
        SellingBuyerAccessLink accessLink,
        CancellationToken ct)
        => await db.SellingPortalMessages
            .AsNoTracking()
            .Where(message =>
                message.TenantId == accessLink.TenantId &&
                message.LienId == accessLink.LienId &&
                message.SellerOrgId == accessLink.SellerOrgId &&
                message.BuyerOrgId == accessLink.BuyerOrgId &&
                message.BuyerContactId == accessLink.BuyerContactId)
            .OrderBy(message => message.CreatedAtUtc)
            .ThenBy(message => message.Id)
            .ToListAsync(ct);

    private static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<SellingPortalMessageAttachment>>> ResolveMessageAttachmentsAsync(
        LiensDbContext db,
        SellingBuyerAccessLink accessLink,
        IReadOnlyList<SellingPortalMessage> messages,
        CancellationToken ct)
    {
        var messageIds = messages.Select(message => message.Id).ToList();
        if (messageIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<SellingPortalMessageAttachment>>();

        var attachments = await db.SellingPortalMessageAttachments
            .AsNoTracking()
            .Where(attachment =>
                attachment.TenantId == accessLink.TenantId &&
                attachment.LienId == accessLink.LienId &&
                attachment.SellerOrgId == accessLink.SellerOrgId &&
                attachment.BuyerOrgId == accessLink.BuyerOrgId &&
                attachment.BuyerContactId == accessLink.BuyerContactId &&
                messageIds.Contains(attachment.MessageId))
            .OrderBy(attachment => attachment.CreatedAtUtc)
            .ThenBy(attachment => attachment.Id)
            .ToListAsync(ct);

        return attachments
            .GroupBy(attachment => attachment.MessageId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<SellingPortalMessageAttachment>)group.ToList());
    }

    private static Task<SellingBuyerAccessLink?> ResolveBuyerResponseAccessLinkAsync(
        LiensDbContext db,
        SellingBuyerAccessLink accessLink,
        CancellationToken ct)
    {
        if (!string.Equals(accessLink.Purpose, SellingAccessLinkPurposes.ConfirmSaleSellerView, StringComparison.Ordinal))
            return Task.FromResult<SellingBuyerAccessLink?>(null);

        return db.SellingBuyerAccessLinks
            .AsNoTracking()
            .Where(link =>
                link.TenantId == accessLink.TenantId &&
                link.LienId == accessLink.LienId &&
                link.SellerOrgId == accessLink.SellerOrgId &&
                link.BuyerOrgId == accessLink.BuyerOrgId &&
                link.BuyerContactId == accessLink.BuyerContactId &&
                link.Purpose == SellingAccessLinkPurposes.ConfirmSaleBuyerResponse)
            .OrderByDescending(link => link.RespondedAtUtc.HasValue)
            .ThenByDescending(link => link.RespondedAtUtc)
            .ThenByDescending(link => link.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
    }

    private static async Task<PublicCaseParties> ResolvePublicCasePartiesAsync(
        LiensDbContext db,
        Guid tenantId,
        Case? caseEntity,
        CancellationToken ct)
    {
        if (caseEntity is null)
            return new PublicCaseParties(null, null, null, null);

        var canonicalLawFirm = caseEntity.HandlingLawFirmCompanyId.HasValue
            ? await db.Companies
                .AsNoTracking()
                .FirstOrDefaultAsync(company =>
                    company.TenantId == tenantId &&
                    company.Id == caseEntity.HandlingLawFirmCompanyId.Value,
                    ct)
            : null;
        var canonicalCaseManager = caseEntity.CaseManagerContactPersonId.HasValue
            ? await db.CompanyContactPersons
                .AsNoTracking()
                .FirstOrDefaultAsync(contact =>
                    contact.TenantId == tenantId &&
                    contact.Id == caseEntity.CaseManagerContactPersonId.Value,
                    ct)
            : null;
        var legacyHandlingLawFirmContact = await ResolveHandlingLawFirmContactAsync(db, tenantId, caseEntity, ct);
        var legacyCaseManager = await ResolveCaseManagerAsync(db, tenantId, caseEntity, ct);

        return new PublicCaseParties(
            FirstNonEmpty(
                canonicalLawFirm?.Name,
                legacyHandlingLawFirmContact?.Organization,
                legacyHandlingLawFirmContact?.DisplayName),
            FirstNonEmpty(legacyHandlingLawFirmContact?.DisplayName),
            FirstNonEmpty(legacyHandlingLawFirmContact?.Email, canonicalLawFirm?.Email),
            FirstNonEmpty(DisplayName(canonicalCaseManager), legacyCaseManager));
    }

    private static async Task<Contact?> ResolveHandlingLawFirmContactAsync(
        LiensDbContext db,
        Guid tenantId,
        Case? caseEntity,
        CancellationToken ct)
    {
        if (caseEntity is null)
            return null;

        var metadata = ParseLegacyNoteFields(caseEntity.Notes);
        if (Guid.TryParse(metadata.GetValueOrDefault("lawFirmId"), out var lawFirmId))
        {
            var lawFirm = await db.Contacts
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == lawFirmId, ct);
            if (lawFirm is not null &&
                lawFirm.ContactType == ContactType.LawFirm &&
                string.IsNullOrWhiteSpace(lawFirm.ContactSubtype) &&
                !lawFirm.LawFirmId.HasValue)
                return lawFirm;
        }

        return await db.Contacts
            .AsNoTracking()
            .Where(c =>
                c.TenantId == tenantId &&
                c.OrgId == caseEntity.OrgId &&
                c.IsActive &&
                c.ContactType == ContactType.LawFirm &&
                (c.ContactSubtype == null || c.ContactSubtype == string.Empty) &&
                !c.LawFirmId.HasValue)
            .OrderBy(c => c.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
    }

    private static async Task<string?> ResolveCaseManagerAsync(
        LiensDbContext db,
        Guid tenantId,
        Case? caseEntity,
        CancellationToken ct)
    {
        if (caseEntity is null)
            return null;

        var metadata = ParseLegacyNoteFields(caseEntity.Notes);
        if (!Guid.TryParse(metadata.GetValueOrDefault("caseManagerId"), out var caseManagerId))
            return null;

        var caseManager = await db.Contacts
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == caseManagerId, ct);

        return FirstNonEmpty(caseManager?.DisplayName);
    }

    private static async Task<IReadOnlyList<PublicDocumentView>> ResolveDocumentsAsync(
        LiensDbContext db,
        Guid tenantId,
        Lien lien,
        CancellationToken ct)
    {
        var items = await db.ServicingItems
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.LienId == lien.Id)
            .OrderBy(item => item.CreatedAtUtc)
            .ThenBy(item => item.Id)
            .ToListAsync(ct);

        var documentCategories = await db.LookupValues
            .AsNoTracking()
            .Where(value =>
                (value.TenantId == null || value.TenantId == tenantId) &&
                value.Category == LookupCategory.DocumentCategory &&
                value.IsActive)
            .Select(value => new { value.Id, value.Name })
            .ToListAsync(ct);
        var documentCategoryNames = FundingCompanySaleDocumentMapper.BuildDocumentCategoryNameLookup(
            documentCategories.Select(category => (category.Id, category.Name)));

        return items
            .Select(item => FundingCompanySaleDocumentMapper.Map(item, documentCategoryNames))
            .Where(document => document is not null)
            .Select(document => MapDocument(document!))
            .DistinctBy(document => document.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static PublicDocumentView MapDocument(FundingCompanySaleDocument document)
        => new(document.DocumentId, document.FileName, document.Category, document.SizeOrType);

    private static async Task<Guid?> ResolvePublicDocumentReferenceAsync(
        LiensDbContext db,
        Guid tenantId,
        Guid lienId,
        Guid documentId,
        CancellationToken ct)
    {
        var items = await db.ServicingItems
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.LienId == lienId)
            .ToListAsync(ct);

        foreach (var item in items.Where(item => FundingCompanySaleDocumentMapper.IsFundingCompanyDocumentTaskType(item.TaskType)))
        {
            var fields = ParseLegacyNoteFields(item.Notes);
            if (TryResolveDocumentId(fields, out var resolvedDocumentId) &&
                resolvedDocumentId == documentId)
            {
                return resolvedDocumentId;
            }
        }

        return null;
    }

    private static Task<SellingPortalMessageAttachment?> ResolvePublicMessageAttachmentAsync(
        LiensDbContext db,
        SellingBuyerAccessLink accessLink,
        Guid attachmentId,
        CancellationToken ct)
        => db.SellingPortalMessageAttachments
            .AsNoTracking()
            .FirstOrDefaultAsync(attachment =>
                attachment.Id == attachmentId &&
                attachment.TenantId == accessLink.TenantId &&
                attachment.LienId == accessLink.LienId &&
                attachment.SellerOrgId == accessLink.SellerOrgId &&
                attachment.BuyerOrgId == accessLink.BuyerOrgId &&
                attachment.BuyerContactId == accessLink.BuyerContactId,
                ct);

    internal static async Task<IReadOnlyList<SellingPortalMessageAttachment>> UploadSellingPortalMessageAttachmentsAsync(
        SellingPortalMessage message,
        IReadOnlyList<SellingPortalMessageAttachmentUpload> uploads,
        ILegacyDocumentUploadClient? uploadClient,
        CancellationToken ct)
    {
        if (uploads.Count == 0)
            return [];

        if (uploadClient is null)
            throw new InvalidOperationException("Document upload client is required for message attachments.");

        var actorUserId = message.CreatedByUserId.GetValueOrDefault(message.BuyerContactId);
        var attachments = new List<SellingPortalMessageAttachment>(uploads.Count);
        foreach (var upload in uploads)
        {
            var uploadResult = await uploadClient.UploadAsync(new Liens.Application.DTOs.LegacyDocumentUploadRequest
            {
                TenantId = message.TenantId,
                ActingUserId = actorUserId,
                ReferenceId = message.Id,
                ReferenceType = "SellingPortalMessage",
                DocumentTypeId = SellingMessageAttachmentDocumentTypeId,
                Title = Path.GetFileNameWithoutExtension(upload.FileName),
                Description = "Selling portal message attachment",
                Content = upload.Content,
                FileName = upload.FileName,
                ContentType = upload.ContentType,
                Length = upload.Length,
            }, ct);

            if (!uploadResult.DocumentId.HasValue)
                throw new InvalidOperationException("Document upload did not return a document id.");

            attachments.Add(SellingPortalMessageAttachment.Create(
                message,
                uploadResult.DocumentId.Value,
                upload.FileName,
                upload.ContentType,
                upload.Length,
                actorUserId));
        }

        return attachments;
    }

    internal static IResult? ValidatePortalMessageAttachments(string message, IFormFileCollection files)
    {
        if (message.Trim().Length == 0 && files.Count == 0)
        {
            return Results.BadRequest(new
            {
                error = new { code = "message-required", message = "Enter a message or attach at least one file before sending." },
            });
        }

        if (message.Trim().Length > MaxPublicMessageLength)
        {
            return Results.BadRequest(new
            {
                error = new { code = "message-too-long", message = $"Message must be {MaxPublicMessageLength} characters or fewer." },
            });
        }

        if (files.Count > MaxPublicMessageAttachmentCount)
        {
            return Results.BadRequest(new
            {
                error = new { code = "too-many-attachments", message = $"Attach up to {MaxPublicMessageAttachmentCount} files per message." },
            });
        }

        foreach (var file in files)
        {
            if (file.Length == 0)
            {
                return Results.BadRequest(new
                {
                    error = new { code = "empty-attachment", message = $"Attachment '{file.FileName}' is empty." },
                });
            }

            if (file.Length > LiensUploadLimits.MaxBytes)
            {
                return Results.BadRequest(new
                {
                    error = new { code = "attachment-too-large", message = $"Attachment '{file.FileName}' exceeds the {LiensUploadLimits.MaxMegabytes} MB limit." },
                });
            }

            var extension = Path.GetExtension(file.FileName)?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(extension) ||
                !SellingMessageAttachmentAllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new
                {
                    error = new
                    {
                        code = "attachment-type-not-allowed",
                        message = $"Attachment '{file.FileName}' uses an unsupported file type. Allowed types: {string.Join(", ", SellingMessageAttachmentAllowedExtensions)}.",
                    },
                });
            }
        }

        return null;
    }

    internal static List<SellingPortalMessageAttachmentUpload> OpenPortalMessageAttachmentUploads(IFormFileCollection files)
        => files
            .Select(file => new SellingPortalMessageAttachmentUpload(
                file.OpenReadStream(),
                Path.GetFileName(file.FileName),
                string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                file.Length))
            .ToList();

    internal static void DisposePortalMessageAttachmentUploads(IEnumerable<SellingPortalMessageAttachmentUpload> uploads)
    {
        foreach (var upload in uploads)
            upload.Content.Dispose();
    }

    internal static async Task<PortalMessageRequestReadResult> ReadPortalMessageRequestAsync(
        HttpRequest httpRequest,
        CancellationToken ct)
    {
        if (httpRequest.HasFormContentType)
        {
            var form = await httpRequest.ReadFormAsync(ct);
            var message = form["message"].FirstOrDefault() ?? string.Empty;
            var validation = ValidatePortalMessageAttachments(message, form.Files);
            if (validation is not null)
                return new PortalMessageRequestReadResult(null, [], validation);

            return new PortalMessageRequestReadResult(
                new PublicPortalMessageRequest(message),
                OpenPortalMessageAttachmentUploads(form.Files),
                null);
        }

        if (httpRequest.ContentLength.GetValueOrDefault() == 0)
        {
            return new PortalMessageRequestReadResult(null, [], PublicLinkState(
                "message-required",
                "Message could not be sent",
                "Enter a message or attach a file before sending.",
                StatusCodes.Status400BadRequest));
        }

        try
        {
            var request = await httpRequest.ReadFromJsonAsync<PublicPortalMessageRequest>(cancellationToken: ct);
            var validation = ValidatePortalMessageAttachments(request?.Message ?? string.Empty, new FormFileCollection());
            return validation is null
                ? new PortalMessageRequestReadResult(request, [], null)
                : new PortalMessageRequestReadResult(null, [], validation);
        }
        catch (JsonException)
        {
            return new PortalMessageRequestReadResult(null, [], PublicLinkState(
                "invalid-message-request",
                "Message could not be sent",
                "Message request body is invalid.",
                StatusCodes.Status400BadRequest));
        }
    }

    private static async Task<string?> IssueDocumentsAccessUrlAsync(
        IHttpClientFactory httpClientFactory,
        IServiceTokenIssuer serviceTokenIssuer,
        ILoggerFactory loggerFactory,
        SellingBuyerAccessLink accessLink,
        Guid documentId,
        string accessType,
        CancellationToken ct)
    {
        var normalizedAccessType = string.Equals(accessType, "download", StringComparison.OrdinalIgnoreCase)
            ? "download"
            : "view";
        var path = normalizedAccessType == "download"
            ? $"/documents/{documentId:D}/download-url"
            : $"/documents/{documentId:D}/view-url";
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        ApplyDocumentsAuthorization(
            request,
            serviceTokenIssuer,
            loggerFactory,
            accessLink.TenantId,
            ResolvePublicResponseActorId(accessLink));
        request.Headers.TryAddWithoutValidation("X-Organization-Id", accessLink.SellerOrgId.ToString());

        try
        {
            var client = httpClientFactory.CreateClient("DocumentsService");
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            using var body = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            var data = body.RootElement.TryGetProperty("data", out var dataElement)
                ? dataElement
                : body.RootElement;

            if (data.TryGetProperty("redeemUrl", out var redeemUrl) &&
                !string.IsNullOrWhiteSpace(redeemUrl.GetString()))
            {
                return NormalizeDocumentsRedeemUrl(redeemUrl.GetString()!);
            }

            if (data.TryGetProperty("accessToken", out var accessToken) &&
                !string.IsNullOrWhiteSpace(accessToken.GetString()))
            {
                return $"/documents/access/{Uri.EscapeDataString(accessToken.GetString()!)}";
            }
        }
        catch (HttpRequestException ex)
        {
            loggerFactory
                .CreateLogger(nameof(SellingPublicEndpoints))
                .LogWarning(ex, "Documents access token request failed for document {DocumentId}", documentId);
        }
        catch (JsonException ex)
        {
            loggerFactory
                .CreateLogger(nameof(SellingPublicEndpoints))
                .LogWarning(ex, "Documents access token response was invalid for document {DocumentId}", documentId);
        }

        return null;
    }

    private static void ApplyDocumentsAuthorization(
        HttpRequestMessage request,
        IServiceTokenIssuer serviceTokenIssuer,
        ILoggerFactory loggerFactory,
        Guid tenantId,
        Guid actorUserId)
    {
        if (!serviceTokenIssuer.IsConfigured)
            return;

        try
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                serviceTokenIssuer.IssueToken(tenantId.ToString(), actorUserId.ToString(), DocumentsServiceAudience));
        }
        catch (Exception ex)
        {
            loggerFactory
                .CreateLogger(nameof(SellingPublicEndpoints))
                .LogWarning(ex, "Unable to mint Documents service token for tenant {TenantId}", tenantId);
        }
    }

    private static string NormalizeDocumentsRedeemUrl(string redeemUrl)
    {
        var trimmed = redeemUrl.Trim();
        if (trimmed.StartsWith("/documents/access/", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        if (trimmed.StartsWith("/access/", StringComparison.OrdinalIgnoreCase))
            return $"/documents{trimmed}";
        return trimmed;
    }

    private static string? BuildPublicDocumentActionUrl(string? publicToken, Guid? documentId, string action)
    {
        if (string.IsNullOrWhiteSpace(publicToken) || !documentId.HasValue)
            return null;

        var normalizedAction = string.Equals(action, "download", StringComparison.OrdinalIgnoreCase)
            ? "download"
            : "view";
        return $"/api/lien/api/liens/selling/public/{Uri.EscapeDataString(publicToken.Trim())}/documents/{documentId.Value:D}/{normalizedAction}";
    }

    private static string? BuildPublicMessageAttachmentActionUrl(string? publicToken, Guid attachmentId, string action)
    {
        if (string.IsNullOrWhiteSpace(publicToken) || attachmentId == Guid.Empty)
            return null;

        var normalizedAction = string.Equals(action, "download", StringComparison.OrdinalIgnoreCase)
            ? "download"
            : "view";
        return $"/api/lien/api/liens/selling/public/{Uri.EscapeDataString(publicToken.Trim())}/message-attachments/{attachmentId:D}/{normalizedAction}";
    }

    private static bool TryResolveDocumentId(
        IReadOnlyDictionary<string, string> fields,
        out Guid documentId)
    {
        if (Guid.TryParse(fields.GetValueOrDefault("documentId"), out documentId))
            return true;

        var url = fields.GetValueOrDefault("url");
        if (string.IsNullOrWhiteSpace(url))
            return false;

        var segment = url.Trim().TrimEnd('/').Split('/').LastOrDefault();
        return Guid.TryParse(segment, out documentId);
    }

    private static PublicBuyerPortalResponse MapPublicPortalResponse(
        PublicPortalView view,
        PublicBuyerAccountResponse? account = null,
        string? publicToken = null)
    {
        var responseAccessLink = view.BuyerResponseAccessLink ?? view.AccessLink;

        return new(
            ResolvePublicAudience(view.AccessLink),
            new PublicBuyerAccessLinkResponse(
                view.AccessLink.CreatedAtUtc,
                view.AccessLink.ExpiresAtUtc,
                view.AccessLink.LastAccessedAtUtc,
                view.AccessLink.NotificationSubmittedAtUtc,
                responseAccessLink.ResponseStatus,
                responseAccessLink.ResponseAmount,
                responseAccessLink.ResponseNotes,
                responseAccessLink.RespondedAtUtc),
            new PublicBuyerLienResponse(
                view.Lien.Id,
                ResolveLienCode(view.Lien),
                view.Lien.Status,
                view.Lien.SellerStatus,
                PacificTimeHelper.Convert(view.Lien.SubmittedForSaleAtUtc ?? view.AccessLink.NotificationSubmittedAtUtc ?? view.AccessLink.CreatedAtUtc),
                view.Lien.ListingVisibility,
                view.Lien.InitialServiceDate,
                view.Lien.EndServiceDate,
                view.Lien.OriginalAmount,
                view.Lien.AskAmount,
                view.Lien.OfferPrice),
            new PublicBuyerSellerResponse(
                view.SellerDisplay.Name,
                view.SellerDisplay.Company,
                null),
            new PublicBuyerOrganizationResponse(
                view.BuyerContact?.DisplayName,
                view.BuyerContact?.Organization,
                view.BuyerContact?.Email,
                view.BuyerContact?.Phone),
            new PublicBuyerCaseResponse(
                view.CaseParties.HandlingLawFirm,
                view.CaseParties.HandlingLawFirmContactName,
                view.CaseParties.HandlingLawFirmEmail,
                view.CaseParties.CaseManager),
            view.Documents
                .Select(document => new PublicBuyerDocumentResponse(
                    document.DocumentId,
                    document.FileName,
                    document.Category,
                    document.SizeOrType,
                    BuildPublicDocumentActionUrl(publicToken, document.DocumentId, "view"),
                    BuildPublicDocumentActionUrl(publicToken, document.DocumentId, "download")))
                .ToList(),
            view.Messages
                .Select(message => MapPublicMessage(
                    message,
                    view.MessageAttachments.TryGetValue(message.Id, out var attachments)
                        ? attachments
                        : [],
                    publicToken))
                .ToList(),
            account);
    }

    private static PublicPortalMessageResponse MapPublicMessage(
        SellingPortalMessage message,
        IReadOnlyList<SellingPortalMessageAttachment>? attachments = null,
        string? publicToken = null,
        Func<SellingPortalMessageAttachment, string, string?>? attachmentUrlBuilder = null)
        => new(
            message.Id,
            message.SenderType,
            message.SenderName,
            message.SenderEmail,
            message.Message,
            message.CreatedAtUtc,
            (attachments ?? [])
                .Select(attachment => MapPublicMessageAttachment(attachment, publicToken, attachmentUrlBuilder))
                .ToList());

    private static PublicPortalMessageAttachmentResponse MapPublicMessageAttachment(
        SellingPortalMessageAttachment attachment,
        string? publicToken,
        Func<SellingPortalMessageAttachment, string, string?>? attachmentUrlBuilder = null)
        => new(
            attachment.Id,
            attachment.FileName,
            attachment.ContentType,
            attachment.FileSizeBytes,
            attachment.CreatedAtUtc,
            attachmentUrlBuilder?.Invoke(attachment, "view") ?? BuildPublicMessageAttachmentActionUrl(publicToken, attachment.Id, "view"),
            attachmentUrlBuilder?.Invoke(attachment, "download") ?? BuildPublicMessageAttachmentActionUrl(publicToken, attachment.Id, "download"));

    private static bool IsSupportedPublicPurpose(string purpose)
        => string.Equals(purpose, SellingAccessLinkPurposes.ConfirmSaleBuyerResponse, StringComparison.Ordinal) ||
           string.Equals(purpose, SellingAccessLinkPurposes.ConfirmSaleSellerView, StringComparison.Ordinal);

    private static bool IsBuyerResponseLink(SellingBuyerAccessLink accessLink)
        => string.Equals(accessLink.Purpose, SellingAccessLinkPurposes.ConfirmSaleBuyerResponse, StringComparison.Ordinal);

    private static string ResolvePublicAudience(SellingBuyerAccessLink accessLink)
        => string.Equals(accessLink.Purpose, SellingAccessLinkPurposes.ConfirmSaleSellerView, StringComparison.Ordinal)
            ? "seller"
            : "buyer";

    private static IResult PublicLinkState(string code, string title, string message, int statusCode)
        => Results.Json(
            new PublicBuyerPortalErrorResponse(new PublicBuyerPortalError(code, title, message)),
            statusCode: statusCode);

    private static CompanyContactPerson? SelectSellerContact(IReadOnlyList<CompanyContactPerson> contacts)
    {
        var orderedContacts = OrderSellerContacts(contacts);
        return orderedContacts.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Email))
           ?? orderedContacts.FirstOrDefault();
    }

    private static IReadOnlyList<CompanyContactPerson> OrderSellerContacts(IReadOnlyList<CompanyContactPerson> contacts)
        => contacts
            .OrderBy(c => c.Company?.Name ?? string.Empty)
            .ThenBy(c => c.LastName)
            .ThenBy(c => c.FirstName)
            .ThenBy(c => c.Email ?? string.Empty)
            .ThenBy(c => c.Id)
            .ToList();

    private static Dictionary<string, string> ParseLegacyNoteFields(string? notes)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(notes))
            return result;

        var trimmed = notes.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        var value = property.Value.ValueKind == JsonValueKind.String
                            ? property.Value.GetString()
                            : property.Value.GetRawText();
                        if (!string.IsNullOrWhiteSpace(value))
                            result[property.Name] = value;
                    }
                }
            }
            catch (JsonException)
            {
                // Fall back to the legacy key/value parser below.
            }
        }

        foreach (var segment in notes.Split("; ", StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = segment.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = segment[..eq].Trim();
            var value = segment[(eq + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(key))
                result[key] = value;
        }

        return result;
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? DisplayName(CompanyContactPerson? contact)
        => contact is null
            ? null
            : FirstNonEmpty($"{contact.FirstName} {contact.LastName}");

    private static string Html(string? value)
        => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string? NormalizePhoneForIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
            return trimmed;

        if (trimmed.StartsWith('+'))
            return "+" + digits;

        return digits.Length switch
        {
            10 => "+1" + digits,
            11 when digits.StartsWith('1') => "+" + digits,
            _ => trimmed,
        };
    }

    private static (string? FirstName, string? LastName) SplitName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (null, null);

        var parts = value.Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts.Length switch
        {
            0 => (null, null),
            1 => (parts[0], null),
            _ => (parts[0], string.Join(' ', parts.Skip(1))),
        };
    }

    private static string ResolveLienCode(Lien lien)
        => string.IsNullOrWhiteSpace(lien.LienNumber) ? lien.Id.ToString() : lien.LienNumber;

    private static bool IsActionableLienStatus(string status)
        => string.Equals(status, LienStatus.Offered, StringComparison.Ordinal)
           || string.Equals(status, LienStatus.UnderReview, StringComparison.Ordinal);

    private static bool HasIdempotencyKey(HttpRequest request) =>
        !string.IsNullOrWhiteSpace(request.Headers["Idempotency-Key"].FirstOrDefault());

    private static bool IsActionableLien(Lien lien)
        => IsActionableBuyerOffer(lien.Status, lien.SellerStatus) &&
           !lien.ArchivedAtUtc.HasValue &&
           !lien.WithdrawnAtUtc.HasValue &&
           !lien.SoldAtUtc.HasValue;

    private static bool IsActionableBuyerOffer(string? lienStatus, string? sellerStatus)
        => string.Equals(sellerStatus, SellingLienStatus.SubmittedForSale, StringComparison.Ordinal) ||
           (string.IsNullOrWhiteSpace(sellerStatus) && IsActionableLienStatus(lienStatus ?? string.Empty));

    private static bool IsNotificationSubmittedStatus(string? status)
        => string.Equals(status, "sent", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(status, "accepted", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase);

    private static string BuildPublicResponseNotificationIdempotencyKey(
        SellingBuyerAccessLink accessLink,
        string responseStatus,
        string recipientRole)
    {
        var key = string.Join(":", new[]
        {
            "liens.public-response.email",
            accessLink.TenantId.ToString("N"),
            accessLink.Id.ToString("N"),
            responseStatus.Trim().ToLowerInvariant(),
            recipientRole.Trim().ToLowerInvariant(),
        });

        return key.Length > 280 ? key[..280] : key;
    }

    private static string? ReadIdempotencyKey(HttpContext httpContext)
        => httpContext.Request.Headers["Idempotency-Key"].FirstOrDefault();

    private static void EnqueueAccessLinkResponseInbox(
        LiensDbContext db,
        PublicPortalView view,
        bool accepted)
    {
        if (view.AccessLink.CreatedByUserId is not { } sellerUserId || sellerUserId == Guid.Empty)
            return;

        var sourceName = FirstNonEmpty(view.BuyerContact?.DisplayName, view.BuyerContact?.Organization, "Buyer")!;
        var eventKey = accepted
            ? NotificationTaxonomy.Liens.Events.OfferAccepted
            : NotificationTaxonomy.Liens.Events.OfferRejected;
        var title = accepted ? "Offer Accepted" : "Offer Declined";
        var verb = accepted ? "accepted" : "declined";
        EnqueueInbox(
            db,
            view.AccessLink.TenantId,
            sellerUserId,
            eventKey,
            "lien",
            title,
            $"{sourceName} {verb} the offer for lien {ResolveLienCode(view.Lien)}.",
            view.AccessLink.RespondedAtUtc ?? DateTime.UtcNow,
            $"selling:access-link:{view.AccessLink.Id:N}:{verb}:{sellerUserId:N}",
            sourceName);
    }

    private static void EnqueueInbox(
        LiensDbContext db,
        Guid tenantId,
        Guid recipientUserId,
        string eventKey,
        string category,
        string title,
        string description,
        DateTime occurredAtUtc,
        string idempotencyKey,
        string sourceDisplayName = "Synq Selling")
    {
        var initials = string.Concat(sourceDisplayName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Take(2)
            .Select(part => char.ToUpperInvariant(part[0])));
        if (string.IsNullOrEmpty(initials)) initials = "SS";

        db.SellingNotificationOutboxItems.Add(SellingNotificationOutboxItem.Create(
            tenantId,
            recipientUserId,
            eventKey,
            category,
            title,
            description,
            sourceDisplayName,
            initials,
            occurredAtUtc,
            idempotencyKey));
    }

    private static void SetNoReferrerHeader(HttpResponse response) =>
        response.Headers["Referrer-Policy"] = "no-referrer";

    private sealed record PublicPortalView(
        SellingBuyerAccessLink AccessLink,
        Lien Lien,
        Case? Case,
        PublicBuyerContact? BuyerContact,
        CompanyContactPerson? SellerContact,
        SellerOrganizationDisplay SellerDisplay,
        PublicCaseParties CaseParties,
        IReadOnlyList<PublicDocumentView> Documents,
        IReadOnlyList<SellingPortalMessage> Messages,
        IReadOnlyDictionary<Guid, IReadOnlyList<SellingPortalMessageAttachment>> MessageAttachments,
        SellingBuyerAccessLink? BuyerResponseAccessLink,
        string? BuyerAccountEmail);

    private sealed record PublicCaseParties(
        string? HandlingLawFirm,
        string? HandlingLawFirmContactName,
        string? HandlingLawFirmEmail,
        string? CaseManager);

    private sealed record PublicBuyerContact(
        Guid Id,
        Guid OrgId,
        string DisplayName,
        string? Organization,
        string? Email,
        string? Phone,
        string FirstName,
        string LastName);

    private sealed record PublicDocumentView(Guid? DocumentId, string FileName, string? Category, string SizeOrType);

    private sealed record PublicBuyerPortalResponse(
        string Audience,
        PublicBuyerAccessLinkResponse AccessLink,
        PublicBuyerLienResponse Lien,
        PublicBuyerSellerResponse Seller,
        PublicBuyerOrganizationResponse Buyer,
        PublicBuyerCaseResponse Case,
        IReadOnlyList<PublicBuyerDocumentResponse> Documents,
        IReadOnlyList<PublicPortalMessageResponse> Messages,
        PublicBuyerAccountResponse? Account);

    private sealed record PublicBuyerAccountResponse(
        bool HasExistingAccount,
        string LoginUrl);

    private sealed record PublicBuyerAccessLinkResponse(
        DateTime CreatedAtUtc,
        DateTime ExpiresAtUtc,
        DateTime? LastAccessedAtUtc,
        DateTime? NotificationSubmittedAtUtc,
        string? ResponseStatus,
        decimal? ResponseAmount,
        string? ResponseNotes,
        DateTime? RespondedAtUtc);

    private sealed record PublicBuyerLienResponse(
        Guid Id,
        string LienCode,
        string Status,
        string? SellerStatus,
        DateTimeOffset SubmittedAtUtc,
        string? ListingVisibility,
        DateOnly? InitialServiceDate,
        DateOnly? EndServiceDate,
        decimal OriginalAmount,
        decimal? AskAmount,
        decimal? OfferPrice);

    private sealed record PublicBuyerSellerResponse(
        string? Name,
        string? Company,
        string? Email);

    private sealed record PublicBuyerOrganizationResponse(
        string? ContactName,
        string? Company,
        string? Email,
        string? Phone);

    private sealed record PublicBuyerCaseResponse(
        string? HandlingLawFirm,
        string? HandlingLawFirmContactName,
        string? HandlingLawFirmEmail,
        string? CaseManager);

    private sealed record PublicBuyerDocumentResponse(
        Guid? Id,
        string FileName,
        string? Category,
        string SizeOrType,
        string? ViewUrl,
        string? DownloadUrl);

    private sealed record PublicPortalMessageResponse(
        Guid Id,
        string SenderType,
        string SenderName,
        string? SenderEmail,
        string Message,
        DateTime CreatedAtUtc,
        IReadOnlyList<PublicPortalMessageAttachmentResponse> Attachments);

    private sealed record PublicPortalMessageAttachmentResponse(
        Guid Id,
        string FileName,
        string ContentType,
        long FileSizeBytes,
        DateTime CreatedAtUtc,
        string? ViewUrl,
        string? DownloadUrl);

    private sealed record PublicBuyerPortalErrorResponse(PublicBuyerPortalError Error);

    private sealed record PublicBuyerPortalError(string Code, string Title, string Message);

    internal sealed record PublicPortalMessageRequest(string? Message);

    internal sealed record PortalMessageRequestReadResult(
        PublicPortalMessageRequest? Request,
        IReadOnlyList<SellingPortalMessageAttachmentUpload> Attachments,
        IResult? Error);

    internal sealed record SellingPortalMessageAttachmentUpload(
        Stream Content,
        string FileName,
        string ContentType,
        long Length);

    private sealed record PublicBuyerOfferRequest(decimal OfferAmount, string? Message);

    internal sealed record PublicBuyerAcceptLienRequest(string? Notes, string? Message);

    internal sealed record PublicBuyerDeclineLienRequest(string? Reason);

    private sealed record PublicBuyerActivateAccountRequest(
        string? CompanyName,
        string? Email,
        string Password,
        string? FirstName,
        string? LastName,
        string? Phone);

    private sealed record PublicBuyerAccountActivationResponse(
        Guid UserId,
        bool IsNew,
        string LoginUrl);
}
