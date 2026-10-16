using System.Security.Claims;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Data;

/// <summary>
/// Ownership scope for the current user, shared by the MVC controllers and the REST API.
/// Admin and Manager see all team records (single sales team); a SalesExecutive only sees records
/// whose AssignedUserId is theirs. Filters are applied in the database query, never after loading.
/// </summary>
public class CrmScope(ApplicationDbContext db, UserManager<IdentityUser> userManager, ClaimsPrincipal user)
{
    public string CurrentUserId => user.FindFirstValue(ClaimTypes.NameIdentifier)!;
    public bool CanSeeAll => user.IsInRole(Roles.Admin) || user.IsInRole(Roles.Manager);

    public IQueryable<T> Scoped<T>(IQueryable<T> query) where T : class, IAssignable
    {
        if (CanSeeAll) return query;
        var userId = CurrentUserId;
        return query.Where(x => x.AssignedUserId == userId);
    }

    // SalesExecutives always own what they create/edit; Admin/Manager may assign anyone (or nobody).
    public async Task<string?> ResolveAssigneeAsync(string? requested, string? existing, ModelStateDictionary ms)
    {
        if (!CanSeeAll) return existing ?? CurrentUserId;
        if (string.IsNullOrEmpty(requested)) return null;
        if (await userManager.FindByIdAsync(requested) is null)
        {
            ms.AddModelError("AssignedUserId", "Selected user does not exist.");
            return null;
        }
        return requested;
    }

    /// <summary>
    /// Each referenced customer/lead/opportunity must exist AND be visible to the current user. When the
    /// record is assigned to a SalesExecutive, linked records must also belong to that SalesExecutive.
    /// </summary>
    public async Task CheckRelatedAsync(int? customerId, int? leadId, int? opportunityId, string? assigneeId, ModelStateDictionary ms)
    {
        string? salesOwner = null;
        if (assigneeId is not null)
        {
            var assignee = await userManager.FindByIdAsync(assigneeId);
            if (assignee is not null)
            {
                var roles = await userManager.GetRolesAsync(assignee);
                if (roles.Contains(Roles.SalesExecutive) && !roles.Contains(Roles.Admin) && !roles.Contains(Roles.Manager))
                    salesOwner = assigneeId;
            }
        }

        async Task Check<T>(IQueryable<T> set, int? id, string field, string label) where T : class, IAssignable
        {
            if (id is not int value) return;
            var owner = await Scoped(set).Where(x => EF.Property<int>(x, "Id") == value)
                .Select(x => new { x.AssignedUserId }).FirstOrDefaultAsync();
            if (owner is null)
                ms.AddModelError(field, $"Selected {label} does not exist or is not assigned to you.");
            else if (salesOwner is not null && owner.AssignedUserId != salesOwner)
                ms.AddModelError(field, $"The selected {label} is not assigned to the chosen sales executive.");
        }

        await Check(db.Customers, customerId, "CustomerId", "customer");
        await Check(db.Leads, leadId, "LeadId", "lead");
        await Check(db.Opportunities, opportunityId, "OpportunityId", "opportunity");
    }
}
