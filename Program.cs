using AcxiomCRM.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

builder.Services.AddHttpContextAccessor();
builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

// Identity with roles. Confirmed-account sign-in is off because there is no email sender;
// users are created by an Admin (public self-registration is disabled below).
builder.Services.AddDefaultIdentity<IdentityUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;

        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireNonAlphanumeric = true;

        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);

        options.User.RequireUniqueEmail = true;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Identity/Account/Login";
    options.AccessDeniedPath = "/Identity/Account/AccessDenied";

    // Hardened auth cookie: not readable from JS, Lax same-site, HTTPS-only outside Development
    // (Development also runs on plain http://localhost), 8-hour sliding session.
    options.Cookie.Name = "AcxiomCRM.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;

    // Audit logout (the login page audits sign-in attempts itself).
    options.Events.OnSigningOut = async ctx =>
    {
        var db = ctx.HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
        db.Audit(ctx.HttpContext.User.Identity?.Name, "Logout", "Account",
            ctx.HttpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "-");
        await db.SaveChangesAsync();
    };

    // API callers get status codes instead of HTML login/access-denied redirects.
    options.Events.OnRedirectToLogin = ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        else ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        else ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    };
});

// Every endpoint requires a signed-in user unless it opts out with [AllowAnonymous]
// (the Identity login/lockout pages do).
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});

// Anti-forgery token validation on every unsafe (POST/PUT/DELETE) MVC request.
builder.Services.AddControllersWithViews(options =>
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));

// API actions run the shared CrmRules themselves after binding, so every problem (annotations and
// business rules) is reported in one 400 response instead of stopping at the first annotation failure.
builder.Services.Configure<ApiBehaviorOptions>(o => o.SuppressModelStateInvalidFilter = true);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// Apply pending EF Core migrations, seed roles and (in Development) demo accounts.
await DbSeeder.InitializeAsync(app.Services, app.Configuration);

app.UseHttpsRedirection();

// API errors never return HTML or stack traces: unhandled exceptions become a generic JSON 500.
app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/api"), api => api.UseExceptionHandler(handler =>
    handler.Run(async ctx =>
    {
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await ctx.Response.WriteAsJsonAsync(new { title = "An unexpected error occurred.", status = 500 });
    })));

// Identity UI pages that must not be reachable:
//  - Register: accounts are created by an Admin on the Users page (no public self-registration).
//  - Personal-data download/delete, email change, external logins, 2FA and email-based recovery:
//    users must not delete their own CRM account or change the login an Admin assigned, and the
//    app has no email sender. Users keep Profile and Change Password.
string[] blockedIdentityPages =
[
    "/Identity/Account/Register", "/Identity/Account/RegisterConfirmation",
    "/Identity/Account/ExternalLogin", "/Identity/Account/ForgotPassword", "/Identity/Account/ForgotPasswordConfirmation",
    "/Identity/Account/ResetPassword", "/Identity/Account/ResetPasswordConfirmation",
    "/Identity/Account/ResendEmailConfirmation", "/Identity/Account/ConfirmEmail", "/Identity/Account/ConfirmEmailChange",
    "/Identity/Account/LoginWith2fa", "/Identity/Account/LoginWithRecoveryCode",
    "/Identity/Account/Manage/PersonalData", "/Identity/Account/Manage/DeletePersonalData",
    "/Identity/Account/Manage/DownloadPersonalData", "/Identity/Account/Manage/Email",
    "/Identity/Account/Manage/ExternalLogins", "/Identity/Account/Manage/TwoFactorAuthentication",
    "/Identity/Account/Manage/EnableAuthenticator", "/Identity/Account/Manage/Disable2fa",
    "/Identity/Account/Manage/ResetAuthenticator", "/Identity/Account/Manage/GenerateRecoveryCodes",
    "/Identity/Account/Manage/ShowRecoveryCodes", "/Identity/Account/Manage/SetPassword"
];
app.Use(async (context, next) =>
{
    if (blockedIdentityPages.Any(p => context.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next();
});

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets().AllowAnonymous();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

// Identity's UI (login, logout, account management) ships as Razor Pages.
app.MapRazorPages().WithStaticAssets();

app.Run();
