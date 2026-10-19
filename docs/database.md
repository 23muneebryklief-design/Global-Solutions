# BE-02 — Supabase PostgreSQL foundation

## Scope

Eight ordered SQL migrations in `supabase/migrations` create 24 application tables.
They define storage and integrity rules, not authentication endpoints, enquiry
workflows, mail workers, malware scanning, or storage bucket provisioning.
Those behaviours belong to subsequent tickets. No remote database is changed
when the API starts, and no connection string is needed for the migration tests.

Development project: `global-talent-solutions-dev` (`aqoiglalbsqprvngmqbf`, Ireland).
The seven initial migrations were applied on 19 October 2026. The hosted check
confirmed the initial 23 tables, RLS on every application table, no browser table grants,
and a rolled-back enquiry write/read test. Versions are recorded in
`supabase_migrations.schema_migrations`. Credentials remain in local user secrets.
The .NET API readiness endpoint also returned HTTP 200 against this hosted project.
BE-06 added migration 20261019000800 for enquiry onboarding. The hosted database
now has eight recorded migrations and 24 application tables; RLS and browser-grant
checks passed again. See [onboarding](onboarding.md).

Advisor review: no default-severity items were shown. Informational results include
RLS without browser policies (intentional for these private schemas) and unused
indexes (expected on a new database). Additional foreign-key indexes were suggested
for internal note authors, invitation company/inviter/acceptor, and proposal request
lookup. Record these in the next versioned performance migration; do not rewrite
the already applied initial files.

## Module ownership

| Schema | Tables | Responsibility |
| --- | --- | --- |
| gts_identity | users, staff, companies, memberships, invitations | Application identities and company access |
| gts_enquiries | enquiries | Guest submissions, ownership, assignment and progress |
| gts_conversations | threads, participants, messages, internal_notes, reply_routes | Separate histories and secure email routing metadata |
| gts_recruitment | candidates, staffing_requests, proposals, feedback | Candidate records and company recruitment progress |
| gts_documents | files, enquiry_attachments, message_attachments, candidate_documents, proposal_documents | Private file metadata and explicit sharing relationships |
| gts_notifications | email_outbox, delivery_attempts, webhook_events | Durable email work, retries and deduplication |

`gts_shared` holds only the updated-at trigger function. Timestamps use timestamptz;
identifiers use random UUIDs. Foreign keys reference stable IDs across modules for
integrity. These references do not authorise modules to write each other's tables.
The API will coordinate explicit module operations within a transaction where needed.

## Relationships and boundaries

- Supabase owns `auth.users`, passwords and sessions. Application users reference
  only its primary key. Deleting an Auth user nulls that link while preserving
  the application user and conversation history. Account suspension and access
  revocation must be checked by BE-03; a null Auth link cannot authenticate.
- Users can have memberships in multiple companies. Staff privileges come from
  the staff table, never from browser-editable identity metadata.
- Invitations contain only a SHA-256 token digest, expiry and intended recipient.
  Single-use redemption, recipient verification and membership activation are BE-03.
- An enquiry can exist without any account. Verified onboarding explicitly links
  users/companies and thread participants. Matching an email address never grants access.
- Each enquiry has at most one thread; the enquiry workflow in BE-04/05 must create
  the enquiry, thread and initial message atomically. Tables alone do not create them.
- Messages and private notes are separate tables. Notes require a staff author.
  Membership status, participant revocation, author identity and role must be
  checked by the API; foreign keys enforce existence, not active authorisation.
- Candidate proposals have a client-safe summary. Composite foreign keys ensure
  proposals belong to their staffing request's company and feedback belongs to
  a member of that same company. Revoked memberships still preserve historical
  feedback; BE-03/10 must reject new writes from revoked members.
- Message attachments have a composite reference to prevent assigning a message
  to a different thread. Other file-sharing checks, clean-scan enforcement and
  document ownership validation belong to BE-09. Metadata does not make objects public.
- The email outbox has stable idempotency keys, retry counters, scheduled times
  and recoverable worker leases. Delivery attempts retain provider results.
  BE-07 must insert a message and its email task in the same transaction and claim
  work safely. A sent timestamp records provider acceptance, not guaranteed inbox delivery.
- Incoming message IDs and webhook event IDs have unique provider-scoped keys.
  BE-08 must verify signatures and sender/reply-route ownership before storing
  or routing inbound events. Do not store credentials in webhook payloads or logs.

## Access model

Every application table has RLS enabled. `anon`, `authenticated` and `PUBLIC`
have no module-schema/table/function access, with no browser RLS policies.
Keep these module schemas out of the Supabase Data API exposed schema list.
Frontend users interact with the .NET API, which will validate tokens and permissions.
The privileged server role has explicit access and bypasses RLS; it must never
be given to browsers. Later API integration should use a dedicated server-only
PostgreSQL connection, with least-privilege credentials configured for the host.
This ticket does not create database passwords or provision that runtime role.

The security migration hardens existing objects and future defaults. Future
migrations must explicitly enable RLS and grant the server required access.

## Verification

From the repository root, with Node 24 installed:

```powershell
npm ci --prefix tests/Database --ignore-scripts
npm test --prefix tests/Database
```

The tests apply every migration to a fresh disposable PostgreSQL engine provided
by PGlite. They bootstrap only minimal Supabase Auth/role fixtures. Tests cover
constraints, relationships, RLS/grants, timestamp triggers, duplicate delivery
events and transaction rollback. They do not run the Supabase Auth/Storage
services or prove hosted networking, JWT validation or portal authorisation.
GitHub CI runs this suite alongside the .NET build and API smoke checks.

## Apply to a Supabase project

1. Create a dedicated development Supabase project. Confirm its project reference
   and check for existing tables before applying these initial migrations.
2. Install the official Supabase CLI and sign in with `supabase login`.
3. From the repository root, run `supabase link --project-ref YOUR_DEV_PROJECT_REF`.
   Enter the database password only when prompted; never commit it or send it in chat.
4. Run `supabase db push --dry-run` and review the pending migrations (eight for a fresh database).
5. Run `supabase db push`, then `supabase migration list` to confirm all versions.
6. Check all six module schemas exist and remain absent from exposed API schemas.
7. Run the full local Supabase verification below before staging deployment.

If this project already has application data, stop and reconcile migration history
before pushing. These initial migrations deliberately fail on existing conflicting
objects rather than silently replacing them. They contain no demo users or passwords.
Never edit a migration that has been applied; add a new version instead. Migrations
are transactional. Restore from a tested backup or write a forward repair migration
for a deployed issue; no destructive automatic down script is supplied.

## Connect the .NET API to hosted Supabase

After provisioning the development project, open its Connect dialog and choose
Session pooler (port 5432) if your network needs IPv4. Copy only the host and
username into this command; the password is requested securely:

```powershell
./scripts/configure-supabase.ps1 -DatabaseHost YOUR_POOLER_HOST -DatabaseUser postgres.YOUR_PROJECT_REF
```

This stores `ConnectionStrings:Supabase` in local .NET user secrets, never Git.
User secrets are local development storage, not an encrypted production vault.
The script enables certificate and hostname verification. The API bundles the
public Supabase CA certificate from the project's Database Settings download
and uses it for its database connection unless an explicit root is configured.
Certificate source: https://supabase-downloads.s3-ap-southeast-1.amazonaws.com/prod/ssl/prod-ca-2021.crt
Use the project's
actual values from Connect; do not guess its region or pooler address.
Restart the API in Development, then visit `/api/health/ready`. HTTP 200 means
the database connection works; HTTP 503 means it is missing or unavailable.
This connectivity probe does not replace migration verification.

The connector manages the project/migrations but does not automatically configure
the local API's database password. Do not send database passwords in chat.
Production should use a separate runtime role with the documented module grants;
the development project's postgres account is not the production permission model.

## Full local Supabase verification

With Supabase CLI and Docker installed, start an isolated local stack with
`supabase start`, then run `supabase db reset --local` from this repository.
The reset recreates only the local stack's database and applies the migrations.
Do not use a linked/remote reset. This environment did not have Docker, psql or
Supabase CLI, so this full-stack check remains to be run before deployment.

## Decisions for later tickets

- File metadata caps files at 20 MiB and accepts PDF, Word, JPEG and PNG. Keep
  the API and private bucket limits consistent when BE-09 provisions storage.
- Data retention/anonymisation and account-deletion policy require a separate
  explicit workflow. Most history relationships restrict hard deletion.
- This is a fresh database foundation. No data from the separate frontend
  reference repository is imported, and no existing hosted schema is assumed.

References: [Supabase migrations](https://supabase.com/docs/guides/deployment/database-migrations),
[Supabase Auth user data](https://supabase.com/docs/guides/auth/managing-user-data).
