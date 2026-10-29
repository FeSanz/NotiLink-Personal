package com.notilink.app.net

import android.content.Context
import android.util.Log
import com.notilink.app.pairing.DeviceIdentity
import com.notilink.app.pairing.PairingTokenStore
import org.json.JSONObject
import java.io.InputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.Executors

/**
 * Holds the single persistent socket used by the notification listener to
 * talk to the PC — both directions. All connect/send/close calls are
 * serialized onto one background thread so callbacks arriving from the
 * listener's binder thread never touch the socket concurrently or block the
 * main thread. A separate daemon thread continuously reads incoming frames
 * (delete/action commands from the PC) for as long as the current socket
 * lives, restarting whenever a fresh connection is established.
 *
 * Every send first tries the existing stream; only on failure does it close
 * and reconnect, then retries once. This is what lets the phone notice a PC
 * that just came back online (closed/reopened its window) — the next
 * heartbeat or notification send simply finds the old stream dead and
 * silently re-establishes it, re-sending handshake.hello on the new socket.
 */
object NotificationConnection {
    private const val TAG = "NotiLink/Connection"
    private const val MAX_FRAME_LENGTH = 8 * 1024 * 1024

    private val executor = Executors.newSingleThreadExecutor()
    private const val AWAY_RETRY_MS = 30_000L
    @Volatile private var lastConnectAttemptMs = 0L
    private var socket: Socket? = null
    private var out: OutputStream? = null
    private var readThread: Thread? = null
    private lateinit var appContext: Context

    /** Set by the listener to handle commands arriving from the PC (delete/action.invoke/etc). */
    var onCommand: ((type: String, payload: JSONObject) -> Unit)? = null

    /** Set by the listener to handle a binary (0x02) frame arriving from the PC — currently
     *  only file.send uses this (see docs/PROTOCOL.md). Frames always immediately follow the
     *  JSON message that referenced them, so the listener is responsible for remembering what
     *  the most recently-seen file.send said before this fires. */
    var onBinaryFrame: ((bytes: ByteArray) -> Unit)? = null

    /** Set by the listener to resync (active notifications, apps, battery, wallpaper) —
     *  fires every time connect() actually establishes a brand-new socket, no matter which
     *  caller's send() happened to be the one that discovered the old one was dead. Deliberately
     *  a durable field here rather than each send()'s own onFreshConnect parameter: that
     *  parameter only fires for the one specific call that triggers the reconnect (e.g. a
     *  battery-change broadcast winning the race against the next heartbeat tick), so a resync
     *  tied only to it could silently never run for an entire connection's lifetime whenever
     *  some other, resync-less caller happens to be first to notice the drop — which is exactly
     *  what was happening: the reconnect went through via whichever send got there first, no
     *  resync fired, and the PC's notification list stayed stuck showing nothing until a
     *  genuinely new notification arrived on its own. */
    var onConnectionEstablished: (() -> Unit)? = null

    /** Must be called once (e.g. from the listener service's onCreate) before the first connect. */
    fun init(context: Context) {
        appContext = context.applicationContext
    }

    fun send(host: String, envelope: JSONObject, onFreshConnect: (() -> Unit)? = null) =
        sendWithImages(host, envelope, emptyList(), onFreshConnect)

    fun sendWithImage(host: String, envelope: JSONObject, imageBytes: ByteArray?, onFreshConnect: (() -> Unit)? = null) =
        sendWithImages(host, envelope, imageBytes?.let { listOf(it) } ?: emptyList(), onFreshConnect)

    /**
     * [images] are written as binary (0x02) frames immediately after the
     * JSON envelope, in order, on the same connection — per docs/PROTOCOL.md,
     * image frames always immediately follow the message that references
     * them via transferId, never independently.
     */
    fun sendWithImages(host: String, envelope: JSONObject, images: List<ByteArray>, onFreshConnect: (() -> Unit)? = null) {
        executor.execute {
            if (ConnectionStatus.manuallyDisconnected) return@execute
            val jsonFrame = Protocol.frameJson(envelope)
            var delivered = writeFrame(jsonFrame)
            if (!delivered) {
                closeQuietly()
                // PC known to be off/closed: retry only every AWAY_RETRY_MS instead of on every
                // heartbeat/notification (each failed attempt can block up to 5 s).
                val now = System.currentTimeMillis()
                if (ConnectionStatus.pcAway && now - lastConnectAttemptMs < AWAY_RETRY_MS) return@execute
                lastConnectAttemptMs = now
                var target = host
                var ok = connect(target)
                if (!ok) alternate(host)?.let { found -> target = found; ok = true }
                if (!ok) rediscover(host)?.let { found -> target = found; ok = connect(found) }
                if (ok) {
                    onFreshConnect?.invoke()
                    delivered = writeFrame(jsonFrame)
                }
            }
            if (delivered) {
                for (image in images) {
                    if (!writeFrame(Protocol.frameBinary(image))) break
                }
            }
        }
    }

    /** Closes the current socket without touching [ConnectionStatus.manuallyDisconnected] —
     *  used internally on write failure, distinct from a user-requested disconnect. */
    fun close() {
        executor.execute {
            Log.i(TAG, "close() executing, socket=${socket != null}")
            closeQuietly()
        }
    }

    private fun writeFrame(frame: ByteArray): Boolean {
        val stream = out ?: return false
        return try {
            stream.write(frame)
            stream.flush()
            true
        } catch (e: Exception) {
            // Routed through DiagLog (not just Log.w) specifically so a disconnect that happens
            // with no USB/adb available still leaves a trail once it reaches the PC on the next
            // successful reconnect — this write path is what a *phone-initiated* disconnect looks
            // like (as opposed to the read loop's own timeout/EOF detection below).
            DiagLog.w(TAG, "write failed, will reconnect: ${e.javaClass.simpleName} ${e.message}")
            false
        }
    }

    private var lastRediscoverMs = 0L

    /** The saved IP didn't answer: try the PC's other known addresses (another LAN IP, or its
     *  Tailscale address when away from home). Connects as it goes; returns the one that worked. */
    private fun alternate(failed: String): String? {
        if (!::appContext.isInitialized) return null
        for (ip in PcSettings.candidates(appContext)) {
            if (ip == failed) continue
            if (connect(ip)) {
                DiagLog.i(TAG, "PC reachable at $ip (instead of $failed)")
                PcSettings.setPcIp(appContext, ip)
                return ip
            }
        }
        return null
    }

    /** NetworkMonitor: the phone switched networks — drop the old socket and reconnect now
     *  (through whichever of the PC's addresses answers on the new network). */
    fun onNetworkChanged() {
        executor.execute {
            closeQuietly()
            lastConnectAttemptMs = 0L
            lastRediscoverMs = 0L
        }
        if (!::appContext.isInitialized) return
        PcSettings.getPcIp(appContext)?.let { send(it, Protocol.buildEnvelope("heartbeat", JSONObject())) }
    }

    /** The PC isn't at the saved IP: the network changed (e.g. the PC joined this phone's hotspot,
     *  or the router gave it another IP). Asks the LAN — at most every 20 s — and, if a NotiLink
     *  PC answers at a new address, saves it so every later send goes there. The pairing token is
     *  per PC, not per IP, so the handshake still authenticates as before. */
    private fun rediscover(oldHost: String): String? {
        val now = System.currentTimeMillis()
        if (now - lastRediscoverMs < 20_000L) return null
        lastRediscoverMs = now
        if (!::appContext.isInitialized) return null
        val ctx = appContext
        // Only ever the ACTIVE PC: with two NotiLink PCs on the network, taking whichever answers
        // first would silently move this phone to the other PC. An active PC whose id isn't known
        // yet (saved before PCs announced one) is only followed when it's the only PC around.
        val activeId = PcSettings.activePcId(ctx)
        val all = com.notilink.app.pairing.DiscoveryClient.discoverAll(com.notilink.app.pairing.DeviceIdentity.getDeviceName(), 2000)
        val match = if (activeId != null && !activeId.startsWith("ip:")) all.firstOrNull { it.pcId == activeId } else all.singleOrNull()
        val found = match?.ip ?: return null
        if (found == oldHost) return null
        DiagLog.i(TAG, "PC moved: $oldHost -> $found (rediscovered)")
        PcSettings.setPcIp(ctx, found)
        return found
    }

    /** Drops the current socket and dials [host] right away — used when the user switches to
     *  another saved PC (only one PC is ever connected at a time) or explicitly reconnects. */
    fun reconnectTo(host: String) {
        executor.execute {
            closeQuietly()
            lastConnectAttemptMs = 0L
            lastRediscoverMs = 0L
        }
        ConnectionStatus.setPcAway(false)
        send(host, Protocol.buildEnvelope("heartbeat", JSONObject()))
    }

    private fun connect(host: String): Boolean {
        return try {
            val s = Socket()
            s.connect(InetSocketAddress(host, Protocol.TCP_PORT), 5_000)
            // A bounded wait, not indefinite (0) — a PC that silently vanishes (its own WiFi
            // drops, sleeps without properly closing the socket, etc.) never sends a TCP FIN/RST
            // either, so an indefinite read here could block forever without ever surfacing a
            // failure — the write-failure-triggers-reconnect path in sendWithImages() never gets
            // a chance to run because nothing ever calls it (this app might have nothing new to
            // send for a while), and this read thread would just sit on a socket that's already
            // dead. 60s is generous next to both sides' 15s heartbeat cadence (see
            // docs/PROTOCOL.md) — a healthy connection always receives SOMETHING well before
            // that; only a genuinely dead one ever times out.
            s.soTimeout = 60_000
            socket = s
            out = s.getOutputStream()
            startReadLoop(s, s.getInputStream())

            // handshake.hello must be the very first frame on a fresh connection
            // (see docs/PROTOCOL.md) — written directly here, synchronously,
            // rather than via send()/sendWithImages(), which would queue it
            // behind whatever message actually triggered this connect().
            val handshakePayload = JSONObject().apply {
                put("deviceId", DeviceIdentity.getDeviceId(appContext))
                put("deviceName", DeviceIdentity.getDeviceName())
                // Same value as deviceName today, but kept as a separate field on purpose: the
                // PC stores this one as an immutable hardware fingerprint (never overwritten by
                // a rename), so if this same physical phone ever re-pairs under a fresh deviceId
                // (e.g. app data was wiped by a real uninstall, not just a replace-install), the
                // PC can recognize it and carry the custom name over instead of resetting it —
                // see MainViewModel.HandlePairConfirm.
                put("deviceModel", DeviceIdentity.getDeviceName())
                put("pairingToken", PairingTokenStore.getToken(appContext) ?: JSONObject.NULL)
                // Every PC this phone has been paired with — see PairingTokenStore.
                put("pairingTokens", org.json.JSONArray(PairingTokenStore.getTokens(appContext)))
                put("protocolVersion", 1)
            }
            writeFrame(Protocol.frameJson(Protocol.buildEnvelope("handshake.hello", handshakePayload)))

            DiagLog.i(TAG, "connected to $host:${Protocol.TCP_PORT} (tokens sent: ${PairingTokenStore.getTokens(appContext).joinToString { it.take(6) + "…" }.ifEmpty { "none" }})")
            ConnectionStatus.setConnected(true)
            onConnectionEstablished?.invoke()
            true
        } catch (e: Exception) {
            DiagLog.w(TAG, "connect to $host failed: ${e.javaClass.simpleName} ${e.message}")
            ConnectionStatus.setConnected(false)
            false
        }
    }

    private fun startReadLoop(socket: Socket, input: InputStream) {
        readThread?.interrupt()
        val thread = Thread({
            try {
                val header = ByteArray(Protocol.HEADER_LENGTH)
                while (!socket.isClosed) {
                    // A clean EOF (the PC closed its end normally) has no exception to log in the
                    // catch block below — logged here instead so "loop just stopped, no error at
                    // all" is distinguishable from a timeout/exception in the DiagLog output.
                    if (!readFully(input, header)) {
                        DiagLog.w(TAG, "read loop: EOF from PC, connection closed cleanly")
                        break
                    }
                    val (length, type) = Protocol.decodeHeader(header)
                    if (length < 0 || length > MAX_FRAME_LENGTH) {
                        DiagLog.w(TAG, "incoming frame length $length exceeds max, dropping connection")
                        break
                    }
                    val payload = ByteArray(length)
                    if (!readFully(input, payload)) break

                    if (type == Protocol.FRAME_TYPE_JSON) {
                        try {
                            val envelope = JSONObject(String(payload, Charsets.UTF_8))
                            val msgType = envelope.optString("type")
                            val msgPayload = envelope.optJSONObject("payload") ?: JSONObject()
                            onCommand?.invoke(msgType, msgPayload)
                        } catch (e: Exception) {
                            Log.w(TAG, "malformed JSON frame from PC: ${e.message}")
                        }
                    } else if (type == Protocol.FRAME_TYPE_BINARY) {
                        // file.send's payload bytes — see onBinaryFrame's doc.
                        onBinaryFrame?.invoke(payload)
                    }
                }
            } catch (e: Exception) {
                // Covers the soTimeout above firing (SocketTimeoutException, meaning 60s+ with no
                // data from the PC at all) just as much as a real I/O error — either way this
                // connection is done. The exception's own class name is what actually tells the
                // two apart when reading this back later (a `SocketTimeoutException` here would
                // mean the PC's own heartbeat send isn't actually reaching this phone reliably,
                // despite TcpServer.MaintenanceTick sending one every 10s — worth checking that
                // specifically if this keeps showing up).
                DiagLog.w(TAG, "read loop ended: ${e.javaClass.simpleName} ${e.message}")
            } finally {
                // Whatever ended this loop (EOF, timeout, malformed-frame drop, any exception),
                // the socket this thread was reading is done for — closing it here (rather than
                // leaving socket/out pointing at a half-dead connection until some future send()
                // happens to notice) means the very next thing that tries to talk to the PC goes
                // through a clean connect() instead of a write attempt that could itself hang on
                // the same dead socket. Only touches state if this thread is still the current
                // one — connect() may have already replaced it with a fresh connection by the
                // time an old thread's loop finally unwinds.
                if (readThread === Thread.currentThread()) closeQuietly()
            }
        }, "NotiLink-read")
        thread.isDaemon = true
        readThread = thread
        thread.start()
    }

    private fun readFully(input: InputStream, buffer: ByteArray): Boolean {
        var offset = 0
        while (offset < buffer.size) {
            val read = input.read(buffer, offset, buffer.size - offset)
            if (read == -1) return false
            offset += read
        }
        return true
    }

    private fun closeQuietly() {
        readThread?.interrupt()
        readThread = null
        try { out?.close() } catch (_: Exception) {}
        try { socket?.close() } catch (_: Exception) {}
        out = null
        socket = null
        ConnectionStatus.setConnected(false)
    }
}
