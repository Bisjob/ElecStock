namespace ElecStock.Contracts;

public sealed class ProjectSummary
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public ProjectStatus Status { get; init; }
    /// <summary>Project icon (SVG or PNG), shown before the cover photo.</summary>
    public string? IconUrl { get; init; }
    public string? CoverThumbUrl { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public List<DeviceSummary> Devices { get; init; } = [];
}

public sealed class DeviceSummary
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public List<RevisionSummary> Revisions { get; init; } = [];
}

public sealed class RevisionSummary
{
    public int Id { get; init; }
    public string Rev { get; init; } = "";
    public RevisionStatus Status { get; init; }
    public int LineCount { get; init; }
    public int ReservedLines { get; init; }
    public int UsedLines { get; init; }
    /// <summary>Lines short and not covered by an order.</summary>
    public int ShortLines { get; init; }
    public int OnOrderLines { get; init; }
    public int UncountedLines { get; init; }
    public int PhotoCount { get; init; }
}

public sealed class ProjectDetail
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public ProjectStatus Status { get; init; }
    /// <summary>Project folder or repository path, optional, e.g. <c>~/projects/WeatherStation</c>.</summary>
    public string RepoPath { get; init; } = "";
    public string Url { get; init; } = "";
    public string Notes { get; init; } = "";
    public string? IconUrl { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public List<PhotoDto> Photos { get; init; } = [];
    public List<DeviceDetail> Devices { get; init; } = [];
}

public sealed class DeviceDetail
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>Folder of the device inside the project, e.g. <c>Elec/Devices/LedStrip2-Env-Meter</c>.</summary>
    public string Path { get; init; } = "";
    public string Notes { get; init; } = "";
    public List<RevisionSummary> Revisions { get; init; } = [];
}

public sealed class RevisionDetail
{
    public int Id { get; init; }
    public string Rev { get; init; } = "";
    public RevisionStatus Status { get; init; }
    public string Notes { get; init; } = "";
    public DateOnly? PcbOrderedAt { get; init; }
    public DateOnly? PcbReceivedAt { get; init; }
    public DateOnly? BuiltAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public int DeviceId { get; init; }
    public string Device { get; init; } = "";
    public string DevicePath { get; init; } = "";
    public int ProjectId { get; init; }
    public string Project { get; init; } = "";
    public List<AllocationLine> Lines { get; init; } = [];
    public List<PhotoDto> Photos { get; init; } = [];
    /// <summary>Sum of qty × last known unit price over the priced lines.</summary>
    public decimal? EstimatedCost { get; init; }
    public string Currency { get; init; } = "EUR";
    public int PricedLines { get; init; }

    public string Title => $"{Device} rev {Rev}";
}

public sealed class AllocationLine
{
    public int Id { get; init; }
    public int PartId { get; init; }
    public string Ref { get; init; } = "";
    public string Category { get; init; } = "";
    public string Designation { get; init; } = "";
    public string Designators { get; init; } = "";
    public int Qty { get; init; }
    public AllocationStatus Status { get; init; }
    public string Notes { get; init; } = "";
    public LineFlag Flag { get; init; }
    /// <summary>Availability of the part, worded: "3 available (5 in stock, 2 reserved)".</summary>
    public string StockText { get; init; } = "";
    public decimal? UnitPrice { get; init; }
    public string? CoverThumbUrl { get; init; }
}

public sealed class ProjectEdit
{
    public int? Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public ProjectStatus Status { get; set; } = ProjectStatus.Design;
    public string RepoPath { get; set; } = "";
    public string Url { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed class DeviceEdit
{
    public int? Id { get; set; }
    public int ProjectId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Path { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed class RevisionEdit
{
    public int? Id { get; set; }
    public int DeviceId { get; set; }
    public string Rev { get; set; } = "A";
    public RevisionStatus Status { get; set; } = RevisionStatus.Design;
    public string Notes { get; set; } = "";
    public DateOnly? PcbOrderedAt { get; set; }
    public DateOnly? PcbReceivedAt { get; set; }
    public DateOnly? BuiltAt { get; set; }
}

/// <summary>Create (Id null) or update an allocation line. A part has at most one line per revision.</summary>
public sealed class AllocationEdit
{
    public int? Id { get; set; }
    public int RevisionId { get; set; }
    public int PartId { get; set; }
    public int Qty { get; set; } = 1;
    public string Designators { get; set; } = "";
    public string Notes { get; set; } = "";
}

/// <summary>What "mark as built" does (or did) to the stock.</summary>
public sealed class BuildPlan
{
    public int RevisionId { get; init; }
    public string Title { get; init; } = "";
    public int LineCount { get; init; }
    public bool Applied { get; init; }
    public List<BuildChange> Changes { get; init; } = [];
}

public sealed record BuildChange(string Ref, string QtyBefore, string QtyAfter, int Consumed, string Remark);

public sealed class BomPreview
{
    public int RevisionId { get; init; }
    public List<BomLineMatch> Lines { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed class BomLineMatch
{
    public int Line { get; init; }
    public string Designators { get; init; } = "";
    public int Qty { get; init; }
    public string Designation { get; init; } = "";
    public string Value { get; init; } = "";
    public string LibraryRef { get; init; } = "";
    public string Supplier { get; init; } = "";
    public string Notes { get; init; } = "";
    public int? PartId { get; init; }
    public string? PartRef { get; init; }
    public string? PartDesignation { get; init; }
    public BomMatchKind Match { get; init; }
    public int? ExistingAllocationId { get; init; }
    public int? ExistingQty { get; init; }
    public string? ExistingDesignators { get; init; }
}

public sealed class BomApply
{
    public int RevisionId { get; set; }
    public List<BomApplyLine> Lines { get; set; } = [];
    /// <summary>Also release the reserved lines of the revision that are not in the BOM.</summary>
    public bool RemoveOthers { get; set; }
}

public sealed class BomApplyLine
{
    public int PartId { get; set; }
    public int Qty { get; set; }
    public string Designators { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed class BomApplyResult
{
    public int Added { get; init; }
    public int Updated { get; init; }
    public int Removed { get; init; }
    public RevisionDetail Revision { get; init; } = new();
}
