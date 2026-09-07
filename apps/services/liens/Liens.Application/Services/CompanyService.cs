using System.Net.Mail;
using BuildingBlocks.Exceptions;
using Liens.Application.DTOs;
using Liens.Application.Interfaces;
using Liens.Application.Repositories;
using Liens.Domain.Enums;
using Liens.Domain.Entities;

namespace Liens.Application.Services;

public sealed class CompanyService : ICompanyService
{
    private readonly ICompanyRepository _repository;

    public CompanyService(ICompanyRepository repository)
    {
        _repository = repository;
    }

    public async Task<List<CompanyTypeResponse>> GetCompanyTypesAsync(CancellationToken ct = default)
        => (await _repository.GetCompanyTypesAsync(ct)).Select(Map).ToList();

    public async Task<List<ContactPersonTypeResponse>> GetContactPersonTypesAsync(
        Guid tenantId, Guid orgId, Guid companyTypeId, CancellationToken ct = default)
    {
        var companyType = await _repository.GetCompanyTypeAsync(companyTypeId, ct);
        if (companyType is null || !companyType.IsActive)
            throw new NotFoundException($"Company type '{companyTypeId}' not found.");

        return (await _repository.GetContactPersonTypesAsync(tenantId, orgId, companyTypeId, ct))
            .Select(Map)
            .ToList();
    }

    public async Task<ContactPersonTypeResponse> CreateContactPersonTypeAsync(
        Guid tenantId, Guid orgId, Guid actingUserId,
        CreateContactPersonTypeRequest request, CancellationToken ct = default)
    {
        ValidateContactPersonType(request);
        var companyType = await _repository.GetCompanyTypeAsync(request.CompanyTypeId, ct);
        if (companyType is null || !companyType.IsActive)
            throw Validation("companyTypeId", "Company type is invalid or inactive.");

        var code = request.Code.Trim();
        if (await _repository.ContactPersonTypeCodeExistsAsync(
                tenantId, orgId, request.CompanyTypeId, code, ct))
            throw new ConflictException("A contact-person type with this code already exists for the company type.");

        var sortOrder = request.SortOrder ?? await _repository.GetNextContactPersonTypeSortOrderAsync(
            tenantId, orgId, request.CompanyTypeId, ct);
        var contactPersonType = ContactPersonType.Create(
            tenantId, orgId, request.CompanyTypeId, code, request.Name,
            sortOrder, actingUserId);
        await _repository.AddContactPersonTypeAsync(contactPersonType, ct);
        return Map(contactPersonType);
    }

    public async Task<PaginatedResult<CompanyResponse>> SearchCompaniesAsync(
        Guid tenantId, Guid orgId, string? search, Guid? companyTypeId, bool? isActive,
        int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var (items, totalCount) = await _repository.SearchCompaniesAsync(
            tenantId, orgId, search, companyTypeId, isActive, page, pageSize, ct);
        return new PaginatedResult<CompanyResponse>
        {
            Items = items.Select(Map).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
        };
    }

    public async Task<List<CompanyResponse>> GetCompaniesForExportAsync(
        Guid tenantId, Guid orgId, string? search, Guid? companyTypeId, bool? isActive,
        CancellationToken ct = default)
        => (await _repository.GetCompaniesForExportAsync(
                tenantId, orgId, search, companyTypeId, isActive, ct))
            .Select(Map)
            .ToList();

    public async Task<CompanyResponse?> GetCompanyAsync(
        Guid tenantId, Guid orgId, Guid id, CancellationToken ct = default)
    {
        var company = await _repository.GetCompanyAsync(tenantId, orgId, id, ct);
        return company is null ? null : Map(company);
    }

    public async Task<CompanyDetailsResponse?> GetCompanyDetailsAsync(
        Guid tenantId, Guid orgId, Guid companyId, int page, int pageSize,
        CancellationToken ct = default)
    {
        var company = await _repository.GetCompanyAsync(tenantId, orgId, companyId, ct);
        if (company is null) return null;

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var snapshot = await _repository.GetCompanyDetailsAsync(
            tenantId, orgId, companyId, company.CompanyTypeId, page, pageSize, ct);

        return new CompanyDetailsResponse
        {
            Company = Map(company),
            TotalCases = snapshot.TotalCases,
            ActiveCases = snapshot.ActiveCases,
            TotalBillingForActiveCases = snapshot.TotalBillingForActiveCases,
            RecentCases = new CompanyRecentCasesResponse
            {
                Items = snapshot.RecentCases.Select(MapRecentCase).ToList(),
                Page = page,
                PageSize = pageSize,
                TotalCount = snapshot.TotalCases,
                TotalPages = snapshot.TotalCases == 0
                    ? 0
                    : (int)Math.Ceiling(snapshot.TotalCases / (double)pageSize),
            },
        };
    }

    public async Task<CompanyResponse> CreateCompanyAsync(
        Guid tenantId, Guid orgId, Guid actingUserId, CreateCompanyRequest request, CancellationToken ct = default)
    {
        ValidateCompany(request.Name, request.LinkedTenantId, request.AddressLine1, request.City,
            request.State, request.PostalCode, request.Phone, request.Email);
        var type = await _repository.GetCompanyTypeAsync(request.CompanyTypeId, ct);
        if (type is null || !type.IsActive)
            throw Validation("companyTypeId", "Company type is invalid or inactive.");

        var normalizedName = Company.NormalizeName(request.Name);
        if (await _repository.CompanyNameExistsAsync(
                tenantId, orgId, request.CompanyTypeId, normalizedName, ct: ct))
            throw new ConflictException("A company with this name and type already exists.");

        var company = Company.Create(
            tenantId, orgId, request.CompanyTypeId, request.Name, actingUserId,
            request.LinkedTenantId, request.AddressLine1, request.City, request.State,
            request.PostalCode, request.Phone, request.Email);
        await _repository.AddCompanyAsync(company, ct);
        company = await _repository.GetCompanyAsync(tenantId, orgId, company.Id, ct) ?? company;
        return Map(company);
    }

    public async Task<CompanyResponse> UpdateCompanyAsync(
        Guid tenantId, Guid orgId, Guid id, Guid actingUserId,
        UpdateCompanyRequest request, CancellationToken ct = default)
    {
        ValidateCompany(request.Name, request.LinkedTenantId, request.AddressLine1, request.City,
            request.State, request.PostalCode, request.Phone, request.Email);
        var company = await RequireCompanyAsync(tenantId, orgId, id, ct);
        if (!company.IsActive)
            throw new ConflictException("Inactive companies cannot be updated.");

        var normalizedName = Company.NormalizeName(request.Name);
        if (await _repository.CompanyNameExistsAsync(
                tenantId, orgId, company.CompanyTypeId, normalizedName, company.Id, ct))
            throw new ConflictException("A company with this name and type already exists.");

        company.Update(request.Name, actingUserId, request.LinkedTenantId, request.AddressLine1,
            request.City, request.State, request.PostalCode, request.Phone, request.Email);
        await _repository.UpdateCompanyAsync(company, ct);
        return Map(company);
    }

    public async Task<CompanyResponse> SetCompanyActiveAsync(
        Guid tenantId, Guid orgId, Guid id, Guid actingUserId,
        bool isActive, CancellationToken ct = default)
    {
        var company = await RequireCompanyAsync(tenantId, orgId, id, ct);
        if (isActive) company.Reactivate(actingUserId); else company.Deactivate(actingUserId);
        await _repository.UpdateCompanyAsync(company, ct);
        return Map(company);
    }

    public async Task<CompanyReassignmentResponse> ReassignCompanyAsync(
        Guid tenantId, Guid orgId, Guid sourceCompanyId, Guid targetCompanyId,
        Guid actingUserId, CancellationToken ct = default)
    {
        if (targetCompanyId == Guid.Empty)
            throw Validation("targetCompanyId", "Target company is required.");
        if (sourceCompanyId == targetCompanyId)
            throw Validation("targetCompanyId", "Source and target companies must differ.");

        var source = await RequireCompanyAsync(tenantId, orgId, sourceCompanyId, ct);
        var target = await RequireCompanyAsync(tenantId, orgId, targetCompanyId, ct);
        if (!target.IsActive)
            throw new ConflictException("The target company must be active.");
        if (source.CompanyTypeId != target.CompanyTypeId)
            throw Validation("targetCompanyId", "Source and target companies must have the same company type.");

        var counts = await _repository.ReassignCompanyAsync(
            tenantId, orgId, source, target, actingUserId, ct);
        var total = counts.ContactPersons + counts.Liens + counts.Cases + counts.Offers +
                    counts.BuyerAccessLinks + counts.PortfolioBuyers;
        return new CompanyReassignmentResponse
        {
            SourceCompanyId = source.Id,
            SourceCompanyName = source.Name,
            TargetCompanyId = target.Id,
            TargetCompanyName = target.Name,
            CompanyTypeId = source.CompanyTypeId,
            ReassignedContactPersonCount = counts.ContactPersons,
            ReassignedLienCount = counts.Liens,
            ReassignedCaseCount = counts.Cases,
            ReassignedOfferCount = counts.Offers,
            ReassignedBuyerAccessLinkCount = counts.BuyerAccessLinks,
            ReassignedPortfolioBuyerCount = counts.PortfolioBuyers,
            TotalReassignedCount = total,
        };
    }

    public async Task<List<CompanyContactPersonResponse>> GetContactPersonsAsync(
        Guid tenantId, Guid orgId, Guid companyId, bool? isActive, CancellationToken ct = default)
    {
        await RequireCompanyAsync(tenantId, orgId, companyId, ct);
        return (await _repository.GetContactPersonsAsync(tenantId, companyId, isActive, ct)).Select(Map).ToList();
    }

    public async Task<ContactPersonDirectoryResponse> SearchContactPersonsAsync(
        Guid tenantId, Guid orgId, string? search, Guid? companyTypeId,
        Guid? contactPersonTypeId, bool? isActive, int page, int pageSize,
        CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var (items, totalCount) = await _repository.SearchContactPersonsAsync(
            tenantId, orgId, search, companyTypeId, contactPersonTypeId, isActive,
            page, pageSize, ct);

        return new ContactPersonDirectoryResponse
        {
            Items = items.Select(MapForExport).ToList(),
            Page = page,
            Limit = pageSize,
            TotalCount = totalCount,
            TotalPages = totalCount == 0
                ? 0
                : (int)Math.Ceiling(totalCount / (double)pageSize),
        };
    }

    public async Task<List<CompanyContactPersonExportResponse>> GetContactPersonsForExportAsync(
        Guid tenantId, Guid orgId, Guid? companyId, string? search, Guid? companyTypeId,
        Guid? contactPersonTypeId, bool? isActive, CancellationToken ct = default)
    {
        if (companyId.HasValue)
            await RequireCompanyAsync(tenantId, orgId, companyId.Value, ct);

        return (await _repository.GetContactPersonsForExportAsync(
                tenantId, orgId, companyId, search, companyTypeId, contactPersonTypeId, isActive, ct))
            .Select(MapForExport)
            .ToList();
    }

    public async Task<CompanyContactPersonResponse?> GetContactPersonAsync(
        Guid tenantId, Guid orgId, Guid companyId, Guid contactId, CancellationToken ct = default)
    {
        await RequireCompanyAsync(tenantId, orgId, companyId, ct);
        var contact = await _repository.GetContactPersonAsync(tenantId, companyId, contactId, ct);
        return contact is null ? null : Map(contact);
    }

    public async Task<CompanyContactPersonResponse> CreateContactPersonAsync(
        Guid tenantId, Guid orgId, Guid companyId, Guid actingUserId,
        CreateCompanyContactPersonRequest request, CancellationToken ct = default)
    {
        var company = await RequireCompanyAsync(tenantId, orgId, companyId, ct);
        if (!company.IsActive)
            throw new ConflictException("Contacts cannot be added to an inactive company.");
        ValidateContact(request.ContactPersonTypeId, request.FirstName, request.LastName,
            request.AddressLine1, request.City, request.State, request.PostalCode, request.Phone, request.Email);
        await EnsureContactEmailIsUniqueAsync(tenantId, request.Email, ct: ct);
        await RequireMatchingRoleAsync(tenantId, orgId, company, request.ContactPersonTypeId, ct);

        var contact = CompanyContactPerson.Create(
            tenantId, companyId, request.ContactPersonTypeId, request.FirstName, request.LastName,
            actingUserId, request.AddressLine1, request.City, request.State, request.PostalCode,
            request.Phone, request.Email);
        await _repository.AddContactPersonAsync(contact, ct);
        contact = await _repository.GetContactPersonAsync(tenantId, companyId, contact.Id, ct) ?? contact;
        return Map(contact);
    }

    public async Task<CompanyContactPersonResponse> UpdateContactPersonAsync(
        Guid tenantId, Guid orgId, Guid companyId, Guid contactId, Guid actingUserId,
        UpdateCompanyContactPersonRequest request, CancellationToken ct = default)
    {
        var company = await RequireCompanyAsync(tenantId, orgId, companyId, ct);
        if (!company.IsActive)
            throw new ConflictException("Contacts for an inactive company cannot be updated.");
        ValidateContact(request.ContactPersonTypeId, request.FirstName, request.LastName,
            request.AddressLine1, request.City, request.State, request.PostalCode, request.Phone, request.Email);
        await RequireMatchingRoleAsync(tenantId, orgId, company, request.ContactPersonTypeId, ct);
        var contact = await RequireContactAsync(tenantId, companyId, contactId, ct);
        if (!contact.IsActive)
            throw new ConflictException("Inactive contacts cannot be updated.");
        await EnsureContactEmailIsUniqueAsync(tenantId, request.Email, contactId, ct);

        contact.Update(request.ContactPersonTypeId, request.FirstName, request.LastName, actingUserId,
            request.AddressLine1, request.City, request.State, request.PostalCode, request.Phone, request.Email);
        await _repository.UpdateContactPersonAsync(contact, ct);
        return Map(contact);
    }

    public async Task<CompanyContactPersonResponse> SetContactPersonActiveAsync(
        Guid tenantId, Guid orgId, Guid companyId, Guid contactId, Guid actingUserId,
        bool isActive, CancellationToken ct = default)
    {
        var company = await RequireCompanyAsync(tenantId, orgId, companyId, ct);
        if (isActive && !company.IsActive)
            throw new ConflictException("Contacts cannot be reactivated while the company is inactive.");
        var contact = await RequireContactAsync(tenantId, companyId, contactId, ct);
        if (isActive) contact.Reactivate(actingUserId); else contact.Deactivate(actingUserId);
        await _repository.UpdateContactPersonAsync(contact, ct);
        return Map(contact);
    }

    public async Task<CompanyContactPersonReassignmentResponse> ReassignContactPersonAsync(
        Guid tenantId, Guid orgId, Guid sourceCompanyId, Guid sourceContactPersonId,
        Guid targetContactPersonId, Guid actingUserId, CancellationToken ct = default)
    {
        if (targetContactPersonId == Guid.Empty)
            throw Validation("targetContactPersonId", "Target contact person is required.");
        if (sourceContactPersonId == targetContactPersonId)
            throw Validation("targetContactPersonId", "Source and target contact persons must differ.");

        var sourceCompany = await RequireCompanyAsync(tenantId, orgId, sourceCompanyId, ct);
        var source = await RequireContactAsync(tenantId, sourceCompanyId, sourceContactPersonId, ct);
        var target = await _repository.GetContactPersonInScopeAsync(tenantId, orgId, targetContactPersonId, ct)
            ?? throw new NotFoundException($"Target company contact '{targetContactPersonId}' not found.");
        var targetCompany = target.Company
            ?? throw new NotFoundException($"Target company contact '{targetContactPersonId}' not found.");

        if (!target.IsActive || !targetCompany.IsActive)
            throw new ConflictException("The target contact person and target company must be active.");
        if (sourceCompany.CompanyTypeId != targetCompany.CompanyTypeId)
            throw Validation("targetContactPersonId",
                "Source and target contact persons must belong to the same company type.");
        if (source.ContactPersonTypeId != target.ContactPersonTypeId)
            throw Validation("targetContactPersonId",
                "Source and target contact persons must have the same contact-person type (role).");

        var counts = await _repository.ReassignContactPersonAsync(
            tenantId, orgId, source, target, actingUserId, ct);
        var total = counts.Liens + counts.Cases + counts.BuyerAccessLinks;
        return new CompanyContactPersonReassignmentResponse
        {
            SourceContactPersonId = source.Id,
            SourceContactPersonName = $"{source.FirstName} {source.LastName}".Trim(),
            TargetContactPersonId = target.Id,
            TargetContactPersonName = $"{target.FirstName} {target.LastName}".Trim(),
            SourceCompanyId = source.CompanyId,
            TargetCompanyId = target.CompanyId,
            CompanyTypeId = sourceCompany.CompanyTypeId,
            ContactPersonTypeId = source.ContactPersonTypeId,
            ReassignedLienCount = counts.Liens,
            ReassignedCaseCount = counts.Cases,
            ReassignedBuyerAccessLinkCount = counts.BuyerAccessLinks,
            TotalReassignedCount = total,
        };
    }

    private async Task<Company> RequireCompanyAsync(Guid tenantId, Guid orgId, Guid id, CancellationToken ct)
        => await _repository.GetCompanyAsync(tenantId, orgId, id, ct)
           ?? throw new NotFoundException($"Company '{id}' not found.");

    private async Task<CompanyContactPerson> RequireContactAsync(
        Guid tenantId, Guid companyId, Guid contactId, CancellationToken ct)
        => await _repository.GetContactPersonAsync(tenantId, companyId, contactId, ct)
           ?? throw new NotFoundException($"Company contact '{contactId}' not found.");

    private async Task RequireMatchingRoleAsync(
        Guid tenantId, Guid orgId, Company company, Guid roleId, CancellationToken ct)
    {
        var role = await _repository.GetContactPersonTypeAsync(tenantId, orgId, roleId, ct);
        if (role is null || !role.IsActive || role.CompanyTypeId != company.CompanyTypeId)
            throw Validation("contactPersonTypeId", "Contact-person type is invalid, inactive, or does not belong to the company's type.");
    }

    private async Task EnsureContactEmailIsUniqueAsync(
        Guid tenantId, string? email, Guid? excludingId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) ||
            !await _repository.ContactPersonEmailExistsAsync(tenantId, email, excludingId, ct))
            return;

        const string message = "A contact person with this email address already exists.";
        throw new ValidationException(message, new Dictionary<string, string[]> { ["email"] = [message] });
    }

    private static void ValidateContactPersonType(CreateContactPersonTypeRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.CompanyTypeId == Guid.Empty)
            errors["companyTypeId"] = ["Company type is required."];
        Required(errors, "code", request.Code, 100);
        Required(errors, "name", request.Name, 150);

        var code = request.Code?.Trim() ?? string.Empty;
        if (!errors.ContainsKey("code") &&
            (!char.IsAsciiLetter(code[0]) || code.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')))
        {
            errors["code"] = ["Code must start with a letter and contain only letters, numbers, hyphens, or underscores."];
        }

        if (request.SortOrder is <= 0 or > 10000)
            errors["sortOrder"] = ["Sort order must be between 1 and 10000 when provided."];
        ThrowIfErrors(errors);
    }

    private static void ValidateCompany(
        string name, Guid? linkedTenantId, string? address, string? city,
        string? state, string? postalCode, string? phone, string? email)
    {
        var errors = new Dictionary<string, string[]>();
        Required(errors, "name", name, 200);
        Optional(errors, "addressLine1", address, 300);
        Optional(errors, "city", city, 100);
        Optional(errors, "state", state, 100);
        Optional(errors, "postalCode", postalCode, 20);
        Optional(errors, "phone", phone, 30);
        ValidateEmail(errors, email);
        if (linkedTenantId == Guid.Empty) errors["linkedTenantId"] = ["Linked tenant ID cannot be empty."];
        ThrowIfErrors(errors);
    }

    private static void ValidateContact(
        Guid roleId, string firstName, string lastName, string? address, string? city,
        string? state, string? postalCode, string? phone, string? email)
    {
        var errors = new Dictionary<string, string[]>();
        if (roleId == Guid.Empty) errors["contactPersonTypeId"] = ["Contact-person type is required."];
        Required(errors, "firstName", firstName, 100);
        Required(errors, "lastName", lastName, 100);
        Optional(errors, "addressLine1", address, 300);
        Optional(errors, "city", city, 100);
        Optional(errors, "state", state, 100);
        Optional(errors, "postalCode", postalCode, 20);
        Optional(errors, "phone", phone, 30);
        ValidateEmail(errors, email);
        ThrowIfErrors(errors);
    }

    private static void Required(Dictionary<string, string[]> errors, string key, string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) errors[key] = [$"{key} is required."];
        else Optional(errors, key, value, maxLength);
    }

    private static void Optional(Dictionary<string, string[]> errors, string key, string? value, int maxLength)
    {
        if (value?.Trim().Length > maxLength) errors[key] = [$"{key} must not exceed {maxLength} characters."];
    }

    private static void ValidateEmail(Dictionary<string, string[]> errors, string? email)
    {
        Optional(errors, "email", email, 320);
        if (string.IsNullOrWhiteSpace(email) || errors.ContainsKey("email")) return;
        try
        {
            var parsed = new MailAddress(email.Trim());
            if (!string.Equals(parsed.Address, email.Trim(), StringComparison.OrdinalIgnoreCase))
                errors["email"] = ["Email is invalid."];
        }
        catch (FormatException)
        {
            errors["email"] = ["Email is invalid."];
        }
    }

    private static void ThrowIfErrors(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0) throw new ValidationException("Company data is invalid.", errors);
    }

    private static ValidationException Validation(string key, string message)
        => new("Company data is invalid.", new Dictionary<string, string[]> { [key] = [message] });

    private static CompanyTypeResponse Map(CompanyType value) => new()
    {
        Id = value.Id, Code = value.Code, Name = value.Name, SortOrder = value.SortOrder,
    };

    private static ContactPersonTypeResponse Map(ContactPersonType value) => new()
    {
        Id = value.Id, CompanyTypeId = value.CompanyTypeId, Code = value.Code,
        Name = value.Name, SortOrder = value.SortOrder, IsSystem = !value.TenantId.HasValue,
    };

    private static CompanyResponse Map(Company value) => new()
    {
        Id = value.Id,
        CompanyTypeId = value.CompanyTypeId,
        CompanyTypeCode = value.CompanyType?.Code ?? string.Empty,
        CompanyTypeName = value.CompanyType?.Name ?? string.Empty,
        LinkedTenantId = value.LinkedTenantId,
        Name = value.Name,
        AddressLine1 = value.AddressLine1,
        City = value.City,
        State = value.State,
        PostalCode = value.PostalCode,
        Phone = value.Phone,
        Email = value.Email,
        IsActive = value.IsActive,
        CreatedAtUtc = value.CreatedAtUtc,
        UpdatedAtUtc = value.UpdatedAtUtc,
    };

    private static CompanyContactPersonResponse Map(CompanyContactPerson value) => new()
    {
        Id = value.Id,
        CompanyId = value.CompanyId,
        ContactPersonTypeId = value.ContactPersonTypeId,
        ContactPersonTypeCode = value.ContactPersonType?.Code ?? string.Empty,
        ContactPersonTypeName = value.ContactPersonType?.Name ?? string.Empty,
        FirstName = value.FirstName,
        LastName = value.LastName,
        AddressLine1 = value.AddressLine1,
        City = value.City,
        State = value.State,
        PostalCode = value.PostalCode,
        Phone = value.Phone,
        Email = value.Email,
        IsActive = value.IsActive,
        CreatedAtUtc = value.CreatedAtUtc,
        UpdatedAtUtc = value.UpdatedAtUtc,
    };

    private static CompanyRecentCaseResponse MapRecentCase(CompanyRecentCaseSnapshot value) => new()
    {
        Id = value.Id,
        CaseNumber = value.CaseNumber,
        ClientName = $"{value.ClientFirstName} {value.ClientLastName}".Trim(),
        Status = value.Status,
        StatusLabel = value.Status switch
        {
            CaseStatus.PreDemand => "Pre-Demand",
            CaseStatus.DemandSent => "Demand Sent",
            CaseStatus.InNegotiation => "In Negotiation",
            CaseStatus.CaseSettled => "Case Settled",
            CaseStatus.Closed => "Closed",
            _ => value.Status,
        },
        BillingAmount = value.BillingAmount,
        UpdatedAtUtc = value.UpdatedAtUtc,
    };

    private static CompanyContactPersonExportResponse MapForExport(CompanyContactPerson value) => new()
    {
        Id = value.Id,
        CompanyId = value.CompanyId,
        CompanyName = value.Company?.Name ?? string.Empty,
        CompanyTypeId = value.Company?.CompanyTypeId ?? Guid.Empty,
        CompanyTypeCode = value.Company?.CompanyType?.Code ?? string.Empty,
        CompanyTypeName = value.Company?.CompanyType?.Name ?? string.Empty,
        ContactPersonTypeId = value.ContactPersonTypeId,
        ContactPersonTypeCode = value.ContactPersonType?.Code ?? string.Empty,
        ContactPersonTypeName = value.ContactPersonType?.Name ?? string.Empty,
        FirstName = value.FirstName,
        LastName = value.LastName,
        AddressLine1 = value.AddressLine1,
        City = value.City,
        State = value.State,
        PostalCode = value.PostalCode,
        Phone = value.Phone,
        Email = value.Email,
        IsActive = value.IsActive,
        CreatedAtUtc = value.CreatedAtUtc,
        UpdatedAtUtc = value.UpdatedAtUtc,
    };
}
