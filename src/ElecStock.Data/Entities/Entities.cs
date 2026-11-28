using ElecStock.Contracts;

namespace ElecStock.Data.Entities;

public sealed class Part
{
    public int Id { get; set; }
    public required string Ref { get; set; }
    public string Category { get; set; } = "";
    public string Designation { get; set; } = "";
    /// <summary>Raw quantity: <c>12</c>, <c>10+</c>, empty (not counted) or free text.</summary>
    public string Qty { get; set; } = "";
    public int? LocationId { get; set; }
    public Location? Location { get; set; }
    /// <summary>Where the rest of the stock is kept when the main location is full (a spare drawer).</summary>
    public int? SpareLocationId { get; set; }
    public Location? SpareLocation { get; set; }
    public string DatasheetUrl { get; set; } = "";
    /// <summary>One-line facts about the part, never project usage.</summary>
    public string Notes { get; set; } = "";
    /// <summary>Technical sheet (Markdown), null when there is none.</summary>
    public string? Sheet { get; set; }
    /// <summary>Free notes (Markdown): measurements, tests, wiring.</summary>
    public string FreeNotes { get; set; } = "";
    /// <summary>Storage path of the offline datasheet PDF.</summary>
    public string? DatasheetFile { get; set; }
    /// <summary>Out of stock and not to buy again: kept off the shopping list until it is ordered again (a reservation still shows it).</summary>
    public bool NoRestock { get; set; }
    /// <summary>KiCad symbol, <c>Device:R</c> or <c>Stock:ESP32-C3-SuperMini</c>; empty when unknown.</summary>
    public string KicadSymbol { get; set; } = "";
    /// <summary>KiCad footprint, <c>Resistor_SMD:R_0603_1608Metric</c> or <c>Stock:…</c>; empty when unknown.</summary>
    public string KicadFootprint { get; set; } = "";
    /// <summary>3D model path, only when the footprint has none: <c>${ELEC_STOCK}/KiCad/3D/…step</c>.</summary>
    public string Kicad3dModel { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<Photo> Photos { get; set; } = [];
    public List<Allocation> Allocations { get; set; } = [];
    public List<OrderLine> OrderLines { get; set; } = [];
}

public sealed class Location
{
    public int Id { get; set; }
    public required string Code { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<Part> Parts { get; set; } = [];
    public List<Part> SpareParts { get; set; } = [];
}

public sealed class Project
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string Description { get; set; } = "";
    public ProjectStatus Status { get; set; }
    public string RepoPath { get; set; } = "";
    public string Url { get; set; } = "";
    public string Notes { get; set; } = "";
    /// <summary>Storage path of the project icon (SVG, or PNG made from an image), null for the cover photo or the folder glyph.</summary>
    public string? IconPath { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<Device> Devices { get; set; } = [];
    public List<Photo> Photos { get; set; } = [];
}

public sealed class Device
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public required string Name { get; set; }
    public string Description { get; set; } = "";
    public string Path { get; set; } = "";
    public string Notes { get; set; } = "";

    public List<Revision> Revisions { get; set; } = [];
}

public sealed class Revision
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public Device Device { get; set; } = null!;
    public required string Rev { get; set; }
    public RevisionStatus Status { get; set; }
    public string Notes { get; set; } = "";
    public DateOnly? PcbOrderedAt { get; set; }
    public DateOnly? PcbReceivedAt { get; set; }
    public DateOnly? BuiltAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public List<Allocation> Allocations { get; set; } = [];
    public List<Photo> Photos { get; set; } = [];
}

/// <summary>One part reserved (or used) by a board revision.</summary>
public sealed class Allocation
{
    public int Id { get; set; }
    public int RevisionId { get; set; }
    public Revision Revision { get; set; } = null!;
    public int PartId { get; set; }
    public Part Part { get; set; } = null!;
    /// <summary>Space-separated: <c>Q201 Q202</c>.</summary>
    public string Designators { get; set; } = "";
    public int Qty { get; set; }
    public AllocationStatus Status { get; set; }
    public string Notes { get; set; } = "";
}

public sealed class Order
{
    public int Id { get; set; }
    public string Supplier { get; set; } = "";
    public string OrderNumber { get; set; } = "";
    public string Url { get; set; } = "";
    public OrderStatus Status { get; set; }
    public DateOnly? OrderedAt { get; set; }
    public DateOnly? ExpectedAt { get; set; }
    public DateOnly? ReceivedAt { get; set; }
    public string Notes { get; set; } = "";
    public string Currency { get; set; } = "EUR";
    public decimal? Shipping { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<OrderLine> Lines { get; set; } = [];
}

public sealed class OrderLine
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public int PartId { get; set; }
    public Part Part { get; set; } = null!;
    public int Qty { get; set; }
    public decimal? UnitPrice { get; set; }
    /// <summary>Set when the order is received: what actually went into the stock.</summary>
    public int? ReceivedQty { get; set; }
    public string Notes { get; set; } = "";
}

/// <summary>A photo of a part, a project or a board revision.</summary>
public sealed class Photo
{
    public int Id { get; set; }
    public int? PartId { get; set; }
    public Part? Part { get; set; }
    public int? ProjectId { get; set; }
    public Project? Project { get; set; }
    public int? RevisionId { get; set; }
    public Revision? Revision { get; set; }
    /// <summary>Original file name, as uploaded.</summary>
    public string FileName { get; set; } = "";
    /// <summary>Storage path of the image (<c>photos/…</c>).</summary>
    public required string StoragePath { get; set; }
    /// <summary>Storage path of the thumbnail (<c>thumbs/…</c>).</summary>
    public string ThumbPath { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string Caption { get; set; } = "";
    public List<Marker> Markers { get; set; } = [];
    public bool IsCover { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Numbered label pinned on a photo (X/Y as fractions of the image size), stored as JSON.</summary>
public sealed class Marker
{
    public double X { get; set; }
    public double Y { get; set; }
    public string Label { get; set; } = "";
}

/// <summary>One file of the stock KiCad library: a symbol (kept as a one-symbol library), a footprint or a 3D model.</summary>
public sealed class KicadItem
{
    public int Id { get; set; }
    public KicadItemKind Kind { get; set; }
    /// <summary>Symbol or footprint name, or the model file name with its extension; unique per kind.</summary>
    public required string Name { get; set; }
    /// <summary>Storage path of the file (<c>cad/…</c>).</summary>
    public required string StoragePath { get; set; }
    public long Size { get; set; }
    /// <summary>For a derived symbol, the symbol it extends.</summary>
    public string? Extends { get; set; }
    /// <summary>Where it comes from: a link, "drawn from the datasheet", "made with FreeCAD"…</summary>
    public string Source { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>History of the changes: who (web, app, Claude), what, and the quantity before/after.</summary>
public sealed class Activity
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public ActivitySource Source { get; set; }
    public required string Kind { get; set; }
    public string Summary { get; set; } = "";
    public int? PartId { get; set; }
    /// <summary>Kept so the history still reads once the part is deleted.</summary>
    public string PartRef { get; set; } = "";
    public int? ProjectId { get; set; }
    public int? RevisionId { get; set; }
    public int? OrderId { get; set; }
    public string? QtyBefore { get; set; }
    public string? QtyAfter { get; set; }
}
