package com.notilink.app.net

import android.content.ComponentName
import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.media.MediaMetadata
import android.media.session.MediaController
import android.media.session.MediaSession
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.os.Handler
import android.os.Looper
import android.util.Log

/**
 * Watches Android's system-wide media sessions (Spotify, YouTube Music, etc.) via
 * MediaSessionManager — available to this app specifically because it's an enabled
 * NotificationListenerService (a "trusted listener" per Android's own media APIs), no extra
 * permission needed beyond the one NotiLink already requires. Drives the PC's persistent
 * "now playing" bar with real MediaController.TransportControls — actual play/pause/skip,
 * never invented, same philosophy as every other action this app relays.
 *
 * Only ever reports one session at a time: whichever is currently PLAYING, falling back to
 * the first active one otherwise — showing every simultaneously-open media app would be more
 * confusing than useful.
 */
class MediaSessionWatcher(
    private val context: Context,
    private val onStateChanged: (MediaState) -> Unit,
    private val onNoSession: () -> Unit,
    private val onCallStateChanged: (Boolean) -> Unit,
) {
    data class MediaState(
        val packageName: String,
        val appLabel: String,
        val title: String,
        val artist: String,
        val album: String,
        val isPlaying: Boolean,
        val position: Long,
        val duration: Long,
        val canPlayPause: Boolean,
        val canNext: Boolean,
        val canPrevious: Boolean,
        val canFavorite: Boolean,
        val isFavorite: Boolean,
        val canShuffle: Boolean,
        val isShuffleOn: Boolean,
        val isSmartShuffle: Boolean,
        val canSeek: Boolean,
        val albumArt: Bitmap?,
    )

    private companion object {
        const val TAG = "NotiLink/Media"
        const val TELECOM_PACKAGE = "com.android.server.telecom"
    }

    private val manager = context.getSystemService(Context.MEDIA_SESSION_SERVICE) as MediaSessionManager
    private val component = ComponentName(context, "com.notilink.app.listener.NotiLinkNotificationListener")
    private val mainHandler = Handler(Looper.getMainLooper())

    // The one currently shown/controlled from the PC.
    private var activeController: MediaController? = null

    /** For "abrir en el dispositivo" on the PC's now-playing bar — the media app's own real
     *  "now playing" notification (excluded from normal mirroring, see EXTRA_MEDIA_SESSION
     *  filtering in NotiLinkNotificationListener, but still posted and still in
     *  activeNotifications) is what NotiLinkAccessibilityService actually opens; this is just
     *  which package to look for it under. */
    fun activePackageName(): String? = activeController?.packageName

    // Every currently-known session gets its own callback registered — not just the active
    // one. Confirmed bug: with only the active controller watched, starting playback in a
    // second app (e.g. Spotify) while a first one (e.g. YouTube) was the displayed session never
    // updated the bar, because nothing was listening to Spotify's own state change — only a
    // session being created/destroyed (OnActiveSessionsChangedListener) or the currently-active
    // controller's own callback could trigger a re-pick, and neither fires just because an
    // already-open, already-listed session starts playing. Watching every candidate's callback
    // means ANY of them changing state re-evaluates who should actually be shown.
    private val watchedControllers = mutableMapOf<MediaSession.Token, MediaController>()
    private val watchedCallbacks = mutableMapOf<MediaSession.Token, MediaController.Callback>()

    private val sessionsListener = MediaSessionManager.OnActiveSessionsChangedListener { controllers ->
        pickController(controllers ?: emptyList())
    }

    // Confirmed the real cause of a flood: onListenerConnected() can fire more than once
    // without a matching onListenerDisconnected() in between (observed directly — two
    // back-to-back "listener connected" log lines within the same millisecond during a
    // reconnect). Without this guard, start() would register the same sessionsListener with
    // MediaSessionManager a second time, so every real session-list change fired pickController
    // twice — and since it unconditionally republishes even when reselecting the same session,
    // that's how "nothing changed" turned into dozens of duplicate media.state + album-art sends
    // per second (confirmed: 56 identical log lines in a 2-second window for one unchanged
    // Spotify track), almost certainly also what was starving the connection queue and delaying
    // real notifications after a reconnect.
    private var isStarted = false

    fun start() {
        if (isStarted) return
        try {
            manager.addOnActiveSessionsChangedListener(sessionsListener, component)
            isStarted = true
            pickController(manager.getActiveSessions(component))
        } catch (e: SecurityException) {
            DiagLog.w(TAG, "no media session access yet: ${e.message}")
        }
    }

    fun stop() {
        isStarted = false
        try {
            manager.removeOnActiveSessionsChangedListener(sessionsListener)
        } catch (e: Exception) {
            // Never registered / already gone — nothing to clean up.
        }
        mainHandler.removeCallbacksAndMessages(null) // cancels the debounce + any pending art retries
        detachAllCallbacks()
        activeController = null
        callActive = false
    }

    /** Called for a PC-initiated play/pause/next/previous/favorite/shuffle/seek — fires the
     *  session's own real TransportControls, exactly what the phone's lock-screen media controls
     *  would do. "favorite"/"shuffle" have no dedicated TransportControls method (there isn't a
     *  standard platform action for either) — they go through sendCustomAction with whatever
     *  action id the session itself last advertised, see favoriteActionId/shuffleActionId below.
     *  "seek" is the one real TransportControls method that needs an argument — [position] is
     *  ignored for every other action. */
    fun control(action: String, position: Long? = null) {
        val controller = activeController ?: return
        val controls = controller.transportControls
        when (action) {
            "playPause" -> {
                val playing = controller.playbackState?.state == PlaybackState.STATE_PLAYING
                if (playing) controls.pause() else controls.play()
            }
            "next" -> controls.skipToNext()
            "previous" -> controls.skipToPrevious()
            "favorite" -> favoriteActionId?.let { controls.sendCustomAction(it, null) }
            "shuffle" -> shuffleActionId?.let { controls.sendCustomAction(it, null) }
            "seek" -> position?.let {
                controls.seekTo(it)
                // position is deliberately excluded from the fingerprint dedup guard (every
                // playing track would otherwise force a republish on every single tick) — which
                // meant a seek's own resulting onPlaybackStateChanged callback was being silently
                // swallowed as "nothing changed", leaving the PC frozen on the pre-seek position
                // until some unrelated field (e.g. play/pause) finally forced a fresh publish
                // through. Invalidating it here lets that same real callback reach the PC.
                lastPublishedFingerprint = null
            }
        }
    }

    /** A real track is loaded — the signal that separates an actually-in-use session from a
     *  pre-installed app's dormant media-session stub (confirmed: a phone with Amazon Music
     *  never installed still had it show up here — some OEMs ship a lightweight package that
     *  registers a MediaSession purely for future radio/casting hooks, and it can sit reporting
     *  a stale STATE_PLAYING with nothing actually loaded, indefinitely). */
    private fun hasRealTrack(controller: MediaController): Boolean =
        !controller.metadata?.getString(MediaMetadata.METADATA_KEY_TITLE).isNullOrBlank()

    private fun pickController(controllers: List<MediaController>) {
        // Logged unconditionally (not just on selection) so a session that's visible here but
        // never gets picked — e.g. reports STATE_PLAYING with no track title, or isn't in this
        // list at all despite the app visibly playing audio, which would mean the app isn't
        // registering a MediaSession the normal way — is diagnosable from a real log instead of
        // guessed at blind.
        DiagLog.i(TAG, "pickController: candidates=${controllers.joinToString {
            "${it.packageName}(state=${it.playbackState?.state},title=${it.metadata?.getString(MediaMetadata.METADATA_KEY_TITLE)})"
        }}")

        checkCallActive(controllers)
        syncWatchedControllers(controllers)

        // A ghost session's stuck STATE_PLAYING must never outrank a real one just by being
        // first in the list — requiring a real track alongside "playing" is what actually fixed
        // Amazon Music's dormant stub permanently winning over Spotify while Spotify played.
        // If literally nothing has a real track, there's genuinely nothing to show (falling
        // back to "just pick anything" used to surface that same trackless/action-less stub as
        // the last-resort choice instead of hiding the bar).
        val next = controllers.firstOrNull { it.playbackState?.state == PlaybackState.STATE_PLAYING && hasRealTrack(it) }
            ?: controllers.firstOrNull { hasRealTrack(it) }

        activeController = next
        schedulePublish(next)
    }

    // Confirmed: a single play/pause/skip tap makes Spotify fire several callbacks back to
    // back (onPlaybackStateChanged and onMetadataChanged rarely land in the same instant), each
    // separately triggering a pickController pass — and at least one of those intermediate
    // passes could momentarily see a stale/inconsistent snapshot (matching what was reported:
    // the cover flashing to blank on pause, or briefly showing the previous track's art on
    // skip). Debouncing so only the LAST pass within a short quiet window actually publishes
    // means those in-between snapshots never reach the PC at all — only the settled state does,
    // once, per action.
    private val PUBLISH_DEBOUNCE_MS = 200L
    private var pendingNext: MediaController? = null
    private val publishRunnable = Runnable {
        val next = pendingNext
        if (next == null) {
            lastPublishedFingerprint = null
            onNoSession()
        } else {
            publishState(next)
        }
    }

    private fun schedulePublish(next: MediaController?) {
        pendingNext = next
        mainHandler.removeCallbacks(publishRunnable)
        mainHandler.postDelayed(publishRunnable, PUBLISH_DEBOUNCE_MS)
    }

    /** Keeps exactly one registered callback per currently-active session — added for any new
     *  controller, removed for any that's gone — so a re-pick fires the instant ANY of them
     *  changes state, not just the currently-displayed one. */
    private fun syncWatchedControllers(controllers: List<MediaController>) {
        val currentTokens = controllers.map { it.sessionToken }.toSet()

        val goneTokens = watchedCallbacks.keys.filter { it !in currentTokens }
        for (token in goneTokens) {
            val callback = watchedCallbacks.remove(token)
            val controller = watchedControllers.remove(token)
            if (callback != null && controller != null) {
                try { controller.unregisterCallback(callback) } catch (e: Exception) { /* already gone */ }
            }
        }

        for (controller in controllers) {
            if (watchedCallbacks.containsKey(controller.sessionToken)) continue
            val callback = object : MediaController.Callback() {
                override fun onPlaybackStateChanged(state: PlaybackState?) = repick()
                override fun onMetadataChanged(metadata: MediaMetadata?) = repick()
                override fun onSessionDestroyed() {
                    watchedCallbacks.remove(controller.sessionToken)
                    watchedControllers.remove(controller.sessionToken)
                    repick()
                }
            }
            watchedControllers[controller.sessionToken] = controller
            watchedCallbacks[controller.sessionToken] = callback
            try {
                controller.registerCallback(callback, mainHandler)
            } catch (e: Exception) {
                DiagLog.w(TAG, "registerCallback failed for ${controller.packageName}: ${e.message}")
            }
        }
    }

    private fun repick() =
        pickController(try { manager.getActiveSessions(component) } catch (e: SecurityException) { emptyList() })

    // Android's own Telecom framework registers this session for as long as ANY call — native
    // SIM or a VoIP app's self-managed ConnectionService (WhatsApp, Telegram, etc.) — is
    // ringing or in progress, confirmed directly from a real WhatsApp call in DiagLog: it
    // appeared in the candidate list for the exact duration of the call and nowhere else, while
    // WhatsApp itself never posted any notification while ringing at all (only a missed-call one
    // afterward). No richer state (direction, ringing vs. answered) is exposed through this
    // MediaController-shaped view — title/playbackState are always null — so this can only ever
    // signal "a call is happening", not who's calling or whether it's incoming or outgoing.
    // Fires on both edges so the PC's call banner can come down again when the call actually
    // ends, not just appear once and never clear.
    private var callActive = false

    private fun checkCallActive(controllers: List<MediaController>) {
        val present = controllers.any { it.packageName == TELECOM_PACKAGE }
        if (present != callActive) onCallStateChanged(present)
        callActive = present
    }

    private fun detachAllCallbacks() {
        for ((token, callback) in watchedCallbacks) {
            watchedControllers[token]?.let { controller ->
                try { controller.unregisterCallback(callback) } catch (e: Exception) { /* already gone */ }
            }
        }
        watchedCallbacks.clear()
        watchedControllers.clear()
    }

    // Second, independent layer of protection against duplicate sends — belt-and-suspenders
    // alongside the isStarted guard above, since MediaController.Callback can genuinely fire
    // more than once for what amounts to the same state even with a single registration (e.g.
    // some apps re-announce identical metadata on their own). Position is deliberately excluded:
    // the PC doesn't render a live progress bar, so a position-only tick isn't a "real" change
    // worth a fresh network send (let alone one that re-transmits the album art image again).
    private var lastPublishedFingerprint: String? = null

    // Custom actions are how modern media apps expose a favorite/save toggle at the session
    // level — there's no standard platform-wide "toggle favorite" TransportControls method, so
    // this is the real API media apps use for it. This is genuinely session data, unlike the
    // heart button that used to only be visible via the notification's own action buttons,
    // which stopped reaching this app once media-style notifications were excluded from the
    // normal mirrored-card pipeline (see EXTRA_MEDIA_SESSION filtering in
    // NotiLinkNotificationListener). Detected by matching the action's own human-readable name
    // against favorite-ish keywords, in both languages this app is used in — there's no
    // standardized action-id constant to match against instead, every app picks its own string.
    // Whether it's CURRENTLY favorited is inferred the same way: apps typically phrase the
    // action as "remove from..." once it's already saved, "add to.../like" otherwise. This is a
    // best-effort heuristic pending a live confirmation of the exact wording real apps use —
    // logged below specifically so it can be verified/tuned against a real device.
    private val FAVORITE_ACTION_REGEX = Regex("favorit|me gusta|like|guardar|save|coraz[oó]n|heart|colecci[oó]n|library", RegexOption.IGNORE_CASE)
    private val ALREADY_FAVORITED_REGEX = Regex("remove|quitar|eliminar|ya no|unlike|unsave", RegexOption.IGNORE_CASE)
    private var favoriteActionId: String? = null

    private fun findFavoriteCustomAction(playbackState: PlaybackState?): PlaybackState.CustomAction? =
        playbackState?.customActions?.firstOrNull { FAVORITE_ACTION_REGEX.containsMatchIn(it.name?.toString() ?: "") }

    // Same custom-action mechanism as favorite — confirmed directly from a real device log:
    // Spotify exposes shuffle as a custom action with id "TURN_SHUFFLE_OFF"/"TURN_SHUFFLE_ON"
    // and a STATIC name ("Activar/desactivar modo aleatorio") that doesn't change with state.
    // That's the opposite of favorite's convention (static id, name that changes) — so unlike
    // isFavorite, isShuffleOn has to read the current state from the action ID itself: which
    // direction it offers to toggle IS the current state (an action offering to turn shuffle OFF
    // means it's currently on). android.media.session.PlaybackState has no dedicated shuffle-mode
    // API on the raw platform classes (only the AndroidX compat wrapper does, which this project
    // doesn't depend on) — this custom-action route is the real, dependency-free way to reach it.
    private val SHUFFLE_ACTION_REGEX = Regex("shuffle|aleatorio", RegexOption.IGNORE_CASE)
    private val SHUFFLE_CURRENTLY_ON_REGEX = Regex("off", RegexOption.IGNORE_CASE)
    private var shuffleActionId: String? = null

    private fun findShuffleCustomAction(playbackState: PlaybackState?): PlaybackState.CustomAction? =
        playbackState?.customActions?.firstOrNull {
            SHUFFLE_ACTION_REGEX.containsMatchIn(it.action) || SHUFFLE_ACTION_REGEX.containsMatchIn(it.name?.toString() ?: "")
        }

    private fun publishState(controller: MediaController) {
        val metadata = controller.metadata
        val playbackState = controller.playbackState
        val isPlaying = playbackState?.state == PlaybackState.STATE_PLAYING
        val actions = playbackState?.actions ?: 0L

        val appLabel = try {
            val pm = context.packageManager
            pm.getApplicationLabel(pm.getApplicationInfo(controller.packageName, 0)).toString()
        } catch (e: Exception) {
            controller.packageName
        }

        val albumArt = resolveAlbumArt(metadata)

        val favoriteAction = findFavoriteCustomAction(playbackState)
        favoriteActionId = favoriteAction?.action
        val isFavorite = favoriteAction != null && ALREADY_FAVORITED_REGEX.containsMatchIn(favoriteAction.name?.toString() ?: "")

        val shuffleAction = findShuffleCustomAction(playbackState)
        shuffleActionId = shuffleAction?.action
        val isShuffleOn = shuffleAction != null && SHUFFLE_CURRENTLY_ON_REGEX.containsMatchIn(shuffleAction.action)
        // Confirmed from a real device log: Spotify's Smart Shuffle is the SAME custom action
        // slot as regular shuffle, never both at once — only the action id gains a "SMART_"
        // infix (TURN_SMART_SHUFFLE_OFF vs TURN_SHUFFLE_OFF), the human-readable name stays
        // identically "Activar/desactivar modo aleatorio" either way, so this can only be told
        // apart from the id, never the name (the opposite of how isFavorite/isShuffleOn work).
        val isSmartShuffle = shuffleAction?.action?.contains("SMART", ignoreCase = true) == true

        val canSeek = (actions and PlaybackState.ACTION_SEEK_TO) != 0L

        val fingerprint = "${controller.packageName}|$isPlaying|${metadata?.getString(MediaMetadata.METADATA_KEY_TITLE)}|" +
            "${metadata?.getString(MediaMetadata.METADATA_KEY_ARTIST)}|${albumArt != null}|$actions|${favoriteAction?.action}|$isFavorite|" +
            "${shuffleAction?.action}|$isShuffleOn"
        if (fingerprint == lastPublishedFingerprint) return
        lastPublishedFingerprint = fingerprint

        DiagLog.i(TAG, "publishState: pkg=${controller.packageName} title=${metadata?.getString(MediaMetadata.METADATA_KEY_TITLE)} " +
            "isPlaying=$isPlaying hasArt=${albumArt != null} actions=$actions canSeek=$canSeek " +
            "artUri=${metadata?.getString(MediaMetadata.METADATA_KEY_ALBUM_ART_URI)} " +
            "favoriteAction=${favoriteAction?.action} favoriteName=${favoriteAction?.name} isFavorite=$isFavorite " +
            "shuffleAction=${shuffleAction?.action} isShuffleOn=$isShuffleOn isSmartShuffle=$isSmartShuffle " +
            "allCustomActions=${playbackState?.customActions?.joinToString { "${it.action}:${it.name}" }}")

        onStateChanged(
            MediaState(
                packageName = controller.packageName,
                appLabel = appLabel,
                title = metadata?.getString(MediaMetadata.METADATA_KEY_TITLE) ?: "",
                artist = metadata?.getString(MediaMetadata.METADATA_KEY_ARTIST) ?: "",
                album = metadata?.getString(MediaMetadata.METADATA_KEY_ALBUM) ?: "",
                isPlaying = isPlaying,
                position = playbackState?.position ?: 0L,
                duration = metadata?.getLong(MediaMetadata.METADATA_KEY_DURATION) ?: 0L,
                canPlayPause = (actions and (PlaybackState.ACTION_PLAY_PAUSE or PlaybackState.ACTION_PLAY or PlaybackState.ACTION_PAUSE)) != 0L,
                canNext = (actions and PlaybackState.ACTION_SKIP_TO_NEXT) != 0L,
                canPrevious = (actions and PlaybackState.ACTION_SKIP_TO_PREVIOUS) != 0L,
                canFavorite = favoriteAction != null,
                isFavorite = isFavorite,
                canShuffle = shuffleAction != null,
                isShuffleOn = isShuffleOn,
                isSmartShuffle = isSmartShuffle,
                canSeek = canSeek,
                albumArt = albumArt,
            )
        )
    }

    // Confirmed intermittent: resolveArtUri hits Spotify's own content provider over IPC, which
    // publishState was redoing on every single onPlaybackStateChanged — including a bare
    // play/pause toggle that doesn't change the track at all. Under rapid taps that cross-process
    // call occasionally came back empty/failed (no fixed pattern, consistent with a provider
    // that's momentarily busy), and with nothing cached, that transient failure was published
    // as "no art" — which the PC then displayed as a blank/placeholder cover for a track it had
    // just shown real art for a second earlier. Caching by URI means the flaky call only ever
    // happens once per track; every play/pause toggle after that reuses the same decoded bitmap
    // instead of re-risking it. A resolution failure now simply keeps showing the last bitmap
    // decoded for THIS uri rather than flipping to no-art — a currently-unresolvable URI is far
    // more likely to be transient IPC flakiness than the art having genuinely vanished.
    private var lastArtUri: String? = null
    private var lastArtBitmap: Bitmap? = null

    private fun resolveAlbumArt(metadata: MediaMetadata?): Bitmap? {
        val embedded = metadata?.getBitmap(MediaMetadata.METADATA_KEY_ALBUM_ART)
            ?: metadata?.getBitmap(MediaMetadata.METADATA_KEY_ART)
            ?: metadata?.getBitmap(MediaMetadata.METADATA_KEY_DISPLAY_ICON)
        if (embedded != null) return embedded
        return resolveAlbumArtByUri(metadata)
    }

    private fun currentArtUri(): String? {
        val metadata = activeController?.metadata
        return metadata?.getString(MediaMetadata.METADATA_KEY_ALBUM_ART_URI)
            ?: metadata?.getString(MediaMetadata.METADATA_KEY_ART_URI)
            ?: metadata?.getString(MediaMetadata.METADATA_KEY_DISPLAY_ICON_URI)
    }

    private fun resolveAlbumArtByUri(metadata: MediaMetadata?): Bitmap? {
        val uri = metadata?.getString(MediaMetadata.METADATA_KEY_ALBUM_ART_URI)
            ?: metadata?.getString(MediaMetadata.METADATA_KEY_ART_URI)
            ?: metadata?.getString(MediaMetadata.METADATA_KEY_DISPLAY_ICON_URI)
            ?: return null

        if (uri == lastArtUri && lastArtBitmap != null) return lastArtBitmap

        val resolved = resolveArtUri(uri)
        if (resolved != null) {
            lastArtUri = uri
            lastArtBitmap = resolved
        } else if (uri == lastArtUri) {
            // Same track, this particular attempt failed — keep serving the bitmap already
            // decoded for it rather than reporting no art.
            return lastArtBitmap
        } else {
            // A genuinely new track's very first resolution attempt failing (confirmed pattern:
            // fails right on a track change, then succeeds on the next unrelated state change
            // like pause/previous) — Spotify's content provider likely hasn't finished making
            // the image available at the exact instant the new metadata (and its URI) first
            // arrives, a startup race rather than a real absence. Retrying shortly after, instead
            // of accepting the first failure as final, is what actually fixes the flash of
            // no-art on every track change instead of just papering over the toggle case.
            scheduleArtRetry(uri)
        }
        return resolved
    }

    private fun scheduleArtRetry(uri: String, attempt: Int = 1) {
        if (attempt > 4) return
        mainHandler.postDelayed({
            // The track may have already changed again by the time this fires — only act if
            // this uri is still the one actually relevant, so a stale retry can never clobber
            // whatever's genuinely playing now.
            if (currentArtUri() != uri) return@postDelayed
            val resolved = resolveArtUri(uri)
            if (resolved != null) {
                lastArtUri = uri
                lastArtBitmap = resolved
                // Force a fresh publish now that art for the current track finally resolved —
                // fingerprint reset so the "nothing changed" dedup guard doesn't swallow it.
                lastPublishedFingerprint = null
                activeController?.let { publishState(it) }
            } else {
                scheduleArtRetry(uri, attempt + 1)
            }
        }, attempt * 400L)
    }

    /** Only content:// is resolved, never http(s) — an outbound network fetch would break this
     *  app's LAN-only, no-cloud premise for a feature that's supposed to be a convenience, not
     *  a new data path. Trusted listener apps like this one get an implicit read grant for a
     *  session's own art URI, same mechanism as the per-notification icon URI grants. */
    private fun resolveArtUri(uriString: String?): Bitmap? {
        if (uriString.isNullOrBlank()) return null
        val uri = android.net.Uri.parse(uriString)
        if (uri.scheme != "content") return null
        return try {
            context.contentResolver.openInputStream(uri)?.use { BitmapFactory.decodeStream(it) }
        } catch (e: Exception) {
            DiagLog.w(TAG, "resolveArtUri failed for $uriString: ${e.message}")
            null
        }
    }
}
