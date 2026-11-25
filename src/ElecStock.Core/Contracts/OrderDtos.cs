namespace ElecStock.Contracts;

public sealed class OrderSummary
{
    public int Id { get; init; }
    public string Supplier { get; init; } = "";
    public string OrderNumber { get; init; } = "";
    public OrderStatus Status { get; init; }
    public DateOnly? OrderedAt { get; init; }
    public DateOnly? ExpectedAt { get; init; }
    public DateOnly? ReceivedAt { get; init; }
    public int LineCount { get; init; }
    /// <summary>Lines total plus shipping, when every line has a price.</summary>
    public decimal? Total { get; init; }
    public string Currency { get; init; } = "EUR";
    public DateTimeOffset CreatedAt { get; init; }

    public string Title => Supplier.Length > 0 ? Supplier : "No supplier yet";
}

public sealed class OrderDetail
{
    public int Id { get; init; }
    public string Supplier { get; init; } = "";
    public string OrderNumber { get; init; } = "";
    public string Url { get; init; } = "";
    public OrderStatus Status { get; init; }
    public DateOnly? OrderedAt { get; init; }
    public DateOnly? ExpectedAt { get; init; }
    public DateOnly? ReceivedAt { get; init; }
    public string Notes { get; init; } = "";
    public string Currency { get; init; } = "EUR";
    public decimal? Shipping { get; init; }
    public decimal? Total { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public List<OrderLineDto> Lines { get; init; } = [];

    public string Title => Supplier.Length > 0 ? Supplier : "No supplier yet";
}

public sealed class OrderLineDto
{
    public int Id { get; init; }
    public int PartId { get; init; }
    public string Ref { get; init; } = "";
    public string Category { get; init; } = "";
    public string Designation { get; init; } = "";
    public int Qty { get; init; }
    public decimal? UnitPrice { get; init; }
    public int? ReceivedQty { get; init; }
    public string Notes { get; init; } = "";
    /// <summary>Current stock quantity of the part (raw).</summary>
    public string PartQty { get; init; } = "";
    public string? CoverThumbUrl { get; init; }
}

public sealed class OrderEdit
{
    public int? Id { get; set; }
    public string Supplier { get; set; } = "";
    public string OrderNumber { get; set; } = "";
    public string Url { get; set; } = "";
    public OrderStatus Status { get; set; } = OrderStatus.Draft;
    public DateOnly? OrderedAt { get; set; }
    public DateOnly? ExpectedAt { get; set; }
    public string Notes { get; set; } = "";
    public string Currency { get; set; } = "EUR";
    public decimal? Shipping { get; set; }
}

public sealed class OrderLineEdit
{
    public int? Id { get; set; }
    public int OrderId { get; set; }
    public int PartId { get; set; }
    public int Qty { get; set; } = 1;
    public decimal? UnitPrice { get; set; }
    public string Notes { get; set; } = "";
}

/// <summary>Adds a part to an order: the given one, else the draft of that supplier, else a new draft.</summary>
public sealed class AddToOrder
{
    public int PartId { get; set; }
    public int Qty { get; set; } = 1;
    public int? OrderId { get; set; }
    public string? Supplier { get; set; }
    public decimal? UnitPrice { get; set; }
    public string Notes { get; set; } = "";
}

/// <summary>Quantities actually received per line; the stock is incremented by them.</summary>
public sealed class ReceiveOrder
{
    public int OrderId { get; set; }
    public List<ReceiveLine> Lines { get; set; } = [];
}

public sealed class ReceiveLine
{
    public int LineId { get; set; }
    public int Qty { get; set; }
}

public sealed class ShoppingList
{
    public List<ShoppingItem> Items { get; init; } = [];
    public List<OrderSummary> OpenOrders { get; init; } = [];
}

public sealed class ShoppingItem
{
    public int PartId { get; init; }
    public string Ref { get; init; } = "";
    public string Designation { get; init; } = "";
    public string Category { get; init; } = "";
    public string Qty { get; init; } = "";
    public int Reserved { get; init; }
    /// <summary>Missing for the reservations, before orders.</summary>
    public int Shortage { get; init; }
    public ShoppingReason Reason { get; init; }
    public int OnOrder { get; init; }
    public int Planned { get; init; }
    /// <summary>Still missing once the placed and planned orders arrive.</summary>
    public int ToBuy => Math.Max(0, Shortage - OnOrder - Planned);
    public List<string> NeededBy { get; init; } = [];
    public string LastSupplier { get; init; } = "";
    public decimal? LastUnitPrice { get; init; }
    public string Currency { get; init; } = "EUR";
    public string? CoverThumbUrl { get; init; }
}
