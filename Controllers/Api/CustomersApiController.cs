using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using AcxiomCRM.Data;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers.Api;

// ---- DTOs: only business fields go in or out; no Identity/password data is ever exposed. ----

public record CustomerDto(int Id, string CustomerCode, string CustomerName, string Email, string Phone, string CompanyName,
    string Address, string City, string State, string Status, DateTime CreatedDate, string CreatedBy, string? AssignedTo);

public class CustomerWriteDto
{
    [Required, StringLength(100, MinimumLength = 2)] public string CustomerName { get; set; } = string.Empty;
    [Required, StringLength(150), EmailAddress] public string Email { get; set; } = string.Empty;
    [Required, StringLength(20), RegularExpression(PhoneFormat.Pattern, ErrorMessage = PhoneFormat.Message)]
    public string Phone { get; set; } = string.Empty;
    [Required, StringLength(150)] public string CompanyName { get; set; } = string.Empty;
    [Required, StringLength(250)] public string Address { get; set; } = string.Empty;
    [Required, StringLength(100)] public string City { get; set; } = string.Empty;
    [Required, StringLength(100)] public string State { get; set; } = string.Empty;
    [Required] public string Status { get; set; } = CustomerStatus.Active;
    // Honoured only for Admin/Manager; a SalesExecutive always owns their records.
    public string? AssignedUserId { get; set; }
}

public record LeadDto(int Id, string LeadCode, string LeadName, string? Email, string? Phone, string? CompanyName,
    string Source, string Status, decimal ExpectedValue, DateTime CreatedDate, string? AssignedTo);

public record OpportunityDto(int Id, string OpportunityName, int? CustomerId, string? CustomerName, int? LeadId, decimal Amount,
    string Stage, int Probability, DateTime ExpectedCloseDate, string Status, DateTime CreatedDate, string? AssignedTo);

/// <summary>
/// JSON API using the same cookie login as the website. Unauthenticated calls get 401 and
/// role failures 403 (see the cookie events in Program.cs). Write endpoints accept only
/// application/json, which a cross-site HTML form cannot send, so they skip the form anti-forgery token.
/// </summary>
[ApiController]
[Route("api/customers")]
[Authorize]
[IgnoreAntiforgeryToken]
[Produces("application/json")]
public class CustomersApiController(ApplicationDbContext db, UserManager<IdentityUser> userManager) : ControllerBase
{
    private string CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private bool CanSeeAll => User.IsInRole(Roles.Admin) || User.IsInRole(Roles.Manager);

    private IQueryable<Customer> Scoped()
    {
        var q = db.Customers.Include(c => c.AssignedUser).AsQueryable();
        if (CanSeeAll) return q;
        var userId = CurrentUserId;
        return q.Where(c => c.AssignedUserId == userId);
    }

    private static CustomerDto ToDto(Customer c) => new(c.Id, c.CustomerCode, c.CustomerName, c.Email, c.Phone, c.CompanyName,
        c.Address, c.City, c.State, c.Status, c.CreatedDate, c.CreatedBy, c.AssignedUser?.UserName);

    // GET /api/customers?search=
    [HttpGet]
    public async Task<ActionResult<IEnumerable<CustomerDto>>> GetAll(string? search)
    {
        var q = Scoped();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            q = q.Where(c => c.CustomerName.ToLower().Contains(term) || c.Email.Contains(term)
                || c.Phone.Contains(term) || c.CompanyName.ToLower().Contains(term));
        }
        var list = await q.OrderBy(c => c.CustomerName).ToListAsync();
        return Ok(list.Select(ToDto));
    }

    // GET /api/customers/5 -> 404 if missing OR not visible to this user
    [HttpGet("{id:int}")]
    public async Task<ActionResult<CustomerDto>> Get(int id)
    {
        var c = await Scoped().FirstOrDefaultAsync(x => x.Id == id);
        return c is null ? NotFound(Problem404()) : Ok(ToDto(c));
    }

    // POST /api/customers -> 201, 400 (validation) or 409 (duplicate)
    [HttpPost]
    [Consumes("application/json")]
    public async Task<ActionResult<CustomerDto>> Create(CustomerWriteDto dto)
    {
        var customer = new Customer();
        var error = await ApplyAsync(dto, customer, existingId: null);
        if (error is not null) return error;

        customer.CustomerCode = $"CUST-{(await db.Customers.MaxAsync(c => (int?)c.Id) ?? 0) + 1:D5}";
        customer.CreatedDate = DateTime.UtcNow;
        customer.CreatedBy = User.Identity!.Name!;
        db.Customers.Add(customer);
        if (!await TrySaveAsync()) return Conflict(new ProblemDetails { Title = "Duplicate customer", Status = 409 });

        db.Audit(User.Identity.Name, "Create", nameof(Customer), customer.Id, "via API", newValue: AuditExtensions.Snapshot(customer.AuditFields()));
        await db.SaveChangesAsync();
        await db.Entry(customer).Reference(c => c.AssignedUser).LoadAsync();
        return CreatedAtAction(nameof(Get), new { id = customer.Id }, ToDto(customer));
    }

    // PUT /api/customers/5 -> 200, 400, 404, 409
    [HttpPut("{id:int}")]
    [Consumes("application/json")]
    public async Task<ActionResult<CustomerDto>> Update(int id, CustomerWriteDto dto)
    {
        var customer = await Scoped().FirstOrDefaultAsync(x => x.Id == id);
        if (customer is null) return NotFound(Problem404());

        var oldValue = AuditExtensions.Snapshot(customer.AuditFields());
        var error = await ApplyAsync(dto, customer, existingId: id);
        if (error is not null) return error;

        db.Audit(User.Identity!.Name, "Update", nameof(Customer), customer.Id, "via API", oldValue, AuditExtensions.Snapshot(customer.AuditFields()));
        if (!await TrySaveAsync()) return Conflict(new ProblemDetails { Title = "Duplicate customer", Status = 409 });
        await db.Entry(customer).Reference(c => c.AssignedUser).LoadAsync();
        return Ok(ToDto(customer));
    }

    // DELETE /api/customers/5 -> soft delete (Status = Inactive), 204 or 404
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int id)
    {
        var customer = await Scoped().FirstOrDefaultAsync(x => x.Id == id);
        if (customer is null) return NotFound(Problem404());
        var oldStatus = customer.Status;
        customer.Status = CustomerStatus.Inactive;
        db.Audit(User.Identity!.Name, "Deactivate", nameof(Customer), customer.Id, "via API", $"Status={oldStatus}", "Status=Inactive");
        await db.SaveChangesAsync();
        return NoContent();
    }

    private static ProblemDetails Problem404() => new() { Title = "Customer not found", Status = 404 };

    // Normalises and validates the payload, then copies it onto the entity.
    // Returns 400/409 result on failure; null on success.
    private async Task<ActionResult?> ApplyAsync(CustomerWriteDto dto, Customer target, int? existingId)
    {
        var email = dto.Email.Trim().ToLowerInvariant();
        var digits = dto.Phone.Count(char.IsDigit);
        if (digits < 7 || digits > 15) ModelState.AddModelError(nameof(dto.Phone), "Phone number must contain 7 to 15 digits.");
        if (!CustomerStatus.All.Contains(dto.Status))
            ModelState.AddModelError(nameof(dto.Status), $"Status must be one of: {string.Join(", ", CustomerStatus.All)}.");

        string? assignee = CanSeeAll ? (string.IsNullOrEmpty(dto.AssignedUserId) ? null : dto.AssignedUserId)
                                     : existingId is null ? CurrentUserId : target.AssignedUserId;
        if (CanSeeAll && assignee is not null && await userManager.FindByIdAsync(assignee) is null)
            ModelState.AddModelError(nameof(dto.AssignedUserId), "Assigned user does not exist.");
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var phone = PhoneFormat.Normalize(dto.Phone);
        var name = dto.CustomerName.Trim();
        var company = dto.CompanyName.Trim();
        var others = db.Customers.Where(x => existingId == null || x.Id != existingId);
        var conflicts = new List<string>();
        if (await others.AnyAsync(x => x.Email == email)) conflicts.Add("A customer with this email already exists.");
        if (await others.AnyAsync(x => x.Phone == phone)) conflicts.Add("A customer with this phone number already exists.");
        if (await others.AnyAsync(x => x.CustomerName.ToLower() == name.ToLower() && x.CompanyName.ToLower() == company.ToLower()))
            conflicts.Add("A customer with this name already exists for this company.");
        if (conflicts.Count > 0)
            return Conflict(new ProblemDetails { Title = "Duplicate customer", Status = 409, Detail = string.Join(" ", conflicts) });

        target.CustomerName = name;
        target.Email = email;
        target.Phone = phone;
        target.CompanyName = company;
        target.Address = dto.Address.Trim();
        target.City = dto.City.Trim();
        target.State = dto.State.Trim();
        target.Status = dto.Status;
        target.AssignedUserId = assignee;
        return null;
    }

    private async Task<bool> TrySaveAsync()
    {
        try { await db.SaveChangesAsync(); return true; }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}

// Read-only lists for leads and opportunities, same ownership scope as the web pages.
[ApiController]
[Authorize]
[Produces("application/json")]
public class CrmReadApiController(ApplicationDbContext db) : ControllerBase
{
    private bool CanSeeAll => User.IsInRole(Roles.Admin) || User.IsInRole(Roles.Manager);
    private string CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpGet("api/leads")]
    public async Task<ActionResult<IEnumerable<LeadDto>>> Leads()
    {
        var q = db.Leads.Include(l => l.AssignedUser).AsQueryable();
        if (!CanSeeAll) { var u = CurrentUserId; q = q.Where(l => l.AssignedUserId == u); }
        var list = await q.OrderByDescending(l => l.CreatedDate).ToListAsync();
        return Ok(list.Select(l => new LeadDto(l.Id, l.LeadCode, l.LeadName, l.Email, l.Phone, l.CompanyName, l.Source,
            l.Status, l.ExpectedValue, l.CreatedDate, l.AssignedUser?.UserName)));
    }

    [HttpGet("api/opportunities")]
    public async Task<ActionResult<IEnumerable<OpportunityDto>>> Opportunities()
    {
        var q = db.Opportunities.Include(o => o.Customer).Include(o => o.AssignedUser).AsQueryable();
        if (!CanSeeAll) { var u = CurrentUserId; q = q.Where(o => o.AssignedUserId == u); }
        var list = await q.OrderBy(o => o.ExpectedCloseDate).ToListAsync();
        return Ok(list.Select(o => new OpportunityDto(o.Id, o.OpportunityName, o.CustomerId, o.Customer?.CustomerName, o.LeadId,
            o.Amount, o.Stage, o.Probability, o.ExpectedCloseDate, o.Status, o.CreatedDate, o.AssignedUser?.UserName)));
    }
}
