import { serverApi } from '@/lib/server-api-client';
import type {
  ProviderSummary,
  ProviderDetail,
  ProviderMarker,
  ProviderSearchParams,
  ProviderAvailabilityResponse,
  AvailabilitySearchParams,
  ReferralSummary,
  ReferralDetail,
  ReferralComment,
  ActivationRequestSummary,
  ActivationRequestDetail,
  ReferralSearchParams,
  AppointmentSummary,
  AppointmentDetail,
  AppointmentSearchParams,
  PagedResponse,
  ActivationFunnelMetrics,
  ProviderReadinessDiagnostics,
  ProvisionCareConnectResult,
  ProviderActivationResult,
  DashboardMetrics,
  BlockedProviderLogPage,
  AdminReferralPage,
  ReferralPerformanceResult,
  NetworkSummary,
  NetworkDetail,
  NetworkProviderMarker,
  NetworkReferralPage,
  SpecialtyOption,
  LawFirmUserSummary,
} from '@/types/careconnect';
import type { OrgTypeValue } from '@/types';

// ── Helpers ───────────────────────────────────────────────────────────────────

function toQs(params: Record<string, unknown>): string {
  const pairs = Object.entries(params)
    .filter(([, v]) => v !== undefined && v !== null && v !== '')
    .map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(String(v))}`);
  return pairs.length ? `?${pairs.join('&')}` : '';
}

// ── Server-side API ───────────────────────────────────────────────────────────
// Use in Server Components and Server Actions ONLY.
// Reads the platform_session cookie and calls the gateway directly (no extra hop).
// DO NOT import this in Client Components — use careconnect-api.ts instead.

export const careConnectServerApi = {
  specialties: {
    list: () =>
      serverApi.get<SpecialtyOption[]>(`/careconnect/api/specialties`),
  },

  access: {
    getMyProductAccess: (productCode: string) =>
      serverApi.get<Array<{
        id: string;
        tenantId: string;
        tenantCode: string;
        tenantName: string;
        tenantSubdomain?: string | null;
        productCode: string;
        accessStatus: string;
        grantedAtUtc?: string | null;
        organizationId?: string | null;
        organizationName?: string | null;
        organizationType?: OrgTypeValue | null;
      }>>(`/identity/api/auth/my-product-access${toQs({ productCode })}`),
  },

  providers: {
    search: (params: ProviderSearchParams = {}) =>
      serverApi.get<PagedResponse<ProviderSummary>>(
        `/careconnect/api/providers${toQs(params as Record<string, unknown>)}`,
      ),

    getById: (id: string) =>
      serverApi.get<ProviderDetail>(`/careconnect/api/providers/${id}`),

    getMarkers: (params: ProviderSearchParams = {}) =>
      serverApi.get<ProviderMarker[]>(
        `/careconnect/api/providers/map${toQs(params as Record<string, unknown>)}`,
      ),

    getAvailability: (id: string, params: AvailabilitySearchParams = {}) =>
      serverApi.get<ProviderAvailabilityResponse>(
        `/careconnect/api/providers/${id}/availability${toQs(params as Record<string, unknown>)}`,
      ),
  },

  referrals: {
    search: (params: ReferralSearchParams = {}) =>
      serverApi.get<PagedResponse<ReferralSummary>>(
        `/careconnect/api/referrals${toQs(params as Record<string, unknown>)}`,
      ),

    getById: (id: string) =>
      serverApi.get<ReferralDetail>(`/careconnect/api/referrals/${id}`),

    getComments: (id: string) =>
      serverApi.get<ReferralComment[]>(`/careconnect/api/referrals/${id}/comments`),
  },

  appointments: {
    search: (params: AppointmentSearchParams = {}) =>
      serverApi.get<PagedResponse<AppointmentSummary>>(
        `/careconnect/api/appointments${toQs(params as Record<string, unknown>)}`,
      ),

    getById: (id: string) =>
      serverApi.get<AppointmentDetail>(`/careconnect/api/appointments/${id}`),
  },

  // LSCC-009: Admin activation queue (server-side only — requires admin session)
  adminActivations: {
    getPending: () =>
      serverApi.get<{ items: ActivationRequestSummary[]; count: number }>(
        `/careconnect/api/admin/activations`,
      ),

    getById: (id: string) =>
      serverApi.get<ActivationRequestDetail>(
        `/careconnect/api/admin/activations/${id}`,
      ),
  },

  // LSCC-011: Activation funnel analytics (admin-only)
  analytics: {
    getFunnel: (params: { days?: number; startDate?: string; endDate?: string } = {}) =>
      serverApi.get<ActivationFunnelMetrics>(
        `/careconnect/api/admin/analytics/funnel${toQs(params as Record<string, unknown>)}`,
      ),
  },

  // LSCC-01-003: Admin CareConnect receiver provisioning (server-side only)
  adminProvisioning: {
    // GET /api/admin/users/{userId}/careconnect-readiness  (Identity service)
    getReadiness: (userId: string) =>
      serverApi.get<ProviderReadinessDiagnostics>(
        `/identity/api/admin/users/${userId}/careconnect-readiness`,
      ),

    // POST /api/admin/users/{userId}/provision-careconnect  (Identity service)
    provision: (userId: string) =>
      serverApi.post<ProvisionCareConnectResult>(
        `/identity/api/admin/users/${userId}/provision-careconnect`,
        {},
      ),

    // POST /api/admin/providers/{providerId}/activate-for-careconnect  (CareConnect service)
    activateProvider: (providerId: string) =>
      serverApi.post<ProviderActivationResult>(
        `/careconnect/api/admin/providers/${providerId}/activate-for-careconnect`,
        {},
      ),
  },

  // LSCC-01-004: Admin dashboard, blocked-provider queue, referral monitor (server-side only)
  adminDashboard: {
    // GET /api/admin/dashboard — aggregate operational metrics
    getMetrics: () =>
      serverApi.get<DashboardMetrics>(`/careconnect/api/admin/dashboard`),

    // GET /api/admin/providers/blocked — paged blocked-access log
    getBlockedProviders: (params: { page?: number; pageSize?: number; since?: string } = {}) =>
      serverApi.get<BlockedProviderLogPage>(
        `/careconnect/api/admin/providers/blocked${toQs(params as Record<string, unknown>)}`,
      ),

    // GET /api/admin/referrals — cross-tenant referral monitor
    getReferrals: (params: {
      page?:     number;
      pageSize?: number;
      status?:   string;
      tenantId?: string;
      since?:    string;
      createdFrom?: string;
      createdTo?: string;
    } = {}) =>
      serverApi.get<AdminReferralPage>(
        `/careconnect/api/admin/referrals${toQs(params as Record<string, unknown>)}`,
      ),

    getReferralById: (id: string) =>
      serverApi.get<ReferralDetail>(`/careconnect/api/admin/referrals/${id}`),
  },

  adminAppointments: {
    search: (params: {
      page?: number;
      pageSize?: number;
      status?: string;
      tenantId?: string;
      providerId?: string;
      from?: string;
      to?: string;
    } = {}) =>
      serverApi.get<PagedResponse<AppointmentSummary>>(
        `/careconnect/api/admin/appointments${toQs(params as Record<string, unknown>)}`,
      ),
  },

  // LSCC-01-005: Referral performance metrics (admin-only, server-side)
  adminPerformance: {
    // GET /api/admin/performance — summary, aging distribution, provider rows
    getMetrics: (params: { days?: number; since?: string } = {}) =>
      serverApi.get<ReferralPerformanceResult>(
        `/careconnect/api/admin/performance${toQs(params as Record<string, unknown>)}`,
      ),
  },

  // CC2-INT-B09: Provider tenant self-onboarding (server-side)
  onboarding: {
    /** GET /api/provider/onboarding/status — check if authenticated provider can self-onboard */
    getStatus: () =>
      serverApi.get<{ providerId: string; accessStage: string; canOnboard: boolean }>(
        `/careconnect/api/provider/onboarding/status`,
      ),
  },

  // CC2-INT-B06: Provider networks (server-side data fetching for RSC pages)
  networks: {
    /** GET /api/networks — list tenant networks */
    list: () =>
      serverApi.get<NetworkSummary[]>(`/careconnect/api/networks`),

    /** GET /api/networks/{id} — get network detail with provider list */
    getById: (id: string) =>
      serverApi.get<NetworkDetail>(`/careconnect/api/networks/${id}`),

    /** GET /api/networks/{id}/providers/markers — map markers for providers in network */
    getMarkers: (id: string) =>
      serverApi.get<NetworkProviderMarker[]>(`/careconnect/api/networks/${id}/providers/markers`),
  },

  // CC-REFERRER-BROWSE: Read-only network directory for law firm referrers
  browseNetworks: {
    /** GET /api/networks/directory — list all active networks (referrer-accessible) */
    list: () =>
      serverApi.get<NetworkSummary[]>(`/careconnect/api/networks/directory`),

    /** GET /api/networks/directory/{id} — get network detail with provider list */
    getById: (id: string) =>
      serverApi.get<NetworkDetail>(`/careconnect/api/networks/directory/${id}`),

    /** GET /api/networks/directory/{id}/markers — map markers for network providers */
    getMarkers: (id: string) =>
      serverApi.get<NetworkProviderMarker[]>(`/careconnect/api/networks/directory/${id}/markers`),
  },

  // LSV3-1083: Law Firm Company Super Admin/Manager — server-side initial fetch
  // for the /careconnect/law-firm-users page.
  lawFirmUsers: {
    list: () =>
      serverApi.get<LawFirmUserSummary[]>(`/careconnect/api/law-firm-users`),
  },

  // Network Referral Monitor — lien company / network manager view of all referrals
  networkReferrals: {
    /** GET /api/network/referrals — all referrals in the network, grouped by law firm */
    list: (params: {
      page?:     number;
      pageSize?: number;
      status?:   string;
      search?:   string;
    } = {}) =>
      serverApi.get<NetworkReferralPage>(
        `/careconnect/api/network/referrals${toQs(params as Record<string, unknown>)}`,
      ),
  },
};
