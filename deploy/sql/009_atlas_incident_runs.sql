SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.AiMentorAtlasIncidentRuns', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiMentorAtlasIncidentRuns (
        RunId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL
            CONSTRAINT PK_AiMentorAtlasIncidentRuns PRIMARY KEY,
        TenantId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
        SubjectId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
        RunbookId nvarchar(128) NOT NULL,
        RunbookVersion nvarchar(64) NOT NULL,
        Status tinyint NOT NULL,
        Version bigint NOT NULL,
        ExpiresAt datetimeoffset(7) NOT NULL,
        KeyVersion nvarchar(64) NOT NULL,
        PayloadCipher nvarchar(max) NOT NULL,
        LeaseToken nvarchar(64) NULL,
        LeaseOwner nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
        LeaseExpiresAt datetimeoffset(7) NULL,
        CreatedAt datetimeoffset(7) NOT NULL,
        UpdatedAt datetimeoffset(7) NOT NULL,
        RowVersion rowversion NOT NULL
    );
    CREATE INDEX IX_AiMentorAtlasIncidentRuns_OwnerStatus
        ON dbo.AiMentorAtlasIncidentRuns(TenantId, SubjectId, Status);
    CREATE INDEX IX_AiMentorAtlasIncidentRuns_LeaseExpiry
        ON dbo.AiMentorAtlasIncidentRuns(Status, LeaseExpiresAt);
    CREATE INDEX IX_AiMentorAtlasIncidentRuns_ExpiresAt
        ON dbo.AiMentorAtlasIncidentRuns(ExpiresAt);
    CREATE INDEX IX_AiMentorAtlasIncidentRuns_KeyVersion
        ON dbo.AiMentorAtlasIncidentRuns(KeyVersion);
END;

COMMIT TRANSACTION;
