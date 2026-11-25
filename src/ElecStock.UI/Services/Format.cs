using System.Globalization;
using ElecStock.Contracts;
using ElecStock.Stock;

namespace ElecStock.UI.Services;

/// <summary>Wording and colours shared by the pages.</summary>
public static class Format
{
    static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-GB");

    public static string Date(DateOnly? date) => date?.ToString("d MMM yyyy", En) ?? "";

    public static string Date(DateTimeOffset at) => at.ToLocalTime().ToString("d MMM yyyy", En);

    public static string DateTime(DateTimeOffset at) => at.ToLocalTime().ToString("d MMM yyyy HH:mm", En);

    public static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm", En);

    public static string Day(DateTimeOffset at)
    {
        var day = DateOnly.FromDateTime(at.ToLocalTime().DateTime);
        var today = DateOnly.FromDateTime(System.DateTime.Now);
        return day == today ? "Today" : day == today.AddDays(-1) ? "Yesterday" : day.ToString("dddd d MMMM yyyy", En);
    }

    public static string Money(decimal? value, string currency) =>
        value == null ? "" : currency.ToUpperInvariant() switch
        {
            "EUR" => value.Value.ToString("0.00", CultureInfo.InvariantCulture) + " €",
            "USD" => "$" + value.Value.ToString("0.00", CultureInfo.InvariantCulture),
            "GBP" => "£" + value.Value.ToString("0.00", CultureInfo.InvariantCulture),
            var c => value.Value.ToString("0.00", CultureInfo.InvariantCulture) + " " + c,
        };

    /// <summary>CSS class of a stock status badge.</summary>
    public static string StatusClass(StockStatus status) => status switch
    {
        StockStatus.Ok => "ok",
        StockStatus.Empty or StockStatus.Check => "warn",
        StockStatus.Short => "bad",
        _ => "muted",
    };

    public static string FlagClass(LineFlag flag) => flag switch
    {
        LineFlag.Ok => "ok",
        LineFlag.Short => "bad",
        LineFlag.OnOrder => "order",
        LineFlag.Uncounted => "warn",
        _ => "muted",
    };

    public static string FlagText(LineFlag flag) => flag switch
    {
        LineFlag.Ok => "In stock",
        LineFlag.Short => "Short",
        LineFlag.OnOrder => "On order",
        LineFlag.Uncounted => "Count needed",
        _ => "Used",
    };

    public static string Title(RevisionStatus status) => status switch
    {
        RevisionStatus.Design => "Design",
        RevisionStatus.PcbOrdered => "PCB ordered",
        RevisionStatus.PcbReceived => "PCB received",
        RevisionStatus.Built => "Built",
        _ => "Abandoned",
    };

    public static string RevisionClass(RevisionStatus status) => status switch
    {
        RevisionStatus.Design => "accent",
        RevisionStatus.PcbOrdered or RevisionStatus.PcbReceived => "order",
        RevisionStatus.Built => "ok",
        _ => "muted",
    };

    public static string ProjectClass(ProjectStatus status) => status switch
    {
        ProjectStatus.Design => "accent",
        ProjectStatus.Prototype => "order",
        ProjectStatus.Idea => "warn",
        ProjectStatus.Built => "ok",
        _ => "muted",
    };

    public static string OrderClass(OrderStatus status) => status switch
    {
        OrderStatus.Draft => "warn",
        OrderStatus.Ordered => "order",
        OrderStatus.Received => "ok",
        _ => "muted",
    };

    public static string ReasonText(ShoppingReason reason) => reason switch
    {
        ShoppingReason.Short => "Short",
        ShoppingReason.Planned => "Planned",
        _ => "Out of stock",
    };

    public static string SourceText(ActivitySource source) => source switch
    {
        ActivitySource.Mcp => "Claude",
        ActivitySource.App => "App",
        ActivitySource.Web => "Web",
        ActivitySource.Import => "Import",
        _ => "System",
    };

    /// <summary>Lines of a revision summary: "12 lines · 2 short · 1 to count".</summary>
    public static string Summary(RevisionSummary r)
    {
        var parts = new List<string> { r.LineCount == 1 ? "1 line" : $"{r.LineCount} lines" };
        if (r.ShortLines > 0) parts.Add($"{r.ShortLines} short");
        if (r.OnOrderLines > 0) parts.Add($"{r.OnOrderLines} on order");
        if (r.UncountedLines > 0) parts.Add($"{r.UncountedLines} to count");
        if (r.PhotoCount > 0) parts.Add(r.PhotoCount == 1 ? "1 photo" : $"{r.PhotoCount} photos");
        return string.Join(" · ", parts);
    }

    /// <summary>State colour of a revision: built, short, to count, all good.</summary>
    public static string StateClass(RevisionSummary r) =>
        r.Status is RevisionStatus.Built or RevisionStatus.Abandoned ? "muted"
        : r.ShortLines > 0 ? "bad"
        : r.OnOrderLines > 0 ? "order"
        : r.UncountedLines > 0 ? "warn"
        : "ok";
}
