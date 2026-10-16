using AcxiomCRM.Data;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

public class ActivitiesController(ApplicationDbContext db, UserManager<IdentityUser> userManager) : CrmController(db, userManager)
{
    private IQueryable<Activity> WithIncludes() => Scoped(Db.Activities)
        .Include(a => a.Customer).Include(a => a.Lead).Include(a => a.Opportunity).Include(a => a.AssignedUser);

    public async Task<IActionResult> Index(string? type, string? status, DateTime? from, DateTime? to, string? assignedTo)
    {
        var query = WithIncludes();
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(a => a.ActivityType == type);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(a => a.Status == status);
        if (from is not null) query = query.Where(a => a.ActivityDate >= from.Value.Date);
        if (to is not null) query = query.Where(a => a.ActivityDate <= to.Value.Date);
        if (CanSeeAll && !string.IsNullOrWhiteSpace(assignedTo)) query = query.Where(a => a.AssignedUserId == assignedTo);

        ViewData["Type"] = type;
        ViewData["Status"] = status;
        ViewData["From"] = from?.ToString("yyyy-MM-dd");
        ViewData["To"] = to?.ToString("yyyy-MM-dd");
        ViewData["AssignedTo"] = assignedTo;
        await LoadUserFilterAsync();
        return View(await query.OrderByDescending(a => a.ActivityDate).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var a = await WithIncludes().FirstOrDefaultAsync(x => x.Id == id);
        return a is null ? NotFound() : View(a);
    }

    public async Task<IActionResult> Create(int? customerId, int? leadId, int? opportunityId)
    {
        await LoadListsAsync(true, true, true);
        return View(new Activity { CustomerId = customerId, LeadId = leadId, OpportunityId = opportunityId });
    }

    [HttpPost]
    public async Task<IActionResult> Create(Activity input)
    {
        input.AssignedUserId = await ResolveAssigneeAsync(input.AssignedUserId);
        await ValidateAsync(input);
        if (!ModelState.IsValid)
        {
            await LoadListsAsync(true, true, true);
            return View(input);
        }

        var activity = new Activity { CreatedDate = DateTime.UtcNow, CreatedBy = UserName };
        Copy(input, activity);
        Db.Activities.Add(activity);
        await Db.SaveChangesAsync();
        Db.Audit(UserName, "Create", nameof(Activity), activity.Id, $"{activity.ActivityType}: {activity.Subject}",
            newValue: AuditExtensions.Snapshot(Fields(activity)));
        await Db.SaveChangesAsync();

        TempData["Success"] = "Activity logged.";
        return RedirectToAction(nameof(Details), new { id = activity.Id });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var a = await Scoped(Db.Activities).FirstOrDefaultAsync(x => x.Id == id);
        if (a is null) return NotFound();
        await LoadListsAsync(true, true, true);
        return View(a);
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, Activity input)
    {
        var a = await Scoped(Db.Activities).FirstOrDefaultAsync(x => x.Id == id);
        if (a is null) return NotFound();

        input.Id = id;
        input.AssignedUserId = await ResolveAssigneeAsync(input.AssignedUserId, CanSeeAll ? null : a.AssignedUserId);
        await ValidateAsync(input);
        if (!ModelState.IsValid)
        {
            await LoadListsAsync(true, true, true);
            return View(input);
        }

        var oldValue = AuditExtensions.Snapshot(Fields(a));
        Copy(input, a);
        Db.Audit(UserName, "Update", nameof(Activity), a.Id, $"{a.ActivityType}: {a.Subject}", oldValue, AuditExtensions.Snapshot(Fields(a)));
        await Db.SaveChangesAsync();
        TempData["Success"] = "Activity updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Complete(int id)
    {
        var activity = await Scoped(Db.Activities).FirstOrDefaultAsync(a => a.Id == id);
        if (activity is null) return NotFound();
        var old = activity.Status;
        activity.Status = "Completed";
        Db.Audit(UserName, "Complete", nameof(Activity), activity.Id, activity.Subject, $"Status={old}", "Status=Completed");
        await Db.SaveChangesAsync();
        TempData["Success"] = "Activity completed.";
        return RedirectToAction(nameof(Index));
    }

    // Activities are notes of work done; anyone who may see one may remove it (audited).
    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var activity = await Scoped(Db.Activities).FirstOrDefaultAsync(a => a.Id == id);
        if (activity is null) return NotFound();
        Db.Activities.Remove(activity);
        Db.Audit(UserName, "Delete", nameof(Activity), activity.Id, activity.Subject, AuditExtensions.Snapshot(Fields(activity)));
        await Db.SaveChangesAsync();
        TempData["Success"] = "Activity deleted.";
        return RedirectToAction(nameof(Index));
    }

    private async Task ValidateAsync(Activity input)
    {
        IgnoreServerFields();
        input.Subject = input.Subject?.Trim() ?? string.Empty;
        input.Description = CrmRules.Clean(input.Description);
        CheckAllowed("ActivityType", input.ActivityType, ActivityTypes.All);
        CheckAllowed(nameof(Activity.Status), input.Status, ActivityStatus.All);
        if (input.CustomerId is null && input.LeadId is null && input.OpportunityId is null)
            ModelState.AddModelError(string.Empty, "Link the activity to a customer, lead or opportunity.");
        await CheckRelatedAsync(input.CustomerId, input.LeadId, input.OpportunityId, input.AssignedUserId);
    }

    private static void Copy(Activity from, Activity to)
    {
        to.ActivityType = from.ActivityType;
        to.Subject = from.Subject;
        to.Description = from.Description;
        to.ActivityDate = from.ActivityDate.Date;
        to.CustomerId = from.CustomerId;
        to.LeadId = from.LeadId;
        to.OpportunityId = from.OpportunityId;
        to.Status = from.Status;
        to.AssignedUserId = from.AssignedUserId;
    }

    private static object Fields(Activity a) => new
    {
        a.ActivityType, a.Subject, a.Description, ActivityDate = a.ActivityDate.ToString("yyyy-MM-dd"),
        a.CustomerId, a.LeadId, a.OpportunityId, a.Status, a.AssignedUserId
    };
}
