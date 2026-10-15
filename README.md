<div align="center">

# 📊 Website Portal Log Report

**Cổng báo cáo nội bộ** — chạy các mẫu báo cáo SQL có tham số, xem kết quả, xuất Excel,
kèm phân quyền theo phòng ban, chống brute‑force và nhật ký kiểm toán (audit).

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![SQL Server](https://img.shields.io/badge/SQL%20Server-2019%2B-CC2927?logo=microsoftsqlserver&logoColor=white)](https://www.microsoft.com/sql-server)
[![Docker](https://img.shields.io/badge/Docker-Compose-2496ED?logo=docker&logoColor=white)](https://www.docker.com/)
[![SPA](https://img.shields.io/badge/Frontend-Vanilla%20JS-F7DF1E?logo=javascript&logoColor=black)](#-công-nghệ-sử-dụng)
[![Security](https://img.shields.io/badge/Security-Hardened-success?logo=shieldsdotio&logoColor=white)](#-bảo-mật)

</div>

---

## 📑 Mục lục

- [Tổng quan](#-tổng-quan)
- [Tính năng chính](#-tính-năng-chính)
- [Kiến trúc](#-kiến-trúc)
- [Công nghệ sử dụng](#-công-nghệ-sử-dụng)
- [Bảo mật](#-bảo-mật)
- [Bắt đầu nhanh](#-bắt-đầu-nhanh)
- [Cấu hình](#-cấu-hình)
- [Tài khoản](#-tài-khoản)
- [Danh mục báo cáo](#-danh-mục-báo-cáo)
- [Tài liệu API](#-tài-liệu-api)
- [Cấu trúc dự án](#-cấu-trúc-dự-án)
- [Phát triển](#-phát-triển)
- [Lưu ý khi triển khai Production](#-lưu-ý-khi-triển-khai-production)

---

## 🎯 Tổng quan

Portal Log Report là ứng dụng web nội bộ giúp **người dùng nghiệp vụ tự tra cứu báo cáo**
mà không cần quyền truy cập trực tiếp vào cơ sở dữ liệu. Quản trị viên định nghĩa sẵn các
**mẫu báo cáo (SQL template)** có tham số; người dùng chỉ việc nhập tham số và chạy — kết quả
được hiển thị trên lưới dữ liệu và có thể xuất ra Excel.

Mọi truy vấn chỉ chạy **chỉ‑đọc** trên database nguồn thông qua tài khoản quyền tối thiểu,
được bọc bởi lớp **SQL Guard** và **tham số hóa** để chống SQL injection, đồng thời ghi lại
đầy đủ nhật ký thực thi để phục vụ kiểm toán.

| | |
|---|---|
| 🔌 **Một container** | API + SPA phục vụ chung tại `http://localhost:8080` |
| 🗄️ **Hai database** | `DBLogReportPortal` (metadata) · `WideWorldImporters` (nguồn mẫu) |
| 📈 **22 mẫu báo cáo** | Doanh thu · Giao dịch · Khách hàng · Kho · Mua hàng · Log |
| 👥 **RBAC + per‑user** | Phân quyền theo vai trò **và** theo từng người (cho phép/chặn) |

---

## ✨ Tính năng chính

### Người dùng
- 🔎 **Tìm & chạy báo cáo** theo mẫu có sẵn; form tham số động với **validate** (bắt buộc,
  kiểu dữ liệu, min/max, độ dài, dropdown, **từ ngày ≤ đến ngày**).
- 📊 **Lưới kết quả**: phân trang, chọn số dòng/trang, sắp xếp, tìm nhanh, copy ô / dòng / toàn bộ.
- 📥 **Xuất Excel** (`.xlsx`) với sheet **Data** + sheet **Parameters** (người thực hiện, thời điểm, thời gian chạy, tham số).
- 🕑 **Lịch sử truy vấn của tôi** với lọc theo tên báo cáo, trạng thái, khoảng ngày, sắp xếp; **chạy lại** 1‑chạm.

### Quản trị
- 👤 **Quản lý người dùng**: tạo/sửa, gán vai trò, khóa/mở, đổi mật khẩu; **lọc** theo phòng ban / vai trò / trạng thái / tên.
- 📦 **Nhập người dùng hàng loạt** từ CSV (xem trước + kết quả từng dòng; file mẫu `PortalApp/sample-users.csv`).
- 🔑 **Phân quyền báo cáo theo từng người** (checkbox theo nhóm, chọn cả nhóm; bỏ tick báo cáo của vai trò = **chặn riêng** cho người đó).
- 📝 **Trình soạn/sửa SQL Template** với **SQL Guard** + versioning; test kết nối DB.
- 📈 **Thống kê sử dụng**: xếp hạng người chạy nhiều báo cáo nhất; bấm vào một người để xem các báo cáo họ đã chạy.
- 🧾 **Audit Log** + **Hộp thư** (xem email chào mừng ở chế độ demo).
- ✉️ **Tự gửi email chào mừng** kèm mật khẩu tạm khi tạo tài khoản (SMTP / Pickup).

### Trải nghiệm
- 🎨 Giao diện **Data‑Dense Dashboard**, hiệu ứng tinh tế, tôn trọng `prefers-reduced-motion`.
- ⬅️ **Back/Forward của trình duyệt** điều hướng trong app (History API).

---

## 🏗️ Kiến trúc

```mermaid
flowchart LR
    U[👤 Trình duyệt<br/>SPA Vanilla JS] -- HTTPS/JWT --> A

    subgraph Container["🐳 Docker container :8080"]
        A[ASP.NET Core 8 Web API<br/>+ wwwroot SPA]
    end

    A -- portal_app · đọc/ghi --> M[(DBLogReportPortal<br/>metadata · portal.*)]
    A -- portal_readonly · chỉ đọc --> S[(WideWorldImporters<br/>dữ liệu nguồn)]

    A -. mã hóa khóa .-> K[[DataProtection keys<br/>volume]]
    A -. .eml demo .-> MB[[Mail pickup<br/>volume]]
```

**Nguyên tắc tách quyền tối thiểu (least privilege):**

| Tài khoản SQL | Phạm vi | Quyền |
|---|---|---|
| `portal_app` | `DBLogReportPortal` (metadata) | `db_datareader` + `db_datawriter` |
| `portal_readonly` | `WideWorldImporters` (nguồn) | **Chỉ đọc** (`db_datareader` + DENY ghi, `ApplicationIntent=ReadOnly`) |

> Container kết nối SQL Server của host qua `host.docker.internal`
> (đã cấu hình `extra_hosts: host-gateway`). Nếu bản ghi kết nối lưu `ServerHost = localhost`,
> ứng dụng tự đổi sang `host.docker.internal` khi chạy trong container.

---

## 🧰 Công nghệ sử dụng

| Lớp | Thành phần |
|---|---|
| **Backend** | ASP.NET Core 8 Web API (C#), Dapper, Microsoft.Data.SqlClient |
| **Xuất Excel** | ClosedXML |
| **Xác thực** | JWT (JwtBearer), PBKDF2‑HMAC‑SHA256 (600k vòng) cho mật khẩu |
| **Mã hóa secret** | ASP.NET Core Data Protection (mật khẩu kết nối) |
| **Frontend** | SPA thuần HTML/CSS/JS (không framework), phục vụ từ `wwwroot` |
| **CSDL** | SQL Server (metadata + nguồn) |
| **Đóng gói** | Docker multi‑stage + Docker Compose |

---

## 🔒 Bảo mật

Dự án đã qua **rà soát bảo mật** (kiểm thử tĩnh + động). Các kiểm soát chính:

- **Chống SQL injection** — 100% tham số hóa (`SqlParameter`/Dapper); `ORDER BY`/sort dùng whitelist; không nối chuỗi.
- **SQL Guard** — chỉ cho phép `SELECT`/`WITH`; chặn ghi/DDL/thực thi, nối lệnh, `SELECT … INTO`, `EXEC`, `xp_/sp_`, v.v.
- **Xác thực & phân quyền** — JWT kiểm tra chặt (issuer/audience/lifetime/chữ ký, clock‑skew 30s); policy backend theo từng API; quyền theo từng báo cáo (chống IDOR).
- **Mật khẩu** — băm PBKDF2‑SHA256, salt ngẫu nhiên, so sánh constant‑time; **bắt buộc đổi mật khẩu lần đầu**.
- **Chống brute‑force** — **khóa tạm tài khoản** sau 5 lần sai (15 phút) + **rate‑limit** đăng nhập theo IP.
- **Mã hóa secret** — mật khẩu kết nối mã hóa bằng Data Protection; **không bao giờ** trả ra client.
- **Security headers** — CSP, `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, ẩn `Server`.
- **Audit & ẩn lỗi** — ghi nhật ký không chứa secret; handler lỗi ẩn stack trace ở Production.

> ⚠️ Khi lên Production: kết thúc **TLS tại reverse proxy** (bật HSTS), cài **chứng chỉ tin cậy**
> cho SQL Server (`TrustServerCertificate=false`), và quản lý secret qua kho bí mật.

---

## 🚀 Bắt đầu nhanh

### Yêu cầu
- **Docker** + **Docker Compose**
- **SQL Server** đang chạy trên host (cổng `1433`)

### 1) Chuẩn bị cơ sở dữ liệu

**a. Tạo database metadata + seed phân quyền/báo cáo**
```bash
# chạy bằng tài khoản sa (SSMS hoặc sqlcmd)
DBLogReportPortal_Schema.sql
DepartmentPermissions_Seed.sql
```

**b. Lấy database nguồn mẫu `WideWorldImporters`**

Tải backup chính thức của Microsoft:
- **[WideWorldImporters-Full.bak](https://github.com/Microsoft/sql-server-samples/releases/download/wide-world-importers-v1.0/WideWorldImporters-Full.bak)** (~127 MB)

Phục hồi:
```sql
RESTORE DATABASE WideWorldImporters
FROM DISK = N'C:\backup\WideWorldImporters-Full.bak'
WITH MOVE 'WWI_Primary'         TO N'C:\Data\WideWorldImporters.mdf',
     MOVE 'WWI_UserData'        TO N'C:\Data\WideWorldImporters_UserData.ndf',
     MOVE 'WWI_Log'             TO N'C:\Data\WideWorldImporters.ldf',
     MOVE 'WWI_InMemory_Data_1' TO N'C:\Data\WideWorldImporters_InMemory_Data_1',
     RECOVERY, REPLACE;
```
> Đổi đường dẫn cho khớp máy (Linux/Docker: `/var/opt/mssql/...`). Cần 4 mệnh đề `MOVE` vì DB có filegroup In‑Memory.

**c. Tạo 2 login quyền tối thiểu**
```bash
PortalApp/sql/CreateAppLogin.sql     # portal_app  (metadata, đọc/ghi)
CreateReadonlyLogin.sql              # portal_readonly (WideWorldImporters, chỉ đọc)
```
> Đặt mật khẩu trong 2 file này **khớp** với `.env` (xem mục [Cấu hình](#-cấu-hình)).

### 2) Cấu hình biến môi trường
```bash
cd PortalApp
cp .env.example .env     # rồi điền mật khẩu & khóa JWT
```

### 3) Chạy ứng dụng
```bash
docker compose up -d --build
```

Mở trình duyệt: **http://localhost:8080** — kiểm tra sức khỏe API: **/api/health**

Dừng: `docker compose down` (thêm `-v` để xóa cả volume khóa mã hóa).

---

## ⚙️ Cấu hình

Tất cả secret nạp từ `PortalApp/.env` (đã được `.gitignore`; mẫu: `PortalApp/.env.example`).

| Biến | Ý nghĩa |
|---|---|
| `PORTAL_APP_PASSWORD` | Mật khẩu login `portal_app` (khớp `CreateAppLogin.sql`) |
| `SOURCE_READONLY_PASSWORD` | Mật khẩu `portal_readonly` — được **mã hóa** rồi lưu vào DB |
| `JWT_SIGNING_KEY` | Khóa ký JWT (**≥ 32 ký tự**, bắt buộc) |
| `SEED_ADMIN_PASSWORD` / `SEED_DEFAULT_PASSWORD` | Mật khẩu khởi tạo tài khoản |
| `EMAIL_MODE` | `Pickup` (demo, ghi `.eml`) · `Smtp` · `Disabled` |
| `SMTP_HOST` · `SMTP_PORT` · `SMTP_USER` · `SMTP_PASSWORD` · `EMAIL_FROM` | Cấu hình SMTP (khi `EMAIL_MODE=Smtp`) |

Ngưỡng bảo mật có thể chỉnh qua `appsettings.json → Portal:Security` hoặc biến môi trường:
`MaxFailedAttempts` (5), `LockoutMinutes` (15), `LoginRatePerMinute` (20).

---

## 👤 Tài khoản

Khi khởi động lần đầu, ứng dụng tự **băm mật khẩu** cho tài khoản còn placeholder và
**mã hóa mật khẩu** kết nối `portal_readonly`.

| Tài khoản | Mật khẩu | Vai trò | Quyền |
|---|---|---|---|
| `admin` | `Admin@123` | Admin | Toàn bộ báo cáo + trang Quản trị |

> Tài khoản `admin` **bắt buộc đổi mật khẩu** ở lần đăng nhập đầu. Các tài khoản người dùng theo
> phòng ban (Sales / Warehouse / Purchasing / Manager) được tạo từ **Quản trị → Người dùng**
> hoặc **nhập hàng loạt** từ CSV.

---

## 📈 Danh mục báo cáo

**22 mẫu báo cáo** trên bộ dữ liệu WideWorldImporters, phân theo 6 nhóm nghiệp vụ.

<details>
<summary><b>Xem danh sách đầy đủ</b></summary>

| Nhóm | Báo cáo |
|---|---|
| **Doanh thu** | Doanh thu theo ngày · Top sản phẩm bán chạy · Doanh thu theo tháng · Doanh thu theo nhân viên bán hàng · Lợi nhuận theo sản phẩm |
| **Giao dịch** | Đơn hàng theo khoảng ngày · Đơn hàng theo khách hàng · Đơn hàng chưa hoàn tất lấy hàng |
| **Khách hàng** | Giao dịch khách hàng · Khách hàng theo khu vực · Top khách hàng theo doanh thu · Công nợ khách hàng |
| **Kho** | Tồn kho sản phẩm · Sản phẩm cần nhập thêm · Lịch sử xuất/nhập kho · Giá trị tồn kho |
| **Mua hàng** | Đơn đặt hàng nhà cung cấp · Danh sách nhà cung cấp · Hàng nhập theo nhà cung cấp · Đơn mua chưa nhận đủ hàng |
| **Log** | Log nhiệt độ theo thời gian · Dữ liệu nhiệt độ bất thường |

</details>

---

## 🔌 Tài liệu API

| Method | Đường dẫn | Mô tả |
|---|---|---|
| `POST` | `/api/auth/login` | Đăng nhập, trả JWT |
| `GET` | `/api/auth/me` | Thông tin người dùng hiện tại |
| `POST` | `/api/auth/change-password` | Đổi mật khẩu (trả token mới) |
| `GET` | `/api/catalog` | Danh mục báo cáo được cấp quyền |
| `GET` | `/api/catalog/templates/{id}` | Chi tiết báo cáo + tham số |
| `POST` | `/api/reports/{id}/run` | Chạy báo cáo (bind tham số) |
| `POST` | `/api/reports/{id}/export` | Xuất Excel (.xlsx) |
| `GET` | `/api/history/my` · `/all` · `/stats/top-users` | Lịch sử & thống kê |
| `GET/POST/PUT/DELETE` | `/api/admin/users\|templates\|connections\|audit` | Quản trị (theo policy) |
| `GET` | `/api/health` | Kiểm tra sức khỏe |

---

## 🗂️ Cấu trúc dự án

```
Website_Portal_Log/
├── DBLogReportPortal_Schema.sql      # schema metadata (portal.*)
├── DepartmentPermissions_Seed.sql    # seed vai trò, quyền & 22 báo cáo
├── CreateReadonlyLogin.sql           # login chỉ-đọc cho DB nguồn
└── PortalApp/
    ├── docker-compose.yml
    ├── .env.example                  # mẫu cấu hình (sao chép thành .env)
    ├── sample-users.csv              # mẫu nhập người dùng hàng loạt
    ├── sql/CreateAppLogin.sql        # login metadata
    └── src/
        ├── Controllers/              # Auth · Catalog · Reports · History · Admin
        ├── Services/                 # nghiệp vụ (Report, Export, Auth, Admin, …)
        ├── Infrastructure/           # SqlGuard · PasswordHasher · SecretProtector · …
        ├── Models/Dtos.cs
        ├── wwwroot/                  # SPA: index.html · styles.css · app.js
        ├── Program.cs                # DI · JWT · rate limit · security headers · pipeline
        └── Dockerfile
```

---

## 🧑‍💻 Phát triển

**Chạy bằng Docker (khuyến nghị):**
```bash
cd PortalApp && docker compose up -d --build
docker compose logs -f          # xem log
```

**Chạy trực tiếp bằng .NET (tùy chọn, cần .NET SDK 8):**
```bash
cd PortalApp/src
# sửa appsettings.json để host trỏ localhost (bỏ SourceHostOverride)
dotnet run
```

---

## 🏭 Lưu ý khi triển khai Production

- 🔐 Đổi **toàn bộ** mật khẩu & `JWT_SIGNING_KEY`; quản lý secret qua kho bí mật.
- 🌐 Đặt sau **reverse proxy** (nginx/traefik) để kết thúc **TLS** và bật **HSTS**.
- 🪪 Cài **chứng chỉ tin cậy** cho SQL Server, đặt `TrustServerCertificate=false`.
- 🧭 Bật `ForwardedHeaders` + `KnownProxies` để rate‑limit/audit dùng đúng IP client.
- ✉️ Dùng `EMAIL_MODE=Smtp` với tài khoản gửi thực; không dùng chế độ Pickup/demo.

---

<div align="center">
<sub>Dự án nội bộ · ASP.NET Core 8 + SQL Server + Docker</sub>
</div>
