using AcxiomCRM.Models;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Data;

/// <summary>
/// Section 17.7 business rules in one place, used by both the MVC controllers and the REST API so
/// the two can never drift apart. Each method normalises the record and adds errors to ModelState;
/// duplicate checks return their conflicts separately so the API can answer 409 instead of 400.
/// </summary>
public static class CrmRules
{
    public const decimal DefaultMaxLeadValue = 100_000_000m;

    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string NormalizeEmail(string? email) => email?.Trim().ToLowerInvariant() ?? string.Empty;

    // Checks 7-15 digits and returns the normalised phone, or null (with an error) if invalid.
    private static string? CheckPhone(string? phone, string field, ModelStateDictionary ms, bool required)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            if (required) ms.AddModelError(field, "Phone is required.");
            return null;
        }
        if (ms.GetFieldValidationState(field) == ModelValidationState.Invalid) return phone;   // annotation already failed
        if (!System.Text.RegularExpressions.Regex.IsMatch(phone.Trim(), PhoneFormat.Pattern))
        {
            ms.AddModelError(field, PhoneFormat.Message);
            return phone;
        }
        var digits = phone.Count(char.IsDigit);
        if (digits < 7 || digits > 15)
        {
            ms.AddModelError(field, "Phone number must contain 7 to 15 digits.");
            return phone;
        }
        return PhoneFormat.Normalize(phone);
    }

    private static void CheckAllowed(string field, string? value, string[] allowed, ModelStateDictionary ms)
    {
        if (value is null || !allowed.Contains(value))
            ms.AddModelError(field, $"Choose a valid {field.ToLower()}: {string.Join(", ", allowed)}.");
    }

    private static void CheckEmail(string? email, string field, ModelStateDictionary ms, bool required)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            if (required) ms.AddModelError(field, "Email is required.");
            return;
        }
        if (ms.GetFieldValidationState(field) != ModelValidationState.Invalid
            && !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email))
            ms.AddModelError(field, "Enter a valid email address.");
    }

    // ---- Customers ----------------------------------------------------------------

    public static void ValidateCustomer(Customer c, ModelStateDictionary ms)
    {
        c.CustomerName = c.CustomerName?.Trim() ?? string.Empty;
        if (c.CustomerName.Length == 0 && ms.GetFieldValidationState(nameof(Customer.CustomerName)) != ModelValidationState.Invalid)
            ms.AddModelError(nameof(Customer.CustomerName), "Customer name is required.");
        CheckEmail(c.Email, nameof(Customer.Email), ms, required: true);
        c.Email = NormalizeEmail(c.Email);
        c.Phone = CheckPhone(c.Phone, nameof(Customer.Phone), ms, required: true) ?? string.Empty;
        c.CompanyName = Clean(c.CompanyName);
        c.Address = Clean(c.Address);
        c.City = Clean(c.City);
        c.State = Clean(c.State);
        CheckAllowed(nameof(Customer.Status), c.Status, CustomerStatus.All, ms);
    }

    /// <summary>
    /// Duplicate check on the business identifiers (normalised email and phone) across ALL customers,
    /// not just the caller's scope, excluding the record being edited. Returns (field, message) pairs.
    /// </summary>
    public static async Task<List<(string Field, string Message)>> FindCustomerConflictsAsync(
        ApplicationDbContext db, string email, string phone, int? existingId)
    {
        var conflicts = new List<(string, string)>();
        var others = db.Customers.Where(x => existingId == null || x.Id != existingId);
        if (email.Length > 0 && await others.AnyAsync(x => x.Email == email))
            conflicts.Add((nameof(Customer.Email), "A customer with this email already exists."));
        if (phone.Length > 0 && await others.AnyAsync(x => x.Phone == phone))
            conflicts.Add((nameof(Customer.Phone), "A customer with this phone number already exists."));
        return conflicts;
    }

    // ---- Leads ----------------------------------------------------------------------

    public static void ValidateLead(Lead l, decimal maxExpectedValue, ModelStateDictionary ms)
    {
        l.LeadName = l.LeadName?.Trim() ?? string.Empty;
        if (l.LeadName.Length == 0 && ms.GetFieldValidationState(nameof(Lead.LeadName)) != ModelValidationState.Invalid)
            ms.AddModelError(nameof(Lead.LeadName), "Lead name is required.");
        CheckEmail(l.Email, nameof(Lead.Email), ms, required: false);
        l.Email = Clean(l.Email)?.ToLowerInvariant();
        l.Phone = CheckPhone(l.Phone, nameof(Lead.Phone), ms, required: false);
        l.CompanyName = Clean(l.CompanyName);
        if (l.ExpectedValue < 0)
            ms.AddModelError(nameof(Lead.ExpectedValue), "Expected value cannot be negative.");
        else if (l.ExpectedValue > maxExpectedValue)
            ms.AddModelError(nameof(Lead.ExpectedValue), $"Expected value cannot exceed {maxExpectedValue:N0}.");
        CheckAllowed(nameof(Lead.Status), l.Status, LeadStatus.All, ms);
        CheckAllowed(nameof(Lead.Source), l.Source, LeadSources.All, ms);
    }

    // ---- Opportunities --------------------------------------------------------------

    public static void ValidateOpportunity(Opportunity o, ModelStateDictionary ms)
    {
        o.OpportunityName = o.OpportunityName?.Trim() ?? string.Empty;
        if (o.OpportunityName.Length == 0 && ms.GetFieldValidationState(nameof(Opportunity.OpportunityName)) != ModelValidationState.Invalid)
            ms.AddModelError(nameof(Opportunity.OpportunityName), "Opportunity name is required.");
        CheckAllowed(nameof(Opportunity.Stage), o.Stage, OpportunityStage.All, ms);
        CheckAllowed(nameof(Opportunity.Status), o.Status, OpportunityStatus.All, ms);

        if (o.Amount < 0)
            ms.AddModelError(nameof(Opportunity.Amount), "Amount cannot be negative.");
        if (o.Probability < 0 || o.Probability > 100)
            ms.AddModelError(nameof(Opportunity.Probability), "Probability must be between 0 and 100.");

        if (o.Status == OpportunityStatus.Open)
        {
            if (o.Amount == 0)   // negative amounts already have their own error above
                ms.AddModelError(nameof(Opportunity.Amount), "An active (Open) opportunity must have an amount greater than 0.");
            if (o.ExpectedCloseDate.Date < DateTime.Today)
                ms.AddModelError(nameof(Opportunity.ExpectedCloseDate), "An active (Open) opportunity cannot have an expected close date in the past.");
            if (o.Stage is OpportunityStage.Won or OpportunityStage.Lost)
                ms.AddModelError(nameof(Opportunity.Status), "A Won or Lost opportunity must have status Closed.");
        }
        if (o.CustomerId is null && ms.GetFieldValidationState(nameof(Opportunity.CustomerId)) != ModelValidationState.Invalid)
            ms.AddModelError(nameof(Opportunity.CustomerId), "Select a customer.");
    }

    // ---- Follow-ups -----------------------------------------------------------------

    /// <param name="historical">True only for the Admin/Manager historical-entry flow.</param>
    public static void ValidateFollowUp(FollowUp f, bool historical, ModelStateDictionary ms)
    {
        CheckAllowed(nameof(FollowUp.FollowUpType), f.FollowUpType, FollowUpTypes.All, ms);
        CheckAllowed(nameof(FollowUp.Status), f.Status, FollowUpStatus.All, ms);
        f.Remarks = Clean(f.Remarks);

        if (f.Status == FollowUpStatus.Planned && f.FollowUpDate.Date < DateTime.Today)
            ms.AddModelError(nameof(FollowUp.FollowUpDate), "A planned follow-up cannot be scheduled before today.");
        if (historical && f.FollowUpDate.Date > DateTime.Today)
            ms.AddModelError(nameof(FollowUp.FollowUpDate), "A historical follow-up must be dated today or earlier.");
        if (f.CustomerId is null && f.LeadId is null && f.OpportunityId is null)
            ms.AddModelError(string.Empty, "Link the follow-up to a customer, lead or opportunity.");
    }
}
