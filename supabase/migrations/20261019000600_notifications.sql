begin;
create schema gts_notifications;
create table gts_notifications.email_outbox (
 id uuid primary key default gen_random_uuid(),
 idempotency_key text not null unique check (char_length(btrim(idempotency_key)) between 1 and 200),
 kind text not null check (kind in ('enquiry_confirmation','team_alert','conversation_reply','invitation')),
 message_id uuid references gts_conversations.messages(id),
 recipient_email text not null check (recipient_email = lower(btrim(recipient_email)) and position('@' in recipient_email) > 1),
 subject text not null check (char_length(btrim(subject)) between 1 and 500),
 body text not null check (char_length(btrim(body)) between 1 and 100000),
 status text not null default 'pending' check (status in ('pending','processing','sent','failed','cancelled')),
 attempts integer not null default 0 check (attempts >= 0),
 available_at timestamptz not null default now(),
 locked_until timestamptz, lock_token uuid, sent_at timestamptz,
 last_error text check (char_length(last_error) <= 2000),
 created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
 check ((status = 'processing') = (locked_until is not null and lock_token is not null)),
 check ((locked_until is null) = (lock_token is null)),
 check ((status = 'sent') = (sent_at is not null))
);
create index email_outbox_ready_idx on gts_notifications.email_outbox(available_at,created_at) where status in ('pending','failed');
create index email_outbox_lease_idx on gts_notifications.email_outbox(locked_until) where status = 'processing';
create index email_outbox_message_idx on gts_notifications.email_outbox(message_id);
create table gts_notifications.delivery_attempts (
 id uuid primary key default gen_random_uuid(),
 outbox_id uuid not null references gts_notifications.email_outbox(id),
 attempt_number integer not null check (attempt_number > 0),
 provider text not null, provider_message_id text,
 outcome text not null check (outcome in ('accepted','failed','unknown')),
 error_code text, error_detail text check (char_length(error_detail) <= 2000),
 created_at timestamptz not null default now(),
 unique(outbox_id,attempt_number)
);
create index delivery_attempts_provider_idx on gts_notifications.delivery_attempts(provider,provider_message_id);
create table gts_notifications.webhook_events (
 id uuid primary key default gen_random_uuid(),
 provider text not null, provider_event_id text not null,
 event_type text not null,
 payload jsonb not null check (jsonb_typeof(payload) = 'object'),
 status text not null default 'pending' check (status in ('pending','processing','processed','unmatched','failed')),
 attempts integer not null default 0 check (attempts >= 0),
 last_error text check (char_length(last_error) <= 2000),
 received_at timestamptz not null default now(), processed_at timestamptz,
 unique(provider,provider_event_id),
 check ((status = 'processed') = (processed_at is not null))
);
create index webhook_events_status_idx on gts_notifications.webhook_events(status,received_at);
commit;
