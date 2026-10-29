package com.notilink.app.ui

import android.content.Context
import android.graphics.drawable.Drawable
import android.os.Bundle
import android.text.Editable
import android.text.TextWatcher
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.EditText
import android.widget.ImageView
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.notilink.app.R
import com.notilink.app.filter.InstalledAppsProvider
import com.notilink.app.net.ConnectionStatus
import com.notilink.app.net.NotificationConnection
import com.notilink.app.net.PcSettings
import com.notilink.app.net.Protocol
import com.notilink.app.net.ShareState
import org.json.JSONObject

/** "Compartir una app" from the phone: pick an app and the PC opens only that app in its own
 *  window (same as choosing it on the PC — see docs/PROTOCOL.md's share.request type "app"). */
class AppPickerActivity : AppCompatActivity() {

    private data class Entry(val packageName: String, val label: String, val recent: Boolean)

    private var all: List<Entry> = emptyList()
    private val adapter = PickerAdapter()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_app_picker)
        title = getString(R.string.title_app_picker)
        findViewById<View>(android.R.id.content).applySystemBarPadding()
        (findViewById<ViewGroup>(android.R.id.content).getChildAt(0))?.let { AccentTheme.applyTo(this, it) }

        val recycler = findViewById<RecyclerView>(R.id.recyclerPickerApps)
        recycler.layoutManager = LinearLayoutManager(this)
        recycler.adapter = adapter

        findViewById<EditText>(R.id.editPickerSearch).addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}
            override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {}
            override fun afterTextChanged(s: Editable?) = applyFilter(s?.toString().orEmpty())
        })

        Thread {
            val recents = Recents.get(this)
            // Dual Messenger / work-profile copies ("pkg#10") can't be opened on another display.
            val apps = InstalledAppsProvider.list(this).filter { '#' !in it.packageName }
            val entries = apps.map { Entry(it.packageName, it.label, it.packageName in recents) }
                .sortedWith(compareBy<Entry>({ if (it.recent) recents.indexOf(it.packageName) else Int.MAX_VALUE }, { it.label.lowercase() }))
            runOnUiThread {
                all = entries
                applyFilter("")
            }
        }.start()
    }

    private fun applyFilter(query: String) {
        val q = query.trim().lowercase()
        adapter.submit(if (q.isEmpty()) all else all.filter { q in it.label.lowercase() || q in it.packageName.lowercase() })
    }

    private fun pick(entry: Entry) {
        val host = PcSettings.getPcIp(this)
        if (host == null || !ConnectionStatus.connected) {
            Toast.makeText(this, R.string.toast_actions_not_connected, Toast.LENGTH_SHORT).show()
            return
        }
        if (ShareState.apps.any { it.packageName == entry.packageName }) {
            Toast.makeText(this, getString(R.string.toast_app_already_shared, entry.label), Toast.LENGTH_SHORT).show()
            return
        }
        NotificationConnection.send(host, Protocol.buildEnvelope("share.request", JSONObject()
            .put("type", "app").put("packageName", entry.packageName).put("label", entry.label)))
        Recents.add(this, entry.packageName)
        Toast.makeText(this, getString(R.string.toast_app_share_requested, entry.label), Toast.LENGTH_SHORT).show()
        finish()
    }

    /** The phone's own last-4 list (the PC keeps its own, per phone). */
    /** "Recientes": the same list here, in the widget and in the PC app. Whichever side changes
     *  it sends the whole list + when it changed (share.recents); the newer one wins. */
    object Recents {
        private const val PREFS = "notilink_prefs"
        private const val KEY = "recent_share_apps"
        private const val KEY_AT = "recent_share_apps_at"
        private const val MAX = 4
        private val listeners = java.util.concurrent.CopyOnWriteArrayList<() -> Unit>()

        fun addListener(l: () -> Unit) { listeners.add(l) }
        fun removeListener(l: () -> Unit) { listeners.remove(l) }

        fun get(context: Context): List<String> =
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString(KEY, "")!!
                .split(',').filter { it.isNotBlank() }
        private fun updatedAt(context: Context): Long =
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getLong(KEY_AT, 0L)

        fun add(context: Context, pkg: String) = save(context, (listOf(pkg) + get(context).filter { it != pkg }).take(MAX))
        fun remove(context: Context, pkg: String) = save(context, get(context).filter { it != pkg })

        private fun save(context: Context, list: List<String>, at: Long = System.currentTimeMillis(), push: Boolean = true) {
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
                .putString(KEY, list.joinToString(",")).putLong(KEY_AT, at).apply()
            RecentAppsWidget.refresh(context)
            listeners.forEach { it() }
            if (push) push(context)
        }

        /** Sends this phone's list to the PC (on every change and on each fresh connection). */
        fun push(context: Context) {
            val host = PcSettings.getPcIp(context) ?: return
            val pm = context.packageManager
            val apps = org.json.JSONArray()
            for (pkg in get(context)) {
                val label = try { pm.getApplicationLabel(pm.getApplicationInfo(pkg, 0)).toString() } catch (e: Exception) { pkg }
                apps.put(org.json.JSONObject().put("packageName", pkg).put("label", label))
            }
            NotificationConnection.init(context.applicationContext)
            NotificationConnection.send(host, Protocol.buildEnvelope("share.recents",
                org.json.JSONObject().put("apps", apps).put("updatedAt", updatedAt(context))))
        }

        /** The PC's list arrived: adopt it if newer, or answer with ours if ours is newer. */
        fun onRemote(context: Context, payload: org.json.JSONObject) {
            val remoteAt = payload.optLong("updatedAt", 0L)
            val localAt = updatedAt(context)
            val arr = payload.optJSONArray("apps") ?: org.json.JSONArray()
            val remote = (0 until arr.length()).mapNotNull { arr.optJSONObject(it)?.optString("packageName")?.takeIf { p -> p.isNotBlank() } }
                .filter { pkg -> try { context.packageManager.getApplicationInfo(pkg, 0); true } catch (e: Exception) { false } }
            when {
                remoteAt > localAt -> if (remote != get(context)) save(context, remote.take(MAX), remoteAt, push = false)
                    else context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putLong(KEY_AT, remoteAt).apply()
                remoteAt < localAt -> push(context)
            }
        }
    }

    private inner class PickerAdapter : RecyclerView.Adapter<PickerAdapter.Holder>() {
        private var items: List<Entry> = emptyList()
        private val icons = HashMap<String, Drawable?>()

        fun submit(list: List<Entry>) {
            items = list
            @Suppress("NotifyDataSetChanged")
            notifyDataSetChanged()
        }

        inner class Holder(view: View) : RecyclerView.ViewHolder(view) {
            val icon: ImageView = view.findViewById(R.id.imagePickerIcon)
            val label: TextView = view.findViewById(R.id.textPickerLabel)
            val tag: TextView = view.findViewById(R.id.textPickerTag)
        }

        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int) =
            Holder(LayoutInflater.from(parent.context).inflate(R.layout.item_picker_app, parent, false))

        override fun getItemCount() = items.size

        override fun onBindViewHolder(holder: Holder, position: Int) {
            val e = items[position]
            holder.label.text = e.label
            val icon = icons.getOrPut(e.packageName) {
                try { packageManager.getApplicationIcon(e.packageName) } catch (ex: Exception) { null }
            }
            holder.icon.setImageDrawable(icon)
            val shared = ShareState.apps.any { it.packageName == e.packageName }
            holder.tag.visibility = if (shared || e.recent) View.VISIBLE else View.GONE
            holder.tag.text = getString(if (shared) R.string.tag_app_on_pc else R.string.tag_app_recent)
            holder.itemView.setOnClickListener { pick(e) }
        }
    }
}
