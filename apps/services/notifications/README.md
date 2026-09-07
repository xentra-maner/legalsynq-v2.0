# Notifications Service

Multi-channel notification delivery — email (SendGrid/SMTP), SMS (Twilio), push, and webhook.

**Port:** 5025

## Responsibilities

- Transactional and event-driven notification delivery
- Template management (platform defaults + tenant overrides)
- Multi-channel dispatch (email, SMS, push, webhook)
- Governance rules — per-tenant policy packs controlling what can be sent
- Governance approval workflow and release management
- Canary rollout and tenant-segmented governance deployment
- Per-tenant rule scoping and isolation
- Cross-channel governance federation (unified runtime enforcement)
- Notification history and retry management
- Dead-letter queue for blocked/failed notifications
- Personal, tenant-scoped in-app inbox items for supported SynqLien Selling events

## Layer Structure

```
Notifications.Api/            Endpoints, middleware, Program.cs (port 5025)
Notifications.Application/    Template resolution, delivery orchestration, governance engines
Notifications.Domain/         Notification, Template, GovernanceRule, DeliveryAttempt
Notifications.Infrastructure/ DbContext (NotificationsDb), SendGrid adapter, Twilio adapter
```

## Key Endpoint Groups

| Prefix | Description |
|---|---|
| `/v1/notifications` | Send + list notifications |
| `/v1/inbox` | User-only in-app inbox list, summary, read, mark-all-read, and dismissal |
| `/v1/templates` | Template CRUD |
| `/notifications/v1/admin/governance/rules` | Governance rule management |
| `/notifications/v1/admin/governance/runtime/` | Runtime status, telemetry, simulate |

## Governance Runtime

Five channel engines: Email, Push, Webhook, SMS (compatibility), and a federation layer. All evaluate against per-tenant governance rules before delivery. Fail-open when `FailOpenOnRuntimeError = true` (default). Decisions are persisted to telemetry for audit.

## External Providers

- **Email:** SendGrid (primary) + SMTP/MailKit (fallback) — configured via `SENDGRID_API_KEY`, `SENDGRID_FROM_EMAIL`, `SENDGRID_FROM_NAME` secrets
- **SMS:** Twilio — configured via `TWILIO_ACCOUNT_SID`, `TWILIO_AUTH_TOKEN`, `TWILIO_FROM_NUMBER` secrets
- **Webhook:** Configurable per-template

## Database

`NotificationsDb` (MySQL, separate from all other services).

## Service Auth

Inbound service calls authenticated via service JWT (`FLOW_SERVICE_TOKEN_SECRET`). Legacy `X-Tenant-Id` header path maintained for backward compatibility.

## Selling user inbox

The inbox is separate from the operational notification-delivery log. Its endpoints require a user JWT containing valid
GUID `sub` and `tenant_id` claims and reject service identities containing `svc`. Tenant and recipient scope always come
from those claims; missing, dismissed, cross-user, and cross-tenant item IDs return `404`.

| Method | Path | Behavior |
|---|---|---|
| `GET` | `/v1/inbox` | Lists items ordered by `occurredAtUtc DESC, id DESC`. Supports `category=all|lien|message`, `readState=all|unread`, `page`, `pageSize=10|25|50`, and the returned `asOfUtc` snapshot. |
| `GET` | `/v1/inbox/summary?limit=3` | Returns the unread count and latest visible items; `limit` is 1–10. |
| `PUT` | `/v1/inbox/{id}/read` | Idempotently marks one owned item read. |
| `POST` | `/v1/inbox/mark-all-read` | Marks owned items through the required JSON `throughUtc` value as read. |
| `DELETE` | `/v1/inbox/{id}` | Permanently hides one owned item through an idempotent soft dismissal. |

Producer submissions normalize `in_app`, `in-app`, and `inapp` to canonical `in_app`. An in-app request must target a
concrete platform-user GUID and include `inboxPresentation` with category, title, generic description, occurrence time,
source display name, and initials. Only the `liens` product events `lien.offer.submitted`, `lien.offer.accepted`,
`lien.offer.rejected`, and `lien.offer.message.created` are materialized. The already-sent operational notification and
its `ntf_UserInboxItems` row are persisted atomically, with one inbox row per source notification.

## Postman

Import [notifications-selling-inbox.postman_collection.json](../../../artifacts/postman/notifications-selling-inbox.postman_collection.json)
and its paired [local environment](../../../artifacts/postman/notifications-selling-inbox.postman_environment.json). The
collection separates the service JWT used for `POST /v1/notifications` from the user JWT used by `/v1/inbox` requests.
