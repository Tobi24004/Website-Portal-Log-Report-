# Website Portal Log Report

Ứng dụng web nội bộ để **chạy báo cáo theo mẫu SQL** có tham số, hiển thị kết quả,
**xuất Excel**, kèm **phân quyền theo phòng ban** và **ghi nhật ký / audit**.

- **Backend:** ASP.NET Core 8 Web API (C#), Dapper + Microsoft.Data.SqlClient.
- **Frontend:** SPA thuần (HTML/CSS/JS) phục vụ ngay từ API (`wwwroot`).
- **CSDL:** SQL Server — metadata `DBLogReportPortal`, DB nguồn `WideWorldImporters`.
- **Đóng gói:** Docker + docker compose. Một container, mở ở **http://localhost:8080**.

### Tính năng đã hoàn thiện (đối chiếu đặc tả)
- Đăng nhập JWT, băm mật khẩu PBKDF2, tự hết phiên khi rảnh 30'.
- **Bắt buộc đổi mật khẩu lần đầu** (Admin tạo tài khoản / reset ⇒ ép đổi; chặn ở backend bằng 409).
- Quản trị: CRUD người dùng + gán vai trò, **nhập người dùng hàng loạt từ CSV** (xem trước + kết quả từng dòng; mẫu: `PortalApp/sample-users.csv`), **lọc theo phòng ban/vai trò + tìm tên**, **phân quyền báo cáo theo từng người** (checkbox theo nhóm, chọn cả nhóm), **tự gửi email chào mừng** (SMTP/Pickup), test kết nối, **trình soạn/sửa SQL Template** (SQL Guard + versioning), Audit Log, hộp thư demo.
- Chạy báo cáo: form tham số động, validate (bắt buộc/kiểu/min-max/độ dài, **từ ngày ≤ đến ngày**), parameter binding chống injection, timeout + giới hạn dòng.
- Lưới kết quả: **phân trang + chọn số dòng/trang**, sắp xếp, tìm nhanh, **copy ô / dòng chọn / toàn bộ**, trạng thái rỗng/đang tải.
- Xuất Excel `.xlsx`: sheet **Data** + sheet **Parameters** (báo cáo, người thực hiện, thời điểm, số dòng, thời gian chạy, tham số).
- Lịch sử truy vấn (của tôi / toàn hệ thống) + chạy lại; lịch sử toàn hệ thống có **lọc theo người chạy, trạng thái (thành công/lỗi), khoảng ngày (từ–đến), sắp xếp theo thời điểm & thời lượng**, và **bảng xếp hạng người dùng chạy nhiều báo cáo nhất**; mã hóa mật khẩu kết nối bằng Data Protection.

### Giao diện
Thiết kế theo phong cách **Data-Dense Dashboard** (bảng màu Analytics, font *Plus Jakarta Sans* + *JetBrains Mono*),
áp dụng bộ hướng dẫn **ui-ux-pro-max** (states hover/active/disabled, focus ring, empty/loading states).

---

## 1. Kiến trúc & bảo mật

| Thành phần | Mô tả |
|---|---|
| Xác thực | Đăng nhập username/mật khẩu → **JWT**; mật khẩu người dùng băm **PBKDF2-SHA256** (100k vòng). |
| Phân quyền | Theo vai trò (RBAC) + quyền chạy từng báo cáo (ReportTemplatePermissions); policy backend từng API. |
| Kết nối DB nguồn | Dùng login **chỉ đọc** `portal_readonly`; mật khẩu kết nối **được mã hóa** (ASP.NET Data Protection), không lưu plaintext. |
| SQL Guard | Chỉ cho phép câu lệnh `SELECT`/`WITH`; chặn INSERT/UPDATE/DELETE/DROP/EXEC…, chặn nối nhiều lệnh. |
| Chống SQL injection | Mọi tham số **bind** qua `SqlParameter`, không nối chuỗi. |
| Giới hạn | Timeout & số dòng tối đa theo từng template; cảnh báo khi cắt bớt. |
| Nhật ký | `QueryExecutions`, `QueryExecutionParameters`, `ExportHistories`, `AuditLogs` (không ghi mật khẩu). |
| Phiên | Tự đăng xuất khi **không hoạt động 30 phút**. |

Hai tài khoản SQL Server tách biệt theo nguyên tắc tối thiểu quyền:
- `portal_app` — đọc/ghi **chỉ** trên `DBLogReportPortal` (metadata).
- `portal_readonly` — **chỉ đọc** trên `WideWorldImporters` (DB nguồn chạy báo cáo).

---

## 2. Yêu cầu

- Docker + Docker Compose.
- SQL Server đang chạy trên **host** (cổng 1433) với 2 database đã tạo:
  `DBLogReportPortal` (chạy `DBLogReportPortal_Schema.sql` + `DepartmentPermissions_Seed.sql`)
  và `WideWorldImporters`.
- Hai login đã tạo: `portal_app` (`PortalApp/sql/CreateAppLogin.sql`) và
  `portal_readonly` (`../CreateReadonlyLogin.sql`).

### Lấy database nguồn `WideWorldImporters`

Tải file backup mẫu chính thức của Microsoft:

- **[WideWorldImporters-Full.bak](https://github.com/Microsoft/sql-server-samples/releases/download/wide-world-importers-v1.0/WideWorldImporters-Full.bak)**
  (~127 MB, từ repo `microsoft/sql-server-samples`).

Phục hồi vào SQL Server (chạy bằng tài khoản `sa` trong SSMS / `sqlcmd`):

```sql
RESTORE DATABASE WideWorldImporters
FROM DISK = N'C:\backup\WideWorldImporters-Full.bak'
WITH MOVE 'WWI_Primary'           TO N'C:\Data\WideWorldImporters.mdf',
     MOVE 'WWI_UserData'          TO N'C:\Data\WideWorldImporters_UserData.ndf',
     MOVE 'WWI_Log'               TO N'C:\Data\WideWorldImporters.ldf',
     MOVE 'WWI_InMemory_Data_1'   TO N'C:\Data\WideWorldImporters_InMemory_Data_1',
     RECOVERY, REPLACE;
```

> Đổi các đường dẫn `C:\backup` / `C:\Data` cho khớp máy bạn (trên Linux/Docker
> thường là `/var/opt/mssql/backup` và `/var/opt/mssql/data`). Cần 4 mệnh đề
> `MOVE` vì database này có cả filegroup In-Memory. Sau khi restore, chạy
> `CreateReadonlyLogin.sql` để cấp tài khoản chỉ đọc cho ứng dụng.

---

## 3. Chạy ứng dụng

```bash
cd PortalApp
docker compose up -d --build
```

Mở trình duyệt: **http://localhost:8080**

Kiểm tra API còn sống: http://localhost:8080/api/health

Dừng: `docker compose down` (thêm `-v` để xóa cả volume khóa mã hóa).

> Container kết nối SQL Server của host qua `host.docker.internal` (đã cấu hình
> `extra_hosts: host-gateway`). Nếu bản ghi kết nối lưu `ServerHost = localhost`,
> ứng dụng tự đổi sang `host.docker.internal` khi chạy trong container
> (biến `Portal__SourceHostOverride`).

---

## 4. Tài khoản quản trị

Khi khởi động lần đầu, ứng dụng tự **băm mật khẩu** cho tài khoản còn
placeholder và **mã hóa mật khẩu** kết nối `portal_readonly`.

| Tài khoản | Mật khẩu | Vai trò | Quyền |
|---|---|---|---|
| `admin` | `Admin@123` | Admin | Tất cả báo cáo + trang Quản trị |

Các tài khoản người dùng theo phòng ban (Sales / Warehouse / Purchasing /
Manager) được tạo từ trang **Quản trị → Người dùng**; khi tạo, hệ thống gửi
email chào mừng và **bắt buộc đổi mật khẩu ở lần đăng nhập đầu**.

> Đổi mật khẩu `admin` ngay sau lần đăng nhập đầu, và đổi secret trong
> `PortalApp/.env` trước khi dùng thật.

---

## 5. Cấu hình (.env)

| Biến | Ý nghĩa |
|---|---|
| `PORTAL_APP_PASSWORD` | Mật khẩu login `portal_app` (khớp `CreateAppLogin.sql`). |
| `SOURCE_READONLY_PASSWORD` | Mật khẩu `portal_readonly` — được mã hóa rồi lưu vào DB. |
| `JWT_SIGNING_KEY` | Khóa ký JWT (≥ 32 ký tự). |
| `SEED_ADMIN_PASSWORD` / `SEED_DEFAULT_PASSWORD` | Mật khẩu khởi tạo tài khoản demo. |

---

## 6. API chính

| Method | Đường dẫn | Mô tả |
|---|---|---|
| POST | `/api/auth/login` | Đăng nhập, trả JWT. |
| GET | `/api/auth/me` | Thông tin người dùng hiện tại. |
| GET | `/api/catalog` | Danh mục báo cáo được cấp quyền. |
| GET | `/api/catalog/templates/{id}` | Chi tiết báo cáo + tham số. |
| POST | `/api/reports/{id}/run` | Chạy báo cáo (bind tham số). |
| POST | `/api/reports/{id}/export` | Xuất Excel (.xlsx). |
| GET | `/api/history/my` · `/all` | Lịch sử chạy. |
| GET | `/api/admin/users\|connections\|templates\|audit` | Quản trị (theo quyền). |

---

## 7. Phát triển không cần Docker (tùy chọn)

Cần .NET SDK 8. Sửa `appsettings.json` để `ServerHost`/host trỏ `localhost`
(bỏ override) rồi:

```bash
cd src
dotnet run
```
