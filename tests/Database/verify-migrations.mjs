import { PGlite } from '@electric-sql/pglite';
import { readdir, readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

// Disposable real PostgreSQL engine (WASM), not a SQL parser or mocked database.
// Only Supabase-owned Auth/roles are stubbed; their service behaviour is not tested.
const db = new PGlite();
let passed = 0;
async function check(name, action) {
  await action(); passed++; console.log(`PASS: ${name}`);
}
async function rejects(sql, code, params = []) {
  await assert.rejects(db.query(sql, params), error => error.code === code);
}
async function one(sql, params = []) { return (await db.query(sql, params)).rows[0]; }
try {
  await db.exec(`
    create role anon; create role authenticated; create role service_role bypassrls;
    create schema auth;
    create table auth.users(id uuid primary key default gen_random_uuid());
  `);
  const migrations = fileURLToPath(new URL('../../supabase/migrations/', import.meta.url));
  for (const name of (await readdir(migrations)).filter(x => x.endsWith('.sql')).sort()) {
    await db.exec(await readFile(`${migrations}/${name}`, 'utf8'));
    console.log(`Applied ${name}`);
  }
  await check('All module tables created on a fresh database', async () => {
    const { count } = await one(`select count(*)::int from pg_tables where schemaname like 'gts_%'`);
    assert.equal(count, 24);
  });
  await check('Every application table enables RLS', async () => {
    const { count } = await one(`select count(*)::int from pg_tables where schemaname like 'gts_%' and not rowsecurity`);
    assert.equal(count, 0);
  });
  const auth = await one(`insert into auth.users default values returning id`);
  const admin = await one(`insert into gts_identity.users(auth_user_id,display_name,email,account_type) values($1,'Admin','admin@example.test','staff') returning id`, [auth.id]);
  await db.query(`insert into gts_identity.staff(user_id,role) values($1,'admin')`, [admin.id]);
  const client = await one(`insert into gts_identity.users(display_name,email,account_type) values('Client','client@example.test','client') returning id`);
  const company = await one(`insert into gts_identity.companies(name,country_code) values('First Company','US') returning id`);
  const otherCompany = await one(`insert into gts_identity.companies(name,country_code) values('Other Company','GB') returning id`);
  await db.query(`insert into gts_identity.memberships(company_id,user_id,role) values($1,$2,'owner')`, [company.id,client.id]);
  await check('Duplicate company membership rejected', () => rejects(`insert into gts_identity.memberships(company_id,user_id,role) values($1,$2,'member')`, '23505', [company.id,client.id]));
  await check('Invalid company role rejected', () => rejects(`insert into gts_identity.memberships(company_id,user_id,role) values($1,$2,'admin')`, '23514', [otherCompany.id,client.id]));
  await check('Unnormalised identity email rejected', () => rejects(`insert into gts_identity.users(display_name,email,account_type) values('Bad','Bad@Example.test','client')`, '23514'));
  await check('Duplicate active email rejected', () => rejects(`insert into gts_identity.users(display_name,email,account_type) values('Duplicate','client@example.test','client')`, '23505'));
  await check('Plaintext invitation token rejected', () => rejects(`insert into gts_identity.invitations(email,account_type,token_hash,invited_by,expires_at) values('candidate@example.test','candidate','plaintext',$1,now()+interval '1 day')`, '23514', [admin.id]));
  await check('Expired-at-creation invitation rejected', () => rejects(`insert into gts_identity.invitations(email,account_type,token_hash,invited_by,expires_at) values('candidate@example.test','candidate',repeat('a',64),$1,now()-interval '1 day')`, '23514', [admin.id]));
  await check('Client invitation requires a company and role', () => rejects(`insert into gts_identity.invitations(email,account_type,token_hash,invited_by,expires_at) values('client2@example.test','client',repeat('b',64),$1,now()+interval '1 day')`, '23514', [admin.id]));
  const business = await one(`insert into gts_enquiries.enquiries(enquiry_type,name,email,service,message,consent_at,privacy_notice_version) values('business','Guest Business','guest@example.test','Staffing','We need a remote assistant',now(),'v1') returning *`);
  const candidate = await one(`insert into gts_enquiries.enquiries(enquiry_type,name,email,service,message,consent_at,privacy_notice_version) values('candidate','Guest Candidate','candidate@example.test','Opportunity','Looking for an opportunity',now(),'v1') returning *`);
  const approvalInvite = await one(`insert into gts_identity.invitations(email,account_type,company_id,membership_role,token_hash,invited_by,expires_at) values('guest@example.test','client',$1,'owner',repeat('c',64),$2,now()+interval '1 day') returning id`, [company.id,admin.id]);
  await db.query(`insert into gts_enquiries.onboarding(enquiry_id,invitation_id,approved_by) values($1,$2,$3)`, [business.id,approvalInvite.id,admin.id]);
  await check('One onboarding approval per enquiry enforced', () => rejects(`insert into gts_enquiries.onboarding(enquiry_id,invitation_id,approved_by) values($1,$2,$3)`, '23505', [business.id,approvalInvite.id,admin.id]));
  await check('Nonstaff onboarding approver rejected', () => rejects(`insert into gts_enquiries.onboarding(enquiry_id,invitation_id,approved_by) values($1,gen_random_uuid(),$2)`, '23503', [candidate.id,client.id]));
  await check('Business and candidate guest enquiries need no account', async () => {
    assert.equal(business.user_id, null); assert.equal(candidate.user_id, null);
    assert.notEqual(business.reference,candidate.reference); assert.match(business.reference,/^GTS-/);
  });
  await check('Unknown enquiry type rejected', () => rejects(`update gts_enquiries.enquiries set enquiry_type='other' where id=$1`, '23514',[business.id]));
  await check('Unknown enquiry status rejected', () => rejects(`update gts_enquiries.enquiries set status='missing' where id=$1`, '23514',[business.id]));
  await check('Candidate enquiry cannot belong to a client company', () => rejects(`update gts_enquiries.enquiries set company_id=$1 where id=$2`, '23514',[company.id,candidate.id]));
  await check('Non-staff enquiry assignee rejected', () => rejects(`update gts_enquiries.enquiries set assigned_staff_id=$1 where id=$2`, '23503',[client.id,business.id]));
  await check('Updated timestamps maintained by database', async () => {
    const result = await one(`update gts_enquiries.enquiries set updated_at='2000-01-01', status='in_progress' where id=$1 returning updated_at`, [business.id]);
    assert.ok(new Date(result.updated_at).getFullYear() > 2000);
  });
  const thread = await one(`insert into gts_conversations.threads(enquiry_id) values($1) returning id`,[business.id]);
  const secondThread = await one(`insert into gts_conversations.threads(enquiry_id) values($1) returning id`,[candidate.id]);
  await check('One thread per enquiry enforced', () => rejects(`insert into gts_conversations.threads(enquiry_id) values($1)`, '23505',[business.id]));
  await check('Unknown enquiry cannot have a thread', () => rejects(`insert into gts_conversations.threads(enquiry_id) values(gen_random_uuid())`, '23503'));
  const message = await one(`insert into gts_conversations.messages(thread_id,sender_kind,channel,body) values($1,'visitor','form','Original enquiry') returning id`,[thread.id]);
  await check('Empty message rejected', () => rejects(`insert into gts_conversations.messages(thread_id,sender_kind,channel,body) values($1,'visitor','form',' ')`, '23514',[thread.id]));
  await check('Staff reply requires an author', () => rejects(`insert into gts_conversations.messages(thread_id,sender_kind,channel,body) values($1,'staff','portal','Hello')`, '23514',[thread.id]));
  await db.query(`insert into gts_conversations.internal_notes(thread_id,author_staff_id,body) values($1,$2,'Staff only')`,[thread.id,admin.id]);
  await check('Internal notes are separate from shared messages', async () => {
    const { count } = await one(`select count(*)::int from gts_conversations.messages where body='Staff only'`); assert.equal(count,0);
  });
  await check('Client cannot be a note author', () => rejects(`insert into gts_conversations.internal_notes(thread_id,author_staff_id,body) values($1,$2,'Invalid note')`, '23503',[thread.id,client.id]));
  await db.query(`insert into gts_conversations.messages(thread_id,sender_kind,channel,body,provider,provider_message_id) values($1,'visitor','email','Email reply','provider','msg-1')`,[thread.id]);
  await check('Duplicate incoming message rejected', () => rejects(`insert into gts_conversations.messages(thread_id,sender_kind,channel,body,provider,provider_message_id) values($1,'visitor','email','Duplicate','provider','msg-1')`, '23505',[thread.id]));
  const profile = await one(`insert into gts_recruitment.candidates(source_enquiry_id,name,email) values($1,'Candidate','candidate@example.test') returning id`,[candidate.id]);
  const request = await one(`insert into gts_recruitment.staffing_requests(company_id,title,requirements) values($1,'Assistant','Office experience') returning id`,[company.id]);
  const proposal = await one(`insert into gts_recruitment.proposals(request_id,company_id,candidate_id,proposed_by,shared_summary) values($1,$2,$3,$4,'Approved client summary') returning id`,[request.id,company.id,profile.id,admin.id]);
  await check('Proposal cannot target a different company than its request', () => rejects(`update gts_recruitment.proposals set company_id=$1 where id=$2`, '23503',[otherCompany.id,proposal.id]));
  await check('Feedback cannot cross company boundaries', () => rejects(`insert into gts_recruitment.feedback(proposal_id,company_id,author_user_id,decision) values($1,$2,$3,'interested')`, '23503',[proposal.id,otherCompany.id,client.id]));
  await db.query(`insert into gts_recruitment.feedback(proposal_id,company_id,author_user_id,decision) values($1,$2,$3,'interested')`,[proposal.id,company.id,client.id]);
  const file = await one(`insert into gts_documents.files(object_path,original_name,content_type,size_bytes) values('test/cv.pdf','cv.pdf','application/pdf',100) returning *`);
  await check('New documents default to private storage and pending scan', async () => {
    assert.equal(file.bucket_name,'private-documents'); assert.equal(file.scan_status,'pending');
  });
  await check('Unsupported file type rejected', () => rejects(`insert into gts_documents.files(object_path,original_name,content_type,size_bytes) values('test/run.exe','run.exe','application/octet-stream',100)`, '23514'));
  await check('Oversized file rejected', () => rejects(`insert into gts_documents.files(object_path,original_name,content_type,size_bytes) values('test/large.pdf','large.pdf','application/pdf',20971521)`, '23514'));
  await check('Attachment cannot be assigned to a different message thread', () => rejects(`insert into gts_documents.message_attachments(file_id,message_id,thread_id) values($1,$2,$3)`, '23503',[file.id,message.id,secondThread.id]));
  const email = await one(`insert into gts_notifications.email_outbox(idempotency_key,kind,message_id,recipient_email,subject,body) values('reply-1','conversation_reply',$1,'client@example.test','Reply','Hello') returning id`,[message.id]);
  await check('Email queue deduplicates delivery requests', () => rejects(`insert into gts_notifications.email_outbox(idempotency_key,kind,recipient_email,subject,body) values('reply-1','team_alert','client@example.test','Duplicate','Hello')`, '23505'));
  await check('Processing email requires a recoverable worker lease', () => rejects(`update gts_notifications.email_outbox set status='processing' where id=$1`, '23514',[email.id]));
  await check('Sent email requires a sent timestamp', () => rejects(`update gts_notifications.email_outbox set status='sent' where id=$1`, '23514',[email.id]));
  await db.query(`insert into gts_notifications.webhook_events(provider,provider_event_id,event_type,payload) values('provider','event-1','email.received','{}')`);
  await check('Webhook event redelivery deduplicated', () => rejects(`insert into gts_notifications.webhook_events(provider,provider_event_id,event_type,payload) values('provider','event-1','email.received','{}')`, '23505'));
  await check('Transaction rollback leaves neither reply nor email task', async () => {
    await db.exec('begin');
    const reply = await one(`insert into gts_conversations.messages(thread_id,sender_kind,sender_user_id,channel,body) values($1,'staff',$2,'portal','Rollback marker') returning id`,[thread.id,admin.id]);
    await db.query(`insert into gts_notifications.email_outbox(idempotency_key,kind,message_id,recipient_email,subject,body) values('rollback-email','conversation_reply',$1,'client@example.test','Reply','Rollback marker')`,[reply.id]);
    await db.exec('rollback');
    assert.equal((await one(`select count(*)::int from gts_conversations.messages where body='Rollback marker'`)).count,0);
    assert.equal((await one(`select count(*)::int from gts_notifications.email_outbox where idempotency_key='rollback-email'`)).count,0);
  });
  for (const role of ['anon','authenticated']) {
    await check(`${role} cannot read or write application tables directly`, async () => {
      await db.exec(`set role ${role}`);
      try {
        await rejects(`select * from gts_conversations.messages`, '42501');
        await rejects(`insert into gts_identity.companies(name,country_code) values('Forbidden','US')`, '42501');
      } finally { await db.exec('reset role'); }
    });
  }
  await check('Server service_role can access module tables', async () => {
    await db.exec('set role service_role');
    try { assert.ok((await one(`select count(*)::int from gts_enquiries.enquiries`)).count >= 2); assert.equal((await one(`select count(*)::int from gts_enquiries.onboarding`)).count,1); }
    finally { await db.exec('reset role'); }
  });
  await check('Deleting Auth identity preserves application history', async () => {
    await db.query(`delete from auth.users where id=$1`,[auth.id]);
    assert.equal((await one(`select auth_user_id from gts_identity.users where id=$1`,[admin.id])).auth_user_id,null);
    assert.equal((await one(`select count(*)::int from gts_conversations.internal_notes`)).count,1);
  });
  console.log(`\n${passed} database checks passed.`);
} finally { await db.close(); }
