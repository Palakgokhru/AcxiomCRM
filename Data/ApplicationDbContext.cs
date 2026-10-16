using AcxiomCRM.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, IHttpContextAccessor? httpContextAccessor = null)
    : IdentityDbContext(options)
{
    // Used by Audit() to record who did what and from where.
    public HttpContext? CurrentHttpContext => httpContextAccessor?.HttpContext;

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<Opportunity> Opportunities => Set<Opportunity>();
    public DbSet<FollowUp> FollowUps => Set<FollowUp>();
    public DbSet<Activity> Activities => Set<Activity>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Customer>(c =>
        {
            // Database-level duplicate protection backing the controller checks.
            c.HasIndex(x => x.CustomerCode).IsUnique();
            c.HasIndex(x => x.Email).IsUnique();
            c.HasIndex(x => x.Phone).IsUnique();
            // Name/company is deliberately NOT unique: two different people can share a name at one
            // company. Normalised email and phone are the business identifiers.
            c.HasIndex(x => x.AssignedUserId);

            c.HasOne(x => x.AssignedUser).WithMany()
                .HasForeignKey(x => x.AssignedUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<AuditLog>().HasIndex(x => x.CreatedDate);

        builder.Entity<Lead>(l =>
        {
            l.HasIndex(x => x.LeadCode).IsUnique();
            l.HasIndex(x => x.AssignedUserId);
            l.HasOne(x => x.AssignedUser).WithMany().HasForeignKey(x => x.AssignedUserId).OnDelete(DeleteBehavior.SetNull);
            l.HasOne(x => x.ConvertedCustomer).WithMany().HasForeignKey(x => x.ConvertedCustomerId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<Opportunity>(o =>
        {
            o.HasIndex(x => x.AssignedUserId);
            // A customer with opportunities cannot be hard-deleted (checked in CustomersController).
            o.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            o.HasOne(x => x.Lead).WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.SetNull);
            o.HasOne(x => x.AssignedUser).WithMany().HasForeignKey(x => x.AssignedUserId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<FollowUp>(f =>
        {
            f.HasIndex(x => new { x.Status, x.FollowUpDate });
            f.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.SetNull);
            f.HasOne(x => x.Lead).WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.SetNull);
            f.HasOne(x => x.Opportunity).WithMany().HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.SetNull);
            f.HasOne(x => x.AssignedUser).WithMany().HasForeignKey(x => x.AssignedUserId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<Activity>(a =>
        {
            a.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.SetNull);
            a.HasOne(x => x.Lead).WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.SetNull);
            a.HasOne(x => x.Opportunity).WithMany().HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.SetNull);
            a.HasOne(x => x.AssignedUser).WithMany().HasForeignKey(x => x.AssignedUserId).OnDelete(DeleteBehavior.SetNull);
        });
    }
}

public static class AuditExtensions
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    // JSON snapshot of the business fields of a record (callers pass only safe fields).
    public static string Snapshot(object values) => System.Text.Json.JsonSerializer.Serialize(values, JsonOptions);

    // Queues an audit row; it is saved with the caller's next SaveChangesAsync.
    // UserId and IP address are taken from the current request when there is one.
    public static void Audit(this ApplicationDbContext db, string? userName, string action, string entity, object entityId,
        string details = "", string? oldValue = null, string? newValue = null)
    {
        var http = db.CurrentHttpContext;
        static string? Cut(string? v, int max) => v is null ? null : v.Length > max ? v[..max] : v;
        db.AuditLogs.Add(new AuditLog
        {
            CreatedDate = DateTime.UtcNow,
            UserId = http?.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
            UserName = userName ?? http?.User.Identity?.Name ?? "(anonymous)",
            Action = action,
            EntityName = entity,
            RecordId = entityId.ToString() ?? string.Empty,
            Details = Cut(details, 1000)!,
            OldValue = Cut(oldValue, 4000),
            NewValue = Cut(newValue, 4000),
            IpAddress = http?.Connection.RemoteIpAddress?.ToString()
        });
    }

    public static object AuditFields(this Customer c) => new
    {
        c.CustomerCode, c.CustomerName, c.Email, c.Phone, c.CompanyName, c.Address, c.City, c.State, c.Status, c.AssignedUserId
    };

    public static object AuditFields(this Lead l) => new
    {
        l.LeadCode, l.LeadName, l.Email, l.Phone, l.CompanyName, l.Source, l.Status, l.ExpectedValue, l.AssignedUserId
    };

    public static object AuditFields(this Opportunity o) => new
    {
        o.OpportunityName, o.CustomerId, o.LeadId, o.Amount, o.Stage, o.Probability,
        ExpectedCloseDate = o.ExpectedCloseDate.ToString("yyyy-MM-dd"), o.Status, o.AssignedUserId
    };
}
