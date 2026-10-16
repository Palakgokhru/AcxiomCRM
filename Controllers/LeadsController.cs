using AcxiomCRM.Data;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

public class LeadsController(ApplicationDbContext db, UserManager<IdentityUser> userManager) : CrmController(db, userManager)
{
    public async Task<IActionResult> Index(string? search, string? status, string? assignedTo)
    {
        var query = Scoped(Db.Leads).Include(l => l.AssignedUser).AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(l => l.LeadName.ToLower().Contains(term) || l.LeadCode.ToLower().Contains(term)
                || (l.Email != null && l.Email.Contains(term)) || (l.Phone != null && l.Phone.Contains(term))
                || (l.CompanyName != null && l.CompanyName.ToLower().Contains(term)));
        }
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(l => l.Status == status);
        if (CanSeeAll && !string.IsNullOrWhiteSpace(assignedTo))
            query = query.Where(l => l.AssignedUserId == assignedTo);

        ViewData["Search"] = search;
        ViewData["Status"] = status;
        ViewData["AssignedTo"] = assignedTo;
        await LoadUserFilterAsync();
        return View(await query.OrderByDescending(l => l.CreatedDate).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var lead = await Scoped(Db.Leads).Include(l => l.AssignedUser).Include(l => l.ConvertedCustomer)
            .FirstOrDefaultAsync(l => l.Id == id);
        if (lead is null) return NotFound();

        ViewBag.Opportunities = await Scoped(Db.Opportunities).Where(o => o.LeadId == id).ToListAsync();
        ViewBag.FollowUps = await Scoped(Db.FollowUps).Where(f => f.LeadId == id).OrderByDescending(f => f.FollowUpDate).ToListAsync();
        ViewBag.Activities = await Scoped(Db.Activities).Where(a => a.LeadId == id).OrderByDescending(a => a.ActivityDate).ToListAsync();
        // Only show the converted customer link if this user may open it.
        ViewBag.CanOpenConvertedCustomer = lead.ConvertedCustomerId is int cid && await Scoped(Db.Customers).AnyAsync(c => c.Id == cid);
        return View(lead);
    }

    public async Task<IActionResult> Create()
    {
        await LoadListsAsync();
        return View(new Lead());
    }

    [HttpPost]
    public async Task<IActionResult> Create(Lead input)
    {
        input.AssignedUserId = await ResolveAssigneeAsync(input.AssignedUserId);
        Validate(input);
        if (input.Status == "Converted")
            ModelState.AddModelError(nameof(Lead.Status), "Use the Convert action on an existing lead to convert it to a customer.");
        if (!ModelState.IsValid)
        {
            await LoadListsAsync();
            return View(input);
        }

        var lead = new Lead { LeadCode = TempCode(), CreatedDate = DateTime.UtcNow, CreatedBy = UserName };
        Copy(input, lead);
        Db.Leads.Add(lead);
        await Db.SaveChangesAsync();
        lead.LeadCode = $"LEAD-{lead.Id:D5}";
        Db.Audit(UserName, "Create", nameof(Lead), lead.Id, $"{lead.LeadCode} {lead.LeadName}", newValue: AuditExtensions.Snapshot(lead.AuditFields()));
        await Db.SaveChangesAsync();

        TempData["Success"] = $"Lead {lead.LeadCode} created.";
        return RedirectToAction(nameof(Details), new { id = lead.Id });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var lead = await Scoped(Db.Leads).FirstOrDefaultAsync(l => l.Id == id);
        if (lead is null) return NotFound();
        await LoadListsAsync();
        return View(lead);
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, Lead input)
    {
        var lead = await Scoped(Db.Leads).FirstOrDefaultAsync(l => l.Id == id);
        if (lead is null) return NotFound();

        input.Id = id;
        input.LeadCode = lead.LeadCode;
        input.AssignedUserId = await ResolveAssigneeAsync(input.AssignedUserId, CanSeeAll ? null : lead.AssignedUserId);
        Validate(input);
        // "Converted" is only reachable through the Convert workflow, which records the customer link.
        if (input.Status == "Converted" && lead.Status != "Converted")
            ModelState.AddModelError(nameof(Lead.Status), "Use the Convert action to convert this lead to a customer.");
        if (lead.Status == "Converted" && input.Status != "Converted")
            ModelState.AddModelError(nameof(Lead.Status), "A converted lead cannot change status.");
        if (!ModelState.IsValid)
        {
            await LoadListsAsync();
            return View(input);
        }

        var oldValue = AuditExtensions.Snapshot(lead.AuditFields());
        Copy(input, lead);
        Db.Audit(UserName, "Update", nameof(Lead), lead.Id, $"{lead.LeadCode} status {lead.Status}", oldValue, AuditExtensions.Snapshot(lead.AuditFields()));
        await Db.SaveChangesAsync();
        TempData["Success"] = "Lead updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // ---- Conversion -------------------------------------------------------------------
    // Converting either links the lead to an EXISTING customer that matches its normalised email
    // or phone (so no duplicate is created), or creates a new customer from the lead's details.

    public async Task<IActionResult> Convert(int id)
    {
        var lead = await Scoped(Db.Leads).FirstOrDefaultAsync(l => l.Id == id);
        if (lead is null) return NotFound();
        if (lead.Status is "Converted" or "Lost" or "Unqualified")
        {
            TempData["Error"] = $"A {lead.Status} lead cannot be converted.";
            return RedirectToAction(nameof(Details), new { id });
        }
        await LoadConvertMatchAsync(lead);
        return View(lead);
    }

    [HttpPost, ActionName("Convert")]
    public async Task<IActionResult> ConvertConfirmed(int id, bool createOpportunity)
    {
        var lead = await Scoped(Db.Leads).FirstOrDefaultAsync(l => l.Id == id);
        if (lead is null) return NotFound();
        if (lead.Status is "Converted" or "Lost" or "Unqualified")
        {
            TempData["Error"] = $"A {lead.Status} lead cannot be converted.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var (match, hidden) = await FindMatchingCustomerAsync(lead);
        if (hidden)
        {
            // A matching customer exists but belongs to another sales executive: never duplicate it.
            ModelState.AddModelError(string.Empty, "A customer with this lead's email or phone already exists and is assigned to another user. Ask a manager to convert this lead.");
            await LoadConvertMatchAsync(lead);
            return View(lead);
        }

        Customer customer;
        if (match is not null)
        {
            customer = match;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(lead.Email) || string.IsNullOrWhiteSpace(lead.Phone))
            {
                ModelState.AddModelError(string.Empty, "The lead needs both an email and a phone number before a new customer can be created from it. Edit the lead first.");
                await LoadConvertMatchAsync(lead);
                return View(lead);
            }
            customer = new Customer
            {
                CustomerCode = TempCode(),
                CustomerName = lead.LeadName,
                Email = lead.Email,
                Phone = lead.Phone,
                CompanyName = lead.CompanyName,
                Status = CustomerStatus.Active,
                AssignedUserId = lead.AssignedUserId ?? (CanSeeAll ? null : CurrentUserId),
                CreatedDate = DateTime.UtcNow,
                CreatedBy = UserName
            };
            Db.Customers.Add(customer);
        }

        await using var tx = await Db.Database.BeginTransactionAsync();
        try
        {
            await Db.SaveChangesAsync();
            if (match is null)
            {
                customer.CustomerCode = $"CUST-{customer.Id:D5}";
                Db.Audit(UserName, "Create", nameof(Customer), customer.Id, $"{customer.CustomerCode} from lead {lead.LeadCode}",
                    newValue: AuditExtensions.Snapshot(customer.AuditFields()));
            }

            var oldValue = AuditExtensions.Snapshot(lead.AuditFields());
            lead.Status = "Converted";
            lead.ConvertedCustomerId = customer.Id;
            lead.ConvertedDate = DateTime.UtcNow;
            Db.Audit(UserName, "Convert", nameof(Lead), lead.Id,
                $"{lead.LeadCode} -> {(match is null ? "new" : "existing")} customer {customer.CustomerCode}",
                oldValue, AuditExtensions.Snapshot(new { lead.Status, lead.ConvertedCustomerId, customer.CustomerCode }));

            if (createOpportunity && lead.ExpectedValue > 0)
            {
                var opp = new Opportunity
                {
                    OpportunityName = $"{lead.LeadName} - {lead.CompanyName ?? "new business"}",
                    CustomerId = customer.Id,
                    LeadId = lead.Id,
                    Amount = lead.ExpectedValue,
                    Stage = "Qualification",
                    Probability = 20,
                    ExpectedCloseDate = DateTime.Today.AddDays(30),
                    Status = OpportunityStatus.Open,
                    AssignedUserId = lead.AssignedUserId ?? customer.AssignedUserId,
                    CreatedDate = DateTime.UtcNow,
                    CreatedBy = UserName
                };
                Db.Opportunities.Add(opp);
                await Db.SaveChangesAsync();
                Db.Audit(UserName, "Create", nameof(Opportunity), opp.Id, $"{opp.OpportunityName} from lead {lead.LeadCode}",
                    newValue: AuditExtensions.Snapshot(opp.AuditFields()));
            }
            await Db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (DbUpdateException)
        {
            // e.g. a customer with the same email/phone was created at the same moment.
            await tx.RollbackAsync();
            Db.ChangeTracker.Clear();
            TempData["Error"] = "The lead could not be converted because a matching customer was created at the same time. Please try again.";
            return RedirectToAction(nameof(Convert), new { id });
        }

        TempData["Success"] = match is null
            ? $"Lead converted: new customer {customer.CustomerCode} created."
            : $"Lead converted and linked to existing customer {customer.CustomerCode} (no duplicate created).";
        return RedirectToAction("Details", "Customers", new { id = customer.Id });
    }

    // Finds a customer with the lead's normalised email or phone across ALL customers.
    // hidden = a match exists but the current user may not see it.
    private async Task<(Customer? Match, bool Hidden)> FindMatchingCustomerAsync(Lead lead)
    {
        var email = CrmRules.NormalizeEmail(lead.Email);
        var phone = PhoneFormat.Normalize(lead.Phone);
        if (email.Length == 0 && phone.Length == 0) return (null, false);

        var any = await Db.Customers.Where(c => (email.Length > 0 && c.Email == email) || (phone.Length > 0 && c.Phone == phone))
            .Select(c => c.Id).ToListAsync();
        if (any.Count == 0) return (null, false);
        var visible = await Scoped(Db.Customers).FirstOrDefaultAsync(c => any.Contains(c.Id));
        return visible is null ? (null, true) : (visible, false);
    }

    private async Task LoadConvertMatchAsync(Lead lead)
    {
        var (match, hidden) = await FindMatchingCustomerAsync(lead);
        ViewBag.Match = match;
        ViewBag.MatchHidden = hidden;
    }

    // ---- Deactivate / delete --------------------------------------------------------

    // Soft delete: mark the lead Lost.
    [HttpPost]
    public async Task<IActionResult> Deactivate(int id)
    {
        var lead = await Scoped(Db.Leads).FirstOrDefaultAsync(l => l.Id == id);
        if (lead is null) return NotFound();
        if (lead.Status == "Converted")
        {
            TempData["Error"] = "A converted lead cannot be marked Lost.";
            return RedirectToAction(nameof(Details), new { id });
        }
        var oldStatus = lead.Status;
        lead.Status = "Lost";
        Db.Audit(UserName, "Deactivate", nameof(Lead), lead.Id, lead.LeadCode, $"Status={oldStatus}", "Status=Lost");
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Lead {lead.LeadCode} marked Lost.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Delete(int id)
    {
        var lead = await Db.Leads.FindAsync(id);
        if (lead is null) return NotFound();
        Db.Leads.Remove(lead);
        Db.Audit(UserName, "Delete", nameof(Lead), lead.Id, lead.LeadCode, AuditExtensions.Snapshot(lead.AuditFields()));
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Lead {lead.LeadCode} deleted.";
        return RedirectToAction(nameof(Index));
    }

    private void Validate(Lead l)
    {
        IgnoreServerFields(nameof(Lead.LeadCode), nameof(Lead.ConvertedCustomerId), nameof(Lead.ConvertedDate));
        CrmRules.ValidateLead(l, MaxLeadValue, ModelState);
    }

    private static void Copy(Lead from, Lead to)
    {
        to.LeadName = from.LeadName;
        to.Email = from.Email;
        to.Phone = from.Phone;
        to.CompanyName = from.CompanyName;
        to.Source = from.Source;
        to.Status = from.Status;
        to.ExpectedValue = from.ExpectedValue;
        to.AssignedUserId = from.AssignedUserId;
    }
}
