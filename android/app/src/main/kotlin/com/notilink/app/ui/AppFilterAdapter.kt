package com.notilink.app.ui

import android.view.LayoutInflater
import android.view.ViewGroup
import androidx.recyclerview.widget.RecyclerView
import com.notilink.app.databinding.ItemAppFilterBinding
import com.notilink.app.filter.AppIconLoader
import com.notilink.app.filter.AppIdentity
import com.notilink.app.filter.InstalledApp

class AppFilterAdapter(
    private val allApps: List<InstalledApp>,
    private val isAllowed: (String) -> Boolean,
    private val onToggle: (packageName: String, allowed: Boolean) -> Unit,
) : RecyclerView.Adapter<AppFilterAdapter.ViewHolder>() {

    private var apps: List<InstalledApp> = allApps
    private var query: String = ""
    private var category: String = "all" // "all" | "user" | "system" — mirrors the PC's own apps-modal tabs

    inner class ViewHolder(val binding: ItemAppFilterBinding) : RecyclerView.ViewHolder(binding.root)

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): ViewHolder {
        val binding = ItemAppFilterBinding.inflate(LayoutInflater.from(parent.context), parent, false)
        return ViewHolder(binding)
    }

    override fun onBindViewHolder(holder: ViewHolder, position: Int) {
        val app = apps[position]
        val context = holder.binding.root.context
        holder.binding.textAppLabel.text = app.label
        holder.binding.imageAppIcon.setImageBitmap(
            AppIconLoader.load(context, AppIdentity.realPackage(app.packageName))
        )

        // Avoid re-firing onToggle while programmatically setting the initial state.
        AccentTheme.tintSwitch(holder.binding.switchAllowed, AccentTheme.get(holder.binding.switchAllowed.context))
        holder.binding.switchAllowed.setOnCheckedChangeListener(null)
        holder.binding.switchAllowed.isChecked = isAllowed(app.packageName)
        holder.binding.switchAllowed.setOnCheckedChangeListener { _, checked ->
            onToggle(app.packageName, checked)
        }
    }

    override fun getItemCount(): Int = apps.size

    /** Called on every search-box keystroke — filters the full list by label, case-insensitive. */
    fun filter(newQuery: String) {
        query = newQuery
        applyFilters()
    }

    /** Called when a Todas/Instaladas/Sistema tab is tapped. */
    fun setCategory(newCategory: String) {
        category = newCategory
        applyFilters()
    }

    private fun applyFilters() {
        apps = allApps
            .filter { category == "all" || (category == "system") == it.isSystemApp }
            .filter { query.isBlank() || it.label.contains(query, ignoreCase = true) }
        notifyDataSetChanged()
    }
}
