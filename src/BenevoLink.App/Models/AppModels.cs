using System.Security.Claims;

namespace BenevoLink.Models;

public sealed record LoginResult(bool Succeeded, string Message, AppUser? User)
{
    public static LoginResult Success(AppUser user) => new(true, "Signed in.", user);
    public static LoginResult Fail(string message) => new(false, message, null);
}

public sealed record OperationResult(bool Succeeded, string Message)
{
    public static OperationResult Success(string message) => new(true, message);
    public static OperationResult Fail(string message) => new(false, message);
}

public sealed record AppUser(string Id, string Email, string DisplayName, string Role, string Status, string Source)
{
    public static AppUser? FromClaims(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true) return null;
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0";
        var email = principal.FindFirstValue(ClaimTypes.Email) ?? "unknown@benevolink.local";
        var name = principal.FindFirstValue(ClaimTypes.Name) ?? email;
        var role = principal.FindFirstValue(ClaimTypes.Role) ?? "Volunteer";
        var status = principal.FindFirstValue("benevolink.status") ?? "Active";
        var source = principal.FindFirstValue("benevolink.source") ?? "users";
        return new AppUser(id, email, name, role, status, source);
    }
}

public sealed record KpiCard(string Label, string Value, string Detail, string Tone, string Icon);
public sealed record TableInfo(string Name, string Type, long Rows, string Size, string Engine);
public sealed record ColumnInfo(string Name, string DataType, bool Nullable, string Key);
public sealed record DatabaseHealth(bool Online, string Database, string Server, string Message, DateTimeOffset CheckedAt, IReadOnlyList<TableInfo> Tables);
public sealed record FieldChoice(string? Id, string? Title, string? Subtitle, string? Status, string? Date, string? Owner, string? Detail);

public sealed class DataGrid
{
    public string Title { get; init; } = "Dataset";
    public string Subtitle { get; init; } = "Live database records";
    public string Source { get; init; } = "database";
    public IReadOnlyList<Dictionary<string, string>> Rows { get; init; } = [];
    public IReadOnlyList<string> PreferredColumns { get; init; } = [];
    public bool IsLive { get; init; }
}

public sealed class EntityCard
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "Untitled";
    public string Subtitle { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Status { get; init; } = "Active";
    public string Date { get; init; } = "";
    public string Source { get; init; } = "";
    public Dictionary<string, string> Raw { get; init; } = [];
}

public sealed class WorkspaceModel
{
    public string Eyebrow { get; init; } = "BenevoLink ImpactOS";
    public string Title { get; init; } = "Command Center";
    public string Subtitle { get; init; } = "Connected to XAMPP MySQL";
    public IReadOnlyList<KpiCard> Kpis { get; init; } = [];
    public IReadOnlyList<EntityCard> PrimaryCards { get; init; } = [];
    public IReadOnlyList<DataGrid> Grids { get; init; } = [];
    public IReadOnlyList<TableInfo> Tables { get; init; } = [];
    public DatabaseHealth? Health { get; init; }
}

public sealed class MissionActionItem
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "Mission";
    public string Organization { get; init; } = "BenevoLink partner";
    public string Location { get; init; } = "Hybrid";
    public string Cause { get; init; } = "Social impact";
    public string Description { get; init; } = "Live mission from XAMPP MySQL.";
    public string Status { get; init; } = "Open";
    public string Date { get; init; } = "";
    public string Source { get; init; } = "missions";
    public string? ApplicationStatus { get; init; }
    public bool IsSaved { get; init; }
    public Dictionary<string, string> Raw { get; init; } = [];
}

public sealed class ApplicationActionItem
{
    public string Id { get; init; } = "";
    public string Applicant { get; init; } = "Volunteer";
    public string Mission { get; init; } = "Mission";
    public string Status { get; init; } = "Pending";
    public string SubmittedAt { get; init; } = "";
    public string Motivation { get; init; } = "";
    public string Source { get; init; } = "applications";
    public Dictionary<string, string> Raw { get; init; } = [];
}

public sealed class EventActionItem
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "Event";
    public string Location { get; init; } = "To be announced";
    public string Date { get; init; } = "";
    public string Status { get; init; } = "Open";
    public string RegistrationStatus { get; init; } = "Not registered";
    public string Source { get; init; } = "events";
    public Dictionary<string, string> Raw { get; init; } = [];
}

public sealed class CertificateActionItem
{
    public string Id { get; init; } = "";
    public string Volunteer { get; init; } = "Volunteer";
    public string Activity { get; init; } = "Social impact activity";
    public string Organization { get; init; } = "BenevoLink";
    public string Hours { get; init; } = "0";
    public string Date { get; init; } = "";
    public string Status { get; init; } = "Completed";
    public string Source { get; init; } = "participations";
}

public sealed class NotificationActionItem
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "Notification";
    public string Body { get; init; } = "";
    public string Status { get; init; } = "Unread";
    public string Date { get; init; } = "";
    public string Source { get; init; } = "notifications";
}

public sealed class ProfileSnapshot
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Email { get; init; } = "";
    public string Role { get; init; } = "";
    public string Status { get; init; } = "";
    public string City { get; init; } = "";
    public string Phone { get; init; } = "";
    public string Bio { get; init; } = "";
    public IReadOnlyList<string> Skills { get; init; } = [];
    public IReadOnlyList<Dictionary<string, string>> RawRows { get; init; } = [];
}

public sealed class OrganizationActionItem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "Organization";
    public string Sector { get; init; } = "Social impact";
    public string City { get; init; } = "";
    public string Status { get; init; } = "Active";
    public string Email { get; init; } = "";
    public string Description { get; init; } = "";
    public string Source { get; init; } = "associations";
    public Dictionary<string, string> Raw { get; init; } = [];
}

public sealed class VolunteerActionItem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "Volunteer";
    public string Email { get; init; } = "";
    public string Role { get; init; } = "Volunteer";
    public string City { get; init; } = "";
    public string Status { get; init; } = "Active";
    public string Hours { get; init; } = "0";
    public string Skills { get; init; } = "";
    public string Source { get; init; } = "users";
    public Dictionary<string, string> Raw { get; init; } = [];
}

public sealed class ImpactGoalActionItem
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "Impact goal";
    public string Target { get; init; } = "";
    public string Current { get; init; } = "";
    public string Status { get; init; } = "Active";
    public string Sdg { get; init; } = "";
    public string Date { get; init; } = "";
    public string Source { get; init; } = "impact_goals";
    public Dictionary<string, string> Raw { get; init; } = [];
}

public sealed class AuditActionItem
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "Action";
    public string Message { get; init; } = "";
    public string Actor { get; init; } = "";
    public string Date { get; init; } = "";
    public string Source { get; init; } = "audit_logs";
}

public sealed class DatabaseTableWorkspace
{
    public string SelectedTable { get; init; } = "";
    public IReadOnlyList<TableInfo> Tables { get; init; } = [];
    public IReadOnlyList<ColumnInfo> Columns { get; init; } = [];
    public IReadOnlyList<Dictionary<string, string>> Rows { get; init; } = [];
    public string IdColumn { get; init; } = "";
}
