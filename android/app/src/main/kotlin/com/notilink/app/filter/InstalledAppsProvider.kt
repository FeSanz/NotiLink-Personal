package com.notilink.app.filter

import android.content.Context
import android.content.pm.ApplicationInfo
import android.content.pm.LauncherApps
import android.os.Process
import android.os.UserManager
import android.util.Log

data class InstalledApp(val packageName: String, val label: String, val isSystemApp: Boolean)

/**
 * Every app (identity, see AppIdentity) that has posted a notification on this phone. Apps with no
 * launcher icon — Samsung's "Modos y rutinas", AlwaysOnDisplay, many system services — never show
 * up in the launcher-based list, yet do post notifications the user may want to silence on the
 * PC; remembering them here lets the filter list (phone + PC) include them.
 */
object NotifyingApps {
    private const val PREFS = "notilink_notifying_apps"
    private const val KEY = "identities"

    fun all(context: Context): Set<String> =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getStringSet(KEY, emptySet()) ?: emptySet()

    /** Returns true when [identity] wasn't known yet (the caller then resends the app list). */
    @Synchronized
    fun add(context: Context, identity: String): Boolean {
        val current = all(context)
        if (identity in current) return false
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
            .putStringSet(KEY, HashSet(current).apply { add(identity) }).apply()
        return true
    }
}

/**
 * User-facing apps only (has a launcher entry) — matches what could plausibly post a
 * notification worth mirroring, not every system service/component. Also walks every user
 * profile the app can see via LauncherApps, not just the primary one, so a Dual
 * Messenger/cloned second instance of an app shows up as its own separate, separately
 * toggleable entry (see AppIdentity) instead of being invisible or merged with the original.
 */
object InstalledAppsProvider {
    private const val TAG = "NotiLink/AppList"

    fun list(context: Context): List<InstalledApp> {
        val ownPackage = context.packageName
        val userManager = context.getSystemService(Context.USER_SERVICE) as UserManager
        val launcherApps = context.getSystemService(Context.LAUNCHER_APPS_SERVICE) as LauncherApps
        val primaryUserId = Process.myUserHandle().hashCode()

        val result = LinkedHashMap<String, InstalledApp>()
        for (userHandle in userManager.userProfiles) {
            val userId = userHandle.hashCode()
            val activities = try {
                launcherApps.getActivityList(null, userHandle)
            } catch (e: SecurityException) {
                // Non-primary profiles (Dual Messenger, work profile) can refuse cross-profile
                // listing to an app that isn't the default launcher — skip that profile rather
                // than crash; its notifications may still arrive and mirror by default even if
                // they can't be listed here to toggle ahead of time.
                Log.w(TAG, "cannot list apps for profile $userId: ${e.message}")
                emptyList()
            }
            activities.asSequence()
                .filter { it.applicationInfo.packageName != ownPackage }
                .distinctBy { it.applicationInfo.packageName }
                .forEach { activity ->
                    val pkg = activity.applicationInfo.packageName
                    val isPrimary = userId == primaryUserId
                    val key = AppIdentity.key(pkg, if (isPrimary) 0 else userId)
                    if (!result.containsKey(key)) {
                        val label = activity.label.toString()
                        // FLAG_UPDATED_SYSTEM_APP covers a system app the user later updated via
                        // the Play Store (e.g. Google apps) — still a "system" app for this
                        // filter's purposes, not something the user personally installed.
                        val flags = activity.applicationInfo.flags
                        val isSystemApp = (flags and ApplicationInfo.FLAG_SYSTEM) != 0 ||
                            (flags and ApplicationInfo.FLAG_UPDATED_SYSTEM_APP) != 0
                        result[key] = InstalledApp(key, if (isPrimary) label else "$label (dual)", isSystemApp)
                    }
                }
        }
        // Plus apps without a launcher icon that have posted notifications (e.g. Modos y rutinas).
        val pm = context.packageManager
        for (identity in NotifyingApps.all(context)) {
            if (result.containsKey(identity)) continue
            val pkg = AppIdentity.realPackage(identity)
            if (pkg == ownPackage) continue
            val info = try { pm.getApplicationInfo(pkg, 0) } catch (e: Exception) { continue } // uninstalled
            val label = try { pm.getApplicationLabel(info).toString() } catch (e: Exception) { pkg }
            val isSystemApp = (info.flags and ApplicationInfo.FLAG_SYSTEM) != 0 ||
                (info.flags and ApplicationInfo.FLAG_UPDATED_SYSTEM_APP) != 0
            result[identity] = InstalledApp(identity, if ('#' in identity) "$label (dual)" else label, isSystemApp)
        }
        return result.values.sortedBy { it.label.lowercase() }
    }
}
