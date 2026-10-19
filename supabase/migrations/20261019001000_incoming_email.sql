begin;
alter table gts_notifications.webhook_events
 add column payload_hash text,
 add column provider_email_id text,
 add column event_at timestamptz,
 add column available_at timestamptz not null default now(),
 add column locked_until timestamptz,
 add column lock_token uuid,
 add column content jsonb,
 add column thread_id uuid references gts_conversations.threads(id),
 add column message_id uuid references gts_conversations.messages(id),
 add column reviewed_by uuid references gts_identity.staff(user_id),
 add column review_reason text check(char_length(review_reason)<=1000),
 add column updated_at timestamptz not null default now();
alter table gts_notifications.webhook_events add constraint webhook_lease_pair check((locked_until is null)=(lock_token is null));
create trigger touch_updated_at before update on gts_notifications.webhook_events for each row execute function gts_shared.touch_updated_at();
create index webhook_ready_idx on gts_notifications.webhook_events(available_at,received_at) where status in ('pending','failed','processing');
create index webhook_email_idx on gts_notifications.webhook_events(provider,provider_email_id);
create index webhook_thread_idx on gts_notifications.webhook_events(thread_id);
create index webhook_message_idx on gts_notifications.webhook_events(message_id);
create index webhook_reviewer_idx on gts_notifications.webhook_events(reviewed_by);
alter table gts_notifications.email_outbox
 add column delivery_status text not null default 'unknown' check(delivery_status in ('unknown','delayed','delivered','failed','bounced','complained')),
 add column delivery_event_at timestamptz;
-- Private schemas retain their RLS and existing server-only grants.
commit;
