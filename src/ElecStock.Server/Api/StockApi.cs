using ElecStock.Contracts;
using ElecStock.Data.Services;
using Microsoft.AspNetCore.Mvc;

namespace ElecStock.Server.Api;

/// <summary>
/// HTTP API used by the MAUI app: one endpoint per <see cref="IStockApi"/> method. Uploads are the
/// raw request body (file name in the query string). A broken stock rule answers 400 with the
/// message in the problem details.
/// </summary>
public static class StockApi
{
    public sealed record MarkdownBody(string? Markdown);
    public sealed record CsvBody(string Csv);

    public static void MapStockApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api")
            .AddEndpointFilter(async (context, next) =>
            {
                context.HttpContext.RequestServices.GetRequiredService<CallerContext>().Source = ActivitySource.App;
                try
                {
                    return await next(context);
                }
                catch (StockException ex)
                {
                    return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Stock rule");
                }
            })
            .DisableAntiforgery();

        // ---------- stock ----------
        api.MapGet("/stock", (StockService s, CancellationToken ct) => s.GetStockAsync(ct));
        api.MapGet("/parts/{id:int}", async (int id, StockService s, CancellationToken ct) => OkOrNotFound(await s.GetPartAsync(id, ct)));
        api.MapGet("/parts/by-ref/{reference}", async (string reference, StockService s, CancellationToken ct) => OkOrNotFound(await s.GetPartByRefAsync(reference, ct)));
        api.MapPost("/parts", (PartEdit edit, StockService s, CancellationToken ct) => s.SavePartAsync(edit, ct));
        api.MapDelete("/parts/{id:int}", async (int id, StockService s, CancellationToken ct) => { await s.DeletePartAsync(id, ct); return Results.NoContent(); });
        api.MapPut("/parts/{id:int}/sheet", async (int id, MarkdownBody body, StockService s, CancellationToken ct) => { await s.SaveSheetAsync(id, body.Markdown, ct); return Results.NoContent(); });
        api.MapPut("/parts/{id:int}/notes", async (int id, MarkdownBody body, StockService s, CancellationToken ct) => { await s.SaveFreeNotesAsync(id, body.Markdown ?? "", ct); return Results.NoContent(); });

        api.MapPost("/photos/{owner}/{ownerId:int}", (PhotoOwner owner, int ownerId, [FromQuery] string fileName, HttpRequest request, StockService s, CancellationToken ct) =>
            s.UploadPhotoAsync(owner, ownerId, fileName, request.Body, ct));
        api.MapPut("/photos/{id:int}", (int id, PhotoEdit edit, StockService s, CancellationToken ct) => s.UpdatePhotoAsync(id, edit, ct));
        api.MapPost("/photos/{id:int}/cover", async (int id, StockService s, CancellationToken ct) => { await s.SetCoverAsync(id, ct); return Results.NoContent(); });
        api.MapDelete("/photos/{id:int}", async (int id, StockService s, CancellationToken ct) => { await s.DeletePhotoAsync(id, ct); return Results.NoContent(); });

        api.MapPost("/parts/{id:int}/datasheet", async (int id, [FromQuery] string fileName, HttpRequest request, StockService s, CancellationToken ct) =>
        {
            await s.UploadDatasheetAsync(id, fileName, request.Body, ct);
            return Results.NoContent();
        });
        api.MapPost("/parts/{id:int}/datasheet/fetch", async (int id, StockService s, CancellationToken ct) => { await s.FetchDatasheetAsync(id, ct); return Results.NoContent(); });
        api.MapDelete("/parts/{id:int}/datasheet", async (int id, StockService s, CancellationToken ct) => { await s.DeleteDatasheetAsync(id, ct); return Results.NoContent(); });

        // ---------- KiCad library ----------
        api.MapGet("/kicad", (StockService s, CancellationToken ct) => s.GetKicadLibraryAsync(ct));
        api.MapPost("/kicad/upload", ([FromQuery] string fileName, [FromQuery] int? partId, [FromQuery] string? source, HttpRequest request, StockService s, CancellationToken ct) =>
            s.UploadKicadFileAsync(fileName, request.Body, partId, source, ct));
        api.MapPut("/kicad/{id:int}", (int id, KicadItemEdit edit, StockService s, CancellationToken ct) => s.UpdateKicadItemAsync(id, edit, ct));
        api.MapDelete("/kicad/{id:int}", async (int id, StockService s, CancellationToken ct) => { await s.DeleteKicadItemAsync(id, ct); return Results.NoContent(); });

        // ---------- projects ----------
        api.MapGet("/projects", (StockService s, CancellationToken ct) => s.GetProjectsAsync(ct));
        api.MapGet("/projects/{id:int}", async (int id, StockService s, CancellationToken ct) => OkOrNotFound(await s.GetProjectAsync(id, ct)));
        api.MapPost("/projects", (ProjectEdit edit, StockService s, CancellationToken ct) => s.SaveProjectAsync(edit, ct));
        api.MapDelete("/projects/{id:int}", async (int id, StockService s, CancellationToken ct) => { await s.DeleteProjectAsync(id, ct); return Results.NoContent(); });
        api.MapPost("/projects/{id:int}/icon", (int id, [FromQuery] string fileName, HttpRequest request, StockService s, CancellationToken ct) =>
            s.SetProjectIconAsync(id, fileName, request.Body, ct));
        api.MapDelete("/projects/{id:int}/icon", (int id, StockService s, CancellationToken ct) => s.RemoveProjectIconAsync(id, ct));
        api.MapPost("/devices", (DeviceEdit edit, StockService s, CancellationToken ct) => s.SaveDeviceAsync(edit, ct));
        api.MapDelete("/devices/{id:int}", (int id, StockService s, CancellationToken ct) => s.DeleteDeviceAsync(id, ct));
        api.MapGet("/revisions/{id:int}", async (int id, StockService s, CancellationToken ct) => OkOrNotFound(await s.GetRevisionAsync(id, ct)));
        api.MapPost("/revisions", (RevisionEdit edit, StockService s, CancellationToken ct) => s.SaveRevisionAsync(edit, ct));
        api.MapDelete("/revisions/{id:int}", async (int id, StockService s, CancellationToken ct) => { await s.DeleteRevisionAsync(id, ct); return Results.NoContent(); });
        api.MapPost("/allocations", (AllocationEdit edit, StockService s, CancellationToken ct) => s.SaveAllocationAsync(edit, ct));
        api.MapDelete("/allocations/{id:int}", (int id, StockService s, CancellationToken ct) => s.DeleteAllocationAsync(id, ct));
        api.MapGet("/revisions/{id:int}/build", (int id, StockService s, CancellationToken ct) => s.PlanBuildAsync(id, ct));
        api.MapPost("/revisions/{id:int}/build", (int id, StockService s, CancellationToken ct) => s.ApplyBuildAsync(id, ct));
        api.MapPost("/revisions/{id:int}/bom/preview", (int id, CsvBody body, StockService s, CancellationToken ct) => s.PreviewBomAsync(id, body.Csv, ct));
        api.MapPost("/bom/apply", (BomApply apply, StockService s, CancellationToken ct) => s.ApplyBomAsync(apply, ct));

        // ---------- shopping and orders ----------
        api.MapGet("/shopping", (StockService s, CancellationToken ct) => s.GetShoppingListAsync(ct));
        api.MapDelete("/shopping/{partId:int}", async (int partId, StockService s, CancellationToken ct) => { await s.RemoveFromShoppingAsync(partId, ct); return Results.NoContent(); });
        api.MapGet("/orders", (StockService s, CancellationToken ct) => s.GetOrdersAsync(ct));
        api.MapGet("/orders/{id:int}", async (int id, StockService s, CancellationToken ct) => OkOrNotFound(await s.GetOrderAsync(id, ct)));
        api.MapPost("/orders", (OrderEdit edit, StockService s, CancellationToken ct) => s.SaveOrderAsync(edit, ct));
        api.MapDelete("/orders/{id:int}", async (int id, StockService s, CancellationToken ct) => { await s.DeleteOrderAsync(id, ct); return Results.NoContent(); });
        api.MapPost("/orders/add", (AddToOrder request, StockService s, CancellationToken ct) => s.AddToOrderAsync(request, ct));
        api.MapPost("/orders/{id:int}/receive", (int id, ReceiveOrder request, StockService s, CancellationToken ct) =>
        {
            request.OrderId = id;
            return s.ReceiveOrderAsync(request, ct);
        });
        api.MapPost("/order-lines", (OrderLineEdit edit, StockService s, CancellationToken ct) => s.SaveOrderLineAsync(edit, ct));
        api.MapDelete("/order-lines/{id:int}", (int id, StockService s, CancellationToken ct) => s.DeleteOrderLineAsync(id, ct));

        // ---------- locations ----------
        api.MapGet("/locations", (StockService s, CancellationToken ct) => s.GetLocationsAsync(ct));
        api.MapGet("/locations/{code}", async (string code, StockService s, CancellationToken ct) => OkOrNotFound(await s.GetLocationAsync(code, ct)));
        api.MapPost("/locations", (LocationEdit edit, StockService s, CancellationToken ct) => s.SaveLocationAsync(edit, ct));
        api.MapDelete("/locations/{id:int}", async (int id, StockService s, CancellationToken ct) => { await s.DeleteLocationAsync(id, ct); return Results.NoContent(); });

        // ---------- history and administration ----------
        api.MapGet("/activity", ([AsParameters] ActivityQuery query, StockService s, CancellationToken ct) => s.GetActivityAsync(query, ct));
        api.MapPost("/import", ([FromQuery] bool? replace, HttpRequest request, StockService s, CancellationToken ct) =>
            s.ImportAsync(request.Body, replace ?? false, ct));
        api.MapGet("/info", (StockService s, CancellationToken ct) => s.GetServerInfoAsync(ct));
        api.MapGet("/export", async (HttpContext context, StockExporter exporter, CancellationToken ct) =>
        {
            context.Response.ContentType = "application/zip";
            context.Response.Headers.ContentDisposition = $"attachment; filename=\"elecstock-{DateTime.Now:yyyyMMdd-HHmm}.zip\"";
            await exporter.WriteZipAsync(context.Response.Body, ct);
        });
    }

    static IResult OkOrNotFound<T>(T? value) where T : class => value == null ? Results.NotFound() : Results.Ok(value);
}
