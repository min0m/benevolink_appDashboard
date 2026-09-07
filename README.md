# BenevoLink ImpactOS

BenevoLink is a Blazor Server application that connects to an existing local XAMPP MySQL database named `benevolink`.

## What this app does

- Uses `src/BenevoLink.App/appsettings.json` to store the MySQL connection string.
- Connects directly to a live XAMPP MySQL instance via `MySqlConnector`.
- Provides user authentication and role-based access control using cookies.
- Exposes administrative pages for:
  - Applications and approvals
  - Certificates
  - Events
  - Organizations
  - Volunteers
  - Notifications
  - A database control surface for browsing and editing tables
- Uses a `DatabaseCatalog` service to inspect the MySQL schema and table metadata.
- Uses `AuthService`, `OperationsService`, and `PlatformService` to perform database-driven actions.

## Key files and components

- `src/BenevoLink.App/Program.cs`: application startup, authentication, authorization, and form endpoints.
- `src/BenevoLink.App/appsettings.json`: MySQL connection settings.
- `src/BenevoLink.App/Services/XamppMySql.cs`: opens connections, checks health, and loads schema metadata.
- `src/BenevoLink.App/Services/AuthService.cs`: authenticates users against the MySQL database.
- `src/BenevoLink.App/Services/OperationsService.cs`: handles mission applications, user management, and CRUD operations.
- `src/BenevoLink.App/Pages/`: Razor pages for login, register, admin dashboard, database browser, and domain-specific features.

## Database configuration

The MySQL connection string is configured in `src/BenevoLink.App/appsettings.json`.

The `BenevoLink` connection string now includes the password provided by the user.

## Running the app

1. Ensure XAMPP and MySQL are running.
2. Confirm the `benevolink` database exists in MySQL.
3. Run the Blazor Server app from the project folder.

> If you run a previously built copy, rebuild the project after changing `appsettings.json` so the updated connection string is included in the output directory.
