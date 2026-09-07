using Liens.Domain.Entities;

namespace Liens.Application.Repositories;

public sealed record CompanyReassignmentCounts(
    int ContactPersons,
    int Liens,
    int Cases,
    int Offers,
    int BuyerAccessLinks,
    int PortfolioBuyers);

public sealed record CompanyContactPersonReassignmentCounts(
    int Liens,
    int Cases,
    int BuyerAccessLinks);

public sealed record CompanyDetailsSnapshot(
    int TotalCases,
    int ActiveCases,
    decimal TotalBillingForActiveCases,
    IReadOnlyList<CompanyRecentCaseSnapshot> RecentCases);

public sealed record CompanyRecentCaseSnapshot(
    Guid Id,
    string CaseNumber,
    string ClientFirstName,
    string ClientLastName,
    string Status,
    decimal BillingAmount,
    DateTime UpdatedAtUtc);

public interface ICompanyRepository
{
    Task<List<CompanyType>> GetCompanyTypesAsync(CancellationToken ct = default);
    Task<CompanyType?> GetCompanyTypeAsync(Guid id, CancellationToken ct = default);
    Task<List<ContactPersonType>> GetContactPersonTypesAsync(
        Guid tenantId, Guid orgId, Guid companyTypeId, CancellationToken ct = default);
    Task<ContactPersonType?> GetContactPersonTypeAsync(
        Guid tenantId, Guid orgId, Guid id, CancellationToken ct = default);
    Task<bool> ContactPersonTypeCodeExistsAsync(
        Guid tenantId, Guid orgId, Guid companyTypeId, string code, CancellationToken ct = default);
    Task<int> GetNextContactPersonTypeSortOrderAsync(
        Guid tenantId, Guid orgId, Guid companyTypeId, CancellationToken ct = default);
    Task AddContactPersonTypeAsync(ContactPersonType contactPersonType, CancellationToken ct = default);
    Task<(List<Company> Items, int TotalCount)> SearchCompaniesAsync(
        Guid tenantId, Guid orgId, string? search, Guid? companyTypeId, bool? isActive,
        int page, int pageSize, CancellationToken ct = default);
    Task<List<Company>> GetCompaniesForExportAsync(
        Guid tenantId, Guid orgId, string? search, Guid? companyTypeId, bool? isActive,
        CancellationToken ct = default);
    Task<Company?> GetCompanyAsync(Guid tenantId, Guid orgId, Guid id, CancellationToken ct = default);
    Task<List<Company>> GetCompaniesByIdsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    Task<List<Company>> FindLawFirmCompaniesByNameAsync(
        Guid tenantId, string search, CancellationToken ct = default);
    Task<CompanyDetailsSnapshot> GetCompanyDetailsAsync(
        Guid tenantId, Guid orgId, Guid companyId, Guid companyTypeId,
        int page, int pageSize, CancellationToken ct = default);
    Task<bool> CompanyNameExistsAsync(
        Guid tenantId, Guid orgId, Guid companyTypeId, string normalizedName,
        Guid? excludingId = null, CancellationToken ct = default);
    Task AddCompanyAsync(Company company, CancellationToken ct = default);
    Task UpdateCompanyAsync(Company company, CancellationToken ct = default);
    Task<CompanyReassignmentCounts> ReassignCompanyAsync(
        Guid tenantId, Guid orgId, Company source, Company target,
        Guid actingUserId, CancellationToken ct = default);
    Task<List<CompanyContactPerson>> GetContactPersonsAsync(
        Guid tenantId, Guid companyId, bool? isActive, CancellationToken ct = default);
    Task<List<CompanyContactPerson>> GetContactPersonsByOrgIdAsync(
        Guid tenantId, Guid orgId, bool? isActive, CancellationToken ct = default);
    Task<(List<CompanyContactPerson> Items, int TotalCount)> SearchContactPersonsAsync(
        Guid tenantId, Guid orgId, string? search, Guid? companyTypeId,
        Guid? contactPersonTypeId, bool? isActive, int page, int pageSize,
        CancellationToken ct = default);
    Task<List<CompanyContactPerson>> GetContactPersonsForExportAsync(
        Guid tenantId, Guid orgId, Guid? companyId, string? search, Guid? companyTypeId,
        Guid? contactPersonTypeId, bool? isActive, CancellationToken ct = default);
    Task<CompanyContactPerson?> GetContactPersonAsync(
        Guid tenantId, Guid companyId, Guid id, CancellationToken ct = default);
    Task<CompanyContactPerson?> GetContactPersonInScopeAsync(
        Guid tenantId, Guid orgId, Guid id, CancellationToken ct = default);
    Task<bool> ContactPersonEmailExistsAsync(
        Guid tenantId, string email, Guid? excludingId = null, CancellationToken ct = default);
    Task<List<CompanyContactPerson>> GetContactPersonsByIdsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    Task AddContactPersonAsync(CompanyContactPerson contact, CancellationToken ct = default);
    Task UpdateContactPersonAsync(CompanyContactPerson contact, CancellationToken ct = default);
    Task<CompanyContactPersonReassignmentCounts> ReassignContactPersonAsync(
        Guid tenantId, Guid orgId, CompanyContactPerson source, CompanyContactPerson target,
        Guid actingUserId, CancellationToken ct = default);
}
