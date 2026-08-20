import { headers } from 'next/headers';
import { resolveTenantFromCode, isProductActiveForTenant } from '@/lib/public-network-api';
import { RepresentativeAccessCodeGate } from '@/components/careconnect/representative-access-code-gate';
import { PublicNetworkShell } from '@/components/careconnect/public-network-shell';
import { RepresentativeShell } from '@/components/shell/representative-shell';
import { ToastProvider } from '@/lib/toast-context';
import { ToastContainer } from '@/components/toast-container';

export const dynamic = 'force-dynamic';

/**
 * Referral Representative Portal layout — wraps all /careconnect/representative/* routes.
 *
 * Lives as a top-level sibling of /careconnect/network (NOT under app/(platform)/careconnect,
 * which carries requireProductAccess + a login-gated layout) — same trick that page uses to
 * stay anonymous under the shared /careconnect URL prefix: Next.js layouts only wrap pages
 * physically nested inside their own folder, and (platform) is a route group that contributes
 * a separate physical tree, so its guard never touches this one.
 *
 * Fully anonymous: no login, no platform session, no product-role gate. Tenant is resolved
 * from the subdomain exactly like /careconnect/network. The access code is the sole
 * credential — RepresentativeAccessCodeGate collects it and every page under it resends the
 * raw code on every data request, re-verified server-side each time (see
 * PublicRepresentativeEndpoints). There is nothing here to trust from the gate itself; it is
 * a UX convenience, not the authorization boundary.
 */
export default async function RepresentativePortalLayout({ children }: { children: React.ReactNode }) {
  const hdrs = await headers();
  const host = hdrs.get('x-forwarded-host') ?? hdrs.get('host') ?? '';

  const parts = host.split('.');
  const tenantCode = parts.length >= 3 ? parts[0] : (process.env.NEXT_PUBLIC_TENANT_CODE ?? '');
  const tenant = tenantCode ? await resolveTenantFromCode(tenantCode) : null;

  if (!tenant) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-gray-50">
        <p className="text-sm text-gray-500">Representative portal not found.</p>
      </div>
    );
  }

  const careConnectActive = await isProductActiveForTenant(tenant.tenantId, 'synq_careconnect');
  if (!careConnectActive) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-gray-50">
        <p className="text-sm text-gray-500">This portal is currently unavailable.</p>
      </div>
    );
  }

  return (
    <ToastProvider>
      <PublicNetworkShell tenantId={tenant.tenantId}>
        <RepresentativeAccessCodeGate tenantId={tenant.tenantId}>
          <RepresentativeShell>{children}</RepresentativeShell>
        </RepresentativeAccessCodeGate>
      </PublicNetworkShell>
      <ToastContainer />
    </ToastProvider>
  );
}
