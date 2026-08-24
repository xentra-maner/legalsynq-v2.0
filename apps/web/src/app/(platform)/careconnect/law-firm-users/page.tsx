import { requireOrg }              from '@/lib/auth-guards';
import { careConnectServerApi }    from '@/lib/careconnect-server-api';
import { ServerApiError }          from '@/lib/server-api-client';
import { LawFirmUsersClient }      from '@/components/careconnect/law-firm-users-client';
import { ProductRole }             from '@/types';
import type { LawFirmUserSummary } from '@/types/careconnect';

export const dynamic = 'force-dynamic';

/**
 * /careconnect/law-firm-users — LSV3-1083: Law Firm Company Super Admin/Manager.
 *
 * A CareConnectReferrerAdmin (the law firm's designated super admin/manager) views
 * and manages the users belonging to their own firm: invite, activate/deactivate,
 * and assign/revoke CareConnect roles. Visibility is enforced server-side (the
 * `/api/law-firm-users` endpoints always scope to the caller's own organization),
 * so this page does not attempt its own redirect when the role is absent — the
 * nav item is hidden and the API 403s, matching the network-setup page's pattern.
 */
export default async function LawFirmUsersPage() {
  const session = await requireOrg();
  const isReferrerAdmin = session.productRoles.includes(ProductRole.CareConnectReferrerAdmin);

  let users: LawFirmUserSummary[] = [];
  let fetchError: string | null = null;

  if (isReferrerAdmin || session.isPlatformAdmin || session.isTenantAdmin) {
    try {
      users = await careConnectServerApi.lawFirmUsers.list();
    } catch (err) {
      if (err instanceof ServerApiError && err.status !== 404) {
        fetchError = err.message;
      } else if (!(err instanceof ServerApiError)) {
        fetchError = 'Unable to load your firm’s users. Please try again.';
      }
    }
  }

  return (
    <div className="space-y-5">
      <LawFirmUsersClient initialUsers={users} fetchError={fetchError} />
    </div>
  );
}
