using AcxiomCRM.Data;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

public class FollowUpsController(ApplicationDbContext db, UserManager<IdentityUser> userManager) : CrmController(db, userManager)
{
    // Default view: pending (Planned) follow-ups, soonest first. ?status=All shows everything.
    public async Task<IActionResult> Index(string? status)
    {
        status ??= FollowUpStatus.Planned;
        var query = Scoped(Db.FollowUps)
            .Include(f => f.Customer).Include(f => f.Lead).Include(f => f.Opportunity).Include(f => f.AssignedUser)
            .AsQueryable();
        if (status != "All") query = query.Where(f => f.Status == status);
        ViewData["Status"] = status;
        return View(await query.OrderBy(f => f.FollowUpDate).ToListAsync());
    }

    public async Task<IActionResult> Create(int? customerId, int? leadId, int? opportunityId)
    {
        await LoadListsAsync(true, true, true);
        return View(new FollowUp { CustomerId = customerId, LeadId = leadId, OpportunityId = opportunityId });
    }

    [HttpPost]
    public async Task<IActionResult> Create(FollowUp input)
    {
        IgnoreServerFields();
        input.AssignedUserId = await ResolveAssigneeAsync(input.AssignedUserId);

        CheckAllowed(nameof(FollowUp.FollowUpType), input.FollowUpType, FollowUpTypes.All);
        CheckAllowed(nameof(FollowUp.Status), input.Status, FollowUpStatus.All);
        // Business rule: a new / planned follow-up cannot be scheduled in the past.
        if (input.Status == FollowUpStatus.Planned && input.FollowUpDate.Date < DateTime.Today)
            ModelState.AddModelError(nameof(FollowUp.FollowUpDate), "A planned follow-up cannot be scheduled before today.");
        if (input.CustomerId is null && input.LeadId is null && input.OpportunityId is null)
            ModelState.AddModelError(string.Empty, "Link the follow-up to a customer, lead or opportunity.");
        await CheckRelatedAsync(input.CustomerId, input.LeadId, input.OpportunityId);

        if (!ModelState.IsValid)
        {
            await LoadListsAsync(true, true, true);
            return View(input);
        }

        var followUp = new FollowUp
        {
            CustomerId = input.CustomerId,
            LeadId = input.LeadId,
            OpportunityId = input.OpportunityId,
            FollowUpDate = input.FollowUpDate.Date,
            FollowUpType = input.FollowUpType,
            Remarks = input.Remarks?.Trim(),
            Status = input.Status,
            AssignedUserId = input.AssignedUserId,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = UserName
        };
        Db.FollowUps.Add(followUp);
        await Db.SaveChangesAsync();
        Db.Audit(UserName, "Create", nameof(FollowUp), followUp.Id, $"{followUp.FollowUpType} on {followUp.FollowUpDate:yyyy-MM-dd}");
        await Db.SaveChangesAsync();

        TempData["Success"] = "Follow-up scheduled.";
        return RedirectToAction(nameof(Index));
    }

    // Complete / Missed / Cancelled from the list.
    [HttpPost]
    public async Task<IActionResult> SetStatus(int id, string status)
    {
        if (status is not (FollowUpStatus.Completed or FollowUpStatus.Missed or FollowUpStatus.Cancelled))
            return BadRequest();
        var followUp = await Scoped(Db.FollowUps).FirstOrDefaultAsync(f => f.Id == id);
        if (followUp is null) return NotFound();

        followUp.Status = status;
        Db.Audit(UserName, "SetStatus", nameof(FollowUp), followUp.Id, status);
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Follow-up marked {status}.";
        return RedirectToAction(nameof(Index));
    }
}
