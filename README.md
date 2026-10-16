# AcxiomCRM

A web-based CRM built with ASP.NET Core MVC: customers, leads (with conversion), opportunities,
follow-ups, activities, a role-scoped dashboard, reports, an audit log and a REST API.

| Piece      | Choice                                                                  |
|------------|-------------------------------------------------------------------------|
| SDK        | .NET 10.0.402 (`net10.0`, ASP.NET Core 10.0.13)                          |
| UI         | ASP.NET Core MVC, Razor views, Bootstrap 5, Chart.js 4.4.1 (jsDelivr CDN)|
| Data       | Entity Framework Core 10 with **SQLite** (`app.db`)                      |
| Auth       | ASP.NET Core Identity (hashed passwords) with roles                     |
| Validation | Data annotations + jQuery unobtrusive client validation, re-checked on the server |

No packages beyond the ASP.NET Core / EF Core / Identity templates are used.

---

## 1. First-time setup

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
cd AcxiomCRM
dotnet restore
```

### Development accounts (passwords are NOT in source control)

`appsettings.Development.json` lists the demo accounts and roles only. Their passwords come from
[user-secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets) (stored in your user
profile, outside the repo). Set them once — **development only, choose your own values if you like**
(they must satisfy the password policy below):

```bash
dotnet user-secrets set "SeedUsers:0:Password" "Admin#12345"
dotnet user-secrets set "SeedUsers:1:Password" "Manager#12345"
dotnet user-secrets set "SeedUsers:2:Password" "Sales#12345"
dotnet user-secrets set "SeedUsers:3:Password" "Sales#12345"
```

| Index | Email                     | Role           | Suggested dev password |
|-------|---------------------------|----------------|------------------------|
| 0     | `admin@acxiomcrm.local`   | Admin          | `Admin#12345`          |
| 1     | `manager@acxiomcrm.local` | Manager        | `Manager#12345`        |
| 2     | `sales@acxiomcrm.local`   | SalesExecutive | `Sales#12345`          |
| 3     | `sales2@acxiomcrm.local`  | SalesExecutive | `Sales#12345`          |

- Accounts are created at startup **only in the Development environment** and only if missing; a
  seed user without a configured password is skipped with a warning. Nothing is seeded in Production.
- Environment variables work too (e.g. `SeedUsers__0__Password`).
- To change a password later: log in and use **(your email) → Password**, or delete `app.db` and
  re-seed with new secrets.
- The roles `Admin`, `Manager` and `SalesExecutive` are always seeded.

## 2. Run

```bash
dotnet run --launch-profile http        # http://localhost:5058
# or HTTPS:
dotnet dev-certs https --trust
dotnet run --launch-profile https       # https://localhost:7139
```

Log in as `admin@acxiomcrm.local`. Stop with **Ctrl+C**.

### Database

SQLite file `app.db` in the project folder. EF Core migrations are applied automatically at
startup (`DbSeeder.InitializeAsync`), so there is no manual migration step. To start clean, stop
the app and delete `app.db`. To add a migration: `dotnet tool install --global dotnet-ef`, then
`dotnet ef migrations add <Name> -o Data/Migrations`.

Configuration (`appsettings.json`, section `Crm`): `MaxLeadExpectedValue` (default 100,000,000)
and `DashboardMonths` (monthly chart period, default 6).

---

## 3. Security and roles

- **Identity**: passwords hashed by ASP.NET Core Identity (no custom password storage).
  Policy: 8+ characters with upper case, lower case, digit and symbol; unique email.
- **Lockout**: 5 failed sign-ins lock the account for 15 minutes; Admins can lock/unlock.
- **Cookie**: `AcxiomCRM.Auth`, HttpOnly, SameSite=Lax, HTTPS-only outside Development,
  8-hour sliding session. Access denied → `/Identity/Account/AccessDenied`.
- **Every page requires sign-in** (global fallback policy); successful login → Dashboard.
- **Anti-forgery**: every MVC POST requires a valid token (global filter). The JSON API accepts only
  `application/json`, which cross-site forms cannot send.
- **No public registration**: Admin creates users on **Users**. Self-service account deletion,
  personal-data download, email change, external logins, 2FA and email recovery pages are disabled
  (404); users keep Profile and Change Password.
- **Queries** use EF Core LINQ only (parameterised), no concatenated SQL.

| Capability | Admin | Manager | SalesExecutive |
|---|---|---|---|
| Customers / leads / opportunities / follow-ups / activities | all | all (team) | **only records assigned to them** |
| Assign records to users | ✓ | ✓ | always themselves |
| Permanently delete customers, leads, opportunities | ✓ | – | – |
| Reports (pipeline, leads, conversion, user activity, …) | ✓ | ✓ | – |
| Audit events report, Audit Log | ✓ | – | – |
| Users & roles, lock/unlock | ✓ | – | – |
| Historical follow-up entry | ✓ | ✓ | – |

The app has a single sales team, so a Manager's "team" is every record. Ownership is enforced in
the database query (`CrmScope.Scoped`) for every list, detail, edit, POST, report, dashboard figure
and API call: another user's record id returns **404**, and referencing it from a form/API is a
validation error. A SalesExecutive's record can only link to customers/leads/opportunities they own.

---

## 4. Modules

| Module | Features |
|---|---|
| **Customers** | List with search (name/email/phone/company/code) and status/assignee filters; create, details (with related opportunities, follow-ups, activities, source lead), edit, deactivate, permanent delete (Admin, only without opportunities). Code `CUST-00001` generated from the never-reused Id. |
| **Leads** | Search, status and assignee filters; create, details, edit, mark Lost, delete (Admin). **Convert** workflow: if a customer with the lead's normalised email/phone exists it is linked (no duplicate), otherwise a new customer is created; optional opportunity from the expected value; lead becomes Converted with customer link + date; audited. Converted status is only set by this workflow. |
| **Opportunities** | Search by name/customer, stage/status/assignee filters; create, details (related follow-ups/activities), edit, close as Lost, delete (Admin). |
| **Follow-ups** | Pending / Overdue / Next 7 days / by status views, date range, assignee and related-record filters; schedule, details, reschedule/edit, Complete / Missed / Cancel (confirmed). Admin/Manager-only historical entry for past follow-ups. |
| **Activities** | Call / Meeting / Email / Task with subject, description, date, related record, assignee, status; filters by type/status/date/assignee; create, details, edit, complete, delete. |
| **Dashboard** | KPI cards, three Chart.js charts (lead status, opportunity pipeline, monthly outcomes), pending follow-ups, Admin statistics; Today / This week / This month / Custom period. |
| **Reports** | Customers, Leads, Follow-ups, Opportunities, Pipeline (incl. weighted), Lead conversion, User activity, Audit events (Admin). Date presets, filters, CSV export. |
| **Users** | Admin: create users with a role, change role, lock/unlock. |
| **Audit Log** | Admin: filter by user, entity, action and date range; paged. |

### Section 17.7 business rules (all enforced on the server in `Data/CrmRules.cs`, shared by MVC and API)

| Rule | Implementation |
|---|---|
| Valid, unique email | `[EmailAddress]` + server check; stored lower-case; unique index + duplicate check (edit excludes itself) |
| Valid, unique phone | 7–15 digits, optional `+`; normalised (`+91 98765-43210` → `+919876543210`); unique index + check |
| Opportunity amount | never negative; **Open** (active) opportunities need Amount > 0 |
| Expected close date | not before today for Open opportunities |
| Required fields | Customer name/email/phone, lead name, opportunity name/customer, etc. |
| Duplicate customers | blocked on normalised email or phone (also during lead conversion); same name with different contact details is allowed, not merged |
| Lead status | only New, Contacted, Qualified, Unqualified, Converted, Lost |
| Follow-up date | new/planned follow-ups cannot be before today (create and reschedule); past entries only via the Admin/Manager historical flow |
| Probability | 0–100 inclusive |

Stages are limited to Qualification, Proposal, Negotiation, Won, Lost; Won/Lost require status Closed.
Duplicate races are caught by the unique indexes and shown as a friendly message.

---

## 5. REST API

Authentication is the same cookie as the website. Responses are DTOs (never Identity fields);
errors are `application/problem+json`.

| Method | Route | Responses |
|---|---|---|
| GET | `/api/customers?search=&status=` | 200 |
| GET | `/api/customers/{id}` | 200, 404 (missing or not yours) |
| POST | `/api/customers` | 201 + Location, 400, 403, 409 duplicate |
| PUT | `/api/customers/{id}` | 200, 400, 403, 404, 409 |
| DELETE | `/api/customers/{id}` | 204 (deactivates), 404 |
| GET | `/api/leads?status=`, `/api/leads/{id}` | 200, 404 |
| POST | `/api/leads` | 201, 400, 403 |
| GET | `/api/opportunities?stage=&status=`, `/api/opportunities/{id}` | 200, 404 |
| POST | `/api/opportunities` | 201, 400 (17.7 rules, invalid/foreign references), 403 |

401 = not signed in; 403 = a SalesExecutive tries to assign a record to someone else; 415 = not JSON.

### curl example (Git Bash)

```bash
B=http://localhost:5058
# 1. sign in (gets the anti-forgery token from the login form, stores the auth cookie in jar.txt)
T=$(curl -s -c jar.txt $B/Identity/Account/Login | grep -o 'name="__RequestVerificationToken" type="hidden" value="[^"]*' | sed 's/.*value="//')
curl -s -o /dev/null -b jar.txt -c jar.txt --data-urlencode "Input.Email=sales@acxiomcrm.local" \
     --data-urlencode "Input.Password=Sales#12345" --data-urlencode "__RequestVerificationToken=$T" $B/Identity/Account/Login

# 2. call the API
curl -s -b jar.txt $B/api/customers
curl -s -b jar.txt -X POST -H 'Content-Type: application/json' $B/api/customers \
     -d '{"customerName":"Asha Rao","email":"asha@example.com","phone":"+91 98765 43210","companyName":"Rao Traders","status":"Active"}'
curl -s -b jar.txt -X POST -H 'Content-Type: application/json' $B/api/opportunities \
     -d '{"opportunityName":"Annual deal","customerId":1,"amount":25000,"stage":"Proposal","probability":60,"expectedCloseDate":"2027-01-31","status":"Open"}'
```

---

## 6. Audit log

`AuditLogs` (append-only; no edit/delete UI) stores `AuditLogId`, `UserId`, user name, `Action`,
`EntityName`, `RecordId`, `OldValue` / `NewValue` (JSON snapshot of business fields),
`CreatedDate` (UTC) and `IpAddress`. Logged events: login success / failure / lockout, logout;
create, update, deactivate, delete of customers, leads, opportunities, follow-ups and activities
(MVC and API); lead conversion; follow-up reschedule and status changes; historical entries; user
creation, role changes, lock/unlock; report CSV exports. Passwords, hashes and tokens are never
written.

---

## 7. Tests

```bash
bash tests/run-tests.sh
```

Builds the app, starts a separate instance on **port 5099 with a fresh `tests/test.db`** (your
`app.db` and a dev server on 5058 are not touched), then runs:

- `tests/acceptance.sh` — 70 HTTP checks: every Section 17.19 scenario, every Section 17.7 rule,
  password hashing, lockout, password policy, cookie flags, blocked Identity pages, anti-forgery,
  search/filters, lead conversion, reports, API status codes, audit contents; database state is
  verified directly with SQLite.
- `tests/browser-tests.mjs` — 9 checks in headless Microsoft Edge (Node 22, no npm packages):
  inline client-side validation blocks invalid submissions, and the Chart.js charts render with the
  server's role-scoped data. Screenshots are saved to `tests/`.

Requires Python 3, Node 22+ and Microsoft Edge, plus the user-secrets above.

---

## 8. Project layout

```
Program.cs                      Identity, password/lockout/cookie policy, global auth + anti-forgery, blocked pages, API errors
Data/
  ApplicationDbContext.cs       EF model, unique indexes, audit helper (Audit, AuditFields)
  CrmRules.cs                   Section 17.7 business rules shared by MVC and API
  CrmScope.cs                   Ownership scope (role + assignment) shared by MVC and API
  DbSeeder.cs                   Migrate on startup, seed roles + dev accounts (passwords from user-secrets)
  Migrations/                   EF Core migrations
Models/
  Customer.cs                   Customer, AuditLog
  Crm.cs                        Lead, Opportunity, FollowUp, Activity, allowed value sets, phone format
  Reporting.cs                  Date range presets, report view model
Controllers/
  CrmController.cs              Base class for CRM pages (scope, assignment, related-record checks)
  HomeController.cs             Dashboard
  CustomersController.cs, LeadsController.cs (incl. Convert), OpportunitiesController.cs,
  FollowUpsController.cs, ActivitiesController.cs, ReportsController.cs
  UsersController.cs            Users/roles (Admin) + AuditLogController
  Api/CustomersApiController.cs REST API: customers, leads, opportunities
Areas/Identity/Pages/Account/   Custom Login (lockout + audit), trimmed account menu
Views/                          Razor views per module; Shared/ partials (_Alerts, _AssignField, _RelatedFields, …)
tests/                          run-tests.sh, acceptance.sh, browser-tests.mjs
```

## 9. Known limitations

- One sales team: Managers see all records (there is no team/territory model).
- Chart.js loads from a CDN; offline, the charts show a notice while cards and reports still work.
- The API uses the website's cookie sign-in; there are no API tokens and no Swagger UI (curl examples above).
- Leads and opportunities have GET/POST in the API but no PUT/DELETE there (available in the UI).
- Stack traces: the Development environment shows ASP.NET's developer error page by design; the
  Production error page and the JSON API error handler hide details, but Production mode was not run
  in the automated tests (it requires HTTPS and has no seeded users).
- SQLite stores decimals as text, so money totals are summed in memory (fine for this data volume).
