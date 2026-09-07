namespace BuildingBlocks.Authorization;

public static class Policies
{
    // Legacy policies (role-based)
    public const string AuthenticatedUser     = "AuthenticatedUser";
    public const string AdminOnly             = "AdminOnly";
    public const string PlatformOrTenantAdmin = "PlatformOrTenantAdmin";

    // LS-NOTIF-CORE-021 — service-to-service submission gate on POST /v1/notifications.
    // Accepts authenticated callers (user or service JWT) OR legacy unauthenticated
    // callers that supply a valid X-Tenant-Id header (backward-compat transition).
    public const string ServiceSubmission = "ServiceSubmission";

    // User JWT only: valid GUID sub + tenant_id, with no service identity claim.
    public const string NotificationInboxUser = "NotificationInboxUser";

    // Capability-based policies (coarse product role gates — use for route groups).
    //
    // REGISTRATION STATUS (BLK-GOV-01 audit, 2026-04-23):
    //   CanReferCareConnect          — registered in Flow.Api (ProductWorkflowsController)
    //   CanReceiveCareConnect        — NOT YET REGISTERED. Reserved for future receive-side gate.
    //                                  Do NOT use with RequireAuthorization() — it will throw at
    //                                  request time. Use RequireProductRole(...) directly instead.
    //   CanManageCareConnectNetworks — NOT YET REGISTERED. CC2-INT-B06 uses RequireProductRole()
    //                                  (RequireProductRoleFilter) instead. Do not use with
    //                                  RequireAuthorization() — it will throw at request time.
    //   CanSellLien                  — registered in Flow.Api
    //   CanBuyLien                   — registered in Flow.Api (if used) or Liens.Api
    //   CanReferFund                 — registered in Flow.Api and Fund.Api
    //   CanFundApplications          — registered in Fund.Api
    public const string CanReferCareConnect          = "CanReferCareConnect";
    public const string CanReceiveCareConnect        = "CanReceiveCareConnect";
    public const string CanManageCareConnectNetworks = "CanManageCareConnectNetworks";
    public const string CanSellLien           = "CanSellLien";
    public const string CanBuyLien            = "CanBuyLien";
    public const string CanReferFund          = "CanReferFund";
    public const string CanFundApplications   = "CanFundApplications";
}
