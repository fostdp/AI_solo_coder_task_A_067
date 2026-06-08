USE master;
GO

IF DB_ID(N'AluminumCellControl') IS NOT NULL
BEGIN
    ALTER DATABASE AluminumCellControl SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE AluminumCellControl;
END
GO

CREATE DATABASE AluminumCellControl;
GO

USE AluminumCellControl;
GO

CREATE TABLE Cells (
    CellId INT PRIMARY KEY,
    CellName NVARCHAR(50) NOT NULL,
    RowIndex INT NOT NULL,
    ColIndex INT NOT NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT N'正常',
    Concentration DECIMAL(5,2) NULL,
    ConcentrationStatus NVARCHAR(10) NOT NULL DEFAULT N'正常',
    AnodeEffectProbability DECIMAL(5,2) NULL,
    LastDataTime DATETIME2 NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME()
);

CREATE TABLE SensorData (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    CellId INT NOT NULL FOREIGN KEY REFERENCES Cells(CellId),
    Timestamp DATETIME2 NOT NULL,
    Voltage DECIMAL(6,3) NOT NULL,
    AnodeCurrentDistribution NVARCHAR(500) NULL,
    CellTemp DECIMAL(5,1) NULL,
    BathTemp DECIMAL(5,1) NULL,
    AlLevel DECIMAL(5,1) NULL,
    BathLevel DECIMAL(5,1) NULL,
    VoltageNoise DECIMAL(6,4) NULL,
    VoltageFluctuationFreq DECIMAL(6,3) NULL
);

CREATE TABLE AluminaConcentrations (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    CellId INT NOT NULL FOREIGN KEY REFERENCES Cells(CellId),
    Timestamp DATETIME2 NOT NULL,
    Concentration DECIMAL(5,2) NOT NULL,
    Status NVARCHAR(10) NOT NULL,
    ModelVersion NVARCHAR(20) NULL
);

CREATE TABLE FeedingRecords (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    CellId INT NOT NULL FOREIGN KEY REFERENCES Cells(CellId),
    Timestamp DATETIME2 NOT NULL,
    FeedAmountKg DECIMAL(5,2) NOT NULL,
    FeedType NVARCHAR(20) NOT NULL,
    TriggerReason NVARCHAR(100) NULL
);

CREATE TABLE AnodeEffectPredictions (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    CellId INT NOT NULL FOREIGN KEY REFERENCES Cells(CellId),
    Timestamp DATETIME2 NOT NULL,
    Probability DECIMAL(5,2) NOT NULL,
    PredictedMinutesAhead INT NOT NULL,
    ModelVersion NVARCHAR(20) NULL
);

CREATE TABLE Alarms (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    CellId INT NOT NULL FOREIGN KEY REFERENCES Cells(CellId),
    Timestamp DATETIME2 NOT NULL,
    AlarmType NVARCHAR(50) NOT NULL,
    AlarmLevel INT NOT NULL,
    Message NVARCHAR(500) NOT NULL,
    IsResolved BIT NOT NULL DEFAULT 0,
    ResolvedAt DATETIME2 NULL
);

CREATE TABLE ConcentrationAlarmTrackers (
    CellId INT PRIMARY KEY FOREIGN KEY REFERENCES Cells(CellId),
    LowStartTime DATETIME2 NULL,
    IsAlarmActive BIT NOT NULL DEFAULT 0
);

CREATE INDEX IX_SensorData_CellId_Timestamp ON SensorData(CellId, Timestamp DESC);
CREATE INDEX IX_AluminaConcentrations_CellId_Timestamp ON AluminaConcentrations(CellId, Timestamp DESC);
CREATE INDEX IX_FeedingRecords_CellId_Timestamp ON FeedingRecords(CellId, Timestamp DESC);
CREATE INDEX IX_AnodeEffectPredictions_CellId_Timestamp ON AnodeEffectPredictions(CellId, Timestamp DESC);
CREATE INDEX IX_Alarms_CellId_Timestamp ON Alarms(CellId, Timestamp DESC);
CREATE INDEX IX_Alarms_IsResolved ON Alarms(IsResolved, Timestamp DESC);

DECLARE @row INT = 1, @col INT = 1, @cellId INT = 1;
WHILE @cellId <= 200
BEGIN
    SET @row = ((@cellId - 1) / 20) + 1;
    SET @col = ((@cellId - 1) % 20) + 1;
    INSERT INTO Cells (CellId, CellName, RowIndex, ColIndex, Status, Concentration, ConcentrationStatus)
    VALUES (@cellId, N'电解槽-' + RIGHT('000' + CAST(@cellId AS NVARCHAR(3)), 3), @row, @col, N'正常', 3.00, N'正常');
    SET @cellId = @cellId + 1;
END
GO

PRINT N'Database AluminumCellControl initialized with 200 cells.';
GO
