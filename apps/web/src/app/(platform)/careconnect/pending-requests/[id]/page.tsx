"use client";

import { useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { useParams, useRouter } from "next/navigation";
import dynamic from "next/dynamic";
import { careConnectApi } from "@/lib/careconnect-api";
import { ApiError } from "@/lib/api-client";
import { formatPhoneDisplay } from "@/lib/phone";
import { UrgencyBadge } from "@/components/careconnect/status-badge";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import type { NumberedMarker } from "@/components/careconnect/public-network-map";
import type {
  AttachmentSummary,
  PendingReferralProviderPreference,
  PendingReferralRequest,
  ProviderSummary,
  TreatmentTypeOption,
  UpdatePendingReferralRequest,
} from "@/types/careconnect";

const NONE_TREATMENT_TYPE = "__none__";

const URGENCY_OPTIONS = ["Low", "Normal", "Urgent", "Emergency"];
const SERVICE_TYPES = [
  "General Referral",
  "Consultation",
  "Initial Service",
  "Diagnostic Service",
  "Laboratory Service",
  "Imaging/Radiology",
  "Emergency Service",
  "Home Health Service",
  "Specialist Referral",
  "Telehealth Service",
  "Follow-up Service",
];
const ALL_SPECIALTIES = "__all_specialties__";

const PublicNetworkMap = dynamic(
  () => import("@/components/careconnect/public-network-map").then(module => module.PublicNetworkMap),
  { ssr: false, loading: () => <div className="h-full w-full animate-pulse bg-gray-100" /> },
);

type ReviewProviderPreference = {
  id: string;
  facilityId?: string | null;
  displayLabel: string;
  markerSubtitle?: string | null;
  phone?: string | null;
};

type ProviderPickerViewMode = "split" | "list" | "map";
type SearchLocation = { latitude: number; longitude: number; label: string };

function providerLocationLine(provider: ProviderSummary): string {
  return [provider.city, provider.state].filter(Boolean).join(", ");
}

function providerIdentity(provider: ProviderSummary): { primary: string; secondary: string | null } {
  const name = provider.name.trim();
  const organization = provider.organizationName?.trim() ?? "";
  return {
    primary: organization || name,
    secondary: organization && organization.toLowerCase() !== name.toLowerCase() ? name : null,
  };
}

function toRad(value: number): number { return value * Math.PI / 180; }

function distanceMiles(a: SearchLocation, b: ProviderSummary): number {
  if (!Number.isFinite(b.latitude) || !Number.isFinite(b.longitude) || (b.latitude === 0 && b.longitude === 0)) return Number.POSITIVE_INFINITY;
  const radius = 3958.7613;
  const dLat = toRad(b.latitude! - a.latitude);
  const dLng = toRad(b.longitude! - a.longitude);
  const lat1 = toRad(a.latitude);
  const lat2 = toRad(b.latitude!);
  const h = Math.sin(dLat / 2) ** 2 + Math.cos(lat1) * Math.cos(lat2) * Math.sin(dLng / 2) ** 2;
  return 2 * radius * Math.atan2(Math.sqrt(Math.min(1, Math.max(0, h))), Math.sqrt(1 - Math.min(1, Math.max(0, h))));
}

function numberedMarker(provider: ProviderSummary, index: number): NumberedMarker {
  return {
    id: providerSelectionValue(provider.id, provider.facilityId),
    networkProviderId: provider.id,
    providerId: provider.id,
    facilityId: provider.facilityId ?? "",
    name: provider.name,
    title: provider.title ?? null,
    organizationName: provider.organizationName ?? null,
    facilityName: provider.markerSubtitle,
    city: provider.city,
    state: provider.state,
    acceptingReferrals: provider.acceptingReferrals,
    latitude: provider.latitude ?? 0,
    longitude: provider.longitude ?? 0,
    specialties: provider.specialties,
    primarySpecialtyId: provider.primarySpecialtyId ?? null,
    primarySpecialty: provider.primarySpecialty ?? null,
    distanceMiles: provider.distanceMiles ?? null,
    isMobile: provider.isMobile,
    serviceRadiusMiles: provider.serviceRadiusMiles ?? null,
    serviceAreaLabel: provider.serviceAreaLabel ?? null,
    phone: provider.phone,
    addressLine1: provider.addressLine1,
    postalCode: provider.postalCode,
    index,
  };
}

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

function toDateInputValue(value?: string | null): string {
  if (!value) return "";
  return value.split("T")[0] ?? "";
}

function providerLabel(provider?: ProviderSummary | null): string {
  if (!provider) return "No provider selected";
  return provider.organizationName?.trim() || provider.displayLabel || provider.name;
}

function reviewProviderKey(provider: Pick<ReviewProviderPreference, "id" | "facilityId">): string {
  return `${provider.id}|${provider.facilityId ?? ""}`;
}

function reviewProviderFromPreference(preference: PendingReferralProviderPreference): ReviewProviderPreference {
  return {
    id: preference.providerId,
    facilityId: preference.facilityId ?? null,
    displayLabel: preference.providerName,
    markerSubtitle: preference.facilityName,
  };
}

function reviewProviderFromSummary(provider: ProviderSummary): ReviewProviderPreference {
  return {
    id: provider.id,
    facilityId: provider.facilityId ?? null,
    displayLabel: providerLabel(provider),
    markerSubtitle: provider.markerSubtitle,
    phone: provider.phone,
  };
}

function PendingStatusBadge() {
  return (
    <span className="inline-flex items-center rounded-full border border-amber-200 bg-amber-50 px-2 py-0.5 text-xs font-medium text-amber-700">
      Pending review
    </span>
  );
}

function formFromRequest(item: PendingReferralRequest): UpdatePendingReferralRequest {
  return {
    clientFirstName: item.clientFirstName,
    clientLastName: item.clientLastName,
    clientDob: toDateInputValue(item.clientDob),
    clientPhone: item.clientPhone,
    clientEmail: item.clientEmail,
    caseNumber: item.caseNumber ?? "",
    requestedService: item.requestedService ?? "",
    urgency: item.urgency || "Normal",
    treatmentTypeId: item.treatmentTypeId ?? "",
    dateOfAccident: toDateInputValue(item.dateOfAccident),
    notes: item.notes ?? "",
    lienCompanyName: item.lienCompanyName ?? "",
    lienCompanyEmail: item.lienCompanyEmail ?? "",
  };
}

function updatePayloadFromForm(form: UpdatePendingReferralRequest): UpdatePendingReferralRequest {
  return {
    ...form,
    clientDob: form.clientDob || undefined,
    clientEmail: form.clientEmail || undefined,
    caseNumber: form.caseNumber || undefined,
    requestedService: form.requestedService || undefined,
    treatmentTypeId: form.treatmentTypeId || undefined,
    dateOfAccident: form.dateOfAccident || undefined,
    notes: form.notes || undefined,
    lienCompanyName: form.lienCompanyName || undefined,
    lienCompanyEmail: form.lienCompanyEmail || undefined,
  };
}

function validateReviewDates(form: UpdatePendingReferralRequest): string | null {
  if (!form.clientDob) return "Date of birth is required before saving or accepting this request.";
  if (!form.dateOfAccident) return "Date of accident is required before saving or accepting this request.";
  return null;
}

function formatFileSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function TextField({
  label,
  value,
  onChange,
  type = "text",
  required,
}: {
  label: string;
  value: string | undefined;
  onChange: (value: string) => void;
  type?: string;
  required?: boolean;
}) {
  return (
    <label className="block">
      <span className="mb-1 block text-sm font-medium text-gray-700">
        {label} {required && <span className="text-red-500">*</span>}
      </span>
      <input
        type={type}
        value={value ?? ""}
        required={required}
        onChange={event => onChange(event.target.value)}
        className="h-10 w-full rounded-md border border-gray-300 bg-white px-3 text-sm text-gray-900 outline-none transition focus:border-orange-400 focus:ring-2 focus:ring-orange-100"
      />
    </label>
  );
}

const ATTACHMENT_ACCEPT = [
  "application/pdf",
  "application/msword",
  "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
  "image/jpeg",
  "image/png",
  "image/gif",
  "text/plain",
  "text/csv",
  "application/vnd.ms-excel",
  "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
].join(",");

function AttachmentsField({
  attachments,
  uploading,
  onUpload,
  onView,
}: {
  attachments: AttachmentSummary[];
  uploading: boolean;
  onUpload: (files: File[]) => void;
  onView: (attachment: AttachmentSummary) => void;
}) {
  return (
    <fieldset>
      <legend className="mb-3 text-xs font-semibold uppercase tracking-wider text-gray-400">
        Attachments
      </legend>
      <div className="space-y-3">
        <label className={`flex items-center gap-3 rounded-lg border border-dashed px-3 py-3 transition-colors ${
          uploading
            ? "pointer-events-none opacity-50"
            : "cursor-pointer border-gray-300 hover:border-primary hover:bg-primary/5"
        }`}>
          <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg bg-gray-50 text-gray-400">
            <i className={uploading ? "ri-loader-4-line animate-spin text-lg" : "ri-upload-2-line text-lg"} />
          </span>
          <span className="min-w-0">
            <span className="block text-sm font-medium text-gray-900">{uploading ? "Uploading documents..." : "Attach documents"}</span>
            <span className="block text-xs text-gray-500">PDF, Word, images, text, CSV, or Excel files.</span>
          </span>
          <input
            type="file"
            className="hidden"
            accept={ATTACHMENT_ACCEPT}
            multiple
            disabled={uploading}
            onChange={event => {
              const files = Array.from(event.target.files ?? []);
              if (files.length > 0) onUpload(files);
              event.target.value = "";
            }}
          />
        </label>

        {attachments.length > 0 ? (
          <div className="rounded-lg border border-gray-200 bg-gray-50">
            <div className="border-b border-gray-200 px-3 py-2 text-xs font-semibold uppercase tracking-wide text-gray-500">
              Documents ({attachments.length})
            </div>
            <div className="divide-y divide-gray-200">
              {attachments.map(file => (
                <div key={file.id} className="flex items-center gap-3 px-3 py-2">
                  <i className="ri-file-line shrink-0 text-gray-400" />
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-sm font-medium text-gray-900">{file.fileName}</p>
                    <p className="text-xs text-gray-500">{formatFileSize(file.fileSizeBytes)}</p>
                  </div>
                  <span className="rounded bg-white px-2 py-1 text-xs text-gray-500">{file.status}</span>
                  <button
                    type="button"
                    onClick={() => onView(file)}
                    aria-label={`View ${file.fileName}`}
                    title="View attachment"
                    className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-gray-400 hover:bg-white hover:text-gray-700"
                  >
                    <i className="ri-eye-line text-base" />
                  </button>
                </div>
              ))}
            </div>
          </div>
        ) : (
          <p className="rounded-lg border border-gray-200 bg-white px-3 py-3 text-sm text-gray-500">
            No documents were attached to this request.
          </p>
        )}
      </div>
    </fieldset>
  );
}

export default function PendingReferralRequestDetailPage() {
  const router = useRouter();
  const params = useParams<{ id: string }>();
  const requestId = params.id;
  const [request, setRequest] = useState<PendingReferralRequest | null>(null);
  const [providers, setProviders] = useState<ProviderSummary[]>([]);
  const [treatmentTypes, setTreatmentTypes] = useState<TreatmentTypeOption[]>([]);
  const [selectedProviders, setSelectedProviders] = useState<ReviewProviderPreference[]>([]);
  const [form, setForm] = useState<UpdatePendingReferralRequest | null>(null);
  const [providerPickerOpen, setProviderPickerOpen] = useState(false);
  const [providerSearch, setProviderSearch] = useState("");
  const [providerZip, setProviderZip] = useState("");
  const [providerZipLocation, setProviderZipLocation] = useState<SearchLocation | null>(null);
  const [providerZipLoading, setProviderZipLoading] = useState(false);
  const [providerZipError, setProviderZipError] = useState<string | null>(null);
  const [providerViewMode, setProviderViewMode] = useState<ProviderPickerViewMode>("split");
  const [providerSpecialtyCode, setProviderSpecialtyCode] = useState("");
  const [providerAcceptingOnly, setProviderAcceptingOnly] = useState(true);
  const [selectedMarkerId, setSelectedMarkerId] = useState<string | null>(null);
  const [zoomToProviderId, setZoomToProviderId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [accepting, setAccepting] = useState(false);
  const [declining, setDeclining] = useState(false);
  const [uploadingAttachments, setUploadingAttachments] = useState(false);

  async function load() {
    setLoading(true);
    setError(null);
    try {
      const [requestRes, providerRes, treatmentTypeRes] = await Promise.all([
        careConnectApi.pendingReferralRequests.getById(requestId),
        careConnectApi.providers.search({ isActive: true, acceptingReferrals: true, pageSize: 100 }),
        careConnectApi.treatmentTypes.list(),
      ]);
      const nextRequest = requestRes.data;
      setRequest(nextRequest);
      setForm(formFromRequest(nextRequest));
      setProviders(providerRes.data.items);
      setTreatmentTypes(treatmentTypeRes.data);
      setSelectedProviders(preferredProvidersFor(nextRequest).map(reviewProviderFromPreference));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Failed to load pending request.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => { void load(); }, [requestId]);

  const selectedProviderKeys = useMemo(
    () => new Set(selectedProviders.map(reviewProviderKey)),
    [selectedProviders],
  );

  const firstSelectedProvider = selectedProviders[0] ?? null;

  const serviceTypeOptions = useMemo(() => {
    const current = form?.requestedService?.trim();
    if (current && !SERVICE_TYPES.includes(current)) return [current, ...SERVICE_TYPES];
    return SERVICE_TYPES;
  }, [form?.requestedService]);

  const providerQuery = providerSearch.trim().toLowerCase();
  const providerSpecialtyOptions = useMemo(() => {
    const specialties = new Map<string, string>();
    providers.forEach(provider => (provider.specialties ?? []).forEach(specialty => {
      if (specialty.code && !specialties.has(specialty.code)) specialties.set(specialty.code, specialty.name);
    }));
    return [...specialties.entries()].sort((a, b) => a[1].localeCompare(b[1]));
  }, [providers]);

  const filteredProviders = useMemo(() => {
    let list = providers;
    if (providerAcceptingOnly) list = list.filter(provider => provider.acceptingReferrals);
    if (providerSpecialtyCode) list = list.filter(provider => (provider.specialties ?? []).some(specialty => specialty.code === providerSpecialtyCode));
    if (providerQuery) list = list.filter(provider => [
      provider.displayLabel, provider.organizationName, provider.name, provider.markerSubtitle,
      provider.addressLine1, provider.city, provider.state, provider.postalCode,
      provider.primarySpecialty, ...(provider.specialties ?? []).map(specialty => specialty.name),
    ].filter(Boolean).join(" ").toLowerCase().includes(providerQuery));
    if (providerZipLocation) list = [...list].sort((a, b) => distanceMiles(providerZipLocation, a) - distanceMiles(providerZipLocation, b));
    return list;
  }, [providerAcceptingOnly, providerQuery, providerSpecialtyCode, providerZipLocation, providers]);

  const numberedMarkers = useMemo(
    () => filteredProviders.map((provider, index) => ({
      ...numberedMarker(provider, index + 1),
      distanceMiles: providerZipLocation ? distanceMiles(providerZipLocation, provider) : provider.distanceMiles ?? null,
    })),
    [filteredProviders, providerZipLocation],
  );

  function toggleProvider(provider: ProviderSummary) {
    const preference = reviewProviderFromSummary(provider);
    const key = reviewProviderKey(preference);
    setSelectedProviders(prev => (
      prev.some(item => reviewProviderKey(item) === key)
        ? prev.filter(item => reviewProviderKey(item) !== key)
        : [...prev, preference]
    ));
    const selectionKey = providerSelectionValue(provider.id, provider.facilityId);
    setSelectedMarkerId(selectionKey);
    setZoomToProviderId(selectionKey);
  }

  async function applyProviderZipFilter() {
    const value = providerZip.trim();
    if (!value) {
      setProviderZipLocation(null);
      setProviderZipError(null);
      return;
    }
    setProviderZipLoading(true);
    setProviderZipError(null);
    try {
      const response = await fetch(`/api/geocode/address?q=${encodeURIComponent(value)}&loose=1`);
      if (!response.ok) throw new Error("Unable to geocode ZIP code.");
      const suggestions = await response.json() as Array<{ latitude: number; longitude: number; displayName?: string | null }>;
      const location = suggestions.map(item => ({ latitude: Number(item.latitude), longitude: Number(item.longitude), label: item.displayName?.trim() || value }))
        .find(item => Number.isFinite(item.latitude) && Number.isFinite(item.longitude) && !(item.latitude === 0 && item.longitude === 0));
      if (!location) throw new Error("No location found for that ZIP code.");
      setProviderZipLocation(location);
    } catch (err) {
      setProviderZipError(err instanceof Error ? err.message : "Unable to geocode ZIP code.");
    } finally {
      setProviderZipLoading(false);
    }
  }

  function clearProviderFilters() {
    setProviderSearch("");
    setProviderZip("");
    setProviderZipLocation(null);
    setProviderZipError(null);
    setProviderSpecialtyCode("");
    setProviderAcceptingOnly(true);
    setSelectedMarkerId(null);
    setZoomToProviderId(null);
  }

  function removeProvider(key: string) {
    setSelectedProviders(prev => prev.filter(item => reviewProviderKey(item) !== key));
  }

  async function saveRequest() {
    if (!request || !form) return;
    const validationError = validateReviewDates(form);
    if (validationError) {
      setError(validationError);
      return;
    }
    setSaving(true);
    setError(null);
    try {
      const result = await careConnectApi.pendingReferralRequests.update(request.id, updatePayloadFromForm(form));
      setRequest(result.data);
      setForm(formFromRequest(result.data));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Failed to update pending request.");
    } finally {
      setSaving(false);
    }
  }

  async function uploadAttachments(files: File[]) {
    if (!request || files.length === 0) return;
    setUploadingAttachments(true);
    setError(null);
    try {
      for (const file of files) {
        await careConnectApi.pendingReferralRequests.uploadAttachment(request.id, file);
      }
      const refreshed = await careConnectApi.pendingReferralRequests.getById(request.id);
      setRequest(refreshed.data);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Failed to upload attachment.");
    } finally {
      setUploadingAttachments(false);
    }
  }

  async function viewAttachment(attachment: AttachmentSummary) {
    if (!request) return;
    const tab = window.open("about:blank", "_blank");
    if (tab) tab.opener = null;
    setError(null);
    try {
      const result = await careConnectApi.pendingReferralRequests.getAttachmentSignedUrl(request.id, attachment.id);
      if (tab) {
        tab.location.href = result.data.url;
      } else {
        window.open(result.data.url, "_blank", "noopener,noreferrer");
      }
    } catch (err) {
      tab?.close();
      setError(err instanceof ApiError ? err.message : "Failed to open attachment.");
    }
  }

  async function acceptRequest() {
    if (!request || !form) return;
    const validationError = validateReviewDates(form);
    if (validationError) {
      setError(validationError);
      return;
    }
    const selection = parseProviderSelection(reviewProviderKey(firstSelectedProvider ?? { id: "", facilityId: "" }));
    if (!selection) {
      setError("Select a provider before accepting the request.");
      setProviderPickerOpen(true);
      return;
    }
    setAccepting(true);
    setError(null);
    try {
      // Conversion copies field values from the persisted pending request row, so any
      // unsaved edits in the form must be saved first or they won't carry over to the referral.
      const saved = await careConnectApi.pendingReferralRequests.update(request.id, updatePayloadFromForm(form));
      setRequest(saved.data);
      setForm(formFromRequest(saved.data));
      const result = await careConnectApi.pendingReferralRequests.convert(request.id, selection);
      router.push(`/careconnect/referrals/${result.data.id}`);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Failed to accept pending request.");
      setAccepting(false);
    }
  }

  async function declineRequest() {
    if (!request) return;
    setDeclining(true);
    setError(null);
    try {
      await careConnectApi.pendingReferralRequests.decline(request.id);
      router.push("/careconnect/pending-requests");
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Failed to decline pending request.");
      setDeclining(false);
    }
  }

  if (loading) {
    return <p className="text-sm text-gray-500">Loading pending request...</p>;
  }

  if (!request || !form) {
    return (
      <div className="rounded-lg border border-gray-200 bg-white p-8 text-center text-sm text-gray-500">
        Pending request was not found.
      </div>
    );
  }

  return (
    <div className="space-y-5">
      <div className="flex flex-col gap-3 md:flex-row md:items-start md:justify-between">
        <div>
          <Link href="/careconnect/pending-requests" className="text-xs text-gray-400 transition-colors hover:text-gray-600">
            ← Back to pending requests
          </Link>
          <div className="mt-3 flex flex-wrap items-center gap-2">
            <h1 className="text-xl font-semibold text-gray-900">{request.clientFirstName} {request.clientLastName}</h1>
            <PendingStatusBadge />
            <UrgencyBadge urgency={request.urgency} />
          </div>
          <p className="mt-0.5 text-sm text-gray-500">Update request values before accepting or declining.</p>
        </div>
        <div className="flex flex-wrap gap-2">
          <button type="button" onClick={() => void saveRequest()} disabled={saving} className="inline-flex items-center gap-2 rounded-md border border-gray-200 px-3 py-2 text-sm font-medium text-gray-700 transition-colors hover:bg-gray-50 disabled:opacity-60">
            <i className="ri-save-line" />
            {saving ? "Saving..." : "Save changes"}
          </button>
          <button type="button" onClick={() => void acceptRequest()} disabled={accepting} className="inline-flex items-center gap-2 rounded-md bg-green-600 px-3 py-2 text-sm font-medium text-white transition-colors hover:bg-green-700 disabled:opacity-60">
            <i className="ri-checkbox-circle-line" />
            {accepting ? "Accepting..." : "Accept"}
          </button>
          <button type="button" onClick={() => void declineRequest()} disabled={declining} className="inline-flex items-center gap-2 rounded-md border border-red-200 px-3 py-2 text-sm font-medium text-red-700 transition-colors hover:bg-red-50 disabled:opacity-60">
            <i className="ri-close-circle-line" />
            {declining ? "Declining..." : "Decline"}
          </button>
        </div>
      </div>

      {error && <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">{error}</div>}

      <section className="overflow-hidden rounded-lg border border-gray-200 bg-white">
        <div className="border-b border-gray-100 bg-gray-50 px-5 py-3">
          <h2 className="text-sm font-semibold text-gray-900">Request details</h2>
          <p className="mt-0.5 text-xs text-gray-500">These values will be copied to the referral when accepted.</p>
        </div>
        <div className="space-y-5 p-5">
          <fieldset>
            <legend className="mb-3 text-xs font-semibold uppercase tracking-wider text-gray-400">
              Patient Information
            </legend>
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
              <TextField label="First name" required value={form.clientFirstName} onChange={value => setForm({ ...form, clientFirstName: value })} />
              <TextField label="Last name" required value={form.clientLastName} onChange={value => setForm({ ...form, clientLastName: value })} />
              <TextField label="Phone" required value={form.clientPhone} onChange={value => setForm({ ...form, clientPhone: value })} />
              <TextField label="Email" type="email" value={form.clientEmail} onChange={value => setForm({ ...form, clientEmail: value })} />
              <TextField label="Date of birth" required type="date" value={form.clientDob} onChange={value => setForm({ ...form, clientDob: value })} />
              <TextField label="Date of accident" required type="date" value={form.dateOfAccident} onChange={value => setForm({ ...form, dateOfAccident: value })} />
            </div>
          </fieldset>

          <fieldset>
            <legend className="mb-3 text-xs font-semibold uppercase tracking-wider text-gray-400">
              Referral Details
            </legend>
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
              <label className="block">
                <span className="mb-1 block text-sm font-medium text-gray-700">Urgency <span className="text-red-500">*</span></span>
                <Select value={form.urgency} onValueChange={value => setForm({ ...form, urgency: value })}>
                  <SelectTrigger className="h-10 w-full"><SelectValue /></SelectTrigger>
                  <SelectContent>
                    {URGENCY_OPTIONS.map(option => <SelectItem key={option} value={option}>{option}</SelectItem>)}
                  </SelectContent>
                </Select>
              </label>
              <label className="block">
                <span className="mb-1 block text-sm font-medium text-gray-700">Type of service <span className="text-red-500">*</span></span>
                <Select value={form.requestedService} onValueChange={value => setForm({ ...form, requestedService: value })}>
                  <SelectTrigger className="h-10 w-full"><SelectValue /></SelectTrigger>
                  <SelectContent>
                    {serviceTypeOptions.map(option => <SelectItem key={option} value={option}>{option}</SelectItem>)}
                  </SelectContent>
                </Select>
              </label>
              <label className="block">
                <span className="mb-1 block text-sm font-medium text-gray-700">Type of treatment</span>
                <Select
                  value={form.treatmentTypeId || NONE_TREATMENT_TYPE}
                  onValueChange={value => setForm({ ...form, treatmentTypeId: value === NONE_TREATMENT_TYPE ? "" : value })}
                >
                  <SelectTrigger className="h-10 w-full"><SelectValue /></SelectTrigger>
                  <SelectContent>
                    <SelectItem value={NONE_TREATMENT_TYPE}>None</SelectItem>
                    {treatmentTypes.map(type => <SelectItem key={type.id} value={type.id}>{type.name}</SelectItem>)}
                  </SelectContent>
                </Select>
              </label>
              <TextField label="Lien company name" value={form.lienCompanyName} onChange={value => setForm({ ...form, lienCompanyName: value })} />
              <TextField label="Lien company email" type="email" value={form.lienCompanyEmail} onChange={value => setForm({ ...form, lienCompanyEmail: value })} />
              <label className="block sm:col-span-2 lg:col-span-3">
                <span className="mb-1 block text-sm font-medium text-gray-700">Notes</span>
                <textarea value={form.notes ?? ""} onChange={event => setForm({ ...form, notes: event.target.value })} rows={3} className="w-full resize-none rounded-md border border-gray-300 bg-white px-3 py-2 text-sm text-gray-900 outline-none transition focus:border-orange-400 focus:ring-2 focus:ring-orange-100" />
              </label>
            </div>
          </fieldset>

          <fieldset>
            <legend className="mb-3 text-xs font-semibold uppercase tracking-wider text-gray-400">
              Medical Provider Preference
            </legend>
            <div className="space-y-3">
              <div className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800">
                Provider selections are recommendations for routing this referral. The first selected provider is used when accepting.
              </div>
              <div className="flex items-center justify-between gap-3 rounded-lg border border-gray-200 bg-white px-3 py-3">
                <div className="min-w-0">
                  <p className="text-sm font-medium text-gray-900">
                    {selectedProviders.length > 0
                      ? `${selectedProviders.length} preferred provider${selectedProviders.length === 1 ? "" : "s"} selected`
                      : "No preferred providers selected"}
                  </p>
                  <p className="mt-0.5 text-xs text-gray-500">Open the provider picker to add or remove recommendations.</p>
                </div>
                <button
                  type="button"
                  onClick={() => setProviderPickerOpen(true)}
                  className="shrink-0 rounded-md bg-primary px-3 py-2 text-sm font-medium text-white hover:bg-primary/90"
                >
                  Open provider map
                </button>
              </div>
              {selectedProviders.length > 0 && (
                <div className="rounded-lg border border-primary/20 bg-primary/5 px-3 py-2">
                  <p className="text-xs font-semibold uppercase tracking-wide text-primary">
                    Preferred providers ({selectedProviders.length})
                  </p>
                  <div className="mt-2 grid grid-cols-1 gap-2 sm:grid-cols-2 lg:grid-cols-3">
                    {selectedProviders.map((provider, index) => {
                      const key = reviewProviderKey(provider);
                      return (
                        <div key={key} className="flex items-center justify-between gap-3 rounded-md bg-white px-3 py-2">
                          <div className="min-w-0">
                            <p className="truncate text-sm font-medium text-gray-900">{index + 1}. {provider.displayLabel}</p>
                            {provider.markerSubtitle && <p className="truncate text-xs text-gray-500">{provider.markerSubtitle}</p>}
                          </div>
                          <button
                            type="button"
                            onClick={() => removeProvider(key)}
                            aria-label={`Remove ${provider.displayLabel} from preferred providers`}
                            title="Remove from preferred providers"
                            className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-gray-400 hover:bg-gray-100 hover:text-gray-700"
                          >
                            <i className="ri-close-line text-base" />
                          </button>
                        </div>
                      );
                    })}
                  </div>
                </div>
              )}
            </div>
          </fieldset>

          <AttachmentsField
            attachments={request.attachments ?? []}
            uploading={uploadingAttachments}
            onUpload={files => void uploadAttachments(files)}
            onView={attachment => void viewAttachment(attachment)}
          />
        </div>
      </section>

      {providerPickerOpen && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4 backdrop-blur-sm" role="dialog" aria-modal="true" aria-labelledby="provider-picker-title" onClick={event => { if (event.target === event.currentTarget) setProviderPickerOpen(false); }}>
          <div className="flex h-[86vh] w-full max-w-[92vw] flex-col overflow-hidden rounded-xl bg-white shadow-2xl">
            <div className="flex items-center justify-between border-b border-gray-100 px-5 py-4">
              <div>
                <h2 id="provider-picker-title" className="text-base font-semibold text-gray-900">Select preferred medical providers</h2>
                <p className="mt-0.5 text-xs text-gray-500">Add or remove provider recommendations before accepting this referral request.</p>
              </div>
              <div className="flex items-center gap-3">
                {selectedProviders.length > 0 && (
                  <button
                    type="button"
                    onClick={() => setSelectedProviders([])}
                    className="text-xs text-gray-500 underline hover:text-gray-700"
                  >
                    Clear selected
                  </button>
                )}
                <button type="button" onClick={() => setProviderPickerOpen(false)} aria-label="Close provider picker" className="rounded-lg p-1 text-gray-400 transition hover:bg-gray-100 hover:text-gray-600">
                  <i className="ri-close-line text-xl" />
                </button>
              </div>
            </div>
            <div className="space-y-2 border-b border-gray-100 px-5 py-3">
              <div className="flex items-center gap-2 overflow-x-auto pb-1">
                <div className="flex shrink-0 items-center overflow-hidden rounded-lg border border-gray-200 bg-gray-50 p-1">
                  {(["split", "list", "map"] as ProviderPickerViewMode[]).map(mode => (
                    <button key={mode} type="button" onClick={() => setProviderViewMode(mode)} className={`rounded-md px-3 py-1.5 text-xs font-medium capitalize transition-colors ${providerViewMode === mode ? "bg-white text-gray-900 shadow-sm" : "text-gray-500 hover:text-gray-700"}`}>
                      {mode}
                    </button>
                  ))}
                </div>
                <div className="relative min-w-[240px] flex-1">
                  <i className="ri-search-line pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-sm text-gray-400" />
                  <input type="search" value={providerSearch} onChange={event => setProviderSearch(event.target.value)} placeholder="Search by provider, specialty, address, city, state, or ZIP..." className="h-10 w-full rounded-lg border border-gray-300 bg-white pl-8 pr-3 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-400 focus:outline-none focus:ring-1 focus:ring-inset focus:ring-blue-100" />
                </div>
                <input value={providerZip} onChange={event => setProviderZip(event.target.value)} onKeyDown={event => { if (event.key === "Enter") void applyProviderZipFilter(); }} aria-label="ZIP code" placeholder="ZIP" className="h-10 w-24 shrink-0 rounded-lg border border-gray-300 bg-white px-3 text-sm" />
                <button type="button" onClick={() => void applyProviderZipFilter()} disabled={providerZipLoading} className="h-10 shrink-0 rounded-lg border border-gray-300 bg-white px-4 text-sm font-medium text-gray-600 hover:bg-gray-50 disabled:opacity-50">{providerZipLoading ? "Finding..." : "Apply ZIP"}</button>
                <Select value={providerSpecialtyCode || ALL_SPECIALTIES} onValueChange={value => setProviderSpecialtyCode(value === ALL_SPECIALTIES ? "" : value)}>
                  <SelectTrigger aria-label="Specialty" className="h-10 w-40 shrink-0 rounded-lg border-gray-300 bg-white text-sm"><SelectValue /></SelectTrigger>
                  <SelectContent>
                    <SelectItem value={ALL_SPECIALTIES}>All specialties</SelectItem>
                    {providerSpecialtyOptions.map(([code, name]) => <SelectItem key={code} value={code}>{name}</SelectItem>)}
                  </SelectContent>
                </Select>
                <button type="button" onClick={() => setProviderAcceptingOnly(value => !value)} className={`flex h-10 shrink-0 items-center gap-1.5 rounded-lg border px-4 text-sm font-medium ${providerAcceptingOnly ? "border-gray-300 bg-white text-gray-600" : "border-gray-900 bg-gray-900 text-white"}`}>
                  <i className="ri-filter-3-line text-sm" />{providerAcceptingOnly ? "Accepting only" : "All providers"}
                </button>
                <button type="button" onClick={clearProviderFilters} className="shrink-0 px-3 py-1.5 text-xs font-medium text-gray-500 hover:text-gray-700">Clear</button>
                <span className="shrink-0 text-xs font-medium text-gray-400">{filteredProviders.length} of {providers.length}</span>
                {selectedProviders.length > 0 && <span className="flex shrink-0 items-center gap-1.5 rounded-lg bg-blue-600 px-3 py-1.5 text-xs font-semibold text-white"><i className="ri-check-line text-xs" />{selectedProviders.length} selected</span>}
              </div>
              {providerZipError && <p className="text-xs text-red-600">{providerZipError}</p>}
            </div>
            <div className={`grid min-h-0 flex-1 ${providerViewMode === "split" ? "grid-cols-[340px_minmax(0,1fr)]" : "grid-cols-1"}`}>
              {providerViewMode !== "map" && <div className={`min-h-0 overflow-y-auto bg-white ${providerViewMode === "split" ? "border-r border-gray-200" : ""}`}>
                {filteredProviders.length === 0 ? <div className="flex h-full items-center justify-center px-6 text-center text-sm text-gray-500">No providers match your search.</div> : <div className={providerViewMode === "list" ? "grid grid-cols-2 gap-3 p-3" : "divide-y divide-gray-100"}>
                  {filteredProviders.map((provider, index) => {
                    const value = providerSelectionValue(provider.id, provider.facilityId);
                    const selected = selectedProviderKeys.has(value);
                    const identity = providerIdentity(provider);
                    return <div key={value} role="button" tabIndex={0} onClick={() => { setSelectedMarkerId(value); setZoomToProviderId(value); }} onKeyDown={event => { if (event.key === "Enter" || event.key === " ") { event.preventDefault(); setSelectedMarkerId(value); setZoomToProviderId(value); } }} className={`w-full cursor-pointer text-left transition-colors ${selectedMarkerId === value ? "bg-blue-50" : "hover:bg-gray-50"} ${providerViewMode === "list" ? "rounded-lg border border-gray-200 bg-white px-3 py-3" : "px-3 py-3"}`}>
                      <div className="flex items-start gap-3"><span className={`mt-0.5 flex h-7 w-7 shrink-0 items-center justify-center rounded-full text-xs font-bold text-white ${selectedMarkerId === value ? "bg-blue-700" : provider.acceptingReferrals ? "bg-red-600" : "bg-gray-500"}`}>{index + 1}</span><div className="min-w-0 flex-1"><p className={`truncate text-sm ${selected ? "font-semibold text-primary" : "font-medium text-gray-900"}`}>{identity.primary}</p>{identity.secondary && <p className="mt-0.5 truncate text-xs text-gray-500">{identity.secondary}</p>}<p className="mt-0.5 truncate text-xs text-gray-500">{providerLocationLine(provider)}</p>{providerZipLocation && <p className="mt-1 text-xs font-medium text-blue-600">{distanceMiles(providerZipLocation, provider).toFixed(1)} mi away</p>}{provider.phone && <p className="mt-1 text-xs text-gray-400">{formatPhoneDisplay(provider.phone)}</p>}{provider.specialties?.length > 0 && <div className="mt-2 flex flex-wrap gap-1">{provider.specialties.slice(0, 3).map(specialty => <span key={specialty.id} className="rounded bg-blue-50 px-1.5 py-0.5 text-[11px] text-blue-700">{specialty.name}</span>)}</div>}</div><button type="button" onClick={event => { event.stopPropagation(); toggleProvider(provider); }} className={`flex h-8 w-8 shrink-0 items-center justify-center rounded-lg text-xs font-semibold ${selected ? "bg-blue-600 text-white hover:bg-blue-700" : "bg-gray-100 text-gray-600 hover:bg-blue-50 hover:text-blue-700"}`} title={selected ? "Remove from preference list" : "Select provider"} aria-label={selected ? "Remove from preference list" : "Select provider"}><i className={selected ? "ri-check-line text-base" : "ri-add-line text-base"} /></button></div>
                    </div>;
                  })}
                </div>}
              </div>}
              {providerViewMode !== "list" && <div className="min-h-0 bg-gray-100">{numberedMarkers.length > 0 ? <PublicNetworkMap markers={numberedMarkers} selectedId={selectedMarkerId} zoomToId={zoomToProviderId} onZoomed={() => setZoomToProviderId(null)} searchLocation={providerZipLocation} onSelect={id => { setSelectedMarkerId(id); }} onRequestReferral={marker => { const provider = providers.find(item => providerSelectionValue(item.id, item.facilityId) === marker.id); if (provider) toggleProvider(provider); }} requestReferralLabel="Select" /> : <div className="flex h-full items-center justify-center text-sm text-gray-500">No location data available.</div>}</div>}
            </div>
            <div className="flex items-center justify-between border-t border-gray-100 px-5 py-3">
              <p className="text-xs text-gray-500">
                {selectedProviders.length > 0
                  ? `${selectedProviders.length} provider preference${selectedProviders.length === 1 ? "" : "s"} selected.`
                  : "Select one or more provider preferences before accepting."}
              </p>
              <button type="button" onClick={() => setProviderPickerOpen(false)} className="rounded-md bg-primary px-4 py-2 text-sm font-medium text-white transition hover:bg-primary/90">Done</button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
