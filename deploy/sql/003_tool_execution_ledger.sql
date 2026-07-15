SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.AiMentorToolExecutions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiMentorToolExecutions (
        ExecutionKey char(64) COLLATE Latin1_General_100_BIN2 NOT NULL
            CONSTRAINT PK_AiMentorToolExecutions PRIMARY KEY,
        RequestFingerprint char(64) NOT NULL,
        RunId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
        Status tinyint NOT NULL,
        LeaseToken nvarchar(64) NOT NULL,
        LeaseExpiresAt datetimeoffset(7) NOT NULL,
        KeyVersion nvarchar(64) NULL,
        ResultCipher nvarchar(max) NULL,
        CreatedAt datetimeoffset(7) NOT NULL,
        UpdatedAt datetimeoffset(7) NOT NULL,
        RowVersion rowversion NOT NULL
    );
    CREATE INDEX IX_AiMentorToolExecutions_StatusUpdated
        ON dbo.AiMentorToolExecutions(Status, UpdatedAt);
END;

COMMIT TRANSACTION;
