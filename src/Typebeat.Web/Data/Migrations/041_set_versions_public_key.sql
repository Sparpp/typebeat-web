-- typebeat-web migration 041: the public (Cloudflare R2) copy of a version's package (backlog 364).
--
-- set_versions.package_key names the assembled package on the box, which stays the download's
-- source for the audio-only variant and for the direct-origin hosts. public_key names the COPY of
-- the same bytes in the public bucket, under a content-unique key (packages/{setId}/{v}-{sha16}.typb)
-- so that a rolled-back ingest never reaches it, a same-version overwrite cannot leave a stale edge
-- copy, and a hidden set's package cannot be fetched by guessing. NULL means "no public copy", and
-- the download route then streams locally exactly as before; the ingest, the prune and the
-- startup backfill are the only writers.

ALTER TABLE set_versions ADD COLUMN IF NOT EXISTS public_key text NULL;
