DECLARE @BackupDir NVARCHAR(500) = N'/var/opt/mssql/backup';
DECLARE @DateSuffix NVARCHAR(20) = FORMAT(GETUTCDATE(), 'yyyyMMdd_HHmmss');
DECLARE @DbName NVARCHAR(128) = DB_NAME();

DECLARE @FullBackupPath NVARCHAR(1000) = @BackupDir + N'/' + @DbName + N'_full_' + @DateSuffix + N'.bak';
DECLARE @LogBackupPath NVARCHAR(1000) = @BackupDir + N'/' + @DbName + N'_log_' + @DateSuffix + N'.trn';

BACKUP DATABASE @DbName TO DISK = @FullBackupPath
WITH FORMAT, INIT, NAME = @DbName + N'-Full Backup',
     COMPRESSION, STATS = 10, CHECKSUM;

BACKUP LOG @DbName TO DISK = @LogBackupPath
WITH FORMAT, INIT, NAME = @DbName + N'-Log Backup',
     COMPRESSION, STATS = 10;

DECLARE @CutoffDate DATETIME = DATEADD(DAY, -7, GETUTCDATE());
DECLARE @OldBackup NVARCHAR(1000);

DECLARE backup_cursor CURSOR FOR
    SELECT physical_device_name
    FROM msdb.dbo.backupset bs
    JOIN msdb.dbo.backupmediafamily bmf ON bs.media_set_id = bmf.media_set_id
    WHERE bs.backup_finish_date < @CutoffDate
      AND bs.database_name = @DbName;

OPEN backup_cursor;
FETCH NEXT FROM backup_cursor INTO @OldBackup;
WHILE @@FETCH_STATUS = 0
BEGIN
    BEGIN TRY
        EXEC xp_delete_file 0, @OldBackup;
    END TRY
    BEGIN CATCH
        PRINT N'Skip: ' + @OldBackup;
    END CATCH
    FETCH NEXT FROM backup_cursor INTO @OldBackup;
END
CLOSE backup_cursor;
DEALLOCATE backup_cursor;
