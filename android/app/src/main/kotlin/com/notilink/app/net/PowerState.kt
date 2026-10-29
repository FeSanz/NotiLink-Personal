package com.notilink.app.net

import android.content.Context

/** Whether NotiLink was switched off from its notification ("Apagar"). Persisted, so a system
 *  rebind (boot, app update) doesn't turn it back on; opening the app does. */
object PowerState {
    private const val PREFS = "notilink_power"
    private const val KEY_OFF = "off"

    fun isOff(context: Context): Boolean =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean(KEY_OFF, false)

    fun set(context: Context, off: Boolean) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean(KEY_OFF, off).commit()
    }
}
