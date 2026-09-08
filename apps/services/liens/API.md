# Liens Service API Documentation

## Table of Contents

- [Overview](#overview)
- [Authentication & Authorization](#authentication--authorization)
- [Permissions Reference](#permissions-reference)
- [Common Models](#common-models)
- [Error Responses](#error-responses)
- [Liens](#liens-endpoints)
- [Cases](#cases-endpoints)
- [Bills of Sale](#bills-of-sale-endpoints)
- [Lien Offers](#lien-offers-endpoints)
- [Selling In-App Notifications](#selling-in-app-notifications)
- [Contacts](#contacts-endpoints)
- [Settlement Reductions](#settlement-reduction-endpoints)
- [Settlement Payments](#settlement-payment-endpoints)
- [Servicing](#servicing-endpoints)
- [Reports](#reports-endpoints)
- [User Management](#user-management)

---

## Overview

Base URL prefix: `/api/liens`

All endpoints in the Liens service are JSON-based (except document download endpoints which return files). Request and response bodies use `application/json` content type unless otherwise noted.

---

## Authentication & Authorization

Every endpoint requires:

1. **Authenticated user** — the caller must be an authenticated user (policy: `AuthenticatedUser`).
2. **Product access** — the caller must have access to the `SYNQ_LIENS` product.
3. **Endpoint-specific permission** — each endpoint requires a specific permission as listed in the tables below.

Requests missing authentication receive a `401 Unauthorized` response.

## User Management

`/api/liens/user-management/{**path}` is the public SynqLien facade for the current law-firm organization. It forwards Users, Invitations, Options, and Roles operations to Identity using an audience-bound service token. Tenant/actor/organization scope is never accepted from the browser body; Identity remains authoritative and returns RFC 7807 errors with stable `synqlien.*` codes for forbidden, conflict, and validation cases. Every state-changing request requires an `Idempotency-Key`; Liens stores the request digest and completed upstream response in its durable idempotency table so safe retries cannot repeat the Identity mutation or invitation email. If Identity does not return a response, Liens records `synqlien.identity_outcome_unknown`; abandoned in-progress records later return `idempotency_outcome_unknown`, and callers must inspect current state before starting a new operation with a new key.

---

## Permissions Reference

| Permission Code | Description |
|---|---|
| `SYNQ_LIENS.lien:read` | Read liens, bills of sale, and lien offers |
| `SYNQ_LIENS.lien:create` | Create new liens |
| `SYNQ_LIENS.lien:update` | Update existing liens; accept lien offers |
| `SYNQ_LIENS.lien:offer` | Create lien offers |
| `SYNQ_LIENS.lien:service` | Manage bills of sale lifecycle (submit/execute/cancel); manage contacts and servicing items |
| `SYNQ_LIENS.case:read` | Read cases |
| `SYNQ_LIENS.case:create` | Create new cases |
| `SYNQ_LIENS.case:update` | Update existing cases |

The following permissions are defined in the system but not currently used by any API endpoint:

| Permission Code | Description |
|---|---|
| `SYNQ_LIENS.lien:read:own` | Read own liens |
| `SYNQ_LIENS.lien:browse` | Browse liens |
| `SYNQ_LIENS.lien:purchase` | Purchase liens |
| `SYNQ_LIENS.lien:read:held` | Read held liens |
| `SYNQ_LIENS.lien:settle` | Settle liens |

---

## Common Models

### PaginatedResult\<T\>

All list/search endpoints return results wrapped in this paginated envelope.

| Field | Type | Description |
|---|---|---|
| `items` | `T[]` | Array of result items for the current page |
| `page` | `integer` | Current page number |
| `pageSize` | `integer` | Number of items per page |
| `totalCount` | `integer` | Total number of matching items across all pages |

---

## Error Responses

### 401 Unauthorized

Returned when the request lacks valid authentication credentials.

### 403 Forbidden

Returned when the user is authenticated but does not have the required product access (`SYNQ_LIENS`) or the endpoint-specific permission.

### 404 Not Found

Returned when a requested resource does not exist.

```json
{
  "error": {
    "code": "not_found",
    "message": "Resource description not found."
  }
}
```

### Common Status Codes

In addition to the endpoint-specific success and error codes documented below, **every** endpoint may return:

| Status | Condition |
|---|---|
| `401 Unauthorized` | Missing or invalid authentication |
| `403 Forbidden` | Authenticated but lacking required product access or permission |

**Per-endpoint status code summary:**

| Endpoint Type | Success | Possible Errors |
|---|---|---|
| List / Search (`GET` returning paginated results) | `200 OK` | `401`, `403` |
| Get by ID / Get by number (`GET` returning single item) | `200 OK` | `401`, `403`, `404` |
| Create (`POST`) | `201 Created` | `401`, `403` |
| Update / Action (`PUT` or `POST` on `{id}`) | `200 OK` | `401`, `403`, `404` |
| Document download (`GET` returning file) | `200 OK` | `401`, `403`, `404` |

---

## Liens Endpoints

Base path: `/api/liens/liens`

### GET `/api/liens/liens`

Search and list liens with optional filters.

Buying-facing lien list responses exclude liens in `Rejected`, `Declined`, or `Cancelled` status and normalize the remaining statuses to `Open` or `Closed`. Selling-specific workflow statuses remain available on selling endpoints and on direct lien detail responses.
All liens API timestamp responses are serialized in U.S. Pacific time (`-07:00` or `-08:00` depending on DST). Legacy string-formatted timestamps use the same Pacific conversion.
`LienResponse.purchaseDate` and `LienResponse.initialServiceDate` are formatted as `MM/dd/yyyy` when present.

**Permission:** `SYNQ_LIENS.lien:read`

**Query Parameters:**

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `search` | `string` | No | `null` | Free-text search filter |
| `status` | `string` | No | `null` | Filter by lien status |
| `lienType` | `string` | No | `null` | Filter by lien type |
| `caseId` | `guid` | No | `null` | Filter by associated case ID |
| `facilityId` | `guid` | No | `null` | Filter by facility ID |
| `sortBy` | `string` | No | `null` | Sort field. Supports `purchaseDate`, `isServicing`, `amountReceived`, and the existing lien list sort fields. |
| `sortDirection` | `string` | No | `asc` | Sort direction: `asc` or `desc`. |
| `page` | `integer` | No | `1` | Page number |
| `pageSize` | `integer` | No | `20` | Items per page |

**Response:** `200 OK`

```json
PaginatedResult<LienResponse>
```

---

### GET `/api/liens/liens/{id}`

Get a lien by its unique identifier.

**Permission:** `SYNQ_LIENS.lien:read`

**Path Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `id` | `guid` | Lien unique identifier |

**Response:** `200 OK` — `LienResponse`

**Error:** `404 Not Found` — if the lien does not exist.

---

### GET `/api/liens/liens/by-number/{lienNumber}`

Get a lien by its lien number.

**Permission:** `SYNQ_LIENS.lien:read`

**Path Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `lienNumber` | `string` | Lien number |

**Response:** `200 OK` — `LienResponse`

**Error:** `404 Not Found` — if the lien does not exist.

---

### POST `/api/liens/liens`

Create a new lien.

**Permission:** `SYNQ_LIENS.lien:create`

**Request Body: `CreateLienRequest`**

| Field | Type | Required | Nullable | Description |
|---|---|---|---|---|
| `lienNumber` | `string` | Yes | No | Unique lien number |
| `externalReference` | `string` | No | Yes | External reference identifier |
| `lienType` | `string` | Yes | No | Type of lien |
| `caseId` | `guid` | No | Yes | Associated case ID |
| `facilityId` | `guid` | No | Yes | Associated facility ID |
| `originalAmount` | `decimal` | Yes | No | Original lien amount |
| `jurisdiction` | `string` | No | Yes | Jurisdiction |
| `isConfidential` | `boolean` | Yes | No | Whether the lien is confidential |
| `subjectFirstName` | `string` | No | Yes | Subject first name |
| `subjectLastName` | `string` | No | Yes | Subject last name |
| `incidentDate` | `date` | No | Yes | Date of incident (format: `YYYY-MM-DD`) |
| `description` | `string` | No | Yes | Description |

**Response:** `201 Created` — `LienResponse`

Returns the created lien with a `Location` header pointing to `/api/liens/liens/{id}`.

---

### PUT `/api/liens/liens/{id}`

Update an existing lien.

**Permission:** `SYNQ_LIENS.lien:update`

**Path Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `id` | `guid` | Lien unique identifier |

**Request Body: `UpdateLienRequest`**

| Field | Type | Required | Nullable | Description |
|---|---|---|---|---|
| `externalReference` | `string` | No | Yes | External reference identifier |
| `lienType` | `string` | Yes | No | Type of lien |
| `caseId` | `guid` | No | Yes | Associated case ID |
| `facilityId` | `guid` | No | Yes | Associated facility ID |
| `originalAmount` | `decimal` | Yes | No | Original lien amount |
| `jurisdiction` | `string` | No | Yes | Jurisdiction |
| `isConfidential` | `boolean` | No | Yes | Whether the lien is confidential |
| `subjectFirstName` | `string` | No | Yes | Subject first name |
| `subjectLastName` | `string` | No | Yes | Subject last name |
| `incidentDate` | `date` | No | Yes | Date of incident (format: `YYYY-MM-DD`) |
| `description` | `string` | No | Yes | Description |

**Response:** `200 OK` — `LienResponse`

**Error:** `404 Not Found` — if the lien does not exist.

---

### LienResponse

| Field | Type | Nullable | Description |
|---|---|---|---|
| `id` | `guid` | No | Unique identifier |
| `lienNumber` | `string` | No | Lien number |
| `externalReference` | `string` | Yes | External reference |
| `lienType` | `string` | No | Type of lien |
| `status` | `string` | No | Current status. Buying list endpoints exclude `Rejected`, `Declined`, and `Cancelled` liens and normalize remaining values to `Open` or `Closed`; direct lien detail responses may still return workflow statuses used by selling flows. |
| `caseId` | `guid` | Yes | Associated case ID |
| `sellingCaseId` | `guid` | Yes | Original Selling case ID when moved to Liens Management |
| `facilityId` | `guid` | Yes | Associated facility ID |
| `originalAmount` | `decimal` | No | Original lien amount |
| `currentBalance` | `decimal` | Yes | Current balance |
| `offerPrice` | `decimal` | Yes | Current offer price |
| `purchasePrice` | `decimal` | Yes | Purchase price |
| `payoffAmount` | `decimal` | Yes | Payoff amount |
| `jurisdiction` | `string` | Yes | Jurisdiction |
| `isConfidential` | `boolean` | No | Confidentiality flag |
| `subjectFirstName` | `string` | Yes | Subject first name |
| `subjectLastName` | `string` | Yes | Subject last name |
| `subjectDisplayName` | `string` | Yes | Computed subject display name |
| `orgId` | `guid` | No | Owning organization ID |
| `sellingOrgId` | `guid` | Yes | Selling organization ID |
| `buyingOrgId` | `guid` | Yes | Buying organization ID |
| `holdingOrgId` | `guid` | Yes | Holding organization ID |
| `sellerStatus` | `string` | Yes | Selling workflow status, including `Internal` |
| `incidentDate` | `date` | Yes | Date of incident |
| `description` | `string` | Yes | Description |
| `openedAtUtc` | `datetime` | Yes | When the lien was opened |
| `closedAtUtc` | `datetime` | Yes | When the lien was closed |
| `createdAtUtc` | `datetime` | No | Record creation timestamp |
| `updatedAtUtc` | `datetime` | No | Record last-updated timestamp |

---

## Selling Endpoints

Base path: `/api/liens/selling`

### Case-first Selling intake

Selling lien creation is a two-step case workflow. First call `POST /case-drafts` with
`accidentTypeId`, `accidentState`, `dateOfLoss`, `handlingLawFirmId`, `caseManagerId`, and
`caseTrackingNotes`; Selling defaults the case status to `PreDemand`. Update an unfinalized draft with
`PUT /case-drafts/{draftId}`. Then call
`POST /case-drafts/{draftId}/plaintiff` with `firstName`, `lastName`, `birthdate`, `email`, `phone`,
`gender`, `address`, `city`, `state`, and `zipcode`; this atomically creates the canonical case and
returns `caseId`. Draft creation and plaintiff finalization do not require `Idempotency-Key`.

After finalization, the update workflow uses the same two steps: `PUT /cases/{caseId}` accepts the
case-information fields from `POST /case-drafts`, then `PUT /cases/{caseId}/plaintiff` accepts the plaintiff
fields from `POST /case-drafts/{draftId}/plaintiff`. Both routes only update finalized Selling cases owned by the
authenticated tenant and seller organization.

`GET /cases/{caseId}` returns those same case-information and plaintiff fields for a finalized Selling case,
including `draftId`, `caseId`, and `caseNumber`. It is limited to the authenticated tenant and seller organization.

`POST /liens` now requires that returned `caseId`, plus `sellerStatus` (`Pending` or `Internal`) and an
optional `source`. The route rejects cases outside the authenticated tenant/seller organization.

`GET /liens/{lienId}` returns lifecycle-driven `availableActions`. It includes `keep` exactly when the lien
is eligible for `move-to-management-v2`; already-managed, archived, sold, settled, and withdrawn liens do not advertise it.

`PUT /liens/{lienId}/lien-information` requires `sellerStatus` and `listingVisibility`. Omitted
`initialServiceDate`, `endServiceDate`, `receivableDueDate`, and `notes` fields preserve their current values;
supplying one of those fields as `null` clears its current value.

`PUT /liens/{lienId}/case-information` is now restricted to lien-owned `fundingCompanyId`,
`fundingCompanyContactId`, `facilityId`, and `medicalProviderId`; it no longer accepts `caseId`,
`createCaseIfMissing`, `handlingLawFirmId`, or `caseManagerId`. `facilityId` may reference either an active
seller-owned legacy facility or an active seller-owned Company Directory Medical Facility.

`GET /lookups/document-types` returns the fixed Selling document codes `MedicalBill`, `MedicalRecord`,
`LienAgreement`, `SettlementStatement`, `Other`, `ItemizedBill`, `HCFA-1500`, `SignedLien`, and
`LetterOfProtection`.

### GET `/api/liens/selling/liens/{lienId}/messages`

Returns the authenticated seller's persisted message thread for a seller-scoped lien. The endpoint is tenant- and
seller-organization-scoped and returns every persisted offer-thread message for the lien across buyer contacts.

**Permission:** `SYNQ_LIENS.lien_sale:read`

**Response:** `200 OK`

```json
{
  "items": [
    {
      "id": "message-guid",
      "senderType": "buyer",
      "senderName": "Buyer Reviewer",
      "senderInitials": "BR",
      "senderEmail": "buyer@capital.test",
      "message": "Can you confirm the signed LOP is final?",
      "createdAtUtc": "2026-07-28T12:30:00Z",
      "isCurrentUser": false,
      "attachments": [
        {
          "id": "attachment-guid",
          "fileName": "signed-lop.pdf",
          "contentType": "application/pdf",
          "fileSizeBytes": 245760,
          "createdAtUtc": "2026-07-28T12:30:00Z",
          "viewUrl": "/api/selling/api/liens/selling/liens/{lienId}/message-attachments/{attachment-guid}/view",
          "downloadUrl": "/api/selling/api/liens/selling/liens/{lienId}/message-attachments/{attachment-guid}/download"
        }
      ]
    }
  ]
}
```

### POST `/api/liens/selling/liens/{lienId}/messages`

Posts a message from the authenticated seller detail page into the same persisted offer thread used by the public
buyer/seller links and authenticated funding-company portal. The lien must already have an offer/access-link thread
with a buyer; otherwise the endpoint returns `409 message_thread_unavailable`. Seller messages use
`senderType=seller` and notify the buyer with the same `lien.offer.message.created` email workflow used by public
seller replies. The email body displays the persisted message `createdAtUtc` timestamp converted to U.S. Pacific time.

**Permission:** `SYNQ_LIENS.lien_sale:update`

**Request:**

Text-only messages may be sent as JSON:

```json
{
  "message": "The LOP is final and attached to the package."
}
```

Messages with attachments are sent as `multipart/form-data` with a `message` field and repeated `files` parts:

```text
message=The LOP is final and attached to the package.
files=@signed-lop.pdf
files=@supporting-bill.pdf
```

Message text is trimmed and limited to 400 characters. Either text or at least one file is required. Up to 10 files are
accepted per message; each file uses the service upload limit and must be one of `.pdf`, `.jpg`, `.jpeg`, `.png`,
`.docx`, `.xlsx`, `.xls`, or `.csv`. Attachments are stored in Documents with
`referenceType=SellingPortalMessage` and returned as message-scoped metadata with same-origin view/download URLs.

**Response:** `201 Created`, same message shape as the public message endpoint.

### GET `/api/liens/selling/liens/{lienId}/message-attachments/{attachmentId}/view`

Issues a short-lived Documents view access URL for a message attachment on the authenticated seller's lien, then
redirects to that URL. The endpoint enforces the seller's tenant, seller organization, lien, and attachment ownership.

**Permission:** `SYNQ_LIENS.lien_sale:read`

**Response:** `302 Found`

### GET `/api/liens/selling/liens/{lienId}/message-attachments/{attachmentId}/download`

Same validation and ownership checks as the authenticated message-attachment view endpoint, but requests a Documents
download URL.

**Permission:** `SYNQ_LIENS.lien_sale:read`

**Response:** `302 Found`

### POST `/api/liens/selling/liens/{lienId}/move-to-management`

Moves a Selling lien into Liens Management without creating a second lien record. Existing same-tenant,
same-organization case and lien data remain on the same records; the `caseId` is preserved in `sellingCaseId`
and `sellerStatus` becomes `Internal`. When no case exists, the API creates a same-tenant, same-organization
management case from the lien information, falling back to `Jane Doe` when no plaintiff/client name is present.

**Permission:** `SYNQ_LIENS.lien_sale:update`

**Header:** `Idempotency-Key` is required.

```json
{
  "reason": "Retained internally"
}
```

Every lien shown on the Selling **Pending** tab is eligible, including `Pending`, `Approval`, `PreparedForSale`,
and `SubmittedForSale`. Submitted liens are atomically withdrawn, buyer access revoked, and pending offers
withdrawn before they become internal. Management receives the Selling billing amount as its billing total and
the Selling ask amount as its purchase total. The lien purchase date is set to the UTC calendar date on which the
move completes. Existing Management medical-code rows are retained when no Selling-pricing rows exist. For a
historical single blank compatibility row, the Management medical-code response recovers code, description, and
amounts from the retained Selling-pricing row and lien totals. Canonical medical-facility and medical-provider selections are projected through reusable legacy
facility/contact records into Management's `LegacyMedicalFacilityInfo` compatibility record; the record is created
when it does not already exist, so both selectors resolve immediately through the Management APIs. The funding company is resolved from
the authenticated tenant's organization name. An active canonical Funding Company with that name is reused, or created
and linked to the tenant when absent, then exposed through the same Management compatibility record. Existing `Internal`
liens and legacy draft liens with no seller status remain eligible for backward compatibility.

### POST `/api/liens/selling/liens/{lienId}/move-to-management-v2`

Moves a seller-scoped lien into Liens Management by setting `SellerStatus=Internal` and linking the lien to a
tenant/seller-owned case. The same canonical case link is used by Selling and Liens Management, and the chosen case is
also persisted on the lien's `sellingCaseId` reference. If the lien already has a valid case, that case is reused. Otherwise, when
`caseInfo` is provided, the API first searches the same tenant and seller organization for an existing case with the
same first name, last name, DOB, and date of loss. Matching an existing case is not an error: the lien is added to that
case and processing continues. If no match exists, a new case is created from `caseInfo`; if `caseInfo` is omitted, a
generic `Jane Doe` case is created. When the lien already has a seller-owned case, supplied `caseInfo` updates that
same case while preserving phone, email, insurance, policy, claim, description, medical-status, tracking, and canonical
party fields that are outside the move request.

Submitted-for-sale liens are first returned to draft Selling state, active buyer links are revoked, pending buyer offers
are expired, and then the lien is moved to Internal. Selling medical-pricing rows are copied into Management
`LegacyMedicalCode` rows so Management billing and purchase totals match Selling billing and target-sale amounts.
The original lien record retains its purchase/service/due dates, notes, financial values, and canonical company links.
All lien-scoped servicing and Selling document-reference rows are associated with the resolved Management case, while
funding-company, medical-facility, and medical-provider details are projected into Management's compatibility read model.
Facility/provider IDs in that read model reference the legacy records understood by Management while the lien retains
its canonical Company Directory associations.

**Permission:** `SYNQ_LIENS.lien_sale:update`

**Headers:**

| Header | Required | Description |
|---|---|---|
| `Idempotency-Key` | Yes | Suppresses duplicate move processing for the same caller/lien |

**Request:**

```json
{
  "reason": "Keep internally",
  "caseInfo": {
    "clientFirstName": "Jane",
    "clientLastName": "Doe",
    "clientDob": "1990-01-15",
    "clientAddress": "123 Main St",
    "clientCity": "Los Angeles",
    "clientState": "CA",
    "clientZipCode": "90001",
    "isServicing": true,
    "statusLabel": "Pre-demand",
    "accidentTypeId": "MVA",
    "stateOfIncident": "CA",
    "dateOfIncident": "2026-08-01",
    "lawFirmId": "guid-or-code",
    "caseManagerId": "guid-or-code",
    "notes": "Brief case notes"
  }
}
```

`caseInfo` is optional. When supplied, `clientFirstName`, `clientLastName`, `clientDob`, `statusLabel`,
`accidentTypeId`, `stateOfIncident`, and `lawFirmId` are required. Duplicate matching only runs when first name,
last name, DOB, and date of loss are all supplied.

**Response:** `200 OK`

```json
{
  "lienId": "guid",
  "caseId": "guid",
  "sellingCaseId": "guid",
  "caseCreated": false,
  "caseNumber": "SC-01A03CDAC567",
  "sellerStatus": "Internal",
  "status": "Draft",
  "message": "Lien moved to management and added to an existing case."
}
```

### POST `/api/liens/selling/liens/{lienId}/confirm-sale`

Confirms a prepared seller lien for sale. The endpoint moves a draft/prepared lien to `Offered` with
`SellerStatus=SubmittedForSale`, copies the persisted `AskAmount` into `OfferPrice`, and keeps `SoldAtUtc` null.

**Permission:** `SYNQ_LIENS.lien_sale:update`

**Headers:**

| Header | Required | Description |
|---|---|---|
| `Idempotency-Key` | No | Used with tenant/lien/buyer/seller contacts to suppress duplicate notification sends on replay |

**Request:**

```json
{
  "confirmationAccepted": true
}
```

Notification delivery is mandatory and cannot be opted out through request payload. The lien must have real
`FundingCompanyId`, `FundingCompanyContactId`, `InitialServiceDate`, `AskAmount`, buyer email, seller
organization display, active seller Company Directory contact-person data, seller notification email, and handling law firm data. Buyer-facing seller name is the
`idt_Users.FirstName` + `LastName` display name for the seller user who confirms/submits the offer
(`SellingBuyerAccessLinks.CreatedByUserId` / confirm-sale acting user), scoped to the seller organization when Identity
validates membership. Seller company represents the selling
organization (`sellerOrgId`) resolved from Identity, with fallback to active `liens_CompanyContactPersons` joined through
active `liens_Companies` in that seller organization. Handling law firm and case manager names stay in
the asset/case fields and are not used as the seller display. Handling law firm is the selected standalone law-firm
contact's `liens_Contacts.Organization` value, falling back to `DisplayName` for legacy or incomplete firm records. In
buyer and seller notification Asset Overview sections, Contact Person, Email Address, and Handling Law Firm all come
from that selected contact: `liens_Contacts.FirstName` + `liens_Contacts.LastName`, `liens_Contacts.Email`, and the
organization/display-name value. Creating a standalone law firm without a separate organization value persists its
display name as the organization.
The seller notification's Buyer Information section omits buyer phone number. The public-link JSON and authenticated funding-company
views use the same seller-user and seller organization resolver. The API creates a 30-day buyer response access link and a separate
30-day seller-view access link from
`Liens:Selling:BuyerPortalBaseUrl`; callers do not provide CTA URLs. If the explicit base URL is absent, the API
derives it from `SYNQLIEN_COMMON_PORTAL_HOSTNAME`; `synqlien-demo.localhost` resolves to
`http://synqlien-demo.localhost:5000/selling/public` for the full `scripts/run-dev.sh` proxy. The configured buyer
portal base URL must be absolute and must match the active tenant-web browser origin; use
`http://synqlien-demo.localhost:3000/selling/public` when running only `pnpm --dir apps/web dev`. Literal loopback hosts
such as `localhost` or `127.0.0.1` are rejected because the email CTA must work from the recipient's inbox, while named
`.localhost` aliases such as `synqlien-demo.localhost` are allowed for local demo runs. The buyer email uses the
`New Lien Offer` copy with a response CTA. The seller receives the same branded format with buyer/funding-company
information and a `View Lien Details` CTA. Neither email inserts sample
document data; both include only real supporting document names found in lien/case document metadata. The LegalSynq mark
and Figma-matched section icons are sent as inline CID image attachments; no remote placeholder assets are required.
For a CTA hosted by the tenant portal, use
`Liens__Selling__BuyerPortalBaseUrl=http://<portal-host>:<web-port>/selling/public` for local demo runs, or
`https://<portal-host>/selling/public` behind a real portal domain; that public browser route renders in `apps/web`,
fetches the Liens JSON endpoint through the gateway, and does not require a `platform_session` cookie. The confirm-sale email disables SendGrid click tracking for this
CTA so the recipient receives the real LegalSynq portal URL instead of a provider tracking URL.

Local SynqLien demo portal example:

```bash
SYNQLIEN_COMMON_PORTAL_HOSTNAME=synqlien-demo.localhost
PORTAL_SYNQLIEN_SUBDOMAIN=synqlien-demo
Liens__Selling__BuyerPortalBaseUrl=http://synqlien-demo.localhost:5000/selling/public
# or, when only apps/web dev is running:
Liens__Selling__BuyerPortalBaseUrl=http://synqlien-demo.localhost:3000/selling/public
```

**Response:** `200 OK`

```json
{
  "lienId": "guid",
  "lienCode": "LIEN-001",
  "status": "Offered",
  "sellerStatus": "SubmittedForSale",
  "askAmount": 2500.00,
  "offerPrice": 2500.00,
  "submittedForSaleAtUtc": "2026-07-22T00:00:00Z",
  "soldAtUtc": null,
  "notification": {
    "requested": true,
    "submitted": true,
    "notificationId": "guid",
    "notificationStatus": "sent",
    "buyerAccessLinkId": "guid",
    "buyerPortalUrl": "<configured-buyer-portal-url>/<token>",
    "expiresAtUtc": "2026-08-21T00:00:00Z",
    "buyerContactId": "guid",
    "buyerOrgId": "guid",
    "buyerEmail": "<buyer-contact-email>"
  },
  "sellerNotification": {
    "requested": true,
    "submitted": true,
    "notificationId": "guid",
    "notificationStatus": "sent",
    "sellerAccessLinkId": "guid",
    "sellerPortalUrl": "<configured-buyer-portal-url>/<seller-token>",
    "expiresAtUtc": "2026-08-21T00:00:00Z",
    "sellerContactId": "guid",
    "sellerOrgId": "guid",
    "sellerEmail": "<seller-notification-email>"
  }
}
```

If notification submission fails after the lien is confirmed, the lien transition remains committed and
`notification.submitted=false` reports the buyer-email failure for retry. The seller email is skipped unless the buyer
email is submitted or already submitted; in that case `sellerNotification.notificationStatus` is `skipped`. If seller
email submission itself fails, `sellerNotification.submitted=false` reports the failure without rolling back the lien
transition or buyer notification.

### GET `/api/liens/selling/buyer/dashboard`

Returns the authenticated funding-company dashboard used by `/funding/dashboard`. The endpoint scopes data to active
tenant buyer contacts whose email matches the authenticated user and whose contact type is `FundingCompany` or
`LienHolder`, then includes only access links where `BuyerContactId` matches one of those contacts.

Summary metrics are buyer-scoped totals across the selected dashboard range:

| Field | Definition |
|---|---|
| `totalLienPendingCount` | Count of buyer access links with no buyer response |
| `totalLienPendingAmount` | Sum of original lien amounts for pending rows |
| `totalPendingOfferCount` | Count of pending buyer offers |
| `totalPendingOfferedAmount` | Sum of pending ask/response offer amounts |
| `purchasedLienCount` | Count of accepted buyer responses |
| `capitalDeployedAmount` | Sum of accepted response amounts, falling back to ask amount |

`summary.trends` contains one trend per KPI card: `totalLienPending`, `totalPendingOffered`, `purchasedLiens`, and
`capitalDeployed`. Each trend compares current calendar month activity with the previous full calendar month and returns
`value` as the absolute percent delta, `direction` as `up`, `down`, or `flat`, and `label` as the previous-month range
shown by the portal.

`range=last7Days|last30Days|custom`, `from=yyyy-MM-dd`, and `to=yyyy-MM-dd` filter summary metrics, pending offers,
acquisition pipeline stages, provider performance, and offer inbox data. Range filtering uses the offer received
timestamp for pending, accepted, and declined rows so the dashboard matches the offered-liens received date. Custom
ranges require both `from` and `to`; missing or invalid custom dates return empty dashboard data.

`pendingOffers` returns at most five pending offers for the dashboard preview within the selected range.
`providerPerformance` returns at most five provider groups within the selected range, ordered by highest `lienCount`
first and then by `providerName`.

**Permission:** `SYNQ_LIENS.lien:browse` or the `SYNQLIEN_BUYER` product role when role fallback is enabled.

**Response:** `200 OK`

```json
{
  "summary": {
    "totalLienPendingCount": 1,
    "totalLienPendingAmount": 9000.00,
    "totalPendingOfferCount": 1,
    "totalPendingOfferedAmount": 2500.00,
    "purchasedLienCount": 1,
    "capitalDeployedAmount": 2500.00,
    "trends": {
      "totalLienPending": {
        "value": 8.9,
        "direction": "up",
        "label": "vs Apr 1 - Apr 30"
      },
      "totalPendingOffered": {
        "value": 6.4,
        "direction": "up",
        "label": "vs Apr 1 - Apr 30"
      },
      "purchasedLiens": {
        "value": 14.2,
        "direction": "up",
        "label": "vs Apr 1 - Apr 30"
      },
      "capitalDeployed": {
        "value": 5.0,
        "direction": "down",
        "label": "vs Apr 1 - Apr 30"
      }
    }
  },
  "pendingOffers": [
    {
      "id": "access-link-guid",
      "lienNumber": "LIEN-001",
      "providerName": "Sunrise Clinic",
      "sellerCompany": "RL Liens1",
      "sellerName": "Seller Processor",
      "offeredAmount": 2500.00,
      "receivedAtUtc": "2026-07-28T12:00:00Z",
      "responseDueAtUtc": "2026-08-27T12:00:00Z",
      "status": "Pending",
      "detailHref": "/funding/offered-liens/<access-link-guid>"
    }
  ],
  "pipelineStages": [
    {
      "key": "pending",
      "label": "Pending",
      "count": 1,
      "totalAmount": 2500.00,
      "conversionRatePercent": null
    }
  ],
  "providerPerformance": [
    {
      "providerId": "facility-guid",
      "providerName": "Sunrise Clinic",
      "lienCount": 2,
      "offeredAmount": 5000.00,
      "acceptedAmount": 2500.00,
      "averageResponseHours": 4.5
    }
  ],
  "offerInbox": {
    "pendingCount": 1,
    "unreadCount": 0,
    "latestReceivedAtUtc": "2026-07-28T12:00:00Z"
  }
}
```

### GET `/api/liens/selling/buyer/liens`

Returns offered-liens rows for the authenticated SynqLien buyer/funding company. The endpoint reads confirmed buyer
access links created by seller confirm-sale notifications and scopes results to active tenant buyer contacts whose email
matches the authenticated user and whose contact type is `FundingCompany` or `LienHolder`. Only access links where
`BuyerContactId` matches one of those contacts are returned, which supports accounts provisioned from public buyer
activation without exposing another contact's offers from the same buyer organization.

**Permission:** `SYNQ_LIENS.lien:browse` or the `SYNQLIEN_BUYER` product role when role fallback is enabled.

**Query Parameters:**

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `status` | `string` | No | `null` | `Pending`, `Accepted`, or `Declined`; omit or use `All` for every status |
| `search` | `string` | No | `null` | Case-insensitive search across lien number, seller, provider, status, dates, amounts, external reference, and subject name |
| `page` | `integer` | No | `1` | 1-based page number |
| `pageSize` | `integer` | No | `10` | Items per page, clamped from 1 to 100 |
| `sort` | `string` | No | `receivedAtUtc` | `lienNumber`, `sellerName`, `initialServiceDate`, `billingAmount`, `askAmount`, `highestBidAmount`, or `status` |
| `direction` | `string` | No | `asc` | `asc` or `desc`; default endpoint ordering is newest received offer first when `sort` is omitted |

**Response:** `200 OK`

```json
{
  "rows": [
    {
      "id": "access-link-guid",
      "lienNumber": "LIEN-001",
      "providerName": "Sunrise Clinic",
      "sellerName": "Seller Processor",
      "initialServiceDate": "2026-05-01",
      "serviceDate": "2026-05-01",
      "billingAmount": 9000.00,
      "originalAmount": 9000.00,
      "askAmount": 2500.00,
      "highestBidAmount": null,
      "highestBid": null,
      "offeredAmount": 2500.00,
      "receivedAtUtc": "2026-07-28T12:00:00Z",
      "status": "Pending",
      "responseDueAtUtc": "2026-08-27T12:00:00Z",
      "allowedActions": ["view", "accept", "decline"],
      "detailHref": "/funding/offered-liens/<access-link-guid>"
    }
  ],
  "page": 1,
  "pageSize": 10,
  "total": 1
}
```

`status` is derived from `SellingBuyerAccessLinks.ResponseStatus`: missing response is `Pending`, accepted responses are
`Accepted`, and declined responses are `Declined`. Pending rows expose `view`, `accept`, and `decline` actions only
while the underlying lien remains actionable by the same public buyer-response rules; responded or otherwise
non-actionable rows expose `view` only.

### GET `/api/liens/selling/buyer/liens/{accessLinkId}`

Returns the authenticated funding-company detail view for one offered lien. The `{accessLinkId}` is the `id` returned
by `GET /api/liens/selling/buyer/liens`; access is scoped to the authenticated buyer contact matched by email, using the
same `BuyerContactId` filtering as the list endpoint.
The `submittedAtUtc` field uses the lien's submitted-for-sale timestamp when present, serialized with the DST-aware
U.S. Pacific offset, and falls back to notification or access-link creation time only for legacy rows without a
sale-submission timestamp.
The `notes` field returns the persisted lien notes shown in the seller portfolio; lien description is used only when
those notes are blank.

**Permission:** `SYNQ_LIENS.lien:browse` or the `SYNQLIEN_BUYER` product role when role fallback is enabled.

**Response:** `200 OK`

```json
{
  "id": "access-link-guid",
  "lienId": "lien-guid",
  "lienNumber": "LIEN-001",
  "title": "Seller Processor",
  "subtitle": "RL Liens1",
  "seller": {
    "name": "Seller Processor",
    "company": "RL Liens1",
    "email": null
  },
  "buyer": {
    "contactName": "Buyer Reviewer",
    "company": "Capital Fund LLC",
    "email": "buyer@capital.test",
    "phone": "3105551212"
  },
  "providerName": "Sunrise Clinic",
  "status": "Pending",
  "submittedAtUtc": "2026-07-28T12:00:00Z",
  "initialServiceDate": "2026-05-01",
  "endServiceDate": "2026-05-31",
  "billingAmount": 9000.00,
  "askAmount": 2500.00,
  "highestBidAmount": null,
  "responseAmount": null,
  "notes": "Persisted lien notes",
  "responseDueAtUtc": "2026-08-27T12:00:00Z",
  "responseStatus": null,
  "responseNotes": null,
  "respondedAtUtc": null,
  "allowedActions": ["view", "accept", "decline"],
  "documents": [
    {
      "id": "servicing-item-guid",
      "fileName": "signed-lien.pdf",
      "category": "Lien Document",
      "sizeOrType": "PDF",
      "url": "/documents/document-guid",
      "viewUrl": "/api/lien/api/liens/selling/buyer/liens/{access-link-guid}/documents/{document-guid}/view",
      "downloadUrl": "/api/lien/api/liens/selling/buyer/liens/{access-link-guid}/documents/{document-guid}/download",
      "createdAtUtc": "2026-07-28T12:00:00Z"
    }
  ],
  "messages": [
    {
      "id": "message-guid",
      "senderType": "buyer",
      "senderName": "Buyer Reviewer",
      "senderInitials": "BR",
      "senderEmail": "buyer@capital.test",
      "message": "Please review the signed lien package.",
      "createdAtUtc": "2026-07-28T12:00:00Z",
      "isCurrentUser": true
    }
  ],
  "activity": [
    {
      "id": "accesslinkguid-response",
      "label": "Pending -> Accepted",
      "occurredAtUtc": "2026-07-28T13:00:00Z",
      "notes": "Accepted after review"
    }
  ]
}
```

`documents`, `messages`, and `activity` are returned only from persisted records. Funding-company document rows include
uploaded document servicing metadata attached to the offered lien: `LegacyCaseDocument`, `LegacyLienDocument`,
`LegacyMedicalDocument`, and seller-wizard `SellingDocumentReference` rows. Document categories resolve from canonical
`documentTypeId` metadata through the tenant's active `DocumentCategory` lookup when available, then fall back to
document metadata such as `documentType`. These arrays are empty when no matching servicing documents, portal messages,
or buyer response activity exist. `allowedActions` exposes `accept` and `decline`
only when the access link has not recorded a response and the lien itself is still actionable. `viewUrl` and
`downloadUrl` are same-origin tenant-portal BFF paths for authenticated funding-portal document access. They are `null`
when the servicing item does not contain a resolvable Documents-service id.

### GET `/api/liens/selling/buyer/liens/{accessLinkId}/documents/{documentId}/view`

Issues a short-lived Documents view access token for a document attached to an authenticated offered lien, then
redirects to the Documents access route. The endpoint validates the same buyer-contact-scoped access link as the detail
endpoint before minting the Documents token. Documents not attached to the offered lien return
`404 document_not_found`.

**Permission:** `SYNQ_LIENS.lien:browse` or the `SYNQLIEN_BUYER` product role when role fallback is enabled.

**Response:** `302 Found`

`Location` points to `/documents/access/{accessToken}` when called through the gateway. The tenant portal BFF path
`/api/lien/api/liens/selling/buyer/liens/{accessLinkId}/documents/{documentId}/view` rewrites that redirect to
`/api/lien/documents/access/{accessToken}` for same-origin browser access.

### GET `/api/liens/selling/buyer/liens/{accessLinkId}/documents/{documentId}/download`

Same validation and ownership checks as the authenticated offered-lien document view endpoint, but requests a Documents
download access token.

**Permission:** `SYNQ_LIENS.lien:browse` or the `SYNQLIEN_BUYER` product role when role fallback is enabled.

**Response:** `302 Found`

### POST `/api/liens/selling/buyer/liens/{accessLinkId}/messages`

Posts a message from the authenticated funding-company detail page into the same persisted offer thread used by
`POST /api/liens/selling/public/{token}/messages`. The endpoint first resolves `{accessLinkId}` with the same
buyer-contact scoping as the detail `GET`, then delegates to the public-link message workflow so both the public email
link and `/funding/offered-liens/{accessLinkId}?tab=messages` show the same messages and notification behavior.

**Permission:** `SYNQ_LIENS.lien:browse` or the `SYNQLIEN_BUYER` product role when role fallback is enabled.

**Request Body:**

```json
{
  "message": "Please review the signed lien package."
}
```

Messages are trimmed, required, and limited to 400 characters.

**Response:** `201 Created`, same message shape as the public message endpoint.

### POST `/api/liens/selling/buyer/liens/{accessLinkId}/accept`

Records an accepted buyer response from the authenticated funding-company detail page. The endpoint resolves
`{accessLinkId}` with authenticated buyer scoping and then uses the same public buyer accept workflow as the email link,
including idempotency handling, response activity, lien status updates, and buyer/seller outcome notifications.

**Permission:** `SYNQ_LIENS.lien:browse` or the `SYNQLIEN_BUYER` product role when role fallback is enabled.

**Request Body:**

```json
{
  "notes": "Accepted from the funding portal."
}
```

`notes` is optional. Use an `Idempotency-Key` header for repeat-safe posts.

**Response:** `200 OK`, same JSON shape as `GET /api/liens/selling/public/{token}` with an accepted `accessLink`.

### POST `/api/liens/selling/buyer/liens/{accessLinkId}/decline`

Records a declined buyer response from the authenticated funding-company detail page using the same shared response
workflow as the public email link.

**Permission:** `SYNQ_LIENS.lien:browse` or the `SYNQLIEN_BUYER` product role when role fallback is enabled.

**Request Body:**

```json
{
  "reason": "Outside current buying criteria."
}
```

`reason` is optional. Use an `Idempotency-Key` header for repeat-safe posts.

**Response:** `200 OK`, same JSON shape as `GET /api/liens/selling/public/{token}` with a declined `accessLink`.

### GET `/api/liens/selling/public/{token}`

Returns the temporary funding-company or seller-view portal data opened from a `New Lien Offer` email CTA. This endpoint
is anonymous; the opaque token controls tenant, lien, buyer contact, expiry, revocation, and audience. It does not
render HTML. The tenant portal route `/selling/public/{token}` fetches this JSON through the gateway and owns the UI
rendering.

**Authentication:** None.

**Response:** `200 OK`, `application/json`

The JSON payload is populated only from persisted lien, case, contact, buyer, seller, access-link, and servicing
document metadata. It includes seller, buyer/funding company, lien summary, case, access-link expiry, and real
supporting-document fields. It never inserts sample company names, sample people, sample files, `example.com`, or
caller-provided CTA data. Seller name is resolved from the Identity user who confirmed/submitted the offer
(`SellingBuyerAccessLinks.CreatedByUserId` / confirm-sale acting user -> `idt_Users.FirstName` + `LastName`), scoped to
the seller organization when Identity validates membership;
seller company is resolved from the selling organization (`sellerOrgId`) with the same resolver used by the confirm-sale
email and authenticated funding-company views. Handling law firm is the selected standalone law-firm contact's
`liens_Contacts.Organization` value, falling back to its `DisplayName` when organization is absent. Law-firm and case-manager
contacts remain case/asset metadata and are not used as the buyer-facing seller identity. For buyer-purpose links, the `account` block indicates whether the access link has already
activated an account or whether the token-scoped buyer email already belongs to an Identity account, so the tenant portal
can render `Log In` instead of `Activate Free Account`.
The lien `submittedAtUtc` field is the submitted-for-sale timestamp when present, serialized with the DST-aware U.S.
Pacific offset; it falls back to the notification submission or access-link creation timestamp only for legacy rows
without a persisted sale-submission timestamp.

```json
{
  "audience": "buyer",
  "accessLink": {
    "createdAtUtc": "2026-07-23T13:59:57.67655Z",
    "expiresAtUtc": "2026-08-22T13:59:57.67655Z",
    "lastAccessedAtUtc": "2026-07-23T14:01:00Z",
    "notificationSubmittedAtUtc": "2026-07-23T13:59:58Z",
    "responseStatus": null,
    "responseAmount": null,
    "responseNotes": null,
    "respondedAtUtc": null
  },
  "lien": {
    "id": "guid",
    "lienCode": "LIEN-001",
    "status": "Offered",
    "sellerStatus": "SubmittedForSale",
    "submittedAtUtc": "2026-07-23T13:59:57.67655Z",
    "listingVisibility": "Private",
    "initialServiceDate": "2026-01-12",
    "endServiceDate": "2026-02-14",
    "originalAmount": 24850.00,
    "askAmount": 21000.00,
    "offerPrice": 21000.00,
    "notes": "Persisted lien notes"
  },
  "seller": {
    "name": "Seller Processor",
    "company": "RL Liens1",
    "email": null
  },
  "buyer": {
    "contactName": "Buyer contact",
    "company": "Funding company",
    "email": "buyer@company.test",
    "phone": "3105551212"
  },
  "case": {
    "handlingLawFirm": "Handling law firm",
    "handlingLawFirmContactName": "Law firm contact",
    "handlingLawFirmEmail": "lawfirm@example.test",
    "caseManager": "Case manager"
  },
  "documents": [
    {
      "id": "document-guid",
      "fileName": "real-document.pdf",
      "category": "Lien Document",
      "sizeOrType": "PDF",
      "viewUrl": "/api/lien/api/liens/selling/public/{token}/documents/{document-guid}/view",
      "downloadUrl": "/api/lien/api/liens/selling/public/{token}/documents/{document-guid}/download"
    }
  ],
  "messages": [
    {
      "id": "guid",
      "senderType": "buyer",
      "senderName": "Buyer contact",
      "senderEmail": "buyer@company.test",
      "message": "Can you confirm the signed LOP is final?",
      "createdAtUtc": "2026-07-23T14:05:00Z",
      "attachments": [
        {
          "id": "attachment-guid",
          "fileName": "signed-lop.pdf",
          "contentType": "application/pdf",
          "fileSizeBytes": 245760,
          "createdAtUtc": "2026-07-23T14:05:00Z",
          "viewUrl": "/api/lien/api/liens/selling/public/{token}/message-attachments/{attachment-guid}/view",
          "downloadUrl": "/api/lien/api/liens/selling/public/{token}/message-attachments/{attachment-guid}/download"
        }
      ]
    }
  ],
  "account": {
    "hasExistingAccount": false,
    "loginUrl": "/login?returnTo=%2Ffunding%2Fdashboard&reason=synqlien-buyer-activation&tenantId=offer-tenant-guid"
  }
}
```

The `account.loginUrl` includes the token-scoped offer tenant id so existing buyer accounts with access to multiple
SynqLien funding organizations sign into the tenant that issued the offer.

For seller-view links, `audience` is `seller`; the same JSON includes buyer/funding-company details. Seller-view links
can post messages, but response and activation endpoints reject that token with `403 read-only-link`. Seller-view JSON
does not include an account-action requirement; `account` may be `null`.

The `documents` array includes uploaded document servicing metadata attached to the offered lien:
`LegacyCaseDocument`, `LegacyLienDocument`, `LegacyMedicalDocument`, and seller-wizard `SellingDocumentReference` rows.
Document categories resolve from canonical `documentTypeId` metadata through the tenant's active `DocumentCategory`
lookup when available, with legacy semicolon metadata and seller-wizard `documentType` metadata still supported.
Case-level documents that are not attached to the lien are excluded.
`viewUrl` and `downloadUrl` are same-origin tenant-portal BFF paths that preserve the public offer token and redirect
through Liens to the anonymous Documents access-token route.

The `messages[].attachments` array contains only files uploaded with that message. These files are not included in the
general `documents` array, and their view/download URLs preserve the public offer token while redirecting through Liens
to short-lived Documents access URLs.

### GET `/api/liens/selling/public/{token}/documents/{documentId}/view`

Issues a short-lived Documents view access token for a document attached to the token-scoped lien, then redirects to the
anonymous Documents access route. This endpoint is anonymous but requires the same valid, unexpired, unrevoked public
offer token as the portal `GET`. Buyer-response and seller-view tokens can both open lien documents. Documents not
attached to that lien return `404 document-not-found`.

**Authentication:** None.

**Response:** `302 Found`

`Location` points to `/documents/access/{accessToken}` when called through the gateway. The tenant portal BFF path
`/api/lien/api/liens/selling/public/{token}/documents/{documentId}/view` rewrites that redirect to
`/api/lien/documents/access/{accessToken}` for same-origin browser access. When local Documents storage then redirects
to `/internal/files`, the tenant portal keeps that final file hop under `/api/lien/documents/internal/files`.

### GET `/api/liens/selling/public/{token}/documents/{documentId}/download`

Same validation and ownership checks as the public document view endpoint, but requests a Documents download access
token.

**Authentication:** None.

**Response:** `302 Found`

### POST `/api/liens/selling/public/{token}/messages`

Adds a message to the token-scoped buyer/seller offer thread. This is anonymous and uses the same token validation as
the public `GET`. Liens derives the sender from the access-link purpose (`buyer` for buyer-response links, `seller` for
seller-view links); callers do not provide or override `senderType`. The message is persisted for the exact tenant,
lien, seller organization, buyer organization, and buyer contact represented by the token, so both public links see the
same chronological thread. After the message is saved, Liens emails the other party with that party's public link using
`lien.offer.message.created` and a message/recipient-specific idempotency key. Buyer-to-seller message notifications
use the seller account email resolved from Identity; seller-to-buyer replies use the activated or authenticated buyer
account email, not law-firm/contact email. Accept/decline outcome emails use the same account-recipient rule for the
seller, and the authenticated/activated buyer account email for the buyer when available. Notification failures are
logged and do not roll back the saved message or response. Message notification emails display the saved message
timestamp converted from `createdAtUtc` to U.S. Pacific time.

**Authentication:** None.

**Request:**

Text-only public messages may be sent as JSON:

```json
{
  "message": "Can you confirm the signed LOP is final?"
}
```

Messages with attachments are sent as `multipart/form-data` with a `message` field and repeated `files` parts. Message
text is trimmed and limited to 400 characters. Either text or at least one file is required. Up to 10 files are accepted
per message; each file uses the service upload limit and must be one of `.pdf`, `.jpg`, `.jpeg`, `.png`, `.docx`,
`.xlsx`, `.xls`, or `.csv`.

**Response:** `201 Created`

```json
{
  "id": "guid",
  "senderType": "buyer",
  "senderName": "Buyer contact",
  "senderEmail": "buyer@company.test",
  "message": "Can you confirm the signed LOP is final?",
  "createdAtUtc": "2026-07-23T14:05:00Z",
  "attachments": [
    {
      "id": "attachment-guid",
      "fileName": "signed-lop.pdf",
      "contentType": "application/pdf",
      "fileSizeBytes": 245760,
      "createdAtUtc": "2026-07-23T14:05:00Z",
      "viewUrl": "/api/lien/api/liens/selling/public/{token}/message-attachments/{attachment-guid}/view",
      "downloadUrl": "/api/lien/api/liens/selling/public/{token}/message-attachments/{attachment-guid}/download"
    }
  ]
}
```

### GET `/api/liens/selling/public/{token}/message-attachments/{attachmentId}/view`

Issues a short-lived Documents view access URL for a token-scoped message attachment, then redirects to that URL. Buyer
and seller public offer tokens can open attachments from the message thread represented by the token.

**Authentication:** None.

**Response:** `302 Found`

### GET `/api/liens/selling/public/{token}/message-attachments/{attachmentId}/download`

Same validation and ownership checks as the public message-attachment view endpoint, but requests a Documents download
URL.

**Authentication:** None.

**Response:** `302 Found`

### POST `/api/liens/selling/public/{token}/activate-account`

Creates a buyer portal account for the token-scoped buyer organization. This endpoint is anonymous, uses the
same token validation as the public `GET`, and is intended to be called by the tenant portal BFF path
`/api/lien/api/liens/selling/public/{token}/activate-account`. Liens asks Identity to create or resolve a tenant-scoped
`LIEN_OWNER` organization for the source Liens buyer organization id, then Identity grants `SYNQ_LIENS` product access
and assigns `SYNQLIEN_BUYER` scoped to that Identity organization. Existing buyer contact values from the token win over
editable request values; request values only fill missing contact data. On successful activation, Liens records the
activated Identity user/email on the access link so later public `GET` requests continue to return
`account.hasExistingAccount=true` even when the original buyer contact did not have an email. Existing account emails
return `409` and should be handled by prompting the buyer to log in with the existing account.

This account activation does not accept or decline the lien, create a Bill of Sale, mark a lien sold, or otherwise
finalize sale. Seller-view tokens are read-only and return `403 read-only-link`.

**Authentication:** None.

**Request:**

```json
{
  "companyName": "Funding company",
  "email": "buyer@company.test",
  "firstName": "Buyer",
  "lastName": "Contact",
  "phone": "3105551212",
  "password": "chosen-password"
}
```

**Response:** `200 OK`

```json
{
  "userId": "guid",
  "isNew": true,
  "loginUrl": "/login?returnTo=%2Ffunding%2Fdashboard&reason=synqlien-buyer-activation&tenantId=offer-tenant-guid"
}
```

### POST `/api/liens/selling/public/{token}/accept`

Compatibility alias: `POST /api/liens/selling/public/{token}/offers`.

Records an accepted buyer response for the token-scoped lien. This is anonymous and uses the same token validation as
the public `GET`: missing or unknown tokens return `404`, revoked or expired tokens return `410`, and contradictory
repeat responses return `409`. Accepting records the current ask amount on the access link and moves the lien lifecycle
status from `Offered` to `Accepted` with `SellerStatus=Accepted`; it does not create a Bill of Sale, mark the lien sold,
or finalize sale. Seller-view tokens are read-only and return `403 read-only-link`. The
`/offers` alias accepts the same response shape; legacy `message` fields are stored as response notes. The first
accepted response submits `lien.offer.accepted` emails to both the buyer and seller through Notifications with
recipient-specific idempotency keys. Repeated same-response posts return the recorded response and retry those
idempotent notification submissions, so transient failures can recover without duplicate emails. Notification submission
failures are logged and do not roll back the recorded buyer response. The email subject is exactly
`Lien Offer Accepted`, and the email includes a pre-rendered HTML body. Liens does not supply a notification template key
for this outcome email, so template rendering cannot override the fixed subject or HTML design.
Liens must be configured with `NotificationsService:BaseUrl` (or legacy `Services:NotificationsUrl`) and the shared
service-token signing key through `FLOW_SERVICE_TOKEN_SECRET` or `ServiceTokens:SigningKey`, because Notifications
requires service JWT auth for producer submissions.

**Authentication:** None.

**Request:**

```json
{
  "notes": "Accepted at ask"
}
```

**Response:** `200 OK`, same JSON shape as `GET /api/liens/selling/public/{token}`, with:

```json
{
  "accessLink": {
    "responseStatus": "Accepted",
    "responseAmount": 2500.00,
    "responseNotes": "Accepted at ask",
    "respondedAtUtc": "2026-07-23T14:10:00Z"
  },
  "lien": {
    "status": "Accepted",
    "sellerStatus": "Accepted"
  }
}
```

### POST `/api/liens/selling/public/{token}/decline`

Records a declined buyer response for the token-scoped lien. This is anonymous and uses the same token validation and
conflict behavior as public accept. Declining can record an optional reason, records the buyer access-link response as
`Declined`, and returns the seller lien to `Pending` so it appears in the seller Pending list and can be submitted for sale
again. It does not mark the lien sold, withdraw the seller listing, or create a Bill of Sale. Seller-view tokens are
read-only and return `403 read-only-link`. The first declined response submits
`lien.offer.rejected` emails to both the buyer and seller through Notifications with recipient-specific idempotency
keys. Repeated same-response posts return the recorded response and retry those idempotent notification submissions, so
transient failures can recover without duplicate emails. Notification submission failures are logged and do not roll back
the recorded buyer response. The email subject is exactly `Lien Offer Declined`, and the email includes a pre-rendered
HTML body. Liens does not supply a notification template key for this outcome email, so template rendering cannot
override the fixed subject or HTML design.
Liens must be configured with `NotificationsService:BaseUrl` (or legacy `Services:NotificationsUrl`) and the shared
service-token signing key through `FLOW_SERVICE_TOKEN_SECRET` or `ServiceTokens:SigningKey`, because Notifications
requires service JWT auth for producer submissions.

**Authentication:** None.

**Request:**

```json
{
  "reason": "Not in buying criteria"
}
```

**Response:** `200 OK`, same JSON shape as `GET /api/liens/selling/public/{token}`, with:

```json
{
  "accessLink": {
    "responseStatus": "Declined",
    "responseAmount": null,
    "responseNotes": "Not in buying criteria",
    "respondedAtUtc": "2026-07-23T14:10:00Z"
  },
  "lien": {
    "status": "Draft",
    "sellerStatus": "Pending"
  }
}
```

**Errors:**

| Status | Description |
|---|---|
| `404 Not Found` | Token or linked lien data cannot be resolved |
| `403 Forbidden` | Token is a seller read-only link and cannot record buyer actions |
| `410 Gone` | Token is expired or revoked |
| `409 Conflict` | Lien is no longer actionable, ask amount is unavailable, or a different response was already recorded |

---

## Selling In-App Notifications

Selling write endpoints do not expose a separate notification response contract. When an eligible business mutation
succeeds, Liens atomically writes a typed row to `liens_SellingNotificationOutbox`; a background worker later submits
it to Notifications as canonical channel `in_app` with a deterministic idempotency key.

| Event | Recipient |
|---|---|
| `lien.offer.submitted` | Authenticated submitting user, or the activated platform user associated with a public buyer link |
| `lien.offer.accepted` / `lien.offer.rejected` | The platform user stored in `LienOffer.SubmittedByPlatformUserId`; access-link responses also notify the platform seller who created the affected link |
| `lien.offer.message.created` | Seller link creator for buyer-authored messages; activated platform buyer for seller-authored messages |

No inbox event is enqueued when a concrete Identity platform user is unavailable; existing email delivery is unchanged.
Message inbox descriptions contain generic sender and lien context only and exclude message bodies, attachments, medical
information, and document excerpts. Offer acceptance emits no additional `lien.sale.finalized` inbox item.

---

## Cases Endpoints

Base path: `/api/liens/cases`

### POST `/api/liens/cases/global-search`

Search cases, liens, and the legacy global-search categories for the authenticated tenant. The request accepts
`query` or the legacy alias `keyword`, plus optional `page` and `limit` values. The response preserves the paginated
`cases` and `liens` objects and adds the legacy `plaintiffs`, `lawFirms`, `medicalFacilities`, `medicalProviders`,
`fundingCompanies`, `Leads`, and `servicing` arrays. Funding-company results include both imported `LienHolder`
contacts and canonical `FundingCompany` contacts.

**Permission:** `SYNQ_LIENS.case:read`

---

### GET `/api/liens/cases`

Search and list cases with optional filters.

Case statuses include `PreDemand`, `DemandSent`, `InNegotiation`, `Litigation (Open)`,
`Litigation (Pending)`, `CaseSettled`, and `Closed`. The two litigation variants are
stored values and can be filtered independently.

**Permission:** `SYNQ_LIENS.case:read`

**Query Parameters:**

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `search` | `string` | No | `null` | Free-text search filter |
| `status` | `string` | No | `null` | Filter by case status |
| `page` | `integer` | No | `1` | Page number |
| `pageSize` | `integer` | No | `20` | Items per page |

**Response:** `200 OK`

```json
PaginatedResult<CaseResponse>
```

---

### GET `/api/liens/cases/{id}`

Get a case by its unique identifier.

**Permission:** `SYNQ_LIENS.case:read`

**Path Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `id` | `guid` | Case unique identifier |

**Response:** `200 OK` — `CaseResponse`. The response includes the latest linked lien's UI lifecycle
label in `lienStatus` and matching LienStatus lookup UUID in `lienStatusId`. It also includes the latest
settlement payment's display value in `settlementStatus` and its stored lookup ID or code in
`settlementStatusId` only when the case has at least one lien and every linked lien is `Settled`
(legacy/UI `Closed`), or when any settlement or payment record on the case declares `No Recovery`. A No
Recovery declaration is normalized to `Closed` when the case has any positive payment or settlement amount;
otherwise it remains `No Recovery` with legacy settlement-status ID `4`. These amount-aware statuses remain
visible while other liens are open. Other settlement statuses remain empty while any linked lien is open or
rejected; cases without liens also return empty settlement fields. Each field pair also returns empty strings
when its corresponding record does not exist.

**Error:** `404 Not Found` — if the case does not exist.

---

### GET `/api/liens/cases/by-number/{caseNumber}`

Get a case by its case number.

**Permission:** `SYNQ_LIENS.case:read`

**Path Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `caseNumber` | `string` | Case number |

**Response:** `200 OK` — `CaseResponse`

**Error:** `404 Not Found` — if the case does not exist.

---

### POST `/api/liens/cases`

Create a new case.

**Permission:** `SYNQ_LIENS.case:create`

**Request Body: `CreateCaseRequest`**

| Field | Type | Required | Nullable | Description |
|---|---|---|---|---|
| `caseNumber` | `string` | Yes | No | Unique case number |
| `clientFirstName` | `string` | Yes | No | Client first name |
| `clientLastName` | `string` | Yes | No | Client last name |
| `externalReference` | `string` | No | Yes | External reference identifier |
| `title` | `string` | No | Yes | Case title |
| `clientDob` | `date` | No | Yes | Client date of birth (format: `YYYY-MM-DD`) |
| `clientPhone` | `string` | No | Yes | Client phone number |
| `clientEmail` | `string` | No | Yes | Client email address |
| `clientAddress` | `string` | No | Yes | Client address |
| `dateOfIncident` | `date` | No | Yes | Date of incident (format: `YYYY-MM-DD`) |
| `insuranceCarrier` | `string` | No | Yes | Insurance carrier name |
| `policyNumber` | `string` | No | Yes | Insurance policy number |
| `claimNumber` | `string` | No | Yes | Insurance claim number |
| `description` | `string` | No | Yes | Case description |
| `notes` | `string` | No | Yes | Additional notes |

**Response:** `201 Created` — `CaseResponse`

Returns the created case with a `Location` header pointing to `/api/liens/cases/{id}`.
Creation atomically adds a `Case Created` entry to `POST /api/liens/cases/case-updates/v3`. The entry contains the persisted business values and is retained even if the Case is later deleted.
Potential duplicate cases are rejected before save when DOB and date of loss exactly match an existing case and first/last names closely or partially match. Clients can call `POST /api/liens/cases/duplicate-check` before create to display the existing case link.

### POST `/api/liens/cases/duplicate-check`

Checks a pending case creation for duplicate risk without saving.

**Permission:** `SYNQ_LIENS.case:create`

**Request Body:**

```json
{
  "firstname": "Jane",
  "lastname": "Doe",
  "dob": "01/15/1990",
  "dateOfLoss": "08/01/2026"
}
```

**Response:** `200 OK`

```json
{
  "isDuplicate": true,
  "message": "A case with similar information already exists. Would you like to view the existing case?",
  "matches": [
    {
      "id": "guid",
      "caseNumber": "26-00042",
      "clientDisplayName": "Jane Doe",
      "clientDob": "1990-01-15",
      "dateOfIncident": "2026-08-01",
      "status": "PreDemand"
    }
  ]
}
```

---

### PUT `/api/liens/cases/{id}`

Update an existing case.

**Permission:** `SYNQ_LIENS.case:update`

Cases in any non-terminal status can be updated. A case whose current status is
`Closed` or `CaseSettled` is immutable through the general and partial case-update
routes; attempts return `409 Conflict`.

**Path Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `id` | `guid` | Case unique identifier |

**Request Body: `UpdateCaseRequest`**

| Field | Type | Required | Nullable | Description |
|---|---|---|---|---|
| `clientFirstName` | `string` | Yes | No | Client first name |
| `clientLastName` | `string` | Yes | No | Client last name |
| `externalReference` | `string` | No | Yes | External reference identifier |
| `title` | `string` | No | Yes | Case title |
| `clientDob` | `date` | No | Yes | Client date of birth (format: `YYYY-MM-DD`) |
| `clientPhone` | `string` | No | Yes | Client phone number |
| `clientEmail` | `string` | No | Yes | Client email address |
| `clientAddress` | `string` | No | Yes | Client address |
| `dateOfIncident` | `date` | No | Yes | Date of incident (format: `YYYY-MM-DD`) |
| `insuranceCarrier` | `string` | No | Yes | Insurance carrier name |
| `policyNumber` | `string` | No | Yes | Insurance policy number |
| `claimNumber` | `string` | No | Yes | Insurance claim number |
| `description` | `string` | No | Yes | Case description |
| `notes` | `string` | No | Yes | Additional notes |
| `status` | `string` | No | Yes | Case status. Accepted values include `Litigation (Open)` and `Litigation (Pending)`. |
| `demandAmount` | `decimal` | No | Yes | Demand amount |
| `settlementAmount` | `decimal` | No | Yes | Settlement amount |

**Response:** `200 OK` — `CaseResponse`

**Error:** `404 Not Found` — if the case does not exist.

---

### GET `/api/liens/cases/notes/{caseId}`

Return the legacy case-note history. Each changed non-empty `notes` value submitted through `PATCH /api/liens/cases/details-update` is appended as a new case-note entry rather than replacing prior entries. Feed notes and system update-history entries are intentionally excluded.

Every persisted root Case change creates one atomic `Case Details Update` entry listing all changed business fields as `previous → new`. `PATCH /api/liens/cases/personal-update` forwards every plaintiff name, birthdate, contact, gender, and structured address field so each actual plaintiff change appears in that entry. Case metadata stored in `notes` is expanded into readable logical fields, and the user-authored note is logged separately from its storage metadata. Normalized no-ops and audit-timestamp-only saves create no entry. The authenticated actor is resolved at read time with the existing tenant-scoped Identity fallback.

**Permission:** `SYNQ_LIENS.case:read`

The response uses the legacy envelope `{ isSuccess, message, data }`. `data` is ordered newest first and each item includes the historical `note` value and creator metadata. `created` is the U.S. Pacific display string, while `createdAtUtc` is the corresponding canonical UTC ISO timestamp.

`POST /api/liens/cases/add-note` and `POST /api/liens/cases/get-notes` are the separate Feed-note routes. Feed notes are shown only in the case Feed; they are not returned by this case-notes endpoint or by case-update history.

When `LegacyUpdateHistory:Enabled` is true, `POST /api/liens/cases/case-updates/v3`
also merges imported Program 1 case-update events and
`POST /api/liens/cases/liens-updates/v3` merges imported lien-update events.
The lien-update timeline excludes case-level `Case Details Update` history that has
no lien association. Those records remain available from `case-updates/v3`; every
row returned by `liens-updates/v3` represents a specific lien and includes its
`lienId`, tenant-scoped `lienCode`, and `action`. Lien codes are resolved by the history row's lien ID, including after the lien has moved to another case. Native lien mutations compare
the persisted previous values with the resulting values. `Lien Created` descriptions
include only Lien Code, Status, Purchase Date, and Initial Service Date when present.
Creation fields show their current values without a `blank →` prefix, and an initial
Draft status is shown as `""`; later updates and deletions record every changed business
field as `previous → new`.
Unchanged submissions do not create a change row. The retained fields are stored in one text-backed activity row. Selling handlers and other direct EF mutations use the same save-boundary comparison, including creation, archive, restore, deletion, and public buyer-response transitions. A move between cases writes the same activity projection to both the former and resulting case timelines.
Native lien-change rows use the `Liens Details` action label. In those response
descriptions, `blank` and `Draft` field values are rendered as `""`. Obsolete
`Lien Update` servicing compatibility rows are omitted from the timeline to avoid
duplicate entries.
`POST /api/liens/cases/liens/update-medical` accepts servicing corrections for
`Settled` liens while continuing to reject edits to declined, withdrawn, and
cancelled liens. When any submitted value changes, the endpoint appends exactly one
lien-scoped `Liens Details` row that combines every changed case, funding-company,
status, purchase/service-date, note, bulk, and servicing field as `previous → new`.
Both `POST /api/liens/cases/liens/medical` and
`POST /api/liens/cases/liens/update-medical` accept legacy status `Open` and persist it as canonical
`Active`. `GET /api/liens/cases/liens/get-medical/{id}` maps that canonical value back to the
legacy/UI value `Open`; other status values remain unchanged.
The row uses the resulting case association and updated lien ID, and `updatedBy`
resolves from the authenticated user's first and last name. Case-update history, including historical native Case Created notes, uses
the same name resolution for `createdBy`, `updatedBy`, and embedded `Created By` text instead of returning the user's email. Clearing a note records its previous value as
changing to `blank`; resubmitting unchanged normalized values creates no row. The
lien mutation and history write commit atomically. `POST /api/liens/cases/liens/update-facility`
updates its medical-information compatibility row only when the normalized facility,
contact, provider, email, or phone values changed; an unchanged resubmission preserves
the existing row and timestamp. `POST /api/liens/cases/liens/update-medicalcode` likewise
preserves the existing medical-code row and timestamp when its description and stored
detail values are unchanged. `POST /api/liens/cases/liens/payment` creates no row for an
empty payee/check submission and updates its existing medical-payment row only when the
payee or outbound check number changes.
Global ordering is timestamp descending, native before imported on an exact
timestamp tie, then stable source sequence/ID descending. Counts and pagination
cover all enabled sources. Timeline requests are limited to 200 rows per page
and the first 25,000 rows; larger windows return `400`. Imported case rows retain the existing wire fields:
`note` repeats `description`, `category` is `legacy`, `isPinned` and `isEdited`
are false, `created` equals `timestamp`, `createdBy`/`updatedBy` contain the
legacy actor or an empty string, and `updated` is empty. The known `ÔåÆ` token
is rendered as `→`; other source text is unchanged. With the flag disabled,
both endpoints retain native-only behavior. Case updates return a successful
empty result; lien updates return `404` when every enabled source is empty.

---

### POST `/api/liens/cases/dashboard/deployed` and `/api/liens/cases/dashboard/cash-received`

Return dashboard totals for deployed liens and cash received. Supplying both `startDate` and `endDate` filters the metric to that inclusive range. When neither date is supplied, the metric includes all dated tenant history; `periodStart` and `periodEnd` are returned as empty strings to indicate the all-time result. Deployed always excludes liens without a persisted `PurchaseDate`, and Cash Received always excludes settlement headers without a persisted `SettlementDate`.

The dashboard Total Lien Report, including its status chart and totals, excludes `Rejected` and `Cancelled` liens before aggregation and pagination.

---

### GET `/api/liens/cases/payoff-quote/{caseId}`

Compatibility alias: `GET /api/liens/cases/payoff-qoute/{caseId}`.

Generates a new payoff PDF from the case and its open servicing liens on every request, uploads it to the Documents service as a case document, and records `LegacyCaseDocument` metadata with legacy type ID `14`. Existing payoff documents do not prevent generation. Before returning success, the service waits briefly for the newly generated document's security scan to report `CLEAN`; terminal scan outcomes remain unavailable rather than returning a URL that the frontend cannot open.

**Response:** `200 OK`

```json
{
  "isSuccess": true,
  "message": "Successfully retrieved Payoff Quote",
  "url": "/documents/{documentId}",
  "base64": "JVBERi0xLjQ..."
}
```

Missing cases return `404` with `Error: Unable to retrieve Payoff Quote`.

---

## Upload Limits

All SynqLien multipart upload endpoints accept files up to 50 MB. Requests over the limit return a size error instead of a generic upload failure.

---

### POST `/api/liens/cases/upload/document`

Legacy-compatible case document upload endpoint.

**Permission:** `SYNQ_LIENS.case:update`

**Content-Type:** `multipart/form-data`

**Form Fields:**

| Field | Type | Required | Description |
|---|---|---|---|
| `file` | file | Yes | Document file. Allowed extensions: `.pdf`, `.jpg`, `.jpeg`, `.png`, `.docx`, `.xlsx`, `.xls`, `.csv`. Maximum size: 50 MB. |
| `caseId` | `guid` | Yes | Case identifier to link the uploaded document to. |
| `DocFileTypeId` | `string` | No | Legacy document type ID. Preserved in local metadata; UUID values are forwarded to Documents as `documentTypeId`. |
| `DocName` | `string` | No | Document title. Defaults to the uploaded filename without extension. |
| `DocDescription` | `string` | No | Document description. Defaults to the file extension label. |

Uploads the file to the Documents service and records legacy document metadata as a `LegacyCaseDocument` servicing item for compatibility with existing case document lookups.

**Response:** `200 OK`

```json
{
  "isSuccess": true,
  "message": "Successfully uploaded document.",
  "data": {
    "url": "/documents/{documentId}",
    "documentId": "guid"
  }
}
```

---

### POST `/api/liens/cases/liens/upload/document`

Legacy-compatible lien document upload endpoint.

**Permission:** `SYNQ_LIENS.lien:update`

**Content-Type:** `multipart/form-data`

**Form Fields:**

| Field | Type | Required | Description |
|---|---|---|---|
| `file` | file | Yes | Document file. Allowed extensions: `.pdf`, `.jpg`, `.jpeg`, `.png`, `.docx`, `.xlsx`, `.xls`, `.csv`. Maximum size: 50 MB. |
| `liensId` | `guid` | Yes | Lien identifier to link the uploaded document to. `lienId` is also accepted. |
| `DocFileTypeId` | `string` | No | Legacy document type ID. Preserved in local metadata; UUID values are forwarded to Documents as `documentTypeId`. |
| `DocName` | `string` | No | Document title. Defaults to the uploaded filename without extension. |
| `DocDescription` | `string` | No | Document description. Defaults to the file extension label. |

Uploads the file to the Documents service and records legacy document metadata as a `LegacyLienDocument` servicing item.

**Response:** `200 OK`

```json
{
  "isSuccess": true,
  "message": "Successfully uploaded document.",
  "data": {
    "url": "/documents/{documentId}",
    "documentId": "guid"
  }
}
```

---

### Legacy document retrieval and opening

`GET /api/liens/cases/get-casedocument/{caseId}`, `GET
/api/liens/cases/liens/get-medicaldocument/{liensId}`, and `GET
/api/liens/cases/get-allcasedocument/{caseId}` return a legacy `url` field.
Document responses also return `documentTypeId`, normalized to the UUID used by
`GET /lookup/document/type`. The existing `typeId` remains available for legacy
callers. When historical metadata has no usable type, both fields fall back to
the canonical `Other` document type so the tenant portal always displays a label.

Current uploads return `/documents/{documentId}` and must be opened through the
Documents-service view-token endpoint. SQL-migrated SL-CORE records instead
retain an allowlisted `https://legal-dmm-prod.legalsynq.com/...` URL because
they do not have a Documents-service ID. The tenant portal's BFF resolves the
legacy object key through a tenant-scoped Liens endpoint and redirects only to
that exact HTTPS host; browser code continues using the existing view-token flow.

### GET `/api/liens/legacy-document-links/{objectKey}/resolve`

Protected compatibility endpoint used by the tenant portal BFF when an existing
Documents-service `view-url` request contains a migrated legacy object key
instead of a Documents GUID. It is tenant-scoped, accepts only a safe filename
key, and returns a URL only when exactly one `LegacyCaseDocument`,
`LegacyLienDocument`, or `LegacyMedicalDocument` record resolves to the
allowlisted legacy host.

**Permission:** `SYNQ_LIENS.case:read`

**Response:** `200 OK`

```json
{
  "url": "https://legal-dmm-prod.legalsynq.com/path/to/document.pdf"
}
```

---

### CaseResponse

| Field | Type | Nullable | Description |
|---|---|---|---|
| `id` | `guid` | No | Unique identifier |
| `caseNumber` | `string` | No | Case number |
| `externalReference` | `string` | Yes | External reference |
| `title` | `string` | Yes | Case title |
| `clientFirstName` | `string` | No | Client first name |
| `clientLastName` | `string` | No | Client last name |
| `clientDisplayName` | `string` | No | Computed client display name |
| `status` | `string` | No | Current status |
| `dateOfIncident` | `date` | Yes | Date of incident |
| `clientDob` | `date` | Yes | Client date of birth |
| `clientPhone` | `string` | Yes | Client phone number |
| `clientEmail` | `string` | Yes | Client email address |
| `clientAddress` | `string` | Yes | Client address |
| `insuranceCarrier` | `string` | Yes | Insurance carrier name |
| `policyNumber` | `string` | Yes | Insurance policy number |
| `claimNumber` | `string` | Yes | Insurance claim number |
| `demandAmount` | `decimal` | Yes | Demand amount |
| `settlementAmount` | `decimal` | Yes | Settlement amount |
| `description` | `string` | Yes | Case description |
| `notes` | `string` | Yes | Additional notes |
| `openedAtUtc` | `datetime` | Yes | When the case was opened |
| `closedAtUtc` | `datetime` | Yes | When the case was closed |
| `createdAtUtc` | `datetime` | No | Record creation timestamp |
| `updatedAtUtc` | `datetime` | No | Record last-updated timestamp |

---

## Bills of Sale Endpoints

Base path: `/api/liens/bill-of-sales`

### GET `/api/liens/bill-of-sales`

Search and list bills of sale with optional filters.

**Permission:** `SYNQ_LIENS.lien:read`

**Query Parameters:**

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `search` | `string` | No | `null` | Free-text search filter |
| `status` | `string` | No | `null` | Filter by bill of sale status |
| `lienId` | `guid` | No | `null` | Filter by associated lien ID |
| `sellerOrgId` | `guid` | No | `null` | Filter by seller organization ID |
| `buyerOrgId` | `guid` | No | `null` | Filter by buyer organization ID |
| `page` | `integer` | No | `1` | Page number |
| `pageSize` | `integer` | No | `20` | Items per page |

**Response:** `200 OK`

```json
PaginatedResult<BillOfSaleResponse>
```

---

### GET `/api/liens/bill-of-sales/{id}`

Get a bill of sale by its unique identifier.

**Permission:** `SYNQ_LIENS.lien:read`

**Path Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `id` | `guid` | Bill of sale unique identifier |

**Response:** `200 OK` — `BillOfSaleResponse`

**Error:** `404 Not Found` — if the bill of sale does not exist.

---

### GET `/api/liens/bill-of-sales/by-number/{billOfSaleNumber}`

Get a bill of sale by its bill of sale number.

**Permission:** `SYNQ_LIENS.lien:read`

**Path Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `billOfSaleNumber` | `string` | Bill of sale number |

**Response:** `200 OK` — `BillOfSaleResponse`

**Error:** `404 Not Found` — if the bill of sale does not exist.

---

### GET `/api/liens/liens/{lienId}/bill-of-sales`

Get all bills of sale associated with a specific lien.

**Permission:** `SYNQ_LIENS.lien:read`

**Path Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `lienId` | `guid` | Lien unique identifier |

**Response:** `200 OK` — `BillOfSaleResponse[]`

---

### GET `/api/liens/bill-of-sales/{id}/document`

Download the document file for a bill of sale by its ID.

**Permission:** `SYNQ_LIENS.lien:read`

**Path Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `id` | `guid` | Bill of sale unique identifier |

**Response:** `200 OK` — Binary file download with appropriate `Content-Type` and `Content-Disposition` headers.

---

### GET `/api/liens/bill-of-sales/by-number/{billOfSaleNumber}/document`

Download the document file for a bill of sale by its number.

**Permission:** `SYNQ_LIENS.lien:read`

**Path Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `billOfSaleNumber` | `string` | Bill of sale number |

**Response:** `200 OK` — Binary file download with appropriate `Content-Type` and `Content-Disposition` headers.

---

### PUT `/api/liens/bill-of-sales/{id}/submit`

Submit a bill of sale for execution.

**Permission:** `SYNQ_LIENS.lien:service`

**Path Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `id` | `guid` | Bill of sale unique identifier |

**Response:** `200 OK` — `BillOfSaleResponse`

**Error:** `404 Not Found` — if the bill of sale does not exist.

---

### PUT `/api/liens/bill-of-sales/{id}/execute`

Execute a bill of sale.

**Permission:** `SYNQ_LIENS.lien:service`

**Path Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `id` | `guid` | Bill of sale unique identifier |

**Response:** `200 OK` — `BillOfSaleResponse`

**Error:** `404 Not Found` — if the bill of sale does not exist.

---

### PUT `/api/liens/bill-of-sales/{id}/cancel`

Cancel a bill of sale.

**Permission:** `SYNQ_LIENS.lien:service`

**Path Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `id` | `guid` | Bill of sale unique identifier |

**Query Parameters:**

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `reason` | `string` | No | `null` | Reason for cancellation |

**Response:** `200 OK` — `BillOfSaleResponse`

**Error:** `404 Not Found` — if the bill of sale does not exist.

---

### BillOfSaleResponse

| Field | Type | Nullable | Description |
|---|---|---|---|
| `id` | `guid` | No | Unique identifier |
| `billOfSaleNumber` | `string` | No | Bill of sale number |
| `externalReference` | `string` | Yes | External reference |
| `status` | `string` | No | Current status |
| `lienId` | `guid` | No | Associated lien ID |
| `lienOfferId` | `guid` | No | Associated lien offer ID |
| `sellerOrgId` | `guid` | No | Seller organization ID |
| `buyerOrgId` | `guid` | No | Buyer organization ID |
| `purchaseAmount` | `decimal` | No | Purchase amount |
| `originalLienAmount` | `decimal` | No | Original lien amount |
| `discountPercent` | `decimal` | Yes | Discount percentage |
| `sellerContactName` | `string` | Yes | Seller contact name |
| `buyerContactName` | `string` | Yes | Buyer contact name |
| `terms` | `string` | Yes | Terms of sale |
| `notes` | `string` | Yes | Additional notes |
| `documentId` | `guid` | Yes | Associated document ID |
| `issuedAtUtc` | `datetime` | No | When the bill of sale was issued |
| `executedAtUtc` | `datetime` | Yes | When executed |
| `effectiveAtUtc` | `datetime` | Yes | When effective |
| `cancelledAtUtc` | `datetime` | Yes | When cancelled |
| `createdAtUtc` | `datetime` | No | Record creation timestamp |
| `updatedAtUtc` | `datetime` | No | Record last-updated timestamp |

---

## Lien Offers Endpoints

Base path: `/api/liens/offers`

### GET `/api/liens/offers`

Search and list lien offers with optional filters.

**Permission:** `SYNQ_LIENS.lien:read`

**Query Parameters:**

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `lienId` | `guid` | No | `null` | Filter by lien ID |
| `status` | `string` | No | `null` | Filter by offer status |
| `buyerOrgId` | `guid` | No | `null` | Filter by buyer organization ID |
| `sellerOrgId` | `guid` | No | `null` | Filter by seller organization ID |
| `page` | `integer` | No | `1` | Page number |
| `pageSize` | `integer` | No | `20` | Items per page |

**Response:** `200 OK`

```json
PaginatedResult<LienOfferResponse>
```

---

### GET `/api/liens/offers/{id}`

Get a lien offer by its unique identifier.

**Permission:** `SYNQ_LIENS.lien:read`

**Path Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `id` | `guid` | Lien offer unique identifier |

**Response:** `200 OK` — `LienOfferResponse`

**Error:** `404 Not Found` — if the lien offer does not exist.

---

### GET `/api/liens/liens/{lienId}/offers`

Get all offers associated with a specific lien.

**Permission:** `SYNQ_LIENS.lien:read`

**Path Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `lienId` | `guid` | Lien unique identifier |

**Response:** `200 OK` — `LienOfferResponse[]`

---

### POST `/api/liens/offers`

Create a new lien offer.

**Permission:** `SYNQ_LIENS.lien:offer`

**Request Body: `CreateLienOfferRequest`**

| Field | Type | Required | Nullable | Description |
|---|---|---|---|---|
| `lienId` | `guid` | Yes | No | ID of the lien being offered on |
| `offerAmount` | `decimal` | Yes | No | Offer amount |
| `notes` | `string` | No | Yes | Additional notes |
| `expiresAtUtc` | `datetime` | No | Yes | Offer expiration date/time (UTC) |

**Response:** `201 Created` — `LienOfferResponse`

Returns the created offer with a `Location` header pointing to `/api/liens/offers/{id}`.

---

### POST `/api/liens/offers/{offerId}/accept`

Accept a lien offer.

**Permission:** `SYNQ_LIENS.lien:update`

**Path Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `offerId` | `guid` | Lien offer unique identifier |

**Response:** `200 OK` — `SaleFinalizationResult`

**Error:** `404 Not Found` — if the lien offer does not exist.

---

### LienOfferResponse

| Field | Type | Nullable | Description |
|---|---|---|---|
| `id` | `guid` | No | Unique identifier |
| `lienId` | `guid` | No | Associated lien ID |
| `offerAmount` | `decimal` | No | Offer amount |
| `status` | `string` | No | Current status |
| `buyerOrgId` | `guid` | No | Buyer organization ID |
| `sellerOrgId` | `guid` | No | Seller organization ID |
| `notes` | `string` | Yes | Offer notes |
| `responseNotes` | `string` | Yes | Response notes from the seller |
| `externalReference` | `string` | Yes | External reference |
| `offeredAtUtc` | `datetime` | No | When the offer was made |
| `expiresAtUtc` | `datetime` | Yes | When the offer expires |
| `respondedAtUtc` | `datetime` | Yes | When the offer was responded to |
| `withdrawnAtUtc` | `datetime` | Yes | When the offer was withdrawn |
| `isExpired` | `boolean` | No | Whether the offer has expired |
| `createdAtUtc` | `datetime` | No | Record creation timestamp |
| `updatedAtUtc` | `datetime` | No | Record last-updated timestamp |

---

### SaleFinalizationResult

Returned when a lien offer is accepted. Contains details about the finalized sale.

| Field | Type | Nullable | Description |
|---|---|---|---|
| `acceptedOfferId` | `guid` | No | ID of the accepted offer |
| `acceptedOfferStatus` | `string` | No | Final status of the accepted offer |
| `lienId` | `guid` | No | ID of the lien involved in the sale |
| `finalLienStatus` | `string` | No | Final status of the lien after sale |
| `billOfSaleId` | `guid` | No | ID of the generated bill of sale |
| `billOfSaleNumber` | `string` | No | Number of the generated bill of sale |
| `billOfSaleStatus` | `string` | No | Status of the generated bill of sale |
| `purchaseAmount` | `decimal` | No | Purchase amount |
| `originalLienAmount` | `decimal` | No | Original lien amount |
| `discountPercent` | `decimal` | Yes | Discount percentage |
| `documentId` | `guid` | Yes | Associated document ID |
| `competingOffersRejected` | `integer` | No | Number of competing offers that were rejected |
| `finalizedAtUtc` | `datetime` | No | When the sale was finalized |

---

## Contacts Endpoints

Base path: `/api/liens/contacts`

### GET `/api/liens/contacts`

Search and list contacts with optional filters.

**Permission:** `SYNQ_LIENS.lien:service`

**Query Parameters:**

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `search` | `string` | No | `null` | Free-text search filter |
| `contactType` | `string` | No | `null` | Filter by contact type |
| `isActive` | `boolean` | No | `null` | Filter by active status |
| `page` | `integer` | No | `1` | Page number |
| `pageSize` | `integer` | No | `20` | Items per page |

**Response:** `200 OK`

```json
PaginatedResult<ContactResponse>
```

---

### GET `/api/liens/contacts/{id}`

Get a contact by its unique identifier.

**Permission:** `SYNQ_LIENS.lien:service`

**Path Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `id` | `guid` | Contact unique identifier |

**Response:** `200 OK` — `ContactResponse`

**Error:** `404 Not Found` — if the contact does not exist.

---

### POST `/api/liens/contacts`

Create a new contact.

**Permission:** `SYNQ_LIENS.lien:service`

**Request Body: `CreateContactRequest`**

| Field | Type | Required | Nullable | Description |
|---|---|---|---|---|
| `contactType` | `string` | Yes | No | Type of contact |
| `firstName` | `string` | Yes | No | First name |
| `lastName` | `string` | Yes | No | Last name |
| `title` | `string` | No | Yes | Job title |
| `organization` | `string` | No | Yes | Organization name |
| `email` | `string` | No | Yes | Email address |
| `phone` | `string` | No | Yes | Phone number |
| `phoneExtension` | `string` | No | Yes | Optional phone extension, stored separately from `phone` |
| `fax` | `string` | No | Yes | Fax number |
| `website` | `string` | No | Yes | Website URL |
| `addressLine1` | `string` | No | Yes | Street address |
| `city` | `string` | No | Yes | City |
| `state` | `string` | No | Yes | State |
| `postalCode` | `string` | No | Yes | Postal code |
| `notes` | `string` | No | Yes | Additional notes |

**Response:** `201 Created` — `ContactResponse`

Returns the created contact with a `Location` header pointing to `/api/liens/contacts/{id}`.

---

### PUT `/api/liens/contacts/{id}`

Update an existing contact.

**Permission:** `SYNQ_LIENS.lien:service`

**Path Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `id` | `guid` | Contact unique identifier |

**Request Body: `UpdateContactRequest`**

| Field | Type | Required | Nullable | Description |
|---|---|---|---|---|
| `contactType` | `string` | Yes | No | Type of contact |
| `firstName` | `string` | Yes | No | First name |
| `lastName` | `string` | Yes | No | Last name |
| `title` | `string` | No | Yes | Job title |
| `organization` | `string` | No | Yes | Organization name |
| `email` | `string` | No | Yes | Email address |
| `phone` | `string` | No | Yes | Phone number |
| `phoneExtension` | `string` | No | Yes | Optional phone extension, stored separately from `phone` |
| `fax` | `string` | No | Yes | Fax number |
| `website` | `string` | No | Yes | Website URL |
| `addressLine1` | `string` | No | Yes | Street address |
| `city` | `string` | No | Yes | City |
| `state` | `string` | No | Yes | State |
| `postalCode` | `string` | No | Yes | Postal code |
| `notes` | `string` | No | Yes | Additional notes |

**Response:** `200 OK` — `ContactResponse`

**Error:** `404 Not Found` — if the contact does not exist.

---

### PUT `/api/liens/contacts/{id}/deactivate`

Deactivate a contact.

**Permission:** `SYNQ_LIENS.lien:service`

**Path Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `id` | `guid` | Contact unique identifier |

**Response:** `200 OK` — `ContactResponse`

**Error:** `404 Not Found` — if the contact does not exist.

---

### PUT `/api/liens/contacts/{id}/reactivate`

Reactivate a previously deactivated contact.

**Permission:** `SYNQ_LIENS.lien:service`

**Path Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `id` | `guid` | Contact unique identifier |

**Response:** `200 OK` — `ContactResponse`

**Error:** `404 Not Found` — if the contact does not exist.

---

### POST `/api/liens/contacts/export-csv`

Export all matching active, top-level contacts as a Base64-encoded CSV. The default columns match the Contacts table: the selected contact-type name, Email, and Active Cases.

**Permission:** `SYNQ_LIENS.lien:service`

**Request Body:**

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `contactType` | `string` | No | `null` | Contact-type tab to export |
| `search` | `string` | No | `null` | Matches the Contacts table search |
| `legacyFormat` | `boolean` | No | `false` | Returns the previous ten-column schema, including inactive and sub-contact records |

**Response:** `200 OK` — `{ "data": "<base64 CSV>" }`

---

### ContactResponse

| Field | Type | Nullable | Description |
|---|---|---|---|
| `id` | `guid` | No | Unique identifier |
| `contactType` | `string` | No | Type of contact |
| `firstName` | `string` | No | First name |
| `lastName` | `string` | No | Last name |
| `displayName` | `string` | No | Computed display name |
| `title` | `string` | Yes | Job title |
| `organization` | `string` | Yes | Organization name |
| `email` | `string` | Yes | Email address |
| `phone` | `string` | Yes | Phone number |
| `phoneExtension` | `string` | Yes | Phone extension, stored separately from `phone` |
| `fax` | `string` | Yes | Fax number |
| `website` | `string` | Yes | Website URL |
| `addressLine1` | `string` | Yes | Street address |
| `city` | `string` | Yes | City |
| `state` | `string` | Yes | State |
| `postalCode` | `string` | Yes | Postal code |
| `notes` | `string` | Yes | Additional notes |
| `isActive` | `boolean` | No | Whether the contact is active |
| `createdAtUtc` | `datetime` | No | Record creation timestamp |
| `updatedAtUtc` | `datetime` | No | Record last-updated timestamp |

---

## Settlement Reduction Endpoints

Base path: `/api/liens/settlement/reductions`

`GET /case/{caseId}` returns only the latest canonical reduction for each lien,
selected by the most recent persisted update rather than the business reduction
date. `GET /lien/{lienId}` returns the lien's canonical reduction history.
For a lien without a canonical reduction, the response also exposes preserved
SL-CORE settlement metadata containing both a valid `reductionAmount` and an
explicit `SLS_REDUCTION_DATE`. Historical source rows without a reduction date
are omitted from this compatibility fallback; the service does not invent a
date. A canonical reduction takes precedence over the legacy fallback for the
same lien.

---

## Settlement Payment Endpoints

Base path: `/api/liens/settlement/payments`

### Canonical case payment ledger

The tenant portal Payments tab uses `GET` and `POST /api/liens/cases/{caseId}/payments` and
`POST /api/liens/cases/{caseId}/payments/{paymentId}/void`.

- `GET` requires `SYNQ_LIENS.lien:read`. Query parameters are `search`, `paymentMethod`,
  `postingStatus`, `sortBy` (`paymentDate`, `paymentMethod`, or `amount`), `sortDirection`,
  `page`, and `pageSize` (maximum 100). The response contains `summary`, `items`, `page`,
  `pageSize`, and `totalCount`. Summary totals are calculated independently of the page and
  include posted rows only. Lien selling amount uses `PurchasePrice`, then `AskAmount`, then
  `OriginalAmount`; lien aging uses the earliest persisted `ReceivableDueDate` and is null when
  no linked lien has one.
- `POST /api/liens/cases/{caseId}/payments` requires `SYNQ_LIENS.lien:settle` and an
  `Idempotency-Key` header. The body requires positive `amount`, `paymentDate`,
  `paymentMethod`, `referenceNumber`, and one or more unique `allocations` containing `lienId`
  and positive `amount`. Allocation totals must equal the payment amount and cannot exceed any
  lien's outstanding selling balance. When the payment amount exceeds the selected liens'
  combined available balance, the `400` response identifies the `amount` field and includes that
  available balance as its client-facing error message. `detailsContext`, `notes`, `settlementType`,
  `settlementStatus`, and `lienStatus` are optional. All allocation rows share one `receiptId`
  and payment number and are written in one transaction with any requested lien-status changes
  and audit event.
- `POST /api/liens/cases/{caseId}/payments/{paymentId}/void` requires
  `SYNQ_LIENS.lien:settle`, `Idempotency-Key`, and `{ "reason": "..." }`. It marks every
  allocation sharing the selected payment's receipt as `Voided`; the original rows remain in
  history and no longer contribute to posted totals. Repeating a completed request with the same
  key and body replays its response; reusing a key with a different body returns `409`.

The older settlement payment routes remain available for compatibility. New case-payment UI
writes must use the canonical case-scoped route so multi-lien allocation and status changes are
atomic.

### POST `/service/update-liens-status`

Legacy servicing endpoint for closing one or more selected liens and declaring No Recovery. `caseId`,
comma-delimited `lienIds`, `lienStatus`, and `closedDate` are required; `closedDate` accepts `yyyy-MM-dd`
and US `MM/dd/yyyy` formats. Every selected lien must belong to the authenticated tenant and the supplied
case. The update is atomic on relational databases: each selected lien receives `lienStatus`, and a
zero-amount payment-detail status declaration is recorded for `closedDate` with the optional `note`. A selected
lien with any positive payment or settlement amount receives status `Closed`; a selected lien with no received
amount receives the canonical No Recovery settlement status ID `4`.

The case-level display follows the same rule for compatibility. A subsequent `GET /api/liens/cases/{id}`
returns `Closed` when the case has any positive payment or settlement amount, and returns
`settlementStatus: "No Recovery"` with `settlementStatusId: "4"` when it has none, even if another lien remains open.

### PUT `/api/liens/settlement/payments/{paymentId}`

Update one existing settlement payment. The payment is resolved from the authenticated tenant and the route `paymentId`; `caseId` and `lienId` are immutable and are not accepted in the body. The legacy `POST /service/liens/update/settlement` remains a create-settlement endpoint and must not be used to edit a payment.

**Permission:** `SYNQ_LIENS.lien:update`

**Path Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `paymentId` | `guid` | Settlement payment identifier returned by payment-details APIs |

**Request Body: `UpdateSettlementPaymentDetailRequest`**

All fields are required. Unknown JSON fields are rejected with `400 Bad Request`.

| Field | Type | Description |
|---|---|---|
| `amount` | `decimal` | Updated payment amount; must be zero or greater |
| `paymentDate` | `date` | Payment date in `YYYY-MM-DD` format |
| `paymentMethod` | `string` | Nonblank payment method, such as `Check` |
| `referenceNumber` | `string` | Nonblank check or external payment reference number; maximum 100 characters |
| `detailsContext` | `string` | Optional payment context; maximum 300 characters |
| `notes` | `string` | User-visible payment note; must be present and non-null but may be empty |
| `settlementType` | `string` | Nonblank settlement source, such as `by_funding_company` |
| `settlementStatus` | `string` | Nonblank payment outcome, such as `full_payment` |
| `lienStatus` | `string` | Linked lien lifecycle value. `Open` and `Closed` normalize to `Active` and `Settled` |

```json
{
  "amount": 530,
  "paymentDate": "2026-08-16",
  "paymentMethod": "Check",
  "referenceNumber": "123456",
  "notes": "Payment Testing",
  "settlementType": "by_funding_company",
  "settlementStatus": "full_payment",
  "lienStatus": "Closed"
}
```

**Response:** `200 OK` — `SettlementPaymentDetailResponse`

The response returns the immutable payment identity and linkage, the updated amount/date/reference/note, `paymentMethod`, `settlementTypeId`, `settlementStatusId`, and audit fields. Current rows use first-class classification columns while preserved legacy metadata remains readable; unrelated legacy metadata is preserved. The payment update and linked-lien status change commit atomically.

Because payment method and settlement classifications use the legacy metadata representation, `paymentMethod`, `settlementType`, and `settlementStatus` reject `;`, `=`, CR/LF, and the `[legacy-meta]` marker. `notes` rejects the exact `[legacy-meta]` marker but otherwise permits normal punctuation, including semicolons and equals signs.

**Errors:**

| Status | Condition |
|---|---|
| `400 Bad Request` | Missing, malformed, unknown, or invalid request field |
| `404 Not Found` | Payment is missing, deleted, or belongs to another tenant |

### DELETE `/api/liens/settlement/payments/{paymentId}`

Soft-delete a settlement payment and reopen its settled liens. When the selected row belongs to
a receipt, every active allocation sharing that `receiptId` is deleted and every distinct settled
lien in the receipt is returned to the open (`Active`) status. Compatibility rows that predate
receipts are grouped by case, payment date, and reference number, falling back to the case-level
payment number when no reference is available. The payment deletion and lien-status changes commit
atomically on relational databases.

**Permission:** `SYNQ_LIENS.lien:update`

**Response:** `200 OK` with `{ "isSuccess": true, "message": "Successfully Deleted." }`.

---

## Servicing Endpoints

Base path: `/api/liens/servicing`

### GET `/api/liens/servicing`

Search and list servicing items with optional filters.

**Permission:** `SYNQ_LIENS.lien:service`

**Query Parameters:**

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `search` | `string` | No | `null` | Free-text search filter |
| `status` | `string` | No | `null` | Filter by status |
| `priority` | `string` | No | `null` | Filter by priority |
| `assignedTo` | `string` | No | `null` | Filter by assignee |
| `caseId` | `guid` | No | `null` | Filter by associated case ID |
| `lienId` | `guid` | No | `null` | Filter by associated lien ID |
| `page` | `integer` | No | `1` | Page number |
| `pageSize` | `integer` | No | `20` | Items per page |

**Response:** `200 OK`

```json
PaginatedResult<ServicingItemResponse>
```

---

### GET `/api/liens/servicing/{id}`

Get a servicing item by its unique identifier.

**Permission:** `SYNQ_LIENS.lien:service`

**Path Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `id` | `guid` | Servicing item unique identifier |

**Response:** `200 OK` — `ServicingItemResponse`

**Error:** `404 Not Found` — if the servicing item does not exist.

---

### POST `/api/liens/servicing`

Create a new servicing item.

**Permission:** `SYNQ_LIENS.lien:service`

**Request Body: `CreateServicingItemRequest`**

| Field | Type | Required | Nullable | Description |
|---|---|---|---|---|
| `taskNumber` | `string` | Yes | No | Unique task number |
| `taskType` | `string` | Yes | No | Type of task |
| `description` | `string` | Yes | No | Task description |
| `assignedTo` | `string` | Yes | No | Name of assignee |
| `assignedToUserId` | `guid` | No | Yes | User ID of assignee |
| `priority` | `string` | No | Yes | Priority level |
| `caseId` | `guid` | No | Yes | Associated case ID |
| `lienId` | `guid` | No | Yes | Associated lien ID |
| `dueDate` | `date` | No | Yes | Due date (format: `YYYY-MM-DD`) |
| `notes` | `string` | No | Yes | Additional notes |

**Response:** `201 Created` — `ServicingItemResponse`

Returns the created servicing item with a `Location` header pointing to `/api/liens/servicing/{id}`.

---

### PUT `/api/liens/servicing/{id}`

Update an existing servicing item.

**Permission:** `SYNQ_LIENS.lien:service`

**Path Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `id` | `guid` | Servicing item unique identifier |

**Request Body: `UpdateServicingItemRequest`**

| Field | Type | Required | Nullable | Description |
|---|---|---|---|---|
| `taskType` | `string` | Yes | No | Type of task |
| `description` | `string` | Yes | No | Task description |
| `assignedTo` | `string` | Yes | No | Name of assignee |
| `assignedToUserId` | `guid` | No | Yes | User ID of assignee |
| `priority` | `string` | No | Yes | Priority level |
| `status` | `string` | No | Yes | Status |
| `caseId` | `guid` | No | Yes | Associated case ID |
| `lienId` | `guid` | No | Yes | Associated lien ID |
| `dueDate` | `date` | No | Yes | Due date (format: `YYYY-MM-DD`) |
| `notes` | `string` | No | Yes | Additional notes |
| `resolution` | `string` | No | Yes | Resolution notes |

**Response:** `200 OK` — `ServicingItemResponse`

**Error:** `404 Not Found` — if the servicing item does not exist.

---

### PUT `/api/liens/servicing/{id}/status`

Update the status of a servicing item.

**Permission:** `SYNQ_LIENS.lien:service`

**Path Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `id` | `guid` | Servicing item unique identifier |

**Request Body: `UpdateStatusRequest`**

| Field | Type | Required | Nullable | Description |
|---|---|---|---|---|
| `status` | `string` | Yes | No | New status value |
| `resolution` | `string` | No | Yes | Resolution notes |

**Response:** `200 OK` — `ServicingItemResponse`

**Error:** `404 Not Found` — if the servicing item does not exist.

---

### ServicingItemResponse

| Field | Type | Nullable | Description |
|---|---|---|---|
| `id` | `guid` | No | Unique identifier |
| `taskNumber` | `string` | No | Task number |
| `taskType` | `string` | No | Type of task |
| `description` | `string` | No | Task description |
| `status` | `string` | No | Current status |
| `priority` | `string` | No | Priority level |
| `assignedTo` | `string` | No | Name of assignee |
| `assignedToUserId` | `guid` | Yes | User ID of assignee |
| `caseId` | `guid` | Yes | Associated case ID |
| `lienId` | `guid` | Yes | Associated lien ID |
| `dueDate` | `date` | Yes | Due date |
| `notes` | `string` | Yes | Additional notes |
| `resolution` | `string` | Yes | Resolution notes |
| `startedAtUtc` | `datetime` | Yes | When work was started |
| `completedAtUtc` | `datetime` | Yes | When work was completed |
| `escalatedAtUtc` | `datetime` | Yes | When item was escalated |
| `createdAtUtc` | `datetime` | No | Record creation timestamp |
| `updatedAtUtc` | `datetime` | No | Record last-updated timestamp |

---

## Reports Endpoints

### POST `/api/liens/reports/case-notes-history`

Returns the tenant-wide Case Notes History report used by the Case Tracking Notes and Feed Notes tabs. The compatibility alias is `POST /report/case-notes-history`. Both routes require authenticated SynqLien product access and `SYNQ_LIENS.case:read`, use the tenant from the authenticated request context, and return `Cache-Control: no-store`.

Request:

```json
{
  "noteType": "TRACKING",
  "page": 1,
  "limit": 10,
  "sortBy": "noteDate",
  "sortDirection": "desc"
}
```

`noteType` is required and accepts `TRACKING` or `FEED` case-insensitively. `TRACKING` includes `general` and `follow-up` notes; `FEED` includes only `feed`. Deleted, blank, internal, case-created, and settlement-history notes are excluded. `page` defaults to 1, `limit` defaults to 10 and is limited to 1-100. `sortBy` accepts `caseId`, `caseName`, `noteType`, `noteDate`, `noteAuthor`, or `noteContent`; every order is stabilized by note timestamp and ID.

```json
{
  "isSuccess": true,
  "message": "Case notes history retrieved.",
  "data": [
    {
      "noteId": "019f0000-0000-7000-8000-000000000001",
      "caseRecordId": "019f0000-0000-7000-8000-000000000002",
      "caseId": "26-31959",
      "caseName": "Greenfield Holdings",
      "noteType": "TRACKING",
      "noteTypeLabel": "Case Tracking Note",
      "noteDate": "2026-07-28",
      "createdAtUtc": "2026-07-28T16:30:00.0000000Z",
      "noteAuthor": "Sarah Mitchell",
      "noteContent": "Complete full note text"
    }
  ],
  "page": 1,
  "limit": 10,
  "totalCount": 37,
  "isComplete": false,
  "excludedUnreconciledLegacyNoteCount": 2,
  "warning": {
    "code": "legacy_history_incomplete",
    "message": "Some unreconciled legacy case notes were excluded. Native and reconciled notes are included.",
    "excludedCount": 2
  }
}
```

The legacy alias omits only the additive `createdAtUtc` row property. An empty or out-of-range page is `200` with an empty `data` array. Invalid selectors, paging, or sort values return `400` with `error.code = validation_error`. Native notes and reconciled legacy notes remain visible when stale legacy crosswalks exist. Only eligible report notes whose target IDs belong to unreconciled, tenant-matching `SL-CORE` `SL_CASE_NOTES` crosswalks are excluded. Both routes return `isComplete`, `excludedUnreconciledLegacyNoteCount`, and a nullable `warning`; complete responses set the count to `0`, `isComplete=true`, and `warning=null`.

### POST `/api/liens/reports/case-notes-history/export`

Exports all rows matching `noteType` and the requested ordering; `page` and `limit` are ignored. The compatibility alias is `POST /report/case-notes-history/export`. The CSV contains the six visible report columns, preserves complete Unicode/multiline content, quotes CSV fields, and neutralizes spreadsheet-formula prefixes. The Base64 CSV envelope is retained for legacy clients:

```json
{
  "isSuccess": true,
  "message": "CSV generated successfully.",
  "isComplete": false,
  "excludedUnreconciledLegacyNoteCount": 2,
  "warning": {
    "code": "legacy_history_incomplete",
    "message": "Some unreconciled legacy case notes were excluded. Native and reconciled notes are included.",
    "excludedCount": 2
  },
  "data": [
    {
      "base64": "Q2FzZSBJRCxDYXNlIE5hbWUuLi4=",
      "filename": "case_notes_history_tracking_20260813123000.csv",
      "export_format": "csv"
    }
  ]
}
```

CSV generation stops at 10 MiB and returns `400 validation_error` rather than materializing an unbounded export. Export uses the same tenant-scoped unreconciled-target exclusion and additive completeness fields as preview, so eligible native and reconciled rows are exported without silently including stale legacy classifications.

### GET `/api/liens/reports/weekly-aging`

Returns a paged weekly aging report for liens accepted by a buyer. The report is tenant-safe and restricted to buyer access links whose `SellerOrgId` matches the authenticated organization. It requires authenticated SynqLien product access, sell mode, and `SYNQ_LIENS.lien_sale:view_analytics`. Responses use `Cache-Control: no-store`.

Query parameters:

- `asOfDate`: optional ISO `yyyy-MM-dd`; defaults to the current UTC date.
- `page`: optional, defaults to `1`, and must be at least `1`.
- `pageSize`: optional, defaults to `50`, and must be between `1` and `100`.

The earliest accepted `SellingBuyerAccessLink.RespondedAtUtc` anchors each lien's age. Acceptance day is day 1. The weekly columns are inclusive days 1-7, 8-14, 15-21, 22-28, and more than 28. Future and declined responses are excluded. The amount is the accepted buyer response amount, and each row places that amount in exactly one aging column. `summaryTotals` contains totals for the full matching result set, independent of pagination. Rows are ordered oldest acceptance first, then by lien code and lien ID.

```json
{
  "isSuccess": true,
  "message": "Weekly aging report generated.",
  "asOfDate": "2026-08-25",
  "currency": "USD",
  "page": 1,
  "pageSize": 50,
  "totalCount": 1,
  "totalPages": 1,
  "summaryTotals": {
    "totalLiens": 1,
    "days1To7": 7500.00,
    "days8To14": 0.00,
    "days15To21": 0.00,
    "days22To28": 0.00,
    "moreThan28": 0.00,
    "totalAmount": 7500.00
  },
  "data": [
    {
      "lienCode": "LIEN-2026-001",
      "fundingCompany": "Atlas Funding",
      "days1To7": 7500.00,
      "days8To14": 0.00,
      "days15To21": 0.00,
      "days22To28": 0.00,
      "moreThan28": 0.00,
      "totalAmount": 7500.00
    }
  ]
}
```

`summaryTotals` always covers the complete matching result set, including when a later page is empty. Invalid dates or pagination return `400`; missing authentication or required access returns `401` or `403`.

### GET `/api/liens/reports/monthly-aging`

Returns the same buyer-accepted lien population, funding-company resolution, ordering, authorization, query parameters, pagination metadata, and `Cache-Control: no-store` behavior as the weekly aging endpoint. The monthly columns are inclusive days 1-30, 31-60, 61-90, 91-120, and more than 120. Each row contains `lienCode`, `fundingCompany`, the five monthly amount columns, and `totalAmount`; `summaryTotals` contains the full-result total for every column.

```json
{
  "isSuccess": true,
  "message": "Monthly aging report generated.",
  "asOfDate": "2026-08-25",
  "currency": "USD",
  "page": 1,
  "pageSize": 50,
  "totalCount": 1,
  "totalPages": 1,
  "summaryTotals": {
    "totalLiens": 1,
    "days1To30": 7500.00,
    "days31To60": 0.00,
    "days61To90": 0.00,
    "days91To120": 0.00,
    "moreThan120": 0.00,
    "totalAmount": 7500.00
  },
  "data": [
    {
      "lienCode": "LIEN-2026-001",
      "fundingCompany": "Atlas Funding",
      "days1To30": 7500.00,
      "days31To60": 0.00,
      "days61To90": 0.00,
      "days91To120": 0.00,
      "moreThan120": 0.00,
      "totalAmount": 7500.00
    }
  ]
}
```

### GET `/api/liens/reports/weekly-aging-detail`

Returns the same seller-scoped accepted-lien population, authorization behavior, ordering, and pagination metadata as `GET /api/liens/reports/weekly-aging`. Each item contains only the report's requested detail columns. `fundingCompany` uses the canonical buyer company name when available and otherwise falls back to the legacy buyer contact's organization or display name. `amount` is the accepted buyer response amount. `agingBucket` is the lien's exact age in days, where acceptance day is day 1.

```json
{
  "isSuccess": true,
  "message": "Weekly aging detail report generated.",
  "asOfDate": "2026-08-25",
  "currency": "USD",
  "page": 1,
  "pageSize": 50,
  "totalCount": 1,
  "totalPages": 1,
  "data": [
    {
      "lienCode": "LIEN-2026-001",
      "fundingCompany": "Atlas Funding",
      "amount": 7500.00,
      "agingBucket": 4
    }
  ]
}
```

The endpoint accepts the same optional `asOfDate`, `page`, and `pageSize` query parameters as the weekly aging report and returns `Cache-Control: no-store`.

### POST `/api/liens/reports/auto-generated/{reportId}/execute`

Executes the tenant-scoped stored report using its saved report date. The compatibility alias is `POST /report/auto-generated/{reportId}/execute`. No request body is required.

Query parameters:

- `page`: optional, defaults to `1`, and must be at least `1`.
- `pageSize`: optional, defaults to `50`, and must be between `1` and `100`.

The stored report date ends the inclusive seven-day purchase range (`date - 6 days` through `date`). Eligible Weekly BCC liens are ordered by purchase date, lien number, and record ID before database paging. Only the selected page is enriched. An out-of-range page returns `200` with an empty `data` array while retaining the full-result count and column schema.

```json
{
  "isSuccess": true,
  "message": "Weekly BCC report generated.",
  "report": {
    "reportId": 42,
    "code": "weekly_bcc_2026-08-14",
    "description": "Weekly BCC - 08/14/2026",
    "date": "2026-08-14",
    "createDate": "2026-08-14T11:00:00Z",
    "tenantId": "11111111-1111-1111-1111-111111111111",
    "apiPath": "/api/liens/reports/weekly-bcc"
  },
  "reportType": "WEEKLY_BCC",
  "schemaVersion": 1,
  "asOfDate": "2026-08-14",
  "page": 1,
  "pageSize": 50,
  "totalPages": 4,
  "totalCount": 187,
  "summaryTotals": {
    "totalCases": 120,
    "totalOpenCases": 80,
    "totalClosedCases": 40,
    "totalLiens": 187,
    "totalOpenLiens": 130,
    "totalClosedLiens": 57,
    "totalPurchaseAmt": 22462370.62,
    "totalReturnedAmt": 19089906.53,
    "totalBillingAmt": 79778606.30
  },
  "columns": [
    { "key": "plaintiffFirstName", "label": "Plaintiff First Name", "index": 0 },
    { "key": "caseId", "label": "Case ID", "index": 9 }
  ],
  "data": [
    {
      "plaintiffFirstName": "Ada",
      "caseId": "CASE-001"
    }
  ]
}
```

`columns` always contains all 57 Weekly BCC v1 descriptors. Keys use the same camelCase names as the objects in `data`, and indexes are unique, contiguous, and zero-based (`0` through `56`). The `noted` field is labeled `Notes` in report previews and CSV exports. Invalid pagination returns `400`; missing or cross-tenant reports return `404`; unsupported stored report paths return `409`. Both direct and saved-report execution responses add `summaryTotals` with `totalCases`, `totalOpenCases`, `totalClosedCases`, `totalLiens`, `totalOpenLiens`, `totalClosedLiens`, `totalPurchaseAmt`, `totalReturnedAmt`, and `totalBillingAmt` calculated from the complete eligible result set.

For the `reduction` field, canonical lien reductions take precedence when any exist for the lien. Preserved SL-CORE settlement metadata containing `reductionAmount` is used only when the lien has no canonical reduction.

### POST `/api/liens/reports/auto-generated/{reportId}/export`

Exports all eligible rows from the tenant-scoped stored Weekly BCC report. The compatibility alias is `POST /report/auto-generated/{reportId}/export`. No request body or pagination parameters are required.

Rows retain the same deterministic purchase-date, lien-number, and record-ID order as execution. The exporter enriches bounded pages into a delete-on-close temporary file, writes headers in the versioned 57-column order, quotes CSV values, preserves Unicode and multiline content, and neutralizes spreadsheet-formula prefixes. After size validation, the API Base64-encodes that file incrementally into the response without buffering the full CSV or Base64 string in memory. The response uses the established Base64 CSV envelope:

```json
{
  "isSuccess": true,
  "message": "CSV generated successfully.",
  "data": [
    {
      "base64": "UGxhaW50aWZmIEZpcnN0IE5hbWUsLi4u",
      "filename": "weekly_bcc_20260814.csv",
      "export_format": "csv"
    }
  ]
}
```

The configurable raw CSV limit is enforced before Base64 encoding. `AutoGeneratedReports:ExportSizeLimitMiB` defaults to 50 MiB and accepts values from 1 through 100 MiB. `AutoGeneratedReports:MaximumConcurrentExports` defaults to 2 and accepts values from 1 through 10; the process-wide lease is held through response streaming, and saturated requests return `429` with `Retry-After: 5` and `error.code = too_many_requests`. An oversized export returns `400` with `error.code = validation_error` and identifies the configured ceiling; missing or cross-tenant reports return `404`; unsupported stored report paths return `409`.

### GET `/report/diy/columns`

Returns the legacy DIY-report column metadata and the ordered default selection for the requested report type. For `LIENS`, the default selection includes `days_since_reduction_approval` in position 9 (zero-based), followed by `case_status` and `date_of_loss` in positions 14 and 15 respectively. `initial_service_date` and `number_of_liens` remain available as optional columns but are not selected by default.

DIY lien reports use canonical lien reductions before preserved SL-CORE settlement metadata. When a legacy reduction has no explicit source reduction date, `reduction_date` and `days_since_reduction_approval` are null; a settlement date is never substituted for a reduction approval date.

`GET /lookup/liens/status`, `GET /api/liens/lookups/LienStatus`, and the `LienStatus` section of `GET /lookup/all` expose only the DIY-supported `Open` and `Closed` filters. DIY preview and export exclude rejected lifecycle states (`Declined`, `Withdrawn`, `Cancelled`, and historical `Rejected`) regardless of legacy saved filter input.

`POST /report/diy/filter-options` returns medical-facility option IDs from the canonical facility records referenced by `Lien.FacilityId`. Supplying those IDs in `medicalFacilityIds` therefore uses the same facility relationship as DIY execution and export.

The optional `notes` column returns the latest active, nonblank Feed note for the row's case. `notes_date` returns that exact note's creation date in `MM/dd/yyyy` format. Both columns are grouped under `procedureInfo`, are not selected by default, and use one tenant-scoped batch lookup for CASES, LIENS, and COMBINED reports. Deleted, blank, non-Feed, and cross-tenant notes are excluded. When no eligible Feed note exists, `notes` is empty and `notes_date` is null in preview responses and blank in CSV exports. Equal creation timestamps are resolved by descending note ID. Saved report preview and export use the same mapping.

The existing compatibility keys have these Tracking Notes definitions:

| Key | Label | Value |
|---|---|---|
| `last_case_note` | `Tracking Notes` | All active, nonblank General and Follow-up notes for the case, newest first and separated by `\n` line breaks |
| `last_case_note_date` | `Last Tracking Note Date` | Date of the newest included Tracking Note in `MM/dd/yyyy` format |

Feed, internal, system/history, deleted, blank, and cross-tenant notes are excluded. `POST /report/diy` and its canonical `/api/liens/reports/diy/run` route return the same aggregated value. Both DIY export routes quote the multiline field in the Base64-encoded CSV, so every Tracking Note is retained.

The distinct Case Update fields use the newest active Case Activity row for the tenant. `last_case_tracking_note` is exposed as `Last Activity` and contains its normalized Description; `last_case_tracking_date` is exposed as `Last Activity Date` and contains its Pacific-time Timestamp in `MM/dd/yyyy hh:mm tt` format. Eligible rows match the Case Activity table (`Case Created` and internal Case Details Update entries), equal timestamps use descending activity ID, and preview and CSV export use the same mapping.

Legacy parity values are populated canonical-first. Typed case fields supply plaintiff address components, state of incident, medical status, tracking follow-up date, minor-comp and dropped-case flags, and imported creator. A canonical attorney contact-person link is used when present; migrated legacy attorney IDs are resolved from guarded `SL_CASE_MANAGER` crosswalk metadata. Tenant-scoped canonical company/contact/facility data supplies law-firm, case-manager, attorney, provider, and facility values; preserved legacy note metadata is only a fallback for older rows. `last_activity` and `last_activity_date` are compatibility aliases for `last_case_tracking_note` and `last_case_tracking_date`.
