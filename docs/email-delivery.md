# BE-07 — Reliable outbound email

Implemented with delivery **disabled**. No provider account or domain is configured,
and no real emails have been sent. New submissions and replies still save normally.
Their email jobs remain pending until delivery is explicitly configured and enabled.

## What queues an email

- A new enquiry queues a receipt to the visitor and an alert to Email:TeamEmail.
- A staff reply queues an email to the original enquiry contact. Once linked to an
  account, it uses that account's current verified address and requires an active
  account/company membership. Revoked contacts are not emailed new replies.
- Both general invitations and enquiry onboarding invitations queue an email with
  the acceptance link. Replacing an invitation cancels its pending email; the worker
  also checks for expired, revoked or accepted invitations before sending.
- Private notes and portal user replies do not send emails in this ticket.

Every email job is written in the same transaction as its originating record through
SharedKernel contracts. A database queue failure rolls back that operation; provider
failures happen later and never remove saved enquiries or messages. Exact submission
and reply retries reuse the existing jobs. Existing historical records are not
automatically emailed when this ticket is installed.

## Queue and worker

The API hosts a background worker. It claims a due job with FOR UPDATE SKIP LOCKED,
sets a unique two-minute lease, and records an attempt before calling the provider.
Only the current lease token can finish a job. A crashed worker's expired lease can
be reclaimed; its unfinished attempt remains unknown. Successful submissions store
the provider message ID and mark the job sent.

The initial adapter uses Resend's HTTPS API with a stable Idempotency-Key derived
from the outbox UUID. The sender/reply-to envelope is frozen on first claim so retries
keep the same provider payload. The HTTP timeout is 20 seconds; workers pause one
second between jobs and five seconds when idle. Multiple instances can share a queue.

429,408,409,5xx and network/timeout failures retry with exponential backoff starting
at 30 seconds, up to one hour, respecting Retry-After. Other 4xx errors stop retries.
Automatic retries stop at eight attempts or 20 hours after the first claim. Resend's
idempotency retention is 24 hours; the shorter application window avoids blindly
retrying after that protection expires. This is at-least-once processing with provider
deduplication, not an unconditional exactly-once delivery guarantee.

No blind retry endpoint resets that window. Terminal failures require staff review,
provider-log reconciliation and a deliberate operational recovery. Fixing a provider
configuration does not automatically resend terminal failures. Timeouts can have an
unknown outcome; do not infer that the provider definitely rejected the email.

**Sent means the provider accepted the request.** It does not prove inbox delivery.
BE-08 now implements [incoming replies and delivery webhooks](incoming-email.md).
Reception remains disabled pending provider setup. When incoming routing is disabled,
ReplyTo must remain a monitored mailbox; incoming email is not automatically imported.

## Admin routes

All require verified active staff/admin access and return no-store responses.

| Method | Route | Purpose |
| --- | --- | --- |
| GET | /api/admin/emails/configuration | Enabled/configured flags and provider name only |
| GET | /api/admin/emails | Queue status and correlation IDs |
| GET | /api/admin/emails/{id}/attempts | Provider IDs, outcomes and safe error codes; 404 if missing |

Queue filters: status=pending/processing/sent/failed/cancelled (optional), page=1–100000
and pageSize=1–100; defaults 1 and 25. Results contain items,totalCount,page,pageSize,
ordered newest creation first. Queue records include enquiryId,invitationId,messageId
when applicable. API responses never include message bodies, invitation tokens, API
keys or raw provider error bodies. Error values are controlled codes such as http_429
and provider_timeout. Provider HTTP request logging is disabled.

## Provider setup when a domain is available

1. Create a Resend account, add your sending domain and complete its DNS verification.
2. Configure Email:FromEmail and a monitored Email:ReplyTo. TeamEmail currently uses
   the temporary personal administrator address and can be changed independently.
3. Store Email:ApiKey using .NET user secrets locally or the deployment secret store.
   Never commit it or paste it into chat. No API key is present in appsettings.json.
4. Configure and back up a persistent Data Protection key ring before real invitations
   are queued. The application name is GlobalTalentSolutions. Windows defaults use the
   account's key storage; Email:KeyDirectory can select an explicit directory outside
   the repository. Multiple API instances must share the same keys/application name.
   On non-Windows hosts, protect the key store with restricted filesystem permissions
   and an appropriate deployment key-encryption mechanism. Losing keys makes queued
   payloads unreadable; the worker will not send ciphertext.
5. Review pending recipients before enabling delivery: previously queued jobs will be
   processed, except stale invitations. Set Email:Enabled=true and restart the API.
   Enabling without API key/from/reply-to/team configuration leaves delivery paused
   and logs a configuration error. Verify an email to an address you control first.

All queued bodies are encrypted with ASP.NET Core Data Protection, including bearer
invitation URLs. The PostgreSQL tables remain private with RLS. Legacy plaintext
outbox rows from earlier operational tooling can still be read; new jobs use dp:v1.

Supabase Auth registration/confirmation/password recovery emails remain handled by
Supabase Auth. Configure its custom SMTP separately when ready; these are not sent
through the application outbox.

## Database and tests

Migration 20261019000900 adds enquiry/invitation correlations, frozen sender fields
and the first-attempt timestamp. The hosted development project now has nine recorded
migrations and 24 application tables; all have RLS and no browser-role table grants.

```powershell
dotnet run --project tests/Notifications -c Release
dotnet run --project tests/Notifications -c Release -- --database
```

HTTP tests cover staff access and validation. Provider tests use a fake HTTP handler;
database tests use real stores, fixture-only queue claims, ephemeral encryption keys
and fake acceptance results. They exercise atomic enqueueing, retries, concurrent
claims, lease recovery, protected invitation payloads, cancellation and rollback.
They never call a real provider or consume real queued emails. Fixtures are removed.
CI runs HTTP/provider tests and fresh-database migration checks; hosted checks need
the saved development database secret.

References: [Resend send API](https://resend.com/docs/api-reference/emails/send-email),
[provider idempotency](https://resend.com/docs/dashboard/emails/idempotency-keys).
