'use client';

/**
 * CC2-INT-B07 — Public Network View.
 * CC2-INT-B08 — Public Referral Initiation.
 *
 * Layout: left 2/3 (provider list + map) | right 1/3 (always-visible referral panel).
 * View modes: Split (default) | List | Map.
 * Multi-select providers → right panel with Patient / Law Firm / Providers form sections.
 */

import { useState, useMemo, useCallback, useRef, forwardRef, useEffect, type FormEvent, type ReactNode } from 'react';
import dynamic from 'next/dynamic';
import { formatPhoneInput, isValidPhone, stripPhone } from '@/lib/phone';
import { isValidIsoDate, hasReasonableYear } from '@/lib/daterange';
import { createEnrollmentToken } from '@/app/enroll/actions';
import type {
  PublicNetworkDetail,
  PublicProviderItem,
  PublicProviderMarker,
  PublicReferralRequest,
} from '@/lib/public-network-api';
import type { NumberedMarker } from './public-network-map';
import { URGENCY_OPTIONS, type ReferralUrgencyValue } from '@/types/careconnect';

const PublicNetworkMap = dynamic(
  () => import('./public-network-map').then(m => m.PublicNetworkMap),
  { ssr: false, loading: () => <div className="h-full w-full bg-gray-100 animate-pulse" /> },
);

export interface PrefillLawFirm {
  firmName:    string;
  email:       string;
  contactName?: string;
}

interface PublicNetworkViewProps {
  detail:          PublicNetworkDetail;
  tenantCode:      string;
  tenantId:        string;
  loginUrl:        string;
  referrerScopeSignature?: string;
  /** When provided, the law firm section is hidden and pre-filled (authenticated referrer flow). */
  prefillLawFirm?: PrefillLawFirm;
}

type ViewMode = 'split' | 'list' | 'map';

interface SearchLocation {
  latitude:  number;
  longitude: number;
  label:     string;
  source?:   'geocode' | 'providerFallback';
}

type GeocodeSuggestion = {
  displayName?: string;
  latitude:     number;
  longitude:    number;
};

type ProviderWithDistance = PublicProviderItem & { distanceMiles?: number | null };

const US_STATE_NAMES = new Set([
  'alabama', 'alaska', 'arizona', 'arkansas', 'california',
  'colorado', 'connecticut', 'delaware', 'florida', 'georgia',
  'hawaii', 'idaho', 'illinois', 'indiana', 'iowa',
  'kansas', 'kentucky', 'louisiana', 'maine', 'maryland',
  'massachusetts', 'michigan', 'minnesota', 'mississippi', 'missouri',
  'montana', 'nebraska', 'nevada', 'new hampshire', 'new jersey',
  'new mexico', 'new york', 'north carolina', 'north dakota',
  'ohio', 'oklahoma', 'oregon', 'pennsylvania', 'rhode island',
  'south carolina', 'south dakota', 'tennessee', 'texas',
  'utah', 'vermont', 'virginia', 'washington', 'west virginia',
  'wisconsin', 'wyoming',
]);
const US_STATE_CODES = new Set([
  'AL', 'AK', 'AZ', 'AR', 'CA', 'CO', 'CT', 'DE', 'FL', 'GA',
  'HI', 'ID', 'IL', 'IN', 'IA', 'KS', 'KY', 'LA', 'ME', 'MD',
  'MA', 'MI', 'MN', 'MS', 'MO', 'MT', 'NE', 'NV', 'NH', 'NJ',
  'NM', 'NY', 'NC', 'ND', 'OH', 'OK', 'OR', 'PA', 'RI', 'SC',
  'SD', 'TN', 'TX', 'UT', 'VT', 'VA', 'WA', 'WV', 'WI', 'WY',
]);
const US_STATE_CODE_TO_NAME: Record<string, string> = {
  AL: 'alabama', AK: 'alaska', AZ: 'arizona', AR: 'arkansas', CA: 'california',
  CO: 'colorado', CT: 'connecticut', DE: 'delaware', FL: 'florida', GA: 'georgia',
  HI: 'hawaii', ID: 'idaho', IL: 'illinois', IN: 'indiana', IA: 'iowa',
  KS: 'kansas', KY: 'kentucky', LA: 'louisiana', ME: 'maine', MD: 'maryland',
  MA: 'massachusetts', MI: 'michigan', MN: 'minnesota', MS: 'mississippi', MO: 'missouri',
  MT: 'montana', NE: 'nebraska', NV: 'nevada', NH: 'new hampshire', NJ: 'new jersey',
  NM: 'new mexico', NY: 'new york', NC: 'north carolina', ND: 'north dakota',
  OH: 'ohio', OK: 'oklahoma', OR: 'oregon', PA: 'pennsylvania', RI: 'rhode island',
  SC: 'south carolina', SD: 'south dakota', TN: 'tennessee', TX: 'texas',
  UT: 'utah', VT: 'vermont', VA: 'virginia', WA: 'washington', WV: 'west virginia',
  WI: 'wisconsin', WY: 'wyoming',
};
const US_STATE_NAME_TO_CODE = new Map(
  Object.entries(US_STATE_CODE_TO_NAME).map(([code, name]) => [name, code]),
);
const ZIP_CODE_PATTERN = /^\d{5}(?:-\d{4})?$/;
const ADDRESS_HINT_PATTERN = /\d|,|\b(?:apt|suite|ste|unit|street|st|road|rd|avenue|ave|boulevard|blvd|drive|dr|lane|ln|court|ct|circle|cir|place|pl|parkway|pkwy|highway|hwy|way|terrace|ter)\b/i;

function normalizeLocationValue(value: string): string {
  return value
    .trim()
    .toLowerCase()
    .replace(/[.,]/g, ' ')
    .replace(/\s+/g, ' ');
}

function stateSearchValues(state: string): string[] {
  const trimmed = state.trim();
  if (!trimmed) return [];

  const values = new Set<string>([trimmed]);
  const upper = trimmed.toUpperCase();
  const normalized = normalizeLocationValue(trimmed);

  const fullName = US_STATE_CODE_TO_NAME[upper];
  if (fullName) values.add(fullName);

  const code = US_STATE_NAME_TO_CODE.get(normalized);
  if (code) values.add(code);

  return [...values];
}

function providerLocationSearchValues(provider: PublicProviderItem): string[] {
  const city = provider.city?.trim() ?? '';
  const state = provider.state?.trim() ?? '';
  const addressLine1 = provider.addressLine1?.trim() ?? '';
  const postalCode = provider.postalCode?.trim() ?? '';
  const stateValues = stateSearchValues(state);
  const values = [
    addressLine1,
    city,
    postalCode,
    ...stateValues,
  ];

  for (const stateValue of stateValues) {
    values.push(
      [addressLine1, city, stateValue, postalCode].filter(Boolean).join(' '),
      [city, stateValue].filter(Boolean).join(' '),
      [city, stateValue, postalCode].filter(Boolean).join(' '),
      [stateValue, postalCode].filter(Boolean).join(' '),
    );
  }

  return values;
}

function hasExactProviderLocationMatch(query: string, providers: PublicProviderItem[]): boolean {
  const target = normalizeLocationValue(query);
  if (!target) return false;

  return providers.some(p => {
    return providerLocationSearchValues(p).some(value => normalizeLocationValue(value) === target);
  });
}

function getProviderLocationFallback(
  query: string,
  providers: PublicProviderItem[],
  markerById: Record<string, PublicProviderMarker>,
): SearchLocation | null {
  const target = normalizeLocationValue(query);
  if (!target) return null;

  const matches: Array<{ latitude: number; longitude: number }> = [];
  const exactStreetProviderIds = getExactStreetProviderIds(query, providers);

  for (const p of providers) {
    const providerId = providerEntryId(p);
    const matchesQuery = exactStreetProviderIds.size > 0
      ? exactStreetProviderIds.has(providerId)
      : providerMatchesLocationContext(p, query);
    if (!matchesQuery) continue;

    const marker = markerById[providerId];
    const coordinates = marker ? usableCoordinates(marker) : null;
    if (coordinates) matches.push(coordinates);
  }

  if (matches.length === 0) return null;

  return {
    latitude: matches.reduce((sum, point) => sum + point.latitude, 0) / matches.length,
    longitude: matches.reduce((sum, point) => sum + point.longitude, 0) / matches.length,
    label: query.trim(),
    source: 'providerFallback',
  };
}

function buildProviderAddressGeocodeQuery(provider: PublicProviderItem): string | null {
  const locality = [provider.city, provider.state, provider.postalCode]
    .map(value => value?.trim() ?? '')
    .filter(Boolean);
  if (locality.length === 0) return null;

  // A mobile facility stores a human-readable service area (for example,
  // "Greater Las Vegas Metro") in addressLine1 rather than a street address.
  // Geocode its locality so the map can use a city centroid for the coverage area.
  if (provider.isMobile) return locality.join(', ');

  const addressLine1 = provider.addressLine1?.trim();
  if (!addressLine1) return null;

  return [addressLine1, ...locality].join(', ');
}

function isStreetAddressQuery(query: string): boolean {
  return ADDRESS_HINT_PATTERN.test(query.trim());
}

function providerMatchesExactLocation(provider: PublicProviderItem, query: string): boolean {
  const target = normalizeLocationValue(query);
  if (!target) return false;

  return providerLocationSearchValues(provider)
    .some(value => normalizeLocationValue(value) === target);
}

function providerMatchesLocationContext(provider: PublicProviderItem, query: string): boolean {
  if (providerMatchesExactLocation(provider, query)) return true;

  const target = normalizeLocationValue(query);
  const addressLine1 = normalizeLocationValue(provider.addressLine1 ?? '');
  if (addressLine1 && target.includes(addressLine1)) return true;

  const city = normalizeLocationValue(provider.city ?? '');
  const postalCode = normalizeLocationValue(provider.postalCode ?? '');
  const stateValues = stateSearchValues(provider.state ?? '').map(normalizeLocationValue);
  return Boolean(
    postalCode && target.includes(postalCode) &&
    city && target.includes(city) &&
    stateValues.some(state => state && target.includes(state)),
  );
}

function getExactStreetProviderIds(query: string, providers: PublicProviderItem[]): Set<string> {
  if (!isStreetAddressQuery(query)) return new Set<string>();

  const target = normalizeLocationValue(query);
  const addressMatches = providers.filter(provider => {
    const addressLine1 = normalizeLocationValue(provider.addressLine1 ?? '');
    return addressLine1.length >= 5 && target.includes(addressLine1);
  });
  if (addressMatches.length > 0) {
    return new Set(addressMatches.map(providerEntryId));
  }

  const postalMatches = providers.filter(provider => {
    const postalCode = normalizeLocationValue(provider.postalCode ?? '');
    return postalCode.length >= 5 && target.includes(postalCode);
  });
  return postalMatches.length === 1
    ? new Set(postalMatches.map(providerEntryId))
    : new Set<string>();
}

function hasProviderTextMatch(query: string, providers: PublicProviderItem[]): boolean {
  const target = normalizeLocationValue(query);
  if (!target) return false;

  return providers.some(p => {
    const values = [
      p.name,
      p.organizationName ?? '',
      p.facilityName ?? '',
      p.primarySpecialty ?? '',
      ...(p.specialties ?? []).map(s => s.name),
    ];

    return values.some(value => normalizeLocationValue(value).includes(target));
  });
}

function shouldTrySearchGeocode(query: string, providers: PublicProviderItem[]): boolean {
  const value = query.trim();
  if (!value) return false;

  if (ZIP_CODE_PATTERN.test(value)) return true;

  const stateCode = value.toUpperCase();
  const normalized = normalizeLocationValue(value);
  if (US_STATE_CODES.has(stateCode) || US_STATE_NAMES.has(normalized)) return true;

  if (value.length < 3) return false;
  if (hasExactProviderLocationMatch(value, providers)) return true;
  if (ADDRESS_HINT_PATTERN.test(value)) return true;
  if (hasProviderTextMatch(value, providers)) return false;

  return /^[a-z][a-z\s'.-]*$/i.test(value) && value.split(/\s+/).length <= 3;
}

function getFirstUsableGeocodeLocation(
  suggestions: GeocodeSuggestion[],
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
      source: 'geocode',
    };
  }

  return null;
}

function usableCoordinates(point: { latitude: number; longitude: number }): { latitude: number; longitude: number } | null {
  const latitude = Number(point.latitude);
  const longitude = Number(point.longitude);
  if (!Number.isFinite(latitude) || !Number.isFinite(longitude)) return null;
  if (latitude === 0 && longitude === 0) return null;
  return { latitude, longitude };
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

function getProviderIdentity(provider: { name: string; organizationName?: string | null }) {
  const providerName = provider.name.trim();
  const organizationName = provider.organizationName?.trim() ?? '';
  const hasDistinctOrganization =
    organizationName.length > 0 && organizationName.toLowerCase() !== providerName.toLowerCase();

  return {
    primary: organizationName || providerName,
    secondary: hasDistinctOrganization ? providerName : null,
  };
}

function providerEntryId(provider: { id: string; networkProviderId?: string | null }): string {
  return provider.networkProviderId || provider.id;
}

function providerIdentityId(provider: { id: string; providerId?: string | null }): string {
  return provider.providerId || provider.id;
}

function compareProvidersByDistance(a: ProviderWithDistance, b: ProviderWithDistance): number {
  const aDistance = typeof a.distanceMiles === 'number' && Number.isFinite(a.distanceMiles)
    ? a.distanceMiles
    : Number.POSITIVE_INFINITY;
  const bDistance = typeof b.distanceMiles === 'number' && Number.isFinite(b.distanceMiles)
    ? b.distanceMiles
    : Number.POSITIVE_INFINITY;

  if (aDistance !== bDistance) return aDistance - bDistance;
  return getProviderIdentity(a).primary.localeCompare(getProviderIdentity(b).primary);
}

// ── Main view ─────────────────────────────────────────────────────────────────

export function PublicNetworkView({
  detail,
  tenantCode,
  tenantId,
  loginUrl,
  referrerScopeSignature,
  prefillLawFirm,
}: PublicNetworkViewProps) {
  const [search,      setSearch]      = useState('');
  const [zipInput,    setZipInput]    = useState('');
  const [selectedSpecialtyCode, setSelectedSpecialtyCode] = useState('');
  const [detectedSearchLocation, setDetectedSearchLocation] = useState<SearchLocation | null>(null);
  const [settledSearchLocationQuery, setSettledSearchLocationQuery] = useState('');
  const [zipLocation, setZipLocation] = useState<SearchLocation | null>(null);
  const [searchLocationLoading, setSearchLocationLoading] = useState(false);
  const [zipLoading,  setZipLoading]  = useState(false);
  const [zipError,    setZipError]    = useState<string | null>(null);
  const [viewMode,    setViewMode]    = useState<ViewMode>('split');
  const [showAll,     setShowAll]     = useState(false);
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [hoveredId,   setHovered]     = useState<string | null>(null);
  const [zoomToId,    setZoomToId]    = useState<string | null>(null);
  const cardRefs = useRef<Record<string, HTMLDivElement | null>>({});

  const [dark, setDark] = useState<boolean>(() => {
    if (typeof window === 'undefined') return false;
    return localStorage.getItem('cc-network-theme') === 'dark';
  });
  function toggleDark() {
    setDark(prev => {
      const next = !prev;
      localStorage.setItem('cc-network-theme', next ? 'dark' : 'light');
      return next;
    });
  }

  const [markers, setMarkers] = useState<PublicProviderMarker[]>(detail.markers);

  useEffect(() => {
    if (detail.providers.length === 0) return;
    const missing = detail.providers.filter(p => {
      const entryId = providerEntryId(p);
      const m = detail.markers.find(mk => providerEntryId(mk) === entryId);
      return !m || !usableCoordinates(m);
    });
    if (missing.length === 0) return;

    let cancelled = false;
    async function geocodeMissing() {
      const results: PublicProviderMarker[] = [...detail.markers];
      await Promise.all(
        missing.map(async p => {
          const q = buildProviderAddressGeocodeQuery(p);
          if (!q) return;
          try {
            const res = await fetch(`/api/geocode/address?q=${encodeURIComponent(q)}&loose=1`);
            if (!res.ok) return;
            const suggestions = await res.json() as Array<{ latitude: number; longitude: number }>;
            if (suggestions.length === 0) return;
            const { latitude, longitude } = suggestions[0];
            results.push({
              id: p.id,
              networkProviderId: providerEntryId(p),
              providerId: providerIdentityId(p),
              facilityId: p.facilityId,
              name: p.name,
              title: p.title,
              organizationName: p.organizationName,
              facilityName: p.facilityName,
              city: p.city, state: p.state, acceptingReferrals: p.acceptingReferrals,
              latitude, longitude,
              specialties: p.specialties ?? [],
              primarySpecialtyId: p.primarySpecialtyId ?? null,
              primarySpecialty: p.primarySpecialty ?? null,
              distanceMiles: null,
              isMobile: p.isMobile,
              serviceRadiusMiles: p.serviceRadiusMiles,
              serviceAreaLabel: p.serviceAreaLabel,
            });
          } catch { /* ignore */ }
        }),
      );
      if (!cancelled) setMarkers(results);
    }
    geocodeMissing();
    return () => { cancelled = true; };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const markerById = useMemo<Record<string, PublicProviderMarker>>(() => {
    const m: Record<string, PublicProviderMarker> = {};
    for (const mk of markers) m[providerEntryId(mk)] = mk;
    return m;
  }, [markers]);

  useEffect(() => {
    const value = search.trim();

    if (!shouldTrySearchGeocode(value, detail.providers)) {
      setDetectedSearchLocation(null);
      setSettledSearchLocationQuery('');
      setSearchLocationLoading(false);
      return;
    }

    // City/state/ZIP searches may safely use the providers' average stored point while
    // geocoding runs. Exact street matches may use only that facility's point; duplicate
    // city-centroid markers are handled below so unrelated facilities are not shown as 0 mi.
    const providerFallbackLocation = getProviderLocationFallback(value, detail.providers, markerById);
    const exactStreetProviderIds = getExactStreetProviderIds(value, detail.providers);
    const initialFallbackLocation = !isStreetAddressQuery(value) || exactStreetProviderIds.size > 0
      ? providerFallbackLocation
      : null;
    setDetectedSearchLocation(initialFallbackLocation);
    if (initialFallbackLocation) {
      setZipLocation(null);
      setZipInput('');
      setZipError(null);
    }

    let cancelled = false;
    const timer = setTimeout(async () => {
      setSearchLocationLoading(true);
      try {
        const geocodeController = new AbortController();
        const geocodeTimeout = window.setTimeout(() => geocodeController.abort(), 10000);
        let res: Response;
        try {
          res = await fetch(`/api/geocode/address?q=${encodeURIComponent(value)}&loose=1`, {
            signal: geocodeController.signal,
          });
        } finally {
          window.clearTimeout(geocodeTimeout);
        }
        if (!res.ok) throw new Error('Unable to geocode search input.');
        const suggestions = await res.json() as GeocodeSuggestion[];
        const location = getFirstUsableGeocodeLocation(suggestions, value);

        if (!cancelled) {
          const nextLocation = location ?? providerFallbackLocation;
          setDetectedSearchLocation(nextLocation);
          if (nextLocation) {
            setZipLocation(null);
            setZipInput('');
            setZipError(null);
          }
        }
      } catch {
        if (!cancelled) setDetectedSearchLocation(providerFallbackLocation);
      } finally {
        if (!cancelled) {
          setSettledSearchLocationQuery(value);
          setSearchLocationLoading(false);
        }
      }
    }, 700);

    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [search, detail.providers, markerById]);

  const searchLocation = detectedSearchLocation ?? zipLocation;
  const searchActsAsLocation = detectedSearchLocation !== null;
  const trimmedSearch = search.trim();
  const searchNeedsLocationResolution = shouldTrySearchGeocode(trimmedSearch, detail.providers);
  const searchLocationPending =
    searchNeedsLocationResolution &&
    trimmedSearch !== settledSearchLocationQuery;

  const filtered = useMemo<ProviderWithDistance[]>(() => {
    let list: ProviderWithDistance[] = detail.providers;
    if (!showAll) list = list.filter(p => p.acceptingReferrals);
    const q = search.trim().toLowerCase();
    if (q && !searchActsAsLocation) list = list.filter(p =>
      p.name.toLowerCase().includes(q) ||
      (p.organizationName?.toLowerCase().includes(q) ?? false) ||
      (p.facilityName?.toLowerCase().includes(q) ?? false) ||
      (p.addressLine1?.toLowerCase().includes(q) ?? false) ||
      (p.postalCode?.toLowerCase().includes(q) ?? false) ||
      p.city.toLowerCase().includes(q) ||
      p.state.toLowerCase().includes(q) ||
      (p.primarySpecialty?.toLowerCase().includes(q) ?? false) ||
      (p.specialties ?? []).some(s => s.name.toLowerCase().includes(q)),
    );
    if (selectedSpecialtyCode) {
      list = list.filter(p => (p.specialties ?? []).some(s => s.code === selectedSpecialtyCode));
    }
    if (searchLocation) {
      const withDistances: ProviderWithDistance[] = [];
      const exactStreetProviderIds = getExactStreetProviderIds(search, detail.providers);
      const exactStreetSearch = exactStreetProviderIds.size > 0;
      for (const p of list) {
        const entryId = providerEntryId(p);
        const exactLocationMatch = exactStreetProviderIds.has(entryId);
        const mk = markerById[entryId];
        if (!mk) {
          if (exactLocationMatch) withDistances.push({ ...p, distanceMiles: 0 });
          continue;
        }
        const coordinates = usableCoordinates(mk);
        if (!coordinates) {
          if (exactLocationMatch) withDistances.push({ ...p, distanceMiles: 0 });
          continue;
        }
        const sharesFallbackOrigin =
          searchLocation.source === 'providerFallback' &&
          coordinates.latitude === searchLocation.latitude &&
          coordinates.longitude === searchLocation.longitude;
        withDistances.push({
          ...p,
          distanceMiles: exactLocationMatch
            ? 0
            : exactStreetSearch && sharesFallbackOrigin
              ? null
            : distanceMilesBetween(searchLocation, coordinates),
        });
      }
      list = withDistances.sort(compareProvidersByDistance);
    }
    return list;
  }, [detail.providers, search, showAll, selectedSpecialtyCode, searchLocation, searchActsAsLocation, markerById]);

  const displayedMarkers = useMemo<NumberedMarker[]>(() => {
    const result: NumberedMarker[] = [];
    const exactStreetProviderIds = getExactStreetProviderIds(search, detail.providers);
    let idx = 1;
    for (const p of filtered) {
      const entryId = providerEntryId(p);
      const mk = markerById[entryId];
      const coordinates = mk ? usableCoordinates(mk) : null;
      if (searchLocation && exactStreetProviderIds.has(entryId)) {
        const markerSource = mk ? { ...mk, id: entryId } : { ...p, id: entryId };
        result.push({
          ...markerSource,
          latitude: searchLocation.latitude,
          longitude: searchLocation.longitude,
          distanceMiles: 0,
          index: idx++,
        });
      } else if (mk && coordinates) {
        result.push({ ...mk, id: entryId, ...coordinates, distanceMiles: p.distanceMiles ?? mk.distanceMiles, index: idx++ });
      }
    }
    return result;
  }, [filtered, markerById, search, detail.providers, searchLocation]);

  const indexFor = (id: string) =>
    displayedMarkers.find(m => m.id === id)?.index ?? null;

  async function applyZipFilter() {
    const value = zipInput.trim();
    if (!value) {
      setZipLocation(null);
      setZipError(null);
      return;
    }
    setZipLoading(true);
    setZipError(null);
    try {
      const res = await fetch(`/api/geocode/address?q=${encodeURIComponent(value)}&loose=1`);
      if (!res.ok) throw new Error('Unable to geocode ZIP code.');
      const suggestions = await res.json() as GeocodeSuggestion[];
      const location = getFirstUsableGeocodeLocation(suggestions, value);
      if (!location) throw new Error('No matching ZIP code was found.');
      if (detectedSearchLocation) setSearch('');
      setDetectedSearchLocation(null);
      setZipLocation(location);
    } catch (err) {
      setZipLocation(null);
      setZipError(err instanceof Error ? err.message : 'Unable to geocode ZIP code.');
    } finally {
      setZipLoading(false);
    }
  }

  function clearFilters() {
    setSearch('');
    setZipInput('');
    setSelectedSpecialtyCode('');
    setDetectedSearchLocation(null);
    setSettledSearchLocationQuery('');
    setZipLocation(null);
    setSearchLocationLoading(false);
    setZipError(null);
  }

  function toggleSelect(id: string) {
    setSelectedIds(prev => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  }

  function handleMapSelect(id: string) {
    setHovered(id);
    cardRefs.current[id]?.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
  }

  function handleMapReferral(m: PublicProviderMarker) {
    toggleSelect(providerEntryId(m));
  }

  const selectedProviders = detail.providers.filter(p => selectedIds.has(providerEntryId(p)));
  const hasMarkers        = displayedMarkers.length > 0 || !!searchLocation;
  const shownCount        = filtered.length;
  const showProviderSearchLoading = searchLocationPending && filtered.length === 0;

  return (
    <div data-theme={dark ? 'dark' : 'light'} className="flex flex-col h-full bg-gray-50 dark:bg-gray-950 overflow-hidden">

      {/* ── Header ─────────────────────────────────────────────────────────── */}
      <header className="flex-shrink-0 border-b border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 shadow-sm">
        <div className="flex items-center gap-3 px-5 pt-3 pb-2">
          {/* Tenant logo */}
          <img
            src={`/api/branding/logo/public?tenantCode=${encodeURIComponent(tenantCode)}`}
            alt=""
            className="h-8 w-auto object-contain flex-shrink-0"
            onError={e => { (e.currentTarget as HTMLImageElement).style.display = 'none'; }}
          />
          <h1 className="text-lg font-bold text-gray-900 dark:text-white leading-tight">
            Provider Network
          </h1>
          <span className="ml-auto text-sm text-gray-500 dark:text-gray-400">
            {detail.providers.length} provider{detail.providers.length !== 1 ? 's' : ''}
          </span>

          {/* Dark / light toggle */}
          <button
            onClick={toggleDark}
            title={dark ? 'Switch to light mode' : 'Switch to dark mode'}
            className="ml-1 w-8 h-8 flex items-center justify-center rounded-lg text-gray-500 dark:text-gray-400 hover:bg-gray-100 dark:hover:bg-gray-700 transition-colors flex-shrink-0"
          >
            <i className={dark ? 'ri-sun-line text-base' : 'ri-moon-line text-base'} />
          </button>
        </div>

        <div className="flex items-center gap-2 px-5 pb-2.5">
          {/* View tabs */}
          <div className="flex items-center border border-gray-200 dark:border-gray-700 rounded-lg overflow-hidden flex-shrink-0">
            {(['split', 'list', 'map'] as ViewMode[]).map(m => (
              <button
                key={m}
                onClick={() => setViewMode(m)}
                className={[
                  'px-3 py-1.5 text-xs font-medium capitalize transition-colors',
                  viewMode === m
                    ? 'bg-gray-900 text-white dark:bg-gray-100 dark:text-gray-900'
                    : 'bg-white dark:bg-gray-800 text-gray-600 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-700',
                ].join(' ')}
              >
                {m}
              </button>
            ))}
          </div>

          {/* Search */}
          <div className="flex-1 relative">
            <i className={[
              searchLocationPending || searchLocationLoading ? 'ri-loader-4-line animate-spin' : 'ri-search-line',
              'absolute left-2.5 top-1/2 -translate-y-1/2 text-gray-400 dark:text-gray-500 text-sm pointer-events-none',
            ].join(' ')} />
            <input
              type="search"
              placeholder="Search by provider, specialty, address, city, state, or ZIP…"
              value={search}
              onChange={e => setSearch(e.target.value)}
              className="h-10 w-full pl-8 pr-3 text-sm border border-gray-200 dark:border-gray-600 rounded-lg
                         focus:outline-none focus:border-blue-400 dark:focus:border-blue-500 focus:ring-1 focus:ring-blue-100 dark:focus:ring-blue-900/30
                         placeholder-gray-400 dark:placeholder-gray-500 bg-white dark:bg-gray-800 text-gray-900 dark:text-white"
            />
          </div>

          <input
            type="text"
            aria-label="ZIP code"
            value={zipInput}
            onChange={e => setZipInput(e.target.value)}
            onKeyDown={e => e.key === 'Enter' && void applyZipFilter()}
            placeholder="ZIP"
            className="h-10 w-24 px-3 text-sm border border-gray-200 dark:border-gray-600 rounded-lg
                       focus:outline-none focus:border-blue-400 dark:focus:border-blue-500 focus:ring-1 focus:ring-blue-100 dark:focus:ring-blue-900/30
                       placeholder-gray-400 dark:placeholder-gray-500 bg-white dark:bg-gray-800 text-gray-900 dark:text-white flex-shrink-0"
          />

          <button
            onClick={() => void applyZipFilter()}
            disabled={zipLoading}
            className="h-10 flex items-center gap-1.5 px-4 text-sm font-medium border rounded-lg flex-shrink-0 transition-colors
                       bg-white dark:bg-gray-800 text-gray-600 dark:text-gray-300 border-gray-200 dark:border-gray-600 hover:border-gray-300 dark:hover:border-gray-500 disabled:opacity-50"
          >
            {zipLoading ? 'Finding…' : 'Apply ZIP'}
          </button>

          <select
            aria-label="Specialty"
            value={selectedSpecialtyCode}
            onChange={e => setSelectedSpecialtyCode(e.target.value)}
            className="h-10 w-40 px-3 text-sm border border-gray-200 dark:border-gray-600 rounded-lg
                       focus:outline-none focus:border-blue-400 dark:focus:border-blue-500 focus:ring-1 focus:ring-blue-100 dark:focus:ring-blue-900/30
                       bg-white dark:bg-gray-800 text-gray-900 dark:text-white flex-shrink-0"
          >
            <option value="">All specialties</option>
            {(detail.specialties ?? []).map(s => (
              <option key={s.id} value={s.code}>{s.name}</option>
            ))}
          </select>

          {/* Filter */}
          <button
            onClick={() => setShowAll(v => !v)}
            className={[
              'h-10 flex items-center gap-1.5 px-4 text-sm font-medium border rounded-lg flex-shrink-0 transition-colors',
              showAll
                ? 'bg-gray-900 dark:bg-gray-100 text-white dark:text-gray-900 border-gray-900 dark:border-gray-100'
                : 'bg-white dark:bg-gray-800 text-gray-600 dark:text-gray-300 border-gray-200 dark:border-gray-600 hover:border-gray-300 dark:hover:border-gray-500',
            ].join(' ')}
          >
            <i className="ri-filter-3-line" />
            {showAll ? 'All providers' : 'Accepting only'}
          </button>

          <button
            onClick={clearFilters}
            className="px-3 py-1.5 text-xs font-medium text-gray-500 dark:text-gray-400 hover:text-gray-700 dark:hover:text-gray-200 flex-shrink-0"
          >
            Clear
          </button>

          <span className="text-xs text-gray-400 font-medium flex-shrink-0">
            {shownCount} of {detail.providers.length}
          </span>

          {selectedIds.size > 0 && (
            <span className="flex items-center gap-1.5 px-3 py-1.5 text-xs font-semibold bg-blue-600 text-white rounded-lg flex-shrink-0">
              <i className="ri-check-line text-xs" />
              {selectedIds.size} selected
            </span>
          )}
        </div>
        {zipError && (
          <p className="px-5 pb-2 text-xs text-red-600 dark:text-red-400">{zipError}</p>
        )}
      </header>

      {/* ── Body: left content + right panel ───────────────────────────────── */}
      <div className="flex flex-1 overflow-hidden">

        {/* ── LEFT: 2/3 provider content ──────────────────────────────────── */}
        <div className="flex flex-1 overflow-hidden">

          {/* Split mode: scrollable list + map side-by-side */}
          {viewMode === 'split' && (
            <>
              {/* Provider list column */}
              <div className="w-[300px] flex-shrink-0 border-r border-gray-200 dark:border-gray-700 overflow-y-auto bg-white dark:bg-gray-900">
                {showProviderSearchLoading ? (
                  <ProviderSearchLoading />
                ) : filtered.length === 0 ? (
                  <div className="p-6 text-center">
                    <i className="ri-map-pin-line text-2xl text-gray-300 dark:text-gray-600 mb-2 block" />
                    <p className="text-sm text-gray-400">No providers found.</p>
                  </div>
                ) : (
                  <div className="divide-y divide-gray-100 dark:divide-gray-800">
                    {filtered.map((provider, i) => {
                      const entryId = providerEntryId(provider);
                      return (
                        <ProviderCard
                          key={entryId}
                          provider={provider}
                          number={indexFor(entryId) ?? i + 1}
                          selected={selectedIds.has(entryId)}
                          hovered={hoveredId === entryId}
                          compact
                          tenantId={tenantId}
                          onHover={setHovered}
                          onToggle={toggleSelect}
                          onClick={() => { setHovered(entryId); setZoomToId(entryId); }}
                          ref={el => { cardRefs.current[entryId] = el; }}
                        />
                      );
                    })}
                  </div>
                )}
              </div>

              {/* Map */}
              <div className="flex-1 relative">
                {hasMarkers ? (
                  <PublicNetworkMap
                    markers={displayedMarkers}
                    selectedId={hoveredId}
                    zoomToId={zoomToId}
                    onZoomed={() => setZoomToId(null)}
                    searchLocation={searchLocation}
                    onSelect={handleMapSelect}
                    onRequestReferral={handleMapReferral}
                  />
                ) : (
                  <div className="h-full bg-gray-100 flex items-center justify-center">
                    <p className="text-sm text-gray-400">No location data available</p>
                  </div>
                )}
              </div>
            </>
          )}

          {/* List mode: rich provider grid */}
          {viewMode === 'list' && (
            <div className="flex-1 overflow-y-auto bg-gray-50 dark:bg-gray-950 p-5">
              {showProviderSearchLoading ? (
                <ProviderSearchLoading />
              ) : filtered.length === 0 ? (
                <div className="flex flex-col items-center justify-center py-20 text-center">
                  <i className="ri-map-pin-line text-3xl text-gray-300 dark:text-gray-600 mb-3 block" />
                  <p className="text-sm text-gray-400">No providers found.</p>
                </div>
              ) : (
                <div className="grid grid-cols-2 gap-4">
                  {filtered.map((provider, i) => {
                    const entryId = providerEntryId(provider);
                    return (
                      <ProviderCard
                        key={entryId}
                        provider={provider}
                        number={indexFor(entryId) ?? i + 1}
                        selected={selectedIds.has(entryId)}
                        hovered={hoveredId === entryId}
                        compact={false}
                        tenantId={tenantId}
                        onHover={setHovered}
                        onToggle={toggleSelect}
                        ref={el => { cardRefs.current[entryId] = el; }}
                      />
                    );
                  })}
                </div>
              )}
            </div>
          )}

          {/* Map mode: full map */}
          {viewMode === 'map' && (
            <div className="flex-1 relative">
              {hasMarkers ? (
                <PublicNetworkMap
                  markers={displayedMarkers}
                  selectedId={hoveredId}
                  searchLocation={searchLocation}
                  onSelect={handleMapSelect}
                  onRequestReferral={handleMapReferral}
                />
              ) : (
                <div className="h-full bg-gray-100 flex items-center justify-center">
                  <p className="text-sm text-gray-400">No location data available</p>
                </div>
              )}
            </div>
          )}
        </div>

        {/* ── RIGHT: 1/3 always-visible referral panel ────────────────────── */}
        <ReferralPanel
          providers={selectedProviders}
          tenantId={tenantId}
          loginUrl={loginUrl}
          referrerScopeSignature={referrerScopeSignature}
          onClearSelection={() => setSelectedIds(new Set())}
          prefillLawFirm={prefillLawFirm}
        />
      </div>
    </div>
  );
}

function ProviderSearchLoading() {
  return (
    <div className="flex flex-col items-center justify-center p-6 py-16 text-center">
      <i className="ri-loader-4-line animate-spin text-2xl text-blue-500 mb-2 block" />
      <p className="text-sm font-medium text-gray-600 dark:text-gray-300">Searching provider locations...</p>
      <p className="mt-1 text-xs text-gray-400 dark:text-gray-500">Checking the address before showing results.</p>
    </div>
  );
}

// ── Provider card ─────────────────────────────────────────────────────────────

const ProviderCard = forwardRef<
  HTMLDivElement,
  {
    provider: PublicProviderItem;
    number:   number;
    selected: boolean;
    hovered:  boolean;
    compact:  boolean;
    tenantId: string;
    onHover:  (id: string | null) => void;
    onToggle: (id: string) => void;
    onClick?: () => void;
  }
>(function ProviderCard({ provider, number, selected, hovered, compact, tenantId, onHover, onToggle, onClick }, ref) {
  const identity = getProviderIdentity(provider);
  const entryId = providerEntryId(provider);
  const facilityName = provider.facilityName?.trim() ?? '';
  const showFacilityName =
    facilityName.length > 0 &&
    facilityName.toLowerCase() !== identity.primary.toLowerCase() &&
    facilityName.toLowerCase() !== (identity.secondary ?? '').toLowerCase();

  return (
    <div
      ref={ref}
      onMouseEnter={() => onHover(entryId)}
      onMouseLeave={() => onHover(null)}
      onClick={onClick}
      className={[
        'transition-colors',
        compact ? 'p-3' : 'p-4 rounded-xl border',
        hovered || selected
          ? compact
            ? 'bg-blue-50 dark:bg-blue-950/40'
            : 'border-blue-300 dark:border-blue-600 bg-blue-50 dark:bg-blue-950/40 shadow-sm'
          : compact
            ? 'hover:bg-gray-50 dark:hover:bg-gray-800'
            : 'border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-800 hover:border-gray-300 dark:hover:border-gray-600 hover:shadow-sm',
      ].join(' ')}
    >
      <div className="flex items-start gap-3">
        {/* Number badge */}
        <div className={[
          'rounded-full text-white text-xs font-bold flex-shrink-0 flex items-center justify-center',
          compact ? 'w-6 h-6 mt-0.5' : 'w-8 h-8',
          selected ? 'bg-blue-600' : 'bg-gray-400 dark:bg-gray-500',
        ].join(' ')}>
          {number}
        </div>

        {/* Info */}
        <div className="flex-1 min-w-0">
          <p className={['font-semibold text-gray-900 dark:text-white leading-tight', compact ? 'text-sm' : 'text-base'].join(' ')}>
            {identity.primary}
          </p>
          {identity.secondary && (
            <p className={['text-gray-500 dark:text-gray-400 mt-0.5 truncate', compact ? 'text-xs' : 'text-sm'].join(' ')}>
              {identity.secondary}
            </p>
          )}
          {showFacilityName && (
            <p className={['text-gray-500 dark:text-gray-400 mt-0.5 truncate', compact ? 'text-xs' : 'text-sm'].join(' ')}>
              {facilityName}
            </p>
          )}
          <p className={['text-gray-400 dark:text-gray-500 mt-0.5', compact ? 'text-xs' : 'text-sm'].join(' ')}>
            {provider.isMobile
              ? `Mobile · ${[provider.serviceAreaLabel, `${provider.city}, ${provider.state}`].filter(Boolean).join(' · ')}${!compact && provider.serviceRadiusMiles ? ` · ${provider.serviceRadiusMiles}mi radius` : ''}`
              : <>{provider.city}, {provider.state}{!compact && provider.postalCode ? ` ${provider.postalCode}` : ''}</>}
          </p>
          {typeof provider.distanceMiles === 'number' && Number.isFinite(provider.distanceMiles) && (
            <p className={['text-blue-600 dark:text-blue-400 mt-0.5 font-medium', compact ? 'text-xs' : 'text-sm'].join(' ')}>
              {provider.distanceMiles.toFixed(1)} mi away
            </p>
          )}
          {provider.phone && !compact && (
            <p className="text-sm text-gray-500 dark:text-gray-400 mt-1 flex items-center gap-1.5">
              <i className="ri-phone-line text-gray-400 dark:text-gray-500 text-xs" />
              {provider.phone}
            </p>
          )}
          {provider.phone && compact && (
            <p className="text-xs text-gray-400 dark:text-gray-500 mt-0.5">{provider.phone}</p>
          )}

          {/* Status */}
          <div className="flex flex-wrap gap-1.5 mt-2">
            <span className={[
              'rounded-full font-medium flex items-center gap-1',
              compact ? 'text-xs px-1.5 py-0.5' : 'text-xs px-2 py-0.5',
              provider.acceptingReferrals
                ? 'bg-green-50 dark:bg-green-900/30 text-green-700 dark:text-green-400'
                : 'bg-gray-100 dark:bg-gray-700 text-gray-500 dark:text-gray-400',
            ].join(' ')}>
              {provider.acceptingReferrals ? (
                <><i className="ri-checkbox-circle-line" />Accepting</>
              ) : (
                'Not accepting'
              )}
            </span>
          </div>

          {/* Specialties */}
          {(provider.specialties ?? []).length > 0 && (
            <div className="flex flex-wrap gap-1.5 mt-1.5">
              {(provider.specialties ?? []).map(s => (
                <span key={s.id} className={['bg-gray-100 dark:bg-gray-700 text-gray-600 dark:text-gray-300 rounded-full font-medium', compact ? 'text-xs px-1.5 py-0.5' : 'text-xs px-2 py-0.5'].join(' ')}>
                  {s.name}
                </span>
              ))}
            </div>
          )}

        </div>

        {/* Select button */}
        <button
          onClick={e => {
            e.stopPropagation();
            onToggle(entryId);
          }}
          className={[
            'flex-shrink-0 rounded-lg text-xs font-semibold transition-colors flex items-center gap-1',
            compact ? 'px-2 py-1' : 'px-3 py-1.5',
            selected
              ? 'bg-blue-600 text-white hover:bg-blue-700'
              : 'bg-gray-100 dark:bg-gray-700 text-gray-600 dark:text-gray-300 hover:bg-blue-50 dark:hover:bg-blue-900/30 hover:text-blue-700 dark:hover:text-blue-400',
          ].join(' ')}
          title={selected ? 'Remove from selection' : 'Select provider'}
        >
          {selected ? (
            <><i className="ri-check-line" />{compact ? '' : 'Selected'}</>
          ) : (
            <><i className="ri-add-line" />{compact ? '' : 'Select'}</>
          )}
        </button>
      </div>
    </div>
  );
});

// ── Referral panel ────────────────────────────────────────────────────────────

const SERVICE_TYPES = [
  'General Referral',
  'Consultation',
  'Initial Service',
  'Diagnostic Service',
  'Laboratory Service',
  'Imaging/Radiology',
  'Emergency Service',
  'Home Health Service',
  'Specialist Referral',
  'Telehealth Service',
  'Follow-up Service',
];

interface ReferralForm {
  patientFirstName:     string;
  patientLastName:      string;
  patientPhone:         string;
  patientEmail:         string;
  patientAddress:       string;
  patientDob:           string;   // YYYY-MM-DD
  patientDateOfAccident: string;  // YYYY-MM-DD
  urgency:              ReferralUrgencyValue;
  serviceType:          string;
  treatmentTypeId:      string;
  referralAttributionId: string;
  lienCompanyName:      string;
  lienCompanyEmail:     string;
  notes:                string;
  firmName:             string;
  contactFirstName:     string;
  contactLastName:      string;
  email:                string;
  phone:                string;
}

const EMPTY_FORM: ReferralForm = {
  patientFirstName: '', patientLastName: '', patientPhone: '', patientEmail: '',
  patientAddress: '', patientDob: '', patientDateOfAccident: '',
  urgency: 'Normal',
  serviceType: 'General Referral',
  treatmentTypeId: '',
  referralAttributionId: '',
  lienCompanyName: '',
  lienCompanyEmail: '',
  notes: '',
  firmName: '', contactFirstName: '', contactLastName: '', email: '', phone: '',
};

type PanelState = 'form' | 'confirm' | 'submitting' | 'success' | 'error' | 'account-exists';

interface CreatedReferralUploadTarget {
  referralId: string;
  providerId: string;
  fileKey:    string;
}

function extractApiErrorMessage(err: unknown, fallback = 'Something went wrong. Please try again.'): string {
  if (err instanceof Error) return err.message;
  if (err && typeof err === 'object' && 'message' in err && typeof (err as { message: unknown }).message === 'string')
    return (err as { message: string }).message;
  if (err && typeof err === 'object' && 'detail' in err && typeof (err as { detail: unknown }).detail === 'string')
    return (err as { detail: string }).detail;
  if (err && typeof err === 'object' && 'title' in err && typeof (err as { title: unknown }).title === 'string')
    return (err as { title: string }).title;
  return fallback;
}

function toCreatedReferralUploadTarget(
  value: unknown,
  fallbackProviderId: string,
  fileKey: string,
): CreatedReferralUploadTarget {
  if (!value || typeof value !== 'object') {
    throw new Error('Referral created, but the server returned an unexpected response.');
  }

  const referralId =
    'referralId' in value && typeof (value as { referralId?: unknown }).referralId === 'string'
      ? (value as { referralId: string }).referralId
      : 'id' in value && typeof (value as { id?: unknown }).id === 'string'
        ? (value as { id: string }).id
        : null;

  const providerId =
    'providerId' in value && typeof (value as { providerId?: unknown }).providerId === 'string'
      ? (value as { providerId: string }).providerId
      : fallbackProviderId;

  if (!referralId) {
    throw new Error('Referral created, but the response did not include a referral id.');
  }

  return { referralId, providerId, fileKey };
}

function ReferralPanel({
  providers, tenantId, loginUrl, referrerScopeSignature, onClearSelection, prefillLawFirm,
}: {
  providers:        PublicProviderItem[];
  tenantId:         string;
  loginUrl:         string;
  referrerScopeSignature?: string;
  onClearSelection: () => void;
  prefillLawFirm?:  PrefillLawFirm;
}) {
  const [form,           setForm]          = useState<ReferralForm>(() =>
    prefillLawFirm
      ? { ...EMPTY_FORM, firmName: prefillLawFirm.firmName, email: prefillLawFirm.email, contactFirstName: prefillLawFirm.contactName ?? '' }
      : EMPTY_FORM
  );
  const [state,          setState]         = useState<PanelState>('form');
  const [errorMsg,       setErrMsg]        = useState('');
  const [fieldErrors,    setErrors]        = useState<Record<string, string>>({});
  const [providerFiles,  setProviderFiles] = useState<Record<string, File | null>>({});
  const [hasPortalAccess, setHasPortalAccess] = useState(false);
  const [enrollToken,    setEnrollToken]   = useState<string | null>(null);
  const [treatmentTypes, setTreatmentTypes] = useState<{ id: string; name: string }[]>([]);
  const [attributionOptions, setAttributionOptions] = useState<{ id: string; firstName: string; lastName: string }[]>([]);
  const [checkingEmail,  setCheckingEmail]  = useState(false);

  const hasPhoneValue        = form.phone.trim().length > 0;
  const hasInvalidPhone      = hasPhoneValue && !isValidPhone(form.phone);
  const hasPatientPhoneValue = form.patientPhone.trim().length > 0;
  const hasInvalidPatientPhone = hasPatientPhoneValue && !isValidPhone(form.patientPhone);

  const canSubmit =
    !!form.patientFirstName.trim() &&
    !!form.patientLastName.trim() &&
    hasPatientPhoneValue && !hasInvalidPatientPhone &&
    !!form.patientDob && isValidIsoDate(form.patientDob) && hasReasonableYear(form.patientDob) && new Date(form.patientDob) <= new Date() &&
    !!form.patientDateOfAccident && isValidIsoDate(form.patientDateOfAccident) && hasReasonableYear(form.patientDateOfAccident) && new Date(form.patientDateOfAccident) <= new Date() &&
    !hasInvalidPhone &&
    (!form.patientEmail.trim() || /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(form.patientEmail.trim())) &&
    (prefillLawFirm
      ? !!([form.contactFirstName.trim(), form.contactLastName.trim()].filter(Boolean).join(' ') || form.firmName.trim())
      : !!form.firmName.trim() && !!form.email.trim() && /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(form.email.trim()));

  useEffect(() => {
    const endpoint = prefillLawFirm
      ? '/api/careconnect/api/treatment-types'
      : `/api/public/careconnect/api/public/treatment-types`;
    fetch(endpoint, prefillLawFirm ? {} : { headers: { 'X-Tenant-Id': tenantId } })
      .then(r => r.ok ? r.json() : [])
      .then((data: { id: string; name: string }[]) => setTreatmentTypes(data))
      .catch(() => {});
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    const endpoint = prefillLawFirm
      ? '/api/careconnect/api/referral-attributions/options'
      : `/api/public/careconnect/api/public/referral-attributions`;
    fetch(endpoint, prefillLawFirm ? {} : { headers: { 'X-Tenant-Id': tenantId } })
      .then(r => r.ok ? r.json() : [])
      .then((data: { id: string; firstName: string; lastName: string }[]) => setAttributionOptions(data))
      .catch(() => {}); // non-fatal — field simply shows no options
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Pre-generate a signed enrollment token when the referral succeeds so the
  // "Activate your free account" CTA never carries raw PII in the URL.
  // Skipped when the user is already authenticated (prefillLawFirm present) — CTA is hidden.
  useEffect(() => {
    if (state !== 'success' || !!prefillLawFirm) return;
    createEnrollmentToken({
      tenantId,
      ...(form.email       ? { email:   form.email }       : {}),
      ...(form.firmName    ? { firm:    form.firmName }    : {}),
      ...(form.phone       ? { phone:   form.phone }       : {}),
      ...(form.contactFirstName.trim() ? { contactFirstName: form.contactFirstName.trim() } : {}),
      ...(form.contactLastName.trim()  ? { contactLastName:  form.contactLastName.trim() }  : {}),
    }).then(t => setEnrollToken(t)).catch(() => {});
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [state]); // form values are stable once state === 'success'

  // ── Address autocomplete ─────────────────────────────────────────────────
  const [addrSuggestions, setAddrSuggestions] = useState<Array<{ displayName: string; addressLine1: string; city: string; state: string; postalCode: string }>>([]);
  const [showAddrSugg,    setShowAddrSugg]    = useState(false);
  const addrDebounce = useRef<ReturnType<typeof setTimeout> | null>(null);

  const update = useCallback((field: keyof ReferralForm, value: string) => {
    setForm(prev => ({ ...prev, [field]: value }));
    const dateFields: (keyof ReferralForm)[] = ['patientDob', 'patientDateOfAccident'];
    if (dateFields.includes(field) && value && isValidIsoDate(value)) {
      if (!hasReasonableYear(value)) {
        setErrors(prev => ({ ...prev, [field]: 'Please enter a valid year (1900 or later).' }));
        return;
      }
      if (new Date(value) > new Date()) {
        const label = field === 'patientDob' ? 'Date of birth' : 'Date of accident';
        setErrors(prev => ({ ...prev, [field]: `${label} cannot be in the future.` }));
        return;
      }
    }
    setErrors(prev => { const n = { ...prev }; delete n[field]; return n; });
  }, []);

  const handleAddressInput = useCallback((value: string) => {
    update('patientAddress', value);
    setShowAddrSugg(false);
    if (addrDebounce.current) clearTimeout(addrDebounce.current);
    if (value.trim().length < 4) { setAddrSuggestions([]); return; }
    addrDebounce.current = setTimeout(async () => {
      try {
        const res = await fetch(`/api/geocode/address?q=${encodeURIComponent(value)}`);
        if (res.ok) {
          const data = await res.json() as Array<{ displayName: string; addressLine1: string; city: string; state: string; postalCode: string }>;
          setAddrSuggestions(data.slice(0, 5));
          setShowAddrSugg(data.length > 0);
        }
      } catch { /* ignore */ }
    }, 350);
  }, [update]);

  const applyAddrSuggestion = useCallback((s: { displayName: string; addressLine1: string; city: string; state: string; postalCode: string }) => {
    const full = [s.addressLine1 || s.displayName, s.city, s.state, s.postalCode].filter(Boolean).join(', ');
    update('patientAddress', full);
    setAddrSuggestions([]);
    setShowAddrSugg(false);
  }, [update]);

  const validate = useCallback((): Record<string, string> => {
    const errs: Record<string, string> = {};
    if (!form.patientFirstName.trim()) errs['patientFirstName'] = 'Patient first name is required.';
    if (!form.patientLastName.trim()) errs['patientLastName'] = 'Patient last name is required.';
    if (!form.patientPhone.trim()) errs['patientPhone'] = 'Patient phone is required.';
    else if (!isValidPhone(form.patientPhone)) errs['patientPhone'] = 'Enter a valid 10-digit phone number.';
    if (form.patientEmail.trim() && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(form.patientEmail.trim()))
      errs['patientEmail'] = 'Enter a valid email address.';
    if (form.lienCompanyEmail.trim() && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(form.lienCompanyEmail.trim()))
      errs['lienCompanyEmail'] = 'Enter a valid lien company email address.';
    if (!form.patientDob) errs['patientDob'] = 'Date of birth is required.';
    else if (!isValidIsoDate(form.patientDob)) errs['patientDob'] = 'Enter a valid date of birth.';
    else if (!hasReasonableYear(form.patientDob)) errs['patientDob'] = 'Please enter a valid year (1900 or later).';
    else if (new Date(form.patientDob) > new Date()) errs['patientDob'] = 'Date of birth cannot be in the future.';
    if (!form.patientDateOfAccident) errs['patientDateOfAccident'] = 'Date of accident is required.';
    else if (!isValidIsoDate(form.patientDateOfAccident)) errs['patientDateOfAccident'] = 'Enter a valid date of accident.';
    else if (!hasReasonableYear(form.patientDateOfAccident)) errs['patientDateOfAccident'] = 'Please enter a valid year (1900 or later).';
    else if (new Date(form.patientDateOfAccident) > new Date()) errs['patientDateOfAccident'] = 'Date of accident cannot be in the future.';
    if (form.phone.trim() && !isValidPhone(form.phone)) errs['phone'] = 'Phone number must be 10 digits.';
    if (!prefillLawFirm) {
      if (!form.firmName.trim()) errs['firmName'] = 'Firm name is required.';
      if (!form.email.trim()) errs['email'] = 'Email is required.';
      else if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(form.email.trim())) errs['email'] = 'Enter a valid email address.';
    } else {
      // prefillLawFirm is active — firmName is server-supplied and not editable.
      // Guard against an empty firmName arriving from the backend (e.g. stale session
      // before the identity service populates org_name), which would produce
      // senderName:"" and a server-side validation failure. Use a form-level error
      // key (_form) so the message surfaces as a banner — there is no contactName
      // input rendered in prefill mode for the user to correct.
      const senderName = [form.contactFirstName.trim(), form.contactLastName.trim()].filter(Boolean).join(' ') || form.firmName.trim();
      if (!senderName) errs['_form'] = 'Unable to submit: your firm name could not be loaded. Please refresh the page or sign out and sign back in.';
    }
    return errs;
  }, [form, prefillLawFirm]);

  // Validate then show confirmation modal (with pre-submit portal account check)
  const handleSubmit = useCallback(async (e: FormEvent) => {
    e.preventDefault();
    setErrMsg('');
    const errs = validate();
    if (Object.keys(errs).length > 0) { setErrors(errs); return; }
    setErrors({});

    // CC-PORTAL-ACCOUNT-CHECK: Check if the sender email already has an active
    // portal account on this tenant. Skip for authenticated users (already logged in).
    if (!prefillLawFirm && form.email.trim()) {
      setCheckingEmail(true);
      try {
        const res = await fetch(
          `/api/public/careconnect/api/public/referrer-status?email=${encodeURIComponent(form.email.trim())}`,
          { headers: { 'X-Tenant-Id': tenantId } },
        );
        if (res.ok) {
          const data = await res.json() as { hasPortalAccess: boolean; status: string };
          if (data.status === 'active_in_tenant') {
            setCheckingEmail(false);
            setState('account-exists');
            return;
          }
        }
      } catch {
        // Fail-open: proceed to confirm on any network error
      }
      setCheckingEmail(false);
    }

    setState('confirm');
  }, [validate, form.email, tenantId, prefillLawFirm]);

  // Called from confirmation modal — actually sends the referral
  const confirmAndSend = useCallback(async () => {
    if (prefillLawFirm && !referrerScopeSignature) {
      setErrMsg('Your CareConnect tenant selection could not be verified. Please return to Available Networks and retry.');
      setState('error');
      return;
    }

    setState('submitting');

    const submissionTargets = providers.map(p => ({
      fileKey: providerEntryId(p),
      facilityId: p.facilityId,
      payload: {
        providerId:             providerIdentityId(p),
        networkProviderId:      providerEntryId(p),
        senderFirstName:        form.contactFirstName.trim() || form.firmName.trim(),
        senderLastName:         form.contactFirstName.trim() ? (form.contactLastName.trim() || undefined) : undefined,
        senderEmail:            form.email.trim(),
        senderFirmName:         form.firmName.trim() || undefined,
        senderPhone:            stripPhone(form.phone) || undefined,
        patientFirstName:       form.patientFirstName.trim(),
        patientLastName:        form.patientLastName.trim(),
        patientPhone:           stripPhone(form.patientPhone),
        patientEmail:           form.patientEmail.trim() || undefined,
        patientDateOfBirth:     form.patientDob || undefined,
        patientDateOfAccident:  form.patientDateOfAccident || undefined,
        patientAddress:         form.patientAddress.trim() || undefined,
        serviceType:            form.serviceType || 'General Referral',
        urgency:                form.urgency,
        treatmentTypeId:        form.treatmentTypeId || undefined,
        referralAttributionId:  form.referralAttributionId || undefined,
        lienCompanyName:        form.lienCompanyName.trim() || undefined,
        lienCompanyEmail:       form.lienCompanyEmail.trim() || undefined,
        notes:                  form.notes.trim() || undefined,
      } satisfies PublicReferralRequest,
    }));

    // Authenticated users (prefillLawFirm present) submit through the auth endpoint —
    // tenant is resolved from the JWT, no host-based resolution needed.
    // Unauthenticated users use the anonymous public endpoint with X-Tenant-Id.
    const isAuthenticated = !!prefillLawFirm;

    try {
      const responses = await Promise.all(submissionTargets.map(async ({ payload, fileKey, facilityId }) => {
        let res: Response;
        if (isAuthenticated) {
          const authNotes = form.notes.trim() || undefined;

          const authBody = {
            tenantId,
            providerId:       payload.providerId,
            facilityId,
            networkProviderId: payload.networkProviderId,
            clientFirstName:  payload.patientFirstName,
            clientLastName:   payload.patientLastName,
            clientPhone:      payload.patientPhone,
            clientEmail:      payload.patientEmail ?? '',
            clientDob:        payload.patientDateOfBirth,
            requestedService: payload.serviceType || 'General Referral',
            urgency:          payload.urgency ?? 'Normal',
            treatmentTypeId:  form.treatmentTypeId || undefined,
            referralAttributionId: form.referralAttributionId || undefined,
            lienCompanyName:  form.lienCompanyName.trim() || undefined,
            lienCompanyEmail: form.lienCompanyEmail.trim() || undefined,
            dateOfAccident:   form.patientDateOfAccident || undefined,
            notes:            authNotes,
            referrerScopeSignature,
            referrerEmail:    payload.senderEmail,
            referrerFirmName: form.firmName.trim() || undefined,
            referrerPhone:    stripPhone(form.phone) || undefined,
            referrerName:     [form.contactFirstName.trim(), form.contactLastName.trim()].filter(Boolean).join(' ') || form.firmName.trim(),
          };
          res = await fetch('/api/careconnect/api/referrals', {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify(authBody),
          });
        } else {
          res = await fetch('/api/public/careconnect/api/public/referrals', {
            method:  'POST',
            headers: { 'Content-Type': 'application/json', 'X-Tenant-Id': tenantId },
            body:    JSON.stringify(payload),
          });
        }
        if (!res.ok) {
          // Parse the error body; fall back to a generic message if parsing fails.
          let body: unknown;
          try { body = await res.json(); } catch { throw new Error('Server error'); }
          throw body;
        }
        const body = await res.json() as unknown;
        return toCreatedReferralUploadTarget(body, payload.providerId, fileKey);
      }));

      await Promise.all(responses.map(async (r) => {
        const fileForProvider = providerFiles[r.fileKey] ?? null;
        if (!fileForProvider) return;
        const fd = new FormData();
        fd.append('file', fileForProvider);
        const uploadEndpoint = isAuthenticated
          ? `/api/careconnect/api/referrals/${r.referralId}/attachments/upload`
          : `/api/public/careconnect/api/public/referrals/${r.referralId}/attachments/upload`;
        const uploadHeaders: Record<string, string> = {};
        if (!isAuthenticated) uploadHeaders['X-Tenant-Id'] = tenantId;
        const uploadRes = await fetch(uploadEndpoint, {
          method:  'POST',
          headers: uploadHeaders,
          body:    fd,
        });
        if (!uploadRes.ok) {
          let body: unknown;
          try { body = await uploadRes.json(); } catch { throw new Error('Document upload failed after the referral was created.'); }
          throw new Error(
            `Referral created, but the document upload failed: ${extractApiErrorMessage(body, 'The uploaded document could not be attached.')}`
          );
        }
      }));

      setState('success');

      // CC-PORTAL-CHECK: fire-and-forget — check if the law firm email already has
      // an active portal account so the success screen shows the right CTA.
      // Skipped when the user is already authenticated (prefillLawFirm present) — CTA is hidden.
      if (form.email && !prefillLawFirm) {
        fetch(`/api/public/careconnect/api/public/referrer-status?email=${encodeURIComponent(form.email)}`, {
          headers: { 'X-Tenant-Id': tenantId },
        })
          .then(r => r.ok ? r.json() : null)
          .then((data: { hasPortalAccess: boolean } | null) => {
            if (data?.hasPortalAccess) setHasPortalAccess(true);
          })
          .catch(() => {});
      }
    } catch (err: unknown) {
      const apiErrors = err && typeof err === 'object' && 'errors' in err
        ? (err as { errors: Record<string, string> }).errors
        : {};
      // Extract the most useful message: prefer 'message', fall back to ProblemDetails
      // 'detail' or 'title', then a generic fallback.
      const msg = extractApiErrorMessage(err);
      if (Object.keys(apiErrors).length > 0) { setErrors(apiErrors); }
      setErrMsg(msg);
      setState('error');
    }
  }, [form, providers, tenantId, providerFiles, prefillLawFirm, referrerScopeSignature]);

  const hasProviders = providers.length > 0;

  return (
    <div className="w-1/3 min-w-[340px] max-w-[480px] flex-shrink-0 border-l border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 flex flex-col overflow-hidden shadow-sm">

      {/* Panel header */}
      <div className="flex-shrink-0 px-5 py-4 border-b border-gray-100 dark:border-gray-700 bg-white dark:bg-gray-900">
        <div className="flex items-center justify-between">
          <div>
            <h2 className="text-sm font-bold text-gray-900 dark:text-white">Send a Referral</h2>
            <p className="text-xs text-gray-400 mt-0.5">
              {hasProviders
                ? `${providers.length} provider${providers.length !== 1 ? 's' : ''} selected`
                : 'Select providers from the list'}
            </p>
          </div>
          {hasProviders && (
            <button
              onClick={() => { onClearSelection(); setState('form'); setErrors({}); setErrMsg(''); }}
              className="text-xs text-gray-400 hover:text-gray-600 dark:hover:text-gray-200 underline transition-colors"
            >
              Clear
            </button>
          )}
        </div>

        {/* Selected provider chips */}
        {hasProviders && (
          <div className="flex flex-wrap gap-1.5 mt-3">
            {providers.map(p => {
              const entryId = providerEntryId(p);
              return (
                <span key={entryId} className="inline-flex items-center gap-1 px-2 py-1 bg-blue-50 dark:bg-blue-900/30 text-blue-700 dark:text-blue-300 text-xs font-medium rounded-full border border-blue-200 dark:border-blue-700">
                  <i className="ri-hospital-line text-blue-500 dark:text-blue-400" />
                  {p.name.length > 22 ? p.name.slice(0, 22) + '…' : p.name}
                </span>
              );
            })}
          </div>
        )}
      </div>

      {/* Panel body */}
      <div className="flex-1 flex flex-col overflow-hidden">

        {/* Empty state */}
        {!hasProviders && (
          <div className="flex flex-col items-center justify-center h-full px-6 text-center">
            <div className="w-16 h-16 rounded-2xl bg-blue-50 dark:bg-blue-900/30 flex items-center justify-center mb-4">
              <i className="ri-send-plane-line text-blue-400 text-3xl" />
            </div>
            <p className="text-sm font-semibold text-gray-700 dark:text-gray-200 mb-2">No providers selected</p>
            <p className="text-sm text-gray-400 leading-relaxed max-w-xs">
              Browse the directory and click <strong className="text-gray-600 dark:text-gray-300">Select</strong> on one or more providers to send them a referral.
            </p>
            <div className="mt-6 w-full max-w-xs space-y-2 text-left">
              <div className="flex items-start gap-3 p-3 rounded-lg bg-gray-50 dark:bg-gray-800 border border-gray-100 dark:border-gray-700">
                <div className="w-6 h-6 rounded-full bg-indigo-100 dark:bg-indigo-900/40 flex items-center justify-center flex-shrink-0 mt-0.5">
                  <i className="ri-briefcase-line text-xs text-indigo-600 dark:text-indigo-400" />
                </div>
                <div>
                  <p className="text-xs font-semibold text-gray-700 dark:text-gray-200">Your firm info</p>
                  <p className="text-xs text-gray-400">Name and email of the referring party</p>
                </div>
              </div>
              <div className="flex items-start gap-3 p-3 rounded-lg bg-gray-50 dark:bg-gray-800 border border-gray-100 dark:border-gray-700">
                <div className="w-6 h-6 rounded-full bg-teal-100 dark:bg-teal-900/40 flex items-center justify-center flex-shrink-0 mt-0.5">
                  <i className="ri-user-heart-line text-xs text-teal-600 dark:text-teal-400" />
                </div>
                <div>
                  <p className="text-xs font-semibold text-gray-700 dark:text-gray-200">Patient details</p>
                  <p className="text-xs text-gray-400">Name and phone</p>
                </div>
              </div>
              <div className="flex items-start gap-3 p-3 rounded-lg bg-gray-50 dark:bg-gray-800 border border-gray-100 dark:border-gray-700">
                <div className="w-6 h-6 rounded-full bg-gray-200 dark:bg-gray-700 flex items-center justify-center flex-shrink-0 mt-0.5">
                  <i className="ri-hospital-line text-xs text-gray-600 dark:text-gray-300" />
                </div>
                <div>
                  <p className="text-xs font-semibold text-gray-700 dark:text-gray-200">Providers</p>
                  <p className="text-xs text-gray-400">Send to one or multiple providers at once</p>
                </div>
              </div>
            </div>
          </div>
        )}


        {/* Error */}
        {hasProviders && state === 'error' && (
          <div className="p-8 text-center space-y-4">
            <div className="mx-auto w-14 h-14 rounded-full bg-red-50 dark:bg-red-900/30 flex items-center justify-center">
              <i className="ri-error-warning-line text-red-500 text-3xl" />
            </div>
            <div>
              <p className="text-base font-semibold text-gray-900 dark:text-white">Submission failed</p>
              <p className="text-sm text-gray-500 dark:text-gray-400 mt-1">{errorMsg}</p>
            </div>
            <button
              onClick={() => setState('form')}
              className="px-5 py-2.5 text-sm font-semibold text-white bg-blue-600 rounded-lg hover:bg-blue-700 transition-colors"
            >
              Try again
            </button>
          </div>
        )}

        {/* Form */}
        {hasProviders && (state === 'form' || state === 'confirm' || state === 'submitting') && (
          <form onSubmit={handleSubmit} className="flex-1 flex flex-col min-h-0">
          <div className="flex-1 overflow-y-auto">

            {/* Law firm section — hidden when the user is a known authenticated referrer */}
            {prefillLawFirm ? (
              <>
              <div className="px-5 py-3 border-b border-gray-100 flex items-center gap-3 bg-indigo-50/60">
                <div className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-indigo-500">
                  <i className="ri-briefcase-line text-white text-sm" />
                </div>
                <div className="min-w-0">
                  <p className="text-xs font-semibold text-indigo-700 truncate">{prefillLawFirm.firmName}</p>
                  <p className="text-[11px] text-indigo-500 truncate">{prefillLawFirm.email}</p>
                </div>
              </div>
              {fieldErrors['_form'] && (
                <div className="mx-5 mt-2 px-3 py-2 rounded-md bg-red-50 border border-red-200 text-xs text-red-700">
                  {fieldErrors['_form']}
                </div>
              )}
              </>
            ) : (
              <SectionRow
                icon="ri-briefcase-line" avatarBg="bg-indigo-500"
                title="Law firm"
                subtitle="Who is sending the referral"
                hasError={!!(fieldErrors['firmName'] || fieldErrors['email'])}
              >
                <div className="px-5 pb-4 space-y-3">
                  <PanelField label="Firm name" required error={fieldErrors['firmName']}>
                    <input
                      type="text" required value={form.firmName}
                      placeholder="Enter firm name"
                      onChange={e => update('firmName', e.target.value)}
                      disabled={state === 'submitting'}
                      className={panelInputCls(!!fieldErrors['firmName'])}
                    />
                  </PanelField>
                  <div className="grid grid-cols-2 gap-3">
                    <PanelField label="Contact first name">
                      <input
                        type="text" value={form.contactFirstName}
                        placeholder="Enter first name"
                        onChange={e => update('contactFirstName', e.target.value)}
                        disabled={state === 'submitting'}
                        className={panelInputCls(false)}
                      />
                    </PanelField>
                    <PanelField label="Contact last name">
                      <input
                        type="text" value={form.contactLastName}
                        placeholder="Enter last name"
                        onChange={e => update('contactLastName', e.target.value)}
                        disabled={state === 'submitting'}
                        className={panelInputCls(false)}
                      />
                    </PanelField>
                  </div>
                  <PanelField label="Email" required error={fieldErrors['email']}>
                    <input
                      type="email" required value={form.email}
                      placeholder="Enter email address"
                      onChange={e => update('email', e.target.value)}
                      disabled={state === 'submitting'}
                      className={panelInputCls(!!fieldErrors['email'])}
                    />
                  </PanelField>
                  <PanelField label="Phone" error={hasInvalidPhone ? 'Phone number must be 10 digits.' : undefined}>
                    <input
                      type="tel" value={form.phone}
                      placeholder="Enter 10-digit phone number"
                      onChange={e => update('phone', formatPhoneInput(e.target.value))}
                      disabled={state === 'submitting'}
                      className={panelInputCls(hasInvalidPhone)}
                    />
                  </PanelField>
                </div>
              </SectionRow>
            )}

            {/* Patient section */}
            <SectionRow
              icon="ri-user-heart-line" avatarBg="bg-teal-500"
              title="Patient"
              subtitle="Who is being referred"
              hasError={!!(fieldErrors['patientFirstName'] || fieldErrors['patientLastName'] || fieldErrors['patientPhone'] || fieldErrors['patientDob'] || fieldErrors['patientDateOfAccident'] || fieldErrors['patientEmail'])}
            >
              <div className="px-5 pb-4 space-y-3">
                <div className="grid grid-cols-2 gap-3">
                  <PanelField label="Patient First name" required error={fieldErrors['patientFirstName']}>
                    <input
                      type="text" required value={form.patientFirstName}
                      placeholder="Enter patient first name"
                      onChange={e => update('patientFirstName', e.target.value)}
                      disabled={state === 'submitting'}
                      className={panelInputCls(!!fieldErrors['patientFirstName'])}
                    />
                  </PanelField>
                  <PanelField label="Patient Last name" required error={fieldErrors['patientLastName']}>
                    <input
                      type="text" required value={form.patientLastName}
                      placeholder="Enter patient last name"
                      onChange={e => update('patientLastName', e.target.value)}
                      disabled={state === 'submitting'}
                      className={panelInputCls(!!fieldErrors['patientLastName'])}
                    />
                  </PanelField>
                </div>
                <PanelField label="Patient phone" required error={hasInvalidPatientPhone ? 'Phone number must be 10 digits.' : fieldErrors['patientPhone']}>
                  <input
                    type="tel" required value={form.patientPhone}
                    placeholder="Enter 10-digit phone number"
                    onChange={e => update('patientPhone', formatPhoneInput(e.target.value))}
                    disabled={state === 'submitting'}
                    className={panelInputCls(hasInvalidPatientPhone || !!fieldErrors['patientPhone'])}
                  />
                </PanelField>
                <PanelField label="Patient email" hint="optional" error={fieldErrors['patientEmail']}>
                  <input
                    type="email" value={form.patientEmail}
                    placeholder="Enter email address"
                    onChange={e => update('patientEmail', e.target.value)}
                    disabled={state === 'submitting'}
                    className={panelInputCls(!!fieldErrors['patientEmail'])}
                  />
                </PanelField>
                {/* Address with autofill */}
                <PanelField label="Patient address" hint="optional" error={fieldErrors['patientAddress']}>
                  <div className="relative">
                    <input
                      type="text" value={form.patientAddress}
                      placeholder="Start typing an address…"
                      autoComplete="off"
                      onChange={e => handleAddressInput(e.target.value)}
                      onBlur={() => setTimeout(() => setShowAddrSugg(false), 150)}
                      disabled={state === 'submitting'}
                      className={panelInputCls(!!fieldErrors['patientAddress'])}
                    />
                    {showAddrSugg && addrSuggestions.length > 0 && (
                      <ul className="absolute z-50 left-0 right-0 mt-1 bg-white dark:bg-gray-800 border border-gray-200 dark:border-gray-600 rounded-lg shadow-lg max-h-44 overflow-y-auto text-xs">
                        {addrSuggestions.map((s, i) => (
                          <li
                            key={i}
                            onMouseDown={() => applyAddrSuggestion(s)}
                            className="px-3 py-2 cursor-pointer hover:bg-blue-50 dark:hover:bg-blue-900/30 text-gray-900 dark:text-white truncate"
                          >
                            {s.displayName}
                          </li>
                        ))}
                      </ul>
                    )}
                  </div>
                </PanelField>
                <div className="grid grid-cols-2 gap-3">
                  <PanelField label="Date of birth" required error={fieldErrors['patientDob']}>
                    <input
                      type="date" required value={form.patientDob}
                      min="1900-01-01" max={new Date().toISOString().split('T')[0]}
                      onChange={e => update('patientDob', e.target.value)}
                      disabled={state === 'submitting'}
                      className={panelInputCls(!!fieldErrors['patientDob'])}
                    />
                  </PanelField>
                  <PanelField label="Date of accident" required error={fieldErrors['patientDateOfAccident']}>
                    <input
                      type="date" required value={form.patientDateOfAccident}
                      min="1900-01-01" max={new Date().toISOString().split('T')[0]}
                      onChange={e => update('patientDateOfAccident', e.target.value)}
                      disabled={state === 'submitting'}
                      className={panelInputCls(!!fieldErrors['patientDateOfAccident'])}
                    />
                  </PanelField>
                </div>
                <PanelField label="Urgency">
                  <select
                    value={form.urgency}
                    onChange={e => update('urgency', e.target.value as ReferralUrgencyValue)}
                    disabled={state === 'submitting'}
                    className={panelInputCls(false)}
                  >
                    {URGENCY_OPTIONS.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
                  </select>
                </PanelField>
                <PanelField label="Type of service">
                  <select
                    value={form.serviceType}
                    onChange={e => update('serviceType', e.target.value)}
                    disabled={state === 'submitting'}
                    className={panelInputCls(false)}
                  >
                    {SERVICE_TYPES.map(s => <option key={s} value={s}>{s}</option>)}
                  </select>
                </PanelField>
                <PanelField label="Type of treatment" hint="optional">
                  <select
                    value={form.treatmentTypeId}
                    onChange={e => update('treatmentTypeId', e.target.value)}
                    disabled={state === 'submitting'}
                    className={panelInputCls(false)}
                  >
                    <option value="">None</option>
                    {treatmentTypes.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}
                  </select>
                </PanelField>
                <PanelField label="Referral Attribution" hint="optional">
                  <select
                    value={form.referralAttributionId}
                    onChange={e => update('referralAttributionId', e.target.value)}
                    disabled={state === 'submitting'}
                    className={panelInputCls(false)}
                  >
                    <option value="">None</option>
                    {attributionOptions.map(a => (
                      <option key={a.id} value={a.id}>
                        {a.firstName} {a.lastName}
                      </option>
                    ))}
                  </select>
                </PanelField>
                <PanelField label="Notes" hint="optional">
                  <textarea
                    rows={3} value={form.notes}
                    placeholder="Background, prior treatment…"
                    onChange={e => update('notes', e.target.value)}
                    disabled={state === 'submitting'}
                    className={panelInputCls(false) + ' resize-none'}
                  />
                </PanelField>
                <PanelField label="Lien company name" hint="optional">
                  <input
                    type="text" value={form.lienCompanyName}
                    placeholder="Lien company"
                    onChange={e => update('lienCompanyName', e.target.value)}
                    disabled={state === 'submitting'}
                    className={panelInputCls(false)}
                  />
                </PanelField>
                <PanelField label="Lien company email" hint="optional">
                  <input
                    type="email" value={form.lienCompanyEmail}
                    placeholder="Email address"
                    onChange={e => update('lienCompanyEmail', e.target.value)}
                    disabled={state === 'submitting'}
                    className={panelInputCls(false)}
                  />
                </PanelField>
              </div>
            </SectionRow>

            {/* Providers section */}
            <SectionRow
              icon="ri-hospital-line" avatarBg="bg-gray-700"
              title="Providers"
              subtitle="Who will treat the patient"
              badge={providers.length}
            >
              <div className="px-5 pb-4 space-y-3">
                {providers.map(p => {
                  const entryId = providerEntryId(p);
                  const file = providerFiles[entryId] ?? null;
                  return (
                    <div key={entryId} className="rounded-xl border border-gray-100 dark:border-gray-700 bg-gray-50 dark:bg-gray-800 p-3 space-y-2">
                      <div className="flex items-center gap-2">
                        <div className="w-7 h-7 rounded-full bg-blue-100 dark:bg-blue-900/40 flex items-center justify-center flex-shrink-0">
                          <i className="ri-hospital-line text-xs text-blue-600 dark:text-blue-400" />
                        </div>
                        <div className="min-w-0 flex-1">
                          <p className="text-sm font-medium text-gray-800 dark:text-gray-100 truncate">{p.name}</p>
                          <p className="text-xs text-gray-400 truncate">{p.city}, {p.state}</p>
                        </div>
                      </div>
                      {file ? (
                        <div className="flex items-center gap-2 px-2 py-1.5 rounded-lg bg-blue-50 dark:bg-blue-900/30 border border-blue-200 dark:border-blue-700">
                          <i className="ri-file-line text-blue-600 dark:text-blue-400 text-sm flex-shrink-0" />
                          <span className="text-xs text-blue-700 dark:text-blue-300 truncate flex-1">{file.name}</span>
                          <button
                            type="button"
                            onClick={() => setProviderFiles(prev => ({ ...prev, [entryId]: null }))}
                            disabled={state === 'submitting'}
                            className="text-blue-400 hover:text-blue-600 dark:hover:text-blue-300 flex-shrink-0"
                          >
                            <i className="ri-close-line text-sm" />
                          </button>
                        </div>
                      ) : (
                        <label className={`flex items-center gap-2 px-2 py-1.5 rounded-lg border border-dashed cursor-pointer transition-colors ${state === 'submitting' ? 'opacity-50 pointer-events-none' : 'border-gray-300 dark:border-gray-600 hover:border-blue-400 dark:hover:border-blue-500 hover:bg-blue-50 dark:hover:bg-blue-900/20'}`}>
                          <i className="ri-upload-2-line text-gray-400 dark:text-gray-500 text-sm" />
                          <span className="text-xs text-gray-500 dark:text-gray-400">Attach document</span>
                          <input
                            type="file"
                            className="hidden"
                            accept=".pdf,.doc,.docx,.jpg,.jpeg,.png,.gif,.webp,.txt,.csv,.xls,.xlsx"
                            disabled={state === 'submitting'}
                            onChange={e => {
                              const f = e.target.files?.[0] ?? null;
                              if (f) setProviderFiles(prev => ({ ...prev, [entryId]: f }));
                              e.target.value = '';
                            }}
                          />
                        </label>
                      )}
                    </div>
                  );
                })}
              </div>
            </SectionRow>

            {/* Validation summary */}
            {Object.keys(fieldErrors).length > 0 && state !== 'submitting' && (() => {
              const hasPatientErr = !!(fieldErrors['patientFirstName'] || fieldErrors['patientLastName'] || fieldErrors['patientPhone'] || fieldErrors['patientDob'] || fieldErrors['patientDateOfAccident'] || fieldErrors['patientEmail']);
              const hasFirmErr    = !!(fieldErrors['firmName']    || fieldErrors['email']);
              const sections = [hasPatientErr && 'Patient', hasFirmErr && 'Law firm'].filter(Boolean).join(' and ');
              return (
                <div className="mx-5 mb-3 px-3 py-2 rounded-lg bg-red-50 dark:bg-red-900/20 border border-red-200 dark:border-red-700/50 flex items-start gap-2">
                  <i className="ri-error-warning-line text-red-500 text-sm mt-0.5 flex-shrink-0" />
                  <p className="text-xs text-red-700 dark:text-red-300">
                    Please complete required fields in the <strong>{sections}</strong> section{sections.includes('and') ? 's' : ''}.
                  </p>
                </div>
              );
            })()}
          </div>

          {/* Submit — pinned outside scroll area */}
          <div className="flex-shrink-0 px-5 py-4 border-t border-gray-100 dark:border-gray-700 bg-white dark:bg-gray-900">
            <button
              type="submit"
              disabled={state === 'submitting' || checkingEmail || !canSubmit}
              className="w-full py-2.5 text-sm font-semibold text-white bg-blue-600 rounded-xl hover:bg-blue-700 disabled:opacity-60 disabled:cursor-not-allowed transition-colors flex items-center justify-center gap-2"
            >
              {checkingEmail ? (
                <><span className="w-4 h-4 border-2 border-white/30 border-t-white rounded-full animate-spin" />Checking…</>
              ) : state === 'submitting' ? (
                <><span className="w-4 h-4 border-2 border-white/30 border-t-white rounded-full animate-spin" />Sending…</>
              ) : (
                <><i className="ri-send-plane-line" />Send Referral{providers.length > 1 ? `s (${providers.length})` : ''}</>
              )}
            </button>
          </div>
          </form>
        )}
      </div>

      {/* Confirmation / success modal overlay */}
      {(state === 'confirm' || state === 'submitting' || state === 'success') && (
        <ReferralConfirmModal
          form={form}
          providers={providers}
          providerFiles={providerFiles}
          state={state}
          tenantId={tenantId}
          loginUrl={loginUrl}
          hasPortalAccess={hasPortalAccess}
          prefillLawFirm={prefillLawFirm}
          enrollToken={enrollToken}
          onConfirm={confirmAndSend}
          onBack={() => setState('form')}
          onClose={() => window.location.reload()}
        />
      )}

      {/* Account exists modal — blocks referral when email is already active on this tenant */}
      {state === 'account-exists' && (
        <AccountExistsModal
          email={form.email}
          loginUrl={loginUrl}
          onCancel={() => setState('form')}
        />
      )}
    </div>
  );
}

// ── Referral confirmation modal ────────────────────────────────────────────────

function fmtDate(iso: string): string {
  if (!iso) return '—';
  const [y, m, d] = iso.split('-').map(Number);
  return new Date(y, m - 1, d).toLocaleDateString('en-US', { year: 'numeric', month: 'long', day: 'numeric' });
}

function ConfirmRow({ label, value }: { label: string; value?: string }) {
  if (!value) return null;
  return (
    <div className="flex gap-3 text-xs">
      <span className="w-36 flex-shrink-0 text-gray-400 font-medium">{label}</span>
      <span className="text-gray-800 dark:text-gray-100 font-medium break-words">{value}</span>
    </div>
  );
}

function ReferralConfirmModal({
  form, providers, providerFiles, state, tenantId, loginUrl, hasPortalAccess, prefillLawFirm, enrollToken, onConfirm, onBack, onClose,
}: {
  form:             ReferralForm;
  providers:        PublicProviderItem[];
  providerFiles:    Record<string, File | null>;
  state:            PanelState;
  tenantId:         string;
  loginUrl:         string;
  hasPortalAccess:  boolean;
  prefillLawFirm?:  PrefillLawFirm;
  enrollToken:      string | null;
  onConfirm:        () => void;
  onBack:           () => void;
  onClose:          () => void;
}) {
  const isSending = state === 'submitting';
  const isSent    = state === 'success';

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50 backdrop-blur-sm">
      <div className="relative w-full max-w-md bg-white dark:bg-gray-800 rounded-2xl shadow-2xl flex flex-col max-h-[90vh]">

        {/* ── SENDING screen ─────────────────────────────────────────────── */}
        {isSending && (
          <div className="flex flex-col items-center justify-center py-16 px-8 gap-5">
            <div className="w-14 h-14 rounded-full bg-blue-50 dark:bg-blue-900/30 flex items-center justify-center">
              <span className="w-7 h-7 border-[3px] border-blue-200 dark:border-blue-700 border-t-blue-600 rounded-full animate-spin" />
            </div>
            <div className="text-center">
              <p className="text-base font-semibold text-gray-900 dark:text-white">Sending referral…</p>
              <p className="text-xs text-gray-400 mt-1">Please wait while we notify the provider{providers.length !== 1 ? 's' : ''}.</p>
            </div>
          </div>
        )}

        {/* ── SENT / SUCCESS screen ──────────────────────────────────────── */}
        {isSent && (
          <>
            <div className="flex-1 overflow-y-auto">
              {/* Top success banner */}
              <div className="px-6 pt-8 pb-6 text-center border-b border-gray-100 dark:border-gray-700">
                <div className="mx-auto w-16 h-16 rounded-full bg-green-50 dark:bg-green-900/30 flex items-center justify-center mb-4">
                  <i className="ri-checkbox-circle-fill text-green-500 text-4xl" />
                </div>
                <h2 className="text-lg font-bold text-gray-900 dark:text-white">Referral Sent!</h2>
                <p className="text-sm text-gray-500 dark:text-gray-400 mt-1.5 leading-relaxed">
                  Successfully sent to{' '}
                  <strong className="text-gray-700 dark:text-gray-200">
                    {providers.length} provider{providers.length !== 1 ? 's' : ''}
                  </strong>.
                </p>
              </div>

              {/* Email copy notice */}
              <div className="px-6 py-5 border-b border-gray-100 dark:border-gray-700">
                <div className="flex gap-3">
                  <div className="w-8 h-8 rounded-full bg-blue-50 dark:bg-blue-900/30 flex items-center justify-center flex-shrink-0 mt-0.5">
                    <i className="ri-mail-check-line text-blue-500 text-sm" />
                  </div>
                  <div>
                    <p className="text-sm font-semibold text-gray-800 dark:text-gray-100">Check your inbox</p>
                    <p className="text-xs text-gray-500 dark:text-gray-400 mt-1 leading-relaxed">
                      A copy of this referral has been sent to{' '}
                      <strong className="text-gray-700 dark:text-gray-200">{form.email}</strong>. Use the link in that
                      email to track the referral status at any time.
                    </p>
                  </div>
                </div>
              </div>

              {/* Account CTA — login if already registered, activate if not */}
              {/* Hidden when the user is already authenticated (prefillLawFirm present) */}
              {!prefillLawFirm && (
                <div className="px-6 py-5">
                {hasPortalAccess ? (
                  <div className="rounded-xl bg-gradient-to-br from-green-50 to-emerald-50 dark:from-green-900/20 dark:to-emerald-900/20 border border-green-100 dark:border-green-700/50 p-4">
                    <div className="flex gap-3 items-start">
                      <div className="w-8 h-8 rounded-full bg-green-100 dark:bg-green-900/40 flex items-center justify-center flex-shrink-0 mt-0.5">
                        <i className="ri-shield-check-line text-green-600 dark:text-green-400 text-sm" />
                      </div>
                      <div className="flex-1">
                        <p className="text-sm font-bold text-green-900 dark:text-green-200">You already have portal access</p>
                        <p className="text-xs text-green-700 dark:text-green-300 mt-1 leading-relaxed">
                          Log in to CareConnect to view this referral, track responses, and manage
                          all your cases in one place.
                        </p>
                        <a
                          href={loginUrl}
                          className="inline-flex items-center gap-1.5 mt-3 px-4 py-2 text-xs font-semibold text-white bg-green-600 rounded-lg hover:bg-green-700 transition-colors"
                        >
                          <i className="ri-login-circle-line" />
                          Login to CareConnect
                        </a>
                      </div>
                    </div>
                  </div>
                ) : (
                  <div className="rounded-xl bg-gradient-to-br from-indigo-50 to-blue-50 dark:from-indigo-900/20 dark:to-blue-900/20 border border-indigo-100 dark:border-indigo-700/50 p-4">
                    <div className="flex gap-3 items-start">
                      <div className="w-8 h-8 rounded-full bg-indigo-100 dark:bg-indigo-900/40 flex items-center justify-center flex-shrink-0 mt-0.5">
                        <i className="ri-rocket-line text-indigo-600 dark:text-indigo-400 text-sm" />
                      </div>
                      <div className="flex-1">
                        <p className="text-sm font-bold text-indigo-900 dark:text-indigo-200">Activate your free account</p>
                        <p className="text-xs text-indigo-700 dark:text-indigo-300 mt-1 leading-relaxed">
                          Get a full dashboard to track all your referrals, view responses, and manage
                          your cases in one place — completely free.
                        </p>
                        <a
                          href={enrollToken ? `/enroll?token=${enrollToken}` : '#'}
                          onClick={!enrollToken ? (e: React.MouseEvent) => e.preventDefault() : undefined}
                          className="inline-flex items-center gap-1.5 mt-3 px-4 py-2 text-xs font-semibold text-white bg-indigo-600 rounded-lg hover:bg-indigo-700 transition-colors"
                        >
                          <i className="ri-user-add-line" />
                          Get free access
                        </a>
                      </div>
                    </div>
                  </div>
                )}
              </div>
              )}
            </div>

            {/* Footer */}
            <div className="flex-shrink-0 px-6 py-4 border-t border-gray-100 dark:border-gray-700">
              <button
                type="button"
                onClick={onClose}
                className="w-full py-2.5 text-sm font-semibold text-gray-700 dark:text-gray-200 bg-gray-100 dark:bg-gray-700 rounded-xl hover:bg-gray-200 dark:hover:bg-gray-600 transition-colors"
              >
                Done
              </button>
            </div>
          </>
        )}

        {/* ── REVIEW screen (default) ───────────────────────────────────── */}
        {!isSending && !isSent && (
          <>
            {/* Modal header */}
            <div className="flex-shrink-0 px-6 pt-6 pb-4 border-b border-gray-100 dark:border-gray-700">
              <div className="flex items-center gap-3">
                <div className="w-9 h-9 rounded-full bg-blue-600 flex items-center justify-center flex-shrink-0">
                  <i className="ri-send-plane-line text-white text-base" />
                </div>
                <div>
                  <h2 className="text-base font-bold text-gray-900 dark:text-white">Review &amp; Confirm</h2>
                  <p className="text-xs text-gray-400">
                    Sending to {providers.length} provider{providers.length !== 1 ? 's' : ''}
                  </p>
                </div>
              </div>
            </div>

            {/* Scrollable details */}
            <div className="flex-1 overflow-y-auto px-6 py-4 space-y-5">

              {/* Law firm */}
              {!prefillLawFirm && (
                <div>
                  <p className="text-[10px] font-bold uppercase tracking-widest text-indigo-500 mb-2 flex items-center gap-1.5">
                    <i className="ri-briefcase-line" /> Law Firm
                  </p>
                  <div className="space-y-1.5 pl-1">
                    <ConfirmRow label="Firm"    value={form.firmName}    />
                    <ConfirmRow label="Contact" value={`${form.contactFirstName} ${form.contactLastName}`.trim()} />
                    <ConfirmRow label="Email"        value={form.email}       />
                    <ConfirmRow label="Phone"        value={form.phone}       />
                  </div>
                </div>
              )}

              {/* Patient */}
              <div>
                <p className="text-[10px] font-bold uppercase tracking-widest text-teal-500 mb-2 flex items-center gap-1.5">
                  <i className="ri-user-heart-line" /> Patient
                </p>
                <div className="space-y-1.5 pl-1">
                  <ConfirmRow label="Name"             value={`${form.patientFirstName} ${form.patientLastName}`.trim()} />
                  <ConfirmRow label="Phone"            value={form.patientPhone}                    />
                  <ConfirmRow label="Email"            value={form.patientEmail}                    />
                  <ConfirmRow label="Date of birth"    value={fmtDate(form.patientDob)}             />
                  <ConfirmRow label="Date of accident" value={fmtDate(form.patientDateOfAccident)}  />
                  <ConfirmRow label="Address"          value={form.patientAddress}                  />
                </div>
              </div>

              {/* Notes */}
              {form.notes.trim() && (
                <div>
                  <p className="text-[10px] font-bold uppercase tracking-widest text-gray-400 mb-2 flex items-center gap-1.5">
                    <i className="ri-file-text-line" /> Notes
                  </p>
                  <p className="text-xs text-gray-700 dark:text-gray-300 pl-1 leading-relaxed whitespace-pre-wrap">{form.notes.trim()}</p>
                </div>
              )}

              {/* Providers */}
              <div>
                <p className="text-[10px] font-bold uppercase tracking-widest text-gray-500 dark:text-gray-400 mb-2 flex items-center gap-1.5">
                  <i className="ri-hospital-line" /> Providers
                </p>
                <div className="space-y-3">
                  {providers.map(p => {
                    const entryId = providerEntryId(p);
                    const file = providerFiles[entryId];
                    const identity = getProviderIdentity(p);
                    const facilityName = p.facilityName?.trim() ?? '';
                    const showFacilityName =
                      facilityName.length > 0 &&
                      facilityName.toLowerCase() !== identity.primary.toLowerCase() &&
                      facilityName.toLowerCase() !== (identity.secondary ?? '').toLowerCase();
                    const location = [
                      showFacilityName ? facilityName : null,
                      [p.city, p.state].filter(Boolean).join(', '),
                    ].filter(Boolean).join(' · ');

                    return (
                      <div key={entryId} className="space-y-1.5 pl-1">
                        <ConfirmRow label="Provider" value={identity.primary} />
                        {identity.secondary && <ConfirmRow label="Contact"  value={identity.secondary} />}
                        <ConfirmRow label="Location" value={location} />
                        <ConfirmRow label="Email"     value={p.email ?? undefined} />
                        <ConfirmRow label="Phone"     value={p.phone} />
                        <ConfirmRow label="Attachment" value={file?.name} />
                      </div>
                    );
                  })}
                </div>
              </div>
            </div>

            {/* Footer actions */}
            <div className="flex-shrink-0 px-6 py-4 border-t border-gray-100 dark:border-gray-700 flex gap-3">
              <button
                type="button"
                onClick={onBack}
                className="flex-1 py-2.5 text-sm font-semibold text-gray-700 dark:text-gray-200 bg-gray-100 dark:bg-gray-700 rounded-xl hover:bg-gray-200 dark:hover:bg-gray-600 transition-colors"
              >
                Go Back
              </button>
              <button
                type="button"
                onClick={onConfirm}
                className="flex-1 py-2.5 text-sm font-semibold text-white bg-blue-600 rounded-xl hover:bg-blue-700 transition-colors flex items-center justify-center gap-2"
              >
                <i className="ri-send-plane-line" />
                Confirm &amp; Send
              </button>
            </div>
          </>
        )}

      </div>
    </div>
  );
}

// ── Account exists modal ─────────────────────────────────────────────────────

function AccountExistsModal({
  email, loginUrl, onCancel,
}: {
  email:    string;
  loginUrl: string;
  onCancel: () => void;
}) {
  const modalRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    modalRef.current?.focus();
  }, []);

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50 backdrop-blur-sm">
      <div
        ref={modalRef}
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="account-exists-title"
        aria-describedby="account-exists-desc"
        tabIndex={-1}
        className="relative w-full max-w-md bg-white dark:bg-gray-800 rounded-2xl shadow-2xl focus:outline-none"
      >
        <div className="px-6 pt-8 pb-6 text-center">
          <div className="mx-auto w-14 h-14 rounded-full bg-amber-50 dark:bg-amber-900/30 flex items-center justify-center mb-4">
            <i className="ri-shield-check-line text-amber-500 text-3xl" />
          </div>
          <h2 id="account-exists-title" className="text-base font-bold text-gray-900 dark:text-white">
            Account Already Exists
          </h2>
          <p id="account-exists-desc" className="text-sm text-gray-500 dark:text-gray-400 mt-2 leading-relaxed">
            The email <strong className="text-gray-700 dark:text-gray-200">{email}</strong> is
            already linked to an active CareConnect account for this network.
          </p>
          <p className="text-xs text-gray-400 dark:text-gray-500 mt-2">
            Please log in to submit referrals through your portal dashboard.
          </p>
        </div>

        <div className="px-6 pb-6 flex gap-3">
          <button
            type="button"
            onClick={onCancel}
            className="flex-1 py-2.5 text-sm font-semibold text-gray-700 dark:text-gray-200 bg-gray-100 dark:bg-gray-700 rounded-xl hover:bg-gray-200 dark:hover:bg-gray-600 transition-colors"
          >
            Cancel
          </button>
          <a
            href={loginUrl}
            className="flex-1 py-2.5 text-sm font-semibold text-white bg-blue-600 rounded-xl hover:bg-blue-700 transition-colors flex items-center justify-center gap-2"
          >
            Login
            <i className="ri-arrow-right-line text-sm" />
          </a>
        </div>
      </div>
    </div>
  );
}

// ── Section row ───────────────────────────────────────────────────────────────

function SectionRow({
  icon, avatarBg, title, subtitle, badge, hasError, children,
}: {
  icon:      string;
  avatarBg:  string;
  title:     string;
  subtitle:  string;
  badge?:    number;
  hasError?: boolean;
  children:  ReactNode;
}) {
  return (
    <div className="border-b border-gray-100 dark:border-gray-700">
      <div className="flex items-center gap-3 px-5 py-3 bg-gray-50 dark:bg-gray-800 border-b border-gray-100 dark:border-gray-700">
        <div className={`relative w-7 h-7 rounded-full ${avatarBg} flex items-center justify-center flex-shrink-0`}>
          <i className={`${icon} text-sm text-white`} />
          {hasError && (
            <span className="absolute -top-0.5 -right-0.5 w-3 h-3 rounded-full bg-red-500 border-2 border-white dark:border-gray-800" />
          )}
        </div>
        <div className="flex-1 min-w-0">
          <p className={`text-xs font-semibold uppercase tracking-wide ${hasError ? 'text-red-600 dark:text-red-400' : 'text-gray-500 dark:text-gray-400'}`}>{title}</p>
          <p className="text-xs text-gray-400">{subtitle}</p>
        </div>
        {badge !== undefined && (
          <span className="w-5 h-5 rounded-full bg-gray-700 dark:bg-gray-600 text-white text-xs font-bold flex items-center justify-center flex-shrink-0">
            {badge}
          </span>
        )}
      </div>
      {children}
    </div>
  );
}

// ── Panel field helpers ───────────────────────────────────────────────────────

function PanelField({
  label, hint, required, error, children,
}: {
  label: string; hint?: string; required?: boolean; error?: string; children: ReactNode;
}) {
  return (
    <div>
      <label className="block text-xs font-medium text-gray-600 dark:text-gray-300 mb-1.5">
        {label}
        {required && <span className="text-red-500 ml-0.5">*</span>}
        {hint && <span className="ml-1 text-gray-400 font-normal">({hint})</span>}
      </label>
      {children}
      {error && <p className="mt-1 text-xs text-red-600">{error}</p>}
    </div>
  );
}

function panelInputCls(hasError: boolean) {
  return [
    'w-full rounded-lg border px-3 py-2 text-sm focus:outline-none focus:ring-1 transition-colors',
    'bg-white dark:bg-gray-800 text-gray-900 dark:text-white placeholder-gray-400 dark:placeholder-gray-500',
    hasError
      ? 'border-red-300 dark:border-red-600 focus:border-red-400 dark:focus:border-red-500 focus:ring-red-100 dark:focus:ring-red-900/30'
      : 'border-gray-200 dark:border-gray-600 focus:border-blue-400 dark:focus:border-blue-500 focus:ring-blue-100 dark:focus:ring-blue-900/30',
  ].join(' ');
}

export type { PublicNetworkViewProps };
