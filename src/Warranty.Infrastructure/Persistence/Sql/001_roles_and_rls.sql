-- database: warranty
-- 001_roles_and_rls.sql — application role, grants and row-level security for database `warranty`.
--
-- Applied by the migration service as the database owner after the EF Core migrations, on every
-- start; every statement is idempotent. The API and worker connect as `warranty_app`, a LOGIN role
-- that owns nothing and cannot bypass RLS (research R8). Its password is set by the migration
-- service from configuration (`ALTER ROLE warranty_app PASSWORD ...`), never stored here.
--
-- Tenant isolation: every table with a `tenant_id` column, except the platform table
-- `tenancy.tenant_channels` (read to resolve the tenant), gets ENABLE + FORCE ROW LEVEL SECURITY and
-- the policy `tenant_isolation`. The tables are discovered rather than listed, so a table added by a
-- later migration is isolated as soon as this script runs again. The policy compares with
-- `app.tenant_id`, which TenantSessionInterceptor sets on every connection; when it is unset or
-- cleared the comparison is NULL and no row is visible or writable (fail closed). Rows with
-- `tenant_id = NULL` (operator-only security events) are never visible to, or insertable by,
-- `warranty_app`.
--
-- Audit: `audit.*` tables are INSERT + SELECT only for `warranty_app`, and a trigger rejects UPDATE
-- and DELETE from every role, the owner included (FR-041a).

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'warranty_app') THEN
        CREATE ROLE warranty_app LOGIN;
    END IF;
END
$$;

ALTER ROLE warranty_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION;

-- Schemas: usage only, never CREATE.
GRANT USAGE ON SCHEMA tenancy, catalog, crm, policy, claims, adjudication, review, audit, aiops, integration
    TO warranty_app;

-- Platform and tenant configuration are seeded by the migration service and only read by the app.
GRANT SELECT ON ALL TABLES IN SCHEMA tenancy TO warranty_app;

-- Business data: full DML, scoped to the current tenant by RLS below.
GRANT SELECT, INSERT, UPDATE, DELETE
    ON ALL TABLES IN SCHEMA catalog, crm, policy, claims, adjudication, review, aiops, integration
    TO warranty_app;

-- Audit: append and read only.
REVOKE UPDATE, DELETE, TRUNCATE ON ALL TABLES IN SCHEMA audit FROM warranty_app;
GRANT SELECT, INSERT ON ALL TABLES IN SCHEMA audit TO warranty_app;

GRANT USAGE, SELECT
    ON ALL SEQUENCES IN SCHEMA tenancy, catalog, crm, policy, claims, adjudication, review, audit, aiops, integration
    TO warranty_app;

-- Row-level security on every tenant-owned table.
DO $$
DECLARE
    tbl record;
BEGIN
    FOR tbl IN
        SELECT c.table_schema, c.table_name
        FROM information_schema.columns c
        JOIN information_schema.tables t
          ON t.table_schema = c.table_schema AND t.table_name = c.table_name
        WHERE c.column_name = 'tenant_id'
          AND t.table_type = 'BASE TABLE'
          AND c.table_schema IN ('tenancy', 'catalog', 'crm', 'policy', 'claims', 'adjudication',
                                 'review', 'audit', 'aiops', 'integration')
          AND (c.table_schema, c.table_name) <> ('tenancy', 'tenant_channels')
    LOOP
        EXECUTE format('ALTER TABLE %I.%I ENABLE ROW LEVEL SECURITY', tbl.table_schema, tbl.table_name);
        EXECUTE format('ALTER TABLE %I.%I FORCE ROW LEVEL SECURITY', tbl.table_schema, tbl.table_name);
        EXECUTE format('DROP POLICY IF EXISTS tenant_isolation ON %I.%I', tbl.table_schema, tbl.table_name);
        EXECUTE format(
            'CREATE POLICY tenant_isolation ON %I.%I '
            'USING (tenant_id = nullif(current_setting(''app.tenant_id'', true), '''')::uuid) '
            'WITH CHECK (tenant_id = nullif(current_setting(''app.tenant_id'', true), '''')::uuid)',
            tbl.table_schema, tbl.table_name);
    END LOOP;
END
$$;

-- Append-only audit tables.
CREATE OR REPLACE FUNCTION audit.reject_modification() RETURNS trigger
LANGUAGE plpgsql AS
$$
BEGIN
    RAISE EXCEPTION 'audit.% is append-only: % is not allowed', TG_TABLE_NAME, TG_OP
        USING ERRCODE = 'insufficient_privilege';
END
$$;

DO $$
DECLARE
    tbl record;
BEGIN
    FOR tbl IN
        SELECT table_name FROM information_schema.tables
        WHERE table_schema = 'audit' AND table_type = 'BASE TABLE'
    LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS append_only ON audit.%I', tbl.table_name);
        EXECUTE format(
            'CREATE TRIGGER append_only BEFORE UPDATE OR DELETE ON audit.%I '
            'FOR EACH ROW EXECUTE FUNCTION audit.reject_modification()',
            tbl.table_name);
    END LOOP;
END
$$;
