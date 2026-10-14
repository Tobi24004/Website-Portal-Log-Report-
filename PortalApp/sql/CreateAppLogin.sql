/* ============================================================================
   TẠO LOGIN CHO ỨNG DỤNG PORTAL: portal_app
   Mục đích : Tài khoản mà backend dùng để ĐỌC/GHI trên metadata DB
              (DBLogReportPortal). KHÔNG dùng để chạy báo cáo trên DB nguồn.
   Quyền    : db_datareader + db_datawriter trên DBLogReportPortal.
   Lưu ý    : Đổi mật khẩu cho khớp biến môi trường PORTAL_APP_PASSWORD (.env).
   ============================================================================ */
SET NOCOUNT ON;
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'portal_app')
BEGIN
    CREATE LOGIN [portal_app]
        WITH PASSWORD = N'CHANGE_ME_MATCH_PORTAL_APP_PASSWORD',
             CHECK_POLICY = ON,
             DEFAULT_DATABASE = [DBLogReportPortal];
    PRINT N'>>> Da tao login portal_app.';
END
ELSE
    PRINT N'>>> Login portal_app da ton tai - bo qua.';
GO

USE [DBLogReportPortal];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'portal_app')
    CREATE USER [portal_app] FOR LOGIN [portal_app];
GO

ALTER ROLE [db_datareader] ADD MEMBER [portal_app];
ALTER ROLE [db_datawriter] ADD MEMBER [portal_app];
GO

GRANT CONNECT TO [portal_app];
GO

PRINT N'>>> Da cap quyen doc/ghi cho portal_app tren DBLogReportPortal.';
GO
