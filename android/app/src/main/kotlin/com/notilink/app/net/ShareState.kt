package com.notilink.app.net

/**
 * What the PC is currently transmitting from this phone — mirrored from every `share.status`
 * the PC sends (see NotiLinkNotificationListener.handleShareStatus). The app's "Acciones"
 * screen reads it so each button shows its real state (and toggles it), whether the share was
 * started from the phone, from the PC, or from a notification.
 */
object ShareState {
    data class SharedApp(val packageName: String, val label: String, val onPhone: Boolean)

    @Volatile var screenSharing = false
        private set
    @Volatile var audioSharing = false
        private set
    @Volatile var apps: List<SharedApp> = emptyList()
        private set

    private val listeners = mutableListOf<() -> Unit>()

    fun addListener(l: () -> Unit) = synchronized(listeners) { listeners.add(l) }
    fun removeListener(l: () -> Unit) = synchronized(listeners) { listeners.remove(l) }

    fun update(screen: Boolean, audio: Boolean, sharedApps: List<SharedApp>) {
        screenSharing = screen
        audioSharing = audio
        apps = sharedApps
        val snapshot = synchronized(listeners) { listeners.toList() }
        snapshot.forEach { it() }
    }

    fun clear() = update(false, false, emptyList())
}
