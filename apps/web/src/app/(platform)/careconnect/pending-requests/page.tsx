"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { careConnectApi } from "@/lib/careconnect-api";
import { ApiError } from "@/lib/api-client";
import { formatPhoneDisplay } from "@/lib/phone";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { UrgencyBadge } from "@/components/careconnect/status-badge";
import type { PendingReferralProviderPreference, PendingReferralRequest } from "@/types/careconnect";

const PAGE_SIZE = 10;

function providerSelectionValue(providerId?: string | null, facilityId?: string | null): string {
  return providerId ? `${providerId}|${facilityId ?? ""}` : "";
}

function parseProviderSelection(value: string): { providerId: string; facilityId?: string | null } | null {
  if (!value) return null;
  const [providerId, facilityId = ""] = value.split("|");
  return providerId ? { providerId, facilityId: facilityId || null } : null;
}

function preferredProvidersFor(item: PendingReferralRequest): PendingReferralProviderPreference[] {
  if (item.preferredProviders?.length > 0) {
    return [...item.preferredProviders].sort((a, b) => a.displayOrder - b.displayOrder);
  }
  if (!item.recommendedProviderId || !item.recommendedProviderName) return [];
  return [{
    id: `${item.id}-legacy-recommendation`,
    providerId: item.recommendedProviderId,
    facilityId: item.recommendedFacilityId,
    providerName: item.recommendedProviderName,
    facilityName: item.recommendedFacilityName,
    displayOrder: 0,
  }];
}

function firstPreferredProvider(item: PendingReferralRequest): PendingReferralProviderPreference | null {
  return preferredProvidersFor(item)[0] ?? null;
}

function formatDate(value?: string | null): string {
  if (!value) return "Not provided";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "Not provided";
  return date.toLocaleDateString(undefined, { month: "short", day: "numeric", year: "numeric" });
}

function PendingStatusBadge() {
  return (
    <span className="inline-flex items-center rounded-full border border-amber-200 bg-amber-50 px-2 py-0.5 text-xs font-medium text-amber-700">
      Pending review
    </span>
  );
}

export default function PendingReferralRequestsPage() {
  const router = useRouter();
  const [items, setItems] = useState<PendingReferralRequest[]>([]);
  const [selectedProviderByRequest, setSelectedProviderByRequest] = useState<Record<string, string>>({});
  const [page, setPage] = useState(1);
  const [totalCount, setTotalCount] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [acceptingId, setAcceptingId] = useState<string | null>(null);
  const [decliningId, setDecliningId] = useState<string | null>(null);

  async function load(nextPage = page) {
    setLoading(true);
    setError(null);
    try {
      const pendingRes = await careConnectApi.pendingReferralRequests.search({
        status: "PendingReview",
        page: nextPage,
        pageSize: PAGE_SIZE,
      });
      const nextItems = pendingRes.data.items;
      setItems(nextItems);
      setTotalCount(pendingRes.data.totalCount);
      setSelectedProviderByRequest(prev => {
        const next = { ...prev };
        for (const item of nextItems) {
          const preference = firstPreferredProvider(item);
          if (!next[item.id] && preference) {
            next[item.id] = providerSelectionValue(preference.providerId, preference.facilityId);
          }
        }
        return next;
      });
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Failed to load pending referral requests.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => { void load(page); }, [page]);

  async function acceptRequest(item: PendingReferralRequest) {
    const preference = firstPreferredProvider(item);
    const selection = parseProviderSelection(
      selectedProviderByRequest[item.id] || providerSelectionValue(preference?.providerId, preference?.facilityId),
    );
    if (!selection) {
      router.push(`/careconnect/pending-requests/${item.id}`);
      return;
    }

    setAcceptingId(item.id);
    setError(null);
    try {
      const result = await careConnectApi.pendingReferralRequests.convert(item.id, selection);
      router.push(`/careconnect/referrals/${result.data.id}`);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Failed to accept pending request.");
      setAcceptingId(null);
    }
  }

  async function declineRequest(item: PendingReferralRequest) {
    setDecliningId(item.id);
    setError(null);
    try {
      await careConnectApi.pendingReferralRequests.decline(item.id);
      await load(page);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Failed to decline pending request.");
    } finally {
      setDecliningId(null);
    }
  }

  const totalPages = Math.max(1, Math.ceil(totalCount / PAGE_SIZE));
  const showingStart = totalCount === 0 ? 0 : (page - 1) * PAGE_SIZE + 1;
  const showingEnd = Math.min(page * PAGE_SIZE, totalCount);

  return (
    <div className="space-y-5">
      <div className="flex flex-col gap-3 md:flex-row md:items-end md:justify-between">
        <div>
          <h1 className="text-xl font-semibold text-gray-900">Pending referral requests</h1>
          <p className="mt-0.5 text-sm text-gray-500">Review associate-submitted requests before routing them to a provider.</p>
        </div>
        <Link href="/careconnect/referrals" className="text-xs text-gray-400 transition-colors hover:text-gray-600">
          View accepted referrals
        </Link>
      </div>

      {error && <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">{error}</div>}

      <div className="overflow-hidden rounded-lg border border-gray-200 bg-white">
        <div className="overflow-x-auto">
          <table className="min-w-full divide-y divide-gray-100">
            <thead>
              <tr className="bg-gray-50">
                <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Client</th>
                <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Provider</th>
                <th className="hidden px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500 sm:table-cell">Service</th>
                <th className="hidden px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500 md:table-cell">Urgency</th>
                <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Status</th>
                <th className="hidden px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500 lg:table-cell">Submitted</th>
                <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {loading ? (
                <tr><td colSpan={7} className="px-4 py-8 text-center text-sm text-gray-500">Loading pending requests...</td></tr>
              ) : items.length === 0 ? (
                <tr>
                  <td colSpan={7} className="px-4 py-12 text-center">
                    <p className="text-sm font-medium text-gray-500">No pending requests need review.</p>
                    <p className="mt-1 text-xs text-gray-400">New referral associate submissions will appear here.</p>
                  </td>
                </tr>
              ) : (
                items.map(item => {
                  const preference = firstPreferredProvider(item);
                  const busy = acceptingId === item.id || decliningId === item.id;
                  return (
                    <tr key={item.id} className="border-l-4 border-l-amber-400 bg-amber-50/20 transition-colors hover:bg-amber-50/50">
                      <td className="px-4 py-3">
                        <p className="max-w-[180px] truncate text-sm font-medium text-gray-900">{item.clientFirstName} {item.clientLastName}</p>
                        <p className="mt-0.5 text-xs text-gray-400">{formatPhoneDisplay(item.clientPhone)}</p>
                      </td>
                      <td className="px-4 py-3">
                        <p className="block max-w-[180px] truncate text-sm text-gray-700">{preference?.providerName || "No preference"}</p>
                        {preference?.facilityName && <p className="mt-0.5 max-w-[180px] truncate text-xs text-gray-400">{preference.facilityName}</p>}
                      </td>
                      <td className="hidden px-4 py-3 text-sm text-gray-600 sm:table-cell">
                        <span className="block max-w-[150px] truncate">{item.requestedService || "General referral"}</span>
                      </td>
                      <td className="hidden px-4 py-3 md:table-cell">
                        <UrgencyBadge urgency={item.urgency} />
                      </td>
                      <td className="px-4 py-3">
                        <PendingStatusBadge />
                      </td>
                      <td className="hidden whitespace-nowrap px-4 py-3 lg:table-cell">
                        <p className="text-xs text-gray-500">{formatDate(item.createdAtUtc)}</p>
                        {item.lienCompanyName && <p className="mt-0.5 max-w-[140px] truncate text-[11px] text-gray-400">{item.lienCompanyName}</p>}
                      </td>
                      <td className="px-4 py-3">
                        <DropdownMenu>
                          <DropdownMenuTrigger asChild>
                            <button
                              type="button"
                              title="Actions"
                              aria-label={`Actions for ${item.clientFirstName} ${item.clientLastName}`}
                              className="rounded p-1.5 text-gray-400 transition-colors hover:bg-gray-100 hover:text-gray-700 disabled:opacity-50"
                              disabled={busy}
                            >
                              <i className="ri-more-2-fill text-base" />
                            </button>
                          </DropdownMenuTrigger>
                          <DropdownMenuContent align="end" className="w-44">
                            <DropdownMenuItem onClick={() => router.push(`/careconnect/pending-requests/${item.id}`)}>
                              <i className="ri-eye-line text-base" />
                              View
                            </DropdownMenuItem>
                            <DropdownMenuItem onClick={() => void acceptRequest(item)}>
                              <i className="ri-checkbox-circle-line text-base" />
                              {acceptingId === item.id ? "Accepting..." : "Accept"}
                            </DropdownMenuItem>
                            <DropdownMenuSeparator />
                            <DropdownMenuItem onClick={() => void declineRequest(item)} className="text-red-700 focus:text-red-700">
                              <i className="ri-close-circle-line text-base" />
                              {decliningId === item.id ? "Declining..." : "Decline"}
                            </DropdownMenuItem>
                          </DropdownMenuContent>
                        </DropdownMenu>
                      </td>
                    </tr>
                  );
                })
              )}
            </tbody>
          </table>
        </div>

        <div className="flex flex-col gap-3 border-t border-gray-100 px-4 py-3 sm:flex-row sm:items-center sm:justify-between">
          <span className="text-xs text-gray-400">
            Showing {showingStart}-{showingEnd} of {totalCount.toLocaleString()}
          </span>
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={() => setPage(p => Math.max(1, p - 1))}
              disabled={page <= 1 || loading}
              className="text-xs text-primary hover:underline disabled:cursor-not-allowed disabled:text-gray-300 disabled:no-underline"
            >
              ← Previous
            </button>
            <span className="text-xs text-gray-400">Page {page} of {totalPages}</span>
            <button
              type="button"
              onClick={() => setPage(p => Math.min(totalPages, p + 1))}
              disabled={page >= totalPages || loading}
              className="text-xs text-primary hover:underline disabled:cursor-not-allowed disabled:text-gray-300 disabled:no-underline"
            >
              Next →
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
