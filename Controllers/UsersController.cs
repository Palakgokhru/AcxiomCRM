using System.ComponentModel.DataAnnotations;
using AcxiomCRM.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

public class CreateUserViewModel
{
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required, DataType(DataType.Password), StringLength(100, MinimumLength = 8)]
    public string Password { get; set; } = string.Empty;

    [Required, DataType(DataType.Password), Compare(nameof(Password), ErrorMessage = "Passwords do not match.")]
    [Display(Name = "Confirm password")]
    public string ConfirmPassword { get; set; } = string.Empty;

    [Required]
    public string Role { get; set; } = Roles.SalesExecutive;
}

public record UserRow(string Id, string Email, string Role, bool IsLockedOut);

// Controlled user creation and role management. Admin only.
[Authorize(Roles = Roles.Admin)]
public class UsersController(UserManager<IdentityUser> userManager, ApplicationDbContext db) : Controller
{
    public async Task<IActionResult> Index()
    {
        var rows = new List<UserRow>();
        foreach (var u in await userManager.Users.OrderBy(u => u.Email).ToListAsync())
        {
            var roles = await userManager.GetRolesAsync(u);
            rows.Add(new UserRow(u.Id, u.Email ?? u.UserName ?? "", string.Join(", ", roles),
                u.LockoutEnd.HasValue && u.LockoutEnd > DateTimeOffset.UtcNow));
        }
        return View(rows);
    }

    public IActionResult Create() => View(new CreateUserViewModel());

    [HttpPost]
    public async Task<IActionResult> Create(CreateUserViewModel model)
    {
        if (!Roles.All.Contains(model.Role))
            ModelState.AddModelError(nameof(model.Role), "Choose a valid role.");
        if (!ModelState.IsValid) return View(model);

        var user = new IdentityUser { UserName = model.Email.Trim(), Email = model.Email.Trim(), EmailConfirmed = true };
        var result = await userManager.CreateAsync(user, model.Password);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors) ModelState.AddModelError(string.Empty, e.Description);
            return View(model);
        }
        await userManager.AddToRoleAsync(user, model.Role);

        db.Audit(User.Identity!.Name, "Create", "User", user.Id, $"{user.Email} as {model.Role}");
        await db.SaveChangesAsync();
        TempData["Success"] = $"User {user.Email} created as {model.Role}.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> SetRole(string id, string role)
    {
        var user = await userManager.FindByIdAsync(id);
        if (user is null) return NotFound();
        if (!Roles.All.Contains(role)) return BadRequest();
        if (user.UserName == User.Identity!.Name && role != Roles.Admin)
        {
            TempData["Error"] = "You cannot remove your own Admin role.";
            return RedirectToAction(nameof(Index));
        }

        await userManager.RemoveFromRolesAsync(user, await userManager.GetRolesAsync(user));
        await userManager.AddToRoleAsync(user, role);
        db.Audit(User.Identity.Name, "SetRole", "User", user.Id, $"{user.Email} -> {role}");
        await db.SaveChangesAsync();
        TempData["Success"] = $"{user.Email} is now {role}.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> ToggleLock(string id)
    {
        var user = await userManager.FindByIdAsync(id);
        if (user is null) return NotFound();
        if (user.UserName == User.Identity!.Name)
        {
            TempData["Error"] = "You cannot lock your own account.";
            return RedirectToAction(nameof(Index));
        }

        var locked = user.LockoutEnd.HasValue && user.LockoutEnd > DateTimeOffset.UtcNow;
        await userManager.SetLockoutEndDateAsync(user, locked ? null : DateTimeOffset.MaxValue);
        if (locked) await userManager.ResetAccessFailedCountAsync(user);

        db.Audit(User.Identity.Name, locked ? "Unlock" : "Lock", "User", user.Id, user.Email ?? "");
        await db.SaveChangesAsync();
        TempData["Success"] = $"{user.Email} {(locked ? "unlocked" : "locked")}.";
        return RedirectToAction(nameof(Index));
    }
}

[Authorize(Roles = Roles.Admin)]
public class AuditLogController(ApplicationDbContext db) : Controller
{
    public async Task<IActionResult> Index() =>
        View(await db.AuditLogs.OrderByDescending(a => a.CreatedDate).Take(500).ToListAsync());
}
