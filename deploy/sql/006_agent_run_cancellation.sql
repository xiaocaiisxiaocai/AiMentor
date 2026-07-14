SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.AiMentorAgentRuns', N'U') IS NULL
    THROW 51000, '必须先执行 001_workflow.sql。', 1;

IF COL_LENGTH(N'dbo.AiMentorAgentRuns', N'CancelRequestedAt') IS NULL
    ALTER TABLE dbo.AiMentorAgentRuns ADD CancelRequestedAt datetimeoffset(7) NULL;

IF COL_LENGTH(N'dbo.AiMentorAgentRuns', N'CancellationReasonHash') IS NULL
    ALTER TABLE dbo.AiMentorAgentRuns ADD CancellationReasonHash char(64) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.AiMentorAgentRuns')
    AND name=N'IX_AiMentorAgentRuns_Cancellation')
    CREATE INDEX IX_AiMentorAgentRuns_Cancellation
        ON dbo.AiMentorAgentRuns(Status, CancelRequestedAt)
        INCLUDE (TenantId, SubjectId);

COMMIT TRANSACTION;
