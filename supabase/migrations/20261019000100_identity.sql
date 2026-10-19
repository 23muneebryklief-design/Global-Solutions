begin;
create schema gts_shared;
create schema gts_identity;
create function gts_shared.touch_updated_at() returns trigger
language plpgsql set search_path = '' as $$
begin new.updated_at = now(); return new; end;
$$;

-- Application identities survive Auth deletion so conversation authorship is retained.
create table gts_identity.users (
 id uuid primary key default gen_random_uuid(),
 auth_user_id uuid unique references auth.users(id) on delete set null,
 display_name text not null check (char_length(btrim(display_name)) between 1 and 150),
 email text not null check (email = lower(btrim(email)) and char_length(email) between 3 and 320 and position('@' in email) > 1),
 account_type text not null check (account_type in ('staff','client','candidate')),
 status text not null default 'active' check (status in ('active','suspended','archived')),
 created_at timestamptz not null default now(), updated_at timestamptz not null default now()
);
create unique index users_active_email_uq on gts_identity.users(email) where status <> 'archived';
create table gts_identity.staff (
 user_id uuid primary key references gts_identity.users(id),
 role text not null check (role in ('admin','staff')),
 created_at timestamptz not null default now()
);
create table gts_identity.companies (
 id uuid primary key default gen_random_uuid(),
 name text not null check (char_length(btrim(name)) between 1 and 200),
 country_code text not null check (country_code ~ '^[A-Z]{2}$'),
 status text not null default 'active' check (status in ('active','archived')),
 created_at timestamptz not null default now(), updated_at timestamptz not null default now()
);
create table gts_identity.memberships (
 company_id uuid not null references gts_identity.companies(id),
 user_id uuid not null references gts_identity.users(id),
 role text not null check (role in ('owner','member')),
 status text not null default 'active' check (status in ('active','revoked')),
 created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
 primary key(company_id,user_id)
);
create index memberships_user_idx on gts_identity.memberships(user_id, status);
create table gts_identity.invitations (
 id uuid primary key default gen_random_uuid(),
 company_id uuid references gts_identity.companies(id),
 email text not null check (email = lower(btrim(email)) and char_length(email) between 3 and 320 and position('@' in email) > 1),
 account_type text not null check (account_type in ('client','candidate')),
 membership_role text check (membership_role in ('owner','member')),
 -- Store a SHA-256 digest, never a usable invitation token.
 token_hash text not null unique check (token_hash ~ '^[0-9a-f]{64}$'),
 invited_by uuid not null references gts_identity.staff(user_id),
 accepted_by uuid references gts_identity.users(id),
 expires_at timestamptz not null, accepted_at timestamptz, revoked_at timestamptz,
 created_at timestamptz not null default now(),
 check (expires_at > created_at),
 check ((account_type = 'client' and company_id is not null and membership_role is not null)
     or (account_type = 'candidate' and company_id is null and membership_role is null)),
 check ((accepted_at is null) = (accepted_by is null)),
 check (accepted_at is null or revoked_at is null)
);
create index invitations_email_idx on gts_identity.invitations(email,expires_at);
commit;
