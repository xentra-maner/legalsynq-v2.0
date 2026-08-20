"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { careConnectApi } from "@/lib/careconnect-api";
import { ApiError } from "@/lib/api-client";
import type { PendingReferralProviderPreference, PendingReferralRequest, ProviderSummary } from "@/types/careconnect";

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

function firstPreferredProvider(item?: PendingReferralRequest): PendingReferralProviderPreference | null {
  if (!item) return null;
  return preferredProvidersFor(item)[0] ?? null;
}

export default function PendingReferralRequestsPage() {
  const [items, setItems] = useState<PendingReferralRequest[]>([]);
  const [providers, setProviders] = useState<ProviderSummary[]>([]);
  const [selectedProviderByRequest, setSelectedProviderByRequest] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [convertingId, setConvertingId] = useState<string | null>(null);

  async function load() {
    setLoading(true);
    setError(null);
    try {
      const [pendingRes, providerRes] = await Promise.all([
        careConnectApi.pendingReferralRequests.search({ status: "PendingReview", pageSize: 50 }),
        careConnectApi.providers.search({ pageSize: 100 }),
      ]);
      setItems(pendingRes.data.items);
      setProviders(providerRes.data.items);
      setSelectedProviderByRequest(prev => {
        const next = { ...prev };
        for (const item of pendingRes.data.items) {
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

  useEffect(() => { void load(); }, []);

  async function convert(requestId: string) {
    const request = items.find(item => item.id === requestId);
    const preference = firstPreferredProvider(request);
    const selection = parseProviderSelection(
      selectedProviderByRequest[requestId] ||
      providerSelectionValue(preference?.providerId, preference?.facilityId),
    );
    if (!selection) {
      setError("Select a provider before converting the request.");
      return;
    }
    setConvertingId(requestId);
    setError(null);
    try {
      await careConnectApi.pendingReferralRequests.convert(requestId, selection);
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Failed to convert pending request.");
    } finally {
      setConvertingId(null);
    }
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-xl font-semibold text-gray-900">Pending Referral Requests</h1>
        <p className="mt-1 text-sm text-gray-500">Review associate-submitted requests and route approved requests to a provider.</p>
      </div>

      {error && <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">{error}</div>}
      {loading ? (
        <p className="text-sm text-gray-500">Loading...</p>
      ) : items.length === 0 ? (
        <div className="rounded-lg border border-gray-200 p-8 text-center text-sm text-gray-500">No pending referral requests.</div>
      ) : (
        <div className="overflow-hidden rounded-lg border border-gray-200">
          <table className="min-w-full divide-y divide-gray-200 text-sm">
            <thead className="bg-gray-50 text-left text-xs font-medium uppercase tracking-wide text-gray-500">
              <tr>
                <th className="px-4 py-3">Client</th>
                <th className="px-4 py-3">Request</th>
                <th className="px-4 py-3">Lien Company</th>
                <th className="px-4 py-3">Provider</th>
                <th className="px-4 py-3" />
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100 bg-white">
              {items.map(item => {
                const preferredProviders = preferredProvidersFor(item);
                return (
                <tr key={item.id}>
                  <td className="px-4 py-3">
                    <div className="font-medium text-gray-900">{item.clientFirstName} {item.clientLastName}</div>
                    <div className="text-xs text-gray-500">{item.clientPhone}</div>
                  </td>
                  <td className="px-4 py-3">
                    <div className="text-gray-900">{item.requestedService || "General referral"}</div>
                    <div className="text-xs text-gray-500">{item.urgency} · {new Date(item.createdAtUtc).toLocaleDateString()}</div>
                    {preferredProviders.length > 0 && (
                      <div className="mt-2 space-y-1 rounded-md bg-amber-50 px-2 py-1.5 text-xs text-amber-800">
                        <div className="font-semibold">Preferred providers</div>
                        {preferredProviders.map((preference, index) => (
                          <div key={preference.id} className="truncate">
                            {index + 1}. {preference.providerName}
                            {preference.facilityName ? ` · ${preference.facilityName}` : ""}
                          </div>
                        ))}
                      </div>
                    )}
                  </td>
                  <td className="px-4 py-3 text-gray-700">
                    {item.lienCompanyName || "—"}
                    {item.lienCompanyEmail && <div className="text-xs text-gray-500">{item.lienCompanyEmail}</div>}
                  </td>
                  <td className="px-4 py-3">
                    <select
                      value={
                        selectedProviderByRequest[item.id] ??
                        providerSelectionValue(preferredProviders[0]?.providerId, preferredProviders[0]?.facilityId)
                      }
                      onChange={event => setSelectedProviderByRequest(prev => ({ ...prev, [item.id]: event.target.value }))}
                      className="w-full min-w-[220px] rounded-md border border-gray-300 px-3 py-2 text-sm"
                    >
                      <option value="">Select provider</option>
                      {providers.map(provider => (
                        <option
                          key={`${provider.id}-${provider.facilityId ?? "provider"}`}
                          value={providerSelectionValue(provider.id, provider.facilityId)}
                        >
                          {provider.organizationName ?? provider.name}
                          {provider.markerSubtitle ? ` - ${provider.markerSubtitle}` : ""}
                          {preferredProviders.some(preference =>
                            preference.providerId === provider.id &&
                            (preference.facilityId ?? "") === (provider.facilityId ?? ""))
                            ? " (preferred)"
                            : ""}
                        </option>
                      ))}
                    </select>
                  </td>
                  <td className="px-4 py-3 text-right">
                    <button
                      onClick={() => void convert(item.id)}
                      disabled={convertingId === item.id}
                      className="rounded-md bg-[#0f1928] px-3 py-2 text-xs font-medium text-white disabled:opacity-60"
                    >
                      {convertingId === item.id ? "Converting..." : "Convert"}
                    </button>
                  </td>
                </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      <Link href="/careconnect/referrals" className="text-sm text-blue-600 hover:underline">View converted referrals</Link>
    </div>
  );
}
