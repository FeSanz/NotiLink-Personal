package com.notilink.app.ui

import android.view.View
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat

/**
 * Apps targeting SDK 35 (Android 15) get edge-to-edge enforced by the system with no opt-out —
 * content draws behind the status bar and gesture nav bar unless it explicitly reserves that
 * space. Without this, the top card's title sits under the status bar clock/icons and looks
 * cramped/overlapped. Adds the system bars' inset on top of whatever padding the view already
 * had in XML, rather than replacing it, and is a no-op on older APIs where insets are already 0.
 */
fun View.applySystemBarPadding() {
    val initialLeft = paddingLeft
    val initialTop = paddingTop
    val initialRight = paddingRight
    val initialBottom = paddingBottom
    ViewCompat.setOnApplyWindowInsetsListener(this) { v, insets ->
        val bars = insets.getInsets(WindowInsetsCompat.Type.systemBars())
        v.setPadding(initialLeft + bars.left, initialTop + bars.top, initialRight + bars.right, initialBottom + bars.bottom)
        insets
    }
}
