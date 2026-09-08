using QRCoder;

namespace Wmsfo.Api.Security;

// api.md 14 / 22: enrollment QR codes are PNG, 8 pixels per module, error
// correction M. QRCoder's PngByteQRCode encoder produces the bytes; the caller
// wraps them in a `data:image/png;base64,...` URL for the JSON response.
public static class QrRenderer
{
    public const int PixelsPerModule = 8;
    public const QRCodeGenerator.ECCLevel ErrorCorrection = QRCodeGenerator.ECCLevel.M;

    public static byte[] RenderPng(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, ErrorCorrection);
        using var qr = new PngByteQRCode(data);
        return qr.GetGraphic(PixelsPerModule);
    }

    public static string RenderPngDataUrl(string payload)
    {
        var bytes = RenderPng(payload);
        return "data:image/png;base64," + Convert.ToBase64String(bytes);
    }
}
