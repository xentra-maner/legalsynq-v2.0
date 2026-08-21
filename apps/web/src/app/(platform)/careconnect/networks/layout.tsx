import { requireProductRole } from '@/lib/auth-guards';
import { ProductRole } from '@/types';

export const dynamic = 'force-dynamic';


/**
 * CC2-INT-B06 / LSV3-1084 — Network Management route guard.
 *
 * Only users with the CARECONNECT_NETWORK_MANAGER or CARECONNECT_REFERRER_ADMIN
 * product role may access /careconnect/networks/* pages. PlatformAdmins and
 * TenantAdmins also pass because requireProductRole calls requireOrg which
 * reads the full session, but the role guard itself redirects to /dashboard
 * for everyone else.
 *
 * This is role-based (not orgType-based): any orgType can hold these roles.
 */
export default async function NetworksLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  await requireProductRole([ProductRole.CareConnectNetworkManager, ProductRole.CareConnectReferrerAdmin]);
  return <>{children}</>;
}
