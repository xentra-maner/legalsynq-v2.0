/**
 * Client for the anonymous Referral Portal. Every call here goes through
 * the generic public CareConnect BFF proxy (apps/web/src/app/api/public/careconnect/[...path]/route.ts),
 * which resolves the tenant server-side from the request Host header and signs it for
 * CareConnect's trust boundary — never trust-boundary logic here, this file only builds
 * URLs and passes the raw access code along as a query parameter.
 *
 * There is no session and nothing is cached client-side beyond the raw code itself (see
 * representative-access-code-gate.tsx) — every read below re-sends the code and the
 * backend re-verifies it from scratch on every single call.
 */
import { apiClient } from '@/lib/api-client';
import type {
  PagedResponse,
  RepresentativeReferralListItem,
  RepresentativeReferralDetail,
  RepresentativeReferralMetrics,
  RepresentativeReferralSearchParams,
  VerifyReferralAttributionAccessCodeResult,
  LawFirmOption,
  TreatmentTypeOption,
  CreatePendingReferralRequest,
  PendingReferralRequest,
  ProviderSummary,
  ProviderMarker,
  ProviderSearchParams,
} from '@/types/careconnect';

const BASE = '/public/careconnect/api/public/referral-portal';

function qs(params: Record<string, string | number | boolean | undefined>): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== '') search.set(key, String(value));
  }
  const s = search.toString();
  return s ? `?${s}` : '';
}

export async function verifyRepresentativeAccessCode(code: string) {
  return apiClient.post<VerifyReferralAttributionAccessCodeResult>(`${BASE}/verify`, { code });
}

export async function fetchRepresentativeReferrals(
  code: string,
  params: RepresentativeReferralSearchParams = {},
) {
  return apiClient.get<PagedResponse<RepresentativeReferralListItem>>(
    `${BASE}/referrals${qs({ ...params, code })}`,
  );
}

export async function fetchRepresentativeReferralById(code: string, referralId: string) {
  return apiClient.get<RepresentativeReferralDetail>(
    `${BASE}/referrals/${referralId}${qs({ code })}`,
  );
}

export async function fetchRepresentativeMetrics(code: string, from?: string, to?: string) {
  return apiClient.get<RepresentativeReferralMetrics>(
    `${BASE}/referral-metrics${qs({ code, from, to })}`,
  );
}

export async function fetchReferralPortalLawFirms(code: string) {
  return apiClient.get<LawFirmOption[]>(`${BASE}/law-firms${qs({ code })}`);
}

export async function fetchReferralPortalTreatmentTypes() {
  return apiClient.get<TreatmentTypeOption[]>('/public/careconnect/api/public/treatment-types');
}

export async function fetchReferralPortalProviders(
  code: string,
  params: ProviderSearchParams = {},
) {
  return apiClient.get<PagedResponse<ProviderSummary>>(
    `${BASE}/providers${qs({ ...params, code })}`,
  );
}

export async function fetchReferralPortalProviderMarkers(
  code: string,
  params: ProviderSearchParams = {},
) {
  return apiClient.get<ProviderMarker[]>(
    `${BASE}/providers/map${qs({ ...params, code })}`,
  );
}

export async function createPendingReferralRequest(code: string, body: CreatePendingReferralRequest) {
  return apiClient.post<PendingReferralRequest>(`${BASE}/pending-referrals${qs({ code })}`, body);
}
