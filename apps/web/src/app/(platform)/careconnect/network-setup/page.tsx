import { requireOrg }              from '@/lib/auth-guards';
import { careConnectServerApi }    from '@/lib/careconnect-server-api';
import { ServerApiError }          from '@/lib/server-api-client';
import { MyNetworkClient }         from '@/components/careconnect/my-network-client';
import { resolveTenantFromCode }   from '@/lib/public-network-api';
import { ProductRole }             from '@/types';
import type { NetworkDetail, SpecialtyOption } from '@/types/careconnect';

export const dynamic = 'force-dynamic';

/**
 * /careconnect/network-setup — Law Firm provider network self-management.
 *
 * Single-tenant-network cutover: law firms (CareConnectReferrerAdmin) add and
 * manage their own providers directly in the tenant's one shared provider
 * network, instead of creating/owning a separate network of their own. This
 * is a distinct screen from the tenant portal's "My Network"
 * (careconnect/my-network — NetworkManager/admin audience) so that page's
 * behavior stays unchanged for its existing users. Reuses MyNetworkClient
 * (same list/cards/map views, Add Provider modal, edit modal, visibility
 * badges) mounted under a law-firm-facing label and route, scoped to providers
 * owned by the caller's organization.
 */
export default async function NetworkSetupPage() {
  const session = await requireOrg();
  // 4-AC3: only a Tenant/Platform Admin may toggle a provider's Public/Private
  // visibility — a law firm's ReferrerAdmin cannot.
  const canManageVisibility = session.isPlatformAdmin || session.isTenantAdmin;
  const canManageAll =
    session.isPlatformAdmin ||
    session.isTenantAdmin ||
    session.productRoles.includes(ProductRole.CareConnectNetworkManager);
  // A plain CareConnectReferrerAdmin (law firm) may add providers to the shared
  // network — they default to Private (ResolveVisibility) and the caller can
  // only edit/remove providers their own organization added.
  const canAddProviders =
    canManageAll || session.productRoles.includes(ProductRole.CareConnectReferrerAdmin);

  let tenantName = session.tenantCode;
  try {
    const resolved = await resolveTenantFromCode(session.tenantCode);
    if (resolved?.displayName) tenantName = resolved.displayName;
  } catch {
    // Keep the tenant code as a fallback display name.
  }

  let network: NetworkDetail | null = null;
  let fetchError: string | null = null;
  let specialtyOptions: SpecialtyOption[] = [];

  try {
    const networks = await careConnectServerApi.networks.list();
    if (networks.length > 0) {
      network = await careConnectServerApi.networks.getById(networks[0].id);
    }
  } catch (err) {
    if (err instanceof ServerApiError && err.status !== 404) {
      fetchError = err.message;
    } else if (!(err instanceof ServerApiError)) {
      fetchError = 'Unable to load your network. Please try again.';
    }
  }

  try {
    specialtyOptions = await careConnectServerApi.specialties.list();
  } catch {
    specialtyOptions = [];
  }

  return (
    <div className="space-y-5">
      <MyNetworkClient
        initialNetwork={network}
        fetchError={fetchError}
        specialtyOptions={specialtyOptions}
        tenantName={tenantName}
        headerLabel="Network Setup"
        canManageAll={canManageAll}
        canManageVisibility={canManageVisibility}
        canAddProviders={canAddProviders}
        callerOrgId={session.orgId ?? null}
        showOnlyCallerOrgProviders
        showNetworkUrl={false}
      />
    </div>
  );
}
