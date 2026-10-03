-- database: warranty
-- 005_operator_events.sql — operator-only security events (data-model.md audit.security_events,
-- research R30).
--
-- Row-level security rejects `tenant_id = NULL` inserts from `warranty_app`, and such rows are never
-- visible to it. Operator-only kinds (an unknown claimant channel, a cross-tenant lookup) are
-- recorded through this SECURITY DEFINER function instead: it accepts only those kinds, always
-- stores `tenant_id = NULL`, and cannot read any row. `warranty_app` has EXECUTE only.

CREATE OR REPLACE FUNCTION audit.record_operator_event(
    kind text, actor text, target text, details jsonb, source_ip text DEFAULT NULL)
RETURNS void
LANGUAGE plpgsql
VOLATILE
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS
$$
#variable_conflict use_variable
BEGIN
    IF kind IS NULL OR kind NOT IN ('UNKNOWN_CHANNEL', 'CROSS_TENANT_ACCESS_DENIED') THEN
        RAISE EXCEPTION 'not an operator-only security event kind: %', kind USING ERRCODE = 'invalid_parameter_value';
    END IF;

    IF actor IS NULL OR btrim(actor) = '' THEN
        RAISE EXCEPTION 'a security event needs an actor' USING ERRCODE = 'invalid_parameter_value';
    END IF;

    INSERT INTO audit.security_events (id, tenant_id, occurred_at, kind, actor, target, source_ip, details)
    VALUES (gen_random_uuid(), NULL, now(), kind, left(actor, 200), left(target, 300), left(source_ip, 45), details);
END
$$;

REVOKE ALL ON FUNCTION audit.record_operator_event(text, text, text, jsonb, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION audit.record_operator_event(text, text, text, jsonb, text) TO warranty_app;
