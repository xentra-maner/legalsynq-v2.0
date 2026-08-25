/**
 * Tenant administration of Referral Origination options.
 * Route: /careconnect/referral-attributions
 *
 * Server component gate (admin only) wrapping a client component that owns
 * its own data fetching/mutations against the tenant-scoped attribution API.
 */

import { requireAdmin } from '@/lib/auth-guards';
import { ReferralAttributionsAdmin } from '@/components/careconnect/referral-attributions-admin';

export const dynamic = 'force-dynamic';

export default async function ReferralAttributionsPage() {
  await requireAdmin();

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-xl font-semibold text-gray-900">Referral Originations</h1>
          <p className="text-sm text-gray-500 mt-0.5">
            Configure who or what can be selected as a referral&apos;s origin — representatives, campaigns, or partners.
          </p>
        </div>
      </div>

      <ReferralAttributionsAdmin />
    </div>
  );
}
