import { notFound } from 'next/navigation';
import Link from 'next/link';
import { requireOrg } from '@/lib/auth-guards';
import { ProductRole } from '@/types';
import { checkCareConnectReceiverAccess } from '@/lib/careconnect-access';
import { careConnectServerApi } from '@/lib/careconnect-server-api';
import { ServerApiError } from '@/lib/server-api-client';
import { resolveReferralDetailBack } from '@/lib/referral-nav';
import { ReferralPageHeader } from '@/components/careconnect/referral-page-header';
import { ReferralDetailPanel } from '@/components/careconnect/referral-detail-panel';
import { ReferralDeliveryCard } from '@/components/careconnect/referral-delivery-card';
import { ReferralStatusActions } from '@/components/careconnect/referral-status-actions';
import { ReferralTimeline } from '@/components/careconnect/referral-timeline';
import { ReferralAuditTimeline } from '@/components/careconnect/referral-audit-timeline';
import { ReferralAccessBlocked } from '@/components/careconnect/referral-access-blocked';
import { AttachmentPanel } from '@/components/careconnect/attachment-panel';
import { ReferralMessageThread } from '@/components/careconnect/referral-message-thread';
import type { ReferralComment } from '@/types/careconnect';

interface ReferralDetailPageProps {
  params:       Promise<{ id: string }>;
  searchParams: Promise<{
    from?:        string;
    status?:      string;
    search?:      string;
    createdFrom?: string;
    createdTo?:   string;
  }>;
}

export default async function ReferralDetailPage({ params, searchParams }: ReferralDetailPageProps) {
  const { id } = await params;
  const session = await requireOrg();

  const hasReferrerRole = session.productRoles.includes(ProductRole.CareConnectReferrer);
  const hasReceiverRole = session.productRoles.includes(ProductRole.CareConnectReceiver);
  const isTenantAdminView = session.isTenantAdmin && !session.isPlatformAdmin;

  // LSCC-01-002-02: Enforce the admin-controlled access model.
  // Only users with a CareConnect role may enter the referral flow.
  // Referral details are NOT rendered in the blocked state.
  if (!hasReferrerRole && !hasReceiverRole && !isTenantAdminView) {
    const readiness = checkCareConnectReceiverAccess(session);
    return <ReferralAccessBlocked reason={readiness.reason} />;
  }

  let referral = null;
  let fetchError: string | null = null;
  let comments: ReferralComment[] = [];
  let commentsError: string | null = null;

  try {
    referral = isTenantAdminView
      ? await careConnectServerApi.adminDashboard.getReferralById(id)
      : await careConnectServerApi.referrals.getById(id);
  } catch (err) {
    if (err instanceof ServerApiError) {
      if (err.isNotFound) notFound();
      if (err.isForbidden) {
        fetchError = 'You do not have access to this referral. Your organization is not a participant.';
      } else {
        fetchError = err.message;
      }
    } else {
      fetchError = 'Failed to load referral.';
    }
  }

  if (referral && !isTenantAdminView) {
    try {
      comments = await careConnectServerApi.referrals.getComments(id);
    } catch (err) {
      if (err instanceof ServerApiError) {
        commentsError = err.isForbidden
          ? 'You do not have access to this referral conversation.'
          : err.message;
      } else {
        commentsError = 'Failed to load messages.';
      }
    }
  }

  const resolvedSearchParams = await searchParams;
  const { href: backHref, label: backLabel } = resolveReferralDetailBack(resolvedSearchParams);

  return (
    <div className="space-y-4">
      {/* Back navigation */}
      <nav className="flex items-center justify-between">
        <Link
          href={backHref}
          className="text-sm text-gray-500 hover:text-gray-800 transition-colors"
        >
          {backLabel}
        </Link>
      </nav>

      {fetchError && (
        <div className="bg-red-50 border border-red-200 rounded-lg px-4 py-3 text-sm text-red-700">
          {fetchError}
        </div>
      )}

      {referral && (() => {
        // Standard org-based participant check.
        // CC-REFERRER-EMAIL: also treat the user as the referrer when the referral was
        // submitted publicly (no org) but their email matches — covers law firms that
        // activated their portal after sending public referrals.
        const isReferrerOfReferral = hasReferrerRole && (
          (!!session.orgId && referral.referringOrganizationId === session.orgId) ||
          (!referral.referringOrganizationId &&
           !!session.email &&
           referral.referrerEmail?.toLowerCase() === session.email.toLowerCase())
        );
        const isReceiverOfReferral = hasReceiverRole && !!session.orgId
          && referral.receivingOrganizationId === session.orgId;
        return <>
          {/* 1. Header — identity + prominent status */}
          <ReferralPageHeader referral={referral} />

          {/* 2. Primary action area */}
          {!isTenantAdminView && (
            <ReferralStatusActions
              referral={referral}
              isReceiver={isReceiverOfReferral}
              isReferrer={isReferrerOfReferral}
            />
          )}

          {/* LSCC-01-001-01: Book appointment prompt removed.
              Appointment scheduling is decoupled from referral status.
              Referrers can book via the provider availability page at any time. */}

          {/* 3. Referral details — body only (header rendered above). Treatment type editing is
              inline. Referral Origination is shown here (read-only) — it's set only once, at
              law firm submission time, and is immutable afterward; there is no admin edit
              control for it. */}
          <ReferralDetailPanel referral={referral} hideHeader />

          {/* 3b. Documents — CC2-INT-B03 */}
          <AttachmentPanel
            entityType="referral"
            entityId={referral.id}
            canUpload={!isTenantAdminView && (session.isPlatformAdmin || session.isTenantAdmin)}
            readOnly={isTenantAdminView || (!session.isPlatformAdmin && !session.isTenantAdmin)}
            adminReferralView={isTenantAdminView}
          />

          {!isTenantAdminView && (
            <ReferralMessageThread
              referralId={referral.id}
              initialComments={comments}
              initialError={commentsError}
            />
          )}

          {/* 4. Delivery / access controls — referrers only */}
          {!isTenantAdminView && isReferrerOfReferral && <ReferralDeliveryCard referral={referral} />}

          {/* 5. Operational audit timeline */}
          <ReferralAuditTimeline referralId={referral.id} />

          {/* 5b. Activity / status history — all roles */}
          <div className="bg-white border border-gray-200 rounded-lg px-5 py-4">
            <h3 className="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-4">
              Activity
            </h3>
            <ReferralTimeline referralId={referral.id} adminView={isTenantAdminView} />
          </div>
        </>;
      })()}
    </div>
  );
}
