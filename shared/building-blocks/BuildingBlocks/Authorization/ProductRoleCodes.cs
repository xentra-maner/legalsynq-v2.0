namespace BuildingBlocks.Authorization;

public static class ProductRoleCodes
{
    // CareConnect
    public const string CareConnectReferrer       = "CARECONNECT_REFERRER";
    public const string CareConnectReceiver       = "CARECONNECT_RECEIVER";
    // CC2-INT-B06: role-based network management (not orgType-based)
    public const string CareConnectNetworkManager = "CARECONNECT_NETWORK_MANAGER";
    // LSV3-1084: law-firm-scoped network self-management (org-restricted to LAW_FIRM in seed data)
    public const string CareConnectReferrerAdmin  = "CARECONNECT_REFERRER_ADMIN";

    // SynqLien
    public const string SynqLienSeller = "SYNQLIEN_SELLER";
    public const string SynqLienBuyer  = "SYNQLIEN_BUYER";
    public const string SynqLienHolder = "SYNQLIEN_HOLDER";

    // SynqFund
    public const string SynqFundReferrer        = "SYNQFUND_REFERRER";
    public const string SynqFundFunder          = "SYNQFUND_FUNDER";
    public const string SynqFundApplicantPortal = "SYNQFUND_APPLICANT_PORTAL";

    // Xenia / SynqAI
    public const string XeniaUser  = "XENIA_USER";
    public const string XeniaAdmin = "XENIA_ADMIN";
}
