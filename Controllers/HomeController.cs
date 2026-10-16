using AcxiomCRM.Data;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

public class DashboardViewModel
{
    public DateRange Range { get; set; } = new("all", null, null);
    public int TotalCustomers { get; set; }
    public int TotalLeads { get; set; }
    public int OpenLeads { get; set; }
    public int TotalOpportunities { get; set; }
    public int OpenOpportunities { get; set; }
    public int WonOpportunities { get; set; }
    public int LostOpportunities { get; set; }
    public decimal PipelineValue { get; set; }
    public int[] LeadStatusCounts { get; set; } = [];
    public int[] StageCounts { get; set; } = [];
    public string[] MonthLabels { get; set; } = [];
    public decimal[] MonthlyWonAmount { get; set; } = [];
    public int[] MonthlyWonCount { get; set; } = [];
    public int[] MonthlyLostCount { get; set; } = [];
    public int[] MonthlyNewLeads { get; set; } = [];
    public List<FollowUp> PendingFollowUps { get; set; } = [];
    // Admin only
    public Dictionary<string, int>? AdminStats { get; set; }
}

public class HomeController(ApplicationDbContext db, UserManager<IdentityUser> userManager) : CrmController(db, userManager)
{
    // Lead statuses counted as "open" (not closed, lost or converted).
    private static readonly string[] OpenLeadStatuses = ["New", "Contacted", "Qualified"];

    /// <summary>
    /// Dashboard. Every figure is computed in the database through Scoped(), the same ownership filter
    /// as the list pages, so a SalesExecutive's cards and charts only ever count their own records.
    /// The optional period filters records by created date.
    /// </summary>
    public async Task<IActionResult> Index(string? preset, DateTime? from, DateTime? to)
    {
        var range = DateRange.Resolve(preset, from, to);
        var customers = Scoped(Db.Customers);
        var leads = Scoped(Db.Leads);
        var opps = Scoped(Db.Opportunities);
        if (range.From is DateTime f)
        {
            customers = customers.Where(c => c.CreatedDate >= f);
            leads = leads.Where(l => l.CreatedDate >= f);
            opps = opps.Where(o => o.CreatedDate >= f);
        }
        if (range.ToExclusive is DateTime t)
        {
            customers = customers.Where(c => c.CreatedDate < t);
            leads = leads.Where(l => l.CreatedDate < t);
            opps = opps.Where(o => o.CreatedDate < t);
        }

        var vm = new DashboardViewModel
        {
            Range = range,
            TotalCustomers = await customers.CountAsync(),
            TotalLeads = await leads.CountAsync(),
            OpenLeads = await leads.CountAsync(l => OpenLeadStatuses.Contains(l.Status)),
            TotalOpportunities = await opps.CountAsync(),
            OpenOpportunities = await opps.CountAsync(o => o.Status == OpportunityStatus.Open),
            WonOpportunities = await opps.CountAsync(o => o.Stage == OpportunityStage.Won),
            LostOpportunities = await opps.CountAsync(o => o.Stage == OpportunityStage.Lost),
            // SQLite cannot SUM decimals in SQL: fetch only the open amounts and add them up here.
            PipelineValue = (await opps.Where(o => o.Status == OpportunityStatus.Open).Select(o => o.Amount).ToListAsync()).Sum(),
        };

        var leadCounts = await leads.GroupBy(l => l.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        vm.LeadStatusCounts = LeadStatus.All.Select(s => leadCounts.FirstOrDefault(x => x.Key == s)?.Count ?? 0).ToArray();
        var stageCounts = await opps.GroupBy(o => o.Stage).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        vm.StageCounts = OpportunityStage.All.Select(s => stageCounts.FirstOrDefault(x => x.Key == s)?.Count ?? 0).ToArray();

        await LoadMonthlyAsync(vm);

        var until = DateTime.Today.AddDays(14);
        vm.PendingFollowUps = await Scoped(Db.FollowUps)
            .Include(x => x.Customer).Include(x => x.Lead).Include(x => x.Opportunity)
            .Where(x => x.Status == FollowUpStatus.Planned && x.FollowUpDate <= until)
            .OrderBy(x => x.FollowUpDate).Take(20).ToListAsync();

        if (User.IsInRole(Roles.Admin))
        {
            var since = DateTime.UtcNow.AddHours(-24);
            var now = DateTimeOffset.UtcNow;
            vm.AdminStats = new()
            {
                ["Users"] = await Users.Users.CountAsync(),
                ["Locked accounts"] = (await Users.Users.Where(u => u.LockoutEnd != null).Select(u => u.LockoutEnd).ToListAsync()).Count(e => e > now),
                ["Failed sign-ins (24h)"] = await Db.AuditLogs.CountAsync(a => a.CreatedDate >= since && (a.Action == "LoginFailed" || a.Action == "LoginLockedOut")),
                ["Audit events (24h)"] = await Db.AuditLogs.CountAsync(a => a.CreatedDate >= since),
            };
        }
        return View(vm);
    }

    // Monthly outcomes for the configured reporting period (Crm:DashboardMonths, default 6), always
    // ending with the current month and independent of the card period filter.
    private async Task LoadMonthlyAsync(DashboardViewModel vm)
    {
        var months = Math.Clamp(HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetValue<int?>("Crm:DashboardMonths") ?? 6, 1, 24);
        var first = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-(months - 1));
        var end = first.AddMonths(months);

        var closed = await Scoped(Db.Opportunities)
            .Where(o => (o.Stage == OpportunityStage.Won || o.Stage == OpportunityStage.Lost) && o.ExpectedCloseDate >= first && o.ExpectedCloseDate < end)
            .Select(o => new { o.Stage, o.Amount, o.ExpectedCloseDate }).ToListAsync();
        var newLeads = await Scoped(Db.Leads).Where(l => l.CreatedDate >= first && l.CreatedDate < end).Select(l => l.CreatedDate).ToListAsync();

        var monthStarts = Enumerable.Range(0, months).Select(i => first.AddMonths(i)).ToList();
        bool InMonth(DateTime d, DateTime m) => d.Year == m.Year && d.Month == m.Month;
        vm.MonthLabels = monthStarts.Select(m => m.ToString("MMM yyyy")).ToArray();
        vm.MonthlyWonAmount = monthStarts.Select(m => closed.Where(o => o.Stage == OpportunityStage.Won && InMonth(o.ExpectedCloseDate, m)).Sum(o => o.Amount)).ToArray();
        vm.MonthlyWonCount = monthStarts.Select(m => closed.Count(o => o.Stage == OpportunityStage.Won && InMonth(o.ExpectedCloseDate, m))).ToArray();
        vm.MonthlyLostCount = monthStarts.Select(m => closed.Count(o => o.Stage == OpportunityStage.Lost && InMonth(o.ExpectedCloseDate, m))).ToArray();
        vm.MonthlyNewLeads = monthStarts.Select(m => newLeads.Count(d => InMonth(d, m))).ToArray();
    }

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = System.Diagnostics.Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
