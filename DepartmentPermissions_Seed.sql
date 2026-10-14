/* ============================================================================
   SEED PHÂN QUYỀN THEO PHÒNG BAN - Portal Log Report
   Nội dung:
     - Role theo phòng ban: Sales_User, Warehouse_User, Purchasing_User, Manager
     - User mẫu: sales01, warehouse01, purchasing01, manager01
     - Nhóm báo cáo bổ sung: "Báo cáo mua hàng"
     - 6 SQL Template mở rộng (trên WideWorldImporters)
     - Cấp quyền: role phòng ban -> báo cáo tương ứng; Manager xem tất cả
   Yêu cầu: đã chạy DBLogReportPortal_Schema.sql trước đó.
   Idempotent: chạy lại sẽ bỏ qua bản ghi đã tồn tại.

   Ghi chú phạm vi: một số "báo cáo mở rộng" trong mô tả (tổng hợp chi phí,
   bảng cân đối số phát sinh, tình trạng hàng hỏng/lỗi) KHÔNG có dữ liệu tương
   ứng trong WideWorldImporters nên chưa tạo template; sẽ bổ sung khi có CSDL
   nghiệp vụ thực tế.
   ============================================================================ */

SET NOCOUNT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

USE [DBLogReportPortal];
GO

/* ---- 1. Role theo phòng ban --------------------------------------------- */
MERGE portal.Roles AS t
USING (VALUES
    (N'Sales_User',      N'Nhân viên phòng Kinh doanh - báo cáo bán hàng, doanh thu, khách hàng'),
    (N'Warehouse_User',  N'Nhân viên phòng Kho bãi - tồn kho, nhiệt độ kho lạnh, xuất nhập kho'),
    (N'Purchasing_User', N'Nhân viên phòng Mua hàng - đơn đặt hàng NCC, nhà cung cấp'),
    (N'Manager',         N'Ban Giám đốc/Quản lý - xem toàn bộ báo cáo nghiệp vụ')
) AS s(RoleName, Description)
    ON t.RoleName = s.RoleName
WHEN NOT MATCHED THEN
    INSERT (RoleName, Description) VALUES (s.RoleName, s.Description);
GO

/* ---- 2. Quyền chạy & xuất báo cáo cho các role phòng ban ---------------- */
INSERT INTO portal.RolePermissions (RoleId, PermissionId)
SELECT r.RoleId, p.PermissionId
FROM portal.Roles r
JOIN portal.Permissions p ON p.PermissionCode IN (N'REPORT.RUN', N'REPORT.EXPORT')
WHERE r.RoleName IN (N'Sales_User', N'Warehouse_User', N'Purchasing_User', N'Manager')
  AND NOT EXISTS (SELECT 1 FROM portal.RolePermissions rp
                  WHERE rp.RoleId = r.RoleId AND rp.PermissionId = p.PermissionId);
GO
-- Manager được xem lịch sử truy vấn toàn hệ thống
INSERT INTO portal.RolePermissions (RoleId, PermissionId)
SELECT r.RoleId, p.PermissionId
FROM portal.Roles r
JOIN portal.Permissions p ON p.PermissionCode = N'HISTORY.VIEW.ALL'
WHERE r.RoleName = N'Manager'
  AND NOT EXISTS (SELECT 1 FROM portal.RolePermissions rp
                  WHERE rp.RoleId = r.RoleId AND rp.PermissionId = p.PermissionId);
GO

/* ---- 3. User mẫu theo phòng ban (PasswordHash placeholder) -------------- */
MERGE portal.Users AS t
USING (VALUES
    (N'sales01',      N'Nhân viên Kinh doanh', N'sales01@local',      N'Phòng Kinh doanh'),
    (N'warehouse01',  N'Nhân viên Kho bãi',    N'warehouse01@local',  N'Phòng Kho bãi'),
    (N'purchasing01', N'Nhân viên Mua hàng',   N'purchasing01@local', N'Phòng Mua hàng'),
    (N'manager01',    N'Giám đốc điều hành',   N'manager01@local',    N'Ban Giám đốc')
) AS s(Username, FullName, Email, Department)
    ON t.Username = s.Username
WHEN NOT MATCHED THEN
    INSERT (Username, FullName, Email, Department, PasswordHash, IsActive)
    VALUES (s.Username, s.FullName, s.Email, s.Department, N'PLACEHOLDER_SET_REAL_HASH_ON_DEPLOY', 1);
GO

/* ---- 4. Gán user vào role phòng ban ------------------------------------- */
INSERT INTO portal.UserRoles (UserId, RoleId)
SELECT u.UserId, r.RoleId
FROM (VALUES
    (N'sales01',      N'Sales_User'),
    (N'warehouse01',  N'Warehouse_User'),
    (N'purchasing01', N'Purchasing_User'),
    (N'manager01',    N'Manager')
) AS m(Username, RoleName)
JOIN portal.Users u ON u.Username = m.Username
JOIN portal.Roles r ON r.RoleName = m.RoleName
WHERE NOT EXISTS (SELECT 1 FROM portal.UserRoles ur WHERE ur.UserId = u.UserId AND ur.RoleId = r.RoleId);
GO

/* ---- 5. Nhóm báo cáo bổ sung -------------------------------------------- */
MERGE portal.ReportCategories AS t
USING (VALUES (N'Báo cáo mua hàng', 80)) AS s(CategoryName, DisplayOrder)
    ON t.CategoryName = s.CategoryName
WHEN NOT MATCHED THEN INSERT (CategoryName, DisplayOrder) VALUES (s.CategoryName, s.DisplayOrder);
GO

/* ============================================================================
   6. SQL TEMPLATE MỞ RỘNG
   ============================================================================ */

/* ---- 6.1 Thống kê khách hàng theo khu vực (Sales) ----------------------- */
IF NOT EXISTS (SELECT 1 FROM portal.ReportTemplates WHERE TemplateCode = N'RPT_CUSTOMER_BY_REGION')
    INSERT INTO portal.ReportTemplates
        (TemplateCode, TemplateName, Description, CategoryId, ConnectionId, SqlCommand,
         MaxRows, TimeoutSeconds, AllowCopy, AllowExport, Status, CreatedBy)
    SELECT N'RPT_CUSTOMER_BY_REGION', N'Thống kê khách hàng theo khu vực',
           N'Số lượng khách hàng theo vùng bán hàng (SalesTerritory) và tỉnh/bang.',
           (SELECT CategoryId FROM portal.ReportCategories WHERE CategoryName = N'Báo cáo khách hàng'),
           (SELECT ConnectionId FROM portal.DatabaseConnections WHERE ConnectionName = N'WideWorldImporters'),
           N'SELECT sp.SalesTerritory, sp.StateProvinceName, COUNT(*) AS CustomerCount
FROM Sales.Customers c
JOIN Application.Cities ci         ON ci.CityID = c.DeliveryCityID
JOIN Application.StateProvinces sp ON sp.StateProvinceID = ci.StateProvinceID
GROUP BY sp.SalesTerritory, sp.StateProvinceName
ORDER BY CustomerCount DESC;',
           50000, 30, 1, 1, N'Published', (SELECT UserId FROM portal.Users WHERE Username = N'admin');
GO

/* ---- 6.2 Top sản phẩm bán chạy theo khoảng ngày (Sales) ----------------- */
IF NOT EXISTS (SELECT 1 FROM portal.ReportTemplates WHERE TemplateCode = N'RPT_TOP_PRODUCTS')
    INSERT INTO portal.ReportTemplates
        (TemplateCode, TemplateName, Description, CategoryId, ConnectionId, SqlCommand,
         MaxRows, TimeoutSeconds, AllowCopy, AllowExport, Status, CreatedBy)
    SELECT N'RPT_TOP_PRODUCTS', N'Top sản phẩm bán chạy nhất',
           N'Top N sản phẩm theo số lượng bán (Quantity) trong khoảng ngày.',
           (SELECT CategoryId FROM portal.ReportCategories WHERE CategoryName = N'Báo cáo doanh thu'),
           (SELECT ConnectionId FROM portal.DatabaseConnections WHERE ConnectionName = N'WideWorldImporters'),
           N'SELECT TOP (@TopN) si.StockItemID, si.StockItemName,
       SUM(il.Quantity)      AS TotalQty,
       SUM(il.ExtendedPrice) AS Revenue
FROM Sales.InvoiceLines il
JOIN Sales.Invoices i        ON i.InvoiceID = il.InvoiceID
JOIN Warehouse.StockItems si ON si.StockItemID = il.StockItemID
WHERE i.InvoiceDate >= @StartDate AND i.InvoiceDate < DATEADD(DAY, 1, @EndDate)
GROUP BY si.StockItemID, si.StockItemName
ORDER BY TotalQty DESC;',
           50000, 45, 1, 1, N'Published', (SELECT UserId FROM portal.Users WHERE Username = N'admin');
GO
DECLARE @t INT = (SELECT TemplateId FROM portal.ReportTemplates WHERE TemplateCode = N'RPT_TOP_PRODUCTS');
IF @t IS NOT NULL AND NOT EXISTS (SELECT 1 FROM portal.ReportTemplateParameters WHERE TemplateId = @t)
    INSERT INTO portal.ReportTemplateParameters
        (TemplateId, ParameterName, DisplayName, DataType, InputType, IsRequired, DefaultValue, DisplayOrder, MinValue, MaxValue)
    VALUES
        (@t, N'@StartDate', N'Từ ngày',  N'Date',    N'Date',   1, NULL,   1, NULL, NULL),
        (@t, N'@EndDate',   N'Đến ngày', N'Date',    N'Date',   1, NULL,   2, NULL, NULL),
        (@t, N'@TopN',      N'Số sản phẩm (Top)', N'Integer', N'Number', 1, N'10', 3, N'1', N'100');
GO

/* ---- 6.3 Lịch sử xuất/nhập kho theo sản phẩm (Warehouse) ---------------- */
IF NOT EXISTS (SELECT 1 FROM portal.ReportTemplates WHERE TemplateCode = N'RPT_STOCK_TRANSACTIONS')
    INSERT INTO portal.ReportTemplates
        (TemplateCode, TemplateName, Description, CategoryId, ConnectionId, SqlCommand,
         MaxRows, TimeoutSeconds, AllowCopy, AllowExport, Status, CreatedBy)
    SELECT N'RPT_STOCK_TRANSACTIONS', N'Lịch sử xuất/nhập kho theo sản phẩm',
           N'Giao dịch kho theo thời gian; có thể lọc theo tên sản phẩm.',
           (SELECT CategoryId FROM portal.ReportCategories WHERE CategoryName = N'Báo cáo kho'),
           (SELECT ConnectionId FROM portal.DatabaseConnections WHERE ConnectionName = N'WideWorldImporters'),
           N'SELECT sit.StockItemTransactionID, si.StockItemName, tt.TransactionTypeName,
       sit.TransactionOccurredWhen, sit.Quantity
FROM Warehouse.StockItemTransactions sit
JOIN Warehouse.StockItems si         ON si.StockItemID = sit.StockItemID
JOIN Application.TransactionTypes tt  ON tt.TransactionTypeID = sit.TransactionTypeID
WHERE sit.TransactionOccurredWhen >= @StartDate AND sit.TransactionOccurredWhen < DATEADD(DAY, 1, @EndDate)
  AND (@StockItemName IS NULL OR si.StockItemName LIKE N''%'' + @StockItemName + N''%'')
ORDER BY sit.TransactionOccurredWhen;',
           100000, 60, 1, 1, N'Published', (SELECT UserId FROM portal.Users WHERE Username = N'admin');
GO
DECLARE @t INT = (SELECT TemplateId FROM portal.ReportTemplates WHERE TemplateCode = N'RPT_STOCK_TRANSACTIONS');
IF @t IS NOT NULL AND NOT EXISTS (SELECT 1 FROM portal.ReportTemplateParameters WHERE TemplateId = @t)
    INSERT INTO portal.ReportTemplateParameters
        (TemplateId, ParameterName, DisplayName, DataType, InputType, IsRequired, DefaultValue, DisplayOrder, MaxLength)
    VALUES
        (@t, N'@StartDate',     N'Từ ngày',     N'Date', N'Date', 1, NULL, 1, NULL),
        (@t, N'@EndDate',       N'Đến ngày',    N'Date', N'Date', 1, NULL, 2, NULL),
        (@t, N'@StockItemName', N'Tên sản phẩm', N'Text', N'Text', 0, NULL, 3, 100);
GO

/* ---- 6.4 Đơn đặt hàng nhà cung cấp theo thời gian (Purchasing) ---------- */
IF NOT EXISTS (SELECT 1 FROM portal.ReportTemplates WHERE TemplateCode = N'RPT_PURCHASE_ORDERS')
    INSERT INTO portal.ReportTemplates
        (TemplateCode, TemplateName, Description, CategoryId, ConnectionId, SqlCommand,
         MaxRows, TimeoutSeconds, AllowCopy, AllowExport, Status, CreatedBy)
    SELECT N'RPT_PURCHASE_ORDERS', N'Đơn đặt hàng nhà cung cấp theo thời gian',
           N'Danh sách purchase order theo khoảng ngày đặt hàng.',
           (SELECT CategoryId FROM portal.ReportCategories WHERE CategoryName = N'Báo cáo mua hàng'),
           (SELECT ConnectionId FROM portal.DatabaseConnections WHERE ConnectionName = N'WideWorldImporters'),
           N'SELECT po.PurchaseOrderID, po.OrderDate, s.SupplierName,
       po.ExpectedDeliveryDate, po.IsOrderFinalized
FROM Purchasing.PurchaseOrders po
JOIN Purchasing.Suppliers s ON s.SupplierID = po.SupplierID
WHERE po.OrderDate >= @StartDate AND po.OrderDate < DATEADD(DAY, 1, @EndDate)
ORDER BY po.OrderDate;',
           50000, 30, 1, 1, N'Published', (SELECT UserId FROM portal.Users WHERE Username = N'admin');
GO
DECLARE @t INT = (SELECT TemplateId FROM portal.ReportTemplates WHERE TemplateCode = N'RPT_PURCHASE_ORDERS');
IF @t IS NOT NULL AND NOT EXISTS (SELECT 1 FROM portal.ReportTemplateParameters WHERE TemplateId = @t)
    INSERT INTO portal.ReportTemplateParameters
        (TemplateId, ParameterName, DisplayName, DataType, InputType, IsRequired, DefaultValue, DisplayOrder)
    VALUES
        (@t, N'@StartDate', N'Từ ngày',  N'Date', N'Date', 1, NULL, 1),
        (@t, N'@EndDate',   N'Đến ngày', N'Date', N'Date', 1, NULL, 2);
GO

/* ---- 6.5 Danh sách nhà cung cấp (Purchasing) --------------------------- */
IF NOT EXISTS (SELECT 1 FROM portal.ReportTemplates WHERE TemplateCode = N'RPT_SUPPLIERS')
    INSERT INTO portal.ReportTemplates
        (TemplateCode, TemplateName, Description, CategoryId, ConnectionId, SqlCommand,
         MaxRows, TimeoutSeconds, AllowCopy, AllowExport, Status, CreatedBy)
    SELECT N'RPT_SUPPLIERS', N'Danh sách nhà cung cấp',
           N'Danh sách nhà cung cấp kèm nhóm, điện thoại, website.',
           (SELECT CategoryId FROM portal.ReportCategories WHERE CategoryName = N'Báo cáo mua hàng'),
           (SELECT ConnectionId FROM portal.DatabaseConnections WHERE ConnectionName = N'WideWorldImporters'),
           N'SELECT s.SupplierID, s.SupplierName, sc.SupplierCategoryName, s.PhoneNumber, s.WebsiteURL
FROM Purchasing.Suppliers s
JOIN Purchasing.SupplierCategories sc ON sc.SupplierCategoryID = s.SupplierCategoryID
ORDER BY s.SupplierName;',
           50000, 30, 1, 1, N'Published', (SELECT UserId FROM portal.Users WHERE Username = N'admin');
GO

/* ---- 6.6 Thống kê hàng nhập theo nhà cung cấp (Purchasing) ------------- */
IF NOT EXISTS (SELECT 1 FROM portal.ReportTemplates WHERE TemplateCode = N'RPT_PURCHASE_BY_SUPPLIER')
    INSERT INTO portal.ReportTemplates
        (TemplateCode, TemplateName, Description, CategoryId, ConnectionId, SqlCommand,
         MaxRows, TimeoutSeconds, AllowCopy, AllowExport, Status, CreatedBy)
    SELECT N'RPT_PURCHASE_BY_SUPPLIER', N'Thống kê hàng nhập theo nhà cung cấp',
           N'Số đơn và tổng số lượng nhận (ReceivedOuters) theo nhà cung cấp trong khoảng ngày.',
           (SELECT CategoryId FROM portal.ReportCategories WHERE CategoryName = N'Báo cáo mua hàng'),
           (SELECT ConnectionId FROM portal.DatabaseConnections WHERE ConnectionName = N'WideWorldImporters'),
           N'SELECT s.SupplierName,
       COUNT(DISTINCT po.PurchaseOrderID) AS OrderCount,
       SUM(pol.ReceivedOuters)            AS TotalReceived
FROM Purchasing.PurchaseOrders po
JOIN Purchasing.Suppliers s          ON s.SupplierID = po.SupplierID
JOIN Purchasing.PurchaseOrderLines pol ON pol.PurchaseOrderID = po.PurchaseOrderID
WHERE po.OrderDate >= @StartDate AND po.OrderDate < DATEADD(DAY, 1, @EndDate)
GROUP BY s.SupplierName
ORDER BY TotalReceived DESC;',
           50000, 45, 1, 1, N'Published', (SELECT UserId FROM portal.Users WHERE Username = N'admin');
GO
DECLARE @t INT = (SELECT TemplateId FROM portal.ReportTemplates WHERE TemplateCode = N'RPT_PURCHASE_BY_SUPPLIER');
IF @t IS NOT NULL AND NOT EXISTS (SELECT 1 FROM portal.ReportTemplateParameters WHERE TemplateId = @t)
    INSERT INTO portal.ReportTemplateParameters
        (TemplateId, ParameterName, DisplayName, DataType, InputType, IsRequired, DefaultValue, DisplayOrder)
    VALUES
        (@t, N'@StartDate', N'Từ ngày',  N'Date', N'Date', 1, NULL, 1),
        (@t, N'@EndDate',   N'Đến ngày', N'Date', N'Date', 1, NULL, 2);
GO

/* ============================================================================
   7. CẤP QUYỀN: ROLE PHÒNG BAN -> BÁO CÁO
   ============================================================================ */

-- Bảng ánh xạ role <-> template
IF OBJECT_ID('tempdb..#Map') IS NOT NULL DROP TABLE #Map;
CREATE TABLE #Map (RoleName SYSNAME, TemplateCode NVARCHAR(100));

INSERT INTO #Map (RoleName, TemplateCode) VALUES
 -- Phòng Kinh doanh
 (N'Sales_User', N'RPT_ORDERS_BY_DATE'),
 (N'Sales_User', N'RPT_ORDERS_BY_CUSTOMER'),
 (N'Sales_User', N'RPT_REVENUE_BY_DAY'),
 (N'Sales_User', N'RPT_CUSTOMER_TRANSACTIONS'),
 (N'Sales_User', N'RPT_CUSTOMER_BY_REGION'),
 (N'Sales_User', N'RPT_TOP_PRODUCTS'),
 -- Phòng Kho bãi
 (N'Warehouse_User', N'RPT_STOCK_ON_HAND'),
 (N'Warehouse_User', N'RPT_STOCK_REORDER'),
 (N'Warehouse_User', N'RPT_COLDROOM_TEMP_LOG'),
 (N'Warehouse_User', N'RPT_COLDROOM_TEMP_ANOMALY'),
 (N'Warehouse_User', N'RPT_STOCK_TRANSACTIONS'),
 -- Phòng Mua hàng
 (N'Purchasing_User', N'RPT_PURCHASE_ORDERS'),
 (N'Purchasing_User', N'RPT_SUPPLIERS'),
 (N'Purchasing_User', N'RPT_PURCHASE_BY_SUPPLIER');

-- Manager: xem tất cả báo cáo nghiệp vụ (union mọi template ở trên)
INSERT INTO #Map (RoleName, TemplateCode)
SELECT DISTINCT N'Manager', TemplateCode FROM #Map;

-- Thực hiện cấp quyền (bỏ qua nếu đã có)
INSERT INTO portal.ReportTemplatePermissions (TemplateId, RoleId, GrantedBy)
SELECT rt.TemplateId, r.RoleId, (SELECT UserId FROM portal.Users WHERE Username = N'admin')
FROM #Map m
JOIN portal.Roles r            ON r.RoleName = m.RoleName
JOIN portal.ReportTemplates rt ON rt.TemplateCode = m.TemplateCode
WHERE NOT EXISTS (SELECT 1 FROM portal.ReportTemplatePermissions p
                  WHERE p.TemplateId = rt.TemplateId AND p.RoleId = r.RoleId);

DROP TABLE #Map;
GO

PRINT N'>>> Da seed phan quyen theo phong ban hoan tat.';
GO
