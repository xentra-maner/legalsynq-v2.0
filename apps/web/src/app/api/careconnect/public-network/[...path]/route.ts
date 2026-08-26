import { createHmac } from 'crypto';
import { cookies } from 'next/headers';
import { type NextRequest, NextResponse } from 'next/server';

const GATEWAY_URL = process.env.GATEWAY_URL ?? 'http://127.0.0.1:5010';
const INTERNAL_REQUEST_SECRET =
  process.env['PublicTrustBoundary__InternalRequestSecret'] ??
  process.env.INTERNAL_REQUEST_SECRET ??
  '';

type RouteContext = { params: Promise<{ path: string[] }> };

interface AuthMeResponse {
  tenantId?: string;
  orgId?: string | null;
  productRoles?: string[];
  systemRoles?: string[];
}

function signTenantId(tenantId: string): string {
  if (!INTERNAL_REQUEST_SECRET) return '';
  return createHmac('sha256', INTERNAL_REQUEST_SECRET).update(tenantId).digest('base64');
}

function hasScopedOrganizationAccess(session: AuthMeResponse, organizationId: string | null): boolean {
  if (!organizationId) return true;

  const normalizedOrgId = organizationId.toLowerCase();
  if (session.orgId?.toLowerCase() === normalizedOrgId) return true;

  const systemRoles = session.systemRoles ?? [];
  if (systemRoles.includes('PlatformAdmin') || systemRoles.includes('TenantAdmin')) return true;

  return (session.productRoles ?? []).some(role =>
    role === 'SYNQ_CARECONNECT:CARECONNECT_NETWORK_MANAGER' || role === 'CARECONNECT_NETWORK_MANAGER');
}

async function resolveSession(token: string): Promise<AuthMeResponse | null> {
  try {
    const res = await fetch(`${GATEWAY_URL}/identity/api/auth/me`, {
      headers: {
        Authorization: `Bearer ${token}`,
        'Content-Type': 'application/json',
      },
      cache: 'no-store',
    });

    if (!res.ok) return null;
    const body = await res.json() as AuthMeResponse;
    if (!body.tenantId?.trim()) return null;
    return body;
  } catch {
    return null;
  }
}

async function proxy(request: NextRequest, { params }: RouteContext): Promise<NextResponse> {
  if (!['GET', 'HEAD'].includes(request.method)) {
    return NextResponse.json({ message: 'Method not allowed.' }, { status: 405 });
  }

  const { path: pathSegments } = await params;
  const relativePath = pathSegments.join('/');
  if (relativePath !== 'api/public/network' && !/^api\/public\/network\/[^/]+\/detail$/.test(relativePath)) {
    return NextResponse.json({ message: 'Not found.' }, { status: 404 });
  }

  const cookieStore = await cookies();
  const token = cookieStore.get('platform_session')?.value;
  if (!token) {
    return NextResponse.json({ message: 'Unauthorized.' }, { status: 401 });
  }

  const session = await resolveSession(token);
  if (!session) {
    return NextResponse.json({ message: 'Tenant could not be resolved.' }, { status: 400 });
  }

  const qs = request.nextUrl.searchParams.toString();
  const organizationId = request.nextUrl.searchParams.get('organizationId')?.trim() || null;
  if (!hasScopedOrganizationAccess(session, organizationId)) {
    return NextResponse.json({ message: 'Forbidden.' }, { status: 403 });
  }

  const tenantId = session.tenantId!.trim();
  const url = `${GATEWAY_URL}/careconnect/${relativePath}${qs ? `?${qs}` : ''}`;
  const sig = signTenantId(tenantId);

  let gatewayRes: Response;
  try {
    gatewayRes = await fetch(url, {
      method: request.method,
      headers: {
        'X-Tenant-Id': tenantId,
        ...(sig ? { 'X-Tenant-Id-Sig': sig } : {}),
      },
    });
  } catch {
    return NextResponse.json({ message: 'Gateway unavailable' }, { status: 503 });
  }

  const responseBody = await gatewayRes.text();
  const resHeaders: Record<string, string> = {
    'Content-Type': gatewayRes.headers.get('Content-Type') ?? 'application/json',
  };
  const correlationId = gatewayRes.headers.get('X-Correlation-Id');
  if (correlationId) resHeaders['X-Correlation-Id'] = correlationId;

  const isNullBodyStatus = gatewayRes.status === 204 || gatewayRes.status === 205 || gatewayRes.status === 304;

  return new NextResponse(isNullBodyStatus ? null : responseBody, {
    status: gatewayRes.status,
    headers: resHeaders,
  });
}

export const GET = proxy;
export const HEAD = proxy;
