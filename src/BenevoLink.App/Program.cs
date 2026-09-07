using System.Security.Claims;
using BenevoLink.Models;
using BenevoLink.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor(options => options.DetailedErrors = true);
builder.Services.AddHttpContextAccessor();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "BenevoLink.ImpactOS.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/access-denied";
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Admin", p => p.RequireRole("Admin"));
    options.AddPolicy("Organization", p => p.RequireRole("Organization", "Admin"));
    options.AddPolicy("Volunteer", p => p.RequireRole("Volunteer", "Admin"));
});

builder.Services.AddSingleton<DatabaseCatalog>();
builder.Services.AddScoped<XamppMySql>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<PlatformService>();
builder.Services.AddScoped<OperationsService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", async (XamppMySql db) => Results.Ok(await db.HealthAsync()));

app.MapPost("/auth/login", async (HttpContext http, AuthService auth) =>
{
    var form = await http.Request.ReadFormAsync();
    var email = form["email"].ToString();
    var password = form["password"].ToString();
    var returnUrl = SafeLocalUrl(form["returnUrl"].ToString());
    var result = await auth.ValidateAsync(email, password, http.RequestAborted);

    if (!result.Succeeded || result.User is null)
    {
        return Results.Redirect($"/login?error={Uri.EscapeDataString(result.Message)}&returnUrl={Uri.EscapeDataString(returnUrl)}");
    }

    var user = result.User;
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.Id),
        new(ClaimTypes.Name, user.DisplayName),
        new(ClaimTypes.Email, user.Email),
        new(ClaimTypes.Role, user.Role),
        new("benevolink.status", user.Status),
        new("benevolink.source", user.Source)
    };

    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity),
        new AuthenticationProperties
        {
            IsPersistent = form["remember"] == "on",
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(form["remember"] == "on" ? 72 : 12),
            AllowRefresh = true
        });

    return Results.Redirect(returnUrl);
});

app.MapPost("/auth/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login?loggedout=true");
});

app.MapPost("/auth/register", async (HttpContext http, OperationsService ops) =>
{
    var form = await http.Request.ReadFormAsync();
    var name = form["name"].ToString();
    var email = form["email"].ToString();
    var password = form["password"].ToString();
    var role = form["role"].ToString();
    if (string.IsNullOrWhiteSpace(role)) role = "Volunteer";
    var result = await ops.CreateUserAsync(name, email, password, role, http.RequestAborted);
    if (!result.Succeeded)
    {
        return Results.Redirect($"/register?error={Uri.EscapeDataString(result.Message)}");
    }
    return Results.Redirect($"/login?registered=true&email={Uri.EscapeDataString(email)}");
});


// HTML form action endpoints. These make every BenevoLink button work through normal POST/redirect,
// even if the browser has not attached a Blazor circuit yet.
app.MapPost("/actions/mission/apply", async (HttpContext http, OperationsService ops) =>
{
    var form = await http.Request.ReadFormAsync();
    var user = CurrentUser(http);
    if (user is null) return Results.Redirect("/login");
    var result = await ops.ApplyToMissionAsync(user, F(form,"missionId"), F(form,"motivation"), http.RequestAborted);
    return ActionRedirect(form, result);
}).RequireAuthorization();

app.MapPost("/actions/mission/save", async (HttpContext http, OperationsService ops) =>
{
    var form = await http.Request.ReadFormAsync();
    var user = CurrentUser(http);
    if (user is null) return Results.Redirect("/login");
    var result = await ops.SaveMissionAsync(user, F(form,"missionId"), http.RequestAborted);
    return ActionRedirect(form, result);
}).RequireAuthorization();

app.MapPost("/actions/mission/withdraw", async (HttpContext http, OperationsService ops) =>
{
    var form = await http.Request.ReadFormAsync();
    var user = CurrentUser(http);
    if (user is null) return Results.Redirect("/login");
    var result = await ops.WithdrawMissionApplicationAsync(user, F(form,"missionId"), http.RequestAborted);
    return ActionRedirect(form, result);
}).RequireAuthorization();

app.MapPost("/actions/mission/create", async (HttpContext http, OperationsService ops) =>
{
    var form = await http.Request.ReadFormAsync();
    var user = CurrentUser(http);
    if (user is null) return Results.Redirect("/login");
    var result = await ops.CreateMissionAsync(user, F(form,"title"), F(form,"description"), F(form,"location"), F(form,"cause"), F(form,"date"), http.RequestAborted);
    return ActionRedirect(form, result);
}).RequireAuthorization();

app.MapPost("/actions/mission/status", async (HttpContext http, OperationsService ops) =>
{
    var form = await http.Request.ReadFormAsync();
    var user = CurrentUser(http);
    if (user is null) return Results.Redirect("/login");
    var result = await ops.UpdateMissionStatusAsync(user, F(form,"missionId"), F(form,"status"), http.RequestAborted);
    return ActionRedirect(form, result);
}).RequireAuthorization();

app.MapPost("/actions/application/decide", async (HttpContext http, OperationsService ops) =>
{
    var form = await http.Request.ReadFormAsync();
    var user = CurrentUser(http);
    if (user is null) return Results.Redirect("/login");
    var result = await ops.DecideApplicationAsync(user, F(form,"applicationId"), F(form,"decision"), F(form,"note"), http.RequestAborted);
    return ActionRedirect(form, result);
}).RequireAuthorization();

app.MapPost("/actions/event/register", async (HttpContext http, OperationsService ops) =>
{
    var form = await http.Request.ReadFormAsync();
    var user = CurrentUser(http);
    if (user is null) return Results.Redirect("/login");
    var result = await ops.RegisterForEventAsync(user, F(form,"eventId"), http.RequestAborted);
    return ActionRedirect(form, result);
}).RequireAuthorization();

app.MapPost("/actions/event/cancel", async (HttpContext http, OperationsService ops) =>
{
    var form = await http.Request.ReadFormAsync();
    var user = CurrentUser(http);
    if (user is null) return Results.Redirect("/login");
    var result = await ops.CancelEventRegistrationAsync(user, F(form,"eventId"), http.RequestAborted);
    return ActionRedirect(form, result);
}).RequireAuthorization();

app.MapPost("/actions/event/create", async (HttpContext http, OperationsService ops) =>
{
    var form = await http.Request.ReadFormAsync();
    var user = CurrentUser(http);
    if (user is null) return Results.Redirect("/login");
    var result = await ops.CreateEventAsync(user, F(form,"title"), F(form,"location"), F(form,"date"), http.RequestAborted);
    return ActionRedirect(form, result);
}).RequireAuthorization();

app.MapPost("/actions/event/status", async (HttpContext http, OperationsService ops) =>
{
    var form = await http.Request.ReadFormAsync();
    var user = CurrentUser(http);
    if (user is null) return Results.Redirect("/login");
    var result = await ops.UpdateEventStatusAsync(user, F(form,"eventId"), F(form,"status"), http.RequestAborted);
    return ActionRedirect(form, result);
}).RequireAuthorization();

app.MapPost("/actions/event/validate", async (HttpContext http, OperationsService ops) =>
{
    var form = await http.Request.ReadFormAsync();
    var user = CurrentUser(http);
    if (user is null) return Results.Redirect("/login");
    _ = decimal.TryParse(F(form,"hours"), out var hours);
    var result = await ops.ValidateAttendanceAsync(user, F(form,"eventId"), F(form,"volunteerId"), hours <= 0 ? 1 : hours, http.RequestAborted);
    return ActionRedirect(form, result);
}).RequireAuthorization();

app.MapPost("/actions/profile/save", async (HttpContext http, OperationsService ops) =>
{
    var form = await http.Request.ReadFormAsync();
    var user = CurrentUser(http);
    if (user is null) return Results.Redirect("/login");
    var result = await ops.UpdateProfileAsync(user, F(form,"name"), F(form,"city"), F(form,"phone"), F(form,"bio"), F(form,"skill"), http.RequestAborted);
    return ActionRedirect(form, result);
}).RequireAuthorization();

app.MapPost("/actions/notifications/read", async (HttpContext http, OperationsService ops) =>
{
    var form = await http.Request.ReadFormAsync();
    var user = CurrentUser(http);
    if (user is null) return Results.Redirect("/login");
    var result = await ops.MarkNotificationReadAsync(user, F(form,"notificationId"), http.RequestAborted);
    return ActionRedirect(form, result);
}).RequireAuthorization();

app.MapPost("/actions/notifications/read-all", async (HttpContext http, OperationsService ops) =>
{
    var form = await http.Request.ReadFormAsync();
    var user = CurrentUser(http);
    if (user is null) return Results.Redirect("/login");
    var result = await ops.MarkAllNotificationsReadAsync(user, http.RequestAborted);
    return ActionRedirect(form, result);
}).RequireAuthorization();

app.MapPost("/actions/organization/save", async (HttpContext http, OperationsService ops) =>
{
    var form = await http.Request.ReadFormAsync();
    var user = CurrentUser(http);
    if (user is null) return Results.Redirect("/login");
    var result = await ops.SaveOrganizationProfileAsync(user, F(form,"associationId"), F(form,"name"), F(form,"sector"), F(form,"city"), F(form,"email"), F(form,"description"), http.RequestAborted);
    return ActionRedirect(form, result);
}).RequireAuthorization();

app.MapPost("/actions/organization/request", async (HttpContext http, OperationsService ops) =>
{
    var form = await http.Request.ReadFormAsync();
    var user = CurrentUser(http);
    if (user is null) return Results.Redirect("/login");
    var result = await ops.CreateVerificationRequestAsync(user, F(form,"subject"), F(form,"message"), http.RequestAborted);
    return ActionRedirect(form, result);
}).RequireAuthorization();

app.MapPost("/actions/organization/verify", async (HttpContext http, OperationsService ops) =>
{
    var form = await http.Request.ReadFormAsync();
    var user = CurrentUser(http);
    if (user is null) return Results.Redirect("/login");
    var result = await ops.VerifyAssociationAsync(user, F(form,"associationId"), F(form,"decision"), http.RequestAborted);
    return ActionRedirect(form, result);
}).RequireAuthorization();

app.MapPost("/actions/message/send", async (HttpContext http, OperationsService ops) =>
{
    var form = await http.Request.ReadFormAsync();
    var user = CurrentUser(http);
    if (user is null) return Results.Redirect("/login");
    var result = await ops.SendMessageAsync(user, F(form,"recipientId"), F(form,"title"), F(form,"message"), http.RequestAborted);
    return ActionRedirect(form, result);
}).RequireAuthorization();

app.MapPost("/actions/volunteer/skill", async (HttpContext http, OperationsService ops) =>
{
    var form = await http.Request.ReadFormAsync();
    var user = CurrentUser(http);
    if (user is null) return Results.Redirect("/login");
    var volunteerId = string.IsNullOrWhiteSpace(F(form,"volunteerId")) ? user.Id : F(form,"volunteerId");
    var result = await ops.AddSkillToVolunteerAsync(user, volunteerId, F(form,"skill"), http.RequestAborted);
    return ActionRedirect(form, result);
}).RequireAuthorization();

app.MapPost("/actions/volunteer/hours", async (HttpContext http, OperationsService ops) =>
{
    var form = await http.Request.ReadFormAsync();
    var user = CurrentUser(http);
    if (user is null) return Results.Redirect("/login");
    _ = decimal.TryParse(F(form,"hours"), out var hours);
    var volunteerId = string.IsNullOrWhiteSpace(F(form,"volunteerId")) ? user.Id : F(form,"volunteerId");
    var result = await ops.RecordManualHoursAsync(user, volunteerId, F(form,"sourceId"), hours <= 0 ? 1 : hours, F(form,"note"), http.RequestAborted);
    return ActionRedirect(form, result);
}).RequireAuthorization();

app.MapPost("/actions/impact/create", async (HttpContext http, OperationsService ops) =>
{
    var form = await http.Request.ReadFormAsync();
    var user = CurrentUser(http);
    if (user is null) return Results.Redirect("/login");
    var result = await ops.CreateImpactGoalAsync(user, F(form,"title"), F(form,"sdg"), F(form,"target"), F(form,"deadline"), http.RequestAborted);
    return ActionRedirect(form, result);
}).RequireAuthorization();

app.MapPost("/actions/impact/update", async (HttpContext http, OperationsService ops) =>
{
    var form = await http.Request.ReadFormAsync();
    var user = CurrentUser(http);
    if (user is null) return Results.Redirect("/login");
    var result = await ops.UpdateImpactGoalAsync(user, F(form,"goalId"), F(form,"progress"), F(form,"status"), http.RequestAborted);
    return ActionRedirect(form, result);
}).RequireAuthorization();

app.MapPost("/actions/impact/complete", async (HttpContext http, OperationsService ops) =>
{
    var form = await http.Request.ReadFormAsync();
    var user = CurrentUser(http);
    if (user is null) return Results.Redirect("/login");
    var result = await ops.UpdateImpactGoalAsync(user, F(form,"goalId"), "100", "Completed", http.RequestAborted);
    return ActionRedirect(form, result);
}).RequireAuthorization();

app.MapPost("/actions/admin/user", async (HttpContext http, OperationsService ops) =>
{
    var form = await http.Request.ReadFormAsync();
    var result = await ops.CreateUserAsync(F(form,"name"), F(form,"email"), F(form,"password"), F(form,"role"), http.RequestAborted);
    return ActionRedirect(form, result);
}).RequireAuthorization("Admin");

app.MapPost("/actions/database/update", async (HttpContext http, OperationsService ops) =>
{
    var form = await http.Request.ReadFormAsync();
    var user = CurrentUser(http);
    if (user is null) return Results.Redirect("/login");
    var result = await ops.UpdateDatabaseCellAsync(user, F(form,"table"), F(form,"rowId"), F(form,"column"), F(form,"value"), http.RequestAborted);
    return ActionRedirect(form, result);
}).RequireAuthorization("Admin");

app.MapPost("/actions/database/insert", async (HttpContext http, OperationsService ops) =>
{
    var form = await http.Request.ReadFormAsync();
    var user = CurrentUser(http);
    if (user is null) return Results.Redirect("/login");
    var result = await ops.InsertDatabaseRecordAsync(user, F(form,"table"), F(form,"values"), http.RequestAborted);
    return ActionRedirect(form, result);
}).RequireAuthorization("Admin");

app.MapPost("/actions/database/delete", async (HttpContext http, OperationsService ops) =>
{
    var form = await http.Request.ReadFormAsync();
    var user = CurrentUser(http);
    if (user is null) return Results.Redirect("/login");
    var result = await ops.DeleteDatabaseRowAsync(user, F(form,"table"), F(form,"rowId"), http.RequestAborted);
    return ActionRedirect(form, result);
}).RequireAuthorization("Admin");

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");
app.Run();

static string SafeLocalUrl(string? value)
{
    return string.IsNullOrWhiteSpace(value) || !value.StartsWith('/') || value.StartsWith("//") ? "/" : value;
}


static AppUser? CurrentUser(HttpContext http) => AppUser.FromClaims(http.User);

static string F(IFormCollection form, string key) => form.TryGetValue(key, out var value) ? value.ToString() : string.Empty;

static IResult ActionRedirect(IFormCollection form, OperationResult result)
{
    var returnUrl = SafeLocalUrl(F(form, "returnUrl"));
    var separator = returnUrl.Contains('?') ? '&' : '?';
    var flag = result.Succeeded ? "ok" : "error";
    return Results.Redirect($"{returnUrl}{separator}{flag}={Uri.EscapeDataString(result.Message)}");
}
