namespace Identity.Infrastructure.Data;

internal static class SeedIds
{
    public static readonly DateTime SeededAt = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── Products ──────────────────────────────────────────────────────────────
    public static readonly Guid ProductSynqFund        = new("10000000-0000-0000-0000-000000000001");
    public static readonly Guid ProductSynqLiens       = new("10000000-0000-0000-0000-000000000002");
    public static readonly Guid ProductSynqCareConnect = new("10000000-0000-0000-0000-000000000003");
    public static readonly Guid ProductSynqPay         = new("10000000-0000-0000-0000-000000000004");
    public static readonly Guid ProductSynqAI          = new("10000000-0000-0000-0000-000000000005");

    // ── Tenant ────────────────────────────────────────────────────────────────
    public static readonly Guid TenantLegalSynq = new("20000000-0000-0000-0000-000000000001");

    // ── System Roles — Platform ──────────────────────────────────────────────
    public static readonly Guid RolePlatformAdmin   = new("30000000-0000-0000-0000-000000000001");
    public static readonly Guid RolePlatformOps     = new("30000000-0000-0000-0000-000000000004");
    public static readonly Guid RolePlatformSupport = new("30000000-0000-0000-0000-000000000005");
    public static readonly Guid RolePlatformBilling = new("30000000-0000-0000-0000-000000000006");
    public static readonly Guid RolePlatformAuditor = new("30000000-0000-0000-0000-000000000007");

    // ── System Roles — Tenant ────────────────────────────────────────────────
    public static readonly Guid RoleTenantAdmin   = new("30000000-0000-0000-0000-000000000002");
    public static readonly Guid RoleTenantManager = new("30000000-0000-0000-0000-000000000008");
    public static readonly Guid RoleTenantStaff   = new("30000000-0000-0000-0000-000000000009");
    public static readonly Guid RoleTenantViewer  = new("30000000-0000-0000-0000-000000000010");
    public static readonly Guid RoleStandardUser  = new("30000000-0000-0000-0000-000000000003");
    public static readonly Guid RoleTenantUser    = new("30000000-0000-0000-0000-000000000014");

    // ── System Roles — Support module ────────────────────────────────────────
    public static readonly Guid RoleSupportAdmin    = new("30000000-0000-0000-0000-000000000011");
    public static readonly Guid RoleSupportManager  = new("30000000-0000-0000-0000-000000000012");
    public static readonly Guid RoleSupportAgent    = new("30000000-0000-0000-0000-000000000013");
    public static readonly Guid RoleExternalCustomer = new("30000000-0000-0000-0000-000000000015");

    // ── Organizations ─────────────────────────────────────────────────────────
    public static readonly Guid OrgLegalSynq = new("40000000-0000-0000-0000-000000000001");

    // ── Organization Domains ──────────────────────────────────────────────────
    public static readonly Guid OrgDomainLegalSynq = new("40000000-0000-0000-0000-000000000002");

    // ── Product Roles ─────────────────────────────────────────────────────────
    public static readonly Guid PrCareConnectReferrer     = new("50000000-0000-0000-0000-000000000001");
    public static readonly Guid PrCareConnectReceiver     = new("50000000-0000-0000-0000-000000000002");
    public static readonly Guid PrSynqLienSeller          = new("50000000-0000-0000-0000-000000000003");
    public static readonly Guid PrSynqLienBuyer           = new("50000000-0000-0000-0000-000000000004");
    public static readonly Guid PrSynqLienHolder          = new("50000000-0000-0000-0000-000000000005");
    public static readonly Guid PrSynqFundReferrer        = new("50000000-0000-0000-0000-000000000006");
    public static readonly Guid PrSynqFundFunder          = new("50000000-0000-0000-0000-000000000007");
    public static readonly Guid PrSynqFundApplicantPortal = new("50000000-0000-0000-0000-000000000008");
    public static readonly Guid PrXeniaUser               = new("50000000-0000-0000-0000-000000000009");
    public static readonly Guid PrXeniaAdmin              = new("50000000-0000-0000-0000-000000000010");
    public static readonly Guid PrCareConnectNetworkManager = new("50000000-0000-0000-0000-000000000011");
    public static readonly Guid PrCareConnectReferrerAdmin = new("50000000-0000-0000-0000-000000000012");

    // ── Permissions — CareConnect ───────────────────────────────────────────
    public static readonly Guid PermReferralCreate        = new("60000000-0000-0000-0000-000000000001");
    public static readonly Guid PermReferralReadOwn       = new("60000000-0000-0000-0000-000000000002");
    public static readonly Guid PermReferralCancel        = new("60000000-0000-0000-0000-000000000003");
    public static readonly Guid PermReferralReadAddressed = new("60000000-0000-0000-0000-000000000004");
    public static readonly Guid PermReferralAccept        = new("60000000-0000-0000-0000-000000000005");
    public static readonly Guid PermReferralDecline       = new("60000000-0000-0000-0000-000000000006");
    public static readonly Guid PermProviderSearch        = new("60000000-0000-0000-0000-000000000007");
    public static readonly Guid PermProviderMap           = new("60000000-0000-0000-0000-000000000008");
    public static readonly Guid PermProviderManage        = new("60000000-0000-0000-0000-000000000072");
    public static readonly Guid PermAppointmentCreate     = new("60000000-0000-0000-0000-000000000009");
    public static readonly Guid PermAppointmentUpdate     = new("60000000-0000-0000-0000-000000000010");
    public static readonly Guid PermAppointmentReadOwn    = new("60000000-0000-0000-0000-000000000011");

    // ── Permissions — SynqLien ──────────────────────────────────────────────
    public static readonly Guid PermLienCreate   = new("60000000-0000-0000-0000-000000000012");
    public static readonly Guid PermLienOffer    = new("60000000-0000-0000-0000-000000000013");
    public static readonly Guid PermLienReadOwn  = new("60000000-0000-0000-0000-000000000014");
    public static readonly Guid PermLienBrowse   = new("60000000-0000-0000-0000-000000000015");
    public static readonly Guid PermLienPurchase = new("60000000-0000-0000-0000-000000000016");
    public static readonly Guid PermLienReadHeld = new("60000000-0000-0000-0000-000000000017");
    public static readonly Guid PermLienService  = new("60000000-0000-0000-0000-000000000018");
    public static readonly Guid PermLienSettle   = new("60000000-0000-0000-0000-000000000019");

    // ── Organization Types ────────────────────────────────────────────────────
    public static readonly Guid OrgTypeInternal  = new("70000000-0000-0000-0000-000000000001");
    public static readonly Guid OrgTypeLawFirm   = new("70000000-0000-0000-0000-000000000002");
    public static readonly Guid OrgTypeProvider  = new("70000000-0000-0000-0000-000000000003");
    public static readonly Guid OrgTypeFunder    = new("70000000-0000-0000-0000-000000000004");
    public static readonly Guid OrgTypeLienOwner = new("70000000-0000-0000-0000-000000000005");

    // ── Relationship Types ────────────────────────────────────────────────────
    public static readonly Guid RelTypeRefersTo             = new("80000000-0000-0000-0000-000000000001");
    public static readonly Guid RelTypeAcceptsReferralsFrom = new("80000000-0000-0000-0000-000000000002");
    public static readonly Guid RelTypeFundedBy             = new("80000000-0000-0000-0000-000000000003");
    public static readonly Guid RelTypeServicesFor          = new("80000000-0000-0000-0000-000000000004");
    public static readonly Guid RelTypeAssignsLienTo        = new("80000000-0000-0000-0000-000000000005");
    public static readonly Guid RelTypeMemberOfNetwork      = new("80000000-0000-0000-0000-000000000006");

    // ── Product–RelationshipType Rules ────────────────────────────────────────
    public static readonly Guid PrRelRuleCareConnectRefersTo             = new("81000000-0000-0000-0000-000000000001");
    public static readonly Guid PrRelRuleCareConnectAcceptsReferralsFrom = new("81000000-0000-0000-0000-000000000002");
    public static readonly Guid PrRelRuleSynqFundFundedBy                = new("81000000-0000-0000-0000-000000000003");
    public static readonly Guid PrRelRuleSynqLienAssignsLienTo           = new("81000000-0000-0000-0000-000000000004");

    // ── Product–OrgType Rules ─────────────────────────────────────────────────
    public static readonly Guid PrOrgTypeRuleCareConnectReferrerLawFirm  = new("90000000-0000-0000-0000-000000000001");
    public static readonly Guid PrOrgTypeRuleCareConnectReceiverProvider = new("90000000-0000-0000-0000-000000000002");
    public static readonly Guid PrOrgTypeRuleSynqLienSellerLawFirm       = new("90000000-0000-0000-0000-000000000003");
    public static readonly Guid PrOrgTypeRuleSynqLienBuyerLienOwner      = new("90000000-0000-0000-0000-000000000004");
    public static readonly Guid PrOrgTypeRuleSynqLienHolderLienOwner     = new("90000000-0000-0000-0000-000000000005");
    public static readonly Guid PrOrgTypeRuleSynqFundReferrerLawFirm     = new("90000000-0000-0000-0000-000000000006");
    public static readonly Guid PrOrgTypeRuleSynqFundFunderFunder        = new("90000000-0000-0000-0000-000000000007");

    // ── Permissions — SynqFund ──────────────────────────────────────────────
    public static readonly Guid PermApplicationCreate        = new("60000000-0000-0000-0000-000000000020");
    public static readonly Guid PermApplicationReadOwn       = new("60000000-0000-0000-0000-000000000021");
    public static readonly Guid PermApplicationCancel        = new("60000000-0000-0000-0000-000000000022");
    public static readonly Guid PermApplicationReadAddressed = new("60000000-0000-0000-0000-000000000023");
    public static readonly Guid PermApplicationEvaluate      = new("60000000-0000-0000-0000-000000000024");
    public static readonly Guid PermApplicationApprove       = new("60000000-0000-0000-0000-000000000025");
    public static readonly Guid PermApplicationDecline       = new("60000000-0000-0000-0000-000000000026");
    public static readonly Guid PermPartyCreate              = new("60000000-0000-0000-0000-000000000027");
    public static readonly Guid PermPartyReadOwn             = new("60000000-0000-0000-0000-000000000028");
    public static readonly Guid PermApplicationStatusView    = new("60000000-0000-0000-0000-000000000029");

    // ── LS-ID-TNT-011: Products — SynqPlatform (tenant-permission catalog anchor) ─
    public static readonly Guid ProductSynqPlatform = new("10000000-0000-0000-0000-000000000006");

    // ── LS-ID-TNT-011: Permissions — Tenant-level operations (SYNQ_PLATFORM) ─────
    public static readonly Guid PermTenantUsersView          = new("60000000-0000-0000-0000-000000000030");
    public static readonly Guid PermTenantUsersManage        = new("60000000-0000-0000-0000-000000000031");
    public static readonly Guid PermTenantGroupsManage       = new("60000000-0000-0000-0000-000000000032");
    public static readonly Guid PermTenantRolesAssign        = new("60000000-0000-0000-0000-000000000033");
    public static readonly Guid PermTenantProductsAssign     = new("60000000-0000-0000-0000-000000000034");
    public static readonly Guid PermTenantSettingsManage     = new("60000000-0000-0000-0000-000000000035");
    public static readonly Guid PermTenantAuditView          = new("60000000-0000-0000-0000-000000000036");
    public static readonly Guid PermTenantInvitationsManage  = new("60000000-0000-0000-0000-000000000037");

    // ── LS-ID-TNT-022-001: Products — SynqInsights ───────────────────────────────
    public static readonly Guid ProductSynqInsights = new("10000000-0000-0000-0000-000000000007");

    // ── LS-ID-TNT-022-001: Permissions — Insights (SYNQ_INSIGHTS) ────────────────
    // IDs 0038-0044, continuing the sequential decimal-in-UUID naming convention.
    public static readonly Guid PermInsightsDashboardView   = new("60000000-0000-0000-0000-000000000038");
    public static readonly Guid PermInsightsReportsView     = new("60000000-0000-0000-0000-000000000039");
    public static readonly Guid PermInsightsReportsRun      = new("60000000-0000-0000-0000-000000000040");
    public static readonly Guid PermInsightsReportsExport   = new("60000000-0000-0000-0000-000000000041");
    public static readonly Guid PermInsightsReportsBuild    = new("60000000-0000-0000-0000-000000000042");
    public static readonly Guid PermInsightsSchedulesManage = new("60000000-0000-0000-0000-000000000043");
    public static readonly Guid PermInsightsSchedulesRun    = new("60000000-0000-0000-0000-000000000044");

    // ── Permissions — SynqLien Tasks (SYNQ_LIENS.task:*) ─────────────────────────
    // IDs 0045-0051
    public static readonly Guid PermTaskRead     = new("60000000-0000-0000-0000-000000000045");
    public static readonly Guid PermTaskCreate   = new("60000000-0000-0000-0000-000000000046");
    public static readonly Guid PermTaskEditOwn  = new("60000000-0000-0000-0000-000000000047");
    public static readonly Guid PermTaskEditAll  = new("60000000-0000-0000-0000-000000000048");
    public static readonly Guid PermTaskAssign   = new("60000000-0000-0000-0000-000000000049");
    public static readonly Guid PermTaskComplete = new("60000000-0000-0000-0000-000000000050");
    public static readonly Guid PermTaskCancel   = new("60000000-0000-0000-0000-000000000051");

    // ── PUM-B02: Permissions — Platform-level operations (SYNQ_PLATFORM) ─────────
    // IDs 0052-0062
    public static readonly Guid PermPlatformUsersRead     = new("60000000-0000-0000-0000-000000000052");
    public static readonly Guid PermPlatformUsersManage   = new("60000000-0000-0000-0000-000000000053");
    public static readonly Guid PermPlatformRolesRead     = new("60000000-0000-0000-0000-000000000054");
    public static readonly Guid PermPlatformRolesManage   = new("60000000-0000-0000-0000-000000000055");
    public static readonly Guid PermPlatformTenantsRead   = new("60000000-0000-0000-0000-000000000056");
    public static readonly Guid PermPlatformTenantsManage = new("60000000-0000-0000-0000-000000000057");
    public static readonly Guid PermPlatformProductsRead  = new("60000000-0000-0000-0000-000000000058");
    public static readonly Guid PermPlatformProductsManage= new("60000000-0000-0000-0000-000000000059");
    public static readonly Guid PermPlatformMonitoringRead= new("60000000-0000-0000-0000-000000000060");
    public static readonly Guid PermPlatformAuditRead     = new("60000000-0000-0000-0000-000000000061");
    public static readonly Guid PermTenantSettingsRead    = new("60000000-0000-0000-0000-000000000062");

    // ── Permissions — Xenia / SynqAI ─────────────────────────────────────────
    public static readonly Guid PermXeniaAssistantUse     = new("60000000-0000-0000-0000-000000000063");
    public static readonly Guid PermXeniaAssistantManage  = new("60000000-0000-0000-0000-000000000064");
    public static readonly Guid PermXeniaUsageRead        = new("60000000-0000-0000-0000-000000000065");

    // ── Product–OrgType Rules — Xenia / SynqAI ───────────────────────────────
    public static readonly Guid PrOrgTypeRuleXeniaUserLawFirm    = new("90000000-0000-0000-0000-000000000008");
    public static readonly Guid PrOrgTypeRuleXeniaUserProvider   = new("90000000-0000-0000-0000-000000000009");
    public static readonly Guid PrOrgTypeRuleXeniaUserFunder     = new("90000000-0000-0000-0000-000000000010");
    public static readonly Guid PrOrgTypeRuleXeniaUserLienOwner  = new("90000000-0000-0000-0000-000000000011");
    public static readonly Guid PrOrgTypeRuleXeniaAdminInternal  = new("90000000-0000-0000-0000-000000000012");
    public static readonly Guid PrOrgTypeRuleCareConnectNetworkManagerLawFirm = new("90000000-0000-0000-0000-000000000013");
    public static readonly Guid PrOrgTypeRuleCareConnectNetworkManagerLienOwner = new("90000000-0000-0000-0000-000000000014");
}
