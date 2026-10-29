package com.notilink.app.net

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.media.audiofx.Visualizer
import org.json.JSONObject
import kotlin.math.hypot
import kotlin.math.log10
import kotlin.math.sqrt

/**
 * What the phone is playing, as three band levels (bass / mids / highs, 0..1), sent to the PC
 * (`media.levels`, ~15 per second) so its now-playing waves move "al ritmo de la música". Uses
 * Android's Visualizer on the global output mix (session 0): it only analyzes the sound — nothing
 * is recorded or stored — but Android still requires RECORD_AUDIO for it. Runs only while the PC
 * asks (`media.levels.request`: song playing + its player visible), never on mobile data.
 */
object MediaLevels {
    private const val TAG = "NotiLink/MediaLevels"
    private const val SEND_EVERY_MS = 66L

    private var visualizer: Visualizer? = null
    private var host: String? = null
    @Volatile private var lastSend = 0L

    fun hasPermission(context: Context) =
        context.checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED

    @Synchronized
    fun start(context: Context, pcHost: String) {
        host = pcHost
        if (visualizer != null) return
        if (!hasPermission(context) || NetworkMonitor.current.metered) return
        try {
            val v = Visualizer(0)
            val range = Visualizer.getCaptureSizeRange()
            v.captureSize = 1024.coerceIn(range[0], range[1])
            val rate = (Visualizer.getMaxCaptureRate()).coerceAtMost(20_000) // mHz
            v.setDataCaptureListener(object : Visualizer.OnDataCaptureListener {
                override fun onWaveFormDataCapture(vis: Visualizer, waveform: ByteArray, samplingRate: Int) {}
                override fun onFftDataCapture(vis: Visualizer, fft: ByteArray, samplingRate: Int) = onFft(fft, samplingRate)
            }, rate, false, true)
            v.enabled = true
            visualizer = v
        } catch (e: Throwable) {
            DiagLog.w(TAG, "Visualizer unavailable: ${e.javaClass.simpleName} ${e.message}")
            visualizer = null
        }
    }

    @Synchronized
    fun stop() {
        val v = visualizer ?: return
        visualizer = null
        try { v.enabled = false } catch (e: Throwable) { }
        try { v.release() } catch (e: Throwable) { }
    }

    private fun onFft(fft: ByteArray, samplingRateMilliHz: Int) {
        val now = System.currentTimeMillis()
        if (now - lastSend < SEND_EVERY_MS) return
        lastSend = now
        val h = host ?: return
        val n = fft.size
        if (n < 8) return
        val binHz = (samplingRateMilliHz / 1000f) / n
        // fft = [Re0, Re(n/2), Re1, Im1, Re2, Im2, ...]
        fun band(lo: Float, hi: Float): Double {
            val a = (lo / binHz).toInt().coerceAtLeast(1)
            val b = (hi / binHz).toInt().coerceAtMost(n / 2 - 1)
            if (b < a) return 0.0
            var sum = 0.0
            for (k in a..b) {
                val mag = hypot(fft[2 * k].toDouble(), fft[2 * k + 1].toDouble())
                sum += mag * mag
            }
            val rms = sqrt(sum / (b - a + 1))
            val db = 20 * log10(rms + 1e-6)
            // ~0 dB (silence) .. ~40 dB (loud) for byte-scaled FFT → 0..1
            return ((db - 2) / 36).coerceIn(0.0, 1.0)
        }
        val payload = JSONObject()
            .put("b", round2(band(25f, 160f)))
            .put("m", round2(band(160f, 2000f)))
            .put("t", round2(band(2000f, 9000f)))
        NotificationConnection.send(h, Protocol.buildEnvelope("media.levels", payload))
    }

    private fun round2(v: Double) = Math.round(v * 100) / 100.0
}
