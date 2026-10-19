begin;
alter table gts_notifications.email_outbox
 add column enquiry_id uuid references gts_enquiries.enquiries(id),
 add column invitation_id uuid references gts_identity.invitations(id),
 add column from_email text,
 add column reply_to text,
 add column first_attempt_at timestamptz;
create index email_outbox_enquiry_idx on gts_notifications.email_outbox(enquiry_id);
create index email_outbox_invitation_idx on gts_notifications.email_outbox(invitation_id);
-- Existing RLS, private grants and updated_at trigger remain in effect.
commit;
