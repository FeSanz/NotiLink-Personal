package com.notilink.app.ui

import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import android.view.View
import android.widget.Switch
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import com.google.zxing.integration.android.IntentIntegrator
import com.notilink.app.BuildConfig
import com.notilink.app.R
import com.notilink.app.databinding.ActivityMainBinding
import com.notilink.app.databinding.ItemActionTileBinding
import com.notilink.app.net.ConnectionStatus
import com.notilink.app.net.DiagLog
import com.notilink.app.net.NotificationConnection
import com.notilink.app.net.PcSettings
import com.notilink.app.net.Protocol
import com.notilink.app.net.ShareState
import com.notilink.app.net.WallpaperSync
import com.notilink.app.pairing.DeviceIdentity
import com.notilink.app.pairing.DiscoveryClient
import com.notilink.app.pairing.PairingState
import com.notilink.app.pairing.PairingTokenStore
import org.json.JSONObject

class MainActivity : AppCompatActivity() {

    private lateinit var binding: ActivityMainBinding

    /** One row in the unified "Permisos y optimización" list (see item_setting_permission.xml).
     *  [onManageGranted] is a real settings deep-link, never a no-op — Android gives apps no API
     *  to silently revoke their own permissions, so "revocar" always means "send the user to the
     *  one place that actually can". */
    private data class PermissionRowSpec(
        val title: String,
        val subtitleGranted: String,
        val subtitleDenied: String,
        val isGranted: () -> Boolean,
        val onRequestGrant: () -> Unit,
        val onManageGranted: () -> Unit,
    )

    private val permissionRows = mutableListOf<Pair<PermissionRowSpec, View>>()

    // Firing two permission-request launches back to back in the same onCreate is unreliable —
    // on at least one device the second launch() was silently dropped (its dialog just never
    // appeared) because the first one's system dialog was still being set up. Chaining them
    // through this callback — wallpaper permissions requested only once notifications' request
    // has actually resolved — guarantees both dialogs really show on a fresh install.
    private val audioLevelsPermissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { refreshPermissionRows() }

    private val notificationPermissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { requestWallpaperPermission() }
    private val wallpaperPermissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { grants ->
            // Granted right now, possibly well after the listener service's own last (failed)
            // attempt at connect time — retry immediately instead of waiting for the next
            // reconnect/heartbeat cycle to pick it up.
            if (grants.values.any { it }) {
                val host = PcSettings.getPcIp(this)
                if (host != null) {
                    NotificationConnection.init(applicationContext)
                    WallpaperSync.send(applicationContext, host)
                }
            }
            // Chained the same way as notifications->wallpaper above, for the same reason:
            // firing this alongside the wallpaper request risks the dialog being dropped.
            locationPermissionLauncher.launch(android.Manifest.permission.ACCESS_FINE_LOCATION)
        }
    private val locationPermissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) {
            // Same chaining reason as everywhere else in this sequence.
            phoneStatePermissionLauncher.launch(android.Manifest.permission.READ_PHONE_STATE)
        }
    // "Fotos y archivos": whatever the user picks goes to the PC exactly like sharing it to
    // NotiLink from another app's Share button (ShareToPcActivity handles both).
    private val mediaPickerLauncher =
        registerForActivityResult(ActivityResultContracts.GetMultipleContents()) { uris ->
            if (uris.isNullOrEmpty()) return@registerForActivityResult
            val intent = Intent(this, ShareToPcActivity::class.java).apply {
                action = Intent.ACTION_SEND_MULTIPLE
                type = "*/*"
                putParcelableArrayListExtra(Intent.EXTRA_STREAM, ArrayList(uris))
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            }
            startActivity(intent)
        }

    private val phoneStatePermissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { /* no-op: next battery.update just includes cellular info if granted */ }

    private fun requestWallpaperPermission() {
        // Purely cosmetic (the PC app shows this faintly behind the notification list) — if
        // denied, WallpaperSync.send() just skips it, nothing else depends on this permission.
        // Both are requested since at least one OEM build gates WallpaperManager on the legacy
        // permission even past API 33, not just the granular media one.
        wallpaperPermissionLauncher.launch(wallpaperPermissions())
    }

    private fun wallpaperPermissions(): Array<String> =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU)
            arrayOf(android.Manifest.permission.READ_MEDIA_IMAGES, android.Manifest.permission.READ_EXTERNAL_STORAGE)
        else
            arrayOf(android.Manifest.permission.READ_EXTERNAL_STORAGE)

    /** Any one of the wallpaper permissions being granted is enough — on 13+ the legacy
     *  READ_EXTERNAL_STORAGE is capped at old target-sdk apps and may never be grantable here,
     *  so requiring both would show "denied" even when the granular one already works fine. */
    private fun hasWallpaperPermission(): Boolean =
        wallpaperPermissions().any { isGranted(it) }

    private fun isGranted(permission: String): Boolean =
        ContextCompat.checkSelfPermission(this, permission) == PackageManager.PERMISSION_GRANTED

    /** No AndroidX helper exists for accessibility services the way NotificationManagerCompat
     *  covers notification listeners — Settings.Secure's own colon-separated list of enabled
     *  ComponentNames is the standard (only) way to check this. */
    private fun isAccessibilityServiceEnabled(): Boolean {
        val expected = ComponentName(this, com.notilink.app.listener.NotiLinkAccessibilityService::class.java).flattenToString()
        val enabled = Settings.Secure.getString(contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES) ?: return false
        return enabled.split(':').any { it.equals(expected, ignoreCase = true) }
    }

    /** Huawei/Honor (HarmonyOS, EMUI) run their own "Inicio de aplicaciones" manager on top of
     *  Android's battery-optimization exemption: an app left on "Automático" gets its background
     *  work killed regardless. There's no public intent for it, so the known system-manager
     *  screens are tried in order, falling back to App Info (also where the user finds
     *  Battery > Launch on newer builds). */
    private fun setupHuaweiHelp() {
        val maker = Build.MANUFACTURER.lowercase()
        if (!maker.contains("huawei") && !maker.contains("honor")) return
        binding.textHuaweiHint.visibility = android.view.View.VISIBLE
        binding.buttonHuaweiLaunch.visibility = android.view.View.VISIBLE
        binding.buttonHuaweiLaunch.setOnClickListener {
            val candidates = listOf(
                "com.huawei.systemmanager" to "com.huawei.systemmanager.appcontrol.activity.StartupAppControlActivity",
                "com.huawei.systemmanager" to "com.huawei.systemmanager.startupmgr.ui.StartupNormalAppListActivity",
                "com.huawei.systemmanager" to "com.huawei.systemmanager.optimize.process.ProtectActivity",
                "com.huawei.systemmanager" to "com.huawei.systemmanager.power.ui.HwPowerManagerActivity",
                "com.hihonor.systemmanager" to "com.hihonor.systemmanager.startupmgr.ui.StartupNormalAppListActivity",
            )
            val opened = candidates.any { (pkg, cls) ->
                try {
                    startActivity(Intent().setClassName(pkg, cls))
                    true
                } catch (_: Exception) {
                    false
                }
            }
            if (!opened) openAppSettings()
        }
    }

    private fun openAppSettings() {
        startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:$packageName")))
    }

    /** A real, separate "special access" grant from ordinary Notification Access — confirmed via
     *  NotificationManager's own docs, not assumed (see AndroidManifest's ACCESS_NOTIFICATION_
     *  POLICY comment). Without this, setInterruptionFilter() from the PC's "No molestar" button
     *  silently does nothing. */
    private fun isDndAccessGranted(): Boolean {
        val manager = getSystemService(NOTIFICATION_SERVICE) as android.app.NotificationManager
        return manager.isNotificationPolicyAccessGranted
    }

    /** Builds the permission list once, from onCreate — order matters (most critical first)
     *  but nothing else about a row is hardcoded in the layout, so adding/removing a
     *  permission only means editing this list, never activity_main.xml. */
    private fun buildPermissionRows() {
        val specs = mutableListOf<PermissionRowSpec>()

        specs += PermissionRowSpec(
            title = getString(R.string.perm_title_notification_access),
            subtitleGranted = getString(R.string.perm_subtitle_notification_access_on),
            subtitleDenied = getString(R.string.perm_subtitle_notification_access_off),
            isGranted = { NotificationManagerCompat.getEnabledListenerPackages(this).contains(packageName) },
            onRequestGrant = { startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS)) },
            // Same screen either way — it's a toggle, not a one-way grant dialog.
            onManageGranted = { startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS)) },
        )

        specs += PermissionRowSpec(
            title = getString(R.string.perm_title_battery),
            subtitleGranted = getString(R.string.perm_subtitle_battery_on),
            subtitleDenied = getString(R.string.perm_subtitle_battery_off),
            isGranted = { (getSystemService(Context.POWER_SERVICE) as PowerManager).isIgnoringBatteryOptimizations(packageName) },
            onRequestGrant = { startActivity(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:$packageName"))) },
            // No system intent un-exempts an app directly — App Info's own Battery entry is
            // the closest real place to do it.
            onManageGranted = { openAppSettings() },
        )

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            specs += PermissionRowSpec(
                title = getString(R.string.perm_title_post_notifications),
                subtitleGranted = getString(R.string.perm_subtitle_post_notifications_on),
                subtitleDenied = getString(R.string.perm_subtitle_post_notifications_off),
                isGranted = { isGranted(android.Manifest.permission.POST_NOTIFICATIONS) },
                onRequestGrant = { notificationPermissionLauncher.launch(android.Manifest.permission.POST_NOTIFICATIONS) },
                onManageGranted = { openAppSettings() },
            )
        }

        specs += PermissionRowSpec(
            title = getString(R.string.perm_title_photos),
            subtitleGranted = getString(R.string.perm_subtitle_photos_on),
            subtitleDenied = getString(R.string.perm_subtitle_photos_off),
            isGranted = { hasWallpaperPermission() },
            onRequestGrant = { requestWallpaperPermission() },
            onManageGranted = { openAppSettings() },
        )

        specs += PermissionRowSpec(
            title = getString(R.string.perm_title_location),
            subtitleGranted = getString(R.string.perm_subtitle_location_on),
            subtitleDenied = getString(R.string.perm_subtitle_location_off),
            isGranted = { isGranted(android.Manifest.permission.ACCESS_FINE_LOCATION) },
            onRequestGrant = { locationPermissionLauncher.launch(android.Manifest.permission.ACCESS_FINE_LOCATION) },
            onManageGranted = { openAppSettings() },
        )

        specs += PermissionRowSpec(
            title = getString(R.string.perm_title_audio_levels),
            subtitleGranted = getString(R.string.perm_subtitle_audio_levels_on),
            subtitleDenied = getString(R.string.perm_subtitle_audio_levels_off),
            isGranted = { isGranted(android.Manifest.permission.RECORD_AUDIO) },
            onRequestGrant = { audioLevelsPermissionLauncher.launch(android.Manifest.permission.RECORD_AUDIO) },
            onManageGranted = { openAppSettings() },
        )

        specs += PermissionRowSpec(
            title = getString(R.string.perm_title_phone_state),
            subtitleGranted = getString(R.string.perm_subtitle_phone_state_on),
            subtitleDenied = getString(R.string.perm_subtitle_phone_state_off),
            isGranted = { isGranted(android.Manifest.permission.READ_PHONE_STATE) },
            onRequestGrant = { phoneStatePermissionLauncher.launch(android.Manifest.permission.READ_PHONE_STATE) },
            onManageGranted = { openAppSettings() },
        )

        specs += PermissionRowSpec(
            title = getString(R.string.perm_title_accessibility),
            subtitleGranted = getString(R.string.perm_subtitle_accessibility_on),
            subtitleDenied = getString(R.string.perm_subtitle_accessibility_off),
            isGranted = { isAccessibilityServiceEnabled() },
            // No API grants this silently or deep-links straight to this app's own toggle —
            // Android deliberately requires the user to land on the general list and find it
            // themselves, the same one-time friction Phone Link's own "Link to Windows"
            // accessibility permission has.
            onRequestGrant = { startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)) },
            onManageGranted = { startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)) },
        )

        specs += PermissionRowSpec(
            title = getString(R.string.perm_title_answer_calls),
            subtitleGranted = getString(R.string.perm_subtitle_answer_calls_on),
            subtitleDenied = getString(R.string.perm_subtitle_answer_calls_off),
            isGranted = { isGranted(android.Manifest.permission.ANSWER_PHONE_CALLS) },
            onRequestGrant = { notificationPermissionLauncher.launch(android.Manifest.permission.ANSWER_PHONE_CALLS) },
            onManageGranted = { openAppSettings() },
        )

        specs += PermissionRowSpec(
            title = getString(R.string.perm_title_rotation),
            subtitleGranted = getString(R.string.perm_subtitle_rotation_on),
            subtitleDenied = getString(R.string.perm_subtitle_rotation_off),
            isGranted = { Settings.System.canWrite(this) },
            onRequestGrant = { startActivity(Intent(Settings.ACTION_MANAGE_WRITE_SETTINGS, Uri.parse("package:$packageName"))) },
            onManageGranted = { startActivity(Intent(Settings.ACTION_MANAGE_WRITE_SETTINGS, Uri.parse("package:$packageName"))) },
        )

        specs += PermissionRowSpec(
            title = getString(R.string.perm_title_dnd),
            subtitleGranted = getString(R.string.perm_subtitle_dnd_on),
            subtitleDenied = getString(R.string.perm_subtitle_dnd_off),
            isGranted = { isDndAccessGranted() },
            // Same as Accessibility above — no API grants this silently or deep-links to this
            // app's own toggle specifically, just the general "apps with DND access" list.
            onRequestGrant = { startActivity(Intent(Settings.ACTION_NOTIFICATION_POLICY_ACCESS_SETTINGS)) },
            onManageGranted = { startActivity(Intent(Settings.ACTION_NOTIFICATION_POLICY_ACCESS_SETTINGS)) },
        )

        val dividerPx = (1 * resources.displayMetrics.density).toInt()
        binding.layoutPermissions.removeAllViews()
        permissionRows.clear()
        specs.forEachIndexed { index, spec ->
            if (index > 0) {
                val divider = View(this).apply { setBackgroundResource(R.drawable.bg_divider) }
                binding.layoutPermissions.addView(divider, android.widget.LinearLayout.LayoutParams(
                    android.widget.LinearLayout.LayoutParams.MATCH_PARENT, dividerPx
                ))
            }
            val row = layoutInflater.inflate(R.layout.item_setting_permission, binding.layoutPermissions, false)
            row.findViewById<TextView>(R.id.rowTitle).text = spec.title
            row.setOnClickListener { if (spec.isGranted()) spec.onManageGranted() else spec.onRequestGrant() }
            binding.layoutPermissions.addView(row)
            permissionRows.add(spec to row)
        }
    }

    private fun refreshPermissionRows() {
        permissionRows.forEach { (spec, row) ->
            val granted = spec.isGranted()
            row.findViewById<TextView>(R.id.rowSubtitle).text = if (granted) spec.subtitleGranted else spec.subtitleDenied
            row.findViewById<Switch>(R.id.rowSwitch).isChecked = granted
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)
        binding.root.applySystemBarPadding()

        PcSettings.getPcIp(this)?.let { binding.editPcIp.setText(it) }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            notificationPermissionLauncher.launch(android.Manifest.permission.POST_NOTIFICATIONS)
        } else {
            requestWallpaperPermission()
        }

        buildPermissionRows()
        setupSegments()
        setupActions()
        applyAccentTheme()

        binding.textAboutVersion.text = getString(R.string.about_version, BuildConfig.VERSION_NAME)
        binding.textAboutBuild.text = getString(R.string.about_build, BuildConfig.BUILD_TIMESTAMP)
        binding.textAboutAuthor.text = getString(R.string.about_author, "Brandroid")


        // See hint_permission_auto_revoke — there's no public, OEM-stable Settings intent that
        // deep-links straight to the "Pausar actividad si no se usa" toggle itself (it lives a
        // couple of taps deep inside the Permissions screen, differently on stock Android vs.
        // One UI), so App Info is the closest reliably-reachable starting point.
        binding.buttonAppInfo.setOnClickListener { openAppSettings() }
        setupHuaweiHelp()

        setupConnectionSection()

        binding.buttonConfirmPairing.setOnClickListener {
            val host = PcSettings.getPcIp(this)
            if (host != null) {
                val payload = JSONObject().put("confirmed", true)
                NotificationConnection.send(host, Protocol.buildEnvelope("pair.confirm", payload))
            }
            hidePairingCode()
        }

        binding.buttonSendLogs.setOnClickListener {
            if (PcSettings.getPcIp(this) == null) {
                Toast.makeText(this, R.string.toast_logs_no_pc, Toast.LENGTH_SHORT).show()
                return@setOnClickListener
            }
            val sent = DiagLog.sendNow(this)
            Toast.makeText(this, if (sent) R.string.toast_logs_sent else R.string.toast_logs_empty, Toast.LENGTH_SHORT).show()
        }
    }

    /** Same share.send message ShareToPcActivity sends from the Android share sheet — this is
     *  just a second, in-app way to trigger it, for when there's no other app's own Share button
     *  to go through (typed directly, or pasted from anywhere without needing a share target). */
    private fun sendShareText(host: String, text: String) {
        NotificationConnection.init(applicationContext)
        val payload = JSONObject().put("text", text)
        NotificationConnection.send(host, Protocol.buildEnvelope("share.send", payload))
        Toast.makeText(this, R.string.toast_shared_to_pc, Toast.LENGTH_SHORT).show()
    }

    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        val result = IntentIntegrator.parseActivityResult(requestCode, resultCode, data)
        if (result?.contents != null) {
            val ip = result.contents.trim()
            if (!PcSettings.isValidIpv4(ip)) {
                Toast.makeText(this, R.string.toast_qr_invalid, Toast.LENGTH_LONG).show()
                return
            }
            binding.editPcIp.setText(ip)
            binding.editPcIp.error = null
            // Checked like a typed IP: is a NotiLink PC really there, and which one? Finding a
            // working PC is an unambiguous "I want to be connected" signal, so it also connects.
            probeAndUse(ip, binding.buttonScanQr)
        } else {
            super.onActivityResult(requestCode, resultCode, data)
        }
    }

    override fun onResume() {
        super.onResume()
        // Back from "Apagar": opening the app is what turns NotiLink on again.
        if (com.notilink.app.net.PowerState.isOff(this)) {
            com.notilink.app.net.PowerState.set(this, false)
            com.notilink.app.net.ConnectionStatus.setManuallyDisconnected(false)
            try {
                android.service.notification.NotificationListenerService.requestRebind(
                    android.content.ComponentName(this, com.notilink.app.listener.NotiLinkNotificationListener::class.java)
                )
            } catch (_: Exception) { }
        }
        // None of Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS / battery exemption / runtime
        // permission screens have a completion callback we can rely on, so re-checking every
        // row here on resume (including right after coming back from Settings) is the only
        // reliable way to reflect whatever the user actually did there.
        refreshPermissionRows()

        PairingState.onCodeReceived = { code -> runOnUiThread { showPairingCode(code) } }
        PairingState.onPaired = { runOnUiThread { refreshConnectionSetupVisibility() } }
        PairingState.pendingCode?.let { showPairingCode(it) }
        ConnectionStatus.addChangeListener(connectionStatusListener)
        ShareState.addListener(shareStateListener)
        AppPickerActivity.Recents.addListener(shareStateListener)
        AccentTheme.addListener(accentListener)
        com.notilink.app.net.NetworkMonitor.addListener(connectionStatusListener)
        applyAccentTheme()
        refreshConnectionSetupVisibility()
        renderActions()
    }

    override fun onPause() {
        super.onPause()
        PairingState.onCodeReceived = null
        PairingState.onPaired = null
        ConnectionStatus.removeChangeListener(connectionStatusListener)
        ShareState.removeListener(shareStateListener)
        AppPickerActivity.Recents.removeListener(shareStateListener)
        AccentTheme.removeListener(accentListener)
        com.notilink.app.net.NetworkMonitor.removeListener(connectionStatusListener)
    }

    private fun applyAccentTheme() {
        AccentTheme.applyTo(this, binding.root)
        listOf(binding.tabCategoryAll, binding.tabCategoryUser, binding.tabCategorySystem).forEach {
            it.background = AccentTheme.pillTab(this)
        }
    }

    /** The PC changed its accent color (phone.prefs): re-skin live. */
    private val accentListener: () -> Unit = {
        runOnUiThread {
            applyAccentTheme()
            renderActions()
            binding.recyclerApps.adapter?.let { @Suppress("NotifyDataSetChanged") it.notifyDataSetChanged() }
        }
    }

    /** Live-updates "esperando conexión"/"conectado" while this activity is in the foreground —
     *  e.g. right after tapping "Reconectar" — instead of only refreshing on the next onResume. */
    private val connectionStatusListener: () -> Unit = { runOnUiThread { refreshConnectionSetupVisibility(); renderActions() } }
    private val shareStateListener: () -> Unit = { runOnUiThread { renderActions() } }

    // ---------------- Segments: Acciones (first) / Permisos (second) ----------------

    private fun setupSegments() {
        val prefs = getSharedPreferences("notilink_prefs", MODE_PRIVATE)
        val last = prefs.getString("main_segment", "actions") ?: "actions"
        binding.toggleSegments.check(when (last) {
            "permissions" -> R.id.buttonSegmentPermissions
            "notifications" -> R.id.buttonSegmentNotifications
            else -> R.id.buttonSegmentActions
        })
        selectSegment(last)
        binding.toggleSegments.addOnButtonCheckedListener { _, checkedId, isChecked ->
            if (!isChecked) return@addOnButtonCheckedListener
            val segment = when (checkedId) {
                R.id.buttonSegmentPermissions -> "permissions"
                R.id.buttonSegmentNotifications -> "notifications"
                else -> "actions"
            }
            selectSegment(segment)
            prefs.edit().putString("main_segment", segment).apply()
        }
    }

    private fun selectSegment(segment: String) {
        binding.layoutActionsPanel.visibility = if (segment == "actions") View.VISIBLE else View.GONE
        binding.layoutPermissionsPanel.visibility = if (segment == "permissions") View.VISIBLE else View.GONE
        binding.layoutNotificationsPanel.visibility = if (segment == "notifications") View.VISIBLE else View.GONE
        if (segment == "permissions") refreshPermissionRows()
        if (segment == "notifications") ensureAppFilterLoaded()
    }

    // ---------------- Notificaciones: which apps get mirrored (was its own screen) ----------------

    private var appFilterAdapter: AppFilterAdapter? = null
    private var appFilterLoading = false

    private fun ensureAppFilterLoaded() {
        if (appFilterAdapter != null || appFilterLoading) return
        appFilterLoading = true
        binding.recyclerApps.layoutManager = androidx.recyclerview.widget.LinearLayoutManager(this)
        binding.editAppSearch.addTextChangedListener(object : android.text.TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}
            override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {}
            override fun afterTextChanged(s: android.text.Editable?) { appFilterAdapter?.filter(s?.toString() ?: "") }
        })
        val categoryTabs = listOf(
            binding.tabCategoryAll to "all",
            binding.tabCategoryUser to "user",
            binding.tabCategorySystem to "system",
        )
        binding.tabCategoryAll.isSelected = true
        categoryTabs.forEach { (tab, category) ->
            tab.setOnClickListener {
                categoryTabs.forEach { (t, _) -> t.isSelected = t === tab }
                appFilterAdapter?.setCategory(category)
            }
        }
        Thread {
            val apps = com.notilink.app.filter.InstalledAppsProvider.list(applicationContext)
            runOnUiThread {
                if (isFinishing) return@runOnUiThread
                val adapter = AppFilterAdapter(
                    allApps = apps,
                    isAllowed = { pkg -> com.notilink.app.filter.AppFilterStore.isAllowed(applicationContext, pkg) },
                    onToggle = { pkg, allowed -> onToggleApp(pkg, allowed) },
                )
                appFilterAdapter = adapter
                binding.recyclerApps.adapter = adapter
                appFilterLoading = false
            }
        }.start()
    }

    /** Same bidirectional sync as before: whichever side changes it sends app.filter.update. */
    private fun onToggleApp(packageName: String, allowed: Boolean) {
        com.notilink.app.filter.AppFilterStore.setAllowed(applicationContext, packageName, allowed)
        val host = PcSettings.getPcIp(applicationContext) ?: return
        NotificationConnection.send(host, Protocol.buildEnvelope("app.filter.update",
            JSONObject().put("packageName", packageName).put("allowed", allowed)))
    }

    // ---------------- Acciones ----------------

    private fun setupActions() {
        binding.tileScreen.tileIcon.text = "🖥️"
        binding.tileScreen.tileTitle.setText(R.string.tile_screen_title)
        binding.tileApp.tileIcon.text = "📱"
        binding.tileApp.tileTitle.setText(R.string.tile_app_title)
        binding.tileAudio.tileIcon.text = "🔊"
        binding.tileAudio.tileTitle.setText(R.string.tile_audio_title)
        binding.tileMedia.tileIcon.text = "🖼️"
        binding.tileMedia.tileTitle.setText(R.string.tile_media_title)
        binding.buttonSendShareText.setOnClickListener {
            val text = binding.editShareText.text.toString().trim()
            if (text.isEmpty()) return@setOnClickListener
            val host = connectedHostOrToast() ?: return@setOnClickListener
            sendShareText(host, text)
            binding.editShareText.setText("")
        }

        binding.tileScreen.root.setOnClickListener {
            val host = connectedHostOrToast() ?: return@setOnClickListener
            if (ShareState.screenSharing) {
                send(host, "share.stop.screen", JSONObject())
                Toast.makeText(this, R.string.toast_share_stopping, Toast.LENGTH_SHORT).show()
            } else {
                send(host, "share.request", JSONObject().put("type", "screen"))
                Toast.makeText(this, R.string.toast_share_requested_screen, Toast.LENGTH_SHORT).show()
            }
        }
        binding.tileApp.root.setOnClickListener {
            connectedHostOrToast() ?: return@setOnClickListener
            startActivity(Intent(this, AppPickerActivity::class.java))
        }
        binding.tileAudio.root.setOnClickListener {
            val host = connectedHostOrToast() ?: return@setOnClickListener
            if (ShareState.audioSharing) {
                send(host, "share.stop.audio", JSONObject())
                Toast.makeText(this, R.string.toast_share_stopping, Toast.LENGTH_SHORT).show()
            } else {
                send(host, "share.request", JSONObject().put("type", "audio"))
                Toast.makeText(this, R.string.toast_share_requested_audio, Toast.LENGTH_SHORT).show()
            }
        }
        binding.tileMedia.root.setOnClickListener {
            connectedHostOrToast() ?: return@setOnClickListener
            mediaPickerLauncher.launch("*/*")
        }
        binding.buttonStopAll.setOnClickListener {
            val host = connectedHostOrToast() ?: return@setOnClickListener
            send(host, "share.stop.all", JSONObject())
            Toast.makeText(this, R.string.toast_share_stopping, Toast.LENGTH_SHORT).show()
        }
    }

    /** "Recientes": up to 4 of the last apps shared from here (icon + name), one tap to open again. */
    private fun renderRecents(connected: Boolean) {
        val row = binding.rowRecents
        row.removeAllViews()
        val recents = AppPickerActivity.Recents.get(this).take(4)
        binding.textRecentsEmpty.visibility = if (recents.isEmpty()) View.VISIBLE else View.GONE
        row.visibility = if (recents.isEmpty()) View.GONE else View.VISIBLE
        binding.cardRecents.alpha = if (connected) 1f else 0.45f
        val density = resources.displayMetrics.density
        // Fixed-width cells, centered as a group, so 1–3 recents don't hug the left edge.
        val cellWidth = minOf((76 * density).toInt(), (resources.displayMetrics.widthPixels - (60 * density).toInt()) / 4)
        for (pkg in recents) {
            val cell = android.widget.LinearLayout(this).apply {
                orientation = android.widget.LinearLayout.VERTICAL
                gravity = android.view.Gravity.CENTER_HORIZONTAL
                layoutParams = android.widget.LinearLayout.LayoutParams(cellWidth, android.widget.LinearLayout.LayoutParams.WRAP_CONTENT)
            }
            run {
                val label = try { packageManager.getApplicationLabel(packageManager.getApplicationInfo(pkg, 0)).toString() } catch (e: Exception) { pkg }
                val shared = ShareState.apps.any { it.packageName == pkg }
                cell.addView(android.widget.ImageView(this).apply {
                    try { setImageDrawable(packageManager.getApplicationIcon(pkg)) } catch (e: Exception) { }
                    contentDescription = label
                    layoutParams = android.widget.LinearLayout.LayoutParams((40 * density).toInt(), (40 * density).toInt())
                })
                cell.addView(TextView(this).apply {
                    text = if (shared) RecentAppsWidget.activeLabel(label) else label
                    textSize = 11f
                    maxLines = 1
                    ellipsize = android.text.TextUtils.TruncateAt.END
                    gravity = android.view.Gravity.CENTER
                    setTextColor(getThemeTextColor())
                    setPadding((2 * density).toInt(), (4 * density).toInt(), (2 * density).toInt(), 0)
                })
                cell.background = android.util.TypedValue().let { tv ->
                    theme.resolveAttribute(android.R.attr.selectableItemBackgroundBorderless, tv, true)
                    ContextCompat.getDrawable(this, tv.resourceId)
                }
                cell.setOnClickListener {
                    val host = connectedHostOrToast() ?: return@setOnClickListener
                    if (ShareState.apps.any { it.packageName == pkg }) {
                        androidx.appcompat.app.AlertDialog.Builder(this)
                            .setTitle(getString(R.string.dialog_stop_share_title, label))
                            .setMessage(R.string.dialog_stop_share_message)
                            .setPositiveButton(R.string.btn_stop_share) { _, _ ->
                                send(host, "share.stop.app", JSONObject().put("packageName", pkg))
                            }
                            .setNegativeButton(android.R.string.cancel, null)
                            .show()
                        return@setOnClickListener
                    }
                    send(host, "share.request", JSONObject().put("type", "app").put("packageName", pkg).put("label", label))
                    AppPickerActivity.Recents.add(this, pkg)
                    Toast.makeText(this, getString(R.string.toast_app_share_requested, label), Toast.LENGTH_SHORT).show()
                }
                cell.setOnLongClickListener { anchor ->
                    android.widget.PopupMenu(this, anchor).apply {
                        menu.add(R.string.menu_remove_recent).setOnMenuItemClickListener {
                            AppPickerActivity.Recents.remove(this@MainActivity, pkg)
                            Toast.makeText(this@MainActivity, getString(R.string.toast_recent_removed, label), Toast.LENGTH_SHORT).show()
                            renderActions()
                            true
                        }
                        show()
                    }
                    true
                }
            }
            row.addView(cell)
        }
    }

    private fun connectedHostOrToast(): String? {
        val host = PcSettings.getPcIp(this)
        if (host == null || !ConnectionStatus.connected) {
            Toast.makeText(this, R.string.toast_actions_not_connected, Toast.LENGTH_SHORT).show()
            return null
        }
        return host
    }

    private fun send(host: String, type: String, payload: JSONObject) {
        NotificationConnection.init(applicationContext)
        NotificationConnection.send(host, Protocol.buildEnvelope(type, payload))
    }

    /** Every tile reflects the real current state (from the PC's share.status), so starting or
     *  stopping something from the PC or a notification shows up here too. */
    private fun renderActions() {
        val connected = ConnectionStatus.connected
        binding.textActionsHint.setText(if (connected) R.string.hint_actions_connected else R.string.hint_actions_disconnected)
        val apps = ShareState.apps
        styleTile(binding.tileScreen, connected, ShareState.screenSharing,
            getString(if (ShareState.screenSharing) R.string.tile_screen_active else R.string.tile_screen_idle))
        styleTile(binding.tileApp, connected, apps.isNotEmpty(),
            if (apps.isNotEmpty()) getString(R.string.tile_app_active, apps.size) else getString(R.string.tile_app_idle))
        styleTile(binding.tileAudio, connected, ShareState.audioSharing,
            getString(if (ShareState.audioSharing) R.string.tile_audio_active else R.string.tile_audio_idle))
        styleTile(binding.tileMedia, connected, false, getString(R.string.tile_media_idle))
        renderRecents(connected)
        renderActiveApps(apps)
        val anything = ShareState.screenSharing || ShareState.audioSharing || apps.isNotEmpty()
        binding.buttonStopAll.visibility = if (anything && connected) View.VISIBLE else View.GONE
    }

    /** Idle: glass card. Active: the accent gradient (as the PC's primary buttons). */
    private fun styleTile(tile: ItemActionTileBinding, enabled: Boolean, active: Boolean, subtitle: String) {
        val card = tile.root
        val density = resources.displayMetrics.density
        card.isEnabled = enabled
        card.alpha = if (enabled) 1f else 0.45f
        card.radius = 16 * density
        card.cardElevation = 0f
        card.strokeWidth = (1 * density).toInt()
        val inner = card.getChildAt(0)
        if (active) {
            card.setCardBackgroundColor(android.graphics.Color.TRANSPARENT)
            card.strokeColor = android.graphics.Color.TRANSPARENT
            inner.background = AccentTheme.brandGradient(this)
        } else {
            card.setCardBackgroundColor(AccentTheme.surface(this))
            card.strokeColor = AccentTheme.stroke(this)
            inner.background = null
        }
        tile.tileTitle.setTextColor(if (active) android.graphics.Color.WHITE else getThemeTextColor())
        tile.tileSubtitle.setTextColor(if (active) 0xEEFFFFFF.toInt() else AccentTheme.mutedText(this))
        tile.tileSubtitle.text = subtitle
    }

    private fun getThemeTextColor(): Int {
        val tv = android.util.TypedValue()
        theme.resolveAttribute(android.R.attr.textColorPrimary, tv, true)
        return if (tv.resourceId != 0) ContextCompat.getColor(this, tv.resourceId) else tv.data
    }

    /** One row per app currently on the PC: its icon and name, where it is right now, and its
     *  own "Mostrar en la PC" (when it's on the phone) and "Detener". */
    private fun renderActiveApps(apps: List<ShareState.SharedApp>) {
        val container = binding.layoutActiveApps
        container.removeAllViews()
        if (apps.isEmpty() || !ConnectionStatus.connected) { container.visibility = View.GONE; return }
        container.visibility = View.VISIBLE
        val density = resources.displayMetrics.density
        container.addView(TextView(this).apply {
            setText(R.string.section_active_apps)
            textSize = 12f
            setTypeface(typeface, android.graphics.Typeface.BOLD)
            setTextColor(android.graphics.Color.parseColor("#888888"))
        })
        for (app in apps) {
            val row = android.widget.LinearLayout(this).apply {
                orientation = android.widget.LinearLayout.HORIZONTAL
                gravity = android.view.Gravity.CENTER_VERTICAL
                setPadding(0, (8 * density).toInt(), 0, 0)
            }
            row.addView(android.widget.ImageView(this).apply {
                try { setImageDrawable(packageManager.getApplicationIcon(app.packageName)) } catch (e: Exception) { }
                layoutParams = android.widget.LinearLayout.LayoutParams((32 * density).toInt(), (32 * density).toInt()).apply {
                    marginEnd = (12 * density).toInt()
                }
            })
            val texts = android.widget.LinearLayout(this).apply {
                orientation = android.widget.LinearLayout.VERTICAL
                layoutParams = android.widget.LinearLayout.LayoutParams(0, android.widget.LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
            }
            texts.addView(TextView(this).apply { text = app.label; textSize = 14f; setTextColor(getThemeTextColor()) })
            texts.addView(TextView(this).apply {
                setText(if (app.onPhone) R.string.active_app_on_phone else R.string.active_app_on_pc)
                textSize = 11f
                setTextColor(android.graphics.Color.parseColor(if (app.onPhone) "#F57C00" else "#2E7D32"))
            })
            row.addView(texts)
            if (app.onPhone) row.addView(smallAction(getString(R.string.btn_show_on_pc), String.format("#%06X", 0xFFFFFF and AccentTheme.get(this))) {
                connectedHostOrToast()?.let { send(it, "share.app.return", JSONObject().put("packageName", app.packageName)) }
            })
            row.addView(smallAction(getString(R.string.btn_stop_share), "#E53935") {
                connectedHostOrToast()?.let { send(it, "share.stop.app", JSONObject().put("packageName", app.packageName)) }
            })
            container.addView(row)
        }
    }

    private fun smallAction(label: String, color: String, onClick: () -> Unit): TextView {
        val density = resources.displayMetrics.density
        return TextView(this).apply {
            text = label
            textSize = 12f
            setTypeface(typeface, android.graphics.Typeface.BOLD)
            setTextColor(android.graphics.Color.parseColor(color))
            setPadding((10 * density).toInt(), (8 * density).toInt(), (6 * density).toInt(), (8 * density).toInt())
            setOnClickListener { onClick() }
        }
    }

    // ---------------- Conexión con la PC ----------------
    // Every saved PC is listed (PcSettings.savedPcs); exactly one is active and connected. Every
    // button that touches the network first checks a NotiLink PC really answers there
    // (DiscoveryClient.probe / discoverAll) and says what it found in a toast, instead of
    // silently saving an address and leaving the user to guess whether it worked.

    /** "＋ Vincular otra PC / cambiar IP" opened the setup panel even though a PC is saved —
     *  reset on process restart, which is fine, that's an edge case worth re-hiding by default. */
    private var connectionSetupManuallyShown = false

    /** A probe/search is running — the connection buttons are disabled until it answers. */
    private var connectionBusy = false

    private fun setupConnectionSection() {
        binding.buttonScanQr.setOnClickListener {
            IntentIntegrator(this)
                .setPrompt(getString(R.string.btn_scan_qr))
                .setBeepEnabled(false)
                .setOrientationLocked(false) // follow the phone's current orientation instead of forcing landscape
                .initiateScan()
        }
        binding.buttonDiscover.setOnClickListener { findPcs(binding.buttonDiscover) }
        binding.buttonFindPcs.setOnClickListener { findPcs(binding.buttonFindPcs) }
        binding.buttonReconnectNow.setOnClickListener { reconnectWithCheck() }
        binding.buttonAddPc.setOnClickListener {
            connectionSetupManuallyShown = !connectionSetupManuallyShown
            refreshConnectionSetupVisibility()
        }
        binding.buttonConnectIp.setOnClickListener { connectToTypedIp() }
        binding.editPcIp.setOnEditorActionListener { _, _, _ -> connectToTypedIp(); true }
    }

    /** The whole connection card: status line, buttons (enabled only when they can do
     *  something), the other saved PCs, and the setup panel (always shown with no PC saved yet). */
    private fun refreshConnectionSetupVisibility() {
        val active = PcSettings.activePc(this)
        val hasPc = active != null
        binding.layoutConnectionState.visibility = if (hasPc) View.VISIBLE else View.GONE
        binding.layoutConnectionSetup.visibility = if (!hasPc || connectionSetupManuallyShown) View.VISIBLE else View.GONE
        binding.textSetupHint.setText(if (hasPc) R.string.hint_setup_more else R.string.hint_setup_first)
        binding.buttonAddPc.setText(if (connectionSetupManuallyShown) R.string.btn_hide_setup else R.string.btn_add_pc)

        listOf(binding.buttonReconnectNow, binding.buttonFindPcs, binding.buttonConnectIp, binding.buttonScanQr, binding.buttonDiscover).forEach {
            it.isEnabled = !connectionBusy
            it.alpha = if (connectionBusy) 0.5f else 1f
        }
        if (active == null) return

        val connected = ConnectionStatus.connected
        binding.textConnectionState.text = when {
            connected -> getString(R.string.status_connected_to_named, active.name)
            ConnectionStatus.manuallyDisconnected -> getString(R.string.status_manual_named, active.name)
            else -> getString(R.string.status_searching_named, active.name)
        }
        binding.viewConnectionDot.background = android.graphics.drawable.GradientDrawable().apply {
            shape = android.graphics.drawable.GradientDrawable.OVAL
            setColor(when {
                connected -> 0xFF2E7D32.toInt()
                ConnectionStatus.manuallyDisconnected -> 0xFF9E9E9E.toInt()
                else -> 0xFFF57C00.toInt()
            })
        }
        // "192.168.1.20 · red local" / "· Tailscale" (+ "· datos móviles, modo ahorro")
        val ip = PcSettings.getPcIp(this) ?: active.ip
        val route = getString(if (PcSettings.isVpnAddress(ip)) R.string.route_vpn else R.string.route_lan)
        val net = com.notilink.app.net.NetworkMonitor.current
        binding.textConnectionDetail.text = "$ip · $route" + (if (connected && net.metered) " · " + getString(R.string.route_metered) else "")
        renderOtherPcs(active)
    }

    private fun renderOtherPcs(active: PcSettings.SavedPc) {
        val container = binding.layoutOtherPcs
        container.removeAllViews()
        val others = PcSettings.savedPcs(this).filter { it.id != active.id }
        binding.textOtherPcsTitle.visibility = if (others.isEmpty()) View.GONE else View.VISIBLE
        val d = resources.displayMetrics.density
        for (pc in others) {
            val row = android.widget.LinearLayout(this).apply {
                orientation = android.widget.LinearLayout.HORIZONTAL
                gravity = android.view.Gravity.CENTER_VERTICAL
                setPadding(0, (2 * d).toInt(), 0, (2 * d).toInt())
            }
            val texts = android.widget.LinearLayout(this).apply {
                orientation = android.widget.LinearLayout.VERTICAL
                addView(TextView(this@MainActivity).apply { text = pc.name; textSize = 14f; setTypeface(typeface, android.graphics.Typeface.BOLD) })
                addView(TextView(this@MainActivity).apply { text = pc.ip; textSize = 12f; setTextColor(0xFF888888.toInt()) })
            }
            row.addView(texts, android.widget.LinearLayout.LayoutParams(0, android.view.ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            val connect = com.google.android.material.button.MaterialButton(this, null, com.google.android.material.R.attr.materialButtonOutlinedStyle).apply {
                setText(R.string.btn_connect_saved_pc)
                isAllCaps = false
                cornerRadius = (18 * d).toInt()
                isEnabled = !connectionBusy
                setOnClickListener { confirmSwitch(active, pc) { switchTo(null, pc, getString(R.string.toast_switching_pc, pc.name)) } }
            }
            row.addView(connect)
            val remove = TextView(this).apply {
                text = "✕"
                textSize = 16f
                setTextColor(0xFF888888.toInt())
                setPadding((14 * d).toInt(), (10 * d).toInt(), (6 * d).toInt(), (10 * d).toInt())
                contentDescription = getString(R.string.dialog_remove)
                setOnClickListener {
                    androidx.appcompat.app.AlertDialog.Builder(this@MainActivity)
                        .setTitle(getString(R.string.dialog_remove_pc_title, pc.name))
                        .setMessage(R.string.dialog_remove_pc_message)
                        .setPositiveButton(R.string.dialog_remove) { _, _ ->
                            PcSettings.removePc(this@MainActivity, pc.id)
                            Toast.makeText(this@MainActivity, getString(R.string.toast_pc_removed, pc.name), Toast.LENGTH_SHORT).show()
                            refreshConnectionSetupVisibility()
                        }
                        .setNegativeButton(R.string.dialog_cancel, null)
                        .show()
                }
            }
            row.addView(remove)
            container.addView(row)
        }
        AccentTheme.style(this, container)
    }

    /** Runs [work] off the UI thread with the connection buttons disabled and [pressed] showing
     *  "Buscando…"; the UI-thread continuation [work] returns runs once it's done. */
    private fun runConnectionCheck(pressed: android.widget.Button, work: () -> (() -> Unit)) {
        if (connectionBusy) return
        connectionBusy = true
        hideKeyboard()
        val label = pressed.text
        pressed.text = getString(R.string.status_checking)
        refreshConnectionSetupVisibility()
        Thread {
            val then: () -> Unit = try { work() } catch (e: Exception) { {} }
            runOnUiThread {
                connectionBusy = false
                pressed.text = label
                refreshConnectionSetupVisibility()
                if (!isFinishing && !isDestroyed) then()
            }
        }.start()
    }

    /** "Reconectar": only with a saved PC. Looks for it first — at its saved address, its other
     *  known addresses, then the whole network (by its id, never another PC) — saves a new address
     *  if it moved, and says in a toast whether it was found. Connects either way (a PC that isn't
     *  there right now is retried in the background), clearing a manual "Desconectar". */
    private fun reconnectWithCheck() {
        val pc = PcSettings.activePc(this)
        if (pc == null) {
            Toast.makeText(this, R.string.toast_need_pc_first, Toast.LENGTH_LONG).show()
            return
        }
        runConnectionCheck(binding.buttonReconnectNow) {
            val idKnown = !pc.id.startsWith("ip:")
            val direct = (listOf(pc.ip) + pc.addresses.map { it.first }).distinct().firstNotNullOfOrNull { ip ->
                DiscoveryClient.probe(ip)?.takeIf { f -> !idKnown || f.pcId == null || f.pcId == pc.id }
            }
            val found = direct ?: DiscoveryClient.discoverAll(DeviceIdentity.getDeviceName()).let { all ->
                if (idKnown) all.firstOrNull { it.pcId == pc.id } else all.singleOrNull()
            }
            ({
                if (found == null) {
                    Toast.makeText(this, getString(R.string.toast_pc_not_found, pc.name), Toast.LENGTH_LONG).show()
                    startConnection(pc.ip)
                } else {
                    val updated = pc.copy(
                        id = found.pcId ?: pc.id,
                        name = if (found.pcId != null) found.name else pc.name,
                        ip = found.ip,
                    )
                    val message = if (found.ip != pc.ip) getString(R.string.toast_pc_found_new_ip, updated.name, found.ip)
                        else getString(R.string.toast_pc_found_connecting, updated.name, found.ip)
                    switchTo(pc, updated, message)
                }
            })
        }
    }

    /** "Buscar PCs" / "Buscar en la red": every NotiLink PC that answers. One → use it; several →
     *  pick from a list (which says which one is connected / already linked). */
    private fun findPcs(pressed: android.widget.Button) {
        runConnectionCheck(pressed) {
            val all = DiscoveryClient.discoverAll(DeviceIdentity.getDeviceName())
            ({
                when (all.size) {
                    0 -> Toast.makeText(this, R.string.toast_no_pcs_found, Toast.LENGTH_LONG).show()
                    1 -> useFoundPc(all[0])
                    else -> chooseFoundPc(all)
                }
            })
        }
    }

    private fun chooseFoundPc(list: List<DiscoveryClient.FoundPc>) {
        val activeId = PcSettings.activePcId(this)
        val saved = PcSettings.savedPcs(this)
        val labels = list.map { f ->
            val id = f.pcId ?: "ip:${f.ip}"
            val tag = when {
                id == activeId -> " (" + getString(R.string.label_pc_current) + ")"
                saved.any { it.id == id } -> " (" + getString(R.string.label_pc_saved) + ")"
                else -> ""
            }
            "${f.name} — ${f.ip}$tag"
        }.toTypedArray()
        androidx.appcompat.app.AlertDialog.Builder(this)
            .setTitle(R.string.dialog_found_pcs_title)
            .setItems(labels) { _, which -> useFoundPc(list[which]) }
            .setNegativeButton(R.string.dialog_cancel, null)
            .show()
    }

    /** IP typed by hand ("Conectar") — must be a valid IPv4, then it's checked like the QR. */
    private fun connectToTypedIp() {
        val ip = binding.editPcIp.text.toString().trim()
        if (!PcSettings.isValidIpv4(ip)) {
            binding.editPcIp.error = getString(R.string.error_ip_invalid)
            binding.editPcIp.requestFocus()
            return
        }
        binding.editPcIp.error = null
        probeAndUse(ip, binding.buttonConnectIp)
    }

    /** Is a NotiLink PC really at [ip]? Yes → use it (saved, connected, toast). No → offer to
     *  save it anyway (a PC that's simply off right now, a VPN address not reachable yet...). */
    private fun probeAndUse(ip: String, pressed: android.widget.Button) {
        runConnectionCheck(pressed) {
            val found = DiscoveryClient.probe(ip)
            ({
                if (found != null) {
                    useFoundPc(found)
                } else {
                    androidx.appcompat.app.AlertDialog.Builder(this)
                        .setTitle(R.string.dialog_no_answer_title)
                        .setMessage(getString(R.string.dialog_no_answer_message, ip))
                        .setPositiveButton(R.string.dialog_save_anyway) { _, _ -> useFoundPc(DiscoveryClient.FoundPc(ip, "PC ($ip)", null)) }
                        .setNegativeButton(R.string.dialog_cancel, null)
                        .show()
                }
            })
        }
    }

    /** A PC was found (or confirmed). The active one → just store its current address. Another
     *  one while a PC is active → ask first: only one PC is connected at a time. */
    private fun useFoundPc(found: DiscoveryClient.FoundPc) {
        val saved = PcSettings.savedPcs(this)
        val existing = saved.firstOrNull { (found.pcId != null && it.id == found.pcId) || it.id == "ip:${found.ip}" }
        val target = PcSettings.SavedPc(
            id = found.pcId ?: existing?.id ?: "ip:${found.ip}",
            name = if (found.pcId != null || existing == null) found.name else existing.name,
            ip = found.ip,
            addresses = existing?.addresses ?: emptyList(),
        )
        val active = PcSettings.activePc(this)
        val message = getString(R.string.toast_pc_saved, target.name, target.ip)
        if (active == null || active.id == target.id || active.id == existing?.id) switchTo(existing, target, message)
        else confirmSwitch(active, target) { switchTo(existing, target, message) }
    }

    private fun confirmSwitch(from: PcSettings.SavedPc, to: PcSettings.SavedPc, onConfirm: () -> Unit) {
        androidx.appcompat.app.AlertDialog.Builder(this)
            .setTitle(R.string.dialog_switch_title)
            .setMessage(getString(R.string.dialog_switch_message, from.name, to.name))
            .setPositiveButton(R.string.dialog_switch_confirm) { _, _ -> onConfirm() }
            .setNegativeButton(R.string.dialog_cancel, null)
            .show()
    }

    /** Makes [target] the active PC ([old] is the entry it replaces, e.g. an "ip:" placeholder
     *  that now has its real id) and connects to it, dropping whatever PC was connected. */
    private fun hideKeyboard() {
        currentFocus?.clearFocus()
        (getSystemService(INPUT_METHOD_SERVICE) as android.view.inputmethod.InputMethodManager)
            .hideSoftInputFromWindow(binding.root.windowToken, 0)
    }

    private fun switchTo(old: PcSettings.SavedPc?, target: PcSettings.SavedPc, message: String) {
        hideKeyboard()
        if (old != null && old.id != target.id) PcSettings.removePc(this, old.id)
        PcSettings.activate(this, target)
        binding.editPcIp.setText(target.ip)
        connectionSetupManuallyShown = false
        Toast.makeText(this, message, Toast.LENGTH_LONG).show()
        startConnection(target.ip)
    }

    /** Connects now instead of waiting for the next background heartbeat tick (up to 8s away,
     *  and not at all if a manual "Desconectar" is in effect — this clears it, since every caller
     *  is an explicit "I want to be connected"). */
    private fun startConnection(ip: String) {
        ConnectionStatus.setManuallyDisconnected(false)
        NotificationConnection.init(applicationContext)
        NotificationConnection.reconnectTo(ip)
        refreshConnectionSetupVisibility()
    }

    private fun showPairingCode(code: String) {
        binding.layoutPairingCode.visibility = View.VISIBLE
        binding.textPairingCode.text = code
    }

    private fun hidePairingCode() {
        binding.layoutPairingCode.visibility = View.GONE
    }
}
