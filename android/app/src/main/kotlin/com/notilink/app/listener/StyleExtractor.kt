package com.notilink.app.listener

import android.app.Notification
import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.drawable.Icon
import android.net.Uri
import android.os.Bundle
import androidx.core.graphics.drawable.toBitmap
import com.notilink.app.net.DiagLog
import org.json.JSONArray

/**
 * style is one of "plain" | "bigText" | "bigPicture" | "messaging" | "inbox"
 * per docs/PROTOCOL.md. Always falls back to "plain" for anything
 * unrecognized rather than throwing — an exotic OEM template must never
 * crash the listener or blank-render on the PC side.
 */
data class ExtractedMessage(
    val sender: String,
    /** True when this message is the local device owner's own (a reply sent from here, from the
     *  phone itself, or historically) rather than the other participant's — see extractMessages'
     *  own doc comment for how this is actually determined. Lets the PC show "Tú" and a distinct
     *  color consistently, instead of depending on whatever literal text (if any) each app
     *  happens to put in [sender] for its own echoed messages. */
    val isMe: Boolean,
    val text: String,
    val ts: Long,
    /** Static photo — gets downscaled and JPEG-re-encoded downstream. */
    val imageBitmap: Bitmap? = null,
    /** Animated sticker (GIF/WebP) — the original bytes, untouched, so the
     *  animation survives; re-encoding through Bitmap would flatten it to one frame. */
    val imageRawBytes: ByteArray? = null,
    val imageRawMimeType: String? = null,
)

data class ExtractedContent(
    val style: String,
    val bigText: String? = null,
    val messages: List<ExtractedMessage>? = null,
    val inboxLines: JSONArray? = null,
    /** bigPicture style's single image only — messaging-style images live per-message. */
    val image: Bitmap? = null,
    val isGroupConversation: Boolean = false,
)

object StyleExtractor {
    private const val TAG = "NotiLink/StyleExtractor"

    fun extract(context: Context, notification: Notification): ExtractedContent {
        val extras = notification.extras
        return try {
            // Checked ahead of EXTRA_TEMPLATE, and independently of it: Notification.CATEGORY_CALL
            // is a much older, more broadly-adopted signal (predates the CallStyle template class,
            // which is API 31+ only) that plenty of apps still set on their own custom-built call
            // notifications without ever using CallStyle. Relying on EXTRA_TEMPLATE alone missed
            // real incoming calls on at least one device/app combination during testing — this
            // catches those too, and "it's a call" should win over whatever template it's also
            // wearing either way.
            if (notification.category == Notification.CATEGORY_CALL) {
                return ExtractedContent(style = "call")
            }
            when (extras.getString(Notification.EXTRA_TEMPLATE)) {
                "android.app.Notification\$BigPictureStyle" ->
                    ExtractedContent(style = "bigPicture", image = extractBigPicture(context, extras))

                "android.app.Notification\$BigTextStyle" ->
                    ExtractedContent(style = "bigText", bigText = extras.getCharSequence(Notification.EXTRA_BIG_TEXT)?.toString())

                "android.app.Notification\$MessagingStyle" ->
                    extractMessages(context, extras)?.let {
                        ExtractedContent(
                            style = "messaging",
                            messages = it,
                            isGroupConversation = extras.getBoolean(Notification.EXTRA_IS_GROUP_CONVERSATION, false),
                        )
                    } ?: ExtractedContent(style = "plain")

                "android.app.Notification\$InboxStyle" ->
                    extractInboxLines(extras)?.let { ExtractedContent(style = "inbox", inboxLines = it) }
                        ?: ExtractedContent(style = "plain")

                // Incoming/ongoing call UI (API 31+) — tagged distinctly so the PC can show it
                // as an alert instead of a regular card; title/text fallback lives in the caller
                // (NotiLinkNotificationListener.sendPosted), since that's where Person data is read.
                "android.app.Notification\$CallStyle" -> ExtractedContent(style = "call")

                else -> ExtractedContent(style = "plain")
            }
        } catch (e: Exception) {
            ExtractedContent(style = "plain")
        }
    }

    /**
     * The contact/sender's profile photo — separate from the app's own
     * launcher icon, shown as the primary avatar the way WhatsApp's own
     * notifications do, with the app icon as a small corner badge.
     */
    @Suppress("DEPRECATION")
    fun extractAvatar(context: Context, notification: Notification): Bitmap? {
        return try {
            notification.getLargeIcon()?.loadDrawable(context)?.toBitmap()
        } catch (e: Exception) {
            null
        } ?: (notification.extras.get(Notification.EXTRA_LARGE_ICON) as? Bitmap)
    }

    @Suppress("DEPRECATION")
    private fun extractBigPicture(context: Context, extras: Bundle): Bitmap? {
        (extras.get(Notification.EXTRA_PICTURE) as? Bitmap)?.let { return it }
        return try {
            val icon = extras.getParcelable<Icon>(Notification.EXTRA_PICTURE_ICON)
            icon?.loadDrawable(context)?.toBitmap()
        } catch (e: Exception) {
            null
        }
    }

    /**
     * WhatsApp (and most modern chat apps) attach photos/stickers to a
     * MessagingStyle.Message as a content:// URI + mime type, not as a
     * separate BigPictureStyle notification — the message text is just a
     * placeholder ("Foto", "Sticker"). Each message keeps its own image so
     * several unread photos in a row can be shown stacked, not just the
     * latest one clobbering the rest.
     */
    /** Above this, even an animated sticker falls back to a static extracted frame —
     *  stickers are normally tiny (well under this), this only guards against an
     *  unexpectedly huge attachment being forwarded raw and unbounded. */
    private const val MAX_RAW_ANIMATED_BYTES = 2 * 1024 * 1024

    // Real-world evidence (this device's own diag log, 163 real messages captured across several
    // live WhatsApp/Messenger conversations) contradicts the "blank Person means me" premise this
    // used to rely on exclusively: not ONE of those 163 messages — including the user's own sent
    // replies — ever had a null senderPerson. WhatsApp on this build always attaches a Person, to
    // every message, including its own echoed replies (which it attributes to whatever Person is
    // registered as EXTRA_MESSAGING_PERSON). So matching senderPerson against EXTRA_MESSAGING_PERSON
    // isn't a nice-to-have here — for this app it's the ONLY signal that ever fires at all, and
    // removing it (an earlier version of this function did, to fix the false positive below) left
    // isOwnMessage permanently false — confirmed live: 0 of 163 logged messages ever came back
    // isMe=true afterwards.
    //
    // The false positive that motivated removing it is real too, though: Banco Azteca sets
    // senderPerson to a marketing greeting ("Brando, para ti 🔝") for its own single promotional
    // message, AND registers that exact same greeting as EXTRA_MESSAGING_PERSON — so a bare match
    // "succeeds" despite neither side representing a real contact or the local user. What actually
    // distinguishes that case isn't the match itself, it's that NOTHING ELSE in the notification
    // proves a real second party exists — [hasDistinctContactPerson] requires at least one message
    // whose Person is attached AND does NOT match userPerson, i.e. proof this notification really
    // is a two-sided conversation with someone else, before trusting either convention (a Person
    // match, or a blank Person) as meaning "this one's mine".
    private fun personsMatch(a: android.app.Person?, b: android.app.Person?): Boolean {
        if (a == null || b == null) return false
        val ak = a.key
        val bk = b.key
        return if (ak != null && bk != null) ak == bk else a.name?.toString() == b.name?.toString()
    }

    private fun isOwnMessage(
        senderPerson: android.app.Person?,
        userPerson: android.app.Person?,
        hasDistinctContactPerson: Boolean,
    ): Boolean {
        if (senderPerson == null) return hasDistinctContactPerson
        return personsMatch(senderPerson, userPerson) && hasDistinctContactPerson
    }

    @Suppress("DEPRECATION")
    private fun extractMessages(context: Context, extras: Bundle): List<ExtractedMessage>? {
        val raw = extras.getParcelableArray(Notification.EXTRA_MESSAGES) ?: return null
        val messages = Notification.MessagingStyle.Message.getMessagesFromBundleArray(raw)
        if (messages.isNullOrEmpty()) return null
        val userPerson = extras.getParcelable<android.app.Person>(Notification.EXTRA_MESSAGING_PERSON)
        val hasDistinctContactPerson = messages.any { it.senderPerson != null && !personsMatch(it.senderPerson, userPerson) }

        // Diagnostic only, never the message text itself (this style is also used by e.g. bank
        // apps for transactional alerts — logging real content there would be a real privacy
        // problem, not just noise). This is what caught both the Banco Azteca false positive and
        // the isMe-never-true regression above.
        DiagLog.i(TAG, "extractMessages: ${messages.size} message(s), userPerson=(name=${userPerson?.name}, key=${userPerson?.key}), hasDistinctContactPerson=$hasDistinctContactPerson")

        return messages.map { m ->
            val isMeDiag = isOwnMessage(m.senderPerson, userPerson, hasDistinctContactPerson)
            DiagLog.i(TAG, "  message: senderPerson=(name=${m.senderPerson?.name}, key=${m.senderPerson?.key}), legacySender=${m.sender}, isMe=$isMeDiag")

            var imageBitmap: Bitmap? = null
            var imageRawBytes: ByteArray? = null
            var imageRawMimeType: String? = null

            val mimeType = m.dataMimeType
            val dataUri = m.dataUri
            if (mimeType != null || dataUri != null) DiagLog.i(TAG, "  message attachment: mime=$mimeType uri=${dataUri?.scheme}://${dataUri?.authority} placeholder=\"${m.text?.toString()?.take(30)}\"")
            // Investigation aid: a WhatsApp album ("📷 4 fotos") arrives as text only, no dataUri —
            // logs whatever the message's own extras bundle carries in that case, to find out if
            // the photos are reachable at all. Remove once answered.
            if (dataUri == null && m.text?.toString()?.contains(Regex("\\d+\\s*(fotos|photos|im[aá]genes|videos)", RegexOption.IGNORE_CASE)) == true) {
                val ex = m.extras
                DiagLog.i(TAG, "  album-like message without uri: extrasKeys=${ex?.keySet()?.joinToString()}")
            }
            if (mimeType != null && mimeType.startsWith("image/") && dataUri != null) {
                if (mimeType == "image/gif" || mimeType == "image/webp") {
                    val rawBytes = loadRawBytes(context, dataUri, MAX_RAW_ANIMATED_BYTES)
                    if (rawBytes != null) {
                        imageRawBytes = rawBytes
                        imageRawMimeType = mimeType
                    } else {
                        imageBitmap = loadBitmapFromUri(context, dataUri) // too large or unreadable — fall back to a static frame
                    }
                } else {
                    imageBitmap = loadBitmapFromUri(context, dataUri)
                }
            }

            ExtractedMessage(
                sender = m.senderPerson?.name?.toString() ?: m.sender?.toString() ?: "",
                isMe = isMeDiag,
                text = m.text?.toString() ?: "",
                ts = m.timestamp,
                imageBitmap = imageBitmap,
                imageRawBytes = imageRawBytes,
                imageRawMimeType = imageRawMimeType,
            )
        }
    }

    private fun loadBitmapFromUri(context: Context, uri: Uri): Bitmap? = try {
        context.contentResolver.openInputStream(uri)?.use { BitmapFactory.decodeStream(it) }
    } catch (e: Exception) {
        DiagLog.w(TAG, "loadRawBytes failed: ${e.javaClass.simpleName} ${e.message}")
        null
    }

    private fun loadRawBytes(context: Context, uri: Uri, maxBytes: Int): ByteArray? = try {
        context.contentResolver.openInputStream(uri)?.use { input ->
            val bytes = input.readBytes()
            if (bytes.size <= maxBytes) bytes else null
        }
    } catch (e: Exception) {
        null
    }

    private fun extractInboxLines(extras: Bundle): JSONArray? {
        val lines = extras.getCharSequenceArray(Notification.EXTRA_TEXT_LINES) ?: return null
        if (lines.isEmpty()) return null
        val array = JSONArray()
        lines.forEach { array.put(it.toString()) }
        return array
    }
}
