SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.AiMentorOperationsActions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiMentorOperationsActions (
        Id nvarchar(64) NOT NULL CONSTRAINT PK_AiMentorOperationsActions PRIMARY KEY,
        TenantId nvarchar(128) NOT NULL,
        TargetType nvarchar(32) NOT NULL,
        TargetId nvarchar(128) NOT NULL,
        Action nvarchar(32) NOT NULL,
        TargetETag nvarchar(80) NOT NULL,
        RequestFingerprint char(64) NOT NULL,
        IdempotencyHash char(64) NOT NULL,
        RequesterSubjectId nvarchar(256) NOT NULL,
        RequestReasonHash char(64) NOT NULL,
        Status tinyint NOT NULL,
        Version bigint NOT NULL,
        CreatedAt datetimeoffset(7) NOT NULL,
        ExpiresAt datetimeoffset(7) NOT NULL,
        ReviewerSubjectId nvarchar(256) NULL,
        ReviewReasonHash char(64) NULL,
        ReviewedAt datetimeoffset(7) NULL,
        CompletedAt datetimeoffset(7) NULL,
        OutcomeCode nvarchar(128) NULL,
        RowVersion rowversion NOT NULL
    );
    CREATE UNIQUE INDEX UX_AiMentorOperationsActions_Idempotency
        ON dbo.AiMentorOperationsActions(TenantId, IdempotencyHash);
    CREATE INDEX IX_AiMentorOperationsActions_Target
        ON dbo.AiMentorOperationsActions(TenantId, TargetType, TargetId, Status);
    CREATE INDEX IX_AiMentorOperationsActions_Queue
        ON dbo.AiMentorOperationsActions(TenantId, Status, ExpiresAt);
END;

COMMIT TRANSACTION;
