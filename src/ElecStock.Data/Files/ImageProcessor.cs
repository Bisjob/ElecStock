using ElecStock.Contracts;
using SkiaSharp;

namespace ElecStock.Data.Files;

public sealed record StoredImage(string StoragePath, string ThumbPath, int Width, int Height);

/// <summary>
/// Stores an uploaded image: kept byte for byte when it is small enough and upright, else
/// re-encoded (EXIF orientation applied, longest side shrunk to <see cref="StorageOptions.MaxPhotoSide"/>;
/// PNG stays PNG so screenshots stay sharp). A WebP thumbnail is written next to it.
/// </summary>
public sealed class ImageProcessor(FileStore files)
{
    public static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"];

    public static bool IsImage(string fileName) => Extensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    public async Task<StoredImage> StoreAsync(Stream content, string fileName, CancellationToken ct, bool shrink = true)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();

        using var codec = SKCodec.Create(new SKMemoryStream(bytes))
            ?? throw new StockException($"'{fileName}' is not an image this server can read (JPEG, PNG, WebP, GIF, BMP).");
        var format = codec.EncodedFormat;
        var origin = codec.EncodedOrigin;
        bool rotated = origin != SKEncodedOrigin.TopLeft;
        using var decoded = SKBitmap.Decode(codec)
            ?? throw new StockException($"'{fileName}' could not be decoded.");
        using var upright = rotated ? Orient(decoded, origin) : decoded.Copy();
        bool tooBig = shrink && Math.Max(upright.Width, upright.Height) > files.Options.MaxPhotoSide;
        bool isPng = format == SKEncodedImageFormat.Png;

        string storagePath;
        int width, height;
        if (!rotated && !tooBig && format is SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp)
        {
            var ext = format switch { SKEncodedImageFormat.Jpeg => ".jpg", SKEncodedImageFormat.Png => ".png", _ => ".webp" };
            storagePath = await files.SaveAsync(FileStore.PhotosDir, ext, bytes, ct);
            (width, height) = (upright.Width, upright.Height);
        }
        else
        {
            using var output = tooBig ? Fit(upright, files.Options.MaxPhotoSide) : upright.Copy();
            var encoded = Encode(output, isPng ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, 88);
            storagePath = await files.SaveAsync(FileStore.PhotosDir, isPng ? ".png" : ".jpg", encoded, ct);
            (width, height) = (output.Width, output.Height);
        }

        using var thumb = Fit(upright, files.Options.ThumbSide);
        var thumbPath = await files.SaveAsync(FileStore.ThumbsDir, ".webp", Encode(thumb, SKEncodedImageFormat.Webp, 80), ct);
        return new StoredImage(storagePath, thumbPath, width, height);
    }

    /// <summary>A project icon: the image shrunk to <paramref name="side"/> pixels, stored as PNG to keep its transparency.</summary>
    public async Task<string> StoreIconAsync(byte[] bytes, string fileName, int side, CancellationToken ct)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(bytes))
            ?? throw new StockException($"'{fileName}' is not an image this server can read (SVG, PNG, JPEG, WebP, GIF, BMP).");
        var origin = codec.EncodedOrigin;
        using var decoded = SKBitmap.Decode(codec)
            ?? throw new StockException($"'{fileName}' could not be decoded.");
        using var upright = origin != SKEncodedOrigin.TopLeft ? Orient(decoded, origin) : decoded.Copy();
        using var icon = Fit(upright, side);
        return await files.SaveAsync(FileStore.IconsDir, ".png", Encode(icon, SKEncodedImageFormat.Png, 100), ct);
    }

    static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality)
            ?? throw new StockException($"The image could not be encoded as {format}.");
        return data.ToArray();
    }

    /// <summary>A copy no larger than <paramref name="side"/> on its longest side.</summary>
    static SKBitmap Fit(SKBitmap source, int side)
    {
        double scale = Math.Min(1.0, (double)side / Math.Max(source.Width, source.Height));
        if (scale >= 1.0) return source.Copy();
        var info = new SKImageInfo(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)),
            SKColorType.Rgba8888, SKAlphaType.Premul);
        return source.Resize(info, Sampling) ?? throw new StockException("The image could not be resized.");
    }

    /// <summary>Applies the EXIF orientation so the bitmap is upright.</summary>
    static SKBitmap Orient(SKBitmap src, SKEncodedOrigin origin)
    {
        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int w = src.Width, h = src.Height;
        var dst = new SKBitmap(swap ? h : w, swap ? w : h, src.ColorType, src.AlphaType);
        using var canvas = new SKCanvas(dst);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: canvas.Translate(w, 0); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.BottomRight: canvas.Translate(w, h); canvas.RotateDegrees(180); break;
            case SKEncodedOrigin.BottomLeft: canvas.Translate(0, h); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.LeftTop: canvas.RotateDegrees(90); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.RightTop: canvas.Translate(h, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: canvas.Translate(h, w); canvas.RotateDegrees(90); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.LeftBottom: canvas.Translate(0, w); canvas.RotateDegrees(270); break;
        }
        using var image = SKImage.FromBitmap(src);
        canvas.DrawImage(image, 0, 0, SKSamplingOptions.Default);
        return dst;
    }
}
