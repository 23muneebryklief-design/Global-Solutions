# Global Talent Solutions backend

ASP.NET Core 10 modular monolith. This repository contains the backend; the existing React design is a separate reference project.

## Local setup

Install the .NET 10 SDK (10.0.301 or newer patch).
Run from the repository root:

```powershell
dotnet restore GlobalTalentSolutions.sln
dotnet build GlobalTalentSolutions.sln
dotnet run --project src/GlobalTalentSolutions.Api -- --urls http://localhost:5187 --environment Development
```

Open http://localhost:5187/swagger for API documentation.
Health endpoints: `/api/health/live` and `/api/health/ready`.
Liveness checks the process. Readiness checks the Supabase database connection and
returns HTTP 503 until a working server-only connection is configured.

Run `./scripts/verify-backend.ps1` after building (PowerShell 7).
CI builds Release and runs the same smoke checks on pushes to main/backend and pull requests.

## Architecture and boundaries

- Identity: accounts, invitations, roles, company memberships.
- Enquiries: public submissions, requirements, status, assignment.
- Conversations: threads, messages, participants, private notes.
- Notifications: email outbox, retries, delivery, inbound processing.
- Documents: private files, scanning, download permissions.
- Recruitment: staffing requests, proposals, feedback, progress.
- SharedKernel: only small shared contracts; no business workflows.
- Api: composition root, HTTP pipeline and infrastructure wiring.

Each module references SharedKernel only. The API composes modules. A module owns its data and writes only its own tables. Communication uses explicit shared contracts. Enquiry creation supplies its database transaction to the Conversations initializer so both modules save atomically.

## Configuration and operations

Nonsecret defaults live in appsettings.json. Use environment variables or local user secrets for credentials; never commit them. Environment variables use double underscores for nested keys.
In production, terminate TLS at a correctly configured host/reverse proxy and configure trusted forwarded headers before HTTPS redirection when applicable. Configure AllowedHosts to your deployment hostnames.

Swagger is enabled only in Development. Responses use Problem Details with traceId; unexpected errors return a generic message. JSON console logs include request path, status, timing and trace ID. Request bodies, query values and credentials are not logged.

BE-01 includes scaffolding, diagnostics, API documentation and CI.
BE-02 adds the Supabase PostgreSQL schema, seven versioned migrations and executable
database integrity checks. See [database setup and architecture](docs/database.md).

```powershell
npm ci --prefix tests/Database --ignore-scripts
npm test --prefix tests/Database
```

Node is used only for disposable PostgreSQL migration tests, not by the .NET backend.
BE-03 adds authentication, invitations, company membership checks and initial
administrator setup. See [authentication setup and API contracts](docs/authentication.md).
BE-04 adds public enquiry submission and staff inbox/search/assignment/status APIs.
See [enquiry contracts and examples](docs/enquiries.md).
BE-05 adds designated enquiry conversations, portal replies and private staff notes.
See [conversation contracts and checks](docs/conversations.md).
BE-06 adds enquiry approval/invitations, atomic account links and each user's
portal conversation list. See [onboarding workflow](docs/onboarding.md).
Automated email delivery and deployment are subsequent tickets.
The development Supabase database is now
provisioned; see docs/database.md for its migration and verification status.
