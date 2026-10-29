using QRCoder;

namespace NotiLink.App.Services;

internal static class QrCodeGenerator
{
    public static string ToDataUrl(string content)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data);
        var bytes = png.GetGraphic(10);
        return $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
    }
}
