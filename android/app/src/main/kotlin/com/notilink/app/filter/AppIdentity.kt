package com.notilink.app.filter

/**
 * Dual Messenger / cloned-app instances (2 WhatsApp, 2 Facebook, etc.) share the exact same
 * package name as the original — Android just runs them under a second user profile. To tell
 * them apart everywhere an app is identified (the filter list, AppFilterStore, and the
 * "packageName" field sent over the wire), the profile's user id is folded into one composite
 * string. The primary profile (userId 0) is left as a bare package name so every existing
 * saved filter preference and every non-dual device keeps working unchanged.
 */
object AppIdentity {
    private const val SEPARATOR = "#"

    fun key(packageName: String, userId: Int): String =
        if (userId == 0) packageName else "$packageName$SEPARATOR$userId"

    fun realPackage(key: String): String = key.substringBefore(SEPARATOR)
}
