# BE-08 — Incoming replies and delivery webhooks

Implemented and disabled by default. No provider account, receiving domain or webhook
secret is configured. Tests use signed fixtures and fake provider responses; no real
emails are received or sent. Existing outbound delivery also remains disabled.

## How a reply finds its conversation

When IncomingEmail:Enabled is true and Domain is configured, new enquiry confirmations
and staff replies receive a random r-...@receiving-domain Reply-To address. Conversations
creates the route in the same database transaction as the outbound job. Its 240-bit
capability is stored as a SHA-256 hash; the full Reply-To address is encrypted in the
outbox using the existing Data Protection key ring. Routes expire after 90 days.
Retries use the same stored route. SMTP local parts stay below 64 characters and use
lowercase hex so mail clients normalizing address case do not break routing.

A signed email.received webhook contains metadata, not the email body. After durable
receipt, a background worker retrieves the provider email by UUID through Resend's
receiving API. The API response is capped at 2 MiB, requests html_format=cid, and never
downloads raw MIME, embedded images or attachments. Provider IDs must match.

Automatic import requires all of the following:

- One unambiguous route token addressed to the configured receiving domain.
- An active, unexpired route whose allowed sender matches the canonical From address.
- The provider-computed authentication result has DMARC=pass and SPF or DKIM=pass.
  Raw Authentication-Results headers are never treated as trusted evidence.
- The enquiry's current contact is still authorized. Account suspension, changed
  verified email, revoked company membership or revoked thread participation prevents
  automatic import through an old address.
- Plain text of 1–20000 characters without null characters, and no attachments.

The subject/reference never grants access or selects a thread. A match inserts one
visitor/email message in that thread, using provider + provider email UUID to deduplicate
even separate webhook IDs for the same email. It updates thread activity without
changing ownership, granting an account, or sending another email. Original messages
remain intact; plain text and quoted text are preserved, with server ingestion time.
React must render message strings as text rather than raw HTML.

Email authentication is evidence about domain authorization, not a portal login or
proof of an individual's identity. No incoming event grants application permissions.

## Secure webhook receipt

POST /api/webhooks/resend is anonymous with respect to portal bearer tokens. It requires
the exact raw-body Svix HMAC-SHA256 signature, including svix-id and svix-timestamp.
Timestamp tolerance is five minutes in either direction; comparisons are constant-time.
IncomingEmail:PreviousWebhookSecret supports controlled key rotation. Duplicate header
values, invalid signatures and stale timestamps are rejected before storage.

Requests are bounded to 256 KiB, including chunked bodies, and rate limited to 300/minute
per client IP. The endpoint returns 503 while disabled or without a valid signing secret,
401 for failed verification, 400 for malformed signed events, and 413 for oversized
payloads. A 202 response means the event is durably saved, not fully processed.
Database failure never receives a false successful acknowledgment.

The same provider event ID/payload is acknowledged once. A changed payload under the
same event ID returns 409. Raw verified payloads and normalized received content are
encrypted inside private RLS-protected tables. Unsupported signed event types are
recorded as processed/ignored instead of being interpreted as incoming messages.

Processing uses SKIP LOCKED claims, two-minute leases and unique completion tokens.
Crashes are recoverable. Provider/network failures retry with exponential backoff up
to eight attempts; terminal failures remain visible to staff. An unknown outbound ID
can retry safely when its webhook races the outgoing sender's database acknowledgment.
This worker processes reads/imports; it never retries an outgoing send itself.

## Staff review

Uncertain incoming emails remain outside all portal conversations with status unmatched
and a controlled reason. This includes unexpected senders, unknown/expired routes,
missing/failed authentication, ambiguous recipients, HTML-only content, attachments,
and oversized bodies. HTML and attachments are not rendered or downloaded. Staff can
inspect the provider separately and supply an explicit plain-text version if needed.

| Method | Route | Purpose |
| --- | --- | --- |
| GET | /api/admin/email-events/configuration | Enabled/domain/signature/provider flags, no secrets |
| GET | /api/admin/email-events | Filter/paginate events, including unmatched and failed |
| GET | /api/admin/email-events/{id} | Safe plain-text preview, reasons and thread/message links |
| POST | /api/admin/email-events/{id}/review | Explicit attach, ignore or retry |

All review routes require active staff/admin and return no-store responses. Clients,
candidates and anonymous users cannot inspect unmatched mail. List supports status
pending/processing/processed/unmatched/failed, page=1–100000 and pageSize=1–100 (defaults
1 and 25). Results contain items,totalCount,page,pageSize, newest receipt first.
Detail previews are limited to 20000 characters with bodyTruncated, hasHtml and
attachmentCount indicators. Raw payloads, HTML, capability addresses and signing keys
are never returned by these routes.

Review example:

```json
{
  "expectedUpdatedAt": "2026-10-19T12:00:00.123456+00:00",
  "action": "attach",
  "threadId": "00000000-0000-0000-0000-000000000001",
  "reason": "Confirmed the sender independently before linking this message",
  "editedBody": null
}
```

Use the current timestamp from event detail and a real thread UUID. Attach is an
explicit staff decision to add received text to a selected conversation, including
messages that could not be authenticated automatically. It does not grant account
access or import attachments. Supply editedBody when plain text is missing/too long.
Ignore marks the event processed without creating a message. Retry requeues an unmatched
or failed event after configuration/manual investigation, preserving content and using
the same provider ID to prevent duplication. Each action records the staff actor and
reason. Concurrent/stale reviews return 409; missing events return 404. A provider email
already assigned to another conversation cannot be copied into a second thread.

## Delivery tracking

Signed delivered, bounced, complained, failed, suppressed and delivery_delayed events
update email_outbox.delivery_status and delivery_event_at independently of sending status.
GET /api/admin/emails now includes these fields. Sent still means provider acceptance.
Delivered means the provider reported recipient-server delivery, not that a person read it.

Events normally correlate via the provider message ID in delivery_attempts. Outgoing
requests also include the gts_outbox_id provider tag; a signed matching tag and recipient
can correlate an event when the initial HTTP send result was unknown. It cannot match
an outbox job with zero attempts or an unrelated recipient. Tags are not accepted from
an unsigned request or an incoming message header.

Status precedence is complained > bounced > failed > delivered > delayed > unknown.
A late/out-of-order delivery event cannot hide a bounce or complaint. Equally ranked
events retain the latest event time. Delivery events do not trigger a new send, alter
conversation content, or prove an incoming sender's identity. Unknown IDs remain failed
for investigation/reprocessing rather than changing an unrelated email record.

## Enabling live reception later

1. Set up a Resend receiving domain/address and its MX records. Do not replace an
   existing business mailbox's MX records without a deliberate mail-routing plan;
   a dedicated receiving subdomain is suitable for reply routes.
2. Configure IncomingEmail:Domain and a provider key authorized to retrieve incoming
   emails (Email:ApiKey in local user secrets or the deployment secret store).
3. Publish a reachable HTTPS /api/webhooks/resend endpoint, register the supported
   event types with Resend and securely save IncomingEmail:WebhookSecret. Keep signing
   secrets out of appsettings.json, Git and chat. Persist/share the Data Protection
   key ring as described in email-delivery.md before storing real emails.
4. Set IncomingEmail:Enabled=true and restart. Confirm safe configuration flags, then
   test an enquiry receipt and a reply using an address you control. Outbound delivery
   is enabled separately with Email:Enabled after its sender-domain setup.
5. Review the unmatched/failed event queues and provider bounce signals in the portal
   implementation. Old messages sent without a route are not matched by subject;
   new outgoing emails use routes only when incoming routing is enabled/configured.

No live provider/domain changes were made during implementation.

## Migration and checks

20261019001000 adds event leases, encrypted normalized content, review metadata and
outbox delivery state. Applied to development Supabase: ten recorded migrations,
24 application tables, all RLS-enabled without browser-role table grants.

```powershell
dotnet run --project tests/IncomingEmail -c Release
dotnet run --project tests/IncomingEmail -c Release -- --database
```

Tests cover the published Svix verification vector, payload tampering, replay timing,
rotation, role boundaries, provider parsing, route isolation, sender authentication,
revocation, duplicate events/emails, concurrent leases/reviews, explicit reprocessing
and out-of-order delivery signals. Hosted claims are restricted to disposable UUIDs;
they never consume real queued events or send/receive actual mail. Fixtures are removed.

References: [Resend received email API and authentication](https://resend.com/docs/api-reference/emails/retrieve-received-email),
[Resend webhook verification](https://resend.com/docs/webhooks/verify-webhooks-requests),
[Svix signed payload format](https://docs.svix.com/receiving/verifying-payloads/how-manual).
