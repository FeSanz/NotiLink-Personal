package com.notilink.app.filter

import android.content.Context
import android.content.pm.PackageManager
import android.graphics.Bitmap
import androidx.core.content.ContextCompat
import androidx.core.graphics.drawable.toBitmap

/**
 * PackageManager.getApplicationIcon() goes through the launcher's icon theming
 * pipeline — on Samsung One UI (and other OEMs) with "Themed icons" enabled,
 * that can return a flat monochrome icon instead of the app's real full-color
 * one. Resolving the icon through a Context scoped to the target package (via
 * createPackageContext) still correctly inflates adaptive icons — unlike raw
 * resource lookup, which breaks on most modern icons — while bypassing that
 * theming wrapper. Shared by the notification listener and the Apps filter screen.
 */
object AppIconLoader {
    fun load(context: Context, packageName: String): Bitmap? {
        val fromPackageContext = try {
            val pkgContext = context.createPackageContext(packageName, 0)
            val iconResId = context.packageManager.getApplicationInfo(packageName, 0).icon
            if (iconResId != 0) ContextCompat.getDrawable(pkgContext, iconResId)?.toBitmap() else null
        } catch (e: Exception) {
            null
        }
        if (fromPackageContext != null) return fromPackageContext

        return try {
            context.packageManager.getApplicationIcon(packageName).toBitmap()
        } catch (e: PackageManager.NameNotFoundException) {
            null
        }
    }
}
