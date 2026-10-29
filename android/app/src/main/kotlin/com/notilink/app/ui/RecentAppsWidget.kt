package com.notilink.app.ui

import android.app.PendingIntent
import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.BroadcastReceiver
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.view.View
import android.widget.RemoteViews
import android.widget.Toast
import androidx.core.graphics.drawable.toBitmap
import com.notilink.app.R
import com.notilink.app.net.ConnectionStatus
import com.notilink.app.net.NotificationConnection
import com.notilink.app.net.PcSettings
import com.notilink.app.net.Protocol
import com.notilink.app.net.ShareState
import org.json.JSONObject

/**
 * Home-screen shortcut to "Compartir una app": shows the recent apps (AppPickerActivity.Recents)
 * and one tap opens that app on the PC. How many it shows follows the widget's shape — 1x1 one,
 * 2x1/1x2 two, 1x3 three, 1x4 four — laid out along its long side (a row or a column).
 */
class RecentAppsWidget : AppWidgetProvider() {

    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) {
        ids.forEach { render(context, manager, it) }
    }

    override fun onAppWidgetOptionsChanged(context: Context, manager: AppWidgetManager, id: Int, newOptions: Bundle) {
        render(context, manager, id)
    }

    companion object {
        private val SLOTS = intArrayOf(R.id.widgetSlot0, R.id.widgetSlot1, R.id.widgetSlot2, R.id.widgetSlot3)
        private val ICONS = intArrayOf(R.id.widgetIcon0, R.id.widgetIcon1, R.id.widgetIcon2, R.id.widgetIcon3)
        private val LABELS = intArrayOf(R.id.widgetLabel0, R.id.widgetLabel1, R.id.widgetLabel2, R.id.widgetLabel3)

        /** Re-draws every placed widget — after the recents change or the PC's share.status does. */
        fun refresh(context: Context) {
            val manager = AppWidgetManager.getInstance(context) ?: return
            val ids = try {
                manager.getAppWidgetIds(ComponentName(context, RecentAppsWidget::class.java))
            } catch (e: Exception) { return }
            ids.forEach { render(context, manager, it) }
        }

        /** Slots along the long side, from the widget's current size: in portrait the launcher
         *  reports min width × max height. Cells aren't square, so this rounds the aspect ratio
         *  (padded a little for the gap between cells) instead of dividing by a fixed cell size. */
        private fun shape(manager: AppWidgetManager, id: Int): Pair<Boolean, Int> {
            val o = manager.getAppWidgetOptions(id)
            val w = o.getInt(AppWidgetManager.OPTION_APPWIDGET_MIN_WIDTH, 0).toFloat()
            val h = o.getInt(AppWidgetManager.OPTION_APPWIDGET_MAX_HEIGHT, 0).toFloat()
            if (w <= 0f || h <= 0f) return true to 1
            val vertical = h > w
            val long = if (vertical) h else w
            val short = if (vertical) w else h
            val count = ((long + 16f) / (short + 16f) + 0.3f).toInt().coerceIn(1, SLOTS.size)
            return !vertical to count
        }

        /** "● Name" with the dot in green: that app is being transmitted to the PC right now. */
        fun activeLabel(label: String): CharSequence =
            android.text.SpannableString("● $label").apply {
                setSpan(android.text.style.ForegroundColorSpan(android.graphics.Color.parseColor("#22C55E")), 0, 1,
                    android.text.Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
            }

        private fun render(context: Context, manager: AppWidgetManager, id: Int) {
            val (horizontal, count) = shape(manager, id)
            val views = RemoteViews(context.packageName,
                if (horizontal) R.layout.widget_recents_h else R.layout.widget_recents_v)
            val recents = AppPickerActivity.Recents.get(context).take(count)
            val pm = context.packageManager
            val iconPx = (48 * context.resources.displayMetrics.density).toInt()

            views.setViewVisibility(R.id.widgetEmpty, if (recents.isEmpty()) View.VISIBLE else View.GONE)
            views.setOnClickPendingIntent(R.id.widgetEmpty, PendingIntent.getActivity(context, 0,
                Intent(context, AppPickerActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT))

            for (i in SLOTS.indices) {
                val pkg = recents.getOrNull(i)
                if (pkg == null) {
                    views.setViewVisibility(SLOTS[i], View.GONE)
                    continue
                }
                views.setViewVisibility(SLOTS[i], View.VISIBLE)
                val label = try { pm.getApplicationLabel(pm.getApplicationInfo(pkg, 0)).toString() } catch (e: Exception) { pkg }
                val onPc = ShareState.apps.any { it.packageName == pkg }
                try {
                    views.setImageViewBitmap(ICONS[i], pm.getApplicationIcon(pkg).toBitmap(iconPx, iconPx))
                } catch (e: Exception) {
                    views.setImageViewResource(ICONS[i], R.mipmap.ic_launcher)
                }
                views.setTextViewText(LABELS[i], if (onPc) activeLabel(label) else label)
                // On the PC right now → tapping asks to stop it; otherwise it opens it there.
                val click = if (onPc) {
                    PendingIntent.getActivity(context, id * 10 + i,
                        Intent(context, StopShareConfirmActivity::class.java)
                            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK)
                            .putExtra(StopShareConfirmActivity.EXTRA_PACKAGE, pkg)
                            .putExtra(StopShareConfirmActivity.EXTRA_LABEL, label),
                        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
                } else {
                    PendingIntent.getBroadcast(context, id * 10 + i,
                        Intent(context, WidgetShareReceiver::class.java)
                            .setAction(WidgetShareReceiver.ACTION_SHARE_APP)
                            .putExtra(WidgetShareReceiver.EXTRA_PACKAGE, pkg)
                            .putExtra(WidgetShareReceiver.EXTRA_LABEL, label),
                        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
                }
                views.setOnClickPendingIntent(SLOTS[i], click)
            }
            manager.updateAppWidget(id, views)
        }
    }
}

/** A tap on one of the widget's apps: same `share.request` the app's own "Recientes" sends. */
class WidgetShareReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != ACTION_SHARE_APP) return
        val pkg = intent.getStringExtra(EXTRA_PACKAGE) ?: return
        val label = intent.getStringExtra(EXTRA_LABEL) ?: pkg
        ConnectionStatus.init(context)
        NotificationConnection.init(context.applicationContext)
        val host = PcSettings.getPcIp(context)
        if (host == null || !ConnectionStatus.connected) {
            Toast.makeText(context, R.string.toast_actions_not_connected, Toast.LENGTH_SHORT).show()
            return
        }
        if (ShareState.apps.any { it.packageName == pkg }) {
            Toast.makeText(context, context.getString(R.string.toast_app_already_shared, label), Toast.LENGTH_SHORT).show()
            return
        }
        NotificationConnection.send(host, Protocol.buildEnvelope("share.request",
            JSONObject().put("type", "app").put("packageName", pkg).put("label", label)))
        AppPickerActivity.Recents.add(context, pkg)
        Toast.makeText(context, context.getString(R.string.toast_app_share_requested, label), Toast.LENGTH_SHORT).show()
    }

    companion object {
        const val ACTION_SHARE_APP = "com.notilink.app.ACTION_WIDGET_SHARE_APP"
        const val EXTRA_PACKAGE = "packageName"
        const val EXTRA_LABEL = "label"
    }
}
