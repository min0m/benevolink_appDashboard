using BenevoLink.Models;

namespace BenevoLink.Services;

public sealed class PlatformService(XamppMySql db, DatabaseCatalog catalog)
{
    public async Task<WorkspaceModel> DashboardAsync(AppUser? user, CancellationToken ct = default)
    {
        var health = await db.HealthAsync(ct);
        if (!health.Online)
        {
            return new WorkspaceModel
            {
                Title = "XAMPP database offline",
                Subtitle = health.Message,
                Health = health,
                Kpis = [new("Connection", "Offline", "Start MySQL in XAMPP", "danger", "database")]
            };
        }

        var missions = await CardsFromFirstExistingAsync(["v_public_missions", "missions"], 8, ct);
        var dashboardRows = await GridFromFirstExistingAsync("Executive database views", ["v_platform_dashboard", "v_platform_stats", "v_owner_operations"], 16, ct);
        var approvalRows = await GridFromFirstExistingAsync("Approval and moderation queue", ["v_admin_final_approval_queue", "approval_requests", "applications"], 12, ct);

        return new WorkspaceModel
        {
            Eyebrow = "Live XAMPP MySQL workspace",
            Title = user?.Role == "Admin" ? "Global Command Center" : user?.Role == "Organization" ? "Organization Operations Center" : "Volunteer Impact Passport",
            Subtitle = "Premium BenevoLink interface connected directly to your existing benevolink database — no SQLite, no replacement database.",
            Health = health,
            Tables = health.Tables,
            Kpis = await CoreKpisAsync(ct),
            PrimaryCards = missions,
            Grids = [dashboardRows, approvalRows]
        };
    }

    public async Task<WorkspaceModel> MissionsAsync(CancellationToken ct = default)
    {
        var cards = await CardsFromFirstExistingAsync(["v_public_missions", "missions"], 80, ct);
        var grid = await GridFromFirstExistingAsync("Mission records", ["v_public_missions", "missions"], 100, ct);
        return Standard("Mission Discovery", "Search, review, and track live missions from the XAMPP database.", cards, [grid], await CoreKpisAsync(ct));
    }

    public async Task<WorkspaceModel> OrganizationsAsync(CancellationToken ct = default)
    {
        var cards = await CardsFromFirstExistingAsync(["associations", "organizations", "ngos"], 80, ct);
        var messages = await GridFromFirstExistingAsync("Association communications", ["association_messages", "approval_requests"], 60, ct);
        return Standard("Organization Network", "NGO profiles, association data, verification status, and operational records.", cards, [messages], await CoreKpisAsync(ct));
    }

    public async Task<WorkspaceModel> VolunteersAsync(CancellationToken ct = default)
    {
        var cards = await CardsFromFirstExistingAsync(["v_member_impact", "users", "participations"], 80, ct);
        var skills = await GridFromFirstExistingAsync("Skills and participation intelligence", ["member_skills", "participations", "volunteer_hours"], 80, ct);
        return Standard("Volunteer System", "Profiles, skills, participation history, impact hours, and member intelligence.", cards, [skills], await CoreKpisAsync(ct));
    }

    public async Task<WorkspaceModel> ApplicationsAsync(CancellationToken ct = default)
    {
        var cards = await CardsFromFirstExistingAsync(["applications", "approval_requests"], 80, ct);
        var grid = await GridFromFirstExistingAsync("Application and approval workflow", ["applications", "approval_requests"], 100, ct);
        return Standard("Applications & Approval Workflow", "Track mission applications, approval requests, reviews, and status movement.", cards, [grid], await CoreKpisAsync(ct));
    }

    public async Task<WorkspaceModel> EventsAsync(CancellationToken ct = default)
    {
        var cards = await CardsFromFirstExistingAsync(["events", "feature_events"], 80, ct);
        var registrations = await GridFromFirstExistingAsync("Registrations and participation", ["event_registrations", "participations", "volunteer_hours"], 100, ct);
        return Standard("Events & Attendance", "Calendar operations, registration records, attendance signals, and validation history.", cards, [registrations], await CoreKpisAsync(ct));
    }

    public async Task<WorkspaceModel> ImpactAsync(CancellationToken ct = default)
    {
        var cards = await CardsFromFirstExistingAsync(["impact_goals", "v_member_impact", "volunteer_hours"], 80, ct);
        var usage = await GridFromFirstExistingAsync("SDG / ODD and impact telemetry", ["impact_goals", "v_member_impact", "v_usage_by_day"], 100, ct);
        return Standard("Analytics & Social Impact", "KPI widgets, SDG/ODD tracking, impact goals, leaderboards, and platform usage.", cards, [usage], await CoreKpisAsync(ct));
    }

    public async Task<WorkspaceModel> AdminAsync(CancellationToken ct = default)
    {
        var health = await db.HealthAsync(ct);
        var users = await GridFromFirstExistingAsync("User management", ["users"], 120, ct);
        var security = await GridFromFirstExistingAsync("Security oversight and audit", ["audit_logs", "feedbacks", "v_owner_operations"], 120, ct);
        return new WorkspaceModel
        {
            Eyebrow = "Administration",
            Title = "Admin Control Center",
            Subtitle = "User management, database visibility, NGO approval queues, audit logs, and platform governance.",
            Health = health,
            Tables = health.Tables,
            Kpis = await CoreKpisAsync(ct),
            PrimaryCards = health.Tables.Take(12).Select(t => new EntityCard { Id = t.Name, Title = t.Name, Subtitle = t.Type, Detail = $"{t.Rows:N0} rows · {t.Size}", Status = t.Engine, Source = "information_schema" }).ToList(),
            Grids = [users, security]
        };
    }

    public async Task<WorkspaceModel> NotificationsAsync(AppUser? user, CancellationToken ct = default)
    {
        var cards = await CardsFromFirstExistingAsync(["notifications", "feedbacks", "association_messages"], 80, ct);
        return Standard("Notifications", "Live notification, feedback, and message stream from your database.", cards, [await GridFromFirstExistingAsync("Notification records", ["notifications", "feedbacks", "association_messages"], 100, ct)], await CoreKpisAsync(ct));
    }

    public async Task<WorkspaceModel> DatabaseAsync(CancellationToken ct = default)
    {
        var health = await db.HealthAsync(ct);
        var schemaCards = health.Tables.Select(t => new EntityCard { Id = t.Name, Title = t.Name, Subtitle = t.Type, Detail = $"{t.Rows:N0} records · {t.Size}", Status = t.Engine, Source = "benevolink" }).ToList();
        return new WorkspaceModel
        {
            Eyebrow = "Database",
            Title = "XAMPP MySQL Link",
            Subtitle = health.Online ? "Directly connected to the existing benevolink database." : health.Message,
            Health = health,
            Tables = health.Tables,
            Kpis = [new("Connection", health.Online ? "Online" : "Offline", health.Server, health.Online ? "success" : "danger", "database"), new("Tables & views", health.Tables.Count.ToString(), "Detected live from phpMyAdmin schema", "primary", "layers"), new("Database", health.Database, "localhost:3306", "neutral", "server"), new("Mode", "Action-enabled", "Direct writes to existing tables only", "success", "shield")],
            PrimaryCards = schemaCards,
            Grids = [new DataGrid { Title = "Detected schema", Subtitle = "Same tables shown in phpMyAdmin", Source = "information_schema", Rows = health.Tables.Select(t => new Dictionary<string, string> { ["Name"] = t.Name, ["Type"] = t.Type, ["Rows"] = t.Rows.ToString("N0"), ["Engine"] = t.Engine, ["Size"] = t.Size }).ToList(), IsLive = health.Online }]
        };
    }

    private async Task<IReadOnlyList<KpiCard>> CoreKpisAsync(CancellationToken ct)
    {
        await catalog.EnsureLoadedAsync(db, ct);
        var users = await CountFirstAsync(["users"], ct);
        var missions = await CountFirstAsync(["missions", "v_public_missions"], ct);
        var apps = await CountFirstAsync(["applications", "approval_requests"], ct);
        var events = await CountFirstAsync(["events", "feature_events"], ct);
        return [
            new KpiCard("Users", users.ToString("N0"), "Existing accounts in benevolink.users", "primary", "users"),
            new KpiCard("Missions", missions.ToString("N0"), "Mission pipeline", "success", "target"),
            new KpiCard("Applications", apps.ToString("N0"), "Approval workflow", "warning", "inbox"),
            new KpiCard("Events", events.ToString("N0"), "Calendar and participation", "neutral", "calendar")
        ];
    }

    private async Task<long> CountFirstAsync(string[] tables, CancellationToken ct)
    {
        foreach (var table in tables)
        {
            if (catalog.HasTable(table)) return await db.CountAsync(table, ct);
        }
        return 0;
    }

    private async Task<List<EntityCard>> CardsFromFirstExistingAsync(string[] tables, int limit, CancellationToken ct)
    {
        await catalog.EnsureLoadedAsync(db, ct);
        foreach (var table in tables)
        {
            if (!catalog.HasTable(table)) continue;
            var rows = await db.QueryTableAsync(table, limit, null, ct);
            return rows.Select(row => ToCard(table, row)).ToList();
        }
        return [];
    }

    private async Task<DataGrid> GridFromFirstExistingAsync(string title, string[] tables, int limit, CancellationToken ct)
    {
        await catalog.EnsureLoadedAsync(db, ct);
        foreach (var table in tables)
        {
            if (!catalog.HasTable(table)) continue;
            var rows = await db.QueryTableAsync(table, limit, null, ct);
            return new DataGrid { Title = title, Subtitle = $"Source: {table}", Source = table, Rows = rows, PreferredColumns = PreferredColumnsFor(table, rows), IsLive = true };
        }
        return new DataGrid { Title = title, Subtitle = "No matching table/view was found in this database.", Source = "missing", Rows = [], IsLive = false };
    }

    private static WorkspaceModel Standard(string title, string subtitle, IReadOnlyList<EntityCard> cards, IReadOnlyList<DataGrid> grids, IReadOnlyList<KpiCard> kpis) => new()
    {
        Eyebrow = "BenevoLink ImpactOS",
        Title = title,
        Subtitle = subtitle,
        Kpis = kpis,
        PrimaryCards = cards,
        Grids = grids
    };

    private static EntityCard ToCard(string source, Dictionary<string, string> row)
    {
        string? Pick(params string[] keys) => FirstValue(row, keys);
        var id = Pick("id", "Id", "mission_id", "user_id", "association_id") ?? "record";
        var title = Pick("title", "name", "display_name", "full_name", "email", "objective", "summary", "label") ?? "Database record";
        var subtitle = Pick("summary", "description", "sector", "role", "email", "city", "status") ?? source;
        var detail = Pick("details", "body", "message", "impact", "cause", "location", "region", "created_at") ?? "Live record from XAMPP MySQL";
        var status = Pick("status", "state", "verification_status", "approved", "role") ?? "Live";
        var date = Pick("created_at", "updated_at", "date", "starts_at", "start_date") ?? "";
        return new EntityCard { Id = id, Title = title, Subtitle = subtitle, Detail = detail, Status = status, Date = date, Source = source, Raw = row };
    }

    private static string? FirstValue(Dictionary<string, string> row, params string[] candidates)
    {
        foreach (var candidate in candidates)
            if (row.TryGetValue(candidate, out var value) && !string.IsNullOrWhiteSpace(value)) return value;
        foreach (var candidate in candidates)
        {
            var normalized = Normalize(candidate);
            var match = row.FirstOrDefault(x => Normalize(x.Key).Contains(normalized) || normalized.Contains(Normalize(x.Key)));
            if (!string.IsNullOrWhiteSpace(match.Value)) return match.Value;
        }
        return null;
    }

    private static IReadOnlyList<string> PreferredColumnsFor(string source, IReadOnlyList<Dictionary<string, string>> rows)
    {
        if (rows.Count == 0) return [];
        var all = rows.SelectMany(r => r.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var preferred = new[] { "id", "title", "name", "email", "role", "status", "city", "mission_id", "user_id", "created_at", "updated_at", "date", "summary", "description" };
        return preferred.Where(p => all.Any(a => string.Equals(a, p, StringComparison.OrdinalIgnoreCase))).Concat(all).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
    }

    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
