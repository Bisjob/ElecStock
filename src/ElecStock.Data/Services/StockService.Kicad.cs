using ElecStock.Contracts;
using ElecStock.Data.Entities;
using ElecStock.Data.Files;
using ElecStock.Kicad;
using Microsoft.EntityFrameworkCore;

namespace ElecStock.Data.Services;

public sealed partial class StockService
{
    public async Task<KicadLibraryDto> GetKicadLibraryAsync(CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var items = await db.KicadItems.AsNoTracking().ToListAsync(ct);
        var links = await PartLinksAsync(db, ct);
        var resolve = KicadLibrary.ModelResolver(items);
        return new KicadLibraryDto
        {
            Version = KicadLibrary.Version(items),
            Items = items
                .OrderBy(i => i.Kind).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .Select(i => ToDto(i, links, resolve))
                .ToList(),
        };
    }

    public async Task<KicadUploadResult> UploadKicadFileAsync(string fileName, Stream content, int? partId = null, string? source = null, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        if (buffer.Length > KicadFiles.MaxBytes) throw new StockException($"{Path.GetFileName(fileName)} is larger than 64 MB.");
        var skipped = new List<string>();
        List<KicadFile> uploads;
        try
        {
            uploads = KicadFiles.Read(fileName, buffer.ToArray(), skipped);
        }
        catch (InvalidDataException)
        {
            throw new StockException($"{Path.GetFileName(fileName)}: not a valid zip.");
        }

        await using var db = await Db(ct);
        Part? part = null;
        if (partId != null)
            part = await db.Parts.FirstOrDefaultAsync(p => p.Id == partId, ct) ?? throw new StockException("This part no longer exists.");

        var upsert = await KicadLibrary.UpsertAsync(db, files, uploads, Now, ct);
        if (!string.IsNullOrWhiteSpace(source))
            foreach (var item in upsert.Added.Concat(upsert.Replaced)) item.Source = source.Trim();

        // A part gets the links it lacks: the one symbol, the one footprint, and the model when it comes without a footprint.
        var linked = new List<string>();
        if (part != null)
        {
            string? One(KicadItemKind kind) => uploads.Count(u => u.Kind == kind) == 1 ? uploads.Single(u => u.Kind == kind).Name : null;
            if (part.KicadSymbol.Length == 0 && One(KicadItemKind.Symbol) is { } symbol)
            {
                part.KicadSymbol = KicadLib.LibId(symbol);
                linked.Add($"symbol {part.KicadSymbol}");
            }
            if (part.KicadFootprint.Length == 0 && One(KicadItemKind.Footprint) is { } footprint)
            {
                part.KicadFootprint = KicadLib.LibId(footprint);
                linked.Add($"footprint {part.KicadFootprint}");
            }
            if (part.Kicad3dModel.Length == 0 && !uploads.Any(u => u.Kind == KicadItemKind.Footprint) && One(KicadItemKind.Model) is { } model)
            {
                part.Kicad3dModel = KicadLib.ModelPath(model);
                linked.Add($"3D model {model}");
            }
            if (linked.Count > 0) part.UpdatedAt = Now;
        }

        var names = string.Join(", ", uploads.Select(u => $"{KicadLib.Title(u.Kind)} {u.Name}"));
        var verb = upsert.Replaced.Count == 0 ? "added" : upsert.Added.Count == 0 ? "replaced" : "added/replaced";
        Log(db, ActivityKinds.KicadChanged, $"KiCad library: {verb} {names}" + (linked.Count > 0 ? $"; {part!.Ref} linked to {string.Join(", ", linked)}" : ""), part);
        await db.SaveChangesAsync(ct);
        foreach (var old in upsert.OldFiles) files.Trash(old);
        Notify(ChangeScope.Kicad);
        if (part != null) Notify(ChangeScope.Part, part.Id);

        var all = await db.KicadItems.AsNoTracking().ToListAsync(ct);
        var links = await PartLinksAsync(db, ct);
        var resolve = KicadLibrary.ModelResolver(all);
        return new KicadUploadResult
        {
            Added = upsert.Added.Select(i => ToDto(i, links, resolve)).ToList(),
            Replaced = upsert.Replaced.Select(i => ToDto(i, links, resolve)).ToList(),
            Skipped = skipped,
            Linked = linked,
        };
    }

    public async Task<KicadItemDto> UpdateKicadItemAsync(int id, KicadItemEdit edit, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var item = await db.KicadItems.FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new StockException("This library item no longer exists.");
        var source = Clean(edit.Source);
        if (source != item.Source)
        {
            item.Source = source;
            item.UpdatedAt = Now;
            Log(db, ActivityKinds.KicadChanged, $"KiCad library: source of {KicadLib.Title(item.Kind)} {item.Name} edited");
            await db.SaveChangesAsync(ct);
            Notify(ChangeScope.Kicad);
        }
        var all = await db.KicadItems.AsNoTracking().ToListAsync(ct);
        return ToDto(item, await PartLinksAsync(db, ct), KicadLibrary.ModelResolver(all));
    }

    public async Task DeleteKicadItemAsync(int id, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var item = await db.KicadItems.FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new StockException("This library item no longer exists.");
        var title = $"{KicadLib.Title(item.Kind)} {item.Name}";

        var users = UsedBy(item, await PartLinksAsync(db, ct));
        if (users.Count > 0) throw new StockException($"The {title} is linked to {string.Join(", ", users)}: change their KiCad links first.");
        if (item.Kind == KicadItemKind.Symbol)
        {
            var derived = await db.KicadItems.Where(i => i.Kind == KicadItemKind.Symbol && i.Extends == item.Name).Select(i => i.Name).ToListAsync(ct);
            if (derived.Count > 0) throw new StockException($"The symbol {item.Name} is extended by {string.Join(", ", derived)}: remove them first.");
        }
        if (item.Kind == KicadItemKind.Model)
        {
            var footprints = (await db.KicadItems.AsNoTracking().Where(i => i.Kind == KicadItemKind.Footprint).ToListAsync(ct))
                .Where(f => FootprintModels(f).Any(m => ModelFile(m).Equals(item.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(f => f.Name)
                .ToList();
            if (footprints.Count > 0) throw new StockException($"The 3D model {item.Name} is used by the footprint(s) {string.Join(", ", footprints)}.");
        }

        db.KicadItems.Remove(item);
        Log(db, ActivityKinds.KicadChanged, $"KiCad library: removed {title}");
        await db.SaveChangesAsync(ct);
        files.Trash(item.StoragePath);
        Notify(ChangeScope.Kicad);
    }

    // ---------- links of the parts ----------

    sealed record PartLinks(string Ref, string Symbol, string Footprint, string Model);

    static async Task<List<PartLinks>> PartLinksAsync(StockDb db, CancellationToken ct) =>
        await db.Parts.AsNoTracking()
            .Where(p => p.KicadSymbol != "" || p.KicadFootprint != "" || p.Kicad3dModel != "")
            .Select(p => new PartLinks(p.Ref, p.KicadSymbol, p.KicadFootprint, p.Kicad3dModel))
            .ToListAsync(ct);

    static List<string> UsedBy(KicadItem item, List<PartLinks> links) =>
        links.Where(l => item.Kind switch
            {
                KicadItemKind.Symbol => KicadLib.StockName(l.Symbol)?.Equals(item.Name, StringComparison.OrdinalIgnoreCase) == true,
                KicadItemKind.Footprint => KicadLib.StockName(l.Footprint)?.Equals(item.Name, StringComparison.OrdinalIgnoreCase) == true,
                _ => KicadLib.StockModelName(l.Model)?.Equals(item.Name, StringComparison.OrdinalIgnoreCase) == true,
            })
            .Select(l => l.Ref)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    KicadItemDto ToDto(KicadItem item, List<PartLinks> links, Func<string, string?> resolve)
    {
        List<string> models = [], missing = [];
        if (item.Kind == KicadItemKind.Footprint)
        {
            foreach (var path in FootprintModels(item))
            {
                var linked = resolve(ModelFile(path)) ?? path;
                models.Add(linked);
                if (KicadLib.StockModelName(linked) != null && resolve(ModelFile(linked)) == null) missing.Add(linked);
            }
        }
        return new KicadItemDto
        {
            Id = item.Id,
            Kind = item.Kind,
            Name = item.Name,
            Link = KicadLib.LinkOf(item.Kind, item.Name),
            Url = FileStore.Url(item.StoragePath),
            Size = item.Size,
            Extends = item.Extends,
            Source = item.Source,
            UpdatedAt = item.UpdatedAt,
            UsedBy = UsedBy(item, links),
            Models = models,
            MissingModels = missing,
        };
    }

    List<string> FootprintModels(KicadItem footprint)
    {
        if (!files.Exists(footprint.StoragePath)) return [];
        try
        {
            return KicadFiles.Models(File.ReadAllText(files.FullPath(footprint.StoragePath)));
        }
        catch (FormatException)
        {
            return [];
        }
    }

    static string ModelFile(string path)
    {
        var p = path.Replace('\\', '/');
        return p[(p.LastIndexOf('/') + 1)..];
    }

    /// <summary>
    /// Checks the KiCad links of a part edit and returns them normalized: a <c>Stock:</c> link must name an item of the
    /// library (and gets its exact case, KiCad being case-sensitive); other libraries are taken as given.
    /// </summary>
    static async Task<(string Symbol, string Footprint, string Model)> CheckLinksAsync(StockDb db, PartEdit edit, CancellationToken ct)
    {
        async Task<string> LibId(string value, KicadItemKind kind)
        {
            var v = Clean(value);
            if (v.Length == 0) return "";
            var colon = v.IndexOf(':');
            if (colon <= 0 || colon == v.Length - 1 || v[..colon].Any(char.IsWhiteSpace))
                throw new StockException($"KiCad {KicadLib.Title(kind)} '{v}': expected Library:Name, e.g. {(kind == KicadItemKind.Symbol ? "Device:R" : "Resistor_SMD:R_0603_1608Metric")}.");
            if (KicadLib.StockName(v) is not { } name) return v;
            var item = await db.KicadItems.AsNoTracking().FirstOrDefaultAsync(i => i.Kind == kind && i.Name == name, ct)
                ?? throw new StockException($"There is no {KicadLib.Title(kind)} '{name}' in the stock KiCad library: upload it first.");
            return KicadLib.LibId(item.Name);
        }

        var model = Clean(edit.Kicad3dModel).Replace('\\', '/');
        if (KicadLib.StockModelName(model) is { } file)
        {
            var item = await db.KicadItems.AsNoTracking().FirstOrDefaultAsync(i => i.Kind == KicadItemKind.Model && i.Name == file, ct)
                ?? throw new StockException($"There is no 3D model '{file}' in the stock KiCad library: upload it first.");
            model = KicadLib.ModelPath(item.Name);
        }
        else if (model.Length > 0 && KicadLib.KindOf(model) != KicadItemKind.Model)
        {
            throw new StockException($"3D model '{model}': expected a .step, .stp or .wrl path.");
        }
        return (await LibId(edit.KicadSymbol, KicadItemKind.Symbol), await LibId(edit.KicadFootprint, KicadItemKind.Footprint), model);
    }
}
