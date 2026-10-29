package com.notilink.app.pairing

import com.notilink.app.net.Protocol
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress

/**
 * One-shot UDP broadcast per docs/PROTOCOL.md Discovery — finds the PC on the
 * LAN without the user typing an IP. Falls back silently to null on timeout
 * (some guest/mesh networks drop broadcasts); the manual IP field and QR scan
 * remain as the required fallbacks.
 */
object DiscoveryClient {
    fun discover(deviceName: String, timeoutMs: Int = 3000, callback: (String?) -> Unit) {
        Thread { callback(discoverBlocking(deviceName, timeoutMs)) }.start()
    }

    /** Where to send the probe: every interface's own subnet broadcast (hotspot "swlan0"/"ap0",
     *  Wi-Fi, USB tethering...) plus the limited broadcast. Just 255.255.255.255 isn't enough:
     *  with the phone's hotspot on, Android sends it out the mobile-data network, never reaching
     *  a PC connected to the hotspot. */
    private fun broadcastTargets(): List<InetAddress> {
        val targets = LinkedHashSet<InetAddress>()
        try {
            for (ni in java.net.NetworkInterface.getNetworkInterfaces().toList()) {
                if (!ni.isUp || ni.isLoopback) continue
                for (ia in ni.interfaceAddresses) {
                    if (ia.address is java.net.Inet4Address) ia.broadcast?.let { targets.add(it) }
                }
            }
        } catch (e: Exception) { }
        targets.add(InetAddress.getByName("255.255.255.255"))
        return targets.toList()
    }

    /** A NotiLink PC that answered. [pcId] is null for PC versions older than 1.4 (no id in the
     *  announce) or when only the TCP port answered (see [probe]). */
    data class FoundPc(val ip: String, val name: String, val pcId: String?)

    /** Blocking; every PC that answers within [timeoutMs] — with two NotiLink PCs on the same
     *  network, "the first one" (discoverBlocking) can be either of them. */
    fun discoverAll(deviceName: String, timeoutMs: Int = 2500): List<FoundPc> =
        collect(deviceName, broadcastTargets(), timeoutMs, stopAtFirst = false)

    /** Blocking; is a NotiLink PC at [ip]? Asks it directly (unicast discovery, which also
     *  returns its name and id), then falls back to just opening its TCP port — a Tailscale or
     *  firewalled path may pass TCP but not that UDP reply. */
    fun probe(ip: String, timeoutMs: Int = 1500): FoundPc? {
        val target = try { InetAddress.getByName(ip) } catch (e: Exception) { return null }
        collect(DeviceIdentity.getDeviceName(), listOf(target), timeoutMs, stopAtFirst = true).firstOrNull()?.let { return it }
        return try {
            java.net.Socket().use { it.connect(java.net.InetSocketAddress(target, Protocol.TCP_PORT), timeoutMs) }
            FoundPc(ip, "PC ($ip)", null)
        } catch (e: Exception) { null }
    }

    private fun collect(deviceName: String, targets: List<InetAddress>, timeoutMs: Int, stopAtFirst: Boolean): List<FoundPc> {
        val found = LinkedHashMap<String, FoundPc>()
        try {
            DatagramSocket().use { socket ->
                socket.broadcast = true
                val message = JSONObject().apply {
                    put("type", "notilink.discover")
                    put("deviceName", deviceName)
                }.toString().toByteArray(Charsets.UTF_8)
                for (target in targets) {
                    try { socket.send(DatagramPacket(message, message.size, target, Protocol.UDP_DISCOVERY_PORT)) } catch (e: Exception) { }
                }
                val deadline = System.currentTimeMillis() + timeoutMs
                val buffer = ByteArray(2048)
                while (true) {
                    val left = deadline - System.currentTimeMillis()
                    if (left <= 0) break
                    socket.soTimeout = left.toInt()
                    val response = DatagramPacket(buffer, buffer.size)
                    try { socket.receive(response) } catch (e: java.net.SocketTimeoutException) { break }
                    val json = try { JSONObject(String(response.data, 0, response.length, Charsets.UTF_8)) } catch (e: Exception) { continue }
                    if (json.optString("type") != "notilink.announce") continue
                    val ip = response.address.hostAddress ?: continue
                    val pcId = json.optString("pcId").takeIf { it.isNotBlank() && it != "null" }
                    val pc = FoundPc(ip, json.optString("pcName").takeIf { it.isNotBlank() } ?: "PC ($ip)", pcId)
                    found.putIfAbsent(pcId ?: "ip:$ip", pc)
                    if (stopAtFirst) break
                }
            }
        } catch (e: Exception) { }
        return found.values.toList()
    }

    /** Blocking; returns the first PC that answers, or null. */
    fun discoverBlocking(deviceName: String, timeoutMs: Int = 3000): String? {
        try {
            DatagramSocket().use { socket ->
                socket.broadcast = true
                socket.soTimeout = timeoutMs
                val message = JSONObject().apply {
                    put("type", "notilink.discover")
                    put("deviceName", deviceName)
                }.toString().toByteArray(Charsets.UTF_8)
                for (target in broadcastTargets()) {
                    try {
                        socket.send(DatagramPacket(message, message.size, target, Protocol.UDP_DISCOVERY_PORT))
                    } catch (e: Exception) { }
                }
                val deadline = System.currentTimeMillis() + timeoutMs
                val buffer = ByteArray(2048)
                while (System.currentTimeMillis() < deadline) {
                    val response = DatagramPacket(buffer, buffer.size)
                    socket.receive(response) // throws SocketTimeoutException if nothing replies
                    val json = try { JSONObject(String(response.data, 0, response.length, Charsets.UTF_8)) } catch (e: Exception) { continue }
                    if (json.optString("type") == "notilink.announce") return response.address.hostAddress
                }
            }
        } catch (e: Exception) {
            // timeout or network error — caller falls back to manual/QR
        }
        return null
    }
}
