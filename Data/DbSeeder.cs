using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Data;

public static class Roles
{
    public const string Admin = "Admin";
    public const string Manager = "Manager";
    public const string SalesExecutive = "SalesExecutive";
    public const string AdminOrManager = Admin + "," + Manager;

    public static readonly string[] All = [Admin, Manager, SalesExecutive];
}

/// <summary>
/// Applies EF Core migrations, seeds the three roles, and (only when configured, i.e. in
/// appsettings.Development.json) seeds development demo accounts. Safe to run on every startup.
/// </summary>
public static class DbSeeder
{
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration config)
    {
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;

        await provider.GetRequiredService<ApplicationDbContext>().Database.MigrateAsync();

        var roleManager = provider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole(role));
        }

        // "SeedUsers" section: [{ "Email": "...", "Role": "Admin" }] in appsettings.Development.json;
        // each "SeedUsers:N:Password" comes from user-secrets / environment variables (never committed).
        var userManager = provider.GetRequiredService<UserManager<IdentityUser>>();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("DbSeeder");
        foreach (var seed in config.GetSection("SeedUsers").GetChildren())
        {
            var email = seed["Email"];
            var password = seed["Password"];
            var role = seed["Role"];
            if (string.IsNullOrWhiteSpace(email) || !Roles.All.Contains(role))
                continue;

            var existing = await userManager.FindByEmailAsync(email);
            if (existing is null && string.IsNullOrWhiteSpace(password))
            {
                // Passwords never live in source control; they come from user-secrets or environment variables.
                logger.LogWarning("Seed user {Email} skipped: no password configured (SeedUsers:{Index}:Password). See README.", email, seed.Key);
                continue;
            }

            var user = existing;
            if (user is null)
            {
                user = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
                var result = await userManager.CreateAsync(user, password);
                if (!result.Succeeded)
                    throw new InvalidOperationException($"Could not create seed user {email}: " +
                        string.Join("; ", result.Errors.Select(e => e.Description)));
            }

            if (!await userManager.IsInRoleAsync(user, role!))
                await userManager.AddToRoleAsync(user, role!);
        }
    }
}
