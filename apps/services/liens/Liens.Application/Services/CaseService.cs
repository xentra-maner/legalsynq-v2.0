using BuildingBlocks.Exceptions;
using Liens.Application.DTOs;
using Liens.Application.Interfaces;
using Liens.Application.Repositories;
using Liens.Application.Search;
using Liens.Domain.Entities;
using Liens.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Liens.Application.Services;

public sealed class CaseService : ICaseService
{
    private const string LegacyMetadataMarker = "[legacy-meta]";
    private readonly ICaseRepository           _caseRepo;
    private readonly ILienRepository           _lienRepo;
    private readonly IContactRepository        _contactRepo;
    private readonly ICompanyRepository        _companyRepo;
    private readonly ISettlementService        _settlementService;
    private readonly ILookupValueService       _lookupValueService;
    private readonly IAuditPublisher           _audit;
    private readonly ILienTaskGenerationDispatcher _taskGenDispatcher;
    private readonly ILogger<CaseService>          _logger;

    public CaseService(
        ICaseRepository caseRepo,
        ILienRepository lienRepo,
        IContactRepository contactRepo,
        ICompanyRepository companyRepo,
        ISettlementService settlementService,
        ILookupValueService lookupValueService,
        IAuditPublisher audit,
        ILienTaskGenerationDispatcher taskGenDispatcher,
        ILogger<CaseService> logger)
    {
        _caseRepo          = caseRepo;
        _lienRepo          = lienRepo;
        _contactRepo       = contactRepo;
        _companyRepo       = companyRepo;
        _settlementService = settlementService;
        _lookupValueService = lookupValueService;
        _audit             = audit;
        _taskGenDispatcher = taskGenDispatcher;
        _logger            = logger;
    }

    public async Task<PaginatedResult<CaseResponse>> SearchAsync(
        Guid tenantId, string? search, string? status, int page, int pageSize,
        Guid? orgId = null,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var hasKeyword = !string.IsNullOrWhiteSpace(search);
        var keyword = search?.Trim();
        var (items, totalCount) = await _caseRepo.SearchAsync(
            tenantId,
            search,
            status,
            page,
            pageSize,
            orgId,
            ct: ct);
        var useFuzzyFallback = hasKeyword && totalCount == 0;

        if (useFuzzyFallback)
        {
            (items, totalCount) = await _caseRepo.SearchAsync(
                tenantId,
                null,
                status,
                1,
                FuzzySearchScorer.CandidateLimit,
                orgId,
                ct: ct);
        }

        if (useFuzzyFallback)
        {
            var matches = items
                .Select(item => new { Item = item, Score = GetCaseKeywordScore(item, keyword!) })
                .Where(match => FuzzySearchScorer.IsAccepted(match.Score))
                .OrderByDescending(match => match.Score.Value)
                .ThenByDescending(match => match.Item.Id)
                .ToList();

            totalCount = matches.Count;
            items = matches
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(match => match.Item)
                .ToList();
        }

        return new PaginatedResult<CaseResponse>
        {
            Items = items.Select(item => MapToResponse(item)).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
        };
    }

    public async Task<PaginatedResult<CaseResponse>> SearchV3Async(
        Guid tenantId,
        string? keyword,
        string? statusId,
        int page,
        int limit,
        string? sortBy,
        string? sortDirection,
        Guid? lawFirmOrgId = null,
        string? accidentTypeId = null,
        string? caseManagerId = null,
        string? lawFirmIds = null,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (limit < 1) limit = 20;
        if (limit > 100) limit = 100;

        var hasKeyword = !string.IsNullOrWhiteSpace(keyword);
        var normalizedKeyword = keyword?.Trim();
        var (items, totalCount) = await _caseRepo.SearchAsync(
            tenantId,
            hasKeyword ? null : keyword,
            statusId,
            hasKeyword ? 1 : page,
            hasKeyword ? FuzzySearchScorer.CandidateLimit : limit,
            lawFirmOrgId,
            hasKeyword ? null : sortBy,
            hasKeyword ? null : sortDirection,
            accidentTypeId,
            caseManagerId,
            lawFirmIds,
            ct);

        if (hasKeyword)
        {
            for (var candidatePage = 2; items.Count < totalCount; candidatePage++)
            {
                var (nextPage, _) = await _caseRepo.SearchAsync(
                    tenantId,
                    null,
                    statusId,
                    candidatePage,
                    FuzzySearchScorer.CandidateLimit,
                    lawFirmOrgId,
                    null,
                    null,
                    accidentTypeId,
                    caseManagerId,
                    lawFirmIds,
                    ct);
                if (nextPage.Count == 0)
                    break;

                items.AddRange(nextPage);
            }
        }

        var lawFirmContacts = await _contactRepo.GetAllByTypeAsync(
            tenantId,
            ContactType.LawFirm,
            isActive: null,
            ct);

        var lawFirmById = lawFirmContacts.ToDictionary(c => c.Id);
        var lawFirmByOrgId = lawFirmContacts
            .GroupBy(c => c.OrgId)
            .ToDictionary(g => g.Key, g => g.First());
        var lawFirmCompanyById = (await _companyRepo.GetCompaniesByIdsAsync(
                tenantId,
                items
                    .Where(item => item.HandlingLawFirmCompanyId.HasValue)
                    .Select(item => item.HandlingLawFirmCompanyId!.Value)
                    .Distinct()
                    .ToList(),
                ct))
            .ToDictionary(company => company.Id);

        var needsCaseManagers = items.Any(item =>
            !string.IsNullOrWhiteSpace(GetMetadataValue(ParseCaseMetadata(item.Notes), "caseManagerId")));

        Dictionary<Guid, Contact> caseManagerById = new();
        if (needsCaseManagers)
        {
            caseManagerById = (await _contactRepo.GetAllByTypeAsync(
                    tenantId,
                    contactType: null,
                    isActive: null,
                    ct))
                .ToDictionary(c => c.Id);
        }

        var candidates = items.Select(item =>
        {
            var metadata = ParseCaseMetadata(item.Notes);
            var lawFirmId = GetMetadataValue(metadata, "lawFirmId");
            var caseManagerIdValue = GetMetadataValue(metadata, "caseManagerId");

            return new CaseSearchCandidate(
                item,
                ResolveLawFirmName(
                    item.OrgId,
                    item.HandlingLawFirmCompanyId,
                    lawFirmId,
                    lawFirmById,
                    lawFirmByOrgId,
                    lawFirmCompanyById),
                ResolveCaseManagerName(caseManagerIdValue, caseManagerById));
        }).ToList();

        if (hasKeyword)
        {
            var matches = candidates
                .Select(candidate => new
                {
                    Candidate = candidate,
                    Score = GetCaseKeywordScore(
                        candidate.Case,
                        normalizedKeyword!,
                        candidate.LawFirm,
                        candidate.CaseManager),
                })
                .Where(match => FuzzySearchScorer.IsAccepted(match.Score))
                .OrderByDescending(match => match.Score.Value)
                .ThenByDescending(match => match.Candidate.Case.Id)
                .ToList();

            totalCount = matches.Count;
            candidates = matches
                .Skip((page - 1) * limit)
                .Take(limit)
                .Select(match => match.Candidate)
                .ToList();
        }

        return new PaginatedResult<CaseResponse>
        {
            Items = candidates.Select(candidate => MapToResponse(
                candidate.Case,
                lawFirm: candidate.LawFirm,
                caseManager: candidate.CaseManager)).ToList(),
            Page = page,
            PageSize = limit,
            TotalCount = totalCount,
        };
    }

    public async Task<CaseResponse?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        var entity = await _caseRepo.GetByIdAsync(tenantId, id, ct);
        return entity is null ? null : await MapToResponseAsync(tenantId, entity, ct);
    }

    public async Task<CaseResponse?> GetByCaseNumberAsync(Guid tenantId, string caseNumber, CancellationToken ct = default)
    {
        var entity = await _caseRepo.GetByCaseNumberAsync(tenantId, caseNumber, ct);
        return entity is null ? null : await MapToResponseAsync(tenantId, entity, ct);
    }

    public async Task<CaseDuplicateCheckResponse> CheckDuplicatesAsync(
        Guid tenantId,
        CaseDuplicateCheckRequest request,
        CancellationToken ct = default)
    {
        var matches = await FindDuplicateMatchesAsync(tenantId, request, ct);
        return new CaseDuplicateCheckResponse
        {
            IsDuplicate = matches.Count > 0,
            Message = matches.Count > 0
                ? "A case with similar information already exists. Would you like to view the existing case?"
                : string.Empty,
            Matches = matches.Select(MapDuplicateMatch).ToList(),
        };
    }

    public async Task<CaseResponse> CreateAsync(
        Guid tenantId, Guid orgId, Guid actingUserId,
        CreateCaseRequest request, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.ClientFirstName))
            errors.Add("clientFirstName", ["Client first name is required."]);
        if (string.IsNullOrWhiteSpace(request.ClientLastName))
            errors.Add("clientLastName", ["Client last name is required."]);
        if (errors.Count > 0)
            throw new ValidationException("One or more required fields are missing.", errors);

        if (!string.IsNullOrWhiteSpace(request.ExternalReference))
        {
            var idempotent = await _caseRepo.GetByExternalReferenceAsync(
                tenantId, request.ExternalReference.Trim(), ct);
            if (idempotent is not null)
                return await MapToResponseAsync(tenantId, idempotent, ct);
        }

        var duplicateCheck = await CheckDuplicatesAsync(
            tenantId,
            new CaseDuplicateCheckRequest
            {
                ClientFirstName = request.ClientFirstName,
                ClientLastName = request.ClientLastName,
                ClientDob = request.ClientDob,
                DateOfIncident = request.DateOfIncident,
            },
            ct);
        if (duplicateCheck.IsDuplicate)
            throw new ConflictException(
                "A case with similar information already exists. Would you like to view the existing case?",
                "CASE_POTENTIAL_DUPLICATE");

        var caseNumber = string.IsNullOrWhiteSpace(request.CaseNumber)
            ? await GenerateCaseNumberAsync(tenantId, ct)
            : request.CaseNumber.Trim();

        var existing = await _caseRepo.GetByCaseNumberAsync(tenantId, caseNumber, ct);
        var isReserved = await _caseRepo.IsCaseNumberReservedAsync(tenantId, caseNumber, ct);
        if (existing is not null || isReserved)
            throw new ConflictException(
                $"Case number '{caseNumber}' has already been used and cannot be reused.",
                "CASE_NUMBER_DUPLICATE");

        var entity = Case.Create(
            tenantId: tenantId,
            orgId: orgId,
            caseNumber: caseNumber,
            clientFirstName: request.ClientFirstName,
            clientLastName: request.ClientLastName,
            createdByUserId: actingUserId,
            externalReference: request.ExternalReference,
            title: request.Title,
            clientDob: request.ClientDob,
            clientPhone: request.ClientPhone,
            clientEmail: request.ClientEmail,
            clientAddress: request.ClientAddress,
            dateOfIncident: request.DateOfIncident,
            insuranceCarrier: request.InsuranceCarrier,
            policyNumber: request.PolicyNumber,
            claimNumber: request.ClaimNumber,
            description: request.Description,
            notes: SerializeCaseNotes(
                request.Notes,
                BuildMetadata(
                    sex: request.Sex,
                    caseType: request.CaseType,
                    currentMedicalStatus: request.CurrentMedicalStatus,
                    stateOfIncident: request.StateOfIncident,
                    trackingFollowUpDate: request.TrackingFollowUpDate,
                    leadId: request.LeadId,
                    shareCase: request.ShareCase,
                    minorComp: request.MinorComp,
                    caseDropped: request.CaseDropped,
                    childSupportLiens: request.ChildSupportLiens,
                    isUccFiled: request.IsUccFiled,
                    lawFirmId: request.LawFirmId,
                    accidentTypeId: request.AccidentTypeId,
                    caseManagerId: request.CaseManagerId,
                    statusLabel: request.StatusLabel)),
            clientAddressLine1: request.ClientStreetAddress,
            clientCity: request.ClientCity,
            clientState: request.ClientState,
            clientPostalCode: request.ClientZipcode,
            incidentState: request.StateOfIncident,
            currentMedicalStatus: request.CurrentMedicalStatus,
            trackingFollowUpDate: request.TrackingFollowUpDate,
            minorComp: ParseNullableCaseFlag(request.MinorComp),
            caseDropped: ParseNullableCaseFlag(request.CaseDropped));

        await _caseRepo.AddAsync(entity, ct);

        _logger.LogInformation(
            "Case created: {CaseId} CaseNumber={CaseNumber} Tenant={TenantId}",
            entity.Id, entity.CaseNumber, tenantId);

        _audit.Publish(
            eventType: "liens.case.created",
            action: "create",
            description: $"Case '{entity.CaseNumber}' created",
            tenantId: tenantId,
            actorUserId: actingUserId,
            entityType: "Case",
            entityId: entity.Id.ToString());

        // Run task generation in an isolated scope so it never reuses the request DbContext.
        var caseId = entity.Id;
        var genContext = new TaskGenerationContext(
            TenantId:       tenantId,
            EventType:      Domain.Enums.TaskGenerationEventType.CaseCreated,
            EntityType:     "CASE",
            EntityId:       caseId,
            CaseId:         caseId,
            LienId:         null,
            WorkflowStageId: null,
            ActorUserId:    actingUserId);

        _taskGenDispatcher.Dispatch(genContext);

        return await MapToResponseAsync(tenantId, entity, ct);
    }

    private async Task<List<Case>> FindDuplicateMatchesAsync(
        Guid tenantId,
        CaseDuplicateCheckRequest request,
        CancellationToken ct)
    {
        if (request.ClientDob is null || request.DateOfIncident is null)
            return [];

        if (string.IsNullOrWhiteSpace(request.ClientFirstName) ||
            string.IsNullOrWhiteSpace(request.ClientLastName))
        {
            return [];
        }

        var candidates = await _caseRepo.GetPotentialDuplicateCandidatesAsync(
            tenantId,
            request.ClientDob.Value,
            request.DateOfIncident.Value,
            ct);

        return candidates
            .Where(candidate =>
                IsSimilarName(candidate.ClientFirstName, request.ClientFirstName) &&
                IsSimilarName(candidate.ClientLastName, request.ClientLastName))
            .OrderByDescending(candidate => GetNameSimilarityScore(candidate.ClientFirstName, request.ClientFirstName) +
                                           GetNameSimilarityScore(candidate.ClientLastName, request.ClientLastName))
            .ThenByDescending(candidate => candidate.CreatedAtUtc)
            .Take(5)
            .ToList();
    }

    private static CaseDuplicateMatchResponse MapDuplicateMatch(Case entity) => new()
    {
        Id = entity.Id,
        CaseNumber = entity.CaseNumber,
        ClientFirstName = entity.ClientFirstName,
        ClientLastName = entity.ClientLastName,
        ClientDisplayName = string.Join(" ", new[] { entity.ClientFirstName, entity.ClientLastName }
            .Where(value => !string.IsNullOrWhiteSpace(value))),
        ClientDob = entity.ClientDob,
        DateOfIncident = entity.DateOfIncident,
        Status = entity.Status,
    };

    private static bool IsSimilarName(string? existing, string? incoming) =>
        GetNameSimilarityScore(existing, incoming) >= 70 ||
        ContainsNormalized(existing, incoming) ||
        ContainsNormalized(incoming, existing);

    private static int GetNameSimilarityScore(string? existing, string? incoming)
    {
        var normalizedExisting = NormalizeName(existing);
        var normalizedIncoming = NormalizeName(incoming);
        if (string.IsNullOrWhiteSpace(normalizedExisting) || string.IsNullOrWhiteSpace(normalizedIncoming))
            return 0;

        if (normalizedExisting == normalizedIncoming)
            return 100;

        var distance = LevenshteinDistance(normalizedExisting, normalizedIncoming);
        var maxLength = Math.Max(normalizedExisting.Length, normalizedIncoming.Length);
        return maxLength == 0 ? 0 : (int)((1.0 - ((double)distance / maxLength)) * 100);
    }

    private static bool ContainsNormalized(string? source, string? target)
    {
        var normalizedSource = NormalizeName(source);
        var normalizedTarget = NormalizeName(target);
        return !string.IsNullOrWhiteSpace(normalizedSource) &&
               !string.IsNullOrWhiteSpace(normalizedTarget) &&
               normalizedSource.Length >= 3 &&
               normalizedTarget.Length >= 3 &&
               normalizedSource.Contains(normalizedTarget, StringComparison.Ordinal);
    }

    private static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
                normalized.Append(char.ToLowerInvariant(character));
        }

        return normalized.ToString();
    }

    private static int LevenshteinDistance(string source, string target)
    {
        if (source.Length == 0)
            return target.Length;
        if (target.Length == 0)
            return source.Length;

        var previous = Enumerable.Range(0, target.Length + 1).ToArray();
        var current = new int[target.Length + 1];

        for (var sourceIndex = 1; sourceIndex <= source.Length; sourceIndex++)
        {
            current[0] = sourceIndex;
            for (var targetIndex = 1; targetIndex <= target.Length; targetIndex++)
            {
                var substitutionCost = source[sourceIndex - 1] == target[targetIndex - 1] ? 0 : 1;
                current[targetIndex] = Math.Min(
                    Math.Min(current[targetIndex - 1] + 1, previous[targetIndex] + 1),
                    previous[targetIndex - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[target.Length];
    }

    private async Task<string> GenerateCaseNumberAsync(Guid tenantId, CancellationToken ct)
    {
        var yearPrefix = DateTime.UtcNow.ToString("yy");
        var prefix = $"{yearPrefix}-";
        var existingCases = await _caseRepo.GetByCaseNumberPrefixAsync(tenantId, prefix, ct);
        var reservedCaseNumbers = await _caseRepo.GetReservedCaseNumbersByPrefixAsync(tenantId, prefix, ct);
        var maxSequence = existingCases
            .Select(c => c.CaseNumber)
            .Concat(reservedCaseNumbers)
            .Select(caseNumber => TryGetCaseSequence(caseNumber, prefix))
            .Where(sequence => sequence.HasValue)
            .Select(sequence => sequence!.Value)
            .DefaultIfEmpty(0)
            .Max();

        return $"{prefix}{maxSequence + 1:00000}";
    }

    private static int? TryGetCaseSequence(string caseNumber, string prefix)
    {
        if (!caseNumber.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var suffix = caseNumber[prefix.Length..];
        return int.TryParse(suffix, out var sequence) ? sequence : null;
    }

    public async Task<CaseResponse> UpdateAsync(
        Guid tenantId, Guid id, Guid actingUserId,
        UpdateCaseRequest request, CancellationToken ct = default)
    {
        var entity = await _caseRepo.GetByIdAsync(tenantId, id, ct)
            ?? throw new NotFoundException($"Case '{id}' not found for tenant '{tenantId}'.");
        if (!CaseStatus.AllowsUpdates(entity.Status))
            throw new ConflictException("Closed and settled cases cannot be updated.");

        var noteBody = ExtractUserNotes(entity.Notes);
        var metadata = ParseCaseMetadata(entity.Notes);

        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.ClientFirstName))
            errors.Add("clientFirstName", ["Client first name is required."]);
        if (string.IsNullOrWhiteSpace(request.ClientLastName))
            errors.Add("clientLastName", ["Client last name is required."]);
        if (request.Status is not null && !CaseStatus.All.Contains(request.Status))
            errors.Add("status", [$"Invalid status: '{request.Status}'. Valid values: {string.Join(", ", CaseStatus.All)}"]);
        if (request.DemandAmount.HasValue && request.DemandAmount.Value < 0)
            errors.Add("demandAmount", ["Demand amount cannot be negative."]);
        if (request.SettlementAmount.HasValue && request.SettlementAmount.Value < 0)
            errors.Add("settlementAmount", ["Settlement amount cannot be negative."]);
        if (errors.Count > 0)
            throw new ValidationException("One or more fields are invalid.", errors);

        var mergedMetadata = MergeMetadata(
            metadata,
            request.Sex,
            request.CaseType,
            request.CurrentMedicalStatus,
            request.StateOfIncident,
            request.TrackingFollowUpDate,
            request.LeadId,
            request.ShareCase,
            request.MinorComp,
            request.CaseDropped,
            request.ChildSupportLiens,
            request.IsUccFiled,
            request.LawFirmId,
            request.PendingLawFirmId,
            request.AccidentTypeId,
            request.CaseManagerId,
            request.AttorneyId,
            request.SwitchedDate);
        ApplyStatusLabelMetadata(mergedMetadata, request.Status, request.StatusLabel);

        entity.Update(
            clientFirstName: request.ClientFirstName,
            clientLastName: request.ClientLastName,
            updatedByUserId: actingUserId,
            title: request.Title,
            externalReference: request.ExternalReference,
            clientDob: request.ClientDob,
            clientPhone: request.ClientPhone,
            clientEmail: request.ClientEmail,
            clientAddress: request.ClientAddress,
            dateOfIncident: request.DateOfIncident,
            insuranceCarrier: request.InsuranceCarrier,
            policyNumber: request.PolicyNumber,
            claimNumber: request.ClaimNumber,
            description: request.Description,
            notes: SerializeCaseNotes(request.Notes ?? noteBody, mergedMetadata),
            clientAddressLine1: request.ClientStreetAddress ?? entity.ClientAddressLine1,
            clientCity: request.ClientCity ?? entity.ClientCity,
            clientState: request.ClientState ?? entity.ClientState,
            clientPostalCode: request.ClientZipcode ?? entity.ClientPostalCode,
            incidentState: request.StateOfIncident ?? entity.IncidentState,
            currentMedicalStatus: request.CurrentMedicalStatus ?? entity.CurrentMedicalStatus,
            trackingFollowUpDate: request.TrackingFollowUpDate ?? entity.TrackingFollowUpDate,
            minorComp: request.MinorComp is null ? entity.MinorComp : ParseNullableCaseFlag(request.MinorComp),
            caseDropped: request.CaseDropped is null ? entity.CaseDropped : ParseNullableCaseFlag(request.CaseDropped),
            attorneyContactPersonId: entity.AttorneyContactPersonId);

        if (request.Status is not null && request.Status != entity.Status)
            entity.TransitionStatus(request.Status, actingUserId);

        if (request.DemandAmount.HasValue)
            entity.SetDemandAmount(request.DemandAmount.Value, actingUserId);

        if (request.SettlementAmount.HasValue)
            entity.SetSettlementAmount(request.SettlementAmount.Value, actingUserId);

        await _caseRepo.UpdateAsync(entity, ct);

        _logger.LogInformation(
            "Case updated: {CaseId} Tenant={TenantId}", entity.Id, tenantId);

        _audit.Publish(
            eventType: "liens.case.updated",
            action: "update",
            description: $"Case '{entity.CaseNumber}' updated",
            tenantId: tenantId,
            actorUserId: actingUserId,
            entityType: "Case",
            entityId: entity.Id.ToString());

        return await MapToResponseAsync(tenantId, entity, ct);
    }

    private async Task<CaseResponse> MapToResponseAsync(
        Guid tenantId,
        Case entity,
        CancellationToken ct)
    {
        var metadata = ParseCaseMetadata(entity.Notes);
        var lawFirmId = GetMetadataValue(metadata, "lawFirmId");
        var caseManagerId = GetMetadataValue(metadata, "caseManagerId");

        string? lawFirm = null;
        if (Guid.TryParse(lawFirmId, out var parsedLawFirmId))
        {
            var lawFirmContact = await _contactRepo.GetByIdAsync(tenantId, parsedLawFirmId, ct);
            lawFirm = FirstNonEmpty(lawFirmContact?.Organization, lawFirmContact?.DisplayName);
        }

        if (string.IsNullOrWhiteSpace(lawFirm) && entity.HandlingLawFirmCompanyId.HasValue)
        {
            var lawFirmCompany = (await _companyRepo.GetCompaniesByIdsAsync(
                    tenantId,
                    [entity.HandlingLawFirmCompanyId.Value],
                    ct))
                .FirstOrDefault();
            lawFirm = lawFirmCompany?.Name;
        }

        if (string.IsNullOrWhiteSpace(lawFirm))
        {
            var defaultLawFirm = (await _contactRepo.GetAllByTypeAsync(
                    tenantId,
                    ContactType.LawFirm,
                    isActive: null,
                    ct))
                .FirstOrDefault(contact => contact.OrgId == entity.OrgId);

            lawFirm = FirstNonEmpty(defaultLawFirm?.Organization, defaultLawFirm?.DisplayName);
        }

        string? caseManager = null;
        if (Guid.TryParse(caseManagerId, out var parsedCaseManagerId))
        {
            var caseManagerContact = await _contactRepo.GetByIdAsync(tenantId, parsedCaseManagerId, ct);
            caseManager = caseManagerContact?.DisplayName;
        }

        var (lienStatus, lienStatusId) = await ResolveLienStatusAsync(
            tenantId,
            entity.Id,
            ct);
        var (settlementStatus, settlementStatusId) = await ResolveSettlementStatusAsync(
            tenantId,
            entity.Id,
            ct);

        // The UI renders the settlement status as a "<lienStatus>-<settlementStatus>" suffix, so a
        // settlement status that just repeats the lien-status rollup (e.g. both "Closed") produces a
        // redundant "Closed-Closed" chip. Suppress it in that case.
        if (!string.IsNullOrEmpty(settlementStatus) &&
            string.Equals(settlementStatus, lienStatus, StringComparison.OrdinalIgnoreCase))
        {
            settlementStatus = string.Empty;
            settlementStatusId = string.Empty;
        }

        return MapToResponse(
            entity,
            lawFirm: lawFirm,
            caseManager: caseManager,
            lienStatus: lienStatus,
            lienStatusId: lienStatusId,
            settlementStatus: settlementStatus,
            settlementStatusId: settlementStatusId);
    }

    public async Task<bool> ReassignLawFirmAsync(
        Guid tenantId,
        Guid caseId,
        Guid lawFirmOrgId,
        Guid actingUserId,
        CancellationToken ct = default)
    {
        var entity = await _caseRepo.GetByIdAsync(tenantId, caseId, ct);
        if (entity is null)
            return false;

        entity.ReassignLawFirm(lawFirmOrgId, actingUserId);
        await _caseRepo.UpdateAsync(entity, ct);

        _logger.LogInformation(
            "Case law firm reassigned: {CaseId} NewOrg={OrgId} Tenant={TenantId}",
            entity.Id, lawFirmOrgId, tenantId);

        _audit.Publish(
            eventType: "liens.case.reassigned.lawfirm",
            action: "update",
            description: $"Case '{entity.CaseNumber}' reassigned to law firm '{lawFirmOrgId}'",
            tenantId: tenantId,
            actorUserId: actingUserId,
            entityType: "Case",
            entityId: entity.Id.ToString());

        return true;
    }

    public async Task<bool> ReassignCaseManagerAsync(
        Guid tenantId,
        Guid caseId,
        Guid caseManagerId,
        Guid actingUserId,
        CancellationToken ct = default)
    {
        var entity = await _caseRepo.GetByIdAsync(tenantId, caseId, ct);
        if (entity is null)
            return false;

        entity.ReassignCaseManager(caseManagerId, actingUserId);
        await _caseRepo.UpdateAsync(entity, ct);

        _logger.LogInformation(
            "Case manager reassigned: {CaseId} CaseManager={CaseManagerId} Tenant={TenantId}",
            entity.Id, caseManagerId, tenantId);

        _audit.Publish(
            eventType: "liens.case.reassigned.casemanager",
            action: "update",
            description: $"Case '{entity.CaseNumber}' reassigned to case manager '{caseManagerId}'",
            tenantId: tenantId,
            actorUserId: actingUserId,
            entityType: "Case",
            entityId: entity.Id.ToString());

        return true;
    }

    private sealed record CaseSearchCandidate(Case Case, string? LawFirm, string? CaseManager);

    private static FuzzyMatchScore GetCaseKeywordScore(
        Case caseEntity,
        string keyword,
        string? lawFirm = null,
        string? caseManager = null) =>
        FuzzySearchScorer.Best(
            FuzzySearchScorer.ScorePersonName(
                caseEntity.ClientFirstName,
                caseEntity.ClientLastName,
                keyword),
            FuzzySearchScorer.ScoreFields(
                keyword,
                caseEntity.CaseNumber,
                caseEntity.ExternalReference,
                caseEntity.Title,
                lawFirm,
                caseManager));

    private static CaseResponse MapToResponse(
        Case entity,
        string? lawFirm = null,
        string? caseManager = null,
        string? lienStatus = null,
        string? lienStatusId = null,
        string? settlementStatus = null,
        string? settlementStatusId = null)
    {
        var noteBody = ExtractUserNotes(entity.Notes);
        var metadata = ParseCaseMetadata(entity.Notes);
        var address = SplitAddress(entity.ClientAddress);
        var lawFirmId = GetMetadataValue(metadata, "lawFirmId");
        var lawFirmName = FirstNonEmpty(GetMetadataValue(metadata, "lawFirm"), lawFirm);
        var caseManagerId = GetMetadataValue(metadata, "caseManagerId");
        var caseManagerName = FirstNonEmpty(GetMetadataValue(metadata, "caseManager"), caseManager);
        var accidentTypeId = GetMetadataValue(metadata, "accidentTypeId");
        var accidentType = GetMetadataValue(metadata, "accidentType");

        return new CaseResponse
        {
            Id = entity.Id,
            CaseNumber = entity.CaseNumber,
            ExternalReference = entity.ExternalReference,
            Title = entity.Title,
            ClientFirstName = entity.ClientFirstName,
            ClientLastName = entity.ClientLastName,
            ClientDisplayName = $"{entity.ClientFirstName} {entity.ClientLastName}".Trim(),
            Status = ResolveCaseStatusValue(entity.Status, GetMetadataValue(metadata, "statusLabel")),
            StatusLabel = ResolveCaseStatusLabel(entity.Status, GetMetadataValue(metadata, "statusLabel")),
            DateOfIncident = entity.DateOfIncident,
            ClientDob = entity.ClientDob,
            ClientPhone = entity.ClientPhone,
            ClientEmail = entity.ClientEmail,
            ClientAddress = entity.ClientAddress,
            ClientStreetAddress = FirstNonEmpty(entity.ClientAddressLine1, address.Address),
            ClientCity = FirstNonEmpty(entity.ClientCity, address.City),
            ClientState = FirstNonEmpty(entity.ClientState, address.State),
            ClientZipcode = FirstNonEmpty(entity.ClientPostalCode, address.Zipcode),
            InsuranceCarrier = entity.InsuranceCarrier,
            PolicyNumber = entity.PolicyNumber,
            ClaimNumber = entity.ClaimNumber,
            DemandAmount = entity.DemandAmount,
            SettlementAmount = entity.SettlementAmount,
            LienStatus = lienStatus ?? string.Empty,
            LienStatusId = lienStatusId ?? string.Empty,
            SettlementStatus = settlementStatus ?? string.Empty,
            SettlementStatusId = settlementStatusId ?? string.Empty,
            Description = entity.Description,
            Notes = noteBody,
            Sex = GetMetadataValue(metadata, "gender"),
            CaseType = GetMetadataValue(metadata, "accidentType"),
            CurrentMedicalStatus = FirstNonEmpty(
                entity.CurrentMedicalStatus,
                GetMetadataValue(metadata, "currentMedicalStatus")),
            StateOfIncident = FirstNonEmpty(
                entity.IncidentState,
                GetMetadataValue(metadata, "stateOfIncident", "accidentState", "state")),
            TrackingFollowUpDate = entity.TrackingFollowUpDate ??
                ParseMetadataDate(GetMetadataValue(metadata, "trackingFollowUpDate")),
            LeadId = GetMetadataValue(metadata, "leadId"),
            ShareCase = NormalizeCaseFlagForResponseOrDefaultFalse(GetMetadataValue(metadata, "shareCase")),
            MinorComp = entity.MinorComp.HasValue
                ? NormalizeCaseFlagForResponse(entity.MinorComp.Value)
                : NormalizeCaseFlagForResponseOrDefaultFalse(GetMetadataValue(metadata, "minorComp")),
            CaseDropped = entity.CaseDropped.HasValue
                ? NormalizeCaseFlagForResponse(entity.CaseDropped.Value)
                : NormalizeCaseFlagForResponseOrDefaultFalse(GetMetadataValue(metadata, "caseDropped")),
            ChildSupportLiens = NormalizeCaseFlagForResponseOrDefaultFalse(GetMetadataValue(metadata, "childSupportLiens")),
            IsUccFiled = NormalizeCaseFlagForResponseOrDefaultFalse(
                FirstNonEmpty(
                    GetMetadataValue(metadata, "isUccFiled"),
                    GetMetadataValue(metadata, "isUCCFiled"))),
            LawFirmId = lawFirmId,
            PendingLawFirmId = GetMetadataValue(metadata, "pendingLawFirmId"),
            LawFirm = lawFirmName,
            CaseManagerId = caseManagerId,
            CaseManager = caseManagerName,
            AttorneyId = FirstNonEmpty(
                entity.AttorneyContactPersonId?.ToString(),
                GetMetadataValue(metadata, "attorneyId"),
                GetMetadataValue(metadata, "attorney")),
            SwitchedDate = GetMetadataValue(metadata, "switchedDate"),
            AccidentTypeId = accidentTypeId,
            AccidentType = accidentType,
            OpenedAtUtc = entity.OpenedAtUtc,
            ClosedAtUtc = entity.ClosedAtUtc,
            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc,
        };
    }

    private async Task<(string Status, string StatusId)> ResolveLienStatusAsync(
        Guid tenantId,
        Guid caseId,
        CancellationToken ct)
    {
        var liensByRecency = (await _lienRepo.GetByCaseIdAsync(tenantId, caseId, ct))
            .OrderByDescending(lien => lien.CreatedAtUtc)
            .ThenByDescending(lien => lien.Id)
            .ToList();
        var representativeLien = liensByRecency
            .FirstOrDefault(lien => LienStatus.Open.Contains(lien.Status))
            ?? liensByRecency.FirstOrDefault();
        if (representativeLien is null)
            return (string.Empty, string.Empty);

        var statusLabel = representativeLien.Status switch
        {
            LienStatus.Cancelled or LienStatus.Declined => "Rejected",
            LienStatus.Settled or LienStatus.Withdrawn => "Closed",
            _ => "Open",
        };
        var statusLookup = await _lookupValueService.GetByCodeAsync(
            tenantId,
            LookupCategory.LienStatus,
            representativeLien.Status,
            ct);

        return (statusLabel, statusLookup?.Id.ToString() ?? string.Empty);
    }

    private async Task<(string Status, string StatusId)> ResolveSettlementStatusAsync(
        Guid tenantId,
        Guid caseId,
        CancellationToken ct)
    {
        var liens = await _lienRepo.GetByCaseIdAsync(tenantId, caseId, ct);
        if (liens.Count == 0)
            return (string.Empty, string.Empty);
        var allLiensClosed = liens.All(lien => lien.Status == LienStatus.Settled);

        var payments = await _settlementService.GetPaymentsByCaseAsync(tenantId, caseId, ct);
        var settlements = await _settlementService.GetSettlementsByCaseAsync(tenantId, caseId, ct);
        var hasReceivedAmount = payments.Any(payment => payment.Amount > 0m) ||
                                settlements.Any(settlement => settlement.Amount > 0m);
        var hasNoRecoveryDeclaration = payments.Any(payment =>
                IsNoRecoveryValue(payment.SettlementStatusId) ||
                (IsLegacyLienStatusValue(payment.SettlementStatusId) &&
                 IsNoRecoveryValue(payment.SettlementTypeId))) ||
            settlements.Any(settlement => IsNoRecoveryValue(settlement.Status));
        if (hasNoRecoveryDeclaration)
        {
            return hasReceivedAmount
                ? ("Closed", "Closed")
                : ("No Recovery", "4");
        }

        var hasClosedDeclaration = payments.Any(payment =>
            string.Equals(
                payment.SettlementStatusId?.Trim(),
                "Closed",
                StringComparison.OrdinalIgnoreCase));
        if (hasReceivedAmount && hasClosedDeclaration)
            return ("Closed", "Closed");

        var latestPayment = payments
            .OrderByDescending(payment => payment.CreatedAtUtc)
            .ThenByDescending(payment => payment.Id)
            .FirstOrDefault();
        var matchingSettlement = latestPayment is null
            ? null
            : settlements
                .Where(settlement =>
                    settlement.LienId == latestPayment.LienId &&
                    settlement.PaymentNumber == latestPayment.PaymentNumber)
                .OrderByDescending(settlement => settlement.CreatedAtUtc)
                .ThenByDescending(settlement => settlement.Id)
                .FirstOrDefault();
        var latestSettlement = settlements
            .OrderByDescending(settlement => settlement.CreatedAtUtc)
            .ThenByDescending(settlement => settlement.Id)
            .FirstOrDefault();
        var statusId = FirstNonEmpty(
            latestPayment?.SettlementStatusId,
            matchingSettlement?.Status,
            latestSettlement?.Status);

        if (statusId is null)
            return (string.Empty, string.Empty);

        var settlementTypes = await _lookupValueService.GetByCategoryAsync(
            tenantId,
            LookupCategory.SettlementType,
            ct);
        var settlementStatuses = await _lookupValueService.GetByCategoryAsync(
            tenantId,
            LookupCategory.SettlementStatus,
            ct);
        var lookup = settlementTypes
            .Concat(settlementStatuses)
            .FirstOrDefault(value =>
                string.Equals(value.Id.ToString(), statusId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value.Code, statusId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value.Name, statusId, StringComparison.OrdinalIgnoreCase));
        var statusName = lookup?.Name ?? ResolveLegacySettlementStatusName(statusId);
        var isNoRecovery = IsNoRecoverySettlementStatus(statusId, lookup?.Code, statusName);

        if (!allLiensClosed && !isNoRecovery)
            return (string.Empty, string.Empty);

        return isNoRecovery
            ? ("No Recovery", "4")
            : (statusName, statusId);
    }

    private static bool IsNoRecoverySettlementStatus(
        string statusId,
        string? statusCode,
        string statusName) =>
        IsNoRecoveryValue(statusId) ||
        IsNoRecoveryValue(statusCode) ||
        IsNoRecoveryValue(statusName);

    private static bool IsNoRecoveryValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        return normalized == "4" ||
               string.Equals(normalized, "NoRecovery", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLegacyLienStatusValue(string? value) =>
        string.Equals(value?.Trim(), "Open", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value?.Trim(), "Closed", StringComparison.OrdinalIgnoreCase);

    private static string ResolveLegacySettlementStatusName(string value) => value switch
    {
        "4" => "No Recovery",
        _ when int.TryParse(value, out _) => string.Empty,
        _ => HumanizeLegacyCode(value),
    };

    private static string HumanizeLegacyCode(string value)
    {
        if (!value.Contains('_'))
            return value;

        return string.Join(' ', value
            .Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
    }

    private static string? GetMetadataValue(
        IReadOnlyDictionary<string, string> metadata,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static Dictionary<string, string> BuildMetadata(
        string? sex,
        string? caseType,
        string? currentMedicalStatus,
        string? stateOfIncident,
        DateOnly? trackingFollowUpDate,
        string? leadId,
        string? shareCase,
        string? minorComp,
        string? caseDropped,
        string? childSupportLiens,
        string? isUccFiled,
        string? lawFirmId,
        string? accidentTypeId,
        string? caseManagerId,
        string? statusLabel)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        SetMetadataValue(metadata, "gender", sex);
        SetMetadataValue(metadata, "accidentType", caseType);
        SetMetadataValue(metadata, "accidentTypeId", accidentTypeId);
        SetMetadataValue(metadata, "currentMedicalStatus", currentMedicalStatus);
        SetMetadataValue(metadata, "accidentState", stateOfIncident);
        SetMetadataValue(
            metadata,
            "trackingFollowUpDate",
            trackingFollowUpDate?.ToString("MM/dd/yyyy"));
        SetMetadataValue(metadata, "leadId", leadId);
        SetMetadataValue(metadata, "shareCase", NormalizeCaseFlagForStorage(shareCase));
        SetMetadataValue(metadata, "minorComp", NormalizeCaseFlagForStorage(minorComp));
        SetMetadataValue(metadata, "caseDropped", NormalizeCaseFlagForStorage(caseDropped));
        SetMetadataValue(metadata, "childSupportLiens", NormalizeCaseFlagForStorage(childSupportLiens));
        SetMetadataValue(metadata, "isUccFiled", NormalizeCaseFlagForStorage(isUccFiled));
        SetMetadataValue(metadata, "lawFirmId", lawFirmId);
        SetMetadataValue(metadata, "caseManagerId", caseManagerId);
        SetMetadataValue(metadata, "statusLabel", statusLabel);
        return metadata;
    }

    private static Dictionary<string, string> MergeMetadata(
        Dictionary<string, string> existing,
        string? sex,
        string? caseType,
        string? currentMedicalStatus,
        string? stateOfIncident,
        DateOnly? trackingFollowUpDate,
        string? leadId,
        string? shareCase,
        string? minorComp,
        string? caseDropped,
        string? childSupportLiens,
        string? isUccFiled,
        string? lawFirmId,
        string? pendingLawFirmId,
        string? accidentTypeId,
        string? caseManagerId,
        string? attorneyId,
        string? switchedDate)
    {
        var metadata = new Dictionary<string, string>(existing, StringComparer.Ordinal);
        if (sex is not null)
            SetMetadataValue(metadata, "gender", sex);
        if (caseType is not null)
            SetMetadataValue(metadata, "accidentType", caseType);
        if (accidentTypeId is not null)
            SetMetadataValue(metadata, "accidentTypeId", accidentTypeId);
        if (currentMedicalStatus is not null)
            SetMetadataValue(metadata, "currentMedicalStatus", currentMedicalStatus);
        if (stateOfIncident is not null)
            SetMetadataValue(metadata, "accidentState", stateOfIncident);
        if (trackingFollowUpDate.HasValue)
            SetMetadataValue(metadata, "trackingFollowUpDate", trackingFollowUpDate.Value.ToString("MM/dd/yyyy"));
        if (leadId is not null)
            SetMetadataValue(metadata, "leadId", leadId);
        if (shareCase is not null)
            SetMetadataValue(metadata, "shareCase", NormalizeCaseFlagForStorage(shareCase));
        if (minorComp is not null)
            SetMetadataValue(metadata, "minorComp", NormalizeCaseFlagForStorage(minorComp));
        if (caseDropped is not null)
            SetMetadataValue(metadata, "caseDropped", NormalizeCaseFlagForStorage(caseDropped));
        if (childSupportLiens is not null)
            SetMetadataValue(metadata, "childSupportLiens", NormalizeCaseFlagForStorage(childSupportLiens));
        if (isUccFiled is not null)
        {
            metadata.Remove("isUCCFiled");
            SetMetadataValue(metadata, "isUccFiled", NormalizeCaseFlagForStorage(isUccFiled));
        }
        if (lawFirmId is not null)
        {
            metadata.Remove("lawFirm");
            SetMetadataValue(metadata, "lawFirmId", lawFirmId);
        }
        if (pendingLawFirmId is not null)
            SetMetadataValue(metadata, "pendingLawFirmId", pendingLawFirmId);
        if (caseManagerId is not null)
            SetMetadataValue(metadata, "caseManagerId", caseManagerId);
        if (attorneyId is not null)
        {
            metadata.Remove("attorney");
            SetMetadataValue(metadata, "attorneyId", attorneyId);
        }
        if (switchedDate is not null)
            SetMetadataValue(metadata, "switchedDate", switchedDate);
        return metadata;
    }

    private static void ApplyStatusLabelMetadata(
        Dictionary<string, string> metadata,
        string? status,
        string? statusLabel)
    {
        if (statusLabel is not null)
        {
            SetMetadataValue(metadata, "statusLabel", statusLabel);
            return;
        }

        if (status is not null && !string.Equals(status, CaseStatus.InNegotiation, StringComparison.Ordinal))
            metadata.Remove("statusLabel");
    }

    private static void SetMetadataValue(Dictionary<string, string> metadata, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            metadata.Remove(key);
            return;
        }

        metadata[key] = value.Trim();
    }

    private static string? SerializeCaseNotes(string? noteBody, Dictionary<string, string> metadata)
    {
        var cleanBody = string.IsNullOrWhiteSpace(noteBody) ? null : noteBody.Trim();
        if (metadata.Count == 0)
            return cleanBody;

        var serialized = string.Join("; ", metadata.Select(pair => $"{pair.Key}={pair.Value}"));
        return cleanBody is null
            ? $"{LegacyMetadataMarker}{Environment.NewLine}{serialized}"
            : $"{cleanBody}{Environment.NewLine}{Environment.NewLine}{LegacyMetadataMarker}{Environment.NewLine}{serialized}";
    }

    private static string? ExtractUserNotes(string? notes)
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

    private static Dictionary<string, string> ParseCaseMetadata(string? notes)
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

        foreach (var segment in rawMetadata.Split("; ", StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = segment.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = segment[..eq].Trim();
            var value = segment[(eq + 1)..].Trim();
            if (key.Length > 0)
                result[key] = value;
        }

        return result;
    }

    private static string? ResolveLawFirmName(
        Guid orgId,
        Guid? handlingLawFirmCompanyId,
        string? lawFirmId,
        IReadOnlyDictionary<Guid, Contact> lawFirmById,
        IReadOnlyDictionary<Guid, Contact> lawFirmByOrgId,
        IReadOnlyDictionary<Guid, Company> lawFirmCompanyById)
    {
        if (Guid.TryParse(lawFirmId, out var parsedLawFirmId) &&
            lawFirmById.TryGetValue(parsedLawFirmId, out var lawFirmContactById))
        {
            return FirstNonEmpty(lawFirmContactById.Organization, lawFirmContactById.DisplayName);
        }

        if (handlingLawFirmCompanyId.HasValue &&
            lawFirmCompanyById.TryGetValue(handlingLawFirmCompanyId.Value, out var lawFirmCompany))
        {
            return lawFirmCompany.Name;
        }

        if (lawFirmByOrgId.TryGetValue(orgId, out var lawFirmContactByOrg))
        {
            return FirstNonEmpty(lawFirmContactByOrg.Organization, lawFirmContactByOrg.DisplayName);
        }

        return null;
    }

    private static string? ResolveCaseManagerName(
        string? caseManagerId,
        IReadOnlyDictionary<Guid, Contact> caseManagerById)
    {
        if (Guid.TryParse(caseManagerId, out var parsedCaseManagerId) &&
            caseManagerById.TryGetValue(parsedCaseManagerId, out var caseManagerContact))
        {
            return caseManagerContact.DisplayName;
        }

        return null;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string ResolveCaseStatusLabel(string status, string? customStatusLabel)
    {
        var litigationStatus = NormalizeConcreteLitigationStatus(status);
        if (litigationStatus is not null)
            return litigationStatus;

        if (!string.IsNullOrWhiteSpace(customStatusLabel))
            return customStatusLabel.Trim();

        return status switch
        {
            CaseStatus.PreDemand => "Pre-Demand",
            CaseStatus.DemandSent => "Demand Sent",
            CaseStatus.InNegotiation => "In Negotiation",
            CaseStatus.CaseSettled => "Case Settled",
            CaseStatus.Closed => "Closed",
            _ => status,
        };
    }

    private static string ResolveCaseStatusValue(string status, string? customStatusLabel)
    {
        var litigationStatus = NormalizeConcreteLitigationStatus(status);
        if (litigationStatus is not null)
            return litigationStatus;

        if (!string.IsNullOrWhiteSpace(customStatusLabel))
            return customStatusLabel.Trim();

        return status;
    }

    private static string? NormalizeConcreteLitigationStatus(string status)
    {
        var normalized = status.Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("(", string.Empty, StringComparison.Ordinal)
            .Replace(")", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();

        return normalized switch
        {
            "LITIGATIONPENDING" => CaseStatus.LitigationPending,
            "LITIGATIONOPEN" => CaseStatus.LitigationOpen,
            _ => null,
        };
    }

    private static string? NormalizeCaseFlagForStorage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        return value.Trim().ToUpperInvariant() switch
        {
            "TRUE" or "YES" or "Y" => "Yes",
            "FALSE" or "NO" or "N" => "No",
            _ => value.Trim(),
        };
    }

    private static string? NormalizeCaseFlagForResponse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        return value.Trim().ToUpperInvariant() switch
        {
            "TRUE" or "YES" or "Y" => "Yes",
            "FALSE" or "NO" or "N" => "No",
            _ => value.Trim(),
        };
    }

    private static string NormalizeCaseFlagForResponse(bool value) => value ? "Yes" : "No";

    private static bool? ParseNullableCaseFlag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().ToUpperInvariant() switch
        {
            "TRUE" or "YES" or "Y" or "1" => true,
            "FALSE" or "NO" or "N" or "0" => false,
            _ => null,
        };
    }

    private static string NormalizeCaseFlagForResponseOrDefaultFalse(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "false"
            : NormalizeCaseFlagForResponse(value) ?? "false";

    private static bool LooksLikeLegacyMetadata(string notes)
    {
        var segments = notes.Split("; ", StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments.All(segment => segment.Contains('='));
    }

    private static string? GetMetadataValue(Dictionary<string, string> metadata, string key)
    {
        if (metadata.TryGetValue(key, out var value))
            return value;

        foreach (var pair in metadata)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return null;
    }

    private static DateOnly? ParseMetadataDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateOnly.TryParse(value, out var parsed) ? parsed : null;
    }

    private static (string? Address, string? City, string? State, string? Zipcode) SplitAddress(string? rawAddress)
    {
        if (string.IsNullOrWhiteSpace(rawAddress))
            return (null, null, null, null);

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
            return (parts[0], parts[1], parts[2], null);

        if (parts.Length == 2)
            return (parts[0], parts[1], null, null);

        return (rawAddress.Trim(), null, null, null);
    }
}
