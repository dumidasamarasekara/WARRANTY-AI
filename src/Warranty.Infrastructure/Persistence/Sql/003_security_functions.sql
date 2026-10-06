-- database: warranty
-- 003_security_functions.sql — cross-tenant denial logging (data-model.md audit.security_events,
-- research R8, R30).
--
-- A staff lookup of a claim or evidence ID that is not visible in the actor's tenant is denied with
-- the same 404 and the same tenant-visible ACCESS_DENIED whether the ID is unknown or belongs to
-- another tenant. Telling the two apart needs a look past row-level security, which is the
-- documented exception these SECURITY DEFINER functions provide (owned by the schema owner, which
-- is not subject to the tenant policy). Neither returns any business data:
--
-- * `audit.exists_in_other_tenant(kind, id, tenant)` answers only whether the claim or evidence ID
--   exists in a tenant other than `tenant`.
-- * `audit.record_cross_tenant_denial(kind, id, tenant, actor, target, source_ip)` writes the
--   operator-only CROSS_TENANT_ACCESS_DENIED through `audit.record_operator_event` (005) when it
--   does, with both tenants in `details`, and returns whether it wrote one. The owning tenant never
--   leaves the database, so the application cannot learn it.
--
-- `kind` is 'claim' or 'evidence'. `warranty_app` has EXECUTE on both and gains no other access.
-- (`audit.record_operator_event` is created by 005; plpgsql resolves it when this function runs.)

CREATE OR REPLACE FUNCTION audit.exists_in_other_tenant(kind text, id uuid, tenant uuid)
RETURNS boolean
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS
$$
#variable_conflict use_variable
BEGIN
    IF kind = 'claim' THEN
        RETURN EXISTS (SELECT 1 FROM claims.claims c WHERE c.id = id AND c.tenant_id IS DISTINCT FROM tenant);
    ELSIF kind = 'evidence' THEN
        RETURN EXISTS (SELECT 1 FROM claims.claim_evidence e WHERE e.id = id AND e.tenant_id IS DISTINCT FROM tenant);
    END IF;

    RAISE EXCEPTION 'not a tenant-scoped ID kind: %', kind USING ERRCODE = 'invalid_parameter_value';
END
$$;

REVOKE ALL ON FUNCTION audit.exists_in_other_tenant(text, uuid, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION audit.exists_in_other_tenant(text, uuid, uuid) TO warranty_app;

CREATE OR REPLACE FUNCTION audit.record_cross_tenant_denial(
    kind text, id uuid, tenant uuid, actor text, target text, source_ip text DEFAULT NULL)
RETURNS boolean
LANGUAGE plpgsql
VOLATILE
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS
$$
#variable_conflict use_variable
DECLARE
    owner_tenant uuid;
BEGIN
    IF tenant IS NULL THEN
        RAISE EXCEPTION 'a cross-tenant denial needs the actor''s tenant' USING ERRCODE = 'invalid_parameter_value';
    END IF;

    IF NOT audit.exists_in_other_tenant(kind, id, tenant) THEN
        RETURN false;
    END IF;

    IF kind = 'claim' THEN
        SELECT c.tenant_id INTO owner_tenant FROM claims.claims c WHERE c.id = id;
    ELSE
        SELECT e.tenant_id INTO owner_tenant FROM claims.claim_evidence e WHERE e.id = id;
    END IF;

    PERFORM audit.record_operator_event(
        'CROSS_TENANT_ACCESS_DENIED',
        actor,
        target,
        jsonb_build_object('kind', kind, 'id', id, 'actorTenantId', tenant, 'ownerTenantId', owner_tenant),
        source_ip);
    RETURN true;
END
$$;

REVOKE ALL ON FUNCTION audit.record_cross_tenant_denial(text, uuid, uuid, text, text, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION audit.record_cross_tenant_denial(text, uuid, uuid, text, text, text) TO warranty_app;
