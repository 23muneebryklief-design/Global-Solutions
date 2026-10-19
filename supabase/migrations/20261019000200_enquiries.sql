begin;
create schema gts_enquiries;
create table gts_enquiries.enquiries (
 id uuid primary key default gen_random_uuid(),
 reference text not null unique default ('GTS-' || upper(replace(gen_random_uuid()::text,'-',''))),
 enquiry_type text not null check (enquiry_type in ('business','candidate')),
 name text not null check (char_length(btrim(name)) between 2 and 150),
 email text not null check (email = lower(btrim(email)) and char_length(email) between 3 and 320 and position('@' in email) > 1),
 phone text check (char_length(phone) <= 50),
 company_name text check (char_length(company_name) <= 200),
 country_code text check (country_code ~ '^[A-Z]{2}$'),
 service text not null check (char_length(btrim(service)) between 1 and 150),
 message text not null check (char_length(btrim(message)) between 10 and 10000),
 -- Ownership is linked by a verified onboarding flow, never by email matching alone.
 user_id uuid references gts_identity.users(id),
 company_id uuid references gts_identity.companies(id),
 assigned_staff_id uuid references gts_identity.staff(user_id),
 status text not null default 'new' check (status in ('new','in_progress','closed')),
 consent_at timestamptz not null,
 privacy_notice_version text not null check (char_length(btrim(privacy_notice_version)) between 1 and 50),
 submission_key uuid unique,
 created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
 check (enquiry_type = 'business' or company_id is null)
);
create index enquiries_inbox_idx on gts_enquiries.enquiries(status,created_at desc,id);
create index enquiries_assignee_idx on gts_enquiries.enquiries(assigned_staff_id,status);
create index enquiries_user_idx on gts_enquiries.enquiries(user_id);
create index enquiries_company_idx on gts_enquiries.enquiries(company_id);
create index enquiries_email_idx on gts_enquiries.enquiries(email);
commit;
