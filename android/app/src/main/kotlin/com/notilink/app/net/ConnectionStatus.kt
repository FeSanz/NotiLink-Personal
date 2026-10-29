package com.notilink.app.net

import android.content.Context

/**
 * Process-wide connection state, read by the persistent status notification
 * (NotiLinkNotificationListener) and written by NotificationConnection and
 * the notification's own "Desconectar"/"Conectar" action (ConnectionActionReceiver).
 *
 * [manuallyDisconnected] is the one thing that survives independently of the
 * socket itself — while true, NotificationConnection.send()/sendWithImages()
 * skip reconnecting entirely, so a user-requested disconnect actually stays
 * disconnected instead of silently reconnecting on the next notification.
 * It's persisted to SharedPreferences (not just held in memory) because the
 * listener service's process can be killed and respawned by the OS at any
 * time (Doze, OEM background limits) — without persistence, a respawn would
 * silently reset this to false and undo the user's own "Desconectar" the
 * moment the next notification or heartbeat fires.
 */
object ConnectionStatus {
    private const val PREFS = "notilink_connection"
    private const val KEY_MANUALLY_DISCONNECTED = "manually_disconnected"

    @Volatile var connected: Boolean = false
        private set
    @Volatile var manuallyDisconnected: Boolean = false
        private set

    /** The PC said goodbye (shut down, went to sleep, or NotiLink was closed there — see
     *  docs/PROTOCOL.md's pc.bye), or it's been unreachable for a while. While true the phone
     *  hides its own NotiLink notifications and only retries quietly every so often; the first
     *  successful reconnect clears it. In memory only: a process restart simply starts visible. */
    @Volatile var pcAway: Boolean = false
        private set

    /** When the connection was last lost (or the service started without one); 0 while connected. */
    @Volatile var disconnectedSinceMs: Long = System.currentTimeMillis()
        private set

    private var prefs: android.content.SharedPreferences? = null

    // A list, not a single nullable callback — the listener service uses this to refresh the
    // persistent status notification, and MainActivity separately wants live updates for its
    // "esperando conexión"/"conectado" line while it's in the foreground. A single slot would
    // mean whichever one attached last silently steals it from the other.
    private val changeListeners = mutableListOf<() -> Unit>()

    fun addChangeListener(listener: () -> Unit) { changeListeners.add(listener) }
    fun removeChangeListener(listener: () -> Unit) { changeListeners.remove(listener) }

    /** Fired by StatusDismissReceiver when the user clears the persistent status
     *  notification — lets the listener service know to stop silently reposting it. */
    var onDismissRequested: (() -> Unit)? = null

    /** Must be called once (e.g. from the listener service's onCreate) before anything else
     *  in this object is read, so [manuallyDisconnected] reflects the last real user choice
     *  from the moment the process comes back up, not just the in-memory default. */
    fun init(context: Context) {
        if (prefs != null) return
        val p = context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        prefs = p
        manuallyDisconnected = p.getBoolean(KEY_MANUALLY_DISCONNECTED, false)
    }

    fun setConnected(value: Boolean) {
        if (connected == value) return
        connected = value
        disconnectedSinceMs = if (value) 0L else System.currentTimeMillis()
        if (value) pcAway = false
        notifyChanged()
    }

    fun setPcAway(value: Boolean) {
        if (pcAway == value) return
        pcAway = value
        notifyChanged()
    }

    fun setManuallyDisconnected(value: Boolean) {
        if (manuallyDisconnected == value) return
        manuallyDisconnected = value
        prefs?.edit()?.putBoolean(KEY_MANUALLY_DISCONNECTED, value)?.apply()
        notifyChanged()
    }

    private fun notifyChanged() {
        changeListeners.toList().forEach { it.invoke() }
    }
}
