using System.IO.Compression;
using System.Text;
using ElecStock.Contracts;
using ElecStock.Kicad;

namespace ElecStock.Tests;

public class KicadFilesTests
{
    // Two symbols, one derived; strings hold parentheses and escaped quotes to trip a naive scanner.
    public const string SymbolLib = """
        (kicad_symbol_lib
        	(version 20241209)
        	(generator "kicad_symbol_editor")
        	(symbol "INA226-Module"
        		(property "Reference" "U" (at 0 0 0))
        		(property "Description" "INA226 (I2C) \"module\" :)" (at 0 0 0))
        		(symbol "INA226-Module_0_1"
        			(pin bidirectional line (at 0 0 0) (length 2.54) (name "SCL") (number "5"))
        		)
        	)
        	(symbol "INA226-Module-v2"
        		(extends "INA226-Module")
        		(property "Reference" "U" (at 0 0 0))
        	)
        )
        """;

    public const string Footprint = """
        (footprint "ESP32-C3-SuperMini"
        	(version 20240108)
        	(layer "F.Cu")
        	(pad "1" thru_hole circle (at 0 0) (size 1.7 1.7) (drill 1) (layers "*.Cu"))
        	(model "D:/Downloads/esp32 c3/ESP32-C3-SuperMini.step"
        		(offset (xyz 0 0 0))
        	)
        	(model "${KICAD10_3DMODEL_DIR}/Connector_PinHeader_2.54mm.3dshapes/PinHeader_1x08_P2.54mm_Vertical.step")
        )
        """;

    static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    static readonly byte[] Step = Bytes("ISO-10303-21;\nHEADER;\nENDSEC;\n");

    [Fact]
    public void A_symbol_library_gives_one_item_per_symbol()
    {
        var items = KicadFiles.Read("Stock.kicad_sym", Bytes(SymbolLib));
        Assert.Equal(["INA226-Module", "INA226-Module-v2"], items.Select(i => i.Name));
        Assert.All(items, i => Assert.Equal(KicadItemKind.Symbol, i.Kind));
        Assert.Null(items[0].Extends);
        Assert.Equal("INA226-Module", items[1].Extends);

        // Each item is a valid library of its own, with the version of the source and the symbol verbatim.
        var single = Encoding.UTF8.GetString(items[0].Content);
        var (head, _, children) = Sexpr.Root(single);
        Assert.Equal("kicad_symbol_lib", head);
        Assert.Equal("20241209", children.Single(c => c.Head == "version").Arg);
        Assert.Contains("\"INA226 (I2C) \\\"module\\\" :)\"", single);
        Assert.Single(children, c => c.Head == "symbol");
    }

    [Fact]
    public void Symbols_merge_sorted_at_the_newest_version()
    {
        var older = KicadFiles.ReadSymbols(SymbolLib.Replace("20241209", "20211014").Replace("INA226-Module", "AO4407A"));
        var newer = KicadFiles.ReadSymbols(SymbolLib);
        var merged = KicadFiles.MergeSymbols(newer.Concat(older).Select(i => Encoding.UTF8.GetString(i.Content)));
        var (_, _, children) = Sexpr.Root(merged);
        Assert.Equal("20241209", children.Single(c => c.Head == "version").Arg);
        Assert.Equal(["AO4407A", "AO4407A-v2", "INA226-Module", "INA226-Module-v2"], children.Where(c => c.Head == "symbol").Select(c => c.Arg));
        Assert.Equal(older.Concat(newer).Count(), KicadFiles.ReadSymbols(merged).Count);
    }

    [Fact]
    public void An_empty_library_is_still_valid()
    {
        var (head, _, children) = Sexpr.Root(KicadFiles.MergeSymbols([]));
        Assert.Equal("kicad_symbol_lib", head);
        Assert.Equal("20211014", children.Single(c => c.Head == "version").Arg);
    }

    [Fact]
    public void A_footprint_is_named_from_its_content_and_its_models_point_to_the_library()
    {
        var item = KicadFiles.Read("ESP32-C3-SuperMini (1).kicad_mod", Bytes(Footprint)).Single();
        Assert.Equal((KicadItemKind.Footprint, "ESP32-C3-SuperMini"), (item.Kind, item.Name));

        var linked = KicadFiles.LinkModels(Footprint, file => file.Equals("esp32-c3-supermini.STEP", StringComparison.OrdinalIgnoreCase) ? KicadLib.ModelPath("ESP32-C3-SuperMini.step") : null);
        Assert.Equal(["${ELEC_STOCK}/KiCad/3D/ESP32-C3-SuperMini.step", "${KICAD10_3DMODEL_DIR}/Connector_PinHeader_2.54mm.3dshapes/PinHeader_1x08_P2.54mm_Vertical.step"],
            KicadFiles.Models(linked));
        Assert.Contains("(offset (xyz 0 0 0))", linked);
    }

    [Fact]
    public void A_zip_brings_its_kicad_files_and_skips_the_rest()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, byte[] content)
            {
                using var s = zip.CreateEntry(path).Open();
                s.Write(content);
            }
            Add("lib/Stock.kicad_sym", Bytes(SymbolLib));
            Add("lib/Stock.pretty/ESP32-C3-SuperMini.kicad_mod", Bytes(Footprint));
            Add("lib/3D/ESP32-C3-SuperMini.step", Step);
            Add("lib/README.md", Bytes("# readme"));
        }
        var skipped = new List<string>();
        var items = KicadFiles.Read("download.zip", buffer.ToArray(), skipped);
        Assert.Equal(4, items.Count);
        Assert.Equal(["lib/README.md"], skipped);
        Assert.Contains(items, i => i is { Kind: KicadItemKind.Model, Name: "ESP32-C3-SuperMini.step", Extension: ".step" });
    }

    [Theory]
    [InlineData("board.step", "not a STEP file")]
    [InlineData("notes.txt", "not a KiCad file")]
    public void Wrong_files_are_refused(string name, string message)
    {
        var ex = Assert.Throws<StockException>(() => KicadFiles.Read(name, Bytes("hello")));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Names_must_be_file_names()
    {
        var ex = Assert.Throws<StockException>(() => KicadFiles.Read("x.kicad_mod", Bytes("(footprint \"A/B\" (layer \"F.Cu\"))")));
        Assert.Contains("cannot contain", ex.Message);
    }

    [Theory]
    [InlineData("Stock:ESP32-C3-SuperMini", "ESP32-C3-SuperMini")]
    [InlineData("stock:R_Custom", "R_Custom")]
    [InlineData("Device:R", null)]
    public void Stock_links(string link, string? name) => Assert.Equal(name, KicadLib.StockName(link));

    [Fact]
    public void Stock_model_paths() =>
        Assert.Equal("Fuse.step", KicadLib.StockModelName("${ELEC_STOCK}\\KiCad\\3D\\Fuse.step"));
}
