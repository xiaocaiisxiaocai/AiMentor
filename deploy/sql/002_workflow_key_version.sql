SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.AiMentorAgentRuns', N'U') IS NULL
    THROW 51000, '必须先执行 001_workflow.sql。', 1;

IF COL_LENGTH(N'dbo.AiMentorAgentRuns', N'KeyVersion') IS NULL
    ALTER TABLE dbo.AiMentorAgentRuns ADD KeyVersion nvarchar(64) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.AiMentorAgentRuns')
    AND name=N'IX_AiMentorAgentRuns_KeyVersion')
    CREATE INDEX IX_AiMentorAgentRuns_KeyVersion ON dbo.AiMentorAgentRuns(KeyVersion);

COMMIT TRANSACTION;
