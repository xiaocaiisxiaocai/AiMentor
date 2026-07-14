SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.AiMentorToolExecutions', N'U') IS NULL
    THROW 50005, '请先执行 003 和 004 工具执行迁移。', 1;

IF OBJECT_ID(N'dbo.AiMentorToolReconciliations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiMentorToolReconciliations (
        ExecutionKey char(64) NOT NULL CONSTRAINT PK_AiMentorToolReconciliations PRIMARY KEY,
        TenantId nvarchar(128) NOT NULL,
        EvidenceState tinyint NOT NULL,
        EvidenceCode nvarchar(128) NOT NULL,
        EvidenceObservedAt datetimeoffset(7) NOT NULL,
        EvidenceExpiresAt datetimeoffset(7) NOT NULL,
        FirstReviewerSubjectId nvarchar(256) NOT NULL,
        FirstConfirmed bit NOT NULL,
        FirstReasonHash char(64) NOT NULL,
        FirstReviewedAt datetimeoffset(7) NOT NULL,
        SecondReviewerSubjectId nvarchar(256) NULL,
        SecondConfirmed bit NULL,
        SecondReasonHash char(64) NULL,
        SecondReviewedAt datetimeoffset(7) NULL,
        Status tinyint NOT NULL,
        UpdatedAt datetimeoffset(7) NOT NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT FK_AiMentorToolReconciliations_Execution
            FOREIGN KEY (ExecutionKey) REFERENCES dbo.AiMentorToolExecutions(ExecutionKey) ON DELETE CASCADE
    );
    CREATE INDEX IX_AiMentorToolReconciliations_TenantStatusExpiry
        ON dbo.AiMentorToolReconciliations(TenantId, Status, EvidenceExpiresAt);
END;

COMMIT TRANSACTION;
