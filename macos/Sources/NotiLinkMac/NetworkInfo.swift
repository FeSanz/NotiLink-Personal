import Foundation

/// Mirrors NetworkInfo.GetLocalIPv4() (C#) — the PC's own LAN IPv4, shown as text for manual
/// entry and encoded into the pairing QR code (confirmed against MainActivity.kt's QR scanner:
/// the payload is just this raw string, nothing structured).
enum NetworkInfo {
    static func localIPv4Addresses() -> [String] {
        var addresses: [String] = []
        var ifaddrPointer: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifaddrPointer) == 0, let firstAddr = ifaddrPointer else { return [] }
        defer { freeifaddrs(ifaddrPointer) }

        for ptr in sequence(first: firstAddr, next: { $0.pointee.ifa_next }) {
            let flags = Int32(ptr.pointee.ifa_flags)
            guard (flags & IFF_UP) == IFF_UP, (flags & IFF_LOOPBACK) == 0,
                  let addr = ptr.pointee.ifa_addr, addr.pointee.sa_family == UInt8(AF_INET) else { continue }

            var hostBuffer = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            getnameinfo(addr, socklen_t(addr.pointee.sa_len), &hostBuffer, socklen_t(hostBuffer.count), nil, 0, NI_NUMERICHOST)
            let address = String(cString: hostBuffer)
            if !address.isEmpty { addresses.append(address) }
        }
        return addresses
    }

    static func primaryLocalIPv4() -> String? {
        localIPv4Addresses().first
    }
}
