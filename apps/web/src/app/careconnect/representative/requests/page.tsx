'use client';

import { useEffect, useState } from 'react';
import Link from 'next/link';
import { fetchRepresentativePendingRequests } from '@/lib/representative-portal-api';
import { useRepresentativePortal } from '@/components/careconnect/representative-access-code-gate';
import { ApiError } from '@/lib/api-client';
import type { PendingReferralRequest } from '@/types/careconnect';

const STATUS_TABS = [
  { label: 'All', value: '' },
  { label: 'Pending', value: 'PendingReview' },
  { label: 'Accepted', value: 'Converted' },
  { label: 'Declined', value: 'Cancelled' },
];

const STATUS_BADGE: Record<string, { label: string; className: string }> = {
  PendingReview: {
    label: 'Pending',
    className: 'bg-orange-50 text-orange-700 ring-orange-200',
  },
  Converted: {
    label: 'Accepted',
    className: 'bg-emerald-50 text-emerald-700 ring-emerald-200',
  },
  Cancelled: {
    label: 'Declined',
    className: 'bg-red-50 text-red-700 ring-red-200',
  },
};

function formatDate(value?: string | null): string {
  if (!value) return 'Not available';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return 'Not available';
  return date.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
}

function formatPhone(value?: string | null): string {
  if (!value) return 'No phone listed';
  const digits = value.replace(/\D/g, '');
  if (digits.length === 10) {
    return `(${digits.slice(0, 3)}) ${digits.slice(3, 6)}-${digits.slice(6)}`;
  }
  if (digits.length === 11 && digits.startsWith('1')) {
    return `+1 (${digits.slice(1, 4)}) ${digits.slice(4, 7)}-${digits.slice(7)}`;
  }
  return value;
}

function patientName(item: PendingReferralRequest): string {
  return [item.clientFirstName, item.clientLastName].filter(Boolean).join(' ') || 'Unnamed patient';
}

function preferredProviderSummary(item: PendingReferralRequest): string {
  const preferences = item.preferredProviders ?? [];
  const first = preferences[0];
  if (!first) return 'No provider preference';
  return preferences.length > 1
    ? `${first.providerName} +${preferences.length - 1}`
    : first.providerName;
}

function StatusBadge({ status }: { status: string }) {
  const badge = STATUS_BADGE[status] ?? {
    label: status,
    className: 'bg-gray-100 text-gray-700 ring-gray-200',
  };

  return (
    <span className={`inline-flex rounded px-2 py-0.5 text-xs font-medium ring-1 ${badge.className}`}>
      {badge.label}
    </span>
  );
}

export default function RepresentativePendingRequestsPage() {
  const { code } = useRepresentativePortal();
  const [status, setStatus] = useState('');
  const [submittedFrom, setFrom] = useState('');
  const [submittedTo, setTo] = useState('');
  const [page, setPage] = useState(1);
  const [items, setItems] = useState<PendingReferralRequest[]>([]);
  const [totalCount, setTotalCount] = useState(0);
  const [pageSize, setPageSize] = useState(20);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setLoading(true);
    fetchRepresentativePendingRequests(code, {
      from: submittedFrom || undefined,
      to: submittedTo || undefined,
      status: status || undefined,
      page,
      pageSize: 20,
    })
      .then(({ data }) => {
        setItems(data.items);
        setTotalCount(data.totalCount);
        setPageSize(data.pageSize);
        setError(null);
      })
      .catch(err => setError(err instanceof ApiError ? err.message : 'Failed to load pending requests.'))
      .finally(() => setLoading(false));
  }, [code, status, submittedFrom, submittedTo, page]);

  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize));
  const activeTab = STATUS_TABS.find(tab => tab.value === status)?.label ?? 'Filtered';

  return (
    <div className="space-y-5">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
        <div>
          <h1 className="text-xl font-semibold text-gray-900">Pending Requests</h1>
          <p className="mt-0.5 text-sm text-gray-500">
            Referral portal submissions and law firm decisions.
          </p>
        </div>
        <div className="rounded-lg border border-orange-200 bg-orange-50 px-4 py-3 text-right">
          <p className="text-2xl font-semibold text-orange-900">{totalCount.toLocaleString()}</p>
          <p className="text-xs font-medium uppercase tracking-wide text-orange-700">{activeTab} Requests</p>
        </div>
      </div>

      <div className="rounded-lg border border-gray-200 bg-white">
        <div className="border-b border-gray-100 p-4">
          <div className="flex flex-wrap gap-2">
            {STATUS_TABS.map(tab => (
              <button
                key={tab.label}
                type="button"
                onClick={() => { setStatus(tab.value); setPage(1); }}
                className={`rounded-md px-3 py-1.5 text-sm font-medium transition-colors ${
                  status === tab.value
                    ? 'bg-primary text-white'
                    : 'bg-gray-50 text-gray-600 hover:bg-gray-100 hover:text-gray-900'
                }`}
              >
                {tab.label}
              </button>
            ))}
          </div>
          <div className="mt-4 flex flex-wrap items-end gap-3">
            <div>
              <label className="mb-1 block text-xs font-medium text-gray-700">Submitted from</label>
              <input
                type="date"
                value={submittedFrom}
                onChange={event => { setFrom(event.target.value); setPage(1); }}
                className="rounded-md border border-gray-300 px-3 py-2 text-sm"
              />
            </div>
            <div>
              <label className="mb-1 block text-xs font-medium text-gray-700">Submitted to</label>
              <input
                type="date"
                value={submittedTo}
                onChange={event => { setTo(event.target.value); setPage(1); }}
                className="rounded-md border border-gray-300 px-3 py-2 text-sm"
              />
            </div>
            {(status || submittedFrom || submittedTo) && (
              <button
                type="button"
                onClick={() => { setStatus(''); setFrom(''); setTo(''); setPage(1); }}
                className="pb-2 text-sm text-gray-500 hover:text-gray-900"
              >
                Clear filters
              </button>
            )}
          </div>
        </div>

        {error && (
          <div className="m-4 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">{error}</div>
        )}

        {loading ? (
          <div className="p-8 text-sm text-gray-500">Loading pending requests...</div>
        ) : items.length === 0 ? (
          <div className="p-12 text-center">
            <h3 className="mb-1 text-base font-semibold text-gray-900">No pending requests found</h3>
            <p className="text-sm text-gray-500">
              {status || submittedFrom || submittedTo
                ? 'No requests match the current filters.'
                : 'No referral portal submissions are currently available.'}
            </p>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full table-fixed text-left">
              <colgroup>
                <col className="w-[22%]" />
                <col className="w-[12%]" />
                <col className="w-[15%]" />
                <col className="w-[21%]" />
                <col className="w-[10%]" />
                <col className="w-[11%]" />
                <col className="w-[9%]" />
              </colgroup>
              <thead>
                <tr className="border-b border-gray-200 bg-gray-50">
                  <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-gray-500">Patient</th>
                  <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-gray-500">Status</th>
                  <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-gray-500">Law Firm</th>
                  <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-gray-500">Preferred Provider</th>
                  <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-gray-500">Urgency</th>
                  <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-gray-500">Submitted</th>
                  <th className="px-4 py-3 pr-6" />
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100">
                {items.map(item => (
                  <tr key={item.id} className="transition-colors hover:bg-gray-50">
                    <td className="px-4 py-3">
                      <p className="text-sm font-semibold text-gray-900">{patientName(item)}</p>
                      <p className="mt-0.5 text-xs text-gray-500">{formatPhone(item.clientPhone)}</p>
                    </td>
                    <td className="px-4 py-3">
                      <StatusBadge status={item.status} />
                    </td>
                    <td className="px-4 py-3 text-sm text-gray-700">{item.lawFirmName ?? 'Law firm pending'}</td>
                    <td className="max-w-xs px-4 py-3 text-sm text-gray-700">
                      <p className="truncate">{preferredProviderSummary(item)}</p>
                    </td>
                    <td className="px-4 py-3 text-sm text-gray-700">{item.urgency || 'Normal'}</td>
                    <td className="px-4 py-3 text-sm text-gray-500">{formatDate(item.createdAtUtc)}</td>
                    <td className="px-4 py-3 pr-6 text-right">
                      <Link
                        href={`/careconnect/referral/requests/${item.id}`}
                        className="inline-flex items-center gap-1 rounded-md border border-gray-200 px-3 py-1.5 text-sm font-medium text-primary hover:border-primary/40 hover:bg-primary/5"
                      >
                        View <i className="ri-arrow-right-line" aria-hidden="true" />
                      </Link>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        {!loading && items.length > 0 && (
          <div className="flex items-center justify-between border-t border-gray-100 px-4 py-3">
            <span className="text-sm text-gray-500">
              Showing {(page - 1) * pageSize + 1}-{Math.min(page * pageSize, totalCount)} of {totalCount.toLocaleString()}
            </span>
            <div className="flex items-center gap-2">
              <button
                onClick={() => setPage(p => Math.max(1, p - 1))}
                disabled={page <= 1}
                className="text-sm text-gray-600 hover:text-gray-900 disabled:cursor-not-allowed disabled:opacity-40"
              >
                Prev
              </button>
              <span className="text-sm text-gray-500">Page {page} of {totalPages}</span>
              <button
                onClick={() => setPage(p => Math.min(totalPages, p + 1))}
                disabled={page >= totalPages}
                className="text-sm text-gray-600 hover:text-gray-900 disabled:cursor-not-allowed disabled:opacity-40"
              >
                Next
              </button>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
