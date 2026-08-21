import Link from 'next/link';
import { notFound } from 'next/navigation';
import { requireOrg } from '@/lib/auth-guards';
import { careConnectServerApi } from '@/lib/careconnect-server-api';
import { ServerApiError } from '@/lib/server-api-client';
import { resolveTenantFromCode } from '@/lib/public-network-api';
import { NetworkDetailClient } from '@/components/careconnect/network-detail-client';
import { ProductRole } from '@/types';
import type { SpecialtyOption } from '@/types/careconnect';

interface NetworkDetailPageProps {
  params: Promise<{ id: string }>;
}

/**
 * /careconnect/networks/[id] — Network Detail
 *
 * CC2-INT-B06: Role-gated by networks/layout.tsx.
 * Fetches network detail server-side; renders interactive client component
 * for provider management.
 */
export default async function NetworkDetailPage({ params }: NetworkDetailPageProps) {
  const { id } = await params;

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

  let network = null;
  let specialtyOptions: SpecialtyOption[] = [];
  let fetchError: string | null = null;

  try {
    [network, specialtyOptions] = await Promise.all([
      careConnectServerApi.networks.getById(id),
      careConnectServerApi.specialties.list(),
    ]);
  } catch (err) {
    if (err instanceof ServerApiError && err.status === 404) {
      notFound();
    }
    fetchError = err instanceof ServerApiError ? err.message : 'Unable to load network.';
  }

  if (!network) {
    return (
      <div className="space-y-4">
        <div className="rounded-md bg-red-50 border border-red-200 p-4 text-sm text-red-700">
          {fetchError}
        </div>
        <Link href="/careconnect/networks" className="inline-block text-sm text-blue-600 hover:underline">
          ← Back to Networks
        </Link>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <Link href="/careconnect/networks" className="text-sm text-blue-600 hover:underline">
        ← Back to Networks
      </Link>

      <NetworkDetailClient
        network={network}
        specialtyOptions={specialtyOptions}
        tenantName={tenantName}
        canManageAll={canManageAll}
        callerOrgId={session.orgId ?? null}
      />
    </div>
  );
}
