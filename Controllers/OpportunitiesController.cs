using AcxiomCRM.Data;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

public class OpportunitiesController(ApplicationDbContext db, UserManager<IdentityUser> userManager) : CrmController(db, userManager)
{
    private IQueryable<Opportunity> WithIncludes() =>
        Scoped(Db.Opportunities).Include(o => o.Customer).Include(o => o.Lead).Include(o => o.AssignedUser);

    public async Task<IActionResult> Index(string? search, string? stage, string? status, string? assignedTo)
    {
        var query = WithIncludes();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(o => o.OpportunityName.ToLower().Contains(term)
                || (o.Customer != null && (o.Customer.CustomerName.ToLower().Contains(term) || o.Customer.CustomerCode.ToLower().Contains(term))));
        }
        if (!string.IsNullOrWhiteSpace(stage)) query = query.Where(o => o.Stage == stage);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(o => o.Status == status);
        if (CanSeeAll && !string.IsNullOrWhiteSpace(assignedTo)) query = query.Where(o => o.AssignedUserId == assignedTo);

        ViewData["Search"] = search;
        ViewData["Stage"] = stage;
        ViewData["Status"] = status;
        ViewData["AssignedTo"] = assignedTo;
        await LoadUserFilterAsync();
        return View(await query.OrderBy(o => o.ExpectedCloseDate).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var opp = await WithIncludes().FirstOrDefaultAsync(o => o.Id == id);
        if (opp is null) return NotFound();
        ViewBag.FollowUps = await Scoped(Db.FollowUps).Where(f => f.OpportunityId == id).OrderByDescending(f => f.FollowUpDate).ToListAsync();
        ViewBag.Activities = await Scoped(Db.Activities).Where(a => a.OpportunityId == id).OrderByDescending(a => a.ActivityDate).ToListAsync();
        return View(opp);
    }

    public async Task<IActionResult> Create(int? customerId, int? leadId)
    {
        await LoadListsAsync(customers: true, leads: true);
        return View(new Opportunity { CustomerId = customerId, LeadId = leadId });
    }

    [HttpPost]
    public async Task<IActionResult> Create(Opportunity input)
    {
        input.AssignedUserId = await ResolveAssigneeAsync(input.AssignedUserId);
        await ValidateAsync(input);
        if (!ModelState.IsValid)
        {
            await LoadListsAsync(customers: true, leads: true, keepCustomerId: input.CustomerId);
            return View(input);
        }

        var opp = new Opportunity { CreatedDate = DateTime.UtcNow, CreatedBy = UserName };
        Copy(input, opp);
        Db.Opportunities.Add(opp);
        await Db.SaveChangesAsync();
        Db.Audit(UserName, "Create", nameof(Opportunity), opp.Id, $"{opp.OpportunityName} {opp.Amount:0.00} {opp.Stage}",
            newValue: AuditExtensions.Snapshot(opp.AuditFields()));
        await Db.SaveChangesAsync();

        TempData["Success"] = "Opportunity created.";
        return RedirectToAction(nameof(Details), new { id = opp.Id });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var opp = await Scoped(Db.Opportunities).FirstOrDefaultAsync(o => o.Id == id);
        if (opp is null) return NotFound();
        await LoadListsAsync(customers: true, leads: true, keepCustomerId: opp.CustomerId);
        return View(opp);
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, Opportunity input)
    {
        var opp = await Scoped(Db.Opportunities).FirstOrDefaultAsync(o => o.Id == id);
        if (opp is null) return NotFound();

        input.Id = id;
        input.AssignedUserId = await ResolveAssigneeAsync(input.AssignedUserId, CanSeeAll ? null : opp.AssignedUserId);
        await ValidateAsync(input);
        if (!ModelState.IsValid)
        {
            await LoadListsAsync(customers: true, leads: true, keepCustomerId: opp.CustomerId);
            return View(input);
        }

        var oldValue = AuditExtensions.Snapshot(opp.AuditFields());
        Copy(input, opp);
        Db.Audit(UserName, "Update", nameof(Opportunity), opp.Id, $"{opp.OpportunityName} {opp.Stage}/{opp.Status}",
            oldValue, AuditExtensions.Snapshot(opp.AuditFields()));
        await Db.SaveChangesAsync();
        TempData["Success"] = "Opportunity updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // Safe deactivation: close the opportunity as Lost (keeps history, follow-ups and reporting intact).
    [HttpPost]
    public async Task<IActionResult> Deactivate(int id)
    {
        var opp = await Scoped(Db.Opportunities).FirstOrDefaultAsync(o => o.Id == id);
        if (opp is null) return NotFound();
        if (opp.Status == OpportunityStatus.Closed)
        {
            TempData["Error"] = "This opportunity is already closed.";
            return RedirectToAction(nameof(Details), new { id });
        }
        var oldValue = AuditExtensions.Snapshot(opp.AuditFields());
        opp.Stage = OpportunityStage.Lost;
        opp.Status = OpportunityStatus.Closed;
        Db.Audit(UserName, "Deactivate", nameof(Opportunity), opp.Id, $"{opp.OpportunityName} closed as Lost", oldValue, AuditExtensions.Snapshot(opp.AuditFields()));
        await Db.SaveChangesAsync();
        TempData["Success"] = "Opportunity closed as Lost.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // Permanent delete: Admin only. Linked follow-ups/activities keep existing with the link cleared.
    [HttpPost, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Delete(int id)
    {
        var opp = await Db.Opportunities.FindAsync(id);
        if (opp is null) return NotFound();
        Db.Opportunities.Remove(opp);
        Db.Audit(UserName, "Delete", nameof(Opportunity), opp.Id, opp.OpportunityName, AuditExtensions.Snapshot(opp.AuditFields()));
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Opportunity \"{opp.OpportunityName}\" deleted.";
        return RedirectToAction(nameof(Index));
    }

    // Section 17.7 rules (shared with the API via CrmRules) + reference/ownership checks.
    private async Task ValidateAsync(Opportunity o)
    {
        IgnoreServerFields();
        CrmRules.ValidateOpportunity(o, ModelState);
        await CheckRelatedAsync(o.CustomerId, o.LeadId, null, o.AssignedUserId);
    }

    private static void Copy(Opportunity from, Opportunity to)
    {
        to.OpportunityName = from.OpportunityName;
        to.CustomerId = from.CustomerId;
        to.LeadId = from.LeadId;
        to.Amount = from.Amount;
        to.Stage = from.Stage;
        to.Probability = from.Probability;
        to.ExpectedCloseDate = from.ExpectedCloseDate.Date;
        to.Status = from.Status;
        to.AssignedUserId = from.AssignedUserId;
    }
}
