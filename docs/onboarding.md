# BE-06 — Enquiry onboarding and portal list

Visitors still submit enquiries without accounts. Staff approve a selected enquiry
for onboarding; a verified recipient accepts its invitation and continues the same
conversation. Matching an email address alone never grants access.

## Business workflow

1. Read the enquiry through GET /api/admin/enquiries/{id}.
2. Choose an existing company from GET /api/companies, or create a profile with
   POST /api/admin/companies (`name`, `countryCode`). Existing company profile,
   membership and last-owner protections from BE-03 remain in use.
3. POST /api/admin/enquiries/{id}/onboarding:

```json
{
  "companyId": "00000000-0000-0000-0000-000000000001",
  "membershipRole": "owner",
  "replaceInvitationId": null
}
```

Use a real company UUID. Choose owner for the first company contact, or member for
an additional contact. For a candidate enquiry, send companyId and membershipRole
as null. The server derives recipient email and account type from the enquiry;
the request cannot override them or grant staff roles.

4. The 201 response contains id, token, expiresAt and acceptanceUrl. Tokens are
   random, stored only as SHA-256 digests, and expire after three days. Save the
   response securely; the token cannot be retrieved afterward. The URL uses the
   configured Supabase:FrontendUrl and puts the token in the fragment.
5. Until automated email in BE-07 is implemented, staff must share the returned
   invitation URL manually. The React acceptance page is a separate frontend task.
6. A new recipient registers through POST /api/auth/register with email, password,
   invitationToken; confirms their email and signs in. Existing recipients sign in.
   POST /api/auth/invitations/accept with token and displayName completes onboarding.

Acceptance requires the verified session email to match the invitation. It creates
or reuses a compatible active application account, grants company membership when
applicable, links the enquiry, and consumes the invitation in **one transaction**.
The original conversation ID, messages and timestamps remain intact. A failure in
any step rolls back the entire acceptance. The frontend should reload /api/auth/me
after acceptance; permissions are resolved from the database on each request.

## Inspection and replacement

GET /api/admin/enquiries/{id}/onboarding returns enquiryId, invitationId, approvedBy,
createdAt and invitation state (expiresAt,acceptedAt,revokedAt). It never returns a
usable token. Missing records return 404. These routes require active staff/admin.

To replace a pending, expired or lost invitation, repeat the approval POST with
replaceInvitationId set to the current invitationId. The previous token is revoked
in the same transaction that creates its replacement. Concurrent replacements
cannot both win. Accidental repeat approvals without this field return 409.
Accepted invitations and already linked enquiries cannot be replaced/reapproved.
Incompatible business/candidate options or unavailable companies return 409.

Approval POST is not idempotent. After an uncertain network response, GET the state;
if approval succeeded, explicitly replace that invitation to get a fresh usable
URL. A failed replacement rolls back revocation of the previous invitation.

## Portal conversation list

GET /api/portal/conversations?page=1&pageSize=25 requires an onboarded application
account. Bounds are page 1–100000 and pageSize 1–100. The response is:

```json
{
  "items": [{ "thread": { "id": "...", "enquiryId": "...", "createdAt": "...", "updatedAt": "..." },
              "enquiry": { "id": "...", "reference": "GTS-...", "enquiryType": "business", "service": "Staffing", "status": "new", "companyName": "Example", "createdAt": "..." } }],
  "totalCount": 1,
  "page": 1,
  "pageSize": 25
}
```

Ordering is newest **enquiry** creation first, then enquiry UUID; it is not sorted
by latest reply. Clients see active company memberships; candidates see individual
ownership. Archived companies, revoked memberships and revoked participants are
excluded. Staff use the admin inbox; the portal list does not grant them a global
view. List summaries contain no original message text, contact email or private notes.
Use the existing conversation message endpoints to open a selected thread.
All admin and portal responses use Cache-Control: no-store.

Module boundaries remain explicit: Identity owns invitation/account/membership
writes, Enquiries owns approval and ownership writes, Conversations owns history.
Shared transaction contracts coordinate acceptance; modules do not write each
other's tables. Portal composition uses module contracts and rechecks access before
returning each thread. Counts/pages reflect a database snapshot; concurrent access
revocation may produce a shorter page until the next refresh.

## Database and validation

Migration 20261019000800 creates gts_enquiries.onboarding with FK/unique constraints,
RLS and server-only grants. Applied to the development Supabase project: eight
recorded migrations, 24 application tables, all RLS-enabled with no anon/authenticated
table grants. No existing enquiry, account or conversation is automatically linked.

```powershell
dotnet run --project tests/Onboarding -c Release
dotnet run --project tests/Onboarding -c Release -- --database
npm test --prefix tests/Database
```

HTTP checks use test-only authentication and substitute stores. Hosted checks use
real stores and transactions for approval, replacement, email mismatch, atomic
acceptance, original conversation retention and access isolation/revocations.
Acceptance checks create Auth fixtures only inside transactions that always roll
back; no Auth users/sessions are retained and no emails are sent. Other committed
fixtures are application-only and removed afterward. CI runs HTTP and disposable
migration checks; hosted checks require local development secrets.
