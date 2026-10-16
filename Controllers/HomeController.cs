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
        // All figures go through Scoped(), so a SalesExecutive only sees their own records.
        var opps = Scoped(Db.Opportunities);
        var leads = Scoped(Db.Leads);
        string[] closedLeadStatuses = ["Converted", "Lost", "Unqualified"];
        ViewBag.TotalCustomers = await Scoped(Db.Customers).CountAsync();
        ViewBag.TotalLeads = await leads.CountAsync();
        ViewBag.OpenLeads = await leads.CountAsync(l => !closedLeadStatuses.Contains(l.Status));
        ViewBag.TotalOpportunities = await opps.CountAsync();
        ViewBag.OpenOpportunities = await opps.CountAsync(o => o.Status == OpportunityStatus.Open);
        ViewBag.WonOpportunities = await opps.CountAsync(o => o.Stage == OpportunityStage.Won);
        ViewBag.LostOpportunities = await opps.CountAsync(o => o.Stage == OpportunityStage.Lost);
        // SQLite cannot SUM decimals in SQL, so sum the open amounts in memory.
        ViewBag.PipelineValue = (await opps.Where(o => o.Status == OpportunityStatus.Open).Select(o => o.Amount).ToListAsync()).Sum();

        // Chart data: opportunities per stage and leads per status (zero-filled, so an empty DB still renders).
        var stageCounts = await opps.GroupBy(o => o.Stage).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        ViewBag.StageLabels = OpportunityStage.All;
        ViewBag.StageCounts = OpportunityStage.All.Select(s => stageCounts.FirstOrDefault(x => x.Key == s)?.Count ?? 0).ToArray();
        var leadCounts = await leads.GroupBy(l => l.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        ViewBag.LeadStatusLabels = LeadStatus.All;
        ViewBag.LeadStatusCounts = LeadStatus.All.Select(s => leadCounts.FirstOrDefault(x => x.Key == s)?.Count ?? 0).ToArray();
        return View();
    }

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = System.Diagnostics.Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
