package com.notilink.app.ui

import android.content.Intent
import android.os.Bundle
import android.provider.OpenableColumns
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import com.notilink.app.R
import com.notilink.app.net.NotificationConnection
import com.notilink.app.net.PcSettings
import com.notilink.app.net.Protocol
import org.json.JSONObject
import java.util.UUID

/**
 * The Android share-sheet target — shows up as "Enviar a la PC" whenever the user taps Share
 * on a link, text, or file anywhere else on the phone (see AndroidManifest's SEND intent-filter,
 * mimeType wildcard). Deliberately invisible (Theme.NotiLink.Transparent, noHistory): it exists
 * only to grab the shared content and hand it to the PC over the existing NotiLink connection,
 * then get out of the way — never a real screen the user has to look at or back out of.
 */
class ShareToPcActivity : AppCompatActivity() {

    // Matches TcpServer.MaxFrameLength on the PC — a larger file would just get its connection
    // dropped there, so it's rejected here instead with a message that actually explains why.
    private val maxFileBytes = 8 * 1024 * 1024

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val host = PcSettings.getPcIp(this)
        if (host == null) {
            Toast.makeText(this, R.string.toast_share_no_pc, Toast.LENGTH_SHORT).show()
            finish()
            return
        }
        NotificationConnection.init(applicationContext)

        val sharedText = intent?.getStringExtra(Intent.EXTRA_TEXT)
        // ACTION_SEND_MULTIPLE (2+ items picked in the share sheet) delivers EXTRA_STREAM as an
        // ArrayList<Uri> instead of a single Uri — a genuinely different extra shape, not just
        // "the same one with more entries", so it needs its own extraction path rather than
        // falling back to the single-Uri getParcelableExtra below and finding nothing.
        @Suppress("DEPRECATION")
        val sharedStreams: List<android.net.Uri> = if (intent?.action == Intent.ACTION_SEND_MULTIPLE) {
            intent?.getParcelableArrayListExtra<android.net.Uri>(Intent.EXTRA_STREAM) ?: emptyList()
        } else {
            intent?.getParcelableExtra<android.net.Uri>(Intent.EXTRA_STREAM)?.let { listOf(it) } ?: emptyList()
        }

        when {
            sharedStreams.isNotEmpty() -> {
                // Each file is its own independent file.send (own transferId, own PC-side card,
                // own save to Downloads) — sent one after another over the same connection,
                // exactly like sharing each of them individually one at a time would. A single
                // combined toast at the end instead of one per file, which would otherwise queue
                // up and flash by one at a time for a multi-file share.
                val sentCount = sharedStreams.count { sendFile(host, it) }
                if (sentCount > 0) {
                    val message = if (sentCount > 1) getString(R.string.toast_shared_multiple_to_pc, sentCount) else getString(R.string.toast_shared_to_pc)
                    Toast.makeText(this, message, Toast.LENGTH_SHORT).show()
                }
            }
            !sharedText.isNullOrBlank() -> sendText(host, sharedText)
            else -> Toast.makeText(this, R.string.toast_share_empty, Toast.LENGTH_SHORT).show()
        }
        finish()
    }

    private fun sendText(host: String, text: String) {
        val payload = JSONObject().put("text", text)
        NotificationConnection.send(host, Protocol.buildEnvelope("share.send", payload))
        Toast.makeText(this, R.string.toast_shared_to_pc, Toast.LENGTH_SHORT).show()
    }

    /** Returns whether the file was actually sent — the caller (onCreate) shows one combined
     *  toast for a whole multi-file share instead of one per file, so this stays quiet on
     *  success; per-file failure toasts (empty/too big) still fire individually since those are
     *  genuinely per-file diagnostic info worth keeping. */
    private fun sendFile(host: String, uri: android.net.Uri): Boolean {
        val fileName = queryDisplayName(uri) ?: uri.lastPathSegment ?: "archivo"
        val mimeType = contentResolver.getType(uri) ?: "application/octet-stream"
        val bytes = try {
            contentResolver.openInputStream(uri)?.use { it.readBytes() }
        } catch (e: Exception) {
            null
        }
        if (bytes == null) {
            Toast.makeText(this, R.string.toast_share_empty, Toast.LENGTH_SHORT).show()
            return false
        }
        if (bytes.size > maxFileBytes) {
            Toast.makeText(this, R.string.toast_share_file_too_big, Toast.LENGTH_LONG).show()
            return false
        }

        val payload = JSONObject().apply {
            put("fileName", fileName)
            put("mimeType", mimeType)
            put("size", bytes.size)
            put("transferId", UUID.randomUUID().toString())
        }
        NotificationConnection.sendWithImage(host, Protocol.buildEnvelope("file.send", payload), bytes)
        return true
    }

    private fun queryDisplayName(uri: android.net.Uri): String? = try {
        contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { cursor ->
            if (cursor.moveToFirst()) cursor.getString(0) else null
        }
    } catch (e: Exception) {
        null
    }
}
