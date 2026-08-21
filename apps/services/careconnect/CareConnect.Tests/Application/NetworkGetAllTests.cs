using CareConnect.Application.Interfaces;
using CareConnect.Application.Repositories;
using CareConnect.Application.Services;
using CareConnect.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CareConnect.Tests.Application;

// Regression test for a bug where GetAllAsync used Task.WhenAll to fan out
// GetWithProvidersAsync calls that all share a single scoped DbContext — EF Core
// throws "A second operation was started on this context instance before a
// previous operation completed" as soon as a tenant has more than one network.
// This never surfaced in this environment until a tenant's second network was
// created via the LSV3-1084 CareConnectReferrerAdmin "New Network" flow.
public class NetworkGetAllTests
{
    [Fact]
    public async Task GetAllAsync_WithMultipleNetworks_NeverOverlapsRepositoryCalls()
    {
        var tenantId = Guid.CreateVersion7();
        var networkA = ProviderNetwork.Create(tenantId, "Network A", string.Empty);
        var networkB = ProviderNetwork.Create(tenantId, "Network B", string.Empty);
        var networkC = ProviderNetwork.Create(tenantId, "Network C", string.Empty);

        var networks = new Mock<INetworkRepository>();
        networks.Setup(r => r.GetAllByTenantAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([networkA, networkB, networkC]);

        var inFlight = 0;
        var maxConcurrent = 0;
        networks.Setup(r => r.GetWithProvidersAsync(tenantId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, Guid id, CancellationToken _) =>
            {
                var current = Interlocked.Increment(ref inFlight);
                maxConcurrent = Math.Max(maxConcurrent, current);
                // Yield to let a buggy Task.WhenAll fan-out actually overlap before returning.
                await Task.Delay(10);
                Interlocked.Decrement(ref inFlight);
                var network = new[] { networkA, networkB, networkC }.First(n => n.Id == id);
                return network;
            });

        var sut = new NetworkService(
            networks.Object,
            Mock.Of<ICategoryRepository>(),
            Mock.Of<ISpecialtyRepository>(),
            Mock.Of<IProviderImportParser>(),
            NullLogger<NetworkService>.Instance);

        var result = await sut.GetAllAsync(tenantId);

        Assert.Equal(3, result.Count);
        Assert.Equal(1, maxConcurrent);
    }
}
