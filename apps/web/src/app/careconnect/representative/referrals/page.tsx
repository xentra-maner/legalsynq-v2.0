'use client';

import { useEffect, useMemo, useState } from 'react';
import Link from 'next/link';
import { fetchRepresentativeReferrals } from '@/lib/representative-portal-api';
import { useRepresentativePortal } from '@/components/careconnect/representative-access-code-gate';
import { ApiError } from '@/lib/api-client';
import type { RepresentativeReferralListItem } from '@/types/careconnect';

const STATUS_TABS = [
  { label: 'All', value: '' },
  { label: 'Pending', value: 'New,NewOpened' },
  { label: 'Accepted', value: 'Accepted,InProgress' },
  { label: 'Declined', value: 'Declined' },
  { label: 'Completed', value: 'Completed' },
  { label: 'Cancelled', value: 'Cancelled' },
];

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

function formatPhone(value?: string | null): string | null {
  if (!value) return null;
  const digits = value.replace(/\D/g, '');
  if (digits.length === 10) {
    return `(${digits.slice(0, 3)}) ${digits.slice(3, 6)}-${digits.slice(6)}`;
  }
  if (digits.length === 11 && digits.startsWith('1')) {
    return `+1 (${digits.slice(1, 4)}) ${digits.slice(4, 7)}-${digits.slice(7)}`;
  }
  return value;
}

function patientName(item: RepresentativeReferralListItem): string {
  return [item.client.firstName, item.client.lastName].filter(Boolean).join(' ') || 'Unnamed patient';
}

function providerLocationLabel(item: RepresentativeReferralListItem): string | null {
  const location = item.providerLocation;
  if (!location) return null;
  return [location.city, location.state].filter(Boolean).join(', ') || null;
}

function StatusBadge({ code, label }: { code: string; label: string }) {
  return (
    <span className={`inline-flex rounded px-2 py-0.5 text-xs font-medium ring-1 ${STATUS_STYLES[code] ?? 'bg-gray-100 text-gray-700 ring-gray-200'}`}>
      {label}
    </span>
  );
}

export default function RepresentativeReferralsPage() {
  const { code } = useRepresentativePortal();
  const [status, setStatus] = useState('');
  const [submittedFrom, setFrom] = useState('');
  const [submittedTo, setTo] = useState('');
  const [page, setPage] = useState(1);
  const [items, setItems] = useState<RepresentativeReferralListItem[]>([]);
  const [totalCount, setTotalCount] = useState(0);
  const [pageSize, setPageSize] = useState(20);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setLoading(true);
    fetchRepresentativeReferrals(code, {
      status: status || undefined,
      submittedFrom: submittedFrom || undefined,
      submittedTo: submittedTo || undefined,
      page,
      pageSize: 20,
    })
      .then(({ data }) => {
        setItems(data.items);
        setTotalCount(data.totalCount);
        setPageSize(data.pageSize);
        setError(null);
      })
      .catch(err => setError(err instanceof ApiError ? err.message : 'Failed to load referrals.'))
      .finally(() => setLoading(false));
  }, [code, status, submittedFrom, submittedTo, page]);

  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize));
  const activeTab = useMemo(
    () => STATUS_TABS.find(tab => tab.value === status)?.label ?? 'Filtered',
    [status],
  );

  return (
    <div className="space-y-5">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
        <div>
          <h1 className="text-xl font-semibold text-gray-900">Converted Referrals</h1>
          <p className="mt-0.5 text-sm text-gray-500">Converted referrals.</p>
        </div>
        <div className="rounded-lg border border-gray-200 bg-white px-4 py-3 text-right">
          <p className="text-2xl font-semibold text-gray-900">{totalCount.toLocaleString()}</p>
          <p className="text-xs font-medium uppercase tracking-wide text-gray-500">{activeTab} referrals</p>
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
          <div className="p-8 text-sm text-gray-500">Loading referrals...</div>
        ) : items.length === 0 ? (
          <div className="p-12 text-center">
            <h3 className="mb-1 text-base font-semibold text-gray-900">No referrals found</h3>
            <p className="text-sm text-gray-500">
              {status || submittedFrom || submittedTo
                ? 'No referrals match the current filters.'
                : 'No referrals are currently attributed to you.'}
            </p>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full table-fixed text-left">
              <colgroup>
                <col className="w-[19%]" />
                <col className="w-[10%]" />
                <col className="w-[14%]" />
                <col className="w-[15%]" />
                <col className="w-[21%]" />
                <col className="w-[11%]" />
                <col className="w-[10%]" />
              </colgroup>
              <thead>
                <tr className="border-b border-gray-200 bg-gray-50">
                  <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-gray-500">Patient</th>
                  <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-gray-500">Status</th>
                  <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-gray-500">Reference</th>
                  <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-gray-500">Law Firm</th>
                  <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-gray-500">Provider</th>
                  <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-gray-500">Submitted</th>
                  <th className="px-4 py-3" />
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100">
                {items.map(item => (
                  <tr key={item.referralId} className="transition-colors hover:bg-gray-50">
                    <td className="px-4 py-3">
                      <p className="text-sm font-semibold text-gray-900">{patientName(item)}</p>
                      <p className="mt-0.5 text-xs text-gray-500">{formatPhone(item.client.phone) ?? item.client.email ?? 'No contact listed'}</p>
                    </td>
                    <td className="px-4 py-3">
                      <StatusBadge code={item.status.code} label={item.status.displayName} />
                    </td>
                    <td className="px-4 py-3 font-mono text-sm text-gray-800">{item.referenceNumber}</td>
                    <td className="px-4 py-3 text-sm text-gray-700">{item.lawFirm.displayName}</td>
                    <td className="max-w-xs px-4 py-3 text-sm text-gray-700">
                      <p className="truncate font-medium text-gray-900">{item.provider.displayName}</p>
                      {providerLocationLabel(item) && (
                        <p className="mt-0.5 truncate text-xs text-gray-500">{providerLocationLabel(item)}</p>
                      )}
                    </td>
                    <td className="px-4 py-3 text-sm text-gray-500">{formatDate(item.submittedAtUtc)}</td>
                    <td className="px-3 py-3 text-right">
                      <Link
                        href={`/careconnect/referral/referrals/${item.referralId}`}
                        className="inline-flex items-center gap-1 rounded-md border border-gray-200 px-2.5 py-1.5 text-sm font-medium text-primary hover:border-primary/40 hover:bg-primary/5"
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
