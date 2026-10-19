# BE-03 — Authentication and access

## Implemented

The API verifies bearer tokens with Supabase Auth on every authenticated request.
Only verified email identities are accepted. After provider verification, the
token subject must match the returned user and its session must still exist in
`auth.sessions` and be unexpired. Application account status and staff roles are
loaded from the database, not user-editable metadata or a hardcoded email list.

An authenticated Supabase account without an accepted application invitation can
accept an invitation or set its password, but cannot access company or staff data.
Suspended/archived application accounts cannot authenticate to protected endpoints.

Access tokens are returned in the login/refresh response. React should keep them
in memory, not URLs or persistent browser storage. Refresh tokens are placed in
an HttpOnly, SameSite=Strict cookie scoped to /api/auth. Cookies are Secure outside
Development; local Development allows HTTP. Production requires HTTPS and a same-origin
reverse proxy. The refresh endpoint requires `X-GTS-Client: portal`, preventing
cross-site form requests. Do not add permissive credentialed CORS. Serialize refresh
requests in the frontend to avoid token-rotation races.

## Endpoints

| Method | Route | Access |
| --- | --- | --- |
| POST | /api/auth/login | Public; email/password |
| POST | /api/auth/refresh | Refresh cookie + X-GTS-Client header |
| POST | /api/auth/logout | Verified bearer session; signs out this session |
| GET | /api/auth/me | Active application account |
| POST | /api/auth/register | Valid invitation token, matching email, 12–128 character password |
| POST | /api/auth/password-recovery | Public; generic response without account enumeration |
| PUT | /api/auth/password | Verified bearer session; 12–128 character password |
| PUT | /api/auth/email | Active application account; provider email confirmation |
| POST | /api/auth/invitations/accept | Verified bearer session matching invitation email |
| POST | /api/admin/invitations | Staff/admin |
| POST | /api/admin/companies | Staff/admin |
| GET | /api/admin/access | Staff/admin access check |
| GET | /api/admin/admin-access | Admin-only access check |
| GET | /api/companies/ | Own active memberships; staff can list active companies (up to 100) |
| GET | /api/companies/{id} | Own active company or staff |
| GET | /api/companies/{id}/members | Company owner or staff (up to 100 members) |
| PATCH | /api/companies/{id}/members/{userId} | Company owner or staff |

Company access returns 404 for inaccessible IDs so it does not reveal other
companies. Ownership is rechecked inside the membership update transaction.
Company-row locking prevents concurrent removal of the last active owner.
Membership status and company status are checked live; revocation is not delayed
until JWT refresh. Profile access uses the authenticated ID only.

Login/register/refresh are rate-limited by client IP. Recovery has a stricter
limit. Before deploying behind a proxy, configure trusted forwarded headers so
rate limits use the correct address. Tokens, passwords, provider response bodies
and authorization headers are not logged by the application.

## Invitation lifecycle

1. Staff creates a company if inviting a business user.
2. Staff POSTs an invitation: `{ "email": "...", "accountType": "client",
   "companyId": "...", "membershipRole": "owner" }`; candidates use
   `accountType: "candidate"` with null company and membership role.
3. The API returns a random single-use token and an acceptance URL. Only a
   SHA-256 digest is stored. Default expiry is three days. Treat the response as
   secret; do not log it or expose it to other users.
4. The invited person registers via /api/auth/register, confirms their email,
   signs in and POSTs `{ "token": "...", "displayName": "..." }` to the accept
   endpoint. Existing Supabase users can sign in and accept directly.
5. Acceptance locks the invitation row and atomically creates the account,
   activates company membership and marks the invitation used. A token cannot
   grant staff/admin access. Existing member roles are preserved when reactivating
   memberships, and unrelated accounts are never linked by email alone.

Invitation emails are NOT automatically sent yet. Staff can share the returned
link; automated delivery belongs to BE-07. Password recovery and signup confirmation
use Supabase's configured mail service. The provider may restrict recipients/rates
until custom SMTP is configured; accepted requests do not guarantee mail delivery.

The React /accept-invitation, /auth/confirm and /auth/reset-password screens are
frontend work. Register `http://localhost:5173/auth/confirm` and
`http://localhost:5173/auth/reset-password` in Supabase's redirect allowlist before
using those email flows locally. Frontend confirmation must establish a Supabase
session and send its access token to the API. Frontend pages are not implemented
by this backend ticket.

## Initial administrator and changing email

Initial administrator: 23muneebryklief@gmail.com. The Supabase Auth account was
created by its owner through the dashboard, then explicitly linked to an
application staff/admin record. Passwords were not accessed by the bootstrap tool.

The operational bootstrap command is:

```powershell
dotnet run --project tools/BootstrapAdmin -- --email YOUR_VERIFIED_EMAIL
```

Run from the repository root. It uses local database user secrets, checks that
the Supabase account is verified and refuses to create an additional administrator
when a different administrator already exists. It is not an HTTP endpoint and
is not invoked automatically when the application starts.

To change your login email later, sign in and PUT `{ "email": "new@example.com" }`
to /api/auth/email. Complete Supabase's email confirmation flow. Once Supabase
returns the verified new email, the API syncs the application profile; the user
ID, admin role and memberships remain unchanged. No admin email is hardcoded in
authorization logic.

## Swagger verification

Restart the API in Development, call POST /api/auth/login with your credentials,
then copy the returned accessToken into Swagger's Authorize button. Do not paste
tokens or passwords into chat. GET /api/auth/me should return your account and
GET /api/admin/admin-access should return 200.

An anonymous request to /api/auth/me returns 401; an authenticated non-staff
account calling /api/admin/access returns 403. Login does not grant application
access merely because Supabase issued a token.

## Tests

```powershell
dotnet build tests/Identity/IdentityChecks.csproj --configuration Release -m:1
dotnet run --project tests/Identity --configuration Release --no-build
```

The HTTP suite uses ASP.NET TestServer with substitute provider/store services to
test real routing, authentication and authorization policies without sending mail.
The provider adapter is separately checked for rejected/unverified/subject-mismatched
tokens. CI runs the suite.

Optional hosted checks (approved development project only):

```powershell
dotnet run --project tests/Identity --configuration Release --no-build -- --database
```

These use the real IdentityStore against PostgreSQL. They create only disposable
application fixtures without Auth IDs, exercise ownership, revocation, last-owner
protection, token hashing/expiry and session lookup, and remove their fixtures in
a finally block. They do not create Supabase Auth users or send emails. Complete
email delivery and real-user login/refresh/recovery journeys need a configured
mail sender and the user's own credentials; they are not simulated as live passes.

References: [password-based authentication](https://supabase.com/docs/guides/auth/passwords),
[Supabase sessions](https://supabase.com/docs/guides/auth/sessions).
