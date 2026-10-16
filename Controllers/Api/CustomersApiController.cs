using System.ComponentModel.DataAnnotations;
using AcxiomCRM.Data;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers.Api;

// ---- DTOs: only business fields go in or out; no Identity/password data is ever exposed. ----

public record CustomerDto(int Id, string CustomerCode, string CustomerName, string Email, string Phone, string? CompanyName,
    string? Address, string? City, string? State, string Status, DateTime CreatedDate, string CreatedBy, string? AssignedTo);

public class CustomerWriteDto
{
    [Required, StringLength(100, MinimumLength = 2)] public string CustomerName { get; set; } = string.Empty;
    [Required, StringLength(150), EmailAddress] public string Email { get; set; } = string.Empty;
    [Required, StringLength(20), RegularExpression(PhoneFormat.Pattern, ErrorMessage = PhoneFormat.Message)]
    public string Phone { get; set; } = string.Empty;
    [StringLength(150)] public string? CompanyName { get; set; }
    [StringLength(250)] public string? Address { get; set; }
    [StringLength(100)] public string? City { get; set; }
    [StringLength(100)] public string? State { get; set; }
    [Required] public string Status { get; set; } = CustomerStatus.Active;
    // Admin/Manager may assign anyone; a SalesExecutive may only leave it empty or use their own id.
    public string? AssignedUserId { get; set; }
}

public record LeadDto(int Id, string LeadCode, string LeadName, string? Email, string? Phone, string? CompanyName,
    string Source, string Status, decimal ExpectedValue, DateTime CreatedDate, string? AssignedTo, int? ConvertedCustomerId);

public class LeadWriteDto
{
    [Required, StringLength(100, MinimumLength = 2)] public string LeadName { get; set; } = string.Empty;
    [StringLength(150), EmailAddress] public string? Email { get; set; }
    [StringLength(20), RegularExpression(PhoneFormat.Pattern, ErrorMessage = PhoneFormat.Message)] public string? Phone { get; set; }
    [StringLength(150)] public string? CompanyName { get; set; }
    [Required] public string Source { get; set; } = "Website";
    [Required] public string Status { get; set; } = "New";
    [Range(typeof(decimal), "0", "999999999999", ErrorMessage = "Expected value cannot be negative.")]
    public decimal ExpectedValue { get; set; }
    public string? AssignedUserId { get; set; }
}

public record OpportunityDto(int Id, string OpportunityName, int? CustomerId, string? CustomerName, int? LeadId, decimal Amount,
    string Stage, int Probability, DateTime ExpectedCloseDate, string Status, DateTime CreatedDate, string? AssignedTo);

public class OpportunityWriteDto
{
    [Required, StringLength(150, MinimumLength = 2)] public string OpportunityName { get; set; } = string.Empty;
    [Required] public int? CustomerId { get; set; }
    public int? LeadId { get; set; }
    [Required] public decimal? Amount { get; set; }
    [Required] public string Stage { get; set; } = "Qualification";
    [Required] public int? Probability { get; set; }
    [Required] public DateTime? ExpectedCloseDate { get; set; }
    [Required] public string Status { get; set; } = OpportunityStatus.Open;
    public string? AssignedUserId { get; set; }
}

/// <summary>
/// Base for the JSON API. Uses the website's login cookie: unauthenticated calls get 401 and role
/// failures 403 (cookie events in Program.cs). Validation, duplicate checks, ownership scope and audit
/// logging use the same CrmRules / CrmScope code as the MVC pages. Write endpoints accept only
/// application/json (a cross-site HTML form cannot send it), so they skip the form anti-forgery token.
/// </summary>
[ApiController]
[Authorize]
[IgnoreAntiforgeryToken]
[Produces("application/json")]
public abstract class CrmApiController(ApplicationDbContext db, UserManager<IdentityUser> userManager) : ControllerBase
{
    protected ApplicationDbContext Db => db;
    private CrmScope? scope;
    protected CrmScope Scope => scope ??= new CrmScope(db, userManager, User);
    protected string UserName => User.Identity?.Name ?? "(unknown)";

    protected static ProblemDetails NotFoundProblem(string what) => new() { Title = $"{what} not found", Status = 404 };

    // A SalesExecutive asking to assign a record to someone else is refused outright (403).
    protected bool IsForbiddenAssignment(string? requested) =>
        !Scope.CanSeeAll && !string.IsNullOrEmpty(requested) && requested != Scope.CurrentUserId;

    protected ObjectResult Forbidden(string detail) =>
        StatusCode(403, new ProblemDetails { Title = "Forbidden", Status = 403, Detail = detail });
}

[Route("api/customers")]
public class CustomersApiController(ApplicationDbContext db, UserManager<IdentityUser> userManager) : CrmApiController(db, userManager)
{
    private IQueryable<Customer> Scoped() => Scope.Scoped(Db.Customers).Include(c => c.AssignedUser);

    private static CustomerDto ToDto(Customer c) => new(c.Id, c.CustomerCode, c.CustomerName, c.Email, c.Phone, c.CompanyName,
        c.Address, c.City, c.State, c.Status, c.CreatedDate, c.CreatedBy, c.AssignedUser?.UserName);

    // GET /api/customers?search=&status=
    [HttpGet]
    public async Task<ActionResult<IEnumerable<CustomerDto>>> GetAll(string? search, string? status)
    {
        var q = Scoped();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            q = q.Where(c => c.CustomerName.ToLower().Contains(term) || c.Email.Contains(term)
                || c.Phone.Contains(term) || (c.CompanyName != null && c.CompanyName.ToLower().Contains(term)));
        }
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(c => c.Status == status);
        var list = await q.OrderBy(c => c.CustomerName).ToListAsync();
        return Ok(list.Select(ToDto));
    }

    // GET /api/customers/5 -> 404 if missing OR not visible to this user (existence is not disclosed)
    [HttpGet("{id:int}")]
    public async Task<ActionResult<CustomerDto>> Get(int id)
    {
        var c = await Scoped().FirstOrDefaultAsync(x => x.Id == id);
        return c is null ? NotFound(NotFoundProblem("Customer")) : Ok(ToDto(c));
    }

    // POST /api/customers -> 201, 400 (validation), 403 (assigning to another user), 409 (duplicate)
    [HttpPost]
    [Consumes("application/json")]
    public async Task<ActionResult<CustomerDto>> Create(CustomerWriteDto dto)
    {
        if (dto is null) return ValidationProblem(ModelState);   // unreadable / malformed JSON body
        if (IsForbiddenAssignment(dto.AssignedUserId)) return Forbidden("You can only create customers assigned to yourself.");
        var customer = new Customer { CustomerCode = "TMP-" + Guid.NewGuid().ToString("N")[..16], CreatedDate = DateTime.UtcNow, CreatedBy = UserName };
        var error = await ApplyAsync(dto, customer, existingId: null);
        if (error is not null) return error;

        Db.Customers.Add(customer);
        if (!await TrySaveAsync()) return DuplicateConflict("A customer with the same email or phone was saved at the same time.");
        customer.CustomerCode = $"CUST-{customer.Id:D5}";
        Db.Audit(UserName, "Create", nameof(Customer), customer.Id, "via API", newValue: AuditExtensions.Snapshot(customer.AuditFields()));
        await Db.SaveChangesAsync();
        await Db.Entry(customer).Reference(c => c.AssignedUser).LoadAsync();
        return CreatedAtAction(nameof(Get), new { id = customer.Id }, ToDto(customer));
    }

    // PUT /api/customers/5 -> 200, 400, 403, 404, 409
    [HttpPut("{id:int}")]
    [Consumes("application/json")]
    public async Task<ActionResult<CustomerDto>> Update(int id, CustomerWriteDto dto)
    {
        if (dto is null) return ValidationProblem(ModelState);   // unreadable / malformed JSON body
        var customer = await Scoped().FirstOrDefaultAsync(x => x.Id == id);
        if (customer is null) return NotFound(NotFoundProblem("Customer"));
        if (IsForbiddenAssignment(dto.AssignedUserId)) return Forbidden("You cannot reassign customers.");

        var oldValue = AuditExtensions.Snapshot(customer.AuditFields());
        var error = await ApplyAsync(dto, customer, existingId: id);
        if (error is not null) return error;

        Db.Audit(UserName, "Update", nameof(Customer), customer.Id, "via API", oldValue, AuditExtensions.Snapshot(customer.AuditFields()));
        if (!await TrySaveAsync()) return DuplicateConflict("A customer with the same email or phone was saved at the same time.");
        await Db.Entry(customer).Reference(c => c.AssignedUser).LoadAsync();
        return Ok(ToDto(customer));
    }

    // DELETE /api/customers/5 -> safe deactivation (Status = Inactive): 204 or 404
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int id)
    {
        var customer = await Scoped().FirstOrDefaultAsync(x => x.Id == id);
        if (customer is null) return NotFound(NotFoundProblem("Customer"));
        var oldStatus = customer.Status;
        customer.Status = CustomerStatus.Inactive;
        Db.Audit(UserName, "Deactivate", nameof(Customer), customer.Id, "via API", $"Status={oldStatus}", "Status=Inactive");
        await Db.SaveChangesAsync();
        return NoContent();
    }

    private ObjectResult DuplicateConflict(string detail) =>
        Conflict(new ProblemDetails { Title = "Duplicate customer", Status = 409, Detail = detail });

    // Shared rules (CrmRules) -> 400; duplicates -> 409; on success copies the payload onto the entity.
    private async Task<ActionResult?> ApplyAsync(CustomerWriteDto dto, Customer target, int? existingId)
    {
        var candidate = new Customer
        {
            CustomerName = dto.CustomerName, Email = dto.Email, Phone = dto.Phone, CompanyName = dto.CompanyName,
            Address = dto.Address, City = dto.City, State = dto.State, Status = dto.Status
        };
        CrmRules.ValidateCustomer(candidate, ModelState);
        var assignee = await Scope.ResolveAssigneeAsync(dto.AssignedUserId, existingId is null ? null : target.AssignedUserId, ModelState);
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var conflicts = await CrmRules.FindCustomerConflictsAsync(Db, candidate.Email, candidate.Phone, existingId);
        if (conflicts.Count > 0) return DuplicateConflict(string.Join(" ", conflicts.Select(c => c.Message)));

        target.CustomerName = candidate.CustomerName;
        target.Email = candidate.Email;
        target.Phone = candidate.Phone;
        target.CompanyName = candidate.CompanyName;
        target.Address = candidate.Address;
        target.City = candidate.City;
        target.State = candidate.State;
        target.Status = candidate.Status;
        target.AssignedUserId = assignee;
        return null;
    }

    private async Task<bool> TrySaveAsync()
    {
        try { await Db.SaveChangesAsync(); return true; }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true)
        {
            Db.ChangeTracker.Clear();
            return false;
        }
    }
}

[Route("api/leads")]
public class LeadsApiController(ApplicationDbContext db, UserManager<IdentityUser> userManager, IConfiguration config) : CrmApiController(db, userManager)
{
    private IQueryable<Lead> Scoped() => Scope.Scoped(Db.Leads).Include(l => l.AssignedUser);

    private static LeadDto ToDto(Lead l) => new(l.Id, l.LeadCode, l.LeadName, l.Email, l.Phone, l.CompanyName, l.Source,
        l.Status, l.ExpectedValue, l.CreatedDate, l.AssignedUser?.UserName, l.ConvertedCustomerId);

    // GET /api/leads?status=
    [HttpGet]
    public async Task<ActionResult<IEnumerable<LeadDto>>> GetAll(string? status)
    {
        var q = Scoped();
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(l => l.Status == status);
        var list = await q.OrderByDescending(l => l.CreatedDate).ToListAsync();
        return Ok(list.Select(ToDto));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<LeadDto>> Get(int id)
    {
        var l = await Scoped().FirstOrDefaultAsync(x => x.Id == id);
        return l is null ? NotFound(NotFoundProblem("Lead")) : Ok(ToDto(l));
    }

    // POST /api/leads -> 201, 400, 403
    [HttpPost]
    [Consumes("application/json")]
    public async Task<ActionResult<LeadDto>> Create(LeadWriteDto dto)
    {
        if (dto is null) return ValidationProblem(ModelState);   // unreadable / malformed JSON body
        if (IsForbiddenAssignment(dto.AssignedUserId)) return Forbidden("You can only create leads assigned to yourself.");
        var lead = new Lead
        {
            LeadName = dto.LeadName, Email = dto.Email, Phone = dto.Phone, CompanyName = dto.CompanyName,
            Source = dto.Source, Status = dto.Status, ExpectedValue = dto.ExpectedValue
        };
        CrmRules.ValidateLead(lead, config.GetValue<decimal?>("Crm:MaxLeadExpectedValue") ?? CrmRules.DefaultMaxLeadValue, ModelState);
        if (lead.Status == "Converted")
            ModelState.AddModelError(nameof(dto.Status), "Leads are converted through the Convert workflow, not created as Converted.");
        lead.AssignedUserId = await Scope.ResolveAssigneeAsync(dto.AssignedUserId, null, ModelState);
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        lead.LeadCode = "TMP-" + Guid.NewGuid().ToString("N")[..16];
        lead.CreatedDate = DateTime.UtcNow;
        lead.CreatedBy = UserName;
        Db.Leads.Add(lead);
        await Db.SaveChangesAsync();
        lead.LeadCode = $"LEAD-{lead.Id:D5}";
        Db.Audit(UserName, "Create", nameof(Lead), lead.Id, "via API", newValue: AuditExtensions.Snapshot(lead.AuditFields()));
        await Db.SaveChangesAsync();
        await Db.Entry(lead).Reference(l => l.AssignedUser).LoadAsync();
        return CreatedAtAction(nameof(Get), new { id = lead.Id }, ToDto(lead));
    }
}

[Route("api/opportunities")]
public class OpportunitiesApiController(ApplicationDbContext db, UserManager<IdentityUser> userManager) : CrmApiController(db, userManager)
{
    private IQueryable<Opportunity> Scoped() => Scope.Scoped(Db.Opportunities).Include(o => o.Customer).Include(o => o.AssignedUser);

    private static OpportunityDto ToDto(Opportunity o) => new(o.Id, o.OpportunityName, o.CustomerId, o.Customer?.CustomerName, o.LeadId,
        o.Amount, o.Stage, o.Probability, o.ExpectedCloseDate, o.Status, o.CreatedDate, o.AssignedUser?.UserName);

    // GET /api/opportunities?stage=&status=
    [HttpGet]
    public async Task<ActionResult<IEnumerable<OpportunityDto>>> GetAll(string? stage, string? status)
    {
        var q = Scoped();
        if (!string.IsNullOrWhiteSpace(stage)) q = q.Where(o => o.Stage == stage);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(o => o.Status == status);
        var list = await q.OrderBy(o => o.ExpectedCloseDate).ToListAsync();
        return Ok(list.Select(ToDto));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<OpportunityDto>> Get(int id)
    {
        var o = await Scoped().FirstOrDefaultAsync(x => x.Id == id);
        return o is null ? NotFound(NotFoundProblem("Opportunity")) : Ok(ToDto(o));
    }

    // POST /api/opportunities -> 201, 400 (Section 17.7 rules, bad references), 403
    [HttpPost]
    [Consumes("application/json")]
    public async Task<ActionResult<OpportunityDto>> Create(OpportunityWriteDto dto)
    {
        if (dto is null) return ValidationProblem(ModelState);   // unreadable / malformed JSON body
        if (IsForbiddenAssignment(dto.AssignedUserId)) return Forbidden("You can only create opportunities assigned to yourself.");
        var opp = new Opportunity
        {
            OpportunityName = dto.OpportunityName, CustomerId = dto.CustomerId, LeadId = dto.LeadId,
            Amount = dto.Amount ?? 0, Stage = dto.Stage, Probability = dto.Probability ?? 0,
            ExpectedCloseDate = dto.ExpectedCloseDate?.Date ?? DateTime.Today, Status = dto.Status
        };
        CrmRules.ValidateOpportunity(opp, ModelState);
        opp.AssignedUserId = await Scope.ResolveAssigneeAsync(dto.AssignedUserId, null, ModelState);
        await Scope.CheckRelatedAsync(opp.CustomerId, opp.LeadId, null, opp.AssignedUserId, ModelState);
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        opp.CreatedDate = DateTime.UtcNow;
        opp.CreatedBy = UserName;
        Db.Opportunities.Add(opp);
        await Db.SaveChangesAsync();
        Db.Audit(UserName, "Create", nameof(Opportunity), opp.Id, "via API", newValue: AuditExtensions.Snapshot(opp.AuditFields()));
        await Db.SaveChangesAsync();
        await Db.Entry(opp).Reference(o => o.Customer).LoadAsync();
        await Db.Entry(opp).Reference(o => o.AssignedUser).LoadAsync();
        return CreatedAtAction(nameof(Get), new { id = opp.Id }, ToDto(opp));
    }
}
