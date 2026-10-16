using System.Security.Claims;
using AcxiomCRM.Data;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

/// <summary>
/// Shared ownership rules for the CRM modules (Leads, Opportunities, Follow-ups, Activities).
/// Admin and Manager see team records; a SalesExecutive only sees records assigned to them.
/// Every record lookup goes through <see cref="Scoped{T}"/>, so changing an id in the URL
/// or a crafted POST returns 404 / a validation error rather than another user's data.
/// </summary>
[Authorize]
public abstract class CrmController(ApplicationDbContext db, UserManager<IdentityUser> userManager) : Controller
{
    protected ApplicationDbContext Db => db;
    protected string CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    protected bool CanSeeAll => User.IsInRole(Roles.Admin) || User.IsInRole(Roles.Manager);
    protected string UserName => User.Identity?.Name ?? "(unknown)";

    protected IQueryable<T> Scoped<T>(IQueryable<T> query) where T : class, IAssignable
    {
        if (CanSeeAll) return query;
        var userId = CurrentUserId;
        return query.Where(x => x.AssignedUserId == userId);
    }

    // SalesExecutives always own what they create/edit; Admin/Manager may assign anyone (or nobody).
    protected async Task<string?> ResolveAssigneeAsync(string? requested, string? existing = null)
    {
        if (!CanSeeAll) return existing ?? CurrentUserId;
        if (string.IsNullOrEmpty(requested)) return null;
        if (await userManager.FindByIdAsync(requested) is null)
        {
            ModelState.AddModelError("AssignedUserId", "Selected user does not exist.");
            return null;
        }
        return requested;
    }

    // Server-side check that a referenced customer/lead/opportunity exists AND is visible to this user.
    protected async Task CheckRelatedAsync(int? customerId, int? leadId, int? opportunityId)
    {
        if (customerId is int c && !await Scoped(db.Customers).AnyAsync(x => x.Id == c))
            ModelState.AddModelError("CustomerId", "Selected customer does not exist or is not assigned to you.");
        if (leadId is int l && !await Scoped(db.Leads).AnyAsync(x => x.Id == l))
            ModelState.AddModelError("LeadId", "Selected lead does not exist or is not assigned to you.");
        if (opportunityId is int o && !await Scoped(db.Opportunities).AnyAsync(x => x.Id == o))
            ModelState.AddModelError("OpportunityId", "Selected opportunity does not exist or is not assigned to you.");
    }

    protected void CheckAllowed(string field, string? value, string[] allowed)
    {
        if (value is null || !allowed.Contains(value))
            ModelState.AddModelError(field, $"Choose a valid {field.ToLower()}: {string.Join(", ", allowed)}.");
    }

    // Dropdown data for forms, limited to what the current user may reference.
    protected async Task LoadListsAsync(bool customers = false, bool leads = false, bool opportunities = false)
    {
        ViewBag.CanAssign = CanSeeAll;
        if (CanSeeAll)
            ViewBag.Users = new SelectList(await userManager.Users.OrderBy(u => u.UserName).ToListAsync(),
                nameof(IdentityUser.Id), nameof(IdentityUser.UserName));
        if (customers)
            ViewBag.Customers = new SelectList(await Scoped(db.Customers).Where(c => c.Status != CustomerStatus.Inactive)
                .OrderBy(c => c.CustomerName).Select(c => new { c.Id, Name = c.CustomerCode + " - " + c.CustomerName }).ToListAsync(), "Id", "Name");
        if (leads)
            ViewBag.Leads = new SelectList(await Scoped(db.Leads).OrderBy(l => l.LeadName)
                .Select(l => new { l.Id, Name = l.LeadCode + " - " + l.LeadName }).ToListAsync(), "Id", "Name");
        if (opportunities)
            ViewBag.Opportunities = new SelectList(await Scoped(db.Opportunities).OrderBy(o => o.OpportunityName)
                .Select(o => new { o.Id, Name = o.OpportunityName }).ToListAsync(), "Id", "Name");
    }

    // Fields set by the server are never taken from the form.
    protected void IgnoreServerFields(params string[] fields)
    {
        foreach (var f in fields.Concat(["CreatedBy", "CreatedDate", "AssignedUserId"]))
            ModelState.Remove(f);
    }
}
