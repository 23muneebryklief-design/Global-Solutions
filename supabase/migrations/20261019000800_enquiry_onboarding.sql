begin;
-- The Enquiries module owns approval links; Identity owns invitation lifecycle.
create table gts_enquiries.onboarding (
 enquiry_id uuid primary key references gts_enquiries.enquiries(id),
 invitation_id uuid not null unique references gts_identity.invitations(id),
 approved_by uuid not null references gts_identity.staff(user_id),
 created_at timestamptz not null default now()
);
create index onboarding_approved_by_idx on gts_enquiries.onboarding(approved_by);
alter table gts_enquiries.onboarding enable row level security;
revoke all on gts_enquiries.onboarding from public,anon,authenticated;
grant select,insert,update,delete on gts_enquiries.onboarding to service_role;
commit;
