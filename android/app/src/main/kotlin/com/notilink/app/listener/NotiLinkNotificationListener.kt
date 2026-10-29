package com.notilink.app.listener

import android.Manifest
import android.annotation.SuppressLint
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.RemoteInput
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.media.AudioManager
import android.content.pm.PackageManager
import android.media.RingtoneManager
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.wifi.WifiInfo
import android.net.wifi.WifiManager
import android.os.BatteryManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.os.VibrationEffect
import android.os.Vibrator
import android.service.notification.StatusBarNotification
import android.service.notification.NotificationListenerService
import android.telephony.SubscriptionManager
import android.telephony.TelephonyManager
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import androidx.core.graphics.drawable.toBitmap
import com.notilink.app.filter.AppFilterStore
import com.notilink.app.filter.AppIconLoader
import com.notilink.app.filter.AppIdentity
import com.notilink.app.filter.InstalledAppsProvider
import com.notilink.app.net.ConnectionStatus
import com.notilink.app.net.ImageUtils
import com.notilink.app.net.DiagLog
import com.notilink.app.net.MediaSessionWatcher
import com.notilink.app.net.NotificationConnection
import com.notilink.app.net.NetworkMonitor
import com.notilink.app.net.PcSettings
import com.notilink.app.net.Protocol
import com.notilink.app.net.WallpaperSync
import com.notilink.app.pairing.PairingState
import com.notilink.app.pairing.PairingTokenStore
import com.notilink.app.ui.MainActivity
import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledExecutorService
import java.util.concurrent.TimeUnit

class NotiLinkNotificationListener : NotificationListenerService() {

    private val ownPackage by lazy { applicationContext.packageName }
    private val primaryUserId by lazy { android.os.Process.myUserHandle().hashCode() }
    private var heartbeatScheduler: ScheduledExecutorService? = null

    /** The composite identity (see AppIdentity) a notification's own package+profile maps to —
     *  distinguishes a Dual Messenger clone from the original app of the same package name. */
    private fun identityKey(sbn: StatusBarNotification): String {
        val userId = sbn.user.hashCode()
        return AppIdentity.key(sbn.packageName, if (userId == primaryUserId) 0 else userId)
    }

    /** Packages whose icon has already been pushed this process lifetime — icons rarely
     *  change, so a resend after the next process restart is a harmless, cheap redundancy. */
    private val iconSentForPackage = mutableSetOf<String>()

    /** Not real notifications a user would want mirrored (e.g. the charging/battery
     *  indicator) — excluded entirely so they never clutter the list or get swept up
     *  by "delete all" (which only ever touches notifications this filter let through). */
    private val systemPackages = setOf("android", "com.android.systemui")

    /** A genuine connect/disconnect flip is exactly the case the user said should bring the
     *  status notification back even after they've dismissed it. Held as a field (not an
     *  inline lambda) so onDestroy can remove this exact instance from
     *  ConnectionStatus's listener list. */
    // Connection changes arrive on network/heartbeat threads; foreground start/stop must happen
    // on the main thread (confirmed: stopForeground from the heartbeat thread silently left the
    // foreground notification in place on this Samsung build).
    private val mainHandler = Handler(Looper.getMainLooper())
    // While connected, keep the CPU and the Wi-Fi radio awake: with the screen locked, Huawei/
    // HarmonyOS (and Doze generally) suspend the CPU and put Wi-Fi in power-save, so the socket and
    // the 8s heartbeat stall until the PC's watchdog declares the phone gone. Released the moment
    // the connection drops, so a disconnected phone costs nothing extra.
    private var connectionWakeLock: PowerManager.WakeLock? = null
    private var connectionWifiLock: WifiManager.WifiLock? = null

    @Suppress("DEPRECATION")
    private fun holdConnectionLocks(hold: Boolean) {
        try {
            if (hold) {
                if (connectionWakeLock == null) {
                    val pm = applicationContext.getSystemService(Context.POWER_SERVICE) as PowerManager
                    connectionWakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "NotiLink:connection").apply { setReferenceCounted(false) }
                }
                if (connectionWifiLock == null) {
                    val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
                    connectionWifiLock = wm.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "NotiLink:connection").apply { setReferenceCounted(false) }
                }
                if (connectionWakeLock?.isHeld == false) connectionWakeLock?.acquire()
                if (connectionWifiLock?.isHeld == false) connectionWifiLock?.acquire()
            } else {
                if (connectionWakeLock?.isHeld == true) connectionWakeLock?.release()
                if (connectionWifiLock?.isHeld == true) connectionWifiLock?.release()
            }
        } catch (e: Exception) {
            DiagLog.w(TAG, "connection locks (hold=$hold) failed: ${e.javaClass.simpleName} ${e.message}")
        }
    }

    private val connectionChangeListener: () -> Unit = {
        holdConnectionLocks(ConnectionStatus.connected)
        if (!ConnectionStatus.connected) com.notilink.app.net.MediaLevels.stop()
        mainHandler.post {
            if (ConnectionStatus.pcAway) {
                enterQuietMode()
            } else {
                leaveQuietMode()
                statusNotificationDismissed = false
                // A connect/disconnect brings the status notification back (for the auto-hide
                // window) even if the previous one already hid itself.
                if (statusHiddenByTimer) { statusHiddenByTimer = false; promoteToForeground() }
                updateStatusNotification()
                scheduleStatusAutoHide()
            }
        }
    }

    // --- Auto-hide (PC Ajustes, opt-in): the connected/disconnected status notification shows
    // for N seconds after each change and then removes itself — which also means leaving
    // foreground until the next change, like quiet mode. ---
    @Volatile private var statusHiddenByTimer = false
    private val hideStatusRunnable = Runnable { hideStatusByTimer() }

    private fun scheduleStatusAutoHide() {
        mainHandler.removeCallbacks(hideStatusRunnable)
        val seconds = PcSettings.getStatusAutoHideSeconds(applicationContext)
        if (seconds > 0) mainHandler.postDelayed(hideStatusRunnable, seconds * 1000L)
    }

    private fun hideStatusByTimer() {
        if (ConnectionStatus.pcAway || statusHiddenByTimer) return
        statusHiddenByTimer = true
        try { stopForeground(STOP_FOREGROUND_REMOVE) } catch (e: Exception) {
            DiagLog.w(TAG, "stopForeground (auto-hide) failed: ${e.javaClass.simpleName} ${e.message}")
        }
        (getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager).cancel(STATUS_NOTIFICATION_ID)
    }

    private fun applyStatusAutoHide(seconds: Int) {
        PcSettings.setStatusAutoHideSeconds(applicationContext, seconds)
        mainHandler.post {
            if (seconds <= 0 && statusHiddenByTimer && !ConnectionStatus.pcAway) {
                statusHiddenByTimer = false
                promoteToForeground()
            }
            mainHandler.removeCallbacks(hideStatusRunnable)
            if (seconds > 0 && !statusHiddenByTimer) mainHandler.postDelayed(hideStatusRunnable, seconds * 1000L)
        }
    }

    // --- Quiet mode: the PC is off or NotiLink is closed there (pc.bye, or unreachable for
    // QUIET_AFTER_MS). Every NotiLink notification on the phone goes away — the persistent status
    // one included, which means leaving foreground — until the PC is back. ---
    @Volatile private var quiet = false

    private fun enterQuietMode() {
        if (quiet) return
        quiet = true
        mainHandler.removeCallbacks(hideStatusRunnable)
        statusHiddenByTimer = false
        DiagLog.i(TAG, "quiet mode: PC away, hiding NotiLink notifications")
        try { stopForeground(STOP_FOREGROUND_REMOVE) } catch (e: Exception) {
            DiagLog.w(TAG, "stopForeground failed: ${e.javaClass.simpleName} ${e.message}")
        }
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        manager.cancel(STATUS_NOTIFICATION_ID)
        cancelShareNotifications(manager)
    }

    private fun leaveQuietMode() {
        if (!quiet) return
        quiet = false
        DiagLog.i(TAG, "quiet mode off: PC is back")
        promoteToForeground()
    }

    private fun cancelShareNotifications(manager: NotificationManager) {
        com.notilink.app.net.ShareState.clear()
        com.notilink.app.ui.RecentAppsWidget.refresh(this)
        manager.cancel(SHARE_NOTIFICATION_ID)
        synchronized(sharedAppNotifications) {
            for (pkg in sharedAppNotifications.keys) manager.cancel(appShareNotificationId(pkg))
            sharedAppNotifications.clear()
        }
    }

    private fun promoteToForeground() {
        // Promoting to a foreground service is what keeps the connection alive through Doze —
        // but it must never be allowed to crash-loop the listener if a stricter OEM/OS variant
        // rejects it; losing the foreground promotion is far better than losing notifications
        // entirely. (Re-promoting from the background after quiet mode can be refused on newer
        // Android versions — then the status notification is just posted as a normal one.)
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
                startForeground(
                    STATUS_NOTIFICATION_ID, buildStatusNotification(),
                    android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE
                )
            } else {
                startForeground(STATUS_NOTIFICATION_ID, buildStatusNotification())
            }
        } catch (e: Exception) {
            Log.w(TAG, "startForeground failed, continuing without it: ${e.message}")
            DiagLog.w(TAG, "startForeground failed: ${e.javaClass.simpleName}")
        }
    }

    override fun onCreate() {
        super.onCreate()
        NotificationConnection.init(applicationContext)
        ConnectionStatus.init(applicationContext)
        DiagLog.init(applicationContext)
        // Switched off with "Apagar": the system restarts a killed listener on its own, which must
        // not bring the status notification (or anything else) back until the app is opened.
        if (com.notilink.app.net.PowerState.isOff(applicationContext)) return
        createStatusChannel()
        promoteToForeground()
        scheduleStatusAutoHide()
        ConnectionStatus.addChangeListener(connectionChangeListener)
        ConnectionStatus.onDismissRequested = { statusNotificationDismissed = true }
    }

    private var lastBatteryLevel = -1
    private var lastBatteryCharging: Boolean? = null
    private val batteryReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            val host = PcSettings.getPcIp(applicationContext) ?: return
            handleBatteryIntent(intent, host)
        }
    }
    private val wallpaperReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            val host = PcSettings.getPcIp(applicationContext) ?: return
            sendWallpaper(host)
        }
    }
    // android.media.VOLUME_CHANGED_ACTION has no public SDK constant (it's an AOSP-internal
    // broadcast), but it's the same de facto stable action widely relied on by real-world volume-
    // control/widget apps — sent with EXTRA_VOLUME_STREAM_TYPE/EXTRA_VOLUME_STREAM_VALUE whenever
    // any stream's volume changes, including from the phone's own hardware buttons.
    private val volumeReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            if (intent.getIntExtra("android.media.EXTRA_VOLUME_STREAM_TYPE", -1) != AudioManager.STREAM_MUSIC) return
            val host = PcSettings.getPcIp(applicationContext) ?: return
            sendCurrentVolume(host)
        }
    }

    // Fires on ANY interruption-filter change — including the user toggling Do No Disturb from
    // Quick Settings, a Bedtime Mode schedule, etc., not just a change this app itself made — so
    // the PC's button always reflects the phone's real current state regardless of who changed it.
    // Auto-rotate lives in Settings.System, which has no broadcast — a ContentObserver on that one
    // key fires on every change (Quick Settings tile, Settings app, or this app's own setRotation),
    // so the PC's button always shows the phone's real state.
    private val rotationObserver by lazy {
        object : android.database.ContentObserver(Handler(Looper.getMainLooper())) {
            override fun onChange(selfChange: Boolean) {
                PcSettings.getPcIp(applicationContext)?.let { sendCurrentRotation(it) }
            }
        }
    }

    private fun sendCurrentRotation(host: String) {
        val autoRotate = try {
            android.provider.Settings.System.getInt(contentResolver, android.provider.Settings.System.ACCELEROMETER_ROTATION, 0) == 1
        } catch (e: Exception) { false }
        NotificationConnection.send(host, Protocol.buildEnvelope("rotation.state", JSONObject().put("active", autoRotate)))
    }

    /** Needs the "Modificar ajustes del sistema" special grant (Settings.System.canWrite) — without
     *  it putInt throws SecurityException, so it's checked first just to log why nothing changed. */
    private fun setRotation(payload: JSONObject) {
        if (!payload.has("active")) return
        if (!android.provider.Settings.System.canWrite(applicationContext)) {
            DiagLog.w(TAG, "setRotation: WRITE_SETTINGS not granted, ignoring")
            return
        }
        try {
            android.provider.Settings.System.putInt(contentResolver,
                android.provider.Settings.System.ACCELEROMETER_ROTATION, if (payload.optBoolean("active", false)) 1 else 0)
        } catch (e: Exception) {
            DiagLog.w(TAG, "setRotation failed: ${e.message}")
        }
    }

    private val dndReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            val host = PcSettings.getPcIp(applicationContext) ?: return
            sendCurrentDnd(host)
        }
    }

    private val mediaSessionWatcher by lazy {
        MediaSessionWatcher(
            context = applicationContext,
            onStateChanged = { state -> sendMediaState(state) },
            onNoSession = { sendMediaState(null) },
            onCallStateChanged = { active -> sendCallState(active) },
        )
    }

    /** "Apagar": stops NotiLink completely: closes the PC connection, removes its notifications,
     *  drops the foreground state and the wake/Wi-Fi locks, unbinds from the system and ends the
     *  process, so nothing of the app keeps running in the background. Opening the app again
     *  (MainActivity) turns it back on. */
    fun powerOff() {
        mainHandler.post {
            DiagLog.i(TAG, "power off requested from the notification")
            com.notilink.app.net.PowerState.set(applicationContext, true)
            ConnectionStatus.setManuallyDisconnected(true)
            NotificationConnection.close()
            holdConnectionLocks(false)
            stopHeartbeat()
            com.notilink.app.net.MediaLevels.stop()
            try { stopForeground(STOP_FOREGROUND_REMOVE) } catch (e: Exception) { }
            (getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager).cancelAll()
            try { requestUnbind() } catch (e: Exception) { }
            mainHandler.postDelayed({ android.os.Process.killProcess(android.os.Process.myPid()) }, 600)
        }
    }

    override fun onListenerConnected() {
        super.onListenerConnected()
        instance = this
        if (com.notilink.app.net.PowerState.isOff(applicationContext)) {
            // Switched off from the notification and the system bound us again (boot, update):
            // stay off until the user opens the app.
            try { requestUnbind() } catch (e: Exception) { }
            return
        }
        Log.i(TAG, "listener connected")
        NotificationConnection.onCommand = { type, payload -> handleCommand(type, payload) }
        NotificationConnection.onBinaryFrame = { bytes -> handleBinaryFrame(bytes) }
        // Fires on every fresh reconnect, regardless of which send() happened to notice the old
        // socket was dead — see NotificationConnection.onConnectionEstablished's own comment for
        // why this can't just be the resync-on-reconnect callback passed to one specific send().
        NetworkMonitor.start(applicationContext)
        try {
            var added = false
            for (sbn in activeNotifications ?: emptyArray()) {
                if (sbn.packageName == ownPackage || sbn.packageName in systemPackages) continue
                if (com.notilink.app.filter.NotifyingApps.add(applicationContext, identityKey(sbn))) added = true
            }
            if (added) scheduleAppSync()
        } catch (e: Exception) { /* not connected yet */ }
        NotificationConnection.onConnectionEstablished = {
            PcSettings.getPcIp(applicationContext)?.let { host ->
                NetworkMonitor.report(host)
                sendAppSync(host)
                resyncActiveNotifications()
                sendCurrentBattery(host)
                sendWallpaper(host)
                sendCurrentVolume(host)
                sendCurrentDnd(host)
                sendCurrentRotation(host)
                com.notilink.app.ui.AppPickerActivity.Recents.push(applicationContext)
            }
        }
        applicationContext.registerReceiver(batteryReceiver, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        applicationContext.registerReceiver(wallpaperReceiver, IntentFilter(Intent.ACTION_WALLPAPER_CHANGED))
        applicationContext.registerReceiver(volumeReceiver, IntentFilter("android.media.VOLUME_CHANGED_ACTION"))
        applicationContext.registerReceiver(dndReceiver, IntentFilter(NotificationManager.ACTION_INTERRUPTION_FILTER_CHANGED))
        contentResolver.registerContentObserver(
            android.provider.Settings.System.getUriFor(android.provider.Settings.System.ACCELEROMETER_ROTATION), false, rotationObserver)
        PcSettings.getPcIp(applicationContext)?.let { sendAppSync(it); sendWallpaper(it); sendCurrentVolume(it); sendCurrentDnd(it); sendCurrentRotation(it) }
        resyncActiveNotifications()
        startHeartbeat()
        mediaSessionWatcher.start()
    }

    override fun onListenerDisconnected() {
        super.onListenerDisconnected()
        stopHeartbeat()
        mediaSessionWatcher.stop()
        try { applicationContext.unregisterReceiver(batteryReceiver) } catch (e: Exception) { /* not registered */ }
        try { applicationContext.unregisterReceiver(wallpaperReceiver) } catch (e: Exception) { /* not registered */ }
        try { applicationContext.unregisterReceiver(volumeReceiver) } catch (e: Exception) { /* not registered */ }
        try { applicationContext.unregisterReceiver(dndReceiver) } catch (e: Exception) { /* not registered */ }
        try { contentResolver.unregisterContentObserver(rotationObserver) } catch (e: Exception) { /* not registered */ }
    }

    override fun onDestroy() {
        super.onDestroy()
        holdConnectionLocks(false)
        com.notilink.app.net.MediaLevels.stop()
        NetworkMonitor.stop()
        stopHeartbeat()
        mediaSessionWatcher.stop()
        ConnectionStatus.removeChangeListener(connectionChangeListener)
        ConnectionStatus.onDismissRequested = null
        try { applicationContext.unregisterReceiver(batteryReceiver) } catch (e: Exception) { /* not registered */ }
        try { applicationContext.unregisterReceiver(wallpaperReceiver) } catch (e: Exception) { /* not registered */ }
        try { applicationContext.unregisterReceiver(volumeReceiver) } catch (e: Exception) { /* not registered */ }
        try { applicationContext.unregisterReceiver(dndReceiver) } catch (e: Exception) { /* not registered */ }
        try { contentResolver.unregisterContentObserver(rotationObserver) } catch (e: Exception) { /* not registered */ }
    }

    /** Sent once per fresh connection (and again whenever the wallpaper actually changes) so
     *  the PC can show it very faintly behind the notification list — see WallpaperSync for why
     *  MainActivity also calls this directly right after the read-photos permission is granted. */
    private fun sendWallpaper(host: String) = WallpaperSync.send(applicationContext, host)

    /** STREAM_MUSIC specifically — the stream that actually controls media/call-adjacent
     *  playback volume, the one a remote volume slider is meant for. No runtime permission
     *  needed for basic get/set on it, unlike do-not-disturb-affecting streams. */
    private fun sendCurrentVolume(host: String) {
        val audioManager = getSystemService(Context.AUDIO_SERVICE) as? AudioManager ?: return
        val payload = JSONObject().apply {
            put("level", audioManager.getStreamVolume(AudioManager.STREAM_MUSIC))
            put("max", audioManager.getStreamMaxVolume(AudioManager.STREAM_MUSIC))
        }
        NotificationConnection.send(host, Protocol.buildEnvelope("volume.state", payload))
    }

    /** FLAG_SHOW_UI mirrors what pressing the phone's own hardware volume buttons would do —
     *  the same native overlay appears, so a change made from the PC still looks/feels like a
     *  normal volume change if the user happens to be looking at the phone when it happens. */
    private fun setVolume(payload: JSONObject) {
        val level = if (payload.has("level")) payload.optInt("level", -1) else return
        if (level < 0) return
        val audioManager = getSystemService(Context.AUDIO_SERVICE) as? AudioManager ?: return
        try {
            audioManager.setStreamVolume(AudioManager.STREAM_MUSIC, level, AudioManager.FLAG_SHOW_UI)
        } catch (e: Exception) {
            Log.w(TAG, "setVolume failed: ${e.message}")
        }
    }

    /** Any interruption filter other than ALL counts as "Do Not Disturb is on" for the PC
     *  button's lit/unlit state — not just the specific PRIORITY mode setDnd below turns it on
     *  with, since the user (or a Bedtime Mode schedule, etc.) could have set ALARMS-only or
     *  total silence directly on the phone and the button should still show it as active. */
    private fun sendCurrentDnd(host: String) {
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as? NotificationManager ?: return
        val active = manager.currentInterruptionFilter != NotificationManager.INTERRUPTION_FILTER_ALL
        NotificationConnection.send(host, Protocol.buildEnvelope("dnd.state", JSONObject().put("active", active)))
    }

    /** Requires the separate "Notification Policy Access" permission (Settings.ACTION_
     *  NOTIFICATION_POLICY_ACCESS_SETTINGS) — a different, additional grant from ordinary
     *  Notification Access, silently no-ops without it (Android throws neither an exception nor
     *  gives any other signal, per NotificationManager's own docs, so isNotificationPolicyAccess
     *  Granted() is checked explicitly first to at least log why nothing happened). ON maps to
     *  INTERRUPTION_FILTER_PRIORITY — the same "Do Not Disturb" mode the phone's own Quick
     *  Settings tile defaults to — rather than total silence (NONE), which would also block
     *  alarms; OFF is the normal ALL filter. */
    private fun setDnd(payload: JSONObject) {
        if (!payload.has("active")) return
        val active = payload.optBoolean("active", false)
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as? NotificationManager ?: return
        if (!manager.isNotificationPolicyAccessGranted) {
            DiagLog.w(TAG, "setDnd: Notification Policy Access not granted, ignoring")
            return
        }
        try {
            manager.setInterruptionFilter(
                if (active) NotificationManager.INTERRUPTION_FILTER_PRIORITY else NotificationManager.INTERRUPTION_FILTER_ALL
            )
        } catch (e: Exception) {
            DiagLog.w(TAG, "setDnd failed: ${e.message}")
        }
    }

    /** Fired by MediaSessionWatcher.onCallStateChanged on both edges of Android's Telecom
     *  framework showing/hiding a call session — covers native SIM calls AND VoIP app calls
     *  (WhatsApp, Telegram, etc.) via their self-managed ConnectionService, confirmed directly
     *  from a real WhatsApp call log: WhatsApp itself never posts any notification while
     *  ringing, only Telecom's own session appears for that window. Can't distinguish incoming
     *  from outgoing at this layer (see MediaSessionWatcher's checkCallActive doc comment), so
     *  the PC just learns whether a call is happening right now — not who's calling or which
     *  direction — and shows/hides its own call banner for exactly that duration. */
    private fun sendCallState(active: Boolean) {
        if (active) startCallMonitor() else stopCallMonitor()
        val host = PcSettings.getPcIp(applicationContext) ?: return
        NotificationConnection.send(host, Protocol.buildEnvelope("call.state", JSONObject().put("state", if (active) "active" else "ended")))
    }

    // --- Persistent status notification: this IS the foreground-service notification
    // (NotificationListenerService can call startForeground on itself), so it both keeps
    // the process exempt from Doze's background network restrictions AND gives the user
    // a visible, always-current "connected / disconnected" indicator with a manual toggle. ---

    private fun createStatusChannel() {
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        val channel = NotificationChannel(
            STATUS_CHANNEL_ID, "Estado de conexión", NotificationManager.IMPORTANCE_LOW
        ).apply { setShowBadge(false) }
        manager.createNotificationChannel(channel)

        val shareChannel = NotificationChannel(
            SHARE_CHANNEL_ID, "Compartir pantalla/audio", NotificationManager.IMPORTANCE_LOW
        ).apply { setShowBadge(false) }
        manager.createNotificationChannel(shareChannel)
    }

    private fun buildStatusNotification(): Notification {
        val connected = ConnectionStatus.connected
        val manuallyDisconnected = ConnectionStatus.manuallyDisconnected
        val text = when {
            manuallyDisconnected -> "Desconectado (manual)"
            connected -> "Conectado a la PC"
            else -> "Buscando PC..."
        }

        val contentIntent = PendingIntent.getActivity(
            applicationContext, 0, Intent(applicationContext, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        val dismissIntent = PendingIntent.getBroadcast(
            applicationContext, 0,
            Intent(applicationContext, StatusDismissReceiver::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        fun broadcast(cls: Class<*>, action: String) = PendingIntent.getBroadcast(
            applicationContext, 0, Intent(applicationContext, cls).setAction(action),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        // Icon-only buttons (custom view): disconnect/connect, share screen, share audio (both
        // only while connected; a phone-initiated share uses the quality settings saved on the
        // PC, see app.js's onPhoneShareRequest) and "apagar", which stops NotiLink completely.
        val night = (resources.configuration.uiMode and android.content.res.Configuration.UI_MODE_NIGHT_MASK) ==
            android.content.res.Configuration.UI_MODE_NIGHT_YES
        val tint = if (night) 0xFFFFFFFF.toInt() else 0xFF202020.toInt()
        val views = android.widget.RemoteViews(packageName, com.notilink.app.R.layout.notification_status)
        views.setTextViewText(com.notilink.app.R.id.notif_text, text)
        views.setImageViewResource(
            com.notilink.app.R.id.notif_btn_toggle,
            if (manuallyDisconnected) com.notilink.app.R.drawable.ic_notif_link else com.notilink.app.R.drawable.ic_notif_link_off
        )
        views.setOnClickPendingIntent(com.notilink.app.R.id.notif_btn_toggle, broadcast(ConnectionActionReceiver::class.java, ConnectionActionReceiver.ACTION_TOGGLE_CONNECTION))
        views.setOnClickPendingIntent(com.notilink.app.R.id.notif_btn_power, broadcast(ConnectionActionReceiver::class.java, ConnectionActionReceiver.ACTION_POWER_OFF))
        val canShare = connected && !manuallyDisconnected
        val shareVisibility = if (canShare) android.view.View.VISIBLE else android.view.View.GONE
        views.setViewVisibility(com.notilink.app.R.id.notif_btn_screen, shareVisibility)
        views.setViewVisibility(com.notilink.app.R.id.notif_btn_audio, shareVisibility)
        views.setOnClickPendingIntent(com.notilink.app.R.id.notif_btn_screen, broadcast(ShareActionReceiver::class.java, ShareActionReceiver.ACTION_REQUEST_SCREEN))
        views.setOnClickPendingIntent(com.notilink.app.R.id.notif_btn_audio, broadcast(ShareActionReceiver::class.java, ShareActionReceiver.ACTION_REQUEST_AUDIO))
        for (id in intArrayOf(
            com.notilink.app.R.id.notif_btn_toggle, com.notilink.app.R.id.notif_btn_screen,
            com.notilink.app.R.id.notif_btn_audio, com.notilink.app.R.id.notif_btn_power
        )) views.setInt(id, "setColorFilter", tint)

        return NotificationCompat.Builder(applicationContext, STATUS_CHANNEL_ID)
            .setSmallIcon(com.notilink.app.R.drawable.ic_notification)
            .setContentTitle("NotiLink")
            .setContentText(text)
            .setStyle(NotificationCompat.DecoratedCustomViewStyle())
            .setCustomContentView(views)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .setContentIntent(contentIntent)
            .setDeleteIntent(dismissIntent)
            .build()
    }

    /** Shown only while the PC reports an active screen and/or audio share (see the
     *  "share.status" handler below) — separate from the always-on connection-status
     *  notification above so it doesn't clutter that one with conditional actions that only
     *  sometimes apply. Text reflects exactly what's active; the single "Detener" action stops
     *  both if both happen to be running (e.g. audio-only was already going and a full screen
     *  share with its own bundled audio got started on top of it) — the user asked for exactly
     *  one notification and one stop action covering whatever's active, not one per session. */
    private fun buildSharingNotification(screenSharing: Boolean, audioSharing: Boolean): Notification {
        val text = when {
            screenSharing && audioSharing -> "Compartiendo pantalla y audio con la PC"
            screenSharing -> "Compartiendo pantalla con la PC"
            audioSharing -> "Compartiendo audio con la PC"
            else -> "Compartiendo con la PC"
        }
        val stopIntent = PendingIntent.getBroadcast(
            applicationContext, 0,
            Intent(applicationContext, ShareActionReceiver::class.java).setAction(ShareActionReceiver.ACTION_STOP_SHARE),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        return NotificationCompat.Builder(applicationContext, SHARE_CHANNEL_ID)
            .setSmallIcon(com.notilink.app.R.drawable.ic_notification)
            .setContentTitle("NotiLink")
            .setContentText(text)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .addAction(0, "⏹ Detener", stopIntent)
            .build()
    }

    /** Some OEM shades let a low-importance ongoing notification be cleared despite
     *  setOngoing(true) — once that happens, respect it and stay gone until the connection
     *  state genuinely changes (or the app is reopened), instead of silently reappearing on
     *  every no-op refresh. */
    @Volatile private var statusNotificationDismissed = false

    private fun updateStatusNotification() {
        if (statusNotificationDismissed || ConnectionStatus.pcAway || statusHiddenByTimer) return
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        manager.notify(STATUS_NOTIFICATION_ID, buildStatusNotification())
    }

    /** Reads the current battery state on demand (e.g. right after reconnecting to a PC that
     *  just came back online) rather than waiting for the next real change to occur. */
    private fun sendCurrentBattery(host: String) {
        val intent = applicationContext.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED)) ?: return
        handleBatteryIntent(intent, host, forceSend = true)
    }

    private fun handleBatteryIntent(intent: Intent, host: String, forceSend: Boolean = false) {
        val level = intent.getIntExtra(BatteryManager.EXTRA_LEVEL, -1)
        val scale = intent.getIntExtra(BatteryManager.EXTRA_SCALE, -1)
        if (level < 0 || scale <= 0) return
        val percent = (level * 100) / scale
        val charging = intent.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) != 0

        if (!forceSend && percent == lastBatteryLevel && charging == lastBatteryCharging) return
        lastBatteryLevel = percent
        lastBatteryCharging = charging

        val powerManager = applicationContext.getSystemService(Context.POWER_SERVICE) as? PowerManager
        val payload = JSONObject().apply {
            put("level", percent)
            put("charging", charging)
            put("lowPowerMode", powerManager?.isPowerSaveMode ?: false)
            putWifiInfo(this)
            putCellularInfo(this)
        }
        NotificationConnection.send(host, Protocol.buildEnvelope("battery.update", payload))
    }

    /** SSID/signal are only readable with location permission granted (Android's own
     *  restriction, since Wi-Fi info can reveal location) — silently omitted otherwise, same
     *  "just don't show it" degradation as every other optional/cosmetic field here. */
    private fun putWifiInfo(payload: JSONObject) {
        val hasLocation = ContextCompat.checkSelfPermission(applicationContext, Manifest.permission.ACCESS_FINE_LOCATION) ==
            PackageManager.PERMISSION_GRANTED
        val info = if (hasLocation) findActiveWifiInfo() else null
        if (info != null && info.networkId != -1) {
            payload.put("wifiSsid", info.ssid?.trim('"') ?: JSONObject.NULL)
            payload.put("wifiLevel", WifiManager.calculateSignalLevel(info.rssi, 5))
        } else {
            payload.put("wifiSsid", JSONObject.NULL)
            payload.put("wifiLevel", JSONObject.NULL)
        }
    }

    /** Looks at the currently-connected Wi-Fi-transport network's own info via
     *  ConnectivityManager instead of the deprecated WifiManager.getConnectionInfo(), which
     *  tracks whichever network Android currently treats as the "default route" — on a phone
     *  with Wi-Fi AND mobile data both active (confirmed: intermittent on the S26 Ultra, never
     *  on the Z Flip, which has no cellular radio at all), Android's own network-switching
     *  heuristics can flip the default route to cellular for a moment even while Wi-Fi stays
     *  connected, which made getConnectionInfo() intermittently report "not connected" and drop
     *  the Wi-Fi icon on dual-connectivity phones specifically. Walking ConnectivityManager's
     *  actual network list for one that carries the Wi-Fi transport sidesteps that default-route
     *  ambiguity entirely — it finds the Wi-Fi connection whether or not it's "the" active one. */
    private fun findActiveWifiInfo(): WifiInfo? {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            val cm = applicationContext.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
            val wifiInfo = cm?.allNetworks
                ?.mapNotNull { cm.getNetworkCapabilities(it) }
                ?.firstOrNull { it.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) }
                ?.transportInfo as? WifiInfo
            if (wifiInfo != null) return wifiInfo
        }
        @Suppress("DEPRECATION")
        val wifiManager = applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
        @Suppress("DEPRECATION")
        return wifiManager?.connectionInfo
    }

    /** One entry per active SIM ("línea") — dual-SIM phones report two independent signals,
     *  each with its own carrier name, shown side by side on the PC. Requires READ_PHONE_STATE
     *  and API 28+ for SignalStrength.getLevel(); omitted entirely otherwise, same graceful
     *  degradation as Wi-Fi info above. */
    private fun putCellularInfo(payload: JSONObject) {
        val cellularArray = JSONArray()
        val hasPhoneState = ContextCompat.checkSelfPermission(applicationContext, Manifest.permission.READ_PHONE_STATE) ==
            PackageManager.PERMISSION_GRANTED
        if (hasPhoneState && Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            try {
                val subscriptionManager = applicationContext.getSystemService(Context.TELEPHONY_SUBSCRIPTION_SERVICE) as? SubscriptionManager
                val baseTelephonyManager = applicationContext.getSystemService(Context.TELEPHONY_SERVICE) as? TelephonyManager
                @Suppress("MissingPermission")
                val subscriptions = subscriptionManager?.activeSubscriptionInfoList ?: emptyList()
                for (sub in subscriptions) {
                    val tm = baseTelephonyManager?.createForSubscriptionId(sub.subscriptionId) ?: continue
                    val level = tm.signalStrength?.level ?: continue
                    val carrier = sub.carrierName?.toString()?.takeIf { it.isNotBlank() }
                        ?: sub.displayName?.toString()?.takeIf { it.isNotBlank() }
                        ?: "SIM"
                    cellularArray.put(JSONObject().apply {
                        put("carrierName", carrier)
                        put("level", level)
                    })
                }
            } catch (e: SecurityException) {
                // permission revoked mid-flight or blocked by OEM policy — just omit it
            } catch (e: Exception) {
                Log.w(TAG, "failed to read cellular signal: ${e.message}")
            }
        }
        payload.put("cellular", cellularArray)
    }

    /** "Buscar celular" — rings + vibrates for a few seconds so the phone can be located, the
     *  same idea as Android's own "Find My Device". No special permission beyond VIBRATE, which
     *  is a normal (not runtime-prompted) permission. */
    private fun ringDevice() {
        try {
            val vibrator = applicationContext.getSystemService(Context.VIBRATOR_SERVICE) as? Vibrator
            vibrator?.vibrate(VibrationEffect.createWaveform(longArrayOf(0, 400, 200, 400, 200, 400, 200, 400), -1))

            val ringtoneUri = RingtoneManager.getActualDefaultRingtoneUri(applicationContext, RingtoneManager.TYPE_RINGTONE)
            val ringtone = ringtoneUri?.let { RingtoneManager.getRingtone(applicationContext, it) }
            ringtone?.play()
            Handler(Looper.getMainLooper()).postDelayed({ try { ringtone?.stop() } catch (e: Exception) { } }, 4000)
        } catch (e: Exception) {
            Log.w(TAG, "ringDevice failed: ${e.message}")
        }
    }

    private fun startHeartbeat() {
        stopHeartbeat()
        val scheduler = Executors.newSingleThreadScheduledExecutor()
        heartbeatScheduler = scheduler
        scheduler.scheduleWithFixedDelay({
            val host = PcSettings.getPcIp(applicationContext) ?: return@scheduleWithFixedDelay
            // Resync-on-reconnect is handled once, globally, by
            // NotificationConnection.onConnectionEstablished — not repeated here, or a reconnect
            // discovered by this exact heartbeat send would resync twice.
            // No goodbye from the PC (power cut, crash, Wi-Fi gone): once it's been unreachable
            // for QUIET_AFTER_MS, go quiet the same way a real pc.bye would.
            val since = ConnectionStatus.disconnectedSinceMs
            if (!ConnectionStatus.connected && !ConnectionStatus.manuallyDisconnected && !ConnectionStatus.pcAway &&
                since > 0 && System.currentTimeMillis() - since > QUIET_AFTER_MS) {
                ConnectionStatus.setPcAway(true)
            }
            NotificationConnection.send(host, Protocol.buildEnvelope("heartbeat", JSONObject()))
            // Checked every tick (every 8s), not just on a fresh reconnect — a fresh reconnect
            // is rare once the connection is stable, which meant new log entries could sit on
            // the phone for a very long time before ever actually shipping. Only sent when the
            // content changed since last time, so a stable connection doesn't mean 8s
            // resends of the same unchanged log.
            maybeSendDiagLog(host)
        }, 0, 8, TimeUnit.SECONDS)
    }

    private var lastSentDiagLogHash = 0

    /** Ships the on-device rolling diagnostic log (DiagLog) to the PC whenever it's changed
     *  since the last send — see MainViewModel's "diag.log" handler and docs/PROTOCOL.md for
     *  where it lands on disk. USB/adb access to the phone isn't always available when
     *  something needs debugging, but a live NotiLink connection almost always is. */
    private fun maybeSendDiagLog(host: String) {
        val text = DiagLog.readAll()
        if (text.isBlank()) return
        val hash = text.hashCode()
        if (hash == lastSentDiagLogHash) return
        lastSentDiagLogHash = hash
        NotificationConnection.send(host, Protocol.buildEnvelope("diag.log", JSONObject().put("text", text)))
    }

    private fun stopHeartbeat() {
        heartbeatScheduler?.shutdownNow()
        heartbeatScheduler = null
    }

    private fun resyncActiveNotifications() {
        val host = PcSettings.getPcIp(applicationContext) ?: return
        val active = mirroredActiveNotifications()
        active.forEach {
            maybeSendIcon(host, it.packageName, identityKey(it))
            sendPosted(host, it, identityKey(it))
        }
        // Reconciles a gap notification.removed alone can't cover: a notification dismissed on
        // the phone *while disconnected* (confirmed real case — another app, e.g. Quick Share,
        // briefly switching the phone's network) has no connection to travel that removal event
        // over, so it's simply lost and the PC card sits there forever with nothing left on the
        // phone to confirm deletion for. Sending the complete current key set lets the PC clear
        // anything it's showing that isn't in this list — see docs/PROTOCOL.md's notification.sync.
        val keysJson = JSONArray().apply { active.forEach { put(it.key) } }
        NotificationConnection.send(host, Protocol.buildEnvelope("notification.sync", JSONObject().put("keys", keysJson)))
    }

    /** The same eligibility rule used everywhere a notification is mirrored or deleted: not
     *  our own app, not a group-summary placeholder, not a system package, and not blocked
     *  by the user's per-app filter (see AppFilterStore / the Apps screen). */
    private fun mirroredActiveNotifications(): List<StatusBarNotification> =
        activeNotifications
            ?.filter {
                it.packageName != ownPackage && it.packageName !in systemPackages && !it.isGroupSummary() &&
                    !it.notification.extras.containsKey(Notification.EXTRA_MEDIA_SESSION) &&
                    AppFilterStore.isAllowed(applicationContext, identityKey(it))
            }
            ?: emptyList()

    override fun onNotificationPosted(sbn: StatusBarNotification) {
        if (sbn.packageName == ownPackage || sbn.packageName in systemPackages) return
        // Remember every app that notifies (even without a launcher icon, e.g. Modos y rutinas)
        // so it shows up in the filter list and can be silenced; new ones → resend the list.
        if (com.notilink.app.filter.NotifyingApps.add(applicationContext, identityKey(sbn))) scheduleAppSync()
        // Apps like WhatsApp post both a per-conversation notification (the
        // one worth mirroring, updates in place with the latest message) and
        // a separate "N new messages" group-summary notification. Without
        // this filter both arrive as distinct entries and look like a dup.
        if (sbn.isGroupSummary()) return
        // Media playback notifications (Spotify, YouTube Music, ...) are already covered by
        // MediaSessionWatcher's own dedicated media.state -> persistent "now playing" bar —
        // mirroring them here too just showed the same song twice, once as a real card and
        // once in the bar.
        if (sbn.notification.extras.containsKey(Notification.EXTRA_MEDIA_SESSION)) return
        val identity = identityKey(sbn)
        // Filtered phone-side, before anything is ever transmitted — a blocked app's
        // content must never leave the device, not just be hidden in the PC UI.
        if (!AppFilterStore.isAllowed(applicationContext, identity)) return
        val host = PcSettings.getPcIp(applicationContext) ?: return
        maybeSendIcon(host, sbn.packageName, identity)
        sendPosted(host, sbn, identity)
    }

    /** Full installed-app inventory + current filter state, so the PC's Apps settings
     *  screen has something to show without a separate "get filter" round trip. Each entry's
     *  packageName is already the composite AppIdentity key (bare package for the primary
     *  profile, "pkg#userId" for a Dual Messenger clone), so no extra plumbing is needed here. */
    private val appSyncRunnable = Runnable { PcSettings.getPcIp(applicationContext)?.let { h -> Thread { sendAppSync(h) }.start() } }

    /** Debounced: several new apps in a burst (e.g. right after connecting) → one app.sync. */
    private fun scheduleAppSync() {
        mainHandler.removeCallbacks(appSyncRunnable)
        mainHandler.postDelayed(appSyncRunnable, 3000)
    }

    private fun sendAppSync(host: String) {
        val appsJson = JSONArray().apply {
            InstalledAppsProvider.list(applicationContext).forEach { app ->
                put(JSONObject().apply {
                    put("packageName", app.packageName)
                    put("label", app.label)
                    put("allowed", AppFilterStore.isAllowed(applicationContext, app.packageName))
                    put("isSystemApp", app.isSystemApp)
                })
            }
        }
        NotificationConnection.send(host, Protocol.buildEnvelope("app.sync", JSONObject().put("apps", appsJson)))
    }

    override fun onNotificationRemoved(sbn: StatusBarNotification) {
        if (sbn.packageName == ownPackage) return
        val host = PcSettings.getPcIp(applicationContext) ?: return
        val payload = JSONObject().put("key", sbn.key)
        NotificationConnection.send(host, Protocol.buildEnvelope("notification.removed", payload))
    }

    /** PC call bar's Contestar / Colgar for a call with no notification of its own (see
     *  docs/PROTOCOL.md's call.control). Two layers, tried in order: TelecomManager (real
     *  acceptRingingCall()/endCall(), needs ANSWER_PHONE_CALLS — works for SIM calls, and Android
     *  documents it as a no-op for another app's self-managed VoIP call like WhatsApp's), then
     *  the Accessibility service pressing the matching button on the phone's on-screen call UI.
     *  Neither reports failure loudly on its own, so the outcome sent back is best-effort: it
     *  says which layer was used, or why neither could be. */
    @SuppressLint("MissingPermission")
    private fun handleCallControl(payload: JSONObject) {
        val action = payload.optString("action")
        val host = PcSettings.getPcIp(applicationContext) ?: return
        // "end" covers both hangup and decline (same request: end the current call).
        val kind = when (action) {
            "answer" -> "answer"
            "hangup", "decline" -> "end"
            "speaker", "mute", "hold" -> action
            else -> return
        }
        val isTool = kind == "speaker" || kind == "mute" || kind == "hold"

        fun report(success: Boolean, method: String) {
            DiagLog.i(TAG, "call.control $action: success=$success method=$method")
            NotificationConnection.send(host, Protocol.buildEnvelope("call.control.result",
                JSONObject().put("action", if (kind == "end") "hangup" else kind).put("success", success).put("method", method)))
            // A speaker/mute press changes real state — push it right away instead of waiting for the next poll.
            if (isTool && success) callMonitorHandler.postDelayed({ pushCallStatus(force = true) }, 600)
        }

        val hasPermission = ContextCompat.checkSelfPermission(applicationContext, Manifest.permission.ANSWER_PHONE_CALLS) ==
            PackageManager.PERMISSION_GRANTED
        var telecomAttempted = false
        if (!isTool && hasPermission) {
            try {
                val telecom = applicationContext.getSystemService(Context.TELECOM_SERVICE) as android.telecom.TelecomManager
                if (kind == "answer") {
                    telecom.acceptRingingCall()
                    telecomAttempted = true
                } else {
                    // endCall() reports whether it actually ended one — a real signal, unlike acceptRingingCall().
                    if (telecom.endCall()) { report(true, "telecom"); return }
                }
            } catch (e: SecurityException) {
                DiagLog.w(TAG, "call.control: telecom refused: ${e.message}")
            }
        }

        // Accessibility (the fallback for answer/end, the only route for speaker/mute/hold).
        NotiLinkAccessibilityService.performCallControl(kind) { success ->
            when {
                success -> report(true, "accessibility")
                telecomAttempted -> report(true, "telecom")
                !NotiLinkAccessibilityService.isAvailable() -> report(false, "no_permission")
                else -> report(false, "not_found")
            }
        }
    }

    // --- Call monitor: while Telecom reports a call, polls (1.5 s) for (a) the on-screen call timer
    // — the moment it appears is when the other side actually answered, so the PC's timer doesn't
    // include ringing time — and (b) the real speaker/mute state, so the PC's buttons reflect it. ---

    private val callMonitorHandler = Handler(Looper.getMainLooper())
    private var callMonitorRunning = false
    private var callAnsweredSent = false
    private var lastCallStatus: String? = null

    private val callMonitorTick = object : Runnable {
        override fun run() {
            if (!callMonitorRunning) return
            pushCallStatus(force = false)
            callMonitorHandler.postDelayed(this, 1500)
        }
    }

    private fun startCallMonitor() {
        if (callMonitorRunning) return
        callMonitorRunning = true
        callAnsweredSent = false
        lastCallStatus = null
        callMonitorHandler.post(callMonitorTick)
    }

    private fun stopCallMonitor() {
        callMonitorRunning = false
        callMonitorHandler.removeCallbacks(callMonitorTick)
    }

    private fun pushCallStatus(force: Boolean) {
        if (!callMonitorRunning) return
        val host = PcSettings.getPcIp(applicationContext) ?: return
        val am = applicationContext.getSystemService(Context.AUDIO_SERVICE) as android.media.AudioManager
        val speaker = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S)
            am.communicationDevice?.type == android.media.AudioDeviceInfo.TYPE_BUILTIN_SPEAKER
        else @Suppress("DEPRECATION") am.isSpeakerphoneOn
        val muted = am.isMicrophoneMute

        fun send(elapsed: Int?) {
            val status = "$callAnsweredSent|$speaker|$muted"
            if (!force && status == lastCallStatus && elapsed == null) return
            lastCallStatus = status
            NotificationConnection.send(host, Protocol.buildEnvelope("call.status", JSONObject()
                .put("answered", callAnsweredSent)
                .put("elapsedSeconds", elapsed ?: JSONObject.NULL)
                .put("speaker", speaker)
                .put("muted", muted)))
        }

        if (callAnsweredSent) { send(null); return }
        NotiLinkAccessibilityService.scanCallTimer { seconds ->
            if (!callMonitorRunning) return@scanCallTimer
            if (seconds != null) {
                callAnsweredSent = true
                DiagLog.i(TAG, "call monitor: on-screen timer seen ($seconds s) — call answered")
                send(seconds)
            } else {
                send(null)
            }
        }
    }

    private fun handleCommand(type: String, payload: JSONObject) {
        when (type) {
            "delete.request" -> {
                val key = payload.optString("key")
                if (key.isEmpty()) return
                cancelNotification(key)
            }
            "delete.request.all" -> {
                // Individually, never the blunt cancelAllNotifications() — that would also
                // dismiss unrelated, never-mirrored system notifications (music, nav, etc).
                mirroredActiveNotifications().forEach { cancelNotification(it.key) }
            }
            "call.control" -> handleCallControl(payload)
            "action.invoke" -> invokeAction(payload)
            "app.filter.update" -> handleFilterUpdate(payload)
            "icon.request" -> handleIconRequest(payload)
            "connection.toggle" -> {
                val disconnect = payload.optBoolean("disconnect", true)
                ConnectionStatus.setManuallyDisconnected(disconnect)
                if (disconnect) NotificationConnection.close()
            }
            "device.ring" -> ringDevice()
            "pair.code" -> {
                val code = payload.optString("code")
                if (code.isNotEmpty()) PairingState.showCode(code)
            }
            "pair.token" -> {
                val token = payload.optString("token")
                if (token.isNotEmpty()) {
                    DiagLog.i(TAG, "pair.token received (${token.take(6)}…), saving")
                    PairingTokenStore.saveToken(applicationContext, token)
                    PairingState.clear()
                }
            }
            "share.send" -> {
                val text = payload.optString("text")
                if (text.isNotEmpty()) {
                    val title = payload.optString("title").takeIf { it.isNotEmpty() }
                    val description = payload.optString("description").takeIf { it.isNotEmpty() }
                    val notificationId = showSharedNotification(text, title, description)
                    // A link-preview image (fetched PC-side) always immediately follows as a
                    // binary frame when transferId is present — see MainViewModel.ShareToDevice.
                    if (payload.has("transferId")) {
                        pendingSharePreview = PendingSharePreview(notificationId, text, title, description)
                    }
                }
            }
            "file.send" -> {
                // The binary frame carrying the actual bytes always immediately follows this
                // JSON message (see docs/PROTOCOL.md) — remembered here, consumed by
                // handleBinaryFrame() when NotificationConnection.onBinaryFrame fires next.
                pendingIncomingFile = payload.optString("fileName", "archivo") to payload.optString("mimeType", "application/octet-stream")
            }
            "media.control" -> mediaSessionWatcher.control(
                payload.optString("action"),
                if (payload.has("position")) payload.optLong("position") else null
            )
            "share.status" -> handleShareStatus(payload)
            "media.levels.request" -> {
                if (payload.optBoolean("on", false)) PcSettings.getPcIp(applicationContext)?.let { com.notilink.app.net.MediaLevels.start(applicationContext, it) }
                else com.notilink.app.net.MediaLevels.stop()
            }
            "pc.addresses" -> {
                val arr = payload.optJSONArray("addresses")
                if (arr != null) {
                    val list = (0 until arr.length()).mapNotNull { i ->
                        arr.optJSONObject(i)?.let { o -> o.optString("ip").takeIf { it.isNotBlank() }?.let { it to o.optString("kind", "lan") } }
                    }
                    if (list.isNotEmpty()) PcSettings.setAddresses(applicationContext, list)
                }
            }
            "share.recents" -> com.notilink.app.ui.AppPickerActivity.Recents.onRemote(applicationContext, payload)
            "phone.prefs" -> {
                if (payload.has("statusAutoHideSeconds")) applyStatusAutoHide(payload.optInt("statusAutoHideSeconds", 0))
                payload.optString("accent").takeIf { Regex("^#[0-9A-Fa-f]{6}$").matches(it) }?.let {
                    com.notilink.app.ui.AccentTheme.set(applicationContext, android.graphics.Color.parseColor(it))
                }
            }
            "pc.bye" -> {
                DiagLog.i(TAG, "pc.bye: ${payload.optString("reason")}")
                ConnectionStatus.setPcAway(true)
                NotificationConnection.close()
            }
            "notification.open" -> openNotification(payload)
            "media.open" -> openMediaNotification()
            "volume.set" -> setVolume(payload)
            "dnd.set" -> setDnd(payload)
            "rotation.set" -> setRotation(payload)
        }
    }

    /** "Abrir en el dispositivo" on the PC's now-playing bar — there's no notification key for a
     *  media session the way there is for a mirrored card, so this looks up the playing app's
     *  own real "now playing" notification by package instead (still posted and still visible
     *  via activeNotifications even though NotiLinkAccessibilityService.openNotification's own
     *  doc comment for why this can't just fire a PendingIntent directly). */
    private fun openMediaNotification() {
        val host = PcSettings.getPcIp(applicationContext)
        val packageName = mediaSessionWatcher.activePackageName()
        val sbn = packageName?.let { pkg -> activeNotifications?.firstOrNull { it.packageName == pkg } }
        if (sbn == null) {
            if (host != null) sendOpenNotificationResult(host, false, "not_found")
            return
        }
        NotiLinkAccessibilityService.openNotification(openCandidatesFor(sbn)) { success, reason ->
            if (host != null) sendOpenNotificationResult(host, success, reason)
        }
    }

    /** See NotiLinkAccessibilityService's own doc comment for the full mechanism — this just
     *  looks up the notification's own title/text (same fields already used for mirroring) so
     *  the accessibility service has something to match against in the shade. Always reports
     *  back to the PC (success or a reason code) so it can show real feedback instead of a
     *  silent no-op — same idea as the screen-share start/stop toasts. */
    private fun openNotification(payload: JSONObject) {
        val key = payload.optString("key")
        val host = PcSettings.getPcIp(applicationContext)
        val sbn = if (key.isEmpty()) null else activeNotifications?.find { it.key == key }
        if (sbn == null) {
            if (host != null) sendOpenNotificationResult(host, false, "not_found")
            return
        }
        NotiLinkAccessibilityService.openNotification(openCandidatesFor(sbn)) { success, reason ->
            if (host != null) sendOpenNotificationResult(host, success, reason)
        }
    }

    /** Text/title alone weren't enough to match a real WhatsApp conversation notification in the
     *  shade (confirmed from a real device log) — EXTRA_TEXT on a MessagingStyle notification can
     *  be a stale/summary line, while the shade actually renders the conversation's own last
     *  message. Reuses StyleExtractor (already built for mirroring) to add that as a candidate
     *  too, ahead of title/text since it's the most likely one to actually be on screen. */
    private fun openCandidatesFor(sbn: StatusBarNotification): List<String> {
        val extras = sbn.notification.extras
        val title = extras.getCharSequence(Notification.EXTRA_TITLE)?.toString() ?: ""
        val text = extras.getCharSequence(Notification.EXTRA_TEXT)?.toString() ?: ""
        val candidates = mutableListOf(text, title)
        try {
            StyleExtractor.extract(applicationContext, sbn.notification).messages?.lastOrNull()?.let {
                candidates.add(0, it.text)
            }
        } catch (e: Exception) {
            // Best-effort — the title/text candidates above still get tried either way.
        }
        return candidates
    }

    private fun sendOpenNotificationResult(host: String, success: Boolean, reason: String?) {
        val payload = JSONObject().apply {
            put("success", success)
            if (reason != null) put("reason", reason)
        }
        NotificationConnection.send(host, Protocol.buildEnvelope("notification.open.result", payload))
    }

    /** Mirrors the PC's own screen/audio share state as a notification on the phone (see
     *  buildSharingNotification) — the PC is the one that actually knows this state (it's the
     *  one running scrcpy), so it pushes it here rather than the phone guessing. */
    private fun handleShareStatus(payload: JSONObject) {
        val screenSharing = payload.optBoolean("screenSharing", false)
        val audioSharing = payload.optBoolean("audioSharing", false)
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (screenSharing || audioSharing) {
            manager.notify(SHARE_NOTIFICATION_ID, buildSharingNotification(screenSharing, audioSharing))
        } else {
            manager.cancel(SHARE_NOTIFICATION_ID)
        }

        // One notification per app shared on its own (PC "Solo una app"), each with its own
        // "Detener" — the list is always the PC's complete current set, so anything not in it
        // anymore is cancelled here.
        val apps = payload.optJSONArray("apps")
        val current = mutableMapOf<String, String>()
        val states = mutableMapOf<String, String>()
        if (apps != null) for (i in 0 until apps.length()) {
            val a = apps.optJSONObject(i) ?: continue
            val pkg = a.optString("packageName")
            if (pkg.isNotEmpty()) {
                current[pkg] = a.optString("label").ifEmpty { pkg }
                states[pkg] = a.optString("state", "pc")
            }
        }
        com.notilink.app.net.ShareState.update(screenSharing, audioSharing,
            current.map { (pkg, label) -> com.notilink.app.net.ShareState.SharedApp(pkg, label, states[pkg] == "phone") })
        com.notilink.app.ui.RecentAppsWidget.refresh(this)
        synchronized(sharedAppNotifications) {
            for (pkg in sharedAppNotifications.keys - current.keys) manager.cancel(appShareNotificationId(pkg))
            for ((pkg, label) in current) manager.notify(appShareNotificationId(pkg), buildAppSharingNotification(pkg, label, states[pkg] == "phone"))
            sharedAppNotifications.clear()
            sharedAppNotifications.putAll(current)
        }
    }

    private val sharedAppNotifications = mutableMapOf<String, String>()

    private fun appShareNotificationId(pkg: String) = APP_SHARE_NOTIFICATION_BASE + (pkg.hashCode() and 0x3FFF)

    private fun buildAppSharingNotification(pkg: String, label: String, onPhone: Boolean): Notification {
        val stopIntent = PendingIntent.getBroadcast(
            applicationContext, appShareNotificationId(pkg),
            Intent(applicationContext, ShareActionReceiver::class.java)
                .setAction(ShareActionReceiver.ACTION_STOP_APP_SHARE)
                .putExtra(ShareActionReceiver.EXTRA_PACKAGE, pkg),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        val icon = try {
            packageManager.getApplicationIcon(pkg).toBitmap(96, 96)
        } catch (e: Exception) { null }
        val builder = NotificationCompat.Builder(applicationContext, SHARE_CHANNEL_ID)
            .setSmallIcon(com.notilink.app.R.drawable.ic_notification)
            .setLargeIcon(icon)
            .setContentTitle(label)
            .setContentText(if (onPhone) "Abierta en el celular — la ventana de la PC está en pausa" else "Se está mostrando en la PC")
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setPriority(NotificationCompat.PRIORITY_LOW)
        if (onPhone) {
            val returnIntent = PendingIntent.getBroadcast(
                applicationContext, appShareNotificationId(pkg) + 1,
                Intent(applicationContext, ShareActionReceiver::class.java)
                    .setAction(ShareActionReceiver.ACTION_RETURN_APP_SHARE)
                    .putExtra(ShareActionReceiver.EXTRA_PACKAGE, pkg),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
            )
            builder.addAction(0, "🖥️ Mostrar en la PC", returnIntent)
        }
        return builder.addAction(0, "⏹ Detener transmisión", stopIntent).build()
    }

    /** Pushes the "now playing" state to the PC's persistent media bar (see
     *  docs/PROTOCOL.md's media.state) — [state] null means no active session, which the PC
     *  renders as the bar hiding itself. Album art (if any) is downscaled the same way every
     *  other image on the wire is (ImageUtils, 512px/JPEG q80). */
    private fun sendMediaState(state: MediaSessionWatcher.MediaState?) {
        val host = PcSettings.getPcIp(applicationContext) ?: return
        if (state == null) {
            NotificationConnection.send(host, Protocol.buildEnvelope("media.state", JSONObject().put("active", false)))
            return
        }
        val payload = JSONObject().apply {
            put("active", true)
            put("packageName", state.packageName)
            put("appLabel", state.appLabel)
            put("title", state.title)
            put("artist", state.artist)
            put("album", state.album)
            put("isPlaying", state.isPlaying)
            put("position", state.position)
            put("duration", state.duration)
            put("canPlayPause", state.canPlayPause)
            put("canNext", state.canNext)
            put("canPrevious", state.canPrevious)
            put("canFavorite", state.canFavorite)
            put("isFavorite", state.isFavorite)
            put("canShuffle", state.canShuffle)
            put("isShuffleOn", state.isShuffleOn)
            put("isSmartShuffle", state.isSmartShuffle)
            put("canSeek", state.canSeek)
        }
        // MediaMetadata's own bitmap keys come back null for at least one real app tested
        // (Spotify) — likely supplying art via a URI key this app doesn't resolve, rather than
        // an embedded bitmap. The matching notification's own large icon is exactly the same
        // album art and is already proven reliable (it's the same extraction the real
        // notification card used before media notifications stopped being mirrored as cards),
        // so it's tried first here.
        val notificationArt = activeNotifications
            ?.find { it.packageName == state.packageName && it.notification.extras.containsKey(Notification.EXTRA_MEDIA_SESSION) }
            ?.let { StyleExtractor.extractAvatar(applicationContext, it.notification) }
        val art = notificationArt ?: state.albumArt
        DiagLog.i(TAG, "sendMediaState: pkg=${state.packageName} notificationArt=${notificationArt != null} metadataArt=${state.albumArt != null}")

        if (art != null) {
            payload.put("albumArtTransferId", UUID.randomUUID().toString())
            NotificationConnection.sendWithImage(host, Protocol.buildEnvelope("media.state", payload), ImageUtils.toDownscaledJpeg(art))
        } else {
            payload.put("albumArtTransferId", JSONObject.NULL)
            NotificationConnection.send(host, Protocol.buildEnvelope("media.state", payload))
        }
    }

    private var pendingIncomingFile: Pair<String, String>? = null // (fileName, mimeType)

    private data class PendingSharePreview(val notificationId: Int, val text: String, val title: String?, val description: String?)
    private var pendingSharePreview: PendingSharePreview? = null

    /** Single dispatcher for every incoming binary frame — routes to whichever transfer is
     *  currently pending (share-preview image vs. a full file.send). The two are mutually
     *  exclusive at any given moment since sends are strictly sequential on one connection. */
    private fun handleBinaryFrame(bytes: ByteArray) {
        pendingSharePreview?.let { pending ->
            pendingSharePreview = null
            attachSharePreviewImage(pending, bytes)
            return
        }
        handleIncomingFile(bytes)
    }

    /** Patches the already-shown notification (posted immediately with just text, so the user
     *  isn't kept waiting on the image fetch) with the fetched link-preview picture — same
     *  notification id, so this replaces it in place rather than adding a second one. */
    private fun attachSharePreviewImage(pending: PendingSharePreview, bytes: ByteArray) {
        val bitmap = try {
            android.graphics.BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
        } catch (e: Exception) {
            null
        } ?: return

        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        val builder = sharedNotificationBuilder(pending.text, pending.title, pending.description)
            .setStyle(NotificationCompat.BigPictureStyle().bigPicture(bitmap))
            .setLargeIcon(bitmap)
        manager.notify(pending.notificationId, builder.build())
    }

    /** PC->phone half of file sharing (see ShareToPcActivity for the other direction) — saved
     *  to the real Downloads folder via MediaStore (scoped storage, API 29+; below that this
     *  device can't be tested against so it's simply skipped with a log line rather than
     *  guessing at legacy WRITE_EXTERNAL_STORAGE behavior). Then a native notification, same
     *  idea as showSharedNotification. */
    private fun handleIncomingFile(bytes: ByteArray) {
        val (fileName, mimeType) = pendingIncomingFile ?: run {
            DiagLog.w(TAG, "binary frame arrived with no pending file.send, ignoring ${bytes.size} bytes")
            return
        }
        pendingIncomingFile = null

        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) {
            DiagLog.w(TAG, "file.send received but SDK ${Build.VERSION.SDK_INT} < 29, skipping save (MediaStore.Downloads needs API 29+)")
            return
        }

        val resolver = applicationContext.contentResolver
        val values = android.content.ContentValues().apply {
            put(android.provider.MediaStore.Downloads.DISPLAY_NAME, fileName)
            put(android.provider.MediaStore.Downloads.MIME_TYPE, mimeType)
            put(android.provider.MediaStore.Downloads.IS_PENDING, 1)
        }
        val uri = resolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)
        if (uri == null) {
            DiagLog.w(TAG, "MediaStore insert failed for $fileName")
            return
        }
        try {
            resolver.openOutputStream(uri)?.use { it.write(bytes) }
            values.clear()
            values.put(android.provider.MediaStore.Downloads.IS_PENDING, 0)
            resolver.update(uri, values, null, null)
            DiagLog.i(TAG, "file.send saved: $fileName (${bytes.size} bytes) -> $uri")
            showFileReceivedNotification(fileName, mimeType, uri, bytes)
        } catch (e: Exception) {
            DiagLog.w(TAG, "failed writing $fileName: ${e.message}")
        }
    }

    /** [bytes] only used for an image preview (BigPictureStyle) — anything without a sensible
     *  preview (a PDF, a zip, ...) is left exactly as before: just the filename, no thumbnail
     *  attempted. Decoded straight from memory rather than re-reading the just-saved MediaStore
     *  entry, since the bytes are already right here. */
    private fun showFileReceivedNotification(fileName: String, mimeType: String, uri: android.net.Uri, bytes: ByteArray) {
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (manager.getNotificationChannel(SHARED_CHANNEL_ID) == null) {
            manager.createNotificationChannel(
                NotificationChannel(SHARED_CHANNEL_ID, "Compartido desde la PC", NotificationManager.IMPORTANCE_DEFAULT)
            )
        }
        val openIntent = Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(uri, mimeType)
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
        val contentIntent = PendingIntent.getActivity(
            applicationContext, 0, openIntent, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        val builder = NotificationCompat.Builder(applicationContext, SHARED_CHANNEL_ID)
            .setSmallIcon(com.notilink.app.R.drawable.ic_notification)
            .setContentTitle("Archivo recibido desde la PC")
            .setContentText("$fileName — guardado en Descargas")
            .setAutoCancel(true)
            .setContentIntent(contentIntent)

        if (mimeType.startsWith("image/")) {
            val bitmap = try {
                android.graphics.BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
            } catch (e: Exception) {
                null
            }
            if (bitmap != null) {
                builder.setStyle(NotificationCompat.BigPictureStyle().bigPicture(bitmap))
                builder.setLargeIcon(bitmap)
            }
        }

        manager.notify(SHARED_NOTIFICATION_ID_BASE + (System.currentTimeMillis() % 1000).toInt(), builder.build())
    }

    /** Shared by showSharedNotification and attachSharePreviewImage's rebuild-in-place — same
     *  content/copy-action/tap-to-open regardless of whether a preview image ends up attached. */
    private fun sharedNotificationBuilder(text: String, title: String?, description: String?): NotificationCompat.Builder {
        val looksLikeUrl = text.startsWith("http://") || text.startsWith("https://")
        val contentIntent = if (looksLikeUrl) {
            PendingIntent.getActivity(
                applicationContext, 0, Intent(Intent.ACTION_VIEW, android.net.Uri.parse(text)),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
            )
        } else {
            PendingIntent.getActivity(
                applicationContext, 0, Intent(applicationContext, MainActivity::class.java),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
            )
        }

        // A fixed id (not tied to a specific notification instance) so both callers' copy
        // buttons keep working after attachSharePreviewImage re-notifies with the same
        // notification id — see PendingSharePreview.notificationId.
        val copyRequestCode = text.hashCode()
        val copyIntent = PendingIntent.getBroadcast(
            applicationContext, copyRequestCode,
            Intent(applicationContext, CopyToClipboardReceiver::class.java)
                .setAction(CopyToClipboardReceiver.ACTION_COPY)
                .putExtra(CopyToClipboardReceiver.EXTRA_TEXT, text)
                .putExtra(CopyToClipboardReceiver.EXTRA_NOTIFICATION_ID, copyRequestCode),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        // title/description come from a PC-side Open Graph fetch (see
        // MainViewModel.ShareToDevice) — shown above the raw URL when present, same as a link
        // preview card anywhere else, rather than just a bare link.
        val displayText = when {
            title != null && description != null -> "$title\n$description\n$text"
            title != null -> "$title\n$text"
            else -> text
        }

        return NotificationCompat.Builder(applicationContext, SHARED_CHANNEL_ID)
            .setSmallIcon(com.notilink.app.R.drawable.ic_notification)
            .setContentTitle(title ?: "Compartido desde la PC")
            .setContentText(if (title != null) text else displayText)
            .setStyle(NotificationCompat.BigTextStyle().bigText(displayText))
            .setAutoCancel(true)
            .setContentIntent(contentIntent)
            .addAction(0, "Copiar", copyIntent)
    }

    /** The PC->phone half of sharing (see ShareToPcActivity for the other direction) — posts a
     *  real native Android notification, since the whole point is surfacing this even when the
     *  user isn't currently looking at the NotiLink app itself. Tapping it opens the link if
     *  the shared text looks like one, otherwise just opens NotiLink. Returns the notification
     *  id so a following link-preview image (if any) can patch this same notification in place. */
    private fun showSharedNotification(text: String, title: String? = null, description: String? = null): Int {
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (manager.getNotificationChannel(SHARED_CHANNEL_ID) == null) {
            manager.createNotificationChannel(
                NotificationChannel(SHARED_CHANNEL_ID, "Compartido desde la PC", NotificationManager.IMPORTANCE_DEFAULT)
            )
        }
        val notificationId = SHARED_NOTIFICATION_ID_BASE + (System.currentTimeMillis() % 1000).toInt()
        manager.notify(notificationId, sharedNotificationBuilder(text, title, description).build())
        return notificationId
    }

    /** Bidirectional: this arrives whether the user toggled the app from the PC's Apps
     *  screen or from the phone's own — either way, persist it and, if it just turned an
     *  app off, retract anything of theirs currently mirrored (per docs/PROTOCOL.md). */
    private fun handleFilterUpdate(payload: JSONObject) {
        val identity = payload.optString("packageName")
        if (identity.isEmpty()) return
        val allowed = payload.optBoolean("allowed", true)
        AppFilterStore.setAllowed(applicationContext, identity, allowed)

        if (!allowed) {
            val host = PcSettings.getPcIp(applicationContext) ?: return
            activeNotifications
                ?.filter { identityKey(it) == identity }
                ?.forEach {
                    val removedPayload = JSONObject().put("key", it.key)
                    NotificationConnection.send(host, Protocol.buildEnvelope("notification.removed", removedPayload))
                }
        }
    }

    private fun handleIconRequest(payload: JSONObject) {
        val identity = payload.optString("packageName")
        val transferId = payload.optString("transferId")
        if (identity.isEmpty() || transferId.isEmpty()) return
        val host = PcSettings.getPcIp(applicationContext) ?: return
        val bitmap = AppIconLoader.load(applicationContext, AppIdentity.realPackage(identity)) ?: return
        val responsePayload = JSONObject().apply {
            put("packageName", identity)
            put("transferId", transferId)
        }
        NotificationConnection.sendWithImage(host, Protocol.buildEnvelope("icon.data", responsePayload), ImageUtils.toPng(bitmap))
    }


    /** Fires the notification's own real Notification.Action — nothing custom-built. If the
     *  notification was dismissed/updated since the PC last saw it, the action/PendingIntent
     *  is simply gone and this silently no-ops rather than erroring. */
    /** Was entirely silent before — no DiagLog line for the attempt, a hit, or any of the ways
     *  it can quietly no-op (stale key, out-of-range index, a thrown exception), which made
     *  "this specific action doesn't work" reports (e.g. missed-call quick actions) impossible
     *  to actually diagnose from a real device log. Every path below now logs. */
    private fun invokeAction(payload: JSONObject) {
        val key = payload.optString("key")
        val actionIndex = payload.optInt("actionIndex", -1)
        val replyText = if (payload.isNull("replyText")) null else payload.optString("replyText")
        DiagLog.i(TAG, "invokeAction: key=$key actionIndex=$actionIndex hasReplyText=${replyText != null}")

        val sbn = activeNotifications?.find { it.key == key }
        if (sbn == null) {
            DiagLog.w(TAG, "invokeAction: no active notification found for key=$key — already dismissed/replaced?")
            return
        }
        val actions = sbn.notification.actions
        val action = actions?.getOrNull(actionIndex)
        if (action == null) {
            DiagLog.w(TAG, "invokeAction: no action at index=$actionIndex for key=$key — " +
                "actions available: ${actions?.joinToString { it.title?.toString() ?: "?" } ?: "none"}")
            return
        }

        try {
            val remoteInputs = action.remoteInputs
            if (replyText != null && !remoteInputs.isNullOrEmpty()) {
                val intent = Intent()
                val results = Bundle()
                for (ri in remoteInputs) results.putCharSequence(ri.resultKey, replyText)
                RemoteInput.addResultsToIntent(remoteInputs, intent, results)
                action.actionIntent.send(applicationContext, 0, intent)
            } else {
                action.actionIntent.send()
            }
            DiagLog.i(TAG, "invokeAction: sent \"${action.title}\" for key=$key")
        } catch (e: PendingIntent.CanceledException) {
            DiagLog.w(TAG, "invokeAction: PendingIntent already canceled for key=$key index=$actionIndex (\"${action.title}\")")
        } catch (e: Exception) {
            DiagLog.w(TAG, "invokeAction: failed for key=$key index=$actionIndex (\"${action.title}\"): ${e.javaClass.simpleName} ${e.message}")
        }
    }

    private fun StatusBarNotification.isGroupSummary(): Boolean =
        (notification.flags and Notification.FLAG_GROUP_SUMMARY) != 0

    /** [identity] is the composite AppIdentity key (see identityKey) — used as the wire
     *  "packageName" and the dedup key, while [packageName] is the real Android package,
     *  used for the actual PackageManager icon lookup. */
    private fun maybeSendIcon(host: String, packageName: String, identity: String) {
        if (!iconSentForPackage.add(identity)) return
        val bitmap = AppIconLoader.load(applicationContext, packageName)
        if (bitmap == null) {
            iconSentForPackage.remove(identity) // failed — allow retry on the next notification
            return
        }
        try {
            val png = ImageUtils.toPng(bitmap)
            val payload = JSONObject().apply {
                put("packageName", identity)
                put("transferId", UUID.randomUUID().toString())
            }
            NotificationConnection.sendWithImage(host, Protocol.buildEnvelope("icon.data", payload), png)
        } catch (e: Exception) {
            iconSentForPackage.remove(identity)
        }
    }

    private fun sendPosted(host: String, sbn: StatusBarNotification, identity: String) {
        val extras = sbn.notification.extras
        var title = extras.getCharSequence(Notification.EXTRA_TITLE)?.toString()
        var text = extras.getCharSequence(Notification.EXTRA_TEXT)?.toString()
        val extracted = StyleExtractor.extract(applicationContext, sbn.notification)
        DiagLog.i(TAG, "posted: pkg=${sbn.packageName} style=${extracted.style} category=${sbn.notification.category}")

        if (extracted.style == "call") {
            DiagLog.i(TAG, "call-style notification from ${sbn.packageName}: title=$title text=$text category=${sbn.notification.category}")
            // CallStyle notifications sometimes leave title/text empty and carry the caller's
            // name only via a Person object instead — fall back rather than dropping it, since
            // an incoming call is exactly the kind of thing worth surfacing on the PC.
            if (title.isNullOrEmpty()) {
                @Suppress("DEPRECATION")
                title = extras.getParcelableArrayList<android.app.Person>(Notification.EXTRA_PEOPLE_LIST)
                    ?.firstOrNull()?.name?.toString() ?: "Llamada"
            }
            if (text.isNullOrEmpty()) text = "Llamada entrante"
        }

        // A notification with neither title nor text carries nothing worth
        // mirroring (e.g. a silent progress/foreground-service placeholder).
        if (title.isNullOrEmpty() && text.isNullOrEmpty()) {
            DiagLog.i(TAG, "dropping empty-content notification from ${sbn.packageName}, " +
                "category=${sbn.notification.category}, template=${extras.getString(Notification.EXTRA_TEMPLATE)}")
            return
        }

        val appLabel = try {
            val pm = applicationContext.packageManager
            val base = pm.getApplicationLabel(pm.getApplicationInfo(sbn.packageName, 0)).toString()
            if (identity != sbn.packageName) "$base (dual)" else base
        } catch (e: Exception) {
            sbn.packageName
        }
        val avatar = StyleExtractor.extractAvatar(applicationContext, sbn.notification)

        // Collect every image this notification carries — avatar first, then
        // the top-level bigPicture image, then each message's own image in
        // order — as one ordered list so they go out as sequential binary
        // frames right after the JSON envelope, per docs/PROTOCOL.md.
        val images = mutableListOf<ByteArray>()

        val avatarTransferId = avatar?.let {
            images.add(ImageUtils.toDownscaledJpeg(it))
            UUID.randomUUID().toString()
        }

        val topImageBytes = extracted.image?.let { ImageUtils.toDownscaledJpeg(it, ImageUtils.CONTENT_MAX_EDGE, ImageUtils.CONTENT_JPEG_QUALITY) }
        val imageTransferId = topImageBytes?.let {
            images.add(it)
            UUID.randomUUID().toString()
        }

        val messagesJson = extracted.messages?.let { list ->
            org.json.JSONArray().apply {
                for (m in list) {
                    put(JSONObject().apply {
                        put("sender", m.sender)
                        put("isMe", m.isMe)
                        put("text", m.text)
                        put("ts", m.ts)

                        // Animated stickers go out as their original bytes (untouched, so
                        // the animation survives); static photos still get downscaled+re-encoded.
                        val msgImageBytes: ByteArray?
                        val msgImageMimeType: String?
                        if (m.imageRawBytes != null) {
                            msgImageBytes = m.imageRawBytes
                            msgImageMimeType = m.imageRawMimeType ?: "application/octet-stream"
                        } else if (m.imageBitmap != null) {
                            msgImageBytes = ImageUtils.toDownscaledJpeg(m.imageBitmap, ImageUtils.CONTENT_MAX_EDGE, ImageUtils.CONTENT_JPEG_QUALITY)
                            msgImageMimeType = "image/jpeg"
                        } else {
                            msgImageBytes = null
                            msgImageMimeType = null
                        }

                        if (msgImageBytes != null) {
                            val transferId = UUID.randomUUID().toString()
                            images.add(msgImageBytes)
                            put("hasImage", true)
                            put("imageTransferId", transferId)
                            put("imageMimeType", msgImageMimeType)
                        } else {
                            put("hasImage", false)
                            put("imageTransferId", JSONObject.NULL)
                            put("imageMimeType", JSONObject.NULL)
                        }
                    })
                }
            }
        }

        val actionsJson = JSONArray().apply {
            sbn.notification.actions?.forEach { action ->
                put(JSONObject().apply {
                    put("title", action.title?.toString() ?: "")
                    put("hasReplyInput", !action.remoteInputs.isNullOrEmpty())
                    // RemoteInput.getChoices() — canned quick-reply suggestions some apps offer
                    // alongside (or instead of) free-text typing, e.g. Gmail's "Sí"/"No"/"Tal
                    // vez". Only the first RemoteInput is checked — a reply action only ever has
                    // one that actually accepts the user's text, same assumption invokeAction
                    // already makes when filling in replyText.
                    val choices = action.remoteInputs?.firstOrNull()?.choices
                    put("replyChoices", if (choices.isNullOrEmpty()) JSONArray() else JSONArray().apply {
                        choices.forEach { put(it.toString()) }
                    })
                })
            }
        }

        // The app's own requested accent color (Notification.color) — COLOR_DEFAULT means "no
        // color requested", not literally black/transparent, so it must be excluded rather than
        // forwarded as a real color.
        val accentColor = sbn.notification.color.takeIf { it != Notification.COLOR_DEFAULT }
            ?.let { String.format("#%06X", 0xFFFFFF and it) }

        // sbn.postTime is unreliable for "when did this actually happen" — it's the wall-clock
        // moment the OS last saw notify() called for this key, which some apps/OEMs bump on
        // updates that don't represent new content (a conversation summary getting silently
        // re-touched, a Doze-deferred delivery, etc.), not necessarily the real event time.
        // Confirmed from a real report: notifications from the night before showed a plausible-
        // looking "this morning" time because of exactly this. Prefer, in priority order: (1) the
        // last individual message's own timestamp for messaging-style — the most granular and
        // accurate, set by the chat app itself per-message; (2) Notification.when — what the app
        // explicitly asked its OWN shade entry to display (WhatsApp et al. set this correctly
        // precisely so the system tray shows the right time); (3) sbn.postTime as a last-resort
        // fallback only when neither of the above is available.
        val messagingTs = extracted.messages?.lastOrNull()?.ts?.takeIf { it > 0 }
        val whenTs = sbn.notification.`when`.takeIf { it > 0 }
        val bestPostedAt = messagingTs ?: whenTs ?: sbn.postTime

        // Notification.EXTRA_PROGRESS/EXTRA_PROGRESS_MAX/EXTRA_PROGRESS_INDETERMINATE is the same
        // standard API that drives a native progress bar in Android's own shade — Play Store
        // installs, Chrome downloads, WhatsApp sending a video, etc. all use it, nothing invented
        // here. Confirmed real bug: checking whether the extra KEY was merely present (rather
        // than its actual value) showed an empty bar on every notification, because plenty of
        // apps' underlying Notification.Builder path populates these extras with harmless
        // defaults (max=0, indeterminate=false) even when setProgress() was never actually
        // called. progressMax > 0 (a real, freshly-started determinate download is still max>0
        // at 0%, so this doesn't miss that case) or indeterminate=true is what genuinely means
        // "this notification has a real progress bar".
        val progress = extras.getInt(Notification.EXTRA_PROGRESS, 0)
        val progressMax = extras.getInt(Notification.EXTRA_PROGRESS_MAX, 0)
        val progressIndeterminate = extras.getBoolean(Notification.EXTRA_PROGRESS_INDETERMINATE, false)
        val hasProgress = progressMax > 0 || progressIndeterminate

        val payload = JSONObject().apply {
            put("key", sbn.key)
            put("packageName", identity)
            put("appLabel", appLabel)
            put("title", title ?: "")
            put("text", text ?: "")
            put("subText", extras.getCharSequence(Notification.EXTRA_SUB_TEXT)?.toString() ?: JSONObject.NULL)
            put("style", extracted.style)
            put("bigText", extracted.bigText ?: JSONObject.NULL)
            put("messages", messagesJson ?: JSONObject.NULL)
            put("actions", actionsJson)
            put("isGroupConversation", extracted.isGroupConversation)
            put("inboxLines", extracted.inboxLines ?: JSONObject.NULL)
            put("hasImage", topImageBytes != null)
            put("imageTransferId", imageTransferId ?: JSONObject.NULL)
            put("hasAvatar", avatar != null)
            put("avatarTransferId", avatarTransferId ?: JSONObject.NULL)
            put("accentColor", accentColor ?: JSONObject.NULL)
            put("hasProgress", hasProgress)
            put("progress", progress)
            put("progressMax", progressMax)
            put("progressIndeterminate", progressIndeterminate)
            put("postedAt", bestPostedAt)
            put("isOngoing", sbn.isOngoing)
            put("isClearable", sbn.isClearable)
        }
        NotificationConnection.sendWithImages(host, Protocol.buildEnvelope("notification.posted", payload), images)
    }

    companion object {
        @Volatile var instance: NotiLinkNotificationListener? = null
        private const val TAG = "NotiLink/Listener"
        private const val STATUS_CHANNEL_ID = "notilink_status"
        private const val STATUS_NOTIFICATION_ID = 1
        private const val SHARED_CHANNEL_ID = "notilink_shared"
        // Each shared notification gets its own id (± up to 1000) so several in a row each
        // show as their own entry instead of the newest silently replacing the last.
        private const val SHARED_NOTIFICATION_ID_BASE = 2000
        private const val SHARE_CHANNEL_ID = "notilink_share_status"
        private const val SHARE_NOTIFICATION_ID = 3
        private const val QUIET_AFTER_MS = 2 * 60 * 1000L
        private const val APP_SHARE_NOTIFICATION_BASE = 40000
    }
}
