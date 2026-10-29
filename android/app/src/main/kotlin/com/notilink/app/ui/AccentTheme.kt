package com.notilink.app.ui

import android.app.Activity
import android.content.Context
import android.content.res.ColorStateList
import android.content.res.Configuration
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.ColorFilter
import android.graphics.Paint
import android.graphics.PixelFormat
import android.graphics.RadialGradient
import android.graphics.Shader
import android.graphics.drawable.Drawable
import android.graphics.drawable.GradientDrawable
import android.view.View
import android.view.ViewGroup
import android.widget.CompoundButton
import android.widget.TextView
import androidx.appcompat.widget.SwitchCompat
import androidx.core.graphics.ColorUtils
import com.google.android.material.button.MaterialButton
import com.google.android.material.button.MaterialButtonToggleGroup
import com.google.android.material.card.MaterialCardView
import java.util.concurrent.CopyOnWriteArrayList

/**
 * The PC's look on the phone (same palette as the PC app, which takes it from Prisma): a near-black
 * violet-tinted background (soft lavender in light mode) with two blurred accent-colored blobs,
 * glass cards, and every accent — segments, buttons, switches, links, the active tiles' gradient —
 * in the accent color the PC sends with `phone.prefs` (its Ajustes → Color de acento, including
 * "Un tono por día"). Default: #7C5CFF.
 */
object AccentTheme {
    const val DEFAULT = 0xFF7C5CFF.toInt()
    private const val PREFS = "notilink_prefs"
    private const val KEY = "accent_color"
    private const val TAG_ACCENT = "accent-text"
    private val listeners = CopyOnWriteArrayList<() -> Unit>()

    fun addListener(l: () -> Unit) { listeners.add(l) }
    fun removeListener(l: () -> Unit) { listeners.remove(l) }

    fun get(context: Context): Int =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getInt(KEY, DEFAULT)

    /** From the PC; ignores the same value so it doesn't redraw for nothing. */
    fun set(context: Context, color: Int) {
        if (color == get(context)) return
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putInt(KEY, color).apply()
        listeners.forEach { it() }
        RecentAppsWidget.refresh(context)
    }

    /** Second gradient color: same hue shifted −40°, slightly lighter (as on the PC). */
    fun secondary(color: Int): Int {
        val hsl = FloatArray(3)
        ColorUtils.colorToHSL(color, hsl)
        hsl[0] = (hsl[0] - 40f + 360f) % 360f
        hsl[2] = (hsl[2] + 0.04f).coerceAtMost(1f)
        return ColorUtils.HSLToColor(hsl)
    }

    fun isDark(context: Context): Boolean =
        (context.resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK) == Configuration.UI_MODE_NIGHT_YES

    fun background(context: Context) = if (isDark(context)) 0xFF0B0B12.toInt() else 0xFFF6F6FB.toInt()
    fun surface(context: Context) = if (isDark(context)) 0x0FFFFFFF else 0xC8FFFFFF.toInt()
    fun stroke(context: Context) = if (isDark(context)) 0x17FFFFFF else 0x17141428
    fun mutedText(context: Context) = if (isDark(context)) 0xFFA3A3B8.toInt() else 0xFF55556B.toInt()

    fun brandGradient(context: Context, radiusDp: Float = 16f): GradientDrawable {
        val accent = get(context)
        return GradientDrawable(GradientDrawable.Orientation.TL_BR, intArrayOf(accent, secondary(accent))).apply {
            cornerRadius = radiusDp * context.resources.displayMetrics.density
        }
    }

    /** Re-skins an activity: background with blobs + every card/button/switch/link under [root]. */
    fun applyTo(activity: Activity, root: View) {
        root.background = BlobBackground(activity)
        activity.window.statusBarColor = Color.TRANSPARENT
        activity.window.navigationBarColor = background(activity)
        style(activity, root)
    }

    fun style(context: Context, view: View) {
        val accent = get(context)
        val density = context.resources.displayMetrics.density
        when (view) {
            is MaterialButtonToggleGroup -> {
                val checked = intArrayOf(android.R.attr.state_checked)
                for (i in 0 until view.childCount) (view.getChildAt(i) as? MaterialButton)?.apply {
                    strokeColor = ColorStateList.valueOf(accent)
                    backgroundTintList = ColorStateList(arrayOf(checked, intArrayOf()), intArrayOf(accent, Color.TRANSPARENT))
                    setTextColor(ColorStateList(arrayOf(checked, intArrayOf()), intArrayOf(Color.WHITE, accent)))
                }
                return
            }
            is MaterialButton -> {
                if (view.strokeWidth == 0) {
                    view.backgroundTintList = ColorStateList.valueOf(accent)
                    view.setTextColor(Color.WHITE)
                } else if (view.currentTextColor != 0xFFE53935.toInt()) {
                    view.strokeColor = ColorStateList.valueOf(accent)
                    view.setTextColor(accent)
                }
            }
            is MaterialCardView -> {
                // Action tiles are styled per state by MainActivity.styleTile.
                if (view.tag != "tile") {
                    view.setCardBackgroundColor(surface(context))
                    view.strokeColor = stroke(context)
                    view.strokeWidth = (1 * density).toInt()
                    view.radius = 16 * density
                    view.cardElevation = 0f
                }
            }
            is SwitchCompat -> tintSwitch(view, accent)
            is android.widget.Switch -> tintSwitch(view, accent)
            is CompoundButton -> view.buttonTintList = ColorStateList(
                arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()), intArrayOf(accent, mutedText(context)))
            is TextView -> if (view.tag == TAG_ACCENT || view.currentTextColor == DEFAULT) {
                view.tag = TAG_ACCENT
                view.setTextColor(accent)
            }
        }
        if (view is ViewGroup) for (i in 0 until view.childCount) style(context, view.getChildAt(i))
    }

    private fun thumbTint() = ColorStateList(arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()), intArrayOf(Color.WHITE, 0xFFD0D0DC.toInt()))
    private fun trackTint(accent: Int) = ColorStateList(arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()), intArrayOf(accent, 0x55888899))

    fun tintSwitch(view: SwitchCompat, accent: Int) {
        view.thumbTintList = thumbTint()
        view.trackTintList = trackTint(accent)
    }

    fun tintSwitch(view: android.widget.Switch, accent: Int) {
        view.thumbTintList = thumbTint()
        view.trackTintList = trackTint(accent)
    }

    /** Pill tabs (Todas / Instaladas / Sistema): selected = accent tint + accent outline. */
    fun pillTab(context: Context): android.graphics.drawable.StateListDrawable {
        val d = context.resources.displayMetrics.density
        val accent = get(context)
        fun shape(fill: Int, strokeColor: Int) = GradientDrawable().apply {
            cornerRadius = 10 * d
            setColor(fill)
            setStroke((1 * d).toInt(), strokeColor)
        }
        return android.graphics.drawable.StateListDrawable().apply {
            addState(intArrayOf(android.R.attr.state_selected), shape(ColorUtils.setAlphaComponent(accent, 0x2E), ColorUtils.setAlphaComponent(accent, 0x99)))
            addState(intArrayOf(), shape(Color.TRANSPARENT, stroke(context)))
        }
    }

    /** Two soft accent-colored blobs (top-right: accent; bottom-left: its −40° hue) on the bg. */
    private class BlobBackground(context: Context) : Drawable() {
        private val dark = isDark(context)
        private val bg = background(context)
        private val accent = get(context)
        private val accent2 = secondary(accent)
        private val paint = Paint(Paint.ANTI_ALIAS_FLAG)

        override fun draw(canvas: Canvas) {
            val b = bounds
            canvas.drawColor(bg)
            val w = b.width().toFloat()
            val h = b.height().toFloat()
            blob(canvas, b.left + w * 0.95f, b.top + h * 0.02f, w * 0.85f, accent, if (dark) 0.26f else 0.16f)
            blob(canvas, b.left + w * 0.10f, b.bottom - h * 0.04f, w * 0.95f, accent2, if (dark) 0.18f else 0.12f)
        }

        private fun blob(canvas: Canvas, cx: Float, cy: Float, r: Float, color: Int, alpha: Float) {
            val c0 = ColorUtils.setAlphaComponent(color, (alpha * 255).toInt())
            val c1 = ColorUtils.setAlphaComponent(color, (alpha * 0.45f * 255).toInt())
            paint.shader = RadialGradient(cx, cy, r, intArrayOf(c0, c1, Color.TRANSPARENT), floatArrayOf(0f, 0.45f, 1f), Shader.TileMode.CLAMP)
            canvas.drawCircle(cx, cy, r, paint)
        }

        override fun setAlpha(alpha: Int) {}
        override fun setColorFilter(colorFilter: ColorFilter?) {}
        @Deprecated("Deprecated in Java")
        override fun getOpacity() = PixelFormat.OPAQUE
    }
}
