-- 共享最近成功清理时间，使未取得锁的副本只能依据数据库事实判断集群清理是否新鲜。
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

IF OBJECT_ID(N'dbo.AiMentorMemoryRetentionState', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiMentorMemoryRetentionState (
        Id tinyint NOT NULL
            CONSTRAINT PK_AiMentorMemoryRetentionState PRIMARY KEY,
        LastCompletedAt datetimeoffset(7) NULL,
        MonitoringStartedAt datetimeoffset(7) NOT NULL
            CONSTRAINT DF_AiMentorMemoryRetentionState_MonitoringStartedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_AiMentorMemoryRetentionState_Singleton CHECK (Id = 1)
    );
END;

IF NOT EXISTS (SELECT 1 FROM dbo.AiMentorMemoryRetentionState WITH (UPDLOCK, HOLDLOCK) WHERE Id = 1)
    INSERT INTO dbo.AiMentorMemoryRetentionState(Id, LastCompletedAt) VALUES(1, NULL);

COMMIT TRANSACTION;
