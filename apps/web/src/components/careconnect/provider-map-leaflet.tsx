'use client';

import 'leaflet/dist/leaflet.css';
import { useEffect, useRef } from 'react';
import type { ProviderMarker } from '@/types/careconnect';

interface ViewportBounds {
  northLat: number;
  southLat: number;
  eastLng:  number;
  westLng:  number;
}

interface ProviderMapProps {
  markers:           ProviderMarker[];
  selectedId:        string | null;
  onSelect:          (id: string) => void;
  onViewportChange:  (bounds: ViewportBounds) => void;
  isReferrer:        boolean;
  centerLat?:        number;
  centerLng?:        number;
  defaultZoom?:      number;
  actionLabel?:      string;
  onAction?:         (marker: ProviderMarker) => void;
}

type L = typeof import('leaflet');

const US_CENTER: [number, number] = [39.5, -98.35];
const MILES_TO_METERS = 1609.34;

/** Escapes HTML special characters to prevent XSS when injecting provider data into popup innerHTML. */
function esc(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

export function ProviderMapLeaflet({
  markers,
  selectedId,
  onSelect,
  onViewportChange,
  isReferrer,
  centerLat,
  centerLng,
  defaultZoom = 5,
  actionLabel,
  onAction,
}: ProviderMapProps) {
  const containerRef      = useRef<HTMLDivElement>(null);
  const mapRef            = useRef<import('leaflet').Map | null>(null);
  const layerRef          = useRef<import('leaflet').LayerGroup | null>(null);
  const viewportTimerRef  = useRef<ReturnType<typeof setTimeout>>();

  const onSelectRef          = useRef(onSelect);
  const onViewportChangeRef  = useRef(onViewportChange);
  const isReferrerRef        = useRef(isReferrer);
  const actionLabelRef       = useRef(actionLabel);
  const onActionRef          = useRef(onAction);
  onSelectRef.current         = onSelect;
  onViewportChangeRef.current = onViewportChange;
  isReferrerRef.current       = isReferrer;
  actionLabelRef.current      = actionLabel;
  onActionRef.current         = onAction;

  // ── Init map once on mount ────────────────────────────────────────────────
  useEffect(() => {
    const el = containerRef.current;
    if (!el) return;

    let cancelled = false;
    void (async () => {
      const Leaflet = (await import('leaflet')).default as unknown as L;
      if (cancelled) return;

      // Clear any stale Leaflet state left by React StrictMode's double-mount or HMR.
      (el as HTMLDivElement & { _leaflet_id?: number })._leaflet_id = undefined;

      const center: [number, number] =
        centerLat != null && centerLng != null ? [centerLat, centerLng] : US_CENTER;
      const zoom = centerLat != null ? 11 : defaultZoom;

      const map = Leaflet.map(el, { center, zoom, scrollWheelZoom: true });

      Leaflet.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
      }).addTo(map);

      // Emit viewport bounds after move/zoom with 350 ms debounce.
      const fireBounds = () => {
        clearTimeout(viewportTimerRef.current);
        viewportTimerRef.current = setTimeout(() => {
          const b = map.getBounds();
          onViewportChangeRef.current({
            northLat: b.getNorth(),
            southLat: b.getSouth(),
            eastLng:  b.getEast(),
            westLng:  b.getWest(),
          });
        }, 350);
      };
      map.on('moveend', fireBounds);
      map.on('zoomend', fireBounds);

      layerRef.current = Leaflet.layerGroup().addTo(map);
      mapRef.current   = map;
    })();

    return () => {
      cancelled = true;
      clearTimeout(viewportTimerRef.current);
      mapRef.current?.remove();
      mapRef.current  = null;
      layerRef.current = null;
    };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // ── Re-center when search location prop changes ───────────────────────────
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    if (centerLat != null && centerLng != null) {
      map.setView([centerLat, centerLng], 11);
    }
  }, [centerLat, centerLng]);

  // ── Sync markers + selection state ───────────────────────────────────────
  useEffect(() => {
    void (async () => {
      const map   = mapRef.current;
      const layer = layerRef.current;
      if (!map || !layer) return;

      const Leaflet = (await import('leaflet')).default as unknown as L;

      layer.clearLayers();

    for (const m of markers) {
      const isSelected = m.id === selectedId;

      const locationLine = m.isMobile
        ? `Mobile · ${[m.serviceAreaLabel, `${m.city}, ${m.state}`].filter((s): s is string => Boolean(s)).map(esc).join(' · ')}${m.serviceRadiusMiles ? ` · ${m.serviceRadiusMiles}mi radius` : ''}`
        : esc(m.markerSubtitle);

      const popupEl = document.createElement('div');
      popupEl.style.fontFamily = 'inherit';
      popupEl.style.minWidth   = '200px';
      popupEl.innerHTML = `
        <p style="font-weight:600;font-size:14px;margin:0 0 2px;color:#111827">${esc(m.displayLabel)}</p>
        <p style="font-size:12px;color:#6b7280;margin:0 0 6px">${locationLine}</p>
        ${typeof m.distanceMiles === 'number' ? `<p style="font-size:12px;color:#2563eb;margin:0 0 6px">${m.distanceMiles.toFixed(1)} mi away</p>` : ''}
        ${(m.specialties ?? []).length > 0
          ? `<p style="font-size:11px;color:#1d4ed8;margin:0 0 6px">${esc(m.specialties.map(s => s.name).join(', '))}</p>`
          : ''
        }
        ${m.acceptingReferrals
          ? `<span style="font-size:11px;color:#15803d;background:#f0fdf4;border:1px solid #bbf7d0;border-radius:9999px;padding:2px 8px;display:inline-block;margin-bottom:8px">Accepting referrals</span>`
          : `<span style="font-size:11px;color:#6b7280;background:#f9fafb;border:1px solid #e5e7eb;border-radius:9999px;padding:2px 8px;display:inline-block;margin-bottom:8px">Not accepting referrals</span>`
        }
        <div style="display:flex;flex-direction:column;gap:4px;margin-top:4px">
          <a href="/careconnect/providers/${encodeURIComponent(m.id)}" style="font-size:12px;color:#2563eb;font-weight:500;text-decoration:none;display:block">View Provider →</a>
          ${isReferrerRef.current && m.acceptingReferrals
            ? `<a href="/careconnect/providers/${encodeURIComponent(m.id)}" style="font-size:12px;color:#7c3aed;text-decoration:none;display:block">Create Referral →</a>`
            : ''
          }
          ${actionLabelRef.current
            ? `<button type="button" data-provider-action style="font-size:12px;color:#fff;background:#2563eb;border:none;border-radius:6px;padding:6px 10px;cursor:pointer;font-weight:600;text-align:center">${esc(actionLabelRef.current)}</button>`
            : ''
          }
        </div>
      `;
      const actionButton = popupEl.querySelector<HTMLButtonElement>('[data-provider-action]');
      actionButton?.addEventListener('click', () => onActionRef.current?.(m));

      if (m.isMobile && m.serviceRadiusMiles) {
        Leaflet.circle([m.latitude, m.longitude], {
          radius:      m.serviceRadiusMiles * MILES_TO_METERS,
          color:       '#7c3aed',
          weight:      2,
          opacity:     0.8,
          dashArray:   '6, 6',
          fillColor:   '#7c3aed',
          fillOpacity: isSelected ? 0.12 : 0,
          interactive: false,
        }).addTo(layer);
      }

      const marker = m.isMobile
        ? Leaflet.marker([m.latitude, m.longitude], {
            icon: Leaflet.divIcon({
              className: '',
              html: `<div style="width:${isSelected ? 16 : 12}px;height:${isSelected ? 16 : 12}px;background:${m.acceptingReferrals ? '#16a34a' : '#6b7280'};border:${isSelected ? 3 : 1.5}px solid ${isSelected ? '#1d4ed8' : '#ffffff'};transform:rotate(45deg);box-sizing:border-box"></div>`,
              iconSize: [isSelected ? 16 : 12, isSelected ? 16 : 12],
              iconAnchor: [(isSelected ? 16 : 12) / 2, (isSelected ? 16 : 12) / 2],
            }),
          })
        : Leaflet.circleMarker([m.latitude, m.longitude], {
            radius:      isSelected ? 11 : 7,
            fillColor:   m.acceptingReferrals ? '#16a34a' : '#6b7280',
            fillOpacity: 0.85,
            color:       isSelected ? '#1d4ed8' : '#ffffff',
            weight:      isSelected ? 3 : 1.5,
          });

      marker
        .bindPopup(popupEl, { minWidth: 200 })
        .on('click', () => {
          map.setView([m.latitude, m.longitude], Math.max(map.getZoom(), m.isMobile ? 10 : 13));
          onSelectRef.current(m.id);
        })
        .addTo(layer);
    }
    })();
  }, [markers, selectedId]);

  return (
    <div style={{ height: '100%', width: '100%', isolation: 'isolate' }}>
      <div ref={containerRef} style={{ height: '100%', width: '100%' }} />
    </div>
  );
}
