-- database: knowledge
-- 002_knowledge.sql — schema, partitions, functions, row-level security and grants for database
-- `knowledge` (data-model.md "Knowledge database", research R6/R7).
--
-- The whole knowledge schema is owned by this script, not by EF Core migrations: `knowledge_chunks`
-- is LIST-partitioned by namespace with one HNSW index per partition, which migrations cannot
-- express. KnowledgeDbContext only maps these tables. Applied by the migration service as the
-- database owner on every start; every statement is idempotent.
--
-- Isolation: `warranty_app` may only read, and only rows whose namespace is `global` or the
-- `app.kb_namespace` that TenantSessionInterceptor sets from the tenant context. With the setting
-- unset or cleared, only `global` is visible. Ingestion runs in the migration service on the owner
-- connection (the documented RLS exception, research R8), never as `warranty_app`.

CREATE EXTENSION IF NOT EXISTS vector;

CREATE SCHEMA IF NOT EXISTS knowledge;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'warranty_app') THEN
        CREATE ROLE warranty_app LOGIN;
    END IF;
END
$$;

CREATE TABLE IF NOT EXISTS knowledge.knowledge_documents (
    id               uuid        NOT NULL PRIMARY KEY,
    namespace        text        NOT NULL,
    tenant_id        uuid        NULL,
    document_type    text        NOT NULL,
    source_ref       text        NOT NULL,
    title            text        NOT NULL,
    version          integer     NOT NULL,
    product_category text        NULL,
    product_model    text        NULL,
    regions          text[]      NOT NULL DEFAULT '{}',
    effective_from   date        NULL,
    effective_to     date        NULL,
    classification   text        NOT NULL,
    allowed_roles    text[]      NOT NULL DEFAULT '{}',
    embedding_model  text        NOT NULL,
    embedding_dim    integer     NOT NULL,
    checksum         text        NOT NULL,
    CONSTRAINT ck_knowledge_documents_namespace
        CHECK (namespace ~ '^(global|tenant-[a-z0-9][a-z0-9-]{0,30})$'),
    CONSTRAINT ck_knowledge_documents_tenant
        CHECK ((namespace = 'global') = (tenant_id IS NULL)),
    CONSTRAINT ck_knowledge_documents_effective
        CHECK (effective_to IS NULL OR effective_from IS NULL OR effective_to >= effective_from),
    CONSTRAINT uq_knowledge_documents_source UNIQUE (namespace, source_ref)
);

CREATE TABLE IF NOT EXISTS knowledge.knowledge_chunks (
    id                uuid        NOT NULL,
    namespace         text        NOT NULL,
    tenant_id         uuid        NULL,
    document_id       uuid        NOT NULL REFERENCES knowledge.knowledge_documents (id) ON DELETE CASCADE,
    chunk_index       integer     NOT NULL,
    clause_key        text        NULL,
    section_title     text        NULL,
    text              text        NOT NULL,
    embedding         vector(768) NOT NULL,
    -- Denormalized from the document so hard filters run before similarity ranking (contracts/rag.md).
    document_type     text        NOT NULL,
    product_category  text        NULL,
    product_model     text        NULL,
    regions           text[]      NOT NULL DEFAULT '{}',
    effective_from    date        NULL,
    effective_to      date        NULL,
    classification    text        NOT NULL,
    allowed_roles     text[]      NOT NULL DEFAULT '{}',
    -- Policy clause metadata copied from policy.policy_clauses (research R26).
    policy_version_id uuid        NULL,
    clause_type       text        NULL,
    exclusion_code    text        NULL,
    CONSTRAINT pk_knowledge_chunks PRIMARY KEY (namespace, id),
    CONSTRAINT uq_knowledge_chunks_document_index UNIQUE (namespace, document_id, chunk_index),
    CONSTRAINT ck_knowledge_chunks_tenant CHECK ((namespace = 'global') = (tenant_id IS NULL))
) PARTITION BY LIST (namespace);

CREATE INDEX IF NOT EXISTS ix_knowledge_chunks_document ON knowledge.knowledge_chunks (document_id);

-- Creates the partition and HNSW (cosine) index for one namespace; safe to call repeatedly.
CREATE OR REPLACE FUNCTION knowledge.ensure_namespace(ns text) RETURNS void
LANGUAGE plpgsql AS
$$
DECLARE
    partition_name text;
BEGIN
    IF ns IS NULL OR ns !~ '^(global|tenant-[a-z0-9][a-z0-9-]{0,30})$' THEN
        RAISE EXCEPTION 'invalid knowledge namespace: %', ns USING ERRCODE = 'invalid_parameter_value';
    END IF;

    partition_name := 'knowledge_chunks_' || replace(ns, '-', '_');
    EXECUTE format(
        'CREATE TABLE IF NOT EXISTS knowledge.%I PARTITION OF knowledge.knowledge_chunks FOR VALUES IN (%L)',
        partition_name, ns);
    EXECUTE format(
        'CREATE INDEX IF NOT EXISTS %I ON knowledge.%I USING hnsw (embedding vector_cosine_ops)',
        'ix_' || partition_name || '_embedding', partition_name);
END
$$;

REVOKE ALL ON FUNCTION knowledge.ensure_namespace(text) FROM PUBLIC;

SELECT knowledge.ensure_namespace('global');

-- Row-level security: global plus the session's tenant namespace, read only.
ALTER TABLE knowledge.knowledge_documents ENABLE ROW LEVEL SECURITY;
ALTER TABLE knowledge.knowledge_documents FORCE ROW LEVEL SECURITY;
ALTER TABLE knowledge.knowledge_chunks ENABLE ROW LEVEL SECURITY;
ALTER TABLE knowledge.knowledge_chunks FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS namespace_isolation ON knowledge.knowledge_documents;
CREATE POLICY namespace_isolation ON knowledge.knowledge_documents
    FOR SELECT
    USING (namespace = 'global' OR namespace = current_setting('app.kb_namespace', true));

DROP POLICY IF EXISTS namespace_isolation ON knowledge.knowledge_chunks;
CREATE POLICY namespace_isolation ON knowledge.knowledge_chunks
    FOR SELECT
    USING (namespace = 'global' OR namespace = current_setting('app.kb_namespace', true));

GRANT USAGE ON SCHEMA knowledge TO warranty_app;
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON ALL TABLES IN SCHEMA knowledge FROM warranty_app;
GRANT SELECT ON knowledge.knowledge_documents, knowledge.knowledge_chunks TO warranty_app;
