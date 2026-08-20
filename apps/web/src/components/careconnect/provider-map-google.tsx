'use client';

import { useRef, useEffect } from 'react';
import { useGoogleMapsScript } from '@/lib/use-google-maps-script';
import { circleOutlinePoints, DASHED_RING_ICONS } from '@/lib/coverage-circle';
import type { ProviderMarker } from '@/types/careconnect';

interface ViewportBounds { northLat: number; southLat: number; eastLng: number; westLng: number; }
interface ProviderMapProps {
  markers: ProviderMarker[]; selectedId: string | null; onSelect: (id: string) => void;
  onViewportChange: (bounds: ViewportBounds) => void; isReferrer: boolean;
  centerLat?: number; centerLng?: number; defaultZoom?: number;
  actionLabel?: string; onAction?: (marker: ProviderMarker) => void;
}

const US_CENTER = { lat: 39.5, lng: -98.35 };

function circleUrl(fill: string, stroke: string, radius: number, sw: number): string {
  const size = (radius + sw) * 2;
  const c = size / 2;
  return `data:image/svg+xml;charset=UTF-8,${encodeURIComponent(
    `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}"><circle cx="${c}" cy="${c}" r="${radius}" fill="${fill}" fill-opacity="0.85" stroke="${stroke}" stroke-width="${sw}"/></svg>`,
  )}`;
}

// Mobile/roaming providers get a diamond pin (vs. the filled-dot pin for a fixed address) so
// "covers this area" reads differently from "clinic is exactly here" at a glance.
function diamondUrl(fill: string, stroke: string, radius: number, sw: number): string {
  const size = (radius + sw) * 2;
  const c = size / 2;
  return `data:image/svg+xml;charset=UTF-8,${encodeURIComponent(
    `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}"><rect x="${c - radius / Math.SQRT2}" y="${c - radius / Math.SQRT2}" width="${(radius / Math.SQRT2) * 2}" height="${(radius / Math.SQRT2) * 2}" transform="rotate(45 ${c} ${c})" fill="${fill}" fill-opacity="0.85" stroke="${stroke}" stroke-width="${sw}"/></svg>`,
  )}`;
}

const MILES_TO_METERS = 1609.34;

export function ProviderMapGoogle({
  markers, selectedId, onSelect, onViewportChange, isReferrer,
  centerLat, centerLng, defaultZoom = 5, actionLabel, onAction,
}: ProviderMapProps) {
  const isLoaded    = useGoogleMapsScript();
  const containerRef = useRef<HTMLDivElement>(null);
  const mapRef       = useRef<google.maps.Map | null>(null);
  const markerRefs   = useRef<Map<string, google.maps.Marker>>(new Map());
  const circleRefs   = useRef<Map<string, google.maps.Circle>>(new Map());
  const ringRefs     = useRef<Map<string, google.maps.Polyline>>(new Map());
  const infoRef      = useRef<google.maps.InfoWindow | null>(null);
  const boundsTimer  = useRef<ReturnType<typeof setTimeout>>();
  const onSelectRef  = useRef(onSelect);
  const onViewportChangeRef = useRef(onViewportChange);
  const actionLabelRef = useRef(actionLabel);
  const onActionRef = useRef(onAction);
  onSelectRef.current = onSelect;
  onViewportChangeRef.current = onViewportChange;
  actionLabelRef.current = actionLabel;
  onActionRef.current = onAction;

  const center = centerLat != null && centerLng != null
    ? { lat: centerLat, lng: centerLng } : US_CENTER;
  const zoom = centerLat != null ? 11 : defaultZoom;

  useEffect(() => {
    if (!isLoaded || !containerRef.current || mapRef.current) return;
    const map = new window.google.maps.Map(containerRef.current, {
      center, zoom,
      gestureHandling: 'greedy',
      fullscreenControl: false, mapTypeControl: false,
    });
    infoRef.current = new window.google.maps.InfoWindow();
    map.addListener('bounds_changed', () => {
      clearTimeout(boundsTimer.current);
      boundsTimer.current = setTimeout(() => {
        const b = map.getBounds();
        if (!b) return;
        onViewportChangeRef.current({
          northLat: b.getNorthEast().lat(), southLat: b.getSouthWest().lat(),
          eastLng:  b.getNorthEast().lng(), westLng:  b.getSouthWest().lng(),
        });
      }, 350);
    });
    mapRef.current = map;
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isLoaded]);

  useEffect(() => {
    const map = mapRef.current;
    if (!map || !isLoaded) return;

    const seen = new Set<string>();
    for (const m of markers) {
      seen.add(m.id);
      const isSelected = m.id === selectedId;
      const fill   = m.acceptingReferrals ? '#16a34a' : '#6b7280';
      const stroke = isSelected ? '#1d4ed8' : '#ffffff';
      const radius = isSelected ? 11 : 7;
      const sw     = isSelected ? 3 : 1.5;
      const size   = (radius + sw) * 2;
      const iconUrl = m.isMobile ? diamondUrl(fill, stroke, radius, sw) : circleUrl(fill, stroke, radius, sw);
      const icon   = { url: iconUrl, scaledSize: new window.google.maps.Size(size, size), anchor: new window.google.maps.Point(size / 2, size / 2) };

      if (m.isMobile && m.serviceRadiusMiles) {
        const circleOptions: google.maps.CircleOptions = {
          center: { lat: m.latitude, lng: m.longitude },
          radius: m.serviceRadiusMiles * MILES_TO_METERS,
          strokeOpacity: 0,
          fillColor: '#7c3aed', fillOpacity: isSelected ? 0.12 : 0,
          clickable: false,
        };
        let circle = circleRefs.current.get(m.id);
        if (!circle) {
          circle = new window.google.maps.Circle({ ...circleOptions, map });
          circleRefs.current.set(m.id, circle);
        } else {
          circle.setOptions(circleOptions);
        }

        const ringOptions: google.maps.PolylineOptions = {
          path: circleOutlinePoints(m.latitude, m.longitude, m.serviceRadiusMiles),
          strokeOpacity: 0,
          icons: DASHED_RING_ICONS,
          strokeColor: '#7c3aed',
          clickable: false,
        };
        let ring = ringRefs.current.get(m.id);
        if (!ring) {
          ring = new window.google.maps.Polyline({ ...ringOptions, map });
          ringRefs.current.set(m.id, ring);
        } else {
          ring.setOptions(ringOptions);
        }
      } else {
        circleRefs.current.get(m.id)?.setMap(null);
        circleRefs.current.delete(m.id);
        ringRefs.current.get(m.id)?.setMap(null);
        ringRefs.current.delete(m.id);
      }

      let marker = markerRefs.current.get(m.id);
      if (!marker) {
        marker = new window.google.maps.Marker({ position: { lat: m.latitude, lng: m.longitude }, map, icon, zIndex: isSelected ? 100 : 1 });
        marker.addListener('click', () => {
          map.panTo({ lat: m.latitude, lng: m.longitude });
          const currentZoom = map.getZoom() ?? 0;
          if (currentZoom < 13) map.setZoom(m.isMobile ? 10 : 13);
          onSelectRef.current(m.id);
          const locationLine = m.isMobile
            ? `Mobile · ${[m.serviceAreaLabel, `${m.city}, ${m.state}`].filter(Boolean).join(' · ')}${m.serviceRadiusMiles ? ` · ${m.serviceRadiusMiles}mi radius` : ''}`
            : m.markerSubtitle;
          const content = document.createElement('div');
          content.style.fontFamily = 'system-ui,sans-serif';
          content.style.minWidth = '180px';
          content.innerHTML = `
            <p style="font-weight:600;font-size:14px;margin:0 0 2px;color:#111827">${m.displayLabel}</p>
            <p style="font-size:12px;color:#6b7280;margin:0 0 6px">${locationLine}</p>
            ${typeof m.distanceMiles === 'number' ? `<p style="font-size:12px;color:#2563eb;margin:0 0 6px">${m.distanceMiles.toFixed(1)} mi away</p>` : ''}
            ${(m.specialties ?? []).length > 0 ? `<p style="font-size:11px;color:#1d4ed8;margin:0 0 6px">${m.specialties.map(s => s.name).join(', ')}</p>` : ''}
            ${m.acceptingReferrals
              ? `<span style="font-size:11px;color:#15803d;background:#f0fdf4;border:1px solid #bbf7d0;border-radius:9999px;padding:2px 8px;display:inline-block;margin-bottom:8px">Accepting referrals</span>`
              : `<span style="font-size:11px;color:#6b7280;background:#f9fafb;border:1px solid #e5e7eb;border-radius:9999px;padding:2px 8px;display:inline-block;margin-bottom:8px">Not accepting referrals</span>`}
            <div style="display:flex;flex-direction:column;gap:4px;margin-top:4px">
              <a href="/careconnect/providers/${m.id}" style="font-size:12px;color:#2563eb;font-weight:500;text-decoration:none">View Provider →</a>
              ${isReferrer && m.acceptingReferrals ? `<a href="/careconnect/providers/${m.id}" style="font-size:12px;color:#7c3aed;text-decoration:none">Create Referral →</a>` : ''}
            </div>`;
          if (actionLabelRef.current) {
            const button = document.createElement('button');
            button.type = 'button';
            button.textContent = actionLabelRef.current;
            button.style.cssText = 'font-size:12px;color:#fff;background:#2563eb;border:none;border-radius:6px;padding:6px 10px;cursor:pointer;font-weight:600;text-align:center;margin-top:4px;width:100%';
            button.addEventListener('click', () => onActionRef.current?.(m));
            content.appendChild(button);
          }
          infoRef.current?.setContent(content);
          infoRef.current?.open({ map, anchor: marker });
        });
        markerRefs.current.set(m.id, marker);
      } else {
        marker.setIcon(icon);
        marker.setZIndex(isSelected ? 100 : 1);
      }
    }

    for (const [id, marker] of markerRefs.current) {
      if (!seen.has(id)) { marker.setMap(null); markerRefs.current.delete(id); }
    }
    for (const [id, circle] of circleRefs.current) {
      if (!seen.has(id)) { circle.setMap(null); circleRefs.current.delete(id); }
    }
    for (const [id, ring] of ringRefs.current) {
      if (!seen.has(id)) { ring.setMap(null); ringRefs.current.delete(id); }
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [markers, selectedId, isLoaded]);

  useEffect(() => () => {
    clearTimeout(boundsTimer.current);
    for (const m of markerRefs.current.values()) m.setMap(null);
    markerRefs.current.clear();
    for (const c of circleRefs.current.values()) c.setMap(null);
    circleRefs.current.clear();
    for (const r of ringRefs.current.values()) r.setMap(null);
    ringRefs.current.clear();
    infoRef.current?.close();
  }, []);

  if (!isLoaded) {
    return <div style={{ height: '100%', width: '100%', background: '#e5e7eb', display: 'flex', alignItems: 'center', justifyContent: 'center', color: '#6b7280', fontSize: 14 }}>Loading map…</div>;
  }

  return <div ref={containerRef} style={{ height: '100%', width: '100%' }} />;
}
