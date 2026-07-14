SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.AiMentorToolCompensations', N'U') IS NULL
    THROW 50008, '请先执行 007 工具补偿迁移。', 1;

-- 复核表只保存稳定证据代码和理由摘要；加密补偿快照及记忆正文仍只存在主补偿表。
IF OBJECT_ID(N'dbo.AiMentorToolCompensationReconciliations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiMentorToolCompensationReconciliations (
        CompensationId nvarchar(64) NOT NULL
            CONSTRAINT PK_AiMentorToolCompensationReconciliations PRIMARY KEY,
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
        CONSTRAINT FK_AiMentorToolCompensationReconciliations_Compensation
            FOREIGN KEY (CompensationId) REFERENCES dbo.AiMentorToolCompensations(Id) ON DELETE CASCADE
    );

    CREATE INDEX IX_AiMentorToolCompensationReconciliations_TenantStatusExpiry
        ON dbo.AiMentorToolCompensationReconciliations(TenantId, Status, EvidenceExpiresAt);
END;

COMMIT TRANSACTION;
