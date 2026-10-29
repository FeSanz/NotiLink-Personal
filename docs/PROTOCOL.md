# NotiLink Wire Protocol v1

Canonical spec. Both `android/` and `windows/` implement against this document — if the code and this file disagree, this file wins and the code has a bug.

## Transport

Single persistent TCP connection per pairing. **PC is always the TCP server**, phone is always the client (see rationale in the plan: Android background processes are unreliable inbound listeners under Doze/OEM restrictions).

- PC TCP server port: **45212**
- PC UDP discovery-listener port: **45211**

## Framing

Every message on the wire, in both directions, uses this frame:

```
+------------------+-----------+-------------------+
| length (4 bytes) | type (1B) | payload (N bytes)  |
+------------------+-----------+-------------------+
```

- `length` — big-endian `uint32`, byte count of `payload` only (not including the 5-byte header itself).
- `type`:
  - `0x01` = JSON control message. `payload` is UTF-8 encoded JSON, always a single envelope object (see below).
  - `0x02` = Binary blob. `payload` is raw bytes (an image). Always sent immediately after the JSON message that references it via a `transferId`, and always on the *same* TCP connection/stream (not a separate socket).
- Max `length`: 8 MiB. Either side closes the connection if a declared length exceeds this (protects against a corrupt/malicious length prefix).

## JSON Envelope

Every `0x01` message is a single JSON object:

```json
{
  "v": 1,
  "type": "notification.posted",
  "id": "b3f1c2a0-...-uuid",
  "ts": 1755763200000,
  "payload": { }
}
```

- `v` — protocol version, integer. This document defines `v = 1`.
- `type` — string, one of the message types below. **Unknown `type` values must be ignored, not treated as an error** — this is the forward-compatibility hook for future message types (e.g. a later `action.invoke` for reply support) without breaking older clients.
- `id` — UUID, unique per message, used for correlation/logging. Not currently used for acks in v1.
- `ts` — epoch milliseconds, when the sender created the message.
- `payload` — type-specific object, see each message type below. Unknown fields inside a known payload must also be ignored (additive forward-compat), not rejected.

## Message Types

### `handshake.hello` (phone → PC)
First message sent on every new connection, before anything else.

```json
{
  "deviceId": "a1b2c3d4-...",
  "deviceName": "Pixel 7",
  "deviceModel": "Pixel 7",
  "pairingToken": "opaque-token-string-or-null",
  "protocolVersion": 1
}
```

- `deviceId` — stable UUID generated once on first app launch, persisted locally. Wiped along with the rest of the app's private storage on a real uninstall (a replace-install/`adb install -r` keeps it).
- `deviceName` — the phone's current default label (`Build.MANUFACTURER + " " + Build.MODEL`). Only used as the *initial* name for a brand-new pairing; PC-side renames are never overwritten by this field on reconnect.
- `deviceModel` — same value as `deviceName` today, but sent as a distinct field on purpose: PC stores it as an immutable hardware fingerprint, separate from the (possibly renamed) display name. If `deviceId` shows up as unpaired but its `deviceModel` matches an existing paired entry under a different `deviceId` (most likely this same phone re-pairing after a real uninstall wiped its old `deviceId`/token), PC carries over that entry's custom name instead of resetting it to the default, and drops the stale duplicate — see `pair.confirm` below.
- `pairingToken` — `null` on a never-paired device; otherwise the token issued during a previous `pair.token` exchange.
- If `pairingToken` is valid for the given `deviceId`, PC proceeds directly to normal operation (skip to `app.sync` from phone). If invalid/null/unknown, PC starts the pairing flow (`pair.code`).

### `pair.code` (PC → phone)
Sent only when `handshake.hello` arrived with no valid token.

```json
{ "code": "482913" }
```

PC also displays this same 6-digit code in its own UI. User visually compares and taps "Confirm" on the phone.

### `pair.confirm` (phone → PC)
```json
{ "confirmed": true }
```

### `pair.token` (PC → phone)
Sent immediately after `pair.confirm` with `confirmed: true`.

```json
{ "token": "opaque-token-string" }
```

Phone persists this (Android Keystore-backed storage) and sends it in `handshake.hello` on every future connection. PC persists the mapping `deviceId -> token -> deviceName` (DPAPI-protected) for its "paired devices" management view, including the ability to revoke (delete) a token, which forces that phone back through the `pair.code` flow next connect.

### `heartbeat` (both directions)
```json
{}
```
Sent every 15s while idle. If PC receives nothing (heartbeat or otherwise) from a phone for 45s, `TcpServer`'s own watchdog force-closes that connection and fires the normal `DeviceDisconnected` event — the same cleanup path a real socket-level disconnect takes, screen/audio share teardown included (see MainViewModel's `DeviceDisconnected` handler). This exists specifically for a phone that silently drops off the network (left WiFi range, airplane mode, ...): that case never produces a TCP error on its own — a blocking read on a dead-but-not-reset socket can hang far longer than 45s — so without an active watchdog nothing would ever notice, and confirmed live: a phone that left WiFi mid-audio-share kept its own audio capture stuck open (system-wide silence) until it was rebooted, since scrcpy's phone-side session had nothing telling it to stop. The phone-side reconnect-with-backoff logic still owns re-establishing the connection afterward — a reconnect always starts clean, with nothing still "active" left over from before the drop.

### `notification.posted` (phone → PC)
```json
{
  "key": "0|com.whatsapp|123|null|10123",
  "packageName": "com.whatsapp",
  "appLabel": "WhatsApp",
  "title": "Juan Perez",
  "text": "Hey, are we still on for lunch?",
  "subText": null,
  "style": "messaging",
  "bigText": null,
  "messages": [
    { "sender": "Juan Perez", "isMe": false, "text": "Hey, are we still on for lunch?", "ts": 1755763100000, "hasImage": false, "imageTransferId": null, "imageMimeType": null },
    { "sender": "Juan Perez", "isMe": false, "text": "Sticker", "ts": 1755763150000, "hasImage": true, "imageTransferId": "b1c2d3e4-...", "imageMimeType": "image/webp" },
    { "sender": "", "isMe": true, "text": "Sí, ahí estaré", "ts": 1755763180000, "hasImage": false, "imageTransferId": null, "imageMimeType": null }
  ],
  "isGroupConversation": false,
  "inboxLines": null,
  "hasImage": false,
  "imageTransferId": null,
  "hasAvatar": true,
  "avatarTransferId": "9c2a1e30-...",
  "accentColor": null,
  "hasProgress": false,
  "progress": 0,
  "progressMax": 0,
  "progressIndeterminate": false,
  "postedAt": 1755763200000,
  "isOngoing": false,
  "isClearable": true
}
```

- `packageName` — **not always a bare Android package name.** For a Dual Messenger/cloned second instance of an app (same package, different Android user profile), this is instead a composite `"<packageName>#<profileUserId>"` (see `AppIdentity.kt`) so the clone gets its own independent identity everywhere — filter state, icon cache key, and this field — while the primary profile's entries stay a bare package name for backward compatibility. Split on `#` to recover the real package name for OS-level calls (`AppIconLoader`, etc.); never assume this field is a queryable package name as-is.

- `key` — exactly Android's `StatusBarNotification.key`. Used as the sole identifier for delete operations. Notification keys are surprisingly not always stable across OEM updates to the same conversation (see risk log in the plan) — treat as best-effort, not a database primary key.
- `style` — one of `"plain" | "bigText" | "bigPicture" | "messaging" | "inbox"`. Determines which of the style-specific fields (`bigText`, `messages`, `inboxLines`) are populated; the others are `null`. If extraction fails to recognize the notification's template, fall back to `style: "plain"` with just `title`/`text` populated — never omit the message or crash.
- `isGroupConversation` — only meaningful when `style == "messaging"`. Mirrors `Notification.EXTRA_IS_GROUP_CONVERSATION`. The PC shows a sender prefix on every line for a group conversation (several possible senders); for a 1:1 conversation it only starts showing one once at least one message in the list has `isMe: true` — until the user has actually replied, every message is from the one contact already identified by the notification's own header, and repeating their name on every line would be pure noise. It's also used as the fallback name for a message with no attributed sender at all: the conversation's own title (1:1 — there's only one other participant, it can only be them) versus left blank (group — which of several participants it was can't be guessed).
- `messages[].sender` / `messages[].isMe` — `sender` is whatever literal name (if any) the source app attributed the message to; apps vary in whether they bother setting this for the local user's own messages (some write "Tú"/"You", most leave it empty). `isMe` is NotiLink's own normalized signal instead: `true` when Android's `Message.getPerson()` is unset (the documented MessagingStyle convention for "this is the local user's own message") or matches `EXTRA_MESSAGING_PERSON` (the Person the app registered as "the local user"), computed the same way regardless of what each app's `sender` text says. The receiver should attribute by `isMe` (always showing a canonical "Tú"/"You" label when true) rather than trusting `sender` for that case, so every app's messages are labeled consistently.
- `hasImage` / `imageTransferId` — the notification's single "big" image, populated for `style == "bigPicture"` only. For `style == "messaging"`, images live per-message instead (see below) since a batch of unread messages can each carry their own photo/sticker and all of them should be shown, not just the latest.
- Each entry in `messages[]` may itself carry `hasImage`/`imageTransferId`/`imageMimeType` — chat apps attach photos/stickers to the individual `MessagingStyle.Message` as a content URI, not as a separate bigPicture notification, and a single notification update can bundle several unread messages (each with its own optional image) that should render stacked in order, not clobber each other. `imageMimeType` is `"image/jpeg"` for a downscaled static photo, or the original `"image/gif"`/`"image/webp"` when the source is an animated sticker — those are forwarded as the untouched original bytes (no re-encode, animation would be lost) rather than flattened to a static frame, so the receiver must use this field to label the data it got rather than assuming JPEG.
- `hasAvatar` / `avatarTransferId` — the sender/contact's profile photo (Android's notification "large icon"), distinct from the app's own launcher icon. Rendered as the primary avatar with the small app icon as a corner badge, matching how WhatsApp's own notifications look. May be `true` alongside any style.
- **Binary frame order**: when any of the above `hasImage`/`hasAvatar` flags are `true`, their binary (`0x02`) frames follow the JSON envelope immediately, back-to-back, in this fixed order: avatar first (if present), then the top-level `bigPicture` image (if present), then each message's image in array order (for any `messages[i].hasImage == true`). A receiver reads binary frames in that same order and pairs each one with the next pending transferId in the sequence.
- `isOngoing` / `isClearable` — mirrors Android's own flags; the PC UI may choose to hide the delete "×" for non-clearable notifications, but this is a UI decision, not a protocol requirement.
- `hasProgress` / `progress` / `progressMax` / `progressIndeterminate` — mirrors `Notification.EXTRA_PROGRESS`/`EXTRA_PROGRESS_MAX`/`EXTRA_PROGRESS_INDETERMINATE` verbatim, the same standard API that drives a download/upload's native progress bar in Android's own shade (Play Store installs, Chrome downloads, WhatsApp sending a video, ...). `hasProgress` is true only when the app actually set `Notification.Builder.setProgress(...)` for this notification — `progress`/`progressMax` are meaningless (`0`) otherwise. `progressIndeterminate` means "still preparing, no real percentage yet" (e.g. Play Store's initial "Descargando..." before the size is known) — show a moving/pulsing bar, not a 0% one.
- `postedAt` — deliberately NOT always `StatusBarNotification.postTime`: that field is the wall-clock moment the OS last saw `notify()` called for this key, which can get bumped by an update that doesn't represent new real-world content (confirmed from a real report — a notification from the night before showed a "this morning" time because of this). Priority order: the last message's own timestamp for `style == "messaging"` (most granular, set per-message by the chat app itself) → `Notification.when` (what the app explicitly asked its own shade entry to show) → `postTime` as a last resort.
- `actions` — the notification's own `Notification.Action[]`, verbatim (not invented): `[{ "title": "Responder", "hasReplyInput": true, "replyChoices": ["Sí", "No", "Tal vez"] }, { "title": "Marcar como leído", "hasReplyInput": false, "replyChoices": [] }]`. Array index is the identifier used by `action.invoke` below — both sides derive it from the same `StatusBarNotification.actions` snapshot, so it's stable for the lifetime of that notification instance. `[]` if the notification declares none. `replyChoices` mirrors `RemoteInput.getChoices()` — canned quick-reply suggestions some apps offer alongside free-text typing (Gmail's "Sí"/"No"/"Tal vez" being the common example); always `[]` when `hasReplyInput` is false or the app didn't provide any. Tapping one should send it exactly like a typed reply — same `action.invoke` with that string as `replyText`, no special-cased message type.
- `accentColor` — mirrors `Notification.color`, the app's own requested accent color, as `"#RRGGBB"` — `null` when the app didn't request one (`Notification.COLOR_DEFAULT`, which is transparent/0, not a real color to forward).

### `notification.removed` (phone → PC)
Sent when a notification disappears on the phone for any reason — user swiped it away, the source app cancelled it, etc.

```json
{ "key": "0|com.whatsapp|123|null|10123" }
```

### `notification.sync` (phone → PC)
Sent once, right after the resync burst of `notification.posted` messages on every fresh
connection (a real (re)pairing or an ordinary reconnect — see the connection lifecycle below).
`keys` is the *complete* current set of mirrored, active notification keys on the phone right now.

```json
{ "keys": ["0|com.whatsapp|123|null|10123", "0|com.whatsapp|124|null|10123"] }
```

Exists specifically to reconcile a gap `notification.removed` alone can't cover: if a notification
gets dismissed on the phone *while it's disconnected from the PC* (confirmed real case — another
app briefly switching the phone's network, e.g. Quick Share), that removal event has no connection
to travel over and is simply lost — the PC never finds out. Without this, that card sits on the PC
forever with no way to clear it (the phone has no record of that key any more, so `delete.request`
for it is a silent no-op). The PC should remove any notification it's currently showing for that
device whose key is *not* in this list — except synthetic PC-only cards (`share-`-prefixed: shared
text/files from the phone), which the phone was never going to know about in the first place and
must never be touched by this reconciliation.

### `delete.request` (PC → phone)
User clicked delete on a single card in the PC app.

```json
{ "key": "0|com.whatsapp|123|null|10123" }
```

Phone calls `NotificationListenerService.cancelNotification(key)`. This is a real cancel — it also removes the notification from the phone's own shade, not just from PC's view.

### `delete.request.all` (PC → phone)
```json
{}
```

Phone iterates its own list of currently-active notifications that are **currently mirrored** (i.e., passed the app filter and were actually sent to PC) via `getActiveNotifications()`, and calls `cancelNotification(key)` on each individually. **Never** call the blunt `cancelAllNotifications()` API — that would also dismiss unrelated, never-mirrored system notifications (ongoing music playback, navigation, etc.).

### `action.invoke` (PC → phone)
User clicked one of the notification's own action buttons (e.g. WhatsApp's "Responder"/"Marcar como leído"/"Silenciar") — this fires that exact `Notification.Action`'s real `PendingIntent`, nothing custom-built.

```json
{ "key": "0|com.whatsapp|123|null|10123", "actionIndex": 0, "replyText": "Ahí voy" }
```

`replyText` is `null` for actions without a reply input (e.g. "Marcar como leído" — just fires the `PendingIntent` as-is); for an action where `actions[actionIndex].hasReplyInput` was `true`, `replyText` carries what the user typed and the phone attaches it via `RemoteInput.addResultsToIntent()` before firing. If the notification has since been dismissed/updated and `key`/`actionIndex` no longer resolve to a live action, the phone silently no-ops (the PendingIntent is simply gone) rather than erroring.

### `notification.open` (PC → phone)
User clicked "Abrir" on a card — the real tap-through behavior a touch on the phone would trigger, not a generic app launch. Firing `Notification.contentIntent` directly is confirmed blocked by Android's background-activity-start restrictions for a third-party app (see CLAUDE.md's "abrir la notificación" investigation), so this goes through `NotiLinkAccessibilityService` instead: opens the notification shade and simulates a real tap on the matching entry, the same mechanism Microsoft's Phone Link is believed to use (confirmed only that it uses an AccessibilityService at all, per Microsoft's own docs — the exact matching approach here is this app's own reconstruction).

```json
{ "key": "0|com.whatsapp|123|null|10123" }
```

Requires a separate, optional permission (Settings → Accessibility) beyond the ones NotiLink otherwise needs. If the screen is off/locked when this arrives, the phone wakes it first (`NotiLinkAccessibilityService.wakeScreen`, a real screen-power-on via `PowerManager.WakeLock` — unrelated to starting an Activity, so none of the background-activity-start restrictions above apply to it) and gives the shade-matching retries much more time, since a locked device also needs room for the user to actually enter their PIN/pattern — Android's own tap-a-locked-notification handling takes it from there once satisfied. Matching the right notification in the shade is a best-effort text search (see `NotiLinkAccessibilityService.clickMatchingNode`), not a guaranteed-unique lookup — two simultaneous notifications with identical title AND body from the same app could match the wrong one.

### `notification.open.result` (phone → PC)
Always sent after a `notification.open`, success or failure — drives a toast on the PC (same "🖥️ "/"⚠️ " convention as screen share's own status toasts), since a silent no-op left no way to tell a granted-but-not-found case apart from the permission never having been granted at all.

```json
{ "success": false, "reason": "accessibility_not_granted" }
```

`reason` (only present when `success` is `false`) is `"accessibility_not_granted"` (the accessibility service isn't running) or `"not_found"` (it ran, but no notification matching the title/text was still around to click by the time the retries gave up).

### `media.state` (phone → PC)
Pushed whenever the phone's active media session changes (song, play/pause, or a different app takes over) — mirrored from Android's `MediaSessionManager`, available to NotiLink specifically because it's an enabled notification listener (a "trusted listener" per Android's own media APIs), no extra permission needed. Drives the PC's persistent "now playing" bar (deliberately not a notification card).

```json
{
  "active": true,
  "packageName": "com.spotify.music",
  "appLabel": "Spotify",
  "title": "Song name",
  "artist": "Artist name",
  "album": "Album name",
  "isPlaying": true,
  "position": 45000,
  "duration": 210000,
  "canPlayPause": true,
  "canNext": true,
  "canPrevious": true,
  "canFavorite": true,
  "isFavorite": false,
  "canShuffle": true,
  "isShuffleOn": false,
  "isSmartShuffle": false,
  "canSeek": true,
  "albumArtTransferId": "e5f6a7b8-..."
}
```

`active: false` (all other fields omitted) means no media session is currently active — the PC hides the bar. `canPlayPause`/`canNext`/`canPrevious`/`canSeek` come straight from the session's own declared `PlaybackState` actions bitmask, so a control never shows enabled unless the app itself says it's usable right now. `canFavorite`/`isFavorite` and `canShuffle`/`isShuffleOn` both come from the session's `PlaybackState.customActions` instead — there's no standard platform action for either toggle on the raw platform classes (only the AndroidX compat wrapper has one, which this project doesn't depend on), so apps expose them as custom actions with their own ids/labels. NotiLink finds favorite by matching the action's human-readable name against favorite-ish keywords, and infers its current state from whether that name reads as "remove from..." vs. "add to.../like". Shuffle is matched by "shuffle"/"aleatorio" in the action id or name instead — confirmed against a real device that Spotify's shuffle action has a *static* name that doesn't describe state, so its current-state signal comes from the action id itself (e.g. `TURN_SHUFFLE_OFF` means shuffle is currently on). `isSmartShuffle` is Spotify's "modo aleatorio inteligente" — confirmed from a real device log that it's the *same* custom action slot as regular shuffle (never both at once), distinguished only by a `SMART_` infix in the action id (`TURN_SMART_SHUFFLE_OFF` vs `TURN_SHUFFLE_OFF`) — the human-readable name is identical either way, so unlike everything else here this can only be read from the id, never the name. All three are best-effort, not a guaranteed-accurate signal for every app. `albumArtTransferId` is present only when the session has cover art; its `0x02` binary frame (JPEG, downscaled the same 512px/q80 way as every other image) follows immediately, same convention as everywhere else in this protocol. Only ever one session is reported at a time — whichever is currently `PLAYING`, falling back to the first active one otherwise.

### `media.control` (PC → phone)
A tap (or drag, for seeking) on the now-playing bar's controls — fires the session's own real `MediaController.TransportControls` (or `sendCustomAction` for favorite/shuffle), never a fake/local-only toggle.

```json
{ "action": "playPause" }
```
```json
{ "action": "seek", "position": 45000 }
```

`action` is one of `"playPause" | "next" | "previous" | "favorite" | "shuffle" | "seek"`. `position` (milliseconds) is only read for `"seek"`. No-ops if there's no active session (or, for `"favorite"`/`"shuffle"`, no detected custom action for it; for `"seek"`, if `canSeek` was `false`).

### `media.open` (PC → phone)
The now-playing bar's own "abrir en el dispositivo" button. No payload — there's no notification key for a media session the way there is for a mirrored card, so the phone instead looks up the currently-playing app's own real "now playing" notification by package name (`MediaSessionWatcher.activePackageName()`) and opens that through the exact same `NotiLinkAccessibilityService` flow as `notification.open` — that notification is still posted and still in `activeNotifications` even though it's excluded from normal mirroring (see `EXTRA_MEDIA_SESSION` filtering in `NotiLinkNotificationListener`). Reports back via the same `notification.open.result`.

```json
{}
```

### `share.request` (phone → PC)
Sent when the user taps "🖥️ Compartir pantalla" or "🔊 Compartir audio" on the phone's persistent connection-status notification (see ShareActionReceiver) — lets a screen/audio share be started from the phone, not just by clicking around on the PC.

```json
{ "type": "screen" }
```

`type` is `"screen"` or `"audio"`. Screen share started this way uses whatever quality settings are already saved on the PC (app.js's own `settings.screen*` fields, the same ones the "Compartir pantalla" confirmation popup edits) — it skips that popup entirely, since walking over to the PC to click "Iniciar" would defeat the point of asking from the phone.

### `share.stop.request` (phone → PC)
Sent when the user taps "⏹ Detener" on the phone's sharing-status notification (see below). No payload. Stops whatever's actually active — screen, audio, or both if both happen to be running at once.

### `share.status` (PC → phone)
Pushed whenever a screen and/or audio share starts or stops, to whichever phone it belongs to — drives a dedicated "Compartiendo pantalla/audio con la PC" notification on the phone (separate from the always-on connection-status one) with its own "⏹ Detener" action. Only one notification ever shows even if both are active at once — the phone doesn't distinguish between "screen share's own bundled audio" and "the separate audio-only feature", it just reflects whether each of the two underlying scrcpy sessions is running.

```json
{ "screenSharing": true, "audioSharing": false }
```

Both `false` means nothing is sharing — the phone cancels the notification rather than showing it empty.

### `call.state` (phone → PC)
Sent the moment Android's own Telecom framework shows a call session, detected via its `com.android.server.telecom` entry appearing in `MediaSessionManager.getActiveSessions()` (see `MediaSessionWatcher.checkCallActive`) rather than any posted notification. Confirmed directly from a real WhatsApp call: WhatsApp itself never posts a notification while ringing (only a missed-call one afterward, once it's over) — but Telecom's own session appeared for the exact duration of the call and nowhere else. This covers native SIM calls AND VoIP app calls (WhatsApp, Telegram, etc. via self-managed `ConnectionService`) with a single mechanism and no extra permission, since it rides on the same `NotificationListenerService`-granted media session access already used for the now-playing bar.

```json
{ "state": "active" }
```
```json
{ "state": "ended" }
```

Deliberately minimal, and deliberately honest about its limits: this layer exposes no title, no direction (incoming vs. outgoing), and no ringing-vs-answered distinction — Telecom's session for a call always reports `null` for both. The PC can only learn whether a call is happening right now, not who's calling or who started it (there's no way to tell an incoming call from one the phone's own user just placed from this signal alone — see `MediaSessionWatcher.checkCallActive`'s doc comment for why). Sent on both edges — `active` when Telecom's call session appears, `ended` when it disappears — driving the PC's own call banner (shown for the actual duration of the call, not a fixed toast timeout). Calls that DO post a real notification while ringing (native SIM calls, typically) already get full caller photo/name and real Contestar/Rechazar actions through the normal `notification.posted` pipeline instead — this message only covers the gap where nothing else is available.

### `battery.update` (phone → PC)
```json
{
  "level": 76,
  "charging": true,
  "lowPowerMode": false,
  "wifiSsid": "MyHomeWifi",
  "wifiLevel": 3,
  "cellular": [
    { "carrierName": "Telcel", "level": 4 },
    { "carrierName": "AT&T", "level": 2 }
  ]
}
```

Sent on connect, and thereafter only when `level` changes by ≥1% or `charging`/`lowPowerMode` flips — not on a fixed timer.

- `wifiSsid` / `wifiLevel` (0-4) — `null` unless the phone granted `ACCESS_FINE_LOCATION` (Android gates Wi-Fi SSID/signal behind location permission). Omitted gracefully, never blocks the rest of the payload.
- `cellular` — one entry per **active SIM** ("línea"), so a dual-SIM phone reports two independent signals with their own carrier name. `[]` if `READ_PHONE_STATE` wasn't granted, unsupported on the API level (<28), or no SIM is active.

### `volume.state` (phone → PC)
`STREAM_MUSIC` specifically — the stream a remote volume slider is meant for. Sent on connect and whenever it changes, including from the phone's own hardware buttons (via the undocumented-but-de-facto-stable `android.media.VOLUME_CHANGED_ACTION` broadcast — no public SDK constant exists for it, matched by raw string).

```json
{ "level": 5, "max": 15 }
```

### `volume.set` (PC → phone)
```json
{ "level": 8 }
```

Sets `STREAM_MUSIC` to an absolute value via `AudioManager.setStreamVolume(..., FLAG_SHOW_UI)` — the same native volume overlay a hardware button press would show appears on the phone too. No-ops if `level` is missing or negative.

### `dnd.state` (phone → PC)
Sent on connect and whenever the phone's interruption filter changes for *any* reason (this app's own `dnd.set`, the phone's Quick Settings tile, a Bedtime Mode schedule, ...) — `NotificationManager.ACTION_INTERRUPTION_FILTER_CHANGED`.

```json
{ "active": true }
```

`active` is `true` for *any* filter other than `INTERRUPTION_FILTER_ALL` (not just the specific mode `dnd.set` turns it on with below) — Do Not Disturb set to alarms-only or total silence directly on the phone still counts as "on" for the PC button's lit state.

### `dnd.set` (PC → phone)
```json
{ "active": true }
```

Requires the phone user to have separately granted **Notification Policy Access** (`Settings.ACTION_NOTIFICATION_POLICY_ACCESS_SETTINGS`) — a genuinely different "special access" grant from ordinary Notification Access, confirmed via `NotificationManager`'s own docs, not implied by it. Silently no-ops without it (`isNotificationPolicyAccessGranted()` is checked first so this at least logs why). `active: true` sets `INTERRUPTION_FILTER_PRIORITY` (the same mode the phone's own Quick Settings Do Not Disturb tile defaults to); `active: false` sets `INTERRUPTION_FILTER_ALL`.

### `connection.toggle` (PC → phone)
PC-initiated disconnect (there is no PC-initiated *reconnect* — once the socket is closed there's no channel left; only the phone itself, via this same toggle sent from its own persistent notification, can reconnect).

```json
{ "disconnect": true }
```

Phone sets `ConnectionStatus.manuallyDisconnected` and, if `disconnect: true`, closes the socket immediately. While that flag is set, the phone's own reconnect/heartbeat loop skips entirely — it won't silently reconnect on the next notification.

### `device.ring` (PC → phone)
"Buscar celular" — rings + vibrates for a few seconds (Android's own default ringtone + a vibration pattern), the same idea as Android's Find My Device.

```json
{}
```

### `wallpaper.data` (phone → PC)
JSON message immediately followed by a `0x02` binary frame (JPEG, downscaled to a 900px max edge, quality 70 — this is shown heavily blurred at low opacity on the PC, so it doesn't need to be high-fidelity). Sent once per fresh connection and again whenever `ACTION_WALLPAPER_CHANGED` fires. Best-effort: requires `READ_EXTERNAL_STORAGE`/`READ_MEDIA_IMAGES` on the phone (some OEM builds gate `WallpaperManager` on the legacy permission even past API 33) — silently omitted if denied.

```json
{ "transferId": "e5f6a7b8-..." }
```

### `share.send` (bidirectional)
Text/link sharing between the two apps.

```json
{ "text": "https://example.com/article" }
```

- **Phone → PC**: sent by `ShareToPcActivity`, the Android share-sheet target ("Enviar a la PC" — shows up next to WhatsApp/Gmail/etc. whenever the user taps Share elsewhere on the phone). PC renders it as a synthetic notification card in the list (`AppLabel: "NotiLink"`, using NotiLink's own icon) exactly like a real mirrored notification — including running it through the existing link-preview fetch if the text is a URL.
- **PC → phone**: sent from the "Compartir al celular" panel in the device menu. If the text is a URL, `MainViewModel.ShareToDevice` fetches its Open Graph title/description (`LinkPreviewService`, the same one the PC's own cards use) and, if it has a preview image, downloads that too (capped at 4MB) and sends it as a binary frame right after — the JSON payload then also carries:
  ```json
  { "text": "...", "title": "Article title", "description": "First line...", "transferId": "e5f6a7b8-..." }
  ```
  `transferId` is present only when an image binary frame follows. Phone posts a native notification (channel `notilink_shared`) immediately from the text/title/description (so the user isn't kept waiting on the image fetch), then patches in `BigPictureStyle` once the image frame arrives, if any — tapping it opens the link directly (`ACTION_VIEW`) if the text looks like a URL, otherwise opens the app. Includes a "Copiar" action either way (`CopyToClipboardReceiver`).

### `file.send` (bidirectional)
JSON message immediately followed by a `0x02` binary frame carrying the raw file bytes, unmodified — no recompression/downscaling, so quality/fidelity is always preserved exactly. Capped at 8MB (`TcpServer.MaxFrameLength`/`NotificationConnection.MAX_FRAME_LENGTH`) — a larger file is rejected client-side before ever attempting the send, with a message explaining why, rather than silently failing partway through.

```json
{ "fileName": "photo.jpg", "mimeType": "image/jpeg", "size": 483920, "transferId": "e5f6a7b8-..." }
```

- **Phone → PC**: sent by `ShareToPcActivity` when the shared content is a file rather than text (the same "Enviar a la PC" share-sheet target as `share.send`, mimeType `*/*`). PC saves it to the real Windows Downloads folder (`%UserProfile%\Downloads`, de-duplicating `name (2).ext` style if it already exists there) and renders a synthetic notification card — an actual image thumbnail for image files, otherwise just the filename.
- **PC → phone**: sent from dragging a file onto the PC app's window (WebView2 hands real OS file drops to the page as standard HTML5 DragEvents — see app.js's `drop` handler) after confirming in the preview modal that appears. Phone saves it via `MediaStore.Downloads` (API 29+; older is unsupported here) and posts a native notification — an image file's own bytes are decoded straight into `BigPictureStyle` for a real thumbnail preview, anything without a sensible preview (PDF, zip, ...) just shows the filename, unchanged. Tapping it opens the file with whatever app handles its type.

### `diag.log` (phone → PC)
Checked every heartbeat tick (~8s) and sent only when its content changed since the last send — carries the phone's on-device rolling diagnostic log (DiagLog.kt), since USB/adb access to the phone isn't always available when something needs debugging but a live NotiLink connection almost always is.

```json
{ "text": "2026-08-22 10:03:11 [NotiLink/Listener] posted: pkg=com.whatsapp style=call category=call\n..." }
```

PC writes this to `%LocalAppData%\NotiLink\logs\<deviceName>_<deviceId>.log`, overwriting the file each time (the phone's own copy is capped at ~200KB and self-trims, so the PC's copy is always the latest full snapshot, not an ever-growing append).

### `app.sync` (phone → PC)
Sent once per connection (after handshake completes), and again whenever the phone's installed-app list changes (install/uninstall detected via `PACKAGE_ADDED`/`PACKAGE_REMOVED`).

```json
{
  "apps": [
    { "packageName": "com.whatsapp", "label": "WhatsApp", "allowed": true, "isSystemApp": false },
    { "packageName": "com.instagram.android", "label": "Instagram", "allowed": false, "isSystemApp": false }
  ]
}
```

`allowed` reflects the phone's currently persisted filter state (see `app.filter.update`). `isSystemApp` is `ApplicationInfo.FLAG_SYSTEM`/`FLAG_UPDATED_SYSTEM_APP` — drives the Todas/Instaladas/Sistema filter tabs on both apps' app-filter screens. This lets the PC settings UI render correct initial checkbox states without a separate "get filter" round trip.

### `app.filter.update` (PC → phone)
```json
{ "packageName": "com.instagram.android", "allowed": false }
```

Phone persists this immediately (DataStore) and applies it **before** any future `notification.posted` for that package is ever transmitted — filtering happens phone-side, not PC-side, so blocked-app content never leaves the device. If this flips an app from allowed→blocked, the phone must also emit `notification.removed` for every currently-mirrored notification from that package, so the PC view retroactively clears them too.

### `icon.request` (PC → phone)
```json
{ "packageName": "com.whatsapp", "transferId": "d4e5f6a7-..." }
```

### `icon.data` (phone → PC)
JSON message immediately followed by a `0x02` binary frame (PNG, app's launcher icon at its natural resolution — no downscaling needed, icons are small).

```json
{ "packageName": "com.whatsapp", "transferId": "d4e5f6a7-..." }
```

PC caches the resulting image to `%LocalAppData%\NotiLink\icons\<packageName>.png` and does not re-request it for that package again unless the cache file is missing.

## Sequencing on a fresh connection

1. TCP connect (phone → PC:45212).
2. Phone sends `handshake.hello`.
3. If token invalid/missing: PC sends `pair.code` → phone shows it, user confirms → phone sends `pair.confirm` → PC sends `pair.token`.
4. Phone sends `app.sync` (full installed-app inventory + current filter state).
5. PC requests any missing icons via `icon.request` for apps not yet in its disk cache; phone responds with `icon.data` + binary frame per request.
6. Phone sends `battery.update` (current state).
7. Phone sends `notification.posted` for each currently-active, allow-listed notification (so PC's view is populated on connect, not just going forward).
8. Normal operation: `notification.posted`/`notification.removed`/`battery.update` flow phone→PC as events occur; `delete.request`/`delete.request.all`/`action.invoke`/`app.filter.update` flow PC→phone as the user acts; `heartbeat` both directions every 15s. This requires the phone to keep *reading* the socket continuously, not just writing to it — the same persistent connection carries both directions.

## Discovery (pre-connection, UDP)

- Phone broadcasts a raw JSON packet (no framing header — this is UDP, not the TCP protocol above) to `255.255.255.255:45211`:
  ```json
  { "type": "notilink.discover", "deviceName": "Pixel 7" }
  ```
- PC listens on UDP `45211`; on receipt, replies unicast to the sender's address:
  ```json
  { "type": "notilink.announce", "pcName": "DESKTOP-ABC123", "tcpPort": 45212 }
  ```
- Phone then opens the TCP connection to that PC's IP on `tcpPort` and proceeds with `handshake.hello` as above.
- **Manual fallback (mandatory, not optional):** PC settings screen displays its own LAN IP and port for manual entry on the phone, since UDP broadcast is silently dropped on some guest/mesh/AP-isolated Wi-Fi networks.

## Explicitly out of scope for v1 (do not implement yet)

- No TLS/encryption on the TCP stream — the 6-digit pairing code + persisted token is the only trust mechanism. Acceptable for a home LAN MVP; flagged as a known trade-off, not an oversight. A future `v = 2` could add TLS without breaking `v = 1` clients, since `v` is carried in every envelope.

### `call.control` (PC → phone)

Answer or end the call currently ringing/in progress, for calls that have no notification of
their own (the bare Telecom signal in `call.state`).

```json
{ "action": "answer" | "hangup" | "decline" }
```

`hangup` and `decline` are the same request (end the current call). The phone tries
`TelecomManager.acceptRingingCall()` / `endCall()` first (needs `ANSWER_PHONE_CALLS`; effective for
SIM calls, a no-op for another app's self-managed VoIP call such as WhatsApp), then falls back to
the Accessibility service pressing the matching button on the on-screen call UI.

### `call.control.result` (phone → PC)

```json
{ "action": "answer" | "hangup", "success": true, "method": "telecom" | "accessibility" | "no_permission" | "not_found" }
```

`method` names the layer that worked when `success`, or the reason when not.


### `call.status` (phone → PC)

Sent while a call is active (`call.state`), about every 1.5 s but only when something changed.

```json
{ "answered": true, "elapsedSeconds": 7, "speaker": false, "muted": false }
```

`answered` flips to `true` when the call screen's own running timer first appears on the phone
(read via the Accessibility service) — i.e. the other side actually picked up, for a SIM call or a
VoIP app alike. `elapsedSeconds` is that timer's value at that moment (only present in the message
that reports the transition). `speaker`/`muted` are the phone's real audio state.

`call.control` also accepts `"speaker"`, `"mute"` and `"hold"` (Accessibility-only: presses the
matching on-screen button); `call.control.result.action` echoes them.


### `rotation.state` (phone → PC) / `rotation.set` (PC → phone)

```json
{ "active": true }
```

`active` = auto-rotate on (`Settings.System.ACCELEROMETER_ROTATION == 1`). `rotation.state` is sent
on connect and whenever the setting changes (any source). `rotation.set` needs the phone's
"Modificar ajustes del sistema" (`WRITE_SETTINGS`) grant; without it the phone ignores it.


### `share.status.apps` / `share.stop.app`

`share.status` (PC → phone) also carries `apps: [{ "packageName", "label" }]` — every app the PC is
currently showing on its own virtual display ("Solo una app"). The phone keeps one ongoing
notification per app with a "Detener transmisión" action, which sends:

```json
// share.stop.app (phone → PC)
{ "packageName": "com.whatsapp" }
```

The phone's screen/audio notification ("share.stop.request") now stops only the full-screen and
audio sessions; per-app sessions are stopped from their own notifications or from the PC.


### `handshake.hello.pairingTokens`

`handshake.hello` also carries `pairingTokens: ["…", …]` — every token the phone has been issued
(newest first, up to 5), so a phone paired with several PCs is recognized by each of them. The PC
accepts the connection if any of them is the one it stored for that deviceId. `pairingToken` is
still sent (the newest) for older PC versions.


### `pc.bye` (PC → phone)

```json
{ "reason": "exit" | "shutdown" | "logoff" | "sleep" }
```

Sent right before NotiLink closes on the PC (tray "Salir"), Windows shuts down/restarts/signs out,
or the PC goes to sleep. The phone enters quiet mode: it removes its own NotiLink notifications
(connection status, sharing) and retries the connection only every 30 s, silently. The first
successful reconnect leaves quiet mode. The phone also enters quiet mode by itself after 2 minutes
without a connection (power cut, crash, Wi-Fi lost) — unless the user disconnected manually.


### `phone.prefs` (PC → phone)

```json
{ "statusAutoHideSeconds": 5 }
```

Hide the phone's connected/disconnected status notification this many seconds after each
connection change (0 = never hide). Sent when the setting changes in the PC's Ajustes and to every
phone when it connects. The phone stores it, so it keeps applying while offline.


### App share state / `share.app.return`

Each `share.status.apps[]` entry also has `state`: `"pc"` (showing in the PC window) or `"phone"`
(the user opened the same app on the phone's own screen, so the PC window is empty). The PC
watches this itself (`dumpsys activity activities`) and, if the app disappears entirely (closed
from Recents), stops that share. The phone's per-app notification offers "Mostrar en la PC" while
`state == "phone"`, which sends:

```json
// share.app.return (phone → PC)
{ "packageName": "com.whatsapp" }
```


### Phone-initiated sharing (app "Acciones" screen)

- `share.request` (phone → PC) `{ "type": "screen" | "audio" | "app", "packageName"?, "label"? }` —
  `"app"` opens that app on its own virtual display with the PC's saved app-window options.
- `share.stop.screen`, `share.stop.audio`, `share.stop.all` (phone → PC, empty payload) stop the
  full-screen share, the audio share, or everything this phone is sharing. (`share.stop.request`
  still stops screen + audio, for the older notification button.)

### `share.recents` (both directions)

`{ "apps": [{ "packageName", "label" }], "updatedAt": <ms epoch> }` — the "Recientes" list for
quick app sharing (newest first, max 4), shared by the phone app, its home-screen widget and the
PC. Whoever changes it sends the whole list; the phone also sends it on every fresh connection.
The receiver adopts it if `updatedAt` is newer than its own, answers with its own list if older,
and ignores it if equal. The first time the PC sees it (no timestamp of its own yet) it merges both
lists, phone's first, and sends the result back.

### `phone.prefs` — accent color

`phone.prefs` may also carry `"accent": "#RRGGBB"`: the PC's current accent color (its Ajustes →
Color de acento, or today's color when "Un tono por día" is on). The phone stores it and re-skins
its own UI with it (AccentTheme). Sent on every connection and whenever the PC's accent changes.

### Reaching the PC from another network (Tailscale)

- `pc.addresses` (PC → phone) `{ "addresses": [{ "ip", "kind": "lan" | "vpn", "adapter" }] }` —
  every IPv4 of the PC (virtual adapters excluded); `vpn` = Tailscale/ZeroTier (100.64.0.0/10),
  reachable from anywhere. Sent on connect and whenever the PC's network changes. When the saved
  IP stops answering, the phone tries these (LAN first, then VPN) before broadcasting again.
- `phone.network` (phone → PC) `{ "transport": "wifi" | "cellular" | "ethernet" | "vpn" | ...,
  "vpn": bool, "metered": bool }` — sent on connect and on every network switch (which also makes
  the phone reconnect at once). On a metered network the phone sends smaller images and no
  wallpaper, and the PC lowers screen-sharing quality (≤1024 px, ≤30 fps, ≤4 Mbps).

### Now-playing waves "al ritmo de la música"

- `media.levels.request` (PC → phone) `{ "on": bool }` — sent while the PC's now-playing bar is
  visible and playing (and again on reconnect); `false` stops it.
- `media.levels` (phone → PC) `{ "b", "m", "t" }` — bass / mid / treble levels 0..1 of what the phone
  is playing (Android Visualizer on the output mix, needs RECORD_AUDIO; analysis only, nothing is
  recorded), ~15 per second, never on metered networks. The PC also measures its own output
  (WASAPI loopback) for music playing on the PC; the waves follow whichever is louder.

## PC ↔ PC ("PCs vinculadas")

Two NotiLink PCs can link to each other to exchange chat messages, clipboard text, files of any
size and a "llamar la atención" nudge. Implemented by `PeerLinkService` (windows/NotiLink.Core).

**Transport.** Same TCP port as the phones (`45212`), same framing and JSON envelope. A connection
whose **first frame is `peer.hello`** (instead of `handshake.hello`) is another PC: `TcpServer`
hands the socket to `PeerLinkService` and stops reading it. Reusing the port means any firewall
rule / Tailscale setup that already lets a phone in works for another PC too. The link is
symmetric: either PC may dial the other, and both redial known peers (~every 30 s) while
disconnected. If two links exist at once, both sides keep the same one: same initiator → the
newer wins; otherwise the link dialed by the lexicographically lower `pcId` wins.

**Discovery.** A PC finds others by sending the normal `notilink.discover` broadcast (plus each
adapter's directed broadcast) to UDP `45211`. `notilink.announce` now also carries `"pcId"` (the
stable id below); the PC ignores announces without one (older builds) and its own.

**Identity.** Each PC has a random, permanent `pcId` (`%LocalAppData%\NotiLink\pc-id.txt`).
Linked PCs and their shared token live DPAPI-encrypted in `paired-pcs.dat`.

### Handshake and pairing

| Message | Direction | Payload |
|---|---|---|
| `peer.hello` | dialer → listener (first frame) | `{ "pcId", "name", "token": string \| null }` |
| `peer.welcome` | listener → dialer | `{ "pcId", "name" }` — token valid, linked |
| `peer.pair.required` | listener → dialer | `{ "pcId", "name" }` — unknown/invalid token; the listener now shows a 6-digit code |
| `peer.pair.code` | dialer → listener | `{ "code" }` — what the user typed |
| `peer.pair.ok` | listener → dialer | `{ "pcId", "name", "token" }` — both sides save the token, linked |
| `peer.pair.fail` | listener → dialer | `{ "reason": "wrong_code" \| "rejected", "attemptsLeft" }` — at 0 the socket is closed |

The code is shown **on the PC being added** and typed **on the PC that started "Añadir PC"**
(proves someone is physically at both). 3 attempts. The listener can also reject the request.
A background redial that gets `peer.pair.required` (the other PC forgot this one) stops redialing
and the UI shows "Hay que volver a vincularla".

### After linking (both directions)

- `peer.ping` `{}` — every 15 s. A link silent for 60 s is closed (and redialed).
- `peer.addresses` `{ "addresses": ["ip", ...] }` — sent on link; the receiver stores them (plus
  the address it actually sees) for redialing.
- `peer.message` `{ "id", "kind": "text" | "clipboard", "text", "at" }` — `clipboard` is also put
  on the receiving PC's clipboard.
- `peer.nudge` `{}` — receiving PC plays a sound, shows its window (restoring it if minimized,
  never moving or resizing it) and shakes the page content.
- `peer.unpair` `{}` — the other side forgot this PC; forget it too and close.
- Files:
  - `peer.file.offer` `{ "transferId", "fileName", "size" }`
  - `peer.file.chunk` `{ "transferId" }` **immediately followed by one binary frame** (≤ 512 KB)
    with the next bytes, in order. Chat messages may interleave between chunks.
  - `peer.file.done` `{ "transferId" }` — the receiver renames its `*.notilink-part` file to the
    final name in Downloads (` (2)`, ` (3)`... if taken).
  - `peer.file.cancel` `{ "transferId" }` — either side; the receiver deletes the partial file.
  A dropped connection fails every transfer in progress on it (no resume).
- Screen sharing (view only — no input is ever sent). "Sharer" = the PC whose screen is shown.
  - `peer.screen.request` `{}` — viewer → sharer: "show me your screen". If the sharer stored a
    standing permission for that PC (`AllowScreen`, set by "Permitir siempre"), it starts right
    away; otherwise the sharer's user is asked ("Permitir siempre" / "Permitir esta vez" /
    "Rechazar"; no answer in 60 s = rejected).
  - `peer.screen.denied` `{}` — sharer → viewer: the request was rejected.
  - `peer.screen.start` `{}` — sharer → viewer: frames follow; the viewer opens its window. Also
    sent unprompted when the sharer pushes its screen ("Compartir mi pantalla").
  - `peer.screen.frame` `{ "width", "height" }` **immediately followed by one binary frame**: a
    JPEG of the sharer's primary monitor (≤ 1920 px wide, cursor drawn in). Sent only when the
    screen or the cursor changed, at most 60 per second (30 on the GDI fallback).
  - `peer.screen.ack` `{}` — viewer → sharer, one per frame received. The sharer never has more
    than 2 frames unacknowledged per viewer and always sends that viewer the newest frame once it
    has room, so a slow link lowers the frame rate instead of building a backlog.
- Remote control (only while the sharer is sharing its screen with that viewer):
  - `peer.control.request` `{}` — viewer → sharer. Granted at once with a standing permission
    (`AllowControl`), otherwise the sharer's user is asked (same three answers as for the screen).
    Refused outright if no screen share to that viewer is running.
  - `peer.control.granted` `{}` / `peer.control.denied` `{}` — sharer → viewer.
  - `peer.input` — viewer → sharer, ignored unless control is granted:
    `{ "t": "move", "x", "y" }` (0..1 across the shared monitor),
    `{ "t": "down" | "up", "x", "y", "b": "left" | "right" | "middle" | "x1" | "x2" }`,
    `{ "t": "wheel", "d" }` (120 per notch), `{ "t": "key", "vk", "down" }` (Windows virtual-key
    code). The sharer injects them with `SendInput`.
  - `peer.control.stop` `{ "role": "viewer" | "sharer" }` — either side. Ending the screen share
    (or the link) ends control too; the sharer then releases any key/button left held.
  - `peer.screen.stop` `{ "role": "viewer" | "sharer" }` — the sender's side: a viewer closing its
    window, or a sharer ending it. A dropped link or `peer.unpair` ends both directions.
