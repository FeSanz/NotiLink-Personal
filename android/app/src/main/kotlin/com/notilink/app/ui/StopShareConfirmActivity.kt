package com.notilink.app.ui

import android.os.Bundle
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import com.notilink.app.R
import com.notilink.app.net.NotificationConnection
import com.notilink.app.net.PcSettings
import com.notilink.app.net.Protocol
import org.json.JSONObject

/**
 * Just the "¿Detener la transmisión?" dialog, over whatever is on screen — opened by tapping an
 * app in the widget that's currently on the PC (an AppWidget can't show a dialog by itself).
 */
class StopShareConfirmActivity : AppCompatActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val pkg = intent.getStringExtra(EXTRA_PACKAGE) ?: return finish()
        val label = intent.getStringExtra(EXTRA_LABEL) ?: pkg
        AlertDialog.Builder(this)
            .setTitle(getString(R.string.dialog_stop_share_title, label))
            .setMessage(R.string.dialog_stop_share_message)
            .setPositiveButton(R.string.btn_stop_share) { _, _ ->
                PcSettings.getPcIp(this)?.let { host ->
                    NotificationConnection.init(applicationContext)
                    NotificationConnection.send(host, Protocol.buildEnvelope("share.stop.app", JSONObject().put("packageName", pkg)))
                }
            }
            .setNegativeButton(android.R.string.cancel, null)
            .setOnDismissListener { finish() }
            .show()
    }

    companion object {
        const val EXTRA_PACKAGE = "packageName"
        const val EXTRA_LABEL = "label"
    }
}
