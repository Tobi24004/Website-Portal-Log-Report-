/* ============================================================================
   TẠO SQL LOGIN CHỈ ĐỌC: portal_readonly
   Mục đích : Tài khoản mà portal dùng để CHẠY các báo cáo trên database nguồn.
              Chỉ có quyền đọc (SELECT), KHÔNG được ghi/sửa/xóa.
   Áp dụng  : WideWorldImporters (database nguồn dùng để kiểm thử).
   Lưu ý    : Đổi mật khẩu bên dưới trước khi dùng Production.
   ============================================================================ */

SET NOCOUNT ON;
GO

/* ---- 1. Tạo server login ------------------------------------------------ */
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'portal_readonly')
BEGIN
    CREATE LOGIN [portal_readonly]
        WITH PASSWORD = N'CHANGE_ME_MATCH_SOURCE_READONLY_PASSWORD',
             CHECK_POLICY = ON,
             DEFAULT_DATABASE = [WideWorldImporters];
    PRINT N'>>> Da tao login portal_readonly.';
END
ELSE
    PRINT N'>>> Login portal_readonly da ton tai - bo qua.';
GO

/* ---- 2. Cấp quyền chỉ đọc trên WideWorldImporters ----------------------- */
USE [WideWorldImporters];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'portal_readonly')
    CREATE USER [portal_readonly] FOR LOGIN [portal_readonly];
GO

-- Chỉ đọc toàn bộ bảng/view
ALTER ROLE [db_datareader] ADD MEMBER [portal_readonly];
GO

-- Bảo đảm KHÔNG có quyền ghi (phòng khi được cấp trước đó)
IF EXISTS (SELECT 1 FROM sys.database_role_members rm
           JOIN sys.database_principals r ON r.principal_id = rm.role_principal_id
           JOIN sys.database_principals m ON m.principal_id = rm.member_principal_id
           WHERE r.name = N'db_datawriter' AND m.name = N'portal_readonly')
    ALTER ROLE [db_datawriter] DROP MEMBER [portal_readonly];
GO

-- Bảo đảm có quyền kết nối database
GRANT CONNECT TO [portal_readonly];
GO

-- Chặn tường minh các thao tác ghi ở cấp database.
-- LƯU Ý: KHÔNG dùng CONTROL/ALTER ở đây vì DENY CONTROL sẽ chặn luôn quyền CONNECT.
DENY INSERT, UPDATE, DELETE TO [portal_readonly];
GO

/* ---- 3. Row-Level Security của WideWorldImporters ----------------------- */
-- WideWorldImporters có policy FilterCustomersBySalesTerritoryRole lọc Sales.Customers
-- theo các role "<SalesTerritory> Sales". Tài khoản báo cáo chỉ-đọc cần đọc mọi khu vực,
-- nên thêm portal_readonly vào tất cả role lãnh thổ (chỉ phục vụ RLS, KHÔNG cấp quyền ghi).
DECLARE @sql nvarchar(max) = N'';
SELECT @sql = @sql + N'ALTER ROLE ' + QUOTENAME(name) + N' ADD MEMBER [portal_readonly];' + CHAR(10)
FROM sys.database_principals
WHERE type = 'R' AND name LIKE N'% Sales';
EXEC sys.sp_executesql @sql;
PRINT N'>>> Da them portal_readonly vao cac role lanh tho Sales (cho RLS).';
GO

PRINT N'>>> Da cap quyen chi doc cho portal_readonly tren WideWorldImporters.';
GO
