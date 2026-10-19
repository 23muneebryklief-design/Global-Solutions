# BE-05 — Enquiry conversations

Each enquiry has one designated conversation. New submissions save the enquiry,
thread and original visitor/form message in a single transaction. Original message
timestamps are preserved. Retrying a submission never duplicates its thread.

## Routes

All routes require a verified bearer session. Admin routes require active staff/admin.
Portal routes require explicit enquiry account ownership or active company membership.
Email matching never grants access. Revoked participants are denied; revoked company
memberships are denied even when the enquiry also names that user's account.
Unauthorized thread/enquiry IDs return 404. Anonymous requests return 401 and
nonstaff requests to admin routes return 403.

| Method | Route | Purpose |
| --- | --- | --- |
| GET | /api/admin/enquiries/{id}/conversation | Get designated thread; initialize legacy enquiry if needed |
| GET | /api/portal/enquiries/{id}/conversation | Get own enquiry's designated thread |
| GET | /api/admin/conversations/{id}/messages | Read conversation |
| POST | /api/admin/conversations/{id}/messages | Staff reply |
| GET | /api/portal/conversations/{id}/messages | Read own conversation |
| POST | /api/portal/conversations/{id}/messages | User reply |
| GET | /api/admin/conversations/{id}/notes | Read staff-only notes |
| POST | /api/admin/conversations/{id}/notes | Add staff-only note |

Message/note lists accept page=1–100000 and pageSize=1–100 (defaults 1 and 50).
Responses contain items,totalCount,page,pageSize. Messages sort chronologically
by createdAt,id, retaining the original enquiry first. A page and count share a
database snapshot. All admin/portal responses have Cache-Control: no-store.

POST a reply with body (1–20000 trimmed characters, no null characters) and a
fresh clientMessageKey UUID. Retain the same UUID/content across network retries.
New messages return 201; identical retries return the original message with 200;
changed content, actor or thread under an existing key returns 409. Concurrent
retries are serialized in PostgreSQL. Sender, author, channel and timestamps
are server-controlled. Reply bodies are plain text; React must render them as text.

POST a note with body (1–10000 trimmed characters, no null characters). Notes
are stored separately and never appear in message queries. There is no portal
notes endpoint. Note POSTs are not idempotent: after an uncertain network result,
reload notes before manually resubmitting. No edit/delete routes are provided.
Writes share a 60/minute IP limit (429). Request bodies are capped at 128 KiB
(413); Kestrel enforces this for chunked requests too.

## Operations and verification

Run from the repository root:

```powershell
dotnet run --project tools/BackfillConversations -c Release
dotnet run --project tests/Conversations -c Release
dotnet run --project tests/Conversations -c Release -- --database
```

Backfill is restricted to the approved hosted development project and uses local
user secrets and the bundled CA. It initializes only missing threads, preserves
original enquiry text/time and verifies all enquiries have original messages.
It can be safely rerun. No schema migration is required for this ticket.

The HTTP suite checks authorization, validation and response codes with TestServer.
Hosted checks exercise real transactions, concurrent retries, thread isolation,
revocations, separate notes and atomic rollback. They remove disposable fixtures
and never create or modify Supabase Auth users. CI runs the HTTP suite only.

Account/company ownership links are now set through the [BE-06 onboarding flow](onboarding.md).
Public enquiries remain staff-only until explicitly linked; this ticket
does not add an email-based claim endpoint. Outbound email and inbound email replies
come in the notification tickets. Current replies are saved portal messages only.
