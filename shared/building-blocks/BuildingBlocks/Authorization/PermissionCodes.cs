namespace BuildingBlocks.Authorization;

public static class PermissionCodes
{
    // ── CareConnect ───────────────────────────────────────────────────────────
    public const string ReferralCreate         = "SYNQ_CARECONNECT.referral:create";
    public const string ReferralReadOwn        = "SYNQ_CARECONNECT.referral:read:own";
    public const string ReferralCancel         = "SYNQ_CARECONNECT.referral:cancel";
    public const string ReferralReadAddressed  = "SYNQ_CARECONNECT.referral:read:addressed";
    public const string ReferralAccept         = "SYNQ_CARECONNECT.referral:accept";
    public const string ReferralDecline        = "SYNQ_CARECONNECT.referral:decline";
    public const string ReferralUpdateStatus   = "SYNQ_CARECONNECT.referral:update_status";
    public const string ProviderSearch         = "SYNQ_CARECONNECT.provider:search";
    public const string ProviderMap            = "SYNQ_CARECONNECT.provider:map";
    public const string ProviderManage         = "SYNQ_CARECONNECT.provider:manage";
    public const string AppointmentCreate      = "SYNQ_CARECONNECT.appointment:create";
    public const string AppointmentUpdate      = "SYNQ_CARECONNECT.appointment:update";
    public const string AppointmentManage      = "SYNQ_CARECONNECT.appointment:manage";
    public const string AppointmentReadOwn     = "SYNQ_CARECONNECT.appointment:read:own";
    public const string ScheduleManage         = "SYNQ_CARECONNECT.schedule:manage";
    public const string DashboardRead          = "SYNQ_CARECONNECT.dashboard:read";

    // ── CareConnect: Referral Origination configuration (tenant admin) ──────────
    public const string ReferralAttributionManage = "SYNQ_CARECONNECT.referral_attribution:manage";
    public const string ReferralRepresentativeAccessManage = "SYNQ_CARECONNECT.referral_representative_access:manage";

    // ── SynqLien ─────────────────────────────────────────────────────────────
    public const string LienCreate    = "SYNQ_LIENS.lien:create";
    public const string LienOffer     = "SYNQ_LIENS.lien:offer";
    public const string LienReadOwn   = "SYNQ_LIENS.lien:read:own";
    public const string LienBrowse    = "SYNQ_LIENS.lien:browse";
    public const string LienPurchase  = "SYNQ_LIENS.lien:purchase";
    public const string LienReadHeld  = "SYNQ_LIENS.lien:read:held";
    public const string LienService   = "SYNQ_LIENS.lien:service";
    public const string LienSettle    = "SYNQ_LIENS.lien:settle";
    public const string LienSaleRead          = "SYNQ_LIENS.lien_sale:read";
    public const string LienSaleCreate        = "SYNQ_LIENS.lien_sale:create";
    public const string LienSaleUpdate        = "SYNQ_LIENS.lien_sale:update";
    public const string LienSalePublish       = "SYNQ_LIENS.lien_sale:publish";
    public const string LienSaleWithdraw      = "SYNQ_LIENS.lien_sale:withdraw";
    public const string LienSaleViewAnalytics = "SYNQ_LIENS.lien_sale:view_analytics";
    public const string CaseRead      = "SYNQ_LIENS.case:read";
    public const string CaseCreate    = "SYNQ_LIENS.case:create";
    public const string CaseUpdate    = "SYNQ_LIENS.case:update";
    /// <summary>
    /// LS-FLOW-MERGE-P4 — capability claim required to start a Flow workflow
    /// for a SynqLien sale path (mapped to the <c>CanSellLien</c> policy).
    /// </summary>
    public const string LienSell      = "SYNQ_LIENS.lien:sell";

    // ── Tenant operations (SYNQ_PLATFORM pseudo-product) ─────────────────────
    // Naming: TENANT.<domain>:<action>
    // Resolved via system role → RolePermissionAssignment, not via product roles.
    // UI ownership: Tenant Portal manages tenant role → permission visibility;
    //               Control Center manages the catalog and role-permission governance.
    public const string TenantUsersView         = "TENANT.users:view";
    public const string TenantUsersManage       = "TENANT.users:manage";
    public const string TenantGroupsManage      = "TENANT.groups:manage";
    public const string TenantRolesAssign       = "TENANT.roles:assign";
    public const string TenantProductsAssign    = "TENANT.products:assign";
    public const string TenantSettingsManage    = "TENANT.settings:manage";
    public const string TenantAuditView         = "TENANT.audit:view";
    public const string TenantInvitationsManage = "TENANT.invitations:manage";

    // ── SynqFund ─────────────────────────────────────────────────────────────
    public const string ApplicationCreate          = "SYNQ_FUND.application:create";
    /// <summary>
    /// LS-FLOW-MERGE-P4 — capability claim required to start a Flow workflow
    /// for a SynqFund referral path (mapped to the <c>CanReferFund</c> policy).
    /// </summary>
    public const string ApplicationRefer           = "SYNQ_FUND.application:refer";
    public const string ApplicationReadOwn         = "SYNQ_FUND.application:read:own";
    public const string ApplicationCancel          = "SYNQ_FUND.application:cancel";
    public const string ApplicationReadAddressed   = "SYNQ_FUND.application:read:addressed";
    public const string ApplicationEvaluate        = "SYNQ_FUND.application:evaluate";
    public const string ApplicationApprove         = "SYNQ_FUND.application:approve";
    public const string ApplicationDecline         = "SYNQ_FUND.application:decline";
    public const string ApplicationStatusView      = "SYNQ_FUND.application:status:view";
    public const string PartyCreate                = "SYNQ_FUND.party:create";
    public const string PartyReadOwn               = "SYNQ_FUND.party:read:own";

    // ── SynqInsights ─────────────────────────────────────────────────────────
    // LS-ID-TNT-022-001: Insights permission catalog.
    // LS-ID-TNT-022-003: Backend constants to match the seeded catalog codes.
    public const string InsightsDashboardView   = "SYNQ_INSIGHTS.dashboard:view";
    public const string InsightsReportsView     = "SYNQ_INSIGHTS.reports:view";
    public const string InsightsReportsRun      = "SYNQ_INSIGHTS.reports:run";
    public const string InsightsReportsExport   = "SYNQ_INSIGHTS.reports:export";
    public const string InsightsReportsBuild    = "SYNQ_INSIGHTS.reports:build";
    public const string InsightsSchedulesManage = "SYNQ_INSIGHTS.schedules:manage";
    public const string InsightsSchedulesRun    = "SYNQ_INSIGHTS.schedules:run";

    // ── Xenia / SynqAI ───────────────────────────────────────────────────────
    public const string XeniaAssistantUse    = "SYNQ_AI.assistant:use";
    public const string XeniaAssistantManage = "SYNQ_AI.assistant:manage";
    public const string XeniaUsageRead       = "SYNQ_AI.usage:read";
}
