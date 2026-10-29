import Foundation

/// App-state message line, derived from the wire's `MessagingLine` — adds the precomputed
/// sender label + "Sender: text" display text. Mirrors MessagingLine (NotiLink.Core, C#) /
/// NotificationItem.cs's ParseMessages.
public struct MessagingLineItem: Equatable {
    public let sender: String
    public let isMe: Bool
    public let text: String
    public let displayText: String
    public let ts: Int64
    public let imageTransferId: String?
    public let imageMimeType: String
}

/// PC-side app state for one mirrored notification — the Swift equivalent of
/// NotiLink.Core.Models.NotificationItem (C#). Built once from the wire `NotificationPosted`
/// payload, then enriched in place as lazily-fetched images/icons arrive.
public struct NotificationItem {
    public var key: String
    public var packageName: String
    public var appLabel: String
    public var title: String
    public var text: String
    public var subText: String?
    public var postedAt: Int64
    public var isOngoing: Bool
    public var isClearable: Bool
    public var hasProgress: Bool
    public var progress: Int
    public var progressMax: Int
    public var progressIndeterminate: Bool
    public var hasSavedFile: Bool
    public var isSharedText: Bool
    public var style: String
    public var bigText: String?
    public var messages: [MessagingLineItem]?
    public var isGroupConversation: Bool
    public var inboxLines: [String]?
    public var accentColor: String?
    public var hasImage: Bool
    public var imageTransferId: String?
    public var imageBytes: Data?
    public var hasAvatar: Bool
    public var avatarTransferId: String?
    public var avatarBytes: Data?
    public var iconBytes: Data?
    public var actions: [NotificationActionDTO]

    public var isMessagingStyle: Bool { style == "messaging" && !(messages?.isEmpty ?? true) }

    /// The body content for every non-messaging style (messaging renders from `messages`
    /// directly instead, since each line can carry its own image) — mirrors
    /// NotificationItem.cs's `DisplayLines`.
    public var displayLines: [String] {
        if style == "inbox", let lines = inboxLines, !lines.isEmpty { return lines }
        if style == "bigText", let big = bigText, !big.isEmpty { return [big] }
        return [text]
    }

    /// Parses a decoded `notification.posted` payload — mirrors NotificationItem.cs's
    /// `FromPayload`/`ParseMessages`.
    public static func from(_ posted: NotificationPosted, iconBytes: Data?) -> NotificationItem {
        // A sender label only earns its keep once there's more than one possible sender:
        // always for a group conversation, or for a 1:1 chat once the user has actually replied.
        let showSender = posted.isGroupConversation || (posted.messages?.contains { $0.isMe } ?? false)

        let mappedMessages = posted.messages?.map { line -> MessagingLineItem in
            let sender: String
            if !showSender {
                sender = ""
            } else if line.isMe {
                sender = "Tú"
            } else if !line.sender.isEmpty {
                sender = line.sender
            } else {
                sender = posted.isGroupConversation ? "" : posted.title
            }
            let displayText = sender.isEmpty ? line.text : "\(sender): \(line.text)"
            return MessagingLineItem(
                sender: sender, isMe: line.isMe, text: line.text, displayText: displayText,
                ts: line.ts, imageTransferId: line.hasImage ? line.imageTransferId : nil,
                imageMimeType: line.imageMimeType ?? "image/jpeg"
            )
        }

        return NotificationItem(
            key: posted.key, packageName: posted.packageName, appLabel: posted.appLabel,
            title: posted.title, text: posted.text, subText: posted.subText,
            postedAt: posted.postedAt, isOngoing: posted.isOngoing, isClearable: posted.isClearable,
            hasProgress: posted.hasProgress, progress: posted.progress, progressMax: posted.progressMax,
            progressIndeterminate: posted.progressIndeterminate, hasSavedFile: false, isSharedText: false,
            style: posted.style, bigText: posted.bigText, messages: mappedMessages,
            isGroupConversation: posted.isGroupConversation, inboxLines: posted.inboxLines,
            accentColor: posted.accentColor, hasImage: posted.hasImage, imageTransferId: posted.imageTransferId,
            imageBytes: nil, hasAvatar: posted.hasAvatar, avatarTransferId: posted.avatarTransferId,
            avatarBytes: nil, iconBytes: iconBytes, actions: posted.actions
        )
    }
}

// MARK: - JS-facing DTOs (the exact shape wwwroot/app.js expects — mirrors JsNotificationMapper.cs)

public func dataUrl(_ bytes: Data?, mimeType: String) -> String? {
    guard let bytes else { return nil }
    return "data:\(mimeType);base64,\(bytes.base64EncodedString())"
}

public struct JsMessageDTO: Encodable {
    public let sender: String
    public let isMe: Bool
    public let text: String
    public let displayText: String
    public let imageTransferId: String?
    public let imageDataUrl: String?
}

public struct JsNotificationDTO: Encodable {
    public let key: String
    public let packageName: String
    public let appLabel: String
    public let title: String
    public let style: String
    public let isMessaging: Bool
    public let displayLines: [String]
    public let messages: [JsMessageDTO]
    public let imageDataUrl: String?
    public let iconDataUrl: String?
    public let avatarDataUrl: String?
    public let isClearable: Bool
    public let postedAt: Int64
    public let actions: [NotificationActionDTO]
    public let hasSavedFile: Bool
    public let isSharedText: Bool
    public let hasProgress: Bool
    public let progress: Int
    public let progressMax: Int
    public let progressIndeterminate: Bool
    public let accentColor: String?
}

extension NotificationItem {
    /// Mirrors JsNotificationMapper.ToJson — the exact field set/shape wwwroot/app.js's
    /// `window.upsertNotification` handler expects.
    public func toJsDTO() -> JsNotificationDTO {
        JsNotificationDTO(
            key: key, packageName: packageName, appLabel: appLabel, title: title, style: style,
            isMessaging: isMessagingStyle, displayLines: displayLines,
            messages: (messages ?? []).map {
                JsMessageDTO(sender: $0.sender, isMe: $0.isMe, text: $0.text, displayText: $0.displayText,
                             imageTransferId: $0.imageTransferId, imageDataUrl: nil)
            },
            imageDataUrl: dataUrl(imageBytes, mimeType: "image/jpeg"),
            iconDataUrl: dataUrl(iconBytes, mimeType: "image/png"),
            avatarDataUrl: dataUrl(avatarBytes, mimeType: "image/jpeg"),
            isClearable: isClearable, postedAt: postedAt, actions: actions,
            hasSavedFile: hasSavedFile, isSharedText: isSharedText, hasProgress: hasProgress,
            progress: progress, progressMax: progressMax, progressIndeterminate: progressIndeterminate,
            accentColor: accentColor
        )
    }
}

// MARK: - Other JS-facing DTOs

public struct JsAppEntryDTO: Encodable {
    public let packageName: String
    public let label: String
    public let allowed: Bool
    public let iconDataUrl: String?
    public let isSystemApp: Bool

    public init(packageName: String, label: String, allowed: Bool, iconDataUrl: String?, isSystemApp: Bool) {
        self.packageName = packageName
        self.label = label
        self.allowed = allowed
        self.iconDataUrl = iconDataUrl
        self.isSystemApp = isSystemApp
    }
}

public struct JsCellularLineDTO: Encodable {
    public let carrierName: String
    public let level: Int

    public init(carrierName: String, level: Int) {
        self.carrierName = carrierName
        self.level = level
    }
}

public struct JsDeviceListEntryDTO: Encodable {
    public let deviceId: String
    public let name: String
    public let connected: Bool
    public let badgeText: String?
    public let badgeColor: String?
    public let badgeCustom: Bool

    public init(deviceId: String, name: String, connected: Bool, badgeText: String? = nil, badgeColor: String? = nil, badgeCustom: Bool = false) {
        self.deviceId = deviceId
        self.name = name
        self.connected = connected
        self.badgeText = badgeText
        self.badgeColor = badgeColor
        self.badgeCustom = badgeCustom
    }
}
