'use client';

import { useEffect, useState } from 'react';
import Link from 'next/link';
import { fetchRepresentativeReferrals } from '@/lib/representative-portal-api';
import { useRepresentativePortal } from '@/components/careconnect/representative-access-code-gate';
import { ApiError } from '@/lib/api-client';
import type { RepresentativeReferralListItem } from '@/types/careconnect';

// Matches the Dashboard's metric groupings (Pending = New + NewOpened, Accepted = Accepted +
// InProgress) — values are comma-separated raw statuses, resolved server-side into an IN filter.
const STATUS_OPTIONS = [
  { label: 'Pending', value: 'New,NewOpened' },
  { label: 'Accepted', value: 'Accepted,InProgress' },
  { label: 'Declined', value: 'Declined' },
  { label: 'Completed', value: 'Completed' },
  { label: 'Cancelled', value: 'Cancelled' },
];

export default function RepresentativeReferralsPage() {
  const { code } = useRepresentativePortal();
  const [status, setStatus]           = useState('');
  const [submittedFrom, setFrom]      = useState('');
  const [submittedTo, setTo]          = useState('');
  const [page, setPage]               = useState(1);
  const [items, setItems]             = useState<RepresentativeReferralListItem[]>([]);
  const [totalCount, setTotalCount]   = useState(0);
  const [pageSize, setPageSize]       = useState(20);
  const [loading, setLoading]         = useState(true);
  const [error, setError]             = useState<string | null>(null);

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

  return (
    <div>
      <div className="mb-6">
        <h1 className="text-xl font-semibold text-gray-900">My Referrals</h1>
        <p className="text-sm text-gray-500 mt-0.5">Referrals attributed to you</p>
      </div>

      {/* Filters */}
      <div className="flex flex-wrap items-end gap-3 mb-5">
        <div>
          <label className="block text-xs font-medium text-gray-700 mb-1">Status</label>
          <select
            value={status}
            onChange={e => { setStatus(e.target.value); setPage(1); }}
            className="border border-gray-300 rounded-md px-3 py-2 text-sm bg-white"
          >
            <option value="">All</option>
            {STATUS_OPTIONS.map(s => <option key={s.value} value={s.value}>{s.label}</option>)}
          </select>
        </div>
        <div>
          <label className="block text-xs font-medium text-gray-700 mb-1">Submitted from</label>
          <input type="date" value={submittedFrom} onChange={e => { setFrom(e.target.value); setPage(1); }} className="border border-gray-300 rounded-md px-3 py-2 text-sm" />
        </div>
        <div>
          <label className="block text-xs font-medium text-gray-700 mb-1">Submitted to</label>
          <input type="date" value={submittedTo} onChange={e => { setTo(e.target.value); setPage(1); }} className="border border-gray-300 rounded-md px-3 py-2 text-sm" />
        </div>
        {(status || submittedFrom || submittedTo) && (
          <button
            onClick={() => { setStatus(''); setFrom(''); setTo(''); setPage(1); }}
            className="text-sm text-gray-500 hover:text-gray-900 pb-2 cursor-pointer"
          >
            Clear filters
          </button>
        )}
      </div>

      {error && (
        <div className="bg-red-50 border border-red-200 rounded-lg px-4 py-3 text-sm text-red-700 mb-6">{error}</div>
      )}

      {loading ? (
        <p className="text-sm text-gray-500">Loading…</p>
      ) : items.length === 0 ? (
        <div className="bg-white rounded-xl border border-gray-200 p-12 text-center">
          <h3 className="text-base font-semibold text-gray-900 mb-1">No referrals found</h3>
          <p className="text-sm text-gray-500">
            {status || submittedFrom || submittedTo
              ? 'No referrals match the current filters.'
              : 'No referrals are currently attributed to you.'}
          </p>
        </div>
      ) : (
        <div className="bg-white border border-gray-200 rounded-lg overflow-hidden">
          <div className="overflow-x-auto">
            <table className="w-full text-left">
              <thead>
                <tr className="border-b border-gray-200 bg-gray-50">
                  <th className="px-4 py-2.5 text-xs font-semibold text-gray-500 uppercase tracking-wide">Reference #</th>
                  <th className="px-4 py-2.5 text-xs font-semibold text-gray-500 uppercase tracking-wide">Submitted</th>
                  <th className="px-4 py-2.5 text-xs font-semibold text-gray-500 uppercase tracking-wide">Status</th>
                  <th className="px-4 py-2.5 text-xs font-semibold text-gray-500 uppercase tracking-wide">Client</th>
                  <th className="px-4 py-2.5 text-xs font-semibold text-gray-500 uppercase tracking-wide">Law Firm</th>
                  <th className="px-4 py-2.5 text-xs font-semibold text-gray-500 uppercase tracking-wide">Provider</th>
                  <th className="px-4 py-2.5 text-xs font-semibold text-gray-500 uppercase tracking-wide">Last Updated</th>
                  <th className="px-4 py-2.5" />
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100">
                {items.map(item => (
                  <tr key={item.referralId} className="hover:bg-gray-50">
                    <td className="px-4 py-3 text-sm font-mono text-gray-900">{item.referenceNumber}</td>
                    <td className="px-4 py-3 text-sm text-gray-500">{new Date(item.submittedAtUtc).toLocaleDateString()}</td>
                    <td className="px-4 py-3">
                      <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-gray-100 text-gray-700">
                        {item.status.displayName}
                      </span>
                    </td>
                    <td className="px-4 py-3 text-sm">
                      <p className="text-gray-900">{item.client.firstName} {item.client.lastName}</p>
                    </td>
                    <td className="px-4 py-3 text-sm text-gray-900">{item.lawFirm.displayName}</td>
                    <td className="px-4 py-3 text-sm">
                      <p className="text-gray-900">{item.providerLocation?.name ?? item.provider.displayName}</p>
                      {item.providerLocation && (
                        <p className="text-xs text-gray-500 mt-0.5">
                          {item.providerLocation.city}, {item.providerLocation.state}
                        </p>
                      )}
                    </td>
                    <td className="px-4 py-3 text-sm text-gray-500">{new Date(item.lastUpdatedAtUtc).toLocaleDateString()}</td>
                    <td className="px-4 py-3 text-right">
                      <Link href={`/careconnect/representative/referrals/${item.referralId}`} className="cursor-pointer text-sm text-primary hover:underline font-medium">
                        View →
                      </Link>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="border-t border-gray-100 px-4 py-3 flex items-center justify-between">
            <span className="text-sm text-gray-500">
              Showing {(page - 1) * pageSize + 1}–{Math.min(page * pageSize, totalCount)} of {totalCount.toLocaleString()}
            </span>
            <div className="flex items-center gap-2">
              <button
                onClick={() => setPage(p => Math.max(1, p - 1))}
                disabled={page <= 1}
                className="text-sm text-gray-600 hover:text-gray-900 cursor-pointer disabled:opacity-40 disabled:cursor-not-allowed"
              >
                ← Prev
              </button>
              <span className="text-sm text-gray-500">Page {page} of {totalPages}</span>
              <button
                onClick={() => setPage(p => Math.min(totalPages, p + 1))}
                disabled={page >= totalPages}
                className="text-sm text-gray-600 hover:text-gray-900 cursor-pointer disabled:opacity-40 disabled:cursor-not-allowed"
              >
                Next →
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
