/* ============================================================================
   SCRIPT TẠO CƠ SỞ DỮ LIỆU METADATA: DBLogReportPortal
   Hệ thống: Website Portal Log Report
   Mục tiêu : Lưu metadata cho portal (users, roles, connections, SQL template,
              tham số, lịch sử truy vấn, audit log, cấu hình hệ thống).
   RDBMS    : Microsoft SQL Server (2017+)
   Ghi chú  :
     - Tất cả bảng dùng cột audit chuẩn: CreatedAt, CreatedBy, UpdatedAt,
       UpdatedBy và cột xóa mềm IsDeleted.
     - Mật khẩu người dùng lưu dạng băm (PasswordHash); mật khẩu DB lưu dạng
       mã hóa (PasswordEncrypted) - KHÔNG lưu plaintext.
     - Thời gian dùng datetime2 theo giờ UTC (SYSUTCDATETIME()).
     - Script idempotent: có thể chạy lại nhiều lần (IF NOT EXISTS).
   ============================================================================ */

SET NOCOUNT ON;
GO

-- Bắt buộc cho filtered index / computed column (nếu chạy bằng sqlcmd).
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ----------------------------------------------------------------------------
   1. TẠO DATABASE
   ---------------------------------------------------------------------------- */
IF DB_ID(N'DBLogReportPortal') IS NULL
BEGIN
    CREATE DATABASE [DBLogReportPortal];
END
GO

ALTER DATABASE [DBLogReportPortal] SET RECOVERY SIMPLE;
GO

USE [DBLogReportPortal];
GO

/* ----------------------------------------------------------------------------
   2. SCHEMA
   ---------------------------------------------------------------------------- */
IF SCHEMA_ID(N'portal') IS NULL
    EXEC(N'CREATE SCHEMA [portal] AUTHORIZATION [dbo];');
GO

/* ============================================================================
   3. NHÓM BẢNG: NGƯỜI DÙNG & PHÂN QUYỀN
   ============================================================================ */

/* ---- 3.1 Users ---------------------------------------------------------- */
IF OBJECT_ID(N'portal.Users', N'U') IS NULL
BEGIN
    CREATE TABLE portal.Users
    (
        UserId        INT            IDENTITY(1,1) NOT NULL,
        Username      NVARCHAR(100)  NOT NULL,
        FullName      NVARCHAR(200)  NULL,
        Email         NVARCHAR(256)  NULL,
        Department    NVARCHAR(200)  NULL,
        PasswordHash  NVARCHAR(512)  NOT NULL,
        IsActive      BIT            NOT NULL CONSTRAINT DF_Users_IsActive  DEFAULT (1),
        MustChangePassword BIT       NOT NULL CONSTRAINT DF_Users_MustChangePwd DEFAULT (0), -- bắt buộc đổi MK lần đầu
        LastLoginAt   DATETIME2(3)   NULL,
        -- audit
        IsDeleted     BIT            NOT NULL CONSTRAINT DF_Users_IsDeleted DEFAULT (0),
        CreatedAt     DATETIME2(3)   NOT NULL CONSTRAINT DF_Users_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CreatedBy     INT            NULL,
        UpdatedAt     DATETIME2(3)   NULL,
        UpdatedBy     INT            NULL,
        CONSTRAINT PK_Users PRIMARY KEY CLUSTERED (UserId)
    );

    CREATE UNIQUE INDEX UX_Users_Username ON portal.Users(Username) WHERE IsDeleted = 0;
    CREATE INDEX IX_Users_Email          ON portal.Users(Email)    WHERE IsDeleted = 0;
END
GO

/* ---- 3.2 Roles ---------------------------------------------------------- */
IF OBJECT_ID(N'portal.Roles', N'U') IS NULL
BEGIN
    CREATE TABLE portal.Roles
    (
        RoleId      INT            IDENTITY(1,1) NOT NULL,
        RoleName    NVARCHAR(100)  NOT NULL,
        Description NVARCHAR(400)  NULL,
        IsDeleted   BIT            NOT NULL CONSTRAINT DF_Roles_IsDeleted DEFAULT (0),
        CreatedAt   DATETIME2(3)   NOT NULL CONSTRAINT DF_Roles_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CreatedBy   INT            NULL,
        UpdatedAt   DATETIME2(3)   NULL,
        UpdatedBy   INT            NULL,
        CONSTRAINT PK_Roles PRIMARY KEY CLUSTERED (RoleId)
    );

    CREATE UNIQUE INDEX UX_Roles_RoleName ON portal.Roles(RoleName) WHERE IsDeleted = 0;
END
GO

/* ---- 3.3 UserRoles (N-N) ------------------------------------------------ */
IF OBJECT_ID(N'portal.UserRoles', N'U') IS NULL
BEGIN
    CREATE TABLE portal.UserRoles
    (
        UserId     INT          NOT NULL,
        RoleId     INT          NOT NULL,
        AssignedAt DATETIME2(3) NOT NULL CONSTRAINT DF_UserRoles_AssignedAt DEFAULT (SYSUTCDATETIME()),
        AssignedBy INT          NULL,
        CONSTRAINT PK_UserRoles PRIMARY KEY CLUSTERED (UserId, RoleId),
        CONSTRAINT FK_UserRoles_User FOREIGN KEY (UserId) REFERENCES portal.Users(UserId),
        CONSTRAINT FK_UserRoles_Role FOREIGN KEY (RoleId) REFERENCES portal.Roles(RoleId)
    );
    CREATE INDEX IX_UserRoles_Role ON portal.UserRoles(RoleId);
END
GO

/* ---- 3.4 Permissions ---------------------------------------------------- */
IF OBJECT_ID(N'portal.Permissions', N'U') IS NULL
BEGIN
    CREATE TABLE portal.Permissions
    (
        PermissionId   INT            IDENTITY(1,1) NOT NULL,
        PermissionCode NVARCHAR(100)  NOT NULL,   -- vd: USER.MANAGE, TEMPLATE.EDIT
        Description    NVARCHAR(400)  NULL,
        CONSTRAINT PK_Permissions PRIMARY KEY CLUSTERED (PermissionId),
        CONSTRAINT UQ_Permissions_Code UNIQUE (PermissionCode)
    );
END
GO

/* ---- 3.5 RolePermissions (N-N) ------------------------------------------ */
IF OBJECT_ID(N'portal.RolePermissions', N'U') IS NULL
BEGIN
    CREATE TABLE portal.RolePermissions
    (
        RoleId       INT NOT NULL,
        PermissionId INT NOT NULL,
        CONSTRAINT PK_RolePermissions PRIMARY KEY CLUSTERED (RoleId, PermissionId),
        CONSTRAINT FK_RolePerm_Role FOREIGN KEY (RoleId)       REFERENCES portal.Roles(RoleId) ON DELETE CASCADE,
        CONSTRAINT FK_RolePerm_Perm FOREIGN KEY (PermissionId) REFERENCES portal.Permissions(PermissionId) ON DELETE CASCADE
    );
    CREATE INDEX IX_RolePerm_Perm ON portal.RolePermissions(PermissionId);
END
GO

/* ============================================================================
   4. NHÓM BẢNG: KẾT NỐI DATABASE NGUỒN
   ============================================================================ */

/* ---- 4.1 DatabaseConnections -------------------------------------------- */
IF OBJECT_ID(N'portal.DatabaseConnections', N'U') IS NULL
BEGIN
    CREATE TABLE portal.DatabaseConnections
    (
        ConnectionId      INT            IDENTITY(1,1) NOT NULL,
        ConnectionName    NVARCHAR(200)  NOT NULL,
        ServerHost        NVARCHAR(256)  NOT NULL,
        Port              INT            NULL,
        DatabaseName      NVARCHAR(256)  NOT NULL,
        Username          NVARCHAR(200)  NOT NULL,
        PasswordEncrypted NVARCHAR(1024) NOT NULL,                 -- mã hóa, không plaintext
        Environment       NVARCHAR(20)   NOT NULL CONSTRAINT DF_Conn_Env DEFAULT (N'Development'),
        ConnectionTimeout INT            NOT NULL CONSTRAINT DF_Conn_ConnTimeout DEFAULT (15),
        CommandTimeout    INT            NOT NULL CONSTRAINT DF_Conn_CmdTimeout  DEFAULT (30),
        IsActive          BIT            NOT NULL CONSTRAINT DF_Conn_IsActive     DEFAULT (1),
        IsDeleted         BIT            NOT NULL CONSTRAINT DF_Conn_IsDeleted     DEFAULT (0),
        CreatedAt         DATETIME2(3)   NOT NULL CONSTRAINT DF_Conn_CreatedAt     DEFAULT (SYSUTCDATETIME()),
        CreatedBy         INT            NULL,
        UpdatedAt         DATETIME2(3)   NULL,
        UpdatedBy         INT            NULL,
        CONSTRAINT PK_DatabaseConnections PRIMARY KEY CLUSTERED (ConnectionId),
        CONSTRAINT CK_Conn_Env CHECK (Environment IN (N'Development', N'Test', N'Production')),
        CONSTRAINT CK_Conn_Port CHECK (Port IS NULL OR (Port BETWEEN 1 AND 65535))
    );
    CREATE UNIQUE INDEX UX_Conn_Name ON portal.DatabaseConnections(ConnectionName) WHERE IsDeleted = 0;
END
GO

/* ============================================================================
   5. NHÓM BẢNG: NHÓM BÁO CÁO & SQL TEMPLATE
   ============================================================================ */

/* ---- 5.1 ReportCategories ----------------------------------------------- */
IF OBJECT_ID(N'portal.ReportCategories', N'U') IS NULL
BEGIN
    CREATE TABLE portal.ReportCategories
    (
        CategoryId   INT            IDENTITY(1,1) NOT NULL,
        CategoryName NVARCHAR(200)  NOT NULL,
        Description  NVARCHAR(400)  NULL,
        DisplayOrder INT            NOT NULL CONSTRAINT DF_Cat_Order DEFAULT (0),
        IsDeleted    BIT            NOT NULL CONSTRAINT DF_Cat_IsDeleted DEFAULT (0),
        CreatedAt    DATETIME2(3)   NOT NULL CONSTRAINT DF_Cat_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CreatedBy    INT            NULL,
        UpdatedAt    DATETIME2(3)   NULL,
        UpdatedBy    INT            NULL,
        CONSTRAINT PK_ReportCategories PRIMARY KEY CLUSTERED (CategoryId)
    );
    CREATE UNIQUE INDEX UX_Cat_Name ON portal.ReportCategories(CategoryName) WHERE IsDeleted = 0;
END
GO

/* ---- 5.2 ReportTemplates ------------------------------------------------ */
IF OBJECT_ID(N'portal.ReportTemplates', N'U') IS NULL
BEGIN
    CREATE TABLE portal.ReportTemplates
    (
        TemplateId     INT            IDENTITY(1,1) NOT NULL,
        TemplateCode   NVARCHAR(100)  NOT NULL,
        TemplateName   NVARCHAR(300)  NOT NULL,
        Description    NVARCHAR(1000) NULL,
        CategoryId     INT            NOT NULL,
        ConnectionId   INT            NOT NULL,
        SqlCommand     NVARCHAR(MAX)  NOT NULL,
        MaxRows        INT            NOT NULL CONSTRAINT DF_Tpl_MaxRows  DEFAULT (10000),
        TimeoutSeconds INT            NOT NULL CONSTRAINT DF_Tpl_Timeout  DEFAULT (30),
        AllowCopy      BIT            NOT NULL CONSTRAINT DF_Tpl_AllowCopy   DEFAULT (1),
        AllowExport    BIT            NOT NULL CONSTRAINT DF_Tpl_AllowExport DEFAULT (1),
        Status         NVARCHAR(20)   NOT NULL CONSTRAINT DF_Tpl_Status      DEFAULT (N'Draft'),
        IsDeleted      BIT            NOT NULL CONSTRAINT DF_Tpl_IsDeleted    DEFAULT (0),
        CreatedAt      DATETIME2(3)   NOT NULL CONSTRAINT DF_Tpl_CreatedAt    DEFAULT (SYSUTCDATETIME()),
        CreatedBy      INT            NULL,
        UpdatedAt      DATETIME2(3)   NULL,
        UpdatedBy      INT            NULL,
        CONSTRAINT PK_ReportTemplates PRIMARY KEY CLUSTERED (TemplateId),
        CONSTRAINT FK_Tpl_Category   FOREIGN KEY (CategoryId)   REFERENCES portal.ReportCategories(CategoryId),
        CONSTRAINT FK_Tpl_Connection FOREIGN KEY (ConnectionId) REFERENCES portal.DatabaseConnections(ConnectionId),
        CONSTRAINT CK_Tpl_Status  CHECK (Status IN (N'Draft', N'Published', N'Disabled')),
        CONSTRAINT CK_Tpl_MaxRows CHECK (MaxRows > 0),
        CONSTRAINT CK_Tpl_Timeout CHECK (TimeoutSeconds > 0)
    );
    CREATE UNIQUE INDEX UX_Tpl_Code  ON portal.ReportTemplates(TemplateCode) WHERE IsDeleted = 0;
    CREATE INDEX IX_Tpl_Category     ON portal.ReportTemplates(CategoryId);
    CREATE INDEX IX_Tpl_Connection   ON portal.ReportTemplates(ConnectionId);
    CREATE INDEX IX_Tpl_Status       ON portal.ReportTemplates(Status) WHERE IsDeleted = 0;
END
GO

/* ---- 5.3 ReportTemplateParameters --------------------------------------- */
IF OBJECT_ID(N'portal.ReportTemplateParameters', N'U') IS NULL
BEGIN
    CREATE TABLE portal.ReportTemplateParameters
    (
        ParameterId   INT            IDENTITY(1,1) NOT NULL,
        TemplateId    INT            NOT NULL,
        ParameterName NVARCHAR(100)  NOT NULL,       -- tên tham số trong SQL (vd @StartDate)
        DisplayName   NVARCHAR(200)  NOT NULL,
        DataType      NVARCHAR(20)   NOT NULL,        -- Text, Integer, Decimal, Date, DateTime, Dropdown, Checkbox
        InputType     NVARCHAR(20)   NOT NULL,
        IsRequired    BIT            NOT NULL CONSTRAINT DF_Param_IsRequired DEFAULT (0),
        DefaultValue  NVARCHAR(400)  NULL,
        DisplayOrder  INT            NOT NULL CONSTRAINT DF_Param_Order DEFAULT (0),
        MinValue      NVARCHAR(100)  NULL,
        MaxValue      NVARCHAR(100)  NULL,
        MaxLength     INT            NULL,
        Options       NVARCHAR(MAX)  NULL,            -- JSON danh sách giá trị cho Dropdown
        CONSTRAINT PK_ReportTemplateParameters PRIMARY KEY CLUSTERED (ParameterId),
        CONSTRAINT FK_Param_Template FOREIGN KEY (TemplateId) REFERENCES portal.ReportTemplates(TemplateId) ON DELETE CASCADE,
        CONSTRAINT CK_Param_DataType CHECK (DataType IN (N'Text', N'Integer', N'Decimal', N'Date', N'DateTime', N'Dropdown', N'Checkbox'))
    );
    CREATE UNIQUE INDEX UX_Param_Template_Name ON portal.ReportTemplateParameters(TemplateId, ParameterName);
END
GO

/* ---- 5.4 ReportTemplatePermissions (cấp quyền BC cho role hoặc user) ---- */
IF OBJECT_ID(N'portal.ReportTemplatePermissions', N'U') IS NULL
BEGIN
    CREATE TABLE portal.ReportTemplatePermissions
    (
        GrantId    INT          IDENTITY(1,1) NOT NULL,
        TemplateId INT          NOT NULL,
        RoleId     INT          NULL,          -- cấp cho vai trò ...
        UserId     INT          NULL,          -- ... hoặc cho người dùng cụ thể
        GrantedAt  DATETIME2(3) NOT NULL CONSTRAINT DF_TplPerm_GrantedAt DEFAULT (SYSUTCDATETIME()),
        GrantedBy  INT          NULL,
        CONSTRAINT PK_ReportTemplatePermissions PRIMARY KEY CLUSTERED (GrantId),
        CONSTRAINT FK_TplPerm_Template FOREIGN KEY (TemplateId) REFERENCES portal.ReportTemplates(TemplateId) ON DELETE CASCADE,
        CONSTRAINT FK_TplPerm_Role     FOREIGN KEY (RoleId)     REFERENCES portal.Roles(RoleId),
        CONSTRAINT FK_TplPerm_User     FOREIGN KEY (UserId)     REFERENCES portal.Users(UserId),
        -- phải cấp cho đúng một trong hai (role XOR user)
        CONSTRAINT CK_TplPerm_Target CHECK ((RoleId IS NOT NULL AND UserId IS NULL) OR (RoleId IS NULL AND UserId IS NOT NULL))
    );
    CREATE UNIQUE INDEX UX_TplPerm_Role ON portal.ReportTemplatePermissions(TemplateId, RoleId) WHERE RoleId IS NOT NULL;
    CREATE UNIQUE INDEX UX_TplPerm_User ON portal.ReportTemplatePermissions(TemplateId, UserId) WHERE UserId IS NOT NULL;
END
GO

/* ---- 5.5 ReportTemplateVersions (lịch sử thay đổi SQL) ------------------ */
IF OBJECT_ID(N'portal.ReportTemplateVersions', N'U') IS NULL
BEGIN
    CREATE TABLE portal.ReportTemplateVersions
    (
        VersionId     INT            IDENTITY(1,1) NOT NULL,
        TemplateId    INT            NOT NULL,
        VersionNo     INT            NOT NULL,
        SqlCommand    NVARCHAR(MAX)  NOT NULL,
        Note          NVARCHAR(1000) NULL,
        ChangedAt     DATETIME2(3)   NOT NULL CONSTRAINT DF_TplVer_ChangedAt DEFAULT (SYSUTCDATETIME()),
        ChangedBy     INT            NULL,
        CONSTRAINT PK_ReportTemplateVersions PRIMARY KEY CLUSTERED (VersionId),
        CONSTRAINT FK_TplVer_Template FOREIGN KEY (TemplateId) REFERENCES portal.ReportTemplates(TemplateId) ON DELETE CASCADE,
        CONSTRAINT UQ_TplVer UNIQUE (TemplateId, VersionNo)
    );
END
GO

/* ============================================================================
   6. NHÓM BẢNG: LỊCH SỬ TRUY VẤN, XUẤT FILE & AUDIT
   ============================================================================ */

/* ---- 6.1 QueryExecutions ------------------------------------------------ */
IF OBJECT_ID(N'portal.QueryExecutions', N'U') IS NULL
BEGIN
    CREATE TABLE portal.QueryExecutions
    (
        ExecutionId  BIGINT         IDENTITY(1,1) NOT NULL,
        UserId       INT            NOT NULL,
        TemplateId   INT            NOT NULL,
        ConnectionId INT            NOT NULL,
        StartedAt    DATETIME2(3)   NOT NULL CONSTRAINT DF_Exec_StartedAt DEFAULT (SYSUTCDATETIME()),
        CompletedAt  DATETIME2(3)   NULL,
        DurationMs   INT            NULL,
        RowCountOut  INT            NULL,                 -- số dòng trả về
        Status       NVARCHAR(20)   NOT NULL CONSTRAINT DF_Exec_Status DEFAULT (N'Running'),
        ErrorMessage NVARCHAR(MAX)  NULL,
        ClientIP     NVARCHAR(64)   NULL,
        IsExported   BIT            NOT NULL CONSTRAINT DF_Exec_IsExported DEFAULT (0),
        CONSTRAINT PK_QueryExecutions PRIMARY KEY CLUSTERED (ExecutionId),
        CONSTRAINT FK_Exec_User       FOREIGN KEY (UserId)       REFERENCES portal.Users(UserId),
        CONSTRAINT FK_Exec_Template   FOREIGN KEY (TemplateId)   REFERENCES portal.ReportTemplates(TemplateId),
        CONSTRAINT FK_Exec_Connection FOREIGN KEY (ConnectionId) REFERENCES portal.DatabaseConnections(ConnectionId),
        CONSTRAINT CK_Exec_Status CHECK (Status IN (N'Running', N'Success', N'Failed', N'Timeout', N'Cancelled'))
    );
    CREATE INDEX IX_Exec_User     ON portal.QueryExecutions(UserId, StartedAt DESC);
    CREATE INDEX IX_Exec_Template ON portal.QueryExecutions(TemplateId, StartedAt DESC);
    CREATE INDEX IX_Exec_Started  ON portal.QueryExecutions(StartedAt DESC);
END
GO

/* ---- 6.2 QueryExecutionParameters --------------------------------------- */
IF OBJECT_ID(N'portal.QueryExecutionParameters', N'U') IS NULL
BEGIN
    CREATE TABLE portal.QueryExecutionParameters
    (
        Id             BIGINT         IDENTITY(1,1) NOT NULL,
        ExecutionId    BIGINT         NOT NULL,
        ParameterName  NVARCHAR(100)  NOT NULL,
        ParameterValue NVARCHAR(MAX)  NULL,
        CONSTRAINT PK_QueryExecutionParameters PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_ExecParam_Exec FOREIGN KEY (ExecutionId) REFERENCES portal.QueryExecutions(ExecutionId) ON DELETE CASCADE
    );
    CREATE INDEX IX_ExecParam_Exec ON portal.QueryExecutionParameters(ExecutionId);
END
GO

/* ---- 6.3 ExportHistories ------------------------------------------------ */
IF OBJECT_ID(N'portal.ExportHistories', N'U') IS NULL
BEGIN
    CREATE TABLE portal.ExportHistories
    (
        ExportId    BIGINT         IDENTITY(1,1) NOT NULL,
        ExecutionId BIGINT         NULL,
        UserId      INT            NOT NULL,
        FileName    NVARCHAR(400)  NOT NULL,
        Format      NVARCHAR(10)   NOT NULL CONSTRAINT DF_Export_Format DEFAULT (N'xlsx'),
        RowCountOut INT            NULL,
        FileSize    BIGINT         NULL,
        ExportedAt  DATETIME2(3)   NOT NULL CONSTRAINT DF_Export_ExportedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ExportHistories PRIMARY KEY CLUSTERED (ExportId),
        CONSTRAINT FK_Export_Exec FOREIGN KEY (ExecutionId) REFERENCES portal.QueryExecutions(ExecutionId),
        CONSTRAINT FK_Export_User FOREIGN KEY (UserId)      REFERENCES portal.Users(UserId),
        CONSTRAINT CK_Export_Format CHECK (Format IN (N'xlsx', N'csv'))
    );
    CREATE INDEX IX_Export_User ON portal.ExportHistories(UserId, ExportedAt DESC);
END
GO

/* ---- 6.4 AuditLogs ------------------------------------------------------ */
IF OBJECT_ID(N'portal.AuditLogs', N'U') IS NULL
BEGIN
    CREATE TABLE portal.AuditLogs
    (
        AuditId      BIGINT         IDENTITY(1,1) NOT NULL,
        UserId       INT            NULL,            -- người thực hiện (NULL nếu hệ thống)
        Action       NVARCHAR(100)  NOT NULL,        -- vd: USER.CREATE, CONNECTION.UPDATE
        TargetObject NVARCHAR(100)  NULL,            -- tên đối tượng bị tác động
        TargetId     NVARCHAR(100)  NULL,            -- khóa của đối tượng
        OldValue     NVARCHAR(MAX)  NULL,            -- KHÔNG ghi password/connection string
        NewValue     NVARCHAR(MAX)  NULL,
        ClientIP     NVARCHAR(64)   NULL,
        CreatedAt    DATETIME2(3)   NOT NULL CONSTRAINT DF_Audit_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_AuditLogs PRIMARY KEY CLUSTERED (AuditId),
        CONSTRAINT FK_Audit_User FOREIGN KEY (UserId) REFERENCES portal.Users(UserId)
    );
    CREATE INDEX IX_Audit_User   ON portal.AuditLogs(UserId, CreatedAt DESC);
    CREATE INDEX IX_Audit_Action ON portal.AuditLogs(Action, CreatedAt DESC);
END
GO

/* ============================================================================
   7. NHÓM BẢNG: CẤU HÌNH HỆ THỐNG
   ============================================================================ */

/* ---- 7.1 SystemSettings ------------------------------------------------- */
IF OBJECT_ID(N'portal.SystemSettings', N'U') IS NULL
BEGIN
    CREATE TABLE portal.SystemSettings
    (
        SettingId    INT            IDENTITY(1,1) NOT NULL,
        SettingKey   NVARCHAR(150)  NOT NULL,
        SettingValue NVARCHAR(MAX)  NULL,
        Description  NVARCHAR(400)  NULL,
        UpdatedAt    DATETIME2(3)   NOT NULL CONSTRAINT DF_Setting_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedBy    INT            NULL,
        CONSTRAINT PK_SystemSettings PRIMARY KEY CLUSTERED (SettingId),
        CONSTRAINT UQ_SystemSettings_Key UNIQUE (SettingKey)
    );
END
GO

/* ============================================================================
   8. DỮ LIỆU KHỞI TẠO (SEED) TỐI THIỂU
   - Vai trò, quyền, cấu hình mặc định và nhóm báo cáo mẫu.
   - Tài khoản admin dùng PasswordHash PLACEHOLDER: ứng dụng phải băm lại
     mật khẩu thật khi triển khai. KHÔNG dùng giá trị này ở Production.
   ============================================================================ */

/* ---- 8.1 Roles ---------------------------------------------------------- */
MERGE portal.Roles AS t
USING (VALUES
    (N'Admin',       N'Quản trị toàn bộ hệ thống'),
    (N'User',        N'Chạy các báo cáo được cấp quyền'),
    (N'ReportAdmin', N'Quản lý connection và SQL Template'),
    (N'Auditor',     N'Xem lịch sử truy vấn và nhật ký hệ thống')
) AS s(RoleName, Description)
    ON t.RoleName = s.RoleName
WHEN NOT MATCHED THEN
    INSERT (RoleName, Description) VALUES (s.RoleName, s.Description);
GO

/* ---- 8.2 Permissions ---------------------------------------------------- */
MERGE portal.Permissions AS t
USING (VALUES
    (N'USER.MANAGE',       N'Quản lý tài khoản người dùng'),
    (N'ROLE.MANAGE',       N'Quản lý vai trò và phân quyền'),
    (N'CONNECTION.MANAGE', N'Quản lý kết nối database'),
    (N'TEMPLATE.MANAGE',   N'Quản lý SQL Template và tham số'),
    (N'REPORT.RUN',        N'Chạy báo cáo được cấp quyền'),
    (N'REPORT.EXPORT',     N'Xuất dữ liệu ra Excel'),
    (N'HISTORY.VIEW.ALL',  N'Xem lịch sử truy vấn toàn hệ thống'),
    (N'AUDIT.VIEW',        N'Xem Audit Log')
) AS s(PermissionCode, Description)
    ON t.PermissionCode = s.PermissionCode
WHEN NOT MATCHED THEN
    INSERT (PermissionCode, Description) VALUES (s.PermissionCode, s.Description);
GO

/* ---- 8.3 Gán toàn quyền cho Admin -------------------------------------- */
INSERT INTO portal.RolePermissions (RoleId, PermissionId)
SELECT r.RoleId, p.PermissionId
FROM portal.Roles r
CROSS JOIN portal.Permissions p
WHERE r.RoleName = N'Admin'
  AND NOT EXISTS (SELECT 1 FROM portal.RolePermissions rp
                  WHERE rp.RoleId = r.RoleId AND rp.PermissionId = p.PermissionId);
GO

/* Quyền cho User: chạy & xuất báo cáo */
INSERT INTO portal.RolePermissions (RoleId, PermissionId)
SELECT r.RoleId, p.PermissionId
FROM portal.Roles r
JOIN portal.Permissions p ON p.PermissionCode IN (N'REPORT.RUN', N'REPORT.EXPORT')
WHERE r.RoleName = N'User'
  AND NOT EXISTS (SELECT 1 FROM portal.RolePermissions rp
                  WHERE rp.RoleId = r.RoleId AND rp.PermissionId = p.PermissionId);
GO

/* ---- 8.4 Tài khoản admin (placeholder hash - phải đổi khi triển khai) --- */
IF NOT EXISTS (SELECT 1 FROM portal.Users WHERE Username = N'admin')
BEGIN
    INSERT INTO portal.Users (Username, FullName, Email, Department, PasswordHash, IsActive)
    VALUES (N'admin', N'Quản trị hệ thống', N'admin@local', N'IT',
            N'PLACEHOLDER_SET_REAL_HASH_ON_DEPLOY', 1);

    INSERT INTO portal.UserRoles (UserId, RoleId)
    SELECT u.UserId, r.RoleId
    FROM portal.Users u, portal.Roles r
    WHERE u.Username = N'admin' AND r.RoleName = N'Admin';
END
GO

/* ---- 8.5 Nhóm báo cáo mẫu ---------------------------------------------- */
MERGE portal.ReportCategories AS t
USING (VALUES
    (N'Báo cáo doanh thu',   10),
    (N'Báo cáo giao dịch',   20),
    (N'Báo cáo tài khoản',   30),
    (N'Báo cáo khách hàng',  40),
    (N'Báo cáo kho',         50),
    (N'Báo cáo hệ thống',    60),
    (N'Báo cáo log',         70)
) AS s(CategoryName, DisplayOrder)
    ON t.CategoryName = s.CategoryName
WHEN NOT MATCHED THEN
    INSERT (CategoryName, DisplayOrder) VALUES (s.CategoryName, s.DisplayOrder);
GO

/* ---- 8.6 Cấu hình hệ thống mặc định ------------------------------------ */
MERGE portal.SystemSettings AS t
USING (VALUES
    (N'Export.MaxRows',          N'100000', N'Số dòng tối đa cho một lần xuất Excel'),
    (N'Query.DefaultTimeoutSec', N'30',     N'Timeout mặc định khi chạy truy vấn (giây)'),
    (N'Session.IdleTimeoutMin',  N'30',     N'Thời gian hết hạn phiên khi không hoạt động (phút)'),
    (N'Report.DefaultMaxRows',   N'10000',  N'Giới hạn số dòng mặc định cho báo cáo')
) AS s(SettingKey, SettingValue, Description)
    ON t.SettingKey = s.SettingKey
WHEN NOT MATCHED THEN
    INSERT (SettingKey, SettingValue, Description) VALUES (s.SettingKey, s.SettingValue, s.Description);
GO

/* ============================================================================
   9. SEED: CONNECTION & SQL TEMPLATE BÁO CÁO MẪU (WideWorldImporters)
   - Tạo 1 connection mẫu tới WideWorldImporters (mật khẩu placeholder).
   - Tạo 8 báo cáo mẫu + tham số theo đúng danh mục đặc tả.
   - Mọi câu lệnh chỉ SELECT và dùng parameter binding (@Param).
   - Idempotent: chạy lại sẽ bỏ qua template/tham số đã tồn tại.
   ============================================================================ */

/* ---- 9.0 Connection mẫu tới WideWorldImporters -------------------------- */
IF NOT EXISTS (SELECT 1 FROM portal.DatabaseConnections WHERE ConnectionName = N'WideWorldImporters')
BEGIN
    INSERT INTO portal.DatabaseConnections
        (ConnectionName, ServerHost, Port, DatabaseName, Username, PasswordEncrypted,
         Environment, ConnectionTimeout, CommandTimeout, IsActive, CreatedBy)
    VALUES
        (N'WideWorldImporters', N'localhost', 1433, N'WideWorldImporters', N'portal_readonly',
         N'PLACEHOLDER_SET_ENCRYPTED_PASSWORD_ON_DEPLOY', N'Development', 15, 60, 1,
         (SELECT UserId FROM portal.Users WHERE Username = N'admin'));
END
GO

/* ---- 9.1 Danh sách đơn hàng theo khoảng ngày ---------------------------- */
IF NOT EXISTS (SELECT 1 FROM portal.ReportTemplates WHERE TemplateCode = N'RPT_ORDERS_BY_DATE')
BEGIN
    INSERT INTO portal.ReportTemplates
        (TemplateCode, TemplateName, Description, CategoryId, ConnectionId, SqlCommand,
         MaxRows, TimeoutSeconds, AllowCopy, AllowExport, Status, CreatedBy)
    SELECT N'RPT_ORDERS_BY_DATE',
           N'Danh sách đơn hàng theo khoảng ngày',
           N'Liệt kê các đơn hàng có OrderDate trong khoảng [Từ ngày; Đến ngày].',
           (SELECT CategoryId FROM portal.ReportCategories WHERE CategoryName = N'Báo cáo giao dịch'),
           (SELECT ConnectionId FROM portal.DatabaseConnections WHERE ConnectionName = N'WideWorldImporters'),
           N'SELECT o.OrderID, o.OrderDate, o.CustomerID, c.CustomerName, o.ExpectedDeliveryDate, o.SalespersonPersonID
FROM Sales.Orders o
JOIN Sales.Customers c ON c.CustomerID = o.CustomerID
WHERE o.OrderDate >= @StartDate AND o.OrderDate < DATEADD(DAY, 1, @EndDate)
ORDER BY o.OrderDate;',
           50000, 30, 1, 1, N'Published',
           (SELECT UserId FROM portal.Users WHERE Username = N'admin');
END
GO
DECLARE @t INT = (SELECT TemplateId FROM portal.ReportTemplates WHERE TemplateCode = N'RPT_ORDERS_BY_DATE');
IF @t IS NOT NULL AND NOT EXISTS (SELECT 1 FROM portal.ReportTemplateParameters WHERE TemplateId = @t)
    INSERT INTO portal.ReportTemplateParameters
        (TemplateId, ParameterName, DisplayName, DataType, InputType, IsRequired, DefaultValue, DisplayOrder)
    VALUES
        (@t, N'@StartDate', N'Từ ngày', N'Date', N'Date', 1, NULL, 1),
        (@t, N'@EndDate',   N'Đến ngày', N'Date', N'Date', 1, NULL, 2);
GO

/* ---- 9.2 Danh sách đơn hàng theo khách hàng ----------------------------- */
IF NOT EXISTS (SELECT 1 FROM portal.ReportTemplates WHERE TemplateCode = N'RPT_ORDERS_BY_CUSTOMER')
BEGIN
    INSERT INTO portal.ReportTemplates
        (TemplateCode, TemplateName, Description, CategoryId, ConnectionId, SqlCommand,
         MaxRows, TimeoutSeconds, AllowCopy, AllowExport, Status, CreatedBy)
    SELECT N'RPT_ORDERS_BY_CUSTOMER',
           N'Danh sách đơn hàng theo khách hàng',
           N'Liệt kê đơn hàng theo tên khách hàng (LIKE) trong khoảng ngày.',
           (SELECT CategoryId FROM portal.ReportCategories WHERE CategoryName = N'Báo cáo giao dịch'),
           (SELECT ConnectionId FROM portal.DatabaseConnections WHERE ConnectionName = N'WideWorldImporters'),
           N'SELECT o.OrderID, o.OrderDate, c.CustomerName, o.CustomerID, o.SalespersonPersonID
FROM Sales.Orders o
JOIN Sales.Customers c ON c.CustomerID = o.CustomerID
WHERE c.CustomerName LIKE N''%'' + @CustomerName + N''%''
  AND o.OrderDate >= @StartDate AND o.OrderDate < DATEADD(DAY, 1, @EndDate)
ORDER BY o.OrderDate;',
           50000, 30, 1, 1, N'Published',
           (SELECT UserId FROM portal.Users WHERE Username = N'admin');
END
GO
DECLARE @t INT = (SELECT TemplateId FROM portal.ReportTemplates WHERE TemplateCode = N'RPT_ORDERS_BY_CUSTOMER');
IF @t IS NOT NULL AND NOT EXISTS (SELECT 1 FROM portal.ReportTemplateParameters WHERE TemplateId = @t)
    INSERT INTO portal.ReportTemplateParameters
        (TemplateId, ParameterName, DisplayName, DataType, InputType, IsRequired, DefaultValue, DisplayOrder, MaxLength)
    VALUES
        (@t, N'@CustomerName', N'Tên khách hàng', N'Text', N'Text', 1, NULL, 1, 100),
        (@t, N'@StartDate',    N'Từ ngày',  N'Date', N'Date', 1, NULL, 2, NULL),
        (@t, N'@EndDate',      N'Đến ngày', N'Date', N'Date', 1, NULL, 3, NULL);
GO

/* ---- 9.3 Doanh thu theo ngày -------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM portal.ReportTemplates WHERE TemplateCode = N'RPT_REVENUE_BY_DAY')
BEGIN
    INSERT INTO portal.ReportTemplates
        (TemplateCode, TemplateName, Description, CategoryId, ConnectionId, SqlCommand,
         MaxRows, TimeoutSeconds, AllowCopy, AllowExport, Status, CreatedBy)
    SELECT N'RPT_REVENUE_BY_DAY',
           N'Doanh thu theo ngày',
           N'Tổng doanh thu (ExtendedPrice) và số hóa đơn theo từng ngày lập hóa đơn.',
           (SELECT CategoryId FROM portal.ReportCategories WHERE CategoryName = N'Báo cáo doanh thu'),
           (SELECT ConnectionId FROM portal.DatabaseConnections WHERE ConnectionName = N'WideWorldImporters'),
           N'SELECT CAST(i.InvoiceDate AS date) AS InvoiceDate,
       COUNT(DISTINCT i.InvoiceID) AS InvoiceCount,
       SUM(il.ExtendedPrice)      AS Revenue
FROM Sales.Invoices i
JOIN Sales.InvoiceLines il ON il.InvoiceID = i.InvoiceID
WHERE i.InvoiceDate >= @StartDate AND i.InvoiceDate < DATEADD(DAY, 1, @EndDate)
GROUP BY CAST(i.InvoiceDate AS date)
ORDER BY InvoiceDate;',
           50000, 45, 1, 1, N'Published',
           (SELECT UserId FROM portal.Users WHERE Username = N'admin');
END
GO
DECLARE @t INT = (SELECT TemplateId FROM portal.ReportTemplates WHERE TemplateCode = N'RPT_REVENUE_BY_DAY');
IF @t IS NOT NULL AND NOT EXISTS (SELECT 1 FROM portal.ReportTemplateParameters WHERE TemplateId = @t)
    INSERT INTO portal.ReportTemplateParameters
        (TemplateId, ParameterName, DisplayName, DataType, InputType, IsRequired, DefaultValue, DisplayOrder)
    VALUES
        (@t, N'@StartDate', N'Từ ngày',  N'Date', N'Date', 1, NULL, 1),
        (@t, N'@EndDate',   N'Đến ngày', N'Date', N'Date', 1, NULL, 2);
GO

/* ---- 9.4 Giao dịch khách hàng ------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM portal.ReportTemplates WHERE TemplateCode = N'RPT_CUSTOMER_TRANSACTIONS')
BEGIN
    INSERT INTO portal.ReportTemplates
        (TemplateCode, TemplateName, Description, CategoryId, ConnectionId, SqlCommand,
         MaxRows, TimeoutSeconds, AllowCopy, AllowExport, Status, CreatedBy)
    SELECT N'RPT_CUSTOMER_TRANSACTIONS',
           N'Giao dịch khách hàng',
           N'Danh sách giao dịch của khách hàng theo khoảng ngày; có thể lọc theo tên khách hàng.',
           (SELECT CategoryId FROM portal.ReportCategories WHERE CategoryName = N'Báo cáo khách hàng'),
           (SELECT ConnectionId FROM portal.DatabaseConnections WHERE ConnectionName = N'WideWorldImporters'),
           N'SELECT ct.CustomerTransactionID, c.CustomerName, ct.TransactionDate,
       tt.TransactionTypeName, ct.TransactionAmount, ct.OutstandingBalance
FROM Sales.CustomerTransactions ct
JOIN Sales.Customers c          ON c.CustomerID = ct.CustomerID
JOIN Application.TransactionTypes tt ON tt.TransactionTypeID = ct.TransactionTypeID
WHERE ct.TransactionDate >= @StartDate AND ct.TransactionDate < DATEADD(DAY, 1, @EndDate)
  AND (@CustomerName IS NULL OR c.CustomerName LIKE N''%'' + @CustomerName + N''%'')
ORDER BY ct.TransactionDate;',
           50000, 45, 1, 1, N'Published',
           (SELECT UserId FROM portal.Users WHERE Username = N'admin');
END
GO
DECLARE @t INT = (SELECT TemplateId FROM portal.ReportTemplates WHERE TemplateCode = N'RPT_CUSTOMER_TRANSACTIONS');
IF @t IS NOT NULL AND NOT EXISTS (SELECT 1 FROM portal.ReportTemplateParameters WHERE TemplateId = @t)
    INSERT INTO portal.ReportTemplateParameters
        (TemplateId, ParameterName, DisplayName, DataType, InputType, IsRequired, DefaultValue, DisplayOrder, MaxLength)
    VALUES
        (@t, N'@StartDate',    N'Từ ngày',       N'Date', N'Date', 1, NULL, 1, NULL),
        (@t, N'@EndDate',      N'Đến ngày',      N'Date', N'Date', 1, NULL, 2, NULL),
        (@t, N'@CustomerName', N'Tên khách hàng', N'Text', N'Text', 0, NULL, 3, 100);
GO

/* ---- 9.5 Tồn kho sản phẩm ----------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM portal.ReportTemplates WHERE TemplateCode = N'RPT_STOCK_ON_HAND')
BEGIN
    INSERT INTO portal.ReportTemplates
        (TemplateCode, TemplateName, Description, CategoryId, ConnectionId, SqlCommand,
         MaxRows, TimeoutSeconds, AllowCopy, AllowExport, Status, CreatedBy)
    SELECT N'RPT_STOCK_ON_HAND',
           N'Tồn kho sản phẩm',
           N'Số lượng tồn kho hiện tại theo sản phẩm; có thể lọc theo tên sản phẩm.',
           (SELECT CategoryId FROM portal.ReportCategories WHERE CategoryName = N'Báo cáo kho'),
           (SELECT ConnectionId FROM portal.DatabaseConnections WHERE ConnectionName = N'WideWorldImporters'),
           N'SELECT si.StockItemID, si.StockItemName, sih.QuantityOnHand,
       sih.ReorderLevel, sih.TargetStockLevel, sih.LastStocktakeQuantity, sih.BinLocation
FROM Warehouse.StockItems si
JOIN Warehouse.StockItemHoldings sih ON sih.StockItemID = si.StockItemID
WHERE (@StockItemName IS NULL OR si.StockItemName LIKE N''%'' + @StockItemName + N''%'')
ORDER BY si.StockItemName;',
           50000, 30, 1, 1, N'Published',
           (SELECT UserId FROM portal.Users WHERE Username = N'admin');
END
GO
DECLARE @t INT = (SELECT TemplateId FROM portal.ReportTemplates WHERE TemplateCode = N'RPT_STOCK_ON_HAND');
IF @t IS NOT NULL AND NOT EXISTS (SELECT 1 FROM portal.ReportTemplateParameters WHERE TemplateId = @t)
    INSERT INTO portal.ReportTemplateParameters
        (TemplateId, ParameterName, DisplayName, DataType, InputType, IsRequired, DefaultValue, DisplayOrder, MaxLength)
    VALUES
        (@t, N'@StockItemName', N'Tên sản phẩm', N'Text', N'Text', 0, NULL, 1, 100);
GO

/* ---- 9.6 Danh sách sản phẩm cần nhập thêm ------------------------------- */
IF NOT EXISTS (SELECT 1 FROM portal.ReportTemplates WHERE TemplateCode = N'RPT_STOCK_REORDER')
BEGIN
    INSERT INTO portal.ReportTemplates
        (TemplateCode, TemplateName, Description, CategoryId, ConnectionId, SqlCommand,
         MaxRows, TimeoutSeconds, AllowCopy, AllowExport, Status, CreatedBy)
    SELECT N'RPT_STOCK_REORDER',
           N'Danh sách sản phẩm cần nhập thêm',
           N'Các sản phẩm có tồn kho (QuantityOnHand) thấp hơn hoặc bằng mức đặt lại (ReorderLevel).',
           (SELECT CategoryId FROM portal.ReportCategories WHERE CategoryName = N'Báo cáo kho'),
           (SELECT ConnectionId FROM portal.DatabaseConnections WHERE ConnectionName = N'WideWorldImporters'),
           N'SELECT si.StockItemID, si.StockItemName, sih.QuantityOnHand, sih.ReorderLevel,
       sih.TargetStockLevel,
       (sih.TargetStockLevel - sih.QuantityOnHand) AS SuggestedReorderQty
FROM Warehouse.StockItems si
JOIN Warehouse.StockItemHoldings sih ON sih.StockItemID = si.StockItemID
WHERE sih.QuantityOnHand <= sih.ReorderLevel
ORDER BY sih.QuantityOnHand ASC;',
           50000, 30, 1, 1, N'Published',
           (SELECT UserId FROM portal.Users WHERE Username = N'admin');
END
GO
-- Báo cáo này không có tham số đầu vào.

/* ---- 9.7 Log nhiệt độ theo thời gian ------------------------------------ */
IF NOT EXISTS (SELECT 1 FROM portal.ReportTemplates WHERE TemplateCode = N'RPT_COLDROOM_TEMP_LOG')
BEGIN
    INSERT INTO portal.ReportTemplates
        (TemplateCode, TemplateName, Description, CategoryId, ConnectionId, SqlCommand,
         MaxRows, TimeoutSeconds, AllowCopy, AllowExport, Status, CreatedBy)
    SELECT N'RPT_COLDROOM_TEMP_LOG',
           N'Log nhiệt độ theo thời gian',
           N'Dữ liệu nhiệt độ kho lạnh trong khoảng thời gian; có thể lọc theo số cảm biến.',
           (SELECT CategoryId FROM portal.ReportCategories WHERE CategoryName = N'Báo cáo log'),
           (SELECT ConnectionId FROM portal.DatabaseConnections WHERE ConnectionName = N'WideWorldImporters'),
           N'SELECT crt.ColdRoomTemperatureID, crt.ColdRoomSensorNumber, crt.RecordedWhen, crt.Temperature
FROM Warehouse.ColdRoomTemperatures crt
WHERE crt.RecordedWhen >= @StartDateTime AND crt.RecordedWhen < @EndDateTime
  AND (@SensorNumber IS NULL OR crt.ColdRoomSensorNumber = @SensorNumber)
ORDER BY crt.RecordedWhen;',
           100000, 60, 1, 1, N'Published',
           (SELECT UserId FROM portal.Users WHERE Username = N'admin');
END
GO
DECLARE @t INT = (SELECT TemplateId FROM portal.ReportTemplates WHERE TemplateCode = N'RPT_COLDROOM_TEMP_LOG');
IF @t IS NOT NULL AND NOT EXISTS (SELECT 1 FROM portal.ReportTemplateParameters WHERE TemplateId = @t)
    INSERT INTO portal.ReportTemplateParameters
        (TemplateId, ParameterName, DisplayName, DataType, InputType, IsRequired, DefaultValue, DisplayOrder, MinValue)
    VALUES
        (@t, N'@StartDateTime', N'Từ thời điểm',  N'DateTime', N'DateTime', 1, NULL, 1, NULL),
        (@t, N'@EndDateTime',   N'Đến thời điểm', N'DateTime', N'DateTime', 1, NULL, 2, NULL),
        (@t, N'@SensorNumber',  N'Số cảm biến',   N'Integer',  N'Number',   0, NULL, 3, N'1');
GO

/* ---- 9.8 Danh sách dữ liệu nhiệt độ bất thường -------------------------- */
IF NOT EXISTS (SELECT 1 FROM portal.ReportTemplates WHERE TemplateCode = N'RPT_COLDROOM_TEMP_ANOMALY')
BEGIN
    INSERT INTO portal.ReportTemplates
        (TemplateCode, TemplateName, Description, CategoryId, ConnectionId, SqlCommand,
         MaxRows, TimeoutSeconds, AllowCopy, AllowExport, Status, CreatedBy)
    SELECT N'RPT_COLDROOM_TEMP_ANOMALY',
           N'Danh sách dữ liệu nhiệt độ bất thường',
           N'Các bản ghi có nhiệt độ nằm ngoài ngưỡng cho phép [MinTemp; MaxTemp] trong khoảng thời gian.',
           (SELECT CategoryId FROM portal.ReportCategories WHERE CategoryName = N'Báo cáo log'),
           (SELECT ConnectionId FROM portal.DatabaseConnections WHERE ConnectionName = N'WideWorldImporters'),
           N'SELECT crt.ColdRoomSensorNumber, crt.RecordedWhen, crt.Temperature
FROM Warehouse.ColdRoomTemperatures crt
WHERE crt.RecordedWhen >= @StartDateTime AND crt.RecordedWhen < @EndDateTime
  AND (crt.Temperature < @MinTemp OR crt.Temperature > @MaxTemp)
ORDER BY crt.RecordedWhen;',
           100000, 60, 1, 1, N'Published',
           (SELECT UserId FROM portal.Users WHERE Username = N'admin');
END
GO
DECLARE @t INT = (SELECT TemplateId FROM portal.ReportTemplates WHERE TemplateCode = N'RPT_COLDROOM_TEMP_ANOMALY');
IF @t IS NOT NULL AND NOT EXISTS (SELECT 1 FROM portal.ReportTemplateParameters WHERE TemplateId = @t)
    INSERT INTO portal.ReportTemplateParameters
        (TemplateId, ParameterName, DisplayName, DataType, InputType, IsRequired, DefaultValue, DisplayOrder)
    VALUES
        (@t, N'@StartDateTime', N'Từ thời điểm',      N'DateTime', N'DateTime', 1, NULL,   1),
        (@t, N'@EndDateTime',   N'Đến thời điểm',     N'DateTime', N'DateTime', 1, NULL,   2),
        (@t, N'@MinTemp',       N'Nhiệt độ tối thiểu', N'Decimal',  N'Number',   1, N'2.0', 3),
        (@t, N'@MaxTemp',       N'Nhiệt độ tối đa',    N'Decimal',  N'Number',   1, N'4.0', 4);
GO

/* ---- 9.9 Cấp quyền cho các báo cáo mẫu ---------------------------------- */
-- Cấp toàn bộ báo cáo mẫu cho vai trò User (demo).
INSERT INTO portal.ReportTemplatePermissions (TemplateId, RoleId, GrantedBy)
SELECT rt.TemplateId, r.RoleId, (SELECT UserId FROM portal.Users WHERE Username = N'admin')
FROM portal.ReportTemplates rt
CROSS JOIN portal.Roles r
WHERE r.RoleName = N'User'
  AND rt.TemplateCode IN (N'RPT_ORDERS_BY_DATE', N'RPT_ORDERS_BY_CUSTOMER', N'RPT_REVENUE_BY_DAY',
                          N'RPT_CUSTOMER_TRANSACTIONS', N'RPT_STOCK_ON_HAND', N'RPT_STOCK_REORDER',
                          N'RPT_COLDROOM_TEMP_LOG', N'RPT_COLDROOM_TEMP_ANOMALY')
  AND NOT EXISTS (SELECT 1 FROM portal.ReportTemplatePermissions p
                  WHERE p.TemplateId = rt.TemplateId AND p.RoleId = r.RoleId);
GO

PRINT N'>>> DBLogReportPortal: tạo schema, seed dữ liệu và báo cáo mẫu hoàn tất.';
GO
