namespace ElecStock.Contracts;

/// <summary>The stock KiCad library: what every PC gets with a sync.</summary>
public sealed class KicadLibraryDto
{
    /// <summary>Changes whenever an item is added, replaced or removed: the sync compares it.</summary>
    public string Version { get; init; } = "";
    public List<KicadItemDto> Items { get; init; } = [];
}

public sealed class KicadItemDto
{
    public int Id { get; init; }
    public KicadItemKind Kind { get; init; }
    public string Name { get; init; } = "";
    /// <summary>What a part links to: <c>Stock:Name</c>, or <c>${ELEC_STOCK}/KiCad/3D/name.step</c> for a model.</summary>
    public string Link { get; init; } = "";
    /// <summary>Server-relative URL of the stored file (<c>files/cad/…</c>).</summary>
    public string Url { get; init; } = "";
    public long Size { get; init; }
    public string? Extends { get; init; }
    public string Source { get; init; } = "";
    public DateTimeOffset UpdatedAt { get; init; }
    /// <summary>Refs of the parts linking to this item.</summary>
    public List<string> UsedBy { get; init; } = [];
    /// <summary>For a footprint: its 3D model paths, as written to the library.</summary>
    public List<string> Models { get; init; } = [];
    /// <summary>For a footprint: model paths in the stock library whose file is not there.</summary>
    public List<string> MissingModels { get; init; } = [];
}

public sealed class KicadItemEdit
{
    public string Source { get; set; } = "";
}

public sealed class KicadUploadResult
{
    public List<KicadItemDto> Added { get; init; } = [];
    public List<KicadItemDto> Replaced { get; init; } = [];
    /// <summary>Files of a zip that are not KiCad files.</summary>
    public List<string> Skipped { get; init; } = [];
    /// <summary>Links of the part filled from the upload: "symbol Stock:X"…</summary>
    public List<string> Linked { get; init; } = [];
}
