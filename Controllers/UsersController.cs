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

// Audit log viewer: Admin only. Read-only (append-oriented): there are no edit/delete actions.
[Authorize(Roles = Roles.Admin)]
public class AuditLogController(ApplicationDbContext db) : Controller
{
    public const int PageSize = 200;

    public async Task<IActionResult> Index(string? user, string? entity, string? act, string? preset, DateTime? from, DateTime? to, int page = 1)
    {
        var range = AcxiomCRM.Models.DateRange.Resolve(preset, from, to);
        var q = db.AuditLogs.AsQueryable();
        if (!string.IsNullOrWhiteSpace(user)) q = q.Where(a => a.UserName.ToLower().Contains(user.Trim().ToLower()));
        if (!string.IsNullOrWhiteSpace(entity)) q = q.Where(a => a.EntityName == entity);
        if (!string.IsNullOrWhiteSpace(act)) q = q.Where(a => a.Action == act);
        if (range.From is DateTime f) q = q.Where(a => a.CreatedDate >= f);
        if (range.ToExclusive is DateTime t) q = q.Where(a => a.CreatedDate < t);

        page = Math.Max(1, page);
        ViewBag.Total = await q.CountAsync();
        ViewBag.Page = page;
        ViewBag.Entities = await db.AuditLogs.Select(a => a.EntityName).Distinct().OrderBy(x => x).ToListAsync();
        ViewBag.Actions = await db.AuditLogs.Select(a => a.Action).Distinct().OrderBy(x => x).ToListAsync();
        ViewBag.Range = range;
        ViewData["User"] = user;
        ViewData["Entity"] = entity;
        ViewData["Act"] = act;
        return View(await q.OrderByDescending(a => a.CreatedDate).Skip((page - 1) * PageSize).Take(PageSize).ToListAsync());
    }
}
