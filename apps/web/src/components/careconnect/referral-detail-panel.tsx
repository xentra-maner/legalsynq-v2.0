'use client';

import type { ReferralDetail } from '@/types/careconnect';
import { useBrowserTimezone } from '@/lib/use-timezone';
import { StatusBadge, UrgencyBadge } from './status-badge';
import { formatPhoneDisplay } from '@/lib/phone';
import { formatDateOnly } from '@/lib/format-date';
import { formatReferralLocation } from '@/lib/referral-location';

interface ReferralDetailPanelProps {
  referral:    ReferralDetail;
  hideHeader?: boolean;
  timezone?:   string;
}

function Field({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div>
      <dt className="text-xs font-medium text-gray-500 uppercase tracking-wide">{label}</dt>
      <dd className="mt-1 text-sm text-gray-900">{value ?? '—'}</dd>
    </div>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="border-t border-gray-100 pt-5 mt-5 first:border-0 first:pt-0 first:mt-0">
      <h3 className="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-4">{title}</h3>
      <dl className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-x-6 gap-y-5">
        {children}
      </dl>
    </section>
  );
}

function formatDate(iso: string | undefined, timezone: string): string {
  if (!iso) return '—';
  return new Date(iso).toLocaleDateString('en-US', {
    month:    'long',
    day:      'numeric',
    year:     'numeric',
    timeZone: timezone,
  });
}

function formatDateOnlyField(iso: string | undefined): string {
  if (!iso) return '—';
  return formatDateOnly(iso, {
    month: 'long',
    day: 'numeric',
    year: 'numeric',
  });
}

export function ReferralDetailPanel({ referral, hideHeader = false, timezone }: ReferralDetailPanelProps) {
  const browserTimezone = useBrowserTimezone();
  const resolvedTimezone = timezone ?? browserTimezone;

  return (
    <div className="bg-white border border-gray-200 rounded-lg">
      {/* Header — omitted when used alongside ReferralPageHeader */}
      {!hideHeader && (
        <div className="px-6 py-5 border-b border-gray-100 flex items-start justify-between gap-4">
          <div>
            <h2 className="text-lg font-semibold text-gray-900">
              {referral.clientFirstName} {referral.clientLastName}
            </h2>
            {referral.caseNumber && (
              <p className="text-sm text-gray-500 mt-0.5">Case #{referral.caseNumber}</p>
            )}
          </div>
          <div className="flex items-center gap-2 shrink-0">
            <UrgencyBadge urgency={referral.urgency} />
            <StatusBadge status={referral.status} size="md" />
          </div>
        </div>
      )}

      {/* Body */}
      <div className="px-6 py-5 space-y-0">
        {/* Referral */}
        <Section title="Referral">
          <Field label="Provider"            value={referral.providerName} />
          <Field label="Provider Location"   value={formatReferralLocation(referral)} />
          <Field label="Requested service"  value={referral.requestedService} />
          <Field label="Origin"             value={referral.origin === 'ReferralAssociate' ? 'Referral Associate' : 'Law Firm'} />
          <Field label="Urgency"            value={<UrgencyBadge urgency={referral.urgency} />} />
          <Field label="Date of accident"   value={referral.dateOfAccident ? formatDateOnlyField(referral.dateOfAccident) : undefined} />
          <Field label="Type of treatment"  value={referral.treatmentTypeName ?? '—'} />
          <Field
            label="Referral Attribution"
            value={referral.referralAttribution?.firstName
              ? `${referral.referralAttribution.firstName} ${referral.referralAttribution.lastName}`
              : undefined}
          />
          <Field label="Status"             value={<StatusBadge status={referral.status} />} />
          <Field label="Created"            value={formatDate(referral.createdAtUtc, resolvedTimezone)} />
          <Field label="Last updated"       value={formatDate(referral.updatedAtUtc, resolvedTimezone)} />
        </Section>

        {(referral.lienCompanyName || referral.lienCompanyEmail) && (
          <Section title="Lien Company">
            <Field label="Name" value={referral.lienCompanyName} />
            <Field label="Email" value={referral.lienCompanyEmail} />
          </Section>
        )}

        {/* Client / Subject party */}
        <Section title="Client">
          <Field label="Name"  value={`${referral.clientFirstName} ${referral.clientLastName}`} />
          <Field label="DOB"   value={referral.clientDob ? formatDateOnlyField(referral.clientDob) : undefined} />
          <Field label="Phone" value={formatPhoneDisplay(referral.clientPhone)} />
          <Field label="Email" value={referral.clientEmail} />
        </Section>

        {/* Notes */}
        {referral.notes && (
          <Section title="Notes">
            <div className="col-span-full">
              <p className="text-sm text-gray-700 whitespace-pre-wrap">{referral.notes}</p>
            </div>
          </Section>
        )}
      </div>
    </div>
  );
}
