using AcxiomCRM.Data;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

public class OpportunitiesController(ApplicationDbContext db, UserManager<IdentityUser> userManager) : CrmController(db, userManager)
{
    private IQueryable<Opportunity> WithIncludes() =>
        Scoped(Db.Opportunities).Include(o => o.Customer).Include(o => o.Lead).Include(o => o.AssignedUser);

    public async Task<IActionResult> Index(string? stage, string? status)
    {
        var query = WithIncludes();
        if (!string.IsNullOrWhiteSpace(stage)) query = query.Where(o => o.Stage == stage);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(o => o.Status == status);
        ViewData["Stage"] = stage;
        ViewData["Status"] = status;
        return View(await query.OrderBy(o => o.ExpectedCloseDate).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var opp = await WithIncludes().FirstOrDefaultAsync(o => o.Id == id);
        return opp is null ? NotFound() : View(opp);
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
            await LoadListsAsync(customers: true, leads: true);
            return View(input);
        }

        var opp = new Opportunity { CreatedDate = DateTime.UtcNow, CreatedBy = UserName };
        Copy(input, opp);
        Db.Opportunities.Add(opp);
        await Db.SaveChangesAsync();
        Db.Audit(UserName, "Create", nameof(Opportunity), opp.Id, $"{opp.OpportunityName} {opp.Amount:0.00} {opp.Stage}", newValue: AuditExtensions.Snapshot(opp.AuditFields()));
        await Db.SaveChangesAsync();

        TempData["Success"] = "Opportunity created.";
        return RedirectToAction(nameof(Details), new { id = opp.Id });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var opp = await Scoped(Db.Opportunities).FirstOrDefaultAsync(o => o.Id == id);
        if (opp is null) return NotFound();
        await LoadListsAsync(customers: true, leads: true);
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
            await LoadListsAsync(customers: true, leads: true);
            return View(input);
        }

        var oldValue = AuditExtensions.Snapshot(opp.AuditFields());
        Copy(input, opp);
        Db.Audit(UserName, "Update", nameof(Opportunity), opp.Id, $"{opp.OpportunityName} {opp.Stage}/{opp.Status}", oldValue, AuditExtensions.Snapshot(opp.AuditFields()));
        await Db.SaveChangesAsync();
        TempData["Success"] = "Opportunity updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // Section 17.7 business rules -- enforced here on every POST regardless of client-side checks.
    private async Task ValidateAsync(Opportunity o)
    {
        IgnoreServerFields();
        o.OpportunityName = o.OpportunityName?.Trim() ?? string.Empty;

        CheckAllowed(nameof(Opportunity.Stage), o.Stage, OpportunityStage.All);
        CheckAllowed(nameof(Opportunity.Status), o.Status, OpportunityStatus.All);

        if (o.Amount < 0)
            ModelState.AddModelError(nameof(Opportunity.Amount), "Amount cannot be negative.");
        if (o.Probability < 0 || o.Probability > 100)
            ModelState.AddModelError(nameof(Opportunity.Probability), "Probability must be between 0 and 100.");

        if (o.Status == OpportunityStatus.Open)
        {
            if (o.Amount <= 0)
                ModelState.AddModelError(nameof(Opportunity.Amount), "An active (Open) opportunity must have an amount greater than 0.");
            if (o.ExpectedCloseDate.Date < DateTime.Today)
                ModelState.AddModelError(nameof(Opportunity.ExpectedCloseDate), "An active (Open) opportunity cannot have an expected close date in the past.");
            if (o.Stage is OpportunityStage.Won or OpportunityStage.Lost)
                ModelState.AddModelError(nameof(Opportunity.Status), "A Won or Lost opportunity must have status Closed.");
        }

        if (o.CustomerId is null)
            ModelState.AddModelError(nameof(Opportunity.CustomerId), "Select a customer.");
        await CheckRelatedAsync(o.CustomerId, o.LeadId, null);
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
