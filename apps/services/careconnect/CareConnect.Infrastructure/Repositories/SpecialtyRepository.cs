using CareConnect.Application.Repositories;
using CareConnect.Domain;
using CareConnect.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace CareConnect.Infrastructure.Repositories;

public class SpecialtyRepository : ISpecialtyRepository
{
    private readonly CareConnectDbContext _db;

    public SpecialtyRepository(CareConnectDbContext db)
    {
        _db = db;
    }

    public async Task<List<Specialty>> GetAllAsync(bool includeInactive = false, CancellationToken ct = default)
    {
        var q = _db.Specialties.AsNoTracking().AsQueryable();
        if (!includeInactive)
            q = q.Where(s => s.IsActive);
        return await q
            .OrderBy(s => s.Code == "OTHER" ? 1 : 0)
            .ThenBy(s => s.Name)
            .ToListAsync(ct);
    }

    public async Task<Specialty?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.Specialties.FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    public async Task<Specialty?> GetByCodeAsync(string code, CancellationToken ct = default)
    {
        var normalized = Specialty.NormalizeCode(code);
        return await _db.Specialties.FirstOrDefaultAsync(s => s.Code == normalized, ct);
    }

    public async Task<List<Specialty>> GetActiveByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var distinct = ids.Distinct().ToList();
        if (distinct.Count == 0) return [];

        return await _db.Specialties
            .AsNoTracking()
            .Where(s => s.IsActive && distinct.Contains(s.Id))
            .ToListAsync(ct);
    }

    public async Task<List<Specialty>> GetActiveByCodesAsync(IEnumerable<string> codes, CancellationToken ct = default)
    {
        var normalized = codes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(Specialty.NormalizeCode)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (normalized.Count == 0) return [];

        return await _db.Specialties
            .AsNoTracking()
            .Where(s => s.IsActive && normalized.Contains(s.Code))
            .ToListAsync(ct);
    }

    public async Task<bool> CodeExistsAsync(string code, Guid? excludeId = null, CancellationToken ct = default)
    {
        var normalized = Specialty.NormalizeCode(code);
        return await _db.Specialties.AnyAsync(
            s => s.Code == normalized && (!excludeId.HasValue || s.Id != excludeId.Value),
            ct);
    }

    public async Task AddAsync(Specialty specialty, CancellationToken ct = default)
    {
        await _db.Specialties.AddAsync(specialty, ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _db.SaveChangesAsync(ct);
    }
}
