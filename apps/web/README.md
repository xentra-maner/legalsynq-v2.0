# Tenant Portal (`apps/web`)

The main product application used by end users (law firms, healthcare providers, funding organisations).

**Port:** 5000 (dev proxy) → internal Next.js on 3050

## Tech

- Next.js 16.2.6 App Router, TypeScript, Tailwind CSS, React 18
- Local development uses the monorepo/root pnpm install path. Production runtime artifacts are packaged with `package.json`, `pnpm-lock.yaml`, and `pnpm-workspace.yaml`, and the app manifest pins `packageManager: pnpm@10.26.1` so Corepack does not drift to a newer pnpm during `pnpm install --production`.

## Auth & Session

- Login: `POST /api/auth/login` → BFF sets `platform_session` HttpOnly cookie
- Session validation: `GET /api/auth/me` (BFF, server-side) — frontend never decodes raw JWTs
- Logout: `POST /api/auth/logout`

## BFF Pattern

All API calls from the browser go through Next.js API routes that:
1. Read `platform_session` cookie
2. Exchange it for a Bearer token
3. Forward the request to the gateway

Client code uses relative `/api/` paths — rewrite in `next.config` maps to the gateway.

For SynqLien document views, current Documents-service references
(`/documents/{guid}`) continue through the BFF's tokenized view-url flow.
For migrated SL-CORE object keys, the BFF resolves a tenant-scoped legacy link
through Liens and redirects only to the exact HTTPS
`legal-dmm-prod.legalsynq.com` host; no browser code handles legacy URLs.

## E2E Tests

See [`e2e/README.md`](e2e/README.md) for how to run them day to day (`--ui`, `--debug`,
environments, credentials setup). To add a new test, use the `create-e2e-test` skill.

## Dev Proxy (`scripts/dev-proxy.js`)

Sits in front of Next.js at port 5000. Gates browser requests until Next.js returns HTTP 200 on `/login` (warm-up guard). Serves an auto-refreshing loading page during the 30-second cold-compile window. WebSocket passthrough for HMR.

## Key Directories

```
src/
  app/                  Next.js App Router pages
    (platform)/         Route group: authenticated product pages
      careconnect/      CareConnect referrals, appointments, providers
      lien/             SynqLien cases, liens, marketplace, tasks
      fund/             SynqFund applications
      insights/         Reports catalog, viewer, builder, schedules
      tenant/           Tenant administration (users, groups, access)
    api/                BFF route handlers
  components/           Shared UI components (careconnect/, fund/, lien/, shell/)
  lib/                  API clients, service layers, auth guards
    cases/              Cases API service layer (types, api, mapper, service)
    liens/              Liens API service layer + task/workflow/notes layers
    servicing/          Servicing API service layer
    documents/          Documents API service layer
    notifications/      Notifications API service layer
    reports/            Reports API service layer (types, api, service)
    provider-mode/      Org config / sell vs manage mode
    unified-activity/   Merged audit + notification activity feed
    role-access/        buildRoleAccess() — action-level role checks
    bulk-operations/    executeBulk() framework for multi-select operations
  hooks/                useSession, useRoleAccess, useProviderMode, useSelectionState
  providers/            SessionProvider, TenantBrandingProvider
  stores/               Zustand lien store (legacy V1 prototype data)
  types/                Shared TypeScript DTOs
```

## Products & Roles

| Role | Access |
|---|---|
| `CARECONNECT_REFERRER` | Find providers, send referrals, book appointments |
| `CARECONNECT_RECEIVER` | Receive referrals, manage appointments |
| `SYNQFUND_REFERRER` | Submit funding applications |
| `SYNQFUND_FUNDER` | Review and decide on funding applications |
| `SYNQLIEN_SELLER` | Create and manage liens |
| `SYNQLIEN_BUYER` | Browse marketplace, submit offers, purchase liens |
| `SYNQLIEN_HOLDER` | View held portfolio |
| `TenantAdmin` | Tenant user/group/permission management |
| `PlatformAdmin` | All tenants + platform admin |

### CareConnect Referral Portal

`/careconnect/referral/*` is the anonymous, access-code Referral Portal for referral associates.
`/careconnect/representative/*` redirects there temporarily for compatibility. `/careconnect/referral/submit`
submits pending referral requests to a selected law firm; `/careconnect/pending-requests` is the
authenticated law-firm review queue where `CARECONNECT_REFERRER` users select a provider and convert
pending requests into normal referrals. Authenticated and public referral submission forms include optional
lien company name/email fields, and referral detail surfaces display the immutable origin and lien company data.

## Environment

`apps/web/.env.local` (gitignored):
```
NEXT_PUBLIC_ENV=development
NEXT_PUBLIC_TENANT_CODE=LEGALSYNQ
GATEWAY_URL=http://127.0.0.1:5010
```

### CareConnect common portal (AUTH-CC01)

Two additional env vars are required when hosting the CareConnect common portal on a separate hostname (e.g. `careconnect.legalsynq.com`):

| Variable | Example | Purpose |
|---|---|---|
| `CC_COMMON_PORTAL_HOSTNAME` | `careconnect.legalsynq.com` | Hostname the BFF uses to detect a common-portal request and set `resolveByEmail=true`. Must match the hostname the reverse proxy routes to this Next.js instance. |
| `NotificationsService__CareConnectPortalBaseUrl` | `https://careconnect.legalsynq.com` | Identity service config. The base URL used to build password-reset links for CC users. Set in `Identity.Api/appsettings.json` or as an environment override. |

If `CC_COMMON_PORTAL_HOSTNAME` is unset, the CC forgot-password path is silently disabled (a startup warning is logged). See `apps/gateway/README.md` for the required proxy header-stripping rules.

### Tenant registration common portal

Tenant self-registration has a dedicated common-portal hostname, following the
same environment naming convention as the product portals.

| Variable | Example / Default | Purpose |
|---|---|---|
| `TENANT_COMMON_PORTAL_HOSTNAME` | `tenant-demo.localhost` | Exact hostname for tenant self-registration. Requests to `/` on this hostname redirect to `/register`; the registration page and submission endpoint return `404` on every other hostname. Use `tenant-qa.legalsynq.com` in QA and `tenant-demo.legalsynq.com` in demo. |

For local development, open `http://tenant-demo.localhost:3000` when running
`pnpm --dir apps/web dev`, or use port `5000` with the full development stack.

### SynqLien funding common portal

The SynqLien funding-company common portal uses the same Identity-backed `platform_session` cookie as CareConnect common portal login, but it serves buyer-side SynqLien users from `/funding/*`.

| Variable | Example / Default | Purpose |
|---|---|---|
| `SYNQLIEN_COMMON_PORTAL_HOSTNAME` | `synqlien-demo.localhost` | Hostname the BFF uses to detect SynqLien common-portal login and send `resolveByEmail=true` with `portalProductCode=SYNQ_LIENS`. Root `/` redirects to `/funding/dashboard`. |
| `PORTAL_SYNQLIEN_SUBDOMAIN` | `synqlien-demo` | Subdomain that renders the SynqLien-branded `/login` layout and defaults successful login to `/funding/dashboard`. |

Use the same hostname as the Liens buyer-offer email CTA:

```bash
SYNQLIEN_COMMON_PORTAL_HOSTNAME=synqlien-demo.localhost
PORTAL_SYNQLIEN_SUBDOMAIN=synqlien-demo
```

Eligibility is enforced in Identity and again in the web route layout: users must have SynqLien product access and only the `SYNQ_LIENS:SYNQLIEN_BUYER` role for SynqLien. Any other SynqLien role, including `SYNQ_LIENS:SYNQLIEN_HOLDER` or `SYNQ_LIENS:SYNQLIEN_SELLER`, and any platform/tenant system role is rejected for the funding portal.

Implemented routes:

| Route | Purpose |
|---|---|
| `/funding/dashboard` | Funding dashboard with KPI summary, pending offers, acquisition pipeline, and Offer Inbox. |
| `/funding/notifications` | Funding-company notification center backed by offered-lien activity, with search/status filters, unread highlighting, empty states, and links into lien details. The header bell shows a recent-activity popover; read state is browser-local until Liens exposes persistent notification receipts. |
| `/funding/offered-liens` | Server-rendered offered-liens list with search, status filters, pagination, and API-authorized row actions. Pending offers expose View, Accept, and Decline actions; response actions require confirmation and show accepted/declined completion feedback. |
| `/funding/offered-liens/{accessLinkId}` | Authenticated offered-lien detail page with Overview, Documents, and Messages tabs backed by real Liens service data. Its Messages tab posts to the same offer thread as the public email link, and its Actions menu uses the same confirmed accept/decline response flow as the list through the Liens workflow. |
| `/selling/public/{token}` | Public, token-gated buyer or seller-view offer page opened from `New Lien Offer` emails; rendered by `apps/web` from Liens JSON without a `platform_session` cookie. Buyer-audience links include accept/decline buttons; seller-audience links are read-only and show buyer/funding-company details. |
| `/selling/public/{token}/activate` | Public SynqLien buyer account activation page for buyer-audience links. Prefills and locks available buyer contact data from the lien offer, then creates or links a `SYNQ_LIENS:SYNQLIEN_BUYER` login through Liens and Identity. |
| `/api/lien/api/liens/selling/public/{token}` | Public BFF path for the Liens JSON data endpoint and response/account-activation actions. Accept/decline posts use `/api/lien/api/liens/selling/public/{token}/{action}` and account activation uses `/api/lien/api/liens/selling/public/{token}/activate-account`; seller-view tokens are rejected for those mutation paths, so browser traffic always runs through the tenant portal BFF before reaching the gateway. |

The frontend does not include mock rows. Server components target Liens endpoints through the gateway:

| Frontend server request | Liens service endpoint after gateway prefix removal |
|---|---|
| `/liens/api/liens/selling/buyer/dashboard?range=last7Days\|last30Days\|custom&from=&to=` | `/api/liens/selling/buyer/dashboard` |
| `/liens/api/liens/selling/buyer/liens?status=&search=&page=&pageSize=&sort=&direction=` | `/api/liens/selling/buyer/liens` |
| `/liens/api/liens/selling/buyer/liens/{accessLinkId}` | `/api/liens/selling/buyer/liens/{accessLinkId}` |
| `/api/lien/api/liens/selling/buyer/liens/{accessLinkId}/documents/{documentId}/view` | `/api/liens/selling/buyer/liens/{accessLinkId}/documents/{documentId}/view` |
| `/api/lien/api/liens/selling/buyer/liens/{accessLinkId}/documents/{documentId}/download` | `/api/liens/selling/buyer/liens/{accessLinkId}/documents/{documentId}/download` |
| `/api/lien/api/liens/selling/buyer/liens/{accessLinkId}/messages` | `/api/liens/selling/buyer/liens/{accessLinkId}/messages` |
| `/api/lien/api/liens/selling/buyer/liens/{accessLinkId}/accept` | `/api/liens/selling/buyer/liens/{accessLinkId}/accept` |
| `/api/lien/api/liens/selling/buyer/liens/{accessLinkId}/decline` | `/api/liens/selling/buyer/liens/{accessLinkId}/decline` |

The `/api/lien/[...path]` BFF forwards the browser `Host` and protocol to Liens as
`x-legal-synq-public-host` and `x-legal-synq-public-proto`, so public reply links in SynqLien message emails point back
to the portal host the user is actually using instead of the internal gateway host.

If the dashboard endpoint is unavailable during rollout, the funding portal converts only `404`, `501`, and `204` responses into semantic empty states. `401`, `403`, and `5xx` remain auth/error states.
