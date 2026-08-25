import { apiClient } from '@/lib/api-client';
import { appendCareConnectMessageFiles, type SelectedCareConnectMessageFile } from '@/lib/careconnect-message-attachments';
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
  ReferralHistoryItem,
  ReferralNotification,
  ReferralAuditEvent,
  CreateReferralRequest,
  CreateReferralCommentRequest,
  ReferralSearchParams,
  AppointmentSummary,
  AppointmentDetail,
  CreateAppointmentRequest,
  AppointmentSearchParams,
  PagedResponse,
  AttachmentSummary,
  SignedUrlResponse,
  NetworkSummary,
  NetworkDetail,
  NetworkProviderItem,
  UpdateNetworkRequest,
  NetworkProviderMarker,
  ProviderSearchResult,
  AddProviderToNetworkRequest,
  UpdateNetworkProviderRequest,
  SpecialtyOption,
  ReferralAttribution,
  ReferralAttributionSummary,
  CreateReferralAttributionRequest,
  UpdateReferralAttributionRequest,
  ReferralAttributionAccessCode,
  GeneratedReferralAttributionAccessCode,
  CreateReferralAttributionAccessCodeRequest,
  PendingReferralRequest,
  ConvertPendingReferralRequest,
  UpdatePendingReferralRequest,
  TreatmentTypeOption,
  LawFirmUserSummary,
  InviteLawFirmUserRequest,
  LawFirmUserInviteResult,
} from '@/types/careconnect';

// ── Helpers ───────────────────────────────────────────────────────────────────

/** Converts a params object to a query string, dropping undefined/empty values */
function toQs(params: Record<string, unknown>): string {
  const pairs = Object.entries(params)
    .filter(([, v]) => v !== undefined && v !== null && v !== '')
    .map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(String(v))}`);
  return pairs.length ? `?${pairs.join('&')}` : '';
}

// ── Client-side API ───────────────────────────────────────────────────────────
// Use in Client Components (forms, interactive UI).
// Calls /api/careconnect/* which routes through the BFF proxy → gateway.

export const careConnectApi = {
  specialties: {
    list: () =>
      apiClient.get<SpecialtyOption[]>(`/careconnect/api/specialties`),
  },

  treatmentTypes: {
    list: () =>
      apiClient.get<TreatmentTypeOption[]>(`/careconnect/api/treatment-types`),
  },

  providers: {
    search: (params: ProviderSearchParams = {}) =>
      apiClient.get<PagedResponse<ProviderSummary>>(
        `/careconnect/api/providers${toQs(params as Record<string, unknown>)}`,
      ),

    getById: (id: string) =>
      apiClient.get<ProviderDetail>(`/careconnect/api/providers/${id}`),

    getMarkers: (params: ProviderSearchParams = {}) =>
      apiClient.get<ProviderMarker[]>(
        `/careconnect/api/providers/map${toQs(params as Record<string, unknown>)}`,
      ),

    getAvailability: (id: string, params: AvailabilitySearchParams = {}) =>
      apiClient.get<ProviderAvailabilityResponse>(
        `/careconnect/api/providers/${id}/availability${toQs(params as Record<string, unknown>)}`,
      ),
  },

  referrals: {
    create: (body: CreateReferralRequest) =>
      apiClient.post<ReferralDetail>('/careconnect/api/referrals', body),

    search: (params: ReferralSearchParams = {}) =>
      apiClient.get<PagedResponse<ReferralSummary>>(
        `/careconnect/api/referrals${toQs(params as Record<string, unknown>)}`,
      ),

    getById: (id: string) =>
      apiClient.get<ReferralDetail>(`/careconnect/api/referrals/${id}`),

    getComments: (id: string) =>
      apiClient.get<ReferralComment[]>(`/careconnect/api/referrals/${id}/comments`),

    postComment: (id: string, body: CreateReferralCommentRequest) =>
      apiClient.post<ReferralComment>(`/careconnect/api/referrals/${id}/comments`, body),

    postCommentWithAttachments: (
      id: string,
      message: string,
      files: SelectedCareConnectMessageFile[],
    ) => {
      const form = new FormData();
      form.append('message', message);
      appendCareConnectMessageFiles(form, files);
      return apiClient.postForm<ReferralComment>(`/careconnect/api/referrals/${id}/comments`, form);
    },

    /** PUT /api/referrals/{id} — update status, fields, or treatment type. Omit status for treatment-type-only updates. */
    update: (id: string, body: { requestedService?: string; urgency: string; status?: string; notes?: string; declineNotes?: string; treatmentTypeId?: string | null }) =>
      apiClient.put<ReferralDetail>(`/careconnect/api/referrals/${id}`, body),

    /** GET /api/referrals/{id}/history — status change audit log */
    getHistory: (id: string) =>
      apiClient.get<ReferralHistoryItem[]>(`/careconnect/api/referrals/${id}/history`),

    /**
     * POST /api/referrals/{id}/accept-by-token — PUBLIC (no auth).
     * Accepts a referral using a secure HMAC view token.
     */
    acceptByToken: (id: string, token: string) =>
      apiClient.post<void>(`/careconnect/api/referrals/${id}/accept-by-token`, { token }),

    // LSCC-005-01: hardening endpoints

    /** GET /api/referrals/{id}/notifications — email delivery history */
    getNotifications: (id: string) =>
      apiClient.get<ReferralNotification[]>(`/careconnect/api/referrals/${id}/notifications`),

    /** POST /api/referrals/{id}/resend-email — re-send provider notification */
    resendEmail: (id: string) =>
      apiClient.post<ReferralDetail>(`/careconnect/api/referrals/${id}/resend-email`, {}),

    /** POST /api/referrals/{id}/revoke-token — revoke all existing view tokens */
    revokeToken: (id: string) =>
      apiClient.post<ReferralDetail>(`/careconnect/api/referrals/${id}/revoke-token`, {}),

    /** GET /api/referrals/{id}/audit — operational audit timeline (LSCC-005-02) */
    getAuditTimeline: (id: string) =>
      apiClient.get<ReferralAuditEvent[]>(`/careconnect/api/referrals/${id}/audit`),
  },

  // ── Referral Origination configuration (tenant admin) ────────────────────────
  referralAttributions: {
    /** GET /api/referral-attributions/options — active options for the current tenant (law firm dropdown) */
    options: () =>
      apiClient.get<ReferralAttributionSummary[]>(`/careconnect/api/referral-attributions/options`),

    /** GET /api/referral-attributions — full admin list, optionally activeOnly */
    list: (activeOnly?: boolean) =>
      apiClient.get<ReferralAttribution[]>(
        `/careconnect/api/referral-attributions${activeOnly !== undefined ? `?activeOnly=${activeOnly}` : ''}`,
      ),

    getById: (id: string) =>
      apiClient.get<ReferralAttribution>(`/careconnect/api/referral-attributions/${id}`),

    create: (body: CreateReferralAttributionRequest) =>
      apiClient.post<ReferralAttribution>(`/careconnect/api/referral-attributions`, body),

    update: (id: string, body: UpdateReferralAttributionRequest) =>
      apiClient.patch<ReferralAttribution>(`/careconnect/api/referral-attributions/${id}`, body),

    setActive: (id: string, isActive: boolean) =>
      apiClient.patch<ReferralAttribution>(`/careconnect/api/referral-attributions/${id}/active`, { isActive }),
  },

  // ── Referral Representative access codes (tenant admin) ──────────────────────
  // Admin generates a code scoped to one origination; the representative enters it
  // themselves at the anonymous portal — see representative-portal-api.ts, not this
  // file. No admin-typed user linking. Exactly one active code per origination —
  // scoped lookup only, no cross-origination list.
  referralAttributionAccessCodes: {
    /** GET .../by-attribution/{id} — the origination's single active code, or undefined (204) if none. */
    getByAttribution: (referralAttributionId: string) =>
      apiClient.get<ReferralAttributionAccessCode | undefined>(
        `/careconnect/api/referral-representative-access-codes/by-attribution/${referralAttributionId}`,
      ),

    generate: (body: CreateReferralAttributionAccessCodeRequest) =>
      apiClient.post<GeneratedReferralAttributionAccessCode>(`/careconnect/api/referral-representative-access-codes`, body),

    setActive: (id: string, isActive: boolean) =>
      apiClient.patch<ReferralAttributionAccessCode>(`/careconnect/api/referral-representative-access-codes/${id}/active`, { isActive }),
  },

  // Referral Representative Portal is fully anonymous (no login) — see
  // apps/web/src/lib/representative-portal-api.ts for its client, not this file.

  adminReferrals: {
    getHistory: (id: string) =>
      apiClient.get<ReferralHistoryItem[]>(`/careconnect/api/admin/referrals/${id}/history`),

    listAttachments: (referralId: string) =>
      apiClient.get<AttachmentSummary[]>(`/careconnect/api/admin/referrals/${referralId}/attachments`),

    getAttachmentSignedUrl: (referralId: string, attachmentId: string, download = false) =>
      apiClient.get<SignedUrlResponse>(
        `/careconnect/api/admin/referrals/${referralId}/attachments/${attachmentId}/url${download ? '?download=true' : ''}`,
      ),
  },

  appointments: {
    create: (body: CreateAppointmentRequest) =>
      apiClient.post<AppointmentDetail>('/careconnect/api/appointments', body),

    search: (params: AppointmentSearchParams = {}) =>
      apiClient.get<PagedResponse<AppointmentSummary>>(
        `/careconnect/api/appointments${toQs(params as Record<string, unknown>)}`,
      ),

    getById: (id: string) =>
      apiClient.get<AppointmentDetail>(`/careconnect/api/appointments/${id}`),

    /** POST /api/appointments/{id}/confirm */
    confirm: (id: string, body: { notes?: string } = {}) =>
      apiClient.post<AppointmentDetail>(`/careconnect/api/appointments/${id}/confirm`, body),

    /** POST /api/appointments/{id}/complete */
    complete: (id: string, body: { notes?: string } = {}) =>
      apiClient.post<AppointmentDetail>(`/careconnect/api/appointments/${id}/complete`, body),

    /** POST /api/appointments/{id}/cancel */
    cancel: (id: string, body: { notes?: string } = {}) =>
      apiClient.post<AppointmentDetail>(`/careconnect/api/appointments/${id}/cancel`, body),

    /** PUT /api/appointments/{id} — update status (NoShow, etc.) */
    update: (id: string, body: { status: string; notes?: string }) =>
      apiClient.put<AppointmentDetail>(`/careconnect/api/appointments/${id}`, body),

    /** POST /api/appointments/{id}/reschedule */
    reschedule: (id: string, body: { newAppointmentSlotId: string; notes?: string }) =>
      apiClient.post<AppointmentDetail>(`/careconnect/api/appointments/${id}/reschedule`, body),
  },

  // CC2-INT-B03: Attachment endpoints — server-side upload proxy + signed URLs

  referralAttachments: {
    /** GET /api/referrals/{id}/attachments — list all attachments for a referral */
    list: (referralId: string) =>
      apiClient.get<AttachmentSummary[]>(`/careconnect/api/referrals/${referralId}/attachments`),

    /** POST /api/referrals/{id}/attachments/upload — upload a file via multipart/form-data */
    upload: (referralId: string, file: File, options: { scope?: string; notes?: string } = {}) => {
      const form = new FormData();
      form.append('file', file, file.name);
      if (options.scope) form.append('scope', options.scope);
      if (options.notes) form.append('notes', options.notes);
      return apiClient.postForm<AttachmentSummary>(
        `/careconnect/api/referrals/${referralId}/attachments/upload`,
        form,
      );
    },

    /** GET /api/referrals/{id}/attachments/{attachmentId}/url — get a short-lived signed URL */
    getSignedUrl: (referralId: string, attachmentId: string, download = false) =>
      apiClient.get<SignedUrlResponse>(
        `/careconnect/api/referrals/${referralId}/attachments/${attachmentId}/url${download ? '?download=true' : ''}`,
      ),
  },

  appointmentAttachments: {
    /** GET /api/appointments/{id}/attachments — list all attachments for an appointment */
    list: (appointmentId: string) =>
      apiClient.get<AttachmentSummary[]>(`/careconnect/api/appointments/${appointmentId}/attachments`),

    /** POST /api/appointments/{id}/attachments/upload — upload a file via multipart/form-data */
    upload: (appointmentId: string, file: File, options: { scope?: string; notes?: string } = {}) => {
      const form = new FormData();
      form.append('file', file, file.name);
      if (options.scope) form.append('scope', options.scope);
      if (options.notes) form.append('notes', options.notes);
      return apiClient.postForm<AttachmentSummary>(
        `/careconnect/api/appointments/${appointmentId}/attachments/upload`,
        form,
      );
    },

    /** GET /api/appointments/{id}/attachments/{attachmentId}/url — get a short-lived signed URL */
    getSignedUrl: (appointmentId: string, attachmentId: string, download = false) =>
      apiClient.get<SignedUrlResponse>(
        `/careconnect/api/appointments/${appointmentId}/attachments/${attachmentId}/url${download ? '?download=true' : ''}`,
      ),
  },

  // CC2-INT-B06: Provider networks (client-side mutations from interactive pages)
  networks: {
    /**
     * GET /api/networks — single-tenant-network cutover: lists (and bootstraps, on
     * first call for a tenant) the tenant's one shared network. There is no longer a
     * separate "create a network" action — every tenant gets exactly one automatically.
     */
    list: () =>
      apiClient.get<NetworkSummary[]>(`/careconnect/api/networks`),

    /** PUT /api/networks/{id} — update network name/description (tenant admin only) */
    update: (id: string, data: UpdateNetworkRequest) =>
      apiClient.put<NetworkSummary>(`/careconnect/api/networks/${id}`, data),

    /**
     * CC2-INT-B06-01: Search the shared global provider registry.
     * GET /api/networks/{id}/providers/search?name=&phone=&npi=&city=
     */
    searchProviders: (networkId: string, params: { name?: string; phone?: string; npi?: string; city?: string }) => {
      const qs = new URLSearchParams();
      if (params.name)  qs.set('name',  params.name);
      if (params.phone) qs.set('phone', params.phone);
      if (params.npi)   qs.set('npi',   params.npi);
      if (params.city)  qs.set('city',  params.city);
      return apiClient.get<ProviderSearchResult[]>(
        `/careconnect/api/networks/${networkId}/providers/search?${qs.toString()}`
      );
    },

    /**
     * CC2-INT-B06-01: Add a provider/location to a network.
     * POST /api/networks/{id}/providers — body: { existingProviderId, existingFacilityId } |
     * { existingProviderId, newProvider: {...location fields...} } | { newProvider: {...new provider...} }
     */
    addProvider: (networkId: string, request: AddProviderToNetworkRequest) =>
      apiClient.post<NetworkProviderItem>(`/careconnect/api/networks/${networkId}/providers`, request),

    /** PUT /api/networks/{id}/providers/{providerId} — edit shared provider through this network */
    updateProvider: (networkId: string, providerId: string, request: UpdateNetworkProviderRequest) =>
      apiClient.put<NetworkProviderItem>(`/careconnect/api/networks/${networkId}/providers/${providerId}`, request),

    /**
     * DELETE /api/networks/{id}/providers/{providerId} — removes association only.
     * cascadeFacility: true only for "Delete location" (Facilities panel) — also tags the
     * underlying Facility inactive. The "Remove from network" icon omits it, keeping its
     * original membership-only soft delete.
     */
    removeProvider: (networkId: string, providerId: string, cascadeFacility = false) =>
      apiClient.delete<void>(`/careconnect/api/networks/${networkId}/providers/${providerId}${cascadeFacility ? '?cascadeFacility=true' : ''}`),

    /** GET /api/networks/{id}/providers/markers — map markers for the network */
    getMarkers: (id: string) =>
      apiClient.get<NetworkProviderMarker[]>(`/careconnect/api/networks/${id}/providers/markers`),
  },

  // LSV3-1083: Law Firm Company Super Admin/Manager — a CareConnectReferrerAdmin
  // manages the users belonging to their own law firm. Always scoped to the
  // caller's own organization server-side — there is no orgId parameter here.
  lawFirmUsers: {
    list: () =>
      apiClient.get<LawFirmUserSummary[]>(`/careconnect/api/law-firm-users`),

    invite: (request: InviteLawFirmUserRequest) =>
      apiClient.post<LawFirmUserInviteResult>(`/careconnect/api/law-firm-users/invite`, request),

    resendInvite: (userId: string) =>
      apiClient.post<void>(`/careconnect/api/law-firm-users/${userId}/resend-invite`, {}),

    activate: (userId: string) =>
      apiClient.post<void>(`/careconnect/api/law-firm-users/${userId}/activate`, {}),

    deactivate: (userId: string) =>
      apiClient.post<void>(`/careconnect/api/law-firm-users/${userId}/deactivate`, {}),

    assignRole: (userId: string, roleCode: string) =>
      apiClient.post<{ assignmentId: string }>(`/careconnect/api/law-firm-users/${userId}/roles`, { roleCode }),

    revokeRole: (userId: string, assignmentId: string) =>
      apiClient.delete<void>(`/careconnect/api/law-firm-users/${userId}/roles/${assignmentId}`),
  },

  pendingReferralRequests: {
    search: (params: { status?: string; page?: number; pageSize?: number } = {}) =>
      apiClient.get<PagedResponse<PendingReferralRequest>>(
        `/careconnect/api/pending-referral-requests${toQs(params as Record<string, unknown>)}`,
      ),
    getById: (id: string) =>
      apiClient.get<PendingReferralRequest>(`/careconnect/api/pending-referral-requests/${id}`),
    update: (id: string, body: UpdatePendingReferralRequest) =>
      apiClient.put<PendingReferralRequest>(`/careconnect/api/pending-referral-requests/${id}`, body),
    decline: (id: string) =>
      apiClient.post<PendingReferralRequest>(`/careconnect/api/pending-referral-requests/${id}/decline`, {}),
    uploadAttachment: (id: string, file: File) => {
      const form = new FormData();
      form.append('file', file, file.name);
      return apiClient.postForm<AttachmentSummary>(
        `/careconnect/api/pending-referral-requests/${id}/attachments/upload`,
        form,
      );
    },
    getAttachmentSignedUrl: (id: string, attachmentId: string, download = false) =>
      apiClient.get<SignedUrlResponse>(
        `/careconnect/api/pending-referral-requests/${id}/attachments/${attachmentId}/url${download ? '?download=true' : ''}`,
      ),
    convert: (id: string, body: ConvertPendingReferralRequest) =>
      apiClient.post<ReferralDetail>(`/careconnect/api/pending-referral-requests/${id}/convert`, body),
  },

  // CC2-INT-B09: Provider tenant self-onboarding
  onboarding: {
    /**
     * GET /api/provider/onboarding/check-code?code=xxx
     * Checks whether the given tenant code is available.
     */
    checkCode: (code: string) =>
      apiClient.get<{ available: boolean; normalizedCode: string; message?: string }>(
        `/careconnect/api/provider/onboarding/check-code?code=${encodeURIComponent(code)}`,
      ),

    /**
     * POST /api/provider/onboarding/provision-tenant
     * Transitions the authenticated COMMON_PORTAL provider to TENANT stage.
     */
    provisionTenant: (body: { tenantName: string; tenantCode: string }) =>
      apiClient.post<{
        providerId:         string;
        tenantId:           string;
        tenantCode:         string;
        subdomain:          string;
        provisioningStatus: string;
        portalUrl:          string | null;
        message:            string;
      }>(`/careconnect/api/provider/onboarding/provision-tenant`, body),
  },
};
