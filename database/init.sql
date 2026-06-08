-- 电解铝槽监控系统 数据库初始化脚本

USE master;
GO

IF DB_ID(N'AlCellMonitor') IS NOT NULL
BEGIN
    ALTER DATABASE AlCellMonitor SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE AlCellMonitor;
END
GO

CREATE DATABASE AlCellMonitor;
GO

USE AlCellMonitor;
GO

-- 1. 槽信息表
CREATE TABLE CellInfo (
    CellId      INT           NOT NULL,
    CellName    NVARCHAR(50)  NOT NULL,
    RowIndex    INT           NOT NULL,
    ColIndex    INT           NOT NULL,
    Zone        NVARCHAR(10)  NOT NULL,
    IsOnline    BIT           NOT NULL DEFAULT 1,
    CreatedAt   DATETIME      NOT NULL DEFAULT GETDATE(),
    CONSTRAINT PK_CellInfo PRIMARY KEY (CellId)
);
GO

-- 2. 槽实时数据表
CREATE TABLE CellRealtimeData (
    Id                      BIGINT           IDENTITY(1,1) NOT NULL,
    CellId                  INT              NOT NULL,
    Voltage                 FLOAT            NULL,
    AnodeCurrentDistribution NVARCHAR(MAX)   NULL,
    CellTemperature         FLOAT            NULL,
    BathTemperature         FLOAT            NULL,
    AluminumLevel           FLOAT            NULL,
    BathLevel               FLOAT            NULL,
    AluminaConcentration    FLOAT            NULL,
    ReceivedAt              DATETIME         NOT NULL DEFAULT GETDATE(),
    CONSTRAINT PK_CellRealtimeData PRIMARY KEY (Id),
    CONSTRAINT FK_CellRealtimeData_CellInfo FOREIGN KEY (CellId) REFERENCES CellInfo(CellId)
);
GO

CREATE NONCLUSTERED INDEX IX_CellRealtimeData_CellId_ReceivedAt
    ON CellRealtimeData (CellId, ReceivedAt);
GO

-- 3. 氧化铝浓度历史表
CREATE TABLE AluminaConcentrationHistory (
    Id                    BIGINT         IDENTITY(1,1) NOT NULL,
    CellId                INT            NOT NULL,
    EstimatedConcentration FLOAT         NULL,
    ModelVersion          NVARCHAR(50)   NULL,
    EstimatedAt           DATETIME       NOT NULL DEFAULT GETDATE(),
    CONSTRAINT PK_AluminaConcentrationHistory PRIMARY KEY (Id),
    CONSTRAINT FK_AluminaConcentrationHistory_CellInfo FOREIGN KEY (CellId) REFERENCES CellInfo(CellId)
);
GO

-- 4. 下料记录表
CREATE TABLE FeedingRecord (
    Id            BIGINT         IDENTITY(1,1) NOT NULL,
    CellId        INT            NOT NULL,
    FeedType      NVARCHAR(20)   NOT NULL,
    FeedAmountKg  FLOAT          NULL,
    FedAt         DATETIME       NOT NULL DEFAULT GETDATE(),
    CONSTRAINT PK_FeedingRecord PRIMARY KEY (Id),
    CONSTRAINT CK_FeedingRecord_FeedType CHECK (FeedType IN (N'CrustBreak', N'PointFeed')),
    CONSTRAINT FK_FeedingRecord_CellInfo FOREIGN KEY (CellId) REFERENCES CellInfo(CellId)
);
GO

-- 5. 阳极效应预测表
CREATE TABLE AnodeEffectPrediction (
    Id            BIGINT   IDENTITY(1,1) NOT NULL,
    CellId        INT      NOT NULL,
    Probability   FLOAT    NULL,
    PredictedAt   DATETIME NOT NULL DEFAULT GETDATE(),
    CONSTRAINT PK_AnodeEffectPrediction PRIMARY KEY (Id),
    CONSTRAINT FK_AnodeEffectPrediction_CellInfo FOREIGN KEY (CellId) REFERENCES CellInfo(CellId)
);
GO

-- 6. 报警记录表
CREATE TABLE AlarmRecord (
    Id              BIGINT         IDENTITY(1,1) NOT NULL,
    CellId          INT            NOT NULL,
    AlarmLevel      INT            NOT NULL,
    AlarmType       NVARCHAR(30)   NOT NULL,
    AlarmMessage    NVARCHAR(500)  NULL,
    IsAcknowledged  BIT            NOT NULL DEFAULT 0,
    TriggeredAt     DATETIME       NOT NULL DEFAULT GETDATE(),
    AcknowledgedAt  DATETIME       NULL,
    CONSTRAINT PK_AlarmRecord PRIMARY KEY (Id),
    CONSTRAINT CK_AlarmRecord_AlarmLevel CHECK (AlarmLevel IN (1, 2)),
    CONSTRAINT CK_AlarmRecord_AlarmType CHECK (AlarmType IN (N'ConcentrationLow', N'AnodeEffect')),
    CONSTRAINT FK_AlarmRecord_CellInfo FOREIGN KEY (CellId) REFERENCES CellInfo(CellId)
);
GO

-- 7. 槽控制指令表
CREATE TABLE CellControlCommand (
    Id            BIGINT          IDENTITY(1,1) NOT NULL,
    CellId        INT             NOT NULL,
    CommandType   NVARCHAR(50)    NOT NULL,
    CommandParams NVARCHAR(MAX)   NULL,
    IssuedAt      DATETIME        NOT NULL DEFAULT GETDATE(),
    ExecutedAt    DATETIME        NULL,
    Status        NVARCHAR(20)    NOT NULL,
    CONSTRAINT PK_CellControlCommand PRIMARY KEY (Id),
    CONSTRAINT FK_CellControlCommand_CellInfo FOREIGN KEY (CellId) REFERENCES CellInfo(CellId)
);
GO

-- 插入200个电解槽: 10行 x 20列, 1-5行=A区, 6-10行=B区
;WITH Rows10   AS (SELECT n FROM (VALUES (1),(2),(3),(4),(5),(6),(7),(8),(9),(10))  AS T(n)),
      Cols20   AS (SELECT n FROM (VALUES (1),(2),(3),(4),(5),(6),(7),(8),(9),(10),(11),(12),(13),(14),(15),(16),(17),(18),(19),(20)) AS T(n))
INSERT INTO CellInfo (CellId, CellName, RowIndex, ColIndex, Zone, IsOnline, CreatedAt)
SELECT
    (r.n - 1) * 20 + c.n                          AS CellId,
    N'电解槽-' + RIGHT(N'000' + CAST((r.n - 1) * 20 + c.n AS NVARCHAR(3)), 3) AS CellName,
    r.n                                             AS RowIndex,
    c.n                                             AS ColIndex,
    CASE WHEN r.n <= 5 THEN N'A区' ELSE N'B区' END  AS Zone,
    1                                                AS IsOnline,
    GETDATE()                                        AS CreatedAt
FROM Rows10 r
CROSS JOIN Cols20 c
ORDER BY CellId;
GO
