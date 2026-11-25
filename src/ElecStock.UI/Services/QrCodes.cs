using Microsoft.AspNetCore.Components;
using QRCoder;

namespace ElecStock.UI.Services;

public static class QrCodes
{
    /// <summary>Inline SVG of a QR code, scaled by its container.</summary>
    public static MarkupString Svg(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(data).GetGraphic(8, "#000000", "#ffffff",
            drawQuietZones: false, sizingMode: SvgQRCode.SizingMode.ViewBoxAttribute);
        return new MarkupString(svg);
    }
}

/// <summary>Implemented by the MAUI app: the server address is a setting there.</summary>
public interface IServerSettings
{
    string ServerUrl { get; }
    Task SetServerUrlAsync(string url);
}
