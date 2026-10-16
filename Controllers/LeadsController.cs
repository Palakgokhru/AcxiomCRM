using AcxiomCRM.Data;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

public class LeadsController(ApplicationDbContext db, UserManager<IdentityUser> userManager) : CrmController(db, userManager)
{
    public async Task<IActionResult> Index(string? search, string? status)
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

        ViewData["Search"] = search;
        ViewData["Status"] = status;
        return View(await query.OrderByDescending(l => l.CreatedDate).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var lead = await Scoped(Db.Leads).Include(l => l.AssignedUser).FirstOrDefaultAsync(l => l.Id == id);
        return lead is null ? NotFound() : View(lead);
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
        if (!ModelState.IsValid)
        {
            await LoadListsAsync();
            return View(input);
        }

        var lead = new Lead
        {
            LeadCode = $"LEAD-{(await Db.Leads.MaxAsync(l => (int?)l.Id) ?? 0) + 1:D5}",
            CreatedDate = DateTime.UtcNow,
            CreatedBy = UserName
        };
        Copy(input, lead);
        Db.Leads.Add(lead);
        await Db.SaveChangesAsync();
        Db.Audit(UserName, "Create", nameof(Lead), lead.Id, $"{lead.LeadCode} {lead.LeadName}");
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
        if (!ModelState.IsValid)
        {
            await LoadListsAsync();
            return View(input);
        }

        Copy(input, lead);
        Db.Audit(UserName, "Update", nameof(Lead), lead.Id, $"{lead.LeadCode} status {lead.Status}");
        await Db.SaveChangesAsync();
        TempData["Success"] = "Lead updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // Soft delete: mark the lead Lost.
    [HttpPost]
    public async Task<IActionResult> Deactivate(int id)
    {
        var lead = await Scoped(Db.Leads).FirstOrDefaultAsync(l => l.Id == id);
        if (lead is null) return NotFound();
        lead.Status = "Lost";
        Db.Audit(UserName, "Deactivate", nameof(Lead), lead.Id, lead.LeadCode);
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
        Db.Audit(UserName, "Delete", nameof(Lead), lead.Id, lead.LeadCode);
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Lead {lead.LeadCode} deleted.";
        return RedirectToAction(nameof(Index));
    }

    private void Validate(Lead l)
    {
        IgnoreServerFields(nameof(Lead.LeadCode));
        l.LeadName = l.LeadName?.Trim() ?? string.Empty;
        l.Email = string.IsNullOrWhiteSpace(l.Email) ? null : l.Email.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(l.Phone) && ModelState.GetValidationState(nameof(Lead.Phone)) != Microsoft.AspNetCore.Mvc.ModelBinding.ModelValidationState.Invalid)
        {
            var digits = l.Phone.Count(char.IsDigit);
            if (digits < 7 || digits > 15) ModelState.AddModelError(nameof(Lead.Phone), PhoneFormat.Message);
            else l.Phone = PhoneFormat.Normalize(l.Phone);
        }
        else if (string.IsNullOrWhiteSpace(l.Phone)) l.Phone = null;

        if (l.ExpectedValue < 0) ModelState.AddModelError(nameof(Lead.ExpectedValue), "Expected value cannot be negative.");
        CheckAllowed(nameof(Lead.Status), l.Status, LeadStatus.All);
        CheckAllowed(nameof(Lead.Source), l.Source, LeadSources.All);
    }

    private static void Copy(Lead from, Lead to)
    {
        to.LeadName = from.LeadName;
        to.Email = from.Email;
        to.Phone = from.Phone;
        to.CompanyName = from.CompanyName?.Trim();
        to.Source = from.Source;
        to.Status = from.Status;
        to.ExpectedValue = from.ExpectedValue;
        to.AssignedUserId = from.AssignedUserId;
    }
}
