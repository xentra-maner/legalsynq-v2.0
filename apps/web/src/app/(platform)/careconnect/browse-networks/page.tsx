import { redirect }             from 'next/navigation';
import { requireProductRole }    from '@/lib/auth-guards';
import { careConnectServerApi }  from '@/lib/careconnect-server-api';
import {
  fetchPublicNetworks,
  isProductActiveForTenant,
  type PublicNetworkSummary,
} from '@/lib/public-network-api';
import { ServerApiError }        from '@/lib/server-api-client';
import { BrowseNetworksClient }  from '@/components/careconnect/browse-networks-client';
import { ProductRole, OrgType }  from '@/types';

export const dynamic = 'force-dynamic';

/**
 * /careconnect/browse-networks — Available provider networks directory.
 * CC-REFERRER-BROWSE: Accessible only to elevated law firm referrers
 * (CareConnectReferrer role). Network managers (lien companies) are
 * redirected to the dashboard — they manage networks, they don't browse them.
 */
export default async function BrowseNetworksPage() {
  // Requires CareConnectReferrer role; redirects to /dashboard if absent.
  const session = await requireProductRole(ProductRole.CareConnectReferrer);

  // Lien company users must never access browse-networks, regardless of which
  // product roles their account carries. NetworkManager is the typical case;
  // the OrgType check covers any edge case where a lien-owner user only holds
  // CareConnectReferrer (e.g. during a partial provisioning state).
  if (
    session.productRoles.includes(ProductRole.CareConnectNetworkManager) ||
    session.orgType === OrgType.LienOwner
  ) redirect('/careconnect/dashboard');

  let tenantNetworkGroups: Array<{
    tenantId: string;
    tenantCode: string;
    tenantName: string;
    networks: PublicNetworkSummary[];
  }> = [];
  let fetchError: string | null = null;

  try {
    const assignments = await careConnectServerApi.access.getMyProductAccess('SYNQ_CARECONNECT');
    const careConnectTenants = assignments
      .filter(item => item.accessStatus === 'Granted')
      .map(item => ({
        tenantId: item.tenantId,
        tenantCode: item.tenantCode,
        tenantName: item.tenantName,
        // The law firm's own organization for this tenant assignment — passed
        // through to the network list so the firm's own (non-tenant-owned)
        // network is included alongside tenant-owned networks, not just those.
        organizationId: item.organizationId ?? undefined,
      }))
      .filter((item, index, arr) => arr.findIndex(x => x.tenantId === item.tenantId) === index);

    // Exclude tenants whose CareConnect product entitlement is inactive.
    const activeChecks = await Promise.all(
      careConnectTenants.map(t => isProductActiveForTenant(t.tenantId, 'synq_careconnect')),
    );
    const activeTenants = careConnectTenants.filter((_, i) => activeChecks[i]);

    const networksByTenant = new Map(
      await Promise.all(
        activeTenants.map(async tenant =>
          [tenant.tenantId, await fetchPublicNetworks(tenant.tenantId, tenant.organizationId)] as const,
        ),
      ),
    );

    tenantNetworkGroups = activeTenants
      .map(tenant => ({
        ...tenant,
        networks: networksByTenant.get(tenant.tenantId) ?? [],
      }))
      .filter(group => group.networks.length > 0);
  } catch (err) {
    if (err instanceof ServerApiError) {
      fetchError = err.message;
    } else {
      fetchError = 'Unable to load networks. Please try again.';
    }
  }

  if (fetchError) {
    return (
      <div className="flex items-start gap-3 rounded-xl border border-red-200 bg-red-50 p-5 text-sm text-red-700">
        <i className="ri-error-warning-line text-lg text-red-500 mt-0.5" />
        <div>
          <p className="font-medium">Unable to load networks</p>
          <p className="text-red-600/90 mt-0.5">{fetchError}</p>
        </div>
      </div>
    );
  }

  if (tenantNetworkGroups.length === 0) {
    return (
      <div className="flex flex-col items-center justify-center rounded-xl border border-dashed border-gray-200 bg-gray-50/50 py-24 text-center">
        <div className="flex h-14 w-14 items-center justify-center rounded-full bg-orange-50 mb-4">
          <i className="ri-share-circle-line text-3xl text-orange-300" />
        </div>
        <p className="text-sm font-medium text-gray-500">No provider networks are available for your CareConnect assignments yet.</p>
        <p className="text-xs text-gray-400 mt-1">Check back later or contact your coordinator.</p>
      </div>
    );
  }

  return <BrowseNetworksClient tenantNetworkGroups={tenantNetworkGroups} />;
}
