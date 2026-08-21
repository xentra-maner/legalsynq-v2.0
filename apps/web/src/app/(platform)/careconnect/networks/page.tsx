import { requireOrg } from '@/lib/auth-guards';
import { careConnectServerApi } from '@/lib/careconnect-server-api';
import { ServerApiError } from '@/lib/server-api-client';
import { resolveTenantFromCode } from '@/lib/public-network-api';
import { NetworkListClient } from '@/components/careconnect/network-list-client';
import { ProductRole } from '@/types';

export const dynamic = 'force-dynamic';


/**
 * /careconnect/networks — Provider Network Management
 *
 * CC2-INT-B06: Role-gated to CARECONNECT_NETWORK_MANAGER or CARECONNECT_REFERRER_ADMIN
 * (enforced by layout.tsx). Fetches the tenant's network list server-side and hands it
 * to the interactive client component for creation, editing, and deletion.
 */
export default async function NetworksPage() {
  const session = await requireOrg();
  const canManageAll =
    session.isPlatformAdmin ||
    session.isTenantAdmin ||
    session.productRoles.includes(ProductRole.CareConnectNetworkManager);

  let tenantName = session.tenantCode;
  try {
    const resolved = await resolveTenantFromCode(session.tenantCode);
    if (resolved?.displayName) tenantName = resolved.displayName;
  } catch {
    // Keep the tenant code as a fallback display name.
  }

  let networks = null;
  let fetchError: string | null = null;

  try {
    networks = await careConnectServerApi.networks.list();
  } catch (err) {
    if (err instanceof ServerApiError) {
      fetchError = err.message;
    } else {
      fetchError = 'Unable to load networks. Please try again.';
    }
  }

  return (
    <div className="space-y-5">
      <div>
        <h1 className="text-xl font-semibold text-gray-900">Provider Networks</h1>
        <p className="mt-0.5 text-sm text-gray-500">
          Create and manage groups of providers for streamlined referral workflows.
        </p>
      </div>

      {fetchError ? (
        <div className="rounded-md bg-red-50 border border-red-200 p-4 text-sm text-red-700">
          {fetchError}
        </div>
      ) : (
        <NetworkListClient
          initialNetworks={networks ?? []}
          tenantName={tenantName}
          canManageAll={canManageAll}
          callerOrgId={session.orgId ?? null}
        />
      )}
    </div>
  );
}
