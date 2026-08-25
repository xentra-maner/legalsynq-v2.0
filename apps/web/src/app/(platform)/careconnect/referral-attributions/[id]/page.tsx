/**
 * Referral Origination detail.
 * Route: /careconnect/referral-attributions/{id}
 *
 * Server component gate (admin only, same as the list page) wrapping a client
 * component that owns the read view, the Edit toggle, and the access-code
 * widget (generate/revoke — folded in here after the standalone Referral
 * Representatives page was retired, since a code is 1:1 with its origination).
 */

import Link from 'next/link';
import { requireAdmin } from '@/lib/auth-guards';
import { ReferralAttributionDetail } from '@/components/careconnect/referral-attribution-detail';

export const dynamic = 'force-dynamic';

interface ReferralAttributionDetailPageProps {
  params: Promise<{ id: string }>;
}

export default async function ReferralAttributionDetailPage({ params }: ReferralAttributionDetailPageProps) {
  await requireAdmin();
  const { id } = await params;

  return (
    <div className="space-y-4">
      <nav className="flex items-center justify-between">
        <Link
          href="/careconnect/referral-attributions"
          className="text-sm text-gray-500 hover:text-gray-800 transition-colors"
        >
          ← Back to Referral Originations
        </Link>
      </nav>

      <ReferralAttributionDetail id={id} />
    </div>
  );
}
