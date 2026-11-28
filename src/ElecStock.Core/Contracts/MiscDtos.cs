namespace ElecStock.Contracts;

public sealed class LocationDto
{
    public int Id { get; init; }
    /// <summary>Short code printed on the label, e.g. <c>A3</c>.</summary>
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>Parts whose main location is this one.</summary>
    public int PartCount { get; init; }
    /// <summary>Parts stored here or in a location it holds (<see cref="Stock.LocationTree"/>).</summary>
    public int TotalPartCount { get; init; }
    /// <summary>Parts whose spare stock is kept here.</summary>
    public int SparePartCount { get; init; }
    /// <summary>The location holding this one (<c>DIO-1</c> for <c>DIO-1.3</c>), empty at the top.</summary>
    public string ParentCode { get; init; } = "";
    /// <summary>0 at the top, 1 inside a top location…</summary>
    public int Depth { get; init; }

    public string Display => Name.Length > 0 && !Name.Equals(Code, StringComparison.OrdinalIgnoreCase) ? $"{Code} · {Name}" : Code;
}

public sealed class LocationDetail
{
    public LocationDto Location { get; init; } = new();
    /// <summary>The locations holding this one, from the top down (DIO, DIO-1 for DIO-1.3).</summary>
    public List<LocationDto> Parents { get; init; } = [];
    /// <summary>The locations directly inside this one.</summary>
    public List<LocationDto> Children { get; init; } = [];
    /// <summary>Parts whose main location is this one or a location inside it, by location then part.</summary>
    public List<PartSummary> Parts { get; init; } = [];
    /// <summary>Parts whose spare stock is kept here or in a location inside it.</summary>
    public List<PartSummary> SpareParts { get; init; } = [];
}

public sealed class LocationEdit
{
    public int? Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class ActivityDto
{
    public long Id { get; init; }
    public DateTimeOffset At { get; init; }
    public ActivitySource Source { get; init; }
    /// <summary>See <see cref="ActivityKinds"/>.</summary>
    public string Kind { get; init; } = "";
    public string Summary { get; init; } = "";
    public int? PartId { get; init; }
    public string PartRef { get; init; } = "";
    public int? ProjectId { get; init; }
    public int? RevisionId { get; init; }
    public int? OrderId { get; init; }
    public string? QtyBefore { get; init; }
    public string? QtyAfter { get; init; }
}

public sealed class ActivityQuery
{
    public int? PartId { get; set; }
    public int? ProjectId { get; set; }
    public int? RevisionId { get; set; }
    public int? OrderId { get; set; }
    public ActivitySource? Source { get; set; }
    public int Take { get; set; } = 100;
}

public static class ActivityKinds
{
    public const string PartCreated = "part.created";
    public const string PartUpdated = "part.updated";
    public const string PartDeleted = "part.deleted";
    public const string QtyChanged = "qty.changed";
    public const string SheetUpdated = "sheet.updated";
    public const string NotesUpdated = "notes.updated";
    public const string PhotoAdded = "photo.added";
    public const string PhotoRemoved = "photo.removed";
    public const string DatasheetUpdated = "datasheet.updated";
    public const string Reserved = "allocation.reserved";
    public const string AllocationUpdated = "allocation.updated";
    public const string Released = "allocation.released";
    public const string Built = "revision.built";
    public const string BomImported = "bom.imported";
    public const string ProjectChanged = "project.changed";
    public const string ProjectDeleted = "project.deleted";
    public const string RevisionChanged = "revision.changed";
    public const string OrderChanged = "order.changed";
    public const string OrderReceived = "order.received";
    public const string LocationChanged = "location.changed";
    public const string Import = "import";
    public const string KicadChanged = "kicad.changed";
}

/// <summary>A change made on the server, pushed to every open client so it refreshes.</summary>
public sealed record ChangeEvent(ChangeScope Scope, int? Id = null);

public sealed class ImportReport
{
    public int Parts { get; set; }
    public int Sheets { get; set; }
    public int Photos { get; set; }
    public int FreeNotes { get; set; }
    public int Datasheets { get; set; }
    public int KicadItems { get; set; }
    public int Locations { get; set; }
    public int Projects { get; set; }
    public int Revisions { get; set; }
    public int Allocations { get; set; }
    public int Orders { get; set; }
    public List<string> Warnings { get; set; } = [];
}

public sealed class ServerInfo
{
    public string Version { get; init; } = "";
    /// <summary>Address of the web UI as seen from the phone (QR labels point there).</summary>
    public string PublicBaseUrl { get; init; } = "";
    public int PartCount { get; init; }
}

/// <summary>A rule of the stock was broken (duplicate ref, comma in a field…): the message is for the user.</summary>
public sealed class StockException(string message) : Exception(message);
