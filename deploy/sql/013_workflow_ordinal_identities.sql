-- 将升级前继承数据库默认 CI 排序规则的租户、主体和资源标识迁移为 .NET Ordinal 等价语义。
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
BEGIN TRANSACTION;

DECLARE @lockResult int;
EXEC @lockResult = sys.sp_getapplock
    @Resource = N'AiMentor.Workflow.Schema',
    @LockMode = 'Exclusive',
    @LockOwner = 'Transaction',
    @LockTimeout = 30000;
IF @lockResult < 0 THROW 51000, N'无法获取工作流架构锁。', 1;

IF OBJECT_ID(N'dbo.AiMentorToolApprovals', N'U') IS NULL
    THROW 51020, N'工具审批表不存在，请先执行 001_workflow.sql。', 1;
IF OBJECT_ID(N'dbo.AiMentorToolCompensations', N'U') IS NULL
    THROW 51021, N'工具补偿表不存在，请先执行 007_tool_compensations.sql。', 1;
IF OBJECT_ID(N'dbo.AiMentorToolCompensationReconciliations', N'U') IS NULL
    THROW 51022, N'工具补偿复核表不存在，请先执行 008_tool_compensation_reconciliation.sql。', 1;
IF OBJECT_ID(N'dbo.AiMentorAtlasIncidentRuns', N'U') IS NULL
    THROW 51023, N'Atlas 运行表不存在，请先执行 009_atlas_incident_runs.sql。', 1;
IF OBJECT_ID(N'dbo.AiMentorAgentRuns', N'U') IS NULL
    THROW 51031, N'Agent 运行表不存在，请先执行 001_workflow.sql。', 1;
IF OBJECT_ID(N'dbo.AiMentorToolExecutions', N'U') IS NULL
    THROW 51032, N'工具执行表不存在，请先执行 003 和 004 工具执行迁移。', 1;
IF OBJECT_ID(N'dbo.AiMentorToolReconciliations', N'U') IS NULL
    THROW 51033, N'工具执行复核表不存在，请先执行 005_tool_reconciliation_reviews.sql。', 1;

-- 旧 CI 语义下出现的大小写别名无法自动判断归属；升级必须失败关闭并由运维先核实数据。
IF EXISTS (
    SELECT 1 FROM dbo.AiMentorAgentRuns WITH (TABLOCKX, HOLDLOCK)
    GROUP BY TenantId
    HAVING COUNT(DISTINCT TenantId COLLATE Latin1_General_100_BIN2) > 1)
    THROW 51034, N'Agent 运行表存在仅大小写不同的租户别名。', 1;
IF EXISTS (
    SELECT 1 FROM dbo.AiMentorAgentRuns
    GROUP BY TenantId, SubjectId
    HAVING COUNT(DISTINCT SubjectId COLLATE Latin1_General_100_BIN2) > 1)
    THROW 51035, N'Agent 运行表存在仅大小写不同的主体别名。', 1;

IF EXISTS (
    SELECT 1 FROM dbo.AiMentorToolExecutions WITH (TABLOCKX, HOLDLOCK)
    WHERE TenantId IS NOT NULL
    GROUP BY TenantId
    HAVING COUNT(DISTINCT TenantId COLLATE Latin1_General_100_BIN2) > 1)
    THROW 51036, N'工具执行表存在仅大小写不同的租户别名。', 1;
IF EXISTS (
    SELECT 1 FROM dbo.AiMentorToolExecutions
    WHERE TenantId IS NOT NULL AND SubjectId IS NOT NULL
    GROUP BY TenantId, SubjectId
    HAVING COUNT(DISTINCT SubjectId COLLATE Latin1_General_100_BIN2) > 1)
    THROW 51037, N'工具执行表存在仅大小写不同的主体别名。', 1;

IF EXISTS (
    SELECT 1 FROM dbo.AiMentorToolReconciliations WITH (TABLOCKX, HOLDLOCK)
    GROUP BY TenantId
    HAVING COUNT(DISTINCT TenantId COLLATE Latin1_General_100_BIN2) > 1)
    THROW 51038, N'工具执行复核表存在仅大小写不同的租户别名。', 1;

IF EXISTS (
    SELECT 1 FROM dbo.AiMentorToolApprovals WITH (TABLOCKX, HOLDLOCK)
    GROUP BY TenantId
    HAVING COUNT(DISTINCT TenantId COLLATE Latin1_General_100_BIN2) > 1)
    THROW 51024, N'工具审批表存在仅大小写不同的租户别名。', 1;
IF EXISTS (
    SELECT 1 FROM dbo.AiMentorToolApprovals
    GROUP BY TenantId, RequesterSubjectId
    HAVING COUNT(DISTINCT RequesterSubjectId COLLATE Latin1_General_100_BIN2) > 1)
    THROW 51025, N'工具审批表存在仅大小写不同的申请人别名。', 1;

IF EXISTS (
    SELECT 1 FROM dbo.AiMentorToolCompensations WITH (TABLOCKX, HOLDLOCK)
    GROUP BY TenantId
    HAVING COUNT(DISTINCT TenantId COLLATE Latin1_General_100_BIN2) > 1)
    THROW 51026, N'工具补偿表存在仅大小写不同的租户别名。', 1;
IF EXISTS (
    SELECT 1 FROM dbo.AiMentorToolCompensations
    GROUP BY TenantId, RequesterSubjectId
    HAVING COUNT(DISTINCT RequesterSubjectId COLLATE Latin1_General_100_BIN2) > 1)
    THROW 51027, N'工具补偿表存在仅大小写不同的申请人别名。', 1;

IF EXISTS (
    SELECT 1 FROM dbo.AiMentorToolCompensationReconciliations WITH (TABLOCKX, HOLDLOCK)
    GROUP BY TenantId
    HAVING COUNT(DISTINCT TenantId COLLATE Latin1_General_100_BIN2) > 1)
    THROW 51028, N'工具补偿复核表存在仅大小写不同的租户别名。', 1;

IF EXISTS (
    SELECT 1 FROM dbo.AiMentorAtlasIncidentRuns WITH (TABLOCKX, HOLDLOCK)
    GROUP BY TenantId
    HAVING COUNT(DISTINCT TenantId COLLATE Latin1_General_100_BIN2) > 1)
    THROW 51029, N'Atlas 运行表存在仅大小写不同的租户别名。', 1;
IF EXISTS (
    SELECT 1 FROM dbo.AiMentorAtlasIncidentRuns
    GROUP BY TenantId, SubjectId
    HAVING COUNT(DISTINCT SubjectId COLLATE Latin1_General_100_BIN2) > 1)
    THROW 51030, N'Atlas 运行表存在仅大小写不同的主体别名。', 1;

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.AiMentorAgentRuns')
      AND name IN (N'RunId', N'TenantId', N'SubjectId', N'ApprovalId', N'LeaseOwner')
      AND collation_name <> N'Latin1_General_100_BIN2')
BEGIN
    DROP INDEX IF EXISTS IX_AiMentorAgentRuns_OwnerStatus ON dbo.AiMentorAgentRuns;
    DROP INDEX IF EXISTS IX_AiMentorAgentRuns_Cancellation ON dbo.AiMentorAgentRuns;
    ALTER TABLE dbo.AiMentorAgentRuns DROP CONSTRAINT PK_AiMentorAgentRuns;
    ALTER TABLE dbo.AiMentorAgentRuns ALTER COLUMN RunId nvarchar(128)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorAgentRuns ALTER COLUMN TenantId nvarchar(128)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorAgentRuns ALTER COLUMN SubjectId nvarchar(256)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorAgentRuns ALTER COLUMN ApprovalId nvarchar(128)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorAgentRuns ALTER COLUMN LeaseOwner nvarchar(256)
        COLLATE Latin1_General_100_BIN2 NULL;
    ALTER TABLE dbo.AiMentorAgentRuns
        ADD CONSTRAINT PK_AiMentorAgentRuns PRIMARY KEY (RunId);
    CREATE INDEX IX_AiMentorAgentRuns_OwnerStatus
        ON dbo.AiMentorAgentRuns(TenantId, SubjectId, Status);
    CREATE INDEX IX_AiMentorAgentRuns_Cancellation
        ON dbo.AiMentorAgentRuns(Status, CancelRequestedAt)
        INCLUDE (TenantId, SubjectId);
END;

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.AiMentorToolExecutions')
      AND name IN (N'ExecutionKey', N'RunId', N'TenantId', N'SubjectId')
      AND collation_name <> N'Latin1_General_100_BIN2')
OR EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.AiMentorToolReconciliations')
      AND name IN (N'ExecutionKey', N'TenantId', N'FirstReviewerSubjectId', N'SecondReviewerSubjectId')
      AND collation_name <> N'Latin1_General_100_BIN2')
BEGIN
    IF EXISTS (
        SELECT 1 FROM sys.foreign_keys
        WHERE parent_object_id = OBJECT_ID(N'dbo.AiMentorToolReconciliations')
          AND name = N'FK_AiMentorToolReconciliations_Execution')
        ALTER TABLE dbo.AiMentorToolReconciliations
            DROP CONSTRAINT FK_AiMentorToolReconciliations_Execution;
    DROP INDEX IF EXISTS IX_AiMentorToolReconciliations_TenantStatusExpiry
        ON dbo.AiMentorToolReconciliations;
    ALTER TABLE dbo.AiMentorToolReconciliations
        DROP CONSTRAINT PK_AiMentorToolReconciliations;
    DROP INDEX IF EXISTS IX_AiMentorToolExecutions_TenantStatusUpdated
        ON dbo.AiMentorToolExecutions;
    ALTER TABLE dbo.AiMentorToolExecutions DROP CONSTRAINT PK_AiMentorToolExecutions;

    ALTER TABLE dbo.AiMentorToolExecutions ALTER COLUMN ExecutionKey char(64)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorToolExecutions ALTER COLUMN RunId nvarchar(128)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorToolExecutions ALTER COLUMN TenantId nvarchar(128)
        COLLATE Latin1_General_100_BIN2 NULL;
    ALTER TABLE dbo.AiMentorToolExecutions ALTER COLUMN SubjectId nvarchar(256)
        COLLATE Latin1_General_100_BIN2 NULL;
    ALTER TABLE dbo.AiMentorToolReconciliations ALTER COLUMN ExecutionKey char(64)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorToolReconciliations ALTER COLUMN TenantId nvarchar(128)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorToolReconciliations ALTER COLUMN FirstReviewerSubjectId nvarchar(256)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorToolReconciliations ALTER COLUMN SecondReviewerSubjectId nvarchar(256)
        COLLATE Latin1_General_100_BIN2 NULL;

    ALTER TABLE dbo.AiMentorToolExecutions
        ADD CONSTRAINT PK_AiMentorToolExecutions PRIMARY KEY (ExecutionKey);
    CREATE INDEX IX_AiMentorToolExecutions_TenantStatusUpdated
        ON dbo.AiMentorToolExecutions(TenantId, Status, UpdatedAt DESC);
    ALTER TABLE dbo.AiMentorToolReconciliations
        ADD CONSTRAINT PK_AiMentorToolReconciliations PRIMARY KEY (ExecutionKey);
    ALTER TABLE dbo.AiMentorToolReconciliations
        ADD CONSTRAINT FK_AiMentorToolReconciliations_Execution
        FOREIGN KEY (ExecutionKey) REFERENCES dbo.AiMentorToolExecutions(ExecutionKey) ON DELETE CASCADE;
    CREATE INDEX IX_AiMentorToolReconciliations_TenantStatusExpiry
        ON dbo.AiMentorToolReconciliations(TenantId, Status, EvidenceExpiresAt);
END;

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.AiMentorToolApprovals')
      AND name IN (N'Id', N'TenantId', N'RequesterSubjectId', N'ApproverSubjectId')
      AND collation_name <> N'Latin1_General_100_BIN2')
BEGIN
    DROP INDEX IF EXISTS IX_AiMentorToolApprovals_TenantStatus ON dbo.AiMentorToolApprovals;
    ALTER TABLE dbo.AiMentorToolApprovals DROP CONSTRAINT PK_AiMentorToolApprovals;
    ALTER TABLE dbo.AiMentorToolApprovals ALTER COLUMN Id nvarchar(128)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorToolApprovals ALTER COLUMN TenantId nvarchar(128)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorToolApprovals ALTER COLUMN RequesterSubjectId nvarchar(256)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorToolApprovals ALTER COLUMN ApproverSubjectId nvarchar(256)
        COLLATE Latin1_General_100_BIN2 NULL;
    ALTER TABLE dbo.AiMentorToolApprovals
        ADD CONSTRAINT PK_AiMentorToolApprovals PRIMARY KEY (Id);
    CREATE INDEX IX_AiMentorToolApprovals_TenantStatus
        ON dbo.AiMentorToolApprovals(TenantId, Status, CreatedAt DESC);
END;

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.AiMentorToolCompensations')
      AND name IN (N'Id', N'TenantId', N'RequesterSubjectId', N'ApprovalId', N'ApproverSubjectId')
      AND collation_name <> N'Latin1_General_100_BIN2')
OR EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.AiMentorToolCompensationReconciliations')
      AND name IN (N'CompensationId', N'TenantId', N'FirstReviewerSubjectId', N'SecondReviewerSubjectId')
      AND collation_name <> N'Latin1_General_100_BIN2')
BEGIN
    IF EXISTS (
        SELECT 1 FROM sys.foreign_keys
        WHERE parent_object_id = OBJECT_ID(N'dbo.AiMentorToolCompensationReconciliations')
          AND name = N'FK_AiMentorToolCompensationReconciliations_Compensation')
        ALTER TABLE dbo.AiMentorToolCompensationReconciliations
            DROP CONSTRAINT FK_AiMentorToolCompensationReconciliations_Compensation;
    DROP INDEX IF EXISTS IX_AiMentorToolCompensationReconciliations_TenantStatusExpiry
        ON dbo.AiMentorToolCompensationReconciliations;
    ALTER TABLE dbo.AiMentorToolCompensationReconciliations
        DROP CONSTRAINT PK_AiMentorToolCompensationReconciliations;
    DROP INDEX IF EXISTS IX_AiMentorToolCompensations_TenantStatusCreated
        ON dbo.AiMentorToolCompensations;
    DROP INDEX IF EXISTS UX_AiMentorToolCompensations_Approval
        ON dbo.AiMentorToolCompensations;
    ALTER TABLE dbo.AiMentorToolCompensations DROP CONSTRAINT PK_AiMentorToolCompensations;

    ALTER TABLE dbo.AiMentorToolCompensations ALTER COLUMN Id nvarchar(64)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorToolCompensations ALTER COLUMN TenantId nvarchar(128)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorToolCompensations ALTER COLUMN RequesterSubjectId nvarchar(256)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorToolCompensations ALTER COLUMN ApprovalId nvarchar(64)
        COLLATE Latin1_General_100_BIN2 NULL;
    ALTER TABLE dbo.AiMentorToolCompensations ALTER COLUMN ApproverSubjectId nvarchar(256)
        COLLATE Latin1_General_100_BIN2 NULL;
    ALTER TABLE dbo.AiMentorToolCompensationReconciliations ALTER COLUMN CompensationId nvarchar(64)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorToolCompensationReconciliations ALTER COLUMN TenantId nvarchar(128)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorToolCompensationReconciliations ALTER COLUMN FirstReviewerSubjectId nvarchar(256)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorToolCompensationReconciliations ALTER COLUMN SecondReviewerSubjectId nvarchar(256)
        COLLATE Latin1_General_100_BIN2 NULL;

    ALTER TABLE dbo.AiMentorToolCompensations
        ADD CONSTRAINT PK_AiMentorToolCompensations PRIMARY KEY (Id);
    CREATE INDEX IX_AiMentorToolCompensations_TenantStatusCreated
        ON dbo.AiMentorToolCompensations(TenantId, Status, CreatedAt DESC);
    CREATE UNIQUE INDEX UX_AiMentorToolCompensations_Approval
        ON dbo.AiMentorToolCompensations(ApprovalId) WHERE ApprovalId IS NOT NULL;
    ALTER TABLE dbo.AiMentorToolCompensationReconciliations
        ADD CONSTRAINT PK_AiMentorToolCompensationReconciliations PRIMARY KEY (CompensationId);
    ALTER TABLE dbo.AiMentorToolCompensationReconciliations
        ADD CONSTRAINT FK_AiMentorToolCompensationReconciliations_Compensation
        FOREIGN KEY (CompensationId) REFERENCES dbo.AiMentorToolCompensations(Id) ON DELETE CASCADE;
    CREATE INDEX IX_AiMentorToolCompensationReconciliations_TenantStatusExpiry
        ON dbo.AiMentorToolCompensationReconciliations(TenantId, Status, EvidenceExpiresAt);
END;

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.AiMentorAtlasIncidentRuns')
      AND name IN (N'RunId', N'TenantId', N'SubjectId', N'LeaseOwner')
      AND collation_name <> N'Latin1_General_100_BIN2')
BEGIN
    DROP INDEX IF EXISTS IX_AiMentorAtlasIncidentRuns_OwnerStatus ON dbo.AiMentorAtlasIncidentRuns;
    ALTER TABLE dbo.AiMentorAtlasIncidentRuns DROP CONSTRAINT PK_AiMentorAtlasIncidentRuns;
    ALTER TABLE dbo.AiMentorAtlasIncidentRuns ALTER COLUMN RunId nvarchar(128)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorAtlasIncidentRuns ALTER COLUMN TenantId nvarchar(128)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorAtlasIncidentRuns ALTER COLUMN SubjectId nvarchar(256)
        COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.AiMentorAtlasIncidentRuns ALTER COLUMN LeaseOwner nvarchar(256)
        COLLATE Latin1_General_100_BIN2 NULL;
    ALTER TABLE dbo.AiMentorAtlasIncidentRuns
        ADD CONSTRAINT PK_AiMentorAtlasIncidentRuns PRIMARY KEY (RunId);
    CREATE INDEX IX_AiMentorAtlasIncidentRuns_OwnerStatus
        ON dbo.AiMentorAtlasIncidentRuns(TenantId, SubjectId, Status);
END;

COMMIT TRANSACTION;
