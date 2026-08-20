import { beforeEach, describe, expect, test, vi } from 'vitest';
import { NextRequest } from 'next/server';

describe('GET /api/geocode/address', () => {
  beforeEach(() => {
    vi.resetModules();
    vi.unstubAllGlobals();
    process.env.PublicTrustBoundary__InternalRequestSecret = 'zip-token-test-secret';
  });

  test('returns signed address selection tokens in strict mode', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
      ok: true,
      json: async () => ([{
        lat: '33.749',
        lon: '-84.388',
        address: {
          house_number: '885',
          road: 'Sample Rd',
          city: 'Atlanta',
          state: 'Georgia',
          postcode: '30316-9999',
        },
      }]),
    }));

    const { GET } = await import('./route');
    const response = await GET(new NextRequest('http://localhost/api/geocode/address?q=885%20Sample'));
    const data = await response.json() as Array<{ postalCode: string; addressSelectionToken: string }>;

    expect(response.status).toBe(200);
    expect(data).toHaveLength(1);
    expect(data[0]?.postalCode).toBe('30316');
    expect(data[0]?.addressSelectionToken).toMatch(/\./);
  });

  test('allows state abbreviation matches in loose mode', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
      ok: true,
      json: async () => ([{
        lat: '36.7783',
        lon: '-119.4179',
        address: {
          state: 'California',
        },
      }]),
    }));

    const { GET } = await import('./route');
    const response = await GET(new NextRequest('http://localhost/api/geocode/address?q=CA&loose=1'));
    const data = await response.json() as Array<{
      displayName: string;
      city: string;
      state: string;
      postalCode: string;
      latitude: number;
      longitude: number;
    }>;

    expect(response.status).toBe(200);
    expect(data).toHaveLength(1);
    expect(data[0]).toMatchObject({
      displayName: 'CA',
      city: '',
      state: 'CA',
      postalCode: '',
      latitude: 36.7783,
      longitude: -119.4179,
    });
  });

  test('falls back to the U.S. Census geocoder for a street Nominatim cannot resolve', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce({ ok: true, json: async () => [] })
      .mockResolvedValueOnce({
        ok: true,
        json: async () => ({
          result: {
            addressMatches: [{
              matchedAddress: '1000 WIGWAM PKWY, HENDERSON, NV, 89074',
              coordinates: { x: -115.03147854692, y: 36.034042304037 },
              addressComponents: { city: 'HENDERSON', state: 'NV', zip: '89074' },
            }],
          },
        }),
      });
    vi.stubGlobal('fetch', fetchMock);

    const { GET } = await import('./route');
    const response = await GET(new NextRequest(
      'http://localhost/api/geocode/address?q=1000%20Wigwam%20Pkwy%20Ste.%20100,%20Henderson,%20NV%2089074&loose=1',
    ));
    const data = await response.json() as Array<{
      displayName: string;
      latitude: number;
      longitude: number;
    }>;

    expect(data).toEqual([expect.objectContaining({
      displayName: '1000 WIGWAM PKWY, HENDERSON, NV, 89074',
      latitude: 36.034042304037,
      longitude: -115.03147854692,
    })]);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  test('falls back to ZIP centroid lookup for loose ZIP-only searches', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce({ ok: true, json: async () => [] })
      .mockResolvedValueOnce({
        ok: true,
        json: async () => ({
          'post code': '90012',
          places: [{
            'place name': 'Los Angeles',
            'state abbreviation': 'CA',
            latitude: '34.0614',
            longitude: '-118.2385',
          }],
        }),
      });
    vi.stubGlobal('fetch', fetchMock);

    const { GET } = await import('./route');
    const response = await GET(new NextRequest(
      'http://localhost/api/geocode/address?q=90012&loose=1',
    ));
    const data = await response.json() as Array<{
      displayName: string;
      city: string;
      state: string;
      postalCode: string;
      latitude: number;
      longitude: number;
    }>;

    expect(data).toEqual([expect.objectContaining({
      displayName: 'Los Angeles, CA, 90012',
      city: 'Los Angeles',
      state: 'CA',
      postalCode: '90012',
      latitude: 34.0614,
      longitude: -118.2385,
    })]);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});
