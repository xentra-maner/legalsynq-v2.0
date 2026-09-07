using Liens.Application.Repositories;
using Liens.Domain;
using Liens.Domain.Entities;
using Liens.Domain.Enums;
using Liens.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using BuildingBlocks.Exceptions;

namespace Liens.Infrastructure.Repositories;

public sealed class CompanyRepository : ICompanyRepository
{
    private readonly LiensDbContext _db;

    public CompanyRepository(LiensDbContext db) => _db = db;

    public Task<List<CompanyType>> GetCompanyTypesAsync(CancellationToken ct = default)
        => _db.CompanyTypes.AsNoTracking()
            .Where(value => value.IsActive)
            .OrderBy(value => value.SortOrder)
            .ThenBy(value => value.Name)
            .ToListAsync(ct);

    public Task<CompanyType?> GetCompanyTypeAsync(Guid id, CancellationToken ct = default)
        => _db.CompanyTypes.AsNoTracking().FirstOrDefaultAsync(value => value.Id == id, ct);

    public Task<List<ContactPersonType>> GetContactPersonTypesAsync(
        Guid tenantId, Guid orgId, Guid companyTypeId, CancellationToken ct = default)
        => _db.ContactPersonTypes.AsNoTracking()
            .Where(value => value.CompanyTypeId == companyTypeId && value.IsActive &&
                ((!value.TenantId.HasValue && !value.OrgId.HasValue) ||
                 (value.TenantId == tenantId && value.OrgId == orgId)))
            .OrderBy(value => value.SortOrder)
            .ThenBy(value => value.Name)
            .ToListAsync(ct);

    public Task<ContactPersonType?> GetContactPersonTypeAsync(
        Guid tenantId, Guid orgId, Guid id, CancellationToken ct = default)
        => _db.ContactPersonTypes.AsNoTracking().FirstOrDefaultAsync(value => value.Id == id &&
            ((!value.TenantId.HasValue && !value.OrgId.HasValue) ||
             (value.TenantId == tenantId && value.OrgId == orgId)), ct);

    public Task<bool> ContactPersonTypeCodeExistsAsync(
        Guid tenantId, Guid orgId, Guid companyTypeId, string code, CancellationToken ct = default)
    {
        var normalizedCode = code.ToUpper();
        return _db.ContactPersonTypes.AsNoTracking().AnyAsync(value =>
            value.CompanyTypeId == companyTypeId &&
            value.Code.ToUpper() == normalizedCode &&
            ((!value.TenantId.HasValue && !value.OrgId.HasValue) ||
             (value.TenantId == tenantId && value.OrgId == orgId)), ct);
    }

    public async Task<int> GetNextContactPersonTypeSortOrderAsync(
        Guid tenantId, Guid orgId, Guid companyTypeId, CancellationToken ct = default)
    {
        var maxSortOrder = await _db.ContactPersonTypes.AsNoTracking()
            .Where(value => value.CompanyTypeId == companyTypeId &&
                ((!value.TenantId.HasValue && !value.OrgId.HasValue) ||
                 (value.TenantId == tenantId && value.OrgId == orgId)))
            .Select(value => (int?)value.SortOrder)
            .MaxAsync(ct);
        return Math.Min((maxSortOrder ?? 0) + 1, 10000);
    }

    public async Task AddContactPersonTypeAsync(
        ContactPersonType contactPersonType, CancellationToken ct = default)
    {
        await _db.ContactPersonTypes.AddAsync(contactPersonType, ct);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is MySqlException { Number: 1062 })
        {
            throw new ConflictException(
                "A contact-person type with this code already exists for the company type.");
        }
    }

    public async Task<(List<Company> Items, int TotalCount)> SearchCompaniesAsync(
        Guid tenantId, Guid orgId, string? search, Guid? companyTypeId, bool? isActive,
        int page, int pageSize, CancellationToken ct = default)
    {
        var query = BuildCompanyQuery(tenantId, orgId, search, companyTypeId, isActive);

        var totalCount = await query.CountAsync(ct);
        var items = await query.OrderBy(value => value.Name)
            .ThenBy(value => value.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return (items, totalCount);
    }

    public Task<List<Company>> GetCompaniesForExportAsync(
        Guid tenantId, Guid orgId, string? search, Guid? companyTypeId, bool? isActive,
        CancellationToken ct = default)
        => BuildCompanyQuery(tenantId, orgId, search, companyTypeId, isActive)
            .OrderBy(value => value.Name)
            .ThenBy(value => value.Id)
            .ToListAsync(ct);

    public Task<Company?> GetCompanyAsync(
        Guid tenantId, Guid orgId, Guid id, CancellationToken ct = default)
        => _db.Companies.Include(value => value.CompanyType)
            .FirstOrDefaultAsync(value => value.TenantId == tenantId && value.OrgId == orgId && value.Id == id, ct);

    public Task<List<Company>> GetCompaniesByIdsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
        => ids.Count == 0
            ? Task.FromResult(new List<Company>())
            : _db.Companies.AsNoTracking()
                .Where(value => value.TenantId == tenantId && ids.Contains(value.Id))
                .ToListAsync(ct);

    public Task<List<Company>> FindLawFirmCompaniesByNameAsync(
        Guid tenantId, string search, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(search))
            return Task.FromResult(new List<Company>());

        var term = Company.NormalizeName(search);
        return _db.Companies.AsNoTracking()
            .Where(value =>
                value.TenantId == tenantId &&
                value.CompanyTypeId == CompanyDirectoryReferenceData.LawFirmId &&
                value.NormalizedName.Contains(term))
            .ToListAsync(ct);
    }

    public async Task<CompanyDetailsSnapshot> GetCompanyDetailsAsync(
        Guid tenantId, Guid orgId, Guid companyId, Guid companyTypeId,
        int page, int pageSize, CancellationToken ct = default)
    {
        var scopedLiens = _db.Liens.AsNoTracking().Where(value =>
            value.TenantId == tenantId &&
            (value.OrgId == orgId || value.SellingOrgId == orgId));
        var companyLiens = companyTypeId switch
        {
            var id when id == CompanyDirectoryReferenceData.FundingCompanyId =>
                scopedLiens.Where(value => value.FundingCompanyCompanyId == companyId),
            var id when id == CompanyDirectoryReferenceData.MedicalProviderId =>
                scopedLiens.Where(value => value.MedicalProviderCompanyId == companyId),
            var id when id == CompanyDirectoryReferenceData.MedicalFacilityId =>
                scopedLiens.Where(value => value.MedicalFacilityCompanyId == companyId),
            _ => scopedLiens.Where(_ => false),
        };

        var scopedCases = _db.Cases.AsNoTracking().Where(value =>
            value.TenantId == tenantId && value.OrgId == orgId);
        var companyCases = companyTypeId == CompanyDirectoryReferenceData.LawFirmId
            ? scopedCases.Where(value => value.HandlingLawFirmCompanyId == companyId)
            : scopedCases.Where(value => companyLiens.Any(lien => lien.CaseId == value.Id));

        var totalCases = await companyCases.CountAsync(ct);
        var activeCases = companyCases.Where(value =>
            value.Status != CaseStatus.CaseSettled && value.Status != CaseStatus.Closed);
        var activeCaseCount = await activeCases.CountAsync(ct);
        var activeCaseIds = activeCases.Select(value => value.Id);
        var billableLiens = companyTypeId == CompanyDirectoryReferenceData.LawFirmId
            ? scopedLiens.Where(value => value.CaseId.HasValue && activeCaseIds.Contains(value.CaseId.Value))
            : companyLiens.Where(value => value.CaseId.HasValue && activeCaseIds.Contains(value.CaseId.Value));
        var totalBilling = await billableLiens
            .Select(value => (decimal?)value.OriginalAmount)
            .SumAsync(ct) ?? 0m;

        var recentCases = await companyCases
            .OrderByDescending(value => value.UpdatedAtUtc)
            .ThenByDescending(value => value.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(value => new
            {
                value.Id,
                value.CaseNumber,
                value.ClientFirstName,
                value.ClientLastName,
                value.Status,
                value.UpdatedAtUtc,
            })
            .ToListAsync(ct);

        var recentCaseIds = recentCases.Select(value => value.Id).ToList();
        var recentCaseBillings = recentCaseIds.Count == 0
            ? new Dictionary<Guid, decimal>()
            : await (companyTypeId == CompanyDirectoryReferenceData.LawFirmId
                    ? scopedLiens.Where(value => value.CaseId.HasValue && recentCaseIds.Contains(value.CaseId.Value))
                    : companyLiens.Where(value => value.CaseId.HasValue && recentCaseIds.Contains(value.CaseId.Value)))
                .GroupBy(value => value.CaseId!.Value)
                .Select(group => new { CaseId = group.Key, BillingAmount = group.Sum(value => value.OriginalAmount) })
                .ToDictionaryAsync(value => value.CaseId, value => value.BillingAmount, ct);

        return new CompanyDetailsSnapshot(
            totalCases,
            activeCaseCount,
            totalBilling,
            recentCases.Select(value => new CompanyRecentCaseSnapshot(
                value.Id,
                value.CaseNumber,
                value.ClientFirstName,
                value.ClientLastName,
                value.Status,
                recentCaseBillings.GetValueOrDefault(value.Id),
                value.UpdatedAtUtc)).ToList());
    }

    public Task<bool> CompanyNameExistsAsync(
        Guid tenantId, Guid orgId, Guid companyTypeId, string normalizedName,
        Guid? excludingId = null, CancellationToken ct = default)
        => _db.Companies.AsNoTracking().AnyAsync(value =>
            value.TenantId == tenantId &&
            value.OrgId == orgId &&
            value.CompanyTypeId == companyTypeId &&
            value.NormalizedName == normalizedName &&
            (!excludingId.HasValue || value.Id != excludingId.Value), ct);

    public async Task AddCompanyAsync(Company company, CancellationToken ct = default)
    {
        await _db.Companies.AddAsync(company, ct);
        await SaveCompanyChangesAsync(ct);
    }

    public Task UpdateCompanyAsync(Company company, CancellationToken ct = default)
        => SaveCompanyChangesAsync(ct);

    public async Task<CompanyReassignmentCounts> ReassignCompanyAsync(
        Guid tenantId, Guid orgId, Company source, Company target,
        Guid actingUserId, CancellationToken ct = default)
    {
        var contacts = await _db.CompanyContactPersons
            .Where(value => value.TenantId == tenantId && value.CompanyId == source.Id)
            .ToListAsync(ct);
        var liens = await _db.Liens
            .Where(value => value.TenantId == tenantId &&
                (value.OrgId == orgId || value.SellingOrgId == orgId) &&
                (value.FundingCompanyCompanyId == source.Id ||
                 value.MedicalProviderCompanyId == source.Id ||
                 value.MedicalFacilityCompanyId == source.Id))
            .ToListAsync(ct);
        var cases = await _db.Cases
            .Where(value => value.TenantId == tenantId && value.OrgId == orgId &&
                value.HandlingLawFirmCompanyId == source.Id)
            .ToListAsync(ct);
        var offers = await _db.LienOffers
            .Where(value => value.TenantId == tenantId && value.SellerOrgId == orgId &&
                value.BuyerCompanyId == source.Id)
            .ToListAsync(ct);
        var buyerAccessLinks = await _db.SellingBuyerAccessLinks
            .Where(value => value.TenantId == tenantId && value.SellerOrgId == orgId &&
                value.BuyerCompanyId == source.Id)
            .ToListAsync(ct);
        var portfolioBuyers = await _db.SellingPortfolioBuyers
            .Where(value => value.TenantId == tenantId && value.BuyerCompanyId == source.Id &&
                _db.SellingPortfolios.Any(portfolio =>
                    portfolio.Id == value.PortfolioId &&
                    portfolio.TenantId == tenantId &&
                    portfolio.SellerOrgId == orgId))
            .ToListAsync(ct);

        foreach (var contact in contacts)
            contact.ReassignCompany(target.Id, actingUserId);
        foreach (var lien in liens)
            lien.ReassignCanonicalCompany(source.Id, target.Id, actingUserId);
        foreach (var caseEntity in cases)
            caseEntity.ReassignCanonicalCompany(source.Id, target.Id, actingUserId);
        foreach (var offer in offers)
            offer.ReassignCanonicalBuyerCompany(source.Id, target.Id, actingUserId);
        foreach (var accessLink in buyerAccessLinks)
            accessLink.ReassignCanonicalBuyerCompany(source.Id, target.Id, actingUserId);
        foreach (var portfolioBuyer in portfolioBuyers)
            portfolioBuyer.ReassignCanonicalBuyerCompany(source.Id, target.Id, actingUserId);

        await _db.SaveChangesAsync(ct);
        return new CompanyReassignmentCounts(
            contacts.Count,
            liens.Count,
            cases.Count,
            offers.Count,
            buyerAccessLinks.Count,
            portfolioBuyers.Count);
    }

    public Task<List<CompanyContactPerson>> GetContactPersonsAsync(
        Guid tenantId, Guid companyId, bool? isActive, CancellationToken ct = default)
    {
        var query = _db.CompanyContactPersons.AsNoTracking()
            .Include(value => value.ContactPersonType)
            .Where(value => value.TenantId == tenantId && value.CompanyId == companyId);
        if (isActive.HasValue) query = query.Where(value => value.IsActive == isActive.Value);
        return query.OrderBy(value => value.LastName).ThenBy(value => value.FirstName).ToListAsync(ct);
    }

    public Task<List<CompanyContactPerson>> GetContactPersonsByOrgIdAsync(
        Guid tenantId, Guid orgId, bool? isActive, CancellationToken ct = default)
    {
        var query = _db.CompanyContactPersons.AsNoTracking()
            .Include(value => value.Company)
                .ThenInclude(value => value!.CompanyType)
            .Include(value => value.ContactPersonType)
            .Where(value => value.TenantId == tenantId &&
                value.Company!.TenantId == tenantId &&
                value.Company.OrgId == orgId &&
                value.Company.IsActive);

        if (isActive.HasValue)
            query = query.Where(value => value.IsActive == isActive.Value);

        return query
            .OrderBy(value => value.Company!.Name)
            .ThenBy(value => value.LastName)
            .ThenBy(value => value.FirstName)
            .ThenBy(value => value.Id)
            .ToListAsync(ct);
    }

    public async Task<(List<CompanyContactPerson> Items, int TotalCount)> SearchContactPersonsAsync(
        Guid tenantId, Guid orgId, string? search, Guid? companyTypeId,
        Guid? contactPersonTypeId, bool? isActive, int page, int pageSize,
        CancellationToken ct = default)
    {
        var query = _db.CompanyContactPersons.AsNoTracking()
            .Include(value => value.Company)
                .ThenInclude(value => value!.CompanyType)
            .Include(value => value.ContactPersonType)
            .Where(value => value.TenantId == tenantId &&
                value.Company!.OrgId == orgId);

        if (companyTypeId.HasValue)
            query = query.Where(value => value.Company!.CompanyTypeId == companyTypeId.Value);
        if (contactPersonTypeId.HasValue)
            query = query.Where(value => value.ContactPersonTypeId == contactPersonTypeId.Value);
        if (isActive.HasValue)
            query = query.Where(value => value.IsActive == isActive.Value);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(value =>
                value.FirstName.Contains(term) ||
                value.LastName.Contains(term) ||
                (value.Email != null && value.Email.Contains(term)) ||
                (value.Phone != null && value.Phone.Contains(term)) ||
                value.Company!.Name.Contains(term) ||
                value.Company.CompanyType!.Name.Contains(term) ||
                value.ContactPersonType!.Name.Contains(term));
        }

        var totalCount = await query.CountAsync(ct);
        var items = await query
            .OrderBy(value => value.Company!.Name)
            .ThenBy(value => value.LastName)
            .ThenBy(value => value.FirstName)
            .ThenBy(value => value.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return (items, totalCount);
    }

    public Task<List<CompanyContactPerson>> GetContactPersonsForExportAsync(
        Guid tenantId, Guid orgId, Guid? companyId, string? search, Guid? companyTypeId,
        Guid? contactPersonTypeId, bool? isActive, CancellationToken ct = default)
    {
        var query = _db.CompanyContactPersons.AsNoTracking()
            .Include(value => value.Company)
                .ThenInclude(value => value!.CompanyType)
            .Include(value => value.ContactPersonType)
            .Where(value => value.TenantId == tenantId && value.Company!.OrgId == orgId);

        if (companyId.HasValue) query = query.Where(value => value.CompanyId == companyId.Value);
        if (companyTypeId.HasValue) query = query.Where(value => value.Company!.CompanyTypeId == companyTypeId.Value);
        if (contactPersonTypeId.HasValue)
            query = query.Where(value => value.ContactPersonTypeId == contactPersonTypeId.Value);
        if (isActive.HasValue) query = query.Where(value => value.IsActive == isActive.Value);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(value => value.FirstName.Contains(term) ||
                value.LastName.Contains(term) ||
                (value.Email != null && value.Email.Contains(term)) ||
                (value.Phone != null && value.Phone.Contains(term)) ||
                value.Company!.Name.Contains(term));
        }

        return query.OrderBy(value => value.Company!.Name)
            .ThenBy(value => value.LastName)
            .ThenBy(value => value.FirstName)
            .ThenBy(value => value.Id)
            .ToListAsync(ct);
    }

    public Task<CompanyContactPerson?> GetContactPersonAsync(
        Guid tenantId, Guid companyId, Guid id, CancellationToken ct = default)
        => _db.CompanyContactPersons.Include(value => value.ContactPersonType)
            .FirstOrDefaultAsync(value => value.TenantId == tenantId && value.CompanyId == companyId && value.Id == id, ct);

    public Task<CompanyContactPerson?> GetContactPersonInScopeAsync(
        Guid tenantId, Guid orgId, Guid id, CancellationToken ct = default)
        => _db.CompanyContactPersons
            .Include(value => value.Company)
                .ThenInclude(value => value!.CompanyType)
            .Include(value => value.ContactPersonType)
            .FirstOrDefaultAsync(value => value.TenantId == tenantId && value.Id == id &&
                value.Company!.OrgId == orgId, ct);

    public Task<bool> ContactPersonEmailExistsAsync(
        Guid tenantId, string email, Guid? excludingId = null, CancellationToken ct = default)
    {
        var normalizedEmail = email.Trim().ToUpperInvariant();
        return _db.CompanyContactPersons.AsNoTracking().AnyAsync(value =>
            value.TenantId == tenantId &&
            value.Email != null &&
            value.Email.ToUpper() == normalizedEmail &&
            (!excludingId.HasValue || value.Id != excludingId.Value), ct);
    }

    public Task<List<CompanyContactPerson>> GetContactPersonsByIdsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
        => ids.Count == 0
            ? Task.FromResult(new List<CompanyContactPerson>())
            : _db.CompanyContactPersons.AsNoTracking()
                .Where(value => value.TenantId == tenantId && ids.Contains(value.Id))
                .ToListAsync(ct);

    public async Task AddContactPersonAsync(CompanyContactPerson contact, CancellationToken ct = default)
    {
        await _db.CompanyContactPersons.AddAsync(contact, ct);
        await _db.SaveChangesAsync(ct);
    }

    public Task UpdateContactPersonAsync(CompanyContactPerson contact, CancellationToken ct = default)
        => _db.SaveChangesAsync(ct);

    public async Task<CompanyContactPersonReassignmentCounts> ReassignContactPersonAsync(
        Guid tenantId, Guid orgId, CompanyContactPerson source, CompanyContactPerson target,
        Guid actingUserId, CancellationToken ct = default)
    {
        var liens = await _db.Liens
            .Where(value => value.TenantId == tenantId &&
                (value.OrgId == orgId || value.SellingOrgId == orgId) &&
                value.FundingCompanyContactPersonId == source.Id)
            .ToListAsync(ct);
        var cases = await _db.Cases
            .Where(value => value.TenantId == tenantId && value.OrgId == orgId &&
                value.CaseManagerContactPersonId == source.Id)
            .ToListAsync(ct);
        var buyerAccessLinks = await _db.SellingBuyerAccessLinks
            .Where(value => value.TenantId == tenantId && value.SellerOrgId == orgId &&
                value.BuyerCompanyContactPersonId == source.Id)
            .ToListAsync(ct);

        foreach (var lien in liens)
            lien.ReassignCanonicalContactPerson(source.Id, target.Id, target.CompanyId, actingUserId);
        foreach (var caseEntity in cases)
            caseEntity.ReassignCanonicalContactPerson(
                source.Id, target.Id, source.CompanyId, target.CompanyId, actingUserId);
        foreach (var accessLink in buyerAccessLinks)
            accessLink.ReassignCanonicalBuyerContactPerson(
                source.Id, target.Id, target.CompanyId, actingUserId);

        await _db.SaveChangesAsync(ct);
        return new CompanyContactPersonReassignmentCounts(
            liens.Count,
            cases.Count,
            buyerAccessLinks.Count);
    }

    private IQueryable<Company> BuildCompanyQuery(
        Guid tenantId, Guid orgId, string? search, Guid? companyTypeId, bool? isActive)
    {
        var query = _db.Companies.AsNoTracking()
            .Include(value => value.CompanyType)
            .Where(value => value.TenantId == tenantId && value.OrgId == orgId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(value => value.Name.Contains(term) ||
                (value.Email != null && value.Email.Contains(term)) ||
                (value.City != null && value.City.Contains(term)));
        }
        if (companyTypeId.HasValue) query = query.Where(value => value.CompanyTypeId == companyTypeId.Value);
        if (isActive.HasValue) query = query.Where(value => value.IsActive == isActive.Value);
        return query;
    }

    private async Task SaveCompanyChangesAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is MySqlException { Number: 1062 })
        {
            throw new ConflictException("A company with this name and type already exists.");
        }
    }
}
