using System.Globalization;

namespace ElecStock.Stock;

/// <summary>
/// The stock rules: available = qty − reserved, <c>10+</c> stays a lower bound, an empty qty is
/// "not counted". Used by the server to compute statuses and by the clients to word them.
/// </summary>
public static class StockMath
{
    public static StockStatus Status(Qty qty, int reserved) => qty.Kind switch
    {
        QtyKind.Exact when qty.Value - reserved < 0 => StockStatus.Short,
        QtyKind.Exact when qty.Value - reserved == 0 => StockStatus.Empty,
        QtyKind.Exact => StockStatus.Ok,
        QtyKind.AtLeast when qty.Value - reserved <= 0 => StockStatus.Check,
        QtyKind.AtLeast or QtyKind.Text => StockStatus.Ok,
        _ => StockStatus.Unknown,
    };

    /// <summary>How many are missing for the reservations (0 when covered or not countable).</summary>
    public static int Shortage(Qty qty, int reserved) =>
        qty.Kind == QtyKind.Exact && reserved > qty.Value ? reserved - qty.Value : 0;

    public static string QtyDisplay(Qty qty) => qty.Kind == QtyKind.Blank ? "?" : qty.Raw;

    /// <summary>Short badge text: <c>3</c>, <c>−2</c>, <c>8+</c>, <c>?</c>.</summary>
    public static string AvailableShort(Qty qty, int reserved) => qty.Kind switch
    {
        QtyKind.Exact => Signed(qty.Value - reserved),
        QtyKind.AtLeast => qty.Value - reserved > 0 ? $"{qty.Value - reserved}+" : "?",
        QtyKind.Text => qty.Raw,
        _ => "?",
    };

    public static string AvailableLong(Qty qty, int reserved)
    {
        var res = reserved > 0 ? $", {reserved} reserved" : "";
        return qty.Kind switch
        {
            QtyKind.Exact when qty.Value - reserved < 0 => $"Short by {reserved - qty.Value}: {qty.Value} in stock{res}",
            QtyKind.Exact when qty.Value - reserved == 0 && reserved > 0 => $"All reserved: {qty.Value} in stock{res}",
            QtyKind.Exact when qty.Value == 0 => "Out of stock",
            QtyKind.Exact => $"{qty.Value - reserved} available ({qty.Value} in stock{res})",
            QtyKind.AtLeast when qty.Value - reserved > 0 => $"At least {qty.Value - reserved} available ({qty.Raw} in stock{res})",
            QtyKind.AtLeast => $"Count needed: {qty.Raw} in stock{res}",
            QtyKind.Text => $"In stock: {qty.Raw}{res}",
            _ => $"Not counted yet{res}",
        };
    }

    public static string StatusTitle(StockStatus status, int reserved) => status switch
    {
        StockStatus.Ok => "In stock",
        StockStatus.Empty => reserved > 0 ? "All reserved" : "Out of stock",
        StockStatus.Short => "Short",
        StockStatus.Check => "Count needed",
        _ => "Not counted",
    };

    /// <summary>Quantity after a build consumed <paramref name="consumed"/>, with a remark when it needs a look.</summary>
    public static (string After, string Remark) Consume(Qty qty, int consumed) => qty.Kind switch
    {
        QtyKind.Exact when qty.Value - consumed < 0 =>
            ("0", $"Only {qty.Value} in stock for {consumed} used: set to 0, check the drawer."),
        QtyKind.Exact => ((qty.Value - consumed).ToString(CultureInfo.InvariantCulture), ""),
        QtyKind.AtLeast when consumed > 0 =>
            (qty.Raw, $"Kept as {qty.Raw}: count it if now below {qty.Value}."),
        QtyKind.AtLeast => (qty.Raw, ""),
        QtyKind.Blank => ("", "Quantity not filled in: left empty."),
        _ => (qty.Raw, $"Quantity '{qty.Raw}' is not a number: left as-is."),
    };

    /// <summary>Quantity after receiving <paramref name="received"/>: <c>3</c> + 10 = <c>13</c>, <c>10+</c> + 10 = <c>20+</c>.</summary>
    public static (string After, string Remark) Receive(Qty qty, int received) => qty.Kind switch
    {
        QtyKind.Exact => ((qty.Value + received).ToString(CultureInfo.InvariantCulture), ""),
        QtyKind.AtLeast => ($"{qty.Value + received}+", ""),
        QtyKind.Blank => (received.ToString(CultureInfo.InvariantCulture), ""),
        _ => (qty.Raw, $"Quantity '{qty.Raw}' is not a number: left as-is, count it."),
    };

    static string Signed(int v) => v < 0 ? "−" + (-v).ToString(CultureInfo.InvariantCulture) : v.ToString(CultureInfo.InvariantCulture);
}
