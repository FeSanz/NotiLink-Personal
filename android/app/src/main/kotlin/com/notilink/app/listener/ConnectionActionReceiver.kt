package com.notilink.app.listener

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log
import com.notilink.app.net.ConnectionStatus
import com.notilink.app.net.NotificationConnection

/**
 * Handles the "Desconectar"/"Conectar" action button on the persistent status
 * notification (see NotiLinkNotificationListener). Toggling to disconnected
 * also drops the live socket immediately, rather than waiting for the next
 * failed write to notice.
 */
class ConnectionActionReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action == ACTION_POWER_OFF) {
            val listener = NotiLinkNotificationListener.instance
            if (listener != null) listener.powerOff()
            else {
                com.notilink.app.net.PowerState.set(context, true)
                NotificationConnection.close()
            }
            return
        }
        if (intent.action != ACTION_TOGGLE_CONNECTION) return
        val disconnect = !ConnectionStatus.manuallyDisconnected
        Log.i(TAG, "toggle received, disconnect=$disconnect")
        ConnectionStatus.setManuallyDisconnected(disconnect)
        if (disconnect) NotificationConnection.close()
    }

    companion object {
        private const val TAG = "NotiLink/ConnAction"
        const val ACTION_POWER_OFF = "com.notilink.app.ACTION_POWER_OFF"
        const val ACTION_TOGGLE_CONNECTION = "com.notilink.app.ACTION_TOGGLE_CONNECTION"
    }
}
