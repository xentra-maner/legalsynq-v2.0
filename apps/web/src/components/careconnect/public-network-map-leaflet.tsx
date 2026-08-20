'use client';

import 'leaflet/dist/leaflet.css';
import { useEffect, useRef } from 'react';
import type { PublicProviderMarker } from '@/lib/public-network-api';

interface NumberedMarker extends PublicProviderMarker {
  index: number;
  phone?: string | null;
  addressLine1?: string | null;
  postalCode?: string | null;
}

interface SearchLocationMarker {
  latitude:  number;
  longitude: number;
  label:     string;
}

interface PublicNetworkMapProps {
  markers:           NumberedMarker[];
  selectedId:        string | null;
  zoomToId?:         string | null;
  onZoomed?:         () => void;
  searchLocation?:   SearchLocationMarker | null;
  hideSearchMarker?: boolean;
  onSelect:          (id: string) => void;
  onRequestReferral: (m: PublicProviderMarker) => void;
  requestReferralLabel?: string;
}

type L = typeof import('leaflet');

const US_CENTER: [number, number] = [39.5, -98.35];
const MILES_TO_METERS = 1609.34;

/** Escapes HTML special characters to prevent XSS when injecting provider data into popup innerHTML. */
function esc(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
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

function makePinHtml(index: number, accepting: boolean, selected: boolean): string {
  const bg   = selected ? '#1d4ed8' : accepting ? '#dc2626' : '#6b7280';
  const size = selected ? 34 : 28;
  const font = selected ? 13 : 11;
  const ring = selected ? 'box-shadow:0 0 0 3px #bfdbfe;' : '';
  return `<div style="width:${size}px;height:${size}px;background:${bg};border-radius:50%;display:flex;align-items:center;justify-content:center;color:#fff;font-weight:700;font-size:${font}px;font-family:system-ui,sans-serif;border:2px solid #fff;${ring}box-shadow:0 2px 6px rgba(0,0,0,.35);transition:all .15s;">${index}</div>`;
}

function makeSearchPinHtml(): string {
  return '<div style="width:30px;height:30px;background:#2563eb;border-radius:50% 50% 50% 0;transform:rotate(-45deg);border:2px solid #fff;box-shadow:0 2px 8px rgba(0,0,0,.35);display:flex;align-items:center;justify-content:center"><div style="width:9px;height:9px;background:#fff;border-radius:50%"></div></div>';
}

function buildPopupEl(m: NumberedMarker, onReferral: (m: NumberedMarker) => void, requestReferralLabel: string): HTMLDivElement {
  const el = document.createElement('div');
  const identity = getProviderIdentity(m);
  const facilityName = m.facilityName?.trim() ?? '';
  const showFacilityName =
    facilityName.length > 0 &&
    facilityName.toLowerCase() !== identity.primary.toLowerCase() &&
    facilityName.toLowerCase() !== (identity.secondary ?? '').toLowerCase();
  el.style.fontFamily = 'system-ui,sans-serif';
  el.style.minWidth   = '220px';
  el.innerHTML = `
    <div style="display:flex;align-items:center;gap:8px;margin-bottom:4px">
      <span style="width:22px;height:22px;border-radius:50%;background:${m.acceptingReferrals ? '#dc2626' : '#6b7280'};color:#fff;display:flex;align-items:center;justify-content:center;font-size:11px;font-weight:700;flex-shrink:0">${m.index}</span>
      <p style="font-weight:700;font-size:14px;color:#111827;margin:0">${esc(identity.primary)}</p>
    </div>
    ${identity.secondary ? `<p style="font-size:12px;color:#6b7280;margin:0 0 4px">${esc(identity.secondary)}</p>` : ''}
    ${showFacilityName ? `<p style="font-size:12px;color:#6b7280;margin:0 0 4px">${esc(facilityName)}</p>` : ''}
    <p style="font-size:12px;color:#9ca3af;margin:0 0 8px">${m.isMobile
      ? `Mobile · ${[m.serviceAreaLabel, `${m.city}, ${m.state}`].filter((s): s is string => Boolean(s)).map(esc).join(' · ')}${m.serviceRadiusMiles ? ` · ${m.serviceRadiusMiles}mi radius` : ''}`
      : `${esc(m.city)}, ${esc(m.state)}`}</p>
    ${m.phone ? `<p style="font-size:12px;color:#6b7280;margin:0 0 8px">${esc(m.phone)}</p>` : ''}
    ${typeof m.distanceMiles === 'number' ? `<p style="font-size:12px;color:#2563eb;margin:0 0 8px;font-weight:600">${m.distanceMiles.toFixed(1)} mi away</p>` : ''}
    ${(m.specialties ?? []).length > 0 ? `<p style="font-size:11px;color:#1d4ed8;margin:0 0 8px">${esc(m.specialties.map(s => s.name).join(', '))}</p>` : ''}
    ${m.acceptingReferrals
      ? `<span style="font-size:11px;color:#15803d;background:#f0fdf4;border:1px solid #bbf7d0;border-radius:9999px;padding:2px 8px;display:inline-block;margin-bottom:10px">Accepting referrals</span>`
      : `<span style="font-size:11px;color:#6b7280;background:#f9fafb;border:1px solid #e5e7eb;border-radius:9999px;padding:2px 8px;display:inline-block;margin-bottom:10px">Not accepting referrals</span>`
    }
    ${m.acceptingReferrals ? `<button style="font-size:12px;color:#fff;background:#dc2626;border:none;border-radius:6px;padding:6px 14px;cursor:pointer;font-weight:600;display:block;width:100%">${esc(requestReferralLabel)}</button>` : ''}
  `;
  if (m.acceptingReferrals) {
    const btn = el.querySelector<HTMLButtonElement>('button');
    if (btn) btn.addEventListener('click', () => onReferral(m));
  }
  return el;
}

export function PublicNetworkMapLeaflet({ markers, selectedId, zoomToId, onZoomed, searchLocation, hideSearchMarker = false, onSelect, onRequestReferral, requestReferralLabel = 'Send Referral' }: PublicNetworkMapProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const mapRef       = useRef<import('leaflet').Map | null>(null);
  const layerRef     = useRef<import('leaflet').LayerGroup | null>(null);

  // Always-current callback refs — avoids stale closures without adding to effect deps.
  const onSelectRef   = useRef(onSelect);
  const onReferralRef = useRef(onRequestReferral);
  const onZoomedRef   = useRef(onZoomed);
  onSelectRef.current   = onSelect;
  onReferralRef.current = onRequestReferral;
  onZoomedRef.current   = onZoomed;

  // Previous marker identities — used to decide whether to re-fit the map view.
  const prevMarkerIdsRef = useRef('');
  // Always-current marker list for the external-zoom effect (avoids adding markers to its deps).
  const markersRef = useRef(markers);
  markersRef.current = markers;

  // ── Init map once on mount ────────────────────────────────────────────────
  useEffect(() => {
    const el = containerRef.current;
    if (!el) return;

    let cancelled = false;
    void (async () => {
      const Leaflet = (await import('leaflet')).default as unknown as L;
      if (cancelled) return;

      // Clear any stale Leaflet state left by React StrictMode's double-mount
      // or by HMR module replacement. Leaflet throws "Map container is already
      // initialized" if _leaflet_id is set when new Leaflet.Map() is called.
      (el as HTMLDivElement & { _leaflet_id?: number })._leaflet_id = undefined;

      const map = Leaflet.map(el, { center: US_CENTER, zoom: 4, scrollWheelZoom: true, zoomControl: true });

      Leaflet.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
      }).addTo(map);

      layerRef.current = Leaflet.layerGroup().addTo(map);
      mapRef.current   = map;
    })();

    return () => {
      cancelled = true;
      mapRef.current?.remove();
      mapRef.current  = null;
      layerRef.current = null;
    };
  }, []);

  // ── Sync markers + selection state ───────────────────────────────────────
  useEffect(() => {
    void (async () => {
      const map   = mapRef.current;
      const layer = layerRef.current;
      if (!map || !layer) return;

      const Leaflet = (await import('leaflet')).default as unknown as L;

      layer.clearLayers();

    if (searchLocation && !hideSearchMarker) {
      const icon = Leaflet.divIcon({
        className: '',
        iconSize: [30, 30] as [number, number],
        iconAnchor: [15, 30] as [number, number],
        popupAnchor: [0, -30] as [number, number],
        html: makeSearchPinHtml(),
      });
      Leaflet
        .marker([searchLocation.latitude, searchLocation.longitude], { icon, zIndexOffset: 2000 })
        .bindPopup(`<p style="font-weight:700;font-size:13px;margin:0;color:#111827">Search location</p><p style="font-size:12px;color:#6b7280;margin:2px 0 0">${esc(searchLocation.label)}</p>`, { closeButton: false })
        .addTo(layer);
    }

    for (const m of markers) {
      const sel  = m.id === selectedId;

      if (m.isMobile && m.serviceRadiusMiles) {
        Leaflet.circle([m.latitude, m.longitude], {
          radius:      m.serviceRadiusMiles * MILES_TO_METERS,
          color:       '#7c3aed',
          weight:      2,
          opacity:     0.8,
          dashArray:   '6, 6',
          fillColor:   '#7c3aed',
          fillOpacity: sel ? 0.1 : 0,
          interactive: false,
        }).addTo(layer);
      }

      const size = sel ? 34 : 28;
      const icon = Leaflet.divIcon({
        className:   '',
        iconSize:    [size, size] as [number, number],
        iconAnchor:  [size / 2, size / 2] as [number, number],
        popupAnchor: [0, -(size / 2 + 4)] as [number, number],
        html:        makePinHtml(m.index, m.acceptingReferrals, sel),
      });

      Leaflet
        .marker([m.latitude, m.longitude], { icon, zIndexOffset: sel ? 1000 : 0 })
        .bindPopup(buildPopupEl(m, mk => onReferralRef.current(mk), requestReferralLabel), { minWidth: 220, closeButton: false })
        .on('click', () => {
          map.setView([m.latitude, m.longitude], Math.max(map.getZoom(), 13));
          onSelectRef.current(m.id);
        })
        .addTo(layer);
    }

    // Re-fit bounds only when the actual marker set changes, not on selectedId changes.
    const locationKey = searchLocation
      ? `${searchLocation.latitude}:${searchLocation.longitude}:${hideSearchMarker}`
      : '';
    const markerKey = markers
      .map(m => `${m.id}:${m.latitude}:${m.longitude}`)
      .join(',');
    const newIds = `${locationKey}:${markerKey}`;
    const providerAtSearchLocation = !!searchLocation && markers.some(m =>
      m.latitude === searchLocation.latitude &&
      m.longitude === searchLocation.longitude,
    );
    if (newIds !== prevMarkerIdsRef.current) {
      prevMarkerIdsRef.current = newIds;
      if (searchLocation) {
        map.setView([searchLocation.latitude, searchLocation.longitude], providerAtSearchLocation ? 13 : 11);
      } else if (markers.length === 1 && !searchLocation) {
        map.setView([markers[0].latitude, markers[0].longitude], 12);
      } else if (markers.length > 0) {
        const points = markers.map(mk => [mk.latitude, mk.longitude] as [number, number]);
        map.fitBounds(
          Leaflet.latLngBounds(points),
          { padding: [40, 40] },
        );
      }
    }    })();  }, [markers, selectedId, searchLocation, hideSearchMarker, requestReferralLabel]);

  // ── Zoom to an externally commanded provider (e.g. card click in split view) ─
  useEffect(() => {
    if (!zoomToId) return;
    const map = mapRef.current;
    if (!map) return;
    const m = markersRef.current.find(mk => mk.id === zoomToId);
    if (m) {
      map.setView([m.latitude, m.longitude], Math.max(map.getZoom(), 13));
      // Reset zoomToId in the parent so re-clicking the same card triggers a new zoom.
      onZoomedRef.current?.();
    }
  }, [zoomToId]);

  useEffect(() => {
    if (!selectedId) return;
    const map = mapRef.current;
    if (!map) return;
    const m = markersRef.current.find(mk => mk.id === selectedId);
    if (m) {
      map.setView([m.latitude, m.longitude], Math.max(map.getZoom(), 13));
    }
  }, [selectedId]);

  // isolation:isolate creates a stacking context that scopes Leaflet's internal
  // z-indexes (200–800) so they cannot bleed above fixed overlays/modals.
  return (
    <div style={{ height: '100%', width: '100%', isolation: 'isolate' }}>
      <div ref={containerRef} style={{ height: '100%', width: '100%' }} />
    </div>
  );
}
