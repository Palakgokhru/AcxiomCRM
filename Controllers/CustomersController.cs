using AcxiomCRM.Data;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

public class CustomersController(ApplicationDbContext db, UserManager<IdentityUser> userManager) : CrmController(db, userManager)
{
    // Admin and Manager see every customer; a SalesExecutive only sees customers assigned to them.
    // Every action loads records through this, so URL tampering returns 404.
    private IQueryable<Customer> ScopedCustomers() => Scoped(Db.Customers).Include(c => c.AssignedUser);

    public async Task<IActionResult> Index(string? search, string? status, string? assignedTo)
    {
        var query = ScopedCustomers();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            var phoneTerm = PhoneFormat.Normalize(search);
            query = query.Where(c =>
                c.CustomerName.ToLower().Contains(term) ||
                c.Email.ToLower().Contains(term) ||
                (c.CompanyName != null && c.CompanyName.ToLower().Contains(term)) ||
                c.CustomerCode.ToLower().Contains(term) ||
                c.Phone.Contains(term) ||
                (phoneTerm.Length > 0 && c.Phone.Contains(phoneTerm)));
        }
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(c => c.Status == status);
        if (CanSeeAll && !string.IsNullOrWhiteSpace(assignedTo))
            query = query.Where(c => c.AssignedUserId == assignedTo);

        ViewData["Search"] = search;
        ViewData["Status"] = status;
        ViewData["AssignedTo"] = assignedTo;
        await LoadUserFilterAsync();
        return View(await query.OrderBy(c => c.CustomerName).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var customer = await ScopedCustomers().FirstOrDefaultAsync(c => c.Id == id);
        if (customer is null) return NotFound();

        // Related records, also limited to what this user may see.
        ViewBag.Opportunities = await Scoped(Db.Opportunities).Where(o => o.CustomerId == id).OrderBy(o => o.ExpectedCloseDate).ToListAsync();
        ViewBag.FollowUps = await Scoped(Db.FollowUps).Where(f => f.CustomerId == id).OrderByDescending(f => f.FollowUpDate).ToListAsync();
        ViewBag.Activities = await Scoped(Db.Activities).Where(a => a.CustomerId == id).OrderByDescending(a => a.ActivityDate).ToListAsync();
        ViewBag.ConvertedLeads = await Scoped(Db.Leads).Where(l => l.ConvertedCustomerId == id).ToListAsync();
        return View(customer);
    }

    public async Task<IActionResult> Create()
    {
        await LoadFormListsAsync(null);
        return View(new Customer());
    }

    [HttpPost]
    public async Task<IActionResult> Create(Customer input)
    {
        input.AssignedUserId = await ResolveAssigneeAsync(input.AssignedUserId);   // SalesExecutive always owns it
        await ValidateAsync(input, existingId: null);
        if (!ModelState.IsValid)
        {
            await LoadFormListsAsync(input.AssignedUserId);
            return View(input);
        }

        var customer = new Customer
        {
            CustomerCode = TempCode(),
            CreatedDate = DateTime.UtcNow,
            CreatedBy = UserName
        };
        CopyEditableFields(input, customer);
        Db.Customers.Add(customer);
        if (!await TrySaveAsync())
        {
            await LoadFormListsAsync(input.AssignedUserId);
            return View(input);
        }

        // Code is derived from the never-reused database Id, so a deleted customer's code is never handed out again.
        customer.CustomerCode = $"CUST-{customer.Id:D5}";
        Db.Audit(UserName, "Create", nameof(Customer), customer.Id, $"{customer.CustomerCode} {customer.CustomerName}",
            newValue: AuditExtensions.Snapshot(customer.AuditFields()));
        await Db.SaveChangesAsync();

        TempData["Success"] = $"Customer {customer.CustomerCode} created.";
        return RedirectToAction(nameof(Details), new { id = customer.Id });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var customer = await ScopedCustomers().FirstOrDefaultAsync(c => c.Id == id);
        if (customer is null) return NotFound();
        await LoadFormListsAsync(customer.AssignedUserId);
        return View(customer);
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, Customer input)
    {
        var customer = await ScopedCustomers().FirstOrDefaultAsync(c => c.Id == id);
        if (customer is null) return NotFound();

        input.AssignedUserId = await ResolveAssigneeAsync(input.AssignedUserId, CanSeeAll ? null : customer.AssignedUserId);
        // Keep server-owned values for redisplay if validation fails.
        input.Id = id;
        input.CustomerCode = customer.CustomerCode;
        input.CreatedBy = customer.CreatedBy;
        input.CreatedDate = customer.CreatedDate;

        await ValidateAsync(input, existingId: id);
        if (!ModelState.IsValid)
        {
            await LoadFormListsAsync(input.AssignedUserId);
            return View(input);
        }

        var oldValue = AuditExtensions.Snapshot(customer.AuditFields());
        CopyEditableFields(input, customer);
        Db.Audit(UserName, "Update", nameof(Customer), customer.Id, $"{customer.CustomerCode} {customer.CustomerName}",
            oldValue, AuditExtensions.Snapshot(customer.AuditFields()));
        if (!await TrySaveAsync())
        {
            await LoadFormListsAsync(input.AssignedUserId);
            return View(input);
        }

        TempData["Success"] = "Customer updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> Delete(int id)
    {
        var customer = await ScopedCustomers().FirstOrDefaultAsync(c => c.Id == id);
        return customer is null ? NotFound() : View(customer);
    }

    // Soft delete, available to anyone who can see the record.
    [HttpPost]
    public async Task<IActionResult> Deactivate(int id)
    {
        var customer = await ScopedCustomers().FirstOrDefaultAsync(c => c.Id == id);
        if (customer is null) return NotFound();

        var oldStatus = customer.Status;
        customer.Status = CustomerStatus.Inactive;
        Db.Audit(UserName, "Deactivate", nameof(Customer), customer.Id, customer.CustomerCode,
            $"Status={oldStatus}", $"Status={customer.Status}");
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Customer {customer.CustomerCode} deactivated.";
        return RedirectToAction(nameof(Index));
    }

    // Permanent delete is restricted to Admin, and only for customers without opportunities.
    [HttpPost, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var customer = await ScopedCustomers().FirstOrDefaultAsync(c => c.Id == id);
        if (customer is null) return NotFound();
        if (await Db.Opportunities.AnyAsync(o => o.CustomerId == id))
        {
            ModelState.AddModelError(string.Empty, "This customer has opportunities and cannot be permanently deleted. Deactivate it instead.");
            return View("Delete", customer);
        }

        Db.Customers.Remove(customer);
        Db.Audit(UserName, "Delete", nameof(Customer), customer.Id, $"{customer.CustomerCode} {customer.CustomerName}",
            AuditExtensions.Snapshot(customer.AuditFields()));
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Customer {customer.CustomerCode} permanently deleted.";
        return RedirectToAction(nameof(Index));
    }

    // ---- helpers ----------------------------------------------------------------

    private static void CopyEditableFields(Customer from, Customer to)
    {
        to.CustomerName = from.CustomerName;
        to.Email = from.Email;
        to.Phone = from.Phone;
        to.CompanyName = from.CompanyName;
        to.Address = from.Address;
        to.City = from.City;
        to.State = from.State;
        to.Status = from.Status;
        to.AssignedUserId = from.AssignedUserId;
    }

    // Shared Section 17.7 rules (CrmRules) + duplicate check. Runs on every POST, so a crafted
    // request that bypasses client validation is still rejected.
    private async Task ValidateAsync(Customer c, int? existingId)
    {
        IgnoreServerFields(nameof(Customer.CustomerCode));
        CrmRules.ValidateCustomer(c, ModelState);
        if (!ModelState.IsValid) return;
        foreach (var (field, message) in await CrmRules.FindCustomerConflictsAsync(Db, c.Email, c.Phone, existingId))
            ModelState.AddModelError(field, message);
    }

    // Safety net for races: the unique indexes reject the duplicate and we show a message.
    private async Task<bool> TrySaveAsync()
    {
        try
        {
            await Db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true)
        {
            var msg = ex.InnerException.Message;
            var field = msg.Contains("Email") ? "email" : msg.Contains("Phone") ? "phone number" : "customer code";
            ModelState.AddModelError(string.Empty, $"Another customer with the same {field} already exists. Please review and try again.");
            Db.ChangeTracker.Clear();
            return false;
        }
    }

    private async Task LoadFormListsAsync(string? selectedUserId)
    {
        ViewBag.Statuses = new SelectList(CustomerStatus.All);
        await LoadListsAsync();
        if (CanSeeAll)
            ViewBag.Users = new SelectList(await Users.Users.OrderBy(u => u.UserName).ToListAsync(),
                nameof(IdentityUser.Id), nameof(IdentityUser.UserName), selectedUserId);
    }
}
