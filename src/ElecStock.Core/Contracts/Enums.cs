using System.Text.Json.Serialization;

namespace ElecStock.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<ProjectStatus>))]
public enum ProjectStatus { Idea, Design, Prototype, Built, Archived }

/// <summary>Life cycle of one board revision.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RevisionStatus>))]
public enum RevisionStatus { Design, PcbOrdered, PcbReceived, Built, Abandoned }

/// <summary><c>Reserved</c> = still in the drawer but promised · <c>Used</c> = soldered, already deducted from the qty.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AllocationStatus>))]
public enum AllocationStatus { Reserved, Used }

/// <summary>State of one allocation line against the stock.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LineFlag>))]
public enum LineFlag { Ok, Used, Short, OnOrder, Uncounted }

[JsonConverter(typeof(JsonStringEnumConverter<OrderStatus>))]
public enum OrderStatus { Draft, Ordered, Received, Cancelled }

/// <summary>Who made a change: the web UI, the MAUI app, Claude through MCP, an import.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ActivitySource>))]
public enum ActivitySource { Web, App, Mcp, Import, System }

[JsonConverter(typeof(JsonStringEnumConverter<PhotoOwner>))]
public enum PhotoOwner { Part, Project, Revision }

[JsonConverter(typeof(JsonStringEnumConverter<ChangeScope>))]
public enum ChangeScope { All, Part, Project, Order, Location, Kicad }

/// <summary>What an item of the stock KiCad library is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<KicadItemKind>))]
public enum KicadItemKind { Symbol, Footprint, Model }

/// <summary>Why a part is on the shopping list.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ShoppingReason>))]
public enum ShoppingReason { Short, OutOfStock, Planned }

/// <summary>How a BOM line was matched to a part.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BomMatchKind>))]
public enum BomMatchKind { None, LibraryRef, Value, Designation, SimilarRef }
