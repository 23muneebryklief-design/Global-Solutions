begin;
create schema gts_documents;
create table gts_documents.files (
 id uuid primary key default gen_random_uuid(),
 uploaded_by uuid references gts_identity.users(id),
 bucket_name text not null default 'private-documents' check (bucket_name = 'private-documents'),
 object_path text not null unique check (char_length(btrim(object_path)) between 1 and 1024),
 original_name text not null check (char_length(btrim(original_name)) between 1 and 255),
 content_type text not null check (content_type in ('application/pdf','application/msword','application/vnd.openxmlformats-officedocument.wordprocessingml.document','image/jpeg','image/png')),
 size_bytes bigint not null check (size_bytes between 1 and 20971520),
 sha256 text check (sha256 ~ '^[0-9a-f]{64}$'),
 scan_status text not null default 'pending' check (scan_status in ('pending','clean','infected','failed')),
 scanned_at timestamptz, deleted_at timestamptz,
 created_at timestamptz not null default now(),
 check (scan_status = 'pending' or scanned_at is not null)
);
create index files_uploader_idx on gts_documents.files(uploaded_by);
create index files_pending_idx on gts_documents.files(scan_status,created_at) where deleted_at is null;
create table gts_documents.enquiry_attachments (
 file_id uuid primary key references gts_documents.files(id),
 enquiry_id uuid not null references gts_enquiries.enquiries(id)
);
create index enquiry_attachments_enquiry_idx on gts_documents.enquiry_attachments(enquiry_id);
create table gts_documents.message_attachments (
 file_id uuid primary key references gts_documents.files(id),
 message_id uuid not null, thread_id uuid not null,
 foreign key(message_id,thread_id) references gts_conversations.messages(id,thread_id)
);
create index message_attachments_message_idx on gts_documents.message_attachments(message_id,thread_id);
create table gts_documents.candidate_documents (
 file_id uuid primary key references gts_documents.files(id),
 candidate_id uuid not null references gts_recruitment.candidates(id),
 document_type text not null check (document_type in ('cv','supporting'))
);
create index candidate_documents_candidate_idx on gts_documents.candidate_documents(candidate_id);
create table gts_documents.proposal_documents (
 proposal_id uuid not null references gts_recruitment.proposals(id),
 file_id uuid not null references gts_documents.files(id),
 shared_by uuid not null references gts_identity.staff(user_id),
 created_at timestamptz not null default now(),
 primary key(proposal_id,file_id)
);
create index proposal_documents_file_idx on gts_documents.proposal_documents(file_id);
create index proposal_documents_staff_idx on gts_documents.proposal_documents(shared_by);
commit;
