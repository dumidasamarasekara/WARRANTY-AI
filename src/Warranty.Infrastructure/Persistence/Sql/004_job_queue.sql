-- database: warranty
-- 004_job_queue.sql — cross-tenant job pickup for the claim worker (research R12).
--
-- Before it claims a job the worker has no tenant context, and row-level security hides every
-- `claims.claim_jobs` row from `warranty_app`. `claims.dequeue_claim_job` is the documented
-- exception: a SECURITY DEFINER function, owned by the schema owner (which is not subject to the
-- tenant policy), that locks one available job with FOR UPDATE SKIP LOCKED, leases it, and returns
-- only the five job header columns. `warranty_app` may EXECUTE it and gains no other access;
-- completing or failing the job then happens under the job's tenant context and RLS.
--
-- A job is available when it is `Queued` and due, or when it is `Running` with an expired lease and
-- attempts left (its worker died). Each lease increments `attempts`; ClaimJob.MaxAttempts is 3.
-- `worker_id` identifies the caller for logging only; the table has no column for it.

CREATE OR REPLACE FUNCTION claims.dequeue_claim_job(worker_id text, lock_seconds integer)
RETURNS TABLE (job_id uuid, tenant_id uuid, claim_id uuid, round integer, correlation_id text)
LANGUAGE sql
VOLATILE
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS
$$
    WITH next AS (
        SELECT j.id
        FROM claims.claim_jobs j
        WHERE (j.status = 'Queued' AND j.available_at <= now())
           OR (j.status = 'Running' AND j.locked_until < now() AND j.attempts < 3)
        ORDER BY j.available_at, j.id
        LIMIT 1
        FOR UPDATE SKIP LOCKED
    )
    UPDATE claims.claim_jobs j
    SET status = 'Running',
        locked_until = now() + make_interval(secs => greatest(1, least(lock_seconds, 3600))),
        attempts = j.attempts + 1
    FROM next
    WHERE j.id = next.id
    RETURNING j.id, j.tenant_id, j.claim_id, j.round, j.correlation_id;
$$;

REVOKE ALL ON FUNCTION claims.dequeue_claim_job(text, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION claims.dequeue_claim_job(text, integer) TO warranty_app;
