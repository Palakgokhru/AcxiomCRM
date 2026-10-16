using System.Globalization;
using System.Text;
using AcxiomCRM.Data;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

/// <summary>
/// Reports for Admin and Manager. Every query goes through the same Scoped() ownership filter as the
/// list pages. The audit report is Admin-only. Add ?format=csv to any report to download it.
/// Aggregation happens in memory because SQLite cannot SUM/ORDER BY decimal columns in SQL.
/// </summary>
[Authorize(Roles = Roles.AdminOrManager)]
public class ReportsController(ApplicationDbContext db, UserManager<IdentityUser> userManager) : CrmController(db, userManager)
{
    private static string Money(decimal v) => v.ToString("N2", CultureInfo.InvariantCulture);
    private static string Date(DateTime? d) => d?.ToString("yyyy-MM-dd") ?? "";

    public IActionResult Index() => View();

    public async Task<IActionResult> Customers(string? preset, DateTime? from, DateTime? to, string? status, string? assignedTo, string? format)
    {
        var range = DateRange.Resolve(preset, from, to);
        var q = Scoped(Db.Customers).Include(c => c.AssignedUser).AsQueryable();
        q = InRange(q, c => c.CreatedDate, range);
        if (!string.IsNullOrEmpty(status)) q = q.Where(c => c.Status == status);
        if (!string.IsNullOrEmpty(assignedTo)) q = q.Where(c => c.AssignedUserId == assignedTo);
        var list = await q.OrderBy(c => c.CustomerName).ToListAsync();

        var vm = await NewReportAsync("customers", "Customers report", "Customers created in the period.", range, assignedTo);
        vm.Filters.Insert(0, Filter("status", "Status", CustomerStatus.All, status));
        vm.Totals = [("Customers", list.Count.ToString()),
                     ("Active", list.Count(c => c.Status == CustomerStatus.Active).ToString()),
                     ("Inactive", list.Count(c => c.Status == CustomerStatus.Inactive).ToString())];
        vm.Tables.Add(new ReportTable
        {
            Title = "Customers",
            Columns = ["Code", "Name", "Company", "Email", "Phone", "City", "Status", "Assigned To", "Created"],
            Rows = list.Select(c => new[] { c.CustomerCode, c.CustomerName, c.CompanyName ?? "", c.Email, c.Phone, c.City ?? "",
                c.Status, c.AssignedUser?.UserName ?? "", Date(c.CreatedDate) }).ToList()
        });
        return Render(vm, format);
    }

    public async Task<IActionResult> Leads(string? preset, DateTime? from, DateTime? to, string? status, string? source, string? assignedTo, string? format)
    {
        var range = DateRange.Resolve(preset, from, to);
        var q = Scoped(Db.Leads).Include(l => l.AssignedUser).AsQueryable();
        q = InRange(q, l => l.CreatedDate, range);
        if (!string.IsNullOrEmpty(status)) q = q.Where(l => l.Status == status);
        if (!string.IsNullOrEmpty(source)) q = q.Where(l => l.Source == source);
        if (!string.IsNullOrEmpty(assignedTo)) q = q.Where(l => l.AssignedUserId == assignedTo);
        var list = (await q.ToListAsync()).OrderByDescending(l => l.CreatedDate).ToList();

        var vm = await NewReportAsync("leads", "Leads report", "Leads created in the period.", range, assignedTo);
        vm.Filters.InsertRange(0, [Filter("status", "Status", LeadStatus.All, status), Filter("source", "Source", LeadSources.All, source)]);
        vm.Totals = [("Leads", list.Count.ToString()), ("Expected value", Money(list.Sum(l => l.ExpectedValue)))];
        vm.Tables.Add(new ReportTable
        {
            Title = "By status",
            Columns = ["Status", "Leads", "Expected value"],
            NumericColumns = [1, 2],
            Rows = LeadStatus.All.Select(s => new[] { s, list.Count(l => l.Status == s).ToString(),
                Money(list.Where(l => l.Status == s).Sum(l => l.ExpectedValue)) }).ToList()
        });
        vm.Tables.Add(new ReportTable
        {
            Title = "Leads",
            Columns = ["Code", "Name", "Company", "Source", "Status", "Expected Value", "Assigned To", "Created"],
            NumericColumns = [5],
            Rows = list.Select(l => new[] { l.LeadCode, l.LeadName, l.CompanyName ?? "", l.Source, l.Status, Money(l.ExpectedValue),
                l.AssignedUser?.UserName ?? "", Date(l.CreatedDate) }).ToList()
        });
        return Render(vm, format);
    }

    public async Task<IActionResult> FollowUps(string? preset, DateTime? from, DateTime? to, string? status, string? assignedTo, string? format)
    {
        var range = DateRange.Resolve(preset, from, to);
        var q = Scoped(Db.FollowUps).Include(f => f.AssignedUser).Include(f => f.Customer).Include(f => f.Lead).Include(f => f.Opportunity).AsQueryable();
        q = InRange(q, f => f.FollowUpDate, range);
        if (!string.IsNullOrEmpty(status)) q = q.Where(f => f.Status == status);
        if (!string.IsNullOrEmpty(assignedTo)) q = q.Where(f => f.AssignedUserId == assignedTo);
        var list = await q.OrderBy(f => f.FollowUpDate).ToListAsync();
        var today = DateTime.Today;
        bool Overdue(FollowUp f) => f.Status == FollowUpStatus.Planned && f.FollowUpDate.Date < today;

        var vm = await NewReportAsync("followups", "Follow-ups report", "Follow-ups dated in the period.", range, assignedTo);
        vm.DateFieldLabel = "Follow-up date";
        vm.Filters.Insert(0, Filter("status", "Status", FollowUpStatus.All, status));
        vm.Totals = [("Follow-ups", list.Count.ToString()), ("Planned", list.Count(f => f.Status == FollowUpStatus.Planned).ToString()),
                     ("Overdue", list.Count(Overdue).ToString()), ("Completed", list.Count(f => f.Status == FollowUpStatus.Completed).ToString())];
        vm.Tables.Add(new ReportTable
        {
            Title = "Follow-ups",
            Columns = ["Date", "Type", "Related To", "Status", "Overdue", "Assigned To", "Remarks"],
            Rows = list.Select(f => new[] { Date(f.FollowUpDate), f.FollowUpType,
                f.Customer?.CustomerName ?? f.Lead?.LeadName ?? f.Opportunity?.OpportunityName ?? "",
                f.Status, Overdue(f) ? "Yes" : "", f.AssignedUser?.UserName ?? "", f.Remarks ?? "" }).ToList()
        });
        return Render(vm, format);
    }

    public async Task<IActionResult> Opportunities(string? preset, DateTime? from, DateTime? to, string? stage, string? status, string? assignedTo, string? format)
    {
        var range = DateRange.Resolve(preset, from, to);
        var q = Scoped(Db.Opportunities).Include(o => o.Customer).Include(o => o.AssignedUser).AsQueryable();
        q = InRange(q, o => o.CreatedDate, range);
        if (!string.IsNullOrEmpty(stage)) q = q.Where(o => o.Stage == stage);
        if (!string.IsNullOrEmpty(status)) q = q.Where(o => o.Status == status);
        if (!string.IsNullOrEmpty(assignedTo)) q = q.Where(o => o.AssignedUserId == assignedTo);
        var list = await q.OrderBy(o => o.ExpectedCloseDate).ToListAsync();

        var vm = await NewReportAsync("opportunities", "Opportunities report", "Opportunities created in the period.", range, assignedTo);
        vm.Filters.InsertRange(0, [Filter("stage", "Stage", OpportunityStage.All, stage), Filter("status", "Status", OpportunityStatus.All, status)]);
        vm.Totals = [("Opportunities", list.Count.ToString()), ("Total amount", Money(list.Sum(o => o.Amount))),
                     ("Won amount", Money(list.Where(o => o.Stage == OpportunityStage.Won).Sum(o => o.Amount)))];
        vm.Tables.Add(new ReportTable
        {
            Title = "Opportunities",
            Columns = ["Name", "Customer", "Amount", "Stage", "Probability", "Expected Close", "Status", "Assigned To"],
            NumericColumns = [2, 4],
            Rows = list.Select(o => new[] { o.OpportunityName, o.Customer?.CustomerName ?? "", Money(o.Amount), o.Stage, $"{o.Probability}%",
                Date(o.ExpectedCloseDate), o.Status, o.AssignedUser?.UserName ?? "" }).ToList()
        });
        return Render(vm, format);
    }

    public async Task<IActionResult> Pipeline(string? preset, DateTime? from, DateTime? to, string? assignedTo, string? format)
    {
        var range = DateRange.Resolve(preset, from, to);
        var q = Scoped(Db.Opportunities).Include(o => o.AssignedUser).AsQueryable();
        q = InRange(q, o => o.ExpectedCloseDate, range);
        if (!string.IsNullOrEmpty(assignedTo)) q = q.Where(o => o.AssignedUserId == assignedTo);
        var all = await q.ToListAsync();
        var open = all.Where(o => o.Status == OpportunityStatus.Open).ToList();

        var vm = await NewReportAsync("pipeline", "Pipeline report",
            "Open pipeline by stage and owner. Weighted value = amount × probability.", range, assignedTo);
        vm.DateFieldLabel = "Expected close date";
        vm.Totals = [("Open opportunities", open.Count.ToString()), ("Pipeline value", Money(open.Sum(o => o.Amount))),
                     ("Weighted value", Money(open.Sum(o => o.Amount * o.Probability / 100m)))];
        vm.Tables.Add(new ReportTable
        {
            Title = "By stage (all statuses)",
            Columns = ["Stage", "Opportunities", "Open", "Amount", "Weighted (open)"],
            NumericColumns = [1, 2, 3, 4],
            Rows = OpportunityStage.All.Select(s =>
            {
                var g = all.Where(o => o.Stage == s).ToList();
                var go = g.Where(o => o.Status == OpportunityStatus.Open).ToList();
                return new[] { s, g.Count.ToString(), go.Count.ToString(), Money(g.Sum(o => o.Amount)), Money(go.Sum(o => o.Amount * o.Probability / 100m)) };
            }).ToList()
        });
        vm.Tables.Add(new ReportTable
        {
            Title = "Open pipeline by owner",
            Columns = ["Assigned To", "Open", "Amount", "Weighted"],
            NumericColumns = [1, 2, 3],
            Rows = open.GroupBy(o => o.AssignedUser?.UserName ?? "(unassigned)").OrderBy(g => g.Key)
                .Select(g => new[] { g.Key, g.Count().ToString(), Money(g.Sum(o => o.Amount)), Money(g.Sum(o => o.Amount * o.Probability / 100m)) }).ToList()
        });
        return Render(vm, format);
    }

    public async Task<IActionResult> LeadConversion(string? preset, DateTime? from, DateTime? to, string? assignedTo, string? format)
    {
        var range = DateRange.Resolve(preset, from, to);
        var q = Scoped(Db.Leads).Include(l => l.AssignedUser).Include(l => l.ConvertedCustomer).AsQueryable();
        q = InRange(q, l => l.CreatedDate, range);
        if (!string.IsNullOrEmpty(assignedTo)) q = q.Where(l => l.AssignedUserId == assignedTo);
        var leads = await q.ToListAsync();
        static string Rate(int part, int total) => total == 0 ? "0.0%" : $"{100.0 * part / total:0.0}%";
        string[] Row(string key, List<Lead> g) =>
        [
            key, g.Count.ToString(), g.Count(l => l.Status == "Converted").ToString(),
            g.Count(l => l.Status is "Lost" or "Unqualified").ToString(), Rate(g.Count(l => l.Status == "Converted"), g.Count)
        ];

        var vm = await NewReportAsync("leadconversion", "Lead conversion report", "Leads created in the period and how many converted to customers.", range, assignedTo);
        var converted = leads.Count(l => l.Status == "Converted");
        vm.Totals = [("Leads", leads.Count.ToString()), ("Converted", converted.ToString()), ("Conversion rate", Rate(converted, leads.Count))];
        string[] cols = ["", "Leads", "Converted", "Lost / Unqualified", "Conversion rate"];
        vm.Tables.Add(new ReportTable
        {
            Title = "By source", Columns = ["Source", .. cols[1..]], NumericColumns = [1, 2, 3, 4],
            Rows = LeadSources.All.Select(s => Row(s, leads.Where(l => l.Source == s).ToList())).ToList()
        });
        vm.Tables.Add(new ReportTable
        {
            Title = "By owner", Columns = ["Assigned To", .. cols[1..]], NumericColumns = [1, 2, 3, 4],
            Rows = leads.GroupBy(l => l.AssignedUser?.UserName ?? "(unassigned)").OrderBy(g => g.Key).Select(g => Row(g.Key, g.ToList())).ToList()
        });
        vm.Tables.Add(new ReportTable
        {
            Title = "Converted leads",
            Columns = ["Lead", "Converted To", "Converted Date", "Assigned To"],
            Rows = leads.Where(l => l.Status == "Converted").OrderByDescending(l => l.ConvertedDate)
                .Select(l => new[] { $"{l.LeadCode} {l.LeadName}", l.ConvertedCustomer?.CustomerCode ?? "", Date(l.ConvertedDate), l.AssignedUser?.UserName ?? "" }).ToList()
        });
        return Render(vm, format);
    }

    public async Task<IActionResult> UserActivity(string? preset, DateTime? from, DateTime? to, string? format)
    {
        var range = DateRange.Resolve(preset ?? "month", from, to);
        var users = await Users.Users.OrderBy(u => u.UserName).ToListAsync();
        var activities = await InRange(Scoped(Db.Activities), a => a.ActivityDate, range).ToListAsync();
        var followUps = await InRange(Scoped(Db.FollowUps), f => f.FollowUpDate, range).ToListAsync();
        var created = await InRange(Db.AuditLogs.Where(a => a.Action == "Create"), a => a.CreatedDate, range)
            .GroupBy(a => a.UserId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        var logins = await InRange(Db.AuditLogs.Where(a => a.Action == "LoginSuccess"), a => a.CreatedDate, range)
            .GroupBy(a => a.UserId).Select(g => new { g.Key, Count = g.Count(), Last = g.Max(a => a.CreatedDate) }).ToListAsync();

        var vm = await NewReportAsync("useractivity", "User activity report",
            "Work recorded per user in the period: activities logged, follow-ups completed, records created and sign-ins.", range, null);
        vm.Filters.Clear();   // no assignee filter: one row per user
        vm.Tables.Add(new ReportTable
        {
            Title = "Per user",
            Columns = ["User", "Activities", "Follow-ups completed", "Follow-ups missed", "Records created", "Sign-ins", "Last sign-in (UTC)"],
            NumericColumns = [1, 2, 3, 4, 5],
            Rows = users.Select(u =>
            {
                var l = logins.FirstOrDefault(x => x.Key == u.Id);
                return new[]
                {
                    u.UserName ?? "", activities.Count(a => a.AssignedUserId == u.Id).ToString(),
                    followUps.Count(f => f.AssignedUserId == u.Id && f.Status == FollowUpStatus.Completed).ToString(),
                    followUps.Count(f => f.AssignedUserId == u.Id && f.Status == FollowUpStatus.Missed).ToString(),
                    (created.FirstOrDefault(x => x.Key == u.Id)?.Count ?? 0).ToString(),
                    (l?.Count ?? 0).ToString(), l is null ? "" : l.Last.ToString("yyyy-MM-dd HH:mm")
                };
            }).ToList()
        });
        return Render(vm, format);
    }

    // Audit events summary: Admin only (unrestricted audit access).
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Audit(string? preset, DateTime? from, DateTime? to, string? format)
    {
        var range = DateRange.Resolve(preset ?? "week", from, to);
        var logs = await InRange(Db.AuditLogs, a => a.CreatedDate, range).ToListAsync();

        var vm = await NewReportAsync("audit", "Audit events report", "Audit events in the period (UTC). Open the Audit Log for individual entries.", range, null);
        vm.Filters.Clear();
        vm.Totals = [("Events", logs.Count.ToString()), ("Failed sign-ins", logs.Count(a => a.Action is "LoginFailed" or "LoginLockedOut").ToString()),
                     ("Users", logs.Select(a => a.UserName).Distinct().Count().ToString())];
        vm.Tables.Add(new ReportTable
        {
            Title = "By entity and action", Columns = ["Entity", "Action", "Events"], NumericColumns = [2],
            Rows = logs.GroupBy(a => (a.EntityName, a.Action)).OrderBy(g => g.Key.EntityName).ThenBy(g => g.Key.Action)
                .Select(g => new[] { g.Key.EntityName, g.Key.Action, g.Count().ToString() }).ToList()
        });
        vm.Tables.Add(new ReportTable
        {
            Title = "By user", Columns = ["User", "Events", "Failed sign-ins"], NumericColumns = [1, 2],
            Rows = logs.GroupBy(a => a.UserName).OrderByDescending(g => g.Count())
                .Select(g => new[] { g.Key, g.Count().ToString(), g.Count(a => a.Action is "LoginFailed" or "LoginLockedOut").ToString() }).ToList()
        });
        return Render(vm, format);
    }

    // ---- helpers ----------------------------------------------------------------

    private static IQueryable<T> InRange<T>(IQueryable<T> q, System.Linq.Expressions.Expression<Func<T, DateTime>> field, DateRange range)
    {
        if (range.From is DateTime f)
        {
            var p = field.Parameters[0];
            var body = System.Linq.Expressions.Expression.GreaterThanOrEqual(field.Body, System.Linq.Expressions.Expression.Constant(f));
            q = q.Where(System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(body, p));
        }
        if (range.ToExclusive is DateTime t)
        {
            var p = field.Parameters[0];
            var body = System.Linq.Expressions.Expression.LessThan(field.Body, System.Linq.Expressions.Expression.Constant(t));
            q = q.Where(System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(body, p));
        }
        return q;
    }

    private static ReportFilter Filter(string name, string label, IEnumerable<string> values, string? selected) =>
        new(name, label, values.Select(v => (v, v)).ToList(), selected);

    private async Task<ReportViewModel> NewReportAsync(string key, string title, string description, DateRange range, string? assignedTo)
    {
        var users = await Users.Users.OrderBy(u => u.UserName).Select(u => new { u.Id, u.UserName }).ToListAsync();
        return new ReportViewModel
        {
            Key = key, Title = title, Description = description, Range = range,
            Filters = [new ReportFilter("assignedTo", "Assigned to", users.Select(u => (u.Id, u.UserName ?? "")).ToList(), assignedTo)]
        };
    }

    private IActionResult Render(ReportViewModel vm, string? format)
    {
        if (format != "csv") return View("Report", vm);

        static string Esc(string v) => v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
        var sb = new StringBuilder();
        sb.AppendLine(Esc(vm.Title)).AppendLine(Esc($"Period: {vm.Range.Label}"));
        foreach (var t in vm.Tables)
        {
            sb.AppendLine().AppendLine(Esc(t.Title)).AppendLine(string.Join(",", t.Columns.Select(Esc)));
            foreach (var r in t.Rows) sb.AppendLine(string.Join(",", r.Select(Esc)));
        }
        Db.Audit(UserName, "Export", "Report", vm.Key, $"CSV {vm.Range.Label}");
        Db.SaveChanges();
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(),
            "text/csv", $"{vm.Key}-{DateTime.Today:yyyyMMdd}.csv");
    }
}
