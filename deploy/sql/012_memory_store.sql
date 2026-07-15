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

IF OBJECT_ID(N'dbo.AiMentorMemoryProposals', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiMentorMemoryProposals (
        Id nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL
            CONSTRAINT PK_AiMentorMemoryProposals PRIMARY KEY,
        TenantId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
        SubjectId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
        Scope tinyint NOT NULL,
        SessionId nvarchar(128) COLLATE Latin1_General_100_BIN2 NULL,
        KeyVersion nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
        KeyCipher nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
        KeyFingerprint char(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
        ValueCipher nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
        CreatedAt datetimeoffset(7) NOT NULL,
        ApprovalExpiresAt datetimeoffset(7) NOT NULL,
        MemoryExpiresAt datetimeoffset(7) NOT NULL,
        Status tinyint NOT NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT CK_AiMentorMemoryProposals_Scope CHECK (Scope BETWEEN 0 AND 2),
        CONSTRAINT CK_AiMentorMemoryProposals_Status CHECK (Status BETWEEN 0 AND 1)
    );
    CREATE INDEX IX_AiMentorMemoryProposals_Owner
        ON dbo.AiMentorMemoryProposals(TenantId, SubjectId, Status, ApprovalExpiresAt);
END;

IF COL_LENGTH(N'dbo.AiMentorMemoryProposals',N'KeyVersion') IS NULL
    ALTER TABLE dbo.AiMentorMemoryProposals
        ADD KeyVersion nvarchar(64) COLLATE Latin1_General_100_BIN2 NULL;
UPDATE dbo.AiMentorMemoryProposals SET KeyVersion=CASE
    WHEN KeyCipher LIKE N'v1.%' THEN N'v1'
    WHEN KeyCipher LIKE N'mem1.%' AND CHARINDEX(N'.',KeyCipher,6)>6
        THEN CONVERT(nvarchar(64),SUBSTRING(KeyCipher,6,CHARINDEX(N'.',KeyCipher,6)-6))
    ELSE NULL END WHERE KeyVersion IS NULL;
IF EXISTS (SELECT 1 FROM dbo.AiMentorMemoryProposals WHERE KeyVersion IS NULL)
    THROW 51000,N'记忆提案包含无法识别的历史密文版本。',1;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.AiMentorMemoryProposals')
    AND name=N'KeyVersion' AND is_nullable=1)
    ALTER TABLE dbo.AiMentorMemoryProposals
        ALTER COLUMN KeyVersion nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.AiMentorMemoryProposals')
      AND name = N'IX_AiMentorMemoryProposals_TenantStatusApprovalExpiry')
    CREATE INDEX IX_AiMentorMemoryProposals_TenantStatusApprovalExpiry
        ON dbo.AiMentorMemoryProposals(TenantId, Status, ApprovalExpiresAt);
IF NOT EXISTS (SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.AiMentorMemoryProposals')
      AND name = N'IX_AiMentorMemoryProposals_TenantMemoryExpiry')
    CREATE INDEX IX_AiMentorMemoryProposals_TenantMemoryExpiry
        ON dbo.AiMentorMemoryProposals(TenantId, MemoryExpiresAt);
IF NOT EXISTS (SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.AiMentorMemoryProposals')
      AND name = N'IX_AiMentorMemoryProposals_ApprovalExpiry')
    CREATE INDEX IX_AiMentorMemoryProposals_ApprovalExpiry
        ON dbo.AiMentorMemoryProposals(ApprovalExpiresAt) INCLUDE(Status);
IF NOT EXISTS (SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.AiMentorMemoryProposals')
      AND name = N'IX_AiMentorMemoryProposals_Status')
    CREATE INDEX IX_AiMentorMemoryProposals_Status
        ON dbo.AiMentorMemoryProposals(Status);
IF NOT EXISTS (SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.AiMentorMemoryProposals')
      AND name = N'IX_AiMentorMemoryProposals_MemoryExpiry')
    CREATE INDEX IX_AiMentorMemoryProposals_MemoryExpiry
        ON dbo.AiMentorMemoryProposals(MemoryExpiresAt);
IF NOT EXISTS (SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.AiMentorMemoryProposals')
      AND name = N'IX_AiMentorMemoryProposals_KeyVersion')
    CREATE INDEX IX_AiMentorMemoryProposals_KeyVersion
        ON dbo.AiMentorMemoryProposals(KeyVersion);

IF OBJECT_ID(N'dbo.AiMentorMemories', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiMentorMemories (
        Id nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL
            CONSTRAINT PK_AiMentorMemories PRIMARY KEY,
        TenantId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
        SubjectId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
        Scope tinyint NOT NULL,
        SessionId nvarchar(128) COLLATE Latin1_General_100_BIN2 NULL,
        KeyVersion nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
        KeyCipher nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
        KeyFingerprint char(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
        ValueCipher nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
        Version int NOT NULL,
        CreatedAt datetimeoffset(7) NOT NULL,
        UpdatedAt datetimeoffset(7) NOT NULL,
        ExpiresAt datetimeoffset(7) NOT NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT CK_AiMentorMemories_Scope CHECK (Scope BETWEEN 0 AND 2),
        CONSTRAINT CK_AiMentorMemories_Version CHECK (Version > 0)
    );
    -- 串行化批准会锁定此索引的精确所有者/会话/键区间，确保跨实例只有一个胜者。
    CREATE INDEX IX_AiMentorMemories_OwnerKey
        ON dbo.AiMentorMemories(TenantId, SubjectId, Scope, SessionId, KeyFingerprint, ExpiresAt);
    CREATE INDEX IX_AiMentorMemories_Expiry ON dbo.AiMentorMemories(ExpiresAt);
END;

IF COL_LENGTH(N'dbo.AiMentorMemories',N'KeyVersion') IS NULL
    ALTER TABLE dbo.AiMentorMemories
        ADD KeyVersion nvarchar(64) COLLATE Latin1_General_100_BIN2 NULL;
UPDATE dbo.AiMentorMemories SET KeyVersion=CASE
    WHEN KeyCipher LIKE N'v1.%' THEN N'v1'
    WHEN KeyCipher LIKE N'mem1.%' AND CHARINDEX(N'.',KeyCipher,6)>6
        THEN CONVERT(nvarchar(64),SUBSTRING(KeyCipher,6,CHARINDEX(N'.',KeyCipher,6)-6))
    ELSE NULL END WHERE KeyVersion IS NULL;
IF EXISTS (SELECT 1 FROM dbo.AiMentorMemories WHERE KeyVersion IS NULL)
    THROW 51000,N'正式记忆包含无法识别的历史密文版本。',1;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.AiMentorMemories')
    AND name=N'KeyVersion' AND is_nullable=1)
    ALTER TABLE dbo.AiMentorMemories
        ALTER COLUMN KeyVersion nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.AiMentorMemories')
      AND name = N'IX_AiMentorMemories_TenantExpiry')
    CREATE INDEX IX_AiMentorMemories_TenantExpiry
        ON dbo.AiMentorMemories(TenantId, ExpiresAt);
IF NOT EXISTS (SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.AiMentorMemories')
      AND name = N'IX_AiMentorMemories_KeyVersion')
    CREATE INDEX IX_AiMentorMemories_KeyVersion
        ON dbo.AiMentorMemories(KeyVersion);

COMMIT TRANSACTION;
