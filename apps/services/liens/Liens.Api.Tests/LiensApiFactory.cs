using Liens.Application.DTOs;
using Liens.Application.Interfaces;
using Liens.Api.Tests.Helpers;
using Liens.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Collections.Concurrent;
using System.Net;

namespace Liens.Api.Tests;

/// <summary>
/// Single WebApplicationFactory shared across all legacy API test classes.
/// Replaces MySQL with an InMemory database and stubs out external HTTP calls.
/// </summary>
public class LiensApiFactory : WebApplicationFactory<Program>
{
    public string DbName { get; } = $"liens-tests-{Guid.CreateVersion7()}";

    static LiensApiFactory()
    {
        // These must be set BEFORE the host builds because Program.cs reads them
        // via builder.Configuration during service registration.
        Environment.SetEnvironmentVariable("Jwt__Issuer",     JwtTokenHelper.Issuer);
        Environment.SetEnvironmentVariable("Jwt__Audience",   JwtTokenHelper.Audience);
        Environment.SetEnvironmentVariable("Jwt__SigningKey",  JwtTokenHelper.SigningKey);

        // Dummy DB connection string — replaced with InMemory in ConfigureServices.
        Environment.SetEnvironmentVariable("ConnectionStrings__LiensDb",
            "Server=localhost;Database=liens_test;Uid=test;Pwd=test;");

        // Dummy URLs for external HTTP clients — not called in tests.
        Environment.SetEnvironmentVariable("Flow__BaseUrl",              "http://localhost:19999/");
        Environment.SetEnvironmentVariable("AuditClient__BaseUrl",       "http://localhost:19998/");
        Environment.SetEnvironmentVariable("Services__NotificationsUrl", "http://localhost:19997/");
        Environment.SetEnvironmentVariable("Services__TaskServiceUrl",   "http://localhost:19996/");
        Environment.SetEnvironmentVariable("Services__DocumentsUrl",     "http://localhost:19995/");
        Environment.SetEnvironmentVariable("Services__CommerceUrl",      "http://localhost:19994/");
        Environment.SetEnvironmentVariable("Liens__Selling__BuyerPortalBaseUrl",
            "https://app.legalsynq.test/selling/public");
        Environment.SetEnvironmentVariable("TenantService__ProvisioningToken",
            StubIdentityServiceHandler.ExpectedProvisioningToken);

        // Service token issuer requires a signing key.
        Environment.SetEnvironmentVariable("FLOW_SERVICE_TOKEN_SECRET",
            JwtTokenHelper.SigningKey);
        Environment.SetEnvironmentVariable("ServiceTokens__SigningKey",
            JwtTokenHelper.SigningKey);
        Environment.SetEnvironmentVariable("ServiceTokens__liens-service__SigningKey",
            JwtTokenHelper.SigningKey);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Replace MySQL DbContext with InMemory.
            // Remove every descriptor whose ServiceType references LiensDbContext
            // (includes DbContext, DbContextOptions<T>, IDbContextOptionsConfiguration<T>).
            var toRemove = services
                .Where(d => d.ServiceType.FullName != null
                    && (d.ServiceType.FullName.Contains("LiensDbContext")
                        || (d.ServiceType.IsGenericType
                            && d.ServiceType.GetGenericArguments()
                               .Any(a => a.FullName != null
                                    && a.FullName.Contains("LiensDbContext")))))
                .ToList();
            foreach (var d in toRemove) services.Remove(d);
            services.AddDbContext<LiensDbContext>(o => o
                .UseInMemoryDatabase(DbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

            // Stub out IFlowInstanceResolver so no Flow HTTP calls happen.
            services.RemoveAll<IFlowInstanceResolver>();
            services.AddScoped<IFlowInstanceResolver, NoOpFlowInstanceResolver>();

            services.RemoveAll<INotificationPublisher>();
            services.AddSingleton<CapturingNotificationPublisher>();
            services.AddSingleton<INotificationPublisher>(sp => sp.GetRequiredService<CapturingNotificationPublisher>());

            services.RemoveAll<IAuditPublisher>();
            services.AddSingleton<CapturingAuditPublisher>();
            services.AddSingleton<IAuditPublisher>(sp => sp.GetRequiredService<CapturingAuditPublisher>());

            services.RemoveAll<ILegacyDocumentUploadClient>();
            services.AddSingleton<CapturingLegacyDocumentUploadClient>();
            services.AddSingleton<ILegacyDocumentUploadClient>(sp => sp.GetRequiredService<CapturingLegacyDocumentUploadClient>());

            services.RemoveAll<ISellingDocumentReferenceValidator>();
            services.AddSingleton<CapturingSellingDocumentReferenceValidator>();
            services.AddSingleton<ISellingDocumentReferenceValidator>(sp => sp.GetRequiredService<CapturingSellingDocumentReferenceValidator>());

            services.RemoveAll<IPublicBuyerAccountProvisioningService>();
            services.AddSingleton<CapturingPublicBuyerAccountProvisioningService>();
            services.AddSingleton<IPublicBuyerAccountProvisioningService>(
                sp => sp.GetRequiredService<CapturingPublicBuyerAccountProvisioningService>());

            services.AddHttpClient("MedicareProcedureLookup")
                .ConfigurePrimaryHttpMessageHandler(() => new StubMedicareProcedureLookupHandler());
            services.AddHttpClient("Identity")
                .ConfigurePrimaryHttpMessageHandler(() => new StubIdentityHandler());
            services.AddHttpClient("IdentityService")
                .ConfigurePrimaryHttpMessageHandler(() => new StubIdentityServiceHandler());
            services.AddHttpClient("DocumentsService")
                .ConfigurePrimaryHttpMessageHandler(() => new StubDocumentsServiceHandler());
        });
    }
}

internal sealed class StubMedicareProcedureLookupHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.TryGetValues("apiKey", out var apiKeyValues).Should().BeTrue();
        apiKeyValues!.Should().Contain("1iuNYl3IYBHTSjmn34m0XOLLqfm1nrmz");

        request.Headers.TryGetValues("amaLicense", out var licenseValues).Should().BeTrue();
        licenseValues!.Should().Contain("b733fd32-ee85-4174-9ab1-e09ec14048bb");

        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        var response = true switch
        {
            _ when path.EndsWith("/codes", StringComparison.OrdinalIgnoreCase) => JsonResponse("""
                [
                  { "code": "45385", "description": "Colonoscopy, flexible; with removal by snare technique (45385)", "frequency": 1075901 }
                ]
                """),
            _ when path.EndsWith("/costs/45385", StringComparison.OrdinalIgnoreCase) => JsonResponse("""
                [
                  { "code": "45385", "facilityType": "hospital", "cost": 1156, "copay": 288, "facilityTotal": 1222, "physicianTotal": 223, "total": 1445 },
                  { "code": "45385", "facilityType": "asc", "cost": 703, "copay": 175, "facilityTotal": 656, "physicianTotal": 223, "total": 879 }
                ]
                """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };

        return Task.FromResult(response);
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
}

internal sealed class StubDocumentsServiceHandler : HttpMessageHandler
{
    private const string ViewToken = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string DownloadToken = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    public static readonly byte[] DownloadContent = "%PDF-1.4 stub payoff"u8.ToArray();
    private static readonly ConcurrentDictionary<Guid, int> MetadataRequestCounts = new();

    public static int GetMetadataRequestCount(Guid documentId) =>
        MetadataRequestCounts.GetValueOrDefault(documentId);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        var response = true switch
        {
            _ when path.EndsWith("/view-url", StringComparison.OrdinalIgnoreCase) => JsonResponse($$"""
                {
                  "data": {
                    "accessToken": "{{ViewToken}}",
                    "redeemUrl": "/access/{{ViewToken}}",
                    "expiresInSeconds": 300,
                    "type": "view"
                  }
                }
                """),
            _ when path.EndsWith("/download-url", StringComparison.OrdinalIgnoreCase) => JsonResponse($$"""
                {
                  "data": {
                    "accessToken": "{{DownloadToken}}",
                    "redeemUrl": "/access/{{DownloadToken}}",
                    "expiresInSeconds": 300,
                    "type": "download"
                  }
                }
                """),
            _ when path.Contains("/content", StringComparison.OrdinalIgnoreCase) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(DownloadContent)
            },
            _ when request.Method == HttpMethod.Get && IsDocumentMetadataPath(path) =>
                DocumentMetadataResponse(path),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };

        return Task.FromResult(response);
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

    private static bool IsDocumentMetadataPath(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 2 &&
               string.Equals(segments[0], "documents", StringComparison.OrdinalIgnoreCase) &&
               Guid.TryParse(segments[1], out _);
    }

    private static HttpResponseMessage DocumentMetadataResponse(string path)
    {
        var documentId = Guid.Parse(path.Split('/', StringSplitOptions.RemoveEmptyEntries)[1]);
        var requestCount = MetadataRequestCounts.AddOrUpdate(documentId, 1, (_, count) => count + 1);
        var scanStatus = requestCount == 1 ? "PENDING" : "CLEAN";

        return JsonResponse($$"""
            {
              "data": {
                "id": "{{documentId}}",
                "scanStatus": "{{scanStatus}}"
              }
            }
            """);
    }
}

internal sealed class StubIdentityHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Authorization.Should().NotBeNull();
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;

        if (string.Equals(
                path,
                $"/api/users/{SeedHelper.IdentityOnlyUserId:D}",
                StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"id":"{{SeedHelper.IdentityOnlyUserId}}","firstName":"Identity","lastName":"Only","email":"identity.only@legalsynq.test"}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            });
        }

        if (!string.Equals(path, "/api/users", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""[{"id":"{{SeedHelper.UserId}}","firstName":"Demo","lastName":"User","email":"demo@example.com"}]""",
                System.Text.Encoding.UTF8,
                "application/json"),
        });
    }
}

internal sealed class StubIdentityServiceHandler : HttpMessageHandler
{
    public const string ExpectedProvisioningToken = "liens-test-provisioning-token";

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        var expectedOrganizationPath = $"/api/admin/organizations/{SeedHelper.OrgId:D}";
        var expectedTenantOwnerDisplayPath = "/api/internal/users/tenant-owner/display";
        var expectedUserDisplayPath = $"/api/internal/users/{SeedHelper.UserId:D}/display";
        var expectedIdentityOnlyUserDisplayPath =
            $"/api/internal/users/{SeedHelper.IdentityOnlyUserId:D}/display";
        if (string.Equals(path, expectedTenantOwnerDisplayPath, StringComparison.OrdinalIgnoreCase))
        {
            AssertProvisioningToken(request);
            request.RequestUri?.Query.Should().Contain($"organizationId={SeedHelper.OrgId:D}");
            request.RequestUri?.Query.Should().Contain($"tenantId={SeedHelper.TenantId:D}");
            return Task.FromResult(JsonResponse($$"""
                {
                  "found": true,
                  "tenantId": "{{SeedHelper.TenantId:D}}",
                  "organizationId": "{{SeedHelper.OrgId:D}}",
                  "userId": "{{SeedHelper.UserId:D}}",
                  "email": "tenant.owner@rl-liens.test",
                  "firstName": "Tenant",
                  "lastName": "Owner",
                  "displayName": "Tenant Owner",
                  "organizationName": "RL Liens1",
                  "organizationDisplayName": "RL Liens1"
                }
                """));
        }

        if (string.Equals(path, expectedUserDisplayPath, StringComparison.OrdinalIgnoreCase))
        {
            AssertProvisioningToken(request);
            request.RequestUri?.Query.Should().Contain($"tenantId={SeedHelper.TenantId:D}");
            request.RequestUri?.Query.Should().Contain($"organizationId={SeedHelper.OrgId:D}");
            return Task.FromResult(JsonResponse($$"""
                {
                  "found": true,
                  "userId": "{{SeedHelper.UserId:D}}",
                  "tenantId": "{{SeedHelper.TenantId:D}}",
                  "email": "seller.processor@rl-liens.test",
                  "firstName": "Seller",
                  "lastName": "Processor",
                  "displayName": "Seller Processor"
                }
                """));
        }

        if (string.Equals(path, expectedIdentityOnlyUserDisplayPath, StringComparison.OrdinalIgnoreCase))
        {
            AssertProvisioningToken(request);
            request.RequestUri?.Query.Should().Contain($"tenantId={SeedHelper.TenantId:D}");
            request.RequestUri?.Query.Should().Contain($"organizationId={SeedHelper.OrgId:D}");
            return Task.FromResult(JsonResponse($$"""
                {
                  "found": true,
                  "userId": "{{SeedHelper.IdentityOnlyUserId:D}}",
                  "tenantId": "{{SeedHelper.TenantId:D}}",
                  "email": "identity.only@legalsynq.test",
                  "firstName": "Identity",
                  "lastName": "Only",
                  "displayName": "Identity Only"
                }
                """));
        }

        if (!string.Equals(path, expectedOrganizationPath, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        AssertProvisioningToken(request);
        return Task.FromResult(JsonResponse($$"""
            {
              "id": "{{SeedHelper.OrgId:D}}",
              "tenantId": "{{SeedHelper.TenantId:D}}",
              "name": "RL Liens1",
              "orgType": "Seller",
              "isActive": true
            }
            """));
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

    private static void AssertProvisioningToken(HttpRequestMessage request)
    {
        request.Headers.TryGetValues("X-Provisioning-Token", out var values).Should().BeTrue();
        values.Should().Contain(ExpectedProvisioningToken);
    }
}

/// <summary>No-op stub — returns (null, null) for every case lookup.</summary>
internal sealed class NoOpFlowInstanceResolver : IFlowInstanceResolver
{
    public Task<(Guid? WorkflowInstanceId, string? WorkflowStepKey)> ResolveAsync(
        Guid caseId, CancellationToken ct = default)
        => Task.FromResult<(Guid?, string?)>((null, null));
}

internal sealed class CapturingNotificationPublisher : INotificationPublisher
{
    private readonly List<CapturedEmail> _emails = [];
    private readonly Dictionary<string, Guid> _idempotentEmails = new(StringComparer.Ordinal);

    public IReadOnlyList<CapturedEmail> Emails => _emails;

    public void Clear()
    {
        _emails.Clear();
        _idempotentEmails.Clear();
        FailEmailSends = false;
    }

    public bool FailEmailSends { get; set; }

    public Task PublishAsync(
        string notificationType,
        Guid tenantId,
        Dictionary<string, string> data,
        CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<NotificationInboxSendResult> SubmitInboxAsync(
        NotificationInboxSendRequest request,
        CancellationToken ct = default)
        => Task.FromResult(new NotificationInboxSendResult(
            true,
            false,
            Guid.CreateVersion7(),
            null));

    public Task<NotificationEmailSendResult> SendEmailAsync(
        string notificationType,
        Guid tenantId,
        string recipientEmail,
        string subject,
        string body,
        Dictionary<string, string> metadata,
        CancellationToken ct = default,
        NotificationEmailSendOptions? options = null)
    {
        if (FailEmailSends)
        {
            return Task.FromResult(new NotificationEmailSendResult(
                null,
                "failed",
                false,
                null,
                "transient",
                "Simulated notification failure."));
        }

        var idempotencyKey = options?.IdempotencyKey;
        if (!string.IsNullOrWhiteSpace(idempotencyKey) &&
            _idempotentEmails.TryGetValue($"{tenantId:N}:{idempotencyKey}", out var existingNotificationId))
        {
            return Task.FromResult(new NotificationEmailSendResult(
                existingNotificationId,
                "sent",
                false,
                null,
                null,
                null));
        }

        var notificationId = Guid.CreateVersion7();
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            _idempotentEmails[$"{tenantId:N}:{idempotencyKey}"] = notificationId;

        _emails.Add(new CapturedEmail(
            notificationType,
            tenantId,
            recipientEmail,
            subject,
            body,
            metadata,
            options,
            notificationId));

        return Task.FromResult(new NotificationEmailSendResult(
            notificationId,
            "sent",
            false,
            null,
            null,
            null));
    }
}

internal sealed class CapturingSellingDocumentReferenceValidator : ISellingDocumentReferenceValidator
{
    public HashSet<Guid> DeniedDocumentIds { get; } = [];

    public Task<bool> IsAccessibleAsync(
        Guid tenantId,
        Guid sellerOrgId,
        Guid actingUserId,
        Guid lienId,
        Guid? caseId,
        Guid documentId,
        CancellationToken ct = default)
        => Task.FromResult(!DeniedDocumentIds.Contains(documentId));
}

internal sealed record CapturedEmail(
    string NotificationType,
    Guid TenantId,
    string RecipientEmail,
    string Subject,
    string Body,
    IReadOnlyDictionary<string, string> Metadata,
    NotificationEmailSendOptions? Options,
    Guid NotificationId);

internal sealed class CapturingPublicBuyerAccountProvisioningService : IPublicBuyerAccountProvisioningService
{
    private readonly List<PublicBuyerAccountProvisioningRequest> _requests = [];
    private readonly List<PublicBuyerAccountStatusRequest> _statusRequests = [];

    public IReadOnlyList<PublicBuyerAccountProvisioningRequest> Requests => _requests;
    public IReadOnlyList<PublicBuyerAccountStatusRequest> StatusRequests => _statusRequests;
    public PublicBuyerAccountStatusResult? NextStatusResult { get; set; }
    public PublicBuyerAccountProvisioningResult? NextResult { get; set; }

    public void Clear()
    {
        _requests.Clear();
        _statusRequests.Clear();
        NextStatusResult = null;
        NextResult = null;
    }

    public Task<PublicBuyerAccountStatusResult> GetBuyerAccountStatusAsync(
        PublicBuyerAccountStatusRequest request,
        CancellationToken ct = default)
    {
        _statusRequests.Add(request);
        return Task.FromResult(
            NextStatusResult
            ?? PublicBuyerAccountStatusResult.Found(accountExists: false));
    }

    public Task<PublicBuyerAccountProvisioningResult> ProvisionBuyerAccountAsync(
        PublicBuyerAccountProvisioningRequest request,
        CancellationToken ct = default)
    {
        _requests.Add(request);
        return Task.FromResult(
            NextResult
            ?? PublicBuyerAccountProvisioningResult.Created(Guid.CreateVersion7(), isNew: true));
    }
}

internal sealed class CapturingAuditPublisher : IAuditPublisher
{
    private readonly List<CapturedAuditEvent> _events = [];
    private readonly AsyncLocal<List<CapturedAuditEvent>?> _bufferedEvents = new();

    public IReadOnlyList<CapturedAuditEvent> Events => _events;

    public void Clear() => _events.Clear();

    public IAuditPublicationBuffer BeginBuffer()
    {
        if (_bufferedEvents.Value is not null)
            throw new InvalidOperationException("An audit publication buffer is already active.");

        _bufferedEvents.Value = [];
        return new CapturingAuditPublicationBuffer(this);
    }

    public void Publish(
        string eventType,
        string action,
        string description,
        Guid tenantId,
        Guid? actorUserId = null,
        string? entityType = null,
        string? entityId = null,
        string? before = null,
        string? after = null,
        string? metadata = null)
    {
        var capturedEvent = new CapturedAuditEvent(
            eventType,
            action,
            description,
            tenantId,
            actorUserId,
            entityType,
            entityId,
            before,
            after,
            metadata,
            DateTimeOffset.UtcNow);

        if (_bufferedEvents.Value is { } bufferedEvents)
            bufferedEvents.Add(capturedEvent);
        else
            _events.Add(capturedEvent);
    }

    private void CommitBuffer()
    {
        var bufferedEvents = _bufferedEvents.Value
            ?? throw new InvalidOperationException("No audit publication buffer is active.");
        _bufferedEvents.Value = null;
        _events.AddRange(bufferedEvents);
    }

    private void DiscardBuffer() => _bufferedEvents.Value = null;

    private sealed class CapturingAuditPublicationBuffer : IAuditPublicationBuffer
    {
        private CapturingAuditPublisher? _owner;

        public CapturingAuditPublicationBuffer(CapturingAuditPublisher owner)
        {
            _owner = owner;
        }

        public void Commit()
        {
            var owner = Interlocked.Exchange(ref _owner, null)
                ?? throw new InvalidOperationException("The audit publication buffer is already complete.");
            owner.CommitBuffer();
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.DiscardBuffer();
        }
    }
}

internal sealed record CapturedAuditEvent(
    string EventType,
    string Action,
    string Description,
    Guid TenantId,
    Guid? ActorUserId,
    string? EntityType,
    string? EntityId,
    string? Before,
    string? After,
    string? Metadata,
    DateTimeOffset OccurredAtUtc);

internal sealed class CapturingLegacyDocumentUploadClient : ILegacyDocumentUploadClient
{
    private readonly List<CapturedLegacyDocumentUpload> _uploads = [];

    public IReadOnlyList<CapturedLegacyDocumentUpload> Uploads => _uploads;

    public void Clear() => _uploads.Clear();

    public Task<LegacyDocumentUploadResult> UploadAsync(
        LegacyDocumentUploadRequest request,
        CancellationToken ct = default)
    {
        var documentId = Guid.CreateVersion7();
        using var content = new MemoryStream();
        request.Content.CopyTo(content);

        _uploads.Add(new CapturedLegacyDocumentUpload(
            request.TenantId,
            request.ActingUserId,
            request.ReferenceId,
            request.ReferenceType,
            request.DocumentTypeId,
            request.Title,
            request.FileName,
            request.ContentType,
            request.Length,
            content.ToArray(),
            documentId));

        return Task.FromResult(new LegacyDocumentUploadResult
        {
            DocumentId = documentId,
            Url = $"/documents/{documentId}",
        });
    }
}

internal sealed record CapturedLegacyDocumentUpload(
    Guid TenantId,
    Guid ActingUserId,
    Guid ReferenceId,
    string ReferenceType,
    Guid DocumentTypeId,
    string Title,
    string FileName,
    string ContentType,
    long Length,
    byte[] Content,
    Guid DocumentId);
