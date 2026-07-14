SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.AiMentorToolExecutions', N'U') IS NULL
    THROW 50004, '请先执行 003_tool_execution_ledger.sql。', 1;

-- 旧记录不具备可靠租户归属，字段保持 NULL 并在查询时排除，避免错误暴露给任一租户。
IF COL_LENGTH(N'dbo.AiMentorToolExecutions', N'TenantId') IS NULL
    ALTER TABLE dbo.AiMentorToolExecutions ADD TenantId nvarchar(128) NULL;
IF COL_LENGTH(N'dbo.AiMentorToolExecutions', N'SubjectId') IS NULL
    ALTER TABLE dbo.AiMentorToolExecutions ADD SubjectId nvarchar(256) NULL;
IF COL_LENGTH(N'dbo.AiMentorToolExecutions', N'ToolName') IS NULL
    ALTER TABLE dbo.AiMentorToolExecutions ADD ToolName nvarchar(128) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
    WHERE object_id=OBJECT_ID(N'dbo.AiMentorToolExecutions')
      AND name=N'IX_AiMentorToolExecutions_TenantStatusUpdated')
    CREATE INDEX IX_AiMentorToolExecutions_TenantStatusUpdated
        ON dbo.AiMentorToolExecutions(TenantId, Status, UpdatedAt DESC);

COMMIT TRANSACTION;
