# CareConnect Service

Healthcare provider directory, referral management, and appointment scheduling.

**Port:** 5003

## Responsibilities

- Provider network management (create, activate, search, geo-discovery)
- Global provider specialty catalog and provider-to-specialty assignment
- Referral lifecycle (Draft → Submitted → Accepted → Completed)
- Appointment scheduling against provider availability slots
- Attachment management for referrals and appointments
- Referral and appointment notes
- Notification delivery on key lifecycle events
- Configurable Referral Origination (referral-source tracking) and the anonymous
  Referral Portal (see "Referral Origination & Referral Portal" below)

## Layer Structure

```
CareConnect.Api/           Endpoints, middleware, Program.cs (port 5003)
CareConnect.Application/   Interfaces, DTOs, services
CareConnect.Domain/        Provider, Specialty, Referral, Appointment, Availability, Attachment,
                            ReferralAttribution, ReferralAttributionAccessCode
CareConnect.Infrastructure/ DbContext, repositories, EF migrations
CareConnect.Tests/         Tests
```

## Key Endpoints

| Method | Path | Auth | Description |
|---|---|---|---|
| `GET` | `/api/careconnect/providers` | Bearer | Search providers |
| `GET` | `/api/careconnect/providers/{id}` | Bearer | Provider detail |
| `GET` | `/api/specialties` | Bearer | List configured CareConnect specialties |
| `POST` | `/api/specialties` | PlatformAdmin | Create a global specialty |
| `PUT` | `/api/specialties/{id}` | PlatformAdmin | Update a global specialty |
| `DELETE` | `/api/specialties/{id}` | PlatformAdmin | Deactivate a global specialty |
| `POST` | `/api/careconnect/referrals` | Bearer | Create referral |
| `GET` | `/api/careconnect/referrals` | Bearer | List referrals with queue and participant filters |
| `GET` | `/api/assistant-tools/referrals/search` | Bearer | Assistant-only referral search surface |
| `GET` | `/api/assistant-tools/referrals/queue-summary` | Bearer | Assistant-only referral queue and KPI summary |
| `GET` | `/api/assistant-tools/referrals/{id}` | Bearer | Assistant referral lookup with recent history |
| `GET` | `/api/assistant-tools/referrals/{id}/history` | Bearer | Assistant referral history lookup |
| `GET` | `/api/assistant-tools/providers/search` | Bearer | Assistant-only provider lookup |
| `GET` | `/api/assistant-tools/referrers/search` | Bearer | Assistant-only referrer lookup |
| `GET` | `/api/careconnect/appointments` | Bearer | List appointments |
| `GET` | `/api/referral-attributions/options` | Bearer | Active Referral Origination options for the caller's tenant (Law Firm Portal dropdown) |
| `GET`/`POST`/`PATCH` | `/api/referral-attributions` | PlatformOrTenantAdmin | Tenant admin CRUD for Referral Origination options |
| `POST`/`PATCH`/`DELETE` | `/api/referral-representative-access-codes` | PlatformOrTenantAdmin | Tenant admin: generate/revoke a referral portal access code (no user selection) |
| `GET` | `/api/referral-representative-access-codes/by-attribution/{id}` | PlatformOrTenantAdmin | The single active code for one origination, or 204 if none — exactly one active code per origination is allowed |
| `POST` | `/api/public/referral-portal/verify` | Anonymous | Referral Portal — stateless access-code check, returns the named origination |
| `GET` | `/api/public/referral-portal/referrals` | Anonymous (`?code=`) | Referral Portal — paginated converted referral list, code re-verified per request |
| `GET` | `/api/public/referral-portal/referrals/{id}` | Anonymous (`?code=`) | Referral Portal — restricted converted referral detail |
| `GET` | `/api/public/referral-portal/referral-metrics` | Anonymous (`?code=`) | Referral Portal — dashboard metrics |
| `GET` | `/api/public/referral-portal/law-firms` | Anonymous (`?code=`) | Referral Portal — law firm selector options |
| `GET` | `/api/public/referral-portal/providers` | Anonymous (`?code=`) | Referral Portal — verified master provider list for recommendations |
| `GET` | `/api/public/referral-portal/providers/map` | Anonymous (`?code=`) | Referral Portal — verified provider map markers for recommendations |
| `GET` | `/api/public/referral-portal/pending-referrals` | Anonymous (`?code=`) | Referral Portal — paginated pending request list for that origination |
| `GET` | `/api/public/referral-portal/pending-referrals/{id}` | Anonymous (`?code=`) | Referral Portal — read one pending request scoped to the access code's origination |
| `POST` | `/api/public/referral-portal/pending-referrals` | Anonymous (`?code=`) | Referral Portal — submit a pending referral request to a law firm |
| `POST` | `/api/public/referral-portal/pending-referrals/{id}/attachments/upload` | Anonymous (`?code=`) | Referral Portal — upload a document attachment for a pending request |
| `GET`/`PUT`/`POST` | `/api/pending-referral-requests` | CARECONNECT_REFERRER or CARECONNECT_REFERRER_ADMIN | Law firm review queue; update request values, decline requests, and convert accepted requests |
| `POST` | `/api/pending-referral-requests/{id}/attachments/upload` | CARECONNECT_REFERRER or CARECONNECT_REFERRER_ADMIN | Law firm review queue — upload document attachments to a pending request before conversion |
| `GET` | `/api/pending-referral-requests/{id}/attachments/{attachmentId}/url` | CARECONNECT_REFERRER or CARECONNECT_REFERRER_ADMIN | Law firm review queue — get a short-lived pending request attachment URL |
| `POST` | `/api/careconnect/appointments` | Bearer | Book appointment |
| `GET` | `/api/public/careconnect/network` | Anonymous | Public provider network |
| `PUT` | `/api/networks/{networkId}/providers/{providerId}` | Bearer | Edit a provider from a tenant network after membership validation |
| `DELETE` | `/api/networks/{networkId}/providers/{id}` | Bearer | Soft-delete a provider-location network membership |
| `POST` | `/api/networks/{networkId}/providers/import` | Anonymous, loopback-only | CSV/XLSX provider migration/import into a tenant network |
| `GET`/`POST`/`DELETE` | `/api/law-firm-users[/invite\|/{userId}/resend-invite\|/{userId}/activate\|/{userId}/deactivate\|/{userId}/roles]` | CARECONNECT_REFERRER_ADMIN | Law Firm Company Admin (LSV3-1083) — list, invite/resend pending invites, activate/deactivate, and assign/revoke roles for a law firm's own users. Pending invitations are listed as `Invited`/`Invite sent` until accepted. No org-id route parameter: a caller always operates on their own organization only (TenantAdmin/PlatformAdmin can act on any org in the tenant). Proxies to Identity's internal `/api/internal/organizations/{organizationId}/users` endpoints, which independently re-verify org ownership. |

### Referral Origination & Referral Portal

User-facing screens refer to this feature as Referral Origination. The underlying API and schema
identifiers remain `ReferralAttribution*` / `cc_ReferralAttributions` for compatibility.

`ReferralAttribution` (`cc_ReferralAttributions`) is a tenant-scoped, configurable label for who or
what originated a referral (a representative, a campaign, a partner) — set on `referrals.ReferralAttributionId`
(nullable). It is optional on Law Firm Portal submission (blank default, never auto-selected) and,
once set, is immutable — set exactly once, at submission time
(`ReferralService.CreateAsync` / `ResolveAttributionForSubmissionAsync`). There is deliberately no
admin edit path for it; the admin referral view shows it read-only alongside the rest of the
referral's details.

`ReferralAttributionAccessCode` (`cc_ReferralAttributionAccessCodes`) grants referral portal
access via a generated code, not admin-typed user linking and not a login. A tenant admin generates a
code scoped to one origination (optionally bounded by `AccessStartAtUtc`/`AccessEndAtUtc`) and shares
it with the intended representative out of band; the code is revealed once, in the generate response,
and hashed (SHA-256 + `ReferralAttributionAccessCode:Pepper`) at rest — the plaintext is never
persisted. There is no "redeemer" and nothing is stamped when a code is used: the Referral Portal
is fully anonymous, and the associate simply presents the raw code on every request. The backend
re-verifies it from scratch each time (`IReferralAttributionAccessCodeService.VerifyAsync`,
stateless — no mutation), so a revoked code or a deactivated origination takes effect on the very next
request, not on next login (there is no login).

**Exactly one active code per origination.** `GenerateAsync` rejects a new code with
`ConflictException("ACTIVE_CODE_EXISTS")` (409) when one is already active for that origination —
`SetActiveAsync(isActive: false)` (revoke) must run first. MySQL has no filtered unique index to
enforce this at the schema level, so it's an application-layer check (`CountActiveAsync`); there is a
narrow TOCTOU window if two generate requests for the same origination land concurrently.

**Deactivating an origination cuts off its code's access immediately**, even if the code is otherwise
active and within its date window — `IsValidAt(nowUtc, attributionIsActive)` takes the origination's
current state as an explicit parameter (resolved via `IReferralAttributionRepository` in
`ReferralAttributionAccessCodeService.VerifyAsync`, never through the entity's own private-set
`ReferralAttribution` navigation property, which only EF's `Include()` can populate and would otherwise
make this check silently dependent on query shape). Reactivating the origination restores access
without a new code.

There is no product role, no login, and no platform session anywhere on this surface — the access
code is the sole credential, checked on every single request. `PublicRepresentativeEndpoints`
(`/api/public/referral-portal/*`, with `/api/public/representative/*` retained temporarily as a compatibility alias) is modeled directly on `PublicNetworkEndpoints`' anonymous pattern:
`.AllowAnonymous()`, rate-limited, and gated by the same two-layer trust boundary (gateway-secret +
HMAC-signed tenant ID) that the public provider directory uses — see `PublicTrustBoundary`
(`CareConnect.Api/Helpers/PublicTrustBoundary.cs`), extracted from `PublicNetworkEndpoints` so both
anonymous surfaces share one implementation. Unlike the public network directory — whose access-code
gate is a one-time, client-side-only UX unlock; the underlying data endpoints stay open regardless of
whether a code was ever verified — referral data is PII (client name, DOB, phone, email), so every
representative read re-verifies the caller's code server-side on every single call. Nothing is cached
and nothing is trusted from a prior request.

The frontend lives at `apps/web/src/app/careconnect/referral/` (`/careconnect/referral/*`;
`/careconnect/representative/*` redirects to it temporarily) — a top-level sibling of
`apps/web/src/app/careconnect/network/`, not under `app/(platform)/careconnect/`,
so it never inherits that route group's login-gated layout; structurally isolated from the admin shell —
it does not import `AppShell`/`PRODUCT_NAV`. It resolves the tenant from the request subdomain the
same way `/careconnect/network` does, and gates its pages behind `RepresentativeAccessCodeGate`
(`apps/web/src/components/careconnect/representative-access-code-gate.tsx`), which persists the raw
code client-side (not just an "unlocked" flag) and resends it on every data call via
`representative-portal-api.ts`. (An earlier iteration required the caller to be logged in and gated
the portal behind a `CARECONNECT_REFERRAL_REPRESENTATIVE` product role on top of the access code —
both were removed: the role reintroduced the same engineering-provisioning step the code model exists
to eliminate, and the login requirement contradicted the product's own "share a code, no account
needed" pitch.)

The tenant-admin configuration screen (`/careconnect/referral-attributions`) lives directly in the
main CareConnect product nav (`PRODUCT_NAV.careconnect` in `apps/web/src/lib/nav.ts`,
`adminOnly: true`), not under the separate `/careconnect/admin/*` area used by other operational
tooling (referral monitor, blocked-provider queue, provisioning). There is no separate "Referral
Representatives" nav item — the list at `/careconnect/referral-attributions` shows only First
Name / Last Name / Status plus a kebab menu (View, Activate/Deactivate); **View** navigates to
`/careconnect/referral-attributions/{id}`, which is where the full field set, the Edit action, and
the access-code widget (generate/revoke) all live. Folding the code-generation UI into the
origination's own detail page — rather than a standalone cross-origination admin page — is what
lets "one origination, one active code" be enforced simply, both in the UI (Generate only shows
when there's no active code) and in the API (the 409 conflict above).

Every representative-facing read is gated by the tenant feature flag before the code is even checked —
a tenant capability on the platform's existing capability store
(`careconnect.referral_representative_portal`, read via
`GET /api/v1/public/tenants/{tenantId}/capabilities/{capabilityKey}` on the Tenant service), disabled
by default.

Referral Portal submissions create `cc_PendingReferralRequests` rows instead of immediately creating
`cc_Referrals`. The pending request stores the selected law firm organization, locked access-code
origination, immutable `Origin = ReferralAssociate`, patient/referral details, lien company name/email,
zero or more preferred medical provider/location recommendations, and review status (`PendingReview`,
`Converted`, `Cancelled`). Preferred providers are advisory only: selecting them from the Referral
Portal's master provider list/map does not create a referral, does not notify any provider, and does
not bypass law-firm review. The portal persists ordered preferences in
`cc_PendingReferralProviderPreferences`; the legacy `RecommendedProvider*` columns mirror the first
preference for backward compatibility/default routing. Authenticated law-firm users with
`CARECONNECT_REFERRER` or `CARECONNECT_REFERRER_ADMIN` list their own organization's pending requests,
review all stored preferences, and convert one by selecting the final provider; conversion creates a normal referral, preserves
origination/origin/lien-company fields, and blocks repeat conversion. If the law firm converts without
selecting a different provider, the first stored preference can be used as the default conversion
target.
The law-firm review queue and Referral Portal request list are labeled Referral Requests in the UI and
expose outcome tabs for All, Pending, Accepted, and Declined requests. These map to an omitted status
filter, `PendingReview`, `Converted`, and `Cancelled` respectively; `Cancelled` is labeled Declined
because that is the state produced when a law-firm user declines a pending request.
The Referral Origination dashboard keeps request outcomes separate from converted referral statuses:
its Referral Requests breakdown counts `PendingReview`, `Converted`, and `Cancelled` requests, while
its Converted Referrals breakdown counts the routed referral's provider-facing lifecycle status.
Law-firm review screens, law-firm tokenized referral status links, and law-firm/referrer notification
email summaries display Referral Origination when it is present. Provider-facing referral emails and
provider thread links do not.
When a law-firm user converts a pending request, the created referral stores the processing user's
email/name plus the resolved law firm organization name as referrer notification metadata. Those fields
drive the provider email subject/body and the law-firm submission confirmation email.

Normal referrals now include immutable `Origin` (`LawFirm` for direct law-firm submissions and
`ReferralAssociate` for converted pending requests) plus optional `LienCompanyName` and
`LienCompanyEmail`. Provider network memberships include `OwningOrganizationId` and `Visibility`
(`Private` or `Public`); non-admin-created entries default to private, and only tenant/platform admins
may make a provider public.

**Single-tenant-network cutover (supersedes the original LSV3-1084 per-network-ownership design
below).** Each tenant now has exactly one shared `ProviderNetwork` — law firms no longer create their
own separate network. `POST /api/networks` (create) and `DELETE /api/networks/{id}` (delete) have been
removed from the API entirely; `GET /api/networks` bootstraps the tenant's one network on first access
via `NetworkService.GetOrCreateTenantNetworkAsync` if it doesn't exist yet, so callers always get a
network id back. `PUT /api/networks/{id}` (rename/describe) is now restricted to
`CARECONNECT_NETWORK_MANAGER` or a tenant/platform admin — a `CARECONNECT_REFERRER_ADMIN`-only caller
can no longer edit the shared network's own name/description at all (there's no "their own network"
carve-out anymore). The frontend's separate "Networks" (list of networks) admin tab has been retired.
Two role-scoped screens share the same `MyNetworkClient` component against this one network: "My Network"
(`/careconnect/my-network`, tenant portal, `CARECONNECT_NETWORK_MANAGER`/admin — unchanged from before the
cutover, includes the public Network URL box) and "Network Setup" (`/careconnect/network-setup`,
`CARECONNECT_REFERRER_ADMIN`/law-firm — labelled "Network Setup", scoped to providers owned by the
law firm's organization, and no Network URL box, since that's a tenant-level concern, not a per-law-firm
one). They were deliberately kept as two separate routes/nav entries rather than merged into one, so the
existing NetworkManager-facing screen's behavior stays untouched for its current users.

Per-provider ownership and visibility (`NetworkProvider.OwningOrganizationId`/`Visibility`) are now the
*sole* mechanism for "whose provider is this" and "who can see it" — and, critically, `Visibility` is
now actually enforced on every read path, not just stored/displayed as before. `ProviderVisibility.IsVisibleTo(np,
viewerOrgId, viewerSeesAll)` gates `NetworkService.GetByIdAsync`/`GetAllAsync`/`GetMarkersAsync` (internal,
authenticated) and `PublicNetworkEndpoints`'s `/providers`, `/providers/markers`, `/detail` (anonymous,
scoped by an optional `organizationId` query param): a `Private` provider is visible only to the
organization that owns it (or a caller who "sees all" — tenant/platform admin or NetworkManager); a
`Public` provider, or one with no recorded owner (legacy/tenant-created), is visible to everyone.

LSV3-1084 (historical): editing or removing a network provider (`PUT`/`DELETE /api/networks/{id}/providers/{providerId}`)
is restricted by `OwningOrganizationId` — a caller holding only `CARECONNECT_REFERRER_ADMIN` (not
`CARECONNECT_NETWORK_MANAGER`, and not a tenant/platform admin) may only edit or remove providers their own
organization added; any other provider in the network is view-only for them. NetworkManager and system
admin callers are unrestricted, as before. This part of LSV3-1084 is unchanged by the single-tenant-network
cutover — only the *network-level* (not provider-level) ownership rules were retired.

`ProviderNetwork.OwningOrganizationId` itself (added via the `EnsureSchemaObjects` runtime schema-repair
path in `CareConnect.Api/Program.cs`, not a classic EF migration — see that file's comments for why) is
retained on the schema but no longer written to a non-null value on new networks going forward; it is a
candidate for removal in a future cleanup migration once nothing reads it.

### Provider specialties

CareConnect has a global Specialty catalog that is separate from legacy provider categories. Categories remain in the
API for compatibility, but new provider setup and provider search behavior should use specialties.

- Default active specialties are seeded for Pain, Spine, Physical Therapy, Neuro, Imaging, Chiropractor, and Extremities.
- Providers must have at least one active specialty when they are created or edited through the provider APIs or the tenant network provider setup flow.
- Provider setup accepts an optional professional title (for example, `Dr.`) alongside first and last name; the single `Name` field remains a computed display string for existing consumers.
- Public provider enrollment prefills and submits that same optional title to Identity self-registration, where it is stored on `idt_Users.Title`.
- Multiple specialties are supported. The first selected specialty is treated as the primary specialty for list/detail display.
- Existing provider specialties are backfilled from provider categories when the category code/name maps to one of the seeded specialty values.
- Public network detail responses include active specialty options plus each provider's assigned specialties so public pages do not need a separate anonymous specialty lookup.

Platform administrators can configure the global catalog with `POST /api/specialties`, `PUT /api/specialties/{id}`,
and `DELETE /api/specialties/{id}`. `GET /api/specialties` returns active options by default and supports
`includeInactive=true` for administrative views.

### Provider locations

CareConnect treats `Provider` as the shared identity/profile record and `Facility` as the canonical location record.
NPI identifies the provider identity and remains unique when present. A provider with multiple locations should have
one provider row, one facility row per address, and one `ProviderFacility` link per provider-location pair.

Network membership is location-scoped through `cc_NetworkProviders.FacilityId`. The tenant network, public network,
and referral flows return one row/card/marker per provider-location membership and expose `networkProviderId`,
`providerId`, and `facilityId`. Frontend selection and public/authenticated referral submission should use
`networkProviderId`; the backend validates that the selected membership belongs to the tenant network and stores
`FacilityId` on the referral.

Shared registry search for tenant network setup also returns one result per provider facility so administrators can
add an existing location directly when a matched provider has multiple addresses.

Tenant network provider setup rejects duplicate provider creation by NPI or tenant email. Administrators should
search the shared registry first; if the provider already exists, the supported path for another address is the
explicit Add new location flow, which creates or reuses a `Facility` and adds a provider-location network membership
without creating another `Provider` row.

Tenant network provider editing is provider-scoped but location-aware: opening Edit from any provider-location row
shows all locations for that provider in the selected network. Provider title/name/organization and specialties remain
shared setup fields, while each facility row has its own facility name, contact, address, active flag, and accepting
referrals flag. Deleting a location is a membership soft delete: `DELETE /api/networks/{networkId}/providers/{id}`
marks that `cc_NetworkProviders` row inactive and not accepting referrals instead of removing the row. My Network keeps
inactive locations visible for edit/restore; public network payloads and provider counts include only active
provider-location memberships whose provider and facility are also active.

### Provider import

The provider import endpoint accepts CSV or XLSX uploads at `POST /api/networks/{networkId}/providers/import`.
It is intentionally unauthenticated (`.AllowAnonymous()`) and gated instead on the caller's raw TCP peer address —
only requests whose physical connection originates from loopback (`127.0.0.1`/`::1`) are allowed; everything else
gets `403`. The raw peer address is captured in `Program.cs` *before* `UseForwardedHeaders()` runs, so the gate
can't be bypassed by sending a spoofed `X-Forwarded-For: 127.0.0.1` through the trusted reverse proxy. In practice
this means: `curl` it directly on the box the service runs on (dev or prod), never through the gateway/LAN.
Each valid row creates or reuses a provider identity, creates or reuses a facility location, links the provider to
that facility, and links that provider-location pair to the network. Matching uses exact NPI first. Blank NPI rows
fall back to tenant email plus provider/facility context. Same NPI plus a different address creates another facility
and another network membership, not another provider.

The import accepts canonical headers and workbook-style headers. Required usable location fields are `email`, `phone`,
address, city, and state. ZIP is required per row unless that row is a mobile provider (see below), so the ZIP/`postalCode`
column itself is optional at the file level — a file made up entirely of mobile providers can omit it. `tenantId` is
optional when the file is imported through a specific network; missing row tenant IDs default to the target network
tenant, while supplied mismatched tenant IDs are rejected.
Provider name is recommended as discrete `Title`, `First Name`, and `Last Name` columns (canonical `title`/`firstName`/`lastName`).
The single-column `Medical Provider`/`providerName` header is still accepted for backward compatibility — when supplied
without discrete columns, it's split into title/first/last (leading `Dr.`/`Mr.`/`Mrs.`/`Ms.`/`Prof.` becomes `title`, the
last remaining token becomes `lastName`, everything else becomes `firstName`); discrete `Title`/`First Name`/`Last Name`
values always take precedence when both are present in the same file. If no provider name is resolved, `Medical Facility`
becomes the organization-level provider identity. `Medical Facility` maps to `Facility.Name` and provider organization
name. Address columns map to `Facility`; `Address 2` is appended to `Address 1` during parsing because CareConnect
currently has one facility street-address field. `NPI` maps only to `Provider.Npi`.

Specialty values may be codes or names such as `Pain`, `Spine`, `Physical Therapy`, `Neuro`, `Imaging`,
`Chiropractor`, and `Extremities`; `Chiro` is normalized to `Chiropractor`. Category/provider-type columns are still
accepted for compatibility and are used as a specialty fallback when no specialty column is supplied.

Optional import columns include `title`, `categoryCodes`, `primaryCategoryCode`, `primarySpecialtyCode`, `latitude`,
`longitude`, and `geoPointSource`. `geoPointSource` is normalized to the supported values `Manual`, `Geocoded`,
`Imported`, or `CityCentroid`; common geocoder labels such as `nominatim` are treated as `Geocoded`, and coordinate
rows with no source default to `Imported`. The current sample is `artifacts/postman/careconnect-provider-import.sample.csv`.

A row for a mobile/roaming provider with no fixed street address (e.g. a mobile clinic that
covers a metro area) can set `Mobile` to `Y`/`true` — this makes ZIP optional for that row (still
required otherwise) and stores the address column as a free-text service-area label (e.g.
"Greater Las Vegas Metro") instead of a street address. `ServiceRadius` (miles) is optional and
defaults to 25; both are capped at `ProviderGeoHelper.ServiceRadiusMilesCap` (60 miles).

### Provider search and distance

Authenticated provider search accepts `specialtyCode` plus ZIP-backed geospatial filters:

- `specialtyCode` filters providers by assigned specialty code.
- `lat`, `lng`, and `radius` filter provider locations by `Facility.Latitude`/`Facility.Longitude` when available, with provider coordinates kept as a compatibility fallback. The repository narrows by bounding box, calculates exact Haversine distance in miles, filters by the requested radius, and sorts matching results by distance.
- Tenant portal ZIP controls geocode ZIP/address input through `/api/geocode/address?loose=1`, then send the derived `lat`, `lng`, and `radius` query params to provider search.

Selected-network public/common pages (`/careconnect/browse-networks/{id}` and `/careconnect/network`) filter the
already-selected network client-side by ZIP and specialty. ZIP search geocodes the entered ZIP/address, displays a
search-location map pin, filters provider-location rows without usable coordinates when a search center is active,
calculates and displays miles from the search point, and sorts provider cards and map markers nearest to farthest.
Users can clear or change ZIP and specialty filters without reloading the page.

### Referral list and lookup filters

`GET /api/careconnect/referrals` supports the standard paging inputs plus assistant-friendly filtering fields:

- `search` tokenizes natural-language phrases and matches across patient/client name, referrer contact, law-firm name, provider name, and provider organization name
- `providerName` to narrow results to a specific receiving provider or provider organization
- `referrerName` to narrow results to a referrer contact, law firm, or referring organization
- Existing queue filters such as `status`, `createdFrom`, `createdTo`, `providerId`, `referrerUserId`, and page params

These read-only filters are used by the tenant portal and by CareConnect's dedicated assistant-tool API. Xenia now
calls the assistant-only endpoints under `/api/assistant-tools/*` instead of composing results from the end-user
referral and provider APIs itself.

`GET /api/assistant-tools/referrals/queue-summary` also accepts assistant KPI filters for count-style questions:

- `status` for a single canonical referral status
- `statusGroup` for assistant-friendly groups: `new`, `open`, or `closed`
- `days` for relative windows such as "last 7 days"
- `createdFrom` and `createdTo` for explicit date ranges

The response includes total visible referrals, counts within the requested window, matching count after any
status/status-group filter, status breakdowns, and recent matching referrals for grounding.

### Assistant tool API

CareConnect owns its grounded assistant contract for referral and provider workflows. The assistant-only endpoints:

- Reuse the caller's normal bearer-token access and participant scoping
- Return tool-shaped JSON for referral lookup/history, referral search, provider search, referrer search, and queue/KPI summaries
- Keep product-specific lookup composition inside CareConnect instead of in Xenia

### Referral documents and cross-tenant access

Authenticated referral document endpoints support the same authorized cross-tenant participant lookup as referral
details and comments. This is required when a multi-tenant referrer submits to a provider network whose tenant differs
from the referrer's currently selected JWT tenant. After the referral is resolved and participant access is verified,
CareConnect uses the referral's owning `TenantId` for Documents service calls and `cc_ReferralAttachments` persistence.
Tenant administrators remain scoped to their selected tenant; only platform administrators have an administrative
global lookup bypass.

This behavior applies consistently to document upload, document listing, and signed-URL retrieval under
`/api/referrals/{referralId}/attachments/*`.

### Referral message attachments

Referral comment endpoints accept both the existing JSON body for text-only comments and `multipart/form-data` when
message-scoped attachments are included:

- Public token flow: `POST /api/public/referrals/thread/comments?token=...` with `senderType`, `message`, and repeated `files`
- Authenticated flow: `POST /api/referrals/{referralId}/comments` with `message` and repeated `files`

A comment must include message text, at least one attachment, or both. Message text is limited to 4000 characters.
Each message can include up to 10 files. File size and MIME validation reuse the service's existing attachment upload
settings, currently 50 MB per file with the configured PDF, image, Office document, text, and CSV allowlist.

Files are uploaded to the Documents service with `referenceType = "referral-comment"`. CareConnect stores only
attachment metadata in `cc_ReferralAttachments`, linked to the creating comment by `ReferralCommentId`. Thread reads
return these attachments on each comment, but the general referral documents list excludes message-scoped attachments.
Clients should open files only through signed URL endpoints:

- Authenticated: `/api/referrals/{referralId}/attachments/{attachmentId}/url`
- Public token: `/api/referrals/{referralId}/public-attachments/{attachmentId}/url?token=...`

## Product Roles

| Role | Access |
|---|---|
| `CARECONNECT_REFERRER` | Send referrals, find providers, book appointments |
| `CARECONNECT_RECEIVER` | Receive referrals, manage appointments, manage availability |
| `CARECONNECT_NETWORK_MANAGER` | Manage a tenant's own provider network (role-based, not orgType-based — assignable to Lien Owner and Law Firm orgs) |
| `CARECONNECT_REFERRER_ADMIN` | Law-firm-scoped admin role — can send/read/process the firm's referrals, manage network/provider directory data, and administer the firm's CareConnect users. Assignable to Law Firm orgs only. |

Migrated Law Firm sessions whose JWT still contains `CARECONNECT_REFERRER` are accepted for
referral status-processing actions until the user receives a refreshed token with
`CARECONNECT_REFERRER_ADMIN`. This compatibility path is limited to `LAW_FIRM` org
tokens and referral accept/decline/status-update permissions.

Authenticated referral creation is enforced in the handler through `CareConnectAuthHelper`
after product and organization product filters run. That keeps migrated law-firm users from
being denied by stale JWT permission claims while still requiring a CareConnect product role
that grants `referral:create`.

### LSV3-1083: Law Firm Company Admin

A `CARECONNECT_REFERRER_ADMIN` is the designated admin for their own Law
Firm organization. At `/careconnect/law-firm-users` (web) they can view, invite,
resend pending invitations, activate/deactivate, and assign/revoke CareConnect roles for the users belonging to
their own firm — restricted to the `CARECONNECT_REFERRER` and `CARECONNECT_REFERRER_ADMIN`
role codes only. Visibility and mutation are both scoped to the caller's own
`OrganizationId`; a TenantAdmin/PlatformAdmin may act on any org in the tenant.

Since CareConnect has no local user domain, `CareConnect.Api`'s `/api/law-firm-users/*`
endpoints (`LawFirmUserEndpoints.cs`) enforce the org-ownership check and then proxy to
new **internal, provisioning-token-secured** endpoints on Identity
(`/api/internal/organizations/{organizationId}/users/*`, in
`Identity.Api/Endpoints/LawFirmUserManagementEndpoints.cs`), which independently
re-validates the role allow-list and re-derives org membership rather than trusting the
caller's check. See `HttpIdentityOrganizationService`'s law-firm-user methods for the
CareConnect → Identity call shape. Identity increments the affected user's `AccessVersion`
when these internal role assignments are added or revoked so refreshed sessions and profile
views stop showing stale CareConnect roles.

## Database

`CareConnectDb` (MySQL).

## External Integrations

- **Identity service** — provider provisioning via `CareConnectProvisioningHandler` (registered in Identity's product provisioning pipeline)
- **Audit service** — all key events published
- **Notifications service** — referral and appointment event notifications
