using BuildingBlocks.Authorization;
using BuildingBlocks.Notifications;
using CareConnect.Application.Interfaces;
using CareConnect.Application.Repositories;
using CareConnect.Application.Services;
using CareConnect.Infrastructure.Data;
using CareConnect.Infrastructure.Documents;
using CareConnect.Infrastructure.Imports;
using CareConnect.Infrastructure.Notifications;
using CareConnect.Infrastructure.Repositories;
using CareConnect.Infrastructure.Services;
using CareConnect.Infrastructure.Workers;
using LegalSynq.AuditClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CareConnect.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// CC2-INT-B03: Validates CareConnect required configuration at startup (before any traffic is served).
    /// Throws <see cref="InvalidOperationException"/> for any missing/invalid required settings
    /// in non-Development environments.
    /// </summary>
    public static void ValidateRequiredConfiguration(IConfiguration configuration)
    {
        var environment = configuration["ASPNETCORE_ENVIRONMENT"] ?? "Production";
        var isDev       = string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase);

        var tokenSecret = configuration["ReferralToken:Secret"];
        if (string.IsNullOrWhiteSpace(tokenSecret) && !isDev)
            throw new InvalidOperationException(
                "ReferralToken:Secret must be configured in non-Development environments. " +
                "Set the 'ReferralToken:Secret' configuration key to a strong random value. " +
                $"Current environment: '{environment}'.");

        // CC2-INT-B03: Documents service requires a valid documentTypeId UUID for every upload.
        // An empty value causes runtime 400s from the Documents API. Fail fast here.
        var docTypeId = configuration["DocumentsService:DocumentTypeId"];
        if (string.IsNullOrWhiteSpace(docTypeId) && !isDev)
            throw new InvalidOperationException(
                "DocumentsService:DocumentTypeId must be configured in non-Development environments. " +
                "Set the 'DocumentsService:DocumentTypeId' configuration key to a valid UUID. " +
                $"Current environment: '{environment}'.");

        // Referral Attribution access codes are hashed with SHA-256 + this server-side pepper
        // before storage (never persisted in plaintext). Without a strong pepper in
        // non-Development environments, the hash would be a bare SHA-256 of the code alone.
        var accessCodePepper = configuration["ReferralAttributionAccessCode:Pepper"];
        if (string.IsNullOrWhiteSpace(accessCodePepper) && !isDev)
            throw new InvalidOperationException(
                "ReferralAttributionAccessCode:Pepper must be configured in non-Development environments. " +
                "Set the 'ReferralAttributionAccessCode:Pepper' configuration key to a strong random value. " +
                $"Current environment: '{environment}'.");
    }

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // CC2-INT-B03: Fail fast at startup if required configuration is missing.
        // This runs during service registration (before app.Run()), ensuring the app never
        // starts serving traffic without a valid HMAC signing secret.
        ValidateRequiredConfiguration(configuration);

        var connectionString = configuration.GetConnectionString("CareConnectDb")
            ?? throw new InvalidOperationException("Connection string 'CareConnectDb' is not configured.");

        // Phase 1 (step 3): Identity service options + named HTTP client for relationship resolution
        services.Configure<IdentityServiceOptions>(
            configuration.GetSection(IdentityServiceOptions.SectionName));
        services.AddHttpClient("IdentityService");

        // BLK-CC-01: Tenant service options + named HTTP client for tenant lifecycle calls
        services.Configure<TenantServiceOptions>(
            configuration.GetSection(TenantServiceOptions.SectionName));
        services.AddHttpClient("TenantService");

        services.AddAuditEventClient(configuration);

        // BLK-PERF-02: In-memory cache for public network surfaces, reference data,
        // and short-lived admin dashboard metrics. Tenant isolation is enforced via
        // cache key construction (CareConnectCacheKeys). Size limit prevents unbounded growth.
        services.AddMemoryCache(options =>
        {
            options.SizeLimit = 4096; // max cache entries before LRU eviction
        });

        services.AddDbContext<CareConnectDbContext>(options =>
            options.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 0))));
        services.AddDbContextFactory<CareConnectDbContext>(options =>
            options.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 0))),
            ServiceLifetime.Scoped);

        services.AddScoped<IProviderRepository, ProviderRepository>();
        services.AddScoped<IReferralRepository, ReferralRepository>();
        services.AddScoped<IReferralCommentRepository, ReferralCommentRepository>();
        services.AddScoped<ICategoryRepository, CategoryRepository>();
        services.AddScoped<ISpecialtyRepository, SpecialtyRepository>();
        services.AddScoped<IFacilityRepository, FacilityRepository>();
        services.AddScoped<IServiceOfferingRepository, ServiceOfferingRepository>();
        services.AddScoped<IAvailabilityTemplateRepository, AvailabilityTemplateRepository>();
        services.AddScoped<IAppointmentSlotRepository, AppointmentSlotRepository>();
        services.AddScoped<IAppointmentRepository, AppointmentRepository>();
        services.AddScoped<IAppointmentStatusHistoryRepository, AppointmentStatusHistoryRepository>();
        services.AddScoped<IAvailabilityExceptionRepository, AvailabilityExceptionRepository>();
        services.AddScoped<IReferralNoteRepository, ReferralNoteRepository>();
        services.AddScoped<IAppointmentNoteRepository, AppointmentNoteRepository>();
        services.AddScoped<IReferralAttachmentRepository, ReferralAttachmentRepository>();
        services.AddScoped<IAppointmentAttachmentRepository, AppointmentAttachmentRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        // LSCC-009: Provider activation queue
        services.AddScoped<IActivationRequestRepository, ActivationRequestRepository>();
        // Referral Attribution + Referral Representative access
        services.AddScoped<IReferralAttributionRepository, ReferralAttributionRepository>();
        services.AddScoped<IReferralAttributionAccessCodeRepository, ReferralAttributionAccessCodeRepository>();
        services.AddScoped<IPendingReferralRequestRepository, PendingReferralRequestRepository>();
        services.AddScoped<IPendingReferralAttachmentRepository, PendingReferralAttachmentRepository>();

        services.AddScoped<IProviderService, ProviderService>();
        services.AddScoped<IReferralService, ReferralService>();
        services.AddScoped<IReferralThreadService, ReferralThreadService>();
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<ISpecialtyService, SpecialtyService>();
        services.AddScoped<IFacilityService, FacilityService>();
        services.AddScoped<IServiceOfferingService, ServiceOfferingService>();
        services.AddScoped<IAvailabilityTemplateService, AvailabilityTemplateService>();
        services.AddScoped<ISlotGenerationService, SlotGenerationService>();
        services.AddScoped<IAppointmentService, AppointmentService>();
        services.AddScoped<IAvailabilityExceptionService, AvailabilityExceptionService>();
        services.AddScoped<IReferralNoteService, ReferralNoteService>();
        services.AddScoped<IAppointmentNoteService, AppointmentNoteService>();
        services.AddScoped<IReferralAttachmentService, ReferralAttachmentService>();
        services.AddScoped<IAppointmentAttachmentService, AppointmentAttachmentService>();
        services.AddScoped<INotificationService, NotificationService>();
        // LSCC-009: Provider activation queue service
        services.AddScoped<IActivationRequestService, ActivationRequestService>();
        // Referral Attribution + Referral Representative access
        services.AddScoped<IReferralAttributionService, ReferralAttributionService>();
        services.AddScoped<IReferralAttributionAccessCodeService, ReferralAttributionAccessCodeService>();
        services.AddScoped<IRepresentativeReferralService, RepresentativeReferralService>();
        services.AddScoped<IPendingReferralRequestService, PendingReferralRequestService>();

        // LSCC-010: Auto-provisioning — Identity org HTTP client + orchestration service
        services.AddScoped<IIdentityOrganizationService, HttpIdentityOrganizationService>();
        services.AddScoped<IAutoProvisionService, AutoProvisionService>();

        // CC2-ENROLL: Self-enrollment OTP store (singleton — intentionally in-memory)
        services.AddSingleton<CareConnect.Application.Services.EnrollmentOtpStore>();

        // BLK-CC-01: Tenant service client (check-code, provision) — replaces retired Identity endpoints
        services.AddScoped<ITenantServiceClient, HttpTenantServiceClient>();

        // BLK-CC-01: Identity membership client (assign-tenant) — uses BLK-ID-02 APIs
        services.AddScoped<IIdentityMembershipClient, HttpIdentityMembershipClient>();

        // CC2-INT-B09 / BLK-CC-01: Provider tenant self-onboarding (now uses Tenant + Identity membership)
        services.AddScoped<IProviderOnboardingService, ProviderOnboardingService>();

        // LSCC-011: Activation funnel analytics
        services.AddScoped<IActivationFunnelAnalyticsService, ActivationFunnelAnalyticsService>();

        // LSCC-01-005: Referral performance metrics
        services.AddScoped<IReferralPerformanceService, ReferralPerformanceService>();

        // CC2-INT-B03: Documents service HTTP client + client implementation.
        // CareConnect proxies file uploads to Documents service; only documentId is stored locally.
        var docsBaseUrl = configuration["DocumentsService:BaseUrl"] ?? "http://localhost:5006";
        services.AddHttpClient("DocumentsService", client =>
        {
            client.BaseAddress = new Uri(docsBaseUrl);
            client.Timeout     = TimeSpan.FromSeconds(30);
        });
        services.AddScoped<IDocumentServiceClient, DocumentServiceClient>();

        // LS-NOTIF-CORE-023: Canonical notification producer — routes outbound emails
        // through POST /v1/notifications on the platform Notifications service.
        // NotificationsAuthDelegatingHandler mints a short-lived service JWT (svc claim)
        // so the Notifications service ServiceSubmission policy accepts the request.
        services.AddTransient<NotificationsAuthDelegatingHandler>();
        services.AddHttpClient("NotificationsService")
            .AddHttpMessageHandler<NotificationsAuthDelegatingHandler>();
        services.AddScoped<INotificationsProducer, NotificationsProducerClient>();
        // Singleton: subdomain slugs never change after provisioning; cache must outlive Scoped email service.
        services.AddSingleton<ITenantSubdomainCache, TenantSubdomainCache>();
        services.AddScoped<IReferralEmailService, ReferralEmailService>();

        // LSCC-005-02: Automatic email retry background worker
        services.AddHostedService<ReferralEmailRetryWorker>();

        // Phase 1 (step 3): HTTP resolver is now the default.
        // It is fail-safe: returns null on any network error, timeout, or 4xx/5xx.
        // When IdentityService:BaseUrl is not set it skips the HTTP call automatically.
        // OrganizationRelationshipNullResolver is retained as an alternative DI
        // registration for integration tests that do not need real Identity calls.
        services.AddScoped<IOrganizationRelationshipResolver, HttpOrganizationRelationshipResolver>();

        services.AddSingleton<IPermissionService, CareConnectPermissionService>();
        services.AddScoped<AuthorizationService>();

        // LSCC-01-002-02: Centralized, read-only provider access-readiness verification.
        // Singleton — depends only on IPermissionService (also singleton); no request-scoped deps.
        services.AddSingleton<IProviderAccessReadinessService, ProviderAccessReadinessService>();

        // LSCC-01-004: Blocked-access logging — best-effort, never blocks the user flow.
        services.AddScoped<IBlockedAccessLogRepository, BlockedAccessLogRepository>();
        services.AddScoped<IBlockedAccessLogService, BlockedAccessLogService>();

        // CC2-INT-B06: Provider network management (role-based, not orgType-based)
        services.AddScoped<INetworkRepository, NetworkRepository>();
        services.AddScoped<IProviderImportParser, CsvProviderImportParser>();
        services.AddScoped<INetworkService, NetworkService>();

        // LSV3-1083: Law Firm Company Super Admin/Manager
        services.AddScoped<ILawFirmUserService, LawFirmUserService>();

        return services;
    }
}
