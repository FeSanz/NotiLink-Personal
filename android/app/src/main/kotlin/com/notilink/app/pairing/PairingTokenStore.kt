package com.notilink.app.pairing

import android.content.Context
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey
import org.json.JSONArray

/** Tokens issued by PCs after the 6-digit pairing code is confirmed — Keystore-backed, sent in
 *  every handshake.hello so reconnects skip straight to trusted.
 *
 *  Several are kept, not just the latest: the same phone paired with two PCs (confirmed real
 *  case — a second computer running NotiLink) used to overwrite the first PC's token with the
 *  second's, so going back to the first PC always looked like an unknown phone and it asked for
 *  the code again. The phone can't tell which PC it's talking to before the handshake, so it
 *  sends all of them and each PC accepts the one it issued. */
object PairingTokenStore {
    private const val PREFS = "notilink_pairing_secure"
    private const val KEY_TOKEN = "pairing_token"
    private const val KEY_TOKENS = "pairing_tokens"
    private const val MAX_TOKENS = 5

    /** The most recently issued token (still sent as pairingToken, for older PC versions). */
    fun getToken(context: Context): String? = prefs(context).getString(KEY_TOKEN, null)

    /** Every remembered token, newest first. */
    fun getTokens(context: Context): List<String> {
        val p = prefs(context)
        val list = try {
            JSONArray(p.getString(KEY_TOKENS, "[]")).let { a -> (0 until a.length()).map { a.getString(it) } }
        } catch (e: Exception) { emptyList() }
        val latest = p.getString(KEY_TOKEN, null)
        return (listOfNotNull(latest) + list).distinct()
    }

    fun saveToken(context: Context, token: String) {
        val tokens = (listOf(token) + getTokens(context)).distinct().take(MAX_TOKENS)
        prefs(context).edit()
            .putString(KEY_TOKEN, token)
            .putString(KEY_TOKENS, JSONArray(tokens).toString())
            .apply()
    }

    private fun prefs(context: Context): android.content.SharedPreferences {
        val masterKey = MasterKey.Builder(context)
            .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
            .build()
        return EncryptedSharedPreferences.create(
            context,
            PREFS,
            masterKey,
            EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
            EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM,
        )
    }
}
