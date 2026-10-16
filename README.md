# AcxiomCRM

A web-based CRM built with ASP.NET Core MVC. **This repository currently contains the base
application only** — see [Project status](#project-status) for exactly what works.

## Stack

| Piece       | Choice                                                      |
|-------------|-------------------------------------------------------------|
| SDK         | .NET 10.0.402                                                |
| Framework   | `net10.0` (ASP.NET Core 10.0.13)                             |
| UI          | ASP.NET Core MVC, Razor views, Bootstrap 5                   |
| Data        | Entity Framework Core 10 with **SQLite** (`app.db`)          |
| Auth        | ASP.NET Core Identity with the default Identity UI, roles on |
| Validation  | Data annotations + jQuery unobtrusive client-side validation |

SQLite was chosen for zero-setup local development: no server to install, and the database
file is created automatically on first run.

## Running it

From the project folder:

```bash
dotnet restore
dotnet build
dotnet run --launch-profile http      # http://localhost:5058
```

For HTTPS instead, trust the dev certificate once and use the `https` profile:

```bash
dotnet dev-certs https --trust
dotnet run --launch-profile https     # https://localhost:7139
```

The database is created and migrated automatically at startup (`DbSeeder.InitializeAsync`
in [Program.cs](Program.cs)), so there is no manual migration step for a normal run.

## Development accounts

Seeded at startup **only from `appsettings.Development.json`** (`SeedUsers` section), so no
credentials are seeded in Production. Remove or change them before deploying anywhere real.

| Email                     | Password        | Role           |
|---------------------------|-----------------|----------------|
| `admin@acxiomcrm.local`   | `Admin#12345`   | Admin          |
| `manager@acxiomcrm.local` | `Manager#12345` | Manager        |
| `sales@acxiomcrm.local`   | `Sales#12345`   | SalesExecutive |
| `sales2@acxiomcrm.local`  | `Sales#12345`   | SalesExecutive |

Public self-registration is disabled; an Admin creates accounts on **Users**.

## Security and roles

- ASP.NET Core Identity (hashed passwords). Password policy: 8+ chars, upper, lower, digit, symbol.
- Lockout: 5 failed logins locks the account for 15 minutes (custom
  `Areas/Identity/Pages/Account/Login` passes `lockoutOnFailure: true`). Admins can lock/unlock users.
- Global fallback policy: every page requires login except the Identity login pages and static files.
- Global `AutoValidateAntiforgeryToken`: any MVC POST without a valid token gets HTTP 400.
- **Admin**: everything, including Users, Audit Log and permanent customer delete.
- **Manager**: all customers (team view) and Reports; no Users/Audit Log (server returns Access Denied).
- **SalesExecutive**: only customers assigned to them; other records return 404 even by URL.
  Customers they create are assigned to themselves; they cannot reassign.

## Project status

Working:

- Login/logout, lockout, Admin user management (create, change role, lock/unlock)
- Customers: list with search (name, email, phone, company, code) and status filter,
  create, details, edit, deactivate (soft delete), permanent delete (Admin)
- Validation: data annotations + unobtrusive client validation, re-checked on the server;
  email and phone format; unique email, phone, and name+company (checked in code and enforced
  by unique indexes; constraint violations show a friendly message, not an exception)
- Audit log of customer create/update/deactivate/delete and user admin actions

- Leads: list (search + status filter), create, details, edit, mark Lost, delete (Admin).
  Status restricted server-side to New/Contacted/Qualified/Unqualified/Converted/Lost;
  ExpectedValue cannot be negative; email/phone format checked.
- Opportunities: list (stage/status filter), create, details, edit. Server-side rules:
  Amount >= 0; Open (active) opportunities need Amount > 0 and ExpectedCloseDate >= today;
  Probability 0-100; Stage limited to Qualification/Proposal/Negotiation/Won/Lost;
  Won/Lost must be Closed; customer/lead must exist and be visible to the user.
- Follow-ups: schedule against a customer/lead/opportunity, list by status (pending by default,
  overdue highlighted), mark Completed/Missed/Cancelled. A Planned follow-up before today is rejected.
  Pending follow-ups are also shown on the Dashboard.
- Activities (basic): log Call/Meeting/Email/Task with subject, date, related record, assignee,
  status; mark Completed. No edit page.
- Ownership: Leads, Opportunities, Follow-ups and Activities follow the same rule as Customers
  (SalesExecutive = own records only, 404 on other ids; Admin/Manager = all).

**Not built yet**: Reports (placeholder page, Admin/Manager only). Follow-ups and activities have
no edit/delete pages.

## Layout

```
Controllers/
  HomeController.cs       Dashboard, error
  CustomersController.cs  Customer CRUD, search, role scoping, duplicate validation
  UsersController.cs      Admin user/role management + AuditLogController
  CrmController.cs        Base class: ownership scoping, related-record and allowed-value checks
  LeadsController.cs, OpportunitiesController.cs, FollowUpsController.cs, ActivitiesController.cs
  ModulesController.cs    Reports placeholder
Data/
  ApplicationDbContext.cs Identity DbContext, Customers, AuditLogs, unique indexes
  DbSeeder.cs             Migrate on startup, seed roles + dev accounts
  Migrations/             EF Core migrations (Identity schema)
Views/
  Home/Index.cshtml       Dashboard
  Modules/Placeholder.cshtml
  Shared/_Layout.cshtml   Navigation and page chrome
Areas/Identity/           Identity UI scaffolding hook
```

## Working with migrations

The EF Core tools are referenced by the project but the `dotnet-ef` CLI is a separate
global tool. Install it once if you need to add migrations:

```bash
dotnet tool install --global dotnet-ef
dotnet ef migrations add <Name>
dotnet ef database update
```

To start from a clean database, stop the app and delete `app.db`; the next run recreates
and reseeds it.
