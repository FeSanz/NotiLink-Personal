package com.notilink.app.listener

import android.accessibilityservice.AccessibilityService
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo
import android.widget.Toast
import com.notilink.app.net.DiagLog

/**
 * Opens the exact notification a PC user clicked "Abrir" on — the real tap-through behavior a
 * touch on the phone would trigger, not a generic app launch (explicitly requested: "no quiero
 * que lances apps, solo quiero que puedas abrir la notificación"). Firing the notification's own
 * PendingIntent directly (Notification.contentIntent.send()) is confirmed blocked by Android's
 * background-activity-start restrictions — a third-party app calling .send() on someone else's
 * PendingIntent from a background service fails Android's two-sided check regardless of any
 * exemption this app itself holds (see CLAUDE.md's "abrir la notificación" section for the full
 * investigation). This sidesteps that entirely by doing what Microsoft's Phone Link is believed
 * to do (confirmed only to use an AccessibilityService at all, per Microsoft's own support docs —
 * the exact mechanism below is this app's own best reconstruction, not decompiled from theirs):
 * simulate the real user interaction — open the shade, find the matching notification, click it
 * — so it's System UI's own already-privileged context that ends up starting the activity, not
 * this app's.
 *
 * A real, separately-granted permission (Settings > Accessibility), far more sensitive than
 * Notification Access — the user opted in with that understood, see MainActivity's onboarding
 * row and its subtitle text.
 */
class NotiLinkAccessibilityService : AccessibilityService() {

    companion object {
        private const val TAG = "NotiLink/Accessibility"

        @Volatile private var instance: NotiLinkAccessibilityService? = null

        fun isAvailable(): Boolean = instance != null

        /** [candidates] are strings the caller already knows about the notification (text,
         *  title, the last individual message's own text for a conversation, in whatever order
         *  they should be tried) — the most specific/unique-per-notification one should come
         *  first, since the title alone repeats across every message in the same conversation
         *  (e.g. every WhatsApp message from the same contact shares a title).
         *  [onResult] always fires exactly once — success, or failure with a reason code the PC
         *  can turn into a real message ("accessibility_not_granted" | "not_found"). */
        fun openNotification(candidates: List<String>, onResult: (success: Boolean, reason: String?) -> Unit) {
            val service = instance
            if (service == null) {
                DiagLog.w(TAG, "openNotification: service not running — accessibility permission not granted?")
                onResult(false, "accessibility_not_granted")
                return
            }
            service.performOpen(candidates.filter { it.isNotBlank() }.distinct(), onResult)
        }

        /** Presses the Answer or End/Decline button on whatever call UI is currently on screen
         *  (the phone's own dialer, WhatsApp's call screen, ...), by matching the button's visible
         *  text or content description — same tree-walk idea as openNotification. [kind] is
         *  "answer" | "end". [onResult] fires exactly once. */
        fun performCallControl(kind: String, onResult: (success: Boolean) -> Unit) {
            val service = instance
            if (service == null) { onResult(false); return }
            service.mainHandler.post { service.attemptCallControl(kind, 1, onResult) }
        }

        // Short labels only: the real buttons are "Contestar"/"Colgar"; longer strings such as
        // "Responder con mensaje" (a different button entirely) must never match.
        private const val CALL_LABEL_MAX_LENGTH = 24
        private val ANSWER_LABEL = Regex("\\b(contestar|responder|aceptar|answer|accept)\\b", RegexOption.IGNORE_CASE)
        private val END_LABEL = Regex("\\b(rechazar|colgar|finalizar|terminar|decline|reject|end call|hang up|end)\\b", RegexOption.IGNORE_CASE)
        // In-call tool buttons. Their labels are longer/vary more per app ("Altavoz, desactivado"),
        // so they get a more generous length cap than Answer/End (see CALL_TOOL_LABEL_MAX_LENGTH).
        private val SPEAKER_LABEL = Regex("altavoz|speaker|bocina|manos libres", RegexOption.IGNORE_CASE)
        private val MUTE_LABEL = Regex("silenciar|silenciado|\\bmute|unmute|activar sonido|micr[oó]fono|microphone", RegexOption.IGNORE_CASE)
        private val HOLD_LABEL = Regex("retener|en espera|\\bhold\\b", RegexOption.IGNORE_CASE)
        private const val CALL_TOOL_LABEL_MAX_LENGTH = 40

        // A call screen's own running timer: "00:07" / "1:02:33", as the ENTIRE text of one node.
        // Only appears once the call is actually connected, which is what makes it a reliable
        // "the other side answered" signal for any dialer or VoIP app without needing the dialer's
        // own API. Never matched inside the status bar / notification shade (their clock is also
        // "17:56"-shaped) — see findCallTimerSeconds.
        private val CALL_TIMER = Regex("^(?:(\\d{1,2}):)?(\\d{1,2}):(\\d{2})$")

        /** Looks for the on-screen call timer and reports its current value in seconds, or null if
         *  none is visible (call not connected yet, or accessibility not running). */
        fun scanCallTimer(onResult: (seconds: Int?) -> Unit) {
            val service = instance
            if (service == null) { onResult(null); return }
            service.mainHandler.post { onResult(service.findCallTimerSeconds()) }
        }

        // Android inserts bidi control characters (LRM/RLM, embedding/override/isolate marks)
        // and sometimes zero-width ones into notification text for correct mixed-direction
        // rendering — confirmed directly from a real device log where a WhatsApp match failed
        // despite the visible text looking identical: the stored candidate string carried a
        // U+200E (LRM) the shade's own accessibility node text didn't have at the same spot (or
        // vice versa). Stripping these from both sides before comparing, plus collapsing
        // whitespace, is what makes the match robust to that instead of requiring a
        // character-for-character coincidence.
        private val INVISIBLE_CHARS_REGEX = Regex("[​-‏‪-‮⁦-⁩﻿]")
        private val WHITESPACE_REGEX = Regex("\\s+")

        fun normalize(text: String): String =
            text.replace(INVISIBLE_CHARS_REGEX, "").trim().replace(WHITESPACE_REGEX, " ")
    }

    private val mainHandler = Handler(Looper.getMainLooper())

    override fun onServiceConnected() {
        super.onServiceConnected()
        instance = this
        DiagLog.i(TAG, "accessibility service connected")
    }

    override fun onDestroy() {
        super.onDestroy()
        if (instance === this) instance = null
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        // Nothing to react to continuously — this service is purely a one-shot action performer,
        // driven by openNotification() calls, not by watching accessibility events.
    }

    override fun onInterrupt() {}

    // openNotification() is called from NotiLinkNotificationListener's socket-read thread (see
    // NotificationConnection.startReadLoop), not the main thread — Toast.show() and
    // performGlobalAction both need to run on a thread with a prepared Looper, so the whole
    // thing is posted onto mainHandler right away rather than assuming the caller's thread.
    // Confirmed the hard way: without this, Toast.makeText().show() throws immediately (no
    // Looper on the read thread), the whole function aborts before ever reaching
    // performGlobalAction, and NotificationConnection's own catch-and-log around onCommand()
    // swallows it silently — "abrir" looked like it stopped doing anything at all.
    private fun performOpen(candidates: List<String>, onResult: (Boolean, String?) -> Unit) {
        mainHandler.post { performOpenOnMainThread(candidates, onResult) }
    }

    private fun performOpenOnMainThread(candidates: List<String>, onResult: (Boolean, String?) -> Unit) {
        if (candidates.isEmpty()) {
            onResult(false, "not_found")
            return
        }
        Toast.makeText(applicationContext, "Abriendo notificación desde la PC…", Toast.LENGTH_SHORT).show()

        // A locked/off screen means GLOBAL_ACTION_NOTIFICATIONS and the tree walk below have
        // nothing to act on — waking it first (real screen-power-on, same as a physical button
        // press, nothing to do with starting an Activity so none of the BAL restrictions above
        // apply here) lets the rest of the normal flow proceed: if the phone has a lock screen,
        // Android's own tap-a-locked-notification handling prompts for it and completes the
        // navigation once satisfied — nothing extra needed from this app for that part.
        val powerManager = getSystemService(POWER_SERVICE) as? PowerManager
        val wasOff = powerManager?.isInteractive == false
        if (wasOff) wakeScreen(powerManager)

        performGlobalAction(GLOBAL_ACTION_NOTIFICATIONS)

        // A freshly woken, possibly-locked screen needs far more time (room for the user to
        // actually enter a PIN/pattern) than an already-unlocked one just needs for the shade's
        // own open animation to finish.
        val maxAttempts = if (wasOff) 30 else 6
        val intervalMs = if (wasOff) 500L else 200L
        attemptClick(candidates, attempt = 1, maxAttempts, intervalMs, onResult)
    }

    @Suppress("DEPRECATION")
    private fun wakeScreen(powerManager: PowerManager?) {
        try {
            val wakeLock = powerManager?.newWakeLock(
                PowerManager.SCREEN_BRIGHT_WAKE_LOCK or PowerManager.ACQUIRE_CAUSES_WAKEUP or PowerManager.ON_AFTER_RELEASE,
                "NotiLink:OpenNotificationWake"
            )
            // Auto-releases on its own after this timeout regardless — never left held
            // indefinitely even if something below goes wrong.
            wakeLock?.acquire(10_000L)
        } catch (e: Exception) {
            DiagLog.w(TAG, "wakeScreen failed: ${e.message}")
        }
    }

    /** The shade's window isn't guaranteed to be laid out the instant performGlobalAction
     *  returns — a short delay with a few retries is simpler and more robust across OEM shade
     *  implementations than trying to listen for the exact right window-shown event. */
    private fun attemptClick(candidates: List<String>, attempt: Int, maxAttempts: Int, intervalMs: Long, onResult: (Boolean, String?) -> Unit) {
        mainHandler.postDelayed({
            val matched = candidates.firstOrNull { clickMatchingNode(it) }
            if (matched != null) {
                DiagLog.i(TAG, "openNotification: clicked match for \"$matched\" on attempt $attempt")
                onResult(true, null)
            } else if (attempt < maxAttempts) {
                attemptClick(candidates, attempt + 1, maxAttempts, intervalMs, onResult)
            } else {
                DiagLog.w(TAG, "openNotification: no match found for $candidates after $attempt attempts")
                onResult(false, "not_found")
            }
        }, intervalMs)
    }

    /** Walks every currently visible window's accessibility tree (not just the shade's — its
     *  exact package/type varies by OEM, so this doesn't try to filter to a specific one) looking
     *  for a node whose text contains [matchText], then clicks the nearest clickable ancestor —
     *  notification rows are typically one clickable container with several non-clickable text
     *  children (title, body, timestamp), so the text match itself is rarely directly clickable. */
    private fun clickMatchingNode(matchText: String): Boolean {
        val normalizedMatch = normalize(matchText)
        if (normalizedMatch.isEmpty()) return false
        for (window in windows) {
            val root = window.root ?: continue
            val found = findNodeContainingText(root, normalizedMatch) ?: continue
            val clickable = nearestClickableAncestor(found) ?: continue
            return clickable.performAction(AccessibilityNodeInfo.ACTION_CLICK)
        }
        return false
    }

    /** [normalizedText] is already run through normalize() by the caller — done once per
     *  candidate rather than re-normalizing on every node visited. */
    private fun findNodeContainingText(node: AccessibilityNodeInfo, normalizedText: String): AccessibilityNodeInfo? {
        val nodeText = node.text?.toString()
        if (!nodeText.isNullOrBlank() && normalize(nodeText).contains(normalizedText, ignoreCase = true)) return node
        for (i in 0 until node.childCount) {
            val child = node.getChild(i) ?: continue
            val match = findNodeContainingText(child, normalizedText)
            if (match != null) return match
        }
        return null
    }

    private fun swipeUp(x: Float, y: Float) {
        val path = android.graphics.Path().apply {
            moveTo(x, y)
            lineTo(x, (y - 700f).coerceAtLeast(40f))
        }
        val gesture = android.accessibilityservice.GestureDescription.Builder()
            .addStroke(android.accessibilityservice.GestureDescription.StrokeDescription(path, 0, 400))
            .build()
        val dispatched = dispatchGesture(gesture, null, null)
        DiagLog.i(TAG, "callControl: swipe-up gesture from ($x,$y) dispatched=$dispatched")
    }

    private fun findCallTimerSeconds(): Int? {
        for (window in windows) {
            val root = window.root ?: continue
            val pkg = root.packageName?.toString()
            if (pkg == "com.android.systemui" || pkg == packageName) continue
            findTimerNode(root, 0)?.let { return it }
        }
        return null
    }

    private fun findTimerNode(node: AccessibilityNodeInfo, depth: Int): Int? {
        if (depth > 25) return null
        for (label in listOf(node.text, node.contentDescription)) {
            val s = label?.toString()?.let { normalize(it) } ?: continue
            val m = CALL_TIMER.matchEntire(s) ?: continue
            val h = m.groupValues[1].toIntOrNull() ?: 0
            return h * 3600 + m.groupValues[2].toInt() * 60 + m.groupValues[3].toInt()
        }
        for (i in 0 until node.childCount) {
            val child = node.getChild(i) ?: continue
            findTimerNode(child, depth + 1)?.let { return it }
        }
        return null
    }

    private fun attemptCallControl(kind: String, attempt: Int, onResult: (Boolean) -> Unit) {
        val regex = when (kind) {
            "answer" -> ANSWER_LABEL
            "speaker" -> SPEAKER_LABEL
            "mute" -> MUTE_LABEL
            "hold" -> HOLD_LABEL
            else -> END_LABEL
        }
        val maxLength = if (kind == "answer" || kind == "end") CALL_LABEL_MAX_LENGTH else CALL_TOOL_LABEL_MAX_LENGTH
        var clicked = false
        for (window in windows) {
            val root = window.root ?: continue
            val node = findCallButton(root, regex, maxLength) ?: continue
            val clickable = nearestClickableAncestor(node) ?: continue
            val ok = clickable.performAction(AccessibilityNodeInfo.ACTION_CLICK)
            DiagLog.i(TAG, "callControl($kind): window pkg=${root.packageName} matched text=\"${node.text}\" desc=\"${node.contentDescription}\" " +
                "nodeClass=${node.className} clickedClass=${clickable.className} performClick=$ok")
            if (ok) {
                clicked = true
                // WhatsApp's incoming-call screen answers on a swipe-up touch gesture, not on a
                // click: its "Aceptar llamada" node accepts ACTION_CLICK (performClick=true above)
                // yet nothing happens — confirmed from a real log. A real swipe from the button's
                // own position is what actually triggers it.
                if (kind == "answer" && root.packageName?.toString() == "com.whatsapp") {
                    val bounds = android.graphics.Rect()
                    clickable.getBoundsInScreen(bounds)
                    mainHandler.postDelayed({ swipeUp(bounds.exactCenterX(), bounds.exactCenterY()) }, 350)
                }
                break
            }
        }
        if (clicked) { onResult(true); return }
        if (attempt >= 5) { onResult(false); return }
        mainHandler.postDelayed({ attemptCallControl(kind, attempt + 1, onResult) }, 300)
    }

    private fun findCallButton(node: AccessibilityNodeInfo, regex: Regex, maxLength: Int): AccessibilityNodeInfo? {
        for (label in listOf(node.text, node.contentDescription)) {
            val s = label?.toString()?.let { normalize(it) }
            if (!s.isNullOrEmpty() && s.length <= maxLength && regex.containsMatchIn(s)) return node
        }
        for (i in 0 until node.childCount) {
            val child = node.getChild(i) ?: continue
            findCallButton(child, regex, maxLength)?.let { return it }
        }
        return null
    }

    private fun nearestClickableAncestor(node: AccessibilityNodeInfo): AccessibilityNodeInfo? {
        var current: AccessibilityNodeInfo? = node
        while (current != null) {
            if (current.isClickable) return current
            current = current.parent
        }
        return null
    }
}
