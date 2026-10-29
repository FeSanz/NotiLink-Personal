package com.notilink.app.net

import android.app.WallpaperManager
import android.content.Context
import android.util.Log
import androidx.core.graphics.drawable.toBitmap
import org.json.JSONObject
import java.util.UUID

/**
 * Shared by both the listener service (resync/fresh-connect) and MainActivity (retried
 * immediately after the user grants the read-photos permission, rather than waiting for the
 * next reconnect cycle to pick it up) — best-effort: some OEMs restrict wallpaper peeking or
 * gate it on the legacy storage permission even past API 33, so any failure here just means no
 * background on the PC, never a crash.
 */
object WallpaperSync {
    private const val TAG = "NotiLink/Wallpaper"

    fun send(context: Context, host: String) {
        if (NetworkMonitor.current.metered) return // purely cosmetic, not worth mobile data
        try {
            val drawable = WallpaperManager.getInstance(context).drawable ?: return
            val jpeg = ImageUtils.toDownscaledJpeg(drawable.toBitmap(), maxEdge = 900, quality = 70)
            val payload = JSONObject().put("transferId", UUID.randomUUID().toString())
            NotificationConnection.sendWithImage(host, Protocol.buildEnvelope("wallpaper.data", payload), jpeg)
        } catch (e: Exception) {
            Log.w(TAG, "failed to read wallpaper: ${e.message}")
        }
    }
}
