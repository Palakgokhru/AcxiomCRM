using AcxiomCRM.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext(options)
{
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
            c.HasIndex(x => new { x.CustomerName, x.CompanyName }).IsUnique();
            c.HasIndex(x => x.AssignedUserId);

            c.HasOne(x => x.AssignedUser).WithMany()
                .HasForeignKey(x => x.AssignedUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<AuditLog>().HasIndex(x => x.Timestamp);

        builder.Entity<Lead>(l =>
        {
            l.HasIndex(x => x.LeadCode).IsUnique();
            l.HasIndex(x => x.AssignedUserId);
            l.HasOne(x => x.AssignedUser).WithMany().HasForeignKey(x => x.AssignedUserId).OnDelete(DeleteBehavior.SetNull);
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
    // Queues an audit row; it is saved with the caller's next SaveChangesAsync.
    public static void Audit(this ApplicationDbContext db, string? userName, string action, string entity, object entityId, string details = "")
    {
        db.AuditLogs.Add(new AuditLog
        {
            Timestamp = DateTime.UtcNow,
            UserName = userName ?? "(unknown)",
            Action = action,
            Entity = entity,
            EntityId = entityId.ToString() ?? string.Empty,
            Details = details.Length > 1000 ? details[..1000] : details
        });
    }
}
