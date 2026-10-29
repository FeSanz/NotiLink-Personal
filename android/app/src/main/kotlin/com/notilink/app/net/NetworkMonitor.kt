package com.notilink.app.net

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.os.Handler
import android.os.Looper
import org.json.JSONObject
import java.util.concurrent.CopyOnWriteArrayList

/**
 * Watches the phone's default network so NotiLink works away from home (e.g. over Tailscale on
 * mobile data):
 * - a network switch (Wi-Fi ↔ datos, hotspot, VPN on/off) drops the old socket and reconnects
 *   right away — through the PC's other known addresses if needed (PcSettings.candidates) —
 *   instead of waiting for the next heartbeat to fail;
 * - on a metered network (mobile data) it turns on the data saver (smaller images, no wallpaper)
 *   and tells the PC (`phone.network`), which then lowers screen-sharing quality.
 */
object NetworkMonitor {
    data class Info(val transport: String, val vpn: Boolean, val metered: Boolean)

    @Volatile var current: Info = Info("unknown", false, false)
        private set

    private val listeners = CopyOnWriteArrayList<() -> Unit>()
    fun addListener(l: () -> Unit) { listeners.add(l) }
    fun removeListener(l: () -> Unit) { listeners.remove(l) }

    private var cm: ConnectivityManager? = null
    private var callback: ConnectivityManager.NetworkCallback? = null
    private var appContext: Context? = null
    private val handler = Handler(Looper.getMainLooper())
    @Volatile private var lastNetwork: Network? = null

    // Several callbacks fire in a burst while Android switches networks: act once it settles.
    private val settle = Runnable { onSettled() }

    fun start(context: Context) {
        if (callback != null) return
        val ctx = context.applicationContext
        appContext = ctx
        val manager = ctx.getSystemService(ConnectivityManager::class.java) ?: return
        cm = manager
        current = read(manager)
        ImageUtils.dataSaver = current.metered
        lastNetwork = manager.activeNetwork
        val cb = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) = schedule()
            override fun onLost(network: Network) = schedule()
            override fun onCapabilitiesChanged(network: Network, caps: NetworkCapabilities) {
                val now = read(manager)
                if (now != current) schedule()
            }
        }
        callback = cb
        try { manager.registerDefaultNetworkCallback(cb) } catch (e: Exception) { callback = null }
    }

    fun stop() {
        val cb = callback ?: return
        try { cm?.unregisterNetworkCallback(cb) } catch (e: Exception) { }
        callback = null
        handler.removeCallbacks(settle)
    }

    private fun schedule() {
        handler.removeCallbacks(settle)
        handler.postDelayed(settle, 1500)
    }

    private fun onSettled() {
        val manager = cm ?: return
        val info = read(manager)
        val network = manager.activeNetwork
        val switched = network != lastNetwork
        lastNetwork = network
        val changed = info != current
        current = info
        ImageUtils.dataSaver = info.metered
        if (switched && network != null) {
            DiagLog.i("NetworkMonitor", "network switched → ${info.transport}${if (info.vpn) "+vpn" else ""}, metered=${info.metered}; reconnecting")
            NotificationConnection.onNetworkChanged()
        }
        if (changed || switched) {
            appContext?.let { ctx -> PcSettings.getPcIp(ctx)?.let { report(it) } }
            listeners.forEach { it() }
        }
    }

    /** Tells the PC what kind of network the phone is on (sent on connect and on every change). */
    fun report(host: String) {
        val i = current
        NotificationConnection.send(host, Protocol.buildEnvelope("phone.network",
            JSONObject().put("transport", i.transport).put("vpn", i.vpn).put("metered", i.metered)))
    }

    private fun read(manager: ConnectivityManager): Info {
        val caps = try { manager.getNetworkCapabilities(manager.activeNetwork) } catch (e: Exception) { null }
            ?: return Info("none", false, false)
        val vpn = caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)
        // A VPN network reports its underlying transports too (Android 10+).
        val transport = when {
            caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> "wifi"
            caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> "cellular"
            caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> "ethernet"
            vpn -> "vpn"
            else -> "unknown"
        }
        val metered = try { manager.isActiveNetworkMetered } catch (e: Exception) { transport == "cellular" }
        return Info(transport, vpn, metered)
    }
}
