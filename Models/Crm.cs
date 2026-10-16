using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace AcxiomCRM.Models;

// Records that are owned by a user; SalesExecutives only see rows where AssignedUserId is theirs.
public interface IAssignable
{
    string? AssignedUserId { get; set; }
}

public static class LeadStatus
{
    public static readonly string[] All = ["New", "Contacted", "Qualified", "Unqualified", "Converted", "Lost"];
}

public static class OpportunityStage
{
    public const string Won = "Won";
    public const string Lost = "Lost";
    public static readonly string[] All = ["Qualification", "Proposal", "Negotiation", Won, Lost];
}

public static class OpportunityStatus
{
    public const string Open = "Open";       // "active" opportunity
    public const string Closed = "Closed";
    public static readonly string[] All = [Open, Closed];
}

public static class FollowUpStatus
{
    public const string Planned = "Planned";
    public const string Completed = "Completed";
    public const string Missed = "Missed";
    public const string Cancelled = "Cancelled";
    public static readonly string[] All = [Planned, Completed, Missed, Cancelled];
}

public static class FollowUpTypes
{
    public static readonly string[] All = ["Call", "Email", "Meeting", "Visit"];
}

public static class ActivityTypes
{
    public static readonly string[] All = ["Call", "Meeting", "Email", "Task"];
}

public static class ActivityStatus
{
    public static readonly string[] All = ["Planned", "Completed", "Cancelled"];
}

public static class LeadSources
{
    public static readonly string[] All = ["Website", "Referral", "Phone", "Email", "Event", "Other"];
}

public static class PhoneFormat
{
    public const string Pattern = @"^\+?[0-9][0-9\s\-()]{6,19}$";
    public const string Message = "Enter a valid phone number (7-15 digits, optional leading +).";

    // "+1 (555) 123-4567" -> "+15551234567"
    public static string Normalize(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return string.Empty;
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return phone.TrimStart().StartsWith('+') && digits.Length > 0 ? "+" + digits : digits;
    }
}

public class Lead : IAssignable
{
    [Display(Name = "Lead Id")]
    public int Id { get; set; }

    [Display(Name = "Lead Code"), StringLength(20)]
    public string LeadCode { get; set; } = string.Empty;

    [Required, StringLength(100, MinimumLength = 2), Display(Name = "Lead Name")]
    public string LeadName { get; set; } = string.Empty;

    [StringLength(150), EmailAddress]
    public string? Email { get; set; }

    [StringLength(20), RegularExpression(PhoneFormat.Pattern, ErrorMessage = PhoneFormat.Message)]
    public string? Phone { get; set; }

    [StringLength(150), Display(Name = "Company Name")]
    public string? CompanyName { get; set; }

    [Required, StringLength(30)]
    public string Source { get; set; } = "Website";

    [Required, StringLength(20)]
    public string Status { get; set; } = "New";

    [Display(Name = "Expected Value"), DataType(DataType.Currency)]
    [Range(typeof(decimal), "0", "999999999999", ErrorMessage = "Expected value cannot be negative.")]
    public decimal ExpectedValue { get; set; }

    [Display(Name = "Created Date")]
    public DateTime CreatedDate { get; set; }

    [Display(Name = "Created By"), StringLength(256)]
    public string CreatedBy { get; set; } = string.Empty;

    [Display(Name = "Assigned To")]
    public string? AssignedUserId { get; set; }
    public IdentityUser? AssignedUser { get; set; }
}

public class Opportunity : IAssignable
{
    public int Id { get; set; }

    [Required, StringLength(150, MinimumLength = 2), Display(Name = "Opportunity Name")]
    public string OpportunityName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Select a customer."), Display(Name = "Customer")]
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    [Display(Name = "Lead")]
    public int? LeadId { get; set; }
    public Lead? Lead { get; set; }

    [DataType(DataType.Currency)]
    [Range(typeof(decimal), "0", "999999999999", ErrorMessage = "Amount cannot be negative.")]
    public decimal Amount { get; set; }

    [Required, StringLength(20)]
    public string Stage { get; set; } = "Qualification";

    [Range(0, 100, ErrorMessage = "Probability must be between 0 and 100.")]
    public int Probability { get; set; }

    [Display(Name = "Expected Close Date"), DataType(DataType.Date)]
    public DateTime ExpectedCloseDate { get; set; } = DateTime.Today.AddDays(30);

    [Required, StringLength(20)]
    public string Status { get; set; } = OpportunityStatus.Open;

    [Display(Name = "Created Date")]
    public DateTime CreatedDate { get; set; }

    [Display(Name = "Created By"), StringLength(256)]
    public string CreatedBy { get; set; } = string.Empty;

    [Display(Name = "Assigned To")]
    public string? AssignedUserId { get; set; }
    public IdentityUser? AssignedUser { get; set; }
}

public class FollowUp : IAssignable
{
    public int Id { get; set; }

    [Display(Name = "Customer")] public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    [Display(Name = "Lead")] public int? LeadId { get; set; }
    public Lead? Lead { get; set; }
    [Display(Name = "Opportunity")] public int? OpportunityId { get; set; }
    public Opportunity? Opportunity { get; set; }

    [Display(Name = "Follow-up Date"), DataType(DataType.Date)]
    public DateTime FollowUpDate { get; set; } = DateTime.Today;

    [Required, StringLength(20), Display(Name = "Type")]
    public string FollowUpType { get; set; } = "Call";

    [StringLength(1000)]
    public string? Remarks { get; set; }

    [Required, StringLength(20)]
    public string Status { get; set; } = FollowUpStatus.Planned;

    public DateTime CreatedDate { get; set; }
    [StringLength(256)] public string CreatedBy { get; set; } = string.Empty;

    [Display(Name = "Assigned To")]
    public string? AssignedUserId { get; set; }
    public IdentityUser? AssignedUser { get; set; }
}

public class Activity : IAssignable
{
    public int Id { get; set; }

    [Required, StringLength(20), Display(Name = "Type")]
    public string ActivityType { get; set; } = "Call";

    [Required, StringLength(200, MinimumLength = 2)]
    public string Subject { get; set; } = string.Empty;

    [Display(Name = "Activity Date"), DataType(DataType.Date)]
    public DateTime ActivityDate { get; set; } = DateTime.Today;

    [Display(Name = "Customer")] public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    [Display(Name = "Lead")] public int? LeadId { get; set; }
    public Lead? Lead { get; set; }
    [Display(Name = "Opportunity")] public int? OpportunityId { get; set; }
    public Opportunity? Opportunity { get; set; }

    [Required, StringLength(20)]
    public string Status { get; set; } = "Planned";

    public DateTime CreatedDate { get; set; }
    [StringLength(256)] public string CreatedBy { get; set; } = string.Empty;

    [Display(Name = "Assigned To")]
    public string? AssignedUserId { get; set; }
    public IdentityUser? AssignedUser { get; set; }
}
