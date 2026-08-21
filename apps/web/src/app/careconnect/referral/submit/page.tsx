"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import type { FormEvent } from "react";
import dynamic from "next/dynamic";
import { toast } from "sonner";
import {
  createPendingReferralRequest,
  fetchReferralPortalLawFirms,
  fetchReferralPortalTreatmentTypes,
  uploadRepresentativePendingRequestAttachment,
} from "@/lib/representative-portal-api";
import { useRepresentativePortal } from "@/components/careconnect/representative-access-code-gate";
import { ApiError } from "@/lib/api-client";
import {
  URGENCY_OPTIONS,
  type LawFirmOption,
  type ProviderMarker,
  type ProviderSummary,
  type TreatmentTypeOption,
  type ReferralUrgencyValue,
} from "@/types/careconnect";
import { formatPhoneDisplay, formatPhoneInput, isValidPhone, stripPhone } from "@/lib/phone";
import { isValidIsoDate, hasReasonableYear } from "@/lib/daterange";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Modal, ConfirmDialog } from "@/components/ui/modal";
import type { PublicNetworkDetail, PublicNetworkSummary, PublicProviderItem, PublicProviderMarker } from "@/lib/public-network-api";
import type { NumberedMarker } from "@/components/careconnect/public-network-map";

const PublicNetworkMap = dynamic(
  () => import("@/components/careconnect/public-network-map").then(m => m.PublicNetworkMap),
  { ssr: false, loading: () => <div className="h-full w-full bg-gray-100 animate-pulse" /> },
);

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

interface SubmitForm {
  lawFirmOrganizationId: string;
  clientFirstName: string;
  clientLastName: string;
  clientDob: string;
  clientPhone: string;
  clientEmail: string;
  requestedService: string;
  treatmentTypeId: string;
  urgency: ReferralUrgencyValue;
  dateOfAccident: string;
  notes: string;
  lienCompanyName: string;
  lienCompanyEmail: string;
}

const EMPTY_FORM: SubmitForm = {
  lawFirmOrganizationId: "",
  clientFirstName: "",
  clientLastName: "",
  clientDob: "",
  clientPhone: "",
  clientEmail: "",
  requestedService: "General Referral",
  treatmentTypeId: "",
  urgency: "Normal",
  dateOfAccident: "",
  notes: "",
  lienCompanyName: "",
  lienCompanyEmail: "",
};

const TODAY = new Date().toISOString().split("T")[0];
const NONE_TREATMENT_TYPE = "__none__";
const ALL_SPECIALTIES = "__all_specialties__";
const ATTACHMENT_ACCEPT = ".pdf,.doc,.docx,.jpg,.jpeg,.png,.gif,.webp,.txt,.csv,.xls,.xlsx";
const ZIP_CODE_PATTERN = /^\d{5}(?:-\d{4})?$/;
const ADDRESS_HINT_PATTERN = /\d|,|\b(?:apt|suite|ste|unit|street|st|road|rd|avenue|ave|boulevard|blvd|drive|dr|lane|ln|court|ct|circle|cir|place|pl|parkway|pkwy|highway|hwy|way|terrace|ter)\b/i;
const US_STATE_CODES = new Set([
  "AL", "AK", "AZ", "AR", "CA", "CO", "CT", "DE", "FL", "GA",
  "HI", "ID", "IL", "IN", "IA", "KS", "KY", "LA", "ME", "MD",
  "MA", "MI", "MN", "MS", "MO", "MT", "NE", "NV", "NH", "NJ",
  "NM", "NY", "NC", "ND", "OH", "OK", "OR", "PA", "RI", "SC",
  "SD", "TN", "TX", "UT", "VT", "VA", "WA", "WV", "WI", "WY",
]);
const US_STATE_NAMES = new Set([
  "alabama", "alaska", "arizona", "arkansas", "california", "colorado", "connecticut", "delaware",
  "florida", "georgia", "hawaii", "idaho", "illinois", "indiana", "iowa", "kansas", "kentucky",
  "louisiana", "maine", "maryland", "massachusetts", "michigan", "minnesota", "mississippi",
  "missouri", "montana", "nebraska", "nevada", "new hampshire", "new jersey", "new mexico",
  "new york", "north carolina", "north dakota", "ohio", "oklahoma", "oregon", "pennsylvania",
  "rhode island", "south carolina", "south dakota", "tennessee", "texas", "utah", "vermont",
  "virginia", "washington", "west virginia", "wisconsin", "wyoming",
]);
const US_STATE_CODE_TO_NAME: Record<string, string> = {
  AL: "alabama", AK: "alaska", AZ: "arizona", AR: "arkansas", CA: "california",
  CO: "colorado", CT: "connecticut", DE: "delaware", FL: "florida", GA: "georgia",
  HI: "hawaii", ID: "idaho", IL: "illinois", IN: "indiana", IA: "iowa",
  KS: "kansas", KY: "kentucky", LA: "louisiana", ME: "maine", MD: "maryland",
  MA: "massachusetts", MI: "michigan", MN: "minnesota", MS: "mississippi", MO: "missouri",
  MT: "montana", NE: "nebraska", NV: "nevada", NH: "new hampshire", NJ: "new jersey",
  NM: "new mexico", NY: "new york", NC: "north carolina", ND: "north dakota",
  OH: "ohio", OK: "oklahoma", OR: "oregon", PA: "pennsylvania", RI: "rhode island",
  SC: "south carolina", SD: "south dakota", TN: "tennessee", TX: "texas",
  UT: "utah", VT: "vermont", VA: "virginia", WA: "washington", WV: "west virginia",
  WI: "wisconsin", WY: "wyoming",
};
const US_STATE_NAME_TO_CODE = new Map(
  Object.entries(US_STATE_CODE_TO_NAME).map(([code, name]) => [name, code]),
);

const inputCls = (hasError?: boolean) =>
  `w-full border rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 ${
    hasError ? "border-red-300 focus:ring-red-400" : "border-gray-300 focus:ring-primary"
  }`;

const selectTriggerCls = (hasError?: boolean) =>
  `h-auto py-2 text-sm ${hasError ? "border-red-300 focus:ring-red-100" : ""}`;

function formatFileSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

type ProviderPreference = Pick<ProviderSummary, "id" | "facilityId" | "displayLabel" | "markerSubtitle" | "phone" | "specialties">;
type ProviderPickerMarker = ProviderMarker & {
  networkProviderId?: string | null;
  facilityName?: string | null;
};
type ProviderPickerViewMode = "split" | "list" | "map";
type SearchLocation = {
  latitude: number;
  longitude: number;
  label: string;
  source?: "geocode" | "providerFallback";
};

function providerPreferenceKey(provider: Pick<ProviderPreference, "id" | "facilityId">): string {
  return `${provider.id}|${provider.facilityId ?? ""}`;
}

function providerMarkerKey(marker: Pick<ProviderPickerMarker, "id" | "facilityId">): string {
  return `${marker.id}|${marker.facilityId ?? ""}`;
}

function providerPreferenceFromMarker(marker: ProviderPickerMarker): ProviderPreference {
  return {
    id: marker.id,
    facilityId: marker.facilityId ?? null,
    displayLabel: marker.displayLabel,
    markerSubtitle: marker.markerSubtitle,
    phone: marker.phone ? formatPhoneDisplay(marker.phone) : null,
    specialties: marker.specialties,
  };
}

function publicProviderDisplayLabel(provider: PublicProviderItem): string {
  return provider.organizationName?.trim() || provider.name;
}

function publicProviderSubtitle(provider: PublicProviderItem): string {
  return [
    provider.city && provider.state ? `${provider.city}, ${provider.state}` : provider.city || provider.state,
    provider.primarySpecialty,
  ].filter(Boolean).join(" · ");
}

function getProviderIdentity(provider: { name: string; organizationName?: string | null }) {
  const providerName = provider.name.trim();
  const organizationName = provider.organizationName?.trim() ?? "";
  const hasDistinctOrganization =
    organizationName.length > 0 && organizationName.toLowerCase() !== providerName.toLowerCase();

  return {
    primary: organizationName || providerName,
    secondary: hasDistinctOrganization ? providerName : null,
  };
}

function providerLocationLine(provider: ProviderPickerMarker): string {
  if (provider.isMobile) {
    return `Mobile · ${[provider.serviceAreaLabel, `${provider.city}, ${provider.state}`].filter(Boolean).join(" · ")}`;
  }

  return [provider.city, provider.state].filter(Boolean).join(", ");
}

function publicNetworkProviderToPickerMarker(
  provider: PublicProviderItem,
  marker?: PublicProviderMarker,
): ProviderPickerMarker {
  return {
    id: provider.providerId,
    networkProviderId: provider.networkProviderId,
    facilityId: provider.facilityId,
    facilityName: provider.facilityName,
    name: provider.name,
    title: provider.title,
    organizationName: provider.organizationName ?? undefined,
    displayLabel: publicProviderDisplayLabel(provider),
    markerSubtitle: publicProviderSubtitle(provider),
    city: provider.city,
    state: provider.state,
    addressLine1: provider.addressLine1,
    postalCode: provider.postalCode,
    email: provider.email ?? "",
    phone: provider.phone,
    acceptingReferrals: provider.acceptingReferrals,
    isActive: provider.isActive,
    latitude: marker?.latitude ?? 0,
    longitude: marker?.longitude ?? 0,
    primaryCategory: provider.primaryCategory ?? undefined,
    categories: provider.primaryCategory ? [provider.primaryCategory] : [],
    specialties: provider.specialties,
    primarySpecialty: provider.primarySpecialty,
    primarySpecialtyId: provider.primarySpecialtyId,
    distanceMiles: provider.distanceMiles,
    isMobile: provider.isMobile,
    serviceRadiusMiles: provider.serviceRadiusMiles,
    serviceAreaLabel: provider.serviceAreaLabel,
  };
}

function buildProviderAddressGeocodeQuery(provider: PublicProviderItem): string | null {
  const locality = [provider.city, provider.state, provider.postalCode]
    .map(value => value?.trim() ?? "")
    .filter(Boolean);
  if (locality.length === 0) return null;

  if (provider.isMobile) return locality.join(", ");

  const addressLine1 = provider.addressLine1?.trim();
  if (!addressLine1) return null;

  return [addressLine1, ...locality].join(", ");
}

function normalizeSearchValue(value: string): string {
  return value.trim().toLowerCase().replace(/[.,]/g, " ").replace(/\s+/g, " ");
}

function providerStateSearchValues(state: string): string[] {
  const trimmed = state.trim();
  if (!trimmed) return [];

  const values = new Set<string>([trimmed]);
  const upper = trimmed.toUpperCase();
  const normalized = normalizeSearchValue(trimmed);

  const fullName = US_STATE_CODE_TO_NAME[upper];
  if (fullName) values.add(fullName);

  const code = US_STATE_NAME_TO_CODE.get(normalized);
  if (code) values.add(code);

  return [...values];
}

function providerTextValues(provider: ProviderPickerMarker): string[] {
  return [
    provider.displayLabel,
    provider.name,
    provider.organizationName ?? "",
    provider.markerSubtitle,
    provider.addressLine1 ?? "",
    provider.postalCode ?? "",
    provider.city,
    provider.state,
    provider.primarySpecialty ?? "",
    ...(provider.specialties ?? []).flatMap(s => [s.name, s.code]),
  ];
}

function providerHasTextMatch(query: string, providers: ProviderPickerMarker[]): boolean {
  const target = normalizeSearchValue(query);
  if (!target) return false;
  return providers.some(provider =>
    [
      provider.displayLabel,
      provider.name,
      provider.organizationName ?? "",
      provider.primarySpecialty ?? "",
      ...(provider.specialties ?? []).map(s => s.name),
    ].some(value => normalizeSearchValue(value).includes(target)),
  );
}

function providerLocationSearchValues(provider: ProviderPickerMarker): string[] {
  const city = provider.city?.trim() ?? "";
  const state = provider.state?.trim() ?? "";
  const addressLine1 = provider.addressLine1?.trim() ?? "";
  const postalCode = provider.postalCode?.trim() ?? "";
  const stateValues = providerStateSearchValues(state);
  const values = [
    addressLine1,
    city,
    postalCode,
    ...stateValues,
  ];

  for (const stateValue of stateValues) {
    values.push(
      [addressLine1, city, stateValue, postalCode].filter(Boolean).join(" "),
      [city, stateValue].filter(Boolean).join(" "),
      [city, stateValue, postalCode].filter(Boolean).join(" "),
      [stateValue, postalCode].filter(Boolean).join(" "),
    );
  }

  return values;
}

function providerHasExactLocationMatch(query: string, providers: ProviderPickerMarker[]): boolean {
  const target = normalizeSearchValue(query);
  if (!target) return false;
  return providers.some(provider =>
    providerLocationSearchValues(provider).some(value => normalizeSearchValue(value) === target),
  );
}

function isProviderStreetAddressQuery(query: string): boolean {
  return ADDRESS_HINT_PATTERN.test(query.trim());
}

function providerMatchesExactLocation(provider: ProviderPickerMarker, query: string): boolean {
  const target = normalizeSearchValue(query);
  if (!target) return false;
  return providerLocationSearchValues(provider)
    .some(value => normalizeSearchValue(value) === target);
}

function providerMatchesLocationContext(provider: ProviderPickerMarker, query: string): boolean {
  if (providerMatchesExactLocation(provider, query)) return true;

  const target = normalizeSearchValue(query);
  const addressLine1 = normalizeSearchValue(provider.addressLine1 ?? "");
  if (addressLine1 && target.includes(addressLine1)) return true;

  const city = normalizeSearchValue(provider.city ?? "");
  const postalCode = normalizeSearchValue(provider.postalCode ?? "");
  const stateValues = providerStateSearchValues(provider.state ?? "").map(normalizeSearchValue);
  return Boolean(
    postalCode && target.includes(postalCode) &&
    city && target.includes(city) &&
    stateValues.some(state => state && target.includes(state)),
  );
}

function getExactStreetProviderKeys(query: string, providers: ProviderPickerMarker[]): Set<string> {
  if (!isProviderStreetAddressQuery(query)) return new Set<string>();

  const target = normalizeSearchValue(query);
  const addressMatches = providers.filter(provider => {
    const addressLine1 = normalizeSearchValue(provider.addressLine1 ?? "");
    return addressLine1.length >= 5 && target.includes(addressLine1);
  });
  if (addressMatches.length > 0) {
    return new Set(addressMatches.map(providerMarkerKey));
  }

  const postalMatches = providers.filter(provider => {
    const postalCode = normalizeSearchValue(provider.postalCode ?? "");
    return postalCode.length >= 5 && target.includes(postalCode);
  });
  return postalMatches.length === 1
    ? new Set(postalMatches.map(providerMarkerKey))
    : new Set<string>();
}

function usableProviderCoordinates(provider: ProviderPickerMarker): { latitude: number; longitude: number } | null {
  const latitude = Number(provider.latitude);
  const longitude = Number(provider.longitude);
  if (!Number.isFinite(latitude) || !Number.isFinite(longitude)) return null;
  if (latitude === 0 && longitude === 0) return null;
  return { latitude, longitude };
}

function getProviderLocationFallback(query: string, providers: ProviderPickerMarker[]): SearchLocation | null {
  const target = normalizeSearchValue(query);
  if (!target) return null;

  const matches: Array<{ latitude: number; longitude: number }> = [];
  const exactStreetProviderKeys = getExactStreetProviderKeys(query, providers);

  for (const provider of providers) {
    const key = providerMarkerKey(provider);
    const matchesQuery = exactStreetProviderKeys.size > 0
      ? exactStreetProviderKeys.has(key)
      : providerMatchesLocationContext(provider, query);
    if (!matchesQuery) continue;

    const coordinates = usableProviderCoordinates(provider);
    if (coordinates) matches.push(coordinates);
  }

  if (matches.length === 0) return null;

  return {
    latitude: matches.reduce((sum, point) => sum + point.latitude, 0) / matches.length,
    longitude: matches.reduce((sum, point) => sum + point.longitude, 0) / matches.length,
    label: query.trim(),
    source: "providerFallback",
  };
}

function shouldTryProviderSearchGeocode(query: string, providers: ProviderPickerMarker[]): boolean {
  const value = query.trim();
  if (!value) return false;
  if (ZIP_CODE_PATTERN.test(value)) return true;

  const normalized = normalizeSearchValue(value);
  if (US_STATE_CODES.has(value.toUpperCase()) || US_STATE_NAMES.has(normalized)) return true;
  if (value.length < 3) return false;
  if (providerHasExactLocationMatch(value, providers)) return true;
  if (ADDRESS_HINT_PATTERN.test(value)) return true;
  if (providerHasTextMatch(value, providers)) return false;

  return /^[a-z][a-z\s'.-]*$/i.test(value) && value.split(/\s+/).length <= 3;
}

function firstUsableSearchLocation(
  suggestions: Array<{ latitude: number; longitude: number; displayName?: string | null }>,
  fallbackLabel: string,
): SearchLocation | null {
  for (const suggestion of suggestions) {
    const latitude = Number(suggestion.latitude);
    const longitude = Number(suggestion.longitude);
    if (!Number.isFinite(latitude) || !Number.isFinite(longitude)) continue;
    if (latitude === 0 && longitude === 0) continue;
    return {
      latitude,
      longitude,
      label: suggestion.displayName?.trim() || fallbackLabel,
    };
  }
  return null;
}

function distanceMilesBetween(a: SearchLocation, b: { latitude: number; longitude: number }): number {
  const toRad = (deg: number) => deg * Math.PI / 180;
  const radiusMiles = 3958.7613;
  const dLat = toRad(b.latitude - a.latitude);
  const dLng = toRad(b.longitude - a.longitude);
  const lat1 = toRad(a.latitude);
  const lat2 = toRad(b.latitude);
  const h =
    Math.sin(dLat / 2) * Math.sin(dLat / 2) +
    Math.cos(lat1) * Math.cos(lat2) *
    Math.sin(dLng / 2) * Math.sin(dLng / 2);
  const clamped = Math.min(1, Math.max(0, h));
  return 2 * radiusMiles * Math.atan2(Math.sqrt(clamped), Math.sqrt(1 - clamped));
}

function toNumberedProviderMarker(marker: ProviderPickerMarker, index: number): NumberedMarker {
  const id = providerMarkerKey(marker);
  return {
    id,
    networkProviderId: marker.networkProviderId ?? id,
    providerId: marker.id,
    facilityId: marker.facilityId ?? "",
    name: marker.name,
    title: marker.title,
    organizationName: marker.organizationName ?? null,
    facilityName: marker.facilityName ?? marker.markerSubtitle,
    city: marker.city,
    state: marker.state,
    acceptingReferrals: marker.acceptingReferrals,
    latitude: marker.latitude,
    longitude: marker.longitude,
    specialties: marker.specialties,
    primarySpecialtyId: marker.primarySpecialtyId ?? null,
    primarySpecialty: marker.primarySpecialty ?? null,
    distanceMiles: marker.distanceMiles,
    isMobile: marker.isMobile,
    serviceRadiusMiles: marker.serviceRadiusMiles,
    serviceAreaLabel: marker.serviceAreaLabel,
    phone: marker.phone,
    addressLine1: marker.addressLine1,
    postalCode: marker.postalCode,
    index,
  };
}

async function fetchReferralPortalPublicNetworkMarkers(): Promise<ProviderPickerMarker[]> {
  const networksRes = await fetch("/api/public/careconnect/api/public/network");
  if (!networksRes.ok) throw new Error("Failed to load public provider networks.");
  const networks = await networksRes.json() as PublicNetworkSummary[];
  const network = networks[0];
  if (!network) return [];

  const detailRes = await fetch(`/api/public/careconnect/api/public/network/${network.id}/detail`);
  if (!detailRes.ok) throw new Error("Failed to load public provider network.");
  const detail = await detailRes.json() as PublicNetworkDetail;
  const markerByNetworkProviderId = new Map(
    detail.markers.map(marker => [marker.networkProviderId, marker]),
  );

  const markers = detail.providers.map(provider =>
    publicNetworkProviderToPickerMarker(provider, markerByNetworkProviderId.get(provider.networkProviderId)),
  );

  await Promise.all(markers.map(async marker => {
    if (usableProviderCoordinates(marker)) return;
    const provider = detail.providers.find(p => p.networkProviderId === marker.networkProviderId);
    if (!provider) return;
    const query = buildProviderAddressGeocodeQuery(provider);
    if (!query) return;

    try {
      const res = await fetch(`/api/geocode/address?q=${encodeURIComponent(query)}&loose=1`);
      if (!res.ok) return;
      const suggestions = await res.json() as Array<{ latitude: number; longitude: number }>;
      const suggestion = suggestions.find(item => {
        const latitude = Number(item.latitude);
        const longitude = Number(item.longitude);
        return Number.isFinite(latitude) && Number.isFinite(longitude) && !(latitude === 0 && longitude === 0);
      });
      if (!suggestion) return;
      marker.latitude = Number(suggestion.latitude);
      marker.longitude = Number(suggestion.longitude);
    } catch {
      // Keep the provider selectable even when its fallback geocode is unavailable.
    }
  }));

  return markers;
}

export default function ReferralPortalSubmitPage() {
  const { code } = useRepresentativePortal();
  const providerRowRefs = useRef<Record<string, HTMLDivElement | null>>({});
  const [lawFirms, setLawFirms] = useState<LawFirmOption[]>([]);
  const [treatmentTypes, setTreatmentTypes] = useState<TreatmentTypeOption[]>([]);
  const [providerMarkers, setProviderMarkers] = useState<ProviderPickerMarker[]>([]);
  const [providersLoading, setProvidersLoading] = useState(true);
  const [providerSearch, setProviderSearch] = useState("");
  const [providerSearchLocation, setProviderSearchLocation] = useState<SearchLocation | null>(null);
  const [providerSearchLocationLoading, setProviderSearchLocationLoading] = useState(false);
  const [settledProviderSearchLocationQuery, setSettledProviderSearchLocationQuery] = useState("");
  const [providerZip, setProviderZip] = useState("");
  const [providerZipLocation, setProviderZipLocation] = useState<SearchLocation | null>(null);
  const [providerZipLoading, setProviderZipLoading] = useState(false);
  const [providerZipError, setProviderZipError] = useState<string | null>(null);
  const [providerSpecialtyCode, setProviderSpecialtyCode] = useState("");
  const [providerAcceptingOnly, setProviderAcceptingOnly] = useState(true);
  const [providerViewMode, setProviderViewMode] = useState<ProviderPickerViewMode>("split");
  const [selectedMarkerId, setSelectedMarkerId] = useState<string | null>(null);
  const [zoomToProviderId, setZoomToProviderId] = useState<string | null>(null);
  const [providerPickerOpen, setProviderPickerOpen] = useState(false);
  const [preferredProviders, setPreferredProviders] = useState<ProviderPreference[]>([]);
  const [form, setForm] = useState<SubmitForm>(EMPTY_FORM);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [submitting, setSubmitting] = useState(false);
  const [attachments, setAttachments] = useState<File[]>([]);
  const [lawFirmModalOpen, setLawFirmModalOpen] = useState(false);
  const [lawFirmSearch, setLawFirmSearch] = useState("");
  const [pendingLawFirmId, setPendingLawFirmId] = useState("");
  const [confirmOpen, setConfirmOpen] = useState(false);

  useEffect(() => {
    fetchReferralPortalLawFirms(code)
      .then(({ data }) => setLawFirms(data))
      .catch(err => toast.error("Failed to load law firms", {
        description: err instanceof ApiError ? err.message : undefined,
      }));
  }, [code]);

  useEffect(() => {
    fetchReferralPortalTreatmentTypes()
      .then(({ data }) => setTreatmentTypes(data))
      .catch(() => {});
  }, []);

  useEffect(() => {
    let cancelled = false;
    setProvidersLoading(true);

    fetchReferralPortalPublicNetworkMarkers()
      .then(markers => {
        if (cancelled) return;
        setProviderMarkers(markers);
      })
      .catch(err => {
        if (cancelled) return;
        toast.error("Failed to load medical providers", {
          description: err instanceof ApiError ? err.message : undefined,
        });
      })
      .finally(() => {
        if (!cancelled) setProvidersLoading(false);
      });

    return () => { cancelled = true; };
  }, [code]);

  useEffect(() => {
    const value = providerSearch.trim();

    if (!shouldTryProviderSearchGeocode(value, providerMarkers)) {
      setProviderSearchLocation(null);
      setSettledProviderSearchLocationQuery("");
      setProviderSearchLocationLoading(false);
      return;
    }

    const providerFallbackLocation = getProviderLocationFallback(value, providerMarkers);
    const exactStreetProviderKeys = getExactStreetProviderKeys(value, providerMarkers);
    const initialFallbackLocation = !isProviderStreetAddressQuery(value) || exactStreetProviderKeys.size > 0
      ? providerFallbackLocation
      : null;
    setProviderSearchLocation(initialFallbackLocation);
    if (initialFallbackLocation) {
      setProviderZip("");
      setProviderZipLocation(null);
      setProviderZipError(null);
    }

    let cancelled = false;
    const timer = window.setTimeout(async () => {
      setProviderSearchLocationLoading(true);
      try {
        const controller = new AbortController();
        const timeout = window.setTimeout(() => controller.abort(), 10000);
        let res: Response;
        try {
          res = await fetch(`/api/geocode/address?q=${encodeURIComponent(value)}&loose=1`, {
            signal: controller.signal,
          });
        } finally {
          window.clearTimeout(timeout);
        }
        if (!res.ok) throw new Error("Unable to geocode search input.");
        const suggestions = await res.json() as Array<{ latitude: number; longitude: number; displayName?: string | null }>;
        const location = firstUsableSearchLocation(suggestions, value);

        if (!cancelled) {
          const nextLocation = location ?? providerFallbackLocation;
          setProviderSearchLocation(nextLocation);
          if (nextLocation) {
            setProviderZip("");
            setProviderZipLocation(null);
            setProviderZipError(null);
          }
        }
      } catch {
        if (!cancelled) setProviderSearchLocation(providerFallbackLocation);
      } finally {
        if (!cancelled) {
          setSettledProviderSearchLocationQuery(value);
          setProviderSearchLocationLoading(false);
        }
      }
    }, 700);

    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [providerSearch, providerMarkers]);

  const update = (field: keyof SubmitForm, value: string) => {
    setForm(prev => ({ ...prev, [field]: value }));
    setFieldErrors(prev => { const n = { ...prev }; delete n[field]; return n; });
  };

  const selectedLawFirm = lawFirms.find(f => f.id === form.lawFirmOrganizationId) ?? null;
  const filteredLawFirms = useMemo(() => {
    const q = lawFirmSearch.trim().toLowerCase();
    if (!q) return lawFirms;
    return lawFirms.filter(f => f.name.toLowerCase().includes(q));
  }, [lawFirms, lawFirmSearch]);

  const activeProviderSearchLocation = providerSearchLocation ?? providerZipLocation;
  const providerSearchNeedsLocationResolution = shouldTryProviderSearchGeocode(providerSearch.trim(), providerMarkers);
  const providerSearchActsAsLocation = providerSearchLocation !== null;
  const providerSearchLocationPending =
    providerSearchNeedsLocationResolution &&
    providerSearch.trim() !== settledProviderSearchLocationQuery;

  const filteredProviderMarkers = useMemo(() => {
    let list = providerMarkers;
    if (providerAcceptingOnly) {
      list = list.filter(provider => provider.acceptingReferrals);
    }
    if (providerSpecialtyCode) {
      list = list.filter(provider => (provider.specialties ?? []).some(s => s.code === providerSpecialtyCode));
    }

    const q = providerSearch.trim().toLowerCase();
    if (q && !providerSearchActsAsLocation) {
      list = list.filter(provider => {
        const values = providerTextValues(provider);
        return values.some(value => value.toLowerCase().includes(q));
      });
    }

    if (activeProviderSearchLocation) {
      list = [...list].sort((a, b) =>
        distanceMilesBetween(activeProviderSearchLocation, a) - distanceMilesBetween(activeProviderSearchLocation, b),
      );
    }

    return list;
  }, [
    providerMarkers,
    providerSearch,
    providerSearchActsAsLocation,
    providerAcceptingOnly,
    providerSpecialtyCode,
    activeProviderSearchLocation,
  ]);

  const numberedMarkers = useMemo(
    () => filteredProviderMarkers.map((marker, index) => {
      const numbered = toNumberedProviderMarker(marker, index + 1);
      return activeProviderSearchLocation
        ? { ...numbered, distanceMiles: distanceMilesBetween(activeProviderSearchLocation, marker) }
        : numbered;
    }),
    [filteredProviderMarkers, activeProviderSearchLocation],
  );

  const preferredProviderKeys = useMemo(
    () => new Set(preferredProviders.map(providerPreferenceKey)),
    [preferredProviders],
  );

  const providerSpecialtyOptions = useMemo(() => {
    const byCode = new Map<string, string>();
    for (const provider of providerMarkers) {
      for (const specialty of provider.specialties ?? []) {
        if (specialty.code && !byCode.has(specialty.code)) byCode.set(specialty.code, specialty.name);
      }
    }
    return [...byCode.entries()]
      .map(([code, name]) => ({ code, name }))
      .sort((a, b) => a.name.localeCompare(b.name));
  }, [providerMarkers]);

  function openLawFirmModal() {
    setPendingLawFirmId(form.lawFirmOrganizationId);
    setLawFirmSearch("");
    setLawFirmModalOpen(true);
  }

  function closeLawFirmModal() {
    setLawFirmModalOpen(false);
    setLawFirmSearch("");
  }

  function confirmLawFirmSelection() {
    update("lawFirmOrganizationId", pendingLawFirmId);
    closeLawFirmModal();
  }

  function clearLawFirmSelection() {
    update("lawFirmOrganizationId", "");
  }

  function addPreferredProvider(provider: ProviderPreference) {
    const key = providerPreferenceKey(provider);
    setPreferredProviders(prev => {
      if (prev.some(p => providerPreferenceKey(p) === key)) return prev;
      return [...prev, provider];
    });
    setSelectedMarkerId(key);
    requestProviderZoom(key);
  }

  function togglePreferredProvider(provider: ProviderPreference) {
    const key = providerPreferenceKey(provider);
    setPreferredProviders(prev => {
      if (prev.some(p => providerPreferenceKey(p) === key)) {
        return prev.filter(p => providerPreferenceKey(p) !== key);
      }
      return [...prev, provider];
    });
    setSelectedMarkerId(key);
    requestProviderZoom(key);
  }

  function selectProviderFromMarker(marker: PublicProviderMarker) {
    const providerMarker = providerMarkers.find(p => providerMarkerKey(p) === marker.id)
      ?? providerMarkers.find(p => p.id === marker.providerId);
    const provider = providerMarker ? providerPreferenceFromMarker(providerMarker) : null;
    if (provider) addPreferredProvider(provider);
    setSelectedMarkerId(marker.id);
    requestProviderZoom(marker.id);
  }

  function focusProvider(provider: ProviderPickerMarker) {
    const key = providerMarkerKey(provider);
    setSelectedMarkerId(key);
    requestProviderZoom(key);
  }

  function togglePreferredProviderMarker(provider: ProviderPickerMarker) {
    togglePreferredProvider(providerPreferenceFromMarker(provider));
  }

  function focusProviderFromMarker(providerKey: string) {
    setSelectedMarkerId(providerKey);
    providerRowRefs.current[providerKey]?.scrollIntoView({ behavior: "smooth", block: "nearest" });
  }

  function requestProviderZoom(providerKey: string) {
    setZoomToProviderId(null);
    window.requestAnimationFrame(() => setZoomToProviderId(providerKey));
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
      const res = await fetch(`/api/geocode/address?q=${encodeURIComponent(value)}&loose=1`);
      if (!res.ok) throw new Error("Unable to geocode ZIP code.");
      const suggestions = await res.json() as Array<{ latitude: number; longitude: number; displayName?: string | null }>;
      const location = suggestions
        .map(suggestion => ({
          latitude: Number(suggestion.latitude),
          longitude: Number(suggestion.longitude),
          label: suggestion.displayName?.trim() || value,
        }))
        .find(suggestion =>
          Number.isFinite(suggestion.latitude) &&
          Number.isFinite(suggestion.longitude) &&
          !(suggestion.latitude === 0 && suggestion.longitude === 0),
        );

      if (!location) throw new Error("No matching ZIP code was found.");
      setProviderSearchLocation(null);
      setSettledProviderSearchLocationQuery("");
      setProviderZipLocation(location);
      setSelectedMarkerId(null);
      setZoomToProviderId(null);
    } catch (err) {
      setProviderZipLocation(null);
      setProviderZipError(err instanceof Error ? err.message : "Unable to geocode ZIP code.");
    } finally {
      setProviderZipLoading(false);
    }
  }

  function clearProviderFilters() {
    setProviderSearch("");
    setProviderSearchLocation(null);
    setProviderSearchLocationLoading(false);
    setSettledProviderSearchLocationQuery("");
    setProviderZip("");
    setProviderZipLocation(null);
    setProviderZipError(null);
    setProviderSpecialtyCode("");
    setProviderAcceptingOnly(true);
    setSelectedMarkerId(null);
    setZoomToProviderId(null);
  }

  function removePreferredProvider(key: string) {
    setPreferredProviders(prev => prev.filter(provider => providerPreferenceKey(provider) !== key));
  }

  function previewAttachment(file: File) {
    const url = URL.createObjectURL(file);
    window.open(url, "_blank", "noopener,noreferrer");
    window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
  }

  const hasClientPhoneValue = form.clientPhone.trim().length > 0;
  const hasInvalidClientPhone = hasClientPhoneValue && !isValidPhone(form.clientPhone);

  const canSubmit =
    !!form.lawFirmOrganizationId &&
    !!form.clientFirstName.trim() &&
    !!form.clientLastName.trim() &&
    hasClientPhoneValue && !hasInvalidClientPhone &&
    (!form.clientEmail.trim() || /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(form.clientEmail.trim())) &&
    (!form.lienCompanyEmail.trim() || /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(form.lienCompanyEmail.trim())) &&
    !!form.clientDob && isValidIsoDate(form.clientDob) && hasReasonableYear(form.clientDob) && new Date(form.clientDob) <= new Date() &&
    !!form.dateOfAccident && isValidIsoDate(form.dateOfAccident) && hasReasonableYear(form.dateOfAccident) && new Date(form.dateOfAccident) <= new Date();

  function validate(): Record<string, string> {
    const errs: Record<string, string> = {};
    if (!form.lawFirmOrganizationId) errs.lawFirmOrganizationId = "Select a law firm.";
    if (!form.clientFirstName.trim()) errs.clientFirstName = "Patient first name is required.";
    if (!form.clientLastName.trim()) errs.clientLastName = "Patient last name is required.";
    if (!form.clientPhone.trim()) errs.clientPhone = "Patient phone is required.";
    else if (!isValidPhone(form.clientPhone)) errs.clientPhone = "Enter a valid 10-digit phone number.";
    if (form.clientEmail.trim() && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(form.clientEmail.trim()))
      errs.clientEmail = "Enter a valid email address.";
    if (form.lienCompanyEmail.trim() && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(form.lienCompanyEmail.trim()))
      errs.lienCompanyEmail = "Enter a valid lien company email address.";
    if (!form.clientDob) errs.clientDob = "Date of birth is required.";
    else if (!isValidIsoDate(form.clientDob)) errs.clientDob = "Enter a valid date of birth.";
    else if (form.clientDob && !hasReasonableYear(form.clientDob)) errs.clientDob = "Please enter a valid year (1900 or later).";
    else if (form.clientDob && new Date(form.clientDob) > new Date()) errs.clientDob = "Date of birth cannot be in the future.";
    if (!form.dateOfAccident) errs.dateOfAccident = "Date of accident is required.";
    else if (!isValidIsoDate(form.dateOfAccident)) errs.dateOfAccident = "Enter a valid date of accident.";
    else if (form.dateOfAccident && !hasReasonableYear(form.dateOfAccident)) errs.dateOfAccident = "Please enter a valid year (1900 or later).";
    else if (form.dateOfAccident && new Date(form.dateOfAccident) > new Date()) errs.dateOfAccident = "Date of accident cannot be in the future.";
    return errs;
  }

  function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const errs = validate();
    setFieldErrors(errs);
    if (Object.keys(errs).length > 0) return;
    setConfirmOpen(true);
  }

  async function performSubmit() {
    setSubmitting(true);
    try {
      const { data: created } = await createPendingReferralRequest(code, {
        lawFirmOrganizationId: form.lawFirmOrganizationId,
        clientFirstName: form.clientFirstName.trim(),
        clientLastName: form.clientLastName.trim(),
        clientDob: form.clientDob || undefined,
        clientPhone: stripPhone(form.clientPhone),
        clientEmail: form.clientEmail.trim() || undefined,
        requestedService: form.requestedService || undefined,
        treatmentTypeId: form.treatmentTypeId || undefined,
        urgency: form.urgency,
        dateOfAccident: form.dateOfAccident || undefined,
        preferredProviders: preferredProviders.map(provider => ({
          providerId: provider.id,
          facilityId: provider.facilityId ?? undefined,
        })),
        recommendedProviderId: preferredProviders[0]?.id,
        recommendedFacilityId: preferredProviders[0]?.facilityId ?? undefined,
        notes: form.notes.trim() || undefined,
        lienCompanyName: form.lienCompanyName.trim() || undefined,
        lienCompanyEmail: form.lienCompanyEmail.trim() || undefined,
      });

      for (const file of attachments) {
        try {
          await uploadRepresentativePendingRequestAttachment(code, created.id, file);
        } catch (uploadError) {
          throw new Error(
            `Referral request submitted, but ${file.name} could not be uploaded. ${
              uploadError instanceof ApiError ? uploadError.message : "Please try uploading again later."
            }`
          );
        }
      }

      setForm(EMPTY_FORM);
      setAttachments([]);
      setPreferredProviders([]);
      setSelectedMarkerId(null);
      setFieldErrors({});
      setConfirmOpen(false);
      toast.success("Referral request submitted", {
        description: "The selected law firm will review it before provider routing.",
      });
    } catch (err) {
      setConfirmOpen(false);
      toast.error("Failed to submit referral request", {
        description: err instanceof ApiError || err instanceof Error ? err.message : undefined,
      });
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className="max-w-6xl">
      <h1 className="text-xl font-semibold text-gray-900">Submit Referral Request</h1>
      <p className="mt-1 text-sm text-gray-500">Requests are reviewed by the selected law firm before provider routing.</p>

      <form onSubmit={onSubmit} className="mt-6 bg-white border border-gray-100 rounded-xl px-6 py-5 space-y-6">
        <fieldset>
          <legend className="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-3">
            Law Firm
          </legend>
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">
              Law firm <span className="text-red-500">*</span>
            </label>
            <div
              onClick={submitting ? undefined : openLawFirmModal}
              className={`w-full flex items-center gap-2 rounded-md border pl-3 pr-1.5 py-1.5 text-sm bg-white ${
                submitting ? "opacity-50 cursor-not-allowed" : "cursor-pointer"
              } ${fieldErrors.lawFirmOrganizationId ? "border-red-300" : "border-gray-300"}`}
            >
              {selectedLawFirm ? (
                <span className="flex-1 min-w-0 truncate text-gray-900">{selectedLawFirm.name}</span>
              ) : (
                <span className="flex-1 min-w-0 text-gray-400">No law firm selected</span>
              )}
              <div className="flex items-center gap-1 shrink-0">
                {selectedLawFirm ? (
                  <button
                    type="button"
                    onClick={e => { e.stopPropagation(); clearLawFirmSelection(); }}
                    disabled={submitting}
                    aria-label="Clear selected law firm"
                    className="flex h-6 w-6 cursor-pointer items-center justify-center rounded-full text-gray-400 hover:text-gray-700 hover:bg-gray-100 disabled:cursor-not-allowed disabled:opacity-50"
                  >
                    <i className="ri-close-line text-base" />
                  </button>
                ) : (
                  <button
                    type="button"
                    onClick={e => { e.stopPropagation(); openLawFirmModal(); }}
                    disabled={submitting}
                    className="text-sm px-3 py-1 rounded-md bg-primary hover:bg-primary/90 text-white cursor-pointer disabled:opacity-50 disabled:cursor-not-allowed"
                  >
                    Select
                  </button>
                )}
              </div>
            </div>
            {fieldErrors.lawFirmOrganizationId && <p className="mt-1 text-xs text-red-600">{fieldErrors.lawFirmOrganizationId}</p>}
          </div>
        </fieldset>

        <fieldset>
          <legend className="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-3">
            Patient Information
          </legend>
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                First name <span className="text-red-500">*</span>
              </label>
              <input
                type="text" value={form.clientFirstName}
                placeholder="Enter first name"
                onChange={e => update("clientFirstName", e.target.value)}
                disabled={submitting}
                className={inputCls(!!fieldErrors.clientFirstName)}
              />
              {fieldErrors.clientFirstName && <p className="mt-1 text-xs text-red-600">{fieldErrors.clientFirstName}</p>}
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Last name <span className="text-red-500">*</span>
              </label>
              <input
                type="text" value={form.clientLastName}
                placeholder="Enter last name"
                onChange={e => update("clientLastName", e.target.value)}
                disabled={submitting}
                className={inputCls(!!fieldErrors.clientLastName)}
              />
              {fieldErrors.clientLastName && <p className="mt-1 text-xs text-red-600">{fieldErrors.clientLastName}</p>}
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Phone <span className="text-red-500">*</span>
              </label>
              <input
                type="tel" value={form.clientPhone}
                placeholder="(555) 555-5555"
                onChange={e => update("clientPhone", formatPhoneInput(e.target.value))}
                disabled={submitting}
                className={inputCls(!!fieldErrors.clientPhone)}
              />
              {fieldErrors.clientPhone && <p className="mt-1 text-xs text-red-600">{fieldErrors.clientPhone}</p>}
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Email</label>
              <input
                type="email" value={form.clientEmail}
                placeholder="Enter email address"
                onChange={e => update("clientEmail", e.target.value)}
                disabled={submitting}
                className={inputCls(!!fieldErrors.clientEmail)}
              />
              {fieldErrors.clientEmail && <p className="mt-1 text-xs text-red-600">{fieldErrors.clientEmail}</p>}
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Date of birth <span className="text-red-500">*</span></label>
              <input
                type="date" value={form.clientDob}
                min="1900-01-01" max={TODAY}
                required
                onChange={e => update("clientDob", e.target.value)}
                disabled={submitting}
                className={inputCls(!!fieldErrors.clientDob)}
              />
              {fieldErrors.clientDob && <p className="mt-1 text-xs text-red-600">{fieldErrors.clientDob}</p>}
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Date of accident <span className="text-red-500">*</span></label>
              <input
                type="date" value={form.dateOfAccident}
                min="1900-01-01" max={TODAY}
                required
                onChange={e => update("dateOfAccident", e.target.value)}
                disabled={submitting}
                className={inputCls(!!fieldErrors.dateOfAccident)}
              />
              {fieldErrors.dateOfAccident && <p className="mt-1 text-xs text-red-600">{fieldErrors.dateOfAccident}</p>}
            </div>
          </div>
        </fieldset>

        <fieldset>
          <legend className="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-3">
            Referral Details
          </legend>
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Urgency <span className="text-red-500">*</span>
              </label>
              <Select
                value={form.urgency}
                onValueChange={v => update("urgency", v as ReferralUrgencyValue)}
                disabled={submitting}
              >
                <SelectTrigger className={selectTriggerCls()}>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {URGENCY_OPTIONS.map(o => <SelectItem key={o.value} value={o.value}>{o.label}</SelectItem>)}
                </SelectContent>
              </Select>
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Type of service <span className="text-red-500">*</span>
              </label>
              <Select
                value={form.requestedService}
                onValueChange={v => update("requestedService", v)}
                disabled={submitting}
              >
                <SelectTrigger className={selectTriggerCls()}>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {SERVICE_TYPES.map(s => <SelectItem key={s} value={s}>{s}</SelectItem>)}
                </SelectContent>
              </Select>
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Type of treatment</label>
              <Select
                value={form.treatmentTypeId || NONE_TREATMENT_TYPE}
                onValueChange={v => update("treatmentTypeId", v === NONE_TREATMENT_TYPE ? "" : v)}
                disabled={submitting}
              >
                <SelectTrigger className={selectTriggerCls()}>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={NONE_TREATMENT_TYPE}>None</SelectItem>
                  {treatmentTypes.map(t => <SelectItem key={t.id} value={t.id}>{t.name}</SelectItem>)}
                </SelectContent>
              </Select>
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Lien company name</label>
              <input
                type="text" value={form.lienCompanyName}
                placeholder="Optional"
                onChange={e => update("lienCompanyName", e.target.value)}
                disabled={submitting}
                className={inputCls()}
              />
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Lien company email</label>
              <input
                type="email" value={form.lienCompanyEmail}
                placeholder="Optional"
                onChange={e => update("lienCompanyEmail", e.target.value)}
                disabled={submitting}
                className={inputCls(!!fieldErrors.lienCompanyEmail)}
              />
              {fieldErrors.lienCompanyEmail && <p className="mt-1 text-xs text-red-600">{fieldErrors.lienCompanyEmail}</p>}
            </div>
            <div className="sm:col-span-2 lg:col-span-3">
              <label className="block text-sm font-medium text-gray-700 mb-1">Notes</label>
              <textarea
                rows={3} value={form.notes}
                placeholder="Background, prior treatment…"
                onChange={e => update("notes", e.target.value)}
                disabled={submitting}
                className={`${inputCls()} resize-none`}
              />
            </div>
          </div>
        </fieldset>

        <fieldset>
          <legend className="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-3">
            Medical Provider Preference
          </legend>
          <div className="space-y-3">
            <div className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800">
              Provider selection is recorded as a recommendation for the law firm. The provider is not contacted from this portal.
            </div>

            <div className="flex items-center justify-between gap-3 rounded-lg border border-gray-200 bg-white px-3 py-3">
              <div className="min-w-0">
                <p className="text-sm font-medium text-gray-900">
                  {preferredProviders.length > 0
                    ? `${preferredProviders.length} preferred provider${preferredProviders.length === 1 ? "" : "s"} selected`
                    : "No preferred providers selected"}
                </p>
                <p className="mt-0.5 text-xs text-gray-500">
                  Search the master provider list and use the large map to select one or more recommendations.
                </p>
              </div>
              <button
                type="button"
                onClick={() => setProviderPickerOpen(true)}
                disabled={submitting || providersLoading}
                className="shrink-0 cursor-pointer rounded-md bg-primary px-3 py-2 text-sm font-medium text-white hover:bg-primary/90 disabled:cursor-not-allowed disabled:opacity-50"
              >
                {providersLoading ? "Loading..." : "Open provider map"}
              </button>
            </div>

            {preferredProviders.length > 0 && (
              <div className="rounded-lg border border-primary/20 bg-primary/5 px-3 py-2">
                <p className="text-xs font-semibold uppercase tracking-wide text-primary">
                  Preferred providers ({preferredProviders.length})
                </p>
                <div className="mt-2 grid grid-cols-1 gap-2 sm:grid-cols-2 lg:grid-cols-3">
                  {preferredProviders.map((provider, index) => {
                    const key = providerPreferenceKey(provider);
                    return (
                      <div key={key} className="flex items-center justify-between gap-3 rounded-md bg-white px-3 py-2">
                        <div className="min-w-0">
                          <p className="truncate text-sm font-medium text-gray-900">
                            {index + 1}. {provider.displayLabel}
                          </p>
                          <p className="truncate text-xs text-gray-500">{provider.markerSubtitle}</p>
                        </div>
                        <button
                          type="button"
                          onClick={() => removePreferredProvider(key)}
                          disabled={submitting}
                          aria-label={`Remove ${provider.displayLabel} from preferred providers`}
                          title="Remove from preferred providers"
                          className="flex h-8 w-8 shrink-0 cursor-pointer items-center justify-center rounded-full text-gray-400 hover:bg-gray-100 hover:text-gray-700 disabled:cursor-not-allowed disabled:opacity-50"
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

        <fieldset>
          <legend className="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-3">
            Attachments
          </legend>
          <div className="space-y-3">
            <label className={`flex cursor-pointer items-center gap-3 rounded-lg border border-dashed px-3 py-3 transition-colors ${
              submitting
                ? "pointer-events-none opacity-50"
                : "border-gray-300 hover:border-primary hover:bg-primary/5"
            }`}>
              <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg bg-gray-50 text-gray-400">
                <i className="ri-upload-2-line text-lg" />
              </span>
              <span className="min-w-0">
                <span className="block text-sm font-medium text-gray-900">Attach documents</span>
                <span className="block text-xs text-gray-500">PDF, Word, images, text, CSV, or Excel files.</span>
              </span>
              <input
                type="file"
                className="hidden"
                accept={ATTACHMENT_ACCEPT}
                multiple
                disabled={submitting}
                onChange={event => {
                  const files = Array.from(event.target.files ?? []);
                  if (files.length > 0) {
                    setAttachments(prev => [...prev, ...files]);
                  }
                  event.target.value = "";
                }}
              />
            </label>

            {attachments.length > 0 && (
              <div className="rounded-lg border border-gray-200 bg-gray-50">
                <div className="border-b border-gray-200 px-3 py-2 text-xs font-semibold uppercase tracking-wide text-gray-500">
                  Selected documents ({attachments.length})
                </div>
                <div className="divide-y divide-gray-200">
                  {attachments.map((file, index) => (
                    <div key={`${file.name}-${file.size}-${index}`} className="flex items-center gap-3 px-3 py-2">
                      <i className="ri-file-line shrink-0 text-gray-400" />
                      <div className="min-w-0 flex-1">
                        <p className="truncate text-sm font-medium text-gray-900">{file.name}</p>
                        <p className="text-xs text-gray-500">{formatFileSize(file.size)}</p>
                      </div>
                      <button
                        type="button"
                        onClick={() => previewAttachment(file)}
                        disabled={submitting}
                        aria-label={`View ${file.name}`}
                        title="View attachment"
                        className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-gray-400 hover:bg-white hover:text-gray-700 disabled:opacity-50"
                      >
                        <i className="ri-eye-line text-base" />
                      </button>
                      <button
                        type="button"
                        onClick={() => setAttachments(prev => prev.filter((_, i) => i !== index))}
                        disabled={submitting}
                        aria-label={`Remove ${file.name}`}
                        title="Remove attachment"
                        className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-gray-400 hover:bg-white hover:text-gray-700 disabled:opacity-50"
                      >
                        <i className="ri-close-line text-base" />
                      </button>
                    </div>
                  ))}
                </div>
              </div>
            )}
          </div>
        </fieldset>

        <div className="flex items-center justify-end pt-2 border-t border-gray-100">
          <button
            type="submit"
            disabled={submitting || !canSubmit}
            className="bg-primary text-white text-sm font-medium px-5 py-2.5 rounded-md hover:opacity-90 cursor-pointer disabled:opacity-60 disabled:cursor-not-allowed transition-opacity flex items-center justify-center gap-2"
          >
            {submitting ? (
              <><span className="w-4 h-4 border-2 border-white/30 border-t-white rounded-full animate-spin" />Submitting…</>
            ) : (
              "Submit request"
            )}
          </button>
        </div>
      </form>

      {providerPickerOpen && (
        <div
          className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4 backdrop-blur-sm"
          role="dialog"
          aria-modal="true"
          aria-labelledby="provider-picker-title"
          onClick={event => {
            if (event.target === event.currentTarget) setProviderPickerOpen(false);
          }}
        >
          <div className="flex h-[86vh] w-full max-w-[92vw] flex-col overflow-hidden rounded-xl bg-white shadow-2xl">
            <div className="flex items-center justify-between border-b border-gray-100 px-5 py-4">
              <div>
                <h2 id="provider-picker-title" className="text-base font-semibold text-gray-900">
                  Select Preferred Medical Providers
                </h2>
                <p className="mt-0.5 text-xs text-gray-500">
                  Provider selections are recommendations only. The law firm makes the final routing decision.
                </p>
              </div>
              <div className="flex items-center gap-3">
                {preferredProviders.length > 0 && (
                  <button
                    type="button"
                    onClick={() => {
                      setPreferredProviders([]);
                      setSelectedMarkerId(null);
                    }}
                    className="cursor-pointer text-xs text-gray-500 underline hover:text-gray-700"
                  >
                    Clear selected
                  </button>
                )}
                <button
                  type="button"
                  onClick={() => setProviderPickerOpen(false)}
                  aria-label="Close provider picker"
                  className="cursor-pointer rounded-lg p-1 text-gray-400 transition-colors hover:bg-gray-100 hover:text-gray-600"
                >
                  <i className="ri-close-line text-xl" />
                </button>
              </div>
            </div>

            <div className="space-y-2 border-b border-gray-100 px-5 py-3">
              <div className="flex items-center gap-2 overflow-x-auto pb-1">
                <div className="flex shrink-0 items-center overflow-hidden rounded-lg border border-gray-200 bg-gray-50 p-1">
                  {(["split", "list", "map"] as ProviderPickerViewMode[]).map(mode => (
                    <button
                      key={mode}
                      type="button"
                      onClick={() => setProviderViewMode(mode)}
                      className={`cursor-pointer rounded-md px-3 py-1.5 text-xs font-medium capitalize transition-colors ${
                        providerViewMode === mode
                          ? "bg-white text-gray-900 shadow-sm"
                          : "text-gray-500 hover:text-gray-700"
                      }`}
                    >
                      {mode}
                    </button>
                  ))}
                </div>

                <div className="relative min-w-[240px] flex-1">
                  <i className={`pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-sm text-gray-400 ${
                    providerSearchLocationPending || providerSearchLocationLoading
                      ? "ri-loader-4-line animate-spin"
                      : "ri-search-line"
                  }`} />
                  <input
                    type="search"
                    value={providerSearch}
                    onChange={e => setProviderSearch(e.target.value)}
                    placeholder="Search by provider, specialty, address, city, state, or ZIP..."
                    className="h-10 w-full rounded-lg border border-gray-300 bg-white pl-8 pr-3 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-400 focus:outline-none focus:ring-1 focus:ring-inset focus:ring-blue-100"
                  />
                </div>

                <input
                  type="text"
                  value={providerZip}
                  onChange={e => setProviderZip(e.target.value)}
                  onKeyDown={e => {
                    if (e.key === "Enter") void applyProviderZipFilter();
                  }}
                  aria-label="ZIP code"
                  placeholder="ZIP"
                  className="h-10 w-24 shrink-0 rounded-lg border border-gray-300 bg-white px-3 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-400 focus:outline-none focus:ring-1 focus:ring-inset focus:ring-blue-100"
                />

                <button
                  type="button"
                  onClick={() => void applyProviderZipFilter()}
                  disabled={providerZipLoading}
                  className="h-10 shrink-0 cursor-pointer rounded-lg border border-gray-300 bg-white px-4 text-sm font-medium text-gray-600 transition-colors hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-50"
                >
                  {providerZipLoading ? "Finding..." : "Apply ZIP"}
                </button>

                <Select
                  value={providerSpecialtyCode || ALL_SPECIALTIES}
                  onValueChange={value => setProviderSpecialtyCode(value === ALL_SPECIALTIES ? "" : value)}
                >
                  <SelectTrigger
                    aria-label="Specialty"
                    className="h-10 w-40 shrink-0 rounded-lg border-gray-300 bg-white text-sm text-gray-900 focus:border-blue-400 focus:ring-1 focus:ring-inset focus:ring-blue-100"
                  >
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value={ALL_SPECIALTIES}>All specialties</SelectItem>
                    {providerSpecialtyOptions.map(specialty => (
                      <SelectItem key={specialty.code} value={specialty.code}>{specialty.name}</SelectItem>
                    ))}
                  </SelectContent>
                </Select>

                <button
                  type="button"
                  onClick={() => setProviderAcceptingOnly(value => !value)}
                  className={`flex h-10 shrink-0 cursor-pointer items-center gap-1.5 rounded-lg border px-4 text-sm font-medium transition-colors ${
                    providerAcceptingOnly
                      ? "border-gray-300 bg-white text-gray-600 hover:bg-gray-50"
                      : "border-gray-900 bg-gray-900 text-white"
                  }`}
                >
                  <i className="ri-filter-3-line text-sm" />
                  {providerAcceptingOnly ? "Accepting only" : "All providers"}
                </button>

                <button
                  type="button"
                  onClick={clearProviderFilters}
                  className="shrink-0 cursor-pointer px-3 py-1.5 text-xs font-medium text-gray-500 hover:text-gray-700"
                >
                  Clear
                </button>

                <span className="shrink-0 text-xs font-medium text-gray-400">
                  {filteredProviderMarkers.length} of {providerMarkers.length}
                </span>

                {preferredProviders.length > 0 && (
                  <span className="flex shrink-0 items-center gap-1.5 rounded-lg bg-blue-600 px-3 py-1.5 text-xs font-semibold text-white">
                    <i className="ri-check-line text-xs" />
                    {preferredProviders.length} selected
                  </span>
                )}
              </div>
              {providerZipError && (
                <p className="text-xs text-red-600">{providerZipError}</p>
              )}
            </div>

            <div className={`grid min-h-0 flex-1 ${
              providerViewMode === "split"
                ? "grid-cols-[340px_minmax(0,1fr)]"
                : "grid-cols-1"
            }`}>
              {providerViewMode !== "map" && (
              <div className={`min-h-0 overflow-y-auto bg-white ${providerViewMode === "split" ? "border-r border-gray-200" : ""}`}>
                {providersLoading ? (
                  <div className="flex h-full items-center justify-center text-sm text-gray-500">
                    Loading providers...
                  </div>
                ) : filteredProviderMarkers.length === 0 ? (
                  <div className="flex h-full items-center justify-center px-6 text-center text-sm text-gray-500">
                    No providers match your search.
                  </div>
                ) : (
                  <div className={providerViewMode === "list" ? "grid grid-cols-2 gap-3 p-3" : "divide-y divide-gray-100"}>
                    {filteredProviderMarkers.map((provider, index) => {
                      const key = providerMarkerKey(provider);
                      const selected = preferredProviderKeys.has(key);
                      const markerIndex = index + 1;
                      const identity = getProviderIdentity(provider);
                      return (
                        <div
                          key={key}
                          ref={element => { providerRowRefs.current[key] = element; }}
                          role="button"
                          tabIndex={0}
                          onClick={() => focusProvider(provider)}
                          onKeyDown={event => {
                            if (event.key === "Enter" || event.key === " ") {
                              event.preventDefault();
                              focusProvider(provider);
                            }
                          }}
                          className={`w-full cursor-pointer text-left transition-colors ${
                            selectedMarkerId === key ? "bg-blue-50" : "hover:bg-gray-50"
                          } ${providerViewMode === "list" ? "rounded-lg border border-gray-200 bg-white px-3 py-3" : "px-3 py-3"}`}
                        >
                          <div className="flex items-start gap-3">
                            <span className={`mt-0.5 flex h-7 w-7 shrink-0 items-center justify-center rounded-full text-xs font-bold text-white ${
                              selectedMarkerId === key ? "bg-blue-700" : provider.acceptingReferrals ? "bg-red-600" : "bg-gray-500"
                            }`}>
                              {markerIndex}
                            </span>
                            <div className="min-w-0 flex-1">
                              <p className={`truncate text-sm ${selected ? "font-semibold text-primary" : "font-medium text-gray-900"}`}>
                                {identity.primary}
                              </p>
                              {identity.secondary && (
                                <p className="mt-0.5 truncate text-xs text-gray-500">{identity.secondary}</p>
                              )}
                              <p className="mt-0.5 truncate text-xs text-gray-500">{providerLocationLine(provider)}</p>
                              {activeProviderSearchLocation && (
                                <p className="mt-1 text-xs font-medium text-blue-600">
                                  {distanceMilesBetween(activeProviderSearchLocation, provider).toFixed(1)} mi away
                                </p>
                              )}
                              {provider.phone && (
                                <p className="mt-1 text-xs text-gray-400">{formatPhoneDisplay(provider.phone)}</p>
                              )}
                              {provider.specialties?.length > 0 && (
                                <div className="mt-2 flex flex-wrap gap-1">
                                  {provider.specialties.slice(0, 3).map(s => (
                                    <span key={s.id} className="rounded bg-blue-50 px-1.5 py-0.5 text-[11px] text-blue-700">
                                      {s.name}
                                    </span>
                                  ))}
                                </div>
                              )}
                            </div>
                            <button
                              type="button"
                              onClick={event => {
                                event.stopPropagation();
                                togglePreferredProviderMarker(provider);
                              }}
                              className={`flex h-8 w-8 shrink-0 cursor-pointer items-center justify-center rounded-lg text-xs font-semibold transition-colors ${
                                selected
                                  ? "bg-blue-600 text-white hover:bg-blue-700"
                                  : "bg-gray-100 text-gray-600 hover:bg-blue-50 hover:text-blue-700"
                              }`}
                              title={selected ? "Remove from preference list" : "Select provider"}
                              aria-label={selected ? "Remove from preference list" : "Select provider"}
                            >
                              <i className={selected ? "ri-check-line text-base" : "ri-add-line text-base"} />
                            </button>
                          </div>
                        </div>
                      );
                    })}
                  </div>
                )}
              </div>
              )}

              {providerViewMode !== "list" && (
              <div className="min-h-0 bg-gray-100">
                {numberedMarkers.length > 0 ? (
                  <PublicNetworkMap
                    markers={numberedMarkers}
                    selectedId={selectedMarkerId}
                    zoomToId={zoomToProviderId}
                    onZoomed={() => setZoomToProviderId(null)}
                    searchLocation={activeProviderSearchLocation}
                    onSelect={focusProviderFromMarker}
                    onRequestReferral={selectProviderFromMarker}
                    requestReferralLabel="Select"
                  />
                ) : (
                  <div className="flex h-full items-center justify-center text-sm text-gray-500">
                    No location data available.
                  </div>
                )}
              </div>
              )}
            </div>

            <div className="flex items-center justify-between border-t border-gray-100 px-5 py-3">
              <p className="text-xs text-gray-500">
                {preferredProviders.length > 0
                  ? `${preferredProviders.length} provider preference${preferredProviders.length === 1 ? "" : "s"} will be sent to the law firm.`
                  : "Select one or more provider preferences, or continue without a recommendation."}
              </p>
              <button
                type="button"
                onClick={() => setProviderPickerOpen(false)}
                className="cursor-pointer rounded-md bg-primary px-4 py-2 text-sm font-medium text-white hover:bg-primary/90"
              >
                Done
              </button>
            </div>
          </div>
        </div>
      )}

      <Modal
        open={lawFirmModalOpen}
        onClose={closeLawFirmModal}
        title="Select Law Firm"
        subtitle="Who will review this referral request"
        size="md"
        footer={
          <>
            <button
              type="button"
              onClick={() => { clearLawFirmSelection(); setPendingLawFirmId(""); closeLawFirmModal(); }}
              disabled={!pendingLawFirmId}
              className="text-sm px-4 py-2 border border-gray-200 rounded-lg hover:bg-gray-50 text-gray-600 cursor-pointer disabled:opacity-50 disabled:cursor-not-allowed"
            >
              Clear
            </button>
            <button
              type="button"
              onClick={confirmLawFirmSelection}
              disabled={!pendingLawFirmId}
              className="text-sm px-4 py-2 rounded-lg bg-primary hover:bg-primary/90 text-white cursor-pointer disabled:opacity-50 disabled:cursor-not-allowed"
            >
              Select
            </button>
          </>
        }
      >
        <input
          type="text"
          autoFocus
          value={lawFirmSearch}
          onChange={e => setLawFirmSearch(e.target.value)}
          placeholder="Search law firms…"
          className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm mb-3 focus:outline-none focus:ring-2 focus:ring-primary"
        />

        {filteredLawFirms.length === 0 ? (
          <p className="py-8 text-center text-sm text-gray-500">
            {lawFirms.length === 0 ? "No law firms are available yet." : "No law firms match your search."}
          </p>
        ) : (
          <div className="space-y-2">
            {filteredLawFirms.map(firm => {
              const selected = firm.id === pendingLawFirmId;
              return (
                <button
                  key={firm.id}
                  type="button"
                  onClick={() => setPendingLawFirmId(firm.id)}
                  className={`w-full cursor-pointer rounded-lg border px-3 py-2.5 text-left transition-colors ${
                    selected
                      ? "border-primary bg-primary/5"
                      : "border-gray-200 hover:border-gray-300 hover:bg-gray-50"
                  }`}
                >
                  <span className={`block text-sm truncate ${selected ? "font-semibold text-primary" : "font-medium text-gray-900"}`}>
                    {firm.name}
                  </span>
                </button>
              );
            })}
          </div>
        )}
      </Modal>

      <ConfirmDialog
        open={confirmOpen}
        onClose={() => setConfirmOpen(false)}
        onConfirm={performSubmit}
        title="Submit Referral Request?"
        description={
          <>
            You&rsquo;re about to submit a referral request for{" "}
            <strong className="text-gray-900">{form.clientFirstName.trim()} {form.clientLastName.trim()}</strong>
            {" "}to <strong className="text-gray-900">{selectedLawFirm?.name}</strong>. They will review it before
            provider routing.
            {preferredProviders.length > 0
              ? ` This includes ${preferredProviders.length} preferred provider recommendation${preferredProviders.length === 1 ? "" : "s"}.`
              : ""}
          </>
        }
        confirmLabel="Submit request"
        loading={submitting}
      />
    </div>
  );
}
