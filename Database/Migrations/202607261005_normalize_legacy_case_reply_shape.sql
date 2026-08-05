-- CBS Support database migration
-- Version: 202607261005_normalize_legacy_case_reply_shape
-- Purpose: normalize historical ticket/inquiry replies that used the legacy
--          type/category sentinel 100 to their canonical root classification.
-- migration-transaction: true
-- Preconditions: archive 202607261000_verify_case_conversation_readiness.sql
-- output and review every reported mismatch. This migration repairs only
-- sentinel-shaped replies; ambiguous or cross-tenant data fails closed.
-- Rollback/forward-fix: classification is made consistent with the authoritative
-- canonical root. Correct unexpected results with an ordered forward migration.

LOCK TABLE digital.instructions IN SHARE ROW EXCLUSIVE MODE;

DO $guard$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM digital.instructions message
        LEFT JOIN digital.instructions root ON root.id = message.instruction_id
        WHERE message.inst_type_id IN (110,111,112,113,114,115,116,117,121,122)
          AND (
                message.instruction_id IS NULL
                OR root.id IS NULL
                OR root.instruction_id IS DISTINCT FROM root.id
          )
    ) THEN
        RAISE EXCEPTION
            'Legacy case reply normalization found an orphan or noncanonical root reference';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.instructions root
        WHERE root.instruction_id = root.id
          AND (
                (root.inst_type_id BETWEEN 110 AND 117
                    AND (root.inst_category_id IS DISTINCT FROM 101
                        OR root.client_id IS NULL))
                OR (root.inst_type_id IN (121,122)
                    AND (root.inst_category_id IS DISTINCT FROM 102
                        OR root.client_id IS NULL))
          )
    ) THEN
        RAISE EXCEPTION
            'Legacy case reply normalization found an invalid canonical root';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.instructions message
        JOIN digital.instructions root ON root.id = message.instruction_id
        WHERE message.id <> root.id
          AND root.instruction_id = root.id
          AND (
                (root.inst_type_id BETWEEN 110 AND 117
                    AND root.inst_category_id = 101)
                OR (root.inst_type_id IN (121,122)
                    AND root.inst_category_id = 102)
          )
          AND message.client_id IS DISTINCT FROM root.client_id
    ) THEN
        RAISE EXCEPTION
            'Legacy case reply normalization found a cross-tenant child';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.instructions message
        JOIN digital.instructions root ON root.id = message.instruction_id
        WHERE message.id <> root.id
          AND root.instruction_id = root.id
          AND (
                (root.inst_type_id BETWEEN 110 AND 117
                    AND root.inst_category_id = 101)
                OR (root.inst_type_id IN (121,122)
                    AND root.inst_category_id = 102)
          )
          AND message.client_id IS NOT DISTINCT FROM root.client_id
          AND (
                message.inst_type_id IS DISTINCT FROM root.inst_type_id
                OR message.inst_category_id IS DISTINCT FROM root.inst_category_id
          )
          AND NOT (
                message.inst_type_id IN (100, root.inst_type_id)
                AND message.inst_category_id IN (100, root.inst_category_id)
          )
    ) THEN
        RAISE EXCEPTION
            'Legacy case reply normalization found an ambiguous type/category mismatch';
    END IF;
END
$guard$;

DO $normalize$
DECLARE
    repaired_count bigint;
BEGIN
    UPDATE digital.instructions message
    SET inst_type_id = root.inst_type_id,
        inst_category_id = root.inst_category_id
    FROM digital.instructions root
    WHERE message.id <> root.id
      AND root.id = message.instruction_id
      AND root.instruction_id = root.id
      AND (
            (root.inst_type_id BETWEEN 110 AND 117
                AND root.inst_category_id = 101)
            OR (root.inst_type_id IN (121,122)
                AND root.inst_category_id = 102)
      )
      AND message.client_id IS NOT DISTINCT FROM root.client_id
      AND (
            message.inst_type_id IS DISTINCT FROM root.inst_type_id
            OR message.inst_category_id IS DISTINCT FROM root.inst_category_id
      )
      AND message.inst_type_id IN (100, root.inst_type_id)
      AND message.inst_category_id IN (100, root.inst_category_id);

    GET DIAGNOSTICS repaired_count = ROW_COUNT;
    RAISE NOTICE
        'Normalized % legacy ticket/inquiry reply classifications',
        repaired_count;
END
$normalize$;

DO $post_guard$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM digital.instructions message
        JOIN digital.instructions root ON root.id = message.instruction_id
        WHERE message.id <> root.id
          AND root.instruction_id = root.id
          AND (
                (root.inst_type_id BETWEEN 110 AND 117
                    AND root.inst_category_id = 101)
                OR (root.inst_type_id IN (121,122)
                    AND root.inst_category_id = 102)
          )
          AND (
                message.client_id IS DISTINCT FROM root.client_id
                OR message.inst_type_id IS DISTINCT FROM root.inst_type_id
                OR message.inst_category_id IS DISTINCT FROM root.inst_category_id
          )
    ) THEN
        RAISE EXCEPTION
            'Legacy case reply normalization did not produce a consistent case history';
    END IF;
END
$post_guard$;
