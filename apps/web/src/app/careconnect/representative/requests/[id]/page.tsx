'use client';

import { useEffect, useState, use as usePromise } from 'react';
import type { ReactNode } from 'react';
import Link from 'next/link';
import { fetchRepresentativePendingRequestById } from '@/lib/representative-portal-api';
import { useRepresentativePortal } from '@/components/careconnect/representative-access-code-gate';
import { ApiError } from '@/lib/api-client';
import type { PendingReferralProviderPreference, PendingReferralRequest } from '@/types/careconnect';

const REQUEST_STATUS: Record<string, { label: string; className: string }> = {
  PendingReview: {
    label: 'Pending',
    className: 'bg-orange-100 text-orange-800 ring-orange-200',
  },
  Converted: {
    label: 'Accepted',
    className: 'bg-emerald-100 text-emerald-800 ring-emerald-200',
  },
  Cancelled: {
    label: 'Declined',
    className: 'bg-red-100 text-red-800 ring-red-200',
  },
};

function formatDate(value?: string | null): string {
  if (!value) return 'Not available';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return 'Not available';
  return date.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
}

function formatFileSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function patientName(item: PendingReferralRequest): string {
  return [item.clientFirstName, item.clientLastName].filter(Boolean).join(' ') || 'Unnamed patient';
}

function preferenceLabel(preference: PendingReferralProviderPreference): string {
  return preference.facilityName
    ? `${preference.providerName} - ${preference.facilityName}`
    : preference.providerName;
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

function StatusBadge({ status }: { status: string }) {
  const badge = REQUEST_STATUS[status] ?? {
    label: status,
    className: 'bg-gray-100 text-gray-700 ring-gray-200',
  };

  return (
    <span className={`inline-flex rounded px-2 py-0.5 text-xs font-medium ring-1 ${badge.className}`}>
      {badge.label}
    </span>
  );
}

interface PageProps {
  params: Promise<{ id: string }>;
}

export default function RepresentativePendingRequestDetailPage({ params }: PageProps) {
  const { id } = usePromise(params);
  const { code } = useRepresentativePortal();
  const [request, setRequest] = useState<PendingReferralRequest | null>(null);
  const [loading, setLoading] = useState(true);
  const [notFound, setNotFound] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const preferences = request?.preferredProviders ?? [];
  const attachments = request?.attachments ?? [];

  useEffect(() => {
    setLoading(true);
    fetchRepresentativePendingRequestById(code, id)
      .then(({ data }) => {
        setRequest(data);
        setNotFound(false);
        setError(null);
      })
      .catch(err => {
        if (err instanceof ApiError && err.status === 404) {
          setNotFound(true);
        } else {
          setError(err instanceof ApiError ? err.message : 'Failed to load referral request.');
        }
      })
      .finally(() => setLoading(false));
  }, [code, id]);

  return (
    <div className="space-y-5">
      <Link href="/careconnect/referral/requests" className="inline-flex items-center gap-1 text-sm text-gray-500 hover:text-gray-800">
        <i className="ri-arrow-left-line" aria-hidden="true" /> Back to Referral Requests
      </Link>

      {loading && <p className="text-sm text-gray-500">Loading referral request...</p>}

      {notFound && (
        <div className="rounded-xl border border-gray-200 bg-white p-12 text-center">
          <h3 className="mb-1 text-base font-semibold text-gray-900">Referral request not found</h3>
          <p className="text-sm text-gray-500">This request does not exist, or is not attributed to you.</p>
        </div>
      )}

      {error && (
        <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">{error}</div>
      )}

      {request && (
        <>
          <div className="rounded-lg border border-orange-200 bg-orange-50 p-5">
            <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
              <div>
                <div className="flex flex-wrap items-center gap-2">
                  <h1 className="text-2xl font-semibold text-gray-900">{patientName(request)}</h1>
                  <StatusBadge status={request.status} />
                </div>
                <p className="mt-1 text-sm text-orange-800">
                  Submitted {formatDate(request.createdAtUtc)} for {request.lawFirmName ?? 'law firm review'}.
                </p>
              </div>
              <div className="rounded-md border border-orange-200 bg-white px-3 py-2 text-sm text-orange-900">
                Provider preferences are recommendations only. The law firm makes the final routing decision.
              </div>
            </div>
          </div>

          <div className="grid gap-5 lg:grid-cols-[minmax(0,1fr)_320px]">
            <div className="space-y-5">
              <Section title="Patient Information">
                <Field label="Name" value={patientName(request)} />
                <Field label="Date of Birth" value={formatDate(request.clientDob)} />
                <Field label="Phone" value={request.clientPhone} />
                <Field label="Email" value={request.clientEmail} />
                <Field label="Date of Accident" value={formatDate(request.dateOfAccident)} />
              </Section>

              <Section title="Referral Details">
                <Field label="Law Firm" value={request.lawFirmName ?? 'Law firm pending'} />
                <Field label="Urgency" value={request.urgency || 'Normal'} />
                <Field label="Requested Service" value={request.requestedService} />
                <Field label="Status" value={REQUEST_STATUS[request.status]?.label ?? request.status} />
              </Section>

              <section className="rounded-lg border border-gray-200 bg-white">
                <div className="border-b border-gray-100 px-5 py-4">
                  <h2 className="text-sm font-semibold text-gray-900">Preferred Medical Providers</h2>
                </div>
                {preferences.length > 0 ? (
                  <div className="divide-y divide-gray-100">
                    {preferences
                      .slice()
                      .sort((a, b) => a.displayOrder - b.displayOrder)
                      .map((preference, index) => (
                        <div key={preference.id} className="flex items-start gap-3 px-5 py-4">
                          <span className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-primary text-sm font-semibold text-white">
                            {index + 1}
                          </span>
                          <div>
                            <p className="text-sm font-medium text-gray-900">{preference.providerName}</p>
                            {preference.facilityName && (
                              <p className="mt-0.5 text-sm text-gray-500">{preference.facilityName}</p>
                            )}
                          </div>
                        </div>
                      ))}
                  </div>
                ) : (
                  <p className="px-5 py-5 text-sm text-gray-500">No provider preference was submitted.</p>
                )}
              </section>

              <section className="rounded-lg border border-gray-200 bg-white">
                <div className="border-b border-gray-100 px-5 py-4">
                  <h2 className="text-sm font-semibold text-gray-900">Attachments</h2>
                </div>
                {attachments.length > 0 ? (
                  <div className="divide-y divide-gray-100">
                    {attachments.map(attachment => (
                      <div key={attachment.id} className="flex items-center gap-3 px-5 py-4">
                        <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg bg-gray-50 text-gray-400">
                          <i className="ri-file-line text-lg" />
                        </span>
                        <div className="min-w-0">
                          <p className="truncate text-sm font-medium text-gray-900">{attachment.fileName}</p>
                          <p className="text-xs text-gray-500">
                            {formatFileSize(attachment.fileSizeBytes)} - {attachment.status}
                          </p>
                        </div>
                      </div>
                    ))}
                  </div>
                ) : (
                  <p className="px-5 py-5 text-sm text-gray-500">No documents were attached.</p>
                )}
              </section>
            </div>

            <aside className="space-y-5">
              <section className="rounded-lg border border-gray-200 bg-white">
                <div className="border-b border-gray-100 px-5 py-4">
                  <h2 className="text-sm font-semibold text-gray-900">Request Summary</h2>
                </div>
                <dl className="space-y-4 px-5 py-5">
                  <Field label="Submitted" value={formatDate(request.createdAtUtc)} />
                  <Field label="Updated" value={formatDate(request.updatedAtUtc)} />
                  <Field label="Origin" value={request.origin} />
                  <Field
                    label="Top Preference"
                    value={preferences[0] ? preferenceLabel(preferences[0]) : undefined}
                  />
                </dl>
              </section>

              <Section title="Lien Information">
                <Field label="Company Name" value={request.lienCompanyName} />
                <Field label="Company Email" value={request.lienCompanyEmail} />
              </Section>

              <section className="rounded-lg border border-gray-200 bg-white">
                <div className="border-b border-gray-100 px-5 py-4">
                  <h2 className="text-sm font-semibold text-gray-900">Notes</h2>
                </div>
                <p className="whitespace-pre-wrap px-5 py-5 text-sm text-gray-700">
                  {request.notes || 'No notes were submitted.'}
                </p>
              </section>
            </aside>
          </div>
        </>
      )}
    </div>
  );
}
