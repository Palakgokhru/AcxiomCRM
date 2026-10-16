using AcxiomCRM.Data;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

public class ActivitiesController(ApplicationDbContext db, UserManager<IdentityUser> userManager) : CrmController(db, userManager)
{
    public async Task<IActionResult> Index(string? type)
    {
        var query = Scoped(Db.Activities)
            .Include(a => a.Customer).Include(a => a.Lead).Include(a => a.Opportunity).Include(a => a.AssignedUser)
            .AsQueryable();
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(a => a.ActivityType == type);
        ViewData["Type"] = type;
        return View(await query.OrderByDescending(a => a.ActivityDate).ToListAsync());
    }

    public async Task<IActionResult> Create()
    {
        await LoadListsAsync(true, true, true);
        return View(new Activity());
    }

    [HttpPost]
    public async Task<IActionResult> Create(Activity input)
    {
        IgnoreServerFields();
        input.AssignedUserId = await ResolveAssigneeAsync(input.AssignedUserId);
        CheckAllowed("ActivityType", input.ActivityType, ActivityTypes.All);
        CheckAllowed(nameof(Activity.Status), input.Status, ActivityStatus.All);
        if (input.CustomerId is null && input.LeadId is null && input.OpportunityId is null)
            ModelState.AddModelError(string.Empty, "Link the activity to a customer, lead or opportunity.");
        await CheckRelatedAsync(input.CustomerId, input.LeadId, input.OpportunityId);

        if (!ModelState.IsValid)
        {
            await LoadListsAsync(true, true, true);
            return View(input);
        }

        var activity = new Activity
        {
            ActivityType = input.ActivityType,
            Subject = input.Subject.Trim(),
            ActivityDate = input.ActivityDate.Date,
            CustomerId = input.CustomerId,
            LeadId = input.LeadId,
            OpportunityId = input.OpportunityId,
            Status = input.Status,
            AssignedUserId = input.AssignedUserId,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = UserName
        };
        Db.Activities.Add(activity);
        await Db.SaveChangesAsync();
        Db.Audit(UserName, "Create", nameof(Activity), activity.Id, $"{activity.ActivityType}: {activity.Subject}");
        await Db.SaveChangesAsync();

        TempData["Success"] = "Activity logged.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Complete(int id)
    {
        var activity = await Scoped(Db.Activities).FirstOrDefaultAsync(a => a.Id == id);
        if (activity is null) return NotFound();
        activity.Status = "Completed";
        Db.Audit(UserName, "Complete", nameof(Activity), activity.Id, activity.Subject);
        await Db.SaveChangesAsync();
        TempData["Success"] = "Activity completed.";
        return RedirectToAction(nameof(Index));
    }
}
