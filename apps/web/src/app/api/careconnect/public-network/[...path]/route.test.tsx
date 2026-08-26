import { createHmac } from 'crypto';
import { afterEach, beforeEach, describe, expect, test, vi } from 'vitest';
import { NextRequest } from 'next/server';

const { getCookie } = vi.hoisted(() => ({
  getCookie: vi.fn(),
}));

vi.mock('next/headers', () => ({
  cookies: vi.fn().mockResolvedValue({
    get: getCookie,
  }),
}));

describe('/api/careconnect/public-network/[...path]', () => {
  const originalSecret = process.env.PublicTrustBoundary__InternalRequestSecret;

  beforeEach(() => {
    vi.resetModules();
    process.env.PublicTrustBoundary__InternalRequestSecret = 'test-public-secret';
    getCookie.mockReturnValue({ value: 'test-session-token' });
  });

  afterEach(() => {
    if (originalSecret === undefined) {
      delete process.env.PublicTrustBoundary__InternalRequestSecret;
    } else {
      process.env.PublicTrustBoundary__InternalRequestSecret = originalSecret;
    }
    vi.restoreAllMocks();
    getCookie.mockReset();
  });

  test('resolves tenant from the authenticated session before forwarding public network reads', async () => {
    const tenantId = '019ea7f6-21e9-7421-ab54-7846cdc6bc76';
    const fetchMock = vi.fn()
      .mockResolvedValueOnce({
        ok: true,
        json: async () => ({ tenantId, orgId: 'org-1', productRoles: [], systemRoles: [] }),
      })
      .mockResolvedValueOnce({
        status: 200,
        text: async () => JSON.stringify([{ id: 'network-1' }]),
        headers: new Headers({ 'Content-Type': 'application/json' }),
      });
    vi.stubGlobal('fetch', fetchMock);

    const { GET } = await import('./route');
    const request = new NextRequest(
      'http://careconnect-qa.legalsynq.net/api/careconnect/public-network/api/public/network?organizationId=org-1',
      { method: 'GET' },
    );

    const response = await GET(request, {
      params: Promise.resolve({ path: ['api', 'public', 'network'] }),
    });
    const body = await response.json();

    expect(response.status).toBe(200);
    expect(body).toEqual([{ id: 'network-1' }]);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(fetchMock.mock.calls[0][0]).toBe('http://127.0.0.1:5010/identity/api/auth/me');
    expect(fetchMock.mock.calls[0][1]?.headers).toMatchObject({
      Authorization: 'Bearer test-session-token',
    });
    expect(fetchMock.mock.calls[1][0]).toBe(
      'http://127.0.0.1:5010/careconnect/api/public/network?organizationId=org-1',
    );
    expect(fetchMock.mock.calls[1][1]?.headers).toMatchObject({
      'X-Tenant-Id': tenantId,
      'X-Tenant-Id-Sig': createHmac('sha256', 'test-public-secret').update(tenantId).digest('base64'),
    });
  });

  test('rejects non-public-network paths', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);

    const { GET } = await import('./route');
    const request = new NextRequest(
      'http://careconnect-qa.legalsynq.net/api/careconnect/public-network/api/public/referrals',
      { method: 'GET' },
    );

    const response = await GET(request, {
      params: Promise.resolve({ path: ['api', 'public', 'referrals'] }),
    });
    const body = await response.json();

    expect(response.status).toBe(404);
    expect(body).toEqual({ message: 'Not found.' });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  test('rejects organizationId values outside the caller scope for non-admin users', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce({
        ok: true,
        json: async () => ({
          tenantId: '019ea7f6-21e9-7421-ab54-7846cdc6bc76',
          orgId: 'org-1',
          productRoles: ['SYNQ_CARECONNECT:CARECONNECT_REFERRER'],
          systemRoles: [],
        }),
      });
    vi.stubGlobal('fetch', fetchMock);

    const { GET } = await import('./route');
    const request = new NextRequest(
      'http://careconnect-qa.legalsynq.net/api/careconnect/public-network/api/public/network?organizationId=org-2',
      { method: 'GET' },
    );

    const response = await GET(request, {
      params: Promise.resolve({ path: ['api', 'public', 'network'] }),
    });
    const body = await response.json();

    expect(response.status).toBe(403);
    expect(body).toEqual({ message: 'Forbidden.' });
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  test('allows tenant administrators to request another organization scope', async () => {
    const tenantId = '019ea7f6-21e9-7421-ab54-7846cdc6bc76';
    const fetchMock = vi.fn()
      .mockResolvedValueOnce({
        ok: true,
        json: async () => ({
          tenantId,
          orgId: null,
          productRoles: [],
          systemRoles: ['TenantAdmin'],
        }),
      })
      .mockResolvedValueOnce({
        status: 200,
        text: async () => JSON.stringify([{ id: 'network-1' }]),
        headers: new Headers({ 'Content-Type': 'application/json' }),
      });
    vi.stubGlobal('fetch', fetchMock);

    const { GET } = await import('./route');
    const request = new NextRequest(
      'http://careconnect-qa.legalsynq.net/api/careconnect/public-network/api/public/network?organizationId=org-2',
      { method: 'GET' },
    );

    const response = await GET(request, {
      params: Promise.resolve({ path: ['api', 'public', 'network'] }),
    });

    expect(response.status).toBe(200);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  test('requires a platform session cookie', async () => {
    getCookie.mockReturnValue(undefined);
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);

    const { GET } = await import('./route');
    const request = new NextRequest(
      'http://careconnect-qa.legalsynq.net/api/careconnect/public-network/api/public/network',
      { method: 'GET' },
    );

    const response = await GET(request, {
      params: Promise.resolve({ path: ['api', 'public', 'network'] }),
    });
    const body = await response.json();

    expect(response.status).toBe(401);
    expect(body).toEqual({ message: 'Unauthorized.' });
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
