package com.notilink.app.listener

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import com.notilink.app.net.ConnectionStatus

/** Fires when the user clears the persistent status notification (see
 *  NotiLinkNotificationListener.buildStatusNotification's setDeleteIntent) — tells the
 *  listener to stop silently reposting it until the connection state actually changes. */
class StatusDismissReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        ConnectionStatus.onDismissRequested?.invoke()
    }
}
