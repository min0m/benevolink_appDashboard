using BenevoLink.Models;

namespace BenevoLink.Services;

public sealed class AuthService(XamppMySql db, DatabaseCatalog catalog)
{
    public async Task<LoginResult> ValidateAsync(string email, string password, CancellationToken ct = default)
    {
        email = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return LoginResult.Fail("Enter your email and password.");
        }

        try
        {
            await catalog.EnsureLoadedAsync(db, ct);
            var usersTable = catalog.FindTable("users", "Users", "user", "accounts", "members");
            if (usersTable is null) return LoginResult.Fail("The connected benevolink database has no users table.");

            var emailColumn = catalog.FindColumn(usersTable, "email", "Email", "mail", "user_email");
            if (emailColumn is null) return LoginResult.Fail("The users table has no email column.");

            var rows = await db.QueryAsync(
                $"SELECT * FROM {XamppMySql.Quote(usersTable)} WHERE LOWER({XamppMySql.Quote(emailColumn)}) = @email LIMIT 1;",
                new Dictionary<string, object?> { ["@email"] = email }, ct);

            if (rows.Count == 0) return LoginResult.Fail("Invalid email or password.");
            var row = rows[0];

            var passwordColumn = FirstValue(row, "password_hash", "PasswordHash", "password", "Password", "hash", "pass_hash");
            var salt = FirstValue(row, "password_salt", "PasswordSalt", "salt", "PasswordSaltBase64");
            if (!PasswordVerifier.Verify(password, passwordColumn ?? string.Empty, salt))
            {
                return LoginResult.Fail("Invalid email or password.");
            }

            var id = FirstValue(row, "id", "Id", "user_id", "UserId") ?? email;
            var name = FirstValue(row, "display_name", "DisplayName", "name", "full_name", "username", "first_name") ?? email.Split('@')[0];
            var roleRaw = FirstValue(row, "role", "Role", "account_type", "type", "user_type") ?? RoleFromEmail(email);
            var status = FirstValue(row, "status", "Status", "state") ?? "Active";
            var role = NormalizeRole(roleRaw, email);

            return LoginResult.Success(new AppUser(id, email, name, role, status, usersTable));
        }
        catch (Exception ex)
        {
            return LoginResult.Fail("Database login failed: " + XamppMySql.CleanError(ex.Message));
        }
    }

    private static string? FirstValue(Dictionary<string, string> row, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (row.TryGetValue(candidate, out var value) && !string.IsNullOrWhiteSpace(value)) return value;
        }
        foreach (var (key, value) in row)
        {
            var normalized = Normalize(key);
            if (candidates.Any(c => Normalize(c) == normalized) && !string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static string NormalizeRole(string raw, string email)
    {
        raw = raw.Trim().ToLowerInvariant();
        if (email.StartsWith("admin@") || raw.Contains("admin") || raw.Contains("owner")) return "Admin";
        if (raw.Contains("org") || raw.Contains("association") || raw.Contains("ngo") || raw.Contains("responsable") || raw.Contains("manager") || email.Contains("croissant") || email.Contains("jeunes")) return "Organization";
        return "Volunteer";
    }

    private static string RoleFromEmail(string email)
    {
        if (email.StartsWith("admin@")) return "Admin";
        if (email.Contains("croissant") || email.Contains("jeunes")) return "Organization";
        return "Volunteer";
    }

    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
