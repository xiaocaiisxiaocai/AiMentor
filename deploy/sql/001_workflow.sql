SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.AiMentorToolApprovals', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiMentorToolApprovals (
        Id nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL
            CONSTRAINT PK_AiMentorToolApprovals PRIMARY KEY,
        TenantId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
        RequesterSubjectId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
        ToolName nvarchar(128) NOT NULL,
        Risk int NOT NULL,
        ArgumentsHash char(64) NOT NULL,
        ArgumentNamesJson nvarchar(max) NOT NULL,
        Justification nvarchar(500) NOT NULL,
        CreatedAt datetimeoffset(7) NOT NULL,
        ExpiresAt datetimeoffset(7) NOT NULL,
        Status int NOT NULL,
        ApproverSubjectId nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
        DecidedAt datetimeoffset(7) NULL,
        DecisionReason nvarchar(500) NULL,
        ConsumedAt datetimeoffset(7) NULL,
        RowVersion rowversion NOT NULL
    );
    CREATE INDEX IX_AiMentorToolApprovals_TenantStatus
        ON dbo.AiMentorToolApprovals(TenantId, Status, CreatedAt DESC);
END;

IF OBJECT_ID(N'dbo.AiMentorAgentRuns', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiMentorAgentRuns (
        RunId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL
            CONSTRAINT PK_AiMentorAgentRuns PRIMARY KEY,
        TenantId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
        SubjectId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
        ApprovalId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
        ExpiresAt datetimeoffset(7) NOT NULL,
        PayloadCipher nvarchar(max) NOT NULL,
        Status tinyint NOT NULL,
        LeaseToken nvarchar(64) NULL,
        LeaseOwner nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
        LeaseExpiresAt datetimeoffset(7) NULL,
        CreatedAt datetimeoffset(7) NOT NULL,
        UpdatedAt datetimeoffset(7) NOT NULL,
        RowVersion rowversion NOT NULL
    );
    CREATE INDEX IX_AiMentorAgentRuns_OwnerStatus
        ON dbo.AiMentorAgentRuns(TenantId, SubjectId, Status);
    CREATE INDEX IX_AiMentorAgentRuns_LeaseExpiry
        ON dbo.AiMentorAgentRuns(Status, LeaseExpiresAt);
END;

COMMIT TRANSACTION;
