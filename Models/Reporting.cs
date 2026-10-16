namespace AcxiomCRM.Models;

/// <summary>
/// A reporting period from a preset (Today / This Week / This Month / Custom / All) or explicit dates.
/// To is inclusive (whole day).
/// </summary>
public record DateRange(string Preset, DateTime? From, DateTime? To)
{
    public static readonly (string Key, string Label)[] Presets =
        [("all", "All time"), ("today", "Today"), ("week", "This week"), ("month", "This month"), ("custom", "Custom range")];

    public static DateRange Resolve(string? preset, DateTime? from, DateTime? to)
    {
        var today = DateTime.Today;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));   // Monday
        return (preset ?? (from is not null || to is not null ? "custom" : "all")) switch
        {
            "today" => new("today", today, today),
            "week" => new("week", weekStart, weekStart.AddDays(6)),
            "month" => new("month", new DateTime(today.Year, today.Month, 1), new DateTime(today.Year, today.Month, 1).AddMonths(1).AddDays(-1)),
            "custom" => new("custom", from?.Date, to?.Date),
            _ => new("all", null, null)
        };
    }

    // Exclusive upper bound for "<" comparisons on DateTime columns that may carry a time part.
    public DateTime? ToExclusive => To?.AddDays(1);

    public string Label => Preset switch
    {
        "all" => "All time",
        _ when From is null && To is null => "All time",
        _ => $"{From?.ToString("dd MMM yyyy") ?? "…"} – {To?.ToString("dd MMM yyyy") ?? "…"}"
    };
}

public class ReportTable
{
    public string Title { get; set; } = string.Empty;
    public List<string> Columns { get; set; } = [];
    public List<string[]> Rows { get; set; } = [];
    // Indexes of right-aligned (numeric) columns.
    public HashSet<int> NumericColumns { get; set; } = [];
}

public record ReportFilter(string Name, string Label, IReadOnlyList<(string Value, string Text)> Options, string? Selected);

public class ReportViewModel
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateRange Range { get; set; } = new("all", null, null);
    public string DateFieldLabel { get; set; } = "Created date";
    public List<ReportFilter> Filters { get; set; } = [];
    public List<(string Label, string Value)> Totals { get; set; } = [];
    public List<ReportTable> Tables { get; set; } = [];
}
