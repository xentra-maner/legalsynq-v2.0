using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Notifications.Application.Interfaces;
using Notifications.Application.Options;
using Notifications.Infrastructure.Data;
using Notifications.Infrastructure.Providers.Adapters;
using Notifications.Infrastructure.Repositories;
using Notifications.Infrastructure.Services;
using Notifications.Infrastructure.Webhooks.Verifiers;
using Notifications.Infrastructure.Workers;
using LegalSynq.AuditClient;

namespace Notifications.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var host = configuration["NOTIF_DB_HOST"] ?? "localhost";
        var port = configuration["NOTIF_DB_PORT"] ?? "3306";
        var database = configuration["NOTIF_DB_NAME"] ?? "notifications_db";
        var user = configuration["NOTIF_DB_USER"] ?? "root";
        var password = configuration["NOTIF_DB_PASSWORD"] ?? "";

        var connectionString = configuration.GetConnectionString("NotificationsDb")
            ?? $"Server={host};Port={port};Database={database};User={user};Password={password};";

        services.AddDbContext<NotificationsDbContext>(options =>
            options.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 0)),
                mysql => mysql.EnableRetryOnFailure(3)));

        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IUserInboxService, UserInboxService>();
        services.AddScoped<INotificationAttemptRepository, NotificationAttemptRepository>();
        services.AddScoped<ITemplateRepository, TemplateRepository>();
        services.AddScoped<ITemplateVersionRepository, TemplateVersionRepository>();
        services.AddScoped<ITenantProviderConfigRepository, TenantProviderConfigRepository>();
        services.AddScoped<ITenantChannelProviderSettingRepository, TenantChannelProviderSettingRepository>();
        services.AddScoped<IProviderHealthRepository, ProviderHealthRepository>();
        services.AddScoped<IWebhookLogRepository, WebhookLogRepository>();
        services.AddScoped<INotificationEventRepository, NotificationEventRepository>();
        services.AddScoped<IContactSuppressionRepository, ContactSuppressionRepository>();
        services.AddScoped<IRecipientContactHealthRepository, RecipientContactHealthRepository>();
        services.AddScoped<IDeliveryIssueRepository, DeliveryIssueRepository>();
        services.AddScoped<ITenantBillingPlanRepository, TenantBillingPlanRepository>();
        services.AddScoped<ITenantBillingRateRepository, TenantBillingRateRepository>();
        services.AddScoped<ITenantRateLimitPolicyRepository, TenantRateLimitPolicyRepository>();
        services.AddScoped<ITenantContactPolicyRepository, TenantContactPolicyRepository>();
        services.AddScoped<ITenantBrandingRepository, TenantBrandingRepository>();
        services.AddScoped<IUsageMeterEventRepository, UsageMeterEventRepository>();
        services.AddScoped<ISmsPreferenceRepository, SmsPreferenceRepository>();
        services.AddScoped<ISmsPreferenceHistoryRepository, SmsPreferenceHistoryRepository>();

        services.AddScoped<INotificationService, NotificationServiceImpl>();
        services.AddScoped<ITemplateService, TemplateServiceImpl>();
        services.AddScoped<ITenantProviderConfigService, TenantProviderConfigServiceImpl>();
        services.AddScoped<ITemplateRenderingService, TemplateRenderingService>();
        services.AddScoped<ITemplateResolutionService, TemplateResolutionService>();
        services.AddScoped<IBrandingResolutionService, BrandingResolutionService>();
        services.AddScoped<IDeliveryStatusService, DeliveryStatusService>();
        services.AddScoped<IDeliveryIssueService, DeliveryIssueServiceImpl>();
        services.AddScoped<IContactEnforcementService, ContactEnforcementService>();
        services.AddScoped<IUsageEvaluationService, UsageEvaluationService>();
        services.AddScoped<IUsageMeteringService, UsageMeteringService>();
        services.AddScoped<IRecipientContactHealthService, RecipientContactHealthService>();
        services.AddScoped<IProviderRoutingService, ProviderRoutingService>();
        services.AddScoped<ISmsPreferenceService, SmsPreferenceServiceImpl>();
        services.AddScoped<IInboundSmsResolverService, InboundSmsResolverService>();
        services.AddScoped<ISmsReconciliationService, SmsReconciliationService>();
        services.AddScoped<ITwilioAdapterFactory, TwilioAdapterFactory>();
        services.AddScoped<ISmsProviderRuntimeResolver, SmsProviderRuntimeResolver>();
        services.AddScoped<ISmsActivityRepository, SmsActivityRepository>();
        services.AddScoped<ISmsActivityService, SmsActivityService>();
        services.AddScoped<ISmsDashboardRepository, SmsDashboardRepository>();
        services.AddScoped<ISmsDashboardService, SmsDashboardService>();
        services.AddScoped<ISmsCostAnalyticsRepository, SmsCostAnalyticsRepository>();
        services.AddScoped<ISmsCostAnalyticsService, SmsCostAnalyticsService>();
        services.AddOptions<SmsCostAnalyticsOptions>()
                .Bind(configuration.GetSection(SmsCostAnalyticsOptions.SectionName));
        services.AddScoped<ISmsOperationalAlertRepository, SmsOperationalAlertRepository>();
        services.AddScoped<ISmsOperationalAlertEvaluator, SmsOperationalAlertEvaluator>();
        services.AddScoped<ISmsOperationalEscalationPolicyRepository, SmsEscalationPolicyRepository>();
        services.AddScoped<ISmsOperationalAlertEscalationRepository, SmsAlertEscalationRepository>();
        services.AddScoped<ISmsAlertEscalationMessageBuilder, SmsAlertEscalationMessageBuilder>();
        services.AddScoped<ISmsAlertEscalationChannelAdapter, InternalEmailEscalationAdapter>();
        services.AddScoped<ISmsAlertEscalationChannelAdapter, TeamsWebhookEscalationAdapter>();
        services.AddScoped<ISmsAlertEscalationChannelAdapter, SlackWebhookEscalationAdapter>();
        services.AddScoped<ISmsAlertEscalationService, SmsAlertEscalationService>();

        // LS-NOTIF-SMS-014: Multi-Provider SMS Routing
        services.AddScoped<ISmsRoutingPolicyRepository, SmsRoutingPolicyRepository>();
        services.AddScoped<ISmsRoutingDecisionRepository, SmsRoutingDecisionRepository>();
        services.AddSingleton<ISmsProviderCapabilityService, SmsProviderCapabilityService>();
        // ISmsProviderAdapterFactory registrations — injected as IEnumerable<ISmsProviderAdapterFactory>
        // into SmsProviderAdapterRegistry. Order matters: first matching factory wins.
        services.AddScoped<ISmsProviderAdapterFactory, TwilioAdapterFactoryWrapper>();
        services.AddScoped<ISmsProviderAdapterFactory, VonageAdapterFactory>();
        services.AddScoped<ISmsProviderAdapterRegistry, SmsProviderAdapterRegistry>();
        services.AddScoped<ISmsRoutingEngine, SmsRoutingEngine>();
        services.AddHttpClient("Vonage");

        // LS-NOTIF-SMS-015: Regional Intelligence, Provider Quality, Adaptive Routing
        services.AddSingleton<ISmsRegionalInferenceService, SmsRegionalInferenceService>();
        services.AddScoped<ISmsProviderQualityRepository, SmsProviderQualityRepository>();
        services.AddScoped<ISmsProviderQualityService, SmsProviderQualityService>();
        services.AddOptions<SmsProviderQualityOptions>()
                .Bind(configuration.GetSection(SmsProviderQualityOptions.SectionName));
        services.AddHostedService<SmsProviderQualityWorker>();

        // LS-NOTIF-SMS-016: Recipient Intelligence, Suppression, Delivery Reputation
        services.AddOptions<SmsRecipientIntelligenceOptions>()
                .Bind(configuration.GetSection(SmsRecipientIntelligenceOptions.SectionName));
        services.AddSingleton<ISmsRecipientIdentityHasher, SmsRecipientIdentityHasher>();
        services.AddScoped<ISmsRecipientIntelligenceService, SmsRecipientIntelligenceService>();
        services.AddScoped<ISmsRetrySuppressionService, SmsRetrySuppressionService>();
        services.AddHostedService<SmsRecipientIntelligenceWorker>();

        // LS-NOTIF-SMS-017: SMS Governance Policies + Compliance Controls
        services.AddOptions<SmsGovernanceOptions>()
                .Bind(configuration.GetSection(SmsGovernanceOptions.SectionName));
        services.AddScoped<ISmsGovernancePolicyService, SmsGovernancePolicyService>();

        // LS-NOTIF-SMS-018: SMS Template Governance, Content Classification, Delivery Compliance
        services.AddOptions<SmsTemplateGovernanceOptions>()
                .Bind(configuration.GetSection(SmsTemplateGovernanceOptions.SectionName));
        services.AddScoped<ISmsTemplateGovernanceService, SmsTemplateGovernanceService>();

        // LS-NOTIF-SMS-019: Dynamic Governance Rule Packs, Rule Engine, Compliance Profiles, Simulation
        services.AddOptions<SmsGovernanceDynamicOptions>()
                .Bind(configuration.GetSection(SmsGovernanceDynamicOptions.SectionName));
        services.AddScoped<ISmsGovernanceRuleResolver, SmsGovernanceRuleResolver>();
        services.AddScoped<ISmsGovernanceRuleEngine, SmsGovernanceRuleEngine>();
        services.AddScoped<ISmsGovernanceSimulationService, SmsGovernanceSimulationService>();

        // LS-NOTIF-SMS-020: Governance Versioning, Bulk Import, Analytics
        services.AddOptions<SmsGovernanceVersioningOptions>()
                .Bind(configuration.GetSection(SmsGovernanceVersioningOptions.SectionName));
        services.AddOptions<SmsGovernanceAnalyticsOptions>()
                .Bind(configuration.GetSection(SmsGovernanceAnalyticsOptions.SectionName));
        services.AddScoped<ISmsGovernanceVersioningService, SmsGovernanceVersioningService>();
        services.AddScoped<ISmsGovernanceImportService, SmsGovernanceImportService>();
        // Analytics service implements both ISmsGovernanceAnalyticsService and ISmsGovernanceMatchRecorder;
        // register as scoped and resolve both interfaces from the same instance per request.
        services.AddScoped<SmsGovernanceAnalyticsService>();
        services.AddScoped<ISmsGovernanceAnalyticsService>(sp =>
            sp.GetRequiredService<SmsGovernanceAnalyticsService>());
        services.AddScoped<ISmsGovernanceMatchRecorder>(sp =>
            sp.GetRequiredService<SmsGovernanceAnalyticsService>());

        // LS-NOTIF-SMS-021: Governance Release Management, Approval Workflow, Scheduled Activation
        services.AddOptions<SmsGovernanceReleaseManagementOptions>()
                .Bind(configuration.GetSection(SmsGovernanceReleaseManagementOptions.SectionName));
        // Approval workflow must be registered before release service (release service takes it as a dependency)
        services.AddScoped<ISmsGovernanceApprovalWorkflowService, SmsGovernanceApprovalWorkflowService>();
        services.AddScoped<ISmsGovernanceReleaseService, SmsGovernanceReleaseService>();
        // LS-NOTIF-SMS-021-HARDENING: read-only integrity + lock status service
        services.AddScoped<ISmsGovernanceReleaseIntegrityService, SmsGovernanceReleaseIntegrityService>();
        services.AddHostedService<SmsGovernanceReleaseActivationWorker>();

        // LS-NOTIF-SMS-022: Canary Governance Rollout
        services.AddOptions<SmsGovernanceRolloutsOptions>()
                .Bind(configuration.GetSection(SmsGovernanceRolloutsOptions.SectionName));
        services.AddScoped<ISmsGovernanceRolloutEvaluator, SmsGovernanceRolloutEvaluator>();
        services.AddScoped<ISmsGovernanceRolloutAnalyticsService, SmsGovernanceRolloutAnalyticsService>();
        services.AddHostedService<SmsGovernanceRolloutWorker>();

        // LS-NOTIF-SMS-023: Per-tenant governance rule pack scoping
        // Resolution service must be registered before the rule resolver (which depends on it).
        services.AddOptions<SmsGovernanceTenantScopingOptions>()
                .Bind(configuration.GetSection(SmsGovernanceTenantScopingOptions.SectionName));
        services.AddScoped<ISmsGovernanceTenantIsolationValidator, SmsGovernanceTenantIsolationValidator>();
        services.AddScoped<ISmsGovernanceTenantAssignmentService, SmsGovernanceTenantAssignmentService>();
        services.AddScoped<ISmsGovernanceTenantResolutionService, SmsGovernanceTenantResolutionService>();
        // Rollout service registered after LS-023 deps it now depends on
        services.AddScoped<ISmsGovernanceRolloutService, SmsGovernanceRolloutService>();

        // LS-NOTIF-SMS-024: Cross-channel governance federation
        services.AddOptions<GovernanceFederationOptions>()
                .Bind(configuration.GetSection("GovernanceFederation"));
        services.AddScoped<IGovernanceFederationService, GovernanceFederationService>();
        services.AddScoped<IGovernanceTopologyResolver, GovernanceTopologyResolver>();
        services.AddScoped<IGovernanceFederationAnalyticsService, GovernanceFederationAnalyticsService>();

        // LS-NOTIF-SMS-025: Cross-channel governance execution runtime
        services.AddOptions<GovernanceExecutionRuntimeOptions>()
                .Bind(configuration.GetSection("GovernanceExecutionRuntime"));
        services.AddScoped<GovernanceRuleEvaluationHelper>();
        // Channel enforcement engines — all registered; runtime selects by channel type
        services.AddScoped<IGovernanceChannelEnforcementEngine, EmailGovernanceEnforcementEngine>();
        services.AddScoped<IGovernanceChannelEnforcementEngine, PushGovernanceEnforcementEngine>();
        services.AddScoped<IGovernanceChannelEnforcementEngine, WebhookGovernanceEnforcementEngine>();
        services.AddScoped<IGovernanceChannelEnforcementEngine, SmsGovernanceCompatibilityEngine>();
        services.AddScoped<IGovernanceExecutionTelemetryService, GovernanceExecutionTelemetryService>();
        services.AddScoped<IGovernanceExecutionRuntime, GovernanceExecutionRuntime>();
        // Role/org membership lookup. The in-memory provider stays registered so
        // tests and dev seeders can hydrate it directly; the live provider in
        // front of it depends on whether IdentityService:BaseUrl is configured:
        //   • configured  → HttpRoleMembershipProvider hits identity (cached briefly).
        //   • unconfigured → InMemoryRoleMembershipProvider (empty unless seeded).
        services.AddSingleton<InMemoryRoleMembershipProvider>();
        services.AddOptions<IdentityServiceOptions>()
                .Bind(configuration.GetSection(IdentityServiceOptions.SectionName));
        services.AddMemoryCache();
        services.AddHttpClient("IdentityService");

        var identityBaseUrl = configuration[$"{IdentityServiceOptions.SectionName}:BaseUrl"];
        if (!string.IsNullOrWhiteSpace(identityBaseUrl))
        {
            // Register the http provider once and resolve it for both
            // IRoleMembershipProvider (lookup) and IMembershipCacheDiagnostics
            // (operator status endpoint) so counters reflect the same instance.
            services.AddSingleton<HttpRoleMembershipProvider>();
            services.AddSingleton<IRoleMembershipProvider>(sp =>
                sp.GetRequiredService<HttpRoleMembershipProvider>());
            services.AddSingleton<IMembershipCacheDiagnostics>(sp =>
                sp.GetRequiredService<HttpRoleMembershipProvider>());
        }
        else
        {
            services.AddSingleton<IRoleMembershipProvider>(sp =>
                sp.GetRequiredService<InMemoryRoleMembershipProvider>());
            // No real cache when running on the in-memory provider; expose a
            // snapshot that says so rather than leaving the endpoint un-mapped.
            services.AddSingleton<IMembershipCacheDiagnostics, NoOpMembershipCacheDiagnostics>();
        }
        services.AddScoped<IRecipientResolver, RecipientResolver>();
        services.AddScoped<IWebhookIngestionService, WebhookIngestionServiceImpl>();
        services.AddScoped<InternalEmailService>();

        services.AddHttpClient("SendGrid");
        services.AddHttpClient("Twilio");
        services.AddHttpClient("EscalationWebhook");

        var sgApiKey = configuration["SENDGRID_API_KEY"] ?? "";
        var sgFromEmail = configuration["SENDGRID_FROM_EMAIL"] ?? "noreply@legalsynq.com";
        var sgFromName = configuration["SENDGRID_FROM_NAME"] ?? "LegalSynq";

        services.AddScoped<IEmailProviderAdapter>(sp =>
        {
            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            var logger = sp.GetRequiredService<ILogger<SendGridAdapter>>();
            return new SendGridAdapter(sgApiKey, sgFromEmail, sgFromName, httpFactory.CreateClient("SendGrid"), logger);
        });

        var twilioSid = configuration["TWILIO_ACCOUNT_SID"] ?? "";
        var twilioToken = configuration["TWILIO_AUTH_TOKEN"] ?? "";
        var twilioFrom = configuration["TWILIO_FROM_NUMBER"] ?? "";

        services.AddScoped<ISmsProviderAdapter>(sp =>
        {
            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            var logger = sp.GetRequiredService<ILogger<TwilioAdapter>>();
            return new TwilioAdapter(twilioSid, twilioToken, twilioFrom, httpFactory.CreateClient("Twilio"), logger);
        });

        var sgWebhookEnabled = configuration.GetValue<bool>("SENDGRID_WEBHOOK_VERIFICATION_ENABLED", true);
        var sgPublicKey = configuration["SENDGRID_WEBHOOK_PUBLIC_KEY"] ?? "";
        var environment = configuration["ASPNETCORE_ENVIRONMENT"] ?? "Development";

        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<SendGridVerifier>>();
            return new SendGridVerifier(sgWebhookEnabled, sgPublicKey, environment, logger);
        });

        var twilioWebhookEnabled = configuration.GetValue<bool>("TWILIO_WEBHOOK_VERIFICATION_ENABLED", true);

        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<TwilioVerifier>>();
            return new TwilioVerifier(twilioWebhookEnabled, twilioToken, environment, logger);
        });

        services.AddAuditEventClient(configuration);

        services.AddHostedService<NotificationWorker>();
        services.AddHostedService<ProviderHealthWorker>();
        services.AddHostedService<StatusSyncWorker>();
        services.AddHostedService<SmsReconciliationWorker>();
        services.AddHostedService<SmsOperationalAlertWorker>();
        services.AddHostedService<SmsAlertEscalationRetryWorker>();

        return services;
    }
}
