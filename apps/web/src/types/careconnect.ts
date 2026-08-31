// ── Provider ──────────────────────────────────────────────────────────────────

export interface SpecialtyOption {
  id:          string;
  name:        string;
  code:        string;
  description?: string | null;
  isActive:    boolean;
}

export interface ProviderSummary {
  id:                 string;
  facilityId?:        string | null;
  name:               string;
  title?:             string | null;
  organizationName?:  string;
  email:              string;
  phone:              string;
  addressLine1?:      string;
  city:               string;
  state:              string;
  postalCode?:        string | null;
  isActive:           boolean;
  acceptingReferrals: boolean;
  categories:         string[];
  primaryCategory?:   string;
  specialties:        SpecialtyOption[];
  specialtyIds:       string[];
  primarySpecialty?:  string | null;
  primarySpecialtyId?: string | null;
  distanceMiles?:     number | null;
  displayLabel:       string;
  markerSubtitle:     string;
  hasGeoLocation:     boolean;
  latitude?:          number;
  longitude?:         number;
  isMobile:            boolean;
  serviceRadiusMiles?: number | null;
  serviceAreaLabel?:   string | null;
}

// ProviderDetail — same DTO as list (backend returns same shape for both)
export type ProviderDetail = ProviderSummary;

export interface ProviderSearchParams {
  name?:               string;
  categoryCode?:       string;
  specialtyCode?:      string;
  city?:               string;
  state?:              string;
  acceptingReferrals?: boolean;
  isActive?:           boolean;
  page?:               number;
  pageSize?:           number;
  latitude?:           number;
  longitude?:          number;
  radiusMiles?:        number;
  northLat?:           number;
  southLat?:           number;
  eastLng?:            number;
  westLng?:            number;
}

export interface ProviderMarker {
  id:                 string;
  facilityId?:        string | null;
  name:               string;
  title?:             string | null;
  organizationName?:  string;
  displayLabel:       string;
  markerSubtitle:     string;
  city:               string;
  state:              string;
  addressLine1:       string;
  postalCode?:        string | null;
  email:              string;
  phone:              string;
  acceptingReferrals: boolean;
  isActive:           boolean;
  latitude:           number;
  longitude:          number;
  geoPointSource?:    string;
  primaryCategory?:   string;
  categories:         string[];
  specialties:        SpecialtyOption[];
  primarySpecialty?:  string | null;
  primarySpecialtyId?: string | null;
  distanceMiles?:     number | null;
  isMobile:            boolean;
  serviceRadiusMiles?: number | null;
  serviceAreaLabel?:   string | null;
}

// ── Referral history ─────────────────────────────────────────────────────────

export interface ReferralHistoryItem {
  id:              string;
  referralId:      string;
  oldStatus:       string;
  newStatus:       string;
  changedByUserId?: string;
  changedAtUtc:    string;
  notes?:          string;
}

export interface ReferralComment {
  id:         string;
  senderType: string;
  senderName: string;
  message:    string;
  createdAtUtc: string;
  attachments?: ReferralMessageAttachment[];
}

export interface ReferralMessageAttachment {
  id:            string;
  fileName:      string;
  contentType:   string;
  fileSizeBytes: number;
  createdAtUtc?: string;
}

// ── Referral ──────────────────────────────────────────────────────────────────

export const ReferrerPortalAccessStatuses = {
  ActiveInTenant:          'active_in_tenant',
  ExistingUserOtherTenant: 'existing_user_other_tenant',
  NoAccount:               'no_account',
} as const;
export type ReferrerPortalAccessStatusValue =
  typeof ReferrerPortalAccessStatuses[keyof typeof ReferrerPortalAccessStatuses];

export const ReferralStatus = {
  New:        'New',
  NewOpened:  'NewOpened',
  Received:   'Received',
  Contacted:  'Contacted',
  Scheduled:  'Scheduled',
  Completed:  'Completed',
  Cancelled:  'Cancelled',
} as const;
export type ReferralStatusValue = typeof ReferralStatus[keyof typeof ReferralStatus];

export const ReferralUrgency = {
  Low:       'Low',
  Normal:    'Normal',
  Urgent:    'Urgent',
  Emergency: 'Emergency',
} as const;
export type ReferralUrgencyValue = typeof ReferralUrgency[keyof typeof ReferralUrgency];

export const URGENCY_OPTIONS: { value: ReferralUrgencyValue; label: string }[] = [
  { value: 'Low',       label: 'Low'       },
  { value: 'Normal',    label: 'Normal'    },
  { value: 'Urgent',    label: 'Urgent'    },
  { value: 'Emergency', label: 'Emergency' },
];

export interface ReferralSummary {
  id:               string;
  tenantId:         string;
  providerId:       string;
  providerName:     string;
  // Referral location — the specific facility this referral was routed to, falling back
  // to the provider's own address for legacy/single-location referrals.
  facilityId?:            string | null;
  facilityName?:           string | null;
  locationAddressLine1?:   string;
  locationCity?:           string;
  locationState?:          string;
  locationPostalCode?:     string;
  clientFirstName:  string;
  clientLastName:   string;
  clientDob?:       string;
  clientPhone:      string;
  clientEmail:      string;
  caseNumber?:       string;
  requestedService?: string;
  urgency:           string;
  status:            string;
  notes?:            string;
  declineNotes?:     string;
  origin?:           string;
  lienCompanyName?:  string | null;
  lienCompanyEmail?: string | null;
  dateOfAccident?:   string;
  createdAtUtc:      string;
  updatedAtUtc:      string;
  // LSCC-005-01: org context
  referringOrganizationId?: string;
  receivingOrganizationId?:  string;
  organizationRelationshipId?: string;
  referringOrganizationName?: string | null;
  referrerName?: string | null;
  referrerEmail?: string | null;
  /** Backend supplies the tenant name for the Network column, or '-' when unavailable. */
  networkName?: string | null;
  // Type of Treatment — set by Referrer at creation.
  treatmentTypeId?:   string;
  treatmentTypeName?: string;
  // Referral Origination — who or what originated this referral. Undefined/null = not set.
  referralAttribution?: ReferralAttributionSummary | null;
}

// ── Referral Origination ─────────────────────────────────────────────────────

export interface ReferralAttributionSummary {
  id:          string;
  firstName:   string;
  lastName:    string;
  isActive:    boolean;
}

export interface ReferralAttribution {
  id:                     string;
  tenantId:               string;
  firstName:              string;
  lastName:               string;
  code:                   string;
  description?:           string | null;
  isActive:               boolean;
  displayOrder?:          number | null;
  isUsed:                 boolean;
  activeAccessCodeCount:  number;
  createdAtUtc:           string;
  updatedAtUtc:           string;
}

export interface CreateReferralAttributionRequest {
  firstName:     string;
  lastName:      string;
  code:          string;
  description?:  string;
  isActive?:     boolean;
  displayOrder?: number;
}

export interface UpdateReferralAttributionRequest {
  firstName:     string;
  lastName:      string;
  description?:  string;
  displayOrder?: number;
}

// ── Referral Representative access codes ──────────────────────────────────────
// Replaces the earlier admin-typed user-linking model: an admin generates a code
// scoped to one origination and shares it out of band. There is no login and no
// "redeemer" — the Representative Portal is fully anonymous and re-checks the raw
// code on every request (see representative-portal-api.ts). No admin screen ever
// picks or types a specific user account.

export interface ReferralAttributionAccessCode {
  id:                              string;
  tenantId:                        string;
  referralAttributionId:           string;
  referralAttributionFullName?:    string | null;
  isActive:                        boolean;
  accessStartAtUtc?:               string | null;
  accessEndAtUtc?:                 string | null;
  createdAtUtc:                    string;
  updatedAtUtc:                    string;
}

/** Returned only from the generate call, only once — the plaintext code can never be retrieved again. */
export interface GeneratedReferralAttributionAccessCode extends ReferralAttributionAccessCode {
  code: string;
}

export interface CreateReferralAttributionAccessCodeRequest {
  referralAttributionId:  string;
  accessStartAtUtc?:      string;
  accessEndAtUtc?:        string;
}

// ── Referral Representative Portal (restricted DTOs) ──────────────────────────

export interface RepresentativeStatusRef {
  code:        string;
  displayName: string;
}

export interface RepresentativeDisplayRef {
  displayName: string;
}

export interface RepresentativeClientRef {
  firstName:    string;
  lastName:     string;
  dateOfBirth?: string | null;
  phone:        string;
  email?:       string | null;
}

export interface RepresentativeFacilityRef {
  name:         string;
  addressLine1: string;
  city:         string;
  state:        string;
  postalCode?:  string | null;
  phone?:       string | null;
  isMobile:            boolean;
  serviceRadiusMiles?: number | null;
  serviceAreaLabel?:   string | null;
}

export interface RepresentativeMilestone {
  code:          string;
  displayName:   string;
  occurredAtUtc: string;
}

export interface RepresentativeReferralListItem {
  referralId:          string;
  referenceNumber:     string;
  submittedAtUtc:      string;
  status:              RepresentativeStatusRef;
  lawFirm:             RepresentativeDisplayRef;
  provider:            RepresentativeDisplayRef;
  providerLocation?:   RepresentativeFacilityRef | null;
  client:              RepresentativeClientRef;
  referralAttribution: ReferralAttributionSummary & { id: string };
  lastUpdatedAtUtc:    string;
}

export interface RepresentativeReferralDetail {
  referralId:          string;
  referenceNumber:     string;
  submittedAtUtc:      string;
  status:              RepresentativeStatusRef;
  lawFirm:             RepresentativeDisplayRef;
  provider:            RepresentativeDisplayRef;
  providerLocation?:   RepresentativeFacilityRef | null;
  client:              RepresentativeClientRef;
  referralAttribution: ReferralAttributionSummary & { id: string };
  milestones:          RepresentativeMilestone[];
  lastUpdatedAtUtc:    string;
}

export interface RepresentativeReferralMetrics {
  totalAttributedReferrals: number;
  pendingRequestReferrals:  number;
  pendingReviewReferrals?:  number;
  pendingReferrals:         number;
  acceptedReferrals:        number;
  declinedReferrals:        number;
  completedReferrals:       number;
  cancelledReferrals:       number;
  referralsByStatus:        Record<string, number>;
}

export interface RepresentativeReferralSearchParams {
  submittedFrom?:         string;
  submittedTo?:           string;
  status?:                string;
  providerId?:            string;
  lawFirmOrganizationId?: string;
  page?:                  number;
  pageSize?:              number;
}

/** Anonymous, stateless code check — see representative-portal-api.ts's verifyRepresentativeCode. */
export interface VerifyReferralAttributionAccessCodeResult {
  ok:                            boolean;
  referralAttributionId?:        string | null;
  referralAttributionFullName?:  string | null;
}

export interface LawFirmOption {
  id: string;
  name: string;
}

export interface TreatmentTypeOption {
  id: string;
  name: string;
}

export interface CreatePendingReferralRequest {
  lawFirmOrganizationId: string;
  clientFirstName: string;
  clientLastName: string;
  clientDob?: string;
  clientPhone: string;
  clientEmail?: string;
  caseNumber?: string;
  requestedService?: string;
  urgency: string;
  treatmentTypeId?: string;
  dateOfAccident?: string;
  recommendedProviderId?: string;
  recommendedFacilityId?: string;
  preferredProviders?: PendingReferralProviderPreferenceRequest[];
  notes?: string;
  lienCompanyName?: string;
  lienCompanyEmail?: string;
}

export interface PendingReferralProviderPreferenceRequest {
  providerId: string;
  facilityId?: string | null;
}

export interface PendingReferralRequest {
  id: string;
  tenantId: string;
  lawFirmOrganizationId: string;
  lawFirmName?: string | null;
  referralAttributionId: string;
  referralAttribution?: ReferralAttributionSummary | null;
  origin: string;
  clientFirstName: string;
  clientLastName: string;
  clientDob?: string | null;
  clientPhone: string;
  clientEmail: string;
  caseNumber?: string | null;
  requestedService?: string | null;
  urgency: string;
  treatmentTypeId?: string | null;
  dateOfAccident?: string | null;
  recommendedProviderId?: string | null;
  recommendedFacilityId?: string | null;
  recommendedProviderName?: string | null;
  recommendedFacilityName?: string | null;
  preferredProviders: PendingReferralProviderPreference[];
  attachments?: AttachmentSummary[];
  notes?: string | null;
  lienCompanyName?: string | null;
  lienCompanyEmail?: string | null;
  status: string;
  convertedReferralId?: string | null;
  convertedAtUtc?: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface ConvertPendingReferralRequest {
  providerId?: string;
  networkProviderId?: string;
  facilityId?: string | null;
}

export interface UpdatePendingReferralRequest {
  clientFirstName: string;
  clientLastName: string;
  clientDob?: string;
  clientPhone: string;
  clientEmail?: string;
  caseNumber?: string;
  requestedService?: string;
  urgency: string;
  treatmentTypeId?: string;
  dateOfAccident?: string;
  notes?: string;
  lienCompanyName?: string;
  lienCompanyEmail?: string;
}

export interface PendingReferralProviderPreference {
  id: string;
  providerId: string;
  facilityId?: string | null;
  providerName: string;
  facilityName?: string | null;
  displayOrder: number;
}

// LSCC-005-01 / LSCC-005-02: notification delivery record
export interface ReferralNotification {
  id:                string;
  notificationType:  string;
  recipientType:     string;
  recipientAddress?: string;
  status:            string;
  attemptCount:      number;
  failureReason?:    string;
  sentAtUtc?:        string;
  failedAtUtc?:      string;
  lastAttemptAtUtc?: string;
  createdAtUtc:      string;
  // LSCC-005-02: retry lifecycle fields
  /** How the notification was triggered: Initial | AutoRetry | ManualResend */
  triggerSource:      string;
  /** ISO 8601 UTC: when the next auto-retry is scheduled. Null if sent or exhausted. */
  nextRetryAfterUtc?: string;
  /** UI-friendly derived status: Pending | Sent | Failed | Retrying | RetryExhausted */
  derivedStatus:      string;
}

// LSCC-005-02: audit timeline event (status history + notification events merged)
export interface ReferralAuditEvent {
  /** Machine-readable event type, e.g. referral.status.accepted */
  eventType:   string;
  /** Human-readable label, e.g. "Provider Notification — Sent" */
  label:       string;
  /** ISO 8601 UTC timestamp */
  occurredAt:  string;
  /** Optional short context detail */
  detail?:     string;
  /** UI colour category: info | success | warning | error | security */
  category:    string;
}

// ReferralDetail — extends summary with hardening fields
export interface ReferralDetail extends ReferralSummary {
  // LSCC-005-01: token versioning + email delivery status
  tokenVersion?:              number;
  providerEmailStatus?:       string;
  providerEmailAttempts?:     number;
  providerEmailFailureReason?: string;
  // CC-REFERRER-EMAIL: referrer identity returned by the backend for participant checks
  referrerEmail?:             string | null;
  referrerName?:              string | null;
}

export interface CreateReferralRequest {
  tenantId?:       string;
  providerId:       string;
  clientFirstName:  string;
  clientLastName:   string;
  clientDob?:       string;
  clientPhone:      string;
  clientEmail:      string;
  caseNumber?:       string;
  requestedService?: string;
  urgency:           string;
  treatmentTypeId?:  string;
  dateOfAccident?:   string;
  notes?:            string;
  lienCompanyName?:  string;
  lienCompanyEmail?: string;
  referrerScopeSignature?: string;
  /** LSCC-005: referrer identity for the notification email */
  referrerEmail?:    string;
  referrerName?:     string;
  /** Optional — who or what originated this referral. Blank/undefined by default. */
  referralAttributionId?: string;
}

export interface ReferralSearchParams {
  status?:      string;
  providerId?:  string;
  clientName?:  string;
  caseNumber?:  string;
  urgency?:     string;
  createdFrom?: string;
  createdTo?:   string;
  page?:        number;
  pageSize?:    number;
}

export interface CreateReferralCommentRequest {
  message: string;
}

// ── Appointment ───────────────────────────────────────────────────────────────

export const AppointmentStatus = {
  Pending:     'Pending',
  Scheduled:   'Scheduled',
  Confirmed:   'Confirmed',
  Rescheduled: 'Rescheduled',
  Cancelled:   'Cancelled',
  Completed:   'Completed',
  NoShow:      'NoShow',
} as const;
export type AppointmentStatusValue = typeof AppointmentStatus[keyof typeof AppointmentStatus];

/** One bookable time block returned by GET /providers/{id}/availability */
export interface AvailabilitySlot {
  id:              string;
  startUtc:        string;   // ISO-8601
  endUtc:          string;   // ISO-8601
  durationMinutes: number;
  isAvailable:     boolean;
  serviceType?:    string;
  location?:       string;
}

/** Full response for GET /providers/{id}/availability */
export interface ProviderAvailabilityResponse {
  providerId:   string;
  providerName: string;
  from:         string;      // ISO date yyyy-MM-dd
  to:           string;      // ISO date yyyy-MM-dd
  slots:        AvailabilitySlot[];
}

export interface AvailabilitySearchParams {
  from?:        string;      // yyyy-MM-dd
  to?:          string;      // yyyy-MM-dd
  serviceType?: string;
}

/** Row in the appointments list */
export interface AppointmentSummary {
  id:               string;
  referralId?:      string;
  providerId:       string;
  providerName:     string;
  scheduledAtUtc:   string;
  durationMinutes:  number;
  status:           string;
  serviceType?:     string;
  clientFirstName:  string;
  clientLastName:   string;
  caseNumber?:      string;
  createdAtUtc:     string;
  updatedAtUtc:     string;
}

export interface AppointmentStatusHistoryItem {
  status:          string;
  changedAtUtc:    string;
  changedByUserId: string;
  changedByName?:  string;
  notes?:          string;
}

/** Full appointment returned by GET /appointments/{id} */
export interface AppointmentDetail extends AppointmentSummary {
  referringOrganizationId?:   string;
  referringOrganizationName?: string;
  receivingOrganizationId?:   string;
  receivingOrganizationName?: string;
  scheduledEndAtUtc?:         string;
  notes?:                     string;
  location?:                  string;
  clientDob?:                 string;
  clientPhone?:               string;
  clientEmail?:               string;
  statusHistory:              AppointmentStatusHistoryItem[];
}

/** Body for POST /appointments */
export interface CreateAppointmentRequest {
  providerId:       string;
  referralId?:      string;
  slotId?:          string;
  scheduledAtUtc:   string;
  durationMinutes?: number;
  serviceType?:     string;
  notes?:           string;
  clientFirstName:  string;
  clientLastName:   string;
  clientDob?:       string;
  clientPhone?:     string;
  clientEmail?:     string;
  caseNumber?:      string;
}

export interface AppointmentSearchParams {
  status?:     string;
  providerId?: string;
  referralId?: string;
  from?:       string;
  to?:         string;
  page?:       number;
  pageSize?:   number;
}

// ── CC2-INT-B03: Attachments ──────────────────────────────────────────────────

/**
 * One attachment record returned by GET /referrals/{id}/attachments or
 * GET /appointments/{id}/attachments.
 * Matches the backend AttachmentMetadataResponse DTO.
 */
export interface AttachmentSummary {
  id:                      string;
  fileName:                string;
  contentType:             string;
  fileSizeBytes:           number;
  status:                  string;
  /** Visibility scope: 'Shared' | 'Private' (optional — omitted means unscoped) */
  scope?:                  string;
  notes?:                  string;
  externalDocumentId?:     string;
  externalStorageProvider?: string;
  createdByUserId?:        string;
  createdAtUtc:            string;
}

/**
 * Response from GET /referrals/{id}/attachments/{attachmentId}/url or
 * GET /appointments/{id}/attachments/{attachmentId}/url.
 * Matches the backend SignedUrlResponse DTO.
 */
export interface SignedUrlResponse {
  url:              string;
  expiresInSeconds: number;
}

// ── Pagination ────────────────────────────────────────────────────────────────

/** Matches the backend PagedResponse<T> envelope */
export interface PagedResponse<T> {
  items:      T[];
  page:       number;
  pageSize:   number;
  totalCount: number;
}

// ── LSCC-009: Admin Activation Queue ─────────────────────────────────────────

export interface ActivationRequestSummary {
  id:               string;
  providerName:     string;
  providerEmail:    string;
  requesterName:    string | null;
  requesterEmail:   string | null;
  clientName:       string | null;
  referringFirmName: string | null;
  requestedService: string | null;
  referralId:       string;
  providerId:       string;
  status:           string;
  createdAtUtc:     string;
}

export interface ActivationRequestDetail {
  id:                     string;
  tenantId:               string;
  referralId:             string;
  providerId:             string;
  providerName:           string;
  providerEmail:          string;
  providerPhone:          string | null;
  providerAddress:        string | null;
  providerOrganizationId: string | null;
  requesterName:          string | null;
  requesterEmail:         string | null;
  clientName:             string | null;
  referringFirmName:      string | null;
  requestedService:       string | null;
  referralStatus:         string;
  status:                 string;
  approvedByUserId:       string | null;
  approvedAtUtc:          string | null;
  linkedOrganizationId:   string | null;
  createdAtUtc:           string;
  isAlreadyActive:        boolean;
}

// ── LSCC-011: Activation Funnel Analytics ──────────────────────────────────

export interface FunnelCounts {
  referralsSent:          number;
  referralsAccepted:      number;
  activationStarted:      number;
  autoProvisionSucceeded: number;
  adminApproved:          number;
  fallbackPending:        number;
  totalPendingSnapshot:   number;
  totalApprovedSnapshot:  number;
  referralViewed:         number | null;
}

export interface FunnelRates {
  activationRate:           number;
  autoProvisionSuccessRate: number;
  fallbackRate:             number;
  overallApprovalRate:      number;
  referralAcceptanceRate:   number;
  viewRate:                 number | null;
}

export interface ActivationFunnelMetrics {
  startDate: string;
  endDate:   string;
  isEmpty:   boolean;
  counts:    FunnelCounts;
  rates:     FunnelRates;
}

// ── LSCC-01-003: Admin CareConnect provider provisioning ──────────────────────

export interface ProviderReadinessDiagnostics {
  userId:               string;
  hasPrimaryOrg:        boolean;
  primaryOrgId:         string | null;
  primaryOrgType:       string | null;
  tenantHasCareConnect: boolean;
  orgHasCareConnect:    boolean;
  hasCareConnectRole:   boolean;
  isFullyProvisioned:   boolean;
}

export interface ProvisionCareConnectResult {
  userId:              string;
  organizationId:      string;
  organizationName:    string;
  tenantProductAdded:  boolean;
  orgProductAdded:     boolean;
  roleAdded:           boolean;
  isFullyProvisioned:  boolean;
}

export interface ProviderActivationResult {
  providerId:         string;
  alreadyActive:      boolean;
  isActive:           boolean;
  acceptingReferrals: boolean;
}

// ── LSCC-01-004: Admin Queue & Operational Visibility ─────────────────────────

/** Aggregate dashboard metrics returned by GET /api/admin/dashboard */
export interface DashboardMetrics {
  referralCountToday:        number;
  referralCountLast7Days:    number;
  openReferrals:             number;
  blockedAccessToday:        number;
  blockedAccessLast7Days:    number;
  distinctBlockedUsersToday: number;
  generatedAtUtc:            string;
}

/**
 * One row in the blocked-provider queue.
 * Represents the most-recent log entry for a (userId, failureReason) pair.
 */
export interface BlockedProviderLogItem {
  userId:          string | null;
  userEmail:       string | null;
  organizationId:  string | null;
  tenantId:        string | null;
  failureReason:   string;
  attemptCount:    number;
  lastAttemptUtc:  string;
  /** Relative path to the provisioning page pre-filled with this userId. */
  remediationPath: string | null;
}

export interface BlockedProviderLogPage {
  items:      BlockedProviderLogItem[];
  total:      number;
  page:       number;
  pageSize:   number;
  windowFrom: string;
}

/** One row in the admin referral monitor. */
export interface AdminReferralItem {
  id:                      string;
  tenantId:                string;
  providerId:              string;
  /** Backend may supply either tenantName or networkName for the Network column. */
  tenantName?:             string | null;
  networkName?:            string | null;
  status:                  string;
  urgency:                 string;
  clientFirstName:         string;
  clientLastName:          string;
  caseNumber?:             string | null;
  requestedService:        string;
  providerName:            string | null;
  providerEmail:           string | null;
  referringOrganizationId: string | null;
  receivingOrganizationId: string | null;
  referringOrganizationName?: string | null;
  referrerName:            string | null;
  referrerEmail:           string | null;
  createdAtUtc:            string;
  updatedAtUtc:            string;
}

export interface AdminReferralPage {
  items:    AdminReferralItem[];
  total:    number;
  page:     number;
  pageSize: number;
}

// ── Network Referral Monitor (lien company / network manager view) ─────────────

/** One referral row in the network manager's referral monitor. */
export interface NetworkReferralItem {
  id:                       string;
  /** Backend may supply either tenantName or networkName for the Network column. */
  tenantName?:              string | null;
  networkName?:             string | null;
  status:                   string;
  urgency:                  string;
  clientFirstName:          string;
  clientLastName:           string;
  caseNumber:               string | null;
  requestedService:         string;
  providerName:             string | null;
  providerOrganizationName: string | null;
  referringOrganizationId:  string | null;
  referrerName:             string | null;
  referrerEmail:            string | null;
  createdAtUtc:             string;
  updatedAtUtc:             string;
}

export interface NetworkReferralPage {
  items:    NetworkReferralItem[];
  total:    number;
  page:     number;
  pageSize: number;
}

// ── LSCC-01-005: Referral Performance Metrics ─────────────────────────────────

export interface PerformanceSummary {
  totalReferrals:       number;
  acceptedReferrals:    number;
  acceptanceRate:       number;   // [0, 1]
  avgTimeToAcceptHours: number | null;
  currentNewReferrals:  number;
}

/** Aging distribution for currently-New referrals. */
export interface AgingDistribution {
  lt1h:   number;
  h1to24: number;
  d1to3:  number;
  gt3d:   number;
  total:  number;
}

export interface ProviderPerformanceRow {
  providerId:           string;
  providerName:         string;
  totalReferrals:       number;
  acceptedReferrals:    number;
  acceptanceRate:       number;   // [0, 1]
  avgTimeToAcceptHours: number | null;
}

export interface ReferralPerformanceResult {
  windowFrom: string;
  windowTo:   string;
  summary:    PerformanceSummary;
  aging:      AgingDistribution;
  providers:  ProviderPerformanceRow[];
}

// ── CC2-INT-B06 / CC2-INT-B06-01: Provider Networks + Shared Registry ────────

/** Result from GET /api/networks/{id}/providers/search — shared global registry */
export interface ProviderSearchResult {
  id:                string;
  facilityId?:        string | null;
  facilityName?:      string | null;
  name:              string;
  title?:            string | null;
  organizationName?: string;
  email:             string;
  phone:             string;
  city:              string;
  state:             string;
  addressLine1:      string;
  postalCode?:       string | null;
  npi?:              string;
  isActive:          boolean;
  acceptingReferrals: boolean;
  accessStage:       string;
  specialties:       SpecialtyOption[];
  primarySpecialtyId?: string | null;
  primarySpecialty?: string | null;
  distanceMiles?:    number | null;
  isMobile?:            boolean;
  serviceRadiusMiles?:  number | null;
  serviceAreaLabel?:    string | null;
}

/**
 * Body for POST /api/networks/{id}/providers.
 * - existingProviderId + existingFacilityId adds an existing provider-location membership.
 * - existingProviderId + newProvider creates a new location for an existing provider.
 * - newProvider alone creates a new provider identity and first location; duplicate NPI/email is rejected.
 */
export interface AddProviderToNetworkRequest {
  existingProviderId?: string;
  existingFacilityId?: string | null;
  newProvider?: {
    title?:              string;
    firstName:           string;
    lastName:            string;
    organizationName?:   string;
    email:               string;
    phone:               string;
    addressLine1:        string;
    city:                string;
    state:               string;
    postalCode?:         string | null;
    isActive:            boolean;
    acceptingReferrals:  boolean;
    visibility?:         string | null;
    npi?:                string;
    categoryCodes?:      string[];
    primaryCategoryCode?: string;
    specialtyCodes?:     string[];
    primarySpecialtyCode?: string;
    latitude?:           number | null;
    longitude?:          number | null;
    geoPointSource?:     string | null;
    isMobile?:           boolean;
    serviceRadiusMiles?: number | null;
  };
}

/** Body for PUT /api/networks/{networkId}/providers/{providerId}. */
export interface UpdateNetworkProviderRequest {
  title?:              string | null;
  firstName:           string;
  lastName:            string;
  organizationName?:   string | null;
  facilityName?:       string | null;
  email:               string;
  phone:               string;
  addressLine1:        string;
  city:                string;
  state:               string;
  postalCode?:         string | null;
  isActive:            boolean;
  acceptingReferrals:  boolean;
  visibility?:         string | null;
  specialtyIds:        string[];
  latitude?:           number | null;
  longitude?:          number | null;
  geoPointSource?:     string | null;
  isMobile?:           boolean;
  serviceRadiusMiles?: number | null;
}

export interface NetworkSummary {
  id:            string;
  name:          string;
  description:   string;
  providerCount: number;
  createdAtUtc:  string;
  updatedAtUtc:  string;
  /** LSV3-1084: the organization that created this network; null for pre-existing/tenant-admin-owned networks. */
  owningOrganizationId?: string | null;
}

// CC2-INT-B06-02: Provider access-stage constants (mirrors ProviderAccessStage domain constants)
export const ProviderAccessStage = {
  Url:          'URL',
  CommonPortal: 'COMMON_PORTAL',
  Tenant:       'TENANT',
} as const;
export type ProviderAccessStageValue = typeof ProviderAccessStage[keyof typeof ProviderAccessStage];

export interface NetworkProviderItem {
  id:                string;
  networkProviderId: string;
  providerId:        string;
  facilityId:        string;
  name:              string;
  title?:            string | null;
  organizationName?: string;
  facilityName:      string;
  email:             string;
  phone:             string;
  city:              string;
  state:             string;
  addressLine1:      string;
  postalCode?:       string | null;
  isActive:          boolean;
  acceptingReferrals: boolean;
  owningOrganizationId?: string | null;
  createdByLawFirm?: string | null;
  visibility:        string;
  /**
   * Whether the underlying cc_Facilities row is active. Distinct from `isActive` above (the
   * NetworkProvider membership's own Active/Accepting-referrals toggle — an existing,
   * independent feature): this is false only when the location itself was soft-deleted via
   * "Delete location". Use this, not `isActive`, to decide whether a location was deleted.
   */
  facilityIsActive:  boolean;
  accessStage:       string;
  specialties:       SpecialtyOption[];
  primarySpecialtyId?: string | null;
  primarySpecialty?: string | null;
  distanceMiles?:    number | null;
  isMobile:            boolean;
  serviceRadiusMiles?: number | null;
  serviceAreaLabel?:   string | null;
}

export interface NetworkDetail {
  id:          string;
  name:        string;
  description: string;
  providers:   NetworkProviderItem[];
  createdAtUtc: string;
  updatedAtUtc: string;
  /** LSV3-1084: the organization that created this network; null for pre-existing/tenant-admin-owned networks. */
  owningOrganizationId?: string | null;
}

export interface NetworkProviderMarker {
  id:                string;
  networkProviderId: string;
  providerId:        string;
  facilityId:        string;
  name:              string;
  title?:            string | null;
  organizationName?: string;
  facilityName:      string;
  city:              string;
  state:             string;
  addressLine1:      string;
  postalCode?:       string | null;
  email:             string;
  phone:             string;
  acceptingReferrals: boolean;
  isActive:          boolean;
  latitude:          number;
  longitude:         number;
  geoPointSource?:   string;
  specialties:       SpecialtyOption[];
  primarySpecialtyId?: string | null;
  primarySpecialty?: string | null;
  distanceMiles?:    number | null;
  isMobile:            boolean;
  serviceRadiusMiles?: number | null;
  serviceAreaLabel?:   string | null;
}

export interface CreateNetworkRequest {
  name:        string;
  description: string;
}

export interface UpdateNetworkRequest {
  name:        string;
  description: string;
}

// ── LSV3-1083: Law Firm Company Super Admin/Manager ──────────────────────────

export interface LawFirmUserRoleAssignment {
  assignmentId: string;
  roleCode:     string;
}

export interface LawFirmUserSummary {
  userId:    string;
  email:     string;
  firstName: string;
  lastName:  string;
  isActive:  boolean;
  status:    string;
  roles:     LawFirmUserRoleAssignment[];
}

export interface InviteLawFirmUserRequest {
  email:     string;
  firstName: string;
  lastName:  string;
  /** Defaults to CARECONNECT_REFERRER when omitted. */
  roleCode?: string;
}

export interface LawFirmUserInviteResult {
  userId:       string;
  invitationId?: string | null;
  email:        string;
  isNew:        boolean;
}
