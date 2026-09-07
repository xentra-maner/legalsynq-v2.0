using BuildingBlocks.Authorization;
using BuildingBlocks.Authorization.Filters;
using BuildingBlocks.Context;
using Liens.Application.DTOs;
using Liens.Application.Interfaces;
using Liens.Application.Repositories;
using Liens.Application.Services;
using Liens.Api;
using Liens.Domain;
using Liens.Domain.Entities;
using Liens.Domain.Enums;
using Liens.Api.Serialization;
using Liens.Infrastructure.Identity;
using Liens.Infrastructure.Persistence;
using Liens.Infrastructure.Options;
using ManualMedicalCodeEntity = Liens.Domain.Entities.ManualMedicalCode;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Liens.Api.Endpoints;

public static class CaseEndpoints
{
    private const string LegacyMetadataMarker = "[legacy-meta]";
    private const int LegacyTimelineMaximumPageSize = 200;
    private const int LegacyTimelineMaximumWindow = 25_000;
    private const string DeletedLienHistoryDescription = "Lien status updated to Delete.";
    private const string MedicareProcedureLookupClientName = "MedicareProcedureLookup";
    private const string MedicareProcedureLookupApiKey = "1iuNYl3IYBHTSjmn34m0XOLLqfm1nrmz";
    private const string MedicareProcedureLookupAmaLicense = "b733fd32-ee85-4174-9ab1-e09ec14048bb";
    private static readonly Guid LegacyFallbackDocumentTypeId =
        Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid LegacyOtherDocumentTypeId =
        Guid.Parse("10000000-0000-0000-0000-000000000005");
    private static readonly JsonSerializerOptions MedicareJsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class IdentityUserResponse
    {
        public Guid Id { get; init; }
        public string? Email { get; init; }
        public string? FirstName { get; init; }
        public string? LastName { get; init; }
    }

    private sealed class IdentityUserDisplayResponse
    {
        public bool Found { get; init; }
        public string? Email { get; init; }
        public string? FirstName { get; init; }
        public string? LastName { get; init; }
        public string? DisplayName { get; init; }
    }

    private sealed record LegacyCaseTimelineItem(
        string Id,
        string CaseId,
        string Action,
        string Description,
        DateTime SortAt,
        int SourceRank,
        long LegacySequence,
        string Note,
        string Category,
        bool IsPinned,
        bool IsEdited,
        DateTime CreatedAt,
        string CreatedBy,
        DateTime? UpdatedAt,
        string UpdatedBy,
        Guid? UpdatedByUserId);

    private sealed record LegacyLienTimelineItem(
        string Id,
        string CaseId,
        string LienId,
        string Action,
        string Description,
        bool EnrichReferences,
        string UpdatedBy,
        Guid? UpdatedByUserId,
        DateTime SortAt,
        int SourceRank,
        long LegacySequence);

    private sealed record LegacyCaseCsvSource(
        Guid OrgId,
        string? Notes,
        string? ImportedCreatedByName,
        Guid? CreatedByUserId,
        Guid? UpdatedByUserId);

    private sealed record LegacyCaseCsvSupplement(
        IReadOnlyDictionary<string, string> Fields,
        string LawFirm,
        string CaseManager,
        string CreatedBy,
        string UpdatedBy);

    private static readonly string[] LegacyAllowedDocumentExtensions =
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

    private sealed class LegacyCreateCaseRequest
    {
        public string? code { get; init; }
        public string? firstname { get; init; }
        public string? lastname { get; init; }
        public string? dob { get; init; }
        public string? email { get; init; }
        public string? phone { get; init; }
        public string? clientEmail { get; init; }
        public string? clientPhone { get; init; }
        public string? address { get; init; }
        public string? city { get; init; }
        public string? state { get; init; }
        public string? zipcode { get; init; }
        public string? dateOfLoss { get; init; }
        public string? note { get; init; }
        public string? notes { get; init; }
        public string? externalCaseId { get; init; }
        public string? externalReference { get; init; }
        public string? lawFirmId { get; init; }
        public string? policyNumber { get; init; }
        public string? claimNumber { get; init; }
        public string? caseStatusId { get; init; }
        public string? caseManagerId { get; init; }
        public string? accidentTypeId { get; init; }
        public string? accidentStateId { get; init; }
        public string? caseType { get; init; }
        public string? stateOfIncident { get; init; }
        public string? minorComp { get; init; }
    }

    private sealed class LegacyUpdateCaseRequest
    {
        public string? firstname { get; init; }
        public string? lastname { get; init; }
        public string? dob { get; init; }
        public string? email { get; init; }
        public string? phone { get; init; }
        public string? clientEmail { get; init; }
        public string? clientPhone { get; init; }
        public string? address { get; init; }
        public string? city { get; init; }
        public string? state { get; init; }
        public string? zipcode { get; init; }
        public string? dateOfLoss { get; init; }
        public string? note { get; init; }
        public string? externalCaseId { get; init; }
        public string? lawFirmId { get; init; }
    }

    private sealed class LegacyCaseV3FilterRequest
    {
        public int page { get; init; } = 1;
        public int limit { get; init; } = 20;
        public string? lawFirmId { get; init; }
        public string? accidentTypeId { get; init; }
        public string? statusId { get; init; }
        public string? caseManagerId { get; init; }
        public string? keyword { get; init; }
        public string? sortBy { get; init; }
        public string? sortDirection { get; init; }
    }

    private sealed class LegacyLawFirmV3Request
    {
        public string? LawFirmId { get; init; }
        public string? Keyword { get; init; }
        public int Page { get; init; } = 1;
        public int Limit { get; init; } = 10;
    }

    private sealed class LegacyMedicalLiensV3Request
    {
        public string? MedicalId { get; init; }
        public string? Keyword { get; init; }
        public int Page { get; init; } = 1;
        public int Limit { get; init; } = 10;
    }

    private sealed class LegacyFundingCompanyLiensV3Request
    {
        public string? FundingCompanyId { get; init; }
        public string? Keyword { get; init; }
        public int Page { get; init; } = 1;
        public int Limit { get; init; } = 10;
    }

    private sealed class LegacyFacilityLiensV3Request
    {
        public string? FacilityId { get; init; }
        public string? Keyword { get; init; }
        public int Page { get; init; } = 1;
        public int Limit { get; init; } = 10;
    }

    private sealed class LegacyLeadCaseV3Request
    {
        public string? LeadId { get; init; }
        public string? Keyword { get; init; }
        public int Page { get; init; } = 1;
        public int Limit { get; init; } = 10;
    }

    private sealed class LegacyCaseUpdatesV3Request
    {
        public string? CaseId { get; init; }
        public int Page { get; init; } = 1;
        public int Limit { get; init; } = 10;
    }

    private sealed class LegacyLiensUpdatesV3Request
    {
        public string? CaseId { get; init; }
        public int Page { get; init; } = 1;
        public int Limit { get; init; } = 10;
    }

    private sealed class LegacyLiensMedicalInformationFacilityRequest
    {
        public string? id { get; init; }
        public string? liensId { get; init; }
        public string? facilityId { get; init; }
        public string? facility { get; init; }
        public string? facilityContactId { get; init; }
        public string? facilityContact { get; init; }
        public string? email { get; init; }
        public string? phone { get; init; }
        public string? medicalProviderId { get; init; }
        public string? medicalProvider { get; init; }
    }

    private sealed class LegacyLiensMedicalRequest
    {
        public string? id { get; init; }
        public string? caseId { get; init; }
        public string? status { get; init; }
        public string? purchaseDate { get; init; }
        public string? initialServiceDate { get; init; }
        public string? endServiceDate { get; init; }
        public string? note { get; init; }
        public string? isBulk { get; init; }
        public string? isServicing { get; init; }
        public string? fundingCompanyId { get; init; }
    }

    private sealed class LegacyLiensMedicalCodeRequest
    {
        public string? id { get; init; }
        public string? liensId { get; init; }
        public string? code { get; init; }
        public string? description { get; init; }
        public string? medicareCost { get; init; }
        public string? billingAmount { get; init; }
        public string? purchaseAmount { get; init; }
        public string? payee { get; init; }
        public string? outboundCheckNumber { get; init; }
    }

    private sealed class LegacyLiensMedicalCodeResponse
    {
        public string id { get; init; } = string.Empty;
        public string liensId { get; init; } = string.Empty;
        public string code { get; init; } = string.Empty;
        public string description { get; init; } = string.Empty;
        public string medicareCost { get; init; } = string.Empty;
        public string billingAmount { get; init; } = string.Empty;
        public string purchaseAmount { get; init; } = string.Empty;
        public string payee { get; init; } = string.Empty;
        public string outboundCheckNumber { get; init; } = string.Empty;
        public string created { get; init; } = string.Empty;
        public string createdBy { get; init; } = string.Empty;
        public string updated { get; init; } = string.Empty;
        public string updatedBy { get; init; } = string.Empty;
    }

    private sealed class LegacyPayeeOutboundRequest
    {
        public string? id { get; init; }
        public string? liensId { get; init; }
        public string? payee { get; init; }
        public string? outboundCheckNumber { get; init; }
    }

    private sealed class LegacyCaseManagerRequest
    {
        public string? firstname { get; init; }
        public string? lastname { get; init; }
        public string? email { get; init; }
        public string? phone { get; init; }
        public string? lawfirmId { get; init; }
        public string? roleId { get; init; }
    }

    private sealed class LegacyCaseManagerUpdateRequest
    {
        public string? id { get; init; }
        public string? firstname { get; init; }
        public string? lastname { get; init; }
        public string? email { get; init; }
        public string? phone { get; init; }
        public string? lawfirmId { get; init; }
        public string? roleId { get; init; }
    }

    private sealed class LegacyReassignLawFirmRequest
    {
        public string? caseId { get; init; }
        public string? lawfirm { get; init; }
    }

    private sealed class LegacyReassignCaseManagerRequest
    {
        public string? caseId { get; init; }
        public string? caseManager { get; init; }
    }

    private sealed class LegacyReassignLeadRequest
    {
        public string? caseId { get; init; }
        public string? leadId { get; init; }
    }

    private sealed class LegacyBatchReassignRequest
    {
        public string? contactType { get; init; }
        public string? oldId { get; init; }
        public string? newId { get; init; }
    }

    private sealed record MedicareProcedureCode(
        string Code,
        string Description,
        int Frequency);

    private sealed record SellingMedicalPricingFallback
    {
        public string? MedicalCode { get; init; }
        public string? Description { get; init; }
        public decimal BillingAmount { get; init; }
        public decimal? MedicareCost { get; init; }
        public decimal TargetSaleAmount { get; init; }
    }

    private sealed class LegacyGenerateCaseCsvRequest
    {
        public string? caseId { get; init; }
        public string? keyword { get; init; }
        public string? lawFirmId { get; init; }
        public string? accidentTypeId { get; init; }
        public string? statusId { get; init; }
        public string? caseManagerId { get; init; }
        public string? sortBy { get; init; }
        public string? sortDirection { get; init; }
        public bool legacyFormat { get; init; }
    }

    private sealed class LegacyGenerateLiensCsvRequest
    {
        public string? keyword { get; init; }
        public string? caseId { get; init; }
        public string? liensId { get; init; }
        public string? lawFirmId { get; init; }
        public string? medicalFacilityId { get; init; }
        public string? purchaseDate { get; init; }
        public string? closedDate { get; init; }
        public string? caseManagerId { get; init; }
        public string? lienStatusId { get; init; }
        public bool legacyFormat { get; init; }
    }

    private sealed class LegacyLiensCsvRow
    {
        public string CaseCode { get; init; } = string.Empty;
        public string LiensCode { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string StatusLabel { get; init; } = string.Empty;
        public string PlaintiffName { get; init; } = string.Empty;
        public string DisplayLawFirm { get; init; } = string.Empty;
        public string DisplayCaseManager { get; init; } = string.Empty;
        public string DisplayFacilityName { get; init; } = string.Empty;
        public string PurchaseDate { get; init; } = string.Empty;
        public string InitialServiceDate { get; init; } = string.Empty;
        public string EndServiceDate { get; init; } = string.Empty;
        public string Note { get; init; } = string.Empty;
        public string FacilityEmail { get; init; } = string.Empty;
        public string FacilityPhone { get; init; } = string.Empty;
        public string TotalPurchase { get; init; } = string.Empty;
        public string TotalBilling { get; init; } = string.Empty;
        public string LawFirm { get; init; } = string.Empty;
        public string CaseManager { get; init; } = string.Empty;
        public string FacilityName { get; init; } = string.Empty;
        public string FacilityContactName { get; init; } = string.Empty;
        public string MedicalProvider { get; init; } = string.Empty;
        public string PlainTiffName { get; init; } = string.Empty;
        public string ClosedDate { get; init; } = string.Empty;
    }

    private sealed class LegacyPayeeOutboundResponse
    {
        public string id { get; init; } = string.Empty;
        public string liensId { get; init; } = string.Empty;
        public string payee { get; init; } = string.Empty;
        public string outboundCheckNumber { get; init; } = string.Empty;
    }

    private sealed class LegacyLiensMedicalInformationFacilityResponse
    {
        public string id { get; init; } = string.Empty;
        public string liensId { get; init; } = string.Empty;
        public string facilityId { get; init; } = string.Empty;
        public string facility { get; init; } = string.Empty;
        public string facilityContactId { get; init; } = string.Empty;
        public string facilityContact { get; init; } = string.Empty;
        public string email { get; init; } = string.Empty;
        public string phone { get; init; } = string.Empty;
        public string medicalProviderId { get; init; } = string.Empty;
        public string medicalProvider { get; init; } = string.Empty;
        public string created { get; init; } = string.Empty;
        public string createdBy { get; init; } = string.Empty;
        public string updated { get; init; } = string.Empty;
        public string updatedBy { get; init; } = string.Empty;
    }

    private sealed class LegacyLiensMedicalResponse
    {
        public string id { get; init; } = string.Empty;
        public string caseId { get; init; } = string.Empty;
        public string status { get; init; } = string.Empty;
        public string purchaseDate { get; init; } = string.Empty;
        public string initialServiceDate { get; init; } = string.Empty;
        public string endServiceDate { get; init; } = string.Empty;
        public string note { get; init; } = string.Empty;
        public string created { get; init; } = string.Empty;
        public string createdBy { get; init; } = string.Empty;
        public string updated { get; init; } = string.Empty;
        public string updatedBy { get; init; } = string.Empty;
        public string fundingCompanyId { get; init; } = string.Empty;
        public string fundingCompany { get; init; } = string.Empty;
        public string isBulk { get; init; } = string.Empty;
        public string isServicing { get; init; } = string.Empty;
    }

    private sealed class LegacyCaseOtherRequest
    {
        public string? caseId { get; init; }
        public string? reductionsRate { get; init; }
        public string? payment { get; init; }
        public string? adjustments { get; init; }
        public string? reductionsDate { get; init; }
        public string? netProfit { get; init; }
        public string? checkNumber { get; init; }
        public string? netOutboundCheckNumber { get; init; }
        public string? bulkPurchase { get; init; }
        public string? bank { get; init; }
    }

    private sealed class LegacyTaskStatusUpdateRequest
    {
        public string? StatusId { get; init; }
    }

    private sealed class LegacyCaseMergeRequest
    {
        public string? caseIdA { get; init; }
        public string? caseIdB { get; init; }
    }

    private sealed class LegacyLiensMedicalDocumentResponse
    {
        public string id { get; init; } = string.Empty;
        public string? liensId { get; init; }
        public string filename { get; init; } = string.Empty;
        public string typeId { get; init; } = string.Empty;
        public string documentTypeId { get; init; } = string.Empty;
        public string url { get; init; } = string.Empty;
        public string created { get; init; } = string.Empty;
        public string createdBy { get; init; } = string.Empty;
        public string updated { get; init; } = string.Empty;
        public string updatedBy { get; init; } = string.Empty;
    }

    private sealed class LegacyAllCaseDocumentResponse
    {
        public string id { get; init; } = string.Empty;
        public string? liensId { get; init; }
        public string filename { get; init; } = string.Empty;
        public string typeId { get; init; } = string.Empty;
        public string documentTypeId { get; init; } = string.Empty;
        public string url { get; init; } = string.Empty;
        public string created { get; init; } = string.Empty;
        public string createdBy { get; init; } = string.Empty;
        public string updated { get; init; } = string.Empty;
        public string updatedBy { get; init; } = string.Empty;
        public string mimeType { get; init; } = string.Empty;
    }

    private sealed class LegacyCaseInfoV2Response
    {
        public string caseId { get; init; } = string.Empty;
        public string caseCode { get; init; } = string.Empty;
        public string firstname { get; init; } = string.Empty;
        public string lastname { get; init; } = string.Empty;
        public string dateOfBirth { get; init; } = string.Empty;
        public string address { get; init; } = string.Empty;
        public string city { get; init; } = string.Empty;
        public string state { get; init; } = string.Empty;
        public string zipcode { get; init; } = string.Empty;
        public string isServicing { get; init; } = string.Empty;
        public string isUccFiled { get; init; } = string.Empty;
        public string isBulk { get; init; } = string.Empty;
        public string accidentType { get; init; } = string.Empty;
        public string accidentState { get; init; } = string.Empty;
        public string dateOfLoss { get; init; } = string.Empty;
        public string lawFirm { get; init; } = string.Empty;
        public string caseManager { get; init; } = string.Empty;
        public string note { get; init; } = string.Empty;
        public string created { get; init; } = string.Empty;
        public string createBy { get; init; } = string.Empty;
        public string updated { get; init; } = string.Empty;
        public string updateBy { get; init; } = string.Empty;
        public string status { get; init; } = string.Empty;
        public string currentStatus { get; init; } = string.Empty;
        public string currentMedicalStatus { get; init; } = string.Empty;
        public string currentAttributes { get; init; } = string.Empty;
        public string email { get; init; } = string.Empty;
        public string phone { get; init; } = string.Empty;
        public string gender { get; init; } = string.Empty;
        public string ssn { get; init; } = string.Empty;
        public string summary { get; init; } = string.Empty;
        public string countIndex { get; init; } = string.Empty;
        public string accidentTypeId { get; init; } = string.Empty;
        public string currentStatusId { get; init; } = string.Empty;
        public string currentMedicalStatusId { get; init; } = string.Empty;
        public string currentAttributesId { get; init; } = string.Empty;
        public string toGeneratePdf { get; init; } = string.Empty;
        public string switchedDate { get; init; } = string.Empty;
        public string lawFirmId { get; init; } = string.Empty;
        public string caseManagerId { get; init; } = string.Empty;
        public string trackingFollowUpDate { get; init; } = string.Empty;
        public string childSupportLiens { get; init; } = string.Empty;
        public string minorComp { get; init; } = string.Empty;
        public string leadId { get; init; } = string.Empty;
        public string caseManagerDesc { get; init; } = string.Empty;
        public string shareCase { get; init; } = string.Empty;
        public string confirmedWriting { get; init; } = string.Empty;
        public string caseAttorney { get; init; } = string.Empty;
        public string caseAttorneyId { get; init; } = string.Empty;
        public string leadDescription { get; init; } = string.Empty;
        public string caseDropped { get; init; } = string.Empty;
        public string externalCaseId { get; init; } = string.Empty;
        public int totalLiens { get; init; }
        public string lienStatus { get; init; } = string.Empty;
        public string lienStatusId { get; init; } = string.Empty;
        public string settlementStatus { get; init; } = string.Empty;
        public string settlementStatusId { get; init; } = string.Empty;
    }

    public static void MapCaseEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/liens/cases")
            .RequireAuthorization(Policies.AuthenticatedUser)
            .RequireProductAccess(LiensPermissions.ProductCode);

        group.MapGet("/", ListCases)
            .RequirePermission(LiensPermissions.CaseRead);

        group.MapGet("/{id:guid}", GetCaseById)
            .RequirePermission(LiensPermissions.CaseRead);

        group.MapGet("/v2", ListCases)
            .RequirePermission(LiensPermissions.CaseRead);

        group.MapGet("/by-number/{caseNumber}", GetCaseByCaseNumber)
            .RequirePermission(LiensPermissions.CaseRead);

        // Legacy compatibility route from previous service: GET /case/getcaseinfo/{id}
        // under the new base path becomes GET /api/liens/cases/getcaseinfo/{id}.
        group.MapGet("/getcaseinfo/{id:guid}", GetCaseInfoV2Legacy)
            .RequirePermission(LiensPermissions.CaseRead);

        // Legacy compatibility route from previous service: GET /case/law/{lawFirmId}/{isTotal?}
        // under the new base path becomes GET /api/liens/cases/law/{lawFirmId}/{isTotal?}.
        group.MapGet("/law/{lawFirmId}/{isTotal?}", GetCaseByLawFirmIdLegacy)
            .RequirePermission(LiensPermissions.CaseRead);

        // Legacy compatibility route from previous service: POST /case/law/v3
        // under the new base path becomes POST /api/liens/cases/law/v3.
        group.MapPost("/law/v3", GetLawFirmV3Legacy)
            .RequirePermission(LiensPermissions.CaseRead);

        // Legacy compatibility route from previous service: POST /case/medical/v3
        // under the new base path becomes POST /api/liens/cases/medical/v3.
        group.MapPost("/medical/v3", GetLiensByMedicalIdV3Legacy)
            .RequirePermission(LiensPermissions.CaseRead);

        // Legacy compatibility route from previous service: POST /case/funding/v3
        // under the new base path becomes POST /api/liens/cases/funding/v3.
        group.MapPost("/funding/v3", GetLiensByFundingCompanyIdV3Legacy)
            .RequirePermission(LiensPermissions.CaseRead);

        // Legacy compatibility route from previous service: POST /case/medical/facility/v3
        // under the new base path becomes POST /api/liens/cases/medical/facility/v3.
        group.MapPost("/medical/facility/v3", GetLiensByMedicalFacilityIdV3Legacy)
            .RequirePermission(LiensPermissions.CaseRead);

        // Legacy compatibility route from previous service: POST /case/leads/v3
        // under the new base path becomes POST /api/liens/cases/leads/v3.
        group.MapPost("/leads/v3", GetLeadV3Legacy)
            .RequirePermission(LiensPermissions.CaseRead);

        // Legacy compatibility route from previous service: POST /case/case-updates/v3
        // under the new base path becomes POST /api/liens/cases/case-updates/v3.
        group.MapPost("/case-updates/v3", GetCaseUpdatesV3Legacy)
            .RequirePermission(LiensPermissions.CaseRead);

        // Legacy compatibility route from previous service: POST /case/liens-updates/v3
        // under the new base path becomes POST /api/liens/cases/liens-updates/v3.
        group.MapPost("/liens-updates/v3", GetLiensUpdatesV3Legacy)
            .RequirePermission(LiensPermissions.CaseRead);

        group.MapPost("/", CreateCase)
            .RequirePermission(LiensPermissions.CaseCreate);

        group.MapPost("/duplicate-check", CheckDuplicateCase)
            .RequirePermission(LiensPermissions.CaseCreate);

        // Legacy compatibility route from previous service: POST /case/v3
        // under the new base path becomes POST /api/liens/cases/v3.
        group.MapPost("/v3", GetCasesV3Legacy)
            .RequirePermission(LiensPermissions.CaseRead);

        group.MapPut("/{id:guid}", UpdateCase)
            .RequirePermission(LiensPermissions.CaseUpdate);

        // Legacy compatibility route from previous service
        group.MapPost("/create", CreateCaseLegacy)
            .RequirePermission(LiensPermissions.CaseCreate);

        group.MapPatch("/update/{id:guid}", UpdateCaseLegacy)
            .RequirePermission(LiensPermissions.CaseUpdate);

        // Legacy compatibility route from previous service: POST /case/liens/facility
        // under the new base path becomes POST /api/liens/cases/liens/facility.
        group.MapPost("/liens/facility", LiensMedicalInformationLegacy)
            .RequirePermission(LiensPermissions.LienUpdate);

        // Legacy compatibility route from previous service: POST /case/liens/update-facility
        // under the new base path becomes POST /api/liens/cases/liens/update-facility.
        group.MapPost("/liens/update-facility", UpdateMedicalInformationLegacy)
            .RequirePermission(LiensPermissions.LienUpdate);

        // Legacy compatibility route from previous service: GET /case/liens/get-facility/{id}
        // under the new base path becomes GET /api/liens/cases/liens/get-facility/{id}.
        group.MapGet("/liens/get-facility/{id}", GetMedicalInformationLegacy)
            .RequirePermission(LiensPermissions.LienRead);

        // Legacy compatibility route from previous service: POST /case/liens/medical
        // under the new base path becomes POST /api/liens/cases/liens/medical.
        group.MapPost("/liens/medical", LiensMedicaLegacy)
            .RequirePermission(LiensPermissions.LienUpdate);

        // Legacy compatibility route from previous service: POST /case/liens/update-medical
        // under the new base path becomes POST /api/liens/cases/liens/update-medical.
        group.MapPost("/liens/update-medical", LiensMedicaUpdateLegacy)
            .RequirePermission(LiensPermissions.LienUpdate);

        // Legacy compatibility route from previous service: GET /case/liens/get-medical/{id}
        // under the new base path becomes GET /api/liens/cases/liens/get-medical/{id}.
        group.MapGet("/liens/get-medical/{id}", GetLiensMedicaLegacy)
            .RequirePermission(LiensPermissions.LienRead);

        // Legacy compatibility route from previous service: POST /case/liens/medicalcode
        // under the new base path becomes POST /api/liens/cases/liens/medicalcode.
        group.MapPost("/liens/medicalcode", LiensMedicalCodeLegacy)
            .RequirePermission(LiensPermissions.LienUpdate);

        // Legacy compatibility route from previous service: POST /case/liens/update-medicalcode
        // under the new base path becomes POST /api/liens/cases/liens/update-medicalcode.
        group.MapPost("/liens/update-medicalcode", LiensUpdateMedica1lCodeLegacy)
            .RequirePermission(LiensPermissions.LienUpdate);

        // Legacy compatibility route from previous service: POST /case/liens/upload/document
        // under the new base path becomes POST /api/liens/cases/liens/upload/document.
        group.MapPost("/liens/upload/document", UploadLienDocumentLegacy)
            .RequirePermission(LiensPermissions.LienUpdate)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(LiensUploadLimits.MultipartRequestBytes))
            .DisableAntiforgery();

        // Legacy compatibility route from previous service: GET /case/liens/get-medicaldocument/{liensId}
        // under the new base path becomes GET /api/liens/cases/liens/get-medicaldocument/{liensId}.
        group.MapGet("/liens/get-medicaldocument/{liensId}", GetMedicalDocumentsByLienIdLegacy)
            .RequirePermission(LiensPermissions.LienRead);

        // Legacy compatibility route from previous service: POST /case/upload/document
        // under the new base path becomes POST /api/liens/cases/upload/document.
        group.MapPost("/upload/document", UploadCaseDocumentLegacy)
            .RequirePermission(LiensPermissions.CaseUpdate)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(LiensUploadLimits.MultipartRequestBytes))
            .DisableAntiforgery();

        // Legacy compatibility route from previous service: GET /case/get-allcasedocument/{caseId}
        // under the new base path becomes GET /api/liens/cases/get-allcasedocument/{caseId}.
        group.MapGet("/get-allcasedocument/{caseId}", GetAllCaseDocumentsLegacy)
            .RequirePermission(LiensPermissions.CaseRead);

        // Legacy compatibility route from previous service: GET /case/get-casedocument/{caseId}
        group.MapGet("/get-casedocument/{caseId}", GetCaseDocumentsLegacy)
            .RequirePermission(LiensPermissions.CaseRead);

        // Legacy compatibility route from previous service: GET /case/liens/get-medicalcode/{caseId}
        // under the new base path becomes GET /api/liens/cases/liens/get-medicalcode/{caseId}.
        group.MapGet("/liens/get-medicalcode/{caseId}", GetMedicalCodeByCaseIdLegacy)
            .RequirePermission(LiensPermissions.LienRead);

        // Legacy compatibility route from previous service: DELETE /case/liens/delete-medicalcode/{lienId}
        // under the new base path becomes DELETE /api/liens/cases/liens/delete-medicalcode/{lienId}.
        group.MapDelete("/liens/delete-medicalcode/{lienId}", DeleteMedicalCodeByLienIdLegacy)
            .RequirePermission(LiensPermissions.LienUpdate);

        // Legacy compatibility route from previous service: POST /case/liens/payment
        // under the new base path becomes POST /api/liens/cases/liens/payment.
        group.MapPost("/liens/payment", UpdateMedicalPayeeOutboundLegacy)
            .RequirePermission(LiensPermissions.LienUpdate);

        // Legacy compatibility route from previous service: GET /case/liens/get-payee-outbound/{liensId}
        // under the new base path becomes GET /api/liens/cases/liens/get-payee-outbound/{liensId}.
        group.MapGet("/liens/get-payee-outbound/{liensId}", GetPayeeOutboundLegacy)
            .RequirePermission(LiensPermissions.LienRead);

        // Legacy compatibility route from previous service: POST /case/casemanager
        // under the new base path becomes POST /api/liens/cases/casemanager.
        group.MapPost("/casemanager", CreateCaseManagerLegacy)
            .RequirePermission(LiensPermissions.CaseCreate);

        // Legacy compatibility route from previous service: POST /case/update-casemanager
        // under the new base path becomes POST /api/liens/cases/update-casemanager.
        group.MapPost("/update-casemanager", UpdateCaseManagerLegacy)
            .RequirePermission(LiensPermissions.CaseUpdate);

        // Legacy compatibility route from previous service: DELETE /case/delete-casemanager/{id}
        // under the new base path becomes DELETE /api/liens/cases/delete-casemanager/{id}.
        group.MapDelete("/delete-casemanager/{id}", DeleteCaseManagerLegacy)
            .RequirePermission(LiensPermissions.CaseUpdate);

        // Legacy compatibility route from previous service: POST /case/reassign/lawfirm
        // under the new base path becomes POST /api/liens/cases/reassign/lawfirm.
        group.MapPost("/reassign/lawfirm", ReassignLawfirmLegacy)
            .RequirePermission(LiensPermissions.CaseUpdate);

        // Legacy compatibility route from previous service: POST /case/reassign/casemanager
        // under the new base path becomes POST /api/liens/cases/reassign/casemanager.
        group.MapPost("/reassign/casemanager", ReassignCaseManagerLegacy)
            .RequirePermission(LiensPermissions.CaseUpdate);

        // Legacy compatibility route from previous service: POST /case/reassign/leads
        // under the new base path becomes POST /api/liens/cases/reassign/leads.
        group.MapPost("/reassign/leads", ReassignLeadLegacy)
            .RequirePermission(LiensPermissions.CaseUpdate);

        // Legacy compatibility route from previous service: POST /case/batch-reassign
        // under the new base path becomes POST /api/liens/cases/batch-reassign.
        group.MapPost("/batch-reassign", BatchReassignLawfirmLegacy)
            .RequirePermission(LiensPermissions.CaseUpdate);

        // Legacy compatibility route from previous service: GET /case/payoff-quote/{caseId}
        // under the new base path becomes GET /api/liens/cases/payoff-quote/{caseId}.
        group.MapGet("/payoff-quote/{caseId:guid}", GeneratePayoffQuoteLegacy)
            .RequirePermission(LiensPermissions.CaseRead);
        // Compatibility for clients that shipped the legacy typo.
        group.MapGet("/payoff-qoute/{caseId:guid}", GeneratePayoffQuoteLegacy)
            .RequirePermission(LiensPermissions.CaseRead);

        // Legacy compatibility route from previous service: GET /case/dashboard/piechart
        // under the new base path becomes GET /api/liens/cases/dashboard/piechart.
        group.MapGet("/dashboard/piechart", GetDashboardLegacy)
            .RequirePermission(LiensPermissions.CaseRead);
        group.MapPost("/dashboard/deployed", GetDashboardDeployedLegacy)
            .RequirePermission(LiensPermissions.CaseRead);
        group.MapPost("/dashboard/cash-received", GetDashboardCashReceivedLegacy)
            .RequirePermission(LiensPermissions.CaseRead);

        // Legacy compatibility route from previous service: POST /case/generate-csv
        // under the new base path becomes POST /api/liens/cases/generate-csv.
        group.MapPost("/generate-csv", GenerateCaseCsvLegacy)
            .RequirePermission(LiensPermissions.CaseRead);

        // Legacy compatibility route from previous service: POST /case/liens/generate-csv
        // under the new base path becomes POST /api/liens/cases/liens/generate-csv.
        group.MapPost("/liens/generate-csv", GenerateLiensCsvLegacy)
            .RequirePermission(LiensPermissions.LienRead);

        // ── Partial update variants ───────────────────────────────────────────
        group.MapPatch("/personal-update", UpdatePersonalInfo)
            .RequirePermission(LiensPermissions.CaseUpdate);
        group.MapPatch("/primary-update", UpdatePrimaryInfo)
            .RequirePermission(LiensPermissions.CaseUpdate);
        group.MapPatch("/details-update", UpdateCaseDetails)
            .RequirePermission(LiensPermissions.CaseUpdate);

        // ── Linked-entity filter routes ───────────────────────────────────────
        group.MapGet("/medical/{medicalId}", GetCasesByLinkedEntity)
            .RequirePermission(LiensPermissions.CaseRead);
        group.MapGet("/funding/{fundingCompanyId}", GetCasesByLinkedEntity)
            .RequirePermission(LiensPermissions.CaseRead);
        group.MapGet("/medical/facility/{facilityId}", GetCasesByLinkedEntity)
            .RequirePermission(LiensPermissions.CaseRead);
        group.MapGet("/case-manager/{caseManagerId}", GetCasesByLinkedEntity)
            .RequirePermission(LiensPermissions.CaseRead);
        group.MapGet("/medical/facility-contact/{facilityContactId}", GetCasesByLinkedEntity)
            .RequirePermission(LiensPermissions.CaseRead);
        group.MapGet("/lead/{leadId}", GetCasesByLinkedEntity)
            .RequirePermission(LiensPermissions.CaseRead);
        group.MapGet("/leads/{leadId}", GetCasesByLinkedEntity)
            .RequirePermission(LiensPermissions.CaseRead);

        // ── Audit log ─────────────────────────────────────────────────────────
        group.MapGet("/case-updates/{caseId:guid}", GetCaseAuditLog)
            .RequirePermission(LiensPermissions.CaseRead);
        group.MapGet("/liens-updates/{caseId:guid}", GetLiensAuditLog)
            .RequirePermission(LiensPermissions.LienRead);

        // ── Liens management from case context ────────────────────────────────
        group.MapPost("/liens", ListLiensByCaseContext)
            .RequirePermission(LiensPermissions.LienRead);
        group.MapPost("/liens/{caseId:guid}", ListLiensByCaseId)
            .RequirePermission(LiensPermissions.LienRead);
        group.MapPost("/liens/v3", SearchLiensV3)
            .RequirePermission(LiensPermissions.LienRead);
        group.MapPost("/liens/details/{caseId:guid}", GetLiensDetailsByCaseId)
            .RequirePermission(LiensPermissions.LienRead);
        group.MapDelete("/liens/delete/{liensId:guid}", DeleteLien)
            .RequirePermission(LiensPermissions.LienUpdate);

        // ── Manual medical codes ──────────────────────────────────────────────
        group.MapPost("/manual/medical/code/create", CreateManualMedicalCode)
            .RequirePermission(LiensPermissions.LienUpdate);
        group.MapPost("/manual/medical/code/update", UpdateManualMedicalCode)
            .RequirePermission(LiensPermissions.LienUpdate);

        // ── Dashboard extended ────────────────────────────────────────────────
        group.MapGet("/dashboard/task-summary", GetDashboardTaskSummary)
            .RequirePermission(LiensPermissions.CaseRead);
        group.MapGet("/dashboard/total-lien-report-export", GetTotalLienReport)
            .RequirePermission(LiensPermissions.LienRead);
        group.MapPost("/dashboard/total-lien-report-export/v3", GetTotalLienReportV3)
            .RequirePermission(LiensPermissions.LienRead);
        group.MapGet("/dashboard/total-case-report-export", GetTotalCaseReport)
            .RequirePermission(LiensPermissions.CaseRead);
        group.MapPost("/dashboard/total-case-report-export/v3", GetTotalCaseReportV3)
            .RequirePermission(LiensPermissions.CaseRead);
        group.MapGet("/dashboard/lawfirm-case-report-export", GetLawFirmCaseReport)
            .RequirePermission(LiensPermissions.CaseRead);
        group.MapPost("/dashboard/lawfirm-case-report-export/v3", GetLawFirmCaseReportV3)
            .RequirePermission(LiensPermissions.CaseRead);
        group.MapGet("/dashboard/medical-provider-report-export", GetMedicalProviderReport)
            .RequirePermission(LiensPermissions.LienRead);
        group.MapPost("/dashboard/medical-provider-report-export/v3", GetMedicalProviderReportV3)
            .RequirePermission(LiensPermissions.LienRead);

        // Report CSV exports
        group.MapGet("/lien-report-csv", GetLienReportCsv)
            .RequirePermission(LiensPermissions.LienRead);
        group.MapGet("/case-report-csv", GetCaseReportCsv)
            .RequirePermission(LiensPermissions.CaseRead);
        group.MapGet("/law-firm-case-report-csv", GetLawFirmCaseReportCsv)
            .RequirePermission(LiensPermissions.CaseRead);
        group.MapGet("/medical-provider-case-report-csv", GetMedicalProviderCaseReportCsv)
            .RequirePermission(LiensPermissions.LienRead);

        // ── CSV imports ───────────────────────────────────────────────────────
        group.MapPost("/import-csv", ImportCsv)
            .RequirePermission(LiensPermissions.CaseCreate);
        group.MapPost("/migrate-csv", ImportCsv)
            .RequirePermission(LiensPermissions.CaseCreate);
        group.MapPost("/migrate-guardian-csv", ImportCsv)
            .RequirePermission(LiensPermissions.CaseCreate);
        group.MapPost("/update-lien-payment-csv", ImportCsv)
            .RequirePermission(LiensPermissions.LienUpdate);

        // ── Document type management ──────────────────────────────────────────
        group.MapPost("/document/type", AddDocumentType)
            .RequirePermission(LiensPermissions.CaseUpdate);

        // ── Global search ─────────────────────────────────────────────────────
        group.MapPost("/global-search", GlobalSearch)
            .RequirePermission(LiensPermissions.CaseRead);

        group.MapDelete("/delete/{id:guid}", DeleteCaseLegacy)
            .RequirePermission(LiensPermissions.CaseUpdate);
        group.MapPost("/other", UpsertCaseOtherLegacy)
            .RequirePermission(LiensPermissions.CaseUpdate);
        group.MapPost("/update-other", UpsertCaseOtherLegacy)
            .RequirePermission(LiensPermissions.CaseUpdate);
        group.MapGet("/get-other/{caseId}", GetCaseOtherLegacy)
            .RequirePermission(LiensPermissions.CaseRead);
        group.MapPost("/mergecase", MergeCaseLegacy)
            .RequirePermission(LiensPermissions.CaseUpdate);

    }

    private static async Task<IResult> LiensMedicalInformationLegacy(
        LegacyLiensMedicalInformationFacilityRequest request,
        ILienService lienService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId = RequireUserId(ctx);

        if (!Guid.TryParse(request.liensId, out var lienId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "Invalid or missing liensId.",
            });
        }

        if (!Guid.TryParse(request.facilityId, out var facilityId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "Invalid or missing facilityId.",
            });
        }

        var existing = await lienService.GetByIdAsync(tenantId, lienId, ct);
        if (existing is null)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = $"Lien '{request.liensId}' not found.",
            });
        }

        var mappedRequest = new UpdateLienRequest
        {
            ExternalReference = existing.ExternalReference,
            LienType = existing.LienType,
            CaseId = existing.CaseId,
            FacilityId = facilityId,
            OriginalAmount = existing.OriginalAmount,
            Jurisdiction = existing.Jurisdiction,
            IsConfidential = existing.IsConfidential,
            SubjectFirstName = existing.SubjectFirstName,
            SubjectLastName = existing.SubjectLastName,
            IncidentDate = existing.IncidentDate,
            Description = existing.Description,
        };

        try
        {
            await lienService.UpdateAsync(tenantId, lienId, userId, mappedRequest, ct);
            return Results.Ok(new
            {
                isSuccess = true,
                message = "Successfully Updated.",
                data = request.liensId ?? string.Empty,
            });
        }
        catch (Exception ex)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> UpdateMedicalInformationLegacy(
        LegacyLiensMedicalInformationFacilityRequest request,
        ILienService lienService,
        IServicingItemService servicingItemService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var orgId = RequireOrgId(ctx);
        var userId = RequireUserId(ctx);

        if (!Guid.TryParse(request.liensId, out var lienId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "Invalid or missing liensId.",
            });
        }

        if (!Guid.TryParse(request.facilityId, out var facilityId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "Invalid or missing facilityId.",
            });
        }

        var existing = await lienService.GetByIdAsync(tenantId, lienId, ct);
        if (existing is null)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "No record found to update.",
            });
        }

        var mappedRequest = new UpdateLienRequest
        {
            ExternalReference = existing.ExternalReference,
            LienType = existing.LienType,
            CaseId = existing.CaseId,
            FacilityId = facilityId,
            OriginalAmount = existing.OriginalAmount,
            Jurisdiction = existing.Jurisdiction,
            IsConfidential = existing.IsConfidential,
            SubjectFirstName = existing.SubjectFirstName,
            SubjectLastName = existing.SubjectLastName,
            IncidentDate = existing.IncidentDate,
            Description = existing.Description,
        };

        try
        {
            var updated = await lienService.UpdateLegacyMedicalFacilityAsync(
                tenantId,
                lienId,
                userId,
                mappedRequest,
                ct);

            var infoResult = await servicingItemService.SearchAsync(
                tenantId,
                search: "LegacyMedicalFacilityInfo",
                status: null,
                priority: null,
                assignedTo: null,
                caseId: null,
                lienId: lienId,
                page: 1,
                pageSize: 50,
                ct);

            var info = infoResult.Items.FirstOrDefault(i =>
                string.Equals(i.TaskType, "LegacyMedicalFacilityInfo", StringComparison.Ordinal) &&
                i.LienId == lienId);

            var fields = ParseLegacyNoteFields(info?.Notes);
            fields["facilityId"] = request.facilityId?.Trim() ?? string.Empty;
            fields["facilityName"] = request.facility?.Trim() ?? string.Empty;
            fields["facilityContactId"] = request.facilityContactId?.Trim() ?? string.Empty;
            fields["facilityContactPerson"] = request.facilityContact?.Trim() ?? string.Empty;
            fields["email"] = request.email?.Trim() ?? string.Empty;
            fields["phone"] = request.phone?.Trim() ?? string.Empty;
            fields["medicalProviderId"] = request.medicalProviderId?.Trim() ?? string.Empty;
            fields["medicalProvider"] = request.medicalProvider?.Trim() ?? string.Empty;

            var notes = SerializeLegacyNoteFields(fields);
            if (info is null)
            {
                var create = new CreateServicingItemRequest
                {
                    TaskNumber = $"LMFI-{DateTime.UtcNow:yyyyMMddHHmmssfff}",
                    TaskType = "LegacyMedicalFacilityInfo",
                    Description = "Legacy medical facility information",
                    AssignedTo = "system",
                    CaseId = updated.CaseId,
                    LienId = lienId,
                    Notes = notes,
                };

                await servicingItemService.CreateAsync(tenantId, orgId, userId, create, ct);
            }
            else if (!string.Equals(info.Notes?.Trim(), notes.Trim(), StringComparison.Ordinal))
            {
                var update = new UpdateServicingItemRequest
                {
                    TaskType = info.TaskType,
                    Description = info.Description,
                    AssignedTo = string.IsNullOrWhiteSpace(info.AssignedTo) ? "system" : info.AssignedTo,
                    AssignedToUserId = info.AssignedToUserId,
                    Priority = info.Priority,
                    Status = info.Status,
                    CaseId = info.CaseId ?? updated.CaseId,
                    LienId = info.LienId,
                    DueDate = info.DueDate,
                    Notes = notes,
                    Resolution = info.Resolution,
                };

                await servicingItemService.UpdateAsync(tenantId, info.Id, userId, update, ct);
            }

            return Results.Ok(new
            {
                isSuccess = true,
                message = "Successfully Updated.",
            });
        }
        catch (Exception ex)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> GetMedicalInformationLegacy(
        string id,
        ILienService lienService,
        IServicingItemService servicingItemService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);

        if (!Guid.TryParse(id, out var lienId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "No record found.",
            });
        }

        var lien = await lienService.GetByIdAsync(tenantId, lienId, ct);
        if (lien is null)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "No record found.",
            });
        }

        var infoResult = await servicingItemService.SearchAsync(
            tenantId,
            search: "LegacyMedicalFacilityInfo",
            status: null,
            priority: null,
            assignedTo: null,
            caseId: null,
            lienId: lienId,
            page: 1,
            pageSize: 50,
            ct);

        var info = infoResult.Items.FirstOrDefault(i =>
            string.Equals(i.TaskType, "LegacyMedicalFacilityInfo", StringComparison.Ordinal) &&
            i.LienId == lienId);

        if (info is null && !lien.FacilityId.HasValue)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "No record found.",
            });
        }

        var infoFields = ParseLegacyNoteFields(info?.Notes);

        var data = new LegacyLiensMedicalInformationFacilityResponse
        {
            id = string.Empty,
            liensId = lien.Id.ToString(),
            facilityId = infoFields.GetValueOrDefault("facilityId", lien.FacilityId?.ToString() ?? string.Empty),
            facility = infoFields.GetValueOrDefault("facilityName", string.Empty),
            facilityContactId = infoFields.GetValueOrDefault("facilityContactId", string.Empty),
            facilityContact = infoFields.GetValueOrDefault("facilityContactPerson", string.Empty),
            email = infoFields.GetValueOrDefault("email", string.Empty),
            phone = infoFields.GetValueOrDefault("phone", string.Empty),
            medicalProviderId = infoFields.GetValueOrDefault("medicalProviderId", string.Empty),
            medicalProvider = infoFields.GetValueOrDefault("medicalProvider", string.Empty),
            created = FormatLegacyTimestamp(lien.CreatedAtUtc),
            createdBy = lien.ImportedCreatedByName ?? string.Empty,
            updated = FormatLegacyTimestamp(lien.UpdatedAtUtc),
            updatedBy = string.Empty,
        };

        return Results.Ok(new
        {
            isSuccess = true,
            message = "Successfully retrieved medical information.",
            data,
        });
    }

    private static async Task<IResult> LiensMedicaLegacy(
        LegacyLiensMedicalRequest request,
        ILienService lienService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var orgId = RequireOrgId(ctx);
        var userId = RequireUserId(ctx);

        var hasValidLegacyId = Guid.TryParse(request.id, out var lienId);

        if (hasValidLegacyId)
        {
            var existing = await lienService.GetByIdAsync(tenantId, lienId, ct);
            if (existing is not null)
            {
                var mappedUpdate = new UpdateLienRequest
                {
                    ExternalReference = existing.ExternalReference,
                    LienType = existing.LienType,
                    CaseId = Guid.TryParse(request.caseId, out var parsedCaseId) ? parsedCaseId : existing.CaseId,
                    FacilityId = existing.FacilityId,
                    OriginalAmount = existing.OriginalAmount,
                    Jurisdiction = existing.Jurisdiction,
                    IsConfidential = existing.IsConfidential,
                    SubjectFirstName = existing.SubjectFirstName,
                    SubjectLastName = existing.SubjectLastName,
                    IncidentDate = existing.IncidentDate,
                    PurchaseDate = ParseLegacyDate(request.purchaseDate) ?? ParseLegacyDate(existing.PurchaseDate),
                    InitialServiceDate = ParseLegacyDate(request.initialServiceDate) ?? existing.InitialServiceDate,
                    EndServiceDate = ParseLegacyDate(request.endServiceDate) ?? existing.EndServiceDate,
                    IsBulk = request.isBulk ?? existing.IsBulk,
                    IsServicing = request.isServicing ?? existing.IsServicing,
                    Description = request.note ?? existing.Description,
                };

                try
                {
                    await lienService.UpdateAsync(tenantId, lienId, userId, mappedUpdate, ct);
                    if (!string.IsNullOrWhiteSpace(request.status))
                        await lienService.SetLegacyMedicalStatusAsync(tenantId, lienId, userId, request.status, ct);

                    return Results.Ok(new
                    {
                        isSuccess = true,
                        message = "Successfully updated medical record.",
                        data = string.Empty,
                    });
                }
                catch (Exception ex)
                {
                    return Results.NotFound(new
                    {
                        isSuccess = false,
                        message = ex.Message,
                    });
                }
            }

            return Results.NotFound(new
            {
                isSuccess = false,
                message = "No record found to update.",
            });
        }

        var mappedCreate = new CreateLienRequest
        {
            LienNumber = string.Empty,
            ExternalReference = request.fundingCompanyId,
            LienType = LienType.MedicalLien,
            CaseId = Guid.TryParse(request.caseId, out var createCaseId) ? createCaseId : null,
            FacilityId = null,
            OriginalAmount = 0,
            Jurisdiction = null,
            IsConfidential = false,
            SubjectFirstName = null,
            SubjectLastName = null,
            PurchaseDate = ParseLegacyDate(request.purchaseDate),
            InitialServiceDate = ParseLegacyDate(request.initialServiceDate),
            EndServiceDate = ParseLegacyDate(request.endServiceDate),
            IsBulk = request.isBulk,
            IsServicing = request.isServicing,
            Description = request.note,
        };

        try
        {
            var created = await lienService.CreateAsync(tenantId, orgId, userId, mappedCreate, ct);
            if (!string.IsNullOrWhiteSpace(request.status) &&
                !string.Equals(request.status.Trim(), created.Status, StringComparison.Ordinal))
            {
                await lienService.SetLegacyMedicalStatusAsync(tenantId, created.Id, userId, request.status, ct);
            }

            return Results.Ok(new
            {
                isSuccess = true,
                message = "Successfully created medical record.",
                data = created.Id.ToString(),
            });
        }
        catch (Exception ex)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> LiensMedicaUpdateLegacy(
        LegacyLiensMedicalRequest request,
        ILienService lienService,
        IUnitOfWork unitOfWork,
        IAuditPublisher auditPublisher,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId = RequireUserId(ctx);

        if (!Guid.TryParse(request.id, out var lienId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "No record found to update.",
            });
        }

        var existing = await lienService.GetByIdAsync(tenantId, lienId, ct);
        if (existing is null)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "No record found to update.",
            });
        }

        var mappedUpdate = new UpdateLienRequest
        {
            ExternalReference = request.fundingCompanyId ?? existing.ExternalReference,
            LienType = existing.LienType,
            CaseId = Guid.TryParse(request.caseId, out var parsedCaseId) ? parsedCaseId : existing.CaseId,
            FacilityId = existing.FacilityId,
            OriginalAmount = existing.OriginalAmount,
            Jurisdiction = existing.Jurisdiction,
            IsConfidential = existing.IsConfidential,
            SubjectFirstName = existing.SubjectFirstName,
            SubjectLastName = existing.SubjectLastName,
            IncidentDate = existing.IncidentDate,
            PurchaseDate = ParseLegacyDate(request.purchaseDate) ?? ParseLegacyDate(existing.PurchaseDate),
            InitialServiceDate = ParseLegacyDate(request.initialServiceDate) ?? existing.InitialServiceDate,
            EndServiceDate = ParseLegacyDate(request.endServiceDate) ?? existing.EndServiceDate,
            IsBulk = request.isBulk ?? existing.IsBulk,
            IsServicing = request.isServicing ?? existing.IsServicing,
            Description = request.note ?? existing.Notes ?? existing.Description,
        };

        using var auditBuffer = auditPublisher.BeginBuffer();
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        try
        {
            await lienService.UpdateLegacyMedicalAsync(
                tenantId,
                lienId,
                userId,
                mappedUpdate,
                request.status,
                ct);

            await transaction.CommitAsync(ct);
            auditBuffer.Commit();

            return Results.Ok(new
            {
                isSuccess = true,
                message = "Successfully updated medical record.",
            });
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return Results.NotFound(new
            {
                isSuccess = false,
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> GetLiensMedicaLegacy(
        string id,
        ILienService lienService,
        IContactService contactService,
        IServicingItemService servicingItemService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);

        if (!Guid.TryParse(id, out var lienId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "No record found.",
            });
        }

        var lien = await lienService.GetByIdAsync(tenantId, lienId, ct);
        if (lien is null)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "No record found.",
            });
        }

        var fundingCompanyName = string.Empty;
        if (Guid.TryParse(lien.ExternalReference, out var fundingCompanyContactId))
        {
            var fundingCompanyContact = await contactService.GetByIdAsync(tenantId, fundingCompanyContactId, ct);
            fundingCompanyName = fundingCompanyContact?.Organization ??
                                 fundingCompanyContact?.DisplayName ??
                                 string.Empty;
        }

        var partyInfoResult = await servicingItemService.SearchAsync(
            tenantId,
            search: "LegacyMedicalFacilityInfo",
            status: null,
            priority: null,
            assignedTo: null,
            caseId: null,
            lienId: lienId,
            page: 1,
            pageSize: 50,
            ct);
        var partyInfo = partyInfoResult.Items.FirstOrDefault(item =>
            string.Equals(item.TaskType, "LegacyMedicalFacilityInfo", StringComparison.Ordinal) &&
            item.LienId == lienId);
        var partyFields = ParseLegacyNoteFields(partyInfo?.Notes);
        var fundingCompanyId = partyFields.GetValueOrDefault("fundingCompanyId", lien.ExternalReference ?? string.Empty);
        fundingCompanyName = partyFields.GetValueOrDefault("fundingCompany", fundingCompanyName);

        var data = new LegacyLiensMedicalResponse
        {
            id = lien.Id.ToString(),
            caseId = lien.CaseId?.ToString() ?? string.Empty,
            status = string.Equals(lien.Status, LienStatus.Active, StringComparison.OrdinalIgnoreCase)
                ? "Open"
                : lien.Status,
            purchaseDate = lien.PurchaseDate ?? string.Empty,
            initialServiceDate = FormatLegacyDate(lien.InitialServiceDate),
            endServiceDate = FormatLegacyDate(lien.EndServiceDate),
            note = lien.Notes ?? lien.Description ?? string.Empty,
            created = FormatLegacyTimestamp(lien.CreatedAtUtc),
            createdBy = string.Empty,
            updated = FormatLegacyTimestamp(lien.UpdatedAtUtc),
            updatedBy = string.Empty,
            fundingCompanyId = fundingCompanyId,
            fundingCompany = fundingCompanyName,
            isBulk = lien.IsBulk ?? string.Empty,
            isServicing = lien.IsServicing ?? string.Empty,
        };

        return Results.Ok(new
        {
            isSuccess = true,
            message = "Successfully retrieved medical record.",
            data,
        });
    }

    private static async Task<IResult> LiensMedicalCodeLegacy(
        LegacyLiensMedicalCodeRequest request,
        ILienService lienService,
        IServicingItemService servicingItemService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var orgId = RequireOrgId(ctx);
        var userId = RequireUserId(ctx);

        if (!Guid.TryParse(request.liensId, out var lienId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "Invalid or missing liensId.",
            });
        }

        var lien = await lienService.GetByIdAsync(tenantId, lienId, ct);
        if (lien is null)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = $"Lien '{request.liensId}' not found.",
            });
        }

        var details =
            $"code={request.code ?? string.Empty}; " +
            $"description={request.description ?? string.Empty}; " +
            $"medicareCost={request.medicareCost ?? string.Empty}; " +
            $"billingAmount={request.billingAmount ?? string.Empty}; " +
            $"purchaseAmount={request.purchaseAmount ?? string.Empty}; " +
            $"payee={request.payee ?? string.Empty}; " +
            $"outboundCheckNumber={request.outboundCheckNumber ?? string.Empty}";

        var mapped = new CreateServicingItemRequest
        {
            TaskNumber = $"LMC-{DateTime.UtcNow:yyyyMMddHHmmssfff}",
            TaskType = "LegacyMedicalCode",
            Description = string.IsNullOrWhiteSpace(request.code)
                ? "Legacy medical code entry"
                : $"Medical code {request.code}",
            AssignedTo = "system",
            CaseId = lien.CaseId,
            LienId = lien.Id,
            Notes = details,
        };

        try
        {
            var created = await servicingItemService.CreateAsync(tenantId, orgId, userId, mapped, ct);
            return Results.Ok(new
            {
                isSuccess = true,
                message = "Successfully created medical code record.",
                data = created.Id.ToString(),
            });
        }
        catch (Exception ex)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> LiensUpdateMedica1lCodeLegacy(
        LegacyLiensMedicalCodeRequest request,
        IServicingItemService servicingItemService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId = RequireUserId(ctx);

        Guid? requestedMedicalCodeId = Guid.TryParse(request.id, out var medicalCodeId)
            ? medicalCodeId
            : null;
        Guid? requestedLienId = Guid.TryParse(request.liensId, out var parsedLienId)
            ? parsedLienId
            : null;

        var existing = await ResolveLegacyMedicalCodeForUpdateAsync(
            tenantId,
            requestedMedicalCodeId,
            requestedLienId,
            request.code,
            servicingItemService,
            ct);
        if (existing is null)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "No record found to update.",
            });
        }

        if (requestedLienId.HasValue && existing.LienId != requestedLienId)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "No record found to update.",
            });
        }

        var details =
            $"code={request.code ?? string.Empty}; " +
            $"description={request.description ?? string.Empty}; " +
            $"medicareCost={request.medicareCost ?? string.Empty}; " +
            $"billingAmount={request.billingAmount ?? string.Empty}; " +
            $"purchaseAmount={request.purchaseAmount ?? string.Empty}; " +
            $"payee={request.payee ?? string.Empty}; " +
            $"outboundCheckNumber={request.outboundCheckNumber ?? string.Empty}";

        var description = string.IsNullOrWhiteSpace(request.code)
            ? existing.Description
            : $"Medical code {request.code}";
        if (LegacyTextValuesEqual(existing.Description, description) &&
            LegacyTextValuesEqual(existing.Notes, details))
        {
            return Results.Ok(new
            {
                isSuccess = true,
                message = "Successfully updated medical code record.",
            });
        }

        var mapped = new UpdateServicingItemRequest
        {
            TaskType = existing.TaskType,
            Description = description,
            AssignedTo = string.IsNullOrWhiteSpace(existing.AssignedTo) ? "system" : existing.AssignedTo,
            AssignedToUserId = existing.AssignedToUserId,
            Priority = existing.Priority,
            Status = existing.Status,
            CaseId = existing.CaseId,
            LienId = existing.LienId,
            DueDate = existing.DueDate,
            Notes = details,
            Resolution = existing.Resolution,
        };

        try
        {
            await servicingItemService.UpdateAsync(tenantId, existing.Id, userId, mapped, ct);
            return Results.Ok(new
            {
                isSuccess = true,
                message = "Successfully updated medical code record.",
            });
        }
        catch (Exception ex)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = ex.Message,
            });
        }
    }

    private static async Task<ServicingItemResponse?> ResolveLegacyMedicalCodeForUpdateAsync(
        Guid tenantId,
        Guid? requestedMedicalCodeId,
        Guid? requestedLienId,
        string? code,
        IServicingItemService servicingItemService,
        CancellationToken ct)
    {
        if (requestedMedicalCodeId.HasValue)
        {
            var byId = await servicingItemService.GetByIdAsync(tenantId, requestedMedicalCodeId.Value, ct);
            if (byId is not null &&
                string.Equals(byId.TaskType, "LegacyMedicalCode", StringComparison.Ordinal) &&
                (!requestedLienId.HasValue || byId.LienId == requestedLienId.Value))
            {
                return byId;
            }
        }

        if (!requestedLienId.HasValue)
            return null;

        var result = await SearchLegacyMedicalCodesAsync(
            servicingItemService,
            tenantId,
            requestedLienId.Value,
            ct);
        var candidates = result.Items
            .Where(i => string.Equals(i.TaskType, "LegacyMedicalCode", StringComparison.Ordinal) &&
                        i.LienId == requestedLienId.Value)
            .ToList();

        if (!string.IsNullOrWhiteSpace(code))
        {
            var codeMatch = candidates.FirstOrDefault(i =>
                string.Equals(
                    ParseLegacyNoteFields(i.Notes).GetValueOrDefault("code", string.Empty),
                    code.Trim(),
                    StringComparison.OrdinalIgnoreCase));
            if (codeMatch is not null)
                return codeMatch;
        }

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static Task<IResult> UploadCaseDocumentLegacy(
        HttpContext httpContext,
        ILegacyDocumentUploadClient uploadClient,
        IServicingItemService servicingItemService,
        ICaseService caseService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        return UploadDocumentLegacy(
            requestType: "case",
            httpContext,
            uploadClient,
            servicingItemService,
            caseService,
            lienService: null,
            ctx,
            ct);
    }

    private static Task<IResult> UploadLienDocumentLegacy(
        HttpContext httpContext,
        ILegacyDocumentUploadClient uploadClient,
        IServicingItemService servicingItemService,
        ILienService lienService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        return UploadDocumentLegacy(
            requestType: "liens",
            httpContext,
            uploadClient,
            servicingItemService,
            caseService: null,
            lienService,
            ctx,
            ct);
    }

    private static async Task<IResult> UploadDocumentLegacy(
        string requestType,
        HttpContext httpContext,
        ILegacyDocumentUploadClient uploadClient,
        IServicingItemService servicingItemService,
        ICaseService? caseService,
        ILienService? lienService,
        ICurrentRequestContext ctx,
        CancellationToken ct)
    {
        if (!httpContext.Request.HasFormContentType)
        {
            if (httpContext.Request.ContentLength.GetValueOrDefault() == 0)
                return Results.BadRequest(new { isSuccess = false, message = "Missing payload" });

            return Results.BadRequest(new
            {
                isSuccess = false,
                message = "Content-Type must be multipart/form-data.",
            });
        }

        var form = await httpContext.Request.ReadFormAsync(ct);
        var file = form.Files["file"];

        var validation = ValidateLegacyUploadFile(file);
        if (validation is not null)
            return validation;

        var tenantId = RequireTenantId(ctx);
        var userId = RequireUserId(ctx);
        var orgId = RequireOrgId(ctx);

        var referenceType = string.Equals(requestType, "case", StringComparison.OrdinalIgnoreCase)
            ? "Case"
            : "Lien";
        var referenceIdValue = string.Equals(requestType, "case", StringComparison.OrdinalIgnoreCase)
            ? form["caseId"].ToString()
            : FirstFormValue(form, "liensId", "lienId");

        if (!Guid.TryParse(referenceIdValue, out var referenceId))
        {
            var fieldName = string.Equals(requestType, "case", StringComparison.OrdinalIgnoreCase)
                ? "caseId"
                : "liensId";
            return Results.BadRequest(new
            {
                isSuccess = false,
                message = $"{fieldName} is required.",
            });
        }

        if (caseService is not null)
        {
            var existingCase = await caseService.GetByIdAsync(tenantId, referenceId, ct);
            if (existingCase is null)
                return Results.NotFound(new { isSuccess = false, message = "Case not found." });
        }

        if (lienService is not null)
        {
            var existingLien = await lienService.GetByIdAsync(tenantId, referenceId, ct);
            if (existingLien is null)
                return Results.NotFound(new { isSuccess = false, message = "Lien not found." });
        }

        var docTypeValue = FirstFormValue(form, "DocFileTypeId", "documentTypeId", "docFileTypeId");
        var documentTypeId = Guid.TryParse(docTypeValue, out var parsedDocumentTypeId)
            ? parsedDocumentTypeId
            : LegacyFallbackDocumentTypeId;
        var title = FirstFormValue(form, "DocName", "title");
        if (string.IsNullOrWhiteSpace(title))
            title = Path.GetFileNameWithoutExtension(file!.FileName);

        var description = FirstFormValue(form, "DocDescription", "description");
        if (string.IsNullOrWhiteSpace(description))
        {
            var extension = Path.GetExtension(file!.FileName).TrimStart('.').ToUpperInvariant();
            description = string.IsNullOrWhiteSpace(extension) ? null : $"{extension} File";
        }

        try
        {
            await using var stream = file!.OpenReadStream();
            var uploadResult = await uploadClient.UploadAsync(new LegacyDocumentUploadRequest
            {
                TenantId = tenantId,
                ActingUserId = userId,
                ReferenceId = referenceId,
                ReferenceType = referenceType,
                DocumentTypeId = documentTypeId,
                Title = title,
                Description = description,
                Content = stream,
                FileName = file.FileName,
                ContentType = string.IsNullOrWhiteSpace(file.ContentType)
                    ? "application/octet-stream"
                    : file.ContentType,
                Length = file.Length,
            }, ct);

            var taskType = string.Equals(requestType, "case", StringComparison.OrdinalIgnoreCase)
                ? "LegacyCaseDocument"
                : "LegacyLienDocument";
            var notes = BuildLegacyDocumentNotes(
                uploadResult,
                file.FileName,
                title,
                docTypeValue,
                documentTypeId,
                referenceType,
                referenceId,
                description);

            await servicingItemService.CreateAsync(
                tenantId,
                orgId,
                userId,
                new CreateServicingItemRequest
                {
                    TaskNumber = $"DOC-{Guid.CreateVersion7():N}"[..36],
                    TaskType = taskType,
                    Description = $"{referenceType} document uploaded: {title}",
                    AssignedTo = ctx.Email ?? ctx.Name ?? userId.ToString(),
                    AssignedToUserId = userId,
                    CaseId = string.Equals(requestType, "case", StringComparison.OrdinalIgnoreCase)
                        ? referenceId
                        : null,
                    LienId = string.Equals(requestType, "liens", StringComparison.OrdinalIgnoreCase)
                        ? referenceId
                        : null,
                    Notes = notes,
                },
                ct);

            return Results.Ok(new
            {
                isSuccess = true,
                message = "Successfully uploaded document.",
                data = new
                {
                    url = uploadResult.Url,
                    documentId = uploadResult.DocumentId,
                },
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Results.Json(
                new
                {
                    isSuccess = false,
                    message = "An unexpected error occurred while processing the upload",
                },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static IResult? ValidateLegacyUploadFile(IFormFile? file)
    {
        if (file is null)
            return Results.BadRequest(new { isSuccess = false, message = "No file uploaded" });

        if (file.Length == 0)
            return Results.BadRequest(new { isSuccess = false, message = "Empty file upload not allowed" });

        if (file.Length > LiensUploadLimits.MaxBytes)
        {
            return Results.BadRequest(new
            {
                isSuccess = false,
                message = $"File size exceeds the allowed limit ({LiensUploadLimits.MaxMegabytes} MB)",
            });
        }

        var fileExt = Path.GetExtension(file.FileName)?.ToLowerInvariant();
        if (string.IsNullOrEmpty(fileExt) ||
            !LegacyAllowedDocumentExtensions.Contains(fileExt, StringComparer.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new
            {
                isSuccess = false,
                message = $"File type not allowed. Allowed types: {string.Join(", ", LegacyAllowedDocumentExtensions)}",
            });
        }

        return null;
    }

    private static string BuildLegacyDocumentNotes(
        LegacyDocumentUploadResult result,
        string fileName,
        string title,
        string legacyDocumentTypeId,
        Guid documentTypeId,
        string referenceType,
        Guid referenceId,
        string? description)
    {
        return string.Join("; ", new Dictionary<string, string?>
            {
                ["documentId"] = result.DocumentId?.ToString(),
                ["documentUrl"] = result.Url,
                ["url"] = result.Url,
                ["filename"] = title,
                ["originalFileName"] = fileName,
                ["typeId"] = legacyDocumentTypeId,
                ["documentTypeId"] = documentTypeId.ToString(),
                ["referenceType"] = referenceType,
                ["referenceId"] = referenceId.ToString(),
                ["description"] = description,
            }
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
            .Select(kvp => $"{kvp.Key}={SanitizeLegacyNoteValue(kvp.Value!)}"));
    }

    private static string FirstFormValue(IFormCollection form, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = form[key].ToString();
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return string.Empty;
    }

    private static string SanitizeLegacyNoteValue(string value)
    {
        return value
            .Replace(";", ",", StringComparison.Ordinal)
            .Replace("=", ":", StringComparison.Ordinal)
            .Trim();
    }

    private static async Task<IResult> GetMedicalDocumentsByLienIdLegacy(
        string liensId,
        IServicingItemService servicingItemService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);

        if (!Guid.TryParse(liensId, out var lienId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "No record found.",
            });
        }

        var items = await SearchLegacyServicingItemsAsync(servicingItemService, tenantId, caseId: null, lienId, ct);

        var data = items
            .Where(IsLegacyLienDocumentTaskType)
            .Select(MapLegacyMedicalDocumentResponse)
            .ToList();

        if (data.Count == 0)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "No record found.",
            });
        }

        return Results.Ok(new
        {
            isSuccess = true,
            message = "Successfully retrieved Medical Documents.",
            data,
        });
    }

    private static async Task<IResult> GetAllCaseDocumentsLegacy(
        string caseId,
        IServicingItemService servicingItemService,
        ILienService lienService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);

        if (!Guid.TryParse(caseId, out var parsedCaseId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "No record found.",
            });
        }

        var caseDocuments = await SearchLegacyServicingItemsAsync(
            servicingItemService,
            tenantId,
            parsedCaseId,
            lienId: null,
            ct);

        var allDocuments = caseDocuments
            .Where(i => string.Equals(i.TaskType, "LegacyCaseDocument", StringComparison.Ordinal))
            .Select(item => MapLegacyAllCaseDocumentResponse(item, includeLienId: false))
            .ToList();

        var liens = await SearchLiensByCaseAsync(lienService, tenantId, parsedCaseId, ct);
        foreach (var lien in liens)
        {
            var lienDocuments = await SearchLegacyServicingItemsAsync(
                servicingItemService,
                tenantId,
                caseId: null,
                lien.Id,
                ct);

            allDocuments.AddRange(lienDocuments
                .Where(IsLegacyLienDocumentTaskType)
                .Select(item => MapLegacyAllCaseDocumentResponse(item, includeLienId: true)));
        }

        if (allDocuments.Count == 0)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "No record found.",
            });
        }

        return Results.Ok(new
        {
            isSuccess = true,
            message = "Successfully retrieved Documents.",
            data = allDocuments,
        });
    }

    private static async Task<IResult> GetCaseDocumentsLegacy(
        string caseId,
        IServicingItemService servicingItemService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);

        if (!Guid.TryParse(caseId, out var parsedCaseId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "No record found.",
            });
        }

        var items = await SearchLegacyServicingItemsAsync(
            servicingItemService,
            tenantId,
            parsedCaseId,
            lienId: null,
            ct);

        var data = items
            .Where(i => string.Equals(i.TaskType, "LegacyCaseDocument", StringComparison.Ordinal))
            .Select(MapLegacyMedicalDocumentResponse)
            .ToList();

        if (data.Count == 0)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "No record found.",
            });
        }

        return Results.Ok(new
        {
            isSuccess = true,
            message = "Successfully retrieved Case Documents.",
            data,
        });
    }

    private static async Task<List<ServicingItemResponse>> SearchLegacyServicingItemsAsync(
        IServicingItemService servicingItemService,
        Guid tenantId,
        Guid? caseId,
        Guid? lienId,
        CancellationToken ct)
    {
        const int pageSize = 200;
        var page = 1;
        var items = new List<ServicingItemResponse>();

        while (true)
        {
            var result = await servicingItemService.SearchAsync(
                tenantId,
                search: null,
                status: null,
                priority: null,
                assignedTo: null,
                caseId,
                lienId,
                page,
                pageSize,
                ct);

            if (result.Items.Count == 0)
                break;

            items.AddRange(result.Items);
            if (items.Count >= result.TotalCount)
                break;

            page++;
        }

        return items;
    }

    private static async Task<List<LienResponse>> SearchLiensByCaseAsync(
        ILienService lienService,
        Guid tenantId,
        Guid caseId,
        CancellationToken ct)
    {
        const int pageSize = 200;
        var page = 1;
        var items = new List<LienResponse>();

        while (true)
        {
            var result = await lienService.SearchAsync(
                tenantId,
                search: null,
                status: null,
                lienType: null,
                caseId,
                facilityId: null,
                page,
                pageSize,
                ct);

            if (result.Items.Count == 0)
                break;

            items.AddRange(result.Items);
            if (items.Count >= result.TotalCount)
                break;

            page++;
        }

        return items;
    }

    private static async Task<List<CaseResponse>> SearchAllCasesAsync(
        ICaseService caseService,
        Guid tenantId,
        CancellationToken ct)
    {
        const int pageSize = 200;
        var page = 1;
        var items = new List<CaseResponse>();

        while (true)
        {
            var result = await caseService.SearchAsync(
                tenantId,
                search: null,
                status: null,
                page: page,
                pageSize: pageSize,
                orgId: null,
                ct: ct);

            if (result.Items.Count == 0)
                break;

            items.AddRange(result.Items);
            if (items.Count >= result.TotalCount)
                break;

            page++;
        }

        return items;
    }

    private static bool MatchesLegacyCaseKeyword(CaseResponse item, string keyword)
    {
        return item.CaseNumber.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               item.ClientFirstName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               item.ClientLastName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrWhiteSpace(item.ClientDisplayName) &&
                item.ClientDisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static IResult BuildLegacyLinkedCasesResult(
        IEnumerable<CaseResponse> cases,
        string? keyword,
        int requestedPage,
        int requestedLimit,
        string notFoundMessage)
    {
        var filtered = cases.ToList();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var term = keyword.Trim();
            filtered = filtered
                .Where(item => MatchesLegacyCaseKeyword(item, term))
                .ToList();
        }

        if (filtered.Count == 0)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = notFoundMessage,
            });
        }

        var page = requestedPage < 1 ? 1 : requestedPage;
        var limit = requestedLimit < 1 ? 10 : requestedLimit;

        var paged = filtered
            .OrderByDescending(c => c.CreatedAtUtc)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToList();

        var totalCount = filtered.Count;
        var totalCases = totalCount;
        var totalActiveCases = filtered.Count(c => !string.Equals(c.Status, CaseStatus.Closed, StringComparison.Ordinal));
        var totalValue = filtered.Sum(c => (double)(c.SettlementAmount ?? c.DemandAmount ?? 0m));

        return Results.Ok(new
        {
            isSuccess = true,
            message = "Case list retrieved successfully.",
            data = paged,
            totalCount,
            totalCases,
            totalActiveCases,
            totalValue,
        });
    }

    private static async Task<List<CaseResponse>> GetCasesFromDashboardCaseFilterAsync(
        string filterType,
        string filterId,
        ICaseService caseService,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct,
        bool requireLawFirm = false)
    {
        var tenantId = RequireTenantId(ctx);
        var allCases = await SearchAllCasesAsync(caseService, tenantId, ct);
        var casesById = allCases.ToDictionary(c => c.Id, EqualityComparer<Guid>.Default);

        var rows = await BuildDashboardCaseReportResultAsync(
            new ReportFilterRequest
            {
                Page = 1,
                Limit = int.MaxValue,
                FilterType = filterType,
                FilterId = filterId,
            },
            db,
            ctx,
            ct,
            requireLawFirm,
            includeAllItems: true);

        return rows.Items
            .Select(row => casesById.GetValueOrDefault(row.Id))
            .Where(item => item is not null)
            .Select(item => item!)
            .DistinctBy(item => item.Id)
            .ToList();
    }

    private static async Task<List<CaseResponse>> GetCasesFromDashboardLienFilterAsync(
        string filterType,
        string filterId,
        ICaseService caseService,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct,
        bool requireMedicalProvider = false)
    {
        var tenantId = RequireTenantId(ctx);
        var allCases = await SearchAllCasesAsync(caseService, tenantId, ct);
        var casesById = allCases.ToDictionary(c => c.Id, EqualityComparer<Guid>.Default);

        var rows = await BuildDashboardLienReportResultAsync(
            new ReportFilterRequest
            {
                Page = 1,
                Limit = int.MaxValue,
                FilterType = filterType,
                FilterId = filterId,
            },
            db,
            ctx,
            ct,
            requireMedicalProvider,
            includeAllItems: true);

        return rows.Items
            .Where(row => row.CaseRecordId.HasValue)
            .Select(row => casesById.GetValueOrDefault(row.CaseRecordId!.Value))
            .Where(item => item is not null)
            .Select(item => item!)
            .DistinctBy(item => item.Id)
            .ToList();
    }

    private static bool IsLegacyLienDocumentTaskType(ServicingItemResponse item)
        => string.Equals(item.TaskType, "LegacyLienDocument", StringComparison.Ordinal) ||
           string.Equals(item.TaskType, "LegacyMedicalDocument", StringComparison.Ordinal);

    private static LegacyLiensMedicalDocumentResponse MapLegacyMedicalDocumentResponse(ServicingItemResponse item)
    {
        var fields = ParseLegacyNoteFields(item.Notes);
        var documentTypeId = GetLegacyDocumentCanonicalTypeId(fields);
        return new LegacyLiensMedicalDocumentResponse
        {
            id = item.Id.ToString(),
            liensId = item.LienId?.ToString(),
            filename = GetLegacyDocumentFileName(fields),
            typeId = GetLegacyDocumentResponseTypeId(fields, documentTypeId),
            documentTypeId = documentTypeId,
            url = GetLegacyDocumentUrl(fields),
            created = FormatLegacyTimestamp(item.CreatedAtUtc),
            createdBy = string.Empty,
            updated = FormatLegacyTimestamp(item.UpdatedAtUtc),
            updatedBy = string.Empty,
        };
    }

    private static LegacyAllCaseDocumentResponse MapLegacyAllCaseDocumentResponse(
        ServicingItemResponse item,
        bool includeLienId)
    {
        var fields = ParseLegacyNoteFields(item.Notes);
        var filename = GetLegacyDocumentFileName(fields);
        var url = GetLegacyDocumentUrl(fields);
        var documentTypeId = GetLegacyDocumentCanonicalTypeId(fields);

        return new LegacyAllCaseDocumentResponse
        {
            id = item.Id.ToString(),
            liensId = includeLienId ? item.LienId?.ToString() : null,
            filename = filename,
            typeId = GetLegacyDocumentResponseTypeId(fields, documentTypeId),
            documentTypeId = documentTypeId,
            url = url,
            created = FormatLegacyTimestamp(item.CreatedAtUtc),
            createdBy = string.Empty,
            updated = FormatLegacyTimestamp(item.UpdatedAtUtc),
            updatedBy = string.Empty,
            mimeType = GetLegacyDocumentMimeType(url, filename),
        };
    }

    private static string GetLegacyDocumentResponseTypeId(
        IReadOnlyDictionary<string, string> fields,
        string documentTypeId)
    {
        var legacyTypeId = fields.GetValueOrDefault("typeId", string.Empty).Trim();
        return string.IsNullOrWhiteSpace(legacyTypeId) ? documentTypeId : legacyTypeId;
    }

    private static string GetLegacyDocumentCanonicalTypeId(IReadOnlyDictionary<string, string> fields)
    {
        var storedDocumentTypeId = fields.GetValueOrDefault("documentTypeId", string.Empty).Trim();
        if (Guid.TryParse(storedDocumentTypeId, out var parsedDocumentTypeId) &&
            parsedDocumentTypeId != LegacyFallbackDocumentTypeId)
        {
            return parsedDocumentTypeId.ToString();
        }

        var legacyTypeId = fields.GetValueOrDefault("typeId", string.Empty).Trim();
        if (Guid.TryParse(legacyTypeId, out var parsedLegacyTypeId))
            return parsedLegacyTypeId.ToString();

        return legacyTypeId switch
        {
            "1" or "12" => "10000000-0000-0000-0000-000000000001",
            "2" => "10000000-0000-0000-0000-000000000002",
            "3" => "10000000-0000-0000-0000-000000000003",
            "4" => "10000000-0000-0000-0000-000000000004",
            "5" => LegacyOtherDocumentTypeId.ToString(),
            "6" => "10000000-0000-0000-0000-000000000006",
            "7" => "10000000-0000-0000-0000-000000000007",
            "8" => "10000000-0000-0000-0000-000000000008",
            "9" => "10000000-0000-0000-0000-000000000009",
            "10" => "10000000-0000-0000-0000-000000000009",
            "11" => "10000000-0000-0000-0000-000000000010",
            "14" => LegacyOtherDocumentTypeId.ToString(),
            _ => LegacyOtherDocumentTypeId.ToString(),
        };
    }

    private static string GetLegacyDocumentFileName(Dictionary<string, string> fields)
        => fields.GetValueOrDefault("filename",
            fields.GetValueOrDefault("originalFileName", string.Empty));

    private static string GetLegacyDocumentUrl(Dictionary<string, string> fields)
    {
        var url = fields.GetValueOrDefault("url", string.Empty);
        return string.IsNullOrWhiteSpace(url)
            ? fields.GetValueOrDefault("documentUrl", string.Empty)
            : url;
    }

    private static string GetLegacyDocumentMimeType(string url, string fileName)
    {
        var extension = Path.GetExtension(url);
        if (!string.IsNullOrWhiteSpace(extension))
            return extension;

        return Path.GetExtension(fileName);
    }

    private static async Task<IResult> DeleteCaseLegacy(
        Guid id,
        LiensDbContext db,
        ILienService lienService,
        IAuditPublisher auditPublisher,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId = RequireUserId(ctx);

        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        using var auditBuffer = auditPublisher.BeginBuffer();

        try
        {
            var item = await db.Cases
                .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == id, ct);
            if (item is null)
            {
                return Results.NotFound(new
                {
                    isSuccess = false,
                    message = "Error: Unable to delete Case.",
                });
            }

            var linkedLiens = await db.Liens
                .Where(l => l.TenantId == tenantId && (l.CaseId == id || l.SellingCaseId == id))
                .ToListAsync(ct);

            foreach (var lien in linkedLiens)
            {
                await lienService.DeleteAsync(tenantId, lien.Id, userId, ct);
                if (lien.CaseId == id)
                    lien.DetachCase(userId);
                if (lien.SellingCaseId == id)
                    lien.DetachSellingCase(userId);
            }

            db.LienCaseNotes.RemoveRange(db.LienCaseNotes.Where(n => n.TenantId == tenantId && n.CaseId == id));
            db.ServicingItems.RemoveRange(db.ServicingItems.Where(s => s.TenantId == tenantId && s.CaseId == id));
            db.LienReductions.RemoveRange(db.LienReductions.Where(r => r.TenantId == tenantId && r.CaseId == id));
            db.LienSettlements.RemoveRange(db.LienSettlements.Where(s => s.TenantId == tenantId && s.CaseId == id));
            db.SettlementPaymentDetails.RemoveRange(db.SettlementPaymentDetails.Where(p => p.TenantId == tenantId && p.CaseId == id));
            item.MarkForDeletion(userId);
            db.Cases.Remove(item);
            await db.SaveChangesAsync(ct);

            if (transaction is not null)
                await transaction.CommitAsync(ct);
            auditBuffer.Commit();

            return Results.Ok(new
            {
                isSuccess = true,
                message = "Successfully deleted Case.",
            });
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<IResult> UpsertCaseOtherLegacy(
        LegacyCaseOtherRequest request,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId = RequireUserId(ctx);

        if (!Guid.TryParse(request.caseId, out var caseId))
        {
            return Results.BadRequest(new
            {
                isSuccess = false,
                message = "Invalid caseId.",
            });
        }

        var item = await db.Cases.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == caseId, ct);
        if (item is null)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "Case not found.",
            });
        }

        var metadata = ParseLegacyNoteFields(item.Notes);
        var noteBody = ExtractLegacyNoteText(item.Notes);
        SetLegacyOtherField(metadata, "reductionsRate", request.reductionsRate);
        SetLegacyOtherField(metadata, "payment", request.payment);
        SetLegacyOtherField(metadata, "adjustments", request.adjustments);
        SetLegacyOtherField(metadata, "reductionsDate", request.reductionsDate);
        SetLegacyOtherField(metadata, "netProfit", request.netProfit);
        SetLegacyOtherField(metadata, "checkNumber", request.checkNumber);
        SetLegacyOtherField(metadata, "netOutboundCheckNumber", request.netOutboundCheckNumber);
        SetLegacyOtherField(metadata, "bulkPurchase", request.bulkPurchase);
        SetLegacyOtherField(metadata, "bank", request.bank);

        item.Update(
            item.ClientFirstName,
            item.ClientLastName,
            userId,
            title: item.Title,
            externalReference: item.ExternalReference,
            clientDob: item.ClientDob,
            clientPhone: item.ClientPhone,
            clientEmail: item.ClientEmail,
            clientAddress: item.ClientAddress,
            dateOfIncident: item.DateOfIncident,
            insuranceCarrier: item.InsuranceCarrier,
            policyNumber: item.PolicyNumber,
            claimNumber: item.ClaimNumber,
            description: item.Description,
            notes: SerializeLegacyNoteFields(noteBody, metadata));

        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            isSuccess = true,
            message = "Successfully Updated Case Other.",
        });
    }

    private static async Task<IResult> GetCaseOtherLegacy(
        string caseId,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);

        if (!Guid.TryParse(caseId, out var parsedCaseId))
        {
            return Results.BadRequest(new
            {
                isSuccess = false,
                message = "Error: retrieving data.",
            });
        }

        var item = await db.Cases.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == parsedCaseId, ct);
        if (item is null)
        {
            return Results.BadRequest(new
            {
                isSuccess = false,
                message = "Error: retrieving data.",
            });
        }

        var metadata = ParseLegacyNoteFields(item.Notes);
        return Results.Ok(new
        {
            isSuccess = true,
            message = "Liens List.",
            data = new
            {
                caseId = parsedCaseId.ToString(),
                reductionsRate = metadata.GetValueOrDefault("other.reductionsRate", string.Empty),
                payment = metadata.GetValueOrDefault("other.payment", string.Empty),
                adjustments = metadata.GetValueOrDefault("other.adjustments", string.Empty),
                reductionsDate = metadata.GetValueOrDefault("other.reductionsDate", string.Empty),
                netProfit = metadata.GetValueOrDefault("other.netProfit", string.Empty),
                checkNumber = metadata.GetValueOrDefault("other.checkNumber", string.Empty),
                netOutboundCheckNumber = metadata.GetValueOrDefault("other.netOutboundCheckNumber", string.Empty),
                bulkPurchase = metadata.GetValueOrDefault("other.bulkPurchase", string.Empty),
                bank = metadata.GetValueOrDefault("other.bank", string.Empty),
            },
        });
    }

    private static async Task<IResult> MergeCaseLegacy(
        LegacyCaseMergeRequest request,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId = RequireUserId(ctx);

        if (!Guid.TryParse(request.caseIdA, out var caseIdA) ||
            !Guid.TryParse(request.caseIdB, out var caseIdB))
        {
            return Results.BadRequest(new
            {
                isSuccess = false,
                message = "Invalid case ids.",
            });
        }

        var primaryCase = await db.Cases.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == caseIdA, ct);
        var secondaryCase = await db.Cases.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == caseIdB, ct);
        if (primaryCase is null || secondaryCase is null)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "Error: unable to merge cases.",
            });
        }

        var liens = await db.Liens.Where(l => l.TenantId == tenantId && l.CaseId == caseIdB).ToListAsync(ct);
        foreach (var lien in liens)
            lien.AttachCase(caseIdA, userId);

        var notes = await db.LienCaseNotes.Where(n => n.TenantId == tenantId && n.CaseId == caseIdB).ToListAsync(ct);
        foreach (var note in notes)
            typeof(Liens.Domain.Entities.LienCaseNote).GetProperty("CaseId")?.SetValue(note, caseIdA);

        secondaryCase.MarkForDeletion(userId);
        db.Cases.Remove(secondaryCase);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            isSuccess = true,
            message = "Successfully merged cases.",
        });
    }

    private static void SetLegacyOtherField(Dictionary<string, string> metadata, string fieldName, string? value)
    {
        var key = $"other.{fieldName}";
        if (string.IsNullOrWhiteSpace(value))
            metadata.Remove(key);
        else
            metadata[key] = value.Trim();
    }

    private static async Task<IResult> GetMedicalCodeByCaseIdLegacy(
        string caseId,
        IServicingItemService servicingItemService,
        ILookupValueService lookupService,
        LiensDbContext db,
        IHttpClientFactory httpClientFactory,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);

        if (!Guid.TryParse(caseId, out var parsedId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "No records found.",
            });
        }

        var results = await SearchLegacyMedicalCodesAsync(
            servicingItemService,
            tenantId,
            parsedId,
            ct);
        var descriptionsByCode = await BuildLegacyProcedureCodeDescriptionsAsync(
            tenantId,
            lookupService,
            db,
            httpClientFactory,
            ct);

        var legacyItems = results.Items
            .Where(i => string.Equals(i.TaskType, "LegacyMedicalCode", StringComparison.Ordinal))
            .ToList();
        var lienIds = legacyItems
            .Where(item => item.LienId.HasValue)
            .Select(item => item.LienId!.Value)
            .Distinct()
            .ToList();
        var sellingPricingRows = await db.ServicingItems.AsNoTracking()
            .Where(item =>
                item.TenantId == tenantId &&
                item.LienId.HasValue &&
                lienIds.Contains(item.LienId.Value) &&
                item.TaskType == "SellingMedicalPricing")
            .OrderBy(item => item.CreatedAtUtc)
            .Select(item => new { item.LienId, item.Description, item.Notes })
            .ToListAsync(ct);
        var parsedSellingPricingRows = sellingPricingRows
            .Select(item => new
            {
                LienId = item.LienId!.Value,
                Fallback = ParseSellingMedicalPricingFallback(item.Description, item.Notes),
            })
            .Where(item => item.Fallback is not null)
            .ToList();
        var singleSellingFallbacks = parsedSellingPricingRows
            .GroupBy(item => item.LienId)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Fallback!);
        var sellingFallbacksByCode = parsedSellingPricingRows
            .Where(item => !string.IsNullOrWhiteSpace(item.Fallback!.MedicalCode))
            .GroupBy(item => item.LienId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .GroupBy(item => item.Fallback!.MedicalCode!, StringComparer.OrdinalIgnoreCase)
                    .Where(codeGroup => codeGroup.Count() == 1)
                    .ToDictionary(
                        codeGroup => codeGroup.Key,
                        codeGroup => codeGroup.Single().Fallback!,
                        StringComparer.OrdinalIgnoreCase));
        var lienAmounts = await db.Liens.AsNoTracking()
            .Where(lien => lien.TenantId == tenantId && lienIds.Contains(lien.Id))
            .Select(lien => new { lien.Id, lien.OriginalAmount, lien.AskAmount })
            .ToDictionaryAsync(lien => lien.Id, ct);
        var legacyRowsPerLien = legacyItems
            .Where(item => item.LienId.HasValue)
            .GroupBy(item => item.LienId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());

        var data = legacyItems
            .Select(i =>
            {
                var fields = ParseLegacyNoteFields(i.Notes);
                var lienId = i.LienId;
                var storedCode = FirstNonEmpty(
                    fields.GetValueOrDefault("code"),
                    ExtractMedicalCodeFromDescription(i.Description));
                var sellingFallback = lienId.HasValue &&
                    !string.IsNullOrWhiteSpace(storedCode) &&
                    sellingFallbacksByCode.TryGetValue(lienId.Value, out var fallbacksByCode) &&
                    fallbacksByCode.TryGetValue(storedCode, out var codeFallback)
                    ? codeFallback
                    : lienId.HasValue
                        ? singleSellingFallbacks.GetValueOrDefault(lienId.Value)
                        : null;
                var lienAmount = lienId.HasValue &&
                    legacyRowsPerLien.GetValueOrDefault(lienId.Value) == 1
                        ? lienAmounts.GetValueOrDefault(lienId.Value)
                        : null;
                var code = FirstNonEmpty(
                    storedCode,
                    sellingFallback?.MedicalCode,
                    ExtractMedicalCodeFromDescription(i.Description)) ?? string.Empty;
                var description = FirstNonEmpty(
                    fields.GetValueOrDefault("description"),
                    sellingFallback?.Description) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(description) &&
                    !string.IsNullOrWhiteSpace(code) &&
                    descriptionsByCode.TryGetValue(code, out var resolvedDescription))
                {
                    description = resolvedDescription;
                }

                return new LegacyLiensMedicalCodeResponse
                {
                    id = i.Id.ToString(),
                    liensId = i.LienId?.ToString() ?? string.Empty,
                    code = code,
                    description = description,
                    medicareCost = ResolveLegacyMedicalAmount(
                        fields.GetValueOrDefault("medicareCost"),
                        sellingFallback?.MedicareCost),
                    billingAmount = ResolveLegacyMedicalAmount(
                        fields.GetValueOrDefault("billingAmount"),
                        sellingFallback?.BillingAmount,
                        lienAmount?.OriginalAmount),
                    purchaseAmount = ResolveLegacyMedicalAmount(
                        fields.GetValueOrDefault("purchaseAmount"),
                        sellingFallback?.TargetSaleAmount,
                        lienAmount?.AskAmount),
                    payee = fields.GetValueOrDefault("payee", string.Empty),
                    outboundCheckNumber = fields.GetValueOrDefault("outboundCheckNumber", string.Empty),
                    created = FormatLegacyTimestamp(i.CreatedAtUtc),
                    createdBy = string.Empty,
                    updated = FormatLegacyTimestamp(i.UpdatedAtUtc),
                    updatedBy = string.Empty,
                };
            })
            .ToList();

        return Results.Ok(new
        {
            isSuccess = true,
            message = "Successfully retrieved medical code records.",
            data,
        });
    }

    private static SellingMedicalPricingFallback? ParseSellingMedicalPricingFallback(
        string? rowDescription,
        string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
            return null;

        try
        {
            var parsed = JsonSerializer.Deserialize<SellingMedicalPricingFallback>(notes, MedicareJsonOptions);
            if (parsed is null)
                return null;
            return parsed with { MedicalCode = FirstNonEmpty(parsed.MedicalCode, rowDescription) };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractMedicalCodeFromDescription(string? description)
    {
        const string prefix = "Medical code ";
        if (string.IsNullOrWhiteSpace(description) ||
            !description.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return FirstNonEmpty(description[prefix.Length..]);
    }

    private static string ResolveLegacyMedicalAmount(
        string? storedValue,
        decimal? sellingValue,
        decimal? lienValue = null)
    {
        if (decimal.TryParse(storedValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var storedAmount) &&
            storedAmount != 0m)
        {
            return storedValue!.Trim();
        }

        var fallback = sellingValue is not null and not 0m
            ? sellingValue
            : lienValue;
        return fallback?.ToString(CultureInfo.InvariantCulture) ?? storedValue?.Trim() ?? string.Empty;
    }

    private static async Task<Dictionary<string, string>> BuildLegacyProcedureCodeDescriptionsAsync(
        Guid tenantId,
        ILookupValueService lookupService,
        LiensDbContext db,
        IHttpClientFactory httpClientFactory,
        CancellationToken ct)
    {
        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var lookupCodes = await lookupService.GetByCategoryAsync(tenantId, LookupCategory.ProcedureCode, ct);
        foreach (var lookupCode in lookupCodes)
        {
            if (!string.IsNullOrWhiteSpace(lookupCode.Code))
            {
                descriptions[lookupCode.Code] = lookupCode.Description ?? lookupCode.Name;
            }
        }

        var manualCodes = await db.ManualMedicalCodes
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.Status == "A")
            .Select(m => new { m.Code, m.Description })
            .ToListAsync(ct);

        foreach (var manualCode in manualCodes)
        {
            if (!string.IsNullOrWhiteSpace(manualCode.Code) &&
                !string.IsNullOrWhiteSpace(manualCode.Description))
            {
                descriptions[manualCode.Code] = $"{manualCode.Description} ({manualCode.Code})";
            }
        }

        var medicareCodes = await GetMedicareProcedureCodesAsync(httpClientFactory, ct);
        foreach (var medicareCode in medicareCodes)
        {
            if (!string.IsNullOrWhiteSpace(medicareCode.Code) &&
                !descriptions.ContainsKey(medicareCode.Code))
            {
                descriptions[medicareCode.Code] = string.IsNullOrWhiteSpace(medicareCode.Description)
                    ? medicareCode.Code
                    : medicareCode.Description;
            }
        }

        return descriptions;
    }

    private static async Task<IReadOnlyList<MedicareProcedureCode>> GetMedicareProcedureCodesAsync(
        IHttpClientFactory httpClientFactory,
        CancellationToken ct)
    {
        try
        {
            using var response = await SendMedicareRequestAsync(httpClientFactory, "codes", ct);
            if (!response.IsSuccessStatusCode)
                return [];

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<List<MedicareProcedureCode>>(stream, MedicareJsonOptions, ct)
                ?? [];
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return [];
        }
        catch (HttpRequestException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static async Task<HttpResponseMessage> SendMedicareRequestAsync(
        IHttpClientFactory httpClientFactory,
        string relativePath,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(MedicareProcedureLookupClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("apiKey", MedicareProcedureLookupApiKey);
        request.Headers.TryAddWithoutValidation("amaLicense", MedicareProcedureLookupAmaLicense);

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static async Task<PaginatedResult<ServicingItemResponse>> SearchLegacyMedicalCodesAsync(
        IServicingItemService servicingItemService,
        Guid tenantId,
        Guid caseOrLienId,
        CancellationToken ct)
    {
        var results = await servicingItemService.SearchAsync(
            tenantId,
            search: "LegacyMedicalCode",
            status: null,
            priority: null,
            assignedTo: null,
            caseId: null,
            lienId: caseOrLienId,
            page: 1,
            pageSize: 500,
            ct);

        if (results.Items.Any(i => string.Equals(i.TaskType, "LegacyMedicalCode", StringComparison.Ordinal)))
            return results;

        return await servicingItemService.SearchAsync(
            tenantId,
            search: "LegacyMedicalCode",
            status: null,
            priority: null,
            assignedTo: null,
            caseId: caseOrLienId,
            lienId: null,
            page: 1,
            pageSize: 500,
            ct);
    }

    private static async Task<IResult> DeleteMedicalCodeByLienIdLegacy(
        string lienId,
        IServicingItemService servicingItemService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId = RequireUserId(ctx);

        if (!Guid.TryParse(lienId, out var parsedLienId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "No records found.",
            });
        }

        var directMatch = await servicingItemService.GetByIdAsync(tenantId, parsedLienId, ct);
        if (directMatch is not null &&
            string.Equals(directMatch.TaskType, "LegacyMedicalCode", StringComparison.Ordinal))
        {
            await servicingItemService.DeleteAsync(tenantId, directMatch.Id, userId, ct);
            return Results.Ok(new
            {
                isSuccess = true,
                message = "Successfully deleted medical code record(s).",
            });
        }

        var results = await servicingItemService.SearchAsync(
            tenantId,
            search: "LegacyMedicalCode",
            status: null,
            priority: null,
            assignedTo: null,
            caseId: null,
            lienId: parsedLienId,
            page: 1,
            pageSize: 500,
            ct);

        var targets = results.Items
            .Where(i => string.Equals(i.TaskType, "LegacyMedicalCode", StringComparison.Ordinal))
            .ToList();

        if (targets.Count == 0)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "No records found.",
            });
        }

        try
        {
            foreach (var item in targets)
                await servicingItemService.DeleteAsync(tenantId, item.Id, userId, ct);

            return Results.Ok(new
            {
                isSuccess = true,
                message = "Successfully deleted medical code record(s).",
            });
        }
        catch (Exception ex)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = ex.Message,
            });
        }
    }

    private static Dictionary<string, string> ParseLegacyNoteFields(string? notes)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(notes))
            return result;

        var rawMetadata = notes;
        var markerIndex = notes.IndexOf(LegacyMetadataMarker, StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            rawMetadata = notes[(markerIndex + LegacyMetadataMarker.Length)..].Trim();
        }
        else if (!LooksLikeLegacyMetadata(notes))
        {
            return result;
        }

        foreach (var segment in rawMetadata.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = segment.IndexOf('=');
            if (eq > 0)
            {
                var key = segment[..eq].Trim();
                var value = segment[(eq + 1)..].Trim();
                result[key] = value;
            }
        }

        return result;
    }

    private static string? ExtractLegacyNoteText(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
            return null;

        var markerIndex = notes.IndexOf(LegacyMetadataMarker, StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            var body = notes[..markerIndex].Trim();
            return body.Length == 0 ? null : body;
        }

        return LooksLikeLegacyMetadata(notes) ? null : notes;
    }

    private static bool LooksLikeLegacyMetadata(string notes)
    {
        var segments = notes.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length > 0 && segments.All(segment => segment.Contains('='));
    }

    private static string NormalizeLegacyCaseStatus(string value)
    {
        var normalized = value.Trim();
        var compact = normalized
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("(", string.Empty, StringComparison.Ordinal)
            .Replace(")", string.Empty, StringComparison.Ordinal);

        return compact.ToUpperInvariant() switch
        {
            "NEW" or "PROCESSING" or "OPEN" or "PREDEMAND" => CaseStatus.PreDemand,
            "DEMANDSENT" => CaseStatus.DemandSent,
            "NEGOTIATIONS" or "INNEGOTIATION" or "LITIGATION" or "LITIGATIONCLOSE" or "LITIGATIONCLOSED" => CaseStatus.InNegotiation,
            "LITIGATIONPENDING" => CaseStatus.LitigationPending,
            "LITIGATIONOPEN" => CaseStatus.LitigationOpen,
            "CASESETTLED" => CaseStatus.CaseSettled,
            "CLOSED" => CaseStatus.Closed,
            _ when CaseStatus.All.Contains(normalized) => normalized,
            _ => throw new InvalidOperationException($"Invalid case status '{value}'."),
        };
    }

    private static string? ResolveLegacyCaseStatusLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        var compact = normalized
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("(", string.Empty, StringComparison.Ordinal)
            .Replace(")", string.Empty, StringComparison.Ordinal);

        return compact.ToUpperInvariant() switch
        {
            "NEW" => "New",
            "PROCESSING" => "Processing",
            "NEGOTIATIONS" or "INNEGOTIATION" => "Negotiations",
            "LITIGATION" => "Litigation",
            "LITIGATIONPENDING" => "Litigation (Pending)",
            "LITIGATIONOPEN" => "Litigation (Open)",
            "LITIGATIONCLOSE" or "LITIGATIONCLOSED" => "Litigation (Closed)",
            _ => null,
        };
    }

    private static string SerializeLegacyNoteFields(Dictionary<string, string> fields)
    {
        if (fields.Count == 0)
            return string.Empty;

        return string.Join("; ", fields.Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private static bool LegacyTextValuesEqual(string? left, string? right) =>
        string.Equals(left?.Trim() ?? string.Empty, right?.Trim() ?? string.Empty, StringComparison.Ordinal);

    private static string SerializeLegacyNoteFields(string? noteBody, Dictionary<string, string> fields)
    {
        var cleanBody = string.IsNullOrWhiteSpace(noteBody) ? null : noteBody.Trim();
        var serialized = SerializeLegacyNoteFields(fields);
        if (string.IsNullOrWhiteSpace(serialized))
            return cleanBody ?? string.Empty;

        return cleanBody is null
            ? $"{LegacyMetadataMarker}{Environment.NewLine}{serialized}"
            : $"{cleanBody}{Environment.NewLine}{Environment.NewLine}{LegacyMetadataMarker}{Environment.NewLine}{serialized}";
    }

    private static async Task<IResult> UpdateMedicalPayeeOutboundLegacy(
        LegacyPayeeOutboundRequest request,
        ILienService lienService,
        IServicingItemService servicingItemService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var orgId = RequireOrgId(ctx);
        var userId = RequireUserId(ctx);

        if (!Guid.TryParse(request.liensId, out var lienId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "Invalid or missing liensId.",
            });
        }

        var lien = await lienService.GetByIdAsync(tenantId, lienId, ct);
        if (lien is null)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = $"Lien '{request.liensId}' not found.",
            });
        }

        var details =
            $"payee={request.payee ?? string.Empty}; " +
            $"outboundCheckNumber={request.outboundCheckNumber ?? string.Empty}";

        try
        {
            var existingItems = await servicingItemService.SearchAsync(
                tenantId,
                search: "LegacyMedicalPayment",
                status: null,
                priority: null,
                assignedTo: null,
                caseId: null,
                lienId: lienId,
                page: 1,
                pageSize: 100,
                ct);

            var existing = existingItems.Items.FirstOrDefault(i =>
                string.Equals(i.TaskType, "LegacyMedicalPayment", StringComparison.Ordinal) &&
                i.LienId == lienId);

            if (existing is null)
            {
                if (string.IsNullOrWhiteSpace(request.payee) &&
                    string.IsNullOrWhiteSpace(request.outboundCheckNumber))
                {
                    return Results.Ok(new
                    {
                        isSuccess = true,
                        message = "No payee or outbound check number changes.",
                    });
                }

                var createRequest = new CreateServicingItemRequest
                {
                    TaskNumber = $"LMP-{DateTime.UtcNow:yyyyMMddHHmmssfff}",
                    TaskType = "LegacyMedicalPayment",
                    Description = "Legacy medical payee/outbound",
                    AssignedTo = "system",
                    CaseId = lien.CaseId,
                    LienId = lienId,
                    Notes = details,
                };

                await servicingItemService.CreateAsync(tenantId, orgId, userId, createRequest, ct);
                return Results.Ok(new
                {
                    isSuccess = true,
                    message = "Successfully inserted payee and outbound check number.",
                });
            }

            if (LegacyTextValuesEqual(existing.Notes, details))
            {
                return Results.Ok(new
                {
                    isSuccess = true,
                    message = "Successfully updated payee and outbound check number.",
                });
            }

            var updateRequest = new UpdateServicingItemRequest
            {
                TaskType = existing.TaskType,
                Description = existing.Description,
                AssignedTo = string.IsNullOrWhiteSpace(existing.AssignedTo) ? "system" : existing.AssignedTo,
                AssignedToUserId = existing.AssignedToUserId,
                Priority = existing.Priority,
                Status = existing.Status,
                CaseId = existing.CaseId,
                LienId = existing.LienId,
                DueDate = existing.DueDate,
                Notes = details,
                Resolution = existing.Resolution,
            };

            await servicingItemService.UpdateAsync(tenantId, existing.Id, userId, updateRequest, ct);
            return Results.Ok(new
            {
                isSuccess = true,
                message = "Successfully updated payee and outbound check number.",
            });
        }
        catch (Exception ex)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> GetPayeeOutboundLegacy(
        string liensId,
        IServicingItemService servicingItemService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);

        if (!Guid.TryParse(liensId, out var lienId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "Error: Unable to retrieve Payee and Outbound Check Number.",
            });
        }

        var results = await servicingItemService.SearchAsync(
            tenantId,
            search: "LegacyMedicalPayment",
            status: null,
            priority: null,
            assignedTo: null,
            caseId: null,
            lienId: lienId,
            page: 1,
            pageSize: 100,
            ct);

        var item = results.Items.FirstOrDefault(i =>
            string.Equals(i.TaskType, "LegacyMedicalPayment", StringComparison.Ordinal) &&
            i.LienId == lienId);

        if (item is null)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "Error: Unable to retrieve Payee and Outbound Check Number.",
            });
        }

        var fields = ParseLegacyNoteFields(item.Notes);
        var data = new LegacyPayeeOutboundResponse
        {
            id = item.Id.ToString(),
            liensId = item.LienId?.ToString() ?? string.Empty,
            payee = fields.GetValueOrDefault("payee", string.Empty),
            outboundCheckNumber = fields.GetValueOrDefault("outboundCheckNumber", string.Empty),
        };

        return Results.Ok(new
        {
            isSuccess = true,
            message = "Successfully retrieved Payee and Outbound Check Number.",
            data,
        });
    }

    private static async Task<IResult> CreateCaseManagerLegacy(
        LegacyCaseManagerRequest request,
        IContactService contactService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var orgId = RequireOrgId(ctx);
        var userId = RequireUserId(ctx);

        var isLawyer = string.Equals(request.roleId, "7", StringComparison.Ordinal);

        var mapped = new CreateContactRequest
        {
            ContactType = ContactType.CaseManager,
            FirstName = request.firstname ?? string.Empty,
            LastName = request.lastname ?? string.Empty,
            Email = request.email,
            Phone = request.phone,
            Organization = request.lawfirmId,
            Notes = string.IsNullOrWhiteSpace(request.roleId)
                ? null
                : $"roleId={request.roleId}",
        };

        try
        {
            var created = await contactService.CreateAsync(tenantId, orgId, userId, mapped, ct);
            return Results.Ok(new
            {
                isSuccess = true,
                message = isLawyer
                    ? "Successfully created Lawyer."
                    : "Successfully created Case Manager.",
                data = created.Id.ToString(),
            });
        }
        catch (Exception ex)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> UpdateCaseManagerLegacy(
        LegacyCaseManagerUpdateRequest request,
        IContactService contactService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId = RequireUserId(ctx);

        if (!Guid.TryParse(request.id, out var contactId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "Error: Updating Case Manager.",
            });
        }

        var existing = await contactService.GetByIdAsync(tenantId, contactId, ct);
        if (existing is null)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "Error: Updating Case Manager.",
            });
        }

        var isLawyer = string.Equals(request.roleId, "7", StringComparison.Ordinal);

        var mapped = new UpdateContactRequest
        {
            ContactType = ContactType.CaseManager,
            FirstName = request.firstname ?? existing.FirstName,
            LastName = request.lastname ?? existing.LastName,
            Email = request.email ?? existing.Email,
            Phone = request.phone ?? existing.Phone,
            Organization = request.lawfirmId ?? existing.Organization,
            Title = existing.Title,
            Fax = existing.Fax,
            Website = existing.Website,
            AddressLine1 = existing.AddressLine1,
            City = existing.City,
            State = existing.State,
            PostalCode = existing.PostalCode,
            Notes = string.IsNullOrWhiteSpace(request.roleId)
                ? existing.Notes
                : $"roleId={request.roleId}",
        };

        try
        {
            await contactService.UpdateAsync(tenantId, contactId, userId, mapped, ct);
            return Results.Ok(new
            {
                isSuccess = true,
                message = isLawyer
                    ? "Successfully updated Lawyer."
                    : "Successfully updated Case Manager.",
            });
        }
        catch (Exception ex)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> DeleteCaseManagerLegacy(
        string id,
        IContactService contactService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId = RequireUserId(ctx);

        if (!Guid.TryParse(id, out var contactId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "Error: Delete Case Manager.",
            });
        }

        var existing = await contactService.GetByIdAsync(tenantId, contactId, ct);
        if (existing is null)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "Error: Delete Case Manager.",
            });
        }

        try
        {
            await contactService.DeactivateAsync(tenantId, contactId, userId, ct);
            return Results.Ok(new
            {
                isSuccess = true,
                message = "Successfully Delete Case Manager.",
            });
        }
        catch (Exception ex)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> ReassignLawfirmLegacy(
        LegacyReassignLawFirmRequest request,
        ICaseService caseService,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId = RequireUserId(ctx);

        if (!Guid.TryParse(request.caseId, out var caseId) ||
            !Guid.TryParse(request.lawfirm, out var lawFirmOrgId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "unable to re-assigned case.",
            });
        }

        var previousLawFirmOrgId = await db.Cases.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.Id == caseId)
            .Select(item => (Guid?)item.OrgId)
            .SingleOrDefaultAsync(ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var isSuccess = await caseService.ReassignLawFirmAsync(
            tenantId,
            caseId,
            lawFirmOrgId,
            userId,
            ct);

        if (!isSuccess)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "unable to re-assigned case.",
            });
        }

        await LawFirmChangeHistory.RecordAsync(
            db,
            tenantId,
            caseId,
            previousLawFirmOrgId?.ToString(),
            lawFirmOrgId.ToString(),
            switchedDate: null,
            userId,
            ctx.Name ?? ctx.Email ?? userId.ToString(),
            ct);
        await transaction.CommitAsync(ct);

        return Results.Ok(new
        {
            isSuccess = true,
            message = "Successfully re-assigned case to new law firm.",
        });
    }

    private static async Task<IResult> ReassignCaseManagerLegacy(
        LegacyReassignCaseManagerRequest request,
        ICaseService caseService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId = RequireUserId(ctx);

        if (!Guid.TryParse(request.caseId, out var caseId) ||
            !Guid.TryParse(request.caseManager, out var caseManagerId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "unable to re-assigned case.",
            });
        }

        var isSuccess = await caseService.ReassignCaseManagerAsync(
            tenantId,
            caseId,
            caseManagerId,
            userId,
            ct);

        if (!isSuccess)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "unable to re-assigned case.",
            });
        }

        return Results.Ok(new
        {
            isSuccess = true,
            message = "Successfully re-assigned case to new case manager.",
        });
    }

    private static async Task<IResult> ReassignLeadLegacy(
        LegacyReassignLeadRequest request,
        ICaseService caseService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId = RequireUserId(ctx);

        if (!Guid.TryParse(request.caseId, out var caseId) ||
            string.IsNullOrWhiteSpace(request.leadId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "unable to re-assigned case.",
            });
        }

        var existing = await caseService.GetByIdAsync(tenantId, caseId, ct);
        if (existing is null)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "unable to re-assigned case.",
            });
        }

        try
        {
            var fields = ParseLegacyNoteFields(existing.Notes);
            fields["leadId"] = request.leadId.Trim();

            var update = new UpdateCaseRequest
            {
                ClientFirstName = existing.ClientFirstName,
                ClientLastName = existing.ClientLastName,
                ExternalReference = existing.ExternalReference,
                Title = existing.Title,
                ClientDob = existing.ClientDob,
                ClientPhone = existing.ClientPhone,
                ClientEmail = existing.ClientEmail,
                ClientAddress = existing.ClientAddress,
                DateOfIncident = existing.DateOfIncident,
                InsuranceCarrier = existing.InsuranceCarrier,
                PolicyNumber = existing.PolicyNumber,
                ClaimNumber = existing.ClaimNumber,
                Description = existing.Description,
                Notes = SerializeLegacyNoteFields(fields),
                Status = existing.Status,
                DemandAmount = existing.DemandAmount,
                SettlementAmount = existing.SettlementAmount,
            };

            await caseService.UpdateAsync(tenantId, caseId, userId, update, ct);

            return Results.Ok(new
            {
                isSuccess = true,
                message = "Successfully re-assigned case to new lead.",
            });
        }
        catch
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "unable to re-assigned case.",
            });
        }
    }

    private static async Task<IResult> BatchReassignLawfirmLegacy(
        LegacyBatchReassignRequest request,
        ICaseService caseService,
        ILienService lienService,
        IServicingItemService servicingItemService,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId = RequireUserId(ctx);

        if (string.IsNullOrWhiteSpace(request.contactType) ||
            string.IsNullOrWhiteSpace(request.oldId) ||
            string.IsNullOrWhiteSpace(request.newId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "unable to re-assign cases.",
            });
        }

        try
        {
            switch (NormalizeLegacyBatchReassignContactType(request.contactType))
            {
                case "1": // law firm
                {
                    if (!Guid.TryParse(request.oldId, out var oldLawFirmOrgId) ||
                        !Guid.TryParse(request.newId, out var newLawFirmOrgId))
                    {
                        return Results.NotFound(new
                        {
                            isSuccess = false,
                            message = "unable to re-assign cases.",
                        });
                    }

                    const int pageSize = 200;
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);
                    while (true)
                    {
                        var pageResult = await caseService.SearchAsync(
                            tenantId,
                            search: null,
                            status: null,
                            page: 1,
                            pageSize: pageSize,
                            orgId: oldLawFirmOrgId,
                            ct);

                        if (pageResult.Items.Count == 0)
                            break;

                        var reassignedCount = 0;
                        foreach (var item in pageResult.Items)
                        {
                            var reassigned = await caseService.ReassignLawFirmAsync(
                                tenantId,
                                item.Id,
                                newLawFirmOrgId,
                                userId,
                                ct);

                            if (reassigned)
                            {
                                reassignedCount++;
                                await LawFirmChangeHistory.RecordAsync(
                                    db,
                                    tenantId,
                                    item.Id,
                                    oldLawFirmOrgId.ToString(),
                                    newLawFirmOrgId.ToString(),
                                    switchedDate: null,
                                    userId,
                                    ctx.Name ?? ctx.Email ?? userId.ToString(),
                                    ct);
                            }
                        }

                        if (reassignedCount == 0)
                            throw new InvalidOperationException("No cases could be reassigned from the selected law firm.");
                    }

                    await transaction.CommitAsync(ct);

                    return Results.Ok(new
                    {
                        isSuccess = true,
                        message = "Successfully Reassigned Cases.",
                    });
                }
                case "2": // medical provider
                {
                    const int pageSize = 200;
                    var page = 1;
                    while (true)
                    {
                        var pageResult = await servicingItemService.SearchAsync(
                            tenantId,
                            search: "LegacyMedicalFacilityInfo",
                            status: null,
                            priority: null,
                            assignedTo: null,
                            caseId: null,
                            lienId: null,
                            page: page,
                            pageSize: pageSize,
                            ct);

                        if (pageResult.Items.Count == 0)
                            break;

                        foreach (var item in pageResult.Items.Where(i =>
                                     string.Equals(i.TaskType, "LegacyMedicalFacilityInfo", StringComparison.Ordinal)))
                        {
                            var fields = ParseLegacyNoteFields(item.Notes);
                            var currentMedicalProvider = fields.GetValueOrDefault("medicalProviderId", string.Empty);
                            if (!string.Equals(currentMedicalProvider, request.oldId, StringComparison.Ordinal))
                                continue;

                            fields["medicalProviderId"] = request.newId.Trim();

                            var update = new UpdateServicingItemRequest
                            {
                                TaskType = item.TaskType,
                                Description = item.Description,
                                AssignedTo = string.IsNullOrWhiteSpace(item.AssignedTo) ? "system" : item.AssignedTo,
                                AssignedToUserId = item.AssignedToUserId,
                                Priority = item.Priority,
                                Status = item.Status,
                                CaseId = item.CaseId,
                                LienId = item.LienId,
                                DueDate = item.DueDate,
                                Notes = SerializeLegacyNoteFields(fields),
                                Resolution = item.Resolution,
                            };

                            await servicingItemService.UpdateAsync(tenantId, item.Id, userId, update, ct);
                        }

                        if ((page * pageSize) >= pageResult.TotalCount)
                            break;

                        page++;
                    }

                    return Results.Ok(new
                    {
                        isSuccess = true,
                        message = "Successfully Reassigned Liens.",
                    });
                }
                case "3": // funding company
                {
                    const int pageSize = 200;
                    var page = 1;
                    while (true)
                    {
                        var pageResult = await lienService.SearchAsync(
                            tenantId,
                            search: null,
                            status: null,
                            lienType: null,
                            caseId: null,
                            facilityId: null,
                            page: page,
                            pageSize: pageSize,
                            ct);

                        if (pageResult.Items.Count == 0)
                            break;

                        foreach (var item in pageResult.Items.Where(i =>
                                     string.Equals(i.ExternalReference, request.oldId, StringComparison.Ordinal)))
                        {
                            var update = new UpdateLienRequest
                            {
                                ExternalReference = request.newId.Trim(),
                                LienType = item.LienType,
                                CaseId = item.CaseId,
                                FacilityId = item.FacilityId,
                                OriginalAmount = item.OriginalAmount,
                                Jurisdiction = item.Jurisdiction,
                                IsConfidential = item.IsConfidential,
                                SubjectFirstName = item.SubjectFirstName,
                                SubjectLastName = item.SubjectLastName,
                                IncidentDate = item.IncidentDate,
                                Description = item.Description,
                            };

                            await lienService.UpdateAsync(tenantId, item.Id, userId, update, ct);
                        }

                        if ((page * pageSize) >= pageResult.TotalCount)
                            break;

                        page++;
                    }

                    return Results.Ok(new
                    {
                        isSuccess = true,
                        message = "Successfully Reassigned Fundings.",
                    });
                }
                case "4": // medical facility
                {
                    const int pageSize = 200;
                    var page = 1;
                    while (true)
                    {
                        var pageResult = await servicingItemService.SearchAsync(
                            tenantId,
                            search: "LegacyMedicalFacilityInfo",
                            status: null,
                            priority: null,
                            assignedTo: null,
                            caseId: null,
                            lienId: null,
                            page: page,
                            pageSize: pageSize,
                            ct);

                        if (pageResult.Items.Count == 0)
                            break;

                        foreach (var item in pageResult.Items.Where(i =>
                                     string.Equals(i.TaskType, "LegacyMedicalFacilityInfo", StringComparison.Ordinal)))
                        {
                            var fields = ParseLegacyNoteFields(item.Notes);
                            var currentFacilityId = fields.GetValueOrDefault("facilityId", string.Empty);
                            if (!string.Equals(currentFacilityId, request.oldId, StringComparison.Ordinal))
                                continue;

                            fields["facilityId"] = request.newId.Trim();

                            var update = new UpdateServicingItemRequest
                            {
                                TaskType = item.TaskType,
                                Description = item.Description,
                                AssignedTo = string.IsNullOrWhiteSpace(item.AssignedTo) ? "system" : item.AssignedTo,
                                AssignedToUserId = item.AssignedToUserId,
                                Priority = item.Priority,
                                Status = item.Status,
                                CaseId = item.CaseId,
                                LienId = item.LienId,
                                DueDate = item.DueDate,
                                Notes = SerializeLegacyNoteFields(fields),
                                Resolution = item.Resolution,
                            };

                            await servicingItemService.UpdateAsync(tenantId, item.Id, userId, update, ct);
                        }

                        if ((page * pageSize) >= pageResult.TotalCount)
                            break;

                        page++;
                    }

                    return Results.Ok(new
                    {
                        isSuccess = true,
                        message = "Successfully Reassigned Liens.",
                    });
                }
                case "5": // leads
                {
                    const int pageSize = 200;
                    var page = 1;
                    while (true)
                    {
                        var pageResult = await caseService.SearchAsync(
                            tenantId,
                            search: null,
                            status: null,
                            page: page,
                            pageSize: pageSize,
                            orgId: null,
                            ct);

                        if (pageResult.Items.Count == 0)
                            break;

                        foreach (var item in pageResult.Items)
                        {
                            var fields = ParseLegacyNoteFields(item.Notes);
                            var currentLeadId = fields.GetValueOrDefault("leadId", string.Empty);
                            if (!string.Equals(currentLeadId, request.oldId, StringComparison.Ordinal))
                                continue;

                            fields["leadId"] = request.newId.Trim();

                            var update = new UpdateCaseRequest
                            {
                                ClientFirstName = item.ClientFirstName,
                                ClientLastName = item.ClientLastName,
                                ExternalReference = item.ExternalReference,
                                Title = item.Title,
                                ClientDob = item.ClientDob,
                                ClientPhone = item.ClientPhone,
                                ClientEmail = item.ClientEmail,
                                ClientAddress = item.ClientAddress,
                                DateOfIncident = item.DateOfIncident,
                                InsuranceCarrier = item.InsuranceCarrier,
                                PolicyNumber = item.PolicyNumber,
                                ClaimNumber = item.ClaimNumber,
                                Description = item.Description,
                                Notes = SerializeLegacyNoteFields(fields),
                                Status = item.Status,
                                DemandAmount = item.DemandAmount,
                                SettlementAmount = item.SettlementAmount,
                            };

                            await caseService.UpdateAsync(tenantId, item.Id, userId, update, ct);
                        }

                        if ((page * pageSize) >= pageResult.TotalCount)
                            break;

                        page++;
                    }

                    return Results.Ok(new
                    {
                        isSuccess = true,
                        message = "Successfully Reassigned Leads.",
                    });
                }
                default:
                    return Results.NotFound(new
                    {
                        isSuccess = false,
                        message = "unable to re-assign cases.",
                    });
            }
        }
        catch
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "unable to re-assign cases.",
            });
        }
    }

    private static async Task<IResult> GeneratePayoffQuoteLegacy(
        Guid caseId,
        IPayoffQuoteService payoffQuoteService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var orgId = RequireOrgId(ctx);
        var userId = RequireUserId(ctx);

        try
        {
            var result = await payoffQuoteService.GetOrGenerateAsync(
                tenantId,
                orgId,
                userId,
                caseId,
                ctx.Email ?? ctx.Name ?? userId.ToString(),
                ct);

            if (result.Status == PayoffQuoteStatus.Success)
            {
                return Results.Ok(new
                {
                    isSuccess = true,
                    message = "Successfully retrieved Payoff Quote",
                    url = result.Url,
                    base64 = result.Base64,
                });
            }

            if (result.Status == PayoffQuoteStatus.CaseNotFound)
            {
                return Results.NotFound(new
                {
                    isSuccess = false,
                    message = "Error: Unable to retrieve Payoff Quote",
                });
            }

            return Results.Ok(new
            {
                isSuccess = false,
                message = "No payoff quote found.",
                url = string.Empty,
                base64 = string.Empty,
            });
        }
        catch
        {
            return Results.Ok(new
            {
                isSuccess = false,
                message = "No payoff quote found.",
                url = string.Empty,
                base64 = string.Empty,
            });
        }
    }

    private static bool IsLegacyPayoffQuoteDocument(
        Dictionary<string, string> fields,
        string? payoffStatementTypeId)
    {
        var typeIds = new[]
        {
            fields.GetValueOrDefault("typeId", string.Empty),
            fields.GetValueOrDefault("docTypeId", string.Empty),
            fields.GetValueOrDefault("documentTypeId", string.Empty),
        };

        if (typeIds.Any(typeId => string.Equals(typeId, "14", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (!string.IsNullOrWhiteSpace(payoffStatementTypeId) &&
            typeIds.Any(typeId => string.Equals(typeId, payoffStatementTypeId, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return LegacyValueIndicatesPayoffStatement(fields.GetValueOrDefault("category", string.Empty)) ||
               LegacyValueIndicatesPayoffStatement(fields.GetValueOrDefault("code", string.Empty)) ||
               LegacyValueIndicatesPayoffStatement(fields.GetValueOrDefault("name", string.Empty)) ||
               LegacyValueIndicatesPayoffStatement(fields.GetValueOrDefault("description", string.Empty)) ||
               LegacyValueIndicatesPayoffStatement(fields.GetValueOrDefault("filename", string.Empty)) ||
               LegacyValueIndicatesPayoffStatement(fields.GetValueOrDefault("originalFileName", string.Empty));
    }

    private static bool LegacyValueIndicatesPayoffStatement(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);

        return string.Equals(normalized, "PayoffStatement", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "PayoffQuote", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IResult> GetDashboardLegacy(
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var lienVisibility = LienVisibilityPolicy.Resolve(ctx);

        var caseStatusRows = await db.Cases
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId)
            .GroupBy(item => item.Status)
            .Select(group => new
            {
                Status = group.Key,
                Count = group.Count(),
            })
            .ToListAsync(ct);

        var visibleLiens = LienVisibilityPolicy.Apply(
            db.Liens.AsNoTracking().Where(item => item.TenantId == tenantId),
            lienVisibility);

        var lienStatusRows = await visibleLiens
            .AsNoTracking()
            .GroupBy(item => item.Status)
            .Select(group => new
            {
                Status = group.Key,
                Count = group.Count(),
                Amount = group.Sum(item => item.OriginalAmount),
            })
            .ToListAsync(ct);

        var totalCases = caseStatusRows.Sum(row => row.Count);
        var totalLiens = lienStatusRows.Sum(row => row.Count);
        if (totalCases == 0 && totalLiens == 0)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "No dashboard data found.",
            });
        }

        var caseStatus = caseStatusRows
            .Select(row => new
            {
                label = string.IsNullOrWhiteSpace(row.Status) ? "Unknown" : row.Status,
                value = row.Count,
            })
            .OrderBy(row => row.label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var lienStatus = lienStatusRows
            .Select(row => new
            {
                label = string.IsNullOrWhiteSpace(row.Status) ? "Unknown" : row.Status,
                value = row.Count,
            })
            .OrderBy(row => row.label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var data = new
        {
            totalCases,
            totalActiveCases = caseStatusRows
                .Where(row => !string.Equals(row.Status, CaseStatus.Closed, StringComparison.OrdinalIgnoreCase))
                .Sum(row => row.Count),
            totalLiens,
            totalLienValue = (double)lienStatusRows.Sum(row => row.Amount),
            caseStatus,
            lienStatus,
        };

        return Results.Ok(new
        {
            isSuccess = true,
            message = "Dashboard data retrieved successfully.",
            data,
        });
    }

    private static async Task<IResult> GenerateCaseCsvLegacy(
        LegacyGenerateCaseCsvRequest request,
        ICaseService caseService,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);

        try
        {
            const int pageSize = 100;
            var page = 1;
            var cases = new List<CaseResponse>();

            while (true)
            {
                var result = await caseService.SearchV3Async(
                    tenantId: tenantId,
                    keyword: request.keyword,
                    statusId: request.statusId,
                    page: page,
                    limit: pageSize,
                    sortBy: request.legacyFormat ? "caseCode" : request.sortBy,
                    sortDirection: request.legacyFormat ? "desc" : request.sortDirection,
                    accidentTypeId: request.accidentTypeId,
                    caseManagerId: request.caseManagerId,
                    lawFirmIds: request.lawFirmId,
                    ct: ct);

                if (result.Items.Count == 0)
                    break;

                cases.AddRange(result.Items);
                if (cases.Count >= result.TotalCount)
                    break;

                page++;
            }

            var filtered = cases
                .Where(c => string.IsNullOrWhiteSpace(request.caseId) ||
                            string.Equals(c.CaseNumber, request.caseId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (request.legacyFormat)
            {
                filtered = filtered
                    .OrderByDescending(c => c.CaseNumber, StringComparer.Ordinal)
                    .ToList();
            }

            if (filtered.Count == 0)
            {
                return Results.NotFound(new
                {
                    isSuccess = false,
                    message = "No cases found.",
                    data = (object?)null,
                });
            }

            if (!request.legacyFormat)
                return BuildCaseCsvResponse(BuildCaseListCsv(filtered));

            var caseIds = filtered.Select(item => item.Id).ToList();
            var sourceByCaseId = await db.Cases
                .AsNoTracking()
                .Where(item => item.TenantId == tenantId && caseIds.Contains(item.Id))
                .Select(item => new
                {
                    item.Id,
                    Source = new LegacyCaseCsvSource(
                        item.OrgId,
                        item.Notes,
                        item.ImportedCreatedByName,
                        item.CreatedByUserId,
                        item.UpdatedByUserId),
                })
                .ToDictionaryAsync(item => item.Id, item => item.Source, ct);

            var fieldsByCaseId = sourceByCaseId.ToDictionary(
                pair => pair.Key,
                pair => ParseLegacyNoteFields(pair.Value.Notes));
            var referencedContactIds = new HashSet<Guid>();
            foreach (var fields in fieldsByCaseId.Values)
            {
                AddDashboardContactId(GetLegacyCsvField(fields, "lawFirmId"), referencedContactIds);
                AddDashboardContactId(GetLegacyCsvField(fields, "caseManagerId"), referencedContactIds);
            }
            foreach (var source in sourceByCaseId.Values)
            {
                if (source.CreatedByUserId.HasValue)
                    referencedContactIds.Add(source.CreatedByUserId.Value);
                if (source.UpdatedByUserId.HasValue)
                    referencedContactIds.Add(source.UpdatedByUserId.Value);
            }

            var caseOrgIds = sourceByCaseId.Values.Select(source => source.OrgId).Distinct().ToList();
            var contacts = await db.Contacts
                .AsNoTracking()
                .Where(contact => contact.TenantId == tenantId &&
                    (referencedContactIds.Contains(contact.Id) ||
                     (contact.ContactType == ContactType.LawFirm && caseOrgIds.Contains(contact.OrgId))))
                .ToListAsync(ct);
            var contactsById = contacts.ToDictionary(contact => contact.Id);
            var lawFirmByOrgId = contacts
                .Where(contact => string.Equals(contact.ContactType, ContactType.LawFirm, StringComparison.Ordinal))
                .GroupBy(contact => contact.OrgId)
                .ToDictionary(group => group.Key, group => group.First());

            var supplements = filtered.ToDictionary(item => item.Id, item =>
            {
                sourceByCaseId.TryGetValue(item.Id, out var source);
                fieldsByCaseId.TryGetValue(item.Id, out var fields);
                fields ??= new Dictionary<string, string>(StringComparer.Ordinal);

                var lawFirm = FirstNonEmpty(item.LawFirm, GetLegacyCsvField(fields, "lawFirm")) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(lawFirm) &&
                    Guid.TryParse(GetLegacyCsvField(fields, "lawFirmId"), out var lawFirmId) &&
                    contactsById.TryGetValue(lawFirmId, out var lawFirmContact))
                {
                    lawFirm = ResolveDashboardContactName(lawFirmContact);
                }
                if (string.IsNullOrWhiteSpace(lawFirm) && source is not null &&
                    lawFirmByOrgId.TryGetValue(source.OrgId, out var organizationLawFirm))
                {
                    lawFirm = ResolveDashboardContactName(organizationLawFirm);
                }

                var caseManager = FirstNonEmpty(item.CaseManager, GetLegacyCsvField(fields, "caseManager")) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(caseManager) &&
                    Guid.TryParse(GetLegacyCsvField(fields, "caseManagerId"), out var caseManagerId) &&
                    contactsById.TryGetValue(caseManagerId, out var caseManagerContact))
                {
                    caseManager = caseManagerContact.DisplayName;
                }

                return new LegacyCaseCsvSupplement(
                    fields,
                    lawFirm,
                    caseManager,
                    FirstNonEmpty(
                        ResolveLegacyCsvUser(source?.CreatedByUserId, contactsById),
                        source?.ImportedCreatedByName) ?? string.Empty,
                    ResolveLegacyCsvUser(source?.UpdatedByUserId, contactsById));
            });

            return BuildCaseCsvResponse(BuildLegacyCaseCsv(filtered, supplements));
        }
        catch (Exception ex)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = $"Error generating CSV: {ex.Message}",
                data = (object?)null,
            });
        }
    }

    private static IResult BuildCaseCsvResponse(byte[] csvBytes)
    {
        if (csvBytes.Length == 0)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "No data generated.",
                data = (object?)null,
            });
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        var pacificNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone);
        var filename = $"case_{pacificNow:yyyyMMddHHmmss}.csv";
        var exportItem = new
        {
            base64 = Convert.ToBase64String(csvBytes),
            filename,
            export_format = "csv",
        };

        return Results.Ok(new
        {
            isSuccess = true,
            message = "CSV generated successfully.",
            data = new object[] { exportItem },
        });
    }

    private static byte[] BuildLegacyCaseCsv(
        List<CaseResponse> items,
        IReadOnlyDictionary<Guid, LegacyCaseCsvSupplement> supplements)
    {
        var sb = new StringBuilder();
        sb.AppendLine("CaseCode,FirstName,LastName,DateOfBirth,Address,City,State,ZipCode,IsServicing,IsUccFiled,IsBulk,AccidentType,AccidentState,DateOfLoss,LawFirm,CaseManager,Note,Created,CreateBy,Updated,UpdateBy,Status,CurrentStatus,CurrentMedicalStatus,CurrentAttributes,Email,Phone,Gender,SSN,Summary,ToGeneratePdf,SwitchedDate");

        foreach (var item in items)
        {
            supplements.TryGetValue(item.Id, out var supplement);
            var fields = supplement?.Fields ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var parsedAddress = SplitLegacyAddress(item.ClientAddress);
            var address = FirstNonEmpty(item.ClientStreetAddress, parsedAddress.Address) ?? string.Empty;
            var city = FirstNonEmpty(item.ClientCity, parsedAddress.City) ?? string.Empty;
            var state = FirstNonEmpty(item.ClientState, parsedAddress.State) ?? string.Empty;
            var zipcode = FirstNonEmpty(item.ClientZipcode, parsedAddress.Zipcode) ?? string.Empty;
            var row = string.Join(",", new[]
            {
                EscapeLegacyCsv(item.CaseNumber),
                EscapeLegacyCsv(item.ClientFirstName),
                EscapeLegacyCsv(item.ClientLastName),
                EscapeLegacyCsv(FormatLegacyDate(item.ClientDob)),
                EscapeLegacyCsv(address),
                EscapeLegacyCsv(city),
                EscapeLegacyCsv(state),
                EscapeLegacyCsv(zipcode),
                EscapeLegacyCsv(GetLegacyCsvField(fields, "isServicing")),
                EscapeLegacyCsv(FirstNonEmpty(
                    GetLegacyCsvField(fields, "isUccFiled", "isUCCFiled"),
                    item.IsUccFiled) ?? string.Empty),
                EscapeLegacyCsv(GetLegacyCsvField(fields, "isBulk")),
                EscapeLegacyCsv(FirstNonEmpty(
                    item.AccidentType,
                    item.CaseType,
                    GetLegacyCsvField(fields, "accidentType")) ?? string.Empty),
                EscapeLegacyCsv(FirstNonEmpty(
                    item.StateOfIncident,
                    GetLegacyCsvField(fields, "accidentState")) ?? string.Empty),
                EscapeLegacyCsv(FormatLegacyDate(item.DateOfIncident)),
                EscapeLegacyCsv(supplement?.LawFirm ?? string.Empty),
                EscapeLegacyCsv(supplement?.CaseManager ?? string.Empty),
                EscapeLegacyCsv(item.Notes ?? string.Empty),
                EscapeLegacyCsv(FormatLegacyTimestamp(item.CreatedAtUtc)),
                EscapeLegacyCsv(supplement?.CreatedBy ?? string.Empty),
                EscapeLegacyCsv(FormatLegacyTimestamp(item.UpdatedAtUtc)),
                EscapeLegacyCsv(supplement?.UpdatedBy ?? string.Empty),
                EscapeLegacyCsv(item.Status),
                EscapeLegacyCsv(item.Status),
                EscapeLegacyCsv(FirstNonEmpty(
                    item.CurrentMedicalStatus,
                    GetLegacyCsvField(fields, "currentMedicalStatus")) ?? string.Empty),
                EscapeLegacyCsv(GetLegacyCsvField(fields, "currentAttributes")),
                EscapeLegacyCsv(item.ClientEmail ?? string.Empty),
                EscapeLegacyCsv(item.ClientPhone ?? string.Empty),
                EscapeLegacyCsv(FirstNonEmpty(item.Sex, GetLegacyCsvField(fields, "gender")) ?? string.Empty),
                EscapeLegacyCsv(GetLegacyCsvField(fields, "ssn")),
                EscapeLegacyCsv(item.Description ?? string.Empty),
                EscapeLegacyCsv(GetLegacyCsvField(fields, "toGeneratePdf")),
                EscapeLegacyCsv(GetLegacyCsvField(fields, "switchedDate")),
            });

            sb.AppendLine(row);
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] BuildCaseListCsv(List<CaseResponse> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Case ID,Plaintiff Name,Law Firm,Case Manager,Accident Type,Date of Loss,DOB,Status");

        foreach (var item in items)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                EscapeLegacyCsv(item.CaseNumber),
                EscapeLegacyCsv(item.ClientDisplayName),
                EscapeLegacyCsv(item.LawFirm ?? string.Empty),
                EscapeLegacyCsv(item.CaseManager ?? string.Empty),
                EscapeLegacyCsv(item.AccidentType ?? string.Empty),
                EscapeLegacyCsv(FormatLegacyDate(item.DateOfIncident)),
                EscapeLegacyCsv(FormatLegacyDate(item.ClientDob)),
                EscapeLegacyCsv(item.StatusLabel),
            }));
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string GetLegacyCsvField(
        IReadOnlyDictionary<string, string> fields,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (fields.TryGetValue(key, out var value))
                return value;

            var match = fields.FirstOrDefault(pair =>
                string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match.Key))
                return match.Value;
        }

        return string.Empty;
    }

    private static string ResolveLegacyCsvUser(
        Guid? userId,
        IReadOnlyDictionary<Guid, Liens.Domain.Entities.Contact> contactsById)
    {
        if (!userId.HasValue)
            return string.Empty;

        return contactsById.TryGetValue(userId.Value, out var contact) &&
               !string.IsNullOrWhiteSpace(contact.DisplayName)
            ? contact.DisplayName
            : userId.Value.ToString();
    }

    private static async Task<IResult> GenerateLiensCsvLegacy(
        LegacyGenerateLiensCsvRequest request,
        ILienService lienService,
        ICaseRepository caseRepository,
        ICompanyRepository companyRepository,
        IServicingItemRepository servicingItemRepository,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);

        try
        {
            var caseIdFilter = ParseGuidCsvValues(request.caseId);
            var lienIdFilter = ParseGuidCsvValues(request.liensId);
            var lawFirmIdFilter = ParseStringCsvValues(request.lawFirmId);
            var facilityIdFilter = ParseStringCsvValues(request.medicalFacilityId);
            var caseManagerIdFilter = ParseStringCsvValues(request.caseManagerId);
            var lienStatusFilter = (request.lienStatusId ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var expandedLienStatusFilter = await LienEndpoints.ResolveLienStatusCodesAsync(
                db,
                tenantId,
                lienStatusFilter,
                ct);
            var excludeRejectedAndCancelled =
                !expandedLienStatusFilter.Contains(LienStatus.Cancelled);

            var allLiens = new List<LienResponse>();
            const int pageSize = 200;
            var page = 1;

            while (true)
            {
                var seededCaseId = caseIdFilter.Count == 1 ? caseIdFilter.First() : (Guid?)null;

                var result = await lienService.SearchAsync(
                    tenantId,
                    search: request.keyword,
                    status: null,
                    lienType: null,
                    caseId: seededCaseId,
                    facilityId: null,
                    page: page,
                    pageSize: pageSize,
                    ct,
                    excludeRejectedAndCancelled: excludeRejectedAndCancelled);

                if (result.Items.Count == 0)
                    break;

                allLiens.AddRange(result.Items);
                if (allLiens.Count >= result.TotalCount)
                    break;

                page++;
            }

            var candidateLienIds = allLiens.Select(lien => lien.Id).ToList();
            var deletedLienIds = new HashSet<Guid>();
            if (candidateLienIds.Count > 0)
            {
                deletedLienIds = (await db.LienStatusHistories
                    .AsNoTracking()
                    .Where(history =>
                        history.TenantId == tenantId &&
                        candidateLienIds.Contains(history.LienId) &&
                        history.Description.StartsWith(DeletedLienHistoryDescription))
                    .Select(history => history.LienId)
                    .Distinct()
                    .ToListAsync(ct))
                    .ToHashSet();
            }

            var advancedFilterRowsByLienId = new Dictionary<Guid, LienEndpoints.AdvancedLienFilterRow>();
            if (lawFirmIdFilter.Count > 0 || facilityIdFilter.Count > 0 || caseManagerIdFilter.Count > 0)
            {
                var candidateLiens = await db.Liens
                    .AsNoTracking()
                    .Where(lien => lien.TenantId == tenantId && candidateLienIds.Contains(lien.Id))
                    .ToListAsync(ct);
                var advancedFilterRows = await LienEndpoints.BuildAdvancedLienFilterRowsAsync(
                    db,
                    tenantId,
                    candidateLiens,
                    ct);
                advancedFilterRowsByLienId = advancedFilterRows.ToDictionary(row => row.Lien.Id);
            }

            var filteredLiens = allLiens
                .Where(l => !deletedLienIds.Contains(l.Id))
                .Where(l => string.IsNullOrWhiteSpace(request.caseId) ||
                            (l.CaseId.HasValue && caseIdFilter.Contains(l.CaseId.Value)))
                .Where(l => string.IsNullOrWhiteSpace(request.liensId) || lienIdFilter.Contains(l.Id))
                .Where(l => MatchesAdvancedLienExportFilters(
                    advancedFilterRowsByLienId.GetValueOrDefault(l.Id),
                    lawFirmIdFilter,
                    facilityIdFilter,
                    caseManagerIdFilter))
                .Where(l => expandedLienStatusFilter.Count == 0 || expandedLienStatusFilter.Contains(l.Status))
                .Where(l => MatchesLegacyPurchaseDateFilter(ParseLegacyDate(l.PurchaseDate), request.purchaseDate))
                .Where(l => MatchesLegacyDateFilter(
                    l.ClosedAtUtc.HasValue ? DateOnly.FromDateTime(l.ClosedAtUtc.Value) : null,
                    request.closedDate))
                .OrderByDescending(l => l.CreatedAtUtc)
                .ToList();

            var filteredCaseIds = filteredLiens
                .Where(lien => lien.CaseId.HasValue)
                .Select(lien => lien.CaseId!.Value)
                .Distinct()
                .ToList();
            var casesById = (await caseRepository.GetByIdsAsync(tenantId, filteredCaseIds, ct))
                .ToDictionary(caseInfo => caseInfo.Id);
            var handlingLawFirmCompanyIds = casesById.Values
                .Where(caseInfo => caseInfo.HandlingLawFirmCompanyId.HasValue)
                .Select(caseInfo => caseInfo.HandlingLawFirmCompanyId!.Value)
                .Distinct()
                .ToList();
            var companiesById = (await companyRepository.GetCompaniesByIdsAsync(
                    tenantId,
                    handlingLawFirmCompanyIds,
                    ct))
                .ToDictionary(company => company.Id);

            var filteredLienIds = filteredLiens
                .Select(lien => lien.Id)
                .Where(id => id != Guid.Empty)
                .ToList();
            var servicingItemsByLienId = (await servicingItemRepository.GetByLienIdsAsync(
                    tenantId,
                    filteredLienIds,
                    ["LegacyMedicalCode", "LegacyMedicalFacilityInfo"],
                    ct))
                .Where(item => item.LienId.HasValue)
                .GroupBy(item => item.LienId!.Value)
                .ToDictionary(group => group.Key, group => group.ToList());

            var rows = new List<LegacyLiensCsvRow>();
            foreach (var lien in filteredLiens)
            {
                Case? caseInfo = null;
                Dictionary<string, string> caseFields;
                if (lien.CaseId.HasValue)
                {
                    if (!casesById.TryGetValue(lien.CaseId.Value, out caseInfo))
                        continue;

                    caseFields = ParseLegacyNoteFields(ExtractLegacyNoteText(caseInfo.Notes));
                }
                else
                {
                    caseFields = new Dictionary<string, string>(StringComparer.Ordinal);
                }

                decimal totalPurchase = 0m;
                decimal totalBilling = 0m;
                var servicingItems = servicingItemsByLienId.GetValueOrDefault(lien.Id) ?? [];
                foreach (var item in servicingItems.Where(item =>
                             string.Equals(item.TaskType, "LegacyMedicalCode", StringComparison.Ordinal)))
                {
                    var codeFields = ParseLegacyNoteFields(item.Notes);
                    if (decimal.TryParse(codeFields.GetValueOrDefault("purchaseAmount", string.Empty), NumberStyles.Any, CultureInfo.InvariantCulture, out var purchase))
                        totalPurchase += purchase;
                    if (decimal.TryParse(codeFields.GetValueOrDefault("billingAmount", string.Empty), NumberStyles.Any, CultureInfo.InvariantCulture, out var billing))
                        totalBilling += billing;
                }

                var facilityFields = new Dictionary<string, string>(StringComparer.Ordinal);
                var infoItem = servicingItems
                    .Where(item => string.Equals(
                        item.TaskType,
                        "LegacyMedicalFacilityInfo",
                        StringComparison.Ordinal))
                    .OrderByDescending(item => item.CreatedAtUtc)
                    .ThenByDescending(item => item.Id)
                    .FirstOrDefault();
                if (infoItem is not null)
                    facilityFields = ParseLegacyNoteFields(infoItem.Notes);

                var casePlaintiffName = caseInfo is null
                    ? null
                    : $"{caseInfo.ClientFirstName} {caseInfo.ClientLastName}".Trim();

                var plaintiffName = lien.IsConfidential
                    ? "Confidential"
                    : FirstNonEmpty(
                        lien.Plaintiff,
                        lien.SubjectDisplayName,
                        casePlaintiffName) ?? string.Empty;
                var plainTiffName = casePlaintiffName ?? string.Empty;
                var canonicalLawFirm = caseInfo?.HandlingLawFirmCompanyId is Guid companyId
                    ? companiesById.GetValueOrDefault(companyId)?.Name
                    : null;

                var closedDate = LienStatus.Terminal.Contains(lien.Status)
                    ? FormatLegacyTimestamp(lien.UpdatedAtUtc)
                    : string.Empty;

                rows.Add(new LegacyLiensCsvRow
                {
                    CaseCode = caseInfo?.CaseNumber ?? string.Empty,
                    LiensCode = lien.LienNumber,
                    Status = lien.Status,
                    StatusLabel = FirstNonEmpty(lien.StatusLabel, lien.Status) ?? string.Empty,
                    PlaintiffName = plaintiffName,
                    DisplayLawFirm = FirstNonEmpty(
                        lien.LawFirm,
                        canonicalLawFirm,
                        caseFields.GetValueOrDefault("lawFirm", string.Empty)) ?? string.Empty,
                    DisplayCaseManager = FirstNonEmpty(
                        lien.CaseManager,
                        caseFields.GetValueOrDefault("caseManager", string.Empty)) ?? string.Empty,
                    DisplayFacilityName = FirstNonEmpty(
                        lien.MedicalFacility,
                        facilityFields.GetValueOrDefault("facilityName", string.Empty)) ?? string.Empty,
                    PurchaseDate = lien.PurchaseDate ?? string.Empty,
                    InitialServiceDate = FormatLegacyDate(lien.InitialServiceDate) is { Length: > 0 } initialServiceDate
                        ? initialServiceDate
                        : facilityFields.GetValueOrDefault("initialServiceDate", string.Empty),
                    EndServiceDate = FormatLegacyDate(lien.EndServiceDate) is { Length: > 0 } endServiceDate
                        ? endServiceDate
                        : facilityFields.GetValueOrDefault("endServiceDate", string.Empty),
                    Note = lien.Description ?? string.Empty,
                    FacilityEmail = facilityFields.GetValueOrDefault("email", string.Empty),
                    FacilityPhone = facilityFields.GetValueOrDefault("phone", string.Empty),
                    TotalPurchase = totalPurchase.ToString("#,##0.00", CultureInfo.InvariantCulture),
                    TotalBilling = totalBilling.ToString("#,##0.00", CultureInfo.InvariantCulture),
                    LawFirm = caseFields.GetValueOrDefault("lawFirm", string.Empty),
                    CaseManager = caseFields.GetValueOrDefault("caseManager", string.Empty),
                    FacilityName = facilityFields.GetValueOrDefault("facilityName", string.Empty),
                    FacilityContactName = facilityFields.GetValueOrDefault("facilityContactPerson", string.Empty),
                    MedicalProvider = facilityFields.GetValueOrDefault("medicalProvider", string.Empty),
                    PlainTiffName = plainTiffName,
                    ClosedDate = closedDate,
                });
            }

            if (rows.Count == 0)
            {
                return Results.NotFound(new
                {
                    isSuccess = false,
                    message = "No liens found. ",
                    data = (object?)null,
                });
            }

            var csvBytes = request.legacyFormat
                ? BuildLegacyLiensCsv(rows)
                : BuildLienListCsv(rows);
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
            var pacificNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone);
            var filename = $"liens_{pacificNow:yyyyMMddHHmmss}.csv";
            var exportItem = new
            {
                base64 = Convert.ToBase64String(csvBytes),
                filename,
                export_format = "csv",
            };

            return Results.Ok(new
            {
                isSuccess = true,
                message = "CSV generated successfully.",
                data = new object[] { exportItem },
            });
        }
        catch (Exception ex)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = $"Error generating CSV:  {ex.Message}",
                data = (object?)null,
            });
        }
    }

    private static byte[] BuildLienListCsv(List<LegacyLiensCsvRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Lien ID,Plaintiff Name,Law Firm,Medical Facility,Purchase Date,Purchase Amount,Billing Amount,Lien Status,Initial Service Date,Case Manager");

        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                EscapeLegacyCsv(row.LiensCode),
                EscapeLegacyCsv(row.PlaintiffName),
                EscapeLegacyCsv(row.DisplayLawFirm),
                EscapeLegacyCsv(row.DisplayFacilityName),
                EscapeLegacyCsv(row.PurchaseDate),
                EscapeLegacyCsv(row.TotalPurchase),
                EscapeLegacyCsv(row.TotalBilling),
                EscapeLegacyCsv(row.StatusLabel),
                EscapeLegacyCsv(row.InitialServiceDate),
                EscapeLegacyCsv(row.DisplayCaseManager),
            }));
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] BuildLegacyLiensCsv(List<LegacyLiensCsvRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("CaseCode,LiensCode,Status,PurchaseDate,InitialServiceDate,EndServiceDate,Note,FacilityEmail,FacilityPhone,TotalPurchase,TotalBilling,LawFirm,CaseManager,FacilityName,FacilityContactName,MedicalProvider,PlainTiffName,ClosedDate");

        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                EscapeLegacyCsv(row.CaseCode),
                EscapeLegacyCsv(row.LiensCode),
                EscapeLegacyCsv(row.Status),
                EscapeLegacyCsv(row.PurchaseDate),
                EscapeLegacyCsv(row.InitialServiceDate),
                EscapeLegacyCsv(row.EndServiceDate),
                EscapeLegacyCsv(row.Note),
                EscapeLegacyCsv(row.FacilityEmail),
                EscapeLegacyCsv(row.FacilityPhone),
                EscapeLegacyCsv(row.TotalPurchase),
                EscapeLegacyCsv(row.TotalBilling),
                EscapeLegacyCsv(row.LawFirm),
                EscapeLegacyCsv(row.CaseManager),
                EscapeLegacyCsv(row.FacilityName),
                EscapeLegacyCsv(row.FacilityContactName),
                EscapeLegacyCsv(row.MedicalProvider),
                EscapeLegacyCsv(row.PlainTiffName),
                EscapeLegacyCsv(row.ClosedDate),
            }));
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static HashSet<Guid> ParseGuidCsvValues(string? raw)
    {
        var set = new HashSet<Guid>();
        if (string.IsNullOrWhiteSpace(raw))
            return set;

        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Guid.TryParse(token, out var id))
                set.Add(id);
        }

        return set;
    }

    private static HashSet<string> ParseStringCsvValues(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool MatchesAdvancedLienExportFilters(
        LienEndpoints.AdvancedLienFilterRow? row,
        HashSet<string> lawFirmIds,
        HashSet<string> facilityIds,
        HashSet<string> caseManagerIds)
    {
        if (lawFirmIds.Count == 0 && facilityIds.Count == 0 && caseManagerIds.Count == 0)
            return true;

        return row is not null &&
               (lawFirmIds.Count == 0 ||
                lawFirmIds.Contains(row.LawFirmId) ||
                lawFirmIds.Contains(row.Lien.OrgId.ToString())) &&
               (facilityIds.Count == 0 ||
                facilityIds.Contains(row.FacilityFilterId) ||
                (row.Lien.FacilityId.HasValue && facilityIds.Contains(row.Lien.FacilityId.Value.ToString()))) &&
               (caseManagerIds.Count == 0 || caseManagerIds.Contains(row.CaseManagerId));
    }

    private static bool MatchesLegacyPurchaseDateFilter(DateOnly? value, string? rawFilter)
        => MatchesLegacyDateFilter(value, rawFilter);

    private static bool MatchesLegacyDateFilter(DateOnly? value, string? rawFilter)
    {
        if (string.IsNullOrWhiteSpace(rawFilter))
            return true;
        if (!value.HasValue)
            return false;

        var normalized = rawFilter.Trim();
        if (TryParseLegacyFilterDate(normalized, out var exact))
            return value.Value == exact;

        foreach (var separator in new[] { " - ", " to ", "," })
        {
            var range = normalized.Split(
                separator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (range.Length == 2 &&
                TryParseLegacyFilterDate(range[0], out var start) &&
                TryParseLegacyFilterDate(range[1], out var end))
            {
                return value.Value >= start && value.Value <= end;
            }
        }

        if (normalized.Length == 21 && normalized[10] == '-' &&
            TryParseLegacyFilterDate(normalized[..10], out var compactStart) &&
            TryParseLegacyFilterDate(normalized[11..], out var compactEnd))
        {
            return value.Value >= compactStart && value.Value <= compactEnd;
        }

        return false;
    }

    private static bool TryParseLegacyFilterDate(string value, out DateOnly date)
    {
        string[] formats = ["MM/dd/yyyy", "yyyy-MM-dd"];
        return DateOnly.TryParseExact(
            value,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

    private static string EscapeLegacyCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var needsQuotes = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        if (!needsQuotes)
            return value;

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static DateOnly? ParseLegacyDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateOnly.TryParseExact(
            value,
            "MM/dd/yyyy",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : null;
    }

    private static string? BuildAddress(string? address, string? city, string? state, string? zipcode)
    {
        var formatted = string.Join(", ", new[] { address, city, state, zipcode }
            .Where(v => !string.IsNullOrWhiteSpace(v)));

        return string.IsNullOrWhiteSpace(formatted) ? null : formatted;
    }

    private static (string Address, string City, string State, string Zipcode) SplitLegacyAddress(string? rawAddress)
    {
        if (string.IsNullOrWhiteSpace(rawAddress))
            return (string.Empty, string.Empty, string.Empty, string.Empty);

        var parts = rawAddress
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length >= 4)
        {
            return (
                string.Join(", ", parts.Take(parts.Length - 3)),
                parts[^3],
                parts[^2],
                parts[^1]);
        }

        if (parts.Length == 3)
            return (parts[0], parts[1], parts[2], string.Empty);

        if (parts.Length == 2)
            return (parts[0], parts[1], string.Empty, string.Empty);

        return (rawAddress.Trim(), string.Empty, string.Empty, string.Empty);
    }

    internal static Guid RequireTenantId(ICurrentRequestContext ctx)
    {
        return ctx.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");
    }

    internal static Guid RequireUserId(ICurrentRequestContext ctx)
    {
        return ctx.UserId
            ?? throw new UnauthorizedAccessException("User context is required.");
    }

    private static Guid RequireOrgId(ICurrentRequestContext ctx)
    {
        return ctx.OrgId
            ?? throw new UnauthorizedAccessException("Organization context is required.");
    }

    private static async Task<IResult> ListCases(
        ICaseService caseService,
        ICurrentRequestContext ctx,
        string? search = null,
        string? status = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var result = await caseService.SearchAsync(tenantId, search, status, page, pageSize, ct: ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetCaseById(
        Guid id,
        ICaseService caseService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var result = await caseService.GetByIdAsync(tenantId, id, ct);
        return result is null
            ? Results.NotFound(new { error = new { code = "not_found", message = $"Case '{id}' not found." } })
            : Results.Ok(result);
    }

    private static async Task<IResult> GetCaseByCaseNumber(
        string caseNumber,
        ICaseService caseService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var result = await caseService.GetByCaseNumberAsync(tenantId, caseNumber, ct);
        return result is null
            ? Results.NotFound(new { error = new { code = "not_found", message = $"Case with number '{caseNumber}' not found." } })
            : Results.Ok(result);
    }

    private static string FormatLegacyDate(DateOnly? value)
        => value?.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string FormatLegacyTimestamp(DateTime value)
        => PacificTimeHelper.FormatTimestamp(value);

    private static async Task<IResult> GetCaseInfoV2Legacy(
        Guid id,
        ICaseService caseService,
        ILienService lienService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var item = await caseService.GetByIdAsync(tenantId, id, ct);
        if (item is null)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "No cases found.",
            });
        }

        var lienResult = await lienService.SearchAsync(
            tenantId,
            search: null,
            status: null,
            lienType: null,
            caseId: id,
            facilityId: null,
            page: 1,
            pageSize: 100,
            ct);

        var openLiens = 0;
        foreach (var openStatus in LienStatus.Open)
        {
            var openResult = await lienService.SearchAsync(
                tenantId,
                search: null,
                status: openStatus,
                lienType: null,
                caseId: id,
                facilityId: null,
                page: 1,
                pageSize: 1,
                ct);
            openLiens += openResult.TotalCount;
        }

        var totalLiens = lienResult.TotalCount;
        var showClosedOnlyStatus = totalLiens > 0 && openLiens == 0;
        var latestTerminalLien = lienResult.Items
            .Where(l => LienStatus.Terminal.Contains(l.Status))
            .OrderByDescending(l => l.CreatedAtUtc)
            .FirstOrDefault();

        var parsedAddress = SplitLegacyAddress(item.ClientAddress);
        var caseMetadata = ParseLegacyNoteFields(item.Notes);

        var legacyItem = new LegacyCaseInfoV2Response
        {
            caseId = item.Id.ToString(),
            caseCode = item.CaseNumber,
            firstname = item.ClientFirstName,
            lastname = item.ClientLastName,
            dateOfBirth = FormatLegacyDate(item.ClientDob),
            address = parsedAddress.Address,
            city = parsedAddress.City,
            state = parsedAddress.State,
            zipcode = parsedAddress.Zipcode,
            isServicing = string.Empty,
            isUccFiled = string.Empty,
            isBulk = string.Empty,
            accidentType = caseMetadata.GetValueOrDefault("accidentType", string.Empty),
            accidentState = FirstNonEmpty(
                item.StateOfIncident,
                caseMetadata.GetValueOrDefault("accidentState", string.Empty)) ?? string.Empty,
            dateOfLoss = FormatLegacyDate(item.DateOfIncident),
            lawFirm = string.Empty,
            caseManager = string.Empty,
            note = ExtractLegacyNoteText(item.Notes) ?? string.Empty,
            created = FormatLegacyTimestamp(item.CreatedAtUtc),
            createBy = string.Empty,
            updated = FormatLegacyTimestamp(item.UpdatedAtUtc),
            updateBy = string.Empty,
            status = item.Status,
            currentStatus = item.Status,
            currentMedicalStatus = caseMetadata.GetValueOrDefault("currentMedicalStatus", string.Empty),
            currentAttributes = string.Empty,
            email = item.ClientEmail ?? string.Empty,
            phone = item.ClientPhone ?? string.Empty,
            gender = caseMetadata.GetValueOrDefault("gender", string.Empty),
            ssn = string.Empty,
            summary = item.Description ?? string.Empty,
            countIndex = string.Empty,
            accidentTypeId = caseMetadata.GetValueOrDefault("accidentTypeId", string.Empty),
            currentStatusId = string.Empty,
            currentMedicalStatusId = caseMetadata.GetValueOrDefault("currentMedicalStatusId", string.Empty),
            currentAttributesId = string.Empty,
            toGeneratePdf = string.Empty,
            switchedDate = string.Empty,
            lawFirmId = string.Empty,
            caseManagerId = caseMetadata.GetValueOrDefault("caseManagerId", string.Empty),
            trackingFollowUpDate = caseMetadata.GetValueOrDefault("trackingFollowUpDate", string.Empty),
            childSupportLiens = caseMetadata.GetValueOrDefault("childSupportLiens", string.Empty),
            minorComp = caseMetadata.GetValueOrDefault("minorComp", string.Empty),
            leadId = caseMetadata.GetValueOrDefault("leadId", string.Empty),
            caseManagerDesc = string.Empty,
            shareCase = caseMetadata.GetValueOrDefault("shareCase", string.Empty),
            confirmedWriting = string.Empty,
            caseAttorney = string.Empty,
            caseAttorneyId = string.Empty,
            leadDescription = string.Empty,
            caseDropped = caseMetadata.GetValueOrDefault("caseDropped", string.Empty),
            externalCaseId = item.ExternalReference ?? string.Empty,
            totalLiens = totalLiens,
            lienStatus = showClosedOnlyStatus ? latestTerminalLien?.Status ?? string.Empty : string.Empty,
            lienStatusId = showClosedOnlyStatus ? latestTerminalLien?.Status ?? string.Empty : string.Empty,
            settlementStatus = string.Empty,
            settlementStatusId = string.Empty,
        };

        return Results.Ok(new
        {
            isSuccess = true,
            message = "Case Info.",
            data = new[] { legacyItem },
        });
    }

    private static async Task<IResult> GetCaseByLawFirmIdLegacy(
        string lawFirmId,
        ICaseService caseService,
        ICurrentRequestContext ctx,
        bool isTotal = false,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);

        if (!Guid.TryParse(lawFirmId, out var lawFirmOrgId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "Error: No cases found for the specified law firm.",
            });
        }

        var page = 1;
        var pageSize = 100;
        var data = new List<CaseResponse>();

        while (true)
        {
            var result = await caseService.SearchAsync(
                tenantId,
                search: null,
                status: null,
                page: page,
                pageSize: pageSize,
                orgId: lawFirmOrgId,
                ct);

            if (result.Items.Count == 0)
                break;

            data.AddRange(result.Items);

            if (!isTotal || data.Count >= result.TotalCount)
                break;

            page++;
        }

        if (data.Count == 0)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "Error: No cases found for the specified law firm.",
            });
        }

        return Results.Ok(new
        {
            isSuccess = true,
            message = "Case list retrieved successfully.",
            data,
        });
    }

    private static async Task<IResult> CreateCase(
        CreateCaseRequest request,
        ICaseService caseService,
        ICurrentRequestContext ctx,
        HttpRequest httpRequest,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var orgId = RequireOrgId(ctx);
        var userId = RequireUserId(ctx);
        var idempotencyKey = httpRequest.Headers["Idempotency-Key"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(request.ExternalReference) &&
            !string.IsNullOrWhiteSpace(idempotencyKey))
            request = request with { ExternalReference = idempotencyKey };
        var result = await caseService.CreateAsync(tenantId, orgId, userId, request, ct);
        return Results.Created($"/api/liens/cases/{result.Id}", result);
    }

    private static async Task<IResult> CheckDuplicateCase(
        LegacyCreateCaseRequest request,
        ICaseService caseService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var result = await caseService.CheckDuplicatesAsync(
            tenantId,
            new CaseDuplicateCheckRequest
            {
                ClientFirstName = request.firstname ?? string.Empty,
                ClientLastName = request.lastname ?? string.Empty,
                ClientDob = ParseLegacyDate(request.dob),
                DateOfIncident = ParseLegacyDate(request.dateOfLoss),
            },
            ct);

        return Results.Ok(result);
    }

    private static async Task<IResult> GetLawFirmV3Legacy(
        LegacyLawFirmV3Request req,
        ICaseService caseService,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var lawFirmId = req.LawFirmId?.Trim();
        if (string.IsNullOrWhiteSpace(lawFirmId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "Error: No cases found.",
            });
        }

        var cases = await GetCasesFromDashboardCaseFilterAsync(
            "lawFirmId",
            lawFirmId,
            caseService,
            db,
            ctx,
            ct,
            requireLawFirm: true);

        return BuildLegacyLinkedCasesResult(
            cases,
            req.Keyword,
            req.Page,
            req.Limit,
            "Error: No cases found.");
    }

    private static async Task<IResult> GetLiensByMedicalIdV3Legacy(
        LegacyMedicalLiensV3Request req,
        ICaseService caseService,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var medicalId = req.MedicalId?.Trim();
        if (string.IsNullOrWhiteSpace(medicalId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "Error: No cases found.",
            });
        }

        var cases = await GetCasesFromDashboardLienFilterAsync(
            "medicalProviderId",
            medicalId,
            caseService,
            db,
            ctx,
            ct,
            requireMedicalProvider: true);

        return BuildLegacyLinkedCasesResult(
            cases,
            req.Keyword,
            req.Page,
            req.Limit,
            "Error: No cases found.");
    }

    private static async Task<IResult> GetLiensByFundingCompanyIdV3Legacy(
        LegacyFundingCompanyLiensV3Request req,
        ICaseService caseService,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var fundingCompanyId = req.FundingCompanyId?.Trim();
        if (string.IsNullOrWhiteSpace(fundingCompanyId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "Error: No cases found.",
            });
        }

        var cases = await GetCasesFromDashboardLienFilterAsync(
            "fundingCompanyId",
            fundingCompanyId,
            caseService,
            db,
            ctx,
            ct);

        return BuildLegacyLinkedCasesResult(
            cases,
            req.Keyword,
            req.Page,
            req.Limit,
            "Error: No cases found.");
    }

    private static async Task<IResult> GetLiensByMedicalFacilityIdV3Legacy(
        LegacyFacilityLiensV3Request req,
        ICaseService caseService,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var facilityId = req.FacilityId?.Trim();
        if (string.IsNullOrWhiteSpace(facilityId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "Error: No cases found.",
            });
        }

        var cases = await GetCasesFromDashboardLienFilterAsync(
            "facilityId",
            facilityId,
            caseService,
            db,
            ctx,
            ct);

        return BuildLegacyLinkedCasesResult(
            cases,
            req.Keyword,
            req.Page,
            req.Limit,
            "Error: No cases found.");
    }

    private static async Task<IResult> GetLeadV3Legacy(
        LegacyLeadCaseV3Request req,
        ICaseService caseService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);

        var leadId = req.LeadId?.Trim();
        if (string.IsNullOrWhiteSpace(leadId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "Error: No cases found.",
            });
        }

        var allCases = await SearchAllCasesAsync(caseService, tenantId, ct);

        var filteredByLead = allCases
            .Where(c => string.Equals(c.LeadId, leadId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return BuildLegacyLinkedCasesResult(
            filteredByLead,
            req.Keyword,
            req.Page,
            req.Limit,
            "Error: No cases found.");
    }

    private static async Task<IResult> GetCaseUpdatesV3Legacy(
        LegacyCaseUpdatesV3Request req,
        LiensDbContext db,
        IOptions<LegacyUpdateHistoryOptions> historyOptions,
        IOptions<IdentityServiceOptions> identityOptions,
        IHttpClientFactory httpClientFactory,
        HttpContext httpContext,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);

        if (!Guid.TryParse(req.CaseId, out var caseId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "Error: No case updates found.",
            });
        }

        if (!TryGetTimelineWindow(req.Page, req.Limit, out var page, out var limit, out var offset, out var fetchCount))
            return LegacyTimelinePaginationError();

        var nativeQuery = db.LienCaseNotes
            .AsNoTracking()
            .Where(note => note.TenantId == tenantId
                && note.CaseId == caseId
                && !note.IsDeleted
                && (note.Category == CaseNoteCategory.Internal || note.Category == CaseNoteCategory.CaseCreated));
        var nativeCount = await nativeQuery.CountAsync(ct);
        var nativeNotes = await nativeQuery
            .OrderByDescending(note => note.UpdatedAtUtc ?? note.CreatedAtUtc)
            .ThenByDescending(note => note.Id)
            .Take(fetchCount)
            .ToListAsync(ct);

        var nativeHistoryQuery = db.CaseUpdateHistories
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.CaseId == caseId);
        var nativeHistoryCount = await nativeHistoryQuery.CountAsync(ct);
        var nativeHistory = await nativeHistoryQuery
            .OrderByDescending(item => item.OccurredAtUtc)
            .ThenByDescending(item => item.Id)
            .Take(fetchCount)
            .ToListAsync(ct);

        var timeline = nativeNotes.Select(note =>
        {
            var description = LegacyCaseUpdateCompatibility.NormalizeDescription(note.Content, note.Category);
            return new LegacyCaseTimelineItem(
                note.Id.ToString(),
                note.CaseId.ToString(),
                string.Equals(note.Category, CaseNoteCategory.Internal, StringComparison.OrdinalIgnoreCase)
                    ? "Case Details Update"
                    : string.IsNullOrWhiteSpace(note.Category) ? "CaseNote" : note.Category,
                description,
                note.UpdatedAtUtc ?? note.CreatedAtUtc,
                0,
                0,
                description,
                note.Category,
                note.IsPinned,
                note.IsEdited,
                note.CreatedAtUtc,
                note.CreatedByName,
                note.UpdatedAtUtc,
                note.CreatedByName,
                note.CreatedByUserId);
        }).ToList();

        timeline.AddRange(nativeHistory.Select(item => new LegacyCaseTimelineItem(
            item.Id.ToString(),
            item.CaseId.ToString(),
            item.Action,
            item.Description,
            item.OccurredAtUtc,
            0,
            0,
            item.Description,
            "history",
            false,
            false,
            item.OccurredAtUtc,
            string.Empty,
            item.OccurredAtUtc,
            string.Empty,
            item.ActorUserId)));

        var importedCount = 0;
        if (historyOptions.Value.Enabled)
        {
            var importedQuery = db.LegacyUpdateEvents
                .AsNoTracking()
                .Where(update => update.TenantId == tenantId
                    && update.CaseId == caseId
                    && update.Scope == LegacyUpdateEvent.CaseScope);
            importedCount = await importedQuery.CountAsync(ct);
            var imported = await importedQuery
                .OrderByDescending(update => update.OccurredAtUtc)
                .ThenByDescending(update => update.LegacySequence)
                .Take(fetchCount)
                .ToListAsync(ct);
            timeline.AddRange(imported.Select(update => new LegacyCaseTimelineItem(
                update.Id.ToString(),
                update.CaseId.ToString(),
                update.Action,
                NormalizeLegacyUpdateDescription(update.Description),
                update.OccurredAtUtc,
                1,
                update.LegacySequence,
                NormalizeLegacyUpdateDescription(update.Description),
                "legacy",
                false,
                false,
                update.OccurredAtUtc,
                update.ActorDisplayName ?? string.Empty,
                null,
                update.ActorDisplayName ?? string.Empty,
                null)));
        }

        var selected = timeline
            .OrderByDescending(item => item.SortAt)
            .ThenBy(item => item.SourceRank)
            .ThenByDescending(item => item.LegacySequence)
            .ThenByDescending(item => item.Id, StringComparer.Ordinal)
            .Skip(offset)
            .Take(limit)
            .ToList();
        var updatedByNames = await ResolveLienHistoryUserNamesAsync(
            selected.Where(item => item.UpdatedByUserId.HasValue).Select(item => item.UpdatedByUserId!.Value),
            tenantId,
            ctx.OrgId,
            httpClientFactory,
            httpContext.Request.Headers.Authorization.ToString(),
            identityOptions.Value,
            ct);
        var data = selected
            .Select(item =>
            {
                var actorName = item.UpdatedByUserId.HasValue
                    ? ResolveLienHistoryUserName(item.UpdatedByUserId.Value, updatedByNames, ctx)
                    : null;
                var description = NormalizeCaseCreatedActor(item.Description, item.Action, actorName);
                return new
                {
                    id = item.Id,
                    caseId = item.CaseId,
                    action = item.Action,
                    description,
                    timestamp = FormatLegacyTimestamp(item.SortAt),
                    note = description,
                    category = item.Category,
                    isPinned = item.IsPinned,
                    isEdited = item.IsEdited,
                    created = FormatLegacyTimestamp(item.CreatedAt),
                    createdBy = actorName ?? item.CreatedBy,
                    updated = item.UpdatedAt.HasValue ? FormatLegacyTimestamp(item.UpdatedAt.Value) : string.Empty,
                    updatedBy = actorName ?? item.UpdatedBy,
                };
            })
            .ToList();

        return Results.Ok(new
        {
            isSuccess = true,
            message = "Case updates retrieved successfully.",
            data,
            totalCount = nativeCount + nativeHistoryCount + importedCount,
            page,
            limit,
        });
    }

    private static async Task<IResult> GetLiensUpdatesV3Legacy(
        LegacyLiensUpdatesV3Request req,
        LiensDbContext db,
        IOptions<LegacyUpdateHistoryOptions> historyOptions,
        IOptions<IdentityServiceOptions> identityOptions,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        HttpContext httpContext,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);

        if (!Guid.TryParse(req.CaseId, out var caseId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "Error: No liens updates found.",
            });
        }

        if (!TryGetTimelineWindow(req.Page, req.Limit, out var page, out var limit, out var offset, out var fetchCount))
            return LegacyTimelinePaginationError();

        var statusQuery = db.LienStatusHistories
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.CaseId == caseId);
        var servicingQuery = db.ServicingItems
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId &&
                           item.CaseId == caseId &&
                           item.LienId != null &&
                           item.TaskType != "Lien Update");
        var statusCount = await statusQuery.CountAsync(ct);
        var servicingCount = await servicingQuery.CountAsync(ct);
        var statusHistory = await statusQuery
            .OrderByDescending(item => item.ChangedAtUtc)
            .ThenByDescending(item => item.Id)
            .Take(fetchCount)
            .ToListAsync(ct);
        var servicingItems = await servicingQuery
            .OrderByDescending(item => item.UpdatedAtUtc)
            .ThenByDescending(item => item.Id)
            .Take(fetchCount)
            .ToListAsync(ct);
        var combined = statusHistory.Select(item => new LegacyLienTimelineItem(
                item.Id.ToString(),
                caseId.ToString(),
                item.LienId.ToString(),
                ResolveLienHistoryAction(item.Description),
                item.Description,
                true,
                string.Empty,
                item.ChangedByUserId,
                item.ChangedAtUtc,
                0,
                0))
            .Concat(servicingItems.Select(item => new LegacyLienTimelineItem(
                item.Id.ToString(),
                item.CaseId?.ToString() ?? caseId.ToString(),
                item.LienId!.Value.ToString(),
                item.TaskType,
                string.IsNullOrWhiteSpace(item.Resolution) ? item.Description : item.Resolution,
                false,
                string.Empty,
                item.UpdatedByUserId ?? item.CreatedByUserId,
                item.UpdatedAtUtc,
                0,
                0)))
            .ToList();

        var importedCount = 0;
        if (historyOptions.Value.Enabled)
        {
            var importedQuery = db.LegacyUpdateEvents
                .AsNoTracking()
                .Where(update => update.TenantId == tenantId
                    && update.CaseId == caseId
                    && update.Scope == LegacyUpdateEvent.LienScope);
            importedCount = await importedQuery.CountAsync(ct);
            var imported = await importedQuery
                .OrderByDescending(update => update.OccurredAtUtc)
                .ThenByDescending(update => update.LegacySequence)
                .Take(fetchCount)
                .ToListAsync(ct);
            combined.AddRange(imported.Select(update => new LegacyLienTimelineItem(
                update.Id.ToString(),
                update.CaseId.ToString(),
                update.LienId!.Value.ToString(),
                update.Action,
                NormalizeLegacyUpdateDescription(update.Description),
                false,
                update.ActorDisplayName ?? string.Empty,
                null,
                update.OccurredAtUtc,
                1,
                update.LegacySequence)));
        }

        var totalCount = statusCount + servicingCount + importedCount;
        if (totalCount == 0)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "Error: No liens updates found.",
            });
        }

        var selected = combined
            .OrderByDescending(item => item.SortAt)
            .ThenBy(item => item.SourceRank)
            .ThenByDescending(item => item.LegacySequence)
            .ThenByDescending(item => item.Id, StringComparer.Ordinal)
            .Skip(offset)
            .Take(limit)
            .ToList();
        var updatedByNames = await ResolveLienHistoryUserNamesAsync(
            selected.Where(item => item.UpdatedByUserId.HasValue).Select(item => item.UpdatedByUserId!.Value),
            tenantId,
            ctx.OrgId,
            httpClientFactory,
            httpContext.Request.Headers.Authorization.ToString(),
            identityOptions.Value,
            ct);
        var selectedLienIds = selected
            .Select(item => Guid.TryParse(item.LienId, out var lienId) ? lienId : Guid.Empty)
            .Where(lienId => lienId != Guid.Empty)
            .Distinct()
            .ToList();
        var lienCodes = await db.Liens.AsNoTracking()
            .Where(lien => lien.TenantId == tenantId && selectedLienIds.Contains(lien.Id))
            .ToDictionaryAsync(lien => lien.Id, lien => lien.LienNumber, ct);
        var historyReferenceDescriptions = await LienHistoryDescriptionEnricher.ResolveAsync(
            db,
            tenantId,
            selected.Where(item => item.EnrichReferences).Select(item => item.Description),
            httpClientFactory,
            identityOptions.Value,
            loggerFactory.CreateLogger(nameof(LienHistoryDescriptionEnricher)),
            ct);
        var data = selected
            .Select(i =>
            {
                var description = i.EnrichReferences
                    ? LienHistoryDescriptionEnricher.Enrich(i.Description, historyReferenceDescriptions)
                    : i.Description;
                description = NormalizeLienDetailsDescription(description, i.Action);
                return new
                {
                    id = i.Id,
                    caseId = i.CaseId,
                    lienId = i.LienId,
                    lienCode = Guid.TryParse(i.LienId, out var lienId) && lienCodes.TryGetValue(lienId, out var lienCode)
                        ? lienCode
                        : string.Empty,
                    action = i.Action,
                    description,
                    updatedBy = i.UpdatedByUserId.HasValue
                        ? ResolveLienHistoryUserName(i.UpdatedByUserId.Value, updatedByNames, ctx)
                        : i.UpdatedBy,
                    timestamp = FormatLegacyTimestamp(i.SortAt),
                };
            })
            .ToList();

        return Results.Ok(new
        {
            isSuccess = true,
            message = "Liens updates retrieved successfully.",
            data,
            totalCount,
            page,
            limit,
        });
    }

    private static bool TryGetTimelineWindow(
        int requestedPage,
        int requestedLimit,
        out int page,
        out int limit,
        out int offset,
        out int fetchCount)
    {
        page = requestedPage < 1 ? 1 : requestedPage;
        limit = requestedLimit < 1 ? 10 : requestedLimit;
        var requestedWindow = (long)page * limit;
        if (limit > LegacyTimelineMaximumPageSize || requestedWindow > LegacyTimelineMaximumWindow)
        {
            offset = 0;
            fetchCount = 0;
            return false;
        }

        offset = (page - 1) * limit;
        fetchCount = (int)requestedWindow;
        return true;
    }

    private static IResult LegacyTimelinePaginationError() => Results.BadRequest(new
    {
        isSuccess = false,
        message = $"Error: Pagination is limited to {LegacyTimelineMaximumPageSize} rows per page and the first {LegacyTimelineMaximumWindow:N0} timeline rows.",
    });

    private static string NormalizeLegacyUpdateDescription(string? description) =>
        (description ?? string.Empty).Replace("ÔåÆ", "→", StringComparison.Ordinal);

    private static string NormalizeLienDetailsDescription(string description, string action)
    {
        if (!string.Equals(action, "Liens Details", StringComparison.Ordinal))
            return description;

        return ReplaceLienDetailsValue(ReplaceLienDetailsValue(description, "blank"), "Draft");
    }

    private static string ReplaceLienDetailsValue(string description, string value)
    {
        var normalized = description
            .Replace($": {value} →", ": \"\" →", StringComparison.Ordinal)
            .Replace($"→ {value};", "→ \"\";", StringComparison.Ordinal)
            .Replace($"→ {value}.", "→ \"\".", StringComparison.Ordinal);

        return normalized.EndsWith($"→ {value}", StringComparison.Ordinal)
            ? $"{normalized[..^value.Length]}\"\""
            : normalized;
    }

    private static string NormalizeCaseCreatedActor(string description, string action, string? actorName)
    {
        if (string.IsNullOrWhiteSpace(actorName) ||
            !string.Equals(action, "Case Created", StringComparison.Ordinal))
        {
            return description;
        }

        const string marker = "Created By:";
        var markerIndex = description.LastIndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            var prefix = description.TrimEnd();
            return prefix.Length == 0
                ? $"{marker} {actorName}."
                : $"{prefix} {marker} {actorName}.";
        }

        var suffixIndex = description.IndexOf(". ", markerIndex, StringComparison.Ordinal);
        var suffix = suffixIndex >= 0 ? description[(suffixIndex + 1)..] : string.Empty;
        return $"{description[..markerIndex]}{marker} {actorName}.{suffix}";
    }

    private static string ResolveLienHistoryAction(string description) =>
        description.StartsWith("Lien Created.", StringComparison.Ordinal) ? "Lien Created" :
        description.StartsWith("Lien Deleted.", StringComparison.Ordinal) ? "Lien Deleted" :
        "Liens Details";

    private static async Task<IReadOnlyDictionary<Guid, string>> ResolveLienHistoryUserNamesAsync(
        IEnumerable<Guid> userIds,
        Guid tenantId,
        Guid? organizationId,
        IHttpClientFactory httpClientFactory,
        string authorizationHeader,
        IdentityServiceOptions identityOptions,
        CancellationToken ct)
    {
        var ids = userIds.Distinct().ToHashSet();
        if (ids.Count == 0)
            return new Dictionary<Guid, string>();

        var resolved = new Dictionary<Guid, string>();
        using var publicIdentityClient = httpClientFactory.CreateClient("Identity");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/users");
            if (AuthenticationHeaderValue.TryParse(authorizationHeader, out var authorization))
                request.Headers.Authorization = authorization;

            using var response = await publicIdentityClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                var users = await response.Content.ReadFromJsonAsync<List<IdentityUserResponse>>(cancellationToken: ct)
                    ?? [];
                foreach (var user in users.Where(user => ids.Contains(user.Id)))
                {
                    var display = FirstNonEmpty(
                        JoinPersonName(user.FirstName, user.LastName),
                        user.Email);
                    if (!string.IsNullOrWhiteSpace(display))
                        resolved[user.Id] = display;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException ||
                                   ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            // Fall through to the authenticated per-user lookup.
        }

        var unresolvedIds = ids.Where(id => !resolved.ContainsKey(id)).ToList();
        foreach (var userId in unresolvedIds)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"api/users/{userId:D}");
                if (AuthenticationHeaderValue.TryParse(authorizationHeader, out var authorization))
                    request.Headers.Authorization = authorization;

                using var response = await publicIdentityClient.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                    continue;

                var user = await response.Content.ReadFromJsonAsync<IdentityUserResponse>(cancellationToken: ct);
                var display = FirstNonEmpty(
                    JoinPersonName(user?.FirstName, user?.LastName),
                    user?.Email);
                if (!string.IsNullOrWhiteSpace(display))
                    resolved[userId] = display;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException ||
                                       ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                // Fall through to the trusted per-user display endpoint.
            }
        }

        unresolvedIds = ids.Where(id => !resolved.ContainsKey(id)).ToList();
        if (unresolvedIds.Count == 0 ||
            !Uri.TryCreate(identityOptions.BaseUrl?.TrimEnd('/') + "/", UriKind.Absolute, out var identityBaseUri))
        {
            return resolved;
        }

        using var identityClient = httpClientFactory.CreateClient("IdentityService");
        identityClient.BaseAddress = identityBaseUri;
        identityClient.Timeout = TimeSpan.FromSeconds(
            identityOptions.TimeoutSeconds > 0 ? identityOptions.TimeoutSeconds : 5);
        if (!string.IsNullOrWhiteSpace(identityOptions.ProvisioningToken))
        {
            identityClient.DefaultRequestHeaders.TryAddWithoutValidation(
                "X-Provisioning-Token",
                identityOptions.ProvisioningToken);
        }
        else if (!string.IsNullOrWhiteSpace(identityOptions.AuthHeaderName) &&
                 !string.IsNullOrWhiteSpace(identityOptions.AuthHeaderValue))
        {
            identityClient.DefaultRequestHeaders.TryAddWithoutValidation(
                identityOptions.AuthHeaderName,
                identityOptions.AuthHeaderValue);
        }

        foreach (var userId in unresolvedIds)
        {
            try
            {
                var path = $"api/internal/users/{userId:D}/display?tenantId={tenantId:D}";
                if (organizationId.HasValue && organizationId.Value != Guid.Empty)
                    path += $"&organizationId={organizationId.Value:D}";

                using var response = await identityClient.GetAsync(path, ct);
                if (!response.IsSuccessStatusCode)
                    continue;

                var user = await response.Content.ReadFromJsonAsync<IdentityUserDisplayResponse>(
                    cancellationToken: ct);
                if (user?.Found != true)
                    continue;

                var display = FirstNonEmpty(
                    user.DisplayName,
                    JoinPersonName(user.FirstName, user.LastName),
                    user.Email);
                if (!string.IsNullOrWhiteSpace(display))
                    resolved[userId] = display;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException ||
                                       ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                // Leave the actor unresolved; the response uses a human-readable placeholder.
            }
        }

        return resolved;
    }

    private static string? JoinPersonName(string? firstName, string? lastName) =>
        FirstNonEmpty(string.Join(" ", new[] { firstName, lastName }
            .Where(value => !string.IsNullOrWhiteSpace(value))).Trim());

    private static string ResolveLienHistoryUserName(
        Guid userId,
        IReadOnlyDictionary<Guid, string> userNames,
        ICurrentRequestContext ctx)
    {
        if (userNames.TryGetValue(userId, out var resolvedName) &&
            !string.IsNullOrWhiteSpace(resolvedName))
        {
            return resolvedName;
        }

        if (ctx.UserId == userId)
            return FirstNonEmpty(ctx.Name, ctx.Email, "Unknown user")!;

        return "Unknown user";
    }

    private static async Task<IResult> GetCasesV3Legacy(
        LegacyCaseV3FilterRequest filter,
        ICaseService caseService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);

        var page = filter.page < 1 ? 1 : filter.page;
        var limit = filter.limit < 1 ? 20 : filter.limit;
        var result = await caseService.SearchV3Async(
            tenantId: tenantId,
            keyword: filter.keyword,
            statusId: filter.statusId,
            page: page,
            limit: limit,
            sortBy: filter.sortBy,
            sortDirection: filter.sortDirection,
            lawFirmOrgId: null,
            accidentTypeId: filter.accidentTypeId,
            caseManagerId: filter.caseManagerId,
            lawFirmIds: filter.lawFirmId,
            ct: ct);

        if (result.TotalCount == 0)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "No cases found.",
            });
        }

        return Results.Ok(new
        {
            isSuccess = true,
            message = "Case List.",
            data = result.Items,
            page = result.Page,
            limit = result.PageSize,
            totalCount = result.TotalCount,
        });
    }

    private static async Task<IResult> CreateCaseLegacy(
        LegacyCreateCaseRequest request,
        ICaseService caseService,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var orgId = RequireOrgId(ctx);
        var userId = RequireUserId(ctx);
        var accidentTypeId = FirstNonEmpty(request.accidentTypeId, LooksLikeGuid(request.caseType) ? request.caseType : null);
        var accidentType = await ResolveLegacyAccidentTypeAsync(db, accidentTypeId, request.caseType, ct);

        var mappedRequest = new CreateCaseRequest
        {
            CaseNumber = request.code ?? string.Empty,
            ClientFirstName = request.firstname ?? string.Empty,
            ClientLastName = request.lastname ?? string.Empty,
            ExternalReference = FirstNonEmpty(request.externalReference, request.externalCaseId),
            ClientDob = ParseLegacyDate(request.dob),
            ClientPhone = FirstNonEmpty(request.clientPhone, request.phone),
            ClientEmail = FirstNonEmpty(request.clientEmail, request.email),
            ClientAddress = BuildAddress(request.address, request.city, request.state, request.zipcode),
            DateOfIncident = ParseLegacyDate(request.dateOfLoss),
            PolicyNumber = request.policyNumber,
            ClaimNumber = request.claimNumber,
            Notes = FirstNonEmpty(request.notes, request.note),
            LawFirmId = request.lawFirmId,
            AccidentTypeId = accidentTypeId,
            CaseType = accidentType,
            StateOfIncident = FirstNonEmpty(request.stateOfIncident, request.accidentStateId),
            CaseManagerId = request.caseManagerId,
            MinorComp = request.minorComp,
            StatusLabel = ResolveLegacyCaseStatusLabel(request.caseStatusId),
        };

        var result = await caseService.CreateAsync(tenantId, orgId, userId, mappedRequest, ct);

        var normalizedCaseStatus = !string.IsNullOrWhiteSpace(request.caseStatusId)
            ? NormalizeLegacyCaseStatus(request.caseStatusId)
            : null;

        if (!string.IsNullOrWhiteSpace(normalizedCaseStatus))
        {
            var updateRequest = new UpdateCaseRequest
            {
                ClientFirstName = result.ClientFirstName,
                ClientLastName = result.ClientLastName,
                ExternalReference = result.ExternalReference,
                Title = result.Title,
                ClientDob = result.ClientDob,
                ClientPhone = result.ClientPhone,
                ClientEmail = result.ClientEmail,
                ClientAddress = result.ClientAddress,
                DateOfIncident = result.DateOfIncident,
                InsuranceCarrier = result.InsuranceCarrier,
                PolicyNumber = result.PolicyNumber,
                ClaimNumber = result.ClaimNumber,
                Description = result.Description,
                Notes = result.Notes,
                Status = normalizedCaseStatus,
                DemandAmount = result.DemandAmount,
                SettlementAmount = result.SettlementAmount,
                Sex = result.Sex,
                CaseType = result.CaseType,
                CurrentMedicalStatus = result.CurrentMedicalStatus,
                StateOfIncident = result.StateOfIncident,
                TrackingFollowUpDate = result.TrackingFollowUpDate,
                LeadId = result.LeadId,
                LawFirmId = result.LawFirmId,
                AccidentTypeId = result.AccidentTypeId,
                CaseManagerId = result.CaseManagerId,
                StatusLabel = ResolveLegacyCaseStatusLabel(request.caseStatusId),
            };

            result = await caseService.UpdateAsync(tenantId, result.Id, userId, updateRequest, ct);
        }

        return Results.Ok(new
        {
            isSuccess = true,
            message = "Successfully Created.",
            data = new Dictionary<string, string>
            {
                ["id"] = result.Id.ToString(),
            },
        });
    }

    private static async Task<IResult> UpdateCaseLegacy(
        string id,
        LegacyUpdateCaseRequest request,
        ICaseService caseService,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var caseId))
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = $"Case '{id}' not found.",
            });
        }

        var tenantId = RequireTenantId(ctx);
        var userId = RequireUserId(ctx);

        var existing = await caseService.GetByIdAsync(tenantId, caseId, ct);
        if (existing is null)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = $"Case '{id}' not found.",
            });
        }

        var mappedRequest = new UpdateCaseRequest
        {
            ClientFirstName = request.firstname ?? string.Empty,
            ClientLastName = request.lastname ?? string.Empty,
            ExternalReference = request.externalCaseId,
            ClientDob = ParseLegacyDate(request.dob),
            ClientPhone = FirstNonEmpty(request.clientPhone, request.phone),
            ClientEmail = FirstNonEmpty(request.clientEmail, request.email),
            ClientAddress = BuildAddress(request.address, request.city, request.state, request.zipcode),
            DateOfIncident = ParseLegacyDate(request.dateOfLoss),
            Notes = request.note,
            LawFirmId = request.lawFirmId,
        };

        var isNoChanges =
            string.Equals(existing.ClientFirstName, mappedRequest.ClientFirstName, StringComparison.Ordinal) &&
            string.Equals(existing.ClientLastName, mappedRequest.ClientLastName, StringComparison.Ordinal) &&
            string.Equals(existing.ExternalReference, mappedRequest.ExternalReference, StringComparison.Ordinal) &&
            existing.ClientDob == mappedRequest.ClientDob &&
            string.Equals(existing.ClientPhone, mappedRequest.ClientPhone, StringComparison.Ordinal) &&
            string.Equals(existing.ClientEmail, mappedRequest.ClientEmail, StringComparison.Ordinal) &&
            string.Equals(existing.ClientAddress, mappedRequest.ClientAddress, StringComparison.Ordinal) &&
            existing.DateOfIncident == mappedRequest.DateOfIncident &&
            string.Equals(existing.Notes, mappedRequest.Notes, StringComparison.Ordinal) &&
            string.Equals(existing.LawFirmId, mappedRequest.LawFirmId, StringComparison.OrdinalIgnoreCase);

        if (isNoChanges)
        {
            return Results.Ok(new
            {
                isSuccess = true,
                message = "No changes detected.",
            });
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var updated = await caseService.UpdateAsync(tenantId, caseId, userId, mappedRequest, ct);
        if (request.lawFirmId is not null)
        {
            await LawFirmChangeHistory.RecordAsync(
                db,
                tenantId,
                caseId,
                existing.LawFirmId,
                updated.LawFirmId ?? request.lawFirmId,
                switchedDate: null,
                userId,
                ctx.Name ?? ctx.Email ?? userId.ToString(),
                ct,
                existing.PendingLawFirmId,
                existing.SwitchedDate);
        }

        await transaction.CommitAsync(ct);

        return Results.Ok(new
        {
            isSuccess = true,
            message = "Successfully Updated.",
        });
    }

    private static async Task<IResult> UpdateCase(
        Guid id,
        UpdateCaseRequest request,
        ICaseService caseService,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId = RequireUserId(ctx);
        var existing = await caseService.GetByIdAsync(tenantId, id, ct);
        var switchedDate = request.SwitchedDate;
        var requestedLawFirmId = LawFirmChangeHistory.IsFutureSwitch(switchedDate)
            ? FirstNonEmpty(request.PendingLawFirmId, request.LawFirmId)
            : request.LawFirmId;
        var repeatsScheduledSwitch = existing is not null &&
            LawFirmChangeHistory.IsFutureSwitch(switchedDate) &&
            await LawFirmChangeHistory.IsSamePendingSwitchAsync(
                db,
                tenantId,
                existing.PendingLawFirmId,
                existing.SwitchedDate,
                requestedLawFirmId,
                switchedDate,
                ct);
        if (existing is not null && requestedLawFirmId is not null &&
            LawFirmChangeHistory.IsFutureSwitch(switchedDate))
        {
            request.LawFirmId = existing.LawFirmId;
            request.PendingLawFirmId = requestedLawFirmId;
            if (repeatsScheduledSwitch)
                request.SwitchedDate = existing.SwitchedDate;
        }
        else if (request.LawFirmId is not null)
        {
            request.PendingLawFirmId = string.Empty;
            request.SwitchedDate = string.Empty;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var result = await caseService.UpdateAsync(tenantId, id, userId, request, ct);
        if (existing is not null && requestedLawFirmId is not null)
        {
            await LawFirmChangeHistory.RecordAsync(
                db,
                tenantId,
                id,
                existing.LawFirmId,
                requestedLawFirmId,
                switchedDate,
                userId,
                ctx.Name ?? ctx.Email ?? userId.ToString(),
                ct,
                existing.PendingLawFirmId,
                existing.SwitchedDate);
        }
        await transaction.CommitAsync(ct);
        return Results.Ok(result);
    }

    private static string NormalizeLegacyBatchReassignContactType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Trim();
        if (normalized is "1" or "2" or "3" or "4" or "5")
            return normalized;

        var canonical = string.Concat(normalized.Where(char.IsLetterOrDigit)).ToLowerInvariant();

        return canonical switch
        {
            "lawfirm" => "1",
            "provider" or "medicalprovider" => "2",
            "fundingcompany" or "lienholder" => "3",
            "medicalfacility" or "facility" => "4",
            "lead" or "leads" => "5",
            _ => normalized,
        };
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static bool LooksLikeGuid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Guid.TryParse(value, out _);

    private static async Task<string?> ResolveLegacyAccidentTypeAsync(
        LiensDbContext db,
        string? accidentTypeId,
        string? caseType,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(caseType) && !LooksLikeGuid(caseType))
            return caseType.Trim();

        if (Guid.TryParse(accidentTypeId, out var parsedAccidentTypeId))
        {
            var lookup = await db.LookupValues.AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.Id == parsedAccidentTypeId && x.Category == LookupCategory.AccidentType,
                    ct);
            if (lookup is not null)
                return FirstNonEmpty(lookup.Name, lookup.Code);
        }

        return FirstNonEmpty(accidentTypeId, caseType);
    }

    // ── Partial-update handlers ───────────────────────────────────────────────

    private sealed class PersonalUpdateRequest
    {
        public Guid    CaseId        { get; init; }
        public string  FirstName     { get; init; } = string.Empty;
        public string  LastName      { get; init; } = string.Empty;
        public string? Sex           { get; init; }
        public string? Dob           { get; init; }
        public string? Phone         { get; init; }
        public string? Email         { get; init; }
        public string? Address       { get; init; }
        public string? City          { get; init; }
        public string? State         { get; init; }
        public string? Zipcode       { get; init; }
    }

    private sealed class PrimaryUpdateRequest
    {
        public Guid     CaseId      { get; init; }
        public string?  Title       { get; init; }
        public string?  Status      { get; init; }
        public string?  DateOfLoss  { get; init; }
        public string?  InsuranceCarrier { get; init; }
        public string?  PolicyNumber    { get; init; }
        public string?  ClaimNumber     { get; init; }
    }

    private sealed class CaseDetailsUpdateRequest
    {
        public Guid     CaseId           { get; init; }
        public string?  CurrentStatus    { get; init; }
        public string?  CurrentMedicalStatus { get; init; }
        public string?  CaseType         { get; init; }
        public string?  StateOfIncident  { get; init; }
        public string?  TrackingFollowUp { get; init; }
        public string?  DateOfLoss       { get; init; }
        public string?  LeadId           { get; init; }
        public string?  ShareCase        { get; init; }
        public string?  MinorComp        { get; init; }
        public string?  CaseDropped      { get; init; }
        public string?  ChildSupportLiens { get; init; }
        public string?  IsUccFiled       { get; init; }
        public string?  Description      { get; init; }
        public string?  Notes            { get; init; }
        public decimal? DemandAmount     { get; init; }
        public decimal? SettlementAmount { get; init; }
    }

    private static async Task<IResult> UpdatePersonalInfo(
        PersonalUpdateRequest req,
        ICaseService caseService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId   = RequireUserId(ctx);
        var existing = await caseService.GetByIdAsync(tenantId, req.CaseId, ct);
        if (existing is null)
            return Results.NotFound(new { isSuccess = false, message = "Case not found." });

        DateOnly? dob = DateOnly.TryParse(req.Dob, out var d) ? d : existing.ClientDob;
        var streetAddress = req.Address ?? existing.ClientStreetAddress;
        var city = req.City ?? existing.ClientCity;
        var state = req.State ?? existing.ClientState;
        var zipcode = req.Zipcode ?? existing.ClientZipcode;
        var addressChanged = req.Address is not null || req.City is not null ||
            req.State is not null || req.Zipcode is not null;
        var stateAndZip = string.Join(' ', new[] { state, zipcode }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        var address = addressChanged
            ? string.Join(", ", new[] { streetAddress, city, stateAndZip }
                .Where(value => !string.IsNullOrWhiteSpace(value)))
            : existing.ClientAddress;

        var request = new UpdateCaseRequest
        {
            ClientFirstName  = req.FirstName,
            ClientLastName   = req.LastName,
            ClientDob        = dob,
            ClientPhone      = req.Phone ?? existing.ClientPhone,
            ClientEmail      = req.Email ?? existing.ClientEmail,
            ClientAddress    = address,
            ClientStreetAddress = streetAddress,
            ClientCity       = city,
            ClientState      = state,
            ClientZipcode    = zipcode,
            ExternalReference= existing.ExternalReference,
            Title            = existing.Title,
            DateOfIncident   = existing.DateOfIncident,
            Status           = existing.Status,
            InsuranceCarrier = existing.InsuranceCarrier,
            PolicyNumber     = existing.PolicyNumber,
            ClaimNumber      = existing.ClaimNumber,
            Description      = existing.Description,
            Notes            = existing.Notes,
            DemandAmount     = existing.DemandAmount,
            SettlementAmount = existing.SettlementAmount,
            Sex              = req.Sex ?? existing.Sex,
            CaseType         = existing.CaseType,
            CurrentMedicalStatus = existing.CurrentMedicalStatus,
            StateOfIncident  = existing.StateOfIncident,
            TrackingFollowUpDate = existing.TrackingFollowUpDate,
            LeadId           = existing.LeadId,
        };
        await caseService.UpdateAsync(tenantId, req.CaseId, userId, request, ct);
        return Results.Ok(new { isSuccess = true, message = "Successfully Updated." });
    }

    private static async Task<IResult> UpdatePrimaryInfo(
        PrimaryUpdateRequest req,
        ICaseService caseService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId   = RequireUserId(ctx);
        var existing = await caseService.GetByIdAsync(tenantId, req.CaseId, ct);
        if (existing is null)
            return Results.NotFound(new { isSuccess = false, message = "Case not found." });

        DateOnly? dateOfLoss = DateOnly.TryParse(req.DateOfLoss, out var dl) ? dl : existing.DateOfIncident;
        var request = new UpdateCaseRequest
        {
            ClientFirstName  = existing.ClientFirstName,
            ClientLastName   = existing.ClientLastName,
            ClientDob        = existing.ClientDob,
            ClientPhone      = existing.ClientPhone,
            ClientEmail      = existing.ClientEmail,
            ClientAddress    = existing.ClientAddress,
            ExternalReference= existing.ExternalReference,
            Title            = req.Title ?? existing.Title,
            DateOfIncident   = dateOfLoss,
            Status           = req.Status ?? existing.Status,
            InsuranceCarrier = req.InsuranceCarrier ?? existing.InsuranceCarrier,
            PolicyNumber     = req.PolicyNumber ?? existing.PolicyNumber,
            ClaimNumber      = req.ClaimNumber ?? existing.ClaimNumber,
            Description      = existing.Description,
            Notes            = existing.Notes,
            DemandAmount     = existing.DemandAmount,
            SettlementAmount = existing.SettlementAmount,
        };
        await caseService.UpdateAsync(tenantId, req.CaseId, userId, request, ct);
        return Results.Ok(new { isSuccess = true, message = "Successfully Updated." });
    }

    private static async Task<IResult> UpdateCaseDetails(
        CaseDetailsUpdateRequest req,
        ICaseService caseService,
        ILienCaseNoteService caseNoteService,
        IUnitOfWork unitOfWork,
        IAuditPublisher auditPublisher,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId   = RequireUserId(ctx);
        var existing = await caseService.GetByIdAsync(tenantId, req.CaseId, ct);
        if (existing is null)
            return Results.NotFound(new { isSuccess = false, message = "Case not found." });

        DateOnly? dateOfLoss = DateOnly.TryParse(req.DateOfLoss, out var dl) ? dl : existing.DateOfIncident;
        DateOnly? trackingFollowUp = DateOnly.TryParse(req.TrackingFollowUp, out var tfu) ? tfu : existing.TrackingFollowUpDate;
        var normalizedStatus = !string.IsNullOrWhiteSpace(req.CurrentStatus)
            ? NormalizeLegacyCaseStatus(req.CurrentStatus)
            : existing.Status;
        var detailsNote = req.Notes?.Trim();
        var detailsNoteChanged =
            req.Notes is not null &&
            !string.Equals(
                existing.Notes?.Trim() ?? string.Empty,
                detailsNote ?? string.Empty,
                StringComparison.Ordinal);
        var request = new UpdateCaseRequest
        {
            ClientFirstName  = existing.ClientFirstName,
            ClientLastName   = existing.ClientLastName,
            ClientDob        = existing.ClientDob,
            ClientPhone      = existing.ClientPhone,
            ClientEmail      = existing.ClientEmail,
            ClientAddress    = existing.ClientAddress,
            ExternalReference= existing.ExternalReference,
            Title            = existing.Title,
            DateOfIncident   = dateOfLoss,
            Status           = normalizedStatus,
            InsuranceCarrier = existing.InsuranceCarrier,
            PolicyNumber     = existing.PolicyNumber,
            ClaimNumber      = existing.ClaimNumber,
            Description      = req.Description ?? existing.Description,
            Notes            = req.Notes ?? existing.Notes,
            DemandAmount     = req.DemandAmount ?? existing.DemandAmount,
            SettlementAmount = req.SettlementAmount ?? existing.SettlementAmount,
            Sex              = existing.Sex,
            CaseType         = req.CaseType ?? existing.CaseType,
            CurrentMedicalStatus = req.CurrentMedicalStatus ?? existing.CurrentMedicalStatus,
            StateOfIncident  = req.StateOfIncident ?? existing.StateOfIncident,
            TrackingFollowUpDate = trackingFollowUp,
            LeadId           = req.LeadId ?? existing.LeadId,
            ShareCase        = req.ShareCase,
            MinorComp        = req.MinorComp,
            CaseDropped      = req.CaseDropped,
            ChildSupportLiens = req.ChildSupportLiens,
            IsUccFiled       = req.IsUccFiled,
            StatusLabel       = ResolveLegacyCaseStatusLabel(req.CurrentStatus),
        };
        using var auditBuffer = auditPublisher.BeginBuffer();
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        try
        {
            await caseService.UpdateAsync(tenantId, req.CaseId, userId, request, ct);

            if (detailsNoteChanged && !string.IsNullOrWhiteSpace(detailsNote))
            {
                await caseNoteService.CreateNoteAsync(
                    tenantId,
                    req.CaseId,
                    userId,
                    new CreateCaseNoteRequest
                    {
                        Content = detailsNote!,
                        Category = CaseNoteCategory.General,
                        CreatedByName = ctx.Name ?? ctx.Email ?? userId.ToString(),
                    },
                    ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        auditBuffer.Commit();

        return Results.Ok(new { isSuccess = true, message = "Successfully Updated." });
    }

    private static string? BuildCaseUpdateSummary(
        CaseResponse existing,
        CaseDetailsUpdateRequest req,
        string normalizedStatus,
        DateOnly? dateOfLoss,
        DateOnly? trackingFollowUp,
        bool detailsNoteChanged,
        string? detailsNote)
    {
        var changes = new List<string>();

        if (!AreLegacyCaseStatusesEquivalent(existing.Status, normalizedStatus))
            changes.Add($"status changed to {ResolveLegacyDisplayStatus(normalizedStatus)}");

        if (!string.Equals(existing.CurrentMedicalStatus ?? string.Empty, req.CurrentMedicalStatus ?? string.Empty, StringComparison.Ordinal))
            changes.Add($"medical status changed to {(string.IsNullOrWhiteSpace(req.CurrentMedicalStatus) ? "blank" : req.CurrentMedicalStatus)}");

        if (!string.Equals(existing.CaseType ?? string.Empty, req.CaseType ?? string.Empty, StringComparison.Ordinal))
            changes.Add($"case type changed to {(string.IsNullOrWhiteSpace(req.CaseType) ? "blank" : req.CaseType)}");

        if (!string.Equals(existing.StateOfIncident ?? string.Empty, req.StateOfIncident ?? string.Empty, StringComparison.Ordinal))
            changes.Add($"state of incident changed to {(string.IsNullOrWhiteSpace(req.StateOfIncident) ? "blank" : req.StateOfIncident)}");

        if (existing.DateOfIncident != dateOfLoss)
            changes.Add($"date of loss changed to {(dateOfLoss.HasValue ? dateOfLoss.Value.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) : "blank")}");

        if (existing.TrackingFollowUpDate != trackingFollowUp)
            changes.Add($"tracking follow up changed to {(trackingFollowUp.HasValue ? trackingFollowUp.Value.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) : "blank")}");

        if (!string.Equals(existing.Description ?? string.Empty, req.Description ?? existing.Description ?? string.Empty, StringComparison.Ordinal))
            changes.Add("description updated");

        if (!string.Equals(existing.LeadId ?? string.Empty, req.LeadId ?? existing.LeadId ?? string.Empty, StringComparison.Ordinal))
            changes.Add("lead updated");

        if (!AreCaseFlagsEquivalent(existing.ShareCase, req.ShareCase))
            changes.Add($"share case changed to {NormalizeCaseFlagForDisplay(req.ShareCase)}");

        if (!AreCaseFlagsEquivalent(existing.MinorComp, req.MinorComp))
            changes.Add($"minor comp changed to {NormalizeCaseFlagForDisplay(req.MinorComp)}");

        if (!AreCaseFlagsEquivalent(existing.CaseDropped, req.CaseDropped))
            changes.Add($"case dropped changed to {NormalizeCaseFlagForDisplay(req.CaseDropped)}");

        if (!AreCaseFlagsEquivalent(existing.ChildSupportLiens, req.ChildSupportLiens))
            changes.Add($"child support liens changed to {NormalizeCaseFlagForDisplay(req.ChildSupportLiens)}");

        if (!AreCaseFlagsEquivalent(existing.IsUccFiled, req.IsUccFiled))
            changes.Add($"ucc filed changed to {NormalizeCaseFlagForDisplay(req.IsUccFiled)}");

        var caseTrackingNoteChange = detailsNoteChanged
            ? DescribeCaseTrackingNoteChange(detailsNote)
            : null;
        if (caseTrackingNoteChange is not null)
            changes.Add(caseTrackingNoteChange);

        if (changes.Count == 0)
            return null;

        if (changes.Count == 1 && caseTrackingNoteChange is not null)
            return caseTrackingNoteChange;

        return $"Case updated: {string.Join("; ", changes)}.";
    }

    private static bool AreLegacyCaseStatusesEquivalent(string existingStatus, string normalizedStatus) =>
        string.Equals(
            NormalizeLegacyCaseStatus(existingStatus),
            normalizedStatus,
            StringComparison.Ordinal);

    private static string DescribeCaseTrackingNoteChange(string? detailsNote) =>
        string.IsNullOrWhiteSpace(detailsNote)
            ? LegacyCaseUpdateCompatibility.CaseTrackingNoteUpdateDescription
            : $"{LegacyCaseUpdateCompatibility.CaseTrackingNoteUpdateDescription}: {detailsNote}";

    private static string ResolveLegacyDisplayStatus(string status) => status switch
    {
        CaseStatus.PreDemand => "Pre-Demand",
        CaseStatus.DemandSent => "Demand Sent",
        CaseStatus.InNegotiation => "In Negotiation",
        CaseStatus.CaseSettled => "Case Settled",
        CaseStatus.Closed => "Closed",
        _ => status,
    };

    private static bool AreCaseFlagsEquivalent(string? existing, string? requested) =>
        requested is null || string.Equals(
            NormalizeCaseFlagForDisplay(existing),
            NormalizeCaseFlagForDisplay(requested),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeCaseFlagForDisplay(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "blank";

        return value.Trim().ToUpperInvariant() switch
        {
            "TRUE" or "YES" or "Y" => "Yes",
            "FALSE" or "NO" or "N" => "No",
            _ => value.Trim(),
        };
    }

    // ── Linked-entity filter stub ─────────────────────────────────────────────
    // The v2 Case entity does not carry direct FK references to contacts (law
    // firm, medical provider, funding company, case manager).  These routes are
    // stubs that return an empty paginated result until the data model is
    // extended with the appropriate FK columns.
    private static Task<IResult> GetCasesByLinkedEntity(
        ICurrentRequestContext _ctx,
        CancellationToken _ct = default)
    {
        return Task.FromResult<IResult>(Results.Ok(new PaginatedResult<CaseResponse>()));
    }

    // ── Audit log stubs ───────────────────────────────────────────────────────
    private static Task<IResult> GetCaseAuditLog(
        Guid caseId,
        ICurrentRequestContext _ctx,
        CancellationToken _ct = default)
        => Task.FromResult<IResult>(Results.Ok(Array.Empty<object>()));

    private static Task<IResult> GetLiensAuditLog(
        Guid caseId,
        ICurrentRequestContext _ctx,
        CancellationToken _ct = default)
        => Task.FromResult<IResult>(Results.Ok(Array.Empty<object>()));

    // ── Liens list from case context ──────────────────────────────────────────
    private sealed class LiensFilterRequest
    {
        public int    Page    { get; init; } = 1;
        public int    Limit   { get; init; } = 20;
        public string? Keyword { get; init; }
        public string? Status { get; init; }
        public Guid?   CaseId { get; init; }
    }

    private static async Task<IResult> ListLiensByCaseContext(
        LiensFilterRequest request,
        ILienService lienService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var result = await lienService.SearchAsync(
            tenantId, request.Keyword, request.Status, null,
            request.CaseId, null, request.Page, request.Limit, ct);
        return Results.Ok(ToLegacyCaseLienResponse(MapBuyingLienStatuses(result)));
    }

    private static async Task<IResult> ListLiensByCaseId(
        Guid caseId,
        LiensFilterRequest request,
        ILienService lienService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var result = await lienService.SearchAsync(
            tenantId, request.Keyword, request.Status, null,
            caseId, null, request.Page, request.Limit, ct);
        return Results.Ok(ToLegacyCaseLienResponse(MapBuyingLienStatuses(result)));
    }

    private static async Task<IResult> SearchLiensV3(
        LiensFilterRequest request,
        ILienService lienService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var result = await lienService.SearchAsync(
            tenantId, request.Keyword, request.Status, null,
            request.CaseId, null, request.Page, request.Limit, ct);
        return Results.Ok(ToLegacyCaseLienResponse(MapBuyingLienStatuses(result)));
    }

    private static async Task<IResult> GetLiensDetailsByCaseId(
        Guid caseId,
        ILienService lienService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var result = await lienService.SearchAsync(
            tenantId, null, null, null,
            caseId, null, page: 1, pageSize: 500, ct);
        return Results.Ok(ToLegacyCaseLienResponse(MapBuyingLienStatuses(result)));
    }

    private static PaginatedResult<LienResponse> MapBuyingLienStatuses(PaginatedResult<LienResponse> result)
    {
        var items = result.Items
            .Select(MapBuyingLienStatus)
            .Where(item => !string.Equals(item.StatusLabel, "Rejected", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new PaginatedResult<LienResponse>
        {
            Items = items,
            Page = result.Page,
            PageSize = result.PageSize,
            TotalCount = items.Count,
        };
    }

    private static LienResponse MapBuyingLienStatus(LienResponse lien)
    {
        var buyingStatus = string.IsNullOrWhiteSpace(lien.StatusLabel) ? lien.Status : lien.StatusLabel;

        return new LienResponse
        {
            Id = lien.Id,
            LienNumber = lien.LienNumber,
            ExternalReference = lien.ExternalReference,
            LienType = lien.LienType,
            Status = buyingStatus,
            StatusLabel = buyingStatus,
            CaseId = lien.CaseId,
            FacilityId = lien.FacilityId,
            OriginalAmount = lien.OriginalAmount,
            CurrentBalance = lien.CurrentBalance,
            OfferPrice = lien.OfferPrice,
            PurchasePrice = lien.PurchasePrice,
            PayoffAmount = lien.PayoffAmount,
            Jurisdiction = lien.Jurisdiction,
            IsConfidential = lien.IsConfidential,
            SubjectFirstName = lien.SubjectFirstName,
            SubjectLastName = lien.SubjectLastName,
            SubjectDisplayName = lien.SubjectDisplayName,
            Plaintiff = lien.Plaintiff,
            LawFirm = lien.LawFirm,
            MedicalFacility = lien.MedicalFacility,
            CaseManager = lien.CaseManager,
            OrgId = lien.OrgId,
            SellingOrgId = lien.SellingOrgId,
            BuyingOrgId = lien.BuyingOrgId,
            HoldingOrgId = lien.HoldingOrgId,
            IncidentDate = lien.IncidentDate,
            PurchaseDate = lien.PurchaseDate,
            InitialServiceDate = lien.InitialServiceDate,
            EndServiceDate = lien.EndServiceDate,
            TotalPurchase = lien.TotalPurchase,
            TotalBilling = lien.TotalBilling,
            IsBulk = lien.IsBulk,
            IsServicing = lien.IsServicing,
            ImportedCreatedByName = lien.ImportedCreatedByName,
            Description = lien.Description,
            Notes = lien.Notes,
            OpenedAtUtc = lien.OpenedAtUtc,
            ClosedAtUtc = lien.ClosedAtUtc,
            CreatedAtUtc = lien.CreatedAtUtc,
            UpdatedAtUtc = lien.UpdatedAtUtc,
        };
    }

    private static object ToLegacyCaseLienResponse(PaginatedResult<LienResponse> result)
    {
        return new
        {
            items = result.Items.Select(item => new
            {
                id = item.Id,
                lienNumber = item.LienNumber,
                externalReference = item.ExternalReference,
                lienType = item.LienType,
                status = item.Status,
                statusLabel = item.StatusLabel,
                caseId = item.CaseId,
                facilityId = item.FacilityId,
                originalAmount = item.OriginalAmount,
                currentBalance = item.CurrentBalance,
                offerPrice = item.OfferPrice,
                purchasePrice = item.PurchasePrice,
                payoffAmount = item.PayoffAmount,
                jurisdiction = item.Jurisdiction,
                isConfidential = item.IsConfidential,
                subjectFirstName = item.SubjectFirstName,
                subjectLastName = item.SubjectLastName,
                subjectDisplayName = item.SubjectDisplayName,
                plaintiff = item.Plaintiff,
                lawFirm = item.LawFirm,
                medicalFacility = item.MedicalFacility,
                caseManager = item.CaseManager,
                orgId = item.OrgId,
                sellingOrgId = item.SellingOrgId,
                buyingOrgId = item.BuyingOrgId,
                holdingOrgId = item.HoldingOrgId,
                incidentDate = item.IncidentDate,
                purchaseDate = item.PurchaseDate,
                initialServiceDate = item.InitialServiceDate,
                endServiceDate = item.EndServiceDate,
                totalPurchase = item.TotalPurchase,
                totalBilling = item.TotalBilling,
                isBulk = item.IsBulk,
                isServicing = item.IsServicing,
                description = item.Description,
                openedAtUtc = item.OpenedAtUtc,
                closedAtUtc = item.ClosedAtUtc,
                createdAtUtc = item.CreatedAtUtc,
                updatedAtUtc = item.UpdatedAtUtc,
            }).ToList(),
            page = result.Page,
            pageSize = result.PageSize,
            totalCount = result.TotalCount,
        };
    }

    private static async Task<IResult> DeleteLien(
        Guid liensId,
        ILienService lienService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId   = RequireUserId(ctx);
        await lienService.DeleteAsync(tenantId, liensId, userId, ct);
        return Results.Ok(new { isSuccess = true, message = "Successfully Deleted." });
    }

    // ── Manual medical codes ─────────────────────────────────────────────────
    private sealed class ManualMedicalCodeRequest
    {
        public string? id { get; init; }
        public string? code { get; init; }
        public string? description { get; init; }
        public string? facilityType { get; init; }
        public decimal cost { get; init; }
        public decimal copay { get; init; }
        public decimal facilityTotal { get; init; }
        public decimal physicianTotal { get; init; }
        public decimal total { get; init; }
    }

    private static async Task<IResult> CreateManualMedicalCode(
        ManualMedicalCodeRequest req,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId = RequireUserId(ctx);

        if (string.IsNullOrWhiteSpace(req.code))
        {
            return Results.BadRequest(new
            {
                isSuccess = false,
                message = "Code is required.",
            });
        }

        try
        {
            var manualCode = ManualMedicalCodeEntity.Create(
                tenantId,
                req.code,
                req.description,
                req.facilityType,
                req.cost,
                req.copay,
                req.facilityTotal,
                req.physicianTotal,
                req.total,
                userId);

            db.ManualMedicalCodes.Add(manualCode);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                isSuccess = true,
                message = "Successfully Created.",
            });
        }
        catch (DbUpdateException ex)
        {
            return Results.BadRequest(new
            {
                isSuccess = false,
                message = ex.GetBaseException().Message,
            });
        }
    }

    private static async Task<IResult> UpdateManualMedicalCode(
        ManualMedicalCodeRequest req,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId = RequireUserId(ctx);

        if (!Guid.TryParse(req.id, out var id))
        {
            return Results.BadRequest(new
            {
                isSuccess = false,
                message = "Id is required.",
            });
        }

        if (string.IsNullOrWhiteSpace(req.code))
        {
            return Results.BadRequest(new
            {
                isSuccess = false,
                message = "Code is required.",
            });
        }

        var manualCode = await db.ManualMedicalCodes
            .FirstOrDefaultAsync(m => m.Id == id && m.TenantId == tenantId, ct);

        if (manualCode is null)
        {
            return Results.NotFound(new
            {
                isSuccess = false,
                message = "Manual medical code not found.",
            });
        }

        try
        {
            manualCode.Update(
                req.code,
                req.description,
                req.facilityType,
                req.cost,
                req.copay,
                req.facilityTotal,
                req.physicianTotal,
                req.total,
                userId);

            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                isSuccess = true,
                message = "Successfully Updated.",
            });
        }
        catch (DbUpdateException ex)
        {
            return Results.BadRequest(new
            {
                isSuccess = false,
                message = ex.GetBaseException().Message,
            });
        }
    }

    // ── Dashboard stubs ───────────────────────────────────────────────────────
    private sealed class ReportFilterRequest
    {
        public int    Page          { get; init; } = 1;
        public int    Limit         { get; init; }
        public string? FilterType   { get; init; }
        public string? FilterId     { get; init; }
        public string? startDate    { get; init; }
        public string? endDate      { get; init; }
        public string? purchaseDateFrom { get; init; }
        public string? purchaseDateTo   { get; init; }
        public JsonElement? IsCsv { get; init; }
    }

    private sealed class DashboardCaseReportRow
    {
        public Guid Id { get; init; }
        public string CaseNumber { get; init; } = string.Empty;
        public string ClientFirstName { get; init; } = string.Empty;
        public string ClientLastName { get; init; } = string.Empty;
        public string ClientName { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string DateOfIncident { get; init; } = string.Empty;
        public string ClientDob { get; init; } = string.Empty;
        public string LawFirmId { get; init; } = string.Empty;
        public string LawFirm { get; init; } = string.Empty;
        public string CaseManagerId { get; init; } = string.Empty;
        public string CaseManager { get; init; } = string.Empty;
        public string AccidentTypeId { get; init; } = string.Empty;
        public string AccidentType { get; init; } = string.Empty;
        public decimal TotalLienAmount { get; init; }
        public int LienCount { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public DateTime UpdatedAtUtc { get; init; }
    }

    private sealed class DashboardLienReportRow
    {
        public Guid Id { get; init; }
        public string LienNumber { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string LienType { get; init; } = string.Empty;
        public string CaseId { get; init; } = string.Empty;
        public Guid? CaseRecordId { get; init; }
        public string CaseNumber { get; init; } = string.Empty;
        public string ClientName { get; init; } = string.Empty;
        public string LawFirmId { get; init; } = string.Empty;
        public string LawFirm { get; init; } = string.Empty;
        public string CaseManagerId { get; init; } = string.Empty;
        public string CaseManager { get; init; } = string.Empty;
        public string FacilityId { get; init; } = string.Empty;
        public string FacilityName { get; init; } = string.Empty;
        public string MedicalProviderId { get; init; } = string.Empty;
        public string MedicalProvider { get; init; } = string.Empty;
        public string FundingCompanyId { get; init; } = string.Empty;
        public string FundingCompany { get; init; } = string.Empty;
        public string IncidentDate { get; init; } = string.Empty;
        public string PurchaseDate { get; init; } = string.Empty;
        public string InitialServiceDate { get; init; } = string.Empty;
        public string EndServiceDate { get; init; } = string.Empty;
        public decimal OriginalAmount { get; init; }
        public decimal CurrentBalance { get; init; }
        public decimal PurchasePrice { get; init; }
        public decimal TotalPurchaseAmount { get; init; }
        public decimal TotalBillingAmount { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public DateTime UpdatedAtUtc { get; init; }
    }

    private sealed record DashboardLienCaseMetadata(
        Guid Id,
        Guid OrgId,
        string CaseNumber,
        string ClientFirstName,
        string ClientLastName,
        string? Notes);

    private sealed record DashboardLienMetadata(
        Guid Id,
        Guid OrgId,
        string LienNumber,
        string? ExternalReference,
        string LienType,
        string Status,
        Guid? CaseId,
        Guid? FacilityId,
        decimal OriginalAmount,
        decimal? CurrentBalance,
        decimal? PurchasePrice,
        DateOnly? IncidentDate,
        DateOnly? PurchaseDate,
        DateOnly? InitialServiceDate,
        DateOnly? EndServiceDate,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc);

    private sealed record DashboardLienFacilityMetadata(
        Guid Id,
        Guid? FacilityId);

    private sealed record DashboardLienServicingMetadata(
        Guid LienId,
        string TaskType,
        string? Notes,
        DateTime CreatedAtUtc);

    private sealed class DashboardAmountSummary
    {
        public decimal Purchase { get; init; }
        public decimal Billing { get; init; }
    }

    private sealed class DashboardLienReportSummary
    {
        public int TotalCount { get; init; }
        public decimal TotalPurchaseAmount { get; init; }
        public decimal TotalBillingAmount { get; init; }
        public IReadOnlyDictionary<string, int> StatusCounts { get; init; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, DashboardAmountSummary> StatusAmounts { get; init; } =
            new Dictionary<string, DashboardAmountSummary>(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class DashboardReportResult<T>
    {
        public IReadOnlyList<T> Items { get; init; } = [];
        public int Page { get; init; }
        public int PageSize { get; init; }
        public int TotalCount { get; init; }
        public decimal TotalPurchaseAmount { get; init; }
        public decimal TotalBillingAmount { get; init; }
        public IReadOnlyDictionary<string, int> StatusCounts { get; init; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, DashboardAmountSummary> StatusAmounts { get; init; } =
            new Dictionary<string, DashboardAmountSummary>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, int> AllocationCounts { get; init; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class LegacyDashboardMetricRequest
    {
        public string? startDate { get; init; }
        public string? endDate { get; init; }
    }

    private static bool TryResolveDashboardDeployedPeriod(
        LegacyDashboardMetricRequest? request,
        out DateTime? periodStart,
        out DateTime? periodEnd,
        out string validationMessage)
        => TryResolveDashboardMetricPeriod(request?.startDate, request?.endDate, out periodStart, out periodEnd, out validationMessage);

    private static bool TryResolveDashboardCashReceivedPeriod(
        LegacyDashboardMetricRequest? request,
        out DateTime? periodStart,
        out DateTime? periodEnd,
        out string validationMessage)
        => TryResolveDashboardMetricPeriod(request?.startDate, request?.endDate, out periodStart, out periodEnd, out validationMessage);

    private static bool TryResolveDashboardMetricPeriod(
        string? startDate,
        string? endDate,
        out DateTime? periodStart,
        out DateTime? periodEnd,
        out string validationMessage)
    {
        validationMessage = string.Empty;
        var hasStart = !string.IsNullOrWhiteSpace(startDate);
        var hasEnd = !string.IsNullOrWhiteSpace(endDate);

        if (!hasStart && !hasEnd)
        {
            periodStart = default;
            periodEnd = default;
            return true;
        }

        if (hasStart != hasEnd)
        {
            periodStart = default;
            periodEnd = default;
            validationMessage = "Both startDate and endDate are required when customizing the date range.";
            return false;
        }

        var supportedFormats = new[] { "MM/dd/yyyy", "M/d/yyyy", "yyyy-MM-dd", "yyyy-M-d" };
        if (!DateTime.TryParseExact(startDate, supportedFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedStart) ||
            !DateTime.TryParseExact(endDate, supportedFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedEnd))
        {
            periodStart = default;
            periodEnd = default;
            validationMessage = "Invalid date range. Use MM/dd/yyyy or yyyy-MM-dd.";
            return false;
        }

        periodStart = parsedStart.Date;
        periodEnd = parsedEnd.Date;
        if (periodStart > periodEnd)
        {
            validationMessage = "startDate cannot be after endDate.";
            return false;
        }

        return true;
    }

    private static DateOnly GetDefaultDashboardPeriodEnd()
    {
        var pacificToday = PacificTimeHelper.Convert(DateTime.UtcNow).Date;
        return DateOnly.FromDateTime(pacificToday.AddDays(-1));
    }

    private static async Task<IResult> GetDashboardDeployedLegacy(
        LegacyDashboardMetricRequest? request,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var orgId = ctx.OrgId;

        if (!TryResolveDashboardDeployedPeriod(request, out var periodStart, out var periodEnd, out var validationMessage))
        {
            return Results.BadRequest(new { isSuccess = false, message = validationMessage });
        }

        var query = db.Liens.AsNoTracking()
            .Where(l => l.TenantId == tenantId &&
                        l.PurchaseDate.HasValue);

        if (periodStart.HasValue && periodEnd.HasValue)
        {
            var purchaseStart = DateOnly.FromDateTime(periodStart.Value);
            var purchaseEnd = DateOnly.FromDateTime(periodEnd.Value);
            query = query.Where(l =>
                l.PurchaseDate!.Value >= purchaseStart &&
                l.PurchaseDate.Value <= purchaseEnd);
        }
        else
        {
            var purchaseEnd = GetDefaultDashboardPeriodEnd();
            query = query.Where(l => l.PurchaseDate!.Value <= purchaseEnd);
        }

        if (orgId.HasValue)
            query = query.Where(l => l.OrgId == orgId.Value || l.SellingOrgId == orgId.Value || l.BuyingOrgId == orgId.Value || l.HoldingOrgId == orgId.Value);

        var medicalPurchaseQuery =
            from servicingItem in db.ServicingItems.AsNoTracking()
            join lien in query on servicingItem.LienId equals (Guid?)lien.Id
            where servicingItem.TenantId == tenantId &&
                  servicingItem.TaskType == "LegacyMedicalCode"
            select new
            {
                LienId = lien.Id,
                servicingItem.Notes,
            };

        var medicalPurchaseAmountsByLienId = new Dictionary<Guid, decimal>();
        await foreach (var row in medicalPurchaseQuery.AsAsyncEnumerable().WithCancellation(ct))
        {
            if (!TryGetLegacyMedicalPurchaseAmount(row.Notes, out var amount))
                continue;

            medicalPurchaseAmountsByLienId[row.LienId] =
                medicalPurchaseAmountsByLienId.GetValueOrDefault(row.LienId) + amount;
        }

        var totalAmount = medicalPurchaseAmountsByLienId.Values.Sum();

        return Results.Ok(new
        {
            isSuccess = true,
            message = "Dashboard deployed metric retrieved successfully.",
            data = new
            {
                periodStart = periodStart?.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) ?? string.Empty,
                periodEnd = periodEnd?.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) ?? string.Empty,
                totalAmount = totalAmount.ToString("0.00", CultureInfo.InvariantCulture),
                totalCount = medicalPurchaseAmountsByLienId.Count,
            },
        });
    }

    private static async Task<IResult> GetDashboardCashReceivedLegacy(
        LegacyDashboardMetricRequest? request,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);

        if (!TryResolveDashboardCashReceivedPeriod(request, out var periodStart, out var periodEnd, out var validationMessage))
        {
            return Results.BadRequest(new { isSuccess = false, message = validationMessage });
        }

        if (!periodStart.HasValue && !periodEnd.HasValue)
        {
            var liens = await db.Liens
                .AsNoTracking()
                .Where(lien => lien.TenantId == tenantId)
                .Select(lien => new
                {
                    lien.Id,
                    lien.PayoffAmount,
                })
                .ToListAsync(ct);

            var settlementNotes = await db.LienSettlements
                .AsNoTracking()
                .Where(settlement =>
                    settlement.TenantId == tenantId &&
                    !settlement.IsDeleted &&
                    settlement.Note != null)
                .Select(settlement => new
                {
                    settlement.LienId,
                    settlement.Note,
                })
                .ToListAsync(ct);

            var importedReturnedAmountsByLienId = settlementNotes
                .Select(settlement => new
                {
                    settlement.LienId,
                    HasReturnedAmount = TryGetLegacyTotalSettledAmount(settlement.Note, out var amount),
                    Amount = amount,
                })
                .Where(settlement => settlement.HasReturnedAmount)
                .GroupBy(settlement => settlement.LienId)
                .ToDictionary(
                    group => group.Key,
                    group => group.Sum(settlement => settlement.Amount));

            var paymentAmountsByLienId = await db.SettlementPaymentDetails
                .AsNoTracking()
                .Where(payment => payment.TenantId == tenantId && !payment.IsDeleted &&
                                  payment.PostingStatus != SettlementPaymentDetail.VoidedStatus)
                .GroupBy(payment => payment.LienId)
                .Select(group => new
                {
                    LienId = group.Key,
                    Amount = group.Sum(payment => payment.Amount),
                })
                .ToDictionaryAsync(group => group.LienId, group => group.Amount, ct);

            var returnedAmounts = liens
                .Select(lien =>
                {
                    if (importedReturnedAmountsByLienId.TryGetValue(lien.Id, out var importedReturnedAmount))
                        return (HasSource: true, Amount: importedReturnedAmount);

                    if (lien.PayoffAmount.HasValue)
                        return (HasSource: true, Amount: lien.PayoffAmount.Value);

                    return paymentAmountsByLienId.TryGetValue(lien.Id, out var paymentAmount)
                        ? (HasSource: true, Amount: paymentAmount)
                        : (HasSource: false, Amount: 0m);
                })
                .Where(result => result.HasSource)
                .ToList();

            return Results.Ok(new
            {
                isSuccess = true,
                message = "Dashboard cash received metric retrieved successfully.",
                data = new
                {
                    periodStart = string.Empty,
                    periodEnd = string.Empty,
                    totalAmount = returnedAmounts.Sum(result => result.Amount).ToString("0.00", CultureInfo.InvariantCulture),
                    totalCount = returnedAmounts.Count,
                },
            });
        }

        var settlementsQuery = db.LienSettlements.AsNoTracking()
            .Where(s => s.TenantId == tenantId &&
                        !s.IsDeleted &&
                        s.SettlementDate.HasValue);

        if (periodStart.HasValue && periodEnd.HasValue)
        {
            var settlementStart = DateOnly.FromDateTime(periodStart.Value);
            var settlementEnd = DateOnly.FromDateTime(periodEnd.Value);
            settlementsQuery = settlementsQuery.Where(s =>
                s.SettlementDate!.Value >= settlementStart &&
                s.SettlementDate.Value <= settlementEnd);
        }
        else
        {
            var settlementEnd = GetDefaultDashboardPeriodEnd();
            settlementsQuery = settlementsQuery.Where(s => s.SettlementDate!.Value <= settlementEnd);
        }

        var aggregate = await settlementsQuery
            .GroupBy(_ => 1)
            .Select(group => new
            {
                TotalAmount = group.Sum(item => item.Amount),
                TotalCount = group.Count(),
            })
            .SingleOrDefaultAsync(ct);

        return Results.Ok(new
        {
            isSuccess = true,
            message = "Dashboard cash received metric retrieved successfully.",
            data = new
            {
                periodStart = periodStart?.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) ?? string.Empty,
                periodEnd = periodEnd?.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) ?? string.Empty,
                totalAmount = (aggregate?.TotalAmount ?? 0m).ToString("0.00", CultureInfo.InvariantCulture),
                totalCount = aggregate?.TotalCount ?? 0,
            },
        });
    }

    private static bool TryGetLegacyTotalSettledAmount(string? notes, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(notes))
            return false;

        var rawMetadata = notes;
        var markerIndex = notes.IndexOf(LegacyMetadataMarker, StringComparison.Ordinal);
        if (markerIndex >= 0)
            rawMetadata = notes[(markerIndex + LegacyMetadataMarker.Length)..].Trim();

        var found = false;
        foreach (var segment in rawMetadata.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0 ||
                !string.Equals(segment[..separator].Trim(), "totalSettledAmount", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            found = true;
            decimal.TryParse(
                segment[(separator + 1)..].Trim(),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out amount);
        }

        return found;
    }

    private static bool TryGetLegacyMedicalPurchaseAmount(string? notes, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(notes))
            return false;

        var found = false;
        foreach (var segment in notes.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0 ||
                !string.Equals(segment[..separator].Trim(), "purchaseAmount", StringComparison.Ordinal) ||
                !decimal.TryParse(
                    segment[(separator + 1)..].Trim(),
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out var parsedAmount))
            {
                continue;
            }

            found = true;
            amount = parsedAmount;
        }

        return found;
    }

    private static async Task<IResult> GetDashboardTaskSummary(
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var userId = RequireUserId(ctx);
        var userIdText = userId.ToString();

        // SL-CORE scoped this dashboard to the signed-in task assignee.  Legacy
        // compatibility tasks retain their legacy values in ServicingItems, while
        // AssignedToUserId supports tasks created after the migration.
        var tasks = await db.ServicingItems
            .AsNoTracking()
            .Where(item =>
                item.TenantId == tenantId &&
                item.TaskType == "LegacyCaseTask" &&
                (item.AssignedToUserId == userId ||
                 item.AssignedTo == userIdText ||
                 (!string.IsNullOrWhiteSpace(ctx.Email) && item.AssignedTo == ctx.Email)))
            .OrderByDescending(item => item.CreatedAtUtc)
            .Select(item => new
            {
                item.Id,
                item.CaseId,
                item.AssignedTo,
                item.Description,
                item.DueDate,
                item.Status,
                item.Priority,
                item.Notes,
            })
            .ToListAsync(ct);

        var caseIds = tasks
            .Where(item => item.CaseId.HasValue)
            .Select(item => item.CaseId!.Value)
            .Distinct()
            .ToList();
        Dictionary<Guid, (string CaseCode, string CaseName)> casesById;
        if (caseIds.Count == 0)
        {
            casesById = [];
        }
        else
        {
            casesById = await db.Cases
                .AsNoTracking()
                .Where(item => item.TenantId == tenantId && caseIds.Contains(item.Id))
                .ToDictionaryAsync(
                    item => item.Id,
                    item => (item.CaseNumber, $"{item.ClientFirstName} {item.ClientLastName}".Trim()),
                    ct);
        }

        var responseTasks = tasks.Select(item =>
        {
            var fields = ParseLegacyNoteFields(item.Notes);
            var statusId = fields.GetValueOrDefault("statusId", fields.GetValueOrDefault("status", item.Status));
            var priorityId = fields.GetValueOrDefault("priorityId", item.Priority);
            casesById.TryGetValue(item.CaseId ?? Guid.Empty, out var caseInfo);

            return new
            {
                taskId = item.Id.ToString(),
                caseId = item.CaseId?.ToString() ?? string.Empty,
                caseCode = caseInfo.CaseCode ?? string.Empty,
                caseName = caseInfo.CaseName ?? string.Empty,
                assignedTo = item.AssignedTo,
                title = fields.GetValueOrDefault("title", item.Description),
                description = item.Description,
                dueDate = item.DueDate?.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) ?? string.Empty,
                status = GetLegacyTaskStatusCode(statusId),
                statusId,
                priority = GetLegacyTaskPriorityName(priorityId),
                priorityId,
            };
        }).ToList();

        var statusCounts = responseTasks
            .Select(task => ResolveLegacyTaskStatus(task.statusId))
            .Where(status => status is not null)
            .GroupBy(status => status!)
            .ToDictionary(group => group.Key, group => group.Count());

        return Results.Ok(new
        {
            isSuccess = true,
            message = "Successfully retrieved all tasks.",
            data = new
            {
                totalTasks = responseTasks.Count,
                upcomingTasks = statusCounts.GetValueOrDefault(TaskStatuses.New),
                inProgressTasks = statusCounts.GetValueOrDefault(TaskStatuses.InProgress),
                inReviewTasks = statusCounts.GetValueOrDefault(TaskStatuses.WaitingBlocked),
                completedTasks = statusCounts.GetValueOrDefault(TaskStatuses.Completed),
                tasks = responseTasks,
            },
        });
    }

    private static string? ResolveLegacyTaskStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return null;

        var normalized = status.Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();

        return normalized switch
        {
            "1" or "UPCOMING" or "NEW" or "OPEN" or "PENDING" => TaskStatuses.New,
            "2" or "INPROGRESS" => TaskStatuses.InProgress,
            "3" or "INREVIEW" or "WAITINGBLOCKED" or "ONHOLD" => TaskStatuses.WaitingBlocked,
            "4" or "COMPLETE" or "COMPLETED" or "DONE" => TaskStatuses.Completed,
            "CANCELLED" or "CANCELED" => TaskStatuses.Cancelled,
            _ => null,
        };
    }

    private static string GetLegacyTaskStatusCode(string? statusId) => ResolveLegacyTaskStatus(statusId) switch
    {
        TaskStatuses.New => "UPCOMING",
        TaskStatuses.InProgress => "INPROGRESS",
        TaskStatuses.WaitingBlocked => "INREVIEW",
        TaskStatuses.Completed => "COMPLETED",
        TaskStatuses.Cancelled => "CANCELLED",
        _ => statusId ?? string.Empty,
    };

    private static string GetLegacyTaskPriorityName(string? priorityId) => priorityId switch
    {
        TaskPriorities.Low => "Low",
        TaskPriorities.Medium => "Medium",
        TaskPriorities.High => "High",
        TaskPriorities.Urgent => "Urgent",
        _ => priorityId ?? string.Empty,
    };

    private static async Task<IResult> GetTotalLienReport(
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var result = await BuildDashboardLienReportResultAsync(
            new ReportFilterRequest { Page = 1, Limit = 500 },
            db,
            ctx,
            ct);
        return Results.Ok(result.Items);
    }

    private static async Task<IResult> GetTotalLienReportV3(
        ReportFilterRequest request,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var isCsv = IsDashboardCsvRequested(request);
        var result = await BuildDashboardLienReportResultAsync(
            request,
            db,
            ctx,
            ct,
            includeAllItems: isCsv);
        return isCsv
            ? BuildDashboardCsvResponse(BuildTotalLienReportCsv(result.Items), "total_lien_report")
            : Results.Ok(result);
    }

    private static async Task<IResult> GetTotalCaseReport(
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var result = await BuildDashboardCaseReportResultAsync(
            new ReportFilterRequest { Page = 1, Limit = 500 },
            db,
            ctx,
            ct);
        return Results.Ok(result.Items);
    }

    private static async Task<IResult> GetTotalCaseReportV3(
        ReportFilterRequest request,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var isCsv = IsDashboardCsvRequested(request);
        var result = await BuildDashboardCaseReportResultAsync(
            request,
            db,
            ctx,
            ct,
            includeAllItems: isCsv);
        return isCsv
            ? BuildDashboardCsvResponse(BuildTotalCaseReportCsv(result.Items), "total_case_report")
            : Results.Ok(result);
    }

    private static async Task<IResult> GetLawFirmCaseReport(
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var result = await BuildDashboardCaseReportResultAsync(
            new ReportFilterRequest { Page = 1, Limit = 500 },
            db,
            ctx,
            ct,
            requireLawFirm: true);
        return Results.Ok(result.Items);
    }

    private static async Task<IResult> GetLawFirmCaseReportV3(
        ReportFilterRequest request,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var isCsv = IsDashboardCsvRequested(request);
        var result = await BuildDashboardCaseReportResultAsync(
            request,
            db,
            ctx,
            ct,
            requireLawFirm: true,
            includeAllItems: isCsv);
        return isCsv
            ? BuildDashboardCsvResponse(BuildLawFirmCaseReportCsv(result.Items), "lawfirm_case_report")
            : Results.Ok(result);
    }

    private static async Task<IResult> GetMedicalProviderReport(
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var result = await BuildDashboardLienReportResultAsync(
            new ReportFilterRequest { Page = 1, Limit = 500 },
            db,
            ctx,
            ct,
            requireMedicalProvider: true);
        return Results.Ok(result.Items);
    }

    private static async Task<IResult> GetMedicalProviderReportV3(
        ReportFilterRequest request,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var isCsv = IsDashboardCsvRequested(request);
        var result = await BuildDashboardLienReportResultAsync(
            request,
            db,
            ctx,
            ct,
            requireMedicalProvider: true,
            includeAllItems: isCsv);
        return isCsv
            ? BuildDashboardCsvResponse(BuildMedicalProviderReportCsv(result.Items), "medical_provider_report")
            : Results.Ok(result);
    }

    private static bool IsDashboardCsvRequested(ReportFilterRequest request)
    {
        if (!request.IsCsv.HasValue)
            return false;

        var value = request.IsCsv.Value;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => string.Equals(value.GetString()?.Trim(), "true", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(value.GetString()?.Trim(), "yes", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static IResult BuildDashboardCsvResponse(byte[] csvBytes, string filenamePrefix)
    {
        var exportItem = new
        {
            base64 = Convert.ToBase64String(csvBytes),
            filename = $"{filenamePrefix}_{DateTime.UtcNow:yyyyMMddHHmmss}.csv",
            export_format = "csv",
        };

        return Results.Ok(new
        {
            isSuccess = true,
            message = "CSV generated successfully.",
            data = new object[] { exportItem },
        });
    }

    private static byte[] BuildTotalCaseReportCsv(IReadOnlyList<DashboardCaseReportRow> rows) =>
        BuildDashboardCsv(
            rows,
            ["Case ID", "Plaintiff Name", "Date of Loss", "Status"],
            row => [row.CaseNumber, row.ClientName, row.DateOfIncident, row.Status]);

    private static byte[] BuildTotalLienReportCsv(IReadOnlyList<DashboardLienReportRow> rows) =>
        BuildDashboardCsv(
            rows,
            ["Lien ID", "Case ID", "Plaintiff Name", "Lien Status"],
            row => [row.LienNumber, row.CaseId, row.ClientName, row.Status]);

    private static byte[] BuildMedicalProviderReportCsv(IReadOnlyList<DashboardLienReportRow> rows) =>
        BuildDashboardCsv(
            rows,
            ["Case ID", "Plaintiff Name", "Date of Loss", "Medical Facility"],
            row => [row.CaseId, row.ClientName, row.IncidentDate, row.FacilityName]);

    private static byte[] BuildLawFirmCaseReportCsv(IReadOnlyList<DashboardCaseReportRow> rows) =>
        BuildDashboardCsv(
            rows,
            ["Case ID", "Plaintiff Name", "Date of Loss", "Law Firm"],
            row => [row.CaseNumber, row.ClientName, row.DateOfIncident, row.LawFirm]);

    private static byte[] BuildDashboardCsv<T>(
        IReadOnlyList<T> rows,
        IReadOnlyList<string> columns,
        Func<T, IEnumerable<object?>> mapValues)
    {
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(',', columns.Select(EscapeLegacyCsv)));

        foreach (var row in rows)
        {
            csv.AppendLine(string.Join(',', mapValues(row).Select(value => EscapeLegacyCsv(FormatDashboardCsvValue(value)))));
        }

        return Encoding.UTF8.GetBytes(csv.ToString());
    }

    private static string FormatDashboardCsvValue(object? value) => value switch
    {
        null => string.Empty,
        DateTime timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static async Task<DashboardReportResult<DashboardCaseReportRow>> BuildDashboardCaseReportResultAsync(
        ReportFilterRequest? request,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct,
        bool requireLawFirm = false,
        bool includeAllItems = false)
    {
        var tenantId = RequireTenantId(ctx);
        var (page, limit) = NormalizeDashboardReportPaging(request);

        var caseQuery = db.Cases
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId);

        if (ctx.OrgId.HasValue)
            caseQuery = caseQuery.Where(c => c.OrgId == ctx.OrgId.Value);

        if (TryResolveDashboardLienReportPeriod(request, out var periodStart, out var periodEnd))
        {
            var purchaseStart = DateOnly.FromDateTime(periodStart);
            var purchaseEnd = DateOnly.FromDateTime(periodEnd);
            caseQuery = caseQuery.Where(c => db.Liens.Any(l =>
                l.TenantId == tenantId &&
                l.CaseId == c.Id &&
                l.PurchaseDate.HasValue &&
                l.PurchaseDate.Value >= purchaseStart &&
                l.PurchaseDate.Value <= purchaseEnd));
        }

        caseQuery = ApplyDashboardCaseDatabaseFilter(caseQuery, request);

        Dictionary<string, int>? precomputedStatusCounts = null;
        IReadOnlyDictionary<string, int>? precomputedAllocationCounts = null;
        List<Liens.Domain.Entities.Case> cases;
        if (CanUseFastPagedCaseReport(request, requireLawFirm, includeAllItems, limit))
        {
            var rawStatusCounts = await caseQuery
                .GroupBy(item => item.Status)
                .Select(group => new
                {
                    Status = group.Key,
                    Count = group.Count(),
                })
                .ToListAsync(ct);
            precomputedStatusCounts = rawStatusCounts
                .GroupBy(item => item.Status ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Sum(item => item.Count),
                    StringComparer.OrdinalIgnoreCase);

            if (requireLawFirm)
            {
                precomputedAllocationCounts = await BuildLawFirmAllocationCountsAsync(
                    caseQuery,
                    db,
                    tenantId,
                    ct);
            }

            var totalCount = precomputedStatusCounts.Values.Sum();
            var skip = (long)(page - 1) * limit;
            cases = skip >= totalCount || skip > int.MaxValue
                ? []
                : await caseQuery
                    .OrderByDescending(item => item.CreatedAtUtc)
                    .ThenByDescending(item => item.CaseNumber)
                    .Skip((int)skip)
                    .Take(limit)
                    .ToListAsync(ct);
        }
        else
        {
            cases = await caseQuery
                .OrderByDescending(c => c.CreatedAtUtc)
                .ToListAsync(ct);
        }

        var caseIds = cases.Select(c => c.Id).ToHashSet();
        var caseLiens = await db.Liens
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.CaseId.HasValue && caseIds.Contains(l.CaseId.Value))
            .ToListAsync(ct);

        var liensByCaseId = caseLiens
            .GroupBy(l => l.CaseId!.Value)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    TotalLienAmount = g.Sum(x => x.OriginalAmount),
                    LienCount = g.Count(),
                });

        var caseFieldsById = cases.ToDictionary(
            item => item.Id,
            item => ParseLegacyNoteFields(item.Notes));
        var referencedContactIds = new HashSet<Guid>();
        var referencedLawFirmNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fields in caseFieldsById.Values)
        {
            AddDashboardContactId(fields.GetValueOrDefault("lawFirmId", string.Empty), referencedContactIds);
            AddDashboardContactId(fields.GetValueOrDefault("caseManagerId", string.Empty), referencedContactIds);
            AddDashboardContactName(fields.GetValueOrDefault("lawFirm", string.Empty), referencedLawFirmNames);
        }

        var caseOrgIds = cases.Select(item => item.OrgId).Distinct().ToList();
        var lawFirmNames = referencedLawFirmNames
            .Select(name => name.ToLowerInvariant())
            .ToList();
        var contacts = await db.Contacts
            .AsNoTracking()
            .Where(contact =>
                contact.TenantId == tenantId &&
                (referencedContactIds.Contains(contact.Id) ||
                 (contact.ContactType == ContactType.LawFirm &&
                  (caseOrgIds.Contains(contact.OrgId) ||
                   lawFirmNames.Contains(contact.DisplayName.ToLower()) ||
                   (contact.Organization != null && lawFirmNames.Contains(contact.Organization.ToLower()))))))
            .OrderBy(contact => contact.DisplayName)
            .ToListAsync(ct);
        var contactsById = contacts.ToDictionary(contact => contact.Id);
        var lawFirmContacts = contacts
            .Where(contact => string.Equals(contact.ContactType, ContactType.LawFirm, StringComparison.Ordinal))
            .ToList();

        var lawFirmByOrgId = lawFirmContacts
            .GroupBy(c => c.OrgId)
            .ToDictionary(
                g => g.Key,
                g => g.First(),
                EqualityComparer<Guid>.Default);
        var lawFirmById = lawFirmContacts
            .ToDictionary(c => c.Id, EqualityComparer<Guid>.Default);
        var lawFirmByName = lawFirmContacts
            .SelectMany(c => GetDashboardContactLookupNames(c).Select(name => new { Name = name, Contact = c }))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Contact, StringComparer.OrdinalIgnoreCase);
        var rows = cases
            .Select(c =>
            {
                var fields = caseFieldsById[c.Id];
                var resolvedLawFirm = ResolveDashboardLawFirm(
                    fields.GetValueOrDefault("lawFirmId", string.Empty),
                    fields.GetValueOrDefault("lawFirm", string.Empty),
                    c.OrgId,
                    lawFirmById,
                    lawFirmByOrgId,
                    lawFirmByName);

                var caseManagerId = fields.GetValueOrDefault("caseManagerId", string.Empty);
                var caseManager = fields.GetValueOrDefault("caseManager", string.Empty);
                if (Guid.TryParse(caseManagerId, out var parsedCaseManagerId) &&
                    contactsById.TryGetValue(parsedCaseManagerId, out var caseManagerContact))
                {
                    if (string.IsNullOrWhiteSpace(caseManager))
                        caseManager = caseManagerContact.DisplayName;
                }

                liensByCaseId.TryGetValue(c.Id, out var lienAggregate);

                return new DashboardCaseReportRow
                {
                    Id = c.Id,
                    CaseNumber = c.CaseNumber,
                    ClientFirstName = c.ClientFirstName,
                    ClientLastName = c.ClientLastName,
                    ClientName = $"{c.ClientFirstName} {c.ClientLastName}".Trim(),
                    Status = c.Status,
                    DateOfIncident = FormatLegacyDate(c.DateOfIncident),
                    ClientDob = FormatLegacyDate(c.ClientDob),
                    LawFirmId = resolvedLawFirm.Id,
                    LawFirm = resolvedLawFirm.Name,
                    CaseManagerId = caseManagerId,
                    CaseManager = caseManager,
                    AccidentTypeId = fields.GetValueOrDefault("accidentTypeId", string.Empty),
                    AccidentType = fields.GetValueOrDefault("accidentType", string.Empty),
                    TotalLienAmount = lienAggregate?.TotalLienAmount ?? 0m,
                    LienCount = lienAggregate?.LienCount ?? 0,
                    CreatedAtUtc = c.CreatedAtUtc,
                    UpdatedAtUtc = c.UpdatedAtUtc,
                };
            })
            .Where(r => MatchesDashboardCaseFilter(request, r))
            .OrderByDescending(r => r.CreatedAtUtc)
            .ThenByDescending(r => r.CaseNumber, StringComparer.Ordinal)
            .ToList();

        if (precomputedStatusCounts is not null)
        {
            return new DashboardReportResult<DashboardCaseReportRow>
            {
                Items = rows,
                Page = page,
                PageSize = limit,
                TotalCount = precomputedStatusCounts.Values.Sum(),
                StatusCounts = precomputedStatusCounts,
                AllocationCounts = precomputedAllocationCounts ??
                    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            };
        }

        return BuildDashboardReportResult(
            rows,
            page,
            limit,
            includeAllItems,
            row => row.Status,
            allocationSelector: requireLawFirm ? row => row.LawFirm : null);
    }

    private static bool CanUseFastPagedCaseReport(
        ReportFilterRequest? request,
        bool requireLawFirm,
        bool includeAllItems,
        int limit) =>
        !includeAllItems &&
        limit > 0 &&
        string.IsNullOrWhiteSpace(request?.FilterType) &&
        string.IsNullOrWhiteSpace(request?.FilterId);

    private static async Task<DashboardReportResult<DashboardLienReportRow>> BuildDashboardLienReportResultAsync(
        ReportFilterRequest? request,
        LiensDbContext db,
        ICurrentRequestContext ctx,
        CancellationToken ct,
        bool requireMedicalProvider = false,
        bool includeAllItems = false)
    {
        var tenantId = RequireTenantId(ctx);
        var (page, limit) = NormalizeDashboardReportPaging(request);
        var periodStart = default(DateTime);
        var periodEnd = default(DateTime);
        var hasDateRange = TryResolveDashboardLienReportPeriod(request, out periodStart, out periodEnd);

        var lienQuery = db.Liens
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId);

        if (ctx.OrgId.HasValue)
        {
            var orgId = ctx.OrgId.Value;
            lienQuery = lienQuery.Where(l =>
                l.OrgId == orgId ||
                l.SellingOrgId == orgId ||
                l.BuyingOrgId == orgId ||
                l.HoldingOrgId == orgId);
        }

        if (hasDateRange)
        {
            var purchaseStart = DateOnly.FromDateTime(periodStart);
            var purchaseEnd = DateOnly.FromDateTime(periodEnd);
            lienQuery = lienQuery.Where(l => l.PurchaseDate.HasValue &&
                                             l.PurchaseDate.Value >= purchaseStart &&
                                             l.PurchaseDate.Value <= purchaseEnd);
        }
        else
        {
            var purchaseEnd = GetDefaultDashboardPeriodEnd();
            lienQuery = lienQuery.Where(l =>
                l.PurchaseDate.HasValue &&
                l.PurchaseDate.Value <= purchaseEnd);
        }

        lienQuery = ApplyDashboardLienDatabaseFilter(lienQuery, request);

        DashboardLienReportSummary? precomputedSummary = null;
        IReadOnlyDictionary<string, int>? precomputedAllocationCounts = null;
        List<DashboardLienMetadata> liens;
        if (CanUseFastPagedLienReport(request, includeAllItems, limit))
        {
            precomputedSummary = await BuildDashboardLienReportSummaryAsync(
                lienQuery,
                db,
                tenantId,
                ct);
            if (requireMedicalProvider)
            {
                precomputedAllocationCounts = await BuildMedicalFacilityAllocationCountsAsync(
                    lienQuery,
                    db,
                    tenantId,
                    ct);
            }

            var skip = (long)(page - 1) * limit;
            liens = skip >= precomputedSummary.TotalCount || skip > int.MaxValue
                ? []
                : await SelectDashboardLienMetadata(
                        lienQuery
                            .OrderByDescending(item => item.CreatedAtUtc)
                            .ThenByDescending(item => item.LienNumber)
                            .Skip((int)skip)
                            .Take(limit))
                    .ToListAsync(ct);
        }
        else
        {
            liens = await SelectDashboardLienMetadata(
                    lienQuery.OrderByDescending(l => l.CreatedAtUtc))
                .ToListAsync(ct);
        }

        var caseIds = liens.Where(l => l.CaseId.HasValue).Select(l => l.CaseId!.Value).Distinct().ToList();
        var lienIds = liens.Select(l => l.Id).ToList();

        var casesById = await db.Cases
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && caseIds.Contains(c.Id))
            .Select(c => new DashboardLienCaseMetadata(
                c.Id,
                c.OrgId,
                c.CaseNumber,
                c.ClientFirstName,
                c.ClientLastName,
                c.Notes))
            .ToDictionaryAsync(c => c.Id, ct);

        var servicingItems = await db.ServicingItems
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId &&
                        s.LienId.HasValue &&
                        lienIds.Contains(s.LienId.Value) &&
                        (s.TaskType == "LegacyMedicalCode" || s.TaskType == "LegacyMedicalFacilityInfo"))
            .Select(s => new DashboardLienServicingMetadata(
                s.LienId!.Value,
                s.TaskType,
                s.Notes,
                s.CreatedAtUtc))
            .ToListAsync(ct);

        var caseFieldsById = casesById.ToDictionary(
            pair => pair.Key,
            pair => ParseLegacyNoteFields(pair.Value.Notes));
        var facilityInfoByLienId = servicingItems
            .Where(s => string.Equals(s.TaskType, "LegacyMedicalFacilityInfo", StringComparison.Ordinal))
            .GroupBy(s => s.LienId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.CreatedAtUtc).First());
        var facilityFieldsByLienId = facilityInfoByLienId.ToDictionary(
            pair => pair.Key,
            pair => ParseLegacyNoteFields(pair.Value.Notes));

        var referencedContactIds = new HashSet<Guid>();
        var referencedLawFirmNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencedProviderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencedFacilityNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fields in caseFieldsById.Values)
        {
            AddDashboardContactId(fields.GetValueOrDefault("lawFirmId", string.Empty), referencedContactIds);
            AddDashboardContactId(fields.GetValueOrDefault("caseManagerId", string.Empty), referencedContactIds);
            AddDashboardContactName(fields.GetValueOrDefault("lawFirm", string.Empty), referencedLawFirmNames);
        }
        foreach (var lien in liens)
        {
            AddDashboardContactId(lien.ExternalReference, referencedContactIds);
            if (lien.FacilityId.HasValue)
                referencedContactIds.Add(lien.FacilityId.Value);
        }
        foreach (var fields in facilityFieldsByLienId.Values)
        {
            AddDashboardContactId(fields.GetValueOrDefault("facilityId", string.Empty), referencedContactIds);
            AddDashboardContactId(fields.GetValueOrDefault("medicalProviderId", string.Empty), referencedContactIds);
            AddDashboardContactName(fields.GetValueOrDefault("facilityName", string.Empty), referencedFacilityNames);
            AddDashboardContactName(fields.GetValueOrDefault("medicalProvider", string.Empty), referencedProviderNames);
        }

        var caseOrgIds = casesById.Values.Select(item => item.OrgId).Distinct().ToList();
        var lienFacilityIds = liens
            .Where(item => item.FacilityId.HasValue)
            .Select(item => item.FacilityId!.Value)
            .Distinct()
            .ToList();
        var lawFirmNames = referencedLawFirmNames.Select(name => name.ToLowerInvariant()).ToList();
        var providerNames = referencedProviderNames.Select(name => name.ToLowerInvariant()).ToList();
        var facilityNames = referencedFacilityNames.Select(name => name.ToLowerInvariant()).ToList();
        var contacts = await db.Contacts
            .AsNoTracking()
            .Where(contact => contact.TenantId == tenantId &&
                (referencedContactIds.Contains(contact.Id) ||
                 (contact.ContactType == ContactType.LawFirm &&
                  (caseOrgIds.Contains(contact.OrgId) ||
                   lawFirmNames.Contains(contact.DisplayName.ToLower()) ||
                   (contact.Organization != null && lawFirmNames.Contains(contact.Organization.ToLower())))) ||
                 (contact.ContactType == ContactType.Provider &&
                  (providerNames.Contains(contact.DisplayName.ToLower()) ||
                   (contact.Organization != null && providerNames.Contains(contact.Organization.ToLower())))) ||
                 ((contact.ContactType == ContactType.Facility ||
                   contact.ContactType == ContactType.MedicalFacility) &&
                  (lienFacilityIds.Contains(contact.Id) ||
                   (contact.FacilityId.HasValue && lienFacilityIds.Contains(contact.FacilityId.Value)) ||
                   facilityNames.Contains(contact.DisplayName.ToLower()) ||
                   (contact.Organization != null && facilityNames.Contains(contact.Organization.ToLower()))))))
            .OrderBy(contact => contact.DisplayName)
            .ToListAsync(ct);
        var contactsById = contacts.ToDictionary(c => c.Id, EqualityComparer<Guid>.Default);
        var lawFirmContacts = contacts
            .Where(c => string.Equals(c.ContactType, ContactType.LawFirm, StringComparison.Ordinal))
            .ToList();
        var facilityContacts = contacts
            .Where(IsStandaloneFacilityContact)
            .ToList();
        var facilityContactsById = facilityContacts.ToDictionary(c => c.Id, EqualityComparer<Guid>.Default);
        var facilityContactsByLinkedFacilityId = facilityContacts
            .Where(c => c.FacilityId.HasValue)
            .GroupBy(c => c.FacilityId!.Value)
            .ToDictionary(g => g.Key, g => g.First(), EqualityComparer<Guid>.Default);
        var facilityContactsByName = facilityContacts
            .SelectMany(c => GetFacilityContactLookupNames(c).Select(name => new { Name = name, Contact = c }))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Contact, StringComparer.OrdinalIgnoreCase);

        var lawFirmByOrgId = lawFirmContacts
            .GroupBy(c => c.OrgId)
            .ToDictionary(
                g => g.Key,
                g => g.First(),
                EqualityComparer<Guid>.Default);
        var lawFirmById = lawFirmContacts
            .ToDictionary(c => c.Id, EqualityComparer<Guid>.Default);
        var lawFirmByName = lawFirmContacts
            .SelectMany(c => GetDashboardContactLookupNames(c).Select(name => new { Name = name, Contact = c }))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Contact, StringComparer.OrdinalIgnoreCase);
        var providerContacts = contactsById.Values
            .Where(c => string.Equals(c.ContactType, ContactType.Provider, StringComparison.Ordinal))
            .ToList();
        var providerById = providerContacts
            .ToDictionary(c => c.Id, EqualityComparer<Guid>.Default);
        var providerByName = providerContacts
            .SelectMany(c => GetDashboardContactLookupNames(c).Select(name => new { Name = name, Contact = c }))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Contact, StringComparer.OrdinalIgnoreCase);

        var medicalCodesByLienId = servicingItems
            .Where(s => string.Equals(s.TaskType, "LegacyMedicalCode", StringComparison.Ordinal))
            .GroupBy(s => s.LienId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var rows = liens
            .Select(l =>
            {
                casesById.TryGetValue(l.CaseId ?? Guid.Empty, out var caseInfo);
                var caseFields = caseInfo is not null && caseFieldsById.TryGetValue(caseInfo.Id, out var savedCaseFields)
                    ? savedCaseFields
                    : new Dictionary<string, string>(StringComparer.Ordinal);

                var resolvedLawFirm = ResolveDashboardLawFirm(
                    caseFields.GetValueOrDefault("lawFirmId", string.Empty),
                    caseFields.GetValueOrDefault("lawFirm", string.Empty),
                    caseInfo?.OrgId ?? l.OrgId,
                    lawFirmById,
                    lawFirmByOrgId,
                    lawFirmByName);

                var caseManagerId = caseFields.GetValueOrDefault("caseManagerId", string.Empty);
                var caseManager = caseFields.GetValueOrDefault("caseManager", string.Empty);
                if (Guid.TryParse(caseManagerId, out var parsedCaseManagerId) &&
                    contactsById.TryGetValue(parsedCaseManagerId, out var caseManagerContact) &&
                    string.IsNullOrWhiteSpace(caseManager))
                {
                    caseManager = caseManagerContact.DisplayName;
                }

                var facilityId = l.FacilityId?.ToString() ?? string.Empty;
                var facilityName = string.Empty;
                var medicalProviderId = string.Empty;
                var medicalProvider = string.Empty;

                if (facilityFieldsByLienId.TryGetValue(l.Id, out var facilityFields))
                {
                    facilityId = facilityFields.GetValueOrDefault("facilityId", facilityId);
                    facilityName = facilityFields.GetValueOrDefault("facilityName", string.Empty);
                    medicalProviderId = facilityFields.GetValueOrDefault("medicalProviderId", string.Empty);
                    medicalProvider = facilityFields.GetValueOrDefault("medicalProvider", string.Empty);
                }

                if (Guid.TryParse(facilityId, out var parsedFacilityId))
                {
                    if (facilityContactsById.TryGetValue(parsedFacilityId, out var facilityContact) ||
                        facilityContactsByLinkedFacilityId.TryGetValue(parsedFacilityId, out facilityContact))
                    {
                        facilityId = facilityContact.Id.ToString();
                        if (string.IsNullOrWhiteSpace(facilityName))
                            facilityName = ResolveFacilityContactName(facilityContact);
                    }
                }

                if (string.IsNullOrWhiteSpace(facilityId) &&
                    !string.IsNullOrWhiteSpace(facilityName) &&
                    facilityContactsByName.TryGetValue(facilityName.Trim(), out var facilityContactByName))
                {
                    facilityId = facilityContactByName.Id.ToString();
                    facilityName = ResolveFacilityContactName(facilityContactByName);
                }

                var resolvedMedicalProvider = ResolveDashboardContactByIdOrName(
                    medicalProviderId,
                    medicalProvider,
                    providerById,
                    providerByName);

                var fundingCompanyId = l.ExternalReference ?? string.Empty;
                var fundingCompany = string.Empty;
                if (Guid.TryParse(fundingCompanyId, out var parsedFundingCompanyId) &&
                    contactsById.TryGetValue(parsedFundingCompanyId, out var fundingCompanyContact))
                {
                    fundingCompany = fundingCompanyContact.Organization ?? fundingCompanyContact.DisplayName;
                }

                var totalPurchaseAmount = 0m;
                var totalBillingAmount = 0m;
                if (medicalCodesByLienId.TryGetValue(l.Id, out var medicalCodeItems))
                {
                    foreach (var item in medicalCodeItems)
                    {
                        var fields = ParseLegacyNoteFields(item.Notes);
                        if (decimal.TryParse(fields.GetValueOrDefault("purchaseAmount", string.Empty), NumberStyles.Any, CultureInfo.InvariantCulture, out var purchase))
                            totalPurchaseAmount += purchase;
                        if (decimal.TryParse(fields.GetValueOrDefault("billingAmount", string.Empty), NumberStyles.Any, CultureInfo.InvariantCulture, out var billing))
                            totalBillingAmount += billing;
                    }
                }

                return new DashboardLienReportRow
                {
                    Id = l.Id,
                    LienNumber = l.LienNumber,
                    Status = MapDashboardLienBusinessStatus(l.Status),
                    LienType = l.LienType,
                    CaseId = caseInfo?.CaseNumber ?? string.Empty,
                    CaseRecordId = l.CaseId,
                    CaseNumber = caseInfo?.CaseNumber ?? string.Empty,
                    ClientName = caseInfo is null
                        ? string.Empty
                        : $"{caseInfo.ClientFirstName} {caseInfo.ClientLastName}".Trim(),
                    LawFirmId = resolvedLawFirm.Id,
                    LawFirm = resolvedLawFirm.Name,
                    CaseManagerId = caseManagerId,
                    CaseManager = caseManager,
                    FacilityId = facilityId,
                    FacilityName = facilityName,
                    MedicalProviderId = resolvedMedicalProvider.Id,
                    MedicalProvider = resolvedMedicalProvider.Name,
                    FundingCompanyId = fundingCompanyId,
                    FundingCompany = fundingCompany,
                    IncidentDate = FormatLegacyDate(l.IncidentDate),
                    PurchaseDate = FormatLegacyDate(l.PurchaseDate),
                    InitialServiceDate = FormatLegacyDate(l.InitialServiceDate),
                    EndServiceDate = FormatLegacyDate(l.EndServiceDate),
                    OriginalAmount = l.OriginalAmount,
                    CurrentBalance = l.CurrentBalance ?? 0m,
                    PurchasePrice = l.PurchasePrice ?? 0m,
                    TotalPurchaseAmount = totalPurchaseAmount,
                    TotalBillingAmount = totalBillingAmount,
                    CreatedAtUtc = l.CreatedAtUtc,
                    UpdatedAtUtc = l.UpdatedAtUtc,
                };
            })
            .Where(r => MatchesDashboardLienFilter(request, r))
            .OrderByDescending(r => r.CreatedAtUtc)
            .ThenByDescending(r => r.LienNumber, StringComparer.Ordinal)
            .ToList();

        if (precomputedSummary is not null)
        {
            return new DashboardReportResult<DashboardLienReportRow>
            {
                Items = rows,
                Page = page,
                PageSize = limit,
                TotalCount = precomputedSummary.TotalCount,
                TotalPurchaseAmount = precomputedSummary.TotalPurchaseAmount,
                TotalBillingAmount = precomputedSummary.TotalBillingAmount,
                StatusCounts = precomputedSummary.StatusCounts,
                StatusAmounts = precomputedSummary.StatusAmounts,
                AllocationCounts = precomputedAllocationCounts ??
                    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            };
        }

        return BuildDashboardReportResult(
            rows,
            page,
            limit,
            includeAllItems,
            row => row.Status,
            row => row.TotalPurchaseAmount,
            row => row.TotalBillingAmount,
            requireMedicalProvider ? row => row.FacilityName : null);
    }

    private static bool CanUseFastPagedLienReport(
        ReportFilterRequest? request,
        bool includeAllItems,
        int limit)
    {
        if (includeAllItems || limit < 1)
            return false;
        if (string.IsNullOrWhiteSpace(request?.FilterType) || string.IsNullOrWhiteSpace(request.FilterId))
            return true;

        var filterType = request.FilterType.Trim().ToLowerInvariant();
        var filterId = request.FilterId.Trim();
        return filterType switch
        {
            "lien" or "lienid" => true,
            "status" or "lienstatus" => filterId.Equals("Open", StringComparison.OrdinalIgnoreCase) ||
                                           filterId.Equals("Closed", StringComparison.OrdinalIgnoreCase) ||
                                           filterId.Equals("Rejected", StringComparison.OrdinalIgnoreCase),
            "case" or "caseid" => Guid.TryParse(filterId, out _),
            "fundingcompany" or "fundingcompanyid" => Guid.TryParse(filterId, out _),
            _ => false,
        };
    }

    private static IQueryable<DashboardLienMetadata> SelectDashboardLienMetadata(
        IQueryable<Liens.Domain.Entities.Lien> query) =>
        query.Select(lien => new DashboardLienMetadata(
            lien.Id,
            lien.OrgId,
            lien.LienNumber,
            lien.ExternalReference,
            lien.LienType,
            lien.Status,
            lien.CaseId,
            lien.FacilityId,
            lien.OriginalAmount,
            lien.CurrentBalance,
            lien.PurchasePrice,
            lien.IncidentDate,
            lien.PurchaseDate,
            lien.InitialServiceDate,
            lien.EndServiceDate,
            lien.CreatedAtUtc,
            lien.UpdatedAtUtc));

    private static async Task<IReadOnlyDictionary<string, int>> BuildLawFirmAllocationCountsAsync(
        IQueryable<Liens.Domain.Entities.Case> caseQuery,
        LiensDbContext db,
        Guid tenantId,
        CancellationToken ct)
    {
        var lawFirmContacts = await db.Contacts
            .AsNoTracking()
            .Where(contact =>
                contact.TenantId == tenantId &&
                contact.ContactType == ContactType.LawFirm)
            .OrderBy(contact => contact.DisplayName)
            .ToListAsync(ct);
        var lawFirmByOrgId = lawFirmContacts
            .GroupBy(contact => contact.OrgId)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                EqualityComparer<Guid>.Default);
        var lawFirmById = lawFirmContacts
            .ToDictionary(contact => contact.Id, EqualityComparer<Guid>.Default);
        var lawFirmByName = lawFirmContacts
            .SelectMany(contact => GetDashboardContactLookupNames(contact)
                .Select(name => new { Name = name, Contact = contact }))
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Contact,
                StringComparer.OrdinalIgnoreCase);

        var allocationCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var allocationQuery = caseQuery.Select(item => new
        {
            item.OrgId,
            item.Notes,
        });
        await foreach (var item in allocationQuery.AsAsyncEnumerable().WithCancellation(ct))
        {
            var fields = ParseLegacyNoteFields(item.Notes);
            var resolved = ResolveDashboardLawFirm(
                fields.GetValueOrDefault("lawFirmId", string.Empty),
                fields.GetValueOrDefault("lawFirm", string.Empty),
                item.OrgId,
                lawFirmById,
                lawFirmByOrgId,
                lawFirmByName);
            var name = NormalizeDashboardSummaryKey(resolved.Name);
            allocationCounts[name] = allocationCounts.GetValueOrDefault(name) + 1;
        }

        return allocationCounts;
    }

    private static async Task<IReadOnlyDictionary<string, int>> BuildMedicalFacilityAllocationCountsAsync(
        IQueryable<Liens.Domain.Entities.Lien> lienQuery,
        LiensDbContext db,
        Guid tenantId,
        CancellationToken ct)
    {
        var lienFacilities = await lienQuery
            .Select(lien => new DashboardLienFacilityMetadata(lien.Id, lien.FacilityId))
            .ToListAsync(ct);

        var facilityInfoQuery =
            from servicingItem in db.ServicingItems.AsNoTracking()
            join lien in lienQuery on servicingItem.LienId equals (Guid?)lien.Id
            where servicingItem.TenantId == tenantId &&
                  servicingItem.TaskType == "LegacyMedicalFacilityInfo"
            select new DashboardLienServicingMetadata(
                servicingItem.LienId!.Value,
                servicingItem.TaskType,
                servicingItem.Notes,
                servicingItem.CreatedAtUtc);
        var facilityFieldsByLienId = new Dictionary<Guid, (DateTime CreatedAtUtc, Dictionary<string, string> Fields)>();
        await foreach (var item in facilityInfoQuery.AsAsyncEnumerable().WithCancellation(ct))
        {
            if (facilityFieldsByLienId.TryGetValue(item.LienId, out var current) &&
                current.CreatedAtUtc >= item.CreatedAtUtc)
            {
                continue;
            }

            facilityFieldsByLienId[item.LienId] =
                (item.CreatedAtUtc, ParseLegacyNoteFields(item.Notes));
        }

        var allFacilityContacts = await db.Contacts
            .AsNoTracking()
            .Where(contact =>
                contact.TenantId == tenantId &&
                (contact.ContactType == ContactType.Facility ||
                 contact.ContactType == ContactType.MedicalFacility))
            .OrderBy(contact => contact.DisplayName)
            .ToListAsync(ct);
        var facilityContacts = allFacilityContacts
            .Where(IsStandaloneFacilityContact)
            .ToList();
        var facilityContactsById = facilityContacts
            .ToDictionary(contact => contact.Id, EqualityComparer<Guid>.Default);
        var facilityContactsByLinkedFacilityId = facilityContacts
            .Where(contact => contact.FacilityId.HasValue)
            .GroupBy(contact => contact.FacilityId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                EqualityComparer<Guid>.Default);
        var facilityContactsByName = facilityContacts
            .SelectMany(contact => GetFacilityContactLookupNames(contact)
                .Select(name => new { Name = name, Contact = contact }))
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Contact,
                StringComparer.OrdinalIgnoreCase);

        return lienFacilities
            .Select(lien =>
            {
                var facilityId = lien.FacilityId?.ToString() ?? string.Empty;
                var facilityName = string.Empty;
                if (facilityFieldsByLienId.TryGetValue(lien.Id, out var facilityInfo))
                {
                    var fields = facilityInfo.Fields;
                    facilityId = fields.GetValueOrDefault("facilityId", facilityId);
                    facilityName = fields.GetValueOrDefault("facilityName", string.Empty);
                }

                if (Guid.TryParse(facilityId, out var parsedFacilityId) &&
                    (facilityContactsById.TryGetValue(parsedFacilityId, out var facilityContact) ||
                     facilityContactsByLinkedFacilityId.TryGetValue(parsedFacilityId, out facilityContact)))
                {
                    if (string.IsNullOrWhiteSpace(facilityName))
                        facilityName = ResolveFacilityContactName(facilityContact);
                }
                else if (string.IsNullOrWhiteSpace(facilityId) &&
                         !string.IsNullOrWhiteSpace(facilityName) &&
                         facilityContactsByName.TryGetValue(facilityName.Trim(), out var facilityContactByName))
                {
                    facilityName = ResolveFacilityContactName(facilityContactByName);
                }

                return NormalizeDashboardSummaryKey(facilityName);
            })
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Count(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<DashboardLienReportSummary> BuildDashboardLienReportSummaryAsync(
        IQueryable<Liens.Domain.Entities.Lien> lienQuery,
        LiensDbContext db,
        Guid tenantId,
        CancellationToken ct)
    {
        var rawStatusCounts = await lienQuery
            .GroupBy(item => item.Status)
            .Select(group => new
            {
                Status = group.Key,
                Count = group.Count(),
            })
            .ToListAsync(ct);
        var statusCounts = rawStatusCounts
            .GroupBy(item => MapDashboardLienBusinessStatus(item.Status), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Count), StringComparer.OrdinalIgnoreCase);

        var medicalCodeQuery =
            from servicingItem in db.ServicingItems.AsNoTracking()
            join lien in lienQuery on servicingItem.LienId equals (Guid?)lien.Id
            where servicingItem.TenantId == tenantId && servicingItem.TaskType == "LegacyMedicalCode"
            select new
            {
                lien.Status,
                servicingItem.Notes,
            };

        var amountsByStatus = statusCounts.Keys.ToDictionary(
            status => status,
            _ => (Purchase: 0m, Billing: 0m),
            StringComparer.OrdinalIgnoreCase);
        await foreach (var item in medicalCodeQuery.AsAsyncEnumerable().WithCancellation(ct))
        {
            var status = MapDashboardLienBusinessStatus(item.Status);
            var amounts = amountsByStatus.GetValueOrDefault(status);
            var fields = ParseLegacyNoteFields(item.Notes);
            if (decimal.TryParse(
                fields.GetValueOrDefault("purchaseAmount", string.Empty),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out var purchase))
            {
                amounts.Purchase += purchase;
            }
            if (decimal.TryParse(
                fields.GetValueOrDefault("billingAmount", string.Empty),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out var billing))
            {
                amounts.Billing += billing;
            }
            amountsByStatus[status] = amounts;
        }

        var statusAmounts = amountsByStatus.ToDictionary(
            pair => pair.Key,
            pair => new DashboardAmountSummary
            {
                Purchase = pair.Value.Purchase,
                Billing = pair.Value.Billing,
            },
            StringComparer.OrdinalIgnoreCase);

        return new DashboardLienReportSummary
        {
            TotalCount = statusCounts.Values.Sum(),
            TotalPurchaseAmount = amountsByStatus.Values.Sum(item => item.Purchase),
            TotalBillingAmount = amountsByStatus.Values.Sum(item => item.Billing),
            StatusCounts = statusCounts,
            StatusAmounts = statusAmounts,
        };
    }

    private static string MapDashboardLienBusinessStatus(string status) => status switch
    {
        LienStatus.Cancelled or LienStatus.Declined => "Rejected",
        LienStatus.Settled or LienStatus.Withdrawn => "Closed",
        _ => "Open",
    };

    private static bool IsStandaloneFacilityContact(Liens.Domain.Entities.Contact contact) =>
        (string.Equals(contact.ContactType, ContactType.Facility, StringComparison.Ordinal) ||
         string.Equals(contact.ContactType, ContactType.MedicalFacility, StringComparison.Ordinal)) &&
        string.IsNullOrWhiteSpace(contact.ContactSubtype);

    private static string ResolveFacilityContactName(Liens.Domain.Entities.Contact contact)
        => string.IsNullOrWhiteSpace(contact.Organization)
            ? contact.DisplayName
            : contact.Organization.Trim();

    private static IEnumerable<string> GetDashboardContactLookupNames(Liens.Domain.Entities.Contact contact)
    {
        if (!string.IsNullOrWhiteSpace(contact.Organization))
            yield return contact.Organization.Trim();

        if (!string.IsNullOrWhiteSpace(contact.DisplayName))
            yield return contact.DisplayName.Trim();
    }

    private static IEnumerable<string> GetFacilityContactLookupNames(Liens.Domain.Entities.Contact contact)
    {
        if (!string.IsNullOrWhiteSpace(contact.Organization))
            yield return contact.Organization.Trim();

        if (!string.IsNullOrWhiteSpace(contact.DisplayName))
            yield return contact.DisplayName.Trim();
    }

    private static (string Id, string Name) ResolveDashboardLawFirm(
        string? rawLawFirmId,
        string? rawLawFirmName,
        Guid orgId,
        IReadOnlyDictionary<Guid, Liens.Domain.Entities.Contact> lawFirmById,
        IReadOnlyDictionary<Guid, Liens.Domain.Entities.Contact> lawFirmByOrgId,
        IReadOnlyDictionary<string, Liens.Domain.Entities.Contact> lawFirmByName)
    {
        var resolved = ResolveDashboardContactByIdOrName(rawLawFirmId, rawLawFirmName, lawFirmById, lawFirmByName);
        if (!string.IsNullOrWhiteSpace(resolved.Id) || !string.IsNullOrWhiteSpace(resolved.Name))
            return resolved;

        if (lawFirmByOrgId.TryGetValue(orgId, out var lawFirmContact))
            return (lawFirmContact.Id.ToString(), ResolveDashboardContactName(lawFirmContact));

        return (orgId.ToString(), rawLawFirmName?.Trim() ?? string.Empty);
    }

    private static (string Id, string Name) ResolveDashboardContactByIdOrName(
        string? rawId,
        string? rawName,
        IReadOnlyDictionary<Guid, Liens.Domain.Entities.Contact> contactsById,
        IReadOnlyDictionary<string, Liens.Domain.Entities.Contact> contactsByName)
    {
        var trimmedName = rawName?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(trimmedName) &&
            contactsByName.TryGetValue(trimmedName, out var matchedByName))
        {
            return (matchedByName.Id.ToString(), ResolveDashboardContactName(matchedByName));
        }

        if (Guid.TryParse(rawId, out var parsedId) &&
            contactsById.TryGetValue(parsedId, out var matchedById))
        {
            return (matchedById.Id.ToString(), ResolveDashboardContactName(matchedById));
        }

        return (rawId?.Trim() ?? string.Empty, trimmedName);
    }

    private static string ResolveDashboardContactName(Liens.Domain.Entities.Contact contact)
        => FirstNonEmpty(contact.Organization, contact.DisplayName) ?? string.Empty;

    private static void AddDashboardContactId(string? value, ISet<Guid> contactIds)
    {
        if (Guid.TryParse(value, out var contactId))
            contactIds.Add(contactId);
    }

    private static void AddDashboardContactName(string? value, ISet<string> contactNames)
    {
        if (!string.IsNullOrWhiteSpace(value))
            contactNames.Add(value.Trim());
    }

    private static IQueryable<Liens.Domain.Entities.Case> ApplyDashboardCaseDatabaseFilter(
        IQueryable<Liens.Domain.Entities.Case> query,
        ReportFilterRequest? request)
    {
        if (string.IsNullOrWhiteSpace(request?.FilterType) || string.IsNullOrWhiteSpace(request.FilterId))
            return query;

        var filterType = request.FilterType.Trim().ToLowerInvariant();
        var filterId = request.FilterId.Trim();
        return filterType switch
        {
            "case" or "caseid" when Guid.TryParse(filterId, out var caseId)
                => query.Where(item => item.Id == caseId),
            "case" or "caseid"
                => query.Where(item => item.CaseNumber == filterId),
            "status" or "casestatus"
                => query.Where(item => item.Status == filterId),
            _ => query,
        };
    }

    private static IQueryable<Liens.Domain.Entities.Lien> ApplyDashboardLienDatabaseFilter(
        IQueryable<Liens.Domain.Entities.Lien> query,
        ReportFilterRequest? request)
    {
        if (string.IsNullOrWhiteSpace(request?.FilterType) || string.IsNullOrWhiteSpace(request.FilterId))
            return query;

        var filterType = request.FilterType.Trim().ToLowerInvariant();
        var filterId = request.FilterId.Trim();
        if (filterType is "lien" or "lienid")
        {
            return Guid.TryParse(filterId, out var lienId)
                ? query.Where(item => item.Id == lienId)
                : query.Where(item => item.LienNumber == filterId);
        }

        if ((filterType is "case" or "caseid") && Guid.TryParse(filterId, out var caseId))
            return query.Where(item => item.CaseId == caseId);

        if ((filterType is "fundingcompany" or "fundingcompanyid") && Guid.TryParse(filterId, out _))
            return query.Where(item => item.ExternalReference == filterId);

        if (filterType is not ("status" or "lienstatus"))
            return query;

        return filterId.ToLowerInvariant() switch
        {
            "rejected" => query.Where(item =>
                item.Status == LienStatus.Cancelled || item.Status == LienStatus.Declined),
            "closed" => query.Where(item =>
                item.Status == LienStatus.Settled || item.Status == LienStatus.Withdrawn),
            "open" => query.Where(item =>
                item.Status != LienStatus.Cancelled &&
                item.Status != LienStatus.Declined &&
                item.Status != LienStatus.Settled &&
                item.Status != LienStatus.Withdrawn),
            _ => query,
        };
    }

    private static bool TryResolveDashboardLienReportPeriod(
        ReportFilterRequest? request,
        out DateTime periodStart,
        out DateTime periodEnd)
    {
        periodStart = default;
        periodEnd = default;

        var startDate = string.IsNullOrWhiteSpace(request?.purchaseDateFrom)
            ? request?.startDate
            : request?.purchaseDateFrom;
        var endDate = string.IsNullOrWhiteSpace(request?.purchaseDateTo)
            ? request?.endDate
            : request?.purchaseDateTo;

        var hasStart = !string.IsNullOrWhiteSpace(startDate);
        var hasEnd = !string.IsNullOrWhiteSpace(endDate);
        if (!hasStart && !hasEnd)
            return false;

        if (hasStart != hasEnd)
            return false;

        if (!TryResolveDashboardMetricPeriod(
            startDate,
            endDate,
            out var resolvedStart,
            out var resolvedEnd,
            out _))
        {
            return false;
        }

        if (!resolvedStart.HasValue || !resolvedEnd.HasValue)
            return false;

        periodStart = resolvedStart.Value;
        periodEnd = resolvedEnd.Value;
        return true;
    }

    private static (int Page, int Limit) NormalizeDashboardReportPaging(ReportFilterRequest? request)
    {
        var page = request?.Page ?? 1;
        var limit = request?.Limit ?? 0;

        if (page < 1)
            page = 1;
        if (limit < 1)
            return (1, 0);

        return (page, limit);
    }

    // ── Dashboard reports ─────────────────────────────────────────────────────
    private static DashboardReportResult<T> BuildDashboardReportResult<T>(
        List<T> rows,
        int page,
        int limit,
        bool includeAllItems,
        Func<T, string> statusSelector,
        Func<T, decimal>? purchaseSelector = null,
        Func<T, decimal>? billingSelector = null,
        Func<T, string>? allocationSelector = null)
    {
        var statusCounts = rows
            .GroupBy(row => NormalizeDashboardSummaryKey(statusSelector(row)), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var statusAmounts = rows
            .GroupBy(row => NormalizeDashboardSummaryKey(statusSelector(row)), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new DashboardAmountSummary
                {
                    Purchase = purchaseSelector is null ? 0m : group.Sum(purchaseSelector),
                    Billing = billingSelector is null ? 0m : group.Sum(billingSelector),
                },
                StringComparer.OrdinalIgnoreCase);
        var allocationCounts = allocationSelector is null
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : rows
                .GroupBy(row => NormalizeDashboardSummaryKey(allocationSelector(row)), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        var pageSize = limit > 0 ? limit : rows.Count;
        var items = rows;
        if (!includeAllItems && limit > 0)
        {
            var skip = (long)(page - 1) * limit;
            items = skip >= rows.Count
                ? []
                : rows.Skip((int)skip).Take(limit).ToList();
        }

        return new DashboardReportResult<T>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = rows.Count,
            TotalPurchaseAmount = purchaseSelector is null ? 0m : rows.Sum(purchaseSelector),
            TotalBillingAmount = billingSelector is null ? 0m : rows.Sum(billingSelector),
            StatusCounts = statusCounts,
            StatusAmounts = statusAmounts,
            AllocationCounts = allocationCounts,
        };
    }

    private static string NormalizeDashboardSummaryKey(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();

    private static bool MatchesDashboardCaseFilter(ReportFilterRequest? request, DashboardCaseReportRow row)
    {
        if (string.IsNullOrWhiteSpace(request?.FilterType) || string.IsNullOrWhiteSpace(request.FilterId))
            return true;

        var filterType = request.FilterType.Trim().ToLowerInvariant();
        var filterId = request.FilterId.Trim();

        return filterType switch
        {
            "case" or "caseid" => MatchesFilterValue(filterId, row.Id.ToString(), row.CaseNumber),
            "status" or "casestatus" => MatchesFilterValue(filterId, row.Status),
            "lawfirm" or "lawfirmid" => MatchesFilterValue(filterId, row.LawFirmId, row.LawFirm),
            "casemanager" or "casemanagerid" => MatchesFilterValue(filterId, row.CaseManagerId, row.CaseManager),
            "accidenttype" or "accidenttypeid" => MatchesFilterValue(filterId, row.AccidentTypeId, row.AccidentType),
            _ => true,
        };
    }

    private static bool MatchesDashboardLienFilter(ReportFilterRequest? request, DashboardLienReportRow row)
    {
        if (string.IsNullOrWhiteSpace(request?.FilterType) || string.IsNullOrWhiteSpace(request.FilterId))
            return true;

        var filterType = request.FilterType.Trim().ToLowerInvariant();
        var filterId = request.FilterId.Trim();

        return filterType switch
        {
            "lien" or "lienid" => MatchesFilterValue(filterId, row.Id.ToString(), row.LienNumber),
            "case" or "caseid" => MatchesFilterValue(
                filterId,
                row.CaseId,
                row.CaseNumber,
                row.CaseRecordId?.ToString() ?? string.Empty),
            "status" or "lienstatus" => MatchesFilterValue(filterId, row.Status),
            "lawfirm" or "lawfirmid" => MatchesFilterValue(filterId, row.LawFirmId, row.LawFirm),
            "casemanager" or "casemanagerid" => MatchesFilterValue(filterId, row.CaseManagerId, row.CaseManager),
            "medicalprovider" or "medicalproviderid" => MatchesFilterValue(filterId, row.MedicalProviderId, row.MedicalProvider),
            "medicalfacility" or "facility" or "facilityid" => MatchesFilterValue(filterId, row.FacilityId, row.FacilityName),
            "fundingcompany" or "fundingcompanyid" => MatchesFilterValue(filterId, row.FundingCompanyId, row.FundingCompany),
            _ => true,
        };
    }

    private static bool MatchesFilterValue(string filterId, params string[] candidates)
    {
        return candidates.Any(candidate =>
            !string.IsNullOrWhiteSpace(candidate) &&
            string.Equals(candidate.Trim(), filterId, StringComparison.OrdinalIgnoreCase));
    }

    private static Task<IResult> GetLienReportCsv(ICurrentRequestContext _) =>
        Task.FromResult<IResult>(Results.Ok(new { data = string.Empty }));
    private static Task<IResult> GetCaseReportCsv(ICurrentRequestContext _) =>
        Task.FromResult<IResult>(Results.Ok(new { data = string.Empty }));
    private static Task<IResult> GetLawFirmCaseReportCsv(ICurrentRequestContext _) =>
        Task.FromResult<IResult>(Results.Ok(new { data = string.Empty }));
    private static Task<IResult> GetMedicalProviderCaseReportCsv(ICurrentRequestContext _) =>
        Task.FromResult<IResult>(Results.Ok(new { data = string.Empty }));

    // ── CSV import stubs ──────────────────────────────────────────────────────
    private sealed class ImportCsvRequest
    {
        public string? FileContent { get; init; }
        public string? FileName    { get; init; }
    }

    private static Task<IResult> ImportCsv(
        ImportCsvRequest _req,
        ICurrentRequestContext _ctx,
        CancellationToken _ct = default)
        => Task.FromResult(Results.StatusCode(501));

    // ── Document type ─────────────────────────────────────────────────────────
    private sealed class DocumentTypeRequest
    {
        public string Name { get; init; } = string.Empty;
    }

    private static Task<IResult> AddDocumentType(
        DocumentTypeRequest _req,
        ICurrentRequestContext _ctx,
        CancellationToken _ct = default)
        => Task.FromResult(Results.StatusCode(501));

    // ── Global search ─────────────────────────────────────────────────────────
    private sealed class GlobalSearchRequest
    {
        public string? Query { get; init; }
        public string? Keyword { get; init; }
        public int Page      { get; init; } = 1;
        public int Limit     { get; init; } = 20;
    }

    private sealed class GlobalSearchResponse
    {
        public required PaginatedResult<CaseResponse> Cases { get; init; }
        public required PaginatedResult<LienResponse> Liens { get; init; }
        public required List<GlobalSearchPlaintiffResponse> Plaintiffs { get; init; }
        public required List<GlobalSearchCompanyResponse> LawFirms { get; init; }
        public required List<GlobalSearchCompanyResponse> MedicalFacilities { get; init; }
        public required List<GlobalSearchCompanyResponse> MedicalProviders { get; init; }
        public required List<GlobalSearchCompanyResponse> FundingCompanies { get; init; }

        [JsonPropertyName("Leads")]
        public required List<GlobalSearchCompanyResponse> Leads { get; init; }

        public required List<GlobalSearchServicingResponse> Servicing { get; init; }
    }

    private sealed class GlobalSearchPlaintiffResponse
    {
        public string CaseId { get; init; } = string.Empty;
        public string PlaintiffName { get; init; } = string.Empty;
        public string CaseCode { get; init; } = string.Empty;
        public string DateOfLoss { get; init; } = string.Empty;
        public string DateOfBirth { get; init; } = string.Empty;
    }

    private sealed class GlobalSearchCompanyResponse
    {
        public string? CaseId { get; init; }
        public string ContactId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string ActiveCases { get; init; } = string.Empty;
        public string Address { get; init; } = string.Empty;
    }

    private sealed class GlobalSearchServicingResponse
    {
        public string CaseId { get; init; } = string.Empty;
        public string PlaintiffName { get; init; } = string.Empty;
        public string CaseCode { get; init; } = string.Empty;
        public string CurrentStatus { get; init; } = string.Empty;
        public string SettlementStatus { get; init; } = string.Empty;
    }

    private static async Task<IResult> GlobalSearch(
        GlobalSearchRequest request,
        ICaseService caseService,
        ILienService lienService,
        IContactService contactService,
        IFacilityService facilityService,
        ICurrentRequestContext ctx,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenantId(ctx);
        var query = string.IsNullOrWhiteSpace(request.Query)
            ? request.Keyword
            : request.Query;
        var cases = await caseService.SearchAsync(
            tenantId, query, null, request.Page, request.Limit, null, ct);
        var liens = await lienService.SearchAsync(
            tenantId, query, null, null, null, null,
            request.Page, request.Limit, ct);

        var lawFirms = await contactService.SearchAsync(
            tenantId, query, ContactType.LawFirm, null,
            request.Page, request.Limit, ct: ct);
        var medicalProviders = await contactService.SearchAsync(
            tenantId, query, ContactType.Provider, null,
            request.Page, request.Limit, ct: ct);
        var legacyFundingCompanies = await contactService.SearchAsync(
            tenantId, query, ContactType.LienHolder, null,
            request.Page, request.Limit, ct: ct);
        var fundingCompanies = await contactService.SearchAsync(
            tenantId, query, ContactType.FundingCompany, null,
            request.Page, request.Limit, ct: ct);
        var leads = await contactService.SearchAsync(
            tenantId, query, ContactType.Lead, null,
            request.Page, request.Limit, ct: ct);
        var medicalFacilities = await facilityService.SearchAsync(
            tenantId, query, null, request.Page, request.Limit, ct);

        var combinedFundingCompanies = legacyFundingCompanies.Items
            .Concat(fundingCompanies.Items)
            .DistinctBy(contact => contact.Id)
            .OrderBy(contact => contact.DisplayName)
            .Take(cases.PageSize)
            .ToList();

        var response = new GlobalSearchResponse
        {
            Cases = cases,
            Liens = liens,
            Plaintiffs = cases.Items.Select(MapGlobalSearchPlaintiff).ToList(),
            LawFirms = lawFirms.Items.Select(MapGlobalSearchCompany).ToList(),
            MedicalFacilities = medicalFacilities.Items.Select(MapGlobalSearchFacility).ToList(),
            MedicalProviders = medicalProviders.Items.Select(MapGlobalSearchCompany).ToList(),
            FundingCompanies = combinedFundingCompanies.Select(MapGlobalSearchCompany).ToList(),
            Leads = leads.Items.Select(MapGlobalSearchCompany).ToList(),
            Servicing = cases.Items.Select(MapGlobalSearchServicing).ToList(),
        };

        return Results.Ok(response);
    }

    private static GlobalSearchPlaintiffResponse MapGlobalSearchPlaintiff(CaseResponse item)
        => new()
        {
            CaseId = item.Id.ToString(),
            PlaintiffName = item.ClientDisplayName,
            CaseCode = item.CaseNumber,
            DateOfLoss = FormatLegacyDate(item.DateOfIncident),
            DateOfBirth = FormatLegacyDate(item.ClientDob),
        };

    private static GlobalSearchCompanyResponse MapGlobalSearchCompany(ContactResponse item)
        => new()
        {
            ContactId = item.Id.ToString(),
            Name = string.IsNullOrWhiteSpace(item.Organization)
                ? item.DisplayName
                : item.Organization,
            ActiveCases = item.ActiveCases.ToString(CultureInfo.InvariantCulture),
            Address = FormatGlobalSearchAddress(
                item.AddressLine1,
                item.City,
                item.State,
                item.PostalCode),
        };

    private static GlobalSearchCompanyResponse MapGlobalSearchFacility(FacilityResponse item)
        => new()
        {
            ContactId = item.Id.ToString(),
            Name = item.Name,
            Address = FormatGlobalSearchAddress(
                item.AddressLine1,
                item.City,
                item.State,
                item.PostalCode),
        };

    private static GlobalSearchServicingResponse MapGlobalSearchServicing(CaseResponse item)
        => new()
        {
            CaseId = item.Id.ToString(),
            PlaintiffName = item.ClientDisplayName,
            CaseCode = item.CaseNumber,
            CurrentStatus = item.StatusLabel,
            SettlementStatus = item.SettlementStatus,
        };

    private static string FormatGlobalSearchAddress(
        string? addressLine1,
        string? city,
        string? state,
        string? postalCode)
    {
        var stateAndPostalCode = string.Join(" ",
            new[] { state, postalCode }.Where(value => !string.IsNullOrWhiteSpace(value)));

        return string.Join(", ",
            new[] { addressLine1, city, stateAndPostalCode }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
    }
}
