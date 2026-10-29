import Foundation

/// Envelope `type` strings for the MVP-core message set plus screen/audio sharing
/// (docs/PROTOCOL.md). Media bar, call, PC<->PC link, volume/DND/rotation, link-preview, badges,
/// history, and single-app sharing are still out of scope.
public enum MessageType: String {
    case handshakeHello = "handshake.hello"
    case pairCode = "pair.code"
    case pairConfirm = "pair.confirm"
    case pairToken = "pair.token"
    case notificationPosted = "notification.posted"
    case notificationRemoved = "notification.removed"
    case notificationSync = "notification.sync"
    case deleteRequest = "delete.request"
    case deleteRequestAll = "delete.request.all"
    case actionInvoke = "action.invoke"
    case iconRequest = "icon.request"
    case iconData = "icon.data"
    case batteryUpdate = "battery.update"
    case appSync = "app.sync"
    case appFilterUpdate = "app.filter.update"
    case heartbeat = "heartbeat"
    case connectionToggle = "connection.toggle"
    case pcBye = "pc.bye"
    case shareRequest = "share.request"
    case shareStopRequest = "share.stop.request"
    case shareStatus = "share.status"
    case shareStopApp = "share.stop.app"
    case shareAppReturn = "share.app.return"
    case shareRecents = "share.recents"
}

/// `{}` payload shared by heartbeat / delete.request.all.
public struct EmptyPayload: Codable {
    public init() {}
}

// MARK: - Handshake / pairing

public struct HandshakeHello: Codable {
    public let deviceId: String
    public let deviceName: String
    public let deviceModel: String
    public let pairingToken: String?
    public let protocolVersion: Int
    public let pairingTokens: [String]?

    public init(deviceId: String, deviceName: String, deviceModel: String, pairingToken: String?, protocolVersion: Int, pairingTokens: [String]?) {
        self.deviceId = deviceId
        self.deviceName = deviceName
        self.deviceModel = deviceModel
        self.pairingToken = pairingToken
        self.protocolVersion = protocolVersion
        self.pairingTokens = pairingTokens
    }
}

public struct PairCode: Codable {
    public let code: String
    public init(code: String) { self.code = code }
}

public struct PairConfirm: Codable {
    public let confirmed: Bool
    public init(confirmed: Bool) { self.confirmed = confirmed }
}

public struct PairToken: Codable {
    public let token: String
    public init(token: String) { self.token = token }
}

public struct PcBye: Codable {
    public enum Reason: String, Codable {
        case exit, shutdown, logoff, sleep
    }
    public let reason: Reason

    public init(reason: Reason) { self.reason = reason }
}

public struct ConnectionToggle: Codable {
    public let disconnect: Bool
    public init(disconnect: Bool) { self.disconnect = disconnect }
}

// MARK: - Notifications

public struct MessagingLine: Codable {
    public let sender: String
    public let isMe: Bool
    public let text: String
    public let ts: Int64
    public let hasImage: Bool
    public let imageTransferId: String?
    public let imageMimeType: String?

    public init(sender: String, isMe: Bool, text: String, ts: Int64, hasImage: Bool, imageTransferId: String?, imageMimeType: String?) {
        self.sender = sender
        self.isMe = isMe
        self.text = text
        self.ts = ts
        self.hasImage = hasImage
        self.imageTransferId = imageTransferId
        self.imageMimeType = imageMimeType
    }
}

public struct NotificationActionDTO: Codable {
    public let title: String
    public let hasReplyInput: Bool
    public let replyChoices: [String]

    public init(title: String, hasReplyInput: Bool, replyChoices: [String]) {
        self.title = title
        self.hasReplyInput = hasReplyInput
        self.replyChoices = replyChoices
    }
}

public struct NotificationPosted: Codable {
    public let key: String
    public let packageName: String
    public let appLabel: String
    public let title: String
    public let text: String
    public let subText: String?
    public let style: String
    public let bigText: String?
    public let messages: [MessagingLine]?
    public let isGroupConversation: Bool
    public let inboxLines: [String]?
    public let hasImage: Bool
    public let imageTransferId: String?
    public let hasAvatar: Bool
    public let avatarTransferId: String?
    public let accentColor: String?
    public let hasProgress: Bool
    public let progress: Int
    public let progressMax: Int
    public let progressIndeterminate: Bool
    public let postedAt: Int64
    public let isOngoing: Bool
    public let isClearable: Bool
    public let actions: [NotificationActionDTO]

    public init(
        key: String, packageName: String, appLabel: String, title: String, text: String,
        subText: String?, style: String, bigText: String?, messages: [MessagingLine]?,
        isGroupConversation: Bool, inboxLines: [String]?, hasImage: Bool, imageTransferId: String?,
        hasAvatar: Bool, avatarTransferId: String?, accentColor: String?, hasProgress: Bool,
        progress: Int, progressMax: Int, progressIndeterminate: Bool, postedAt: Int64,
        isOngoing: Bool, isClearable: Bool, actions: [NotificationActionDTO]
    ) {
        self.key = key
        self.packageName = packageName
        self.appLabel = appLabel
        self.title = title
        self.text = text
        self.subText = subText
        self.style = style
        self.bigText = bigText
        self.messages = messages
        self.isGroupConversation = isGroupConversation
        self.inboxLines = inboxLines
        self.hasImage = hasImage
        self.imageTransferId = imageTransferId
        self.hasAvatar = hasAvatar
        self.avatarTransferId = avatarTransferId
        self.accentColor = accentColor
        self.hasProgress = hasProgress
        self.progress = progress
        self.progressMax = progressMax
        self.progressIndeterminate = progressIndeterminate
        self.postedAt = postedAt
        self.isOngoing = isOngoing
        self.isClearable = isClearable
        self.actions = actions
    }
}

public struct NotificationRemoved: Codable {
    public let key: String
    public init(key: String) { self.key = key }
}

public struct NotificationSync: Codable {
    public let keys: [String]
    public init(keys: [String]) { self.keys = keys }
}

public struct DeleteRequest: Codable {
    public let key: String
    public init(key: String) { self.key = key }
}

public struct ActionInvoke: Codable {
    public let key: String
    public let actionIndex: Int
    public let replyText: String?

    public init(key: String, actionIndex: Int, replyText: String?) {
        self.key = key
        self.actionIndex = actionIndex
        self.replyText = replyText
    }
}

// MARK: - Icons

public struct IconRequest: Codable {
    public let packageName: String
    public let transferId: String

    public init(packageName: String, transferId: String) {
        self.packageName = packageName
        self.transferId = transferId
    }
}

public struct IconData: Codable {
    public let packageName: String
    public let transferId: String

    public init(packageName: String, transferId: String) {
        self.packageName = packageName
        self.transferId = transferId
    }
}

// MARK: - Status

public struct CellularLine: Codable {
    public let carrierName: String
    public let level: Int

    public init(carrierName: String, level: Int) {
        self.carrierName = carrierName
        self.level = level
    }
}

public struct BatteryUpdate: Codable {
    public let level: Int
    public let charging: Bool
    public let lowPowerMode: Bool
    public let wifiSsid: String?
    public let wifiLevel: Int?
    public let cellular: [CellularLine]

    public init(level: Int, charging: Bool, lowPowerMode: Bool, wifiSsid: String?, wifiLevel: Int?, cellular: [CellularLine]) {
        self.level = level
        self.charging = charging
        self.lowPowerMode = lowPowerMode
        self.wifiSsid = wifiSsid
        self.wifiLevel = wifiLevel
        self.cellular = cellular
    }
}

// MARK: - App filter

public struct AppEntry: Codable {
    public let packageName: String
    public let label: String
    public let allowed: Bool
    public let isSystemApp: Bool

    public init(packageName: String, label: String, allowed: Bool, isSystemApp: Bool) {
        self.packageName = packageName
        self.label = label
        self.allowed = allowed
        self.isSystemApp = isSystemApp
    }
}

public struct AppSync: Codable {
    public let apps: [AppEntry]
    public init(apps: [AppEntry]) { self.apps = apps }
}

public struct AppFilterUpdate: Codable {
    public let packageName: String
    public let allowed: Bool

    public init(packageName: String, allowed: Bool) {
        self.packageName = packageName
        self.allowed = allowed
    }
}

// MARK: - UDP discovery (unframed, raw JSON datagrams)

public struct DiscoveryDiscover: Codable {
    public let type: String
    public let deviceName: String

    public init(type: String = "notilink.discover", deviceName: String) {
        self.type = type
        self.deviceName = deviceName
    }
}

public struct DiscoveryAnnounce: Codable {
    public let type: String
    public let pcName: String
    public let tcpPort: Int

    public init(pcName: String, tcpPort: Int) {
        self.type = "notilink.announce"
        self.pcName = pcName
        self.tcpPort = tcpPort
    }
}

// MARK: - Screen/audio sharing (scrcpy)

public struct ShareRequest: Codable {
    public let type: String // "screen" | "audio" | "app"
    public let packageName: String?
    public let label: String?

    public init(type: String, packageName: String? = nil, label: String? = nil) {
        self.type = type
        self.packageName = packageName
        self.label = label
    }
}

public struct ShareStatusApp: Codable {
    public let packageName: String
    public let label: String
    public let state: String // "pc" | "phone"

    public init(packageName: String, label: String, state: String) {
        self.packageName = packageName
        self.label = label
        self.state = state
    }
}

public struct ShareStatus: Codable {
    public let screenSharing: Bool
    public let audioSharing: Bool
    public let apps: [ShareStatusApp]

    public init(screenSharing: Bool, audioSharing: Bool, apps: [ShareStatusApp] = []) {
        self.screenSharing = screenSharing
        self.audioSharing = audioSharing
        self.apps = apps
    }
}

public struct ShareStopApp: Codable {
    public let packageName: String
    public init(packageName: String) { self.packageName = packageName }
}

public struct ShareAppReturn: Codable {
    public let packageName: String
    public init(packageName: String) { self.packageName = packageName }
}

public struct ShareRecentApp: Codable {
    public let packageName: String
    public let label: String

    public init(packageName: String, label: String) {
        self.packageName = packageName
        self.label = label
    }
}

public struct ShareRecents: Codable {
    public let apps: [ShareRecentApp]
    public let updatedAt: Int64

    public init(apps: [ShareRecentApp], updatedAt: Int64) {
        self.apps = apps
        self.updatedAt = updatedAt
    }
}
