'use client';

import dynamic from 'next/dynamic';
import { googleMapsKey } from '@/lib/use-map-provider';
import { useSettings } from '@/contexts/settings-context';
import type { ProviderMarker } from '@/types/careconnect';

export interface ViewportBounds {
  northLat: number;
  southLat: number;
  eastLng:  number;
  westLng:  number;
}

export interface ProviderMapProps {
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

const LeafletMap = dynamic(
  () => import('./provider-map-leaflet').then(m => m.ProviderMapLeaflet),
  { ssr: false },
);

const GoogleMap = dynamic(
  () => import('./provider-map-google').then(m => m.ProviderMapGoogle),
  { ssr: false },
);

export function ProviderMap(props: ProviderMapProps) {
  const { careConnect } = useSettings();
  const hasGoogleKey = !!googleMapsKey();

  if (careConnect.defaultMapProvider === 'google' && hasGoogleKey) {
    return <GoogleMap {...props} />;
  }
  return <LeafletMap {...props} />;
}
