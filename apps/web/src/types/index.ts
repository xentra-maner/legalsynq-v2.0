// ── API Response ─────────────────────────────────────────────────────────────

export interface ApiResponse<T> {
  data: T;
  correlationId: string;
  status: number;
}

// ── Platform constants ────────────────────────────────────────────────────────
// Mirror of BuildingBlocks.Authorization — keep in sync with backend
// LS-COR-AUT-006A: ProductRole values use unified PRODUCT:Role claim format.

export const OrgType = {
  Internal: "INTERNAL",
  LawFirm: "LAW_FIRM",
  Provider: "PROVIDER",
  Funder: "FUNDER",
  LienOwner: "LIEN_OWNER",
} as const;
export type OrgTypeValue = (typeof OrgType)[keyof typeof OrgType];

export const SystemRole = {
  PlatformAdmin: "PlatformAdmin",
  TenantAdmin: "TenantAdmin",
  StandardUser: "StandardUser",
} as const;
export type SystemRoleValue = (typeof SystemRole)[keyof typeof SystemRole];

export const ProductRole = {
  // CareConnect (product code: SYNQ_CARECONNECT)
  CareConnectReferrer: "SYNQ_CARECONNECT:CARECONNECT_REFERRER",
  CareConnectReceiver: "SYNQ_CARECONNECT:CARECONNECT_RECEIVER",
  // CC2-INT-B06: role-based network management (not orgType-based)
  CareConnectNetworkManager: "SYNQ_CARECONNECT:CARECONNECT_NETWORK_MANAGER",
  // LSV3-1084: law-firm-scoped network self-management
  CareConnectReferrerAdmin: "SYNQ_CARECONNECT:CARECONNECT_REFERRER_ADMIN",
  // SynqFund (product code: SYNQ_FUND)
  SynqFundReferrer: "SYNQ_FUND:SYNQFUND_REFERRER",
  SynqFundFunder: "SYNQ_FUND:SYNQFUND_FUNDER",
  SynqFundApplicantPortal: "SYNQ_FUND:SYNQFUND_APPLICANT_PORTAL",
  // SynqLien (product code: SYNQ_LIENS)
  SynqLienSeller: "SYNQ_LIENS:SYNQLIEN_SELLER",
  SynqLienBuyer: "SYNQ_LIENS:SYNQLIEN_BUYER",
  SynqLienHolder: "SYNQ_LIENS:SYNQLIEN_HOLDER",
  // Xenia (product code: SYNQ_AI)
  XeniaUser: "SYNQ_AI:XENIA_USER",
  XeniaAdmin: "SYNQ_AI:XENIA_ADMIN",
} as const;
export type ProductRoleValue = (typeof ProductRole)[keyof typeof ProductRole];

// ── Session shapes ────────────────────────────────────────────────────────────

/**
 * The authoritative frontend session.
 * Populated from GET /identity/api/auth/me — never from raw browser JWT decode alone.
 * The /auth/me endpoint validates the token server-side and returns a safe envelope.
 */
export interface PlatformSession {
  // Identity
  userId: string;
  email: string;

  // Tenant
  tenantId: string;
  tenantCode: string;

  // Organization
  orgId?: string;
  orgType?: OrgTypeValue;
  orgName?: string;

  // Access
  productRoles: ProductRoleValue[];
  systemRoles: SystemRoleValue[];
  isPlatformAdmin: boolean;
  isTenantAdmin: boolean;
  hasOrg: boolean;

  // Session
  avatarDocumentId?: string;
  phone?: string;
  expiresAt: Date;
  sessionTimeoutMinutes: number;

  // Permissions
  /**
   * LS-ID-TNT-015: Effective permission codes for the authenticated user.
   * Populated from the JWT `permissions` claim via /auth/me.
   * Use usePermission(code) or usePermissions() rather than accessing this directly.
   * Frontend checks are UX-only — backend enforcement remains authoritative.
   */
  permissions?: string[];

  // Products
  enabledProducts?: string[];
  /**
   * LS-ID-TNT-009: User-specific effective product codes from the JWT product_codes claim.
   * Reflects direct grants + group inheritance + TenantAdmin auto-grant + LegacyDefault.
   * Use this to drive the product switcher; prefer it over enabledProducts (tenant-level).
   */
  userProducts?: string[];
}

// ── Navigation ────────────────────────────────────────────────────────────────

export interface NavItem {
  heading?: string;
  href?: string;
  label?: string;
  icon?: string;
  badge?: string;
  badgeKey?: string;
  requiredRoles?: ProductRoleValue[];
  /** Item is hidden when user has ANY of these roles, even if requiredRoles also matches. */
  excludedRoles?: ProductRoleValue[];
  sellModeOnly?: boolean;
  adminOnly?: boolean;
  /** Item is hidden for these org types (e.g. hide Network from PROVIDER orgs). */
  hiddenForOrgTypes?: OrgTypeValue[];
  /**
   * Makes this item visible whenever the user is a TenantAdmin AND their org type
   * matches one of the listed values — even if they don't hold the requiredRoles.
   * Use for management-level features that all tenant admins of a given org type
   * should access regardless of product-role provisioning state.
   */
  visibleForTenantAdminInOrgTypes?: OrgTypeValue[];
  /**
   * Marks this item as excluded from the current Phase 1 migration scope (LSV3-628).
   * Hidden from the rendered sidebar until Phase 1 is complete, but kept in the
   * nav definition so it's easy to re-enable later. Absent/false = visible.
   */
  notInPhase1?: boolean;
  /**
   * When set, clicking this item shows this message as a toast instead of
   * navigating to its href. Use for features that are temporarily disabled
   * but should stay visible in the nav (e.g. "Coming soon").
   */
  disabledMessage?: string;
  /**
   * Hides this item when the current subdomain resolves to a restricted,
   * single-product portal (see lib/portal.ts's PORTAL_CONFIGS — e.g. the
   * CareConnect-only "common portal" subdomain used by CareConnect-only
   * customers). Use for tenant-configuration screens that only make sense
   * in the full multi-product tenant portal, not a white-labeled single-
   * product entry point.
   */
  hiddenInProductPortal?: boolean;
  /**
   * Nested sub-items rendered under this item as a collapsible dropdown.
   * When present, the item acts as an expand/collapse toggle in addition
   * to (or instead of) being a link.
   */
  children?: NavItem[];
}

export interface NavSection {
  heading?: string;
  items: NavItem[];
  sellModeOnly?: boolean;
  /**
   * Marks this entire section as excluded from the current Phase 1 migration scope
   * (LSV3-628). The section is dropped entirely from the rendered sidebar (not just
   * emptied) until Phase 1 is complete. Absent/false = visible.
   */
  notInPhase1?: boolean;
}

/** @deprecated Use NavSection[] */
export interface NavGroup {
  id: string;
  label: string;
  icon?: string;
  items: NavItem[];
}

export interface TenantBranding {
  tenantId?: string;
  tenantCode?: string;
  displayName?: string;
  primaryColor?: string;
  logoUrl?: string;
  logoDocumentId?: string;
  logoWhiteDocumentId?: string;
  faviconUrl?: string;
}

// ── CareConnect ───────────────────────────────────────────────────────────────

export type CareConnectUserType = "Provider" | "CareConnectReceiver";

export interface ApplicantPortalSession extends Pick<
  PlatformSession,
  "userId" | "email" | "tenantId" | "tenantCode" | "orgId" | "orgType"
> {
  productRoles: ["SYNQ_FUND:SYNQFUND_APPLICANT_PORTAL"];
}
