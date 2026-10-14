namespace Portal.Api.Models;

// ---- Auth ----
public sealed record LoginRequest(string Username, string Password);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record UserInfo(
    int UserId, string Username, string? FullName, string? Email, string? Department,
    IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions, bool MustChangePassword);
public sealed record LoginResponse(string Token, DateTime ExpiresAt, UserInfo User);

// ---- Catalog ----
public sealed record CatalogTemplate(
    int TemplateId, string TemplateCode, string TemplateName, string? Description,
    bool AllowCopy, bool AllowExport, int MaxRows, int ParameterCount);
public sealed record CatalogCategory(
    int CategoryId, string CategoryName, string? Description, int DisplayOrder,
    IReadOnlyList<CatalogTemplate> Templates);

// ---- Template detail / parameters ----
public sealed record TemplateParameter(
    int ParameterId, string ParameterName, string DisplayName, string DataType, string InputType,
    bool IsRequired, string? DefaultValue, int DisplayOrder,
    string? MinValue, string? MaxValue, int? MaxLength, string? Options);
public sealed record TemplateDetail(
    int TemplateId, string TemplateCode, string TemplateName, string? Description,
    int CategoryId, string CategoryName, bool AllowCopy, bool AllowExport,
    int MaxRows, int TimeoutSeconds, IReadOnlyList<TemplateParameter> Parameters);

// ---- Run ----
public sealed record RunRequest(Dictionary<string, string?>? Parameters);
public sealed record RunResult(
    long ExecutionId, IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows,
    int RowCount, bool Truncated, int DurationMs, bool AllowCopy, bool AllowExport);

// ---- History ----
public sealed record HistoryItem(
    long ExecutionId, int TemplateId, string TemplateCode, string TemplateName,
    string Username, DateTime StartedAt, int? DurationMs, int? RowCountOut,
    string Status, bool IsExported, string? ErrorMessage);

// ---- Admin ----
public sealed record AdminUser(
    int UserId, string Username, string? FullName, string? Email, string? Department,
    bool IsActive, DateTime? LastLoginAt, IReadOnlyList<string> Roles);
public sealed record AdminConnection(
    int ConnectionId, string ConnectionName, string ServerHost, int? Port, string DatabaseName,
    string Username, string Environment, bool IsActive, bool PasswordEncrypted);
public sealed record AdminTemplate(
    int TemplateId, string TemplateCode, string TemplateName, string CategoryName,
    string ConnectionName, string Status, int ParameterCount);
public sealed record AuditItem(
    long AuditId, string? Username, string Action, string? TargetObject, string? TargetId,
    string? ClientIP, DateTime CreatedAt);

public sealed record CreateUserRequest(
    string Username, string? FullName, string? Email, string? Department,
    string Password, string[]? Roles);
public sealed record ResetPasswordRequest(string NewPassword);
public sealed record SetActiveRequest(bool IsActive);
public sealed record TestConnectionResult(bool Ok, string Message, int DurationMs);

// ---- Admin: SQL Template CRUD ----
public sealed record Lookup(int Id, string Name);
public sealed record AdminLookups(
    IReadOnlyList<Lookup> Categories, IReadOnlyList<Lookup> Connections, IReadOnlyList<Lookup> Roles);

public sealed record TemplateParamInput(
    string ParameterName, string DisplayName, string DataType,
    bool IsRequired, string? DefaultValue, int DisplayOrder,
    string? MinValue, string? MaxValue, int? MaxLength, string? Options);

public sealed record TemplateFormRequest(
    string TemplateCode, string TemplateName, string? Description,
    int CategoryId, int ConnectionId, string SqlCommand,
    int MaxRows, int TimeoutSeconds, bool AllowCopy, bool AllowExport, string Status,
    int[]? RoleIds, TemplateParamInput[]? Parameters, string? VersionNote);

public sealed record TemplateFull(
    int TemplateId, string TemplateCode, string TemplateName, string? Description,
    int CategoryId, int ConnectionId, string SqlCommand, int MaxRows, int TimeoutSeconds,
    bool AllowCopy, bool AllowExport, string Status,
    IReadOnlyList<int> RoleIds, IReadOnlyList<TemplateParameter> Parameters);

public sealed record SqlValidateRequest(string Sql);
public sealed record SqlValidateResult(bool Ok, string? Reason);
