-- Manual application for Liens migration:
--   20260906010000_AddSellingNotificationInboxOutbox
--
-- Adds `liens_LienOffers.SubmittedByPlatformUserId` and the
-- `liens_SellingNotificationOutbox` table + indexes. Without the column every
-- query that materializes a LienOffer (notably GET /api/liens/selling/liens/{id})
-- fails with MySQL "Unknown column 'l.SubmittedByPlatformUserId'" and returns 500
-- for all lien IDs.
--
-- Stop the Liens API and back up the database before running this script.
-- Run it against the Liens database, for example:
--   mysql --defaults-extra-file=/secure/liens.cnf liens < scripts/apply-selling-notification-inbox-outbox-migration.sql
--
-- The script is restart-safe. Each object is created only when missing, and the
-- migration is recorded only after the resulting schema contract is verified and
-- the preceding EF migration (20260904010000_AddContactPhoneExtension) is present.

SET @selling_notif_migration_id =
    '20260906010000_AddSellingNotificationInboxOutbox';
SET @selling_notif_predecessor_id =
    '20260904010000_AddContactPhoneExtension';

SET @selling_notif_predecessor_present = EXISTS (
    SELECT 1
    FROM `__EFMigrationsHistory`
    WHERE CAST(`MigrationId` AS BINARY) =
          CAST(@selling_notif_predecessor_id AS BINARY)
);

-- ---------------------------------------------------------------------------
-- 1) liens_LienOffers.SubmittedByPlatformUserId  (char(36) NULL, ascii collation)
-- ---------------------------------------------------------------------------
SET @lien_offers_table_present = EXISTS (
    SELECT 1
    FROM information_schema.TABLES
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'liens_LienOffers'
);

SET @submitted_by_column_present = EXISTS (
    SELECT 1
    FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'liens_LienOffers'
      AND COLUMN_NAME = 'SubmittedByPlatformUserId'
);

SET @add_submitted_by_sql = IF(
    @selling_notif_predecessor_present = 1
    AND @lien_offers_table_present = 1
    AND @submitted_by_column_present = 0,
    'ALTER TABLE `liens_LienOffers` ADD COLUMN `SubmittedByPlatformUserId` char(36) CHARACTER SET ascii NULL COLLATE ascii_general_ci',
    'SELECT 1'
);

PREPARE add_submitted_by_statement FROM @add_submitted_by_sql;
EXECUTE add_submitted_by_statement;
DEALLOCATE PREPARE add_submitted_by_statement;

-- ---------------------------------------------------------------------------
-- 2) liens_SellingNotificationOutbox table
-- ---------------------------------------------------------------------------
SET @outbox_table_present = EXISTS (
    SELECT 1
    FROM information_schema.TABLES
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'liens_SellingNotificationOutbox'
);

SET @create_outbox_sql = IF(
    @selling_notif_predecessor_present = 1
    AND @outbox_table_present = 0,
    'CREATE TABLE `liens_SellingNotificationOutbox` (
        `Id` char(36) CHARACTER SET ascii NOT NULL COLLATE ascii_general_ci,
        `TenantId` char(36) CHARACTER SET ascii NOT NULL COLLATE ascii_general_ci,
        `RecipientUserId` char(36) CHARACTER SET ascii NOT NULL COLLATE ascii_general_ci,
        `EventKey` varchar(128) CHARACTER SET utf8mb4 NOT NULL,
        `Category` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
        `Title` varchar(160) CHARACTER SET utf8mb4 NOT NULL,
        `Description` varchar(500) CHARACTER SET utf8mb4 NOT NULL,
        `SourceDisplayName` varchar(160) CHARACTER SET utf8mb4 NOT NULL,
        `SourceInitials` varchar(8) CHARACTER SET utf8mb4 NOT NULL,
        `OccurredAtUtc` datetime(6) NOT NULL,
        `IdempotencyKey` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
        `AttemptCount` int NOT NULL,
        `NextAttemptAtUtc` datetime(6) NOT NULL,
        `LeaseUntilUtc` datetime(6) NULL,
        `LeaseOwner` varchar(100) CHARACTER SET utf8mb4 NULL,
        `ProcessedAtUtc` datetime(6) NULL,
        `DeadLetteredAtUtc` datetime(6) NULL,
        `LastError` varchar(1000) CHARACTER SET utf8mb4 NULL,
        `CreatedAtUtc` datetime(6) NOT NULL,
        `UpdatedAtUtc` datetime(6) NOT NULL,
        `CreatedByUserId` char(36) CHARACTER SET ascii NULL COLLATE ascii_general_ci,
        `UpdatedByUserId` char(36) CHARACTER SET ascii NULL COLLATE ascii_general_ci,
        CONSTRAINT `PK_liens_SellingNotificationOutbox` PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4',
    'SELECT 1'
);

PREPARE create_outbox_statement FROM @create_outbox_sql;
EXECUTE create_outbox_statement;
DEALLOCATE PREPARE create_outbox_statement;

-- ---------------------------------------------------------------------------
-- 3) Indexes
-- ---------------------------------------------------------------------------
SET @dispatch_index_present = EXISTS (
    SELECT 1
    FROM information_schema.STATISTICS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'liens_SellingNotificationOutbox'
      AND INDEX_NAME = 'IX_SellingNotificationOutbox_Dispatch'
);

SET @create_dispatch_index_sql = IF(
    EXISTS (
        SELECT 1 FROM information_schema.TABLES
        WHERE TABLE_SCHEMA = DATABASE()
          AND TABLE_NAME = 'liens_SellingNotificationOutbox'
    )
    AND @dispatch_index_present = 0,
    'CREATE INDEX `IX_SellingNotificationOutbox_Dispatch` ON `liens_SellingNotificationOutbox` (`ProcessedAtUtc`, `DeadLetteredAtUtc`, `NextAttemptAtUtc`, `LeaseUntilUtc`)',
    'SELECT 1'
);

PREPARE create_dispatch_index_statement FROM @create_dispatch_index_sql;
EXECUTE create_dispatch_index_statement;
DEALLOCATE PREPARE create_dispatch_index_statement;

SET @idempotency_index_present = EXISTS (
    SELECT 1
    FROM information_schema.STATISTICS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'liens_SellingNotificationOutbox'
      AND INDEX_NAME = 'UX_SellingNotificationOutbox_Tenant_Idempotency'
);

SET @create_idempotency_index_sql = IF(
    EXISTS (
        SELECT 1 FROM information_schema.TABLES
        WHERE TABLE_SCHEMA = DATABASE()
          AND TABLE_NAME = 'liens_SellingNotificationOutbox'
    )
    AND @idempotency_index_present = 0,
    'CREATE UNIQUE INDEX `UX_SellingNotificationOutbox_Tenant_Idempotency` ON `liens_SellingNotificationOutbox` (`TenantId`, `IdempotencyKey`)',
    'SELECT 1'
);

PREPARE create_idempotency_index_statement FROM @create_idempotency_index_sql;
EXECUTE create_idempotency_index_statement;
DEALLOCATE PREPARE create_idempotency_index_statement;

-- ---------------------------------------------------------------------------
-- 4) Record the migration once the schema contract is satisfied
-- ---------------------------------------------------------------------------
SET @submitted_by_contract_valid = EXISTS (
    SELECT 1
    FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'liens_LienOffers'
      AND COLUMN_NAME = 'SubmittedByPlatformUserId'
      AND COLUMN_TYPE = 'char(36)'
      AND IS_NULLABLE = 'YES'
);

SET @outbox_contract_valid = (
    (SELECT COUNT(*)
     FROM information_schema.TABLES
     WHERE TABLE_SCHEMA = DATABASE()
       AND TABLE_NAME = 'liens_SellingNotificationOutbox') = 1
    AND (SELECT COUNT(*)
         FROM information_schema.COLUMNS
         WHERE TABLE_SCHEMA = DATABASE()
           AND TABLE_NAME = 'liens_SellingNotificationOutbox') = 22
    AND (SELECT COUNT(DISTINCT INDEX_NAME)
         FROM information_schema.STATISTICS
         WHERE TABLE_SCHEMA = DATABASE()
           AND TABLE_NAME = 'liens_SellingNotificationOutbox'
           AND INDEX_NAME IN (
               'IX_SellingNotificationOutbox_Dispatch',
               'UX_SellingNotificationOutbox_Tenant_Idempotency')) = 2
);

INSERT IGNORE INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
SELECT @selling_notif_migration_id, '8.0.2'
WHERE @selling_notif_predecessor_present = 1
  AND @submitted_by_contract_valid = 1
  AND @outbox_contract_valid = 1;

-- ---------------------------------------------------------------------------
-- 5) Report
-- ---------------------------------------------------------------------------
SELECT
    @selling_notif_migration_id AS `MigrationId`,
    IF(@selling_notif_predecessor_present = 1, 'RECORDED', 'NOT_RECORDED') AS `PredecessorStatus`,
    IF(@submitted_by_contract_valid = 1, 'VALID', 'INVALID') AS `LienOfferColumnStatus`,
    IF(@outbox_contract_valid = 1, 'VALID', 'INVALID') AS `OutboxTableStatus`,
    IF(EXISTS (
        SELECT 1 FROM `__EFMigrationsHistory`
        WHERE CAST(`MigrationId` AS BINARY) = CAST(@selling_notif_migration_id AS BINARY)
    ), 'RECORDED', 'NOT_RECORDED') AS `HistoryStatus`,
    IF(
        @selling_notif_predecessor_present = 1
        AND @submitted_by_contract_valid = 1
        AND @outbox_contract_valid = 1
        AND EXISTS (
            SELECT 1 FROM `__EFMigrationsHistory`
            WHERE CAST(`MigrationId` AS BINARY) = CAST(@selling_notif_migration_id AS BINARY)
        ),
        'READY', 'NOT_READY'
    ) AS `Status`;

SELECT COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, CHARACTER_SET_NAME
FROM information_schema.COLUMNS
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = 'liens_LienOffers'
  AND COLUMN_NAME = 'SubmittedByPlatformUserId';
