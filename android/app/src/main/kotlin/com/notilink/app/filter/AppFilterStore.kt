package com.notilink.app.filter

import android.content.Context

/**
 * Per-package allow/block state for notification mirroring. Default is
 * allowed for any package with no explicit entry, so existing installs keep
 * mirroring everything until the user opts an app out.
 */
object AppFilterStore {
    private const val PREFS = "notilink_app_filter"

    fun isAllowed(context: Context, packageName: String): Boolean =
        prefs(context).getBoolean(packageName, true)

    fun setAllowed(context: Context, packageName: String, allowed: Boolean) {
        prefs(context).edit().putBoolean(packageName, allowed).apply()
    }

    private fun prefs(context: Context) =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
}
