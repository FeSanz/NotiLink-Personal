package com.notilink.app.net

import android.graphics.Bitmap
import java.io.ByteArrayOutputStream
import kotlin.math.max

/**
 * Bounds bandwidth/CPU per docs/PROTOCOL.md: notification images are
 * downscaled to a 512px max edge and JPEG q80 before ever going on the wire.
 * App icons are small already, so they're just PNG-encoded as-is.
 */
object ImageUtils {
    private const val MAX_EDGE = 512

    /** Photos/images inside a notification (not avatars or icons) — 512px was visibly blurry once
     *  zoomed on the PC; 1920px q90 stays well under the 8 MB frame cap (typically 0.3-1 MB). */
    const val CONTENT_MAX_EDGE = 1920
    const val CONTENT_JPEG_QUALITY = 90
    private const val JPEG_QUALITY = 80

    /** On a metered network (NetworkMonitor): smaller, lighter images. */
    @Volatile var dataSaver = false

    fun toDownscaledJpeg(bitmap: Bitmap, maxEdge: Int = MAX_EDGE, quality: Int = JPEG_QUALITY): ByteArray {
        @Suppress("NAME_SHADOWING") val maxEdge = if (dataSaver) minOf(maxEdge, 1080) else maxEdge
        @Suppress("NAME_SHADOWING") val quality = if (dataSaver) minOf(quality, 70) else quality
        val longestEdge = max(bitmap.width, bitmap.height)
        val scale = maxEdge.toFloat() / longestEdge
        val resized = if (scale < 1f) {
            Bitmap.createScaledBitmap(
                bitmap,
                (bitmap.width * scale).toInt().coerceAtLeast(1),
                (bitmap.height * scale).toInt().coerceAtLeast(1),
                true
            )
        } else {
            bitmap
        }
        return ByteArrayOutputStream().use { stream ->
            resized.compress(Bitmap.CompressFormat.JPEG, quality, stream)
            stream.toByteArray()
        }
    }

    fun toPng(bitmap: Bitmap): ByteArray =
        ByteArrayOutputStream().use { stream ->
            bitmap.compress(Bitmap.CompressFormat.PNG, 100, stream)
            stream.toByteArray()
        }
}
