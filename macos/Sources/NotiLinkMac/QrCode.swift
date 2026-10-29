import CoreImage
import AppKit

/// Mirrors QrCodeGenerator.cs — the QR payload is just the PC's own raw IPv4 string (confirmed
/// against the Android scanner, MainActivity.kt: it treats the scanned content as a bare IP,
/// not JSON/a URI), so this only needs to encode a string to a PNG data: URL.
enum QrCode {
    static func dataUrl(for content: String) -> String? {
        guard let data = content.data(using: .isoLatin1) else { return nil }
        let filter = CIFilter(name: "CIQRCodeGenerator")
        filter?.setValue(data, forKey: "inputMessage")
        filter?.setValue("Q", forKey: "inputCorrectionLevel")
        guard let outputImage = filter?.outputImage else { return nil }

        // The generator produces a tiny (~29x29) image; scale up for a crisp on-screen QR code.
        let scale: CGFloat = 10
        let scaled = outputImage.transformed(by: CGAffineTransform(scaleX: scale, y: scale))

        let rep = NSCIImageRep(ciImage: scaled)
        let image = NSImage(size: rep.size)
        image.addRepresentation(rep)

        guard let tiffData = image.tiffRepresentation,
              let bitmap = NSBitmapImageRep(data: tiffData),
              let pngData = bitmap.representation(using: .png, properties: [:]) else { return nil }

        return "data:image/png;base64,\(pngData.base64EncodedString())"
    }
}
