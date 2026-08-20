'use client';

import { useEffect, useState, use as usePromise } from 'react';
import Link from 'next/link';
import { fetchRepresentativeReferralById } from '@/lib/representative-portal-api';
import { useRepresentativePortal } from '@/components/careconnect/representative-access-code-gate';
import { ApiError } from '@/lib/api-client';
import type { RepresentativeFacilityRef, RepresentativeReferralDetail } from '@/types/careconnect';

function formatProviderLocation(location: RepresentativeFacilityRef): string {
  if (location.isMobile) {
    const parts = [location.serviceAreaLabel ?? location.addressLine1, `${location.city}, ${location.state}`]
      .filter(Boolean)
      .join(' · ');
    return location.serviceRadiusMiles
      ? `Mobile · ${parts} · ${location.serviceRadiusMiles}mi radius`
      : `Mobile · ${parts}`;
  }
  return [location.addressLine1, `${location.city}, ${location.state}`, location.postalCode]
    .filter(Boolean)
    .join(' ');
}

function Field({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div>
      <dt className="text-xs font-medium text-gray-500 uppercase tracking-wide">{label}</dt>
      <dd className="mt-1 text-sm text-gray-900">{value ?? '—'}</dd>
    </div>
  );
}

interface PageProps {
  params: Promise<{ id: string }>;
}

/**
 * Restricted, read-only referral detail — renders only RepresentativeReferralDetail
 * fields returned by the backend's dedicated representative DTO. There is no admin
 * ReferralResponse anywhere in this component's data path, so there is no client-side
 * data to accidentally leak — the server never sends it.
 */
export default function RepresentativeReferralDetailPage({ params }: PageProps) {
  const { id } = usePromise(params);
  const { code } = useRepresentativePortal();
  const [referral, setReferral] = useState<RepresentativeReferralDetail | null>(null);
  const [loading, setLoading]   = useState(true);
  const [notFound, setNotFound] = useState(false);
  const [error, setError]       = useState<string | null>(null);

  useEffect(() => {
    setLoading(true);
    fetchRepresentativeReferralById(code, id)
      .then(({ data }) => { setReferral(data); setNotFound(false); setError(null); })
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
    <div className="space-y-4">
      <Link href="/careconnect/referral/referrals" className="cursor-pointer text-sm text-gray-500 hover:text-gray-800 transition-colors">
        ← Back to My Referrals
      </Link>

      {loading && <p className="text-sm text-gray-500">Loading…</p>}

      {notFound && (
        <div className="bg-white rounded-xl border border-gray-200 p-12 text-center">
          <h3 className="text-base font-semibold text-gray-900 mb-1">Referral not found</h3>
          <p className="text-sm text-gray-500">
            This referral doesn&apos;t exist, or isn&apos;t attributed to you.
          </p>
        </div>
      )}

      {error && (
        <div className="bg-red-50 border border-red-200 rounded-lg px-4 py-3 text-sm text-red-700">{error}</div>
      )}

      {referral && (
        <div className="bg-white border border-gray-200 rounded-lg">
          <div className="px-6 py-5 border-b border-gray-100 flex items-start justify-between gap-4">
            <div>
              <h2 className="text-lg font-semibold text-gray-900">{referral.referenceNumber}</h2>
              <p className="text-sm text-gray-500 mt-0.5">
                {referral.referralAttribution.firstName} {referral.referralAttribution.lastName}
              </p>
            </div>
            <span className="inline-flex items-center px-2.5 py-1 rounded text-xs font-medium bg-gray-100 text-gray-700 shrink-0">
              {referral.status.displayName}
            </span>
          </div>

          <div className="px-6 py-5">
            <dl className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-x-6 gap-y-5">
              <Field label="Reference number" value={referral.referenceNumber} />
              <Field label="Submitted" value={new Date(referral.submittedAtUtc).toLocaleDateString()} />
              <Field label="Status" value={referral.status.displayName} />
              <Field label="Law firm" value={referral.lawFirm.displayName} />
              <Field label="Provider" value={referral.provider.displayName} />
              <Field
                label="Provider location"
                value={referral.providerLocation ? formatProviderLocation(referral.providerLocation) : null}
              />
              <Field label="Last updated" value={new Date(referral.lastUpdatedAtUtc).toLocaleDateString()} />
            </dl>

            <div className="mt-6 pt-5 border-t border-gray-100">
              <h3 className="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-3">Client</h3>
              <dl className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-x-6 gap-y-5">
                <Field label="Name" value={`${referral.client.firstName} ${referral.client.lastName}`.trim()} />
                <Field
                  label="Date of birth"
                  value={referral.client.dateOfBirth ? new Date(referral.client.dateOfBirth).toLocaleDateString() : null}
                />
                <Field label="Phone" value={referral.client.phone} />
                <Field label="Email" value={referral.client.email} />
              </dl>
            </div>

            {referral.milestones.length > 0 && (
              <div className="mt-6 pt-5 border-t border-gray-100">
                <h3 className="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-3">Status Milestones</h3>
                <ul className="space-y-2">
                  {referral.milestones.map((m, i) => (
                    <li key={`${m.code}-${i}`} className="flex items-center justify-between text-sm">
                      <span className="text-gray-700">{m.displayName}</span>
                      <span className="text-gray-500">{new Date(m.occurredAtUtc).toLocaleDateString()}</span>
                    </li>
                  ))}
                </ul>
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
