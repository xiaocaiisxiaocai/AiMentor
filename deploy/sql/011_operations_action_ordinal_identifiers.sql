SET XACT_ABORT ON;
SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
BEGIN TRANSACTION;

DECLARE @lockResult int;
EXEC @lockResult = sys.sp_getapplock
    @Resource = N'AiMentor.Workflow.Schema',
    @LockMode = 'Exclusive',
    @LockOwner = 'Transaction',
    @LockTimeout = 30000;
IF @lockResult < 0 THROW 51000, N'无法获取工作流建表锁。', 1;

IF OBJECT_ID(N'dbo.AiMentorOperationsActions', N'U') IS NULL
    THROW 51010, N'运营动作表不存在，请先执行 010_operations_actions.sql。', 1;

IF EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.AiMentorOperationsActions')
      AND name IN (N'Id', N'TenantId', N'TargetType', N'TargetId', N'Action', N'TargetETag',
                   N'RequestFingerprint', N'IdempotencyHash', N'RequesterSubjectId', N'RequestReasonHash',
                   N'ReviewerSubjectId', N'ReviewReasonHash', N'OutcomeCode')
      AND collation_name <> N'Latin1_General_100_BIN2')
BEGIN
    -- 升级期间锁定全表，避免检查后又写入新的租户别名。
    DECLARE @rowCount bigint;
    SELECT @rowCount = COUNT_BIG(1)
    FROM dbo.AiMentorOperationsActions WITH (TABLOCKX, HOLDLOCK);

    -- 旧排序规则若把多个原始租户标识视为同一值，无法安全猜测归属；失败关闭且不修改任何数据。
    IF EXISTS (
        SELECT 1
        FROM dbo.AiMentorOperationsActions
        GROUP BY TenantId
        HAVING COUNT(DISTINCT TenantId COLLATE Latin1_General_100_BIN2) > 1)
        THROW 51011, N'检测到仅大小写或排序差异的多个租户标识；请先核实归属并清理别名。', 1;

    IF EXISTS (
        SELECT Id COLLATE Latin1_General_100_BIN2
        FROM dbo.AiMentorOperationsActions
        GROUP BY Id COLLATE Latin1_General_100_BIN2
        HAVING COUNT_BIG(1) > 1)
        THROW 51012, N'检测到完全相同的运营动作标识，无法重建主键。', 1;

    IF EXISTS (
        SELECT TenantId COLLATE Latin1_General_100_BIN2,
               IdempotencyHash COLLATE Latin1_General_100_BIN2
        FROM dbo.AiMentorOperationsActions
        GROUP BY TenantId COLLATE Latin1_General_100_BIN2,
                 IdempotencyHash COLLATE Latin1_General_100_BIN2
        HAVING COUNT_BIG(1) > 1)
        THROW 51013, N'检测到完全相同的租户幂等键，无法重建唯一索引。', 1;

    DROP INDEX IF EXISTS UX_AiMentorOperationsActions_Idempotency
        ON dbo.AiMentorOperationsActions;
    DROP INDEX IF EXISTS IX_AiMentorOperationsActions_Target
        ON dbo.AiMentorOperationsActions;
    DROP INDEX IF EXISTS IX_AiMentorOperationsActions_Queue
        ON dbo.AiMentorOperationsActions;
    IF EXISTS (
        SELECT 1 FROM sys.key_constraints
        WHERE parent_object_id = OBJECT_ID(N'dbo.AiMentorOperationsActions')
          AND name = N'PK_AiMentorOperationsActions')
        ALTER TABLE dbo.AiMentorOperationsActions
            DROP CONSTRAINT PK_AiMentorOperationsActions;

    ALTER TABLE dbo.AiMentorOperationsActions ALTER COLUMN Id nvarchar(64)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorOperationsActions ALTER COLUMN TenantId nvarchar(128)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorOperationsActions ALTER COLUMN TargetType nvarchar(32)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorOperationsActions ALTER COLUMN TargetId nvarchar(128)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorOperationsActions ALTER COLUMN Action nvarchar(32)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorOperationsActions ALTER COLUMN TargetETag nvarchar(80)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorOperationsActions ALTER COLUMN RequestFingerprint char(64)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorOperationsActions ALTER COLUMN IdempotencyHash char(64)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorOperationsActions ALTER COLUMN RequesterSubjectId nvarchar(256)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorOperationsActions ALTER COLUMN RequestReasonHash char(64)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorOperationsActions ALTER COLUMN ReviewerSubjectId nvarchar(256)
        COLLATE Latin1_General_100_BIN2 NULL;
    ALTER TABLE dbo.AiMentorOperationsActions ALTER COLUMN ReviewReasonHash char(64)
        COLLATE Latin1_General_100_BIN2 NULL;
    ALTER TABLE dbo.AiMentorOperationsActions ALTER COLUMN OutcomeCode nvarchar(128)
        COLLATE Latin1_General_100_BIN2 NULL;

    ALTER TABLE dbo.AiMentorOperationsActions
        ADD CONSTRAINT PK_AiMentorOperationsActions PRIMARY KEY (Id);
    CREATE UNIQUE INDEX UX_AiMentorOperationsActions_Idempotency
        ON dbo.AiMentorOperationsActions(TenantId, IdempotencyHash);
    CREATE INDEX IX_AiMentorOperationsActions_Target
        ON dbo.AiMentorOperationsActions(TenantId, TargetType, TargetId, Status);
    CREATE INDEX IX_AiMentorOperationsActions_Queue
        ON dbo.AiMentorOperationsActions(TenantId, Status, ExpiresAt);
END;

COMMIT TRANSACTION;
