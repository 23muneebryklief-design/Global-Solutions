# BE-04 — Enquiry submission and management

Public enquiries are saved in PostgreSQL without requiring an account.
Only authenticated active staff/admin users can access the admin endpoints.
Public responses contain a receipt/reference, never the enquiry ID, contact
details, assignments, consent metadata or timestamps.

## Public submission

POST /api/enquiries with JSON:

```json
{
  "enquiryType": "business",
  "name": "Example Contact",
  "email": "contact@example.com",
  "phone": "+1 555 0100",
  "companyName": "Example Company",
  "countryCode": "US",
  "service": "Remote administrative assistant",
  "message": "We need an experienced assistant for our operations team.",
  "consent": true,
  "submissionKey": "f8e09b16-c6ac-4389-9c69-71e2528cd337",
  "website": ""
}
```

For candidates, use enquiryType=candidate, leave companyName null/empty and use
service for the preferred role. Put experience and requirements in message.
CV uploads come in BE-09; this endpoint does not accept files.

Generate a fresh UUID submissionKey for each new form. Reuse that UUID with the
same content when retrying a request after a network error. A new submission
returns 201 with reference/message. An exact normalized retry returns 200 with
the same reference. Reusing a key for different content returns 409. Retries
are serialized by PostgreSQL and do not change the original updatedAt value.

Required values:
- enquiryType: business or candidate.
- name: 2–150 trimmed characters.
- email: plain valid address, at most 320 characters.
- service: 1–150 trimmed characters.
- message: 10–10000 trimmed characters.
- companyName: required for businesses, empty for candidates; at most 200 characters.
- consent: true. The frontend must show the privacy notice and collect this explicitly.
- submissionKey: nonempty UUID.

Optional phone is limited to 50 characters; countryCode must be two ASCII
letters and is stored uppercase. Email is stored lowercase and text is trimmed.
Null characters are rejected before PostgreSQL. The server stores the consent
timestamp and privacy-notice version v1. Change the server version when publishing
a revised notice; clients cannot supply or forge its recorded timestamp/version.

The website field is a hidden honeypot. Keep it empty, hidden and excluded from
keyboard navigation/autofill in React. Filled values receive a generic 202 and
are not saved. Rate limiting permits five attempts per client IP every ten
minutes and returns 429 afterward. Replays and rejected submissions count too.
JSON bodies are capped at 64 KiB (413). Kestrel also enforces the cap for chunked
requests before model binding.

## Admin inbox

All routes below require a verified staff/admin bearer token:

| Method | Route | Behaviour |
| --- | --- | --- |
| GET | /api/admin/enquiries | Search/filter/paginate |
| GET | /api/admin/enquiries/{id} | Full detail or 404 |
| GET | /api/admin/enquiries/assignees | Active staff IDs/names/roles |
| PATCH | /api/admin/enquiries/{id} | Update status and/or assignment |

List parameters:
- status: new, in_progress or closed.
- enquiryType: business or candidate.
- assignedStaffId: UUID, or unassigned=true (mutually exclusive).
- search: literal partial name, email, company or reference; at most 200 characters.
- page: 1–100000, default 1.
- pageSize: 1–100, default 25.

Response fields are items, totalCount, page, pageSize, totalPages. Ordering is
createdAt descending then id descending. Count/page reads share a consistent
database snapshot. Pages beyond the result set return empty items and retain
the total. Search is parameterized and treats %, _ and backslash literally.
Admin responses are marked no-store by the API pipeline.

## Assignment and status updates

Read the enquiry first, then PATCH using its exact updatedAt:

```json
{
  "expectedUpdatedAt": "2026-10-19T12:00:00.123456+00:00",
  "status": "in_progress",
  "assignedStaffId": "4766a8dc-b1d9-4dd3-8cb5-2b09b27db99c",
  "clearAssignment": false
}
```

Use a real ID from /assignees. Omit assignedStaffId to preserve the assignment;
set clearAssignment=true to remove it. Setting both is invalid. Missing fields
preserve their current values. At least one change and expectedUpdatedAt are
required. Only new/in_progress/closed are accepted; staff may reopen enquiries.

A stale timestamp returns 409 without overwriting the record. React must reload
the enquiry and let the user review the newer version before retrying. Unknown
IDs return 404. New assignments require an active staff account, checked through
Identity's shared IStaffDirectory contract. Existing assignments are not silently
removed if a staff account is subsequently suspended.

No endpoint publicly retrieves an enquiry by its reference. Submission does not
link company/user ownership by email. Those links require the verified onboarding
workflow. There are no deletion endpoints in this ticket.

## Scope and verification

BE-05 now creates the enquiry, its designated thread and original visitor message
in one database transaction. See [conversation contracts](conversations.md).
BE-07 now queues confirmation emails and staff alerts transactionally. Delivery
remains disabled until provider setup; see [email delivery](email-delivery.md).
No frontend files from the separate design repository were changed.

```powershell
dotnet build tests/Enquiries/EnquiryChecks.csproj --configuration Release -m:1
dotnet run --project tests/Enquiries --configuration Release --no-build
dotnet run --project tests/Enquiries --configuration Release --no-build -- --database
```

The HTTP suite uses TestServer, substitute storage and test-only authentication to
exercise validation, access boundaries, request limits and update response codes.
The hosted suite uses the real stores and PostgreSQL to check persistence,
concurrent deduplication, search, pagination, status/assignment and stale edits.
It removes only its UUID-marked disposable fixtures and never creates Auth users.
CI runs the HTTP suite; hosted tests require local development user secrets.

Restart the API in Development after building. Submit a public enquiry in Swagger,
log in, Authorize with your access token and use the admin enquiry routes.
