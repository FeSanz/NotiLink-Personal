package com.notilink.app.net

import android.content.Context

/**
 * Persists the manually-entered PC IP. Stands in for real discovery/pairing
 * (M7) so the listener has somewhere to send events to in the meantime.
 */
object PcSettings {
    private const val PREFS = "notilink_prefs"
    private const val KEY_PC_IP = "pc_ip"

    fun getPcIp(context: Context): String? =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString(KEY_PC_IP, null)

    fun setPcIp(context: Context, ip: String) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
            .putString(KEY_PC_IP, ip)
            .apply()
        activePc(context)?.let { if (it.ip != ip) savePc(context, it.copy(ip = ip)) }
    }

    // ---- Saved PCs: every PC this phone was linked with. Only ONE is active (the one pc_ip /
    // pc_addresses point at, and the only one the connection ever dials) — the phone keeps a
    // single socket, so switching means dropping the current PC first. Pairing tokens for all of
    // them live in PairingTokenStore and are all sent in handshake.hello, so going back to an
    // earlier PC needs no new code.

    /** [id] is the PC's own pcId from its discovery announce, or "ip:<address>" until it's known. */
    data class SavedPc(val id: String, val name: String, val ip: String, val addresses: List<Pair<String, String>> = emptyList())

    private const val KEY_SAVED_PCS = "saved_pcs"
    private const val KEY_ACTIVE_PC = "active_pc_id"

    fun savedPcs(context: Context): List<SavedPc> {
        val p = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val list = try {
            val arr = org.json.JSONArray(p.getString(KEY_SAVED_PCS, "[]"))
            (0 until arr.length()).map { i ->
                val o = arr.getJSONObject(i)
                val addrs = o.optJSONArray("addresses")
                SavedPc(o.getString("id"), o.optString("name", "PC"), o.getString("ip"),
                    if (addrs == null) emptyList() else (0 until addrs.length()).map { j ->
                        addrs.getString(j).let { it.substringBefore('|') to it.substringAfter('|', "lan") }
                    })
            }
        } catch (e: Exception) { emptyList() }
        if (list.isNotEmpty()) return list
        // Upgrading from the single-PC version: the one saved IP becomes the first saved PC.
        val ip = getPcIp(context) ?: return emptyList()
        val migrated = SavedPc("ip:$ip", "PC", ip, getAddresses(context))
        writeSaved(context, listOf(migrated))
        p.edit().putString(KEY_ACTIVE_PC, migrated.id).apply()
        return listOf(migrated)
    }

    fun activePcId(context: Context): String? {
        val pcs = savedPcs(context) // runs the migration first if needed
        val id = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString(KEY_ACTIVE_PC, null)
        return id?.takeIf { wanted -> pcs.any { it.id == wanted } }
    }

    fun activePc(context: Context): SavedPc? = activePcId(context)?.let { id -> savedPcs(context).firstOrNull { it.id == id } }

    /** Adds or replaces (by id; a placeholder "ip:" entry for the same address is folded in). */
    fun savePc(context: Context, pc: SavedPc) {
        val others = savedPcs(context).filterNot { it.id == pc.id || (it.id == "ip:${pc.ip}" && it.id != pc.id) }
        val wasActive = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString(KEY_ACTIVE_PC, null)
        writeSaved(context, others + pc)
        if (wasActive == "ip:${pc.ip}" && pc.id != wasActive)
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putString(KEY_ACTIVE_PC, pc.id).apply()
    }

    fun removePc(context: Context, id: String) {
        writeSaved(context, savedPcs(context).filterNot { it.id == id })
    }

    /** Makes [pc] the one the connection dials (saving it too). The caller reconnects. */
    fun activate(context: Context, pc: SavedPc) {
        savePc(context, pc)
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
            .putString(KEY_ACTIVE_PC, pc.id)
            .putString(KEY_PC_IP, pc.ip)
            .putString(KEY_ADDRESSES, pc.addresses.distinctBy { it.first }.take(6).joinToString(",") { it.first + "|" + it.second })
            .apply()
    }

    private fun writeSaved(context: Context, list: List<SavedPc>) {
        val arr = org.json.JSONArray()
        list.forEach { pc ->
            arr.put(org.json.JSONObject()
                .put("id", pc.id).put("name", pc.name).put("ip", pc.ip)
                .put("addresses", org.json.JSONArray(pc.addresses.map { it.first + "|" + it.second })))
        }
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putString(KEY_SAVED_PCS, arr.toString()).apply()
    }

    /** A plain dotted IPv4 address (what the PC's QR and discovery hand out). */
    fun isValidIpv4(text: String?): Boolean {
        val parts = text?.trim()?.split('.') ?: return false
        return parts.size == 4 && parts.all { part -> part.isNotEmpty() && part.length <= 3 && part.all(Char::isDigit) && part.toInt() in 0..255 }
    }

    private const val KEY_ADDRESSES = "pc_addresses"

    /** Every address the PC reported (`pc.addresses`: its LAN IPs and, with Tailscale, its
     *  100.x address), as "ip|kind" — kind "lan" or "vpn". */
    fun getAddresses(context: Context): List<Pair<String, String>> =
        (context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString(KEY_ADDRESSES, "") ?: "")
            .split(',').filter { it.contains('|') }.map { it.substringBefore('|') to it.substringAfter('|') }

    fun setAddresses(context: Context, list: List<Pair<String, String>>) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
            .putString(KEY_ADDRESSES, list.distinctBy { it.first }.take(6).joinToString(",") { it.first + "|" + it.second })
            .apply()
        // Remembered per saved PC too, so switching back to it still knows its other addresses.
        activePc(context)?.let { savePc(context, it.copy(addresses = list.distinctBy { a -> a.first }.take(6))) }
    }

    /** Where to try, in order: the address that worked last, then the PC's other LAN addresses,
     *  then the VPN one (works from anywhere, e.g. on mobile data with Tailscale). */
    fun candidates(context: Context): List<String> {
        val current = getPcIp(context)
        val others = getAddresses(context).sortedBy { if (it.second == "vpn") 1 else 0 }.map { it.first }
        return (listOfNotNull(current) + others).distinct()
    }

    fun isVpnAddress(ip: String?): Boolean {
        val parts = ip?.split('.')?.mapNotNull { it.toIntOrNull() } ?: return false
        return parts.size == 4 && parts[0] == 100 && parts[1] in 64..127 // 100.64.0.0/10 (Tailscale / CGNAT range)
    }

    private const val KEY_STATUS_AUTOHIDE_S = "status_autohide_s"

    /** Seconds after which the connected/disconnected status notification hides itself
     *  (0 = never). Set from the PC's Ajustes — see docs/PROTOCOL.md's phone.prefs. */
    fun getStatusAutoHideSeconds(context: Context): Int =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getInt(KEY_STATUS_AUTOHIDE_S, 0)

    fun setStatusAutoHideSeconds(context: Context, seconds: Int) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
            .putInt(KEY_STATUS_AUTOHIDE_S, seconds.coerceIn(0, 3600))
            .apply()
    }
}
