using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Collections.Concurrent;
using BuildingBlocks.Authentication.ServiceTokens;
using BuildingBlocks.Authorization;
using BuildingBlocks.Authorization.Filters;
using BuildingBlocks.Context;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Notifications;
using Liens.Application.DTOs;
using Liens.Application.Interfaces;
using Liens.Application.Services;
using Liens.Domain;
using Liens.Domain.Entities;
using Liens.Domain.Enums;
using Liens.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Liens.Api.Endpoints;

/// <summary>
/// Lien-first Selling V2 routes. These handlers deliberately scope every seller
/// lookup to tenant and selling organisation; the legacy portfolio routes remain
/// available beside this contract during the frontend migration.
/// </summary>
public static class SellingV2Endpoints
{
    private const string SellingMedicalPricingTaskType = "SellingMedicalPricing";
    private const string SellingDocumentTaskType = "SellingDocumentReference";
    private const string LegacyCaseMetadataMarker = "[legacy-meta]";
    private const string DocumentsServiceAudience = "documents-service";
    private const int MaxSellingCaseTrackingNotesLength = 3_500;
    private static readonly JsonSerializerOptions SellingPricingJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> SellingCaseDraftFinalizationLocks = new();
    private static readonly HashSet<string> IntakeStatuses =
    [
        SellingLienStatus.Pending,
        SellingLienStatus.Internal,
    ];

    public static void MapSellingV2Endpoints(this WebApplication app)
    {
        var seller = app.MapGroup("/api/liens/selling")
            .RequireAuthorization(Policies.AuthenticatedUser)
            .RequireProductAccess(LiensPermissions.ProductCode)
            .RequireSellMode();

        seller.MapPost("/case-drafts", CreateCaseDraft)
            .RequirePermission(LiensPermissions.LienSaleCreate);
        seller.MapGet("/case-drafts/{draftId:guid}", GetCaseDraft)
            .RequirePermission(LiensPermissions.LienSaleRead);
        seller.MapPut("/case-drafts/{draftId:guid}", UpdateCaseDraft)
            .RequirePermission(LiensPermissions.LienSaleUpdate);
        seller.MapPost("/case-drafts/{draftId:guid}/plaintiff", FinalizeCaseDraft)
            .RequirePermission(LiensPermissions.LienSaleCreate);
        seller.MapGet("/cases", GetSellingCases)
            .RequirePermission(LiensPermissions.LienSaleRead);
        seller.MapGet("/cases/{caseId:guid}", GetSellingCase)
            .RequirePermission(LiensPermissions.LienSaleRead);
        seller.MapPut("/cases/{caseId:guid}", UpdateSellingCaseInformation)
            .RequirePermission(LiensPermissions.LienSaleUpdate);
        seller.MapPut("/cases/{caseId:guid}/plaintiff", UpdateSellingCasePlaintiff)
            .RequirePermission(LiensPermissions.LienSaleUpdate);
        seller.MapPost("/liens", CreateLien)
            .RequirePermission(LiensPermissions.LienSaleCreate);
        seller.MapGet("/liens/{lienId:guid}", GetLienDetail)
            .RequirePermission(LiensPermissions.LienSaleRead);
        seller.MapGet("/liens/{lienId:guid}/archived-status", GetArchivedStatus)
            .RequirePermission(LiensPermissions.LienSaleRead);
        seller.MapGet("/liens/{lienId:guid}/activity", GetLienActivity)
            .RequirePermission(LiensPermissions.LienSaleRead);
        seller.MapGet("/liens/{lienId:guid}/messages", GetSellerLienMessages)
            .RequirePermission(LiensPermissions.LienSaleRead);
        seller.MapPost("/liens/{lienId:guid}/messages", PostSellerLienMessage)
            .RequirePermission(LiensPermissions.LienSaleUpdate)
            .DisableAntiforgery();
        seller.MapGet("/liens/{lienId:guid}/message-attachments/{attachmentId:guid}/view", ViewSellerLienMessageAttachment)
            .RequirePermission(LiensPermissions.LienSaleRead);
        seller.MapGet("/liens/{lienId:guid}/message-attachments/{attachmentId:guid}/download", DownloadSellerLienMessageAttachment)
            .RequirePermission(LiensPermissions.LienSaleRead);
        seller.MapPut("/liens/{lienId:guid}/lien-information", SaveLienInformation)
            .RequirePermission(LiensPermissions.LienSaleUpdate);
        seller.MapPut("/liens/{lienId:guid}/case-information", SaveCaseInformation)
            .RequirePermission(LiensPermissions.LienSaleUpdate);
        seller.MapPut("/liens/{lienId:guid}/medical-pricing", SaveMedicalPricing)
            .RequirePermission(LiensPermissions.LienSaleUpdate);
        seller.MapPut("/liens/{lienId:guid}/documents", SaveDocuments)
            .RequirePermission(LiensPermissions.LienSaleUpdate);
        seller.MapPost("/liens/{lienId:guid}/prepare-sale", PrepareSale)
            .RequirePermission(LiensPermissions.LienSaleUpdate);
        seller.MapPost("/liens/{lienId:guid}/confirm-sale", ConfirmSale)
            .RequirePermission(LiensPermissions.LienSalePublish);
        seller.MapPost("/liens/{lienId:guid}/withdraw-sale", WithdrawSale)
            .RequirePermission(LiensPermissions.LienSaleWithdraw);
        seller.MapPost("/liens/{lienId:guid}/move-to-management", MoveToManagement)
            .RequirePermission(LiensPermissions.LienSaleUpdate);
        seller.MapPost("/liens/{lienId:guid}/move-to-management-v2", MoveToManagementV2)
            .RequirePermission(LiensPermissions.LienSaleUpdate);
        seller.MapPost("/liens/{lienId:guid}/archive", ArchiveLien)
            .RequirePermission(LiensPermissions.LienSaleUpdate);
        seller.MapPost("/liens/{lienId:guid}/restore", RestoreLien)
            .RequirePermission(LiensPermissions.LienSaleUpdate);
        seller.MapPost("/liens/{lienId:guid}/buyer-access-links", CreateBuyerAccessLink)
            .RequirePermission(LiensPermissions.LienSalePublish);

        seller.MapGet("/bulk-imports/{importId:guid}", GetBulkImport)
            .RequirePermission(LiensPermissions.LienSaleRead);
        seller.MapGet("/bulk-imports/{importId:guid}/rows", GetBulkImportRows)
            .RequirePermission(LiensPermissions.LienSaleRead);
        seller.MapPost("/bulk-imports/{importId:guid}/validate", ValidateBulkImport)
            .RequirePermission(LiensPermissions.LienSaleUpdate);
        seller.MapPost("/bulk-imports/{importId:guid}/confirm", ConfirmBulkImport)
            .RequirePermission(LiensPermissions.LienSaleCreate);
        seller.MapDelete("/bulk-imports/{importId:guid}", CancelBulkImport)
            .RequirePermission(LiensPermissions.LienSaleUpdate);

        seller.MapGet("/lookups/funding-companies", GetFundingCompanies)
            .RequirePermission(LiensPermissions.LienSaleRead);
        seller.MapGet("/lookups/funding-company-contacts", GetFundingCompanyContacts)
            .RequirePermission(LiensPermissions.LienSaleRead);
        seller.MapGet("/lookups/law-firms", GetLawFirms)
            .RequirePermission(LiensPermissions.LienSaleRead);
        seller.MapGet("/lookups/case-managers", GetCaseManagers)
            .RequirePermission(LiensPermissions.LienSaleRead);
        seller.MapGet("/lookups/facilities", GetFacilities)
            .RequirePermission(LiensPermissions.LienSaleRead);
        seller.MapGet("/lookups/medical-codes", GetMedicalCodes)
            .RequirePermission(LiensPermissions.LienSaleRead);
        seller.MapGet("/lookups/document-types", GetDocumentTypes)
            .RequirePermission(LiensPermissions.LienSaleRead);

        var buyer = app.MapGroup("/api/liens/selling/buyer")
            .RequireAuthorization(Policies.AuthenticatedUser)
            .RequireProductAccess(LiensPermissions.ProductCode);

        buyer.MapGet("/liens/by-lien/{lienId:guid}", GetBuyerLien)
            .AddEndpointFilter(new BuyerViewPermissionFilter());
        buyer.MapPost("/liens/by-lien/{lienId:guid}/offers", SubmitBuyerOffer)
            .RequirePermission(LiensPermissions.LienOffer);
        buyer.MapPost("/liens/by-lien/{lienId:guid}/decline", DeclineBuyerLien)
            .RequirePermission(LiensPermissions.LienOffer);
    }

    private static async Task<IResult> CreateLien(
        CreateSellingLienRequest request,
        HttpRequest httpRequest,
        LiensDbContext db,
        ICurrentRequestContext context,
        CancellationToken ct)
    {
        var (tenantId, sellerOrgId, userId) = RequireSellerContext(context);
        if (!SellingIdempotency.TryGetKey(httpRequest, out var idempotencyKey, out var idempotencyError)) return idempotencyError!;
        var replay = await SellingIdempotency.GetReplayAsync(
            db, tenantId, "User", userId, "/api/liens/selling/liens", "SellerOrganization", sellerOrgId.ToString(), idempotencyKey!, request, ct);
        if (replay is not null) return replay;
        var sellerStatus = NormalizeIntakeStatus(request.SellerStatus);
        if (sellerStatus is null)
            return ValidationError("sellerStatus", "sellerStatus must be Pending or Internal.");
        if (request.CaseId == Guid.Empty)
            return ValidationError("caseId", "caseId is required.");

        var caseExists = await db.Cases.AsNoTracking().AnyAsync(caseEntity =>
            caseEntity.TenantId == tenantId &&
            caseEntity.OrgId == sellerOrgId &&
            caseEntity.Id == request.CaseId, ct);
        if (!caseExists)
            return ValidationError("caseId", "Case was not found for the seller organization.");

        var started = await SellingIdempotency.TryBeginAsync(
            db, tenantId, "User", userId, "/api/liens/selling/liens", "SellerOrganization", sellerOrgId.ToString(), idempotencyKey!, request, ct);
        if (started.Result is not null) return started.Result;

        var lien = Lien.Create(
            tenantId,
            sellerOrgId,
            $"SL-{Guid.CreateVersion7():N}"[..15].ToUpperInvariant(),
            LienType.MedicalLien,
            0m,
            userId,
            description: request.Source?.Trim(),
            caseId: request.CaseId);
        lien.UpdateSellingAnalyticsFields(userId, sellerStatus: sellerStatus);
        db.Liens.Add(lien);
        AddActivity(db, lien, userId, $"Selling lien created with status {sellerStatus}.");
        await db.SaveChangesAsync(ct);

        return await SellingIdempotency.CompleteAsync(db, started.Record!, userId, StatusCodes.Status201Created, new
        {
            lienId = lien.Id,
            lienNumber = lien.LienNumber,
            sellerStatus = lien.SellerStatus,
        }, ct);
    }

    private static async Task<IResult> CreateCaseDraft(
        CreateSellingCaseDraftRequest request,
        LiensDbContext db,
        ICurrentRequestContext context,
        CancellationToken ct)
    {
        var (tenantId, sellerOrgId, userId) = RequireSellerContext(context);
        var validation = await ValidateSellingCaseInformationAsync(
            db, tenantId, sellerOrgId, CaseStatus.PreDemand, request.AccidentTypeId, request.AccidentState,
            request.DateOfLoss, request.HandlingLawFirmId, request.CaseManagerId, request.CaseTrackingNotes, ct);
        if (validation.Error is not null) return validation.Error;

        var intake = validation.Value!;
        var draft = SellingCaseDraft.Create(
            tenantId, sellerOrgId, intake.CaseStatus, userId, intake.AccidentTypeId,
            intake.AccidentState, request.DateOfLoss, intake.HandlingLawFirmId,
            intake.CaseManagerId, request.CaseTrackingNotes);
        db.SellingCaseDrafts.Add(draft);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/liens/selling/case-drafts/{draft.Id}", new
        {
            draftId = draft.Id,
            draft.CaseStatus,
            draft.AccidentTypeId,
            draft.AccidentState,
            draft.DateOfLoss,
            handlingLawFirmId = draft.HandlingLawFirmCompanyId,
            caseManagerId = draft.CaseManagerContactPersonId,
            draft.CaseTrackingNotes,
        });
    }

    private static async Task<IResult> GetCaseDraft(
        Guid draftId,
        LiensDbContext db,
        ICurrentRequestContext context,
        CancellationToken ct)
    {
        var (tenantId, sellerOrgId, _) = RequireSellerContext(context);
        var draft = await db.SellingCaseDrafts.AsNoTracking().FirstOrDefaultAsync(item =>
            item.TenantId == tenantId && item.OrgId == sellerOrgId && item.Id == draftId, ct);
        if (draft is null) return Results.NotFound(new { message = "Selling case draft was not found." });

        return Results.Ok(new
        {
            draftId = draft.Id,
            draft.CaseStatus,
            draft.AccidentTypeId,
            draft.AccidentState,
            draft.DateOfLoss,
            handlingLawFirmId = draft.HandlingLawFirmCompanyId,
            caseManagerId = draft.CaseManagerContactPersonId,
            draft.CaseTrackingNotes,
            draft.CaseId,
            draft.FinalizedAtUtc,
            draft.CreatedAtUtc,
            draft.UpdatedAtUtc,
        });
    }

    private static async Task<IResult> UpdateCaseDraft(
        Guid draftId,
        CreateSellingCaseDraftRequest request,
        LiensDbContext db,
        ICurrentRequestContext context,
        CancellationToken ct)
    {
        var (tenantId, sellerOrgId, userId) = RequireSellerContext(context);
        var draft = await db.SellingCaseDrafts.FirstOrDefaultAsync(item =>
            item.TenantId == tenantId && item.OrgId == sellerOrgId && item.Id == draftId, ct);
        if (draft is null) return Results.NotFound(new { message = "Selling case draft was not found." });
        if (draft.CaseId.HasValue)
            return ValidationError("draftId", "A finalized selling case draft cannot be updated.");

        var validation = await ValidateSellingCaseInformationAsync(
            db, tenantId, sellerOrgId, CaseStatus.PreDemand, request.AccidentTypeId, request.AccidentState,
            request.DateOfLoss, request.HandlingLawFirmId, request.CaseManagerId, request.CaseTrackingNotes, ct);
        if (validation.Error is not null) return validation.Error;

        var intake = validation.Value!;
        draft.UpdateCaseInformation(
            intake.CaseStatus, userId, intake.AccidentTypeId, intake.AccidentState,
            request.DateOfLoss, intake.HandlingLawFirmId, intake.CaseManagerId, request.CaseTrackingNotes);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            draftId = draft.Id,
            draft.CaseStatus,
            draft.AccidentTypeId,
            draft.AccidentState,
            draft.DateOfLoss,
            handlingLawFirmId = draft.HandlingLawFirmCompanyId,
            caseManagerId = draft.CaseManagerContactPersonId,
            draft.CaseTrackingNotes,
        });
    }

    private static async Task<IResult> FinalizeCaseDraft(
        Guid draftId,
        FinalizeSellingCaseDraftPlaintiffRequest request,
        LiensDbContext db,
        ICurrentRequestContext context,
        CancellationToken ct)
    {
        var (tenantId, sellerOrgId, userId) = RequireSellerContext(context);
        var finalizationLock = SellingCaseDraftFinalizationLocks.GetOrAdd(draftId, _ => new SemaphoreSlim(1, 1));
        await finalizationLock.WaitAsync(ct);
        try
        {
            var draft = await db.SellingCaseDrafts.FirstOrDefaultAsync(item =>
                item.TenantId == tenantId && item.OrgId == sellerOrgId && item.Id == draftId, ct);
            if (draft is null) return Results.NotFound(new { message = "Selling case draft was not found." });
            if (draft.CaseId is { } completedCaseId)
                return Results.Ok(new { draftId = draft.Id, caseId = completedCaseId, finalizedAtUtc = draft.FinalizedAtUtc });

            if (ValidatePlaintiff(request.FirstName, request.LastName, request.Birthdate, request.Email, request.Phone,
                    request.Gender, request.Address, request.City, request.State, request.Zipcode) is { } plaintiffError)
                return plaintiffError;
            var route = $"/api/liens/selling/case-drafts/{draftId}/plaintiff";
            var finalizationGate = await SellingIdempotency.TryBeginAsync(
                db, tenantId, "SellingCaseDraftFinalization", draftId, route, "SellingCaseDraft", draftId.ToString(),
                "selling-case-draft-finalization-v1", request: null, ct: ct);
            if (finalizationGate.Result is not null)
            {
                return Results.Conflict(new
                {
                    error = new
                    {
                        code = "case_draft_finalization_conflict",
                        message = "The selling case draft is being finalized. Retry shortly.",
                    },
                });
            }

            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var accidentTypeName = await ResolveAccidentTypeNameAsync(db, tenantId, draft.AccidentTypeId, ct);
                var caseEntity = CreateCaseFromDraft(draft, request, userId, accidentTypeName);
                db.Cases.Add(caseEntity);
                draft.Finalize(caseEntity.Id, userId);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                var response = new
                {
                    draftId = draft.Id,
                    caseId = caseEntity.Id,
                    caseNumber = caseEntity.CaseNumber,
                    finalizedAtUtc = draft.FinalizedAtUtc,
                };
                return await SellingIdempotency.CompleteAsync(db, finalizationGate.Record!, userId, StatusCodes.Status201Created, response, ct);
            }
            catch
            {
                var finalized = await db.SellingCaseDrafts.AsNoTracking().AnyAsync(item =>
                    item.TenantId == tenantId && item.OrgId == sellerOrgId && item.Id == draftId && item.CaseId.HasValue, ct);
                if (!finalized)
                {
                    db.SellingIdempotencyRecords.Remove(finalizationGate.Record!);
                    await db.SaveChangesAsync(ct);
                }
                throw;
            }
        }
        finally
        {
            finalizationLock.Release();
        }
    }

    private static async Task<IResult> UpdateSellingCaseInformation(
        Guid caseId,
        CreateSellingCaseDraftRequest request,
        LiensDbContext db,
        ICurrentRequestContext context,
        CancellationToken ct)
    {
        var (tenantId, sellerOrgId, userId) = RequireSellerContext(context);
        var caseEntity = await db.Cases.FirstOrDefaultAsync(item =>
            item.TenantId == tenantId && item.OrgId == sellerOrgId && item.Id == caseId, ct);
        if (caseEntity is null) return Results.NotFound(new { message = "Case was not found for the seller organization." });
        var sellingDraftExists = await db.SellingCaseDrafts.AsNoTracking().AnyAsync(draft =>
            draft.TenantId == tenantId &&
            draft.OrgId == sellerOrgId &&
            draft.CaseId == caseId &&
            draft.FinalizedAtUtc.HasValue, ct);
        if (!sellingDraftExists)
            return Results.NotFound(new { message = "Selling case was not found for the seller organization." });

        var validation = await ValidateSellingCaseInformationAsync(
            db, tenantId, sellerOrgId, caseEntity.Status, request.AccidentTypeId, request.AccidentState,
            request.DateOfLoss, request.HandlingLawFirmId, request.CaseManagerId, request.CaseTrackingNotes, ct);
        if (validation.Error is not null) return validation.Error;

        var intake = validation.Value!;
        var metadata = ParseCaseMetadata(caseEntity.Notes);
        caseEntity.Update(
            caseEntity.ClientFirstName, caseEntity.ClientLastName, userId,
            title: caseEntity.Title,
            externalReference: caseEntity.ExternalReference,
            clientDob: caseEntity.ClientDob,
            clientPhone: caseEntity.ClientPhone,
            clientEmail: caseEntity.ClientEmail,
            clientAddress: caseEntity.ClientAddress,
            dateOfIncident: request.DateOfLoss,
            insuranceCarrier: caseEntity.InsuranceCarrier,
            policyNumber: caseEntity.PolicyNumber,
            claimNumber: caseEntity.ClaimNumber,
            description: caseEntity.Description,
            notes: ComposeSellingCaseNotes(request.CaseTrackingNotes, caseEntity.Notes, intake.AccidentTypeId, intake.AccidentTypeName, metadata.GetValueOrDefault("gender")),
            clientAddressLine1: caseEntity.ClientAddressLine1,
            clientCity: caseEntity.ClientCity,
            clientState: caseEntity.ClientState,
            clientPostalCode: caseEntity.ClientPostalCode,
            incidentState: intake.AccidentState,
            currentMedicalStatus: caseEntity.CurrentMedicalStatus,
            trackingFollowUpDate: caseEntity.TrackingFollowUpDate,
            minorComp: caseEntity.MinorComp,
            caseDropped: caseEntity.CaseDropped,
            attorneyContactPersonId: caseEntity.AttorneyContactPersonId);
        caseEntity.SetCanonicalCaseParties(intake.HandlingLawFirmId, intake.CaseManagerId, userId);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { caseId = caseEntity.Id, caseNumber = caseEntity.CaseNumber, caseStatus = caseEntity.Status });
    }

    private static async Task<IResult> UpdateSellingCasePlaintiff(
        Guid caseId,
        FinalizeSellingCaseDraftPlaintiffRequest request,
        LiensDbContext db,
        ICurrentRequestContext context,
        CancellationToken ct)
    {
        var (tenantId, sellerOrgId, userId) = RequireSellerContext(context);
        var caseEntity = await db.Cases.FirstOrDefaultAsync(item =>
            item.TenantId == tenantId && item.OrgId == sellerOrgId && item.Id == caseId, ct);
        if (caseEntity is null) return Results.NotFound(new { message = "Case was not found for the seller organization." });
        var sellingDraftExists = await db.SellingCaseDrafts.AsNoTracking().AnyAsync(draft =>
            draft.TenantId == tenantId &&
            draft.OrgId == sellerOrgId &&
            draft.CaseId == caseId &&
            draft.FinalizedAtUtc.HasValue, ct);
        if (!sellingDraftExists)
            return Results.NotFound(new { message = "Selling case was not found for the seller organization." });

        if (ValidatePlaintiff(request.FirstName, request.LastName, request.Birthdate, request.Email, request.Phone,
                request.Gender, request.Address, request.City, request.State, request.Zipcode) is { } plaintiffError)
            return plaintiffError;

        var metadata = ParseCaseMetadata(caseEntity.Notes);
        caseEntity.Update(
            request.FirstName, request.LastName, userId,
            title: caseEntity.Title,
            externalReference: caseEntity.ExternalReference,
            clientDob: request.Birthdate,
            clientPhone: request.Phone,
            clientEmail: request.Email,
            clientAddress: request.Address,
            dateOfIncident: caseEntity.DateOfIncident,
            insuranceCarrier: caseEntity.InsuranceCarrier,
            policyNumber: caseEntity.PolicyNumber,
            claimNumber: caseEntity.ClaimNumber,
            description: caseEntity.Description,
            notes: ComposeSellingCaseNotes(
                ExtractSellingCaseTrackingNotes(caseEntity.Notes),
                caseEntity.Notes,
                metadata.GetValueOrDefault("accidentTypeId"),
                metadata.GetValueOrDefault("accidentType"),
                request.Gender),
            clientAddressLine1: request.Address,
            clientCity: request.City,
            clientState: request.State,
            clientPostalCode: request.Zipcode,
            incidentState: caseEntity.IncidentState,
            currentMedicalStatus: caseEntity.CurrentMedicalStatus,
            trackingFollowUpDate: caseEntity.TrackingFollowUpDate,
            minorComp: caseEntity.MinorComp,
            caseDropped: caseEntity.CaseDropped,
            attorneyContactPersonId: caseEntity.AttorneyContactPersonId);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { caseId = caseEntity.Id, caseNumber = caseEntity.CaseNumber, caseStatus = caseEntity.Status });
    }

    private static async Task<IResult> GetSellingCase(
        Guid caseId,
        LiensDbContext db,
        ICurrentRequestContext context,
        CancellationToken ct)
    {
        var (tenantId, sellerOrgId, _) = RequireSellerContext(context);
        var caseEntity = await db.Cases.AsNoTracking().FirstOrDefaultAsync(item =>
            item.TenantId == tenantId && item.OrgId == sellerOrgId && item.Id == caseId, ct);
        if (caseEntity is null) return Results.NotFound(new { message = "Case was not found for the seller organization." });

        var sellingCaseDraft = await db.SellingCaseDrafts.AsNoTracking()
            .Where(draft => draft.TenantId == tenantId &&
                            draft.OrgId == sellerOrgId &&
                            draft.CaseId == caseId &&
                            draft.FinalizedAtUtc.HasValue)
            .Select(draft => new { draft.Id })
            .FirstOrDefaultAsync(ct);
        if (sellingCaseDraft is null)
            return Results.NotFound(new { message = "Selling case was not found for the seller organization." });

        var lookupNames = await LoadSellingCaseLookupNamesAsync(
            db, tenantId, sellerOrgId, [caseEntity], ct);
        return Results.Ok(MapSellingCase(sellingCaseDraft.Id, caseEntity, lookupNames));
    }

    private static async Task<IResult> GetSellingCases(
        LiensDbContext db,
        ICurrentRequestContext context,
        CancellationToken ct)
    {
        var (tenantId, sellerOrgId, _) = RequireSellerContext(context);
        var rows = await (
                from draft in db.SellingCaseDrafts.AsNoTracking()
                join caseEntity in db.Cases.AsNoTracking()
                    on draft.CaseId equals (Guid?)caseEntity.Id
                where draft.TenantId == tenantId &&
                      draft.OrgId == sellerOrgId &&
                      draft.FinalizedAtUtc.HasValue &&
                      caseEntity.TenantId == tenantId &&
                      caseEntity.OrgId == sellerOrgId
                orderby draft.FinalizedAtUtc descending, draft.CreatedAtUtc descending
                select new { DraftId = draft.Id, Case = caseEntity })
            .ToListAsync(ct);
        var lookupNames = await LoadSellingCaseLookupNamesAsync(
            db, tenantId, sellerOrgId, rows.Select(row => row.Case).ToList(), ct);
        var items = rows.Select(row => MapSellingCase(row.DraftId, row.Case, lookupNames)).ToList();

        return Results.Ok(new { totalCount = items.Count, items });
    }

    private static async Task<SellingCaseLookupNames> LoadSellingCaseLookupNamesAsync(
        LiensDbContext db,
        Guid tenantId,
        Guid sellerOrgId,
        IReadOnlyCollection<Case> cases,
        CancellationToken ct)
    {
        var accidentTypeReferences = cases
            .Select(caseEntity => ParseCaseMetadata(caseEntity.Notes).GetValueOrDefault("accidentTypeId"))
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Select(reference => reference!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var accidentTypeIds = accidentTypeReferences
            .Select(reference => Guid.TryParse(reference, out var id) ? id : (Guid?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();
        var accidentTypes = accidentTypeReferences.Count == 0
            ? []
            : await db.LookupValues.AsNoTracking()
                .Where(value =>
                    value.Category == LookupCategory.AccidentType &&
                    (value.TenantId == null || value.TenantId == tenantId) &&
                    (accidentTypeReferences.Contains(value.Code) || accidentTypeIds.Contains(value.Id)))
                .Select(value => new { value.Id, value.TenantId, value.Code, value.Name })
                .ToListAsync(ct);
        var accidentTypeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var accidentType in accidentTypes.OrderBy(value => value.TenantId.HasValue))
        {
            accidentTypeNames[accidentType.Id.ToString()] = accidentType.Name;
            accidentTypeNames[accidentType.Code] = accidentType.Name;
        }

        var lawFirmIds = cases
            .Where(caseEntity => caseEntity.HandlingLawFirmCompanyId.HasValue)
            .Select(caseEntity => caseEntity.HandlingLawFirmCompanyId!.Value)
            .Distinct()
            .ToList();
        var lawFirmNames = lawFirmIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Companies.AsNoTracking()
                .Where(company =>
                    company.TenantId == tenantId &&
                    company.OrgId == sellerOrgId &&
                    company.CompanyTypeId == CompanyDirectoryReferenceData.LawFirmId &&
                    lawFirmIds.Contains(company.Id))
                .ToDictionaryAsync(company => company.Id, company => company.Name, ct);

        var caseManagerIds = cases
            .Where(caseEntity => caseEntity.CaseManagerContactPersonId.HasValue)
            .Select(caseEntity => caseEntity.CaseManagerContactPersonId!.Value)
            .Distinct()
            .ToList();
        var caseManagerNames = caseManagerIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.CompanyContactPersons.AsNoTracking()
                .Where(contact =>
                    contact.TenantId == tenantId &&
                    caseManagerIds.Contains(contact.Id) &&
                    contact.Company != null &&
                    contact.Company.TenantId == tenantId &&
                    contact.Company.OrgId == sellerOrgId &&
                    contact.Company.CompanyTypeId == CompanyDirectoryReferenceData.LawFirmId)
                .ToDictionaryAsync(
                    contact => contact.Id,
                    contact => (contact.FirstName + " " + contact.LastName).Trim(),
                    ct);

        return new SellingCaseLookupNames(accidentTypeNames, lawFirmNames, caseManagerNames);
    }

    private static SellingCaseResponse MapSellingCase(
        Guid draftId,
        Case caseEntity,
        SellingCaseLookupNames lookupNames)
    {
        var metadata = ParseCaseMetadata(caseEntity.Notes);
        var accidentTypeId = metadata.GetValueOrDefault("accidentTypeId");
        var accidentTypeName = metadata.GetValueOrDefault("accidentType");
        if (string.IsNullOrWhiteSpace(accidentTypeName) &&
            !string.IsNullOrWhiteSpace(accidentTypeId) &&
            lookupNames.AccidentTypeNames.TryGetValue(accidentTypeId, out var resolvedAccidentTypeName))
            accidentTypeName = resolvedAccidentTypeName;
        return new SellingCaseResponse(
            draftId,
            caseEntity.Id,
            caseEntity.CaseNumber,
            caseEntity.Status,
            accidentTypeId,
            accidentTypeName,
            caseEntity.IncidentState,
            caseEntity.DateOfIncident,
            caseEntity.HandlingLawFirmCompanyId,
            caseEntity.HandlingLawFirmCompanyId is { } handlingLawFirmId
                ? lookupNames.LawFirmNames.GetValueOrDefault(handlingLawFirmId)
                : null,
            caseEntity.CaseManagerContactPersonId,
            caseEntity.CaseManagerContactPersonId is { } caseManagerId
                ? lookupNames.CaseManagerNames.GetValueOrDefault(caseManagerId)
                : null,
            ExtractSellingCaseTrackingNotes(caseEntity.Notes),
            caseEntity.ClientFirstName,
            caseEntity.ClientLastName,
            caseEntity.ClientDob,
            caseEntity.ClientEmail,
            caseEntity.ClientPhone,
            metadata.GetValueOrDefault("gender"),
            caseEntity.ClientAddressLine1 ?? caseEntity.ClientAddress,
            caseEntity.ClientCity,
            caseEntity.ClientState,
            caseEntity.ClientPostalCode);
    }

    private static async Task<IResult> GetLienDetail(
        Guid lienId,
        LiensDbContext db,
        ICurrentRequestContext context,
        CancellationToken ct)
    {
        var (tenantId, sellerOrgId, _) = RequireSellerContext(context);
        var lien = await GetSellerLienAsync(db, tenantId, sellerOrgId, lienId, ct);
        if (lien is null) return NotFoundLien(lienId);

        var sellingCaseId = lien.SellingCaseId ?? lien.CaseId;
        var caseEntity = sellingCaseId.HasValue
            ? await db.Cases.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == sellingCaseId.Value, ct)
            : null;
        var caseMetadata = ParseCaseMetadata(caseEntity?.Notes);
        var caseManagerId = ParseMetadataGuid(caseMetadata, "caseManagerId");
        var lawFirmId = ParseMetadataGuid(caseMetadata, "lawFirmId");
        var canonicalFundingCompany = lien.FundingCompanyCompanyId.HasValue
            ? await db.Companies.AsNoTracking().FirstOrDefaultAsync(c =>
                c.TenantId == tenantId && c.Id == lien.FundingCompanyCompanyId.Value, ct)
            : null;
        var canonicalFundingContact = lien.FundingCompanyContactPersonId.HasValue
            ? await db.CompanyContactPersons.AsNoTracking().FirstOrDefaultAsync(c =>
                c.TenantId == tenantId && c.Id == lien.FundingCompanyContactPersonId.Value, ct)
            : null;
        var canonicalMedicalProvider = lien.MedicalProviderCompanyId.HasValue
            ? await db.Companies.AsNoTracking().FirstOrDefaultAsync(c =>
                c.TenantId == tenantId &&
                c.OrgId == sellerOrgId &&
                c.Id == lien.MedicalProviderCompanyId.Value &&
                c.CompanyTypeId == CompanyDirectoryReferenceData.MedicalProviderId, ct)
            : null;
        var canonicalMedicalFacility = lien.MedicalFacilityCompanyId.HasValue
            ? await db.Companies.AsNoTracking().FirstOrDefaultAsync(c =>
                c.TenantId == tenantId &&
                c.OrgId == sellerOrgId &&
                c.Id == lien.MedicalFacilityCompanyId.Value &&
                c.CompanyTypeId == CompanyDirectoryReferenceData.MedicalFacilityId, ct)
            : null;
        var facility = lien.FacilityId.HasValue
            ? await db.Facilities.AsNoTracking().FirstOrDefaultAsync(f =>
                f.TenantId == tenantId && f.OrgId == sellerOrgId && f.Id == lien.FacilityId.Value, ct)
            : null;
        var legacyFacilityInfo = await db.ServicingItems.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.LienId == lien.Id && item.TaskType == "LegacyMedicalFacilityInfo")
            .OrderByDescending(item => item.CreatedAtUtc)
            .Select(item => item.Notes)
            .FirstOrDefaultAsync(ct);
        var legacyFacilityMetadata = ParseCaseMetadata(legacyFacilityInfo);
        var effectiveFacilityId = canonicalMedicalFacility?.Id ?? facility?.Id;
        var effectiveFacilityName = canonicalMedicalFacility?.Name ?? facility?.Name ??
            (legacyFacilityMetadata.TryGetValue("facilityName", out var importedFacilityName) ? importedFacilityName : null);
        var effectiveMedicalProviderName = canonicalMedicalProvider?.Name ??
            (legacyFacilityMetadata.TryGetValue("medicalProvider", out var importedProviderName) ? importedProviderName : null);
        var legacyFundingCompany = lien.FundingCompanyId.HasValue
            ? await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == lien.FundingCompanyId.Value, ct)
            : null;
        var legacyFundingContact = lien.FundingCompanyContactId.HasValue
            ? await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == lien.FundingCompanyContactId.Value, ct)
            : null;
        var canonicalCaseManager = caseEntity?.CaseManagerContactPersonId is { } canonicalCaseManagerId
            ? await db.CompanyContactPersons.AsNoTracking().FirstOrDefaultAsync(c =>
                c.TenantId == tenantId && c.Id == canonicalCaseManagerId, ct)
            : null;
        var canonicalLawFirm = caseEntity?.HandlingLawFirmCompanyId is { } canonicalLawFirmId
            ? await db.Companies.AsNoTracking().FirstOrDefaultAsync(c =>
                c.TenantId == tenantId && c.Id == canonicalLawFirmId, ct)
            : null;
        var legacyCaseManager = caseManagerId.HasValue
            ? await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == caseManagerId.Value, ct)
            : null;
        var legacyLawFirm = lawFirmId.HasValue
            ? await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == lawFirmId.Value, ct)
            : null;
        var effectiveFundingCompanyId = canonicalFundingCompany?.Id ?? legacyFundingCompany?.Id;
        var effectiveFundingCompanyName = canonicalFundingCompany?.Name ??
            (legacyFundingCompany is null ? lien.ExternalReference : DisplayName(legacyFundingCompany));
        var effectiveFundingContactId = canonicalFundingContact?.Id ?? legacyFundingContact?.Id;
        var effectiveFundingContactName = canonicalFundingContact is null
            ? legacyFundingContact?.DisplayName
            : DisplayName(canonicalFundingContact);
        var effectiveFundingContactEmail = canonicalFundingContact?.Email ?? legacyFundingContact?.Email;
        var effectiveCaseManagerId = canonicalCaseManager?.Id ?? legacyCaseManager?.Id;
        var effectiveCaseManagerName = canonicalCaseManager is null
            ? legacyCaseManager?.DisplayName
            : DisplayName(canonicalCaseManager);
        var effectiveLawFirmId = canonicalLawFirm?.Id ?? legacyLawFirm?.Id;
        var effectiveLawFirmName = canonicalLawFirm?.Name ??
            (legacyLawFirm is null ? null : DisplayName(legacyLawFirm));
        var pricing = await db.ServicingItems.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.LienId == lien.Id && item.TaskType == SellingMedicalPricingTaskType)
            .OrderBy(item => item.CreatedAtUtc)
            .Select(item => new { item.Id, item.Description, item.Notes, item.CreatedAtUtc })
            .ToListAsync(ct);
        var documents = await db.ServicingItems.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.LienId == lien.Id && item.TaskType == SellingDocumentTaskType)
            .OrderBy(item => item.CreatedAtUtc)
            .Select(item => new { item.Id, item.Description, item.Notes })
            .ToListAsync(ct);
        var offers = await db.LienOffers.AsNoTracking()
            .Where(offer => offer.TenantId == tenantId && offer.LienId == lien.Id)
            .OrderByDescending(offer => offer.OfferAmount)
            .ToListAsync(ct);
        var activity = await db.LienStatusHistories.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.LienId == lien.Id)
            .OrderByDescending(item => item.ChangedAtUtc)
            .Take(100)
            .Select(item => new { item.Id, item.Description, item.ChangedByUserId, item.ChangedAtUtc })
            .ToListAsync(ct);

        return Results.Ok(new
        {
            lienId = lien.Id,
            lienInformation = new
            {
                lien.LienNumber,
                lien.SellerStatus,
                lien.Status,
                lien.PurchaseDate,
                lien.InitialServiceDate,
                lien.EndServiceDate,
                lien.ReceivableDueDate,
                lien.ListingVisibility,
                lien.Notes,
                lien.BuyerMessage,
            },
            caseInformation = caseEntity is null ? null : new
            {
                caseEntity.Id,
                caseEntity.CaseNumber,
                caseEntity.Title,
                caseManagerId = effectiveCaseManagerId,
                caseManagerName = effectiveCaseManagerName,
                lawFirmId = effectiveLawFirmId,
                lawFirm = effectiveLawFirmName,
            },
            fundingCompany = lien.WithdrawnAtUtc.HasValue ||
                (!effectiveFundingCompanyId.HasValue && string.IsNullOrWhiteSpace(lien.ExternalReference)) ? null : new
            {
                id = effectiveFundingCompanyId,
                name = effectiveFundingCompanyName,
                contactPerson = effectiveFundingContactName,
                emailAddress = effectiveFundingContactEmail,
                contact = !effectiveFundingContactId.HasValue ? null : new { Id = effectiveFundingContactId.Value, name = effectiveFundingContactName },
            },
            facility = string.IsNullOrWhiteSpace(effectiveFacilityName) ? null : new
            {
                id = effectiveFacilityId,
                name = effectiveFacilityName,
                emailAddress = canonicalMedicalFacility?.Email ?? facility?.Email,
            },
            medicalProvider = string.IsNullOrWhiteSpace(effectiveMedicalProviderName) ? null : new
            {
                id = canonicalMedicalProvider?.Id,
                name = effectiveMedicalProviderName,
            },
            medicalPricing = new { lien.AskAmount, billingAmount = lien.OriginalAmount, rows = pricing },
            documents,
            saleReadiness = Readiness(lien, caseEntity is not null, pricing.Count, documents.Count),
            buyerOfferSummary = new
            {
                count = offers.Count,
                highestBidAmount = offers.Where(IsActiveOffer).Select(offer => offer.OfferAmount).DefaultIfEmpty().Max(),
            },
            activity,
            availableActions = AvailableActions(lien),
        });
    }

    private static async Task<IResult> GetArchivedStatus(
        Guid lienId,
        LiensDbContext db,
        ICurrentRequestContext context,
        CancellationToken ct)
    {
        var (tenantId, sellerOrgId, _) = RequireSellerContext(context);
        var lien = await GetSellerLienAsync(db, tenantId, sellerOrgId, lienId, ct);
        if (lien is null) return NotFoundLien(lienId);

        var isArchived = lien.ArchivedAtUtc.HasValue || lien.SellerStatus == SellingLienStatus.Archived;
        return Results.Ok(new
        {
            lienId = lien.Id,
            lien.LienNumber,
            isArchived,
            lien.SellerStatus,
            lien.ArchivedAtUtc,
            lien.ArchivedReason,
        });
    }

    private static async Task<IResult> GetLienActivity(
        Guid lienId,
        LiensDbContext db,
        ICurrentRequestContext context,
        CancellationToken ct)
    {
        var (tenantId, sellerOrgId, _) = RequireSellerContext(context);
        if (await GetSellerLienAsync(db, tenantId, sellerOrgId, lienId, ct) is null)
            return NotFoundLien(lienId);

        var items = await db.LienStatusHistories.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.LienId == lienId)
            .OrderByDescending(item => item.ChangedAtUtc)
            .Select(item => new
            {
                item.Id,
                eventType = "SellingLienActivity",
                item.Description,
                actorUserId = item.ChangedByUserId,
                timestampUtc = item.ChangedAtUtc,
            })
            .ToListAsync(ct);
        return Results.Ok(new { lienId, items });
    }

    private static async Task<IResult> GetSellerLienMessages(
        Guid lienId,
        LiensDbContext db,
        ICurrentRequestContext context,
        CancellationToken ct)
    {
        var (tenantId, sellerOrgId, _) = RequireSellerContext(context);
        if (await GetSellerLienAsync(db, tenantId, sellerOrgId, lienId, ct) is null)
            return NotFoundLien(lienId);

        var messageRows = await db.SellingPortalMessages
            .AsNoTracking()
            .Where(message =>
                message.TenantId == tenantId &&
                message.LienId == lienId &&
                message.SellerOrgId == sellerOrgId)
            .OrderBy(message => message.CreatedAtUtc)
            .ThenBy(message => message.Id)
            .ToListAsync(ct);
        var attachmentsByMessage = await LoadSellerMessageAttachmentsAsync(db, tenantId, sellerOrgId, lienId, messageRows, ct);
        var messages = messageRows
            .Select(message => MapSellerLienMessage(
                message,
                attachmentsByMessage.TryGetValue(message.Id, out var attachments) ? attachments : []))
            .ToList();

        return Results.Ok(new { items = messages });
    }

    private static async Task<IResult> PostSellerLienMessage(
        Guid lienId,
        HttpContext httpContext,
        INotificationPublisher notifications,
        ISellingBuyerAccessLinkService accessLinks,
        ILoggerFactory loggerFactory,
        IConfiguration configuration,
        ISellerOrganizationDisplayResolver sellerDisplayResolver,
        ILegacyDocumentUploadClient uploadClient,
        LiensDbContext db,
        ICurrentRequestContext context,
        CancellationToken ct)
    {
        var parsedRequest = await ReadSellerPortalMessageRequestAsync(httpContext.Request, ct);
        if (parsedRequest.Error is not null)
            return parsedRequest.Error;

        try
        {
            var (tenantId, sellerOrgId, _) = RequireSellerContext(context);
            if (await GetSellerLienAsync(db, tenantId, sellerOrgId, lienId, ct) is null)
                return NotFoundLien(lienId);

            var accessLink = await db.SellingBuyerAccessLinks
                .Where(link =>
                    link.TenantId == tenantId &&
                    link.LienId == lienId &&
                    link.SellerOrgId == sellerOrgId &&
                    !link.RevokedAtUtc.HasValue)
                .OrderByDescending(link => link.Purpose == SellingAccessLinkPurposes.ConfirmSaleSellerView)
                .ThenByDescending(link => link.NotificationSubmittedAtUtc ?? link.CreatedAtUtc)
                .ThenByDescending(link => link.Id)
                .FirstOrDefaultAsync(ct);

            if (accessLink is null)
            {
                return Results.Conflict(new
                {
                    error = new
                    {
                        code = "message_thread_unavailable",
                        message = "Messages can be sent after the lien has an offer thread with a buyer.",
                    },
                });
            }

            return await SellingPublicEndpoints.PostResolvedSellerPortalMessage(
                accessLink,
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
                (attachment, action) => BuildSellerMessageAttachmentActionUrl(lienId, attachment.Id, action),
                ct);
        }
        finally
        {
            SellingPublicEndpoints.DisposePortalMessageAttachmentUploads(parsedRequest.Attachments);
        }
    }

    private static Task<IResult> ViewSellerLienMessageAttachment(
        Guid lienId,
        Guid attachmentId,
        LiensDbContext db,
        ICurrentRequestContext context,
        IHttpClientFactory httpClientFactory,
        IServiceTokenIssuer serviceTokenIssuer,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
        => RedirectSellerLienMessageAttachment(
            lienId,
            attachmentId,
            "view",
            db,
            context,
            httpClientFactory,
            serviceTokenIssuer,
            loggerFactory,
            ct);

    private static Task<IResult> DownloadSellerLienMessageAttachment(
        Guid lienId,
        Guid attachmentId,
        LiensDbContext db,
        ICurrentRequestContext context,
        IHttpClientFactory httpClientFactory,
        IServiceTokenIssuer serviceTokenIssuer,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
        => RedirectSellerLienMessageAttachment(
            lienId,
            attachmentId,
            "download",
            db,
            context,
            httpClientFactory,
            serviceTokenIssuer,
            loggerFactory,
            ct);

    private static async Task<IResult> RedirectSellerLienMessageAttachment(
        Guid lienId,
        Guid attachmentId,
        string accessType,
        LiensDbContext db,
        ICurrentRequestContext context,
        IHttpClientFactory httpClientFactory,
        IServiceTokenIssuer serviceTokenIssuer,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var (tenantId, sellerOrgId, userId) = RequireSellerContext(context);
        if (await GetSellerLienAsync(db, tenantId, sellerOrgId, lienId, ct) is null)
            return NotFoundLien(lienId);

        var attachment = await db.SellingPortalMessageAttachments
            .AsNoTracking()
            .FirstOrDefaultAsync(item =>
                item.TenantId == tenantId &&
                item.LienId == lienId &&
                item.SellerOrgId == sellerOrgId &&
                item.Id == attachmentId,
                ct);
        if (attachment is null)
            return Results.NotFound(new { error = new { code = "attachment_not_found", message = "Attachment was not found for this lien message thread." } });

        var redeemUrl = await IssueSellerMessageAttachmentAccessUrlAsync(
            httpClientFactory,
            serviceTokenIssuer,
            loggerFactory,
            tenantId,
            sellerOrgId,
            userId,
            attachment.DocumentId,
            accessType,
            ct);
        if (string.IsNullOrWhiteSpace(redeemUrl))
            return Results.StatusCode(StatusCodes.Status502BadGateway);

        return Results.Redirect(redeemUrl, permanent: false, preserveMethod: false);
    }

    private static async Task<IResult> SaveLienInformation(
        Guid lienId,
        SaveSellingLienInformationRequest request,
        LiensDbContext db,
        ICurrentRequestContext context,
        CancellationToken ct)
    {
        var (tenantId, sellerOrgId, userId) = RequireSellerContext(context);
        var lien = await GetSellerLienAsync(db, tenantId, sellerOrgId, lienId, ct);
        if (lien is null) return NotFoundLien(lienId);
        if (lien.MovedToManagementAtUtc.HasValue)
            return Results.Conflict(new { error = new { code = "lien_moved_to_management", message = "This lien is managed through Liens Management and can no longer be changed through Selling intake." } });
        if (IntakeMutationBlocked(lien) is { } intakeError) return intakeError;

        var sellerStatus = NormalizeIntakeStatus(request.SellerStatus);
        if (sellerStatus is null) return ValidationError("sellerStatus", "sellerStatus must be Pending or Internal during intake.");
        var visibility = NormalizeVisibility(request.ListingVisibility);
        if (visibility is null) return ValidationError("listingVisibility", "listingVisibility must be Public or Private.");
        if (!TryParseOptionalDate(request.InitialServiceDate, "initialServiceDate", out var initialServiceDate, out var initialServiceDateError))
            return initialServiceDateError!;
        if (!TryParseOptionalDate(request.EndServiceDate, "endServiceDate", out var endServiceDate, out var endServiceDateError))
            return endServiceDateError!;
        if (!TryParseOptionalDate(request.ReceivableDueDate, "receivableDueDate", out var receivableDueDate, out var dueDateError))
            return dueDateError!;
        if (!TryParseOptionalString(request.Notes, "notes", out var notes, out var notesError))
            return notesError!;

        var effectiveInitialServiceDate = request.InitialServiceDate.ValueKind == JsonValueKind.Undefined
            ? lien.InitialServiceDate
            : initialServiceDate;
        var effectiveEndServiceDate = request.EndServiceDate.ValueKind == JsonValueKind.Undefined
            ? lien.EndServiceDate
            : endServiceDate;
        var effectiveNotes = request.Notes.ValueKind == JsonValueKind.Undefined
            ? lien.Notes
            : notes;

        lien.Update(
            lien.LienType, lien.OriginalAmount, userId, lien.ExternalReference,
            lien.SubjectFirstName, lien.SubjectLastName, lien.IsConfidential, lien.Jurisdiction,
            lien.IncidentDate, effectiveInitialServiceDate, effectiveEndServiceDate,
            lien.IsBulk, lien.IsServicing, lien.Description, effectiveNotes,
            purchaseDate: lien.PurchaseDate);
        lien.UpdateSellingAnalyticsFields(userId, sellerStatus: sellerStatus, listingVisibility: visibility);
        if (request.ReceivableDueDate.ValueKind != JsonValueKind.Undefined)
            lien.SetReceivableDueDate(receivableDueDate, userId);
        AddActivity(db, lien, userId, "Selling lien information updated.");
        await db.SaveChangesAsync(ct);
        return Results.Ok(new
        {
            lienId = lien.Id,
            lien.SellerStatus,
            lien.InitialServiceDate,
            lien.EndServiceDate,
            lien.ReceivableDueDate,
            lien.ListingVisibility,
        });
    }

    private static async Task<IResult> SaveCaseInformation(
        Guid lienId,
        SaveSellingCaseInformationRequest request,
        LiensDbContext db,
        ICurrentRequestContext context,
        CancellationToken ct)
    {
        var (tenantId, sellerOrgId, userId) = RequireSellerContext(context);
        var lien = await GetSellerLienAsync(db, tenantId, sellerOrgId, lienId, ct);
        if (lien is null) return NotFoundLien(lienId);
        if (IntakeMutationBlocked(lien) is { } intakeError) return intakeError;

        var fundingCompanyId = NormalizeOptionalGuid(request.FundingCompanyId);
        var fundingCompanyContactId = NormalizeOptionalGuid(request.FundingCompanyContactId);
        var facilityId = NormalizeOptionalGuid(request.FacilityId);

        Company? canonicalFundingCompany = null;
        Contact? legacyFundingCompany = null;
        CompanyContactPerson? canonicalFundingContact = null;
        if (fundingCompanyId.HasValue)
        {
            canonicalFundingCompany = await db.Companies.AsNoTracking().FirstOrDefaultAsync(company =>
                company.TenantId == tenantId &&
                company.OrgId == sellerOrgId &&
                company.Id == fundingCompanyId.Value &&
                company.CompanyTypeId == CompanyDirectoryReferenceData.FundingCompanyId &&
                company.IsActive, ct);
            if (canonicalFundingCompany is not null)
            {
                if (fundingCompanyContactId.HasValue)
                {
                    canonicalFundingContact = await db.CompanyContactPersons
                        .AsNoTracking()
                        .Include(contact => contact.ContactPersonType)
                        .FirstOrDefaultAsync(contact =>
                            contact.TenantId == tenantId &&
                            contact.Id == fundingCompanyContactId.Value &&
                            contact.CompanyId == canonicalFundingCompany.Id &&
                            contact.IsActive &&
                            contact.ContactPersonType != null &&
                            contact.ContactPersonType.IsActive &&
                            contact.ContactPersonType.CompanyTypeId == CompanyDirectoryReferenceData.FundingCompanyId &&
                            (contact.ContactPersonType.TenantId == null ||
                             (contact.ContactPersonType.TenantId == tenantId && contact.ContactPersonType.OrgId == sellerOrgId)),
                            ct);
                    if (canonicalFundingContact is null)
                        return ValidationError("fundingCompanyContactId", "Funding company contact must be active and belong to the selected funding company.");
                }
            }
            else
            {
                legacyFundingCompany = await GetFundingCompanyAsync(db, tenantId, fundingCompanyId.Value, ct);
                if (legacyFundingCompany is null)
                    return ValidationError("fundingCompanyId", "Funding company was not found in this tenant.");
                if (fundingCompanyContactId.HasValue)
                {
                    var contact = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c =>
                        c.TenantId == tenantId && c.Id == fundingCompanyContactId.Value && c.IsActive, ct);
                    if (contact is null || contact.OrgId != legacyFundingCompany.OrgId)
                        return ValidationError("fundingCompanyContactId", "Funding company contact must be active and belong to the selected funding company.");
                }
            }
        }
        else if (fundingCompanyContactId.HasValue)
        {
            return ValidationError("fundingCompanyContactId", "Funding company contact requires a funding company.");
        }

        Company? canonicalMedicalFacility = null;
        var hasLegacyFacility = false;
        if (facilityId.HasValue)
        {
            hasLegacyFacility = await db.Facilities.AsNoTracking().AnyAsync(facility =>
                facility.TenantId == tenantId &&
                facility.OrgId == sellerOrgId &&
                facility.Id == facilityId.Value &&
                facility.IsActive, ct);
            if (!hasLegacyFacility)
            {
                canonicalMedicalFacility = await db.Companies.AsNoTracking().FirstOrDefaultAsync(company =>
                    company.TenantId == tenantId &&
                    company.OrgId == sellerOrgId &&
                    company.Id == facilityId.Value &&
                    company.CompanyTypeId == CompanyDirectoryReferenceData.MedicalFacilityId &&
                    company.IsActive, ct);
            }
            if (!hasLegacyFacility && canonicalMedicalFacility is null)
                return ValidationError("facilityId", "Facility must be active and owned by the seller organization.");
        }

        Company? canonicalMedicalProvider = null;
        if (request.MedicalProviderId.HasValue)
        {
            canonicalMedicalProvider = await db.Companies.AsNoTracking().FirstOrDefaultAsync(company =>
                company.TenantId == tenantId &&
                company.OrgId == sellerOrgId &&
                company.Id == request.MedicalProviderId.Value &&
                company.CompanyTypeId == CompanyDirectoryReferenceData.MedicalProviderId &&
                company.IsActive, ct);
            if (canonicalMedicalProvider is null)
                return ValidationError("medicalProviderId", "Medical provider must be an active Medical Provider company owned by the seller organization.");
        }
        lien.SetSellingFundingReferences(
            legacyFundingCompany?.Id,
            legacyFundingCompany is null ? null : fundingCompanyContactId,
            canonicalFundingCompany?.Id,
            canonicalFundingContact?.Id,
            userId);
        if (hasLegacyFacility && facilityId.HasValue)
            lien.AttachFacility(facilityId.Value, userId);
        if (canonicalMedicalFacility is not null)
            lien.SetCanonicalMedicalFacility(canonicalMedicalFacility.Id, userId);
        if (canonicalMedicalProvider is not null)
            lien.SetCanonicalMedicalProvider(canonicalMedicalProvider.Id, userId);
        AddActivity(db, lien, userId, "Selling funding-company, facility, and medical-provider information updated.");
        await db.SaveChangesAsync(ct);
        return Results.Ok(new
        {
            lienId = lien.Id,
            fundingCompanyId = lien.FundingCompanyCompanyId ?? lien.FundingCompanyId,
            fundingCompanyContactId = lien.FundingCompanyContactPersonId ?? lien.FundingCompanyContactId,
            facilityId = lien.MedicalFacilityCompanyId ?? lien.FacilityId,
            medicalProviderId = lien.MedicalProviderCompanyId,
        });
    }

    private static async Task<IResult> SaveMedicalPricing(
        Guid lienId,
        SaveSellingMedicalPricingRequest request,
        LiensDbContext db,
        ICurrentRequestContext context,
        CancellationToken ct)
    {
        var (tenantId, sellerOrgId, userId) = RequireSellerContext(context);
        var lien = await GetSellerLienAsync(db, tenantId, sellerOrgId, lienId, ct);
        if (lien is null) return NotFoundLien(lienId);
        if (IntakeMutationBlocked(lien) is { } intakeError) return intakeError;
        if (request.AskAmount is < 0 || request.BillingAmount is < 0 || request.Rows.Any(row =>
                row.BillingAmount < 0 || row.MedicareCost < 0 || row.TargetSaleAmount < 0))
            return ValidationError("medicalPricing", "Ask, billing, Medicare, and target sale amounts must be non-negative.");
        if (request.Rows.Any(row => string.IsNullOrWhiteSpace(row.MedicalCode)))
            return ValidationError("rows", "Every medical pricing row requires medicalCode.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var priorRows = await db.ServicingItems
            .Where(item => item.TenantId == tenantId && item.LienId == lien.Id && item.TaskType == SellingMedicalPricingTaskType)
            .ToListAsync(ct);
        db.ServicingItems.RemoveRange(priorRows);
        foreach (var row in request.Rows)
        {
            db.ServicingItems.Add(ServicingItem.Create(
                tenantId, sellerOrgId, $"SMP-{Guid.CreateVersion7():N}".ToUpperInvariant(),
                SellingMedicalPricingTaskType, row.MedicalCode.Trim(), "Selling", userId,
                caseId: lien.CaseId, lienId: lien.Id,
                notes: JsonSerializer.Serialize(new
                {
                    row.MedicalCode,
                    row.Description,
                    row.ServiceDate,
                    row.BillingAmount,
                    row.MedicareCost,
                    targetSaleAmount = row.TargetSaleAmount,
                })));
        }
        lien.SetFinancials(request.BillingAmount ?? lien.OriginalAmount, userId);
        lien.UpdateSellingAnalyticsFields(userId, askAmount: request.AskAmount);
        AddActivity(db, lien, userId, "Selling medical pricing updated.");
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { lienId = lien.Id, lien.AskAmount, billingAmount = lien.OriginalAmount, rowCount = request.Rows.Count });
    }

    private static async Task<IResult> SaveDocuments(
        Guid lienId,
        SaveSellingDocumentsRequest request,
        LiensDbContext db,
        ISellingDocumentReferenceValidator documentValidator,
        ICurrentRequestContext context,
        CancellationToken ct)
    {
        var (tenantId, sellerOrgId, userId) = RequireSellerContext(context);
        var lien = await GetSellerLienAsync(db, tenantId, sellerOrgId, lienId, ct);
        if (lien is null) return NotFoundLien(lienId);
        if (IntakeMutationBlocked(lien) is { } intakeError) return intakeError;
        if (request.Documents.Any(document => document.DocumentId == Guid.Empty || string.IsNullOrWhiteSpace(document.DocumentType)))
            return ValidationError("documents", "Each document requires a documentId and documentType.");

        foreach (var document in request.Documents)
        {
            if (!await documentValidator.IsAccessibleAsync(tenantId, sellerOrgId, userId, lien.Id, lien.CaseId, document.DocumentId, ct))
                return ValidationError("documents", $"Document '{document.DocumentId}' is unavailable or is not owned by this seller lien/case.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var existing = await db.ServicingItems.Where(item =>
            item.TenantId == tenantId && item.LienId == lien.Id && item.TaskType == SellingDocumentTaskType).ToListAsync(ct);
        db.ServicingItems.RemoveRange(existing);
        foreach (var document in request.Documents)
        {
            db.ServicingItems.Add(ServicingItem.Create(
                tenantId, sellerOrgId, $"SDR-{Guid.CreateVersion7():N}".ToUpperInvariant(),
                SellingDocumentTaskType, document.DisplayName?.Trim() ?? document.DocumentId.ToString(), "Selling", userId,
                caseId: lien.CaseId, lienId: lien.Id,
                notes: JsonSerializer.Serialize(new { document.DocumentId, document.DocumentType, document.DisplayName })));
        }
        AddActivity(db, lien, userId, "Selling document references updated.");
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { lienId = lien.Id, documentCount = request.Documents.Count });
    }

    private static async Task<IResult> PrepareSale(
        Guid lienId,
        PrepareSellingLienRequest request,
        HttpRequest httpRequest,
        LiensDbContext db,
        ICurrentRequestContext context,
        CancellationToken ct)
    {
        var (tenantId, sellerOrgId, userId) = RequireSellerContext(context);
        if (!SellingIdempotency.TryGetKey(httpRequest, out var idempotencyKey, out var idempotencyError)) return idempotencyError!;
        var replay = await SellingIdempotency.GetReplayAsync(
            db, tenantId, "User", userId, "/api/liens/selling/liens/{lienId}/prepare-sale", "Lien", lienId.ToString(), idempotencyKey!, request, ct);
        if (replay is not null) return replay;
        var lien = await GetSellerLienAsync(db, tenantId, sellerOrgId, lienId, ct);
        if (lien is null) return NotFoundLien(lienId);
        if (SellingMutationBlocked(lien) is { } movedError) return movedError;
        if (!IntakeStatuses.Contains(lien.SellerStatus ?? string.Empty))
            return ValidationError("sellerStatus", "Only Pending or Internal liens can be prepared for sale.");

        Contact? buyerContact = null;
        CompanyContactPerson? canonicalBuyerContact = null;
        if (request.BuyerContactId is { } buyerContactId && buyerContactId != Guid.Empty)
        {
            buyerContact = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c =>
                c.TenantId == tenantId && c.Id == buyerContactId && c.IsActive, ct);
            if (buyerContact is null)
            {
                canonicalBuyerContact = await db.CompanyContactPersons
                    .AsNoTracking()
                    .Include(contact => contact.Company)
                    .Include(contact => contact.ContactPersonType)
                    .FirstOrDefaultAsync(contact =>
                        contact.TenantId == tenantId &&
                        contact.Id == buyerContactId &&
                        contact.IsActive &&
                        contact.Company != null &&
                        contact.Company.TenantId == tenantId &&
                        contact.Company.OrgId == sellerOrgId &&
                        contact.Company.CompanyTypeId == CompanyDirectoryReferenceData.FundingCompanyId &&
                        contact.Company.IsActive &&
                        contact.ContactPersonType != null &&
                        contact.ContactPersonType.IsActive &&
                        contact.ContactPersonType.CompanyTypeId == CompanyDirectoryReferenceData.FundingCompanyId &&
                        (contact.ContactPersonType.TenantId == null ||
                         (contact.ContactPersonType.TenantId == tenantId && contact.ContactPersonType.OrgId == sellerOrgId)),
                        ct);
                if (canonicalBuyerContact is null)
                    return ValidationError("buyerContactId", "Buyer contact must be an active legacy or Company Directory funding-company contact in this tenant.");
                if (request.BuyerFundingCompanyId is { } requestedCompanyId &&
                    requestedCompanyId != Guid.Empty &&
                    canonicalBuyerContact.CompanyId != requestedCompanyId)
                {
                    return ValidationError("buyerFundingCompanyId", "Buyer contact must belong to the selected funding company.");
                }
            }
        }
        var pricingRows = await db.ServicingItems.AnyAsync(item => item.TenantId == tenantId && item.LienId == lien.Id && item.TaskType == SellingMedicalPricingTaskType, ct);
        var documents = await db.ServicingItems.AnyAsync(item => item.TenantId == tenantId && item.LienId == lien.Id && item.TaskType == SellingDocumentTaskType, ct);
        if (!Readiness(lien, lien.CaseId.HasValue, pricingRows ? 1 : 0, documents ? 1 : 0, requireFundingCompany: false).ready)
            return ValidationError("saleReadiness", "Initial service date, case, pricing, ask amount, and at least one document are required before preparing a sale.");
        if (request.AskAmount is <= 0) return ValidationError("askAmount", "askAmount must be positive.");
        if (request.MessageToBuyer?.Trim().Length > 4000) return ValidationError("messageToBuyer", "messageToBuyer must not exceed 4000 characters.");
        var visibility = NormalizeVisibility(request.ListingVisibility);
        if (visibility is null) return ValidationError("listingVisibility", "listingVisibility must be Public or Private.");

        var started = await SellingIdempotency.TryBeginAsync(
            db, tenantId, "User", userId, "/api/liens/selling/liens/{lienId}/prepare-sale", "Lien", lienId.ToString(), idempotencyKey!, request, ct);
        if (started.Result is not null) return started.Result;

        if (canonicalBuyerContact is not null)
        {
            lien.SetSellingFundingReferences(
                null,
                null,
                canonicalBuyerContact.CompanyId,
                canonicalBuyerContact.Id,
                userId);
        }
        else if (buyerContact is not null)
        {
            // Legacy buyer organization IDs are carried by Contact.OrgId.
            lien.SetSellingFundingReferences(
                buyerContact.OrgId,
                buyerContact.Id,
                null,
                null,
                userId);
        }
        lien.UpdateSellingAnalyticsFields(userId,
            listingVisibility: visibility,
            askAmount: request.AskAmount);
        lien.SetBuyerMessage(request.MessageToBuyer, userId);
        AddActivity(db, lien, userId, "Sale preparation details saved; lien remains in intake until confirmation succeeds.");
        await db.SaveChangesAsync(ct);
        return await SellingIdempotency.CompleteAsync(db, started.Record!, userId, StatusCodes.Status200OK,
            new { lienId = lien.Id, lien.SellerStatus, lien.AskAmount, lien.ListingVisibility }, ct);
    }

    private static async Task<IResult> ConfirmSale(
        Guid lienId,
        ConfirmSellingLienSaleRequest request,
        HttpRequest httpRequest,
        LiensDbContext db,
        ISellingPortfolioService service,
        ICurrentRequestContext context,
        CancellationToken ct)
    {
        var (tenantId, sellerOrgId, userId) = RequireSellerContext(context);
        if (!SellingIdempotency.TryGetKey(httpRequest, out var idempotencyKey, out var idempotencyError)) return idempotencyError!;
        var replay = await SellingIdempotency.GetReplayAsync(
            db, tenantId, "User", userId, "/api/liens/selling/liens/{lienId}/confirm-sale", "Lien", lienId.ToString(), idempotencyKey!, request, ct);
        if (replay is not null) return replay;
        var lien = await GetSellerLienAsync(db, tenantId, sellerOrgId, lienId, ct);
        if (lien is null) return NotFoundLien(lienId);
        if (SellingMutationBlocked(lien) is { } movedError) return movedError;
        if (!request.ConfirmationAccepted)
            return ValidationError("confirmationAccepted", "Confirm the sale before submitting it.");
        var canConfirm = IntakeStatuses.Contains(lien.SellerStatus ?? string.Empty) ||
            lien.SellerStatus == SellingLienStatus.PreparedForSale;
        if (!canConfirm)
        {
            if (lien.SellerStatus == SellingLienStatus.SubmittedForSale)
                return Results.Conflict(new { error = new { code = "sale_already_submitted", message = "This lien has already been submitted for sale." } });
            return ValidationError("sellerStatus", "Only Pending, Internal, or legacy PreparedForSale liens can be confirmed for sale.");
        }
        if (IntakeStatuses.Contains(lien.SellerStatus ?? string.Empty))
        {
            var pricingRows = await db.ServicingItems.AnyAsync(item =>
                item.TenantId == tenantId && item.LienId == lien.Id && item.TaskType == SellingMedicalPricingTaskType, ct);
            var documents = await db.ServicingItems.AnyAsync(item =>
                item.TenantId == tenantId && item.LienId == lien.Id && item.TaskType == SellingDocumentTaskType, ct);
            if (!Readiness(lien, lien.CaseId.HasValue, pricingRows ? 1 : 0, documents ? 1 : 0).ready)
                return ValidationError("saleReadiness", "The lien must have case, buyer, pricing, ask amount, and document details before it can be confirmed for sale.");
        }

        // A second client key must not race the intake -> Submitted
        // transition. This one-per-lien gate is persisted before invoking the
        // legacy service, whose notification/link work runs in its own unit of
        // work transaction.
        var transitionGate = await SellingIdempotency.TryBeginAsync(
            db, tenantId, "LienTransition", lienId, "/api/liens/selling/liens/{lienId}/confirm-sale", "Lien", lienId.ToString(),
            BuildSubmitForSaleTransitionKey(lien), request: null, ct: ct);
        if (transitionGate.Result is not null)
        {
            return Results.Conflict(new { error = new { code = "sale_submission_in_progress", message = "This lien is already being submitted for sale. Retry with the original idempotency key." } });
        }

        var started = await SellingIdempotency.TryBeginAsync(
            db, tenantId, "User", userId, "/api/liens/selling/liens/{lienId}/confirm-sale", "Lien", lienId.ToString(), idempotencyKey!, request, ct);
        if (started.Result is not null) return started.Result;
        ConfirmSellingLienSaleResponse result;
        try
        {
            result = await service.ConfirmSaleAsync(tenantId, lienId, sellerOrgId, userId, request, ct);
        }
        catch
        {
            var transitioned = await db.Liens.AsNoTracking().AnyAsync(item =>
                item.TenantId == tenantId && item.Id == lienId &&
                item.Status == LienStatus.Offered && item.SellerStatus == SellingLienStatus.SubmittedForSale,
                ct);
            if (!transitioned)
            {
                db.SellingIdempotencyRecords.Remove(started.Record!);
                db.SellingIdempotencyRecords.Remove(transitionGate.Record!);
                await db.SaveChangesAsync(ct);
            }
            throw;
        }
        // A newly generated portal URL contains a bearer capability. The
        // one-time API response may contain it, but durable replay data must
        // never retain that capability.
        var replayBody = new ConfirmSellingLienSaleResponse
        {
            LienId = result.LienId,
            LienCode = result.LienCode,
            Status = result.Status,
            SellerStatus = result.SellerStatus,
            AskAmount = result.AskAmount,
            OfferPrice = result.OfferPrice,
            SubmittedForSaleAtUtc = result.SubmittedForSaleAtUtc,
            SoldAtUtc = result.SoldAtUtc,
            Notification = result.Notification is null ? null : new ConfirmSellingLienBuyerNotificationResponse
            {
                Requested = result.Notification.Requested,
                Submitted = result.Notification.Submitted,
                NotificationId = result.Notification.NotificationId,
                NotificationStatus = result.Notification.NotificationStatus,
                FailureMessage = result.Notification.FailureMessage,
                BuyerAccessLinkId = result.Notification.BuyerAccessLinkId,
                BuyerPortalUrl = null,
                ExpiresAtUtc = result.Notification.ExpiresAtUtc,
                BuyerContactId = result.Notification.BuyerContactId,
                BuyerOrgId = result.Notification.BuyerOrgId,
                BuyerEmail = result.Notification.BuyerEmail,
            },
        };
        AddActivity(db, lien, userId, "Lien submitted for sale.");
        await SellingIdempotency.CompleteAsync(db, started.Record!, userId, StatusCodes.Status200OK, replayBody, ct);
        await SellingIdempotency.CompleteAsync(db, transitionGate.Record!, userId, StatusCodes.Status200OK, replayBody, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> MoveToManagement(
        Guid lienId,
        MoveSellingLienToManagementRequest request,
        HttpRequest httpRequest,
        LiensDbContext db,
        ICurrentRequestContext context,
        ISellerOrganizationDisplayResolver sellerDisplayResolver,
        CancellationToken ct)
    {
        const string route = "/api/liens/selling/liens/{lienId}/move-to-management";
        var (tenantId, sellerOrgId, userId) = RequireSellerContext(context);
        if (!SellingIdempotency.TryGetKey(httpRequest, out var idempotencyKey, out var idempotencyError)) return idempotencyError!;
        var replay = await SellingIdempotency.GetReplayAsync(
            db, tenantId, "User", userId, route, "Lien", lienId.ToString(), idempotencyKey!, request, ct);
        if (replay is not null) return replay;

        var lien = await GetSellerLienAsync(db, tenantId, sellerOrgId, lienId, ct);
        if (lien is null) return NotFoundLien(lienId);
        if (lien.MovedToManagementAtUtc.HasValue)
            return Results.Conflict(new { error = new { code = "lien_already_moved_to_management", message = "This lien has already been moved to management." } });
        if (!IsMoveToManagementEligible(lien))
            return Results.Conflict(new { error = new { code = "lien_not_eligible_for_management", message = "Only Selling Pending-tab or existing Internal liens can be moved to management." } });

        var existingCase = lien.CaseId.HasValue && await db.Cases.AnyAsync(item =>
            item.Id == lien.CaseId.Value && item.TenantId == tenantId && item.OrgId == sellerOrgId, ct);
        if (lien.CaseId.HasValue && !existingCase)
            return ValidationError("caseId", "The lien case was not found in this tenant and seller organization.");

        var sellerDisplay = await sellerDisplayResolver.ResolveAsync(
            tenantId,
            sellerOrgId,
            Array.Empty<Contact>(),
            userId,
            context.Email,
            includeIdentityOwnerEmailFallback: true,
            ct);
        var tenantFundingCompanyName = string.Equals(
            sellerDisplay.Company,
            "Seller company unavailable",
            StringComparison.OrdinalIgnoreCase)
            ? null
            : sellerDisplay.Company.Trim();
        if (string.IsNullOrWhiteSpace(tenantFundingCompanyName))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Tenant name unavailable",
                detail: "The tenant name could not be resolved, so the Management funding company was not created.");
        }

        var lienTransition = await SellingIdempotency.TryBeginAsync(
            db, tenantId, "LienStateTransition", lienId, "/api/liens/selling/liens/{lienId}/state-transition", "Lien", lienId.ToString(),
            "lien-state-transition-v1", request: null, ct: ct);
        if (lienTransition.Result is not null)
            return Results.Conflict(new { error = new { code = "lien_transition_in_progress", message = "This lien is changing state and cannot be moved to management." } });
        var started = await SellingIdempotency.TryBeginAsync(
            db, tenantId, "User", userId, route, "Lien", lienId.ToString(), idempotencyKey!, request, ct);
        if (started.Result is not null)
        {
            db.SellingIdempotencyRecords.Remove(lienTransition.Record!);
            await db.SaveChangesAsync(ct);
            return started.Result;
        }

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (!lien.CaseId.HasValue)
            {
                var caseEntity = CreateManagementCase(lien, tenantId, sellerOrgId, userId);
                db.Cases.Add(caseEntity);
                lien.AttachCase(caseEntity.Id, userId);
            }

            var tenantFundingCompany = await EnsureTenantFundingCompanyAsync(
                db,
                tenantId,
                sellerOrgId,
                tenantFundingCompanyName,
                userId,
                ct);
            lien.SetSellingFundingReferences(
                legacyFundingCompanyId: null,
                legacyFundingCompanyContactId: null,
                fundingCompanyCompanyId: tenantFundingCompany.Id,
                fundingCompanyContactPersonId: null,
                updatedByUserId: userId);

            if (IsSubmittedLien(lien))
            {
                lien.ReturnToSellingPending(userId, recordWithdrawal: true);

                var accessLinks = await db.SellingBuyerAccessLinks
                    .Where(link => link.TenantId == tenantId && link.LienId == lienId && !link.RevokedAtUtc.HasValue)
                    .ToListAsync(ct);
                foreach (var accessLink in accessLinks)
                    accessLink.Revoke(userId);

                var pendingOffers = await db.LienOffers
                    .Where(offer => offer.TenantId == tenantId && offer.LienId == lienId && offer.Status == OfferStatus.Pending)
                    .ToListAsync(ct);
                foreach (var offer in pendingOffers)
                {
                    if (offer.IsExpired)
                        offer.Expire(userId);
                    else
                        offer.Withdraw(userId);
                }
            }

            lien.SetPurchaseDate(DateOnly.FromDateTime(DateTime.UtcNow), userId);
            await EnsureManagementAmountsAsync(db, lien, tenantId, sellerOrgId, userId, ct);
            await EnsureManagementPartyInfoAsync(
                db,
                tenantId,
                sellerOrgId,
                lien,
                lien.CaseId!.Value,
                userId,
                ct);
            lien.MoveToInternalManagement(userId);

            AddActivity(db, lien, userId,
                $"Lien moved to management case {lien.CaseId}; seller status set to Internal. {request.Reason}".Trim());
            var response = new
            {
                lienId = lien.Id,
                sellerStatus = lien.SellerStatus,
                status = lien.Status,
                caseId = lien.CaseId,
                sellingCaseId = lien.SellingCaseId,
            };
            db.SellingIdempotencyRecords.Remove(lienTransition.Record!);
            var completed = await SellingIdempotency.CompleteAsync(db, started.Record!, userId, StatusCodes.Status200OK, response, ct);
            await transaction.CommitAsync(ct);
            return completed;
        }
        catch
        {
            var failedRecordIds = new[] { started.Record!.Id, lienTransition.Record!.Id };
            db.ChangeTracker.Clear();
            var failedRecords = await db.SellingIdempotencyRecords.Where(record =>
                record.TenantId == tenantId && failedRecordIds.Contains(record.Id) &&
                record.ProcessingState != SellingIdempotencyRecord.Completed).ToListAsync(CancellationToken.None);
            db.SellingIdempotencyRecords.RemoveRange(failedRecords);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<IResult> WithdrawSale(
        Guid lienId,
        WithdrawSellingLienRequest request,
        HttpRequest httpRequest,
        LiensDbContext db,
        ICurrentRequestContext context,
        CancellationToken ct)
    {
        var (tenantId, sellerOrgId, userId) = RequireSellerContext(context);
        if (!SellingIdempotency.TryGetKey(httpRequest, out var idempotencyKey, out var idempotencyError)) return idempotencyError!;
        var replay = await SellingIdempotency.GetReplayAsync(
            db, tenantId, "User", userId, "/api/liens/selling/liens/{lienId}/withdraw-sale", "Lien", lienId.ToString(), idempotencyKey!, request, ct);
        if (replay is not null) return replay;
        var lien = await GetSellerLienAsync(db, tenantId, sellerOrgId, lienId, ct);
        if (lien is null) return NotFoundLien(lienId);
        if (SellingMutationBlocked(lien) is { } movedError) return movedError;
        if (lien.SellerStatus != SellingLienStatus.SubmittedForSale || lien.Status != LienStatus.Offered)
            return ValidationError("sellerStatus", "Only SubmittedForSale liens can be withdrawn.");
        var lienTransition = await SellingIdempotency.TryBeginAsync(
            db, tenantId, "LienStateTransition", lienId, "/api/liens/selling/liens/{lienId}/state-transition", "Lien", lienId.ToString(),
            "lien-state-transition-v1", request: null, ct: ct);
        if (lienTransition.Result is not null)
            return Results.Conflict(new { error = new { code = "lien_transition_in_progress", message = "This lien is changing state and cannot be withdrawn." } });
        var started = await SellingIdempotency.TryBeginAsync(
            db, tenantId, "User", userId, "/api/liens/selling/liens/{lienId}/withdraw-sale", "Lien", lienId.ToString(), idempotencyKey!, request, ct);
        if (started.Result is not null)
        {
            db.SellingIdempotencyRecords.Remove(lienTransition.Record!);
            await db.SaveChangesAsync(ct);
            return started.Result;
        }
        try
        {
            lien.ReturnToSellingPending(userId, recordWithdrawal: true);
            lien.ClearSellingFundingReferences(userId);

            var accessLinks = await db.SellingBuyerAccessLinks
                .Where(link =>
                    link.TenantId == tenantId &&
                    link.LienId == lienId &&
                    !link.RevokedAtUtc.HasValue)
                .ToListAsync(ct);
            foreach (var accessLink in accessLinks)
                accessLink.Revoke(userId);

            var pendingOffers = await db.LienOffers
                .Where(offer =>
                    offer.TenantId == tenantId &&
                    offer.LienId == lienId &&
                    offer.Status == OfferStatus.Pending)
                .ToListAsync(ct);
            foreach (var offer in pendingOffers)
            {
                if (offer.IsExpired)
                    offer.Expire(userId);
                else
                    offer.Withdraw(userId);
            }

            AddActivity(db, lien, userId, $"Sale withdrawn; lien returned to Pending. {request.Reason}".Trim());
            var response = new { lienId = lien.Id, lien.SellerStatus, lien.Status, lien.WithdrawnAtUtc };
            db.SellingIdempotencyRecords.Remove(lienTransition.Record!);
            return await SellingIdempotency.CompleteAsync(
                db,
                started.Record!,
                userId,
                StatusCodes.Status200OK,
                response,
                ct);
        }
        catch
        {
            var failedRecordIds = new[] { started.Record!.Id, lienTransition.Record!.Id };
            db.ChangeTracker.Clear();
            var failedRecords = await db.SellingIdempotencyRecords
                .Where(record =>
                    record.TenantId == tenantId &&
                    failedRecordIds.Contains(record.Id) &&
                    record.ProcessingState != SellingIdempotencyRecord.Completed)
                .ToListAsync(CancellationToken.None);
            db.SellingIdempotencyRecords.RemoveRange(failedRecords);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<IResult> MoveToManagementV2(
        Guid lienId,
        MoveSellingLienToManagementV2Request request,
        HttpRequest httpRequest,
        LiensDbContext db,
        ICurrentRequestContext context,
        CancellationToken ct)
    {
        var (tenantId, sellerOrgId, userId) = RequireSellerContext(context);
        if (!SellingIdempotency.TryGetKey(httpRequest, out var idempotencyKey, out var idempotencyError)) return idempotencyError!;

        const string route = "/api/liens/selling/liens/{lienId}/move-to-management-v2";
        var replay = await SellingIdempotency.GetReplayAsync(
            db, tenantId, "User", userId, route, "Lien", lienId.ToString(), idempotencyKey!, request, ct);
        if (replay is not null) return replay;

        var lien = await GetSellerLienAsync(db, tenantId, sellerOrgId, lienId, ct);
        if (lien is null) return NotFoundLien(lienId);
        if (lien.MovedToManagementAtUtc.HasValue)
            return Results.Conflict(new { error = new { code = "lien_already_moved_to_management", message = "This lien has already been moved to management." } });
        if (!IsMoveToManagementEligible(lien))
            return ValidationError("sellerStatus", "Only Selling Pending-tab or existing Internal liens can be moved to management.");

        var transitionGate = await SellingIdempotency.TryBeginAsync(
            db, tenantId, "LienStateTransition", lienId, route, "Lien", lienId.ToString(),
            $"move-to-management-v2-transition:{lien.UpdatedAtUtc.Ticks.ToString(CultureInfo.InvariantCulture)}", request: null, ct: ct);
        if (transitionGate.Result is not null)
            return Results.Conflict(new { error = new { code = "lien_transition_in_progress", message = "This lien is already being moved to management. Retry with the original idempotency key." } });

        SellingIdempotency.IdempotencyStart? started = null;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            started = await SellingIdempotency.TryBeginAsync(
                db, tenantId, "User", userId, route, "Lien", lienId.ToString(), idempotencyKey!, request, ct);
            if (started.Result is not null)
            {
                db.SellingIdempotencyRecords.Remove(transitionGate.Record!);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return started.Result;
            }

            await db.Entry(lien).ReloadAsync(ct);
            if (lien.MovedToManagementAtUtc.HasValue)
            {
                db.SellingIdempotencyRecords.Remove(started.Record!);
                db.SellingIdempotencyRecords.Remove(transitionGate.Record!);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.Conflict(new { error = new { code = "lien_already_moved_to_management", message = "This lien has already been moved to management." } });
            }
            if (!IsMoveToManagementEligible(lien))
            {
                db.SellingIdempotencyRecords.Remove(started.Record!);
                db.SellingIdempotencyRecords.Remove(transitionGate.Record!);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return ValidationError("sellerStatus", "Only Selling Pending-tab or existing Internal liens can be moved to management.");
            }
            if (ValidateMoveToManagementCaseInfo(request.CaseInfo) is { } caseInfoError)
            {
                db.SellingIdempotencyRecords.Remove(started.Record!);
                db.SellingIdempotencyRecords.Remove(transitionGate.Record!);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return caseInfoError;
            }
            if (await ValidateExistingMoveToManagementCaseAsync(db, tenantId, sellerOrgId, lien, ct) is { } caseError)
            {
                db.SellingIdempotencyRecords.Remove(started.Record!);
                db.SellingIdempotencyRecords.Remove(transitionGate.Record!);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return caseError;
            }

            var caseResolution = await ResolveMoveToManagementCaseAsync(db, tenantId, sellerOrgId, userId, lien, request.CaseInfo, ct);
            ApplyMoveToManagementCaseInfo(caseResolution.Case, request.CaseInfo, userId);
            if (request.CaseInfo?.IsServicing is { } isServicing)
            {
                lien.Update(
                    lien.LienType,
                    lien.OriginalAmount,
                    userId,
                    lien.ExternalReference,
                    lien.SubjectFirstName,
                    lien.SubjectLastName,
                    lien.IsConfidential,
                    lien.Jurisdiction,
                    lien.IncidentDate,
                    lien.InitialServiceDate,
                    lien.EndServiceDate,
                    lien.IsBulk,
                    isServicing ? "true" : "false",
                    lien.Description,
                    lien.Notes,
                    lien.PurchaseDate);
            }

            if (IsSubmittedLien(lien))
            {
                lien.ReturnToSellingPending(userId);
                var links = await db.SellingBuyerAccessLinks
                    .Where(link => link.TenantId == tenantId && link.LienId == lien.Id && !link.RevokedAtUtc.HasValue)
                    .ToListAsync(ct);
                foreach (var link in links)
                    link.Revoke(userId);

                var activeOffers = await db.LienOffers
                    .Where(offer => offer.TenantId == tenantId && offer.LienId == lien.Id)
                    .ToListAsync(ct);
                foreach (var offer in activeOffers.Where(IsActiveOffer))
                    offer.Expire(userId);
            }

            await ReassignLienServicingItemsToCaseAsync(db, tenantId, lien.Id, caseResolution.Case.Id, userId, ct);
            await EnsureManagementAmountsAsync(db, tenantId, sellerOrgId, lien, caseResolution.Case.Id, userId, ct);
            await EnsureManagementPartyInfoAsync(db, tenantId, sellerOrgId, lien, caseResolution.Case.Id, userId, ct);
            lien.AttachCase(caseResolution.Case.Id, userId);
            lien.MoveToInternalManagement(userId);
            AddActivity(db, lien, userId, caseResolution.CaseCreated
                ? "Lien moved to management and linked to a new case."
                : "Lien moved to management and added to an existing case.");

            var response = new
            {
                lienId = lien.Id,
                caseId = lien.CaseId,
                sellingCaseId = lien.SellingCaseId,
                caseCreated = caseResolution.CaseCreated,
                caseNumber = caseResolution.Case.CaseNumber,
                sellerStatus = lien.SellerStatus,
                status = lien.Status,
                message = caseResolution.CaseCreated
                    ? "Lien moved to management and linked to a new case."
                    : "Lien moved to management and added to an existing case.",
            };

            await db.SaveChangesAsync(ct);
            var completed = await SellingIdempotency.CompleteAsync(db, started.Record!, userId, StatusCodes.Status200OK, response, ct);
            await SellingIdempotency.CompleteAsync(db, transitionGate.Record!, userId, StatusCodes.Status200OK, response, ct);
            await transaction.CommitAsync(ct);
            return completed;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            if (started?.Record is not null && started.Record.ProcessingState != SellingIdempotencyRecord.Completed)
                db.SellingIdempotencyRecords.Remove(started.Record);
            db.SellingIdempotencyRecords.Remove(transitionGate.Record!);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<IResult> ArchiveLien(
        Guid lienId,
        ArchiveSellingLienRequest request,
        HttpRequest httpRequest,
        LiensDbContext db,
        ICurrentRequestContext context,
        CancellationToken ct)
    {
        var (tenantId, sellerOrgId, userId) = RequireSellerContext(context);
        if (!SellingIdempotency.TryGetKey(httpRequest, out var idempotencyKey, out var idempotencyError)) return idempotencyError!;
        var replay = await SellingIdempotency.GetReplayAsync(
            db, tenantId, "User", userId, "/api/liens/selling/liens/{lienId}/archive", "Lien", lienId.ToString(), idempotencyKey!, request, ct);
        if (replay is not null) return replay;
        var lien = await GetSellerLienAsync(db, tenantId, sellerOrgId, lienId, ct);
        if (lien is null) return NotFoundLien(lienId);
        if (SellingMutationBlocked(lien) is { } movedError) return movedError;
        if (lien.SellerStatus is SellingLienStatus.Sold or SellingLienStatus.Archived)
            return ValidationError("sellerStatus", "Sold or already archived liens cannot be archived through this workflow.");
        var lienTransition = await SellingIdempotency.TryBeginAsync(
            db, tenantId, "LienStateTransition", lienId, "/api/liens/selling/liens/{lienId}/state-transition", "Lien", lienId.ToString(),
            "lien-state-transition-v1", request: null, ct: ct);
        if (lienTransition.Result is not null)
            return Results.Conflict(new { error = new { code = "lien_transition_in_progress", message = "This lien is changing state and cannot be archived." } });
        var started = await SellingIdempotency.TryBeginAsync(
            db, tenantId, "User", userId, "/api/liens/selling/liens/{lienId}/archive", "Lien", lienId.ToString(), idempotencyKey!, request, ct);
        if (started.Result is not null)
        {
            db.SellingIdempotencyRecords.Remove(lienTransition.Record!);
            await db.SaveChangesAsync(ct);
            return started.Result;
        }
        lien.UpdateSellingAnalyticsFields(userId,
            sellerStatus: SellingLienStatus.Archived,
            archivedAtUtc: DateTime.UtcNow,
            archivedReason: request.Reason);
        AddActivity(db, lien, userId, "Lien archived.");
        await db.SaveChangesAsync(ct);
        var response = new { lienId = lien.Id, lien.SellerStatus, lien.ArchivedAtUtc, lien.ArchivedReason };
        var completed = await SellingIdempotency.CompleteAsync(db, started.Record!, userId, StatusCodes.Status200OK, response, ct);
        await SellingIdempotency.CompleteAsync(db, lienTransition.Record!, userId, StatusCodes.Status200OK, response, ct);
        return completed;
    }

    private static async Task<IResult> RestoreLien(
        Guid lienId,
        HttpRequest httpRequest,
        LiensDbContext db,
        ICurrentRequestContext context,
        CancellationToken ct)
    {
        var (tenantId, sellerOrgId, userId) = RequireSellerContext(context);
        if (!SellingIdempotency.TryGetKey(httpRequest, out var idempotencyKey, out var idempotencyError)) return idempotencyError!;
        var request = new { };
        var replay = await SellingIdempotency.GetReplayAsync(
            db, tenantId, "User", userId, "/api/liens/selling/liens/{lienId}/restore", "Lien", lienId.ToString(), idempotencyKey!, request, ct);
        if (replay is not null) return replay;
        var lien = await GetSellerLienAsync(db, tenantId, sellerOrgId, lienId, ct);
        if (lien is null) return NotFoundLien(lienId);
        if (SellingMutationBlocked(lien) is { } movedError) return movedError;
        if (lien.ArchivedAtUtc is null && lien.SellerStatus != SellingLienStatus.Archived)
            return ValidationError("sellerStatus", "Only archived liens can be restored.");
        var started = await SellingIdempotency.TryBeginAsync(
            db, tenantId, "User", userId, "/api/liens/selling/liens/{lienId}/restore", "Lien", lienId.ToString(), idempotencyKey!, request, ct);
        if (started.Result is not null) return started.Result;
        lien.RestoreFromArchive(userId);
        AddActivity(db, lien, userId, "Lien restored from archive.");
        await db.SaveChangesAsync(ct);
        var response = new { lienId = lien.Id, lien.SellerStatus, lien.ArchivedAtUtc, lien.ArchivedReason };
        return await SellingIdempotency.CompleteAsync(db, started.Record!, userId, StatusCodes.Status200OK, response, ct);
    }

    private static async Task<IResult> CreateBuyerAccessLink(
        Guid lienId,
        CreateSellingBuyerAccessLinkRequest request,
        HttpRequest httpRequest,
        LiensDbContext db,
        ISellingBuyerAccessLinkService links,
        ICurrentRequestContext context,
        CancellationToken ct)
    {
        var (tenantId, sellerOrgId, userId) = RequireSellerContext(context);
        if (!SellingIdempotency.TryGetKey(httpRequest, out var idempotencyKey, out var idempotencyError)) return idempotencyError!;
        var replay = await SellingIdempotency.GetReplayAsync(
            db, tenantId, "User", userId, "/api/liens/selling/liens/{lienId}/buyer-access-links", "Lien", lienId.ToString(), idempotencyKey!, request, ct);
        if (replay is not null) return replay;
        var lien = await GetSellerLienAsync(db, tenantId, sellerOrgId, lienId, ct);
        if (lien is null) return NotFoundLien(lienId);
        if (SellingMutationBlocked(lien) is { } movedError) return movedError;
        if (!IsSubmittedLien(lien)) return ValidationError("sellerStatus", "Buyer access links require a submitted-for-sale lien.");
        var buyerCompany = await GetFundingCompanyAsync(db, tenantId, request.BuyerFundingCompanyId, ct);
        var buyerContact = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c =>
            c.TenantId == tenantId && c.Id == request.BuyerContactId && c.IsActive, ct);
        if (buyerCompany is null || buyerContact is null || buyerContact.OrgId != buyerCompany.OrgId)
            return ValidationError("buyerContactId", "The buyer funding company and contact must be active and related within this tenant.");
        var started = await SellingIdempotency.TryBeginAsync(
            db, tenantId, "User", userId, "/api/liens/selling/liens/{lienId}/buyer-access-links", "Lien", lienId.ToString(), idempotencyKey!, request, ct);
        if (started.Result is not null) return started.Result;
        var expires = Math.Clamp(request.ExpiresInHours ?? 168, 1, 24 * 30);
        var result = await links.CreateAsync(tenantId, lien.Id, sellerOrgId, buyerCompany.OrgId, buyerContact.Id,
            userId, "/api/liens/selling/liens/{lienId}/buyer-access-links", idempotencyKey!, TimeSpan.FromHours(expires), ct);
        AddActivity(db, lien, userId, "Buyer access link created.");
        await db.SaveChangesAsync(ct);
        // The raw capability is intentionally returned exactly once. Persisted
        // retries replay a token-free completion snapshot, never the token.
        var safeReplay = new
        {
            accessLinkId = result.Id,
            token = (string?)null,
            buyerPortalUrl = (string?)null,
            result.ExpiresAtUtc,
            created = !result.AlreadyExisted,
        };
        await SellingIdempotency.CompleteAsync(db, started.Record!, userId, StatusCodes.Status200OK, safeReplay, ct);
        return Results.Ok(new
        {
            accessLinkId = result.Id,
            token = result.Token,
            buyerPortalUrl = result.Token is null ? null : result.BuyerPortalUrl,
            result.ExpiresAtUtc,
            created = !result.AlreadyExisted,
        });
    }

    private static async Task<IResult> GetBuyerLien(
        Guid lienId,
        LiensDbContext db,
        ICurrentRequestContext context,
        CancellationToken ct)
    {
        var (tenantId, buyerOrgId, _) = RequireBuyerContext(context);
        var lien = await ResolveGrantedBuyerLienAsync(db, tenantId, buyerOrgId, lienId, ct);
        if (lien is null) return NotFoundLien(lienId);
        return Results.Ok(new
        {
            lienId = lien.Id,
            lien.LienNumber,
            lien.Status,
            lien.SellerStatus,
            lien.InitialServiceDate,
            lien.EndServiceDate,
            lien.AskAmount,
            lien.OfferPrice,
            lien.OriginalAmount,
        });
    }

    private static async Task<IResult> SubmitBuyerOffer(
        Guid lienId,
        SubmitSellingBuyerOfferRequest request,
        HttpRequest httpRequest,
        ISellingNotificationOutbox notificationOutbox,
        LiensDbContext db,
        ICurrentRequestContext context,
        CancellationToken ct)
    {
        var (tenantId, buyerOrgId, userId) = RequireBuyerContext(context);
        if (!SellingIdempotency.TryGetKey(httpRequest, out var idempotencyKey, out var idempotencyError)) return idempotencyError!;
        var replay = await SellingIdempotency.GetReplayAsync(
            db, tenantId, "User", userId, "/api/liens/selling/buyer/liens/by-lien/{lienId}/offers", "Lien", lienId.ToString(), idempotencyKey!, request, ct);
        if (replay is not null) return replay;
        var lien = await ResolveGrantedBuyerLienAsync(db, tenantId, buyerOrgId, lienId, ct);
        if (lien is null) return NotFoundLien(lienId);
        if (request.OfferAmount <= 0) return ValidationError("offerAmount", "offerAmount must be positive.");
        if (await db.LienOffers.AnyAsync(offer => offer.TenantId == tenantId && offer.LienId == lien.Id && offer.BuyerOrgId == buyerOrgId && offer.Status == OfferStatus.Pending && (!offer.ExpiresAtUtc.HasValue || offer.ExpiresAtUtc > DateTime.UtcNow), ct))
            return Results.Conflict(new { error = new { code = "active_offer_exists", message = "This buyer already has an active offer." } });
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct)
            : null;
        try
        {
            var started = await SellingIdempotency.TryBeginAsync(
                db, tenantId, "User", userId, "/api/liens/selling/buyer/liens/by-lien/{lienId}/offers", "Lien", lienId.ToString(), idempotencyKey!, request, ct);
            if (started.Result is not null) return started.Result;
            var activeOfferExists = await db.LienOffers.AnyAsync(offer =>
                offer.TenantId == tenantId && offer.LienId == lien.Id && offer.BuyerOrgId == buyerOrgId &&
                offer.Status == OfferStatus.Pending && (!offer.ExpiresAtUtc.HasValue || offer.ExpiresAtUtc > DateTime.UtcNow), ct);
            if (activeOfferExists)
            {
                var conflict = await SellingIdempotency.CompleteAsync(db, started.Record!, userId, StatusCodes.Status409Conflict,
                    new { error = new { code = "active_offer_exists", message = "This buyer already has an active offer." } }, ct);
                if (transaction is not null) await transaction.CommitAsync(ct);
                return conflict;
            }

            var offer = LienOffer.Create(
                tenantId, lien.Id, buyerOrgId, lien.SellingOrgId ?? lien.OrgId,
                request.OfferAmount, userId, request.Message,
                submittedByPlatformUserId: userId);
            db.LienOffers.Add(offer);
            notificationOutbox.Enqueue(new NotificationInboxSendRequest(
                tenantId,
                userId,
                NotificationTaxonomy.Liens.Events.OfferSubmitted,
                "lien",
                "Offer Submitted",
                $"Your offer for lien {lien.LienNumber} was submitted.",
                "Synq Selling",
                "SS",
                offer.OfferedAtUtc,
                $"selling:offer:{offer.Id:N}:submitted:{userId:N}"));
            if (!lien.HighestBidAmount.HasValue || offer.OfferAmount > lien.HighestBidAmount.Value)
                lien.UpdateSellingAnalyticsFields(userId, highestBidAmount: offer.OfferAmount);
            await db.SaveChangesAsync(ct);
            var completed = await SellingIdempotency.CompleteAsync(db, started.Record!, userId, StatusCodes.Status201Created,
                new { offer.Id, offer.LienId, offer.OfferAmount, offer.Status, offer.OfferedAtUtc }, ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return completed;
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private static async Task<IResult> DeclineBuyerLien(
        Guid lienId,
        DeclineSellingBuyerLienRequest request,
        HttpRequest httpRequest,
        ISellingNotificationOutbox notificationOutbox,
        LiensDbContext db,
        ICurrentRequestContext context,
        CancellationToken ct)
    {
        var (tenantId, buyerOrgId, userId) = RequireBuyerContext(context);
        if (!SellingIdempotency.TryGetKey(httpRequest, out var idempotencyKey, out var idempotencyError)) return idempotencyError!;
        var replay = await SellingIdempotency.GetReplayAsync(
            db, tenantId, "User", userId, "/api/liens/selling/buyer/liens/by-lien/{lienId}/decline", "Lien", lienId.ToString(), idempotencyKey!, request, ct);
        if (replay is not null) return replay;
        var lien = await ResolveGrantedBuyerLienAsync(db, tenantId, buyerOrgId, lienId, ct);
        if (lien is null) return NotFoundLien(lienId);
        var link = await db.SellingBuyerAccessLinks.FirstOrDefaultAsync(item => item.TenantId == tenantId && item.LienId == lienId && item.BuyerOrgId == buyerOrgId && !item.RevokedAtUtc.HasValue && item.ExpiresAtUtc > DateTime.UtcNow, ct);
        if (link is null) return NotFoundLien(lienId);

        if (!string.IsNullOrWhiteSpace(link.ResponseStatus))
        {
            return Results.Conflict(new
            {
                error = new
                {
                    code = "response_conflict",
                    message = "A buyer response has already been recorded for this access link.",
                },
            });
        }

        // This deliberately uses the public portal's response-transition identity.
        // An authenticated buyer and a token-link buyer can act on the same access
        // link, so both paths must contend on one per-link serialization gate.
        var responseTransition = await SellingIdempotency.TryBeginAsync(
            db,
            tenantId,
            "BuyerLinkResponseTransition",
            link.Id,
            "/api/liens/selling/public/{token}/response",
            "BuyerAccessLink",
            link.Id.ToString(),
            "buyer-response-transition-v1",
            request: null,
            ct: ct);
        if (responseTransition.Result is not null)
        {
            return Results.Conflict(new
            {
                error = new
                {
                    code = "response_conflict",
                    message = "A buyer response is already being recorded for this access link.",
                },
            });
        }

        var started = await SellingIdempotency.TryBeginAsync(
            db, tenantId, "User", userId, "/api/liens/selling/buyer/liens/by-lien/{lienId}/decline", "Lien", lienId.ToString(), idempotencyKey!, request, ct);
        if (started.Result is not null)
        {
            db.SellingIdempotencyRecords.Remove(responseTransition.Record!);
            await db.SaveChangesAsync(ct);
            return started.Result;
        }

        // A buyer decline is recorded as a non-sale response; it does not mutate the core lien lifecycle.
        link.RecordResponse(SellingBuyerResponseStatus.Declined, null, request.Reason);
        AddActivity(db, lien, userId, "Buyer declined lien review.");
        if (link.CreatedByUserId is { } sellerUserId && sellerUserId != Guid.Empty)
        {
            notificationOutbox.Enqueue(new NotificationInboxSendRequest(
                tenantId,
                sellerUserId,
                NotificationTaxonomy.Liens.Events.OfferRejected,
                "lien",
                "Offer Declined",
                $"A buyer declined the offer for lien {lien.LienNumber}.",
                "Synq Selling",
                "SS",
                link.RespondedAtUtc ?? DateTime.UtcNow,
                $"selling:access-link:{link.Id:N}:rejected:{sellerUserId:N}"));
        }
        await db.SaveChangesAsync(ct);
        var completed = await SellingIdempotency.CompleteAsync(db, started.Record!, userId, StatusCodes.Status200OK,
            new { lienId, response = SellingBuyerResponseStatus.Declined }, ct);
        await SellingIdempotency.CompleteAsync(db, responseTransition.Record!, userId, StatusCodes.Status200OK,
            new { lienId, response = SellingBuyerResponseStatus.Declined }, ct);
        return completed;
    }

    private static async Task<IResult> GetBulkImport(Guid importId, LiensDbContext db, ICurrentRequestContext context, CancellationToken ct)
    {
        var (tenantId, _, _) = RequireSellerContext(context);
        var batch = await GetSellingImportAsync(db, tenantId, importId, ct);
        return batch is null ? Results.NotFound() : Results.Ok(MapBulkImport(batch));
    }

    private static async Task<IResult> GetBulkImportRows(Guid importId, string? status, int page, int pageSize, LiensDbContext db, ICurrentRequestContext context, CancellationToken ct)
    {
        var (tenantId, _, _) = RequireSellerContext(context);
        if (page < 1 || pageSize is < 1 or > 100) return ValidationError("page", "page must be positive and pageSize must be between 1 and 100.");
        var batch = await GetSellingImportAsync(db, tenantId, importId, ct);
        if (batch is null) return Results.NotFound();
        var query = db.BatchUploadDetails.AsNoTracking().Where(row => row.TenantId == tenantId && row.BatchUploadId == batch.Id && row.RecordStatus == "A");
        if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
            query = query.Where(row => row.Status == NormalizeRowStatus(status));
        var totalCount = await query.CountAsync(ct);
        var rows = await query.OrderBy(row => row.RowNumber).Skip((page - 1) * pageSize).Take(pageSize).Select(row => new { row.Id, row.RowNumber, row.Status, row.Reason, row.DataJson }).ToListAsync(ct);
        return Results.Ok(new { importId, page, pageSize, totalCount, items = rows });
    }

    private static async Task<IResult> ValidateBulkImport(Guid importId, LiensDbContext db, ICurrentRequestContext context, CancellationToken ct)
    {
        var (tenantId, _, userId) = RequireSellerContext(context);
        var batch = await GetSellingImportAsync(db, tenantId, importId, ct);
        if (batch is null) return Results.NotFound();
        if (batch.Status != "A") return Results.Conflict(new { error = new { code = "import_cancelled" } });
        if (batch.ProcessStatus is "CONFIRMED" or "PARTIAL")
            return Results.Conflict(new { error = new { code = "import_already_confirmed" } });
        var transitionGate = await SellingIdempotency.TryBeginAsync(
            db, tenantId, "BulkImportTransition", importId, "/api/liens/selling/bulk-imports/{importId}/confirm-transition",
            "BulkImport", importId.ToString(), "bulk-import-confirm-transition-v1", request: null, ct: ct);
        if (transitionGate.Result is not null)
            return Results.Conflict(new { error = new { code = "import_transition_in_progress", message = "This bulk import is currently being confirmed or cancelled. Retry shortly." } });

        try
        {
            await db.Entry(batch).ReloadAsync(ct);
            if (batch.Status != "A")
            {
                db.SellingIdempotencyRecords.Remove(transitionGate.Record!);
                await db.SaveChangesAsync(CancellationToken.None);
                return Results.Conflict(new { error = new { code = "import_cancelled" } });
            }
            if (batch.ProcessStatus is "CONFIRMED" or "PARTIAL")
            {
                db.SellingIdempotencyRecords.Remove(transitionGate.Record!);
                await db.SaveChangesAsync(CancellationToken.None);
                return Results.Conflict(new { error = new { code = "import_already_confirmed" } });
            }

            var rows = await db.BatchUploadDetails.Where(row => row.TenantId == tenantId && row.BatchUploadId == batch.Id && row.RecordStatus == "A").ToListAsync(ct);
            foreach (var row in rows)
            {
                var values = JsonSerializer.Deserialize<Dictionary<string, string>>(row.DataJson) ?? [];
                var reason = ValidateImportRow(values);
                row.SetResult(reason is null ? "VALID" : "INVALID", reason, userId);
            }
            batch.SetProcessStatus(rows.Any(row => row.Status == "INVALID") ? "VALIDATED_WITH_ERRORS" : "VALIDATED", userId);
            await db.SaveChangesAsync(ct);
            var response = Results.Ok(new { importId, status = batch.ProcessStatus, validCount = rows.Count(row => row.Status == "VALID"), invalidCount = rows.Count(row => row.Status == "INVALID") });
            db.SellingIdempotencyRecords.Remove(transitionGate.Record!);
            await db.SaveChangesAsync(CancellationToken.None);
            return response;
        }
        catch
        {
            db.ChangeTracker.Clear();
            db.SellingIdempotencyRecords.Remove(transitionGate.Record!);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<IResult> ConfirmBulkImport(Guid importId, HttpRequest httpRequest, LiensDbContext db, ICurrentRequestContext context, CancellationToken ct)
    {
        var (tenantId, sellerOrgId, userId) = RequireSellerContext(context);
        if (!SellingIdempotency.TryGetKey(httpRequest, out var idempotencyKey, out var idempotencyError)) return idempotencyError!;
        var replay = await SellingIdempotency.GetReplayAsync(
            db, tenantId, "User", userId, "/api/liens/selling/bulk-imports/{importId}/confirm", "BulkImport", importId.ToString(), idempotencyKey!, request: null, ct);
        if (replay is not null) return replay;
        var batch = await GetSellingImportAsync(db, tenantId, importId, ct);
        if (batch is null) return Results.NotFound();
        if (batch.Status != "A") return Results.Conflict(new { error = new { code = "import_cancelled" } });
        if (batch.ProcessStatus is "CONFIRMED" or "PARTIAL")
            return Results.Conflict(new { error = new { code = "import_already_confirmed" } });
        var rows = await db.BatchUploadDetails.Where(row => row.TenantId == tenantId && row.BatchUploadId == batch.Id && row.RecordStatus == "A").ToListAsync(ct);
        if (rows.Any(row => row.Status == "PENDING")) return ValidationError("importId", "Validate the bulk import before confirming it.");
        if (rows.Any(row => row.Status == "INVALID")) return ValidationError("importId", "Correct invalid rows before confirming the bulk import.");
        var fundingCompanies = await db.Contacts.AsNoTracking()
            .Where(contact => contact.TenantId == tenantId && contact.IsActive &&
                (contact.ContactType == ContactType.FundingCompany || contact.ContactType == ContactType.LienHolder))
            .ToListAsync(ct);
        var medicalProviders = await db.Contacts.AsNoTracking()
            .Where(contact => contact.TenantId == tenantId && contact.IsActive && contact.ContactType == ContactType.Provider)
            .ToListAsync(ct);
        var facilities = await db.Facilities.AsNoTracking()
            .Where(facility => facility.TenantId == tenantId && facility.OrgId == sellerOrgId && facility.IsActive)
            .ToListAsync(ct);

        // A user idempotency key protects one caller. This batch-level gate also
        // prevents a second caller/key from creating the staged rows twice.
        var transitionGate = await SellingIdempotency.TryBeginAsync(
            db, tenantId, "BulkImportTransition", importId, "/api/liens/selling/bulk-imports/{importId}/confirm-transition",
            "BulkImport", importId.ToString(), "bulk-import-confirm-transition-v1", request: null, ct: ct);
        if (transitionGate.Result is not null)
            return Results.Conflict(new { error = new { code = "import_confirmation_in_progress", message = "This bulk import is already being confirmed. Retry shortly." } });

        await db.Entry(batch).ReloadAsync(ct);
        if (batch.Status != "A")
        {
            db.SellingIdempotencyRecords.Remove(transitionGate.Record!);
            await db.SaveChangesAsync(CancellationToken.None);
            return Results.Conflict(new { error = new { code = "import_cancelled" } });
        }
        if (batch.ProcessStatus is "CONFIRMED" or "PARTIAL")
        {
            db.SellingIdempotencyRecords.Remove(transitionGate.Record!);
            await db.SaveChangesAsync(CancellationToken.None);
            return Results.Conflict(new { error = new { code = "import_already_confirmed" } });
        }

        var caseNumbers = rows.Where(row => row.Status == "VALID")
            .Select(row => SellingBulkImportSchema.GetValue(
                JsonSerializer.Deserialize<Dictionary<string, string>>(row.DataJson) ?? [],
                SellingBulkImportSchema.CaseCode))
            .Where(caseNumber => !string.IsNullOrWhiteSpace(caseNumber))
            .Select(caseNumber => caseNumber!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var casesByNumber = (await db.Cases.Where(caseEntity =>
                caseEntity.TenantId == tenantId && caseEntity.OrgId == sellerOrgId && caseNumbers.Contains(caseEntity.CaseNumber))
            .ToListAsync(ct))
            .GroupBy(caseEntity => caseEntity.CaseNumber, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        SellingIdempotency.IdempotencyStart? started = null;
        try
        {
            started = await SellingIdempotency.TryBeginAsync(
                db, tenantId, "User", userId, "/api/liens/selling/bulk-imports/{importId}/confirm", "BulkImport", importId.ToString(), idempotencyKey!, request: null, ct);
            if (started.Result is not null)
            {
                db.SellingIdempotencyRecords.Remove(transitionGate.Record!);
                await db.SaveChangesAsync(CancellationToken.None);
                return started.Result;
            }

            var created = 0;
            foreach (var row in rows.Where(row => row.Status == "VALID"))
            {
                Lien? lien = null;
                Case? createdCase = null;
                string? caseNumber = null;
                var rowEntities = new List<object>();
                try
                {
                    var values = JsonSerializer.Deserialize<Dictionary<string, string>>(row.DataJson) ?? [];
                    caseNumber = SellingBulkImportSchema.GetValue(values, SellingBulkImportSchema.CaseCode)!;
                    if (!casesByNumber.TryGetValue(caseNumber, out var caseEntity))
                    {
                        createdCase = Case.Create(tenantId, sellerOrgId, caseNumber, "Pending", "Lien", userId,
                            externalReference: caseNumber, title: caseNumber);
                        db.Cases.Add(createdCase);
                        rowEntities.Add(createdCase);
                        caseEntity = createdCase;
                        casesByNumber[caseNumber] = caseEntity;
                    }
                    var fundingCompanyName = SellingBulkImportSchema.GetValue(values, SellingBulkImportSchema.FundingCompany);
                    var fundingCompany = ResolveImportContactByName(fundingCompanies, fundingCompanyName);
                    var facilityName = SellingBulkImportSchema.GetValue(values, SellingBulkImportSchema.FacilityName);
                    var facility = ResolveImportFacilityByName(facilities, facilityName);
                    var medicalProviderName = SellingBulkImportSchema.GetValue(
                        values,
                        SellingBulkImportSchema.MedicalProvider,
                        "Medical Provider Name");
                    var medicalProvider = ResolveImportContactByName(medicalProviders, medicalProviderName);

                    var (medicalCode, medicalDescription) = ParseImportMedicalCode(
                        SellingBulkImportSchema.GetValue(values, SellingBulkImportSchema.MedicalCodeAndDescription));
                    var targetAskAmount = ParseImportDecimal(
                        values,
                        SellingBulkImportSchema.TargetAskAmount,
                        "Purchase Amount*");
                    lien = Lien.Create(tenantId, sellerOrgId, ResolveImportLienNumber(values), LienType.MedicalLien,
                        ParseImportDecimal(values, SellingBulkImportSchema.BillingAmount), userId,
                        externalReference: fundingCompany?.Id.ToString() ?? fundingCompanyName,
                        facilityId: facility?.Id,
                        initialServiceDate: ParseImportDate(values, SellingBulkImportSchema.InitialServiceDate),
                        endServiceDate: ParseImportDate(values, SellingBulkImportSchema.EndServiceDate),
                        notes: SellingBulkImportSchema.GetValue(values, SellingBulkImportSchema.LienNotes, "Notes"),
                        purchaseDate: ParseImportDate(values, SellingBulkImportSchema.PurchaseDate, "Purchase Date*"));
                    lien.AttachCase(caseEntity.Id, userId);
                    lien.UpdateSellingAnalyticsFields(userId,
                        sellerStatus: NormalizeImportStatus(values),
                        listingVisibility: NormalizeImportVisibility(values),
                        fundingCompanyId: fundingCompany?.Id,
                        askAmount: targetAskAmount > 0m ? targetAskAmount : null);
                    db.Liens.Add(lien);
                    rowEntities.Add(lien);
                    var pricing = ServicingItem.Create(
                        tenantId, sellerOrgId, $"SMP-{Guid.CreateVersion7():N}".ToUpperInvariant(),
                        SellingMedicalPricingTaskType, medicalCode, "Selling", userId,
                        caseId: caseEntity.Id, lienId: lien.Id,
                        notes: JsonSerializer.Serialize(new
                        {
                            medicalCode,
                            description = medicalDescription,
                            billingAmount = ParseImportDecimal(values, SellingBulkImportSchema.BillingAmount),
                            medicareCost = ParseImportDecimal(values, SellingBulkImportSchema.MedicareCost),
                            targetSaleAmount = targetAskAmount,
                        }));
                    db.ServicingItems.Add(pricing);
                    rowEntities.Add(pricing);
                    var legacyMedicalCode = ServicingItem.Create(
                        tenantId, sellerOrgId, $"LMC-{Guid.CreateVersion7():N}".ToUpperInvariant(),
                        "LegacyMedicalCode", $"Medical code {medicalCode}", "system", userId,
                        caseId: caseEntity.Id, lienId: lien.Id,
                        notes: $"code={medicalCode}; description={medicalDescription}; medicareCost={SellingBulkImportSchema.GetValue(values, SellingBulkImportSchema.MedicareCost) ?? string.Empty}; billingAmount={SellingBulkImportSchema.GetValue(values, SellingBulkImportSchema.BillingAmount) ?? string.Empty}; purchaseAmount={SellingBulkImportSchema.GetValue(values, SellingBulkImportSchema.TargetAskAmount, "Purchase Amount*") ?? string.Empty}; payee={GetImportValue(values, "Payee") ?? string.Empty}; outboundCheckNumber={GetImportValue(values, "Outbound Check Number") ?? string.Empty}");
                    db.ServicingItems.Add(legacyMedicalCode);
                    rowEntities.Add(legacyMedicalCode);
                    var facilityInfo = ServicingItem.Create(
                        tenantId, sellerOrgId, $"LMFI-{Guid.CreateVersion7():N}".ToUpperInvariant(),
                        "LegacyMedicalFacilityInfo", "Legacy medical facility information", "system", userId,
                        caseId: caseEntity.Id, lienId: lien.Id,
                        notes: $"facilityId={facility?.Id}; facilityName={facility?.Name ?? facilityName ?? string.Empty}; medicalProviderId={medicalProvider?.Id}; medicalProvider={medicalProvider?.Organization ?? medicalProvider?.DisplayName ?? medicalProviderName ?? string.Empty}");
                    db.ServicingItems.Add(facilityInfo);
                    rowEntities.Add(facilityInfo);
                    row.SetResult("CREATED", null, userId);
                    await db.SaveChangesAsync(ct);
                    created++;
                }
                catch (OperationCanceledException)
                {
                    DetachImportRowEntities(db, rowEntities);
                    if (createdCase is not null && caseNumber is not null) casesByNumber.Remove(caseNumber);
                    throw;
                }
                catch (Exception ex)
                {
                    DetachImportRowEntities(db, rowEntities);
                    if (createdCase is not null && caseNumber is not null) casesByNumber.Remove(caseNumber);
                    row.SetResult("FAILED", TruncateImportFailureReason(ex.Message), userId);
                }
            }
            batch.SetProcessStatus(rows.Any(row => row.Status == "FAILED") ? "PARTIAL" : "CONFIRMED", userId);
            await db.SaveChangesAsync(ct);
            var response = new { importId, status = batch.ProcessStatus, createdCount = created, failedCount = rows.Count(row => row.Status == "FAILED") };
            var completed = await SellingIdempotency.CompleteAsync(db, started.Record!, userId, StatusCodes.Status200OK, response, ct);
            await SellingIdempotency.CompleteAsync(db, transitionGate.Record!, userId, StatusCodes.Status200OK, response, ct);
            return completed;
        }
        catch
        {
            // A completed row is saved with its matching lien, so releasing this
            // gate permits a safe retry after cancellation or an infrastructure
            // failure without duplicating the rows that already succeeded.
            db.ChangeTracker.Clear();
            if (started?.Record is not null && started.Record.ProcessingState != SellingIdempotencyRecord.Completed)
                db.SellingIdempotencyRecords.Remove(started.Record);
            db.SellingIdempotencyRecords.Remove(transitionGate.Record!);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<IResult> CancelBulkImport(Guid importId, LiensDbContext db, ICurrentRequestContext context, CancellationToken ct)
    {
        var (tenantId, _, userId) = RequireSellerContext(context);
        var batch = await GetSellingImportAsync(db, tenantId, importId, ct);
        if (batch is null) return Results.NotFound();
        if (batch.ProcessStatus is "CONFIRMED" or "PARTIAL") return Results.Conflict(new { error = new { code = "import_already_confirmed" } });
        var transitionGate = await SellingIdempotency.TryBeginAsync(
            db, tenantId, "BulkImportTransition", importId, "/api/liens/selling/bulk-imports/{importId}/confirm-transition",
            "BulkImport", importId.ToString(), "bulk-import-confirm-transition-v1", request: null, ct: ct);
        if (transitionGate.Result is not null)
            return Results.Conflict(new { error = new { code = "import_confirmation_in_progress", message = "This bulk import is being confirmed and cannot be cancelled." } });

        try
        {
            await db.Entry(batch).ReloadAsync(ct);
            if (batch.ProcessStatus is "CONFIRMED" or "PARTIAL")
            {
                db.SellingIdempotencyRecords.Remove(transitionGate.Record!);
                await db.SaveChangesAsync(CancellationToken.None);
                return Results.Conflict(new { error = new { code = "import_already_confirmed" } });
            }
            if (batch.Status != "A")
            {
                db.SellingIdempotencyRecords.Remove(transitionGate.Record!);
                await db.SaveChangesAsync(CancellationToken.None);
                return Results.Conflict(new { error = new { code = "import_cancelled" } });
            }

            batch.Deactivate(userId);
            batch.SetProcessStatus("CANCELLED", userId);
            await db.SaveChangesAsync(ct);
            await SellingIdempotency.CompleteAsync(db, transitionGate.Record!, userId, StatusCodes.Status200OK,
                new { importId, status = "CANCELLED" }, ct);
            return Results.NoContent();
        }
        catch
        {
            db.ChangeTracker.Clear();
            db.SellingIdempotencyRecords.Remove(transitionGate.Record!);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<IResult> GetFundingCompanies(LiensDbContext db, ICurrentRequestContext context, CancellationToken ct)
    {
        var (tenantId, sellerOrgId, _) = RequireSellerContext(context);
        var legacyItems = await db.Contacts.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsActive &&
                (c.ContactType == ContactType.FundingCompany || c.ContactType == ContactType.LienHolder))
            .Select(c => new FundingCompanyLookupItem(
                c.Id,
                c.Organization == null || c.Organization == string.Empty ? c.DisplayName : c.Organization,
                c.OrgId))
            .ToListAsync(ct);
        var canonicalItems = await db.Companies.AsNoTracking()
            .Where(company =>
                company.TenantId == tenantId &&
                company.OrgId == sellerOrgId &&
                company.CompanyTypeId == CompanyDirectoryReferenceData.FundingCompanyId &&
                company.IsActive)
            .Select(company => new FundingCompanyLookupItem(company.Id, company.Name, company.Id))
            .ToListAsync(ct);
        var items = legacyItems.Concat(canonicalItems)
            .DistinctBy(item => item.Id)
            .OrderBy(item => item.Name)
            .ThenBy(item => item.Id)
            .ToList();
        return Results.Ok(new { items });
    }

    private static async Task<IResult> GetFundingCompanyContacts(Guid fundingCompanyId, LiensDbContext db, ICurrentRequestContext context, CancellationToken ct)
    {
        var (tenantId, sellerOrgId, _) = RequireSellerContext(context);
        var canonicalCompany = await db.Companies.AsNoTracking().FirstOrDefaultAsync(company =>
            company.TenantId == tenantId &&
            company.OrgId == sellerOrgId &&
            company.Id == fundingCompanyId &&
            company.CompanyTypeId == CompanyDirectoryReferenceData.FundingCompanyId &&
            company.IsActive, ct);
        if (canonicalCompany is not null)
        {
            var canonicalItems = await db.CompanyContactPersons.AsNoTracking()
                .Where(contact =>
                    contact.TenantId == tenantId &&
                    contact.CompanyId == canonicalCompany.Id &&
                    contact.IsActive &&
                    contact.ContactPersonType != null &&
                    contact.ContactPersonType.IsActive &&
                    contact.ContactPersonType.CompanyTypeId == CompanyDirectoryReferenceData.FundingCompanyId &&
                    (contact.ContactPersonType.TenantId == null ||
                     (contact.ContactPersonType.TenantId == tenantId && contact.ContactPersonType.OrgId == sellerOrgId)))
                .OrderBy(contact => contact.LastName)
                .ThenBy(contact => contact.FirstName)
                .Select(contact => new FundingCompanyContactLookupItem(
                    contact.Id,
                    (contact.FirstName + " " + contact.LastName).Trim(),
                    contact.Email))
                .ToListAsync(ct);
            return Results.Ok(new { items = canonicalItems });
        }

        var company = await GetFundingCompanyAsync(db, tenantId, fundingCompanyId, ct);
        if (company is null) return ValidationError("fundingCompanyId", "Funding company was not found in this tenant.");
        var items = await db.Contacts.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.OrgId == company.OrgId && c.IsActive)
            .OrderBy(c => c.DisplayName)
            .Select(c => new FundingCompanyContactLookupItem(
                c.Id,
                c.Organization == null || c.Organization == string.Empty ? c.DisplayName : c.Organization,
                c.Email))
            .ToListAsync(ct);
        return Results.Ok(new { items });
    }

    private static async Task<IResult> GetLawFirms(LiensDbContext db, ICurrentRequestContext context, CancellationToken ct)
    {
        var (tenantId, _, _) = RequireSellerContext(context);
        var items = await db.Contacts
            .AsNoTracking()
            .Where(c =>
                c.TenantId == tenantId &&
                c.ContactType == ContactType.LawFirm &&
                c.IsActive &&
                (c.ContactSubtype == null || c.ContactSubtype == string.Empty) &&
                !c.LawFirmId.HasValue)
            .OrderBy(c => c.DisplayName)
            .Select(c => new { c.Id, name = DisplayName(c), c.OrgId })
            .ToListAsync(ct);
        return Results.Ok(new { items });
    }
    private static async Task<IResult> GetCaseManagers(Guid? lawFirmId, LiensDbContext db, ICurrentRequestContext context, CancellationToken ct) => await GetContactsByType(db, context, ContactType.CaseManager, lawFirmId, ct);
    private static async Task<IResult> GetContactsByType(LiensDbContext db, ICurrentRequestContext context, string type, Guid? lawFirmId, CancellationToken ct)
    {
        var (tenantId, _, _) = RequireSellerContext(context);
        var query = db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId && c.ContactType == type && c.IsActive);
        if (lawFirmId.HasValue) query = query.Where(c => c.LawFirmId == lawFirmId || c.OrgId == lawFirmId);
        var items = await query.OrderBy(c => c.DisplayName).Select(c => new { c.Id, name = DisplayName(c), c.OrgId }).ToListAsync(ct);
        return Results.Ok(new { items });
    }
    private static async Task<IResult> GetFacilities(LiensDbContext db, ICurrentRequestContext context, CancellationToken ct)
    {
        var (tenantId, sellerOrgId, _) = RequireSellerContext(context);
        var items = await db.Facilities.AsNoTracking().Where(f => f.TenantId == tenantId && f.IsActive && f.OrgId == sellerOrgId).OrderBy(f => f.Name).Select(f => new { f.Id, f.Name, f.Code }).ToListAsync(ct);
        return Results.Ok(new { items });
    }
    private static async Task<IResult> GetMedicalCodes(string? search, LiensDbContext db, ICurrentRequestContext context, CancellationToken ct)
    {
        var (tenantId, _, _) = RequireSellerContext(context);
        var query = db.ManualMedicalCodes.AsNoTracking().Where(code => code.TenantId == tenantId && code.Status == "A");
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(code => code.Code.Contains(search.Trim()) || (code.Description != null && code.Description.Contains(search.Trim())));
        var items = await query.OrderBy(code => code.Code).Take(100).Select(code => new { code.Id, code.Code, code.Description, code.Cost }).ToListAsync(ct);
        return Results.Ok(new { items });
    }
    private static IResult GetDocumentTypes() => Results.Ok(new
    {
        items = new[]
        {
            "MedicalBill",
            "MedicalRecord",
            "LienAgreement",
            "SettlementStatement",
            "Other",
            "ItemizedBill",
            "HCFA-1500",
            "SignedLien",
            "LetterOfProtection",
        },
    });

    private static async Task<(SellingCaseIntake? Value, IResult? Error)> ValidateSellingCaseInformationAsync(
        LiensDbContext db,
        Guid tenantId,
        Guid sellerOrgId,
        string? caseStatus,
        string? accidentTypeId,
        string? accidentState,
        DateOnly? dateOfLoss,
        Guid? handlingLawFirmId,
        Guid? caseManagerId,
        string? caseTrackingNotes,
        CancellationToken ct)
    {
        var normalizedStatus = NormalizeCaseStatus(caseStatus);
        if (normalizedStatus is null)
            return (null, ValidationError("caseStatus", "caseStatus must be a valid case status."));
        if (dateOfLoss > DateOnly.FromDateTime(DateTime.UtcNow))
            return (null, ValidationError("dateOfLoss", "dateOfLoss cannot be in the future."));
        if (accidentTypeId?.Trim().Length > 100)
            return (null, ValidationError("accidentTypeId", "accidentTypeId cannot exceed 100 characters."));
        if (accidentState?.Trim().Length > 100)
            return (null, ValidationError("accidentState", "accidentState cannot exceed 100 characters."));
        if (caseTrackingNotes?.Trim().Length > MaxSellingCaseTrackingNotesLength)
            return (null, ValidationError("caseTrackingNotes", $"caseTrackingNotes cannot exceed {MaxSellingCaseTrackingNotesLength} characters."));

        var normalizedAccidentTypeId = string.IsNullOrWhiteSpace(accidentTypeId) ? null : accidentTypeId.Trim();
        string? accidentTypeName = null;
        if (normalizedAccidentTypeId is not null)
        {
            var accidentTypeQuery = db.LookupValues.AsNoTracking().Where(value =>
                value.Category == LookupCategory.AccidentType &&
                value.IsActive &&
                (value.TenantId == null || value.TenantId == tenantId));
            var accidentType = Guid.TryParse(normalizedAccidentTypeId, out var accidentTypeGuid)
                ? await accidentTypeQuery.FirstOrDefaultAsync(value =>
                    value.Code == normalizedAccidentTypeId || value.Id == accidentTypeGuid, ct)
                : await accidentTypeQuery.FirstOrDefaultAsync(value => value.Code == normalizedAccidentTypeId, ct);
            if (accidentType is null)
                return (null, ValidationError("accidentTypeId", "accidentTypeId must identify an active accident type."));
            accidentTypeName = accidentType.Name;
        }

        var lawFirmId = NormalizeOptionalGuid(handlingLawFirmId);
        Company? lawFirm = null;
        if (lawFirmId.HasValue)
        {
            lawFirm = await db.Companies.AsNoTracking().FirstOrDefaultAsync(company =>
                company.TenantId == tenantId &&
                company.OrgId == sellerOrgId &&
                company.Id == lawFirmId.Value &&
                company.CompanyTypeId == CompanyDirectoryReferenceData.LawFirmId &&
                company.IsActive, ct);
            if (lawFirm is null)
                return (null, ValidationError("handlingLawFirmId", "Handling law firm must be an active Law Firm company owned by the seller organization."));
        }

        var managerId = NormalizeOptionalGuid(caseManagerId);
        if (managerId.HasValue)
        {
            var caseManager = await GetCanonicalCaseManagerAsync(
                db, tenantId, sellerOrgId, managerId.Value, lawFirm?.Id, ct);
            if (caseManager is null)
                return (null, ValidationError("caseManagerId", lawFirm is null
                    ? "Case manager must be active, have the Case Manager role, and belong to a seller law firm."
                    : "Case manager must be active, have the Case Manager role, and belong to the selected law firm."));
            lawFirm ??= caseManager.Company;
            lawFirmId = lawFirm!.Id;
        }

        return (new SellingCaseIntake(
            normalizedStatus,
            normalizedAccidentTypeId,
            accidentState?.Trim(),
            accidentTypeName,
            lawFirmId,
            managerId), null);
    }

    private static IResult? ValidatePlaintiff(
        string? firstName,
        string? lastName,
        DateOnly? birthdate,
        string? email,
        string? phone,
        string? gender,
        string? address,
        string? city,
        string? state,
        string? zipcode)
    {
        if (string.IsNullOrWhiteSpace(firstName) || firstName.Trim().Length > 100)
            return ValidationError("firstName", "firstName is required and cannot exceed 100 characters.");
        if (string.IsNullOrWhiteSpace(lastName) || lastName.Trim().Length > 100)
            return ValidationError("lastName", "lastName is required and cannot exceed 100 characters.");
        if (birthdate > DateOnly.FromDateTime(DateTime.UtcNow))
            return ValidationError("birthdate", "birthdate cannot be in the future.");
        if (!string.IsNullOrWhiteSpace(email) &&
            (email.Trim().Length > 320 || !email.Contains('@', StringComparison.Ordinal)))
            return ValidationError("email", "email must be a valid email address.");
        if (phone?.Trim().Length > 30)
            return ValidationError("phone", "phone cannot exceed 30 characters.");
        if (gender?.Trim().Length > 100)
            return ValidationError("gender", "gender cannot exceed 100 characters.");
        if (address?.Trim().Length > 300)
            return ValidationError("address", "address cannot exceed 300 characters.");
        if (city?.Trim().Length > 100)
            return ValidationError("city", "city cannot exceed 100 characters.");
        if (state?.Trim().Length > 100)
            return ValidationError("state", "state cannot exceed 100 characters.");
        if (zipcode?.Trim().Length > 20)
            return ValidationError("zipcode", "zipcode cannot exceed 20 characters.");

        return null;
    }

    private static Case CreateCaseFromDraft(
        SellingCaseDraft draft,
        FinalizeSellingCaseDraftPlaintiffRequest plaintiff,
        Guid userId,
        string? accidentTypeName)
    {
        var caseEntity = Case.Create(
            draft.TenantId,
            draft.OrgId,
            $"SC-{Guid.CreateVersion7():N}"[..15].ToUpperInvariant(),
            plaintiff.FirstName,
            plaintiff.LastName,
            userId,
            clientDob: plaintiff.Birthdate,
            clientPhone: plaintiff.Phone,
            clientEmail: plaintiff.Email,
            clientAddress: plaintiff.Address,
            dateOfIncident: draft.DateOfLoss,
            notes: ComposeSellingCaseNotes(
                draft.CaseTrackingNotes,
                null,
                draft.AccidentTypeId,
                accidentTypeName,
                plaintiff.Gender),
            clientAddressLine1: plaintiff.Address,
            clientCity: plaintiff.City,
            clientState: plaintiff.State,
            clientPostalCode: plaintiff.Zipcode,
            incidentState: draft.AccidentState);
        caseEntity.SetCanonicalCaseParties(
            draft.HandlingLawFirmCompanyId,
            draft.CaseManagerContactPersonId,
            userId);
        if (!string.Equals(caseEntity.Status, draft.CaseStatus, StringComparison.Ordinal))
            caseEntity.TransitionStatus(draft.CaseStatus, userId);
        return caseEntity;
    }

    private static string? ComposeSellingCaseNotes(
        string? trackingNotes,
        string? existingNotes,
        string? accidentTypeId,
        string? accidentTypeName,
        string? gender)
    {
        var metadata = ParseCaseMetadata(existingNotes);
        metadata.Remove("accidentTypeId");
        metadata.Remove("accidentType");
        metadata.Remove("gender");
        AddMetadata(metadata, "accidentTypeId", accidentTypeId);
        AddMetadata(metadata, "accidentType", accidentTypeName);
        AddMetadata(metadata, "gender", gender);

        var text = trackingNotes?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return metadata.Count == 0 ? null : $"{LegacyCaseMetadataMarker}\n{string.Join("; ", metadata.Select(item => $"{item.Key}={item.Value}"))}";
        if (metadata.Count == 0)
            return text;
        return $"{text}\n\n{LegacyCaseMetadataMarker}\n{string.Join("; ", metadata.Select(item => $"{item.Key}={item.Value}"))}";
    }

    private static string? ExtractSellingCaseTrackingNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        var markerIndex = notes.IndexOf(LegacyCaseMetadataMarker, StringComparison.Ordinal);
        var trackingNotes = markerIndex >= 0 ? notes[..markerIndex] : notes;
        return string.IsNullOrWhiteSpace(trackingNotes) ? null : trackingNotes.Trim();
    }

    private static void AddMetadata(IDictionary<string, string> metadata, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        metadata[key] = value.Trim();
    }

    private static async Task<string?> ResolveAccidentTypeNameAsync(
        LiensDbContext db,
        Guid tenantId,
        string? accidentTypeId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accidentTypeId))
            return null;
        var normalizedAccidentTypeId = accidentTypeId.Trim();
        var accidentTypeQuery = db.LookupValues.AsNoTracking().Where(value =>
            value.Category == LookupCategory.AccidentType &&
            (value.TenantId == null || value.TenantId == tenantId));
        return Guid.TryParse(normalizedAccidentTypeId, out var accidentTypeGuid)
            ? await accidentTypeQuery
                .Where(value => value.Code == normalizedAccidentTypeId || value.Id == accidentTypeGuid)
                .Select(value => value.Name)
                .FirstOrDefaultAsync(ct)
            : await accidentTypeQuery
                .Where(value => value.Code == normalizedAccidentTypeId)
                .Select(value => value.Name)
                .FirstOrDefaultAsync(ct);
    }

    private static string? NormalizeCaseStatus(string? value)
        => CaseStatus.All.FirstOrDefault(status => string.Equals(status, value?.Trim(), StringComparison.OrdinalIgnoreCase));

    private sealed record SellingCaseIntake(
        string CaseStatus,
        string? AccidentTypeId,
        string? AccidentState,
        string? AccidentTypeName,
        Guid? HandlingLawFirmId,
        Guid? CaseManagerId);

    private static async Task<Lien?> GetSellerLienAsync(LiensDbContext db, Guid tenantId, Guid sellerOrgId, Guid lienId, CancellationToken ct) =>
        await db.Liens.FirstOrDefaultAsync(lien => lien.TenantId == tenantId && lien.Id == lienId &&
            (lien.SellingOrgId == sellerOrgId || (lien.SellingOrgId == null && lien.OrgId == sellerOrgId)), ct);

    private static Guid? NormalizeOptionalGuid(Guid? value) =>
        value is { } id && id != Guid.Empty ? id : null;

    private static async Task<Lien?> ResolveGrantedBuyerLienAsync(LiensDbContext db, Guid tenantId, Guid buyerOrgId, Guid lienId, CancellationToken ct)
    {
        var lien = await db.Liens.FirstOrDefaultAsync(item => item.TenantId == tenantId && item.Id == lienId, ct);
        if (lien is null || !IsSubmittedLien(lien)) return null;
        var granted = await db.SellingBuyerAccessLinks.AsNoTracking().AnyAsync(link => link.TenantId == tenantId && link.LienId == lienId && link.BuyerOrgId == buyerOrgId && !link.RevokedAtUtc.HasValue && link.ExpiresAtUtc > DateTime.UtcNow, ct);
        return granted ? lien : null;
    }

    private static bool IsSubmittedLien(Lien lien) => lien.Status == LienStatus.Offered && lien.SellerStatus == SellingLienStatus.SubmittedForSale && lien.ArchivedAtUtc is null && lien.SoldAtUtc is null && lien.WithdrawnAtUtc is null;

    private static bool IsMoveToManagementEligible(Lien lien)
    {
        if (lien.MovedToManagementAtUtc.HasValue || lien.ArchivedAtUtc.HasValue ||
            lien.SoldAtUtc.HasValue || lien.WithdrawnAtUtc.HasValue)
            return false;
        if (IsSubmittedLien(lien))
            return true;
        if (lien.Status != LienStatus.Draft)
            return false;
        return lien.SellerStatus is null or "" ||
            lien.SellerStatus == SellingLienStatus.Pending ||
            lien.SellerStatus == SellingLienStatus.Internal ||
            lien.SellerStatus == SellingLienStatus.Approval ||
            lien.SellerStatus == SellingLienStatus.PreparedForSale;
    }

    private static Case CreateManagementCase(Lien lien, Guid tenantId, Guid sellerOrgId, Guid userId) =>
        Case.Create(
            tenantId,
            sellerOrgId,
            $"SC-{Guid.CreateVersion7():N}"[..15].ToUpperInvariant(),
            string.IsNullOrWhiteSpace(lien.SubjectFirstName) ? "Jane" : lien.SubjectFirstName,
            string.IsNullOrWhiteSpace(lien.SubjectLastName) ? "Doe" : lien.SubjectLastName,
            userId,
            externalReference: lien.ExternalReference,
            title: $"Lien {lien.LienNumber}",
            dateOfIncident: lien.IncidentDate,
            description: lien.Description,
            notes: lien.Notes);

    private static async Task EnsureManagementAmountsAsync(
        LiensDbContext db,
        Lien lien,
        Guid tenantId,
        Guid sellerOrgId,
        Guid userId,
        CancellationToken ct)
    {
        var existingManagementRows = await db.ServicingItems
            .Where(item => item.TenantId == tenantId && item.LienId == lien.Id && item.TaskType == "LegacyMedicalCode")
            .ToListAsync(ct);

        var sellingPricingRows = await db.ServicingItems.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.LienId == lien.Id && item.TaskType == SellingMedicalPricingTaskType)
            .OrderBy(item => item.CreatedAtUtc)
            .Select(item => new { item.Description, item.Notes })
            .ToListAsync(ct);

        if (sellingPricingRows.Count == 0)
        {
            if (existingManagementRows.Count > 0)
            {
                foreach (var row in existingManagementRows)
                {
                    row.Update(
                        row.TaskType,
                        row.Description,
                        row.AssignedTo,
                        userId,
                        row.Priority,
                        lien.CaseId,
                        lien.Id,
                        row.DueDate,
                        row.Notes,
                        row.AssignedToUserId);
                }
                return;
            }

            AddManagementPricingRow(
                db, lien, tenantId, sellerOrgId, userId,
                "Selling lien pricing", lien.OriginalAmount, lien.AskAmount ?? 0m, null, null);
            return;
        }

        db.ServicingItems.RemoveRange(existingManagementRows);

        foreach (var sellingPricingRow in sellingPricingRows)
        {
            SellingMedicalPricingEntry pricing;
            try
            {
                pricing = JsonSerializer.Deserialize<SellingMedicalPricingEntry>(
                    sellingPricingRow.Notes ?? "{}",
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new SellingMedicalPricingEntry();
            }
            catch (JsonException)
            {
                pricing = new SellingMedicalPricingEntry();
            }
            var description = string.IsNullOrWhiteSpace(pricing.MedicalCode)
                ? sellingPricingRow.Description
                : pricing.MedicalCode;
            AddManagementPricingRow(
                db, lien, tenantId, sellerOrgId, userId,
                string.IsNullOrWhiteSpace(description) ? "Selling lien pricing" : description,
                pricing.BillingAmount,
                pricing.TargetSaleAmount,
                pricing.Description,
                pricing.MedicareCost);
        }
    }

    private static void AddManagementPricingRow(
        LiensDbContext db,
        Lien lien,
        Guid tenantId,
        Guid sellerOrgId,
        Guid userId,
        string code,
        decimal billingAmount,
        decimal purchaseAmount,
        string? description,
        decimal? medicareCost)
    {
        var notes = $"code={code}; billingAmount={billingAmount.ToString(CultureInfo.InvariantCulture)}; purchaseAmount={purchaseAmount.ToString(CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrWhiteSpace(description)) notes += $"; description={description}";
        if (medicareCost.HasValue) notes += $"; medicareCost={medicareCost.Value.ToString(CultureInfo.InvariantCulture)}";

        db.ServicingItems.Add(ServicingItem.Create(
            tenantId,
            sellerOrgId,
            $"LMC-{Guid.CreateVersion7():N}".ToUpperInvariant(),
            "LegacyMedicalCode",
            $"Medical code {code}",
            "system",
            userId,
            caseId: lien.CaseId,
            lienId: lien.Id,
            notes: notes));
    }

    private static IResult? ValidateMoveToManagementCaseInfo(MoveSellingLienToManagementCaseInfoRequest? caseInfo)
    {
        if (caseInfo is null)
            return null;
        if (string.IsNullOrWhiteSpace(caseInfo.ClientFirstName))
            return ValidationError("caseInfo.clientFirstName", "First name is required when caseInfo is provided.");
        if (string.IsNullOrWhiteSpace(caseInfo.ClientLastName))
            return ValidationError("caseInfo.clientLastName", "Last name is required when caseInfo is provided.");
        if (caseInfo.ClientDob is null)
            return ValidationError("caseInfo.clientDob", "Date of birth is required when caseInfo is provided.");
        if (string.IsNullOrWhiteSpace(caseInfo.StatusLabel))
            return ValidationError("caseInfo.statusLabel", "Status is required when caseInfo is provided.");
        if (string.IsNullOrWhiteSpace(caseInfo.AccidentTypeId))
            return ValidationError("caseInfo.accidentTypeId", "Accident type is required when caseInfo is provided.");
        if (string.IsNullOrWhiteSpace(caseInfo.StateOfIncident))
            return ValidationError("caseInfo.stateOfIncident", "Accident state is required when caseInfo is provided.");
        if (string.IsNullOrWhiteSpace(caseInfo.LawFirmId))
            return ValidationError("caseInfo.lawFirmId", "Law firm is required when caseInfo is provided.");
        return null;
    }

    private static async Task<IResult?> ValidateExistingMoveToManagementCaseAsync(
        LiensDbContext db,
        Guid tenantId,
        Guid sellerOrgId,
        Lien lien,
        CancellationToken ct)
    {
        if (lien.CaseId is not { } caseId || caseId == Guid.Empty)
            return null;
        var existing = await db.Cases.AsNoTracking().FirstOrDefaultAsync(item => item.Id == caseId && item.TenantId == tenantId, ct);
        if (existing is null)
            return ValidationError("caseId", "Linked case was not found in this tenant.");
        if (existing.OrgId != sellerOrgId)
            return ValidationError("caseId", "Linked case is not owned by the seller organization.");
        return null;
    }

    private static string BuildSubmitForSaleTransitionKey(Lien lien) => $"submit-for-sale-transition-v1:{lien.UpdatedAtUtc.Ticks.ToString(CultureInfo.InvariantCulture)}";
    private static bool IsActiveOffer(LienOffer offer) => offer.Status is not OfferStatus.Rejected and not OfferStatus.Withdrawn and not OfferStatus.Expired && !offer.IsExpired;
    private static async Task<Contact?> GetFundingCompanyAsync(LiensDbContext db, Guid tenantId, Guid id, CancellationToken ct) => await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == id && c.IsActive && (c.ContactType == ContactType.FundingCompany || c.ContactType == ContactType.LienHolder), ct);
    private static async Task<bool> IsActiveContactAsync(LiensDbContext db, Guid tenantId, Guid id, string type, CancellationToken ct) => await db.Contacts.AsNoTracking().AnyAsync(c => c.TenantId == tenantId && c.Id == id && c.IsActive && c.ContactType == type, ct);
    private static async Task<CompanyContactPerson?> GetCanonicalCaseManagerAsync(
        LiensDbContext db,
        Guid tenantId,
        Guid sellerOrgId,
        Guid contactPersonId,
        Guid? lawFirmCompanyId,
        CancellationToken ct) => await db.CompanyContactPersons
        .AsNoTracking()
        .Include(contact => contact.Company)
        .Include(contact => contact.ContactPersonType)
        .FirstOrDefaultAsync(contact =>
            contact.TenantId == tenantId &&
            contact.Id == contactPersonId &&
            contact.IsActive &&
            (!lawFirmCompanyId.HasValue || contact.CompanyId == lawFirmCompanyId.Value) &&
            contact.Company != null &&
            contact.Company.TenantId == tenantId &&
            contact.Company.OrgId == sellerOrgId &&
            contact.Company.CompanyTypeId == CompanyDirectoryReferenceData.LawFirmId &&
            contact.Company.IsActive &&
            contact.ContactPersonType != null &&
            contact.ContactPersonType.IsActive &&
            contact.ContactPersonType.CompanyTypeId == CompanyDirectoryReferenceData.LawFirmId &&
            contact.ContactPersonType.Code == "CaseManager" &&
            (contact.ContactPersonType.TenantId == null ||
             (contact.ContactPersonType.TenantId == tenantId && contact.ContactPersonType.OrgId == sellerOrgId)),
            ct);
    private static async Task<bool> IsActiveStandaloneLawFirmAsync(LiensDbContext db, Guid tenantId, Guid id, CancellationToken ct) => await db.Contacts.AsNoTracking().AnyAsync(c =>
        c.TenantId == tenantId &&
        c.Id == id &&
        c.IsActive &&
        c.ContactType == ContactType.LawFirm &&
        (c.ContactSubtype == null || c.ContactSubtype == string.Empty) &&
        !c.LawFirmId.HasValue,
        ct);
    private static async Task<MoveToManagementCaseResolution> ResolveMoveToManagementCaseAsync(
        LiensDbContext db,
        Guid tenantId,
        Guid sellerOrgId,
        Guid userId,
        Lien lien,
        MoveSellingLienToManagementCaseInfoRequest? caseInfo,
        CancellationToken ct)
    {
        if (lien.CaseId is { } existingCaseId && existingCaseId != Guid.Empty)
        {
            var existing = await db.Cases.FirstOrDefaultAsync(item =>
                item.TenantId == tenantId &&
                item.OrgId == sellerOrgId &&
                item.Id == existingCaseId, ct);
            if (existing is not null)
                return new MoveToManagementCaseResolution(existing, false);
        }

        var duplicate = await FindMoveToManagementDuplicateCaseAsync(db, tenantId, sellerOrgId, caseInfo, ct);
        if (duplicate is not null)
            return new MoveToManagementCaseResolution(duplicate, false);

        var created = CreateMoveToManagementCase(tenantId, sellerOrgId, userId, lien, caseInfo);
        db.Cases.Add(created);
        return new MoveToManagementCaseResolution(created, true);
    }

    private static async Task<Case?> FindMoveToManagementDuplicateCaseAsync(
        LiensDbContext db,
        Guid tenantId,
        Guid sellerOrgId,
        MoveSellingLienToManagementCaseInfoRequest? caseInfo,
        CancellationToken ct)
    {
        if (caseInfo is null ||
            string.IsNullOrWhiteSpace(caseInfo.ClientFirstName) ||
            string.IsNullOrWhiteSpace(caseInfo.ClientLastName) ||
            caseInfo.ClientDob is null ||
            caseInfo.DateOfIncident is null)
        {
            return null;
        }

        var firstName = caseInfo.ClientFirstName.Trim();
        var lastName = caseInfo.ClientLastName.Trim();
        return await db.Cases
            .Where(item =>
                item.TenantId == tenantId &&
                item.OrgId == sellerOrgId &&
                item.ClientDob == caseInfo.ClientDob &&
                item.DateOfIncident == caseInfo.DateOfIncident)
            .OrderByDescending(item => item.CreatedAtUtc)
            .FirstOrDefaultAsync(item =>
                item.ClientFirstName.ToLower() == firstName.ToLower() &&
                item.ClientLastName.ToLower() == lastName.ToLower(), ct);
    }

    private static Case CreateMoveToManagementCase(
        Guid tenantId,
        Guid sellerOrgId,
        Guid userId,
        Lien lien,
        MoveSellingLienToManagementCaseInfoRequest? caseInfo)
    {
        var firstName = FirstNonEmpty(caseInfo?.ClientFirstName, lien.SubjectFirstName, "Jane")!;
        var lastName = FirstNonEmpty(caseInfo?.ClientLastName, lien.SubjectLastName, "Doe")!;
        var title = FirstNonEmpty($"{firstName} {lastName}".Trim(), lien.LienNumber);
        var notes = BuildMoveToManagementCaseNotes(caseInfo);
        var address = BuildMoveToManagementCaseAddress(caseInfo);
        var created = Case.Create(
            tenantId,
            sellerOrgId,
            $"SC-{Guid.CreateVersion7():N}"[..15].ToUpperInvariant(),
            firstName,
            lastName,
            userId,
            externalReference: lien.ExternalReference,
            title: title,
            clientDob: caseInfo?.ClientDob,
            clientAddress: address,
            dateOfIncident: caseInfo?.DateOfIncident ?? lien.IncidentDate,
            description: lien.Description,
            notes: notes,
            clientAddressLine1: caseInfo?.ClientAddress,
            clientCity: caseInfo?.ClientCity,
            clientState: caseInfo?.ClientState,
            clientPostalCode: caseInfo?.ClientZipCode,
            incidentState: caseInfo?.StateOfIncident);

        return created;
    }

    private static void ApplyMoveToManagementCaseInfo(
        Case caseEntity,
        MoveSellingLienToManagementCaseInfoRequest? caseInfo,
        Guid userId)
    {
        if (caseInfo is null)
            return;

        caseEntity.Update(
            caseInfo.ClientFirstName!,
            caseInfo.ClientLastName!,
            userId,
            title: caseEntity.Title,
            externalReference: caseEntity.ExternalReference,
            clientDob: caseInfo.ClientDob,
            clientPhone: caseEntity.ClientPhone,
            clientEmail: caseEntity.ClientEmail,
            clientAddress: BuildMoveToManagementCaseAddress(caseInfo) ?? caseEntity.ClientAddress,
            dateOfIncident: caseInfo.DateOfIncident ?? caseEntity.DateOfIncident,
            insuranceCarrier: caseEntity.InsuranceCarrier,
            policyNumber: caseEntity.PolicyNumber,
            claimNumber: caseEntity.ClaimNumber,
            description: caseEntity.Description,
            notes: BuildMoveToManagementCaseNotes(caseInfo, caseEntity.Notes),
            clientAddressLine1: caseInfo.ClientAddress ?? caseEntity.ClientAddressLine1,
            clientCity: caseInfo.ClientCity ?? caseEntity.ClientCity,
            clientState: caseInfo.ClientState ?? caseEntity.ClientState,
            clientPostalCode: caseInfo.ClientZipCode ?? caseEntity.ClientPostalCode,
            incidentState: caseInfo.StateOfIncident ?? caseEntity.IncidentState,
            currentMedicalStatus: caseEntity.CurrentMedicalStatus,
            trackingFollowUpDate: caseEntity.TrackingFollowUpDate,
            minorComp: caseEntity.MinorComp,
            caseDropped: caseEntity.CaseDropped,
            attorneyContactPersonId: caseEntity.AttorneyContactPersonId);
    }

    private static string? BuildMoveToManagementCaseNotes(
        MoveSellingLienToManagementCaseInfoRequest? caseInfo,
        string? existingNotes = null)
    {
        if (caseInfo is null)
            return existingNotes;

        var metadata = ParseCaseMetadata(existingNotes);
        AddMetadata(metadata, "isServicing", caseInfo.IsServicing?.ToString(CultureInfo.InvariantCulture));
        AddMetadata(metadata, "statusLabel", caseInfo.StatusLabel);
        AddMetadata(metadata, "accidentTypeId", caseInfo.AccidentTypeId);
        AddMetadata(metadata, "accidentState", caseInfo.StateOfIncident);
        AddMetadata(metadata, "lawFirmId", caseInfo.LawFirmId);
        AddMetadata(metadata, "caseManagerId", caseInfo.CaseManagerId);

        var userNotes = FirstNonEmpty(caseInfo.Notes, ExtractSellingCaseTrackingNotes(existingNotes));
        if (metadata.Count == 0)
            return userNotes;

        var serializedMetadata = string.Join("; ", metadata.Select(item => $"{item.Key}={item.Value}"));
        return string.IsNullOrWhiteSpace(userNotes)
            ? $"{LegacyCaseMetadataMarker}{Environment.NewLine}{serializedMetadata}"
            : $"{userNotes}{Environment.NewLine}{Environment.NewLine}{LegacyCaseMetadataMarker}{Environment.NewLine}{serializedMetadata}";
    }

    private static string? BuildMoveToManagementCaseAddress(MoveSellingLienToManagementCaseInfoRequest? caseInfo)
    {
        if (caseInfo is null)
            return null;

        var parts = new[]
        {
            caseInfo.ClientAddress,
            caseInfo.ClientCity,
            caseInfo.ClientState,
            caseInfo.ClientZipCode,
        }
        .Select(part => part?.Trim())
        .Where(part => !string.IsNullOrWhiteSpace(part))
        .ToList();

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static void AddMetadata(List<string> parts, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            parts.Add($"{key}={value.Trim()}");
    }

    private static async Task ReassignLienServicingItemsToCaseAsync(
        LiensDbContext db,
        Guid tenantId,
        Guid lienId,
        Guid caseId,
        Guid userId,
        CancellationToken ct)
    {
        var items = await db.ServicingItems
            .Where(item =>
                item.TenantId == tenantId &&
                item.LienId == lienId &&
                item.CaseId != caseId)
            .ToListAsync(ct);

        foreach (var item in items)
        {
            item.Update(
                item.TaskType,
                item.Description,
                item.AssignedTo,
                userId,
                item.Priority,
                caseId,
                lienId,
                item.DueDate,
                item.Notes,
                item.AssignedToUserId);
        }
    }

    private static async Task<Company> EnsureTenantFundingCompanyAsync(
        LiensDbContext db,
        Guid tenantId,
        Guid sellerOrgId,
        string tenantName,
        Guid userId,
        CancellationToken ct)
    {
        var normalizedName = Company.NormalizeName(tenantName);
        var company = await db.Companies.FirstOrDefaultAsync(item =>
            item.TenantId == tenantId &&
            item.OrgId == sellerOrgId &&
            item.CompanyTypeId == CompanyDirectoryReferenceData.FundingCompanyId &&
            item.NormalizedName == normalizedName,
            ct);

        if (company is null)
        {
            company = Company.Create(
                tenantId,
                sellerOrgId,
                CompanyDirectoryReferenceData.FundingCompanyId,
                tenantName,
                userId,
                linkedTenantId: tenantId);
            db.Companies.Add(company);
            return company;
        }

        if (!company.IsActive)
            company.Reactivate(userId);

        return company;
    }

    private static async Task EnsureManagementPartyInfoAsync(
        LiensDbContext db,
        Guid tenantId,
        Guid sellerOrgId,
        Lien lien,
        Guid caseId,
        Guid userId,
        CancellationToken ct)
    {
        var existingRows = await db.ServicingItems
            .Where(item =>
                item.TenantId == tenantId &&
                item.LienId == lien.Id &&
                item.TaskType == "LegacyMedicalFacilityInfo")
            .OrderByDescending(item => item.UpdatedAtUtc)
            .ToListAsync(ct);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in existingRows.OrderBy(item => item.UpdatedAtUtc))
        {
            foreach (var field in ParseCaseMetadata(row.Notes))
                fields[field.Key] = field.Value;
        }

        foreach (var managedField in new[]
        {
            "facilityId",
            "facilityName",
            "email",
            "phone",
            "medicalProviderId",
            "medicalProvider",
            "fundingCompanyId",
            "fundingCompany",
            "fundingCompanyContactId",
            "fundingCompanyContact",
            "initialServiceDate",
            "endServiceDate",
            "receivableDueDate",
        })
        {
            fields.Remove(managedField);
        }

        var canonicalFacility = lien.MedicalFacilityCompanyId.HasValue
            ? await db.Companies.AsNoTracking().FirstOrDefaultAsync(company =>
                company.TenantId == tenantId &&
                company.OrgId == sellerOrgId &&
                company.Id == lien.MedicalFacilityCompanyId.Value, ct)
            : null;
        var legacyFacility = lien.FacilityId.HasValue
            ? await db.Facilities.AsNoTracking().FirstOrDefaultAsync(facility =>
                facility.TenantId == tenantId &&
                facility.OrgId == sellerOrgId &&
                facility.Id == lien.FacilityId.Value, ct)
            : null;
        var medicalProvider = lien.MedicalProviderCompanyId.HasValue
            ? await db.Companies.AsNoTracking().FirstOrDefaultAsync(company =>
                company.TenantId == tenantId &&
                company.OrgId == sellerOrgId &&
                company.Id == lien.MedicalProviderCompanyId.Value, ct)
            : null;
        Company? canonicalFundingCompany = null;
        if (lien.FundingCompanyCompanyId.HasValue)
        {
            canonicalFundingCompany = db.Companies.Local.FirstOrDefault(company =>
                company.TenantId == tenantId &&
                company.OrgId == sellerOrgId &&
                company.Id == lien.FundingCompanyCompanyId.Value);
            canonicalFundingCompany ??= await db.Companies.AsNoTracking().FirstOrDefaultAsync(company =>
                company.TenantId == tenantId &&
                company.OrgId == sellerOrgId &&
                company.Id == lien.FundingCompanyCompanyId.Value, ct);
        }
        var canonicalFundingContact = lien.FundingCompanyContactPersonId.HasValue && lien.FundingCompanyCompanyId.HasValue
            ? await db.CompanyContactPersons.AsNoTracking().FirstOrDefaultAsync(contact =>
                contact.TenantId == tenantId &&
                contact.Id == lien.FundingCompanyContactPersonId.Value &&
                contact.CompanyId == lien.FundingCompanyCompanyId.Value, ct)
            : null;
        var legacyFundingCompany = lien.FundingCompanyId.HasValue
            ? await db.Contacts.AsNoTracking().FirstOrDefaultAsync(contact =>
                contact.TenantId == tenantId &&
                contact.Id == lien.FundingCompanyId.Value, ct)
            : null;
        var legacyFundingContact = lien.FundingCompanyContactId.HasValue
            ? await db.Contacts.AsNoTracking().FirstOrDefaultAsync(contact =>
                contact.TenantId == tenantId &&
                contact.Id == lien.FundingCompanyContactId.Value, ct)
            : null;

        var managementFacility = legacyFacility;
        if (canonicalFacility is not null)
        {
            managementFacility = await EnsureLegacyFacilityCompatibilityAsync(
                db, tenantId, sellerOrgId, canonicalFacility, userId, ct);
            lien.SetSellingMedicalFacility(managementFacility.Id, canonicalFacility.Id, userId);
        }

        var managementProvider = medicalProvider is null
            ? null
            : await EnsureLegacyProviderCompatibilityAsync(
                db, tenantId, sellerOrgId, medicalProvider, userId, ct);

        AddMetadata(fields, "facilityId", managementFacility?.Id.ToString());
        AddMetadata(fields, "facilityName", canonicalFacility?.Name ?? managementFacility?.Name);
        AddMetadata(fields, "email", canonicalFacility?.Email ?? managementFacility?.Email);
        AddMetadata(fields, "phone", canonicalFacility?.Phone ?? managementFacility?.Phone);
        AddMetadata(fields, "medicalProviderId", managementProvider?.Id.ToString());
        AddMetadata(fields, "medicalProvider", medicalProvider?.Name ?? managementProvider?.Organization ?? managementProvider?.DisplayName);
        AddMetadata(fields, "fundingCompanyId", canonicalFundingCompany?.Id.ToString() ?? legacyFundingCompany?.Id.ToString());
        AddMetadata(fields, "fundingCompany", canonicalFundingCompany?.Name ?? legacyFundingCompany?.Organization ?? legacyFundingCompany?.DisplayName);
        AddMetadata(fields, "fundingCompanyContactId", canonicalFundingContact?.Id.ToString() ?? legacyFundingContact?.Id.ToString());
        AddMetadata(fields, "fundingCompanyContact", canonicalFundingContact is null ? legacyFundingContact?.DisplayName : DisplayName(canonicalFundingContact));
        AddMetadata(fields, "initialServiceDate", lien.InitialServiceDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        AddMetadata(fields, "endServiceDate", lien.EndServiceDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        AddMetadata(fields, "receivableDueDate", lien.ReceivableDueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        if (fields.Count == 0)
        {
            db.ServicingItems.RemoveRange(existingRows);
            return;
        }

        var notes = string.Join("; ", fields.Select(item => $"{item.Key}={item.Value}"));
        var existing = existingRows.FirstOrDefault();
        if (existing is null)
        {
            db.ServicingItems.Add(ServicingItem.Create(
                tenantId,
                sellerOrgId,
                $"LMFI-{Guid.CreateVersion7():N}".ToUpperInvariant(),
                "LegacyMedicalFacilityInfo",
                "Legacy medical facility information",
                "system",
                userId,
                caseId: caseId,
                lienId: lien.Id,
                notes: notes));
            return;
        }

        existing.Update(
            existing.TaskType,
            existing.Description,
            existing.AssignedTo,
            userId,
            existing.Priority,
            caseId,
            lien.Id,
            existing.DueDate,
            notes,
            existing.AssignedToUserId);
        db.ServicingItems.RemoveRange(existingRows.Skip(1));
    }

    private static async Task<Facility> EnsureLegacyFacilityCompatibilityAsync(
        LiensDbContext db,
        Guid tenantId,
        Guid sellerOrgId,
        Company company,
        Guid userId,
        CancellationToken ct)
    {
        var marker = $"SellingCompanyId={company.Id}";
        var contact = await db.Contacts.FirstOrDefaultAsync(item =>
            item.TenantId == tenantId &&
            item.OrgId == sellerOrgId &&
            (item.ContactType == ContactType.Facility || item.ContactType == ContactType.MedicalFacility) &&
            item.ContactSubtype == null &&
            (item.Notes == marker || item.Organization == company.Name || item.DisplayName == company.Name), ct);

        Facility? facility = null;
        if (contact?.FacilityId is { } linkedFacilityId)
        {
            facility = await db.Facilities.FirstOrDefaultAsync(item =>
                item.TenantId == tenantId &&
                item.OrgId == sellerOrgId &&
                item.Id == linkedFacilityId, ct);
        }

        facility ??= await db.Facilities.FirstOrDefaultAsync(item =>
            item.TenantId == tenantId &&
            item.OrgId == sellerOrgId &&
            (item.ExternalReference == company.Id.ToString() || item.Name == company.Name), ct);
        if (facility is null)
        {
            facility = Facility.Create(
                tenantId,
                sellerOrgId,
                company.Name,
                userId,
                externalReference: company.Id.ToString(),
                addressLine1: company.AddressLine1,
                city: company.City,
                state: company.State,
                postalCode: company.PostalCode,
                phone: company.Phone,
                email: company.Email);
            db.Facilities.Add(facility);
        }
        else if (!facility.IsActive)
        {
            facility.Reactivate(userId);
        }

        if (contact is null)
        {
            var (firstName, lastName) = SplitCompatibilityName(company.Name, "Facility");
            contact = Contact.Create(
                tenantId,
                sellerOrgId,
                ContactType.MedicalFacility,
                firstName,
                lastName,
                userId,
                facilityId: facility.Id,
                organization: company.Name,
                email: company.Email,
                phone: company.Phone,
                addressLine1: company.AddressLine1,
                city: company.City,
                state: company.State,
                postalCode: company.PostalCode,
                notes: marker);
            db.Contacts.Add(contact);
        }
        else
        {
            if (!contact.IsActive)
                contact.Reactivate(userId);
            if (contact.FacilityId != facility.Id)
            {
                contact.Update(
                    contact.FirstName,
                    contact.LastName,
                    contact.ContactType,
                    userId,
                    facilityId: facility.Id,
                    contactSubtype: contact.ContactSubtype,
                    title: contact.Title,
                    organization: contact.Organization,
                    email: contact.Email,
                    phone: contact.Phone,
                    fax: contact.Fax,
                    website: contact.Website,
                    addressLine1: contact.AddressLine1,
                    city: contact.City,
                    state: contact.State,
                    postalCode: contact.PostalCode,
                    notes: contact.Notes);
            }
        }

        return facility;
    }

    private static async Task<Contact> EnsureLegacyProviderCompatibilityAsync(
        LiensDbContext db,
        Guid tenantId,
        Guid sellerOrgId,
        Company company,
        Guid userId,
        CancellationToken ct)
    {
        var marker = $"SellingCompanyId={company.Id}";
        var contact = await db.Contacts.FirstOrDefaultAsync(item =>
            item.TenantId == tenantId &&
            item.OrgId == sellerOrgId &&
            item.ContactType == ContactType.Provider &&
            item.ContactSubtype == null &&
            (item.Notes == marker || item.Organization == company.Name || item.DisplayName == company.Name), ct);
        if (contact is not null)
        {
            if (!contact.IsActive)
                contact.Reactivate(userId);
            return contact;
        }

        var (firstName, lastName) = SplitCompatibilityName(company.Name, "Provider");
        contact = Contact.Create(
            tenantId,
            sellerOrgId,
            ContactType.Provider,
            firstName,
            lastName,
            userId,
            organization: company.Name,
            email: company.Email,
            phone: company.Phone,
            addressLine1: company.AddressLine1,
            city: company.City,
            state: company.State,
            postalCode: company.PostalCode,
            notes: marker);
        db.Contacts.Add(contact);
        return contact;
    }

    private static (string FirstName, string LastName) SplitCompatibilityName(string name, string fallbackLastName)
    {
        var parts = name.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => (fallbackLastName, fallbackLastName),
            1 => (parts[0], fallbackLastName),
            _ => (parts[0], parts[1]),
        };
    }

    private static async Task EnsureManagementAmountsAsync(
        LiensDbContext db,
        Guid tenantId,
        Guid sellerOrgId,
        Lien lien,
        Guid caseId,
        Guid userId,
        CancellationToken ct)
    {
        var staleRows = await db.ServicingItems
            .Where(item => item.TenantId == tenantId && item.LienId == lien.Id && item.TaskType == "LegacyMedicalCode")
            .ToListAsync(ct);

        var sellingRows = await db.ServicingItems.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.LienId == lien.Id && item.TaskType == SellingMedicalPricingTaskType)
            .OrderBy(item => item.CreatedAtUtc)
            .ToListAsync(ct);

        if (sellingRows.Count == 0)
        {
            if (staleRows.Count > 0)
            {
                foreach (var row in staleRows)
                {
                    row.Update(
                        row.TaskType,
                        row.Description,
                        row.AssignedTo,
                        userId,
                        row.Priority,
                        caseId,
                        lien.Id,
                        row.DueDate,
                        row.Notes,
                        row.AssignedToUserId);
                }
                return;
            }

            AddManagementPricingRow(db, tenantId, sellerOrgId, caseId, lien.Id, userId, "SellingLien", lien.Description, lien.OriginalAmount, lien.AskAmount ?? 0m, null);
            return;
        }

        db.ServicingItems.RemoveRange(staleRows);

        foreach (var row in sellingRows)
        {
            SellingMedicalPricingEntry? pricing = null;
            if (!string.IsNullOrWhiteSpace(row.Notes))
            {
                try
                {
                    pricing = JsonSerializer.Deserialize<SellingMedicalPricingEntry>(row.Notes, SellingPricingJsonOptions);
                }
                catch (JsonException)
                {
                    pricing = null;
                }
            }

            AddManagementPricingRow(
                db,
                tenantId,
                sellerOrgId,
                caseId,
                lien.Id,
                userId,
                FirstNonEmpty(pricing?.MedicalCode, row.Description, "SellingLien")!,
                pricing?.Description,
                pricing?.BillingAmount ?? 0m,
                pricing?.TargetSaleAmount ?? 0m,
                pricing?.MedicareCost);
        }
    }

    private static void AddManagementPricingRow(
        LiensDbContext db,
        Guid tenantId,
        Guid sellerOrgId,
        Guid caseId,
        Guid lienId,
        Guid userId,
        string medicalCode,
        string? description,
        decimal billingAmount,
        decimal purchaseAmount,
        decimal? medicareCost)
    {
        db.ServicingItems.Add(ServicingItem.Create(
            tenantId,
            sellerOrgId,
            $"LMC-{Guid.CreateVersion7():N}".ToUpperInvariant(),
            "LegacyMedicalCode",
            $"Medical code {medicalCode}",
            "system",
            userId,
            caseId: caseId,
            lienId: lienId,
            notes: $"code={medicalCode}; description={description ?? string.Empty}; medicareCost={medicareCost?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}; billingAmount={billingAmount.ToString(CultureInfo.InvariantCulture)}; purchaseAmount={purchaseAmount.ToString(CultureInfo.InvariantCulture)}"));
    }

    private static async Task<SellerPortalMessageRequestReadResult> ReadSellerPortalMessageRequestAsync(
        HttpRequest httpRequest,
        CancellationToken ct)
    {
        if (httpRequest.HasFormContentType)
        {
            var form = await httpRequest.ReadFormAsync(ct);
            var message = form["message"].FirstOrDefault() ?? string.Empty;
            var files = form.Files;
            var validation = SellingPublicEndpoints.ValidatePortalMessageAttachments(message, files);
            if (validation is not null)
                return new SellerPortalMessageRequestReadResult(null, [], validation);

            return new SellerPortalMessageRequestReadResult(
                new SellingPublicEndpoints.PublicPortalMessageRequest(message),
                SellingPublicEndpoints.OpenPortalMessageAttachmentUploads(files),
                null);
        }

        if (httpRequest.ContentLength.GetValueOrDefault() == 0)
            return new SellerPortalMessageRequestReadResult(null, [], Results.BadRequest(new { error = new { code = "message_required", message = "Request body is required." } }));

        try
        {
            var request = await httpRequest.ReadFromJsonAsync<SellingPublicEndpoints.PublicPortalMessageRequest>(cancellationToken: ct);
            var message = request?.Message ?? string.Empty;
            var validation = SellingPublicEndpoints.ValidatePortalMessageAttachments(message, new FormFileCollection());
            return validation is null
                ? new SellerPortalMessageRequestReadResult(request, [], null)
                : new SellerPortalMessageRequestReadResult(null, [], validation);
        }
        catch (JsonException)
        {
            return new SellerPortalMessageRequestReadResult(null, [], Results.BadRequest(new { error = new { code = "invalid_message_request", message = "Message request body is invalid." } }));
        }
    }

    private static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<SellingPortalMessageAttachment>>> LoadSellerMessageAttachmentsAsync(
        LiensDbContext db,
        Guid tenantId,
        Guid sellerOrgId,
        Guid lienId,
        IReadOnlyList<SellingPortalMessage> messages,
        CancellationToken ct)
    {
        var messageIds = messages.Select(message => message.Id).ToList();
        if (messageIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<SellingPortalMessageAttachment>>();

        var attachments = await db.SellingPortalMessageAttachments
            .AsNoTracking()
            .Where(attachment =>
                attachment.TenantId == tenantId &&
                attachment.SellerOrgId == sellerOrgId &&
                attachment.LienId == lienId &&
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

    private static async Task<string?> IssueSellerMessageAttachmentAccessUrlAsync(
        IHttpClientFactory httpClientFactory,
        IServiceTokenIssuer serviceTokenIssuer,
        ILoggerFactory loggerFactory,
        Guid tenantId,
        Guid sellerOrgId,
        Guid actorUserId,
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
        if (serviceTokenIssuer.IsConfigured)
        {
            try
            {
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    serviceTokenIssuer.IssueToken(tenantId.ToString(), actorUserId.ToString(), DocumentsServiceAudience));
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger(nameof(SellingV2Endpoints))
                    .LogWarning(ex, "Unable to mint Documents service token for tenant {TenantId}", tenantId);
            }
        }

        request.Headers.TryAddWithoutValidation("X-Organization-Id", sellerOrgId.ToString());

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
                return NormalizeSellerMessageAttachmentRedeemUrl(redeemUrl.GetString()!);
            }

            if (data.TryGetProperty("accessToken", out var accessToken) &&
                !string.IsNullOrWhiteSpace(accessToken.GetString()))
            {
                return $"/documents/access/{Uri.EscapeDataString(accessToken.GetString()!)}";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            loggerFactory.CreateLogger(nameof(SellingV2Endpoints))
                .LogWarning(ex, "Documents access token request failed for seller message attachment document {DocumentId}", documentId);
        }

        return null;
    }

    private static string NormalizeSellerMessageAttachmentRedeemUrl(string redeemUrl)
    {
        var trimmed = redeemUrl.Trim();
        if (trimmed.StartsWith("/documents/access/", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        if (trimmed.StartsWith("/access/", StringComparison.OrdinalIgnoreCase))
            return $"/documents{trimmed}";
        return trimmed;
    }

    private static string? BuildSellerMessageAttachmentActionUrl(Guid lienId, Guid attachmentId, string action)
    {
        if (attachmentId == Guid.Empty)
            return null;

        var normalizedAction = string.Equals(action, "download", StringComparison.OrdinalIgnoreCase)
            ? "download"
            : "view";
        return $"/api/selling/api/liens/selling/liens/{lienId:D}/message-attachments/{attachmentId:D}/{normalizedAction}";
    }

    private static (Guid TenantId, Guid OrgId, Guid UserId) RequireSellerContext(ICurrentRequestContext context) => (context.TenantId ?? throw new UnauthorizedAccessException("Tenant context is required."), context.OrgId ?? throw new UnauthorizedAccessException("Organization context is required."), context.UserId ?? throw new UnauthorizedAccessException("User context is required."));
    private static (Guid TenantId, Guid OrgId, Guid UserId) RequireBuyerContext(ICurrentRequestContext context) => RequireSellerContext(context);
    private static SellerLienMessageResponse MapSellerLienMessage(
        SellingPortalMessage message,
        IReadOnlyList<SellingPortalMessageAttachment>? attachments = null)
        => new(
            message.Id,
            message.SenderType,
            message.SenderName,
            BuildInitials(message.SenderName),
            message.SenderEmail,
            message.Message,
            message.CreatedAtUtc,
            string.Equals(message.SenderType, SellingPortalMessageSenderType.Seller, StringComparison.Ordinal),
            (attachments ?? [])
                .Select(attachment => new SellerLienMessageAttachmentResponse(
                    attachment.Id,
                    attachment.FileName,
                    attachment.ContentType,
                    attachment.FileSizeBytes,
                    attachment.CreatedAtUtc,
                    BuildSellerMessageAttachmentActionUrl(message.LienId, attachment.Id, "view"),
                    BuildSellerMessageAttachmentActionUrl(message.LienId, attachment.Id, "download")))
                .ToList());

    private static string BuildInitials(string value)
    {
        var parts = value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(2)
            .Select(part => part[0])
            .ToArray();

        return parts.Length == 0
            ? "SL"
            : new string(parts).ToUpperInvariant();
    }

    private static string? NormalizeIntakeStatus(string? status) => IntakeStatuses.FirstOrDefault(candidate => string.Equals(candidate, status?.Trim(), StringComparison.OrdinalIgnoreCase));
    private static IResult? SellingMutationBlocked(Lien lien) => lien.MovedToManagementAtUtc.HasValue
        ? Results.Conflict(new { error = new { code = "lien_moved_to_management", message = "This lien is managed through Liens Management and can no longer be changed through Selling." } })
        : null;
    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    private static IResult? IntakeMutationBlocked(Lien lien) => IntakeStatuses.Contains(lien.SellerStatus ?? string.Empty)
        ? SellingMutationBlocked(lien)
        : Results.Conflict(new { error = new { code = "intake_locked", message = "Lien intake can be changed only while sellerStatus is Pending or Internal." } });
    private static string? NormalizeVisibility(string? visibility) => SellingListingVisibility.All.FirstOrDefault(candidate => string.Equals(candidate, visibility?.Trim(), StringComparison.OrdinalIgnoreCase));
    private static bool TryParseOptionalDate(
        JsonElement value,
        string key,
        out DateOnly? date,
        out IResult? error)
    {
        date = null;
        error = null;
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return true;

        if (value.ValueKind == JsonValueKind.String &&
            DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            date = parsed;
            return true;
        }

        error = ValidationError(key, $"{key} must be ISO yyyy-MM-dd or null.");
        return false;
    }

    private static bool TryParseOptionalString(
        JsonElement value,
        string key,
        out string? text,
        out IResult? error)
    {
        text = null;
        error = null;
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return true;
        if (value.ValueKind == JsonValueKind.String)
        {
            text = value.GetString();
            return true;
        }

        error = ValidationError(key, $"{key} must be a string or null.");
        return false;
    }

    private static IResult ValidationError(string key, string message) => Results.BadRequest(new { error = new { code = "validation_error", message, errors = new Dictionary<string, string[]> { [key] = [message] } } });
    private static IResult NotFoundLien(Guid lienId) => Results.NotFound(new { error = new { code = "not_found", message = $"Lien '{lienId}' was not found." } });
    private static bool HasIdempotencyKey(HttpRequest request, out IResult? error, out string? key)
    {
        key = request.Headers["Idempotency-Key"].FirstOrDefault()?.Trim();
        error = string.IsNullOrWhiteSpace(key) ? ValidationError("Idempotency-Key", "Idempotency-Key header is required.") : null;
        return error is null;
    }
    private static bool HasIdempotencyKey(HttpRequest request, out IResult? error) => HasIdempotencyKey(request, out error, out _);
    private static void AddActivity(LiensDbContext db, Lien lien, Guid userId, string description)
    {
        db.ChangeTracker.DetectChanges();
        var excludedProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(Lien.UpdatedAtUtc),
            nameof(Lien.UpdatedByUserId),
        };
        var changes = db.Entry(lien).Properties
            .Where(property => property.IsModified && !excludedProperties.Contains(property.Metadata.Name))
            .Select(property => new LienFieldChange(
                LienUpdateHistoryFormatter.DisplayFieldName(property.Metadata.Name),
                property.OriginalValue,
                property.CurrentValue))
            .ToList();

        var lienStatus = string.IsNullOrWhiteSpace(lien.SellerStatus) ? lien.Status : lien.SellerStatus;
        var activityDescription = LienUpdateHistoryFormatter.BuildSingleDescription(
            $"Lien Status: {lienStatus}. {description}",
            changes);
        db.LienStatusHistories.Add(LienStatusHistory.Create(
            lien.TenantId,
            lien.Id,
            lien.CaseId,
            activityDescription,
            userId));
    }
    private static string DisplayName(Contact contact) => string.IsNullOrWhiteSpace(contact.Organization) ? contact.DisplayName : contact.Organization;
    private static string DisplayName(CompanyContactPerson contact) => $"{contact.FirstName} {contact.LastName}".Trim();
    private static (bool ready, string[] missing) Readiness(
        Lien lien,
        bool hasCase,
        int pricingRows,
        int documents,
        bool requireFundingCompany = true)
    {
        var missing = new List<string>();
        if (!lien.InitialServiceDate.HasValue) missing.Add("initialServiceDate");
        if (!hasCase) missing.Add("caseInformation");
        if (requireFundingCompany && !lien.FundingCompanyId.HasValue && !lien.FundingCompanyCompanyId.HasValue) missing.Add("fundingCompany");
        if (!lien.AskAmount.HasValue || lien.AskAmount.Value <= 0m) missing.Add("askAmount");
        if (pricingRows == 0) missing.Add("medicalPricing");
        if (documents == 0) missing.Add("documents");
        return (missing.Count == 0, missing.ToArray());
    }
    private static string[] AvailableActions(Lien lien)
    {
        string[] actions = lien.SellerStatus switch
        {
            SellingLienStatus.Pending or SellingLienStatus.Internal => ["prepare-sale", "archive"],
            SellingLienStatus.PreparedForSale => ["confirm-sale", "archive"],
            SellingLienStatus.SubmittedForSale => ["withdraw-sale", "archive", "buyer-access-links"],
            SellingLienStatus.Archived => ["restore"],
            _ => [],
        };

        return IsMoveToManagementEligible(lien) ? [.. actions, "keep"] : actions;
    }
    private static Dictionary<string, string> ParseCaseMetadata(string? notes)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(notes)) return metadata;

        const string legacyMetadataMarker = "[legacy-meta]";
        var markerIndex = notes.IndexOf(legacyMetadataMarker, StringComparison.Ordinal);
        var rawMetadata = markerIndex >= 0 ? notes[(markerIndex + legacyMetadataMarker.Length)..].Trim() : notes;
        foreach (var segment in rawMetadata.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex > 0)
                metadata[segment[..separatorIndex].Trim()] = segment[(separatorIndex + 1)..].Trim();
        }

        return metadata;
    }
    private sealed record MoveToManagementCaseResolution(Case Case, bool CaseCreated);
    private sealed class SellingMedicalPricingEntry
    {
        public string? MedicalCode { get; init; }
        public string? Description { get; init; }
        public DateOnly? ServiceDate { get; init; }
        public decimal BillingAmount { get; init; }
        public decimal? MedicareCost { get; init; }
        public decimal TargetSaleAmount { get; init; }
    }
    private static Guid? ParseMetadataGuid(IReadOnlyDictionary<string, string> metadata, string key)
        => metadata.TryGetValue(key, out var value) && Guid.TryParse(value, out var id) ? id : null;
    private static string AppendMetadata(string? notes, string key, Guid value)
    {
        var map = (notes ?? string.Empty).Split("; ", StringSplitOptions.RemoveEmptyEntries).Where(segment => segment.Contains('='))
            .Select(segment => segment.Split('=', 2)).ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
        map[key] = value.ToString();
        return string.Join("; ", map.Select(pair => $"{pair.Key}={pair.Value}"));
    }
    private static async Task<BatchUpload?> GetSellingImportAsync(LiensDbContext db, Guid tenantId, Guid id, CancellationToken ct) => await db.BatchUploads.FirstOrDefaultAsync(batch => batch.TenantId == tenantId && batch.Id == id && batch.Template == "SellingLienImport", ct);
    private static object MapBulkImport(BatchUpload batch) => new { importId = batch.Id, status = batch.ProcessStatus, batch.Rows, batch.FileName, batch.CreatedAtUtc, batch.CreatedByUserId, batch.UpdatedAtUtc };
    private static string NormalizeRowStatus(string status) => status.Trim().ToLowerInvariant() switch { "valid" => "VALID", "invalid" => "INVALID", "created" => "CREATED", "failed" => "FAILED", _ => status.Trim().ToUpperInvariant() };
    private static string TruncateImportFailureReason(string message) => message.Length <= 4000 ? message : message[..4000];
    private static void DetachImportRowEntities(LiensDbContext db, IEnumerable<object> entities)
    {
        foreach (var entity in entities)
            db.Entry(entity).State = EntityState.Detached;
    }
    private static string? ValidateImportRow(IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrWhiteSpace(SellingBulkImportSchema.GetValue(values, SellingBulkImportSchema.CaseCode)))
            return $"{SellingBulkImportSchema.CaseCode} is required.";
        if (ParseImportDate(values, SellingBulkImportSchema.InitialServiceDate) is null)
            return $"{SellingBulkImportSchema.InitialServiceDate} must be a valid date.";
        if (string.IsNullOrWhiteSpace(SellingBulkImportSchema.GetValue(values, SellingBulkImportSchema.FacilityName)))
            return $"{SellingBulkImportSchema.FacilityName} is required.";
        if (string.IsNullOrWhiteSpace(SellingBulkImportSchema.GetValue(values, SellingBulkImportSchema.MedicalCodeAndDescription)))
            return $"{SellingBulkImportSchema.MedicalCodeAndDescription} is required.";
        if (!TryParseImportDecimal(values, SellingBulkImportSchema.BillingAmount, out var billing) || billing < 0m)
            return $"{SellingBulkImportSchema.BillingAmount} must be a non-negative decimal.";

        var status = SellingBulkImportSchema.GetValue(values, SellingBulkImportSchema.LienStatus, "Lien Status*", "Seller Status");
        if (!string.IsNullOrWhiteSpace(status) && NormalizeImportStatusValue(status) is null)
            return $"{SellingBulkImportSchema.LienStatus} must be Pending or Internal.";

        var visibility = SellingBulkImportSchema.GetValue(values, SellingBulkImportSchema.ListingVisibility, "Lien Visibility");
        if (!string.IsNullOrWhiteSpace(visibility) && NormalizeVisibility(visibility) is null)
            return $"{SellingBulkImportSchema.ListingVisibility} must be Public or Private.";

        var purchaseDate = SellingBulkImportSchema.GetValue(values, SellingBulkImportSchema.PurchaseDate, "Purchase Date*");
        if (!string.IsNullOrWhiteSpace(purchaseDate) &&
            ParseImportDate(values, SellingBulkImportSchema.PurchaseDate, "Purchase Date*") is null)
            return $"{SellingBulkImportSchema.PurchaseDate} must be a valid date.";

        var targetAskAmount = SellingBulkImportSchema.GetValue(values, SellingBulkImportSchema.TargetAskAmount, "Purchase Amount*");
        if (!string.IsNullOrWhiteSpace(targetAskAmount) &&
            (!TryParseImportDecimal(values, SellingBulkImportSchema.TargetAskAmount, out var askAmount, "Purchase Amount*") || askAmount < 0m))
            return $"{SellingBulkImportSchema.TargetAskAmount} must be a non-negative decimal.";

        return null;
    }
    private static string ResolveImportLienNumber(IReadOnlyDictionary<string, string> values) => $"SL-{Guid.CreateVersion7():N}".ToUpperInvariant();
    private static Contact? ResolveImportContactByName(IEnumerable<Contact> contacts, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var matches = contacts.Where(contact => ImportContactNameMatches(contact, name)).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
    private static Facility? ResolveImportFacilityByName(IEnumerable<Facility> facilities, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var matches = facilities.Where(facility => ImportFacilityNameMatches(facility, name)).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
    private static bool ImportContactNameMatches(Contact contact, string name)
        => string.Equals(contact.Organization, name.Trim(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(contact.DisplayName, name.Trim(), StringComparison.OrdinalIgnoreCase);
    private static bool ImportFacilityNameMatches(Facility facility, string name)
        => string.Equals(facility.Name, name.Trim(), StringComparison.OrdinalIgnoreCase);
    private sealed record FundingCompanyLookupItem(Guid Id, string Name, Guid? OrgId);
    private sealed record FundingCompanyContactLookupItem(Guid Id, string Name, string? Email);
    private sealed record SellerPortalMessageRequestReadResult(
        SellingPublicEndpoints.PublicPortalMessageRequest? Request,
        IReadOnlyList<SellingPublicEndpoints.SellingPortalMessageAttachmentUpload> Attachments,
        IResult? Error);
    private sealed record SellerLienMessageResponse(
        Guid Id,
        string SenderType,
        string SenderName,
        string SenderInitials,
        string? SenderEmail,
        string Message,
        DateTime CreatedAtUtc,
        bool IsCurrentUser,
        IReadOnlyList<SellerLienMessageAttachmentResponse> Attachments);
    private sealed record SellerLienMessageAttachmentResponse(
        Guid Id,
        string FileName,
        string ContentType,
        long FileSizeBytes,
        DateTime CreatedAtUtc,
        string? ViewUrl,
        string? DownloadUrl);
    private sealed record SellingCaseLookupNames(
        IReadOnlyDictionary<string, string> AccidentTypeNames,
        IReadOnlyDictionary<Guid, string> LawFirmNames,
        IReadOnlyDictionary<Guid, string> CaseManagerNames);
    private sealed record SellingCaseResponse(
        Guid DraftId,
        Guid CaseId,
        string CaseNumber,
        string CaseStatus,
        string? AccidentTypeId,
        string? AccidentTypeName,
        string? AccidentState,
        DateOnly? DateOfLoss,
        Guid? HandlingLawFirmId,
        string? HandlingLawFirmName,
        Guid? CaseManagerId,
        string? CaseManagerName,
        string? CaseTrackingNotes,
        string FirstName,
        string LastName,
        DateOnly? Birthdate,
        string? Email,
        string? Phone,
        string? Gender,
        string? Address,
        string? City,
        string? State,
        string? Zipcode);
    private static (string Code, string Description) ParseImportMedicalCode(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        var separator = normalized.IndexOf(" - ", StringComparison.Ordinal);
        return separator > 0
            ? (normalized[..separator].Trim(), normalized[(separator + 3)..].Trim())
            : (normalized, string.Empty);
    }
    private static string? GetImportValue(IReadOnlyDictionary<string, string> values, string key) => values.FirstOrDefault(pair => string.Equals(pair.Key.Trim(), key, StringComparison.OrdinalIgnoreCase)).Value?.Trim();
    private static bool TryParseImportDecimal(
        IReadOnlyDictionary<string, string> values,
        string key,
        out decimal value,
        params string[] legacyAliases) =>
        decimal.TryParse(
            SellingBulkImportSchema.GetValue(values, key, legacyAliases),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out value);
    private static decimal ParseImportDecimal(
        IReadOnlyDictionary<string, string> values,
        string key,
        params string[] legacyAliases) =>
        TryParseImportDecimal(values, key, out var value, legacyAliases) ? value : 0m;
    private static DateOnly? ParseImportDate(
        IReadOnlyDictionary<string, string> values,
        string key,
        params string[] legacyAliases) =>
        DateOnly.TryParse(
            SellingBulkImportSchema.GetValue(values, key, legacyAliases),
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date)
            ? date
            : null;
    private static string NormalizeImportStatus(IReadOnlyDictionary<string, string> values) =>
        NormalizeImportStatusValue(SellingBulkImportSchema.GetValue(
            values,
            SellingBulkImportSchema.LienStatus,
            "Lien Status*",
            "Seller Status")) ?? SellingLienStatus.Pending;
    private static string? NormalizeImportStatusValue(string? status) =>
        string.Equals(status?.Trim(), "Open", StringComparison.OrdinalIgnoreCase)
            ? SellingLienStatus.Pending
            : NormalizeIntakeStatus(status);
    private static string NormalizeImportVisibility(IReadOnlyDictionary<string, string> values) =>
        NormalizeVisibility(SellingBulkImportSchema.GetValue(
            values,
            SellingBulkImportSchema.ListingVisibility,
            "Lien Visibility")) ?? SellingListingVisibility.Private;

    private sealed class BuyerViewPermissionFilter : IEndpointFilter
    {
        public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var user = context.HttpContext.User;
            if (!user.HasPermission(LiensPermissions.LienBrowse) && !user.HasPermission(LiensPermissions.LienReadHeld))
                return ValueTask.FromResult<object?>(Results.Forbid());
            return next(context);
        }
    }
}
