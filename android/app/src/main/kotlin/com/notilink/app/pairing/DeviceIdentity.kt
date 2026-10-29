package com.notilink.app.pairing

import android.content.Context
import android.os.Build
import java.util.UUID

/** Stable per-install identity sent in every handshake.hello (see docs/PROTOCOL.md). */
object DeviceIdentity {
    private const val PREFS = "notilink_identity"
    private const val KEY_DEVICE_ID = "device_id"

    fun getDeviceId(context: Context): String {
        val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        prefs.getString(KEY_DEVICE_ID, null)?.let { return it }
        val id = UUID.randomUUID().toString()
        prefs.edit().putString(KEY_DEVICE_ID, id).apply()
        return id
    }

    fun getDeviceName(): String = "${Build.MANUFACTURER} ${Build.MODEL}".trim()
}
