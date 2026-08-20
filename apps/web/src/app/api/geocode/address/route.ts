import { createHmac } from 'crypto';
import { NextResponse, type NextRequest } from 'next/server';

/**
 * GET /api/geocode/address?q={query}
 *
 * BFF proxy for OpenStreetMap Nominatim address autocomplete.
 * Required so we can add the mandatory User-Agent header and avoid CORS.
 * Results are cached for 60 s on the CDN edge.
 *
 * Nominatim usage policy: https://operations.osmfoundation.org/policies/nominatim/
 * Rate-limited to 1 req/s — client debouncing keeps us within that.
 */
const NOMINATIM = 'https://nominatim.openstreetmap.org';
const US_CENSUS_GEOCODER = 'https://geocoding.geo.census.gov/geocoder/locations/onelineaddress';
const ZIPPOPOTAM = 'https://api.zippopotam.us/us';
const USER_AGENT = 'LegalSynq/2.0 contact@legalsynq.com';
const INTERNAL_REQUEST_SECRET =
  process.env['PublicTrustBoundary__InternalRequestSecret'] ??
  process.env.INTERNAL_REQUEST_SECRET ??
  '';
const ADDRESS_SELECTION_TOKEN_TTL_SECONDS = 86400;

export interface AddressSuggestion {
  displayName:  string;
  addressLine1: string;
  city:         string;
  state:        string;
  postalCode:   string;
  addressSelectionToken: string;
  latitude:     number;
  longitude:    number;
}

const STATE_ABBR: Record<string, string> = {
  Alabama: 'AL', Alaska: 'AK', Arizona: 'AZ', Arkansas: 'AR', California: 'CA',
  Colorado: 'CO', Connecticut: 'CT', Delaware: 'DE', Florida: 'FL', Georgia: 'GA',
  Hawaii: 'HI', Idaho: 'ID', Illinois: 'IL', Indiana: 'IN', Iowa: 'IA',
  Kansas: 'KS', Kentucky: 'KY', Louisiana: 'LA', Maine: 'ME', Maryland: 'MD',
  Massachusetts: 'MA', Michigan: 'MI', Minnesota: 'MN', Mississippi: 'MS', Missouri: 'MO',
  Montana: 'MT', Nebraska: 'NE', Nevada: 'NV', 'New Hampshire': 'NH', 'New Jersey': 'NJ',
  'New Mexico': 'NM', 'New York': 'NY', 'North Carolina': 'NC', 'North Dakota': 'ND',
  Ohio: 'OH', Oklahoma: 'OK', Oregon: 'OR', Pennsylvania: 'PA', 'Rhode Island': 'RI',
  'South Carolina': 'SC', 'South Dakota': 'SD', Tennessee: 'TN', Texas: 'TX',
  Utah: 'UT', Vermont: 'VT', Virginia: 'VA', Washington: 'WA', 'West Virginia': 'WV',
  Wisconsin: 'WI', Wyoming: 'WY',
};
const STATE_CODES = new Set(Object.values(STATE_ABBR));
const ZIP_CODE_PATTERN = /^\d{5}(?:-\d{4})?$/;

export async function GET(request: NextRequest): Promise<NextResponse> {
  const q     = (request.nextUrl.searchParams.get('q') ?? '').trim();
  // loose=1: relax the addressLine1 requirement — used by map geocoding where
  // city/state-level precision is sufficient (no street address needed).
  const loose = request.nextUrl.searchParams.get('loose') === '1';
  if (q.length < 3 && !(loose && STATE_CODES.has(q.toUpperCase()))) {
    return NextResponse.json([] as AddressSuggestion[]);
  }

  const url = new URL(`${NOMINATIM}/search`);
  url.searchParams.set('q', `${q}, USA`);
  url.searchParams.set('format', 'json');
  url.searchParams.set('limit', '6');
  url.searchParams.set('addressdetails', '1');
  url.searchParams.set('countrycodes', 'us');

  let raw: Record<string, unknown>[] = [];
  try {
    const res = await fetch(url.toString(), {
      headers: { 'User-Agent': USER_AGENT, 'Accept-Language': 'en-US,en' },
      signal: AbortSignal.timeout(3000),
      next: { revalidate: 60 },
    });
    if (res.ok) raw = await res.json();
  } catch {
    // Continue to the U.S. Census fallback below.
  }

  const suggestions: AddressSuggestion[] = [];

  for (const item of raw) {
    const addr = item.address as Record<string, string> | undefined;
    if (!addr) continue;

    const lat = parseFloat(item.lat as string);
    const lon = parseFloat(item.lon as string);
    if (isNaN(lat) || isNaN(lon)) continue;

    const houseNumber = addr.house_number ?? '';
    const road = addr.road ?? '';
    const addressLine1 = houseNumber ? `${houseNumber} ${road}` : road;

    // In strict mode (autocomplete form) require a street address.
    // In loose mode (map geocoding) city/state-level results are acceptable.
    if (!addressLine1 && !loose) continue;

    const city =
      addr.city ??
      addr.town ??
      addr.village ??
      addr.municipality ??
      addr.county ??
      '';

    const stateFull = addr.state ?? '';
    const state = STATE_ABBR[stateFull] ?? stateFull.slice(0, 2).toUpperCase();
    const postalCode = addr.postcode?.slice(0, 5) ?? '';

    if ((!city && !loose) || !state || (!postalCode && !loose)) continue;

    const displayName = [
      addressLine1,
      city,
      postalCode ? `${state} ${postalCode}` : state,
    ].filter(Boolean).join(', ');

    if (!suggestions.some(s => s.displayName === displayName)) {
      suggestions.push({
        displayName,
        addressLine1,
        city,
        state,
        postalCode,
        addressSelectionToken: createAddressSelectionToken({ addressLine1, city, state, postalCode }),
        latitude: lat,
        longitude: lon,
      });
    }
    if (suggestions.length >= 5) break;
  }

  // Nominatim often returns no coordinates for ZIP-only queries. The provider
  // map's ZIP filter needs a ZIP centroid, not a deliverable street address.
  if (suggestions.length === 0 && loose && ZIP_CODE_PATTERN.test(q)) {
    const zipSuggestion = await getZipCodeSuggestion(q);
    if (zipSuggestion) suggestions.push(zipSuggestion);
  }

  // Nominatim can be rate-limited and does not contain every U.S. street address.
  // The Census geocoder is keyless and authoritative for domestic address ranges.
  if (suggestions.length === 0) {
    const censusSuggestion = await getCensusAddressSuggestion(q);
    if (censusSuggestion) suggestions.push(censusSuggestion);
  }

  return NextResponse.json(suggestions, {
    headers: { 'Cache-Control': 'public, s-maxage=60, stale-while-revalidate=120' },
  });
}

async function getZipCodeSuggestion(q: string): Promise<AddressSuggestion | null> {
  const zip = q.slice(0, 5);

  try {
    const res = await fetch(`${ZIPPOPOTAM}/${encodeURIComponent(zip)}`, {
      headers: { 'User-Agent': USER_AGENT, Accept: 'application/json' },
      signal: AbortSignal.timeout(3000),
      next: { revalidate: 86400 },
    });
    if (!res.ok) return null;

    const payload = await res.json() as {
      'post code'?: string;
      places?: Array<{
        'place name'?: string;
        'state abbreviation'?: string;
        latitude?: string;
        longitude?: string;
      }>;
    };
    const place = payload.places?.[0];
    const latitude = Number(place?.latitude);
    const longitude = Number(place?.longitude);
    if (!place || !Number.isFinite(latitude) || !Number.isFinite(longitude)) return null;

    const postalCode = payload['post code']?.slice(0, 5) || zip;
    const city = place['place name'] ?? '';
    const state = place['state abbreviation'] ?? '';
    const displayName = [city, state, postalCode].filter(Boolean).join(', ');

    return {
      displayName,
      addressLine1: '',
      city,
      state,
      postalCode,
      addressSelectionToken: createAddressSelectionToken({ addressLine1: '', city, state, postalCode }),
      latitude,
      longitude,
    };
  } catch {
    return null;
  }
}

async function getCensusAddressSuggestion(q: string): Promise<AddressSuggestion | null> {
  const url = new URL(US_CENSUS_GEOCODER);
  url.searchParams.set('address', q);
  url.searchParams.set('benchmark', 'Public_AR_Current');
  url.searchParams.set('format', 'json');

  try {
    const res = await fetch(url.toString(), {
      headers: { 'User-Agent': USER_AGENT, Accept: 'application/json' },
      signal: AbortSignal.timeout(5000),
      next: { revalidate: 60 },
    });
    if (!res.ok) return null;

    const payload = await res.json() as {
      result?: {
        addressMatches?: Array<{
          matchedAddress?: string;
          coordinates?: { x?: number; y?: number };
          addressComponents?: { city?: string; state?: string; zip?: string };
        }>;
      };
    };
    const match = payload.result?.addressMatches?.[0];
    const latitude = Number(match?.coordinates?.y);
    const longitude = Number(match?.coordinates?.x);
    if (!match || !Number.isFinite(latitude) || !Number.isFinite(longitude)) return null;

    const components = match.addressComponents ?? {};
    const matchedParts = (match.matchedAddress ?? '').split(',').map(part => part.trim());
    const addressLine1 = matchedParts[0] ?? '';
    const city = components.city ?? matchedParts[1] ?? '';
    const state = components.state ?? matchedParts[2] ?? '';
    const postalCode = (components.zip ?? matchedParts[3] ?? '').slice(0, 5);
    const displayName = match.matchedAddress?.trim() || q;

    return {
      displayName,
      addressLine1,
      city,
      state,
      postalCode,
      addressSelectionToken: createAddressSelectionToken({ addressLine1, city, state, postalCode }),
      latitude,
      longitude,
    };
  } catch {
    return null;
  }
}

function createAddressSelectionToken(address: {
  addressLine1: string;
  city: string;
  state: string;
  postalCode: string;
}): string {
  if (!INTERNAL_REQUEST_SECRET) return '';

  const payload = {
    ...address,
    exp: Math.floor(Date.now() / 1000) + ADDRESS_SELECTION_TOKEN_TTL_SECONDS,
  };
  const body = Buffer.from(JSON.stringify(payload)).toString('base64url');
  const sig = createHmac('sha256', INTERNAL_REQUEST_SECRET).update(body).digest('base64url');
  return `${body}.${sig}`;
}
