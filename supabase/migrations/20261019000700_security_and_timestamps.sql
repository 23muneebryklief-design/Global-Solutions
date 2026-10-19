begin;
-- The API is the only application data gateway. No browser-facing grants/policies.
-- Backend service credentials bypass RLS and MUST remain on the server.
do $$
declare schema_name text; item record;
begin
 foreach schema_name in array array['gts_shared','gts_identity','gts_enquiries','gts_conversations','gts_recruitment','gts_documents','gts_notifications']
 loop
  execute format('revoke all on schema %I from public, anon, authenticated', schema_name);
  execute format('grant usage on schema %I to service_role', schema_name);
  execute format('revoke all on all tables in schema %I from public, anon, authenticated', schema_name);
  execute format('grant select, insert, update, delete on all tables in schema %I to service_role', schema_name);
  execute format('revoke all on all functions in schema %I from public, anon, authenticated', schema_name);
  execute format('grant execute on all functions in schema %I to service_role', schema_name);
  execute format('alter default privileges in schema %I revoke all on tables from public, anon, authenticated', schema_name);
  execute format('alter default privileges in schema %I revoke execute on functions from public', schema_name);
  -- Every future migration must also enable RLS and explicitly grant server access.
  for item in select tablename from pg_tables where schemaname = schema_name
  loop
   execute format('alter table %I.%I enable row level security', schema_name,item.tablename);
   if exists (select 1 from information_schema.columns where table_schema=schema_name and table_name=item.tablename and column_name='updated_at') then
    execute format('create trigger touch_updated_at before update on %I.%I for each row execute function gts_shared.touch_updated_at()',schema_name,item.tablename);
   end if;
  end loop;
 end loop;
end $$;
commit;
