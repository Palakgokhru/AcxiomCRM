using AcxiomCRM.Data;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

public class HomeController(ApplicationDbContext db, UserManager<IdentityUser> userManager) : CrmController(db, userManager)
{
    // Dashboard: pending (Planned) follow-ups for the next 14 days plus overdue ones, scoped to the user.
    public async Task<IActionResult> Index()
    {
        var until = DateTime.Today.AddDays(14);
        ViewBag.PendingFollowUps = await Scoped(Db.FollowUps)
            .Include(f => f.Customer).Include(f => f.Lead).Include(f => f.Opportunity)
            .Where(f => f.Status == FollowUpStatus.Planned && f.FollowUpDate <= until)
            .OrderBy(f => f.FollowUpDate).Take(20).ToListAsync();
        ViewBag.OpenOpportunities = await Scoped(Db.Opportunities).CountAsync(o => o.Status == OpportunityStatus.Open);
        ViewBag.LeadCount = await Scoped(Db.Leads).CountAsync(l => l.Status != "Converted" && l.Status != "Lost");
        ViewBag.CustomerCount = await Scoped(Db.Customers).CountAsync(c => c.Status != CustomerStatus.Inactive);
        return View();
    }

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = System.Diagnostics.Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
