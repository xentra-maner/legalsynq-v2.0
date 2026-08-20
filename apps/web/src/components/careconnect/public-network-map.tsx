'use client';

import dynamic from 'next/dynamic';
import { googleMapsKey } from '@/lib/use-map-provider';
import { useSettings } from '@/contexts/settings-context';
import type { PublicProviderMarker } from '@/lib/public-network-api';

export interface NumberedMarker extends PublicProviderMarker {
  index: number;
  phone?: string | null;
  addressLine1?: string | null;
  postalCode?: string | null;
}

export interface SearchLocationMarker {
  latitude:  number;
  longitude: number;
  label:     string;
}

export interface PublicNetworkMapProps {
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

const LeafletMap = dynamic(
  () => import('./public-network-map-leaflet').then(m => m.PublicNetworkMapLeaflet),
  { ssr: false },
);

const GoogleMap = dynamic(
  () => import('./public-network-map-google').then(m => m.PublicNetworkMapGoogle),
  { ssr: false },
);

export function PublicNetworkMap(props: PublicNetworkMapProps) {
  const { careConnect } = useSettings();
  const hasGoogleKey = !!googleMapsKey();

  if (careConnect.defaultMapProvider === 'google' && hasGoogleKey) {
    return <GoogleMap {...props} />;
  }
  return <LeafletMap {...props} />;
}
