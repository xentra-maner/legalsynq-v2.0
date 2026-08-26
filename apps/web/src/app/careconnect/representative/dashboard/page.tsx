'use client';

import { useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import Link from 'next/link';
import {
  Activity,
  ArrowRight,
  CheckCircle2,
  ClipboardList,
  Clock3,
  Send,
  XCircle,
  type LucideIcon,
} from 'lucide-react';
import {
  fetchRepresentativePendingRequests,
  fetchRepresentativeMetrics,
  fetchRepresentativeReferrals,
} from '@/lib/representative-portal-api';
import { useRepresentativePortal } from '@/components/careconnect/representative-access-code-gate';
import { ApiError } from '@/lib/api-client';
import type {
  PendingReferralRequest,
  RepresentativeReferralListItem,
  RepresentativeReferralMetrics,
} from '@/types/careconnect';

type DatePreset = '7d' | '30d' | '90d' | 'custom';

const STATUS_TONES: Record<string, string> = {
  New: 'bg-amber-50 text-amber-700 ring-amber-200',
  NewOpened: 'bg-amber-50 text-amber-700 ring-amber-200',
  Accepted: 'bg-blue-50 text-blue-700 ring-blue-200',
  InProgress: 'bg-blue-50 text-blue-700 ring-blue-200',
  Completed: 'bg-emerald-50 text-emerald-700 ring-emerald-200',
  Declined: 'bg-red-50 text-red-700 ring-red-200',
  Cancelled: 'bg-gray-100 text-gray-700 ring-gray-200',
};

const STATUS_BAR = [
  { key: 'pendingRequest', label: 'Pending Request', color: 'bg-orange-500' },
  { key: 'pending', label: 'Pending', color: 'bg-amber-500' },
  { key: 'accepted', label: 'Accepted', color: 'bg-blue-500' },
  { key: 'completed', label: 'Completed', color: 'bg-emerald-500' },
  { key: 'declined', label: 'Declined', color: 'bg-red-500' },
  { key: 'cancelled', label: 'Cancelled', color: 'bg-gray-400' },
] as const;

function asCount(value?: number | null): number {
  return Number.isFinite(value) ? Number(value) : 0;
}

function toInputDate(date: Date): string {
  return date.toISOString().slice(0, 10);
}

function presetRange(preset: Exclude<DatePreset, 'custom'>): { from: string; to: string } {
  const to = new Date();
  const from = new Date();
  const days = preset === '7d' ? 7 : preset === '30d' ? 30 : 90;
  from.setDate(to.getDate() - (days - 1));
  return { from: toInputDate(from), to: toInputDate(to) };
}

function formatDate(value?: string | null): string {
  if (!value) return 'Not available';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return 'Not available';
  return date.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
}

function fullName(item: RepresentativeReferralListItem): string {
  return [item.client.firstName, item.client.lastName].filter(Boolean).join(' ') || 'Unnamed patient';
}

function pendingPatientName(item: PendingReferralRequest): string {
  return [item.clientFirstName, item.clientLastName].filter(Boolean).join(' ') || 'Unnamed patient';
}

function preferredProviderLabel(item: PendingReferralRequest): string {
  const preferences = item.preferredProviders ?? [];
  const first = preferences[0];
  if (!first) return 'No preference';
  const extra = preferences.length > 1 ? ` +${preferences.length - 1}` : '';
  return `${first.providerName}${extra}`;
}

function statusTone(status: string): string {
  return STATUS_TONES[status] ?? 'bg-gray-100 text-gray-700 ring-gray-200';
}

function KpiCard({
  label,
  value,
  detail,
  icon: Icon,
  tone,
}: {
  label: string;
  value?: number | null;
  detail: string;
  icon: LucideIcon;
  tone: string;
}) {
  const displayValue = asCount(value);

  return (
    <div className="rounded-lg border border-gray-200 bg-white p-4 shadow-sm">
      <div className="flex items-start justify-between gap-3">
        <div>
          <p className="text-xs font-semibold uppercase tracking-wide text-gray-500">{label}</p>
          <p className="mt-2 text-3xl font-semibold text-gray-950">{displayValue.toLocaleString()}</p>
        </div>
        <span className={`flex h-10 w-10 items-center justify-center rounded-md ${tone}`}>
          <Icon className="h-5 w-5" aria-hidden="true" />
        </span>
      </div>
      <p className="mt-3 text-sm text-gray-500">{detail}</p>
    </div>
  );
}

function Panel({
  title,
  action,
  children,
}: {
  title: string;
  action?: ReactNode;
  children: ReactNode;
}) {
  return (
    <section className="rounded-lg border border-gray-200 bg-white shadow-sm">
      <div className="flex min-h-12 items-center justify-between gap-3 border-b border-gray-100 px-4 py-3">
        <h2 className="text-sm font-semibold text-gray-950">{title}</h2>
        {action}
      </div>
      <div className="p-4">{children}</div>
    </section>
  );
}

export default function RepresentativeDashboardPage() {
  const { code } = useRepresentativePortal();
  const [preset, setPreset] = useState<DatePreset>('30d');
  const [{ from, to }, setRange] = useState(() => presetRange('30d'));
  const [metrics, setMetrics] = useState<RepresentativeReferralMetrics | null>(null);
  const [pendingRequests, setPendingRequests] = useState<PendingReferralRequest[]>([]);
  const [recentReferrals, setRecentReferrals] = useState<RepresentativeReferralListItem[]>([]);
  const [totalPendingRequests, setTotalPendingRequests] = useState(0);
  const [totalRecent, setTotalRecent] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const pendingRequestCount = asCount(metrics?.pendingRequestReferrals ?? metrics?.pendingReviewReferrals);
  const totalAttributed = asCount(metrics?.totalAttributedReferrals);
  const totalVisible = totalAttributed + pendingRequestCount;

  const statusCounts = useMemo(() => ({
    pendingRequest: pendingRequestCount,
    pending: asCount(metrics?.pendingReferrals),
    accepted: asCount(metrics?.acceptedReferrals),
    completed: asCount(metrics?.completedReferrals),
    declined: asCount(metrics?.declinedReferrals),
    cancelled: asCount(metrics?.cancelledReferrals),
  }), [metrics, pendingRequestCount]);

  const statusTotal = Object.values(statusCounts).reduce((sum, value) => sum + value, 0);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);

    Promise.all([
      fetchRepresentativeMetrics(code, from || undefined, to || undefined),
      fetchRepresentativePendingRequests(code, {
        from: from || undefined,
        to: to || undefined,
        page: 1,
        pageSize: 3,
      }),
      fetchRepresentativeReferrals(code, {
        submittedFrom: from || undefined,
        submittedTo: to || undefined,
        page: 1,
        pageSize: 6,
      }),
    ])
      .then(([metricsResult, pendingRequestsResult, referralsResult]) => {
        if (cancelled) return;
        setMetrics(metricsResult.data);
        setPendingRequests(pendingRequestsResult.data.items);
        setTotalPendingRequests(pendingRequestsResult.data.totalCount);
        setRecentReferrals(referralsResult.data.items);
        setTotalRecent(referralsResult.data.totalCount);
        setError(null);
      })
      .catch(err => {
        if (cancelled) return;
        setError(err instanceof ApiError ? err.message : 'Failed to load dashboard metrics.');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => { cancelled = true; };
  }, [code, from, to]);

  function selectPreset(nextPreset: DatePreset) {
    setPreset(nextPreset);
    if (nextPreset !== 'custom') setRange(presetRange(nextPreset));
  }

  function updateCustomRange(next: { from?: string; to?: string }) {
    setPreset('custom');
    setRange(current => ({ ...current, ...next }));
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-4 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <h1 className="text-2xl font-semibold text-gray-950">Dashboard</h1>
          <p className="mt-1 text-sm text-gray-500">Track requests, routed referrals, and outcomes attributed to you.</p>
        </div>

        <div className="flex flex-wrap items-end gap-2">
          <div className="flex rounded-md border border-gray-200 bg-white p-1">
            {[
              ['7d', '7 days'],
              ['30d', '30 days'],
              ['90d', '90 days'],
              ['custom', 'Custom'],
            ].map(([value, label]) => (
              <button
                key={value}
                type="button"
                onClick={() => selectPreset(value as DatePreset)}
                className={`rounded px-3 py-1.5 text-sm font-medium ${
                  preset === value ? 'bg-gray-950 text-white' : 'text-gray-600 hover:bg-gray-50'
                }`}
              >
                {label}
              </button>
            ))}
          </div>
          <input
            type="date"
            value={from}
            onChange={event => updateCustomRange({ from: event.target.value })}
            className="h-10 rounded-md border border-gray-300 px-3 text-sm"
            aria-label="Submitted from"
          />
          <input
            type="date"
            value={to}
            onChange={event => updateCustomRange({ to: event.target.value })}
            className="h-10 rounded-md border border-gray-300 px-3 text-sm"
            aria-label="Submitted to"
          />
        </div>
      </div>

      {error && (
        <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">{error}</div>
      )}

      {loading ? (
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-4">
          {[0, 1, 2, 3].map(index => (
            <div key={index} className="h-32 animate-pulse rounded-lg border border-gray-200 bg-gray-50" />
          ))}
        </div>
      ) : metrics ? (
        <>
          <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-4">
            <KpiCard
              label="Total activity"
              value={totalVisible}
              detail="Pending requests plus routed referrals"
              icon={Activity}
              tone="bg-gray-100 text-gray-700"
            />
            <KpiCard
              label="Pending Request"
              value={pendingRequestCount}
              detail="Awaiting law firm review"
              icon={ClipboardList}
              tone="bg-orange-50 text-orange-700"
            />
            <KpiCard
              label="Pending provider"
              value={metrics.pendingReferrals}
              detail="Routed, not yet accepted"
              icon={Clock3}
              tone="bg-amber-50 text-amber-700"
            />
            <KpiCard
              label="Completed"
              value={metrics.completedReferrals}
              detail="Resolved originated referrals"
              icon={CheckCircle2}
              tone="bg-emerald-50 text-emerald-700"
            />
          </div>

          <div className="grid grid-cols-1 items-start gap-4 xl:grid-cols-[minmax(0,1.35fr)_minmax(320px,0.65fr)]">
            <div className="space-y-4">
              <Panel
                title="Recent Referral Activity"
                action={(
                  <Link href="/careconnect/referral/referrals" className="inline-flex items-center gap-1 text-sm font-medium text-primary hover:underline">
                    View all <ArrowRight className="h-4 w-4" aria-hidden="true" />
                  </Link>
                )}
              >
                {recentReferrals.length === 0 ? (
                  <div className="py-8 text-center">
                    <Send className="mx-auto h-8 w-8 text-gray-300" aria-hidden="true" />
                    <p className="mt-3 text-sm font-medium text-gray-900">No routed referrals in this range</p>
                    <p className="mt-1 text-sm text-gray-500">New activity will appear here after the law firm routes a request.</p>
                  </div>
                ) : (
                  <div className="divide-y divide-gray-100">
                    {recentReferrals.map(item => (
                      <Link
                        key={item.referralId}
                        href={`/careconnect/referral/referrals/${item.referralId}`}
                        className="grid gap-3 py-3 hover:bg-gray-50 sm:grid-cols-[minmax(0,1fr)_140px_120px]"
                      >
                        <div className="min-w-0">
                          <div className="flex items-center gap-2">
                            <p className="truncate text-sm font-semibold text-gray-950">{fullName(item)}</p>
                            <span className={`shrink-0 rounded px-2 py-0.5 text-xs font-medium ring-1 ${statusTone(item.status.code)}`}>
                              {item.status.displayName}
                            </span>
                          </div>
                          <p className="mt-1 truncate text-sm text-gray-500">
                            {item.lawFirm.displayName} - {item.providerLocation?.name ?? item.provider.displayName}
                          </p>
                        </div>
                        <div className="text-sm text-gray-500">
                          <p className="font-medium text-gray-700">{item.referenceNumber}</p>
                          <p>{formatDate(item.submittedAtUtc)}</p>
                        </div>
                        <div className="flex items-center justify-start text-sm font-medium text-primary sm:justify-end">
                          View <ArrowRight className="ml-1 h-4 w-4" aria-hidden="true" />
                        </div>
                      </Link>
                    ))}
                  </div>
                )}
              </Panel>

              <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
                <KpiCard
                  label="Accepted"
                  value={metrics.acceptedReferrals}
                  detail="Accepted or in progress"
                  icon={CheckCircle2}
                  tone="bg-blue-50 text-blue-700"
                />
                <KpiCard
                  label="Declined"
                  value={metrics.declinedReferrals}
                  detail="Provider or firm declined"
                  icon={XCircle}
                  tone="bg-red-50 text-red-700"
                />
                <KpiCard
                  label="Cancelled"
                  value={metrics.cancelledReferrals}
                  detail={`${totalRecent.toLocaleString()} routed referral${totalRecent === 1 ? '' : 's'} in range`}
                  icon={Clock3}
                  tone="bg-gray-100 text-gray-700"
                />
              </div>
            </div>

            <div className="space-y-4">
              <Panel
                title="Pending Requests"
                action={(
                  <Link href="/careconnect/referral/requests" className="inline-flex items-center gap-1 text-sm font-medium text-primary hover:underline">
                    View all <ArrowRight className="h-4 w-4" aria-hidden="true" />
                  </Link>
                )}
              >
                {pendingRequests.length === 0 ? (
                  <div className="py-6 text-center">
                    <ClipboardList className="mx-auto h-8 w-8 text-gray-300" aria-hidden="true" />
                    <p className="mt-3 text-sm font-medium text-gray-900">No pending requests</p>
                    <p className="mt-1 text-sm text-gray-500">Portal submissions awaiting review will appear here.</p>
                  </div>
                ) : (
                  <div className="space-y-3">
                    {pendingRequests.map(item => (
                      <div key={item.id} className="rounded-md border border-orange-100 bg-orange-50/60 p-3">
                        <div className="flex items-start justify-between gap-3">
                          <div className="min-w-0">
                            <p className="truncate text-sm font-semibold text-gray-950">{pendingPatientName(item)}</p>
                            <p className="mt-1 truncate text-sm text-gray-600">{item.lawFirmName ?? 'Law firm pending'}</p>
                          </div>
                          <span className="shrink-0 rounded bg-orange-100 px-2 py-0.5 text-xs font-medium text-orange-700">
                            Pending Request
                          </span>
                        </div>
                        <div className="mt-3 grid gap-1 text-sm text-gray-600">
                          <p className="truncate">{preferredProviderLabel(item)}</p>
                          <p>{formatDate(item.createdAtUtc)}</p>
                        </div>
                      </div>
                    ))}
                  </div>
                )}
              </Panel>

              <Panel title="Status Breakdown">
                {statusTotal === 0 ? (
                  <p className="text-sm text-gray-500">No activity in this range.</p>
                ) : (
                  <>
                    <div className="flex h-2 overflow-hidden rounded-full bg-gray-100">
                      {STATUS_BAR.map(segment => {
                        const value = statusCounts[segment.key];
                        if (value <= 0) return null;
                        return (
                          <div
                            key={segment.key}
                            className={segment.color}
                            style={{ width: `${Math.max(4, (value / statusTotal) * 100)}%` }}
                          />
                        );
                      })}
                    </div>
                    <div className="mt-4 space-y-3">
                      {STATUS_BAR.map(segment => (
                        <div key={segment.key} className="flex items-center justify-between gap-3 text-sm">
                          <span className="flex items-center gap-2 text-gray-600">
                            <span className={`h-2.5 w-2.5 rounded-full ${segment.color}`} />
                            {segment.label}
                          </span>
                          <span className="font-semibold text-gray-950">{statusCounts[segment.key].toLocaleString()}</span>
                        </div>
                      ))}
                    </div>
                  </>
                )}
              </Panel>
            </div>
          </div>
        </>
      ) : null}
    </div>
  );
}
