using System.Text.Json.Serialization;
using ElecStock.Stock;
using QtyValue = ElecStock.Stock.Qty;

namespace ElecStock.Contracts;

/// <summary>One part as shown in the stock tiles and lists.</summary>
public sealed class PartSummary
{
    public int Id { get; init; }
    public string Ref { get; init; } = "";
    public string Category { get; init; } = "";
    public string Designation { get; init; } = "";
    /// <summary>Raw quantity: <c>12</c>, <c>10+</c>, empty (not counted) or free text.</summary>
    public string Qty { get; init; } = "";
    public int Reserved { get; init; }
    public int Used { get; init; }
    /// <summary>Quantity in orders placed but not received yet.</summary>
    public int OnOrder { get; init; }
    /// <summary>Quantity in draft orders (the shopping basket).</summary>
    public int Planned { get; init; }
    public StockStatus Status { get; init; }
    public int? LocationId { get; init; }
    public string LocationCode { get; init; } = "";
    public string LocationName { get; init; } = "";
    public string DatasheetUrl { get; init; } = "";
    public bool HasLocalDatasheet { get; init; }
    public bool HasSheet { get; init; }
    public bool HasFreeNotes { get; init; }
    /// <summary>One-line facts about the part (never project usage).</summary>
    public string Notes { get; init; } = "";
    public int PhotoCount { get; init; }
    public string? CoverThumbUrl { get; init; }
    /// <summary>"WeatherStation · Sensor-Board rev A" for every revision that has a line on this part.</summary>
    public List<string> Projects { get; init; } = [];
    /// <summary>Lower-case text searched by the stock filter: projects, designators, photo captions and markers.</summary>
    public string SearchText { get; init; } = "";
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonIgnore] public QtyValue ParsedQty => QtyValue.Parse(Qty);
    [JsonIgnore] public string AvailableShort => StockMath.AvailableShort(ParsedQty, Reserved);
    [JsonIgnore] public string AvailableLong => StockMath.AvailableLong(ParsedQty, Reserved);
    [JsonIgnore] public string StatusTitle => StockMath.StatusTitle(Status, Reserved);
    [JsonIgnore] public string LocationDisplay => LocationName.Length > 0 ? LocationName : LocationCode;
    [JsonIgnore] public bool NeedsBuying => Status == StockStatus.Short || (ParsedQty.Kind == QtyKind.Exact && ParsedQty.Value == 0) || Planned > 0;
}

public sealed class StockOverview
{
    public List<PartSummary> Parts { get; init; } = [];
    public List<LocationDto> Locations { get; init; } = [];
}

public sealed class PartDetail
{
    public PartSummary Part { get; init; } = new();
    /// <summary>Technical sheet (Markdown), or null when the part has none.</summary>
    public string? Sheet { get; init; }
    /// <summary>Free notes (Markdown): measurements, tests, wiring.</summary>
    public string FreeNotes { get; init; } = "";
    public string? DatasheetFileUrl { get; init; }
    public List<PhotoDto> Photos { get; init; } = [];
    public List<PartAllocation> Allocations { get; init; } = [];
    public List<PartOrderLine> OrderLines { get; init; } = [];
    public List<ActivityDto> History { get; init; } = [];
}

public sealed class PartAllocation
{
    public int Id { get; init; }
    public int RevisionId { get; init; }
    public int ProjectId { get; init; }
    public string Project { get; init; } = "";
    public string Device { get; init; } = "";
    public string Rev { get; init; } = "";
    public RevisionStatus RevisionStatus { get; init; }
    public string Designators { get; init; } = "";
    public int Qty { get; init; }
    public AllocationStatus Status { get; init; }
    public string Notes { get; init; } = "";
}

public sealed class PartOrderLine
{
    public int OrderId { get; init; }
    public int LineId { get; init; }
    public string Supplier { get; init; } = "";
    public string OrderNumber { get; init; } = "";
    public OrderStatus Status { get; init; }
    public int Qty { get; init; }
    public decimal? UnitPrice { get; init; }
    public string Currency { get; init; } = "";
    public DateOnly? OrderedAt { get; init; }
    public DateOnly? ReceivedAt { get; init; }
}

/// <summary>Create (Id null) or update a part.</summary>
public sealed class PartEdit
{
    public int? Id { get; set; }
    public string Ref { get; set; } = "";
    public string Category { get; set; } = "";
    public string Designation { get; set; } = "";
    public string Qty { get; set; } = "";
    public int? LocationId { get; set; }
    public string DatasheetUrl { get; set; } = "";
    public string Notes { get; set; } = "";
    /// <summary>Why the quantity changed (count, found more…): kept in the history.</summary>
    public string? QtyReason { get; set; }
}

public sealed class PhotoDto
{
    public int Id { get; init; }
    public string Url { get; init; } = "";
    public string ThumbUrl { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Caption { get; init; } = "";
    public List<PhotoMarker> Markers { get; init; } = [];
    public bool IsCover { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}

/// <summary>A numbered label pinned on a photo; X/Y are fractions of the image size (0..1).</summary>
public sealed class PhotoMarker
{
    public double X { get; set; }
    public double Y { get; set; }
    public string Label { get; set; } = "";
}

public sealed class PhotoEdit
{
    public string Caption { get; set; } = "";
    public List<PhotoMarker> Markers { get; set; } = [];
}
