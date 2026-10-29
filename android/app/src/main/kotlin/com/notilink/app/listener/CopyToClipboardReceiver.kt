package com.notilink.app.listener

import android.app.NotificationManager
import android.content.BroadcastReceiver
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.widget.Toast

/**
 * "Copiar" action button on the notification NotiLink posts for a PC->phone share
 * (NotiLinkNotificationListener.showSharedNotification) — copies the shared text/link to the
 * clipboard and dismisses the notification, the same one-tap flow a native app's own
 * "copy link" action would give.
 */
class CopyToClipboardReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != ACTION_COPY) return
        val text = intent.getStringExtra(EXTRA_TEXT) ?: return
        val clipboard = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        clipboard.setPrimaryClip(ClipData.newPlainText("NotiLink", text))
        Toast.makeText(context, "Copiado", Toast.LENGTH_SHORT).show()

        val notificationId = intent.getIntExtra(EXTRA_NOTIFICATION_ID, -1)
        if (notificationId != -1) {
            val manager = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            manager.cancel(notificationId)
        }
    }

    companion object {
        const val ACTION_COPY = "com.notilink.app.ACTION_COPY_TO_CLIPBOARD"
        const val EXTRA_TEXT = "text"
        const val EXTRA_NOTIFICATION_ID = "notificationId"
    }
}
