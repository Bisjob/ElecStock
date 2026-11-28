using System.IO.Compression;
using System.Text;
using ElecStock.Contracts;

namespace ElecStock.Kicad;

/// <summary>
/// The KiCad library of the stock, as every PC sees it after a sync: nickname <c>Stock</c>, path variable
/// <c>ELEC_STOCK</c> pointing at the synced folder, files under <c>KiCad/</c> (the layout of the stock folder
/// before the server, so the boards drawn with it keep working).
/// </summary>
public static class KicadLib
{
    public const string Nickname = "Stock";
    public const string PathVariable = "ELEC_STOCK";
    public const string SymbolFile = "KiCad/Stock.kicad_sym";
    public const string FootprintDir = "KiCad/Stock.pretty";
    public const string ModelDir = "KiCad/3D";
    public const string ModelPrefix = "${" + PathVariable + "}/" + ModelDir + "/";

    public static readonly string[] ModelExtensions = [".step", ".stp", ".wrl"];

    /// <summary><c>Stock:ESP32-C3-SuperMini</c>.</summary>
    public static string LibId(string name) => Nickname + ":" + name;

    /// <summary><c>${ELEC_STOCK}/KiCad/3D/ESP32-C3-SuperMini.step</c>.</summary>
    public static string ModelPath(string name) => ModelPrefix + name;

    /// <summary>What a part links to, as shown and searched: the symbol/footprint lib id or the 3D model path.</summary>
    public static string LinkOf(KicadItemKind kind, string name) => kind == KicadItemKind.Model ? ModelPath(name) : LibId(name);

    /// <summary>The item name when <paramref name="libId"/> is in the stock library (<c>Stock:X</c> → X), else null.</summary>
    public static string? StockName(string libId) =>
        libId.StartsWith(Nickname + ":", StringComparison.OrdinalIgnoreCase) ? libId[(Nickname.Length + 1)..] : null;

    /// <summary>The model file name when <paramref name="path"/> is in the stock library, else null.</summary>
    public static string? StockModelName(string path)
    {
        var p = path.Replace('\\', '/');
        return p.StartsWith(ModelPrefix, StringComparison.OrdinalIgnoreCase) ? p[ModelPrefix.Length..] : null;
    }

    public static KicadItemKind? KindOf(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext == ".kicad_sym") return KicadItemKind.Symbol;
        if (ext == ".kicad_mod") return KicadItemKind.Footprint;
        if (ModelExtensions.Contains(ext)) return KicadItemKind.Model;
        return null;
    }

    /// <summary>Names become file names on every PC: no path or reserved characters.</summary>
    public static string CheckName(string name, KicadItemKind kind)
    {
        var n = name.Trim();
        if (n.Length == 0) throw new StockException($"A {Title(kind)} has no name.");
        if (n.Length > 200) throw new StockException($"'{n[..40]}…': a name is at most 200 characters.");
        if (n.Any(c => c < ' ' || "<>:\"/\\|?*".Contains(c)))
            throw new StockException($"'{n}': a {Title(kind)} name cannot contain < > : \" / \\ | ? *");
        return n;
    }

    public static string Title(KicadItemKind kind) => kind switch
    {
        KicadItemKind.Symbol => "symbol",
        KicadItemKind.Footprint => "footprint",
        _ => "3D model",
    };
}

/// <summary>One item of the library read from an upload: a symbol (as a one-symbol library), a footprint or a 3D model.</summary>
/// <param name="Extends">For a derived symbol, the symbol it extends (must be in the library too).</param>
public sealed record KicadFile(KicadItemKind Kind, string Name, string Extension, byte[] Content, string? Extends = null);

/// <summary>Reads uploaded KiCad files and writes the merged library. Content is sliced, never re-formatted.</summary>
public static class KicadFiles
{
    public const int MaxBytes = 64 * 1024 * 1024;
    const string Generator = "elecstock";
    /// <summary>KiCad 6, the first version of the current format.</summary>
    const long DefaultVersion = 20211014;

    /// <summary>
    /// The items of an upload: a <c>.kicad_sym</c> (one item per symbol), a <c>.kicad_mod</c>, a <c>.step</c>/<c>.stp</c>/<c>.wrl</c>,
    /// or a <c>.zip</c> of them (what SnapEDA, Ultra Librarian or a folder copy give). Other files of a zip go to <paramref name="skipped"/>.
    /// </summary>
    public static List<KicadFile> Read(string fileName, byte[] content, List<string>? skipped = null)
    {
        if (!Path.GetExtension(fileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            if (KicadLib.KindOf(fileName) == null)
                throw new StockException($"{Path.GetFileName(fileName)}: not a KiCad file (.kicad_sym, .kicad_mod, .step, .stp, .wrl or a .zip of them).");
            return ReadOne(fileName, content);
        }

        var items = new List<KicadFile>();
        using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
        foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith('/')).OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
        {
            var path = entry.FullName.Replace('\\', '/');
            if (path.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)) continue;
            if (KicadLib.KindOf(path) == null)
            {
                skipped?.Add(path);
                continue;
            }
            if (entry.Length > MaxBytes) throw new StockException($"{path} is larger than 64 MB.");
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            items.AddRange(ReadOne(path, buffer.ToArray()));
        }
        if (items.Count == 0) throw new StockException($"{Path.GetFileName(fileName)} holds no KiCad file.");
        return items;
    }

    static List<KicadFile> ReadOne(string fileName, byte[] content)
    {
        var name = Path.GetFileName(fileName);
        var kind = KicadLib.KindOf(name)!.Value;
        try
        {
            switch (kind)
            {
                case KicadItemKind.Symbol:
                    return ReadSymbols(Text(content), name);
                case KicadItemKind.Footprint:
                    var text = Text(content);
                    var (head, arg, _) = Sexpr.Root(text);
                    if (head is not ("footprint" or "module")) throw new StockException($"{name}: not a footprint (starts with '{head}').");
                    // The name inside the file survives a renamed download ("SOT-23 (1).kicad_mod"); a lib prefix is dropped.
                    var footprint = arg is { Length: > 0 } ? arg[(arg.LastIndexOf(':') + 1)..] : Path.GetFileNameWithoutExtension(name);
                    return [new KicadFile(kind, KicadLib.CheckName(footprint, kind), ".kicad_mod", content)];
                default:
                    var ext = Path.GetExtension(name).ToLowerInvariant();
                    int skip = 0;
                    while (skip < content.Length && skip < 16 && content[skip] is 0xEF or 0xBB or 0xBF or (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t') skip++;
                    var start = Encoding.ASCII.GetString(content, skip, Math.Min(content.Length - skip, 32));
                    var valid = ext == ".wrl" ? start.StartsWith("#VRML", StringComparison.Ordinal) : start.StartsWith("ISO-10303-21", StringComparison.Ordinal);
                    if (!valid) throw new StockException($"{name}: not a {(ext == ".wrl" ? "VRML" : "STEP")} file.");
                    return [new KicadFile(kind, KicadLib.CheckName(name, kind), ext, content)];
            }
        }
        catch (FormatException ex)
        {
            throw new StockException($"{name}: unreadable ({ex.Message})");
        }
    }

    /// <summary>One item per top-level symbol, each kept as a library of its own (with the version of the source file).</summary>
    public static List<KicadFile> ReadSymbols(string text, string fileName = "")
    {
        var (head, _, children) = Sexpr.Root(text);
        if (head != "kicad_symbol_lib") throw new StockException($"{fileName}: not a symbol library (starts with '{head}').");
        var version = Version(text, children);
        var items = new List<KicadFile>();
        foreach (var node in children.Where(c => c.Head == "symbol"))
        {
            var name = KicadLib.CheckName(node.Arg ?? "", KicadItemKind.Symbol);
            var extends = Sexpr.Children(text, node).FirstOrDefault(c => c.Head == "extends").Arg;
            var lib = Library(version, [text[node.Start..node.End]]);
            items.Add(new KicadFile(KicadItemKind.Symbol, name, ".kicad_sym", Encoding.UTF8.GetBytes(lib), extends));
        }
        if (items.Count == 0) throw new StockException($"{fileName}: the library has no symbol.");
        return items;
    }

    /// <summary>The <c>Stock.kicad_sym</c> of the given one-symbol libraries, sorted by name, at the newest of their versions.</summary>
    public static string MergeSymbols(IEnumerable<string> libraries)
    {
        long version = 0;
        var symbols = new List<(string Name, string Text)>();
        foreach (var text in libraries)
        {
            var (_, _, children) = Sexpr.Root(text);
            version = Math.Max(version, Version(text, children));
            symbols.AddRange(children.Where(c => c.Head == "symbol").Select(c => (c.Arg ?? "", text[c.Start..c.End])));
        }
        if (version == 0) version = DefaultVersion;
        return Library(version, symbols.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(s => s.Text));
    }

    /// <summary>
    /// The footprint as written to <c>Stock.pretty</c>: each <c>(model "…/name.step")</c> whose file is a model of the
    /// library points to it (<c>${ELEC_STOCK}/KiCad/3D/name.step</c>); other paths are kept.
    /// </summary>
    public static string LinkModels(string footprint, Func<string, string?> modelPath)
    {
        var (_, _, children) = Sexpr.Root(footprint);
        var sb = new StringBuilder(footprint);
        foreach (var model in children.Where(c => c.Head == "model" && c.Arg != null).OrderByDescending(c => c.ArgStart))
        {
            var file = model.Arg!.Replace('\\', '/');
            file = file[(file.LastIndexOf('/') + 1)..];
            var path = modelPath(file);
            if (path == null || path == model.Arg) continue;
            sb.Remove(model.ArgStart, model.ArgEnd - model.ArgStart).Insert(model.ArgStart, Sexpr.Quote(path));
        }
        return sb.ToString();
    }

    /// <summary>The <c>(model …)</c> paths of a footprint.</summary>
    public static List<string> Models(string footprint) =>
        Sexpr.Root(footprint).Children.Where(c => c.Head == "model" && c.Arg != null).Select(c => c.Arg!).ToList();

    static long Version(string text, List<SexprNode> children)
    {
        var node = children.FirstOrDefault(c => c.Head == "version");
        return node.Arg != null && long.TryParse(node.Arg, out var v) ? v : DefaultVersion;
    }

    static string Library(long version, IEnumerable<string> symbols)
    {
        var sb = new StringBuilder();
        sb.Append("(kicad_symbol_lib\n");
        sb.Append($"\t(version {version})\n");
        sb.Append($"\t(generator \"{Generator}\")\n");
        foreach (var symbol in symbols) sb.Append('\t').Append(symbol.Replace("\r\n", "\n")).Append('\n');
        sb.Append(")\n");
        return sb.ToString();
    }

    static string Text(byte[] content) => new UTF8Encoding(false).GetString(content).TrimStart('﻿');
}
