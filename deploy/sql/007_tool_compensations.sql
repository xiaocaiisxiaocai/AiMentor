-- sqlcmd 容器连接的 QUOTED_IDENTIFIER 默认值可能关闭；筛选唯一索引要求显式开启。
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.AiMentorToolCompensations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiMentorToolCompensations (
        Id nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL
            CONSTRAINT PK_AiMentorToolCompensations PRIMARY KEY,
        ForwardExecutionKey char(64) NOT NULL,
        TenantId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
        RequesterSubjectId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
        ForwardToolName nvarchar(128) NOT NULL,
        CompensationToolName nvarchar(128) NOT NULL,
        Status tinyint NOT NULL,
        PreparationTokenHash char(64) NOT NULL,
        KeyVersion nvarchar(64) NOT NULL,
        SnapshotCipher nvarchar(max) NOT NULL,
        CreatedAt datetimeoffset(7) NOT NULL,
        ExpiresAt datetimeoffset(7) NOT NULL,
        ApprovalId nvarchar(64) COLLATE Latin1_General_100_BIN2 NULL,
        Justification nvarchar(500) NULL,
        ApprovalExpiresAt datetimeoffset(7) NULL,
        ApproverSubjectId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
        DecisionReasonHash char(64) NULL,
        CompensationExecutionKey char(64) NULL,
        ExecutionLeaseToken nvarchar(64) NULL,
        ExecutionLeaseExpiresAt datetimeoffset(7) NULL,
        CompletedAt datetimeoffset(7) NULL,
        UpdatedAt datetimeoffset(7) NOT NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT UQ_AiMentorToolCompensations_ForwardExecution UNIQUE (ForwardExecutionKey)
    );
    CREATE INDEX IX_AiMentorToolCompensations_TenantStatusCreated
        ON dbo.AiMentorToolCompensations(TenantId, Status, CreatedAt DESC);
    CREATE UNIQUE INDEX UX_AiMentorToolCompensations_Approval
        ON dbo.AiMentorToolCompensations(ApprovalId) WHERE ApprovalId IS NOT NULL;
END;

COMMIT TRANSACTION;
