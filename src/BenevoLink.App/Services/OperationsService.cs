using System.Text.Json;
using BenevoLink.Models;

namespace BenevoLink.Services;

public sealed partial class OperationsService
{
    private readonly XamppMySql db;
    private readonly DatabaseCatalog catalog;
    private static readonly SemaphoreSlim ActionSchemaLock = new(1, 1);
    private static bool ActionSchemaReady;

    public OperationsService(XamppMySql db, DatabaseCatalog catalog)
    {
        this.db = db;
        this.catalog = catalog;
    }

    public async Task<IReadOnlyList<MissionActionItem>> ListMissionsAsync(AppUser? user, CancellationToken ct = default)
    {
        await PrepareAsync(ct);
        var list = new List<MissionActionItem>();

        await TryAsync(async () =>
        {
            var source = catalog.FindTable("v_public_missions", "missions");
            if (source is null) return;
            var rows = await db.QueryTableAsync(source, 120, null, ct);
            foreach (var row in rows)
            {
                var id = Pick(row, "id", "mission_id", "id_mission") ?? StableId(row);
                list.Add(new MissionActionItem
                {
                    Id = id,
                    Title = Pick(row, "title", "name", "mission_title", "objective") ?? "Mission",
                    Organization = Pick(row, "association_name", "organization", "ngo", "owner", "association", "created_by", "association_id") ?? "BenevoLink partner",
                    Location = Pick(row, "city", "location", "region", "address", "country") ?? "Hybrid",
                    Cause = Pick(row, "cause", "category", "sdg", "odd", "sector") ?? "Social impact",
                    Description = Pick(row, "summary", "description", "details", "body") ?? "Live mission from your benevolink database.",
                    Status = await MissionStatusOverlayAsync(source, id, Pick(row, "status", "state", "visibility", "approval_status") ?? "Open", ct),
                    Date = Pick(row, "starts_at", "start_date", "date", "created_at", "updated_at") ?? "",
                    Source = source,
                    ApplicationStatus = user is null ? null : await MissionApplicationStatusAsync(user, id, ct),
                    IsSaved = user is not null && await IsMissionSavedAsync(user, id, ct),
                    Raw = row
                });
            }
        });

        await TryAsync(async () =>
        {
            var rows = await db.QueryAsync("SELECT id,title,description,location,cause,status,event_date,organization_id,created_by,created_at FROM bl_missions ORDER BY id DESC LIMIT 120;", null, ct);
            foreach (var row in rows)
            {
                var id = "bl:" + (Pick(row, "id") ?? StableId(row));
                list.Add(new MissionActionItem
                {
                    Id = id,
                    Title = Pick(row, "title") ?? "Mission",
                    Organization = Pick(row, "organization_id", "created_by") ?? "BenevoLink workspace",
                    Location = Pick(row, "location") ?? "Hybrid",
                    Cause = Pick(row, "cause") ?? "Social impact",
                    Description = Pick(row, "description") ?? "Mission created inside the BenevoLink desktop workspace.",
                    Status = Pick(row, "status") ?? "Open",
                    Date = Pick(row, "event_date", "created_at") ?? "",
                    Source = "bl_missions",
                    ApplicationStatus = user is null ? null : await MissionApplicationStatusAsync(user, id, ct),
                    IsSaved = user is not null && await IsMissionSavedAsync(user, id, ct),
                    Raw = row
                });
            }
        });

        return list.GroupBy(x => x.Id).Select(g => g.First()).ToList();
    }

    public async Task<OperationResult> ApplyToMissionAsync(AppUser user, string missionId, string motivation, CancellationToken ct = default)
    {
        if (user.Role is not ("Volunteer" or "Admin")) return OperationResult.Fail("Only volunteer accounts can enroll in missions.");
        await PrepareAsync(ct);
        var title = await FindMissionTitleAsync(missionId, ct);
        var existing = await ScalarLongAsync("SELECT COUNT(*) FROM bl_applications WHERE mission_id=@m AND user_id=@u AND status <> 'Withdrawn';", new() { ["@m"] = missionId, ["@u"] = user.Id }, ct);
        if (existing > 0) return OperationResult.Success("You already enrolled for this mission. It is visible in Applications.");

        await ExecuteSafeAsync(@"INSERT INTO bl_applications(mission_id,user_id,applicant_name,mission_title,motivation,status,created_at,updated_at)
VALUES(@m,@u,@n,@t,@msg,'Pending',NOW(),NOW());", new()
        {
            ["@m"] = missionId,
            ["@u"] = user.Id,
            ["@n"] = user.DisplayName,
            ["@t"] = title,
            ["@msg"] = string.IsNullOrWhiteSpace(motivation) ? "I want to contribute to this mission through BenevoLink." : motivation.Trim()
        }, ct);

        await TryNativeApplicationInsertAsync(user, missionId, motivation, ct);
        await NotifyAsync(user.Id, "Mission enrollment submitted", $"Your application for {title} is pending review.", ct);
        await AuditAsync(user, "ApplyToMission", $"Submitted application for {missionId}.", ct);
        return OperationResult.Success("Application submitted. Organizations/Admin can now accept or reject it.");
    }

    public async Task<OperationResult> SaveMissionAsync(AppUser user, string missionId, CancellationToken ct = default)
    {
        await PrepareAsync(ct);
        await ExecuteSafeAsync("INSERT IGNORE INTO bl_saved_missions(mission_id,user_id,created_at) VALUES(@m,@u,NOW());", new() { ["@m"] = missionId, ["@u"] = user.Id }, ct);
        await TryNativeSimpleInsertAsync("saved_missions", new() { ["mission_id"] = missionId, ["user_id"] = user.Id, ["created_at"] = DateTime.Now }, ct);
        await AuditAsync(user, "SaveMission", $"Saved mission {missionId}.", ct);
        return OperationResult.Success("Mission saved.");
    }

    public async Task<OperationResult> CreateMissionAsync(AppUser user, string title, string description, string location, string cause, string date, CancellationToken ct = default)
    {
        if (user.Role is not ("Organization" or "Admin")) return OperationResult.Fail("Only organization/admin accounts can create missions.");
        if (string.IsNullOrWhiteSpace(title)) return OperationResult.Fail("Mission title is required.");
        await PrepareAsync(ct);
        var finalDate = string.IsNullOrWhiteSpace(date) ? DateTime.Today.AddDays(7).ToString("yyyy-MM-dd") : date.Trim();
        await ExecuteSafeAsync(@"INSERT INTO bl_missions(title,description,location,cause,status,event_date,organization_id,created_by,created_at,updated_at)
VALUES(@t,@d,@l,@c,@s,@dt,@o,@u,NOW(),NOW());", new()
        {
            ["@t"] = title.Trim(),
            ["@d"] = string.IsNullOrWhiteSpace(description) ? "Mission created from BenevoLink ImpactOS." : description.Trim(),
            ["@l"] = string.IsNullOrWhiteSpace(location) ? "Hybrid" : location.Trim(),
            ["@c"] = string.IsNullOrWhiteSpace(cause) ? "Social impact" : cause.Trim(),
            ["@s"] = user.Role == "Admin" ? "Open" : "Pending",
            ["@dt"] = finalDate,
            ["@o"] = user.Id,
            ["@u"] = user.Id
        }, ct);

        await TryNativeSimpleInsertAsync("missions", new()
        {
            ["title"] = title.Trim(), ["name"] = title.Trim(), ["mission_title"] = title.Trim(),
            ["description"] = description, ["summary"] = description, ["details"] = description,
            ["location"] = location, ["city"] = location, ["cause"] = cause, ["category"] = cause,
            ["status"] = user.Role == "Admin" ? "Open" : "Pending", ["state"] = user.Role == "Admin" ? "Open" : "Pending",
            ["association_id"] = user.Id, ["organization_id"] = user.Id, ["created_by"] = user.Id,
            ["starts_at"] = finalDate, ["start_date"] = finalDate, ["date"] = finalDate, ["created_at"] = DateTime.Now
        }, ct);
        await NotifyAdminsAsync("New mission created", $"{user.DisplayName} created mission: {title}.", ct);
        await AuditAsync(user, "CreateMission", $"Created mission {title}.", ct);
        return OperationResult.Success("Mission created and visible in the marketplace.");
    }

    public async Task<IReadOnlyList<ApplicationActionItem>> ListApplicationsAsync(AppUser user, CancellationToken ct = default)
    {
        await PrepareAsync(ct);
        var list = new List<ApplicationActionItem>();
        await TryAsync(async () =>
        {
            var table = catalog.FindTable("applications", "approval_requests");
            if (table is null) return;
            var rows = await QueryMaybeScopedToUserAsync(table, user, 120, ct);
            foreach (var row in rows)
            {
                var id = Pick(row, "id", "application_id", "request_id") ?? StableId(row);
                var overrideStatus = await ExternalDecisionStatusAsync(table, id, ct);
                list.Add(new ApplicationActionItem
                {
                    Id = id,
                    Applicant = Pick(row, "applicant_name", "volunteer_name", "user_name", "name", "user_id", "member_id", "email") ?? "Volunteer",
                    Mission = Pick(row, "mission_title", "mission", "mission_id", "title", "object_type") ?? "Mission / approval request",
                    Status = overrideStatus ?? Pick(row, "status", "state", "decision", "approval_status") ?? "Pending",
                    SubmittedAt = Pick(row, "created_at", "submitted_at", "date", "updated_at") ?? "",
                    Motivation = Pick(row, "motivation", "message", "cover_letter", "note", "notes", "reason") ?? "No message provided.",
                    Source = table,
                    Raw = row
                });
            }
        });
        await TryAsync(async () =>
        {
            var where = user.Role == "Volunteer" ? "WHERE user_id=@u" : "";
            var rows = await db.QueryAsync($"SELECT * FROM bl_applications {where} ORDER BY id DESC LIMIT 120;", user.Role == "Volunteer" ? new() { ["@u"] = user.Id } : null, ct);
            list.AddRange(rows.Select(row => new ApplicationActionItem
            {
                Id = "bl:" + (Pick(row, "id") ?? StableId(row)),
                Applicant = Pick(row, "applicant_name", "user_id") ?? "Volunteer",
                Mission = Pick(row, "mission_title", "mission_id") ?? "Mission",
                Status = Pick(row, "status") ?? "Pending",
                SubmittedAt = Pick(row, "created_at", "updated_at") ?? "",
                Motivation = Pick(row, "motivation", "review_note") ?? "",
                Source = "bl_applications",
                Raw = row
            }));
        });
        return list;
    }

    public async Task<OperationResult> DecideApplicationAsync(AppUser user, string applicationId, string decision, string note, CancellationToken ct = default)
    {
        if (user.Role is not ("Organization" or "Admin")) return OperationResult.Fail("Only organization/admin accounts can review applications.");
        await PrepareAsync(ct);
        var final = decision.Equals("reject", StringComparison.OrdinalIgnoreCase) ? "Rejected" : "Approved";
        if (applicationId.StartsWith("bl:", StringComparison.OrdinalIgnoreCase))
        {
            var id = applicationId[3..];
            await ExecuteSafeAsync("UPDATE bl_applications SET status=@s, review_note=@n, reviewed_by=@u, updated_at=NOW() WHERE id=@id LIMIT 1;", new() { ["@s"] = final, ["@n"] = note, ["@u"] = user.Id, ["@id"] = id }, ct);
            if (final == "Approved") await CreateParticipationFromBlApplicationAsync(id, user, ct);
        }
        else
        {
            await TryNativeApplicationDecisionAsync(user, applicationId, final, note, ct);
            await ExecuteSafeAsync("REPLACE INTO bl_external_decisions(source_table,external_id,status,note,decided_by,decided_at) VALUES('applications',@id,@s,@n,@u,NOW());", new() { ["@id"] = applicationId, ["@s"] = final, ["@n"] = note, ["@u"] = user.Id }, ct);
        }
        await AuditAsync(user, "ReviewApplication", $"{final} application {applicationId}.", ct);
        return OperationResult.Success($"Application {final.ToLowerInvariant()}.");
    }

    public async Task<IReadOnlyList<EventActionItem>> ListEventsAsync(AppUser? user, CancellationToken ct = default)
    {
        await PrepareAsync(ct);
        var list = new List<EventActionItem>();
        await TryAsync(async () =>
        {
            var table = catalog.FindTable("events", "feature_events");
            if (table is null) return;
            var rows = await db.QueryTableAsync(table, 120, null, ct);
            foreach (var row in rows)
            {
                var id = Pick(row, "id", "event_id") ?? StableId(row);
                list.Add(new EventActionItem
                {
                    Id = id,
                    Title = Pick(row, "title", "name", "event_title") ?? "Event",
                    Location = Pick(row, "location", "city", "region", "address") ?? "To be announced",
                    Date = Pick(row, "starts_at", "start_date", "date", "event_date", "created_at") ?? "",
                    Status = await ExternalStatusOverlayAsync(table, id, Pick(row, "status", "state") ?? "Open", ct),
                    RegistrationStatus = user is null ? "Not signed in" : await EventRegistrationStatusAsync(user, id, ct),
                    Source = table,
                    Raw = row
                });
            }
        });
        await TryAsync(async () =>
        {
            var rows = await db.QueryAsync("SELECT * FROM bl_events ORDER BY id DESC LIMIT 120;", null, ct);
            foreach (var row in rows)
            {
                var id = "bl:" + (Pick(row, "id") ?? StableId(row));
                list.Add(new EventActionItem
                {
                    Id = id,
                    Title = Pick(row, "title") ?? "Event",
                    Location = Pick(row, "location") ?? "To be announced",
                    Date = Pick(row, "event_date", "created_at") ?? "",
                    Status = Pick(row, "status") ?? "Open",
                    RegistrationStatus = user is null ? "Not signed in" : await EventRegistrationStatusAsync(user, id, ct),
                    Source = "bl_events",
                    Raw = row
                });
            }
        });
        return list.GroupBy(x => x.Id).Select(g => g.First()).ToList();
    }

    public async Task<OperationResult> RegisterForEventAsync(AppUser user, string eventId, CancellationToken ct = default)
    {
        await PrepareAsync(ct);
        await ExecuteSafeAsync(@"INSERT INTO bl_event_registrations(event_id,user_id,status,created_at,updated_at) VALUES(@e,@u,'Registered',NOW(),NOW())
ON DUPLICATE KEY UPDATE status='Registered', updated_at=NOW();", new() { ["@e"] = eventId, ["@u"] = user.Id }, ct);
        await TryNativeEventRegistrationAsync(user, eventId, "Registered", ct);
        await NotifyAsync(user.Id, "Event registration confirmed", $"You are registered for event #{eventId}.", ct);
        await AuditAsync(user, "RegisterEvent", $"Registered for event {eventId}.", ct);
        return OperationResult.Success("Event registration saved.");
    }

    public async Task<OperationResult> ValidateAttendanceAsync(AppUser user, string eventId, string volunteerId, decimal hours, CancellationToken ct = default)
    {
        if (user.Role is not ("Organization" or "Admin")) return OperationResult.Fail("Only organization/admin accounts can validate attendance.");
        if (string.IsNullOrWhiteSpace(volunteerId)) return OperationResult.Fail("Volunteer/User ID is required.");
        await PrepareAsync(ct);
        var safeHours = hours <= 0 ? 1 : hours;
        await ExecuteSafeAsync(@"INSERT INTO bl_event_registrations(event_id,user_id,status,hours,validated_by,created_at,updated_at) VALUES(@e,@u,'Attended',@h,@by,NOW(),NOW())
ON DUPLICATE KEY UPDATE status='Attended', hours=@h, validated_by=@by, updated_at=NOW();", new() { ["@e"] = eventId, ["@u"] = volunteerId.Trim(), ["@h"] = safeHours, ["@by"] = user.Id }, ct);
        await ExecuteSafeAsync("INSERT INTO bl_volunteer_hours(user_id,source_id,hours,note,validated_by,created_at) VALUES(@u,@s,@h,@n,@by,NOW());", new() { ["@u"] = volunteerId.Trim(), ["@s"] = eventId, ["@h"] = safeHours, ["@n"] = "Attendance validated", ["@by"] = user.Id }, ct);
        await TryNativeEventRegistrationAsync(new AppUser(volunteerId.Trim(), "", volunteerId.Trim(), "Volunteer", "Active", "users"), eventId, "Attended", ct);
        await AddVolunteerHoursNativeAsync(volunteerId.Trim(), eventId, safeHours, user, ct);
        await NotifyAsync(volunteerId.Trim(), "Attendance validated", $"Your attendance was validated for event #{eventId}.", ct);
        await AuditAsync(user, "ValidateAttendance", $"Validated {volunteerId} for event {eventId}.", ct);
        return OperationResult.Success("Attendance validated and hours recorded.");
    }

    public async Task<OperationResult> CreateEventAsync(AppUser user, string title, string location, string date, CancellationToken ct = default)
    {
        if (user.Role is not ("Organization" or "Admin")) return OperationResult.Fail("Only organization/admin accounts can create events.");
        if (string.IsNullOrWhiteSpace(title)) return OperationResult.Fail("Event title is required.");
        await PrepareAsync(ct);
        var finalDate = string.IsNullOrWhiteSpace(date) ? DateTime.Today.AddDays(14).ToString("yyyy-MM-dd") : date.Trim();
        await ExecuteSafeAsync("INSERT INTO bl_events(title,location,status,event_date,created_by,created_at,updated_at) VALUES(@t,@l,'Open',@d,@u,NOW(),NOW());", new() { ["@t"] = title.Trim(), ["@l"] = string.IsNullOrWhiteSpace(location) ? "To be announced" : location.Trim(), ["@d"] = finalDate, ["@u"] = user.Id }, ct);
        await TryNativeSimpleInsertAsync("events", new() { ["title"] = title.Trim(), ["name"] = title.Trim(), ["location"] = location, ["date"] = finalDate, ["event_date"] = finalDate, ["status"] = "Open", ["created_by"] = user.Id, ["created_at"] = DateTime.Now }, ct);
        await AuditAsync(user, "CreateEvent", $"Created event {title}.", ct);
        return OperationResult.Success("Event created and visible in the calendar.");
    }

    public async Task<IReadOnlyList<CertificateActionItem>> ListCertificatesAsync(AppUser user, CancellationToken ct = default)
    {
        await PrepareAsync(ct);
        var list = new List<CertificateActionItem>();
        await TryAsync(async () =>
        {
            var table = catalog.FindTable("participations", "volunteer_hours", "event_registrations");
            if (table is null) return;
            var rows = await QueryMaybeScopedToUserAsync(table, user, 120, ct);
            list.AddRange(rows.Where(r => IsPositiveStatus(Pick(r, "status", "state", "registration_status", "decision") ?? "Completed")).Select(row => new CertificateActionItem
            {
                Id = Pick(row, "id", "participation_id", "registration_id") ?? StableId(row),
                Volunteer = Pick(row, "volunteer_name", "user_name", "name", "user_id", "member_id", "volunteer_id") ?? user.DisplayName,
                Activity = Pick(row, "mission_title", "event_title", "activity", "mission_id", "event_id") ?? "Social impact activity",
                Organization = Pick(row, "organization", "association_name", "association_id", "ngo") ?? "BenevoLink",
                Hours = Pick(row, "hours", "total_hours", "duration", "volunteer_hours") ?? "0",
                Date = Pick(row, "validated_at", "completed_at", "created_at", "date", "updated_at") ?? DateTime.Today.ToString("yyyy-MM-dd"),
                Status = Pick(row, "status", "state", "registration_status", "decision") ?? "Completed",
                Source = table
            }));
        });
        await TryAsync(async () =>
        {
            var where = user.Role == "Volunteer" ? "WHERE user_id=@u" : "";
            var rows = await db.QueryAsync($"SELECT * FROM bl_event_registrations {where} ORDER BY updated_at DESC LIMIT 120;", user.Role == "Volunteer" ? new() { ["@u"] = user.Id } : null, ct);
            list.AddRange(rows.Where(r => IsPositiveStatus(Pick(r, "status") ?? "Registered")).Select(row => new CertificateActionItem
            {
                Id = "bl:" + (Pick(row, "id") ?? StableId(row)),
                Volunteer = Pick(row, "user_id") ?? user.DisplayName,
                Activity = "Event participation " + (Pick(row, "event_id") ?? ""),
                Organization = "BenevoLink",
                Hours = Pick(row, "hours") ?? "0",
                Date = Pick(row, "updated_at", "created_at") ?? "",
                Status = Pick(row, "status") ?? "Registered",
                Source = "bl_event_registrations"
            }));
        });
        return list;
    }

    public async Task<ProfileSnapshot> ProfileAsync(AppUser user, CancellationToken ct = default)
    {
        await PrepareAsync(ct);
        var table = catalog.FindTable("users");
        var rows = table is null ? [] : await QueryCurrentUserRowsAsync(table, user, ct);
        var row = rows.FirstOrDefault() ?? [];
        var edit = await LatestProfileEditAsync(user.Id, ct);
        var skills = await UserSkillsAsync(user, ct);
        return new ProfileSnapshot
        {
            Id = user.Id,
            Email = Pick(row, "email", "mail") ?? user.Email,
            Name = Pick(edit, "name") ?? Pick(row, "display_name", "full_name", "name", "username", "first_name") ?? user.DisplayName,
            Role = Pick(row, "role", "account_type", "type", "user_type") ?? user.Role,
            Status = Pick(row, "status", "state") ?? user.Status,
            City = Pick(edit, "city") ?? Pick(row, "city", "location", "region") ?? "",
            Phone = Pick(edit, "phone") ?? Pick(row, "phone", "mobile", "telephone") ?? "",
            Bio = Pick(edit, "bio") ?? Pick(row, "bio", "about", "description") ?? "",
            Skills = skills,
            RawRows = rows
        };
    }

    public async Task<OperationResult> UpdateProfileAsync(AppUser user, string name, string city, string phone, string bio, string skill, CancellationToken ct = default)
    {
        await PrepareAsync(ct);
        await ExecuteSafeAsync("INSERT INTO bl_profile_edits(user_id,name,city,phone,bio,created_at) VALUES(@u,@n,@c,@p,@b,NOW());", new() { ["@u"] = user.Id, ["@n"] = name, ["@c"] = city, ["@p"] = phone, ["@b"] = bio }, ct);
        await TryNativeProfileUpdateAsync(user, name, city, phone, bio, ct);
        if (!string.IsNullOrWhiteSpace(skill)) await AddSkillInternalAsync(user.Id, skill.Trim(), ct);
        await AuditAsync(user, "UpdateProfile", "Updated profile.", ct);
        return OperationResult.Success("Profile saved.");
    }

    public async Task<IReadOnlyList<NotificationActionItem>> ListNotificationsAsync(AppUser user, CancellationToken ct = default)
    {
        await PrepareAsync(ct);
        var list = new List<NotificationActionItem>();
        await TryAsync(async () =>
        {
            var table = catalog.FindTable("notifications", "association_messages", "feedbacks");
            if (table is null) return;
            var rows = await QueryMaybeScopedToUserAsync(table, user, 100, ct);
            foreach (var row in rows)
            {
                var id = Pick(row, "id", "notification_id", "message_id") ?? StableId(row);
                list.Add(new NotificationActionItem
                {
                    Id = id,
                    Title = Pick(row, "title", "subject", "type", "event_type") ?? "Notification",
                    Body = Pick(row, "body", "message", "content", "description") ?? "Live message from the database.",
                    Status = await ExternalStatusOverlayAsync(table, id, Pick(row, "status", "state", "read_status", "is_read") ?? "Unread", ct),
                    Date = Pick(row, "created_at", "date", "updated_at", "sent_at") ?? "",
                    Source = table
                });
            }
        });
        await TryAsync(async () =>
        {
            var rows = await db.QueryAsync("SELECT * FROM bl_notifications WHERE user_id=@u OR user_id='all' ORDER BY id DESC LIMIT 100;", new() { ["@u"] = user.Id }, ct);
            list.AddRange(rows.Select(row => new NotificationActionItem
            {
                Id = "bl:" + (Pick(row, "id") ?? StableId(row)),
                Title = Pick(row, "title") ?? "Notification",
                Body = Pick(row, "body") ?? "",
                Status = Pick(row, "status") ?? "Unread",
                Date = Pick(row, "created_at") ?? "",
                Source = "bl_notifications"
            }));
        });
        return list;
    }

    public async Task<OperationResult> MarkNotificationReadAsync(AppUser user, string notificationId, CancellationToken ct = default)
    {
        await PrepareAsync(ct);
        if (notificationId.StartsWith("bl:", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteSafeAsync("UPDATE bl_notifications SET status='Read', read_at=NOW() WHERE id=@id LIMIT 1;", new() { ["@id"] = notificationId[3..] }, ct);
        }
        else
        {
            await TryNativeNotificationReadAsync(notificationId, ct);
            var source = catalog.FindTable("notifications", "association_messages", "feedbacks") ?? "notifications";
            await ExecuteSafeAsync("REPLACE INTO bl_external_decisions(source_table,external_id,status,note,decided_by,decided_at) VALUES(@t,@id,'Read','notification read',@u,NOW());", new() { ["@t"] = source, ["@id"] = notificationId, ["@u"] = user.Id }, ct);
        }
        await AuditAsync(user, "MarkNotificationRead", $"Read notification {notificationId}.", ct);
        return OperationResult.Success("Notification marked as read.");
    }

    public async Task<OperationResult> VerifyAssociationAsync(AppUser user, string associationId, string decision, CancellationToken ct = default)
    {
        if (user.Role != "Admin") return OperationResult.Fail("Only admin accounts can verify associations.");
        await PrepareAsync(ct);
        var final = decision.Equals("reject", StringComparison.OrdinalIgnoreCase) ? "Rejected" : "Verified";
        await TryNativeAssociationStatusAsync(associationId, final, user.Id, ct);
        await ExecuteSafeAsync("REPLACE INTO bl_external_decisions(source_table,external_id,status,note,decided_by,decided_at) VALUES('associations',@id,@s,'organization verification',@u,NOW());", new() { ["@id"] = associationId, ["@s"] = final, ["@u"] = user.Id }, ct);
        await ExecuteSafeAsync("UPDATE bl_organization_profiles SET status=@s, updated_at=NOW() WHERE association_id=@a;", new() { ["@a"] = associationId, ["@s"] = final }, ct);
        await ExecuteSafeAsync("INSERT INTO bl_organization_requests(association_id,user_id,subject,message,status,created_at,updated_at) VALUES(@a,@u,'Admin verification',@m,@s,NOW(),NOW());", new() { ["@a"] = associationId, ["@u"] = user.Id, ["@m"] = $"Admin set association {associationId} to {final}.", ["@s"] = final }, ct);
        await AuditAsync(user, "VerifyAssociation", $"{final} association {associationId}.", ct);
        return OperationResult.Success($"Association {final.ToLowerInvariant()}.");
    }

    public async Task<IReadOnlyList<OrganizationActionItem>> ListOrganizationsAsync(AppUser? user, CancellationToken ct = default)
    {
        await PrepareAsync(ct);
        var list = new List<OrganizationActionItem>();
        await TryAsync(async () =>
        {
            var table = catalog.FindTable("associations", "organizations", "ngos");
            if (table is null) return;
            var rows = await db.QueryTableAsync(table, 120, null, ct);
            foreach (var row in rows)
            {
                var id = Pick(row, "id", "association_id", "organization_id", "ngo_id") ?? StableId(row);
                list.Add(new OrganizationActionItem
                {
                    Id = id,
                    Name = Pick(row, "name", "association_name", "organization_name", "ngo_name", "title") ?? "Organization",
                    Sector = Pick(row, "sector", "cause", "category", "domain", "type") ?? "Social impact",
                    City = Pick(row, "city", "location", "region", "address", "country") ?? "",
                    Status = await OrganizationStatusOverlayAsync(id, Pick(row, "verification_status", "status", "state", "approved") ?? "Active", ct),
                    Email = Pick(row, "email", "contact_email", "mail") ?? "",
                    Description = Pick(row, "description", "bio", "about", "mission", "summary") ?? "Verified BenevoLink partner organization.",
                    Source = table,
                    Raw = row
                });
            }
        });
        await TryAsync(async () =>
        {
            var rows = await db.QueryAsync("SELECT * FROM bl_organization_profiles ORDER BY updated_at DESC, id DESC LIMIT 120;", null, ct);
            list.AddRange(rows.Select(row => new OrganizationActionItem
            {
                Id = Pick(row, "association_id", "id") ?? StableId(row),
                Name = Pick(row, "name") ?? "Organization",
                Sector = Pick(row, "sector") ?? "Social impact",
                City = Pick(row, "city") ?? "",
                Status = Pick(row, "status") ?? "Saved",
                Email = Pick(row, "email") ?? "",
                Description = Pick(row, "description") ?? "BenevoLink saved organization profile.",
                Source = "bl_organization_profiles",
                Raw = row
            }));
        });
        return list.OrderByDescending(x => x.Source.Equals("bl_organization_profiles", StringComparison.OrdinalIgnoreCase)).GroupBy(x => x.Id).Select(g => g.First()).ToList();
    }

    public async Task<OperationResult> SaveOrganizationProfileAsync(AppUser user, string associationId, string name, string sector, string city, string email, string description, CancellationToken ct = default)
    {
        if (user.Role is not ("Organization" or "Admin")) return OperationResult.Fail("Only organization/admin accounts can edit organization profiles.");
        await PrepareAsync(ct);
        var orgId = string.IsNullOrWhiteSpace(associationId) ? user.Id : associationId.Trim();
        await TryNativeOrganizationUpdateAsync(orgId, name, sector, city, email, description, ct);
        await ExecuteSafeAsync(@"INSERT INTO bl_organization_profiles(association_id,user_id,name,sector,city,email,description,status,created_at,updated_at)
VALUES(@a,@u,@n,@s,@c,@e,@d,'Saved',NOW(),NOW())
ON DUPLICATE KEY UPDATE user_id=@u,name=@n,sector=@s,city=@c,email=@e,description=@d,status='Saved',updated_at=NOW();",
            new() { ["@a"] = orgId, ["@u"] = user.Id, ["@n"] = name, ["@s"] = sector, ["@c"] = city, ["@e"] = email, ["@d"] = description }, ct);
        await ExecuteSafeAsync("INSERT INTO bl_organization_requests(association_id,user_id,subject,message,status,created_at,updated_at) VALUES(@a,@u,'Profile update',@m,'Saved',NOW(),NOW());", new() { ["@a"] = orgId, ["@u"] = user.Id, ["@m"] = $"Name={name}; Sector={sector}; City={city}; Email={email}; Description={description}" }, ct);
        await AuditAsync(user, "UpdateOrganization", "Saved organization profile.", ct);
        return OperationResult.Success("Organization profile saved.");
    }

    public async Task<OperationResult> CreateVerificationRequestAsync(AppUser user, string subject, string message, CancellationToken ct = default)
    {
        if (user.Role is not ("Organization" or "Admin")) return OperationResult.Fail("Only organization/admin accounts can request verification.");
        await PrepareAsync(ct);
        await ExecuteSafeAsync("INSERT INTO bl_organization_requests(association_id,user_id,subject,message,status,created_at,updated_at) VALUES(@a,@u,@s,@m,'Pending',NOW(),NOW());", new() { ["@a"] = user.Id, ["@u"] = user.Id, ["@s"] = string.IsNullOrWhiteSpace(subject) ? "Verification request" : subject.Trim(), ["@m"] = string.IsNullOrWhiteSpace(message) ? "Please verify this organization." : message.Trim() }, ct);
        await TryNativeSimpleInsertAsync(catalog.FindTable("approval_requests", "feedbacks", "association_messages") ?? "approval_requests", new() { ["user_id"] = user.Id, ["association_id"] = user.Id, ["subject"] = subject, ["title"] = "NGO verification", ["message"] = message, ["status"] = "Pending", ["created_at"] = DateTime.Now }, ct);
        await NotifyAdminsAsync("Verification request", $"{user.DisplayName} requested organization verification.", ct);
        await AuditAsync(user, "RequestVerification", "Submitted organization verification request.", ct);
        return OperationResult.Success("Verification request sent.");
    }

    public async Task<OperationResult> SendMessageAsync(AppUser user, string recipientId, string title, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recipientId)) return OperationResult.Fail("Recipient user ID is required.");
        await PrepareAsync(ct);
        await ExecuteSafeAsync("INSERT INTO bl_messages(sender_id,recipient_id,title,body,status,created_at) VALUES(@s,@r,@t,@b,'Unread',NOW());", new() { ["@s"] = user.Id, ["@r"] = recipientId.Trim(), ["@t"] = string.IsNullOrWhiteSpace(title) ? "BenevoLink message" : title.Trim(), ["@b"] = string.IsNullOrWhiteSpace(message) ? "You received a new BenevoLink message." : message.Trim() }, ct);
        await NotifyAsync(recipientId.Trim(), string.IsNullOrWhiteSpace(title) ? "BenevoLink message" : title.Trim(), string.IsNullOrWhiteSpace(message) ? "You received a new BenevoLink message." : message.Trim(), ct);
        await AuditAsync(user, "SendMessage", $"Sent message to {recipientId}.", ct);
        return OperationResult.Success("Message sent.");
    }

    public async Task<IReadOnlyList<VolunteerActionItem>> ListVolunteersAsync(CancellationToken ct = default)
    {
        await PrepareAsync(ct);
        var list = new List<VolunteerActionItem>();
        await TryAsync(async () =>
        {
            var table = catalog.FindTable("users", "members", "accounts");
            if (table is null) return;
            var rows = await db.QueryTableAsync(table, 180, null, ct);
            foreach (var row in rows)
            {
                var id = Pick(row, "id", "user_id", "member_id") ?? StableId(row);
                var email = Pick(row, "email", "mail", "user_email") ?? "";
                list.Add(new VolunteerActionItem
                {
                    Id = id,
                    Name = Pick(row, "display_name", "full_name", "name", "username", "first_name") ?? (email.Contains('@') ? email.Split('@')[0] : id),
                    Email = email,
                    Role = Pick(row, "role", "account_type", "type", "user_type") ?? "Volunteer",
                    City = Pick(row, "city", "location", "region") ?? "",
                    Status = Pick(row, "status", "state") ?? "Active",
                    Hours = await TotalHoursForUserIdAsync(id, ct),
                    Skills = string.Join(", ", await SkillsForUserIdListAsync(id, ct)),
                    Source = table,
                    Raw = row
                });
            }
        });
        return list;
    }

    public async Task<OperationResult> AddSkillToVolunteerAsync(AppUser actor, string volunteerId, string skill, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(volunteerId)) volunteerId = actor.Id;
        if (actor.Role == "Volunteer" && !string.Equals(volunteerId.Trim(), actor.Id, StringComparison.OrdinalIgnoreCase)) return OperationResult.Fail("Volunteer accounts can add skills only to their own profile.");
        if (string.IsNullOrWhiteSpace(skill)) return OperationResult.Fail("Skill name is required.");
        await PrepareAsync(ct);
        await AddSkillInternalAsync(volunteerId.Trim(), skill.Trim(), ct);
        await AuditAsync(actor, "AddVolunteerSkill", $"Added {skill} to {volunteerId}.", ct);
        return OperationResult.Success("Skill added.");
    }

    public async Task<OperationResult> RecordManualHoursAsync(AppUser actor, string volunteerId, string sourceId, decimal hours, string note, CancellationToken ct = default)
    {
        if (actor.Role is not ("Organization" or "Admin")) return OperationResult.Fail("Only organization/admin accounts can record hours.");
        if (string.IsNullOrWhiteSpace(volunteerId)) return OperationResult.Fail("Volunteer/User ID is required.");
        await PrepareAsync(ct);
        var safeHours = hours <= 0 ? 1 : hours;
        await ExecuteSafeAsync("INSERT INTO bl_volunteer_hours(user_id,source_id,hours,note,validated_by,created_at) VALUES(@u,@s,@h,@n,@by,NOW());", new() { ["@u"] = volunteerId.Trim(), ["@s"] = sourceId, ["@h"] = safeHours, ["@n"] = note, ["@by"] = actor.Id }, ct);
        await AddVolunteerHoursNativeAsync(volunteerId.Trim(), sourceId, safeHours, actor, ct);
        await AuditAsync(actor, "RecordHours", $"Recorded {safeHours} hours for {volunteerId}.", ct);
        return OperationResult.Success("Volunteer hours recorded.");
    }

    public async Task<IReadOnlyList<ImpactGoalActionItem>> ListImpactGoalsAsync(CancellationToken ct = default)
    {
        await PrepareAsync(ct);
        var list = new List<ImpactGoalActionItem>();
        await TryAsync(async () =>
        {
            var table = catalog.FindTable("impact_goals");
            if (table is not null)
            {
                var rows = await db.QueryTableAsync(table, 120, null, ct);
                foreach (var row in rows)
                {
                    var id = Pick(row, "id", "goal_id") ?? StableId(row);
                    list.Add(new ImpactGoalActionItem
                    {
                        Id = id,
                        Title = Pick(row, "title", "name", "goal") ?? "Impact goal",
                        Target = Pick(row, "target", "target_value", "objective") ?? "",
                        Current = Pick(row, "current", "current_value", "progress") ?? "0",
                        Status = await ExternalStatusOverlayAsync("impact_goals", id, Pick(row, "status", "state") ?? "Active", ct),
                        Sdg = Pick(row, "sdg", "odd", "sdg_code") ?? "",
                        Date = Pick(row, "deadline", "created_at", "updated_at", "date") ?? "",
                        Source = table,
                        Raw = row
                    });
                }
            }
        });
        await TryAsync(async () =>
        {
            var rows = await db.QueryAsync("SELECT * FROM bl_impact_goals ORDER BY id DESC LIMIT 120;", null, ct);
            list.AddRange(rows.Select(row => new ImpactGoalActionItem
            {
                Id = "bl:" + (Pick(row, "id") ?? StableId(row)),
                Title = Pick(row, "title") ?? "Impact goal",
                Target = Pick(row, "target_value") ?? "",
                Current = Pick(row, "current_value") ?? "0",
                Status = Pick(row, "status") ?? "Active",
                Sdg = Pick(row, "sdg") ?? "",
                Date = Pick(row, "deadline", "created_at") ?? "",
                Source = "bl_impact_goals",
                Raw = row
            }));
        });
        return list;
    }

    public async Task<OperationResult> CreateImpactGoalAsync(AppUser user, string title, string sdg, string target, string deadline, CancellationToken ct = default)
    {
        if (user.Role is not ("Organization" or "Admin")) return OperationResult.Fail("Only organization/admin accounts can create impact goals.");
        if (string.IsNullOrWhiteSpace(title)) return OperationResult.Fail("Goal title is required.");
        await PrepareAsync(ct);
        await ExecuteSafeAsync("INSERT INTO bl_impact_goals(title,sdg,target_value,current_value,status,deadline,created_by,created_at,updated_at) VALUES(@t,@s,@target,0,'Active',@d,@u,NOW(),NOW());", new() { ["@t"] = title.Trim(), ["@s"] = sdg, ["@target"] = target, ["@d"] = deadline, ["@u"] = user.Id }, ct);
        await TryNativeSimpleInsertAsync("impact_goals", new() { ["title"] = title, ["name"] = title, ["sdg"] = sdg, ["odd"] = sdg, ["target"] = target, ["target_value"] = target, ["current"] = 0, ["current_value"] = 0, ["status"] = "Active", ["deadline"] = deadline, ["created_by"] = user.Id, ["created_at"] = DateTime.Now }, ct);
        await AuditAsync(user, "CreateImpactGoal", $"Created goal {title}.", ct);
        return OperationResult.Success("Impact goal created.");
    }

    public async Task<OperationResult> UpdateImpactGoalAsync(AppUser user, string goalId, string progress, string status, CancellationToken ct = default)
    {
        if (user.Role is not ("Organization" or "Admin")) return OperationResult.Fail("Only organization/admin accounts can update impact goals.");
        await PrepareAsync(ct);
        if (goalId.StartsWith("bl:", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteSafeAsync("UPDATE bl_impact_goals SET current_value=@p,status=@s,updated_at=NOW() WHERE id=@id LIMIT 1;", new() { ["@p"] = progress, ["@s"] = string.IsNullOrWhiteSpace(status) ? "Active" : status, ["@id"] = goalId[3..] }, ct);
        }
        else
        {
            await TryNativeGoalUpdateAsync(goalId, progress, status, ct);
            await ExecuteSafeAsync("REPLACE INTO bl_external_decisions(source_table,external_id,status,note,decided_by,decided_at) VALUES('impact_goals',@id,@s,@n,@u,NOW());", new() { ["@id"] = goalId, ["@s"] = string.IsNullOrWhiteSpace(status) ? "Updated" : status, ["@n"] = progress, ["@u"] = user.Id }, ct);
        }
        await AuditAsync(user, "UpdateImpactGoal", $"Updated goal {goalId}.", ct);
        return OperationResult.Success("Impact goal updated.");
    }

    public async Task<OperationResult> MarkAllNotificationsReadAsync(AppUser user, CancellationToken ct = default)
    {
        await PrepareAsync(ct);
        await ExecuteSafeAsync("UPDATE bl_notifications SET status='Read', read_at=NOW() WHERE user_id=@u OR user_id='all';", new() { ["@u"] = user.Id }, ct);
        await TryNativeMarkAllNotificationsReadAsync(user, ct);
        await AuditAsync(user, "MarkAllNotificationsRead", "Marked all notifications read.", ct);
        return OperationResult.Success("All notifications marked as read.");
    }

    public async Task<IReadOnlyList<AuditActionItem>> ListAuditAsync(CancellationToken ct = default)
    {
        await PrepareAsync(ct);
        var list = new List<AuditActionItem>();
        await TryAsync(async () =>
        {
            var table = catalog.FindTable("audit_logs");
            if (table is not null)
            {
                var rows = await db.QueryTableAsync(table, 120, null, ct);
                list.AddRange(rows.Select(row => new AuditActionItem
                {
                    Id = Pick(row, "id", "log_id") ?? StableId(row),
                    Type = Pick(row, "event_type", "type", "action") ?? "Action",
                    Message = Pick(row, "message", "description", "details") ?? "Audit event",
                    Actor = Pick(row, "user_id", "actor_id", "admin_id", "email") ?? "system",
                    Date = Pick(row, "created_at", "date") ?? "",
                    Source = table
                }));
            }
        });
        await TryAsync(async () =>
        {
            var rows = await db.QueryAsync("SELECT * FROM bl_action_audit ORDER BY id DESC LIMIT 120;", null, ct);
            list.AddRange(rows.Select(row => new AuditActionItem
            {
                Id = "bl:" + (Pick(row, "id") ?? StableId(row)),
                Type = Pick(row, "action_type") ?? "Action",
                Message = Pick(row, "message") ?? "",
                Actor = Pick(row, "user_id") ?? "system",
                Date = Pick(row, "created_at") ?? "",
                Source = "bl_action_audit"
            }));
        });
        return list;
    }

    public async Task<DatabaseTableWorkspace> DatabaseWorkspaceAsync(string? selectedTable, int limit = 80, CancellationToken ct = default)
    {
        await PrepareAsync(ct);
        var tables = catalog.Tables.Concat(await ActionTableInfoAsync(ct)).GroupBy(t => t.Name).Select(g => g.First()).OrderBy(t => t.Name).ToList();
        var selected = string.IsNullOrWhiteSpace(selectedTable) ? tables.FirstOrDefault(t => t.Type == "Table")?.Name ?? "users" : selectedTable;
        var columns = await ColumnsForTableAsync(selected, ct);
        var rows = await SafeQueryTableAsync(selected, limit, ct);
        var idCol = columns.FirstOrDefault(c => c.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase))?.Name ?? columns.FirstOrDefault(c => c.Name.Equals("id", StringComparison.OrdinalIgnoreCase))?.Name ?? columns.FirstOrDefault()?.Name ?? "";
        return new DatabaseTableWorkspace { SelectedTable = selected, Tables = tables, Columns = columns, Rows = rows, IdColumn = idCol };
    }

    public async Task<OperationResult> UpdateDatabaseCellAsync(AppUser user, string table, string rowId, string column, string value, CancellationToken ct = default)
    {
        if (user.Role != "Admin") return OperationResult.Fail("Only admins can edit database records.");
        await PrepareAsync(ct);
        var columns = await ColumnsForTableAsync(table, ct);
        var idCol = columns.FirstOrDefault(c => c.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase))?.Name ?? columns.FirstOrDefault(c => c.Name.Equals("id", StringComparison.OrdinalIgnoreCase))?.Name;
        if (string.IsNullOrWhiteSpace(idCol) || columns.All(c => !c.Name.Equals(column, StringComparison.OrdinalIgnoreCase))) return OperationResult.Fail("Select a valid table, id, and column.");
        try
        {
            var affected = await db.ExecuteAsync($"UPDATE {Q(table)} SET {Q(column)}=@v WHERE {Q(idCol)}=@id LIMIT 1;", new() { ["@v"] = value, ["@id"] = rowId }, ct);
            await AuditAsync(user, "DatabaseUpdate", $"Updated {table}.{column}.", ct);
            return affected > 0 ? OperationResult.Success("Database cell updated.") : OperationResult.Fail("No matching row was updated.");
        }
        catch (Exception ex) { return OperationResult.Fail("Database update failed: " + XamppMySql.CleanError(ex.Message)); }
    }

    public async Task<OperationResult> DeleteDatabaseRowAsync(AppUser user, string table, string rowId, CancellationToken ct = default)
    {
        if (user.Role != "Admin") return OperationResult.Fail("Only admins can delete database records.");
        await PrepareAsync(ct);
        var columns = await ColumnsForTableAsync(table, ct);
        var idCol = columns.FirstOrDefault(c => c.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase))?.Name ?? columns.FirstOrDefault(c => c.Name.Equals("id", StringComparison.OrdinalIgnoreCase))?.Name;
        if (string.IsNullOrWhiteSpace(idCol)) return OperationResult.Fail("This table has no editable id column.");
        try
        {
            var affected = await db.ExecuteAsync($"DELETE FROM {Q(table)} WHERE {Q(idCol)}=@id LIMIT 1;", new() { ["@id"] = rowId }, ct);
            await AuditAsync(user, "DatabaseDelete", $"Deleted row {rowId} from {table}.", ct);
            return affected > 0 ? OperationResult.Success("Database row deleted.") : OperationResult.Fail("No matching row was deleted.");
        }
        catch (Exception ex) { return OperationResult.Fail("Database delete failed: " + XamppMySql.CleanError(ex.Message)); }
    }

    public async Task<OperationResult> InsertDatabaseRecordAsync(AppUser user, string table, string keyValueLines, CancellationToken ct = default)
    {
        if (user.Role != "Admin") return OperationResult.Fail("Only admins can insert database records.");
        await PrepareAsync(ct);
        var parsed = ParseKeyValues(keyValueLines);
        var result = await InsertAnyTableAsync(table, parsed, ct);
        if (result.Succeeded) await AuditAsync(user, "DatabaseInsert", $"Inserted row into {table}.", ct);
        return result;
    }

    public async Task<OperationResult> CreateUserAsync(string name, string email, string password, string role, CancellationToken ct = default)
    {
        await PrepareAsync(ct);
        var table = catalog.FindTable("users", "members", "accounts");
        if (table is null) return OperationResult.Fail("No users table exists.");
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) return OperationResult.Fail("Email and password are required.");
        var existing = await ScalarLongAsync($"SELECT COUNT(*) FROM {Q(table)} WHERE LOWER({Q(catalog.FindColumn(table, "email", "mail", "user_email") ?? "email")})=@e;", new() { ["@e"] = email.Trim().ToLowerInvariant() }, ct);
        if (existing > 0) return OperationResult.Fail("An account with this email already exists.");
        return await InsertAnyTableAsync(table, new()
        {
            ["display_name"] = string.IsNullOrWhiteSpace(name) ? email.Split('@')[0] : name.Trim(),
            ["full_name"] = string.IsNullOrWhiteSpace(name) ? email.Split('@')[0] : name.Trim(),
            ["name"] = string.IsNullOrWhiteSpace(name) ? email.Split('@')[0] : name.Trim(),
            ["email"] = email.Trim().ToLowerInvariant(), ["mail"] = email.Trim().ToLowerInvariant(),
            ["password"] = password, ["password_hash"] = password, ["role"] = role, ["type"] = role,
            ["status"] = role.Equals("Organization", StringComparison.OrdinalIgnoreCase) ? "Pending" : "Active",
            ["created_at"] = DateTime.Now
        }, ct);
    }

    public async Task<OperationResult> UpdateMissionStatusAsync(AppUser user, string missionId, string status, CancellationToken ct = default)
    {
        if (user.Role is not ("Organization" or "Admin")) return OperationResult.Fail("Only organization/admin accounts can moderate missions.");
        await PrepareAsync(ct);
        if (missionId.StartsWith("bl:", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteSafeAsync("UPDATE bl_missions SET status=@s,updated_at=NOW() WHERE id=@id LIMIT 1;", new() { ["@s"] = status, ["@id"] = missionId[3..] }, ct);
        }
        else
        {
            await TryNativeStatusUpdateAsync("missions", missionId, status, ct);
            await ExecuteSafeAsync("REPLACE INTO bl_external_decisions(source_table,external_id,status,note,decided_by,decided_at) VALUES('missions',@id,@s,'status',@u,NOW());", new() { ["@id"] = missionId, ["@s"] = status, ["@u"] = user.Id }, ct);
        }
        await AuditAsync(user, "MissionStatus", $"Set mission {missionId} to {status}.", ct);
        return OperationResult.Success($"Mission set to {status}.");
    }

    public async Task<OperationResult> WithdrawMissionApplicationAsync(AppUser user, string missionId, CancellationToken ct = default)
    {
        await PrepareAsync(ct);
        await ExecuteSafeAsync("UPDATE bl_applications SET status='Withdrawn',updated_at=NOW() WHERE mission_id=@m AND user_id=@u AND status <> 'Withdrawn';", new() { ["@m"] = missionId, ["@u"] = user.Id }, ct);
        await TryNativeWithdrawAsync(user, missionId, ct);
        await AuditAsync(user, "WithdrawApplication", $"Withdrew application for mission {missionId}.", ct);
        return OperationResult.Success("Application withdrawn.");
    }

    public async Task<OperationResult> UpdateEventStatusAsync(AppUser user, string eventId, string status, CancellationToken ct = default)
    {
        if (user.Role is not ("Organization" or "Admin")) return OperationResult.Fail("Only organization/admin accounts can update events.");
        await PrepareAsync(ct);
        if (eventId.StartsWith("bl:", StringComparison.OrdinalIgnoreCase))
            await ExecuteSafeAsync("UPDATE bl_events SET status=@s,updated_at=NOW() WHERE id=@id LIMIT 1;", new() { ["@s"] = status, ["@id"] = eventId[3..] }, ct);
        else
        {
            var source = catalog.FindTable("events", "feature_events") ?? "events";
            await TryNativeStatusUpdateAsync(source, eventId, status, ct);
            await ExecuteSafeAsync("REPLACE INTO bl_external_decisions(source_table,external_id,status,note,decided_by,decided_at) VALUES(@t,@id,@s,'event status',@u,NOW());", new() { ["@t"] = source, ["@id"] = eventId, ["@s"] = status, ["@u"] = user.Id }, ct);
        }
        await AuditAsync(user, "EventStatus", $"Set event {eventId} to {status}.", ct);
        return OperationResult.Success($"Event set to {status}.");
    }

    public async Task<OperationResult> CancelEventRegistrationAsync(AppUser user, string eventId, CancellationToken ct = default)
    {
        await PrepareAsync(ct);
        await ExecuteSafeAsync("UPDATE bl_event_registrations SET status='Cancelled',updated_at=NOW() WHERE event_id=@e AND user_id=@u LIMIT 1;", new() { ["@e"] = eventId, ["@u"] = user.Id }, ct);
        await TryNativeEventRegistrationAsync(user, eventId, "Cancelled", ct);
        await AuditAsync(user, "CancelEventRegistration", $"Cancelled event registration {eventId}.", ct);
        return OperationResult.Success("Event registration cancelled.");
    }

    private async Task PrepareAsync(CancellationToken ct)
    {
        await catalog.EnsureLoadedAsync(db, ct);
        await EnsureActionTablesAsync(ct);
    }

    private async Task EnsureActionTablesAsync(CancellationToken ct)
    {
        if (ActionSchemaReady) return;
        await ActionSchemaLock.WaitAsync(ct);
        try
        {
            if (ActionSchemaReady) return;
            var statements = new[]
            {
                @"CREATE TABLE IF NOT EXISTS bl_missions (id BIGINT AUTO_INCREMENT PRIMARY KEY,title VARCHAR(255) NOT NULL,description TEXT NULL,location VARCHAR(255) NULL,cause VARCHAR(160) NULL,status VARCHAR(60) NOT NULL DEFAULT 'Open',event_date VARCHAR(80) NULL,organization_id VARCHAR(80) NULL,created_by VARCHAR(80) NULL,created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,updated_at DATETIME NULL, INDEX(status), INDEX(created_by)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS bl_applications (id BIGINT AUTO_INCREMENT PRIMARY KEY,mission_id VARCHAR(80) NOT NULL,user_id VARCHAR(80) NOT NULL,applicant_name VARCHAR(255) NULL,mission_title VARCHAR(255) NULL,motivation TEXT NULL,status VARCHAR(60) NOT NULL DEFAULT 'Pending',review_note TEXT NULL,reviewed_by VARCHAR(80) NULL,created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,updated_at DATETIME NULL, INDEX(mission_id), INDEX(user_id), INDEX(status)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS bl_saved_missions (id BIGINT AUTO_INCREMENT PRIMARY KEY,mission_id VARCHAR(80) NOT NULL,user_id VARCHAR(80) NOT NULL,created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, UNIQUE KEY uq_saved(user_id,mission_id)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS bl_events (id BIGINT AUTO_INCREMENT PRIMARY KEY,title VARCHAR(255) NOT NULL,location VARCHAR(255) NULL,status VARCHAR(60) NOT NULL DEFAULT 'Open',event_date VARCHAR(80) NULL,created_by VARCHAR(80) NULL,created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,updated_at DATETIME NULL, INDEX(status)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS bl_event_registrations (id BIGINT AUTO_INCREMENT PRIMARY KEY,event_id VARCHAR(80) NOT NULL,user_id VARCHAR(80) NOT NULL,status VARCHAR(60) NOT NULL DEFAULT 'Registered',hours DECIMAL(8,2) NULL,validated_by VARCHAR(80) NULL,created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,updated_at DATETIME NULL, UNIQUE KEY uq_event_user(event_id,user_id), INDEX(status)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS bl_notifications (id BIGINT AUTO_INCREMENT PRIMARY KEY,user_id VARCHAR(80) NOT NULL,title VARCHAR(255) NOT NULL,body TEXT NULL,status VARCHAR(60) NOT NULL DEFAULT 'Unread',created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,read_at DATETIME NULL, INDEX(user_id), INDEX(status)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS bl_skills (id BIGINT AUTO_INCREMENT PRIMARY KEY,user_id VARCHAR(80) NOT NULL,skill VARCHAR(160) NOT NULL,created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, UNIQUE KEY uq_skill(user_id,skill)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS bl_volunteer_hours (id BIGINT AUTO_INCREMENT PRIMARY KEY,user_id VARCHAR(80) NOT NULL,source_id VARCHAR(80) NULL,hours DECIMAL(8,2) NOT NULL DEFAULT 1,note TEXT NULL,validated_by VARCHAR(80) NULL,created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, INDEX(user_id)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS bl_impact_goals (id BIGINT AUTO_INCREMENT PRIMARY KEY,title VARCHAR(255) NOT NULL,sdg VARCHAR(80) NULL,target_value VARCHAR(120) NULL,current_value VARCHAR(120) NULL,status VARCHAR(60) NOT NULL DEFAULT 'Active',deadline VARCHAR(80) NULL,created_by VARCHAR(80) NULL,created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,updated_at DATETIME NULL) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS bl_organization_profiles (id BIGINT AUTO_INCREMENT PRIMARY KEY,association_id VARCHAR(80) NOT NULL,user_id VARCHAR(80) NULL,name VARCHAR(255) NULL,sector VARCHAR(160) NULL,city VARCHAR(160) NULL,email VARCHAR(255) NULL,description TEXT NULL,status VARCHAR(80) NOT NULL DEFAULT 'Draft',created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,updated_at DATETIME NULL, UNIQUE KEY uq_org_profile(association_id)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS bl_organization_requests (id BIGINT AUTO_INCREMENT PRIMARY KEY,association_id VARCHAR(80) NULL,user_id VARCHAR(80) NULL,subject VARCHAR(255) NULL,message TEXT NULL,status VARCHAR(60) NOT NULL DEFAULT 'Pending',created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,updated_at DATETIME NULL) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS bl_messages (id BIGINT AUTO_INCREMENT PRIMARY KEY,sender_id VARCHAR(80) NOT NULL,recipient_id VARCHAR(80) NOT NULL,title VARCHAR(255) NULL,body TEXT NULL,status VARCHAR(60) NOT NULL DEFAULT 'Unread',created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, INDEX(recipient_id)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS bl_profile_edits (id BIGINT AUTO_INCREMENT PRIMARY KEY,user_id VARCHAR(80) NOT NULL,name VARCHAR(255) NULL,city VARCHAR(160) NULL,phone VARCHAR(80) NULL,bio TEXT NULL,created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, INDEX(user_id)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS bl_external_decisions (source_table VARCHAR(80) NOT NULL,external_id VARCHAR(80) NOT NULL,status VARCHAR(80) NULL,note TEXT NULL,decided_by VARCHAR(80) NULL,decided_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, PRIMARY KEY(source_table,external_id)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS bl_action_audit (id BIGINT AUTO_INCREMENT PRIMARY KEY,user_id VARCHAR(80) NULL,action_type VARCHAR(120) NOT NULL,message TEXT NULL,created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, INDEX(user_id)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;"
            };
            foreach (var sql in statements) await db.ExecuteAsync(sql, null, ct);
            ActionSchemaReady = true;
        }
        finally { ActionSchemaLock.Release(); }
    }

    private async Task TryNativeApplicationInsertAsync(AppUser user, string missionId, string motivation, CancellationToken ct)
    {
        var table = catalog.FindTable("applications");
        if (table is null) return;
        await TryNativeSimpleInsertAsync(table, new()
        {
            ["mission_id"] = missionId, ["id_mission"] = missionId, ["user_id"] = user.Id, ["volunteer_id"] = user.Id, ["member_id"] = user.Id, ["applicant_id"] = user.Id,
            ["status"] = "Pending", ["state"] = "Pending", ["decision"] = "Pending", ["motivation"] = motivation, ["message"] = motivation, ["created_at"] = DateTime.Now, ["submitted_at"] = DateTime.Now
        }, ct);
    }

    private async Task TryNativeApplicationDecisionAsync(AppUser user, string applicationId, string final, string note, CancellationToken ct)
    {
        var table = catalog.FindTable("applications", "approval_requests");
        if (table is null) return;
        var idCol = catalog.FindColumn(table, "id", "application_id", "request_id");
        if (idCol is null) return;
        await UpdateAnyTableByIdAsync(table, idCol, applicationId, new()
        {
            ["status"] = final, ["state"] = final, ["decision"] = final, ["approval_status"] = final,
            ["review_note"] = note, ["admin_note"] = note, ["note"] = note, ["reviewed_by"] = user.Id, ["approved_by"] = user.Id, ["reviewed_at"] = DateTime.Now, ["updated_at"] = DateTime.Now
        }, ct);
    }

    private async Task TryNativeEventRegistrationAsync(AppUser user, string eventId, string status, CancellationToken ct)
    {
        var table = catalog.FindTable("event_registrations", "participations");
        if (table is null) return;
        await TryNativeSimpleInsertAsync(table, new() { ["event_id"] = eventId, ["id_event"] = eventId, ["user_id"] = user.Id, ["volunteer_id"] = user.Id, ["member_id"] = user.Id, ["status"] = status, ["state"] = status, ["registration_status"] = status, ["created_at"] = DateTime.Now, ["date"] = DateTime.Now }, ct);
    }

    private async Task TryNativeSimpleInsertAsync(string table, Dictionary<string, object?> values, CancellationToken ct)
    {
        if (!catalog.HasTable(table)) return;
        await InsertAnyTableAsync(table, values, ct);
    }

    private async Task<OperationResult> InsertAnyTableAsync(string table, Dictionary<string, object?> values, CancellationToken ct)
    {
        try
        {
            var columns = await ColumnsForTableAsync(table, ct);
            if (columns.Count == 0) return OperationResult.Fail($"No columns found for {table}.");
            var available = columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var chosen = new List<(string Column, object? Value)>();
            foreach (var (key, value) in values)
            {
                var col = FindColumnName(columns, key);
                if (col is not null && !chosen.Any(x => x.Column.Equals(col, StringComparison.OrdinalIgnoreCase)) && value is not null && !string.IsNullOrWhiteSpace(Convert.ToString(value))) chosen.Add((col, value));
            }
            foreach (var col in columns.Where(c => !c.Nullable && !c.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase) && chosen.All(x => !x.Column.Equals(c.Name, StringComparison.OrdinalIgnoreCase))))
            {
                if (col.Name.Contains("created", StringComparison.OrdinalIgnoreCase) || col.DataType.Contains("date", StringComparison.OrdinalIgnoreCase) || col.DataType.Contains("time", StringComparison.OrdinalIgnoreCase)) chosen.Add((col.Name, DateTime.Now));
                else if (col.Name.Contains("status", StringComparison.OrdinalIgnoreCase) || col.Name.Contains("state", StringComparison.OrdinalIgnoreCase)) chosen.Add((col.Name, "Pending"));
                else if (col.Name.Contains("role", StringComparison.OrdinalIgnoreCase) || col.Name.Contains("type", StringComparison.OrdinalIgnoreCase)) chosen.Add((col.Name, "Volunteer"));
                else if (col.DataType.Contains("int", StringComparison.OrdinalIgnoreCase) || col.DataType.Contains("decimal", StringComparison.OrdinalIgnoreCase)) chosen.Add((col.Name, 0));
                else chosen.Add((col.Name, ""));
            }
            if (chosen.Count == 0) return OperationResult.Fail($"No compatible columns were found for {table}.");
            var parameters = new Dictionary<string, object?>();
            for (var i = 0; i < chosen.Count; i++) parameters[$"@p{i}"] = chosen[i].Value;
            var sql = $"INSERT INTO {Q(table)} ({string.Join(",", chosen.Select(x => Q(x.Column)))}) VALUES ({string.Join(",", parameters.Keys)});";
            await db.ExecuteAsync(sql, parameters, ct);
            return OperationResult.Success("Saved successfully.");
        }
        catch (Exception ex) { return OperationResult.Fail("Database insert failed: " + XamppMySql.CleanError(ex.Message)); }
    }

    private async Task UpdateAnyTableByIdAsync(string table, string idColumn, string id, Dictionary<string, object?> values, CancellationToken ct)
    {
        try
        {
            var columns = await ColumnsForTableAsync(table, ct);
            var assignments = new List<string>();
            var parameters = new Dictionary<string, object?> { ["@id"] = id };
            var i = 0;
            foreach (var (key, value) in values)
            {
                var col = FindColumnName(columns, key);
                if (col is null || col.Equals(idColumn, StringComparison.OrdinalIgnoreCase)) continue;
                assignments.Add($"{Q(col)}=@p{i}");
                parameters[$"@p{i}"] = value ?? DBNull.Value;
                i++;
            }
            if (assignments.Count == 0) return;
            await db.ExecuteAsync($"UPDATE {Q(table)} SET {string.Join(",", assignments)} WHERE {Q(idColumn)}=@id LIMIT 1;", parameters, ct);
        }
        catch { }
    }

    private async Task TryNativeProfileUpdateAsync(AppUser user, string name, string city, string phone, string bio, CancellationToken ct)
    {
        var table = catalog.FindTable("users");
        if (table is null) return;
        var idCol = catalog.FindColumn(table, "id", "user_id");
        if (idCol is null) return;
        await UpdateAnyTableByIdAsync(table, idCol, user.Id, new() { ["display_name"] = name, ["full_name"] = name, ["name"] = name, ["city"] = city, ["location"] = city, ["phone"] = phone, ["mobile"] = phone, ["bio"] = bio, ["about"] = bio, ["updated_at"] = DateTime.Now }, ct);
    }

    private async Task TryNativeNotificationReadAsync(string id, CancellationToken ct)
    {
        var table = catalog.FindTable("notifications", "association_messages", "feedbacks");
        if (table is null) return;
        var idCol = catalog.FindColumn(table, "id", "notification_id", "message_id");
        if (idCol is null) return;
        await UpdateAnyTableByIdAsync(table, idCol, id, new() { ["is_read"] = true, ["read"] = true, ["seen"] = true, ["status"] = "Read", ["state"] = "Read", ["read_status"] = "Read", ["read_at"] = DateTime.Now, ["updated_at"] = DateTime.Now }, ct);
    }

    private async Task TryNativeMarkAllNotificationsReadAsync(AppUser user, CancellationToken ct)
    {
        var table = catalog.FindTable("notifications", "association_messages", "feedbacks");
        if (table is null) return;
        var columns = await ColumnsForTableAsync(table, ct);
        var userCol = FindColumnName(columns, "user_id") ?? FindColumnName(columns, "recipient_id") ?? FindColumnName(columns, "member_id");
        var statusCol = FindColumnName(columns, "status") ?? FindColumnName(columns, "read_status") ?? FindColumnName(columns, "state");
        if (userCol is null || statusCol is null) return;
        await ExecuteSafeAsync($"UPDATE {Q(table)} SET {Q(statusCol)}='Read' WHERE {Q(userCol)}=@u;", new() { ["@u"] = user.Id }, ct);
    }

    private async Task TryNativeAssociationStatusAsync(string id, string status, string actorId, CancellationToken ct)
    {
        var table = catalog.FindTable("associations", "organizations", "ngos");
        if (table is null) return;
        var idCol = catalog.FindColumn(table, "id", "association_id", "organization_id", "ngo_id");
        if (idCol is null) return;
        await UpdateAnyTableByIdAsync(table, idCol, id, new() { ["verification_status"] = status, ["status"] = status, ["state"] = status, ["approved"] = status, ["verified_by"] = actorId, ["updated_at"] = DateTime.Now }, ct);
    }

    private async Task TryNativeOrganizationUpdateAsync(string id, string name, string sector, string city, string email, string description, CancellationToken ct)
    {
        var table = catalog.FindTable("associations", "organizations", "ngos");
        if (table is null || string.IsNullOrWhiteSpace(id)) return;
        var idCol = catalog.FindColumn(table, "id", "association_id", "organization_id", "ngo_id");
        if (idCol is null) return;
        await UpdateAnyTableByIdAsync(table, idCol, id, new() { ["name"] = name, ["association_name"] = name, ["organization_name"] = name, ["sector"] = sector, ["category"] = sector, ["city"] = city, ["location"] = city, ["email"] = email, ["contact_email"] = email, ["description"] = description, ["about"] = description, ["updated_at"] = DateTime.Now }, ct);
    }

    private async Task TryNativeGoalUpdateAsync(string id, string progress, string status, CancellationToken ct)
    {
        var table = catalog.FindTable("impact_goals");
        if (table is null) return;
        var idCol = catalog.FindColumn(table, "id", "goal_id");
        if (idCol is null) return;
        await UpdateAnyTableByIdAsync(table, idCol, id, new() { ["current"] = progress, ["current_value"] = progress, ["progress"] = progress, ["status"] = status, ["state"] = status, ["updated_at"] = DateTime.Now }, ct);
    }

    private async Task TryNativeStatusUpdateAsync(string table, string id, string status, CancellationToken ct)
    {
        if (!catalog.HasTable(table)) return;
        var idCol = catalog.FindColumn(table, "id", table.TrimEnd('s') + "_id", "mission_id", "event_id");
        if (idCol is null) return;
        await UpdateAnyTableByIdAsync(table, idCol, id, new() { ["status"] = status, ["state"] = status, ["visibility"] = status, ["approval_status"] = status, ["updated_at"] = DateTime.Now }, ct);
    }

    private async Task TryNativeWithdrawAsync(AppUser user, string missionId, CancellationToken ct)
    {
        var table = catalog.FindTable("applications");
        if (table is null) return;
        var columns = await ColumnsForTableAsync(table, ct);
        var missionCol = FindColumnName(columns, "mission_id") ?? FindColumnName(columns, "id_mission");
        var userCol = FindColumnName(columns, "user_id") ?? FindColumnName(columns, "volunteer_id") ?? FindColumnName(columns, "member_id");
        var statusCol = FindColumnName(columns, "status") ?? FindColumnName(columns, "state") ?? FindColumnName(columns, "decision");
        if (missionCol is null || userCol is null || statusCol is null) return;
        await ExecuteSafeAsync($"UPDATE {Q(table)} SET {Q(statusCol)}='Withdrawn' WHERE {Q(missionCol)}=@m AND {Q(userCol)}=@u;", new() { ["@m"] = missionId, ["@u"] = user.Id }, ct);
    }

    private async Task AddVolunteerHoursNativeAsync(string volunteerId, string sourceId, decimal hours, AppUser validator, CancellationToken ct)
    {
        var table = catalog.FindTable("volunteer_hours", "participations");
        if (table is null) return;
        await TryNativeSimpleInsertAsync(table, new() { ["user_id"] = volunteerId, ["volunteer_id"] = volunteerId, ["member_id"] = volunteerId, ["event_id"] = sourceId, ["mission_id"] = sourceId, ["hours"] = hours, ["total_hours"] = hours, ["duration"] = hours, ["status"] = "Validated", ["state"] = "Validated", ["validated_by"] = validator.Id, ["created_at"] = DateTime.Now, ["date"] = DateTime.Now }, ct);
    }

    private async Task CreateParticipationFromBlApplicationAsync(string id, AppUser reviewer, CancellationToken ct)
    {
        var rows = await db.QueryAsync("SELECT * FROM bl_applications WHERE id=@id LIMIT 1;", new() { ["@id"] = id }, ct);
        if (rows.Count == 0) return;
        var row = rows[0];
        var userId = Pick(row, "user_id") ?? "";
        var missionId = Pick(row, "mission_id") ?? "";
        if (string.IsNullOrWhiteSpace(userId)) return;
        await ExecuteSafeAsync("INSERT INTO bl_volunteer_hours(user_id,source_id,hours,note,validated_by,created_at) VALUES(@u,@s,0,'Approved mission participation',@by,NOW());", new() { ["@u"] = userId, ["@s"] = missionId, ["@by"] = reviewer.Id }, ct);
        await NotifyAsync(userId, "Application approved", $"Your mission application for {Pick(row, "mission_title") ?? missionId} was approved.", ct);
    }

    private async Task<string> FindMissionTitleAsync(string missionId, CancellationToken ct)
    {
        if (missionId.StartsWith("bl:", StringComparison.OrdinalIgnoreCase))
        {
            var title = await db.ScalarAsync("SELECT title FROM bl_missions WHERE id=@id LIMIT 1;", new() { ["@id"] = missionId[3..] }, ct);
            return Convert.ToString(title) ?? missionId;
        }
        foreach (var table in new[] { "missions", "v_public_missions" })
        {
            if (!catalog.HasTable(table)) continue;
            var idCol = catalog.FindColumn(table, "id", "mission_id", "id_mission");
            if (idCol is null) continue;
            var rows = await db.QueryAsync($"SELECT * FROM {Q(table)} WHERE {Q(idCol)}=@id LIMIT 1;", new() { ["@id"] = missionId }, ct);
            if (rows.Count > 0) return Pick(rows[0], "title", "name", "mission_title") ?? missionId;
        }
        return missionId;
    }

    private async Task<string?> MissionApplicationStatusAsync(AppUser user, string missionId, CancellationToken ct)
    {
        var rows = await db.QueryAsync("SELECT status FROM bl_applications WHERE mission_id=@m AND user_id=@u ORDER BY id DESC LIMIT 1;", new() { ["@m"] = missionId, ["@u"] = user.Id }, ct);
        if (rows.Count > 0) return Pick(rows[0], "status") ?? "Submitted";
        var table = catalog.FindTable("applications");
        if (table is null) return null;
        var missionCol = catalog.FindColumn(table, "mission_id", "MissionId", "id_mission", "mission");
        var userCol = catalog.FindColumn(table, "user_id", "UserId", "volunteer_id", "member_id", "applicant_id", "id_user");
        var statusCol = catalog.FindColumn(table, "status", "state", "decision", "approval_status");
        if (missionCol is null || userCol is null) return null;
        var native = await db.QueryAsync($"SELECT * FROM {Q(table)} WHERE {Q(missionCol)}=@mission AND {Q(userCol)}=@user LIMIT 1;", new() { ["@mission"] = missionId, ["@user"] = user.Id }, ct);
        return native.Count == 0 ? null : Pick(native[0], statusCol ?? "status", "state", "decision", "approval_status") ?? "Submitted";
    }

    private async Task<bool> IsMissionSavedAsync(AppUser user, string missionId, CancellationToken ct)
    {
        if (await ScalarLongAsync("SELECT COUNT(*) FROM bl_saved_missions WHERE mission_id=@m AND user_id=@u;", new() { ["@m"] = missionId, ["@u"] = user.Id }, ct) > 0) return true;
        var table = catalog.FindTable("saved_missions");
        if (table is null) return false;
        var missionCol = catalog.FindColumn(table, "mission_id", "MissionId", "id_mission", "mission");
        var userCol = catalog.FindColumn(table, "user_id", "UserId", "member_id", "volunteer_id", "id_user");
        if (missionCol is null || userCol is null) return false;
        var count = await db.ScalarAsync($"SELECT COUNT(*) FROM {Q(table)} WHERE {Q(missionCol)}=@mission AND {Q(userCol)}=@user;", new() { ["@mission"] = missionId, ["@user"] = user.Id }, ct);
        return Convert.ToInt64(count ?? 0) > 0;
    }

    private async Task<string> EventRegistrationStatusAsync(AppUser user, string eventId, CancellationToken ct)
    {
        var rows = await db.QueryAsync("SELECT status FROM bl_event_registrations WHERE event_id=@e AND user_id=@u LIMIT 1;", new() { ["@e"] = eventId, ["@u"] = user.Id }, ct);
        if (rows.Count > 0) return Pick(rows[0], "status") ?? "Registered";
        var table = catalog.FindTable("event_registrations", "participations");
        if (table is null) return "Not registered";
        var eventCol = catalog.FindColumn(table, "event_id", "EventId", "id_event", "event");
        var userCol = catalog.FindColumn(table, "user_id", "UserId", "member_id", "volunteer_id", "id_user");
        if (eventCol is null || userCol is null) return "Not registered";
        var native = await db.QueryAsync($"SELECT * FROM {Q(table)} WHERE {Q(eventCol)}=@event AND {Q(userCol)}=@user LIMIT 1;", new() { ["@event"] = eventId, ["@user"] = user.Id }, ct);
        return native.Count == 0 ? "Not registered" : Pick(native[0], "status", "state", "registration_status") ?? "Registered";
    }

    private async Task<List<Dictionary<string, string>>> QueryMaybeScopedToUserAsync(string table, AppUser user, int limit, CancellationToken ct)
    {
        var columns = await ColumnsForTableAsync(table, ct);
        var userCol = FindColumnName(columns, "user_id") ?? FindColumnName(columns, "member_id") ?? FindColumnName(columns, "volunteer_id") ?? FindColumnName(columns, "applicant_id") ?? FindColumnName(columns, "recipient_id");
        if (user.Role == "Volunteer" && userCol is not null)
            return await db.QueryAsync($"SELECT * FROM {Q(table)} WHERE {Q(userCol)}=@u ORDER BY {OrderBy(columns)} DESC LIMIT @limit;", new() { ["@u"] = user.Id, ["@limit"] = limit }, ct);
        return await SafeQueryTableAsync(table, limit, ct);
    }

    private async Task<List<Dictionary<string, string>>> QueryCurrentUserRowsAsync(string table, AppUser user, CancellationToken ct)
    {
        var columns = await ColumnsForTableAsync(table, ct);
        var idCol = FindColumnName(columns, "id") ?? FindColumnName(columns, "user_id");
        var emailCol = FindColumnName(columns, "email") ?? FindColumnName(columns, "mail") ?? FindColumnName(columns, "user_email");
        if (idCol is not null)
        {
            var rows = await db.QueryAsync($"SELECT * FROM {Q(table)} WHERE {Q(idCol)}=@id LIMIT 1;", new() { ["@id"] = user.Id }, ct);
            if (rows.Count > 0) return rows;
        }
        if (emailCol is not null)
            return await db.QueryAsync($"SELECT * FROM {Q(table)} WHERE LOWER({Q(emailCol)})=@email LIMIT 1;", new() { ["@email"] = user.Email.ToLowerInvariant() }, ct);
        return [];
    }

    private async Task<Dictionary<string, string>> LatestProfileEditAsync(string userId, CancellationToken ct)
    {
        var rows = await db.QueryAsync("SELECT * FROM bl_profile_edits WHERE user_id=@u ORDER BY id DESC LIMIT 1;", new() { ["@u"] = userId }, ct);
        return rows.FirstOrDefault() ?? [];
    }

    private async Task<IReadOnlyList<string>> UserSkillsAsync(AppUser user, CancellationToken ct)
    {
        var list = new List<string>();
        list.AddRange(await SkillsForUserIdListAsync(user.Id, ct));
        var table = catalog.FindTable("member_skills");
        if (table is not null)
        {
            var cols = await ColumnsForTableAsync(table, ct);
            var userCol = FindColumnName(cols, "user_id") ?? FindColumnName(cols, "member_id") ?? FindColumnName(cols, "volunteer_id");
            if (userCol is not null)
            {
                var rows = await db.QueryAsync($"SELECT * FROM {Q(table)} WHERE {Q(userCol)}=@u LIMIT 40;", new() { ["@u"] = user.Id }, ct);
                list.AddRange(rows.Select(r => Pick(r, "skill", "name", "label", "title", "skill_name") ?? "").Where(x => !string.IsNullOrWhiteSpace(x)));
            }
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<IReadOnlyList<string>> SkillsForUserIdListAsync(string userId, CancellationToken ct)
    {
        var rows = await db.QueryAsync("SELECT skill FROM bl_skills WHERE user_id=@u ORDER BY id DESC LIMIT 20;", new() { ["@u"] = userId }, ct);
        return rows.Select(r => Pick(r, "skill") ?? "").Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task AddSkillInternalAsync(string userId, string skill, CancellationToken ct)
    {
        await ExecuteSafeAsync("INSERT IGNORE INTO bl_skills(user_id,skill,created_at) VALUES(@u,@s,NOW());", new() { ["@u"] = userId, ["@s"] = skill }, ct);
        var table = catalog.FindTable("member_skills");
        if (table is not null) await TryNativeSimpleInsertAsync(table, new() { ["user_id"] = userId, ["member_id"] = userId, ["volunteer_id"] = userId, ["skill"] = skill, ["name"] = skill, ["created_at"] = DateTime.Now }, ct);
    }

    private async Task<string> TotalHoursForUserIdAsync(string userId, CancellationToken ct)
    {
        decimal total = 0;
        await TryAsync(async () =>
        {
            var value = await db.ScalarAsync("SELECT COALESCE(SUM(hours),0) FROM bl_volunteer_hours WHERE user_id=@u;", new() { ["@u"] = userId }, ct);
            total += Convert.ToDecimal(value ?? 0);
        });
        await TryAsync(async () =>
        {
            var table = catalog.FindTable("volunteer_hours", "participations");
            if (table is null) return;
            var cols = await ColumnsForTableAsync(table, ct);
            var userCol = FindColumnName(cols, "user_id") ?? FindColumnName(cols, "member_id") ?? FindColumnName(cols, "volunteer_id");
            var hoursCol = FindColumnName(cols, "hours") ?? FindColumnName(cols, "total_hours") ?? FindColumnName(cols, "duration") ?? FindColumnName(cols, "volunteer_hours");
            if (userCol is null || hoursCol is null) return;
            var value = await db.ScalarAsync($"SELECT COALESCE(SUM(CAST({Q(hoursCol)} AS DECIMAL(10,2))),0) FROM {Q(table)} WHERE {Q(userCol)}=@u;", new() { ["@u"] = userId }, ct);
            total += Convert.ToDecimal(value ?? 0);
        });
        return total.ToString("0.##");
    }

    private async Task NotifyAsync(string userId, string title, string message, CancellationToken ct)
    {
        await ExecuteSafeAsync("INSERT INTO bl_notifications(user_id,title,body,status,created_at) VALUES(@u,@t,@b,'Unread',NOW());", new() { ["@u"] = userId, ["@t"] = title, ["@b"] = message }, ct);
        var table = catalog.FindTable("notifications");
        if (table is not null) await TryNativeSimpleInsertAsync(table, new() { ["user_id"] = userId, ["recipient_id"] = userId, ["title"] = title, ["subject"] = title, ["body"] = message, ["message"] = message, ["status"] = "Unread", ["is_read"] = false, ["created_at"] = DateTime.Now }, ct);
    }

    private async Task NotifyAdminsAsync(string title, string message, CancellationToken ct)
    {
        var table = catalog.FindTable("users");
        if (table is null)
        {
            await ExecuteSafeAsync("INSERT INTO bl_notifications(user_id,title,body,status,created_at) VALUES('all',@t,@b,'Unread',NOW());", new() { ["@t"] = title, ["@b"] = message }, ct);
            return;
        }
        var cols = await ColumnsForTableAsync(table, ct);
        var idCol = FindColumnName(cols, "id") ?? FindColumnName(cols, "user_id");
        var roleCol = FindColumnName(cols, "role") ?? FindColumnName(cols, "type") ?? FindColumnName(cols, "account_type");
        if (idCol is null || roleCol is null) return;
        var rows = await db.QueryAsync($"SELECT * FROM {Q(table)} WHERE LOWER({Q(roleCol)}) LIKE '%admin%' LIMIT 20;", null, ct);
        foreach (var row in rows)
        {
            var id = Pick(row, idCol);
            if (!string.IsNullOrWhiteSpace(id)) await NotifyAsync(id!, title, message, ct);
        }
    }

    private async Task AuditAsync(AppUser user, string type, string message, CancellationToken ct)
    {
        await ExecuteSafeAsync("INSERT INTO bl_action_audit(user_id,action_type,message,created_at) VALUES(@u,@t,@m,NOW());", new() { ["@u"] = user.Id, ["@t"] = type, ["@m"] = message }, ct);
        var table = catalog.FindTable("audit_logs");
        if (table is not null) await TryNativeSimpleInsertAsync(table, new() { ["user_id"] = user.Id, ["actor_id"] = user.Id, ["event_type"] = type, ["type"] = type, ["action"] = type, ["message"] = message, ["description"] = message, ["ip_address"] = "127.0.0.1", ["created_at"] = DateTime.Now }, ct);
    }


    private async Task<string> MissionStatusOverlayAsync(string sourceTable, string externalId, string fallback, CancellationToken ct)
    {
        var status = await ExternalDecisionStatusAsync(sourceTable, externalId, ct) ?? await ExternalDecisionStatusAsync("missions", externalId, ct);
        return string.IsNullOrWhiteSpace(status) ? fallback : status!;
    }

    private async Task<string> ExternalStatusOverlayAsync(string sourceTable, string externalId, string fallback, CancellationToken ct)
    {
        var status = await ExternalDecisionStatusAsync(sourceTable, externalId, ct);
        return string.IsNullOrWhiteSpace(status) ? fallback : status!;
    }

    private async Task<string> OrganizationStatusOverlayAsync(string organizationId, string fallback, CancellationToken ct)
    {
        var status = await ExternalDecisionStatusAsync("associations", organizationId, ct);
        if (!string.IsNullOrWhiteSpace(status)) return status!;
        var rows = await db.QueryAsync("SELECT status FROM bl_organization_requests WHERE association_id=@a ORDER BY id DESC LIMIT 1;", new() { ["@a"] = organizationId }, ct);
        return rows.Count == 0 ? fallback : Pick(rows[0], "status") ?? fallback;
    }

    private async Task<string?> ExternalDecisionStatusAsync(string sourceTable, string externalId, CancellationToken ct)
    {
        var rows = await db.QueryAsync("SELECT status FROM bl_external_decisions WHERE source_table=@t AND external_id=@id LIMIT 1;", new() { ["@t"] = sourceTable, ["@id"] = externalId }, ct);
        return rows.Count == 0 ? null : Pick(rows[0], "status");
    }

    private async Task<IReadOnlyList<TableInfo>> ActionTableInfoAsync(CancellationToken ct)
    {
        var rows = await db.QueryAsync("SELECT TABLE_NAME, TABLE_TYPE, COALESCE(TABLE_ROWS,0) TABLE_ROWS, COALESCE(ROUND((DATA_LENGTH+INDEX_LENGTH)/1024,1),0) SIZE_KB FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME LIKE 'bl\\_%' ORDER BY TABLE_NAME;", null, ct);
        return rows.Select(r => new TableInfo(Pick(r, "TABLE_NAME") ?? "", "Table", Convert.ToInt64(Pick(r, "TABLE_ROWS") ?? "0"), $"{Pick(r, "SIZE_KB") ?? "0"} KiB", "InnoDB")).ToList();
    }

    private async Task<IReadOnlyList<ColumnInfo>> ColumnsForTableAsync(string table, CancellationToken ct)
    {
        try { return await db.LoadColumnsAsync(table, ct); } catch { return []; }
    }

    private async Task<List<Dictionary<string, string>>> SafeQueryTableAsync(string table, int limit, CancellationToken ct)
    {
        var columns = await ColumnsForTableAsync(table, ct);
        if (columns.Count == 0) return [];
        var sqlColumns = string.Join(",", columns.Take(32).Select(c => Q(c.Name)));
        return await db.QueryAsync($"SELECT {sqlColumns} FROM {Q(table)} ORDER BY {OrderBy(columns)} DESC LIMIT @limit;", new() { ["@limit"] = Math.Clamp(limit, 1, 500) }, ct);
    }

    private async Task<long> ScalarLongAsync(string sql, Dictionary<string, object?> parameters, CancellationToken ct)
    {
        try { return Convert.ToInt64(await db.ScalarAsync(sql, parameters, ct) ?? 0); } catch { return 0; }
    }

    private async Task ExecuteSafeAsync(string sql, Dictionary<string, object?>? parameters, CancellationToken ct)
    {
        try { await db.ExecuteAsync(sql, parameters, ct); } catch { }
    }

    private static async Task TryAsync(Func<Task> action)
    {
        try { await action(); } catch { }
    }

    private static string? FindColumnName(IEnumerable<ColumnInfo> columns, params string[] candidates)
    {
        var list = columns.ToList();
        foreach (var c in candidates)
        {
            var exact = list.FirstOrDefault(x => x.Name.Equals(c, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact.Name;
        }
        foreach (var c in candidates)
        {
            var normalized = Normalize(c);
            var match = list.FirstOrDefault(x => Normalize(x.Name).Contains(normalized) || normalized.Contains(Normalize(x.Name)));
            if (match is not null) return match.Name;
        }
        return null;
    }

    private static Dictionary<string, object?> ParseKeyValues(string text)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = rawLine.IndexOf('=');
            if (index <= 0) continue;
            var key = rawLine[..index].Trim();
            var value = rawLine[(index + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(key)) values[key] = value;
        }
        return values;
    }

    private static bool IsPositiveStatus(string status)
    {
        var s = status.ToLowerInvariant();
        return !s.Contains("reject") && !s.Contains("cancel") && !s.Contains("deny") && !s.Contains("pending") && !s.Contains("withdraw");
    }

    private static string? Pick(Dictionary<string, string> row, params string?[] candidates)
    {
        foreach (var candidate in candidates.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!))
            if (row.TryGetValue(candidate, out var value) && !string.IsNullOrWhiteSpace(value)) return value;
        foreach (var candidate in candidates.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Normalize(x!)))
        {
            var match = row.FirstOrDefault(x => Normalize(x.Key).Contains(candidate) || candidate.Contains(Normalize(x.Key)));
            if (!string.IsNullOrWhiteSpace(match.Value)) return match.Value;
        }
        return null;
    }

    private static string StableId(Dictionary<string, string> row) => Math.Abs(string.Join("|", row.Values).GetHashCode()).ToString();
    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static string Q(string identifier) => XamppMySql.Quote(identifier);
    private static string OrderBy(IEnumerable<ColumnInfo> columns)
    {
        var list = columns.ToList();
        var candidate = list.FirstOrDefault(c => new[] { "created_at", "updated_at", "date", "id" }.Any(x => x.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))?.Name;
        return candidate is null ? "1" : Q(candidate);
    }
}
