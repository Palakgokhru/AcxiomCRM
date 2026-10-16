using System.Security.Claims;
using AcxiomCRM.Data;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers;

[Authorize]
public class CustomersController(ApplicationDbContext db, UserManager<IdentityUser> userManager) : Controller
{
    private string CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private bool CanSeeAll => User.IsInRole(Roles.Admin) || User.IsInRole(Roles.Manager);

    // Admin and Manager see every customer; a SalesExecutive only sees customers assigned to them.
    // Every action loads records through this, so URL tampering returns 404.
    private IQueryable<Customer> ScopedCustomers()
    {
        var query = db.Customers.Include(c => c.AssignedUser).AsQueryable();
        if (!CanSeeAll)
        {
            var userId = CurrentUserId;
            query = query.Where(c => c.AssignedUserId == userId);
        }
        return query;
    }

    public async Task<IActionResult> Index(string? search, string? status)
    {
        var query = ScopedCustomers();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            var phoneTerm = NormalizePhone(search);
            query = query.Where(c =>
                c.CustomerName.ToLower().Contains(term) ||
                c.Email.ToLower().Contains(term) ||
                c.CompanyName.ToLower().Contains(term) ||
                c.CustomerCode.ToLower().Contains(term) ||
                c.Phone.Contains(term) ||
                (phoneTerm.Length > 0 && c.Phone.Contains(phoneTerm)));
        }
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(c => c.Status == status);

        ViewData["Search"] = search;
        ViewData["Status"] = status;
        return View(await query.OrderBy(c => c.CustomerName).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var customer = await ScopedCustomers().FirstOrDefaultAsync(c => c.Id == id);
        return customer is null ? NotFound() : View(customer);
    }

    public async Task<IActionResult> Create()
    {
        await LoadFormListsAsync(null);
        return View(new Customer());
    }

    [HttpPost]
    public async Task<IActionResult> Create(Customer input)
    {
        Normalize(input);
        if (!CanSeeAll)
            input.AssignedUserId = CurrentUserId;   // a SalesExecutive always owns what they create
        else if (string.IsNullOrEmpty(input.AssignedUserId))
            input.AssignedUserId = null;

        await ValidateAsync(input, existingId: null);
        if (!ModelState.IsValid)
        {
            await LoadFormListsAsync(input.AssignedUserId);
            return View(input);
        }

        var customer = new Customer
        {
            CustomerCode = await NextCustomerCodeAsync(),
            CreatedDate = DateTime.UtcNow,
            CreatedBy = User.Identity!.Name!
        };
        CopyEditableFields(input, customer);
        db.Customers.Add(customer);

        if (!await TrySaveAsync())
        {
            await LoadFormListsAsync(input.AssignedUserId);
            return View(input);
        }
        db.Audit(User.Identity.Name, "Create", nameof(Customer), customer.Id, $"{customer.CustomerCode} {customer.CustomerName}");
        await db.SaveChangesAsync();

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

        Normalize(input);
        if (!CanSeeAll)
            input.AssignedUserId = customer.AssignedUserId;   // SalesExecutive cannot reassign
        else if (string.IsNullOrEmpty(input.AssignedUserId))
            input.AssignedUserId = null;

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

        CopyEditableFields(input, customer);
        db.Audit(User.Identity!.Name, "Update", nameof(Customer), customer.Id, $"{customer.CustomerCode} {customer.CustomerName}");
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

        customer.Status = CustomerStatus.Inactive;
        db.Audit(User.Identity!.Name, "Deactivate", nameof(Customer), customer.Id, customer.CustomerCode);
        await db.SaveChangesAsync();
        TempData["Success"] = $"Customer {customer.CustomerCode} deactivated.";
        return RedirectToAction(nameof(Index));
    }

    // Permanent delete is restricted to Admin.
    [HttpPost, Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var customer = await db.Customers.FindAsync(id);
        if (customer is null) return NotFound();
        if (await db.Opportunities.AnyAsync(o => o.CustomerId == id))
        {
            TempData["Success"] = null;
            ModelState.AddModelError(string.Empty, "This customer has opportunities and cannot be permanently deleted. Deactivate it instead.");
            return View("Delete", await ScopedCustomers().FirstAsync(c => c.Id == id));
        }

        db.Customers.Remove(customer);
        db.Audit(User.Identity!.Name, "Delete", nameof(Customer), customer.Id, $"{customer.CustomerCode} {customer.CustomerName}");
        await db.SaveChangesAsync();
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

    private static void Normalize(Customer c)
    {
        c.CustomerName = c.CustomerName?.Trim() ?? string.Empty;
        c.Email = c.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        c.CompanyName = c.CompanyName?.Trim() ?? string.Empty;
        c.Address = c.Address?.Trim() ?? string.Empty;
        c.City = c.City?.Trim() ?? string.Empty;
        c.State = c.State?.Trim() ?? string.Empty;
        c.Phone = c.Phone?.Trim() ?? string.Empty;
    }

    // "+1 (555) 123-4567" -> "+15551234567"; used for storage and duplicate detection.
    private static string NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return string.Empty;
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return phone.TrimStart().StartsWith('+') && digits.Length > 0 ? "+" + digits : digits;
    }

    // Server-side rules that data annotations alone cannot express. Runs on every POST,
    // so a crafted request that bypasses client validation is still rejected.
    private async Task ValidateAsync(Customer c, int? existingId)
    {
        // Server-generated / server-set fields are never taken from the form.
        ModelState.Remove(nameof(Customer.CustomerCode));
        ModelState.Remove(nameof(Customer.CreatedBy));
        ModelState.Remove(nameof(Customer.CreatedDate));
        ModelState.Remove(nameof(Customer.AssignedUserId));

        if (!ModelState.TryGetValue(nameof(Customer.Phone), out var phoneState) || phoneState.Errors.Count == 0)
        {
            var digits = c.Phone.Count(char.IsDigit);
            if (digits < 7 || digits > 15)
                ModelState.AddModelError(nameof(Customer.Phone), "Phone number must contain 7 to 15 digits.");
            else
                c.Phone = NormalizePhone(c.Phone);
        }

        if (!CustomerStatus.All.Contains(c.Status))
            ModelState.AddModelError(nameof(Customer.Status), "Choose a valid status.");

        if (c.AssignedUserId is not null && await userManager.FindByIdAsync(c.AssignedUserId) is null)
            ModelState.AddModelError(nameof(Customer.AssignedUserId), "Selected user does not exist.");

        if (!ModelState.IsValid) return;

        // Duplicate checks run across ALL customers, not just the current user's scope.
        var others = db.Customers.Where(x => existingId == null || x.Id != existingId);
        if (await others.AnyAsync(x => x.Email == c.Email))
            ModelState.AddModelError(nameof(Customer.Email), "A customer with this email already exists.");
        if (await others.AnyAsync(x => x.Phone == c.Phone))
            ModelState.AddModelError(nameof(Customer.Phone), "A customer with this phone number already exists.");
        var name = c.CustomerName.ToLower();
        var company = c.CompanyName.ToLower();
        if (await others.AnyAsync(x => x.CustomerName.ToLower() == name && x.CompanyName.ToLower() == company))
            ModelState.AddModelError(nameof(Customer.CustomerName), "A customer with this name already exists for this company.");
    }

    // Safety net for races: the unique indexes reject the duplicate and we show a message.
    private async Task<bool> TrySaveAsync()
    {
        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true)
        {
            var msg = ex.InnerException.Message;
            var field = msg.Contains("Email") ? "email" : msg.Contains("Phone") ? "phone number"
                : msg.Contains("CustomerCode") ? "customer code" : "name and company";
            ModelState.AddModelError(string.Empty, $"Another customer with the same {field} already exists. Please review and try again.");
            db.ChangeTracker.Clear();
            return false;
        }
    }

    private async Task<string> NextCustomerCodeAsync()
    {
        var maxId = await db.Customers.MaxAsync(c => (int?)c.Id) ?? 0;
        return $"CUST-{maxId + 1:D5}";
    }

    private async Task LoadFormListsAsync(string? selectedUserId)
    {
        ViewBag.Statuses = new SelectList(CustomerStatus.All);
        ViewBag.CanAssign = CanSeeAll;
        if (CanSeeAll)
        {
            var users = await userManager.Users.OrderBy(u => u.UserName).ToListAsync();
            ViewBag.Users = new SelectList(users, nameof(IdentityUser.Id), nameof(IdentityUser.UserName), selectedUserId);
        }
    }
}
