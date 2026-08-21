import { redirect } from 'next/navigation';
import { getServerSession, requireSession } from '@/lib/session';
import type { PlatformSession, ProductRoleValue } from '@/types';
import { OrgType } from '@/types';
import { CC_ACCESS_DENIED_URL, CC_LOGIN_URL } from '@/lib/control-center-config';

// ── LS-ID-TNT-010: Frontend-friendly product codes ────────────────────────────
// Keep in sync with AuthService.DbToFrontendProductCode.
export const FrontendProductCode = {
  CareConnect:  'CareConnect',
  SynqFund:     'SynqFund',
  SynqLien:     'SynqLien',
  SynqInsights: 'SynqInsights',
  SynqComms:    'SynqComms',
  SynqAI:       'SynqAI',
  Xenia:        'Xenia',
} as const;
export type FrontendProductCodeValue = (typeof FrontendProductCode)[keyof typeof FrontendProductCode];

export function sessionHasProductAccess(
  session: Pick<PlatformSession, 'isPlatformAdmin' | 'isTenantAdmin' | 'userProducts' | 'enabledProducts'>,
  productCode: FrontendProductCodeValue,
): boolean {
  const isXeniaProduct =
    productCode === FrontendProductCode.Xenia ||
    productCode === FrontendProductCode.SynqAI;

  if (!isXeniaProduct && (session.isPlatformAdmin || session.isTenantAdmin)) return true;

  // `userProducts` is the authoritative explicit-access list from Identity.
  // Only fall back to tenant-enabled products when the field is truly absent,
  // which covers older sessions emitted before user-level product claims existed.
  const products =
    session.userProducts !== undefined
      ? session.userProducts
      : (session.enabledProducts ?? []);

  if (products.includes(productCode)) return true;
  if (products.includes('XENIA')) {
    return productCode === FrontendProductCode.Xenia || productCode === FrontendProductCode.SynqAI;
  }
  if (productCode === FrontendProductCode.Xenia) return products.includes(FrontendProductCode.SynqAI);
  if (productCode === FrontendProductCode.SynqAI) return products.includes(FrontendProductCode.Xenia);
  return false;
}

/**
 * Ensure a valid session exists.
 * Redirects to /login if not.
 */
export async function requireAuthenticated(): Promise<PlatformSession> {
  return requireSession();
}

/**
 * Ensure the session has an org membership.
 * Redirects to /no-org if the user is authenticated but has no org.
 */
export async function requireOrg(): Promise<PlatformSession & { orgId: string }> {
  const session = await requireSession();
  if (!session.hasOrg) redirect('/no-org');
  return session as PlatformSession & { orgId: string };
}

/**
 * Ensure the session includes the given product role (or, if an array is
 * passed, at least one of the given product roles). Redirects to /dashboard
 * if not.
 *
 * Usage:
 *   const session = await requireProductRole(ProductRole.SynqFundReferrer);
 *   const session = await requireProductRole([ProductRole.CareConnectNetworkManager, ProductRole.CareConnectReferrerAdmin]);
 */
export async function requireProductRole(role: ProductRoleValue | ProductRoleValue[]): Promise<PlatformSession> {
  const session = await requireOrg();
  const roles = Array.isArray(role) ? role : [role];
  if (!roles.some((r) => session.productRoles.includes(r))) redirect('/dashboard');
  return session;
}

/**
 * LS-ID-TNT-010 — Route-level product access guard.
 *
 * Ensures the authenticated user has access to the given product before
 * rendering any page under a product route group layout. PlatformAdmins
 * and TenantAdmins bypass the check for legacy product routes, but Xenia
 * requires explicit product access.
 *
 * For regular users, checks `session.userProducts` (JWT-derived per-user
 * list from LS-ID-TNT-009). Falls back to `session.enabledProducts`
 * (tenant-level) when userProducts is absent (e.g., legacy sessions).
 *
 * Redirects to `/access-denied` instead of `/dashboard` so the user
 * receives a clear product-access message rather than a silent redirect.
 *
 * Usage (in a product route layout.tsx server component):
 *   await requireProductAccess(FrontendProductCode.CareConnect);
 */
export async function requireProductAccess(productCode: FrontendProductCodeValue): Promise<PlatformSession> {
  const session = await requireOrg();
  if (!sessionHasProductAccess(session, productCode)) redirect('/access-denied');
  return session;
}

/**
 * Ensure the session is a TenantAdmin or PlatformAdmin.
 * Redirects to /dashboard if not.
 */
export async function requireAdmin(): Promise<PlatformSession> {
  const session = await requireSession();
  if (!session.isTenantAdmin && !session.isPlatformAdmin) redirect('/dashboard');
  return session;
}

/**
 * Ensure the session is a PlatformAdmin.
 * TenantAdmins are NOT granted access — this guard is strictly for
 * LegalSynq platform administrators operating the Control Center.
 * Redirects to /dashboard if not.
 */
export async function requirePlatformAdmin(): Promise<PlatformSession> {
  const session = await requireSession();
  if (!session.isPlatformAdmin) redirect('/dashboard');
  return session;
}

/**
 * Control Center guard — requires PlatformAdmin system role.
 *
 * This is the guard to use inside all (control-center) Server Components and layouts.
 * Unlike requirePlatformAdmin(), this guard uses CC_ACCESS_DENIED_URL for the fallback
 * redirect so it works correctly in both deployment modes:
 *
 *   Embedded (default): redirects non-admins to /dashboard (operator portal)
 *   Standalone:         redirects non-admins to CC_LOGIN_URL (same host login)
 *
 * Also redirects to CC_LOGIN_URL if no session exists, rather than the shared /login page.
 * In standalone mode CC_LOGIN_URL should point to whatever login page serves that host.
 *
 * NOTE: In standalone mode the middleware still gates on platform_session cookie existence
 * before this guard is ever reached. The CC_LOGIN_URL redirect is for the case where a
 * session exists but the user is not a PlatformAdmin.
 */
export async function requireCCPlatformAdmin(): Promise<PlatformSession> {
  const session = await getServerSession();
  if (!session) redirect(CC_LOGIN_URL);
  if (!session.isPlatformAdmin) redirect(CC_ACCESS_DENIED_URL);
  return session;
}

/**
 * Lightweight check — no redirect. Use in layouts to conditionally render UI.
 */
export async function getOptionalSession(): Promise<PlatformSession | null> {
  return getServerSession();
}

/**
 * CC2-INT-B05 — Common Portal guard.
 *
 * Requires an authenticated Identity-backed session belonging to an external
 * actor: orgType === PROVIDER or orgType === LAW_FIRM.
 *
 * - Unauthenticated → /login?returnTo={currentPath}
 * - No org → /no-org
 * - Wrong org type (tenant/platform user) → /dashboard
 *
 * Usage (Common Portal layouts and pages):
 *   const session = await requireExternalPortal(request);
 */
export async function requireExternalPortal(
  returnTo?: string,
): Promise<PlatformSession & { orgId: string }> {
  const session = await getServerSession();
  if (!session) {
    const loginUrl = returnTo
      ? `/login?returnTo=${encodeURIComponent(returnTo)}`
      : '/login';
    redirect(loginUrl);
  }
  if (!session.hasOrg) redirect('/no-org');
  if (session.orgType !== OrgType.Provider && session.orgType !== OrgType.LawFirm) {
    redirect('/dashboard');
  }
  return session as PlatformSession & { orgId: string };
}
