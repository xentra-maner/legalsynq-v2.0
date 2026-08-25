'use client';

import { useEffect, useState, use as usePromise } from 'react';
import type { ReactNode } from 'react';
import Link from 'next/link';
import { fetchRepresentativeReferralById } from '@/lib/representative-portal-api';
import { useRepresentativePortal } from '@/components/careconnect/representative-access-code-gate';
import { ApiError } from '@/lib/api-client';
import type { RepresentativeFacilityRef, RepresentativeReferralDetail } from '@/types/careconnect';

const STATUS_STYLES: Record<string, string> = {
  New: 'bg-orange-50 text-orange-700 ring-orange-200',
  NewOpened: 'bg-orange-50 text-orange-700 ring-orange-200',
  Accepted: 'bg-blue-50 text-blue-700 ring-blue-200',
  InProgress: 'bg-blue-50 text-blue-700 ring-blue-200',
  Completed: 'bg-emerald-50 text-emerald-700 ring-emerald-200',
  Declined: 'bg-red-50 text-red-700 ring-red-200',
  Cancelled: 'bg-gray-100 text-gray-700 ring-gray-200',
};

function formatDate(value?: string | null): string {
  if (!value) return 'Not available';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return 'Not available';
  return date.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
}

function formatProviderLocation(location: RepresentativeFacilityRef): string {
  if (location.isMobile) {
    const parts = [location.serviceAreaLabel ?? location.addressLine1, `${location.city}, ${location.state}`]
      .filter(Boolean)
      .join(' - ');
    return location.serviceRadiusMiles
      ? `Mobile - ${parts} - ${location.serviceRadiusMiles}mi radius`
      : `Mobile - ${parts}`;
  }
  return [location.addressLine1, `${location.city}, ${location.state}`, location.postalCode]
    .filter(Boolean)
    .join(' ');
}

function patientName(referral: RepresentativeReferralDetail): string {
  return [referral.client.firstName, referral.client.lastName].filter(Boolean).join(' ') || 'Unnamed patient';
}

function Field({ label, value }: { label: string; value?: ReactNode }) {
  return (
    <div>
      <dt className="text-xs font-medium uppercase tracking-wide text-gray-400">{label}</dt>
      <dd className="mt-1 text-sm text-gray-900">{value || 'Not available'}</dd>
    </div>
  );
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="rounded-lg border border-gray-200 bg-white">
      <div className="border-b border-gray-100 px-5 py-4">
        <h2 className="text-sm font-semibold text-gray-900">{title}</h2>
      </div>
      <dl className="grid gap-x-6 gap-y-5 px-5 py-5 sm:grid-cols-2">{children}</dl>
    </section>
  );
}

interface PageProps {
  params: Promise<{ id: string }>;
}

export default function RepresentativeReferralDetailPage({ params }: PageProps) {
  const { id } = usePromise(params);
  const { code } = useRepresentativePortal();
  const [referral, setReferral] = useState<RepresentativeReferralDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [notFound, setNotFound] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setLoading(true);
    fetchRepresentativeReferralById(code, id)
      .then(({ data }) => {
        setReferral(data);
        setNotFound(false);
        setError(null);
      })
      .catch(err => {
        if (err instanceof ApiError && err.status === 404) {
          setNotFound(true);
        } else {
          setError(err instanceof ApiError ? err.message : 'Failed to load referral.');
        }
      })
      .finally(() => setLoading(false));
  }, [code, id]);

  return (
    <div className="space-y-5">
      <Link href="/careconnect/referral/referrals" className="inline-flex items-center gap-1 text-sm text-gray-500 hover:text-gray-800">
        <i className="ri-arrow-left-line" aria-hidden="true" /> Back to My Referrals
      </Link>

      {loading && <p className="text-sm text-gray-500">Loading referral...</p>}

      {notFound && (
        <div className="rounded-xl border border-gray-200 bg-white p-12 text-center">
          <h3 className="mb-1 text-base font-semibold text-gray-900">Referral not found</h3>
          <p className="text-sm text-gray-500">This referral does not exist, or is not attributed to you.</p>
        </div>
      )}

      {error && (
        <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">{error}</div>
      )}

      {referral && (
        <>
          <div className="rounded-lg border border-gray-200 bg-white p-5">
            <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
              <div>
                <div className="flex flex-wrap items-center gap-2">
                  <h1 className="text-2xl font-semibold text-gray-900">{patientName(referral)}</h1>
                  <span className={`inline-flex rounded px-2 py-0.5 text-xs font-medium ring-1 ${STATUS_STYLES[referral.status.code] ?? 'bg-gray-100 text-gray-700 ring-gray-200'}`}>
                    {referral.status.displayName}
                  </span>
                </div>
                <p className="mt-1 text-sm text-gray-500">
                  {referral.referenceNumber} - submitted {formatDate(referral.submittedAtUtc)}
                </p>
              </div>
              <div className="grid grid-cols-2 gap-3 text-sm sm:min-w-80">
                <div className="rounded-md bg-gray-50 px-3 py-2">
                  <p className="text-xs font-medium uppercase tracking-wide text-gray-400">Law Firm</p>
                  <p className="mt-1 font-medium text-gray-900">{referral.lawFirm.displayName}</p>
                </div>
                <div className="rounded-md bg-gray-50 px-3 py-2">
                  <p className="text-xs font-medium uppercase tracking-wide text-gray-400">Last Updated</p>
                  <p className="mt-1 font-medium text-gray-900">{formatDate(referral.lastUpdatedAtUtc)}</p>
                </div>
              </div>
            </div>
          </div>

          <div className="grid gap-5 lg:grid-cols-[minmax(0,1fr)_320px]">
            <div className="space-y-5">
              <Section title="Patient Information">
                <Field label="Name" value={patientName(referral)} />
                <Field label="Date of Birth" value={formatDate(referral.client.dateOfBirth)} />
                <Field label="Phone" value={referral.client.phone} />
                <Field label="Email" value={referral.client.email} />
              </Section>

              <Section title="Provider Routing">
                <Field label="Provider" value={referral.provider.displayName} />
                <Field
                  label="Provider Location"
                  value={referral.providerLocation ? formatProviderLocation(referral.providerLocation) : undefined}
                />
                <Field label="Law Firm" value={referral.lawFirm.displayName} />
                <Field
                  label="Referral Origination"
                  value={`${referral.referralAttribution.firstName} ${referral.referralAttribution.lastName}`.trim()}
                />
              </Section>
            </div>

            <aside className="space-y-5">
              <section className="rounded-lg border border-gray-200 bg-white">
                <div className="border-b border-gray-100 px-5 py-4">
                  <h2 className="text-sm font-semibold text-gray-900">Referral Summary</h2>
                </div>
                <dl className="space-y-4 px-5 py-5">
                  <Field label="Reference Number" value={referral.referenceNumber} />
                  <Field label="Current Status" value={referral.status.displayName} />
                  <Field label="Submitted" value={formatDate(referral.submittedAtUtc)} />
                </dl>
              </section>

              <section className="rounded-lg border border-gray-200 bg-white">
                <div className="border-b border-gray-100 px-5 py-4">
                  <h2 className="text-sm font-semibold text-gray-900">Status Timeline</h2>
                </div>
                {referral.milestones.length > 0 ? (
                  <ol className="space-y-4 px-5 py-5">
                    {referral.milestones.map((milestone, index) => (
                      <li key={`${milestone.code}-${index}`} className="flex gap-3">
                        <span className="mt-1 h-2 w-2 rounded-full bg-primary" />
                        <div>
                          <p className="text-sm font-medium text-gray-900">{milestone.displayName}</p>
                          <p className="text-xs text-gray-500">{formatDate(milestone.occurredAtUtc)}</p>
                        </div>
                      </li>
                    ))}
                  </ol>
                ) : (
                  <p className="px-5 py-5 text-sm text-gray-500">No timeline entries available.</p>
                )}
              </section>
            </aside>
          </div>
        </>
      )}
    </div>
  );
}
