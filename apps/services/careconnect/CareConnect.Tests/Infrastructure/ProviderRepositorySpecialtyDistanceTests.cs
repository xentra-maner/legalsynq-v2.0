using CareConnect.Application.DTOs;
using CareConnect.Domain;
using CareConnect.Infrastructure.Data;
using CareConnect.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CareConnect.Tests.Infrastructure;

public class ProviderRepositorySpecialtyDistanceTests
{
    [Fact]
    public async Task SearchAsync_FiltersBySpecialtyAndSortsByExactDistance()
    {
        await using var db = CreateDb();
        var tenantId = Guid.CreateVersion7();
        var specialty = Specialty.Create("Physical Therapy", "PHYSICAL_THERAPY", null);
        var otherSpecialty = Specialty.Create("Neurology", "NEUROLOGY", null);

        var nearProvider = Provider.Create(
            tenantId, "Near PT", null, "near@example.com", "555-0100",
            "1 Main St", "Los Angeles", "CA", "90012", true, true, null,
            latitude: 34.0522, longitude: -118.2437, geoPointSource: "test");
        var farProvider = Provider.Create(
            tenantId, "Far PT", null, "far@example.com", "555-0101",
            "2 Main St", "Austin", "TX", "78701", true, true, null,
            latitude: 30.2672, longitude: -97.7431, geoPointSource: "test");
        var wrongSpecialtyProvider = Provider.Create(
            tenantId, "Near Neuro", null, "neuro@example.com", "555-0102",
            "3 Main St", "Los Angeles", "CA", "90012", true, true, null,
            latitude: 34.0523, longitude: -118.2436, geoPointSource: "test");

        db.Specialties.AddRange(specialty, otherSpecialty);
        db.Providers.AddRange(nearProvider, farProvider, wrongSpecialtyProvider);
        db.ProviderSpecialties.AddRange(
            new ProviderSpecialty { ProviderId = nearProvider.Id, SpecialtyId = specialty.Id, Specialty = specialty, IsPrimary = true },
            new ProviderSpecialty { ProviderId = farProvider.Id, SpecialtyId = specialty.Id, Specialty = specialty, IsPrimary = true },
            new ProviderSpecialty { ProviderId = wrongSpecialtyProvider.Id, SpecialtyId = otherSpecialty.Id, Specialty = otherSpecialty, IsPrimary = true });
        await db.SaveChangesAsync();

        var repository = new ProviderRepository(db);

        var result = await repository.SearchAsync(tenantId, new GetProvidersQuery
        {
            SpecialtyCode = "physical therapy",
            Latitude = 34.0522,
            Longitude = -118.2437,
            RadiusMiles = 1500,
            Page = 1,
            PageSize = 10,
            IsActive = true
        });

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(["Near PT", "Far PT"], result.Items.Select(row => row.Provider.Name).ToList());
        Assert.All(result.Items, row => Assert.NotNull(row.DistanceMiles));
        Assert.True(result.Items[0].DistanceMiles < result.Items[1].DistanceMiles);
    }

    [Fact]
    public async Task SearchAsync_OnlyReturnsProvidersForTheRequestingTenant()
    {
        await using var db = CreateDb();
        var tenantId = Guid.CreateVersion7();
        var otherTenantId = Guid.CreateVersion7();

        var ownProvider = Provider.Create(
            tenantId, "Own Tenant PT", null, "own@example.com", "555-0200",
            "1 Main St", "Los Angeles", "CA", "90012", true, true, null);
        var otherTenantProvider = Provider.Create(
            otherTenantId, "Other Tenant PT", null, "other@example.com", "555-0201",
            "2 Main St", "Los Angeles", "CA", "90012", true, true, null);

        db.Providers.AddRange(ownProvider, otherTenantProvider);
        await db.SaveChangesAsync();

        var repository = new ProviderRepository(db);

        var result = await repository.SearchAsync(tenantId, new GetProvidersQuery
        {
            Page = 1,
            PageSize = 10,
            IsActive = true
        });

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("Own Tenant PT", Assert.Single(result.Items).Provider.Name);
    }

    private static CareConnectDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<CareConnectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new CareConnectDbContext(options);
    }
}
