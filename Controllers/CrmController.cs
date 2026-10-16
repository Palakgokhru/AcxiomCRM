using AcxiomCRM.Data;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

/// <summary>
/// Shared ownership rules for the CRM modules.
/// Admin and Manager see all team records (there is a single sales team); a SalesExecutive only
/// sees records assigned to them. Every record lookup goes through <see cref="Scoped{T}"/>, so
/// changing an id in the URL or a crafted POST returns 404 / a validation error, never another
/// user's data.
/// </summary>
[Authorize]
public abstract class CrmController(ApplicationDbContext db, UserManager<IdentityUser> userManager) : Controller
{
    protected ApplicationDbContext Db => db;
    protected UserManager<IdentityUser> Users => userManager;
    protected string UserName => User.Identity?.Name ?? "(unknown)";

    // Ownership rules live in CrmScope so the REST API applies exactly the same checks.
    private CrmScope? scope;
    protected CrmScope Scope => scope ??= new CrmScope(db, userManager, User);
    protected string CurrentUserId => Scope.CurrentUserId;
    protected bool CanSeeAll => Scope.CanSeeAll;

    protected decimal MaxLeadValue =>
        HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetValue<decimal?>("Crm:MaxLeadExpectedValue")
        ?? CrmRules.DefaultMaxLeadValue;

    protected IQueryable<T> Scoped<T>(IQueryable<T> query) where T : class, IAssignable => Scope.Scoped(query);

    protected Task<string?> ResolveAssigneeAsync(string? requested, string? existing = null) =>
        Scope.ResolveAssigneeAsync(requested, existing, ModelState);

    protected Task CheckRelatedAsync(int? customerId, int? leadId, int? opportunityId, string? assigneeId) =>
        Scope.CheckRelatedAsync(customerId, leadId, opportunityId, assigneeId, ModelState);

    protected void CheckAllowed(string field, string? value, string[] allowed)
    {
        if (value is null || !allowed.Contains(value))
            ModelState.AddModelError(field, $"Choose a valid {field.ToLower()}: {string.Join(", ", allowed)}.");
    }

    /// <summary>
    /// Dropdown data for forms, limited to what the current user may reference. Inactive customers
    /// are hidden, except the one already linked (so editing doesn't silently change it).
    /// </summary>
    protected async Task LoadListsAsync(bool customers = false, bool leads = false, bool opportunities = false, int? keepCustomerId = null)
    {
        ViewBag.CanAssign = CanSeeAll;
        if (CanSeeAll)
            ViewBag.Users = new SelectList(await userManager.Users.OrderBy(u => u.UserName).ToListAsync(),
                nameof(IdentityUser.Id), nameof(IdentityUser.UserName));
        if (customers)
            ViewBag.Customers = new SelectList(await Scoped(db.Customers)
                .Where(c => c.Status != CustomerStatus.Inactive || c.Id == keepCustomerId)
                .OrderBy(c => c.CustomerName).Select(c => new { c.Id, Name = c.CustomerCode + " - " + c.CustomerName }).ToListAsync(), "Id", "Name");
        if (leads)
            ViewBag.Leads = new SelectList(await Scoped(db.Leads).OrderBy(l => l.LeadName)
                .Select(l => new { l.Id, Name = l.LeadCode + " - " + l.LeadName }).ToListAsync(), "Id", "Name");
        if (opportunities)
            ViewBag.Opportunities = new SelectList(await Scoped(db.Opportunities).OrderBy(o => o.OpportunityName)
                .Select(o => new { o.Id, Name = o.OpportunityName }).ToListAsync(), "Id", "Name");
    }

    // Users list for "assigned to" filters on list pages (Admin/Manager only).
    protected async Task LoadUserFilterAsync()
    {
        ViewBag.CanAssign = CanSeeAll;
        if (CanSeeAll)
            ViewBag.FilterUsers = await userManager.Users.OrderBy(u => u.UserName)
                .Select(u => new SelectListItem(u.UserName, u.Id)).ToListAsync();
    }

    // Fields set by the server are never taken from the form.
    protected void IgnoreServerFields(params string[] fields)
    {
        foreach (var f in fields.Concat(["CreatedBy", "CreatedDate", "AssignedUserId"]))
            ModelState.Remove(f);
    }

    // A temporary unique code used until the database assigns the Id the real code is built from.
    protected static string TempCode() => "TMP-" + Guid.NewGuid().ToString("N")[..16];
}
