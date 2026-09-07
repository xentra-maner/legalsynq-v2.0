-- Restore the single missing QA parent case required by four SL-CORE case-note
-- crosswalks. This is intentionally bound to LS_QA_LIENS, legacy case 24289,
-- and its reviewed target UUID. It recreates the target using the original
-- sl-core-core-liens-v1 case mapping; it never changes a populated case or
-- redirects a crosswalk.
--
-- Execute the complete file in DBeaver, then dry run and apply with:
--   CALL liens_repair_sl_core_missing_case_24289_qa(-1, '0');
--   CALL liens_repair_sl_core_missing_case_24289_qa(<ChangesToApply>, '1');
--
-- Error/reference prefix: LSLMCQ-

SET NAMES utf8mb4 COLLATE utf8mb4_0900_ai_ci;

DROP PROCEDURE IF EXISTS liens_repair_sl_core_missing_case_24289_qa;

DELIMITER $$

CREATE PROCEDURE liens_repair_sl_core_missing_case_24289_qa(
    IN p_expected_changes INT,
    IN p_apply CHAR(1)
)
SQL SECURITY DEFINER
BEGIN
    DECLARE v_tenant_id CHAR(36) DEFAULT '019fb470-f161-7fbd-93a0-c808d43c43c3';
    DECLARE v_legacy_case_id VARCHAR(100) DEFAULT '24289';
    DECLARE v_target_case_id CHAR(36) DEFAULT '196dc70c-9e1a-11f1-9a38-0a971fa4811b';
    DECLARE v_apply BOOLEAN;
    DECLARE v_lock_name VARCHAR(64);
    DECLARE v_locked INT DEFAULT 0;
    DECLARE v_in_transaction BOOLEAN DEFAULT FALSE;
    DECLARE v_original_time_zone VARCHAR(64);
    DECLARE v_time_zone_changed BOOLEAN DEFAULT FALSE;
    DECLARE v_table_count INT DEFAULT 0;
    DECLARE v_column_count INT DEFAULT 0;
    DECLARE v_core_run_count INT DEFAULT 0;
    DECLARE v_provenance_count INT DEFAULT 0;
    DECLARE v_crosswalk_count INT DEFAULT 0;
    DECLARE v_source_case_count INT DEFAULT 0;
    DECLARE v_existing_target_count INT DEFAULT 0;
    DECLARE v_matching_target_count INT DEFAULT 0;
    DECLARE v_collision_count INT DEFAULT 0;
    DECLARE v_changes_to_apply INT DEFAULT 0;
    DECLARE v_rows_inserted INT DEFAULT 0;
    DECLARE v_postcondition_errors INT DEFAULT 0;
    DECLARE v_core_run_id CHAR(36);
    DECLARE v_crosswalk_run_id CHAR(36);
    DECLARE v_crosswalk_target_id CHAR(36);
    DECLARE v_crosswalk_source_hash VARCHAR(128);
    DECLARE v_org_id CHAR(36);
    DECLARE v_migration_user_id CHAR(36);
    DECLARE v_legacy_program VARCHAR(50);
    DECLARE v_source_fingerprint CHAR(64);

    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        IF v_in_transaction THEN ROLLBACK; END IF;
        DROP TEMPORARY TABLE IF EXISTS tmp_sl_core_missing_case_24289;
        IF v_time_zone_changed THEN SET @@session.time_zone = v_original_time_zone; END IF;
        IF v_locked = 1 THEN DO RELEASE_LOCK(v_lock_name); END IF;
        RESIGNAL;
    END;

    SET v_apply = p_apply = '1';
    SET v_lock_name = CONCAT('liens:slcore:', v_tenant_id);

    IF BINARY DATABASE() <> BINARY 'LS_QA_LIENS' THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = 'LSLMCQ-001 target schema must be LS_QA_LIENS';
    END IF;
    IF p_apply IS NULL OR p_apply NOT IN ('0', '1')
       OR p_expected_changes IS NULL
       OR (NOT v_apply AND p_expected_changes <> -1)
       OR (v_apply AND p_expected_changes < 0) THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = 'LSLMCQ-002 invalid expected change count or apply flag';
    END IF;

    SELECT GET_LOCK(v_lock_name, 10) INTO v_locked;
    IF COALESCE(v_locked, 0) <> 1 THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = 'LSLMCQ-003 SL-CORE import or repair is already active for this tenant';
    END IF;

    SELECT COUNT(*) INTO v_table_count
    FROM information_schema.tables
    WHERE (table_schema = DATABASE() AND table_type = 'BASE TABLE'
           AND table_name IN ('liens_Cases', 'liens_LegacyIdCrosswalks', 'liens_LegacyImportRuns'))
       OR (table_schema = 'SL-CORE' AND table_type = 'BASE TABLE'
           AND table_name IN ('SL_CASE', 'SL_MIGRATION_SOURCE_PROVENANCE'));
    IF v_table_count <> 5 THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = 'LSLMCQ-004 required source or target tables are unavailable';
    END IF;

    SELECT COUNT(*) INTO v_column_count
    FROM information_schema.columns
    WHERE (table_schema = DATABASE() AND
           ((table_name = 'liens_Cases'
             AND column_name IN (
                 'Id', 'TenantId', 'OrgId', 'CaseNumber', 'ExternalReference', 'Title',
                 'ClientFirstName', 'ClientLastName', 'ClientDob', 'ClientPhone', 'ClientEmail',
                 'ClientAddress', 'Status', 'DateOfIncident', 'OpenedAtUtc', 'ClosedAtUtc',
                 'InsuranceCarrier', 'PolicyNumber', 'ClaimNumber', 'DemandAmount',
                 'SettlementAmount', 'Description', 'Notes', 'CreatedByUserId',
                 'UpdatedByUserId', 'CreatedAtUtc', 'UpdatedAtUtc'))
            OR (table_name = 'liens_LegacyIdCrosswalks'
                AND column_name IN ('TenantId', 'SourceSystem', 'SourceTable', 'LegacyId',
                                    'TargetEntity', 'TargetId', 'SourceHash', 'ImportRunId'))
            OR (table_name = 'liens_LegacyImportRuns'
                AND column_name IN ('Id', 'TenantId', 'OrgId', 'SourceSystem', 'SourceFingerprint',
                                    'LegacyProgram', 'MappingVersion', 'Status', 'CreatedByUserId'))))
       OR (table_schema = 'SL-CORE' AND
           ((table_name = 'SL_CASE'
             AND column_name IN ('CASE_ID', 'CASE_CODE', 'CASE_FNAME', 'CASE_LNAME', 'CASE_DOB',
                                 'CASE_ADDRESS', 'CASE_CITY', 'CASE_STATE', 'CASE_ZIPCODE',
                                 'CASE_STATUS', 'CASE_DATE_OF_LOSS', 'CASE_NOTE', 'CASE_CREATED',
                                 'CASE_UPDATED', 'CASE_PROGRAM', 'CASE_IS_DELETED'))
            OR (table_name = 'SL_MIGRATION_SOURCE_PROVENANCE'
                AND column_name IN ('PROVENANCE_KEY', 'SOURCE_FINGERPRINT', 'IMPORT_SCOPE'))));
    IF v_column_count <> 63 THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = 'LSLMCQ-005 required source or target column contract is incomplete';
    END IF;

    SET v_original_time_zone = @@session.time_zone;
    SET @@session.time_zone = '+00:00';
    SET v_time_zone_changed = TRUE;

    START TRANSACTION;
    SET v_in_transaction = TRUE;

    SELECT COUNT(*) INTO v_core_run_count
    FROM liens_LegacyImportRuns run
    WHERE BINARY run.TenantId = BINARY v_tenant_id
      AND run.SourceSystem = 'SL-CORE'
      AND run.MappingVersion = 'sl-core-core-liens-v1'
      AND run.Status = 'Completed'
      AND EXISTS (
          SELECT 1
          FROM liens_LegacyIdCrosswalks x
          WHERE BINARY x.ImportRunId = BINARY run.Id
            AND BINARY x.TenantId = BINARY run.TenantId
            AND x.SourceSystem = 'SL-CORE'
            AND x.SourceTable = 'SL_CASE_NOTES'
            AND x.TargetEntity = 'CaseNote')
      AND EXISTS (
          SELECT 1
          FROM liens_LegacyIdCrosswalks x
          WHERE BINARY x.ImportRunId = BINARY run.Id
            AND BINARY x.TenantId = BINARY run.TenantId
            AND x.SourceSystem = 'SL-CORE'
            AND x.SourceTable = 'SL_CASE'
            AND x.TargetEntity = 'Case')
    FOR UPDATE;
    IF v_core_run_count <> 1 THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = 'LSLMCQ-006 exactly one completed SL-CORE note-owning import is required';
    END IF;

    SELECT run.Id, run.OrgId, LOWER(CAST(run.CreatedByUserId AS CHAR)),
           run.LegacyProgram, LOWER(run.SourceFingerprint)
      INTO v_core_run_id, v_org_id, v_migration_user_id,
           v_legacy_program, v_source_fingerprint
    FROM liens_LegacyImportRuns run
    WHERE BINARY run.TenantId = BINARY v_tenant_id
      AND run.SourceSystem = 'SL-CORE'
      AND run.MappingVersion = 'sl-core-core-liens-v1'
      AND run.Status = 'Completed'
      AND EXISTS (
          SELECT 1 FROM liens_LegacyIdCrosswalks x
          WHERE BINARY x.ImportRunId = BINARY run.Id
            AND BINARY x.TenantId = BINARY run.TenantId
            AND x.SourceSystem = 'SL-CORE'
            AND x.SourceTable = 'SL_CASE_NOTES'
            AND x.TargetEntity = 'CaseNote')
      AND EXISTS (
          SELECT 1 FROM liens_LegacyIdCrosswalks x
          WHERE BINARY x.ImportRunId = BINARY run.Id
            AND BINARY x.TenantId = BINARY run.TenantId
            AND x.SourceSystem = 'SL-CORE'
            AND x.SourceTable = 'SL_CASE'
            AND x.TargetEntity = 'Case');

    IF v_org_id IS NULL OR v_migration_user_id IS NULL
       OR v_legacy_program IS NULL OR TRIM(v_legacy_program) NOT REGEXP '^[0-9]+$' THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = 'LSLMCQ-007 completed import metadata is invalid';
    END IF;

    SELECT COUNT(*) INTO v_provenance_count
    FROM `SL-CORE`.`SL_MIGRATION_SOURCE_PROVENANCE`
    WHERE PROVENANCE_KEY = 'sl-core-current'
      AND HEX(LOWER(SOURCE_FINGERPRINT)) = HEX(v_source_fingerprint)
      AND HEX(IMPORT_SCOPE) = HEX('sl-core-core-liens-v1');
    IF v_provenance_count <> 1 THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = 'LSLMCQ-008 source provenance does not match the completed import';
    END IF;

    SELECT COUNT(*) INTO v_crosswalk_count
    FROM liens_LegacyIdCrosswalks x
    WHERE BINARY x.TenantId = BINARY v_tenant_id
      AND x.SourceSystem = 'SL-CORE'
      AND x.SourceTable = 'SL_CASE'
      AND BINARY x.LegacyId = BINARY v_legacy_case_id
      AND x.TargetEntity = 'Case';
    IF v_crosswalk_count <> 1 THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = 'LSLMCQ-009 exactly one case crosswalk is required';
    END IF;

    SELECT x.TargetId, x.ImportRunId, x.SourceHash
      INTO v_crosswalk_target_id, v_crosswalk_run_id, v_crosswalk_source_hash
    FROM liens_LegacyIdCrosswalks x
    WHERE BINARY x.TenantId = BINARY v_tenant_id
      AND x.SourceSystem = 'SL-CORE'
      AND x.SourceTable = 'SL_CASE'
      AND BINARY x.LegacyId = BINARY v_legacy_case_id
      AND x.TargetEntity = 'Case';
    IF BINARY v_crosswalk_target_id <> BINARY v_target_case_id
       OR BINARY v_crosswalk_run_id <> BINARY v_core_run_id THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = 'LSLMCQ-010 case crosswalk target or import run is not the reviewed value';
    END IF;

    SELECT COUNT(*) INTO v_source_case_count
    FROM `SL-CORE`.`SL_CASE` source_case
    WHERE CAST(source_case.CASE_ID AS CHAR) = v_legacy_case_id
      AND source_case.CASE_PROGRAM = CAST(v_legacy_program AS UNSIGNED)
      AND UPPER(TRIM(COALESCE(source_case.CASE_IS_DELETED, 'N'))) <> 'Y';
    IF v_source_case_count <> 1 THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = 'LSLMCQ-011 exactly one active legacy source case is required';
    END IF;

    DROP TEMPORARY TABLE IF EXISTS tmp_sl_core_missing_case_24289;
    CREATE TEMPORARY TABLE tmp_sl_core_missing_case_24289 AS
    SELECT
        v_target_case_id AS Id,
        v_tenant_id AS TenantId,
        v_org_id AS OrgId,
        CASE WHEN NULLIF(TRIM(source_case.CASE_CODE), '') IS NULL
             THEN CONCAT('SL-CORE-CASE-', source_case.CASE_ID)
             ELSE TRIM(source_case.CASE_CODE) END AS CaseNumber,
        CONCAT('SL-CORE:SL_CASE:', source_case.CASE_ID) AS ExternalReference,
        TRIM(source_case.CASE_FNAME) AS ClientFirstName,
        TRIM(source_case.CASE_LNAME) AS ClientLastName,
        source_case.CASE_DOB AS ClientDob,
        NULLIF(CONCAT_WS(', ',
            NULLIF(TRIM(source_case.CASE_ADDRESS), ''),
            NULLIF(TRIM(source_case.CASE_CITY), ''),
            NULLIF(TRIM(source_case.CASE_STATE), ''),
            NULLIF(TRIM(source_case.CASE_ZIPCODE), '')), '') AS ClientAddress,
        CASE COALESCE(UPPER(TRIM(source_case.CASE_STATUS)), '')
          WHEN '' THEN 'PreDemand' WHEN 'N' THEN 'PreDemand'
          WHEN 'P' THEN 'PreDemand' WHEN 'PD' THEN 'PreDemand'
          WHEN 'NEW' THEN 'PreDemand' WHEN 'PROCESSING' THEN 'PreDemand'
          WHEN 'PRE-DEMAND' THEN 'PreDemand' WHEN 'PREDEMAND' THEN 'PreDemand'
          WHEN 'DS' THEN 'DemandSent' WHEN 'DEMAND SENT' THEN 'DemandSent'
          WHEN 'NT' THEN 'InNegotiation' WHEN 'LP' THEN 'InNegotiation'
          WHEN 'LO' THEN 'InNegotiation' WHEN 'LC' THEN 'InNegotiation'
          WHEN 'NEGOTIATIONS' THEN 'InNegotiation' WHEN 'LITIGATION' THEN 'InNegotiation'
          WHEN 'CS' THEN 'CaseSettled' WHEN 'CASE SETTLED' THEN 'CaseSettled'
          WHEN 'C' THEN 'Closed' WHEN 'CLOSED' THEN 'Closed'
          ELSE NULL END AS Status,
        CASE
          WHEN NULLIF(TRIM(source_case.CASE_DATE_OF_LOSS), '') IS NULL THEN NULL
          WHEN TRIM(source_case.CASE_DATE_OF_LOSS) REGEXP '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
            THEN STR_TO_DATE(TRIM(source_case.CASE_DATE_OF_LOSS), '%Y-%m-%d')
          WHEN TRIM(source_case.CASE_DATE_OF_LOSS) REGEXP '^[0-9]{1,2}/[0-9]{1,2}/[0-9]{4}$'
            THEN STR_TO_DATE(TRIM(source_case.CASE_DATE_OF_LOSS), '%c/%e/%Y')
          ELSE NULL END AS DateOfIncident,
        source_case.CASE_DATE_OF_LOSS AS IncidentDateText,
        NULLIF(TRIM(source_case.CASE_NOTE), '') AS Notes,
        COALESCE(source_case.CASE_CREATED, UTC_TIMESTAMP(6)) AS OpenedAtUtc,
        CASE
          WHEN COALESCE(UPPER(TRIM(source_case.CASE_STATUS)), '')
               IN ('CS', 'CASE SETTLED', 'C', 'CLOSED')
            THEN source_case.CASE_UPDATED
          ELSE NULL END AS ClosedAtUtc,
        v_migration_user_id AS CreatedByUserId,
        v_migration_user_id AS UpdatedByUserId,
        COALESCE(source_case.CASE_CREATED, UTC_TIMESTAMP(6)) AS CreatedAtUtc,
        COALESCE(source_case.CASE_UPDATED, source_case.CASE_CREATED, UTC_TIMESTAMP(6)) AS UpdatedAtUtc,
        SHA2(CONCAT_WS('|', source_case.CASE_ID, source_case.CASE_CODE,
                       source_case.CASE_FNAME, source_case.CASE_LNAME,
                       source_case.CASE_DOB, source_case.CASE_ADDRESS,
                       source_case.CASE_CITY, source_case.CASE_STATE,
                       source_case.CASE_ZIPCODE, source_case.CASE_STATUS,
                       source_case.CASE_DATE_OF_LOSS, source_case.CASE_NOTE,
                       source_case.CASE_CREATED, source_case.CASE_UPDATED,
                       v_source_fingerprint), 256) AS SourceHash
    FROM `SL-CORE`.`SL_CASE` source_case
    WHERE CAST(source_case.CASE_ID AS CHAR) = v_legacy_case_id
      AND source_case.CASE_PROGRAM = CAST(v_legacy_program AS UNSIGNED)
      AND UPPER(TRIM(COALESCE(source_case.CASE_IS_DELETED, 'N'))) <> 'Y';

    ALTER TABLE tmp_sl_core_missing_case_24289
        ADD PRIMARY KEY (Id),
        ADD UNIQUE KEY UX_tmp_sl_core_missing_case_24289_case_number (CaseNumber);

    IF EXISTS (
        SELECT 1
        FROM tmp_sl_core_missing_case_24289 staged
        WHERE NULLIF(staged.ClientFirstName, '') IS NULL
           OR NULLIF(staged.ClientLastName, '') IS NULL
           OR staged.Status IS NULL
           OR CHAR_LENGTH(staged.CaseNumber) > 50
           OR CHAR_LENGTH(COALESCE(staged.ClientAddress, '')) > 500
           OR CHAR_LENGTH(COALESCE(staged.Notes, '')) > 4000
           OR (NULLIF(TRIM(staged.IncidentDateText), '') IS NOT NULL
               AND staged.DateOfIncident IS NULL)
           OR v_crosswalk_source_hash IS NULL
           OR BINARY staged.SourceHash <> BINARY v_crosswalk_source_hash
    ) THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = 'LSLMCQ-012 source case mapping or crosswalk hash is invalid';
    END IF;

    SELECT COUNT(*) INTO v_existing_target_count
    FROM liens_Cases target_case
    WHERE BINARY target_case.Id = BINARY v_target_case_id;

    SELECT COUNT(*) INTO v_matching_target_count
    FROM liens_Cases target_case
    INNER JOIN tmp_sl_core_missing_case_24289 staged
      ON BINARY staged.Id = BINARY target_case.Id
    WHERE BINARY target_case.TenantId = BINARY staged.TenantId
      AND BINARY target_case.OrgId = BINARY staged.OrgId
      AND BINARY target_case.CaseNumber = BINARY staged.CaseNumber
      AND BINARY target_case.ExternalReference = BINARY staged.ExternalReference
      AND BINARY target_case.ClientFirstName = BINARY staged.ClientFirstName
      AND BINARY target_case.ClientLastName = BINARY staged.ClientLastName
      AND target_case.ClientDob <=> staged.ClientDob
      AND target_case.ClientAddress <=> staged.ClientAddress
      AND BINARY target_case.Status = BINARY staged.Status
      AND target_case.DateOfIncident <=> staged.DateOfIncident
      AND target_case.OpenedAtUtc <=> staged.OpenedAtUtc
      AND target_case.ClosedAtUtc <=> staged.ClosedAtUtc
      AND target_case.Notes <=> staged.Notes
      AND BINARY target_case.CreatedByUserId = BINARY staged.CreatedByUserId
      AND BINARY target_case.UpdatedByUserId = BINARY staged.UpdatedByUserId
      AND target_case.CreatedAtUtc <=> staged.CreatedAtUtc
      AND target_case.UpdatedAtUtc <=> staged.UpdatedAtUtc;

    IF v_existing_target_count <> v_matching_target_count THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = 'LSLMCQ-013 reviewed target UUID exists with a conflicting case';
    END IF;

    SELECT COUNT(*) INTO v_collision_count
    FROM liens_Cases target_case
    INNER JOIN tmp_sl_core_missing_case_24289 staged
      ON BINARY target_case.TenantId = BINARY staged.TenantId
     AND (BINARY target_case.CaseNumber = BINARY staged.CaseNumber
          OR BINARY target_case.ExternalReference = BINARY staged.ExternalReference)
    WHERE BINARY target_case.Id <> BINARY staged.Id;
    IF v_collision_count <> 0 THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = 'LSLMCQ-014 an existing case already owns the source number or reference';
    END IF;

    SET v_changes_to_apply = IF(v_existing_target_count = 0, 1, 0);

    IF NOT v_apply THEN
        SELECT v_changes_to_apply AS ChangesToApply,
               v_existing_target_count AS AlreadyRestored,
               v_collision_count AS Conflicts,
               v_legacy_case_id AS LegacyCaseId,
               v_target_case_id AS TargetCaseId;
        SELECT Id, TenantId, OrgId, CaseNumber, ExternalReference,
               ClientFirstName, ClientLastName, Status, DateOfIncident,
               CreatedByUserId, CreatedAtUtc, UpdatedAtUtc
        FROM tmp_sl_core_missing_case_24289;
        ROLLBACK;
        SET v_in_transaction = FALSE;
    ELSE
        IF p_expected_changes <> v_changes_to_apply THEN
            SIGNAL SQLSTATE '45000'
                SET MESSAGE_TEXT = 'LSLMCQ-015 expected change count does not match dry run';
        END IF;

        INSERT INTO liens_Cases (
            Id, TenantId, OrgId, CaseNumber, ExternalReference, Title,
            ClientFirstName, ClientLastName, ClientDob, ClientPhone, ClientEmail,
            ClientAddress, Status, DateOfIncident, OpenedAtUtc, ClosedAtUtc,
            InsuranceCarrier, PolicyNumber, ClaimNumber, DemandAmount,
            SettlementAmount, Description, Notes, CreatedByUserId,
            UpdatedByUserId, CreatedAtUtc, UpdatedAtUtc)
        SELECT
            staged.Id, staged.TenantId, staged.OrgId, staged.CaseNumber,
            staged.ExternalReference, NULL, staged.ClientFirstName,
            staged.ClientLastName, staged.ClientDob, NULL, NULL,
            staged.ClientAddress, staged.Status, staged.DateOfIncident,
            staged.OpenedAtUtc, staged.ClosedAtUtc, NULL, NULL, NULL, NULL,
            NULL, NULL, staged.Notes, staged.CreatedByUserId,
            staged.UpdatedByUserId, staged.CreatedAtUtc, staged.UpdatedAtUtc
        FROM tmp_sl_core_missing_case_24289 staged
        WHERE v_changes_to_apply = 1
          AND NOT EXISTS (
              SELECT 1 FROM liens_Cases target_case
              WHERE BINARY target_case.Id = BINARY staged.Id)
          AND NOT EXISTS (
              SELECT 1 FROM liens_Cases target_case
              WHERE BINARY target_case.TenantId = BINARY staged.TenantId
                AND (BINARY target_case.CaseNumber = BINARY staged.CaseNumber
                     OR BINARY target_case.ExternalReference = BINARY staged.ExternalReference));
        SET v_rows_inserted = ROW_COUNT();

        SELECT COUNT(*) INTO v_postcondition_errors
        FROM tmp_sl_core_missing_case_24289 staged
        LEFT JOIN liens_Cases target_case
          ON BINARY target_case.Id = BINARY staged.Id
        WHERE target_case.Id IS NULL
           OR BINARY target_case.TenantId <> BINARY staged.TenantId
           OR BINARY target_case.OrgId <> BINARY staged.OrgId
           OR BINARY target_case.CaseNumber <> BINARY staged.CaseNumber
           OR BINARY target_case.ExternalReference <> BINARY staged.ExternalReference
           OR BINARY target_case.ClientFirstName <> BINARY staged.ClientFirstName
           OR BINARY target_case.ClientLastName <> BINARY staged.ClientLastName
           OR NOT (target_case.ClientDob <=> staged.ClientDob)
           OR NOT (target_case.ClientAddress <=> staged.ClientAddress)
           OR BINARY target_case.Status <> BINARY staged.Status
           OR NOT (target_case.DateOfIncident <=> staged.DateOfIncident)
           OR BINARY target_case.CreatedByUserId <> BINARY staged.CreatedByUserId;
        IF v_rows_inserted <> v_changes_to_apply OR v_postcondition_errors <> 0 THEN
            SIGNAL SQLSTATE '45000'
                SET MESSAGE_TEXT = 'LSLMCQ-016 insert count or postcondition failed';
        END IF;

        COMMIT;
        SET v_in_transaction = FALSE;
        SELECT v_rows_inserted AS RowsInserted,
               v_changes_to_apply AS ExpectedRowsChanged,
               v_postcondition_errors AS PostconditionErrors;
    END IF;

    DROP TEMPORARY TABLE IF EXISTS tmp_sl_core_missing_case_24289;
    SET @@session.time_zone = v_original_time_zone;
    SET v_time_zone_changed = FALSE;
    DO RELEASE_LOCK(v_lock_name);
    SET v_locked = 0;
END$$

DELIMITER ;
