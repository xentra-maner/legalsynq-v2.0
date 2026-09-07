using Microsoft.EntityFrameworkCore;
using Notifications.Domain;

namespace Notifications.Infrastructure.Data;

public class NotificationsDbContext : DbContext
{
    public NotificationsDbContext(DbContextOptions<NotificationsDbContext> options)
        : base(options) { }

    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<UserInboxItem> UserInboxItems => Set<UserInboxItem>();
    public DbSet<NotificationAttempt> NotificationAttempts => Set<NotificationAttempt>();
    public DbSet<Template> Templates => Set<Template>();
    public DbSet<TemplateVersion> TemplateVersions => Set<TemplateVersion>();
    public DbSet<TenantProviderConfig> TenantProviderConfigs => Set<TenantProviderConfig>();
    public DbSet<TenantChannelProviderSetting> TenantChannelProviderSettings => Set<TenantChannelProviderSetting>();
    public DbSet<ProviderHealth> ProviderHealthRecords => Set<ProviderHealth>();
    public DbSet<ProviderWebhookLog> ProviderWebhookLogs => Set<ProviderWebhookLog>();
    public DbSet<NotificationEvent> NotificationEvents => Set<NotificationEvent>();
    public DbSet<RecipientContactHealth> RecipientContactHealthRecords => Set<RecipientContactHealth>();
    public DbSet<DeliveryIssue> DeliveryIssues => Set<DeliveryIssue>();
    public DbSet<ContactSuppression> ContactSuppressions => Set<ContactSuppression>();
    public DbSet<TenantBillingPlan> TenantBillingPlans => Set<TenantBillingPlan>();
    public DbSet<TenantBillingRate> TenantBillingRates => Set<TenantBillingRate>();
    public DbSet<TenantRateLimitPolicy> TenantRateLimitPolicies => Set<TenantRateLimitPolicy>();
    public DbSet<TenantContactPolicy> TenantContactPolicies => Set<TenantContactPolicy>();
    public DbSet<TenantBranding> TenantBrandings => Set<TenantBranding>();
    public DbSet<UsageMeterEvent> UsageMeterEvents => Set<UsageMeterEvent>();
    public DbSet<SmsContactPreference> SmsContactPreferences => Set<SmsContactPreference>();
    public DbSet<SmsPreferenceHistory> SmsPreferenceHistories => Set<SmsPreferenceHistory>();
    public DbSet<SmsOperationalAlert> SmsOperationalAlerts => Set<SmsOperationalAlert>();
    public DbSet<SmsOperationalEscalationPolicy> SmsEscalationPolicies => Set<SmsOperationalEscalationPolicy>();
    public DbSet<SmsOperationalAlertEscalation> SmsAlertEscalations => Set<SmsOperationalAlertEscalation>();

    // LS-NOTIF-SMS-014: Multi-Provider SMS Routing
    public DbSet<SmsRoutingPolicy> SmsRoutingPolicies => Set<SmsRoutingPolicy>();
    public DbSet<SmsRoutingDecision> SmsRoutingDecisions => Set<SmsRoutingDecision>();

    // LS-NOTIF-SMS-015: Provider Quality Snapshots
    public DbSet<SmsProviderQualitySnapshot> SmsProviderQualitySnapshots => Set<SmsProviderQualitySnapshot>();

    // LS-NOTIF-SMS-016: Recipient Intelligence + Suppression
    public DbSet<SmsRecipientReputationSnapshot> SmsRecipientReputationSnapshots => Set<SmsRecipientReputationSnapshot>();
    public DbSet<SmsSuppressionDecision> SmsSuppressionDecisions => Set<SmsSuppressionDecision>();

    // LS-NOTIF-SMS-017: Governance Policies + Decisions
    public DbSet<SmsGovernancePolicy>   SmsGovernancePolicies  => Set<SmsGovernancePolicy>();
    public DbSet<SmsGovernanceDecision> SmsGovernanceDecisions => Set<SmsGovernanceDecision>();

    // LS-NOTIF-SMS-018: Template Governance
    public DbSet<SmsTemplate>                   SmsTemplates                     => Set<SmsTemplate>();
    public DbSet<SmsTemplateVersion>             SmsTemplateVersions              => Set<SmsTemplateVersion>();
    public DbSet<SmsTemplateGovernanceDecision>  SmsTemplateGovernanceDecisions   => Set<SmsTemplateGovernanceDecision>();

    // LS-NOTIF-SMS-019: Dynamic Governance Rule Packs, Rules, Compliance Profiles
    public DbSet<SmsGovernanceRulePack>                SmsGovernanceRulePacks             => Set<SmsGovernanceRulePack>();
    public DbSet<SmsGovernanceRule>                    SmsGovernanceRules                 => Set<SmsGovernanceRule>();
    public DbSet<SmsComplianceProfile>                 SmsComplianceProfiles              => Set<SmsComplianceProfile>();
    public DbSet<SmsComplianceProfileAssignment>       SmsComplianceProfileAssignments    => Set<SmsComplianceProfileAssignment>();

    // LS-NOTIF-SMS-020: Governance Versioning, Import, Analytics
    public DbSet<SmsGovernanceRuleVersion>             SmsGovernanceRuleVersions          => Set<SmsGovernanceRuleVersion>();
    public DbSet<SmsGovernanceRulePackVersion>         SmsGovernanceRulePackVersions      => Set<SmsGovernanceRulePackVersion>();
    public DbSet<SmsGovernanceRuleMatchMetric>         SmsGovernanceRuleMatchMetrics      => Set<SmsGovernanceRuleMatchMetric>();

    // LS-NOTIF-SMS-021: Governance Release Management, Approval Workflow
    public DbSet<SmsGovernanceReleasePackage>    SmsGovernanceReleasePackages    => Set<SmsGovernanceReleasePackage>();
    public DbSet<SmsGovernanceReleaseItem>       SmsGovernanceReleaseItems       => Set<SmsGovernanceReleaseItem>();
    public DbSet<SmsGovernanceApprovalRequest>   SmsGovernanceApprovalRequests   => Set<SmsGovernanceApprovalRequest>();
    public DbSet<SmsGovernanceApprovalDecision>  SmsGovernanceApprovalDecisions  => Set<SmsGovernanceApprovalDecision>();
    public DbSet<SmsGovernanceReleaseAuditEvent> SmsGovernanceReleaseAuditEvents => Set<SmsGovernanceReleaseAuditEvent>();

    // LS-NOTIF-SMS-022: Canary Governance Rollout
    public DbSet<SmsGovernanceRolloutPlan>       SmsGovernanceRolloutPlans       => Set<SmsGovernanceRolloutPlan>();
    public DbSet<SmsGovernanceRolloutStage>      SmsGovernanceRolloutStages      => Set<SmsGovernanceRolloutStage>();
    public DbSet<SmsGovernanceTenantCohort>      SmsGovernanceTenantCohorts      => Set<SmsGovernanceTenantCohort>();
    public DbSet<SmsGovernanceRolloutAuditEvent> SmsGovernanceRolloutAuditEvents => Set<SmsGovernanceRolloutAuditEvent>();

    // LS-NOTIF-SMS-023: Per-tenant governance rule pack scoping
    public DbSet<SmsGovernanceTenantRulePackAssignment>   SmsGovernanceTenantRulePackAssignments   => Set<SmsGovernanceTenantRulePackAssignment>();
    public DbSet<SmsGovernanceTenantOverlay>              SmsGovernanceTenantOverlays              => Set<SmsGovernanceTenantOverlay>();
    public DbSet<SmsGovernanceTenantAssignmentAuditEvent> SmsGovernanceTenantAssignmentAuditEvents => Set<SmsGovernanceTenantAssignmentAuditEvent>();

    // LS-NOTIF-SMS-024: Cross-channel governance federation
    public DbSet<GovernanceChannelScope>          GovernanceChannelScopes          => Set<GovernanceChannelScope>();
    public DbSet<GovernanceFederatedRulePack>     GovernanceFederatedRulePacks     => Set<GovernanceFederatedRulePack>();
    public DbSet<GovernanceFederationOverlay>     GovernanceFederationOverlays     => Set<GovernanceFederationOverlay>();
    public DbSet<GovernanceFederationAuditEvent>  GovernanceFederationAuditEvents  => Set<GovernanceFederationAuditEvent>();

    // LS-NOTIF-SMS-025: Governance execution runtime telemetry
    public DbSet<GovernanceExecutionRecord>       GovernanceExecutionRecords       => Set<GovernanceExecutionRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfiguration(new Configurations.NotificationConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.UserInboxItemConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.NotificationAttemptConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.TemplateConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.TemplateVersionConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.TenantProviderConfigConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.TenantChannelProviderSettingConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.ProviderHealthConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.ProviderWebhookLogConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.NotificationEventConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.RecipientContactHealthConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.DeliveryIssueConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.ContactSuppressionConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.TenantBillingPlanConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.TenantBillingRateConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.TenantRateLimitPolicyConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.TenantContactPolicyConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.TenantBrandingConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.UsageMeterEventConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.SmsContactPreferenceConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.SmsPreferenceHistoryConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.SmsOperationalAlertConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.SmsEscalationPolicyConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.SmsAlertEscalationConfiguration());
        // LS-NOTIF-SMS-014
        modelBuilder.ApplyConfiguration(new Configurations.SmsRoutingPolicyConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.SmsRoutingDecisionConfiguration());
        // LS-NOTIF-SMS-015
        modelBuilder.ApplyConfiguration(new Configurations.SmsProviderQualitySnapshotConfiguration());
        // LS-NOTIF-SMS-016
        modelBuilder.ApplyConfiguration(new Configurations.SmsRecipientReputationSnapshotConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.SmsSuppressionDecisionConfiguration());
        // LS-NOTIF-SMS-017
        modelBuilder.ApplyConfiguration(new Configurations.SmsGovernancePolicyConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.SmsGovernanceDecisionConfiguration());
        // LS-NOTIF-SMS-018
        modelBuilder.ApplyConfiguration(new Configurations.SmsTemplateConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.SmsTemplateVersionConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.SmsTemplateGovernanceDecisionConfiguration());
        // LS-NOTIF-SMS-019
        modelBuilder.ApplyConfiguration(new Configurations.SmsGovernanceRulePackConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.SmsGovernanceRuleConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.SmsComplianceProfileConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.SmsComplianceProfileAssignmentConfiguration());
        // LS-NOTIF-SMS-020
        modelBuilder.ApplyConfiguration(new Configurations.SmsGovernanceRuleVersionConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.SmsGovernanceRulePackVersionConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.SmsGovernanceRuleMatchMetricConfiguration());
        // LS-NOTIF-SMS-021
        modelBuilder.ApplyConfiguration(new Configurations.SmsGovernanceReleasePackageConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.SmsGovernanceReleaseItemConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.SmsGovernanceApprovalRequestConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.SmsGovernanceApprovalDecisionConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.SmsGovernanceReleaseAuditEventConfiguration());
        // LS-NOTIF-SMS-022
        modelBuilder.ApplyConfiguration(new Configurations.SmsGovernanceRolloutPlanConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.SmsGovernanceRolloutStageConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.SmsGovernanceTenantCohortConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.SmsGovernanceRolloutAuditEventConfiguration());

        // LS-NOTIF-SMS-023
        modelBuilder.ApplyConfiguration(new Configurations.SmsGovernanceTenantRulePackAssignmentConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.SmsGovernanceTenantOverlayConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.SmsGovernanceTenantAssignmentAuditEventConfiguration());

        // LS-NOTIF-SMS-024
        modelBuilder.ApplyConfiguration(new Configurations.GovernanceChannelScopeConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.GovernanceFederatedRulePackConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.GovernanceFederationOverlayConfiguration());
        modelBuilder.ApplyConfiguration(new Configurations.GovernanceFederationAuditEventConfiguration());

        // LS-NOTIF-SMS-025
        modelBuilder.ApplyConfiguration(new Configurations.GovernanceExecutionRecordConfiguration());
    }
}
