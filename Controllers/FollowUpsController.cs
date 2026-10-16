using AcxiomCRM.Data;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

public class FollowUpsController(ApplicationDbContext db, UserManager<IdentityUser> userManager) : CrmController(db, userManager)
{
    private IQueryable<FollowUp> WithIncludes() => Scoped(Db.FollowUps)
        .Include(f => f.Customer).Include(f => f.Lead).Include(f => f.Opportunity).Include(f => f.AssignedUser);

    /// <param name="view">Pending (default: all Planned), Overdue, Upcoming (next 7 days), or a status, or All.</param>
    public async Task<IActionResult> Index(string? view, DateTime? from, DateTime? to, string? assignedTo, string? related)
    {
        view ??= "Pending";
        var today = DateTime.Today;
        var query = WithIncludes();
        query = view switch
        {
            "Pending" => query.Where(f => f.Status == FollowUpStatus.Planned),
            "Overdue" => query.Where(f => f.Status == FollowUpStatus.Planned && f.FollowUpDate < today),
            "Upcoming" => query.Where(f => f.Status == FollowUpStatus.Planned && f.FollowUpDate >= today && f.FollowUpDate <= today.AddDays(7)),
            "All" => query,
            _ when FollowUpStatus.All.Contains(view) => query.Where(f => f.Status == view),
            _ => query.Where(f => f.Status == FollowUpStatus.Planned)
        };
        if (from is not null) query = query.Where(f => f.FollowUpDate >= from.Value.Date);
        if (to is not null) query = query.Where(f => f.FollowUpDate <= to.Value.Date);
        if (CanSeeAll && !string.IsNullOrWhiteSpace(assignedTo)) query = query.Where(f => f.AssignedUserId == assignedTo);
        if (!string.IsNullOrWhiteSpace(related))
        {
            var term = related.Trim().ToLower();
            query = query.Where(f =>
                (f.Customer != null && f.Customer.CustomerName.ToLower().Contains(term)) ||
                (f.Lead != null && f.Lead.LeadName.ToLower().Contains(term)) ||
                (f.Opportunity != null && f.Opportunity.OpportunityName.ToLower().Contains(term)));
        }

        ViewData["View"] = view;
        ViewData["From"] = from?.ToString("yyyy-MM-dd");
        ViewData["To"] = to?.ToString("yyyy-MM-dd");
        ViewData["AssignedTo"] = assignedTo;
        ViewData["Related"] = related;
        await LoadUserFilterAsync();
        return View(await query.OrderBy(f => f.FollowUpDate).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var f = await WithIncludes().FirstOrDefaultAsync(x => x.Id == id);
        return f is null ? NotFound() : View(f);
    }

    public async Task<IActionResult> Create(int? customerId, int? leadId, int? opportunityId)
    {
        await LoadListsAsync(true, true, true);
        return View(new FollowUp { CustomerId = customerId, LeadId = leadId, OpportunityId = opportunityId });
    }

    /// <param name="historical">
    /// Explicit historical-entry flow (Admin/Manager only): records a past follow-up that already
    /// happened, so it must be Completed/Missed/Cancelled and dated today or earlier. Everyone else
    /// can only schedule new Planned follow-ups dated today or later.
    /// </param>
    [HttpPost]
    public async Task<IActionResult> Create(FollowUp input, bool historical = false)
    {
        IgnoreServerFields();
        if (historical && !CanSeeAll) return Forbid();
        if (!historical)
            input.Status = FollowUpStatus.Planned;
        else if (input.Status == FollowUpStatus.Planned)
            ModelState.AddModelError(nameof(FollowUp.Status), "A historical entry must be Completed, Missed or Cancelled.");

        input.AssignedUserId = await ResolveAssigneeAsync(input.AssignedUserId);
        CrmRules.ValidateFollowUp(input, historical, ModelState);
        await CheckRelatedAsync(input.CustomerId, input.LeadId, input.OpportunityId, input.AssignedUserId);

        if (!ModelState.IsValid)
        {
            ViewBag.Historical = historical;
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
            Remarks = input.Remarks,
            Status = input.Status,
            AssignedUserId = input.AssignedUserId,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = UserName
        };
        Db.FollowUps.Add(followUp);
        await Db.SaveChangesAsync();
        Db.Audit(UserName, historical ? "CreateHistorical" : "Create", nameof(FollowUp), followUp.Id,
            $"{followUp.FollowUpType} on {followUp.FollowUpDate:yyyy-MM-dd} ({followUp.Status})",
            newValue: AuditExtensions.Snapshot(FollowUpFields(followUp)));
        await Db.SaveChangesAsync();

        TempData["Success"] = historical ? "Historical follow-up recorded." : "Follow-up scheduled.";
        return RedirectToAction(nameof(Index));
    }

    // Edit = reschedule / change details of a Planned follow-up. Closed follow-ups are read-only.
    public async Task<IActionResult> Edit(int id)
    {
        var f = await Scoped(Db.FollowUps).FirstOrDefaultAsync(x => x.Id == id);
        if (f is null) return NotFound();
        if (f.Status != FollowUpStatus.Planned)
        {
            TempData["Error"] = $"A {f.Status} follow-up cannot be edited.";
            return RedirectToAction(nameof(Details), new { id });
        }
        await LoadListsAsync(true, true, true);
        return View(f);
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, FollowUp input)
    {
        var f = await Scoped(Db.FollowUps).FirstOrDefaultAsync(x => x.Id == id);
        if (f is null) return NotFound();
        if (f.Status != FollowUpStatus.Planned) return BadRequest();

        IgnoreServerFields();
        input.Id = id;
        input.Status = FollowUpStatus.Planned;   // status changes go through Complete / Missed / Cancel
        input.AssignedUserId = await ResolveAssigneeAsync(input.AssignedUserId, CanSeeAll ? null : f.AssignedUserId);
        CrmRules.ValidateFollowUp(input, historical: false, ModelState);
        await CheckRelatedAsync(input.CustomerId, input.LeadId, input.OpportunityId, input.AssignedUserId);
        if (!ModelState.IsValid)
        {
            await LoadListsAsync(true, true, true);
            return View(input);
        }

        var oldValue = AuditExtensions.Snapshot(FollowUpFields(f));
        var rescheduled = f.FollowUpDate.Date != input.FollowUpDate.Date;
        f.CustomerId = input.CustomerId;
        f.LeadId = input.LeadId;
        f.OpportunityId = input.OpportunityId;
        f.FollowUpDate = input.FollowUpDate.Date;
        f.FollowUpType = input.FollowUpType;
        f.Remarks = input.Remarks;
        f.AssignedUserId = input.AssignedUserId;
        Db.Audit(UserName, rescheduled ? "Reschedule" : "Update", nameof(FollowUp), f.Id, $"{f.FollowUpType} on {f.FollowUpDate:yyyy-MM-dd}",
            oldValue, AuditExtensions.Snapshot(FollowUpFields(f)));
        await Db.SaveChangesAsync();

        TempData["Success"] = rescheduled ? $"Follow-up rescheduled to {f.FollowUpDate:dd MMM yyyy}." : "Follow-up updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // Complete / Missed / Cancelled from the list or details page.
    [HttpPost]
    public async Task<IActionResult> SetStatus(int id, string status, string? returnTo)
    {
        if (status is not (FollowUpStatus.Completed or FollowUpStatus.Missed or FollowUpStatus.Cancelled))
            return BadRequest();
        var followUp = await Scoped(Db.FollowUps).FirstOrDefaultAsync(f => f.Id == id);
        if (followUp is null) return NotFound();
        if (followUp.Status != FollowUpStatus.Planned)
        {
            TempData["Error"] = $"This follow-up is already {followUp.Status}.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var old = followUp.Status;
        followUp.Status = status;
        Db.Audit(UserName, "SetStatus", nameof(FollowUp), followUp.Id, status, $"Status={old}", $"Status={status}");
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Follow-up marked {status}.";
        return returnTo == "details" ? RedirectToAction(nameof(Details), new { id }) : RedirectToAction(nameof(Index));
    }

    private static object FollowUpFields(FollowUp f) => new
    {
        f.CustomerId, f.LeadId, f.OpportunityId, FollowUpDate = f.FollowUpDate.ToString("yyyy-MM-dd"),
        f.FollowUpType, f.Remarks, f.Status, f.AssignedUserId
    };
}
