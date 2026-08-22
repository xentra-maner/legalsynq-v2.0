"use client";

import { useState, useEffect, useRef } from "react";
import dynamic from "next/dynamic";

import { careConnectApi } from "@/lib/careconnect-api";
import { AccessStageBadge } from "@/components/careconnect/status-badge";
import { ConfirmDialog } from "@/components/lien/modal";
import {
  formatPhoneDisplay,
  formatPhoneInput,
  isValidPhone,
  stripPhone,
} from "@/lib/phone";
import { isValidUsZipCode } from "@/lib/address";
import { networkDisplayName } from "@/lib/network-display-name";
import type {
  NetworkDetail,
  NetworkProviderItem,
  NetworkProviderMarker,
  ProviderSearchResult,
  SpecialtyOption,
} from "@/types/careconnect";
import { Modal } from "../ui/modal";
import { ProviderEditModal } from "@/components/careconnect/provider-edit-modal";

const MyNetworkMap = dynamic(
  () =>
    import("@/components/careconnect/my-network-map").then(
      (m) => m.MyNetworkMap,
    ),
  {
    ssr: false,
    loading: () => (
      <div className="h-[480px] rounded-xl bg-gray-100 animate-pulse" />
    ),
  },
);

interface AddressSuggestion {
  displayName: string;
  addressLine1: string;
  city: string;
  state: string;
  postalCode: string;
  latitude: number;
  longitude: number;
}

interface MyNetworkClientProps {
  initialNetwork: NetworkDetail | null;
  fetchError: string | null;
  specialtyOptions: SpecialtyOption[];
  /** LSV3-1084: tenant display name used for the "{tenantName} Preferred Providers" heading. */
  tenantName: string;
  /** True for NetworkManager / TenantAdmin / PlatformAdmin — can edit/remove any provider. */
  canManageAll: boolean;
  /** 4-AC3: True only for TenantAdmin / PlatformAdmin — can toggle a provider's Public/Private visibility. */
  canManageVisibility: boolean;
  /** Single-tenant-network cutover: true for anyone who may add a provider to the shared network (NetworkManager / ReferrerAdmin / admin) — no longer gated by network ownership. */
  canAddProviders: boolean;
  /** The caller's own organization id — a CareConnectReferrerAdmin without canManageAll may only edit/remove providers their org added. */
  callerOrgId?: string | null;
  /** True on the tenant portal's "My Network" screen (NetworkManager audience) — shows the public network URL. False on the law firm "Network Setup" screen, which doesn't need it. */
  showNetworkUrl?: boolean;
}

type PanelMode =
  | "closed"
  | "search"
  | "confirm"
  | "create"
  | "location"
  | "edit";
type ViewMode = "list" | "cards" | "map";

const GEOCODED_POINT_SOURCE = "Geocoded";
const CITY_CENTROID_POINT_SOURCE = "CityCentroid";
const KNOWN_PROVIDER_TITLES: Record<string, string> = {
  dr: "Dr.",
  "dr.": "Dr.",
  mr: "Mr.",
  "mr.": "Mr.",
  mrs: "Mrs.",
  "mrs.": "Mrs.",
  ms: "Ms.",
  "ms.": "Ms.",
  prof: "Prof.",
  "prof.": "Prof.",
};

const DEFAULT_SERVICE_RADIUS_MILES = "25";
const MAX_SERVICE_RADIUS_MILES = 60;

const EMPTY_NEW_FORM = {
  title: "",
  firstName: "",
  lastName: "",
  organizationName: "",
  email: "",
  phone: "",
  addressLine1: "",
  city: "",
  state: "",
  postalCode: "",
  npi: "",
  isActive: true,
  acceptingReferrals: true,
  specialtyIds: [] as string[],
  isMobile: false,
  serviceRadiusMiles: DEFAULT_SERVICE_RADIUS_MILES,
};

function networkProviderEntryId(
  provider: Pick<NetworkProviderItem, "id" | "networkProviderId">,
): string {
  return provider.networkProviderId || provider.id;
}

function providerLocationKey(
  providerId: string,
  facilityId?: string | null,
): string {
  return `${providerId}:${facilityId ?? ""}`;
}

function searchResultKey(provider: ProviderSearchResult): string {
  return providerLocationKey(provider.id, provider.facilityId);
}

function formatLocationLine(location: {
  addressLine1?: string | null;
  city?: string | null;
  state?: string | null;
  postalCode?: string | null;
  isMobile?: boolean;
  serviceRadiusMiles?: number | null;
}): string {
  const cityState = [location.city, location.state].filter(Boolean).join(", ");
  if (location.isMobile) {
    const parts = [location.addressLine1, cityState]
      .filter(Boolean)
      .join(" · ");
    return location.serviceRadiusMiles != null
      ? `Mobile · ${parts} · ${location.serviceRadiusMiles}mi radius`
      : `Mobile · ${parts}`;
  }
  return [location.addressLine1, cityState, location.postalCode]
    .filter(Boolean)
    .join(" ");
}

function shouldShowFacilityName(
  provider: Pick<
    NetworkProviderItem,
    "name" | "organizationName" | "facilityName"
  >,
): boolean {
  const facilityName = provider.facilityName?.trim() ?? "";
  if (!facilityName) return false;
  const providerName = provider.name.trim().toLowerCase();
  const orgName = provider.organizationName?.trim().toLowerCase() ?? "";
  const normalizedFacility = facilityName.toLowerCase();
  return normalizedFacility !== providerName && normalizedFacility !== orgName;
}

export function MyNetworkClient({
  initialNetwork,
  fetchError,
  specialtyOptions,
  tenantName,
  canManageAll,
  canManageVisibility,
  canAddProviders,
  callerOrgId,
  showNetworkUrl = true,
}: MyNetworkClientProps) {
  const [network, setNetwork] = useState<NetworkDetail | null>(initialNetwork);
  const [providers, setProviders] = useState<NetworkProviderItem[]>(
    initialNetwork?.providers ?? [],
  );
  const [creating, setCreating] = useState(false);

  // View mode
  const [viewMode, setViewMode] = useState<ViewMode>("list");
  const [markers, setMarkers] = useState<NetworkProviderMarker[]>([]);
  const [markersLoaded, setMarkersLoaded] = useState(false);
  const [markersLoading, setMarkersLoading] = useState(false);
  const [mapSelectedId, setMapSelectedId] = useState<string | null>(null);

  // Add-provider panel state
  const [panelMode, setPanelMode] = useState<PanelMode>("closed");
  const [searchQuery, setSearchQuery] = useState({
    name: "",
    phone: "",
    npi: "",
    city: "",
  });
  const [searching, setSearching] = useState(false);
  const [searchError, setSearchError] = useState<string | null>(null);
  const [searchResults, setSearchResults] = useState<
    ProviderSearchResult[] | null
  >(null);
  const [confirmTarget, setConfirmTarget] =
    useState<ProviderSearchResult | null>(null);
  const [locationTarget, setLocationTarget] =
    useState<ProviderSearchResult | null>(null);
  const [locationReturnsToEdit, setLocationReturnsToEdit] = useState(false);
  const [addingId, setAddingId] = useState<string | null>(null);
  const [newForm, setNewForm] = useState(EMPTY_NEW_FORM);
  const [createError, setCreateError] = useState<string | null>(null);
  const [editingProvider, setEditingProvider] =
    useState<NetworkProviderItem | null>(null);
  const [removingId, setRemovingId] = useState<string | null>(null);
  const [removeTarget, setRemoveTarget] = useState<{
    providerId: string;
    providerName: string;
  } | null>(null);
  const [toast, setToast] = useState<string | null>(null);
  const [networkUrl, setNetworkUrl] = useState<string>("");
  const [urlCopied, setUrlCopied] = useState(false);

  // Address autocomplete state
  const [addrSuggestions, setAddrSuggestions] = useState<AddressSuggestion[]>(
    [],
  );
  const [addrLoading, setAddrLoading] = useState(false);
  const [addrOpen, setAddrOpen] = useState(false);
  const [geoLat, setGeoLat] = useState<number | null>(null);
  const [geoLng, setGeoLng] = useState<number | null>(null);
  const [selectedPostalCode, setSelectedPostalCode] = useState<string | null>(
    null,
  );
  const addrDebounce = useRef<ReturnType<typeof setTimeout> | null>(null);

  const hasInvalidPhone =
    newForm.phone.trim().length > 0 && !isValidPhone(newForm.phone);
  const hasInvalidPostalCode =
    !newForm.isMobile &&
    newForm.postalCode.trim().length > 0 &&
    !isValidUsZipCode(newForm.postalCode);
  const hasZipMismatch =
    !newForm.isMobile &&
    !!selectedPostalCode &&
    newForm.postalCode.trim().slice(0, 5) !== selectedPostalCode.slice(0, 5);
  const hasInvalidServiceRadius =
    newForm.isMobile &&
    (!newForm.serviceRadiusMiles.trim() ||
      !(Number(newForm.serviceRadiusMiles) > 0) ||
      Number(newForm.serviceRadiusMiles) > MAX_SERVICE_RADIUS_MILES);
  const hasNoSpecialty = newForm.specialtyIds.length === 0;
  useEffect(() => {
    if (showNetworkUrl) setNetworkUrl(window.location.origin + "/careconnect/network");
  }, [showNetworkUrl]);

  // ── Network bootstrap ────────────────────────────────────────────────────
  // Single-tenant-network cutover: there's no longer a user-facing "create a
  // network" action — GET /api/networks bootstraps the tenant's one shared
  // network automatically on first access. This retry path only exists for the
  // rare case where the initial server-side fetch failed transiently.

  async function handleCreateNetwork() {
    setCreating(true);
    try {
      const { data } = await careConnectApi.networks.list();
      const id = data[0]?.id;
      if (!id) throw new Error("No network returned.");
      const detailRes = await fetch(
        `/api/careconnect/api/networks/${id}`,
        { credentials: "include" },
      );
      if (detailRes.ok) {
        const detail: NetworkDetail = await detailRes.json();
        setNetwork(detail);
        setProviders(detail.providers ?? []);
      } else {
        setNetwork({ ...data[0], providers: [] } as NetworkDetail);
        setProviders([]);
      }
    } catch {
      showToast("Failed to load your network. Please try again.");
    } finally {
      setCreating(false);
    }
  }

  // ── Toast helper ─────────────────────────────────────────────────────────

  function showToast(msg: string) {
    setToast(msg);
    setTimeout(() => setToast(null), 4000);
  }

  async function copyNetworkUrl() {
    if (!networkUrl) return;
    try {
      await navigator.clipboard.writeText(networkUrl);
      setUrlCopied(true);
      setTimeout(() => setUrlCopied(false), 2000);
    } catch {
      showToast("Could not copy URL. Please copy it manually.");
    }
  }

  // ── View mode + marker loading ────────────────────────────────────────────

  async function switchView(mode: ViewMode) {
    setViewMode(mode);
    if (mode === "map" && network && !markersLoaded) {
      setMarkersLoading(true);
      try {
        const { data } = await careConnectApi.networks.getMarkers(network.id);
        const raw = data ?? [];

        // Geocode any markers that are missing coordinates but have an address
        const enriched = await Promise.all(
          raw.map(async (m) => {
            if (m.latitude && m.longitude) return m;

            // Build best available query from address fields
            const parts = [
              m.addressLine1,
              m.city,
              m.state,
              m.postalCode,
            ].filter(Boolean);
            if (parts.length === 0) return m;

            try {
              const q = encodeURIComponent(parts.join(", "));
              const res = await fetch(`/api/geocode/address?q=${q}&loose=1`, {
                credentials: "include",
              });
              if (!res.ok) return m;
              const suggestions: { latitude: number; longitude: number }[] =
                await res.json();
              if (suggestions.length > 0) {
                return {
                  ...m,
                  latitude: suggestions[0].latitude,
                  longitude: suggestions[0].longitude,
                };
              }
            } catch {
              /* silently skip */
            }
            return m;
          }),
        );

        setMarkers(enriched);
        setMarkersLoaded(true);
      } catch {
        showToast("Could not load map data. Please try again.");
      } finally {
        setMarkersLoading(false);
      }
    }
  }

  // ── Address autocomplete ──────────────────────────────────────────────────

  function handleAddressChange(value: string) {
    setNewForm((f) => ({ ...f, addressLine1: value }));
    setGeoLat(null);
    setGeoLng(null);
    setSelectedPostalCode(null);
    if (addrDebounce.current) clearTimeout(addrDebounce.current);
    if (value.trim().length < 3) {
      setAddrSuggestions([]);
      setAddrOpen(false);
      return;
    }
    addrDebounce.current = setTimeout(async () => {
      setAddrLoading(true);
      try {
        const res = await fetch(
          `/api/geocode/address?q=${encodeURIComponent(value)}`,
          { credentials: "include" },
        );
        if (res.ok) {
          const suggestions: AddressSuggestion[] = await res.json();
          setAddrSuggestions(suggestions);
          setAddrOpen(suggestions.length > 0);
        }
      } catch {
        /* silently ignore */
      } finally {
        setAddrLoading(false);
      }
    }, 300);
  }

  function selectAddress(s: AddressSuggestion) {
    setNewForm((f) => ({
      ...f,
      addressLine1: s.addressLine1,
      city: s.city,
      state: s.state,
      postalCode: s.postalCode,
    }));
    setGeoLat(s.latitude);
    setGeoLng(s.longitude);
    setSelectedPostalCode(s.postalCode);
    setAddrSuggestions([]);
    setAddrOpen(false);
  }

  // Mobile providers have no street address — anchor them to a city-level centroid
  // (loose=1 asks the geocoder to match on city/state alone, no house number required).
  async function geocodeServiceArea(city: string, state: string) {
    if (!city.trim() || !state.trim()) return;
    try {
      const q = encodeURIComponent(`${city.trim()}, ${state.trim()}`);
      const res = await fetch(`/api/geocode/address?q=${q}&loose=1`, {
        credentials: "include",
      });
      if (!res.ok) return;
      const suggestions: AddressSuggestion[] = await res.json();
      if (suggestions.length > 0) {
        setGeoLat(suggestions[0].latitude);
        setGeoLng(suggestions[0].longitude);
      }
    } catch {
      /* silently ignore */
    }
  }

  // City autocomplete for mobile providers — same debounced-suggestion UX as the street
  // address field, but loose=1 so a bare city name (no house number) resolves.
  function handleCityChange(value: string) {
    setNewForm((f) => ({ ...f, city: value }));
    setGeoLat(null);
    setGeoLng(null);
    if (addrDebounce.current) clearTimeout(addrDebounce.current);
    if (value.trim().length < 3) {
      setAddrSuggestions([]);
      setAddrOpen(false);
      return;
    }
    addrDebounce.current = setTimeout(async () => {
      setAddrLoading(true);
      try {
        const res = await fetch(
          `/api/geocode/address?q=${encodeURIComponent(value)}&loose=1`,
          { credentials: "include" },
        );
        if (res.ok) {
          const suggestions: AddressSuggestion[] = await res.json();
          setAddrSuggestions(suggestions);
          setAddrOpen(suggestions.length > 0);
        }
      } catch {
        /* silently ignore */
      } finally {
        setAddrLoading(false);
      }
    }, 300);
  }

  function selectCitySuggestion(s: AddressSuggestion) {
    setNewForm((f) => ({ ...f, city: s.city, state: s.state }));
    setGeoLat(s.latitude);
    setGeoLng(s.longitude);
    setAddrSuggestions([]);
    setAddrOpen(false);
  }

  function toggleMobile(checked: boolean) {
    setNewForm((f) => ({
      ...f,
      isMobile: checked,
      postalCode: checked ? "" : f.postalCode,
    }));
    setGeoLat(null);
    setGeoLng(null);
    setSelectedPostalCode(null);
    setAddrSuggestions([]);
    setAddrOpen(false);
    if (checked) void geocodeServiceArea(newForm.city, newForm.state);
  }

  function toggleSpecialty(id: string) {
    setNewForm((f) => ({
      ...f,
      specialtyIds: f.specialtyIds.includes(id)
        ? f.specialtyIds.filter((existing) => existing !== id)
        : [...f.specialtyIds, id],
    }));
    if (createError === "Select at least one specialty.") {
      setCreateError(null);
    }
  }

  function selectedSpecialtyCodes(): string[] {
    const selected = new Set(newForm.specialtyIds);
    return specialtyOptions
      .filter((s) => selected.has(s.id))
      .map((s) => s.code);
  }

  function splitProviderName(name: string): {
    title: string;
    firstName: string;
    lastName: string;
  } {
    const parts = name.trim().split(/\s+/).filter(Boolean);
    const title =
      parts.length > 0
        ? (KNOWN_PROVIDER_TITLES[parts[0].toLowerCase()] ?? "")
        : "";
    if (title) parts.shift();
    if (parts.length <= 1)
      return { title, firstName: parts[0] ?? "", lastName: "" };
    return {
      title,
      firstName: parts.slice(0, -1).join(" "),
      lastName: parts[parts.length - 1] ?? "",
    };
  }

  function providerDisplayName(form: typeof EMPTY_NEW_FORM): string {
    return [form.title, form.firstName, form.lastName]
      .map((v) => v.trim())
      .filter(Boolean)
      .join(" ");
  }

  function providerIdentityId(provider: NetworkProviderItem): string {
    return provider.providerId || provider.id;
  }

  // ── Add panel open/close ──────────────────────────────────────────────────

  function openAddPanel() {
    setSearchQuery({ name: "", phone: "", npi: "", city: "" });
    setSearchResults(null);
    setSearchError(null);
    setConfirmTarget(null);
    setLocationTarget(null);
    setLocationReturnsToEdit(false);
    setNewForm(EMPTY_NEW_FORM);
    setCreateError(null);
    setEditingProvider(null);
    setGeoLat(null);
    setGeoLng(null);
    setSelectedPostalCode(null);
    setPanelMode("search");
  }

  function closeAddPanel() {
    setPanelMode("closed");
    setSearchResults(null);
    setConfirmTarget(null);
    setLocationTarget(null);
    setLocationReturnsToEdit(false);
    setCreateError(null);
    setSearchError(null);
    setEditingProvider(null);
    setGeoLat(null);
    setGeoLng(null);
    setSelectedPostalCode(null);
  }

  function openEditPanel(provider: NetworkProviderItem) {
    setSearchResults(null);
    setSearchError(null);
    setConfirmTarget(null);
    setLocationTarget(null);
    setLocationReturnsToEdit(false);
    setEditingProvider(provider);
    setCreateError(null);
    setPanelMode("edit");
  }

  function openLocationPanel(provider: ProviderSearchResult) {
    const { title, firstName, lastName } = splitProviderName(provider.name);
    setConfirmTarget(null);
    setLocationTarget(provider);
    setLocationReturnsToEdit(false);
    setEditingProvider(null);
    setCreateError(null);
    setSearchError(null);
    setAddrSuggestions([]);
    setAddrOpen(false);
    setGeoLat(null);
    setGeoLng(null);
    setSelectedPostalCode(null);
    setNewForm({
      title: provider.title?.trim() || title,
      firstName,
      lastName,
      organizationName: provider.organizationName ?? provider.name,
      email: provider.email ?? "",
      phone: formatPhoneInput(provider.phone ?? ""),
      addressLine1: "",
      city: "",
      state: "",
      postalCode: "",
      npi: provider.npi ?? "",
      isActive: true,
      acceptingReferrals: provider.acceptingReferrals,
      specialtyIds: provider.specialties?.map((s) => s.id) ?? [],
      isMobile: false,
      serviceRadiusMiles: DEFAULT_SERVICE_RADIUS_MILES,
    });
    setPanelMode("location");
  }

  // ── Search ────────────────────────────────────────────────────────────────

  async function handleSearch(e: React.FormEvent) {
    e.preventDefault();
    const hasQuery = Object.values(searchQuery).some((v) => v.trim() !== "");
    if (!hasQuery) {
      setSearchError("Enter at least one search field.");
      return;
    }
    setSearching(true);
    setSearchError(null);
    setSearchResults(null);
    try {
      const { data } = await careConnectApi.networks.searchProviders(
        network!.id,
        {
          name: searchQuery.name || undefined,
          phone: stripPhone(searchQuery.phone) || undefined,
          npi: searchQuery.npi || undefined,
          city: searchQuery.city || undefined,
        },
      );
      setSearchResults(data ?? []);
    } catch {
      setSearchError("Search failed. Please try again.");
    } finally {
      setSearching(false);
    }
  }

  // ── Confirm add from search ───────────────────────────────────────────────

  function requestConfirm(provider: ProviderSearchResult) {
    setConfirmTarget(provider);
    setPanelMode("confirm");
  }

  async function handleConfirmAdd() {
    if (!confirmTarget || !network) return;
    const key = searchResultKey(confirmTarget);
    setAddingId(key);
    try {
      const { data } = await careConnectApi.networks.addProvider(network.id, {
        existingProviderId: confirmTarget.id,
        existingFacilityId: confirmTarget.facilityId ?? undefined,
      });
      if (
        data &&
        !providers.find(
          (p) => networkProviderEntryId(p) === networkProviderEntryId(data),
        )
      ) {
        setProviders((prev) => [...prev, data]);
      }
      setMarkersLoaded(false);
      showToast(`${confirmTarget.name} added to your network.`);
      closeAddPanel();
    } catch {
      setSearchError("Failed to add provider. Please try again.");
      setPanelMode("search");
    } finally {
      setAddingId(null);
      setConfirmTarget(null);
    }
  }

  // ── Create new provider ──────────────────────────────────────────────────

  async function handleCreate(e: React.FormEvent) {
    e.preventDefault();
    if (!network) return;
    if (hasNoSpecialty) {
      setCreateError("Select at least one specialty.");
      return;
    }
    if (
      hasInvalidPhone ||
      hasInvalidPostalCode ||
      hasZipMismatch ||
      hasInvalidServiceRadius
    )
      return;
    const specialtyCodes = selectedSpecialtyCodes();
    if (specialtyCodes.length === 0) {
      setCreateError("Select at least one specialty.");
      return;
    }
    setCreating(true);
    setCreateError(null);
    try {
      const { data } = await careConnectApi.networks.addProvider(network.id, {
        newProvider: {
          title: newForm.title.trim() || undefined,
          firstName: newForm.firstName.trim(),
          lastName: newForm.lastName.trim(),
          organizationName: newForm.organizationName.trim() || undefined,
          email: newForm.email.trim(),
          phone: stripPhone(newForm.phone),
          addressLine1: newForm.addressLine1.trim(),
          city: newForm.city.trim(),
          state: newForm.state.trim().toUpperCase(),
          postalCode: newForm.postalCode.trim() || null,
          isActive: newForm.isActive,
          acceptingReferrals: newForm.acceptingReferrals,
          npi: newForm.npi.trim() || undefined,
          specialtyCodes,
          primarySpecialtyCode: specialtyCodes[0],
          isMobile: newForm.isMobile,
          serviceRadiusMiles: newForm.isMobile
            ? Number(newForm.serviceRadiusMiles)
            : null,
          ...(geoLat !== null && geoLng !== null
            ? {
                latitude: geoLat,
                longitude: geoLng,
                geoPointSource: newForm.isMobile
                  ? CITY_CENTROID_POINT_SOURCE
                  : GEOCODED_POINT_SOURCE,
              }
            : {}),
        },
      });
      if (
        data &&
        !providers.find(
          (p) => networkProviderEntryId(p) === networkProviderEntryId(data),
        )
      ) {
        setProviders((prev) => [...prev, data]);
      }
      setMarkersLoaded(false);
      showToast(
        `${providerDisplayName(newForm)} added to the registry and your network.`,
      );
      closeAddPanel();
    } catch (err: unknown) {
      setCreateError(
        err instanceof Error
          ? err.message
          : "Failed to add provider. Please try again.",
      );
    } finally {
      setCreating(false);
    }
  }

  async function handleAddLocation(e: React.FormEvent) {
    e.preventDefault();
    if (!network || !locationTarget) return;
    if (
      hasInvalidPhone ||
      hasInvalidPostalCode ||
      hasZipMismatch ||
      hasInvalidServiceRadius
    )
      return;

    const fallback = splitProviderName(locationTarget.name);
    setCreating(true);
    setCreateError(null);
    try {
      const { data } = await careConnectApi.networks.addProvider(network.id, {
        existingProviderId: locationTarget.id,
        newProvider: {
          title:
            newForm.title.trim() ||
            locationTarget.title ||
            fallback.title ||
            undefined,
          firstName:
            newForm.firstName.trim() ||
            fallback.firstName ||
            locationTarget.name,
          lastName: newForm.lastName.trim() || fallback.lastName || "",
          organizationName:
            newForm.organizationName.trim() ||
            locationTarget.organizationName ||
            locationTarget.name,
          email: newForm.email.trim(),
          phone: stripPhone(newForm.phone),
          addressLine1: newForm.addressLine1.trim(),
          city: newForm.city.trim(),
          state: newForm.state.trim().toUpperCase(),
          postalCode: newForm.postalCode.trim() || null,
          isActive: newForm.isActive,
          acceptingReferrals: newForm.acceptingReferrals,
          isMobile: newForm.isMobile,
          serviceRadiusMiles: newForm.isMobile
            ? Number(newForm.serviceRadiusMiles)
            : null,
          ...(geoLat !== null && geoLng !== null
            ? {
                latitude: geoLat,
                longitude: geoLng,
                geoPointSource: newForm.isMobile
                  ? CITY_CENTROID_POINT_SOURCE
                  : GEOCODED_POINT_SOURCE,
              }
            : {}),
        },
      });
      let nextProviders = providers;
      if (
        data &&
        !providers.find(
          (p) => networkProviderEntryId(p) === networkProviderEntryId(data),
        )
      ) {
        nextProviders = [...providers, data];
        setProviders(nextProviders);
      }
      setMarkersLoaded(false);
      showToast(`${locationTarget.name} location added to your network.`);
      closeAddPanel();
    } catch (err: unknown) {
      setCreateError(
        err instanceof Error
          ? err.message
          : "Failed to add provider location. Please try again.",
      );
    } finally {
      setCreating(false);
    }
  }

  // ── Remove (Delete Icon in the provider list/cards — distinct from Delete location above) ──

  function requestRemove(providerId: string, providerName: string) {
    setRemoveTarget({ providerId, providerName });
  }

  async function confirmRemove() {
    if (!network || !removeTarget) return;
    const { providerId, providerName } = removeTarget;
    setRemovingId(providerId);
    try {
      await careConnectApi.networks.removeProvider(network.id, providerId);
      setProviders((prev) =>
        prev.map((p) =>
          networkProviderEntryId(p) === providerId
            ? { ...p, isActive: false, acceptingReferrals: false }
            : p,
        ),
      );
      setMarkersLoaded(false);
      showToast(`${providerName} set to inactive.`);
      setRemoveTarget(null);
    } catch {
      showToast(
        "Failed to set provider location to inactive. Please try again.",
      );
    } finally {
      setRemovingId(null);
    }
  }

  const alreadyInNetwork = new Set(
    providers.map((p) =>
      providerLocationKey(p.providerId ?? p.id, p.facilityId),
    ),
  );
  const providerIdsInNetwork = new Set(
    providers.map((p) => p.providerId ?? p.id),
  );
  // Deleted locations (facilityIsActive: false) stay in `providers` so search-dedup and
  // Facilities-panel logic can still reference them — the visible list itself hides only
  // truly deleted locations, not ones merely toggled inactive via the separate Active
  // checkbox (`isActive`, the NetworkProvider membership's own status).
  const visibleProviders = providers.filter((p) => p.facilityIsActive);

  // ── Render: no network yet ───────────────────────────────────────────────

  if (fetchError) {
    return (
      <div className="rounded-md bg-red-50 border border-red-200 p-4 text-sm text-red-700">
        {fetchError}
      </div>
    );
  }

  if (!network) {
    return (
      <div className="rounded-xl border-2 border-dashed border-gray-200 py-16 text-center">
        <i className="ri-share-circle-line text-4xl text-gray-300" />
        <p className="mt-3 text-base font-medium text-gray-700">
          Unable to load your provider network
        </p>
        <p className="mt-1 text-sm text-gray-400">
          Something went wrong loading your network. Try again.
        </p>
        <button
          onClick={handleCreateNetwork}
          disabled={creating}
          className="mt-5 inline-flex items-center gap-1.5 rounded-lg bg-blue-600 px-5 py-2.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
        >
          {creating ? (
            <>
              <span className="h-3.5 w-3.5 rounded-full border-2 border-white/60 border-t-transparent animate-spin" />
              Loading…
            </>
          ) : (
            <>
              <i className="ri-refresh-line" />
              Retry
            </>
          )}
        </button>
      </div>
    );
  }

  // ── Render: network loaded ───────────────────────────────────────────────

  return (
    <div className="space-y-5">
      {/* Toast */}
      {toast && (
        <div className="fixed bottom-5 right-5 z-50 flex items-center gap-2 rounded-lg bg-gray-900 px-4 py-3 text-sm text-white shadow-lg">
          <i className="ri-checkbox-circle-line text-green-400" />
          {toast}
        </div>
      )}

      {/* Remove from network confirmation (distinct from Delete location, owned by ProviderEditModal — no Facility cascade) */}
      <ConfirmDialog
        open={!!removeTarget}
        onClose={() => setRemoveTarget(null)}
        onConfirm={confirmRemove}
        title="Remove from network?"
        description={`Delete ${removeTarget?.providerName || "this provider"} from your network? This will mark the location inactive and stop accepting referrals.`}
        confirmLabel="Remove"
        confirmVariant="danger"
        loading={!!removingId}
      />

      {/* Header */}
      <div className="flex items-start justify-between gap-4">
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-3">
            <h1 className="text-xl font-semibold text-gray-900">
              {networkDisplayName(network, tenantName)}
            </h1>
            <span className="inline-flex items-center rounded-full bg-blue-50 px-2.5 py-0.5 text-xs font-medium text-blue-700 border border-blue-200">
              {visibleProviders.length}{" "}
              {visibleProviders.length === 1 ? "provider" : "providers"}
            </span>
          </div>

          {/* Network URL */}
          {showNetworkUrl && networkUrl && (
            <div className="mt-2 flex items-center gap-2">
              <span className="text-xs font-medium text-gray-400 shrink-0">
                Network URL
              </span>
              <div className="flex items-center gap-1 rounded-md border border-gray-200 bg-gray-50 px-2.5 py-1 min-w-0">
                <i className="ri-link text-gray-400 text-xs shrink-0" />
                <span className="text-xs text-gray-600 font-mono truncate">
                  {networkUrl}
                </span>
              </div>
              <button
                onClick={copyNetworkUrl}
                title="Copy network URL"
                className="shrink-0 inline-flex items-center gap-1 rounded-md border border-gray-200 bg-white px-2 py-1 text-xs font-medium text-gray-600 hover:bg-gray-50 transition-colors"
              >
                <i
                  className={
                    urlCopied
                      ? "ri-check-line text-green-600"
                      : "ri-clipboard-line"
                  }
                />
                {urlCopied ? "Copied!" : "Copy"}
              </button>
              <a
                href={networkUrl}
                target="_blank"
                rel="noopener noreferrer"
                title="Open network URL in new tab"
                className="shrink-0 inline-flex items-center gap-1 rounded-md border border-gray-200 bg-white px-2 py-1 text-xs font-medium text-gray-600 hover:bg-gray-50 transition-colors"
              >
                <i className="ri-external-link-line" />
                Open
              </a>
            </div>
          )}
        </div>

        {panelMode === "closed" && canAddProviders && (
          <button
            onClick={openAddPanel}
            className="inline-flex items-center gap-1.5 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 transition-colors shrink-0"
          >
            <i className="ri-user-add-line" />
            Add Provider
          </button>
        )}
      </div>

      {/* ── Add Provider Modal ──────────────────────────────────────────── */}
      <Modal
        size="2xl"
        open={panelMode !== "closed" && panelMode !== "edit"}
        onClose={closeAddPanel}
        title={panelMode === "location" ? "Add Provider Location" : "Add Provider"}
        titleSizeClassName="text-xl"
        cardClassName="rounded-[20px] shadow-xl border border-neutral-200"
        headerClassName="flex items-center justify-between px-8 pt-8 pb-6"
        bodyClassName="flex-1 overflow-y-auto px-8 pb-8"
        titleExtra={
          panelMode === "confirm" ? (
            <span className="inline-flex items-center h-7 px-3 py-1 rounded-2xl text-sm font-medium text-blue-700 bg-blue-500/5 border border-blue-500">
              Confirm Match
            </span>
          ) : panelMode === "create" ? (
            <span className="inline-flex items-center h-7 px-3 py-1 rounded-2xl text-sm font-medium text-violet-700 bg-violet-500/5 border border-violet-500">
              New Provider
            </span>
          ) : panelMode === "location" ? (
            <span className="inline-flex items-center h-7 px-3 py-1 rounded-2xl text-sm font-medium text-cyan-700 bg-cyan-500/5 border border-cyan-500">
              New Location
            </span>
          ) : undefined
        }
        headerActions={
          panelMode === "search" ? (
            <button
              onClick={() => setPanelMode("create")}
              className="text-xs text-blue-600 hover:underline cursor-pointer"
            >
              Not found? Add new instead
            </button>
          ) : panelMode === "create" ? (
            <button
              onClick={() => setPanelMode("search")}
              className="text-xs text-gray-500 hover:underline cursor-pointer"
            >
              ← Back to search
            </button>
          ) : panelMode === "location" ? (
            <button
              onClick={() => {
                if (locationReturnsToEdit) {
                  setLocationReturnsToEdit(false);
                  setLocationTarget(null);
                  setCreateError(null);
                  setPanelMode("edit");
                } else {
                  setPanelMode("search");
                  setLocationTarget(null);
                  setCreateError(null);
                }
              }}
              className="text-xs text-gray-500 hover:underline cursor-pointer"
            >
              {locationReturnsToEdit ? "← Back to provider" : "← Back to search"}
            </button>
          ) : panelMode === "confirm" ? (
            <button
              onClick={() => setPanelMode("search")}
              className="text-xs text-gray-500 hover:underline cursor-pointer"
            >
              ← Back
            </button>
          ) : undefined
        }
      >
        <div className="space-y-6">
          {/* ── Search mode ── */}
          {panelMode === "search" && (
            <div className="space-y-3">
              <p className="text-xs text-blue-800 bg-blue-100 border border-blue-200 rounded-lg px-3 py-2">
                <i className="ri-search-line mr-1" />
                Search the shared provider registry first. If the provider is
                found, add an existing location or use Add new location. If not,
                add them as a new provider.
              </p>
              <form onSubmit={handleSearch} className="space-y-2">
                <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
                  <div>
                    <label className="block text-xs font-medium text-gray-600 mb-1">
                      Name or organization
                    </label>
                    <input
                      type="text"
                      value={searchQuery.name}
                      onChange={(e) =>
                        setSearchQuery((q) => ({ ...q, name: e.target.value }))
                      }
                      placeholder="Dr. Smith…"
                      className="w-full rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm focus:border-blue-500 focus:outline-none"
                    />
                  </div>
                  <div>
                    <label className="block text-xs font-medium text-gray-600 mb-1">
                      Phone
                    </label>
                    <input
                      type="text"
                      value={searchQuery.phone}
                      onChange={(e) =>
                        setSearchQuery((q) => ({
                          ...q,
                          phone: formatPhoneInput(e.target.value),
                        }))
                      }
                      placeholder="(555) 000-0000"
                      className="w-full rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm focus:border-blue-500 focus:outline-none"
                    />
                  </div>
                  <div>
                    <label className="block text-xs font-medium text-gray-600 mb-1">
                      NPI number
                    </label>
                    <input
                      type="text"
                      value={searchQuery.npi}
                      onChange={(e) =>
                        setSearchQuery((q) => ({ ...q, npi: e.target.value }))
                      }
                      placeholder="1234567890"
                      className="w-full rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-mono focus:border-blue-500 focus:outline-none"
                      maxLength={10}
                    />
                  </div>
                  <div>
                    <label className="block text-xs font-medium text-gray-600 mb-1">
                      City
                    </label>
                    <input
                      type="text"
                      value={searchQuery.city}
                      onChange={(e) =>
                        setSearchQuery((q) => ({ ...q, city: e.target.value }))
                      }
                      placeholder="Chicago"
                      className="w-full rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm focus:border-blue-500 focus:outline-none"
                    />
                  </div>
                </div>
                {searchError && (
                  <p className="text-xs text-red-600">{searchError}</p>
                )}
                <button
                  type="submit"
                  disabled={searching}
                  className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-4 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
                >
                  {searching ? (
                    <>
                      <span className="h-3 w-3 rounded-full border-2 border-white/50 border-t-transparent animate-spin" />
                      Searching…
                    </>
                  ) : (
                    <>
                      <i className="ri-search-line" />
                      Search Registry
                    </>
                  )}
                </button>
              </form>

              {/* Search results */}
              {searchResults !== null && (
                <div className="mt-3">
                  {searchResults.length === 0 ? (
                    <div className="rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800 flex items-center justify-between">
                      <span>
                        <i className="ri-search-line mr-1" />
                        No matches found in the shared registry.
                      </span>
                      <button
                        onClick={() => setPanelMode("create")}
                        className="ml-3 text-xs font-medium text-blue-600 hover:underline shrink-0"
                      >
                        Add new provider →
                      </button>
                    </div>
                  ) : (
                    <div className="rounded-lg border border-gray-200 bg-white overflow-hidden">
                      <div className="border-b border-gray-100 px-4 py-2.5 bg-gray-50 flex items-center justify-between">
                        <span className="text-xs font-medium text-gray-500">
                          {searchResults.length} match
                          {searchResults.length !== 1 ? "es" : ""} found
                        </span>
                        <button
                          onClick={() => setPanelMode("create")}
                          className="text-xs text-blue-600 hover:underline"
                        >
                          Provider not listed? Add new →
                        </button>
                      </div>
                      <div className="divide-y divide-gray-100 max-h-64 overflow-y-auto">
                        {searchResults.map((p) => {
                          const key = searchResultKey(p);
                          const inNetwork = p.facilityId
                            ? alreadyInNetwork.has(key)
                            : providerIdsInNetwork.has(p.id);
                          return (
                            <div
                              key={key}
                              className="flex items-center justify-between px-4 py-3 hover:bg-gray-50"
                            >
                              <div className="min-w-0 flex-1">
                                <div className="flex items-center gap-2">
                                  <p className="text-sm font-medium text-gray-900 truncate">
                                    {p.name}
                                  </p>
                                  <AccessStageBadge stage={p.accessStage} />
                                </div>
                                {p.organizationName && (
                                  <p className="text-xs text-gray-500 truncate">
                                    {p.organizationName}
                                  </p>
                                )}
                                {p.facilityName &&
                                  p.facilityName !== p.organizationName && (
                                    <p className="text-xs text-gray-500 truncate">
                                      {p.facilityName}
                                    </p>
                                  )}
                                <p className="text-xs text-gray-400 mt-0.5">
                                  {formatLocationLine(p)}
                                  {p.npi && (
                                    <span className="ml-2 font-mono">
                                      NPI: {p.npi}
                                    </span>
                                  )}
                                  <span className="ml-2">
                                    {formatPhoneDisplay(p.phone)}
                                  </span>
                                </p>
                                {p.specialties?.length > 0 && (
                                  <div className="mt-1 flex flex-wrap gap-1">
                                    {p.specialties.map((s) => (
                                      <span
                                        key={s.id}
                                        className="inline-flex items-center rounded-full bg-blue-50 px-2 py-0.5 text-[11px] font-medium text-blue-700 border border-blue-200"
                                      >
                                        {s.name}
                                      </span>
                                    ))}
                                  </div>
                                )}
                              </div>
                              <div className="ml-3 flex shrink-0 flex-col items-end gap-1.5">
                                {inNetwork ? (
                                  <span className="inline-flex items-center gap-1 text-xs font-medium text-green-600">
                                    <i className="ri-check-line" />
                                    Already in network
                                  </span>
                                ) : (
                                  <button
                                    onClick={() => requestConfirm(p)}
                                    className="inline-flex items-center gap-1 rounded-md border border-blue-300 bg-white px-3 py-1 text-xs font-medium text-blue-600 hover:bg-blue-50 transition-colors"
                                  >
                                    <i className="ri-user-add-line" />
                                    Add to Network
                                  </button>
                                )}
                                <button
                                  onClick={() => openLocationPanel(p)}
                                  className="inline-flex items-center gap-1 rounded-md border border-cyan-300 bg-white px-3 py-1 text-xs font-medium text-cyan-700 hover:bg-cyan-50 transition-colors"
                                >
                                  <i className="ri-map-pin-add-line" />
                                  Add new location
                                </button>
                              </div>
                            </div>
                          );
                        })}
                      </div>
                    </div>
                  )}
                </div>
              )}
            </div>
          )}

          {/* ── Confirm mode ── */}
          {panelMode === "confirm" && confirmTarget && (
            <div className="space-y-4">
              <p className="text-sm text-gray-600">
                Review this provider and confirm you want to add them to your
                network.
              </p>

              {/* Provider detail card */}
              <div className="rounded-lg border border-gray-200 bg-white p-4 space-y-2">
                <div className="flex items-start gap-3">
                  <div className="h-9 w-9 rounded-full bg-blue-100 flex items-center justify-center shrink-0">
                    <i className="ri-user-heart-line text-blue-600" />
                  </div>
                  <div className="min-w-0 flex-1">
                    <div className="flex items-center gap-2 flex-wrap">
                      <p className="font-semibold text-gray-900">
                        {confirmTarget.name}
                      </p>
                      <AccessStageBadge stage={confirmTarget.accessStage} />
                    </div>
                    {confirmTarget.organizationName && (
                      <p className="text-sm text-gray-500">
                        {confirmTarget.organizationName}
                      </p>
                    )}
                    {confirmTarget.facilityName &&
                      confirmTarget.facilityName !==
                        confirmTarget.organizationName && (
                        <p className="text-sm text-gray-500">
                          {confirmTarget.facilityName}
                        </p>
                      )}
                  </div>
                </div>

                <div className="grid grid-cols-2 gap-x-4 gap-y-1 text-xs text-gray-600 pl-12">
                  <div>
                    <span className="font-medium text-gray-500">Location:</span>{" "}
                    {formatLocationLine(confirmTarget)}
                  </div>
                  <div>
                    <span className="font-medium text-gray-500">Phone:</span>{" "}
                    {formatPhoneDisplay(confirmTarget.phone)}
                  </div>
                  <div>
                    <span className="font-medium text-gray-500">Email:</span>{" "}
                    {confirmTarget.email}
                  </div>
                  {confirmTarget.npi && (
                    <div>
                      <span className="font-medium text-gray-500">NPI:</span>{" "}
                      <span className="font-mono">{confirmTarget.npi}</span>
                    </div>
                  )}
                  {confirmTarget.specialties?.length > 0 && (
                    <div className="col-span-2 flex flex-wrap items-center gap-1">
                      <span className="font-medium text-gray-500 mr-1">
                        Specialties:
                      </span>
                      {confirmTarget.specialties.map((s) => (
                        <span
                          key={s.id}
                          className="inline-flex items-center rounded-full bg-blue-50 px-2 py-0.5 text-[11px] font-medium text-blue-700 border border-blue-200"
                        >
                          {s.name}
                        </span>
                      ))}
                    </div>
                  )}
                  <div>
                    <span className="font-medium text-gray-500">
                      Referrals:
                    </span>{" "}
                    <span
                      className={
                        confirmTarget.acceptingReferrals
                          ? "text-green-600 font-medium"
                          : "text-gray-400"
                      }
                    >
                      {confirmTarget.acceptingReferrals
                        ? "Accepting"
                        : "Not accepting"}
                    </span>
                  </div>
                </div>
              </div>

              <div className="flex items-center gap-2">
                <button
                  onClick={handleConfirmAdd}
                  disabled={!!addingId}
                  className="inline-flex items-center gap-1.5 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
                >
                  {addingId ? (
                    <>
                      <span className="h-3.5 w-3.5 rounded-full border-2 border-white/60 border-t-transparent animate-spin" />
                      Adding…
                    </>
                  ) : (
                    <>
                      <i className="ri-check-line" />
                      Confirm — Add to My Network
                    </>
                  )}
                </button>
                <button
                  onClick={() => {
                    setPanelMode("search");
                    setConfirmTarget(null);
                  }}
                  className="rounded-lg border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-600 hover:bg-gray-50"
                >
                  Cancel
                </button>
                <button
                  type="button"
                  onClick={() => openLocationPanel(confirmTarget)}
                  className="rounded-lg border border-cyan-300 bg-white px-4 py-2 text-sm font-medium text-cyan-700 hover:bg-cyan-50"
                >
                  Add new location instead
                </button>
              </div>
            </div>
          )}

          {/* ── Create / location mode ── */}
          {(panelMode === "create" || panelMode === "location") && (
            <div className="space-y-3">
              <p className="text-xs text-violet-800 bg-violet-50 border border-violet-200 rounded-lg px-3 py-2">
                <i className="ri-information-line mr-1" />
                {panelMode === "location"
                  ? "Add a new facility/location for this existing provider. Provider identity and specialties stay on the existing registry record."
                  : "This provider will be added to the shared platform registry and linked to your network. Duplicate NPI or email records must be handled by searching the registry and using Add new location."}
              </p>
              {panelMode === "location" && locationTarget && (
                <div className="rounded-lg border border-cyan-200 bg-white p-4 text-sm">
                  <div className="flex items-start gap-3">
                    <div className="h-9 w-9 rounded-full bg-cyan-50 flex items-center justify-center shrink-0">
                      <i className="ri-user-location-line text-cyan-700" />
                    </div>
                    <div className="min-w-0 flex-1">
                      <div className="flex flex-wrap items-center gap-2">
                        <p className="font-semibold text-gray-900">
                          {locationTarget.name}
                        </p>
                        <AccessStageBadge stage={locationTarget.accessStage} />
                      </div>
                      {locationTarget.organizationName && (
                        <p className="text-xs text-gray-500">
                          {locationTarget.organizationName}
                        </p>
                      )}
                      <div className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-xs text-gray-500">
                        {locationTarget.npi && (
                          <span>
                            NPI:{" "}
                            <span className="font-mono">
                              {locationTarget.npi}
                            </span>
                          </span>
                        )}
                        {locationTarget.specialties?.length > 0 && (
                          <span>
                            Specialties:{" "}
                            {locationTarget.specialties
                              .map((s) => s.name)
                              .join(", ")}
                          </span>
                        )}
                      </div>
                    </div>
                  </div>
                </div>
              )}
              <form
                onSubmit={
                  panelMode === "location" ? handleAddLocation : handleCreate
                }
                className="space-y-3"
              >
                {panelMode !== "location" && (
                  <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
                    <div>
                      <label className="block text-xs font-medium text-gray-600 mb-1">
                        Title
                      </label>
                      <input
                        value={newForm.title}
                        onChange={(e) =>
                          setNewForm((f) => ({ ...f, title: e.target.value }))
                        }
                        placeholder="Dr."
                        maxLength={50}
                        className="w-full rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm focus:border-blue-500 focus:outline-none"
                      />
                    </div>
                    <div>
                      <label className="block text-xs font-medium text-gray-600 mb-1">
                        First name <span className="text-red-500">*</span>
                      </label>
                      <input
                        required
                        value={newForm.firstName}
                        onChange={(e) =>
                          setNewForm((f) => ({
                            ...f,
                            firstName: e.target.value,
                          }))
                        }
                        placeholder="Jane"
                        className="w-full rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm focus:border-blue-500 focus:outline-none"
                      />
                    </div>
                    <div>
                      <label className="block text-xs font-medium text-gray-600 mb-1">
                        Last name <span className="text-red-500">*</span>
                      </label>
                      <input
                        required
                        value={newForm.lastName}
                        onChange={(e) =>
                          setNewForm((f) => ({
                            ...f,
                            lastName: e.target.value,
                          }))
                        }
                        placeholder="Smith"
                        className="w-full rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm focus:border-blue-500 focus:outline-none"
                      />
                    </div>
                  </div>
                )}

                <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
                  <div
                    className={
                      panelMode === "location" ? "sm:col-span-2" : undefined
                    }
                  >
                    <label className="block text-xs font-medium text-gray-600 mb-1">
                      Organization / Practice
                    </label>
                    {panelMode === "location" ? (
                      <p className="w-full rounded-md border border-gray-200 bg-gray-50 px-3 py-1.5 text-sm text-gray-700">
                        {newForm.organizationName || "—"}
                      </p>
                    ) : (
                      <input
                        value={newForm.organizationName}
                        onChange={(e) =>
                          setNewForm((f) => ({
                            ...f,
                            organizationName: e.target.value,
                          }))
                        }
                        placeholder="Smith Family Practice"
                        className="w-full rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm focus:border-blue-500 focus:outline-none"
                      />
                    )}
                  </div>
                  {panelMode === "create" && (
                    <div>
                      <label className="block text-xs font-medium text-gray-600 mb-1">
                        NPI Number
                      </label>
                      <input
                        value={newForm.npi}
                        onChange={(e) =>
                          setNewForm((f) => ({ ...f, npi: e.target.value }))
                        }
                        placeholder="1234567890"
                        maxLength={10}
                        className="w-full rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-mono focus:border-blue-500 focus:outline-none"
                      />
                    </div>
                  )}
                  <div className="sm:col-span-2 flex items-center gap-5">
                    <label className="flex items-center gap-2 cursor-pointer select-none">
                      <input
                        type="checkbox"
                        checked={newForm.isActive}
                        onChange={(e) =>
                          setNewForm((f) => ({
                            ...f,
                            isActive: e.target.checked,
                          }))
                        }
                        className="rounded border-gray-300 text-blue-600"
                      />
                      <span className="text-xs text-gray-700">Active</span>
                    </label>
                    <label className="flex items-center gap-2 cursor-pointer select-none">
                      <input
                        type="checkbox"
                        checked={newForm.acceptingReferrals}
                        onChange={(e) =>
                          setNewForm((f) => ({
                            ...f,
                            acceptingReferrals: e.target.checked,
                          }))
                        }
                        className="rounded border-gray-300 text-blue-600"
                      />
                      <span className="text-xs text-gray-700">
                        Accepting referrals
                      </span>
                    </label>
                  </div>
                  {panelMode !== "location" && (
                    <div className="sm:col-span-2">
                      <label className="block text-xs font-medium text-gray-600 mb-1">
                        Specialty <span className="text-red-500">*</span>
                      </label>
                      <div
                        className={`rounded-md border bg-white p-2 ${
                          hasNoSpecialty &&
                          createError === "Select at least one specialty."
                            ? "border-red-300"
                            : "border-gray-300"
                        }`}
                      >
                        {specialtyOptions.length === 0 ? (
                          <p className="text-xs text-gray-400">
                            No active specialties are configured.
                          </p>
                        ) : (
                          <div className="grid gap-1 sm:grid-cols-2">
                            {specialtyOptions.map((s) => (
                              <label
                                key={s.id}
                                className="flex items-center gap-2 rounded px-2 py-1 text-xs text-gray-700 hover:bg-gray-50"
                              >
                                <input
                                  type="checkbox"
                                  checked={newForm.specialtyIds.includes(s.id)}
                                  onChange={() => toggleSpecialty(s.id)}
                                  className="rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                                />
                                <span>{s.name}</span>
                              </label>
                            ))}
                          </div>
                        )}
                      </div>
                      {hasNoSpecialty &&
                        createError === "Select at least one specialty." && (
                          <p className="text-xs text-red-500 mt-1">
                            Select at least one specialty.
                          </p>
                        )}
                    </div>
                  )}
                  <div>
                    <label className="block text-xs font-medium text-gray-600 mb-1">
                      Email{panelMode === "location" ? "" : <span className="text-red-500"> *</span>}
                    </label>
                    {panelMode === "location" ? (
                      <p className="w-full rounded-md border border-gray-200 bg-gray-50 px-3 py-1.5 text-sm text-gray-700">
                        {newForm.email || "—"}
                      </p>
                    ) : (
                      <input
                        required
                        type="email"
                        value={newForm.email}
                        onChange={(e) =>
                          setNewForm((f) => ({ ...f, email: e.target.value }))
                        }
                        placeholder="jane@example.com"
                        className="w-full rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm focus:border-blue-500 focus:outline-none"
                      />
                    )}
                  </div>
                  <div>
                    <label className="block text-xs font-medium text-gray-600 mb-1">
                      Phone{panelMode === "location" ? "" : <span className="text-red-500"> *</span>}
                    </label>
                    {panelMode === "location" ? (
                      <p className="w-full rounded-md border border-gray-200 bg-gray-50 px-3 py-1.5 text-sm text-gray-700">
                        {newForm.phone || "—"}
                      </p>
                    ) : (
                      <>
                        <input
                          required
                          type="tel"
                          value={newForm.phone}
                          onChange={(e) =>
                            setNewForm((f) => ({
                              ...f,
                              phone: formatPhoneInput(e.target.value),
                            }))
                          }
                          placeholder="(555) 555-5555"
                          className={`w-full rounded-md border bg-white px-3 py-1.5 text-sm focus:outline-none ${
                            hasInvalidPhone
                              ? "border-red-300 focus:border-red-400"
                              : "border-gray-300 focus:border-blue-500"
                          }`}
                        />
                        {hasInvalidPhone && (
                          <p className="text-xs text-red-500 mt-1">
                            Phone number must be 10 digits.
                          </p>
                        )}
                      </>
                    )}
                  </div>
                  <div className="flex items-center gap-2">
                    <input
                      type="checkbox"
                      id="newProviderIsMobile"
                      checked={newForm.isMobile}
                      onChange={(e) => toggleMobile(e.target.checked)}
                      className="h-3.5 w-3.5 rounded border-gray-300 text-violet-600 focus:ring-violet-500"
                    />
                    <label
                      htmlFor="newProviderIsMobile"
                      className="text-xs font-medium text-gray-600"
                    >
                      Mobile / roaming provider (no fixed address)
                    </label>
                  </div>
                  <div className="relative">
                    <label className="block text-xs font-medium text-gray-600 mb-1">
                      {newForm.isMobile ? "Service area description" : "Address"}{" "}
                      <span className="text-red-500">*</span>
                    </label>
                    <div className="relative">
                      <input
                        required
                        autoComplete="off"
                        value={newForm.addressLine1}
                        onChange={(e) =>
                          newForm.isMobile
                            ? setNewForm((f) => ({
                                ...f,
                                addressLine1: e.target.value,
                              }))
                            : handleAddressChange(e.target.value)
                        }
                        onFocus={() =>
                          !newForm.isMobile &&
                          addrSuggestions.length > 0 &&
                          setAddrOpen(true)
                        }
                        onBlur={() => setTimeout(() => setAddrOpen(false), 150)}
                        placeholder={
                          newForm.isMobile
                            ? "e.g. Greater Las Vegas Metro"
                            : "123 Main St"
                        }
                        className="w-full rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm focus:border-blue-500 focus:outline-none pr-7"
                      />
                      {addrLoading && !newForm.isMobile && (
                        <span className="absolute right-2 top-1/2 -translate-y-1/2 h-3.5 w-3.5 rounded-full border-2 border-blue-400 border-t-transparent animate-spin" />
                      )}
                    </div>
                    {!newForm.isMobile &&
                      addrOpen &&
                      addrSuggestions.length > 0 && (
                        <ul className="absolute z-50 mt-1 w-full rounded-md border border-gray-200 bg-white shadow-lg text-sm overflow-hidden">
                          {addrSuggestions.map((s, i) => (
                            <li
                              key={i}
                              onMouseDown={() => selectAddress(s)}
                              className="flex items-start gap-2 px-3 py-2 cursor-pointer hover:bg-blue-50 transition-colors border-b border-gray-100 last:border-0"
                            >
                              <i className="ri-map-pin-line text-gray-400 mt-0.5 shrink-0" />
                              <span className="text-gray-700">
                                {s.displayName}
                              </span>
                            </li>
                          ))}
                        </ul>
                      )}
                  </div>
                  <div className="relative">
                    <label className="block text-xs font-medium text-gray-600 mb-1">
                      City <span className="text-red-500">*</span>
                    </label>
                    <div className="relative">
                      <input
                        required
                        autoComplete="off"
                        value={newForm.city}
                        onChange={(e) =>
                          newForm.isMobile
                            ? handleCityChange(e.target.value)
                            : setNewForm((f) => ({
                                ...f,
                                city: e.target.value,
                              }))
                        }
                        onFocus={() =>
                          newForm.isMobile &&
                          addrSuggestions.length > 0 &&
                          setAddrOpen(true)
                        }
                        onBlur={() => {
                          setTimeout(() => setAddrOpen(false), 150);
                          if (newForm.isMobile && geoLat === null)
                            void geocodeServiceArea(
                              newForm.city,
                              newForm.state,
                            );
                        }}
                        className="w-full rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm focus:border-blue-500 focus:outline-none pr-7"
                      />
                      {newForm.isMobile && addrLoading && (
                        <span className="absolute right-2 top-1/2 -translate-y-1/2 h-3.5 w-3.5 rounded-full border-2 border-blue-400 border-t-transparent animate-spin" />
                      )}
                    </div>
                    {newForm.isMobile &&
                      addrOpen &&
                      addrSuggestions.length > 0 && (
                        <ul className="absolute z-50 mt-1 w-full rounded-md border border-gray-200 bg-white shadow-lg text-sm overflow-hidden">
                          {addrSuggestions.map((s, i) => (
                            <li
                              key={i}
                              onMouseDown={() => selectCitySuggestion(s)}
                              className="flex items-start gap-2 px-3 py-2 cursor-pointer hover:bg-blue-50 transition-colors border-b border-gray-100 last:border-0"
                            >
                              <i className="ri-map-pin-line text-gray-400 mt-0.5 shrink-0" />
                              <span className="text-gray-700">
                                {s.displayName}
                              </span>
                            </li>
                          ))}
                        </ul>
                      )}
                  </div>
                  <div className="flex gap-2">
                    <div className="flex-1">
                      <label className="block text-xs font-medium text-gray-600 mb-1">
                        State <span className="text-red-500">*</span>
                      </label>
                      <input
                        required
                        value={newForm.state}
                        onChange={(e) =>
                          setNewForm((f) => ({ ...f, state: e.target.value }))
                        }
                        onBlur={() =>
                          newForm.isMobile &&
                          geoLat === null &&
                          void geocodeServiceArea(newForm.city, newForm.state)
                        }
                        placeholder="IL"
                        maxLength={2}
                        className="w-full rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm uppercase focus:border-blue-500 focus:outline-none"
                      />
                    </div>
                    <div className="flex-1">
                      {newForm.isMobile ? (
                        <>
                          <label className="block text-xs font-medium text-gray-600 mb-1">
                            Service radius (mi) <span className="text-red-500">*</span>
                          </label>
                          <input
                            required
                            type="number"
                            min={1}
                            max={MAX_SERVICE_RADIUS_MILES}
                            value={newForm.serviceRadiusMiles}
                            onChange={(e) =>
                              setNewForm((f) => ({
                                ...f,
                                serviceRadiusMiles: e.target.value,
                              }))
                            }
                            className={`w-full rounded-md border bg-white px-3 py-1.5 text-sm focus:outline-none ${
                              hasInvalidServiceRadius
                                ? "border-red-300 focus:border-red-400"
                                : "border-gray-300 focus:border-blue-500"
                            }`}
                          />
                          {hasInvalidServiceRadius && (
                            <p className="text-xs text-red-500 mt-1">
                              Enter a radius between 1 and{" "}
                              {MAX_SERVICE_RADIUS_MILES} miles.
                            </p>
                          )}
                        </>
                      ) : (
                        <>
                          <label className="block text-xs font-medium text-gray-600 mb-1">
                            ZIP <span className="text-red-500">*</span>
                          </label>
                          <input
                            required
                            value={newForm.postalCode}
                            onChange={(e) =>
                              setNewForm((f) => ({
                                ...f,
                                postalCode: e.target.value,
                              }))
                            }
                            placeholder="60601"
                            className={`w-full rounded-md border bg-white px-3 py-1.5 text-sm focus:outline-none ${
                              hasInvalidPostalCode || hasZipMismatch
                                ? "border-red-300 focus:border-red-400"
                                : "border-gray-300 focus:border-blue-500"
                            }`}
                          />
                          {(hasInvalidPostalCode || hasZipMismatch) && (
                            <p className="text-xs text-red-500 mt-1">
                              {hasZipMismatch
                                ? "ZIP code must match the selected address."
                                : "ZIP code must be 5 digits or 5+4 format."}
                            </p>
                          )}
                        </>
                      )}
                    </div>
                  </div>
                </div>

                {createError && (
                  <p className="text-xs text-red-600">{createError}</p>
                )}

                <div className="flex items-center gap-2">
                  <button
                    type="submit"
                    disabled={creating}
                    className="inline-flex items-center gap-1.5 rounded-lg bg-violet-600 px-4 py-2 text-sm font-medium text-white hover:bg-violet-700 disabled:opacity-50 transition-colors"
                  >
                    {creating ? (
                      <>
                        <span className="h-3.5 w-3.5 rounded-full border-2 border-white/60 border-t-transparent animate-spin" />
                        Adding…
                      </>
                    ) : (
                      <>
                        <i
                          className={
                            panelMode === "location"
                              ? "ri-map-pin-add-line"
                              : "ri-user-add-line"
                          }
                        />
                        {panelMode === "location"
                          ? "Add Location to My Network"
                          : "Add to Registry & My Network"}
                      </>
                    )}
                  </button>
                  <button
                    type="button"
                    onClick={() => {
                      setLocationTarget(null);
                      setCreateError(null);
                      if (locationReturnsToEdit) {
                        setLocationReturnsToEdit(false);
                        setPanelMode("edit");
                      } else {
                        setPanelMode("search");
                      }
                    }}
                    className="rounded-lg border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-600 hover:bg-gray-50"
                  >
                    Cancel
                  </button>
                </div>
              </form>
            </div>
          )}
        </div>
      </Modal>

      {network && (
        <ProviderEditModal
          network={network}
          provider={editingProvider}
          providers={providers}
          specialtyOptions={specialtyOptions}
          canManageVisibility={canManageVisibility}
          onClose={closeAddPanel}
          onProvidersChange={setProviders}
          onToast={showToast}
        />
      )}

      {/* ── View toggle bar ─────────────────────────────────────────────────── */}
      {visibleProviders.length > 0 && (
        <div className="flex items-center justify-between">
          <span className="text-xs text-gray-400">
            {visibleProviders.length} provider
            {visibleProviders.length !== 1 ? "s" : ""}
          </span>
          <div className="inline-flex rounded-lg border border-gray-200 bg-white overflow-hidden shadow-sm">
            {[
              {
                mode: "list" as ViewMode,
                icon: "ri-list-unordered",
                label: "List",
              },
              {
                mode: "cards" as ViewMode,
                icon: "ri-layout-grid-line",
                label: "Cards",
              },
              { mode: "map" as ViewMode, icon: "ri-map-2-line", label: "Map" },
            ].map(({ mode, icon, label }) => (
              <button
                key={mode}
                onClick={() => switchView(mode)}
                className={`flex items-center gap-1.5 px-3 py-1.5 text-xs font-medium transition-colors border-r border-gray-200 last:border-0 ${
                  viewMode === mode
                    ? "bg-blue-600 text-white"
                    : "text-gray-600 hover:bg-gray-50"
                }`}
              >
                <i className={icon} />
                {label}
              </button>
            ))}
          </div>
        </div>
      )}

      {/* ── Empty state ─────────────────────────────────────────────────────── */}
      {visibleProviders.length === 0 && (
        <div className="rounded-xl border-2 border-dashed border-gray-200 py-14 text-center">
          <i className="ri-hospital-line text-4xl text-gray-300" />
          <p className="mt-3 text-sm font-medium text-gray-500">
            No providers in your network yet.
          </p>
          <p className="text-xs text-gray-400 mt-1">
            Click "Add Provider" above to search the registry or add a new one.
          </p>
        </div>
      )}

      {/* ── List view ───────────────────────────────────────────────────────── */}
      {visibleProviders.length > 0 && viewMode === "list" && (
        <div className="rounded-xl border border-gray-200 bg-white overflow-hidden">
          <div className="overflow-x-auto">
            <table className="w-full min-w-[700px] text-sm">
              <thead className="bg-gray-50 border-b border-gray-200">
                <tr>
                  <th className="px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                    Provider
                  </th>
                  <th className="px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                    Specialties
                  </th>
                  <th className="px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                    Contact
                  </th>
                  <th className="px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                    Location
                  </th>
                  <th className="px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                    Status
                  </th>
                  <th className="px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                    Referrals
                  </th>
                  <th className="px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                    Access
                  </th>
                  <th className="px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                    Visibility
                  </th>
                  <th className="w-10" />
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100">
                {visibleProviders.map((p) => (
                  <ProviderRow
                    key={networkProviderEntryId(p)}
                    provider={p}
                    removing={removingId === networkProviderEntryId(p)}
                    canManage={
                      canManageAll ||
                      (!!callerOrgId && p.owningOrganizationId === callerOrgId)
                    }
                    onEdit={() => openEditPanel(p)}
                    onRemove={() =>
                      requestRemove(networkProviderEntryId(p), p.name)
                    }
                  />
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {/* ── Cards view ──────────────────────────────────────────────────────── */}
      {visibleProviders.length > 0 && viewMode === "cards" && (
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
          {visibleProviders.map((p) => (
            <ProviderCard
              key={networkProviderEntryId(p)}
              provider={p}
              removing={removingId === networkProviderEntryId(p)}
              canManage={
                canManageAll ||
                (!!callerOrgId && p.owningOrganizationId === callerOrgId)
              }
              onEdit={() => openEditPanel(p)}
              onRemove={() => requestRemove(networkProviderEntryId(p), p.name)}
            />
          ))}
        </div>
      )}

      {/* ── Map view ────────────────────────────────────────────────────────── */}
      {visibleProviders.length > 0 && viewMode === "map" && (
        <div className="space-y-2">
          {markersLoading ? (
            <div className="h-[480px] rounded-xl bg-gray-100 flex items-center justify-center">
              <div className="flex items-center gap-2 text-sm text-gray-500">
                <span className="h-4 w-4 rounded-full border-2 border-blue-500 border-t-transparent animate-spin" />
                Loading map…
              </div>
            </div>
          ) : (
            <>
              <div className="rounded-xl border border-gray-200 overflow-hidden">
                <MyNetworkMap
                  markers={markers}
                  selectedId={mapSelectedId}
                  onSelect={setMapSelectedId}
                />
              </div>
              {markers.filter((m) => m.latitude && m.longitude).length <
                visibleProviders.length && (
                <p className="text-xs text-gray-400 text-center">
                  <i className="ri-information-line mr-1" />
                  {visibleProviders.length -
                    markers.filter((m) => m.latitude && m.longitude)
                      .length}{" "}
                  provider(s) couldn't be located — add a full address when
                  editing to pin them on the map.
                </p>
              )}
            </>
          )}
        </div>
      )}
    </div>
  );
}

// ── Provider card (cards view) ────────────────────────────────────────────────

function ProviderCard({
  provider,
  removing,
  canManage,
  onEdit,
  onRemove,
}: {
  provider: NetworkProviderItem;
  removing: boolean;
  canManage: boolean;
  onEdit: () => void;
  onRemove: () => void;
}) {
  const showFacilityName = shouldShowFacilityName(provider);

  return (
    <div className="rounded-xl border border-gray-200 bg-white p-5 flex flex-col gap-4 hover:shadow-sm transition-shadow">
      {/* Card header */}
      <div className="flex items-start justify-between gap-2">
        <div className="flex items-start gap-3 min-w-0">
          <div className="h-10 w-10 rounded-full bg-blue-100 flex items-center justify-center shrink-0">
            <i className="ri-user-heart-line text-blue-600 text-lg" />
          </div>
          <div className="min-w-0">
            <p className="font-semibold text-gray-900 leading-tight truncate">
              {provider.name}
            </p>
            {provider.organizationName && (
              <p className="text-xs text-gray-500 mt-0.5 truncate">
                {provider.organizationName}
              </p>
            )}
            {showFacilityName && (
              <p className="text-xs text-gray-500 mt-0.5 truncate">
                {provider.facilityName}
              </p>
            )}
            {(provider.addressLine1 || provider.city || provider.state) && (
              <p className="text-xs text-gray-400 mt-0.5 truncate">
                {formatLocationLine(provider)}
              </p>
            )}
          </div>
        </div>
        {canManage && (
          <div className="flex items-center gap-1 shrink-0">
            <button
              onClick={onEdit}
              title="Edit provider"
              className="p-1.5 rounded-md text-gray-400 hover:text-blue-600 hover:bg-blue-50 transition-colors"
            >
              <i className="ri-pencil-line text-base" />
            </button>
            <button
              onClick={onRemove}
              disabled={removing}
              title="Remove from network"
              className="p-1.5 rounded-md text-gray-300 hover:text-red-500 hover:bg-red-50 disabled:opacity-40 transition-colors"
            >
              <i
                className={`text-base ${removing ? "ri-loader-4-line animate-spin" : "ri-close-circle-line"}`}
              />
            </button>
          </div>
        )}
      </div>

      {/* Contact + location */}
      <div className="space-y-1.5 text-xs text-gray-600">
        {(provider.email || provider.phone) && (
          <div className="flex items-start gap-2">
            <i className="ri-phone-line text-gray-400 mt-0.5 shrink-0" />
            <div className="space-y-0.5">
              {provider.phone && <p>{formatPhoneDisplay(provider.phone)}</p>}
              {provider.email && <p className="truncate">{provider.email}</p>}
            </div>
          </div>
        )}
        {(provider.city || provider.state) && (
          <div className="flex items-center gap-2">
            <i className="ri-map-pin-line text-gray-400 shrink-0" />
            <span>
              {provider.city}
              {provider.city && provider.state ? ", " : ""}
              {provider.state}
            </span>
          </div>
        )}
      </div>

      {/* Status */}
      <div className="flex flex-wrap gap-1.5 pt-1 border-t border-gray-100">
        <span
          className={`inline-flex items-center rounded-full px-2 py-0.5 text-[11px] font-medium border ${
            provider.isActive
              ? "bg-emerald-50 text-emerald-700 border-emerald-200"
              : "bg-gray-50 text-gray-500 border-gray-200"
          }`}
        >
          {provider.isActive ? "Active" : "Inactive"}
        </span>
        <span
          className={`inline-flex items-center rounded-full px-2 py-0.5 text-[11px] font-medium border ${
            provider.acceptingReferrals
              ? "bg-green-50 text-green-700 border-green-200"
              : "bg-amber-50 text-amber-700 border-amber-200"
          }`}
        >
          {provider.acceptingReferrals
            ? "Accepting referrals"
            : "Not accepting"}
        </span>
        <AccessStageBadge stage={provider.accessStage} />
        <span
          className={`inline-flex items-center rounded-full px-2 py-0.5 text-[11px] font-medium border ${
            provider.visibility === "Public"
              ? "bg-blue-50 text-blue-700 border-blue-200"
              : "bg-gray-50 text-gray-600 border-gray-200"
          }`}
        >
          {provider.visibility === "Public" ? "Public" : "Private"}
        </span>
      </div>

      {/* Specialties */}
      {provider.specialties?.length > 0 && (
        <div className="flex flex-wrap gap-1.5">
          {provider.specialties.map((s) => (
            <span
              key={s.id}
              className="inline-flex items-center rounded-full bg-blue-50 px-2 py-0.5 text-[11px] font-medium text-blue-700 border border-blue-200"
            >
              {s.name}
            </span>
          ))}
        </div>
      )}
    </div>
  );
}

// ── Provider row (list view) ───────────────────────────────────────────────────

function ProviderRow({
  provider,
  removing,
  canManage,
  onEdit,
  onRemove,
}: {
  provider: NetworkProviderItem;
  removing: boolean;
  canManage: boolean;
  onEdit: () => void;
  onRemove: () => void;
}) {
  const showFacilityName = shouldShowFacilityName(provider);

  return (
    <tr className="hover:bg-gray-50 transition-colors">
      <td className="px-4 py-3">
        <p className="font-medium text-gray-900 leading-tight">
          {provider.name}
        </p>
        {provider.organizationName && (
          <p className="text-xs text-gray-500 mt-0.5 leading-tight">
            {provider.organizationName}
          </p>
        )}
        {showFacilityName && (
          <p className="text-xs text-gray-500 mt-0.5 leading-tight">
            {provider.facilityName}
          </p>
        )}
        {(provider.addressLine1 || provider.postalCode) && (
          <p className="text-xs text-gray-400 mt-0.5 leading-tight">
            {provider.isMobile
              ? formatLocationLine(provider)
              : [provider.addressLine1, provider.postalCode]
                  .filter(Boolean)
                  .join(" ")}
          </p>
        )}
      </td>
      <td className="px-4 py-3">
        {provider.specialties?.length ? (
          <div className="flex flex-wrap gap-1">
            {provider.specialties.map((s) => (
              <span
                key={s.id}
                className="inline-flex items-center rounded-full bg-blue-50 px-2 py-0.5 text-[11px] font-medium text-blue-700 border border-blue-200"
              >
                {s.name}
              </span>
            ))}
          </div>
        ) : (
          <span className="text-xs text-gray-400">—</span>
        )}
      </td>
      <td className="px-4 py-3 text-gray-600 text-xs space-y-0.5">
        <p>{provider.email}</p>
        <p>{formatPhoneDisplay(provider.phone)}</p>
      </td>
      <td className="px-4 py-3 text-xs text-gray-600 whitespace-nowrap">
        {provider.city}, {provider.state}
      </td>
      <td className="px-4 py-3">
        <span
          className={`inline-flex items-center rounded-full px-2 py-0.5 text-[11px] font-medium border ${
            provider.isActive
              ? "bg-emerald-50 text-emerald-700 border-emerald-200"
              : "bg-gray-50 text-gray-500 border-gray-200"
          }`}
        >
          {provider.isActive ? "Active" : "Inactive"}
        </span>
      </td>
      <td className="px-4 py-3">
        <span
          className={`inline-flex items-center rounded-full px-2 py-0.5 text-[11px] font-medium border ${
            provider.acceptingReferrals
              ? "bg-green-50 text-green-700 border-green-200"
              : "bg-amber-50 text-amber-700 border-amber-200"
          }`}
        >
          {provider.acceptingReferrals ? "Accepting" : "Not accepting"}
        </span>
      </td>
      <td className="px-4 py-3">
        <AccessStageBadge stage={provider.accessStage} />
      </td>
      <td className="px-4 py-3">
        <span
          className={`inline-flex items-center rounded-full px-2 py-0.5 text-[11px] font-medium border ${
            provider.visibility === "Public"
              ? "bg-blue-50 text-blue-700 border-blue-200"
              : "bg-gray-50 text-gray-600 border-gray-200"
          }`}
        >
          {provider.visibility === "Public" ? "Public" : "Private"}
        </span>
      </td>
      <td className="px-4 py-3 text-right">
        {canManage ? (
          <div className="flex justify-end gap-1">
            <button
              onClick={onEdit}
              title="Edit provider"
              className="p-1.5 rounded-md text-gray-400 hover:text-blue-600 hover:bg-blue-50 transition-colors"
            >
              <i className="ri-pencil-line text-base" />
            </button>
            <button
              onClick={onRemove}
              disabled={removing}
              title="Remove from network"
              className="p-1.5 rounded-md text-gray-400 hover:text-red-600 hover:bg-red-50 disabled:opacity-40 transition-colors"
            >
              <i
                className={`text-base ${removing ? "ri-loader-4-line animate-spin" : "ri-close-circle-line"}`}
              />
            </button>
          </div>
        ) : null}
      </td>
    </tr>
  );
}
