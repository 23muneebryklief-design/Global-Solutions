begin;
create schema gts_recruitment;
create table gts_recruitment.candidates (
 id uuid primary key default gen_random_uuid(),
 source_enquiry_id uuid unique references gts_enquiries.enquiries(id),
 user_id uuid unique references gts_identity.users(id),
 name text not null check (char_length(btrim(name)) between 2 and 150),
 email text not null check (email = lower(btrim(email)) and position('@' in email) > 1),
 location text check (char_length(location) <= 200),
 preferred_roles text not null default '' check (char_length(preferred_roles) <= 2000),
 experience_summary text not null default '' check (char_length(experience_summary) <= 10000),
 status text not null default 'new' check (status in ('new','screening','available','placed','archived')),
 created_at timestamptz not null default now(), updated_at timestamptz not null default now()
);
create index candidates_status_idx on gts_recruitment.candidates(status,created_at desc);
create table gts_recruitment.staffing_requests (
 id uuid primary key default gen_random_uuid(),
 company_id uuid not null references gts_identity.companies(id),
 source_enquiry_id uuid unique references gts_enquiries.enquiries(id),
 assigned_staff_id uuid references gts_identity.staff(user_id),
 title text not null check (char_length(btrim(title)) between 1 and 200),
 requirements text not null check (char_length(btrim(requirements)) between 1 and 10000),
 workers_needed integer not null default 1 check (workers_needed between 1 and 10000),
 status text not null default 'open' check (status in ('open','sourcing','review','filled','closed')),
 created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
 unique(id,company_id)
);
create index staffing_requests_company_idx on gts_recruitment.staffing_requests(company_id,status);
create index staffing_requests_assignee_idx on gts_recruitment.staffing_requests(assigned_staff_id);
create table gts_recruitment.proposals (
 id uuid primary key default gen_random_uuid(),
 request_id uuid not null, company_id uuid not null,
 candidate_id uuid not null references gts_recruitment.candidates(id),
 proposed_by uuid not null references gts_identity.staff(user_id),
 -- Explicit client-safe snapshot; do not expose the complete candidate record.
 shared_summary text not null check (char_length(btrim(shared_summary)) between 1 and 10000),
 status text not null default 'proposed' check (status in ('proposed','shortlisted','accepted','declined','withdrawn','placed')),
 created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
 foreign key(request_id,company_id) references gts_recruitment.staffing_requests(id,company_id),
 unique(request_id,candidate_id), unique(id,company_id)
);
create index proposals_candidate_idx on gts_recruitment.proposals(candidate_id);
create index proposals_company_idx on gts_recruitment.proposals(company_id,status);
create index proposals_staff_idx on gts_recruitment.proposals(proposed_by);
create table gts_recruitment.feedback (
 id uuid primary key default gen_random_uuid(),
 proposal_id uuid not null, company_id uuid not null, author_user_id uuid not null,
 decision text not null check (decision in ('interested','not_interested','request_information')),
 body text not null default '' check (char_length(body) <= 10000),
 created_at timestamptz not null default now(),
 foreign key(proposal_id,company_id) references gts_recruitment.proposals(id,company_id),
 foreign key(company_id,author_user_id) references gts_identity.memberships(company_id,user_id)
);
create index feedback_proposal_idx on gts_recruitment.feedback(proposal_id,company_id);
create index feedback_author_idx on gts_recruitment.feedback(company_id,author_user_id);
commit;
