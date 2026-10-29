package com.notilink.app.net

import android.content.Context
import android.util.Log
import org.json.JSONObject
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * A small rolling diagnostic log, kept on-device and shipped to the PC on every fresh
 * reconnect (see NotiLinkNotificationListener.sendDiagLog) — written because USB/adb access to
 * the phone isn't always available when something needs debugging (open-on-device failures,
 * missed call detection), but a live NotiLink connection almost always is. Every entry also
 * still goes to Log.i/Log.w as before, so adb logcat keeps working exactly the same when it
 * IS available.
 */
object DiagLog {
    private const val FILE_NAME = "diag.log"
    private const val MAX_BYTES = 200_000 // trimmed from the oldest entries once exceeded
    private val timestampFormat = SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US)

    private var logFile: File? = null
    private val lock = Any()

    fun init(context: Context) {
        if (logFile != null) return
        logFile = File(context.filesDir, FILE_NAME)
    }

    fun i(tag: String, message: String) {
        Log.i(tag, message)
        append(tag, message)
    }

    fun w(tag: String, message: String) {
        Log.w(tag, message)
        append(tag, message)
    }

    private fun append(tag: String, message: String) {
        val file = logFile ?: return
        synchronized(lock) {
            try {
                file.appendText("${timestampFormat.format(Date())} [$tag] $message\n")
                trimIfNeeded(file)
            } catch (e: Exception) {
                // Logging must never be the thing that crashes the app.
            }
        }
    }

    private fun trimIfNeeded(file: File) {
        if (file.length() <= MAX_BYTES) return
        val lines = file.readLines()
        file.writeText(lines.takeLast(lines.size / 2).joinToString("\n") + "\n")
    }

    fun readAll(): String = try {
        logFile?.takeIf { it.exists() }?.readText() ?: ""
    } catch (e: Exception) {
        ""
    }

    /** Manual override for MainActivity's "Enviar logs ahora" button — sends immediately
     *  regardless of the listener service's own change-detection cadence (every ~8s, only if
     *  changed), for when the user wants a guaranteed, on-demand send rather than waiting.
     *  Returns false if there was nothing to send (empty log) or no PC configured yet. */
    fun sendNow(context: Context): Boolean {
        val host = PcSettings.getPcIp(context) ?: return false
        val text = readAll()
        if (text.isBlank()) return false
        NotificationConnection.init(context)
        NotificationConnection.send(host, Protocol.buildEnvelope("diag.log", JSONObject().put("text", text)))
        return true
    }
}
