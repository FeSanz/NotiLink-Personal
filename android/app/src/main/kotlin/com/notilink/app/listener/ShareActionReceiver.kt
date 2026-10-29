package com.notilink.app.listener

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import com.notilink.app.net.NotificationConnection
import com.notilink.app.net.PcSettings
import com.notilink.app.net.Protocol
import org.json.JSONObject

/**
 * Handles the "Compartir pantalla"/"Compartir audio" actions on the persistent status
 * notification, and "Detener" on the sharing-status notification (see
 * NotiLinkNotificationListener.buildStatusNotification/buildSharingNotification) — lets the
 * phone kick off (or stop) a screen/audio share with the PC, rather than that only ever being
 * something you start by clicking around on the PC itself.
 */
class ShareActionReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val host = PcSettings.getPcIp(context) ?: return
        when (intent.action) {
            ACTION_REQUEST_SCREEN -> NotificationConnection.send(host, Protocol.buildEnvelope("share.request", JSONObject().put("type", "screen")))
            ACTION_REQUEST_AUDIO -> NotificationConnection.send(host, Protocol.buildEnvelope("share.request", JSONObject().put("type", "audio")))
            ACTION_STOP_SHARE -> NotificationConnection.send(host, Protocol.buildEnvelope("share.stop.request", JSONObject()))
            ACTION_RETURN_APP_SHARE -> intent.getStringExtra(EXTRA_PACKAGE)?.let { pkg ->
                NotificationConnection.send(host, Protocol.buildEnvelope("share.app.return", JSONObject().put("packageName", pkg)))
            }
            ACTION_STOP_APP_SHARE -> intent.getStringExtra(EXTRA_PACKAGE)?.let { pkg ->
                NotificationConnection.send(host, Protocol.buildEnvelope("share.stop.app", JSONObject().put("packageName", pkg)))
            }
        }
    }

    companion object {
        const val ACTION_REQUEST_SCREEN = "com.notilink.app.ACTION_REQUEST_SCREEN_SHARE"
        const val ACTION_REQUEST_AUDIO = "com.notilink.app.ACTION_REQUEST_AUDIO_SHARE"
        const val ACTION_STOP_SHARE = "com.notilink.app.ACTION_STOP_SHARE"
        const val ACTION_STOP_APP_SHARE = "com.notilink.app.ACTION_STOP_APP_SHARE"
        const val ACTION_RETURN_APP_SHARE = "com.notilink.app.ACTION_RETURN_APP_SHARE"
        const val EXTRA_PACKAGE = "packageName"
    }
}
