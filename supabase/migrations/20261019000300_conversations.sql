begin;
create schema gts_conversations;
create table gts_conversations.threads (
 id uuid primary key default gen_random_uuid(),
 enquiry_id uuid not null unique references gts_enquiries.enquiries(id),
 created_at timestamptz not null default now(), updated_at timestamptz not null default now()
);
create table gts_conversations.participants (
 thread_id uuid not null references gts_conversations.threads(id),
 user_id uuid not null references gts_identity.users(id),
 last_read_at timestamptz,
 joined_at timestamptz not null default now(), revoked_at timestamptz,
 primary key(thread_id,user_id)
);
create index participants_user_idx on gts_conversations.participants(user_id);
create table gts_conversations.messages (
 id uuid primary key default gen_random_uuid(),
 thread_id uuid not null references gts_conversations.threads(id),
 sender_kind text not null check (sender_kind in ('visitor','client','staff','system')),
 sender_user_id uuid references gts_identity.users(id),
 channel text not null check (channel in ('form','portal','email','system')),
 body text not null check (char_length(btrim(body)) between 1 and 20000),
 client_message_key uuid unique,
 -- Incoming provider events are deduplicated independently from their subject line.
 provider text, provider_message_id text,
 created_at timestamptz not null default now(),
 unique(id,thread_id),
 check (sender_kind not in ('client','staff') or sender_user_id is not null),
 check ((provider is null) = (provider_message_id is null)),
 check (channel <> 'email' or provider_message_id is not null)
);
create unique index messages_provider_uq on gts_conversations.messages(provider,provider_message_id)
 where provider_message_id is not null;
create index messages_thread_idx on gts_conversations.messages(thread_id,created_at,id);
create index messages_sender_idx on gts_conversations.messages(sender_user_id);
-- Separate storage prevents notes from being accidentally included in message queries.
create table gts_conversations.internal_notes (
 id uuid primary key default gen_random_uuid(),
 thread_id uuid not null references gts_conversations.threads(id),
 author_staff_id uuid not null references gts_identity.staff(user_id),
 body text not null check (char_length(btrim(body)) between 1 and 10000),
 created_at timestamptz not null default now(), updated_at timestamptz not null default now()
);
create index internal_notes_thread_idx on gts_conversations.internal_notes(thread_id,created_at);
create table gts_conversations.reply_routes (
 id uuid primary key default gen_random_uuid(),
 thread_id uuid not null references gts_conversations.threads(id),
 token_hash text not null unique check (token_hash ~ '^[0-9a-f]{64}$'),
 allowed_sender_email text not null check (allowed_sender_email = lower(btrim(allowed_sender_email)) and position('@' in allowed_sender_email) > 1),
 revoked_at timestamptz, expires_at timestamptz,
 created_at timestamptz not null default now(),
 check (expires_at is null or expires_at > created_at)
);
create index reply_routes_thread_idx on gts_conversations.reply_routes(thread_id);
commit;
