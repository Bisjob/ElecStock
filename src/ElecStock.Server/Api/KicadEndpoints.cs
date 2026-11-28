using ElecStock.Contracts;
using ElecStock.Data.Services;
using Microsoft.AspNetCore.Mvc;

namespace ElecStock.Server.Api;

/// <summary>
/// The stock KiCad library for the PCs: <c>/kicad/version</c> and <c>/kicad/library.zip</c> (what the sync script
/// fetches), <c>/kicad/sync.ps1</c> (the script, with this server's address in it) and <c>/kicad/upload</c>
/// (adding files by ref, for Claude and scripts: logged as Claude).
/// </summary>
public static class KicadEndpoints
{
    public static void MapKicadLibrary(this IEndpointRouteBuilder app)
    {
        var kicad = app.MapGroup("/kicad").DisableAntiforgery();

        kicad.MapGet("/version", async (KicadLibrary library, CancellationToken ct) => Results.Text(await library.VersionAsync(ct)));

        kicad.MapGet("/library.zip", async (HttpContext context, KicadLibrary library, CancellationToken ct) =>
        {
            context.Response.ContentType = "application/zip";
            context.Response.Headers.ContentDisposition = "attachment; filename=\"elecstock-kicad.zip\"";
            context.Response.Headers.CacheControl = "no-store";
            await library.WriteZipAsync(context.Response.Body, ct);
        });

        kicad.MapGet("/sync.ps1", (HttpRequest request) =>
        {
            using var stream = typeof(KicadEndpoints).Assembly.GetManifestResourceStream("ElecStock.Server.Kicad.sync.ps1")!;
            using var reader = new StreamReader(stream);
            var server = $"{request.Scheme}://{request.Host}{request.PathBase}";
            return Results.Text(reader.ReadToEnd().Replace("__SERVER__", server), "text/plain; charset=utf-8");
        });

        kicad.MapPost("/upload", async ([FromQuery] string fileName, [FromQuery] string? part, [FromQuery] string? source, HttpRequest request,
            StockService stock, CallerContext caller, CancellationToken ct) =>
        {
            caller.Source = ActivitySource.Mcp;
            try
            {
                int? partId = null;
                if (!string.IsNullOrWhiteSpace(part))
                    partId = (await stock.GetPartByRefAsync(part, ct))?.Part.Id ?? throw new StockException($"No part '{part}' in the stock.");
                return Results.Ok(await stock.UploadKicadFileAsync(fileName, request.Body, partId, source, ct));
            }
            catch (StockException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Stock rule");
            }
        });
    }
}
