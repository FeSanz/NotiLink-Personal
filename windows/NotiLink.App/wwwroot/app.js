// Rendering surface for NotiLink's whole UI — status header, notification
// list, theme toggle. All window.* functions below are called from the WPF
// host (MainWindow.xaml.cs) via ExecuteScriptAsync; every click in here is
// sent back to C# via window.chrome.webview.postMessage. Everything lives in
// this one page specifically so it all shares a single CSS theme system.
(function () {
  const listEl = document.getElementById('list');
  const connLabelDot = document.getElementById('conn-label-dot');
  const connLabelText = document.getElementById('conn-label-text');
  const batteryLabel = document.getElementById('battery-label');
  const wifiLabel = document.getElementById('wifi-label');
  const cellularLabel = document.getElementById('cellular-label');
  const screenShareBtn = document.getElementById('screen-share-btn');
  const audioShareBtn = document.getElementById('audio-share-btn');
  const screenShareStatsEl = document.getElementById('screen-share-stats');
  const volumeBtn = document.getElementById('volume-btn');
  const volumeMenu = document.getElementById('volume-menu');
  const volumeSlider = document.getElementById('volume-slider');
  const volumeLabel = document.getElementById('volume-label');
  const dndBtn = document.getElementById('dnd-btn');
  const rotationBtn = document.getElementById('rotation-btn');
  const deviceMenuBtn = document.getElementById('device-menu-btn');
  const deviceMenu = document.getElementById('device-menu');
  const deviceMenuRename = document.getElementById('device-menu-rename');
  const deviceMenuApps = document.getElementById('device-menu-apps');
  const deviceMenuWallpaper = document.getElementById('device-menu-wallpaper');
  const deviceMenuShare = document.getElementById('device-menu-share');
  const deviceMenuRing = document.getElementById('device-menu-ring');
  const deviceMenuPower = document.getElementById('device-menu-power');
  const deviceMenuRemove = document.getElementById('device-menu-remove');
  const deviceSegmentGroup = document.getElementById('device-segment-group');
  const themeFab = document.getElementById('theme-fab');
  const listCollapseFab = document.getElementById('list-collapse-fab');
  const deleteAllBtn = document.getElementById('delete-all-btn');
  const toastContainer = document.getElementById('toast-container');
  const wallpaperBg = document.getElementById('wallpaper-bg');
  const nowPlayingBar = document.getElementById('now-playing-bar');
  const nowPlayingSourceBtn = document.getElementById('now-playing-source-btn');
  const nowPlayingArt = document.getElementById('now-playing-art');
  const nowPlayingArtPlaceholder = document.querySelector('.now-playing-art-placeholder');
  const nowPlayingEq = document.getElementById('now-playing-eq');
  const nowPlayingTitle = document.getElementById('now-playing-title');
  const nowPlayingSubtitle = document.getElementById('now-playing-subtitle');
  const nowPlayingPrev = document.getElementById('now-playing-prev');
  const nowPlayingPlayPause = document.getElementById('now-playing-playpause');
  const nowPlayingPlayPauseIcon = document.getElementById('now-playing-playpause-icon');
  const nowPlayingNext = document.getElementById('now-playing-next');
  const nowPlayingFavorite = document.getElementById('now-playing-favorite');
  const nowPlayingShuffle = document.getElementById('now-playing-shuffle');
  const nowPlayingOpen = document.getElementById('now-playing-open');
  const nowPlayingProgressRow = document.getElementById('now-playing-progress-row');
  const nowPlayingElapsed = document.getElementById('now-playing-elapsed');
  const nowPlayingDuration = document.getElementById('now-playing-duration');
  const nowPlayingSeek = document.getElementById('now-playing-seek');
  const filterMessagingBtn = document.getElementById('filter-messaging-btn');
  const callBar = document.getElementById('call-bar');

  function showToast(message, duration = 3200) {
    const toast = document.createElement('div');
    toast.className = 'toast';
    toast.textContent = message;
    toastContainer.appendChild(toast);
    requestAnimationFrame(() => toast.classList.add('show'));
    setTimeout(() => {
      toast.classList.remove('show');
      setTimeout(() => toast.remove(), 300);
    }, duration);
  }

  // Every paired device gets its own isolated bucket of notifications/apps/battery —
  // only the "active" device (the one picked in the switcher) is ever rendered, so
  // two phones' data never blends together. General rendering settings (below) are
  // the one thing that stays outside this and applies to every device.
  const ACTIVE_DEVICE_KEY = 'notilink-active-device';
  const DEVICE_ORDER_KEY = 'notilink-device-order'; // [deviceId] — the user's order of the device tabs
  let devices = [];               // [{deviceId, name, connected}], from window.setDeviceList
  let activeDeviceId = localStorage.getItem(ACTIVE_DEVICE_KEY) || null;
  const deviceStates = new Map(); // deviceId -> { data: Map<key,item>, order: [keys], appList: [], battery: {level,charging} }

  // Linked PCs (see the PC ↔ PC section at the end) also get a tab in the switcher, after the
  // phones. Picking one swaps the list for that PC's activity cards; activeDeviceId keeps the
  // last phone so going back to a phone tab lands where it was. Declared up here (not with the
  // rest of the PC ↔ PC state) because renderDeviceSegments/renderAll read them.
  const ACTIVE_PEER_TAB_KEY = 'notilink-active-peer-tab';
  let peers = [];                 // [{pcId, name, connected, address, lastSeen, needsRepair}]
  let activePeerTab = localStorage.getItem(ACTIVE_PEER_TAB_KEY) || null;
  const peerChats = new Map();    // pcId -> [item], once its history was loaded
  const peerUnread = new Map();   // pcId -> count
  const peerTabActive = () => !!activePeerTab && peers.some(p => p.pcId === activePeerTab);

  function getDeviceState(deviceId) {
    if (!deviceStates.has(deviceId)) {
      deviceStates.set(deviceId, {
        data: new Map(), order: [], appList: [],
        battery: { level: null, charging: false },
        wifi: { ssid: null, level: null },
        cellular: [],
        wallpaperDataUrl: null,
        media: null, // current now-playing state for this device, or null — see setMediaState
        callActive: false, // raw Telecom-session signal for this device — see call.state/renderCallBar
        callAnswered: false, callAnsweredAt: null, callSpeaker: false, callMuted: false, // see window.onCallStatus
        callDeviceName: null,
        volume: { level: 0, max: 0 }, // STREAM_MUSIC — see window.setVolume
        dnd: false, // Do Not Disturb — see window.setDnd
        autoRotate: false, // see window.setRotation
        screenSharing: false, audioSharing: false, screenStats: '', // per-phone share sessions — see renderShareButtons
        appShares: new Set(), // packages open as single-app shares — see renderShareAppList
        appShareState: new Map(), // package -> 'pc' | 'phone' (opened on the phone meanwhile)
      });
    }
    return deviceStates.get(deviceId);
  }

  window.setWallpaper = function (deviceId, dataUrl) {
    getDeviceState(deviceId).wallpaperDataUrl = dataUrl;
    if (deviceId === activeDeviceId) applyWallpaper();
  };

  // --- Persistent "now playing" bar (Spotify, YouTube Music, ...) — a fixed spot between the
  // header and the list, not a notification card. Real transport controls only: prev/play-
  // pause/next post straight through to the phone's own MediaController, same as every other
  // action NotiLink relays (see docs/PROTOCOL.md's media.state/media.control). ---
  const PLAY_ICON = '<path fill="currentColor" d="M8 5v14l11-7z"/>';
  const PAUSE_ICON = '<path fill="currentColor" d="M7 5h4v14H7zm6 0h4v14h-4z"/>';

  window.setMediaState = function (deviceId, state) {
    // position/duration only ever arrive as a snapshot on a real state change, never streamed —
    // stamping when THIS snapshot was received is what lets updateNowPlayingProgress interpolate
    // a live-feeling tick between updates instead of the number sitting frozen until the next
    // song/play-pause/skip event actually pushes a fresh one.
    if (state) state.receivedAt = Date.now();
    getDeviceState(deviceId).media = state;
    if (deviceId === activeDeviceId) renderNowPlaying();
  };

  // --- "Now playing" source toggle: the exact same bar/controls can drive either the selected
  // phone's media session (default, unchanged behavior) or this PC's own local playback (Spotify
  // desktop, a browser tab, whatever Windows itself currently considers "the" session — see
  // Services/PcMediaService.cs). A plain preference, not per-device data: there's only one PC
  // regardless of which phone is selected in the device switcher, so it lives outside
  // deviceStates entirely. ---
  const NOW_PLAYING_SOURCE_KEY = 'notilink-now-playing-source';
  let nowPlayingSource = localStorage.getItem(NOW_PLAYING_SOURCE_KEY) === 'pc' ? 'pc' : 'phone';
  let pcMedia = null; // this PC's own local session, or null — see window.setPcMediaState

  window.setPcMediaState = function (state) {
    if (state) state.receivedAt = Date.now();
    pcMedia = state;
    if (nowPlayingSource === 'pc') renderNowPlaying();
  };

  function currentNowPlayingMedia() {
    if (nowPlayingSource === 'pc') return pcMedia;
    const state = activeState();
    return state && state.media;
  }

  function applyNowPlayingSourceUi() {
    const isPc = nowPlayingSource === 'pc';
    nowPlayingSourceBtn.classList.toggle('pc-mode', isPc);
    nowPlayingSourceBtn.title = isPc
      ? 'Controlando: PC (clic para cambiar al teléfono)'
      : 'Controlando: teléfono (clic para cambiar a la PC)';
  }
  applyNowPlayingSourceUi();

  nowPlayingSourceBtn.addEventListener('click', () => {
    nowPlayingSource = nowPlayingSource === 'phone' ? 'pc' : 'phone';
    localStorage.setItem(NOW_PLAYING_SOURCE_KEY, nowPlayingSource);
    applyNowPlayingSourceUi();
    renderNowPlaying();
  });

  function formatTrackTime(ms) {
    const totalSec = Math.max(0, Math.floor((ms || 0) / 1000));
    const hours = Math.floor(totalSec / 3600);
    const mins = Math.floor((totalSec % 3600) / 60);
    const secs = totalSec % 60;
    if (hours > 0) return `${hours}:${String(mins).padStart(2, '0')}:${String(secs).padStart(2, '0')}`;
    return `${mins}:${String(secs).padStart(2, '0')}`;
  }

  let nowPlayingProgressInterval = null;
  // True from the moment the user grabs the seek thumb until they release it — the ticking
  // interval below must never overwrite the slider's value while they're actively dragging it,
  // or every tick would yank it back to the actual playback position mid-drag.
  let nowPlayingSeekDragging = false;

  // ---- One UI–style waves over the played part of the bar ----
  // Three translucent layers of soft humps drawn on a canvas above the track, in the album art's
  // color (accent when there's none), moving while playing and settling flat when paused — the
  // look of One UI's media player progress bar. Pure decoration: the <input> underneath still
  // handles all seeking.
  const waveCanvas = document.getElementById('now-playing-wave');
  const waveCtx = waveCanvas.getContext('2d');
  const wave = { pct: 0, playing: false, amp: 0, color: null, raf: 0, lastT: 0,
    levels: { pc: null, phone: null }, band: [0, 0, 0], levelsOn: null };
  const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
  // One layer per band (back → front: bass, mids, highs). Each layer emits a crest from the left
  // edge every `every` seconds; the crest keeps the height (and width) the sound had at that
  // moment and travels right toward the thumb — a train of waves of different sizes rolling
  // left → right, like One UI's player. Bass: wide, slow, tall; highs: narrow, quick, frequent.
  const WAVE_LAYERS = [
    { alpha: 0.30, height: 1.00, every: 1.15, sigma: 17, speed: 36 },
    { alpha: 0.38, height: 0.82, every: 0.80, sigma: 12, speed: 48 },
    { alpha: 0.52, height: 0.62, every: 0.52, sigma: 9, speed: 62 },
  ];
  wave.crests = [[], [], []];
  wave.nextEmit = [0, 0, 0];

  function waveRgb() {
    const c = wave.color || getComputedStyle(document.documentElement).getPropertyValue('--accent').trim() || '#7C5CFF';
    const m = /^#?([0-9a-f]{6})$/i.exec(c);
    if (m) { const n = parseInt(m[1], 16); return [(n >> 16) & 255, (n >> 8) & 255, n & 255]; }
    const r = c.match(/\d+/g);
    return r ? r.slice(0, 3).map(Number) : [124, 92, 255];
  }

  function drawWave(now) {
    wave.raf = 0;
    const dt = Math.min(0.1, (now - (wave.lastT || now)) / 1000);
    wave.lastT = now;
    const target = wave.playing && !reduceMotion.matches ? 1 : 0;
    wave.amp += (target - wave.amp) * Math.min(1, dt * 3.5);

    const dpr = window.devicePixelRatio || 1;
    const cw = waveCanvas.clientWidth, ch = waveCanvas.clientHeight;
    if (waveCanvas.width !== Math.round(cw * dpr) || waveCanvas.height !== Math.round(ch * dpr)) {
      waveCanvas.width = Math.round(cw * dpr);
      waveCanvas.height = Math.round(ch * dpr);
    }
    waveCtx.setTransform(dpr, 0, 0, dpr, 0, 0);
    waveCtx.clearRect(0, 0, cw, ch);

    // The range thumb (13 px) travels between 6.5 px and width − 6.5 px; stop just before it.
    const end = Math.max(0, 6.5 + (cw - 13) * wave.pct / 100 - 3);
    // Live band levels (bass/mid/treble) from wherever the music is playing: the PC's own output
    // or the phone's visualizer — whichever is fresh and louder. Smoothed: fast rise, slow fall.
    const fresh = s => s && now - s.at < 400 ? s : null;
    const pcL = fresh(wave.levels.pc), phL = fresh(wave.levels.phone);
    const src = settings.npReactive
      ? [pcL, phL].filter(Boolean).sort((a, b) => (b.b + b.m + b.t) - (a.b + a.m + a.t))[0]
      : null;
    const reactive = !!src && (src.b + src.m + src.t) > 0.05;
    const targets = reactive ? [src.b, src.m, src.t] : [0, 0, 0];
    for (let i = 0; i < 3; i++) {
      const k = targets[i] > wave.band[i] ? 18 : 5;
      wave.band[i] += (targets[i] - wave.band[i]) * Math.min(1, dt * k);
    }
    // Little progress = little waves: they grow with the song and reach full height at 25 %.
    const progressScale = Math.max(0.18, Math.min(1, wave.pct / 25));

    // Emit / move / drop the crests of each layer.
    const t = now / 1000;
    for (let li = 0; li < WAVE_LAYERS.length; li++) {
      const L = WAVE_LAYERS[li], list = wave.crests[li];
      for (const c of list) c.x += L.speed * dt;
      while (list.length && list[0].x - 3 * list[0].s > end) list.shift();
      if (wave.playing && t >= wave.nextEmit[li]) {
        // Height of this crest = the band's level right now (live), or a smooth pseudo-random
        // value when there's no live audio (option off, no permission, nothing measurable).
        const level = reactive
          ? wave.band[li]
          : 0.45 + 0.3 * Math.sin(t * 1.7 + li) * Math.sin(t * 0.63 + 2 * li) + 0.15 * Math.sin(t * 4.1 + 3 * li);
        const h = Math.max(0.08, Math.min(1, reactive ? 0.1 + 0.95 * level : level));
        const sig = L.sigma * (0.7 + 0.6 * h);          // louder → broader ("densidad")
        list.push({ x: -sig, h, s: sig });
        wave.nextEmit[li] = t + L.every * (0.8 + 0.4 * Math.random());
      }
    }

    // Never taller than the free space between the track and the subtitle (artist · app):
    // measured, not assumed, so it holds with any font size / layout.
    if (!wave.limitAt || now - wave.limitAt > 500) {
      wave.limitAt = now;
      const cr = waveCanvas.getBoundingClientRect(), sr = nowPlayingSubtitle.getBoundingClientRect();
      wave.maxH = sr.height ? Math.max(4, Math.min(ch - 2, cr.bottom - sr.bottom - 4)) : ch - 2;
    }
    const maxH = wave.maxH || ch - 2;

    if (end > 6 && wave.amp > 0.01) {
      const [r, g, b] = waveRgb();
      const base = ch - 1;
      for (let li = 0; li < WAVE_LAYERS.length; li++) {
        const L = WAVE_LAYERS[li], list = wave.crests[li];
        if (!list.length) continue;
        waveCtx.beginPath();
        waveCtx.moveTo(0, base);
        for (let x = 0; x <= end; x += 2) {
          let sum = 0;
          for (const c of list) {
            const d = (x - c.x) / c.s;
            if (d > -3 && d < 3) sum += c.h * Math.exp(-d * d);
          }
          // Rises in from the left edge and settles into the thumb.
          const edge = Math.min(1, x / 12) * Math.min(1, (end - x) / 16);
          // Soft saturation instead of clamping: overlapping crests approach the limit but never
          // hit it, so every crest keeps a rounded top (a hard min() flattened them).
          const y = base - maxH * L.height * progressScale * wave.amp * edge * (1 - Math.exp(-1.25 * sum));
          waveCtx.lineTo(x, y);
        }
        waveCtx.lineTo(end, base);
        waveCtx.closePath();
        const grad = waveCtx.createLinearGradient(0, 0, 0, base);
        // Crest a bit lighter (toward white), body in the color — like One UI's glowing waves.
        const lr = Math.round(r + (255 - r) * 0.35), lg = Math.round(g + (255 - g) * 0.35), lb = Math.round(b + (255 - b) * 0.35);
        grad.addColorStop(0, `rgba(${lr}, ${lg}, ${lb}, ${Math.min(1, L.alpha + 0.2)})`);
        grad.addColorStop(1, `rgba(${r}, ${g}, ${b}, ${L.alpha * 0.6})`);
        waveCtx.fillStyle = grad;
        waveCtx.fill();
      }
    }
    // Keep animating while playing or still settling; stop when flat or hidden.
    const rolling = wave.crests.some(l => l.length);
    if ((wave.playing || wave.amp > 0.01 || rolling) && !document.hidden && nowPlayingBar.getClientRects().length > 0) {
      wave.raf = requestAnimationFrame(drawWave);
    }
  }
  function kickWave() {
    if (!wave.raf) { wave.lastT = 0; wave.raf = requestAnimationFrame(drawWave); }
  }
  // Live levels only while they'd be seen: playing, bar visible, window visible, option on.
  function syncAudioLevels() {
    const want = !!(settings.npReactive && wave.playing && !document.hidden && nowPlayingBar.getClientRects().length > 0);
    const key = want + '|' + (activeDeviceId || '');
    if (key === wave.levelsOn) return;
    wave.levelsOn = key;
    postToHost({ action: 'audioLevels', on: want, deviceId: activeDeviceId || '' });
  }
  window.setAudioLevels = function (source, b, m, t, deviceId) {
    if (source === 'phone' && deviceId && deviceId !== activeDeviceId) return;
    wave.levels[source] = { b, m, t, at: performance.now() };
    kickWave();
  };
  document.addEventListener('visibilitychange', () => { if (!document.hidden) kickWave(); syncAudioLevels(); });
  window.addEventListener('resize', kickWave);

  // The album art's dominant (saturated) color, as One UI tints its player with it.
  let waveArtUrl = null;
  function setWaveColorFromArt(url) {
    if (url === waveArtUrl) return;
    waveArtUrl = url;
    if (!url) { wave.color = null; nowPlayingBar.style.removeProperty('--np-color'); kickWave(); return; }
    const img = new Image();
    img.onload = () => {
      if (waveArtUrl !== url) return;
      try {
        const c = document.createElement('canvas');
        c.width = c.height = 24;
        const g = c.getContext('2d', { willReadFrequently: true });
        g.drawImage(img, 0, 0, 24, 24);
        const d = g.getImageData(0, 0, 24, 24).data;
        let R = 0, G = 0, B = 0, W = 0;
        for (let i = 0; i < d.length; i += 4) {
          const r = d[i], gg = d[i + 1], b = d[i + 2];
          const max = Math.max(r, gg, b), min = Math.min(r, gg, b);
          const sat = max ? (max - min) / max : 0;
          if (max < 40 || (max > 235 && sat < 0.15)) continue;
          const w = sat * sat * (max / 255) + 0.02;
          R += r * w; G += gg * w; B += b * w; W += w;
        }
        if (W > 0) {
          let col = [R / W, G / W, B / W];
          // Lift dull/dark averages so the waves read on both themes.
          const mx = Math.max(...col);
          if (mx < 170) col = col.map(v => v * (170 / Math.max(mx, 1)));
          const hex = '#' + col.map(v => Math.round(Math.min(255, v)).toString(16).padStart(2, '0')).join('').toUpperCase();
          wave.color = hex;
          nowPlayingBar.style.setProperty('--np-color', hex);
        } else {
          wave.color = null;
          nowPlayingBar.style.removeProperty('--np-color');
        }
      } catch (e) { wave.color = null; }
      kickWave();
    };
    img.src = url;
  }

  function updateNowPlayingProgress(media) {
    const elapsed = media.isPlaying ? media.position + (Date.now() - media.receivedAt) : media.position;
    const clamped = media.duration > 0 ? Math.min(Math.max(elapsed, 0), media.duration) : Math.max(elapsed, 0);
    nowPlayingElapsed.textContent = formatTrackTime(clamped);
    nowPlayingDuration.textContent = formatTrackTime(media.duration);
    if (nowPlayingSeekDragging) return;
    nowPlayingSeek.max = media.duration || 0;
    nowPlayingSeek.value = clamped;
    // Fakes a "played" vs. "remaining" two-tone track — <input type="range"> has no standard
    // CSS hook for how far along the value is. A CSS custom property (inherited into the
    // ::-webkit-slider-runnable-track pseudo-element, see app.css) is what actually makes this
    // reach the track's own gradient background; setting .style.background on the <input>
    // itself would land on the wrong box and never show through the pseudo-element at all.
    const pct = media.duration > 0 ? (clamped / media.duration) * 100 : 0;
    nowPlayingSeek.style.setProperty('--seek-pct', `${pct}%`);
    wave.pct = pct;
  }

  // Fixed part of the layout now — always visible, never hidden/collapsed to a dead gap. An
  // earlier version hid the bar entirely whenever there was no active session, but that made a
  // genuinely-nothing-playing state indistinguishable from the bar just having disappeared
  // (which is exactly what several of this session's media bugs looked like) — showing an
  // explicit "no reproduciendo nada" state instead of vanishing is less ambiguous either way.
  function renderNowPlaying() {
    nowPlayingBar.classList.add('visible');
    const isPc = nowPlayingSource === 'pc';
    const media = currentNowPlayingMedia();
    // Favorite and "abrir en el teléfono" have no PC-side equivalent — confirmed there's no
    // cross-app "like" concept anywhere in Windows' System Media Transport Controls surface (see
    // PcMediaService.cs's own doc comment) — hidden rather than shown-disabled so it doesn't
    // look like a bug/missing permission. Shuffle IS real on the PC side (TryChangeShuffleActive
    // Async) so it stays visible in both modes; only the "smart shuffle" sparkle never appears
    // for PC media since pcMedia has no isSmartShuffle field (Windows has no such concept at the
    // OS level — that's a Spotify-app-only distinction).
    nowPlayingFavorite.style.display = isPc ? 'none' : '';
    nowPlayingOpen.style.display = isPc ? 'none' : '';
    if (!media) {
      nowPlayingArt.style.backgroundImage = 'none';
      nowPlayingArtPlaceholder.style.display = '';
      nowPlayingTitle.textContent = 'No hay nada reproduciéndose';
      nowPlayingSubtitle.textContent = '';
      nowPlayingPlayPauseIcon.innerHTML = PLAY_ICON;
      nowPlayingPlayPause.disabled = true;
      nowPlayingPrev.disabled = true;
      nowPlayingNext.disabled = true;
      nowPlayingFavorite.disabled = true;
      nowPlayingFavorite.classList.remove('active');
      nowPlayingShuffle.disabled = true;
      nowPlayingShuffle.classList.remove('active', 'smart');
      nowPlayingOpen.disabled = true;
      nowPlayingSeek.disabled = true;
      nowPlayingEq.classList.remove('playing');
      clearInterval(nowPlayingProgressInterval);
      nowPlayingProgressRow.style.visibility = 'hidden';
      return;
    }
    // A background-image on the container (not an <img src>) so a missing/failed album art
    // just falls back to showing the placeholder note icon underneath, never a broken-image
    // glyph or a collapsed-to-nothing box.
    nowPlayingArt.style.backgroundImage = media.albumArtDataUrl ? `url("${media.albumArtDataUrl}")` : 'none';
    nowPlayingArtPlaceholder.style.display = media.albumArtDataUrl ? 'none' : '';
    nowPlayingEq.classList.toggle('playing', !!media.isPlaying);
    wave.playing = !!media.isPlaying;
    setWaveColorFromArt(settings.npArtColor ? (media.albumArtDataUrl || null) : null);
    kickWave();
    syncAudioLevels();
    nowPlayingTitle.textContent = media.title || media.appLabel;
    nowPlayingSubtitle.textContent = media.artist ? `${media.artist} · ${media.appLabel}` : media.appLabel;
    nowPlayingPlayPauseIcon.innerHTML = media.isPlaying ? PAUSE_ICON : PLAY_ICON;
    nowPlayingPlayPause.disabled = !media.canPlayPause;
    nowPlayingPrev.disabled = !media.canPrevious;
    nowPlayingNext.disabled = !media.canNext;
    nowPlayingFavorite.disabled = !media.canFavorite;
    nowPlayingFavorite.classList.toggle('active', !!media.isFavorite);
    nowPlayingShuffle.disabled = !media.canShuffle;
    nowPlayingShuffle.classList.toggle('active', !!media.isShuffleOn);
    nowPlayingShuffle.classList.toggle('smart', !!media.isSmartShuffle);
    nowPlayingOpen.disabled = false;
    nowPlayingSeek.disabled = !media.canSeek;

    nowPlayingProgressRow.style.visibility = 'visible';
    updateNowPlayingProgress(media);
    clearInterval(nowPlayingProgressInterval);
    if (media.isPlaying) nowPlayingProgressInterval = setInterval(() => updateNowPlayingProgress(media), 500);
  }

  function sendMediaControl(control) {
    if (nowPlayingSource === 'pc') {
      postToHost({ action: 'pcMediaControl', control: control });
      return;
    }
    if (activeDeviceId) postToHost({ action: 'mediaControl', deviceId: activeDeviceId, control: control });
  }
  nowPlayingPrev.addEventListener('click', () => sendMediaControl('previous'));
  nowPlayingPlayPause.addEventListener('click', () => sendMediaControl('playPause'));
  nowPlayingNext.addEventListener('click', () => sendMediaControl('next'));
  nowPlayingFavorite.addEventListener('click', () => sendMediaControl('favorite'));
  nowPlayingShuffle.addEventListener('click', () => sendMediaControl('shuffle'));
  nowPlayingOpen.addEventListener('click', () => {
    if (nowPlayingSource === 'pc') return; // no PC-side equivalent — button is hidden in this mode anyway
    if (activeDeviceId) postToHost({ action: 'openMedia', deviceId: activeDeviceId });
  });

  // 'input' fires continuously while dragging (or on every arrow-key step) — used just to keep
  // the elapsed label live so it doesn't look frozen mid-drag; 'change' fires once, on release,
  // which is the actual moment to send the real seek command to the phone.
  nowPlayingSeek.addEventListener('pointerdown', () => { nowPlayingSeekDragging = true; });
  nowPlayingSeek.addEventListener('input', () => {
    // The fill has to track the thumb live while dragging, not just the label — updateNowPlaying
    // Progress skips writing it during a drag (see its own nowPlayingSeekDragging guard) so this
    // is the one place responsible for keeping it in sync until release.
    const value = parseInt(nowPlayingSeek.value, 10);
    const max = parseInt(nowPlayingSeek.max, 10) || 0;
    nowPlayingElapsed.textContent = formatTrackTime(value);
    nowPlayingSeek.style.setProperty('--seek-pct', `${max > 0 ? (value / max) * 100 : 0}%`);
    wave.pct = max > 0 ? (value / max) * 100 : 0;
    kickWave();
  });
  nowPlayingSeek.addEventListener('change', () => {
    const position = parseInt(nowPlayingSeek.value, 10);
    if (nowPlayingSource === 'pc') {
      postToHost({ action: 'pcMediaControl', control: 'seek', position });
    } else if (activeDeviceId) {
      postToHost({ action: 'mediaControl', deviceId: activeDeviceId, control: 'seek', position });
    }
    nowPlayingSeekDragging = false;
  });

  // A user-picked background per device, applied through this exact same element/CSS as the
  // wallpaper auto-extracted from that phone (see WallpaperSync on Android, window.setWallpaper
  // above) — when set for the active device, it takes priority over that phone's own. Keyed by
  // deviceId in localStorage (not global): the S26 Ultra having a custom pick must never affect
  // the Z Flip, which should keep showing its own real wallpaper by default.
  function customWallpaperKey(deviceId) {
    return `notilink-custom-wallpaper-${deviceId}`;
  }
  function getCustomWallpaper(deviceId) {
    return deviceId ? localStorage.getItem(customWallpaperKey(deviceId)) : null;
  }

  function applyWallpaper() {
    const state = activeState();
    const url = getCustomWallpaper(activeDeviceId) || (state && state.wallpaperDataUrl);
    if (url) {
      wallpaperBg.style.backgroundImage = `url("${url}")`;
      wallpaperBg.classList.add('visible');
    } else {
      wallpaperBg.classList.remove('visible');
    }
  }

  function activeState() {
    return activeDeviceId ? getDeviceState(activeDeviceId) : null;
  }

  // --- Settings: purely a rendering preference, so it lives entirely in the
  // page (localStorage) rather than round-tripping through C#/the phone. ---
  const SETTINGS_KEY = 'notilink-settings';
  const DEFAULT_SETTINGS = {
    fontSize: 13,       // px, drives --content-font-size
    mediaSize: 190,     // px, drives --media-max-height
    showMedia: true,
    mediaMode: 'all',   // 'all' | 'lastOnly' — when a chat has several images
    messageMode: 'count', // 'last' | 'count'
    messageCount: 20,
    darkMode: true,
    npArtColor: true,         // now-playing bar takes its color from the album art (else the accent)
    npReactive: true,         // waves follow the music's real levels (PC loopback / phone visualizer)
    accent: '#7C5CFF',        // accent color (PC UI + the phone app); see applyAccent
    accentDaily: true,       // one color per weekday instead of [accent], switching at midnight
    dailyAccents: null,       // [7] custom color per weekday (Sunday first); null = DAILY_ACCENTS
    lineSpacing: 0,      // px, drives --line-spacing (gap between message blocks)
    autoExpandOnUpdate: true, // expand a card back open when a new message arrives for it
    wallpaperIntensity: 25,   // 0-40, % opacity of the phone wallpaper shown behind the list
    showLinkPreviews: true,  // opt-in: fetches title/description/image for URLs found in notifications
    cardRadius: 10,           // px, drives --card-radius (each notification's corner rounding)
    nowPlayingArtSize: 76,    // px, drives --now-playing-art-size (now-playing bar's album art)
    screenMaxSize: 0,         // px, longest-edge cap for scrcpy mirroring (0 = native/uncapped)
    screenMaxFps: 120,         // fps cap for scrcpy mirroring (0 = uncapped)
    screenBitrate: 20,        // Mbps, scrcpy's video bitrate
    screenVideoCodec: 'h264', // 'h264' | 'h265' | 'av1'
    screenBufferMs: 0,        // extra client-side render buffer, smooths jitter at the cost of latency
    screenTurnOff: false,     // turn the phone's own screen off once mirroring starts
    screenDimPhone: true,     // phone stays awake/unlocked at minimum brightness while mirrored (like Huawei's multi-screen)
    screenStayAwake: false,   // keep the phone from auto-locking while mirroring (USB only, reliably)
    screenAudio: true,       // also forward the phone's audio to the PC
    messagingOnlyFilter: false, // view-only filter: show only messaging/email notifications in #list
    cardAnimations: true,       // slide-left+fade on delete, height-animate on collapse/expand
    historyEnabled: true,      // keep every notification (and when/why it went away) on disk — see the history panel
    phoneStatusAutoHide: true, // hide the phone's own connected/disconnected notification after a while
    phoneStatusAutoHideSeconds: 5,
    shareMode: 'screen',        // last-used tab of the share popup: 'screen' | 'app'
    appDisplaySize: '',         // single-app window: virtual display size ('' = like the phone)
    appDpi: 0,                  // single-app window: density (0 = like the phone)
    appImePolicy: 'local',      // 'local' | 'fallback' | 'hide'
    appDecorations: false,      // show status bar / launcher / navigation bar
    appKeepOnClose: false,      // closing the window moves the app to the phone instead of closing it
    appAutoReturn: true,        // opened on the phone meanwhile → back to the PC once the user leaves it
    appMirrorWhileOnPhone: true, // meanwhile, show the phone's own screen over the app's empty window
    appRestart: false,          // force-stop the app before opening it
    appAudio: false,            // forward the phone's audio too
    appAlwaysOnTop: false,
    appBorderless: false,
    recentShareApps: {},        // deviceId -> [{ packageName, label }] (newest first, max 4)
    recentShareAppsAt: {},      // deviceId -> when that list last changed (ms), to sync it with the phone's
  };

  function loadSettings() {
    try {
      const raw = localStorage.getItem(SETTINGS_KEY);
      return raw ? Object.assign({}, DEFAULT_SETTINGS, JSON.parse(raw)) : Object.assign({}, DEFAULT_SETTINGS);
    } catch (e) {
      return Object.assign({}, DEFAULT_SETTINGS);
    }
  }

  let settings = loadSettings();

  function saveSettings() {
    localStorage.setItem(SETTINGS_KEY, JSON.stringify(settings));
  }

  // ---- Accent color: picked from a palette (or custom), or one per weekday ("Un tono por día").
  // The second gradient color is the same hue shifted ~40°, so any accent gets a matching gradient.
  const ACCENTS = ['#7C5CFF', '#4F8CFF', '#14B8A6', '#22C55E', '#F59E0B', '#F97316', '#EC4899', '#EF4444'];
  // Sunday..Saturday (Date.getDay order).
  const DAILY_ACCENTS = ['#EC4899', '#7C5CFF', '#4F8CFF', '#14B8A6', '#22C55E', '#F59E0B', '#F97316'];
  const DAY_NAMES = ['Domingo', 'Lunes', 'Martes', 'Miércoles', 'Jueves', 'Viernes', 'Sábado'];
  let appliedAccent = null;

  function hexToRgb(hex) {
    const m = /^#?([0-9a-f]{6})$/i.exec(hex || '');
    const n = m ? parseInt(m[1], 16) : 0x7C5CFF;
    return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
  }
  function rgbToHex(r, g, b) {
    return '#' + [r, g, b].map(v => Math.max(0, Math.min(255, Math.round(v))).toString(16).padStart(2, '0')).join('').toUpperCase();
  }
  function shiftHue(hex, deg, lightDelta) {
    let [r, g, b] = hexToRgb(hex).map(v => v / 255);
    const max = Math.max(r, g, b), min = Math.min(r, g, b), d = max - min;
    let h = 0, s = 0, l = (max + min) / 2;
    if (d) {
      s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
      h = max === r ? (g - b) / d + (g < b ? 6 : 0) : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
      h *= 60;
    }
    h = (h + deg + 360) % 360;
    l = Math.max(0, Math.min(1, l + lightDelta));
    const c = (1 - Math.abs(2 * l - 1)) * s, x = c * (1 - Math.abs((h / 60) % 2 - 1)), m = l - c / 2;
    const [r1, g1, b1] = h < 60 ? [c, x, 0] : h < 120 ? [x, c, 0] : h < 180 ? [0, c, x] : h < 240 ? [0, x, c] : h < 300 ? [x, 0, c] : [c, 0, x];
    return rgbToHex((r1 + m) * 255, (g1 + m) * 255, (b1 + m) * 255);
  }
  function dailyAccent(day) {
    const custom = Array.isArray(settings.dailyAccents) ? settings.dailyAccents[day] : null;
    return /^#[0-9a-fA-F]{6}$/.test(custom || '') ? custom.toUpperCase() : DAILY_ACCENTS[day];
  }
  function currentAccent() {
    return settings.accentDaily ? dailyAccent(new Date().getDay()) : (settings.accent || ACCENTS[0]);
  }
  function applyAccent() {
    const accent = currentAccent().toUpperCase();
    const root = document.documentElement.style;
    root.setProperty('--accent', accent);
    root.setProperty('--accent-rgb', hexToRgb(accent).join(', '));
    root.setProperty('--accent-strong', shiftHue(accent, 0, -0.08));
    root.setProperty('--accent-2', shiftHue(accent, -40, 0.04));
    root.setProperty('--accent-2-rgb', hexToRgb(shiftHue(accent, -40, 0.04)).join(', '));
    if (accent !== appliedAccent) {
      appliedAccent = accent;
      // The phone app follows the PC's accent (sent with phone.prefs; see MainViewModel.SetPhonePrefs).
      postToHost({ action: 'phonePrefs', accent });
    }
  }
  // "Un tono por día": re-check every minute (survives sleep, unlike one long timeout to midnight).
  setInterval(() => { if (settings.accentDaily) { applyAccent(); if (typeof renderAccentUi === 'function') renderAccentUi(); } }, 60000);

  function applySettingsToDom() {
    applyAccent();
    document.documentElement.style.setProperty('--content-font-size', settings.fontSize + 'px');
    document.documentElement.style.setProperty('--media-max-height', settings.mediaSize + 'px');
    document.documentElement.style.setProperty('--line-spacing', settings.lineSpacing + 'px');
    const intensity = settings.wallpaperIntensity / 100;
    document.documentElement.style.setProperty('--wallpaper-opacity', intensity);
    document.documentElement.style.setProperty('--wallpaper-opacity-dark', Math.min(intensity * 1.6, 0.5));
    document.documentElement.style.setProperty('--card-radius', settings.cardRadius + 'px');
    document.documentElement.style.setProperty('--now-playing-art-size', settings.nowPlayingArtSize + 'px');
  }

  function visibleMessages(messages, all) {
    if (!messages || !messages.length) return [];
    if (all) return messages;
    return settings.messageMode === 'last' ? messages.slice(-1) : messages.slice(-settings.messageCount);
  }

  // A message is "media-only" when its text is just the app's placeholder for the attachment
  // rather than a real caption. WhatsApp prefixes a sticker with the sticker's OWN emoji
  // ("😂 Sticker", "🩷 Sticker"), so every emoji/symbol is stripped first and only the remaining
  // words are compared — no fixed emoji list can cover that.
  // Covers both the bare form ("Sticker", "Foto") and WhatsApp's sentence form — confirmed live:
  // a sticker arrives as "💟 Envió un sticker." — plus English equivalents.
  const MEDIA_PLACEHOLDER_WORDS = /^(?:(?:envi[oó]|mand[oó]|sent|sends?)\s+(?:un|una|a|an)\s+)?(?:sticker|sticker animado|animated sticker|foto|fotograf[ií]a|imagen|image|photo|gif|video|v[ií]deo)?$/i;
  function isMediaOnlyMessage(m) {
    const words = (m.text || '').replace(/[^\p{L}\p{N}]+/gu, ' ').trim();
    return MEDIA_PLACEHOLDER_WORDS.test(words);
  }

  // Splits messages into display blocks: runs of 2+ consecutive media-only messages from the
  // same sender become one horizontal row; everything else stays its own block.
  function groupMediaRuns(messages, isShownImage) {
    const blocks = [];
    let run = [];
    const flush = () => {
      if (run.length >= 2) blocks.push({ run: run.map(r => r.msg) });
      else run.forEach(r => blocks.push(r));
      run = [];
    };
    messages.forEach((msg, index) => {
      const media = isShownImage(msg, index) && isMediaOnlyMessage(msg);
      if (media && run.length && (run[0].msg.sender !== msg.sender || run[0].msg.isMe !== msg.isMe)) flush();
      if (media) run.push({ msg, index });
      else { flush(); blocks.push({ msg, index }); }
    });
    flush();
    return blocks;
  }

  function escapeHtml(s) {
    return (s ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  }

  // Turns http(s) URLs in plain notification text into real clickable links —
  // escapes everything else normally so this can't be used to inject markup.
  const URL_REGEX = /(https?:\/\/[^\s<>"]+)/g;

  // Same idea, for an email address mentioned in a message instead of a link — a plain,
  // deliberately conservative pattern (no attempt at the full RFC 5322 grammar) since this only
  // needs to catch the common case well enough to offer a one-tap copy, not validate arbitrary
  // input. Used both here (inline highlighting) and by firstEmail/copyEmailBtnHtml below (the
  // footer's own "Copiar correo" button) — kept as two separate mechanisms deliberately: the
  // footer button is one easy-to-hit target for the first email found anywhere on the card,
  // while inline highlighting covers every occurrence in every message, including a second one.
  const EMAIL_REGEX = /([a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,})/g;

  // A phone number, account number, reference/order number, or a short verification code — all
  // genuinely different things, but all get the exact same treatment here (highlighted, copy on
  // click), so one deliberately broad pattern covers all of them rather than trying to tell them
  // apart. Matches a run of digits allowing the punctuation real numbers are actually formatted
  // with (spaces, dashes, parentheses, a leading +) — never letters, which is what keeps this
  // from swallowing ordinary words. isLikelyCopyableToken below is what actually gates which
  // matches are worth highlighting; the regex alone is intentionally permissive.
  const NUMBER_TOKEN_REGEX = /(\+?[\d][\d\s\-().]{2,}\d)/g;
  const MIN_COPYABLE_DIGITS = 4; // a 4-digit run is already a real verification code; much less
                                  // than that is just as likely to be an ordinary quantity/count.

  function isLikelyCopyableToken(candidate) {
    const digitCount = (candidate.match(/\d/g) || []).length;
    return digitCount >= MIN_COPYABLE_DIGITS;
  }

  // One combined pass over the text so a URL, an email, and a phone/account/reference number can
  // all appear correctly in the same message without three separate incompatible regex passes
  // fighting over the same characters. Alternation order matters: URL and email are tried before
  // the much more permissive number pattern, so (for example) the digits inside a URL's own path
  // or an email's domain are never separately re-matched as if they were a phone number.
  // Each of URL_REGEX/EMAIL_REGEX/NUMBER_TOKEN_REGEX already has its own single capturing group
  // in its source (the outer parens in each definition above) — concatenated directly, not
  // re-wrapped, so this combined regex ends up with exactly the 3 groups match[1..3] below
  // expect, rather than nesting an extra group per alternative and shifting every index.
  const LINKIFY_REGEX = new RegExp(
    `${URL_REGEX.source}|${EMAIL_REGEX.source}|${NUMBER_TOKEN_REGEX.source}`, 'g'
  );

  // URLs open (real navigation, the one case where that's actually useful); everything else here
  // — email, phone numbers, account/reference numbers, short verification codes — only ever gets
  // copied to the clipboard, never "opened", since there's nothing sensible to open them to. Both
  // still render with the same blue underlined look a link already has, per explicit request:
  // the user only needs one visual cue for "this bit of text is clickable", the click handler
  // (see the .copyable-token delegate below) is what actually tells them apart by behavior.
  function linkifyHtml(raw) {
    const text = raw ?? '';
    let result = '';
    let lastIndex = 0;
    let match;
    LINKIFY_REGEX.lastIndex = 0;
    while ((match = LINKIFY_REGEX.exec(text)) !== null) {
      const [full, url, email, numberToken] = match;
      result += escapeHtml(text.slice(lastIndex, match.index));
      if (url) {
        const safeUrl = escapeHtml(url);
        result += `<a href="${safeUrl}" target="_blank" rel="noopener">${safeUrl}</a>`;
      } else if (email) {
        const safeEmail = escapeHtml(email);
        result += `<span class="copyable-token" data-copy="${safeEmail}" title="Copiar">${safeEmail}</span>`;
      } else if (numberToken && isLikelyCopyableToken(numberToken)) {
        const safeNumber = escapeHtml(numberToken);
        result += `<span class="copyable-token" data-copy="${safeNumber}" title="Copiar">${safeNumber}</span>`;
      } else {
        // Matched the permissive number pattern but didn't clear the digit-count bar (e.g. "3
        // fotos") — plain text, exactly as if it had never matched at all.
        result += escapeHtml(full);
      }
      lastIndex = match.index + full.length;
    }
    result += escapeHtml(text.slice(lastIndex));
    return result;
  }

  const CHEVRON_SVG = `<svg viewBox="0 0 24 24" width="18" height="18"><path d="M6 9l6 6 6-6" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"/></svg>`;

  // --- Link previews: off by default (see the settings checkbox) — fetching a URL found in a
  // notification reaches out to the internet, so this only ever happens when the user opted
  // in. Cached globally by URL (the preview content doesn't depend on which phone reported it)
  // and fetched at most once per URL for the life of the page. ---
  const linkPreviews = new Map(); // url -> preview object | 'pending' | 'failed'

  function urlIn(text) {
    URL_REGEX.lastIndex = 0;
    const match = URL_REGEX.exec(text || '');
    return match ? match[0] : null;
  }

  function firstUrl(item) {
    const haystacks = [item.title, item.text, ...(item.displayLines || [])];
    if (item.messages) for (const m of item.messages) haystacks.push(m.displayText);
    for (const h of haystacks) {
      const url = urlIn(h);
      if (url) return url;
    }
    return null;
  }

  // Same idea as urlIn/firstUrl above, for an email address (EMAIL_REGEX is already declared
  // above, shared with linkifyHtml's own combined pattern).
  function emailIn(text) {
    EMAIL_REGEX.lastIndex = 0;
    const match = EMAIL_REGEX.exec(text || '');
    return match ? match[0] : null;
  }

  function firstEmail(item) {
    const haystacks = [item.title, item.text, ...(item.displayLines || [])];
    if (item.messages) for (const m of item.messages) haystacks.push(m.displayText);
    for (const h of haystacks) {
      const email = emailIn(h);
      if (email) return email;
    }
    return null;
  }

  // Takes the URL directly (not an item) so a messaging-style card with several messages can
  // call this once per message, each with its own link — a fixed single preview for the whole
  // card (via firstUrl) was hiding every link past the first one in a multi-message chat.
  function linkPreviewHtml(url) {
    if (!settings.showLinkPreviews || !url) return '';

    const cached = linkPreviews.get(url);
    if (cached === undefined) {
      linkPreviews.set(url, 'pending');
      postToHost({ action: 'fetchLinkPreview', url });
      return '';
    }
    if (cached === 'pending' || cached === 'failed' || !cached) return '';

    const domain = (() => { try { return new URL(url).hostname; } catch (e) { return url; } })();
    return `
      <a class="link-preview" href="${escapeHtml(url)}" target="_blank" rel="noopener">
        ${cached.imageUrl ? `<img class="link-preview-image" src="${escapeHtml(cached.imageUrl)}">` : ''}
        <div class="link-preview-text">
          <div class="link-preview-title">${escapeHtml(cached.title || domain)}</div>
          ${cached.description ? `<div class="link-preview-desc">${escapeHtml(cached.description)}</div>` : ''}
          <div class="link-preview-domain">${escapeHtml(domain)}</div>
        </div>
      </a>`;
  }

  window.setLinkPreview = function (url, preview) {
    linkPreviews.set(url, preview || 'failed');
    renderAll();
  };

  function postToHost(message) {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(message);
    }
  }

  // The single line shown when a card is collapsed — always the most recent
  // message/line, regardless of the "how many messages" setting. A single WhatsApp message can
  // itself contain multiple literal line breaks (Enter within one message) — .body-text's own
  // pre-wrap would render each of those as a separate visual line even here, making a collapsed
  // card just as tall as an expanded one for a multi-line message. Collapsing them to spaces is
  // what actually makes "collapsed" mean one line, matching .collapsed-line's own nowrap/
  // ellipsis truncation in app.css.
  function summaryLine(item) {
    const raw = isTreatedAsMessaging(item) && item.messages && item.messages.length
      ? item.messages[item.messages.length - 1].displayText
      : (item.displayLines && item.displayLines.length ? item.displayLines[item.displayLines.length - 1] : '');
    return (raw || '').replace(/\s*\n+\s*/g, ' ');
  }

  // Collapsing only ever has a visible effect when the expanded view actually has more than
  // one line's worth of content — a notification that's already just a single short message/line
  // and no image looks identical collapsed or not, so the collapse toggle is pointless (and
  // confusing) there. Anything counted as "more than one line" here: more than one message/line
  // shown, an image, or a single message/line that itself contains a real line break.
  // Messages the card isn't showing right now (older/unread ones beyond the "how many messages"
  // setting) — what the phone's own expand chevron would reveal.
  function hiddenMessageCount(item) {
    if (!isTreatedAsMessaging(item) || !item.messages) return 0;
    return item.messages.length - visibleMessages(item.messages).length;
  }

  function hasCollapsibleContent(item) {
    if (hiddenMessageCount(item) > 0) return true;
    if (settings.showMedia && item.imageDataUrl) return true;
    if (isTreatedAsMessaging(item)) {
      const shown = visibleMessages(item.messages);
      if (shown.length > 1) return true;
      const only = shown[0];
      return !!only && (/\n/.test(only.displayText || '') || (settings.showMedia && !!only.imageDataUrl));
    }
    const lines = item.displayLines || [];
    if (lines.length > 1) return true;
    return lines.length === 1 && /\n/.test(lines[0] || '');
  }

  // "Silenciar"/"Mute"/"Snooze"-style actions don't invoke reliably from the PC side
  // (the phone just ignores them), so they're pulled out of the normal actions row
  // entirely and rendered instead as a small hover-only icon next to collapse/delete.
  const MUTE_ACTION_REGEX = /silenciar|mute|snooze|desactivar/i;

  function splitMuteAction(actions) {
    let muteAction = null;
    let muteIndex = -1;
    const normal = [];
    actions.forEach((a, i) => {
      if (muteIndex === -1 && MUTE_ACTION_REGEX.test(a.title || '')) {
        muteAction = a;
        muteIndex = i;
      } else {
        normal.push({ action: a, index: i });
      }
    });
    return { muteAction, muteIndex, normal };
  }

  function muteBtnHtml(muteIndex) {
    return muteIndex >= 0
      ? `<button class="mute-btn" title="Silenciar" data-action-index="${muteIndex}">🔕</button>`
      : '';
  }

  // Fixed top-right corner, always visible — time, then silenciar, then contraer, then eliminar
  // rightmost. Eliminar moved up here from the bottom footer per user feedback — the bottom
  // corner wasn't a natural place to look for it, and it now sits right next to contraer instead.
  function topRightIconsHtml(muteIndex, collapsed, postedAt, canCollapse, isClearable) {
    const collapseBtn = canCollapse
      ? `<button class="collapse-btn${collapsed ? ' collapsed' : ''}" title="${collapsed ? 'Expandir' : 'Contraer'}">${CHEVRON_SVG}</button>`
      : '';
    return `
      <span class="card-top-time">${escapeHtml(formatTime(postedAt))}</span>
      ${muteBtnHtml(muteIndex)}
      ${collapseBtn}
      ${deleteBtnHtml(isClearable)}`;
  }

  // Eliminar — top-right corner now, next to contraer (see topRightIconsHtml), not the bottom
  // footer — moved per user feedback that the bottom-right spot wasn't where they expected it.
  function deleteBtnHtml(isClearable) {
    return `<button class="delete-btn" title="Borrar" style="display:${isClearable ? '' : 'none'}">&times;</button>`;
  }

  // "Abrir" — fires the exact same tap-through behavior the notification would get if you
  // actually touched it on the phone (see NotiLinkAccessibilityService), not a generic app
  // launch. Sits next to Eliminar since both act on the notification itself rather than one of
  // its own real action buttons.
  // Copiar — only shown when the notification actually contains a link (reuses firstUrl, the
  // same extraction already used for link previews), for exactly the case the user asked for:
  // grabbing the link without needing to open the notification on the phone at all. Styled as a
  // real .action-btn pill so it sits naturally among the notification's own actions, but it's
  // NotiLink's own synthetic one — handled locally (see the click delegate below), never sent
  // to the phone as an action.invoke.
  function copyBtnHtml(item) {
    const url = firstUrl(item);
    return url ? `<button class="action-btn copy-link-btn" data-url="${escapeHtml(url)}">📋 Copiar enlace</button>` : '';
  }

  // A notification mentioning an email address (a WhatsApp/Messenger message with someone's
  // email, a "verify your account" style alert, etc.) gets the same one-tap copy treatment as a
  // URL — independent of copyBtnHtml above, so a message containing both a link and an email
  // shows both buttons rather than only whichever is checked first.
  function copyEmailBtnHtml(item) {
    const email = firstEmail(item);
    return email ? `<button class="action-btn copy-email-btn" data-email="${escapeHtml(email)}">📧 Copiar correo</button>` : '';
  }

  // Only on the synthetic "shared text from phone" card (see MainViewModel.HandleIncomingShare)
  // — always present there, unlike copyBtnHtml above, since this card might not contain a URL
  // at all and copying its full text is the whole point of it. Carries just the key (not the
  // raw text itself) so the click handler can look the current text up from state.data instead
  // of needing to HTML-escape arbitrary shared content into a data-* attribute.
  function copyTextBtnHtml(item) {
    return item.isSharedText ? `<button class="action-btn copy-text-btn" data-key="${item.key}">📋 Copiar</button>` : '';
  }

  // Only on the synthetic "file received from phone" card (see MainViewModel.HandleIncomingFile)
  // — the file already lives on this PC's own disk, so both of these are handled entirely
  // locally (see the openFile/openFilePath cases below), never sent to the phone as an
  // action.invoke the way a real notification action would be.
  function fileBtnsHtml(item) {
    if (!item.hasSavedFile) return '';
    // No confirmation dialog on delete-file, deliberately (explicit request) — unlike the card's
    // own "×" (which only ever hid the card, never touched the actual file on disk — there was
    // previously no way at all to delete a received file once saved), this one both removes the
    // real file from Downloads and clears the card in one click.
    return `<button class="action-btn open-file-btn" data-key="${item.key}">📄 Abrir archivo</button>` +
           `<button class="action-btn open-file-path-btn" data-key="${item.key}">📁 Abrir ruta</button>` +
           `<button class="action-btn delete-file-btn" data-key="${item.key}">🗑️ Eliminar archivo</button>`;
  }

  // Mirrors Android's own Notification.EXTRA_PROGRESS — the same standard API behind a native
  // progress bar in the phone's own shade (Play Store installs, Chrome downloads, WhatsApp
  // sending a video, ...), nothing invented here. Indeterminate ("still preparing, no real
  // percentage yet") gets a moving/pulsing bar instead of a fill width, same visual language as
  // Android's own indeterminate progress bar.
  function progressBarHtml(item) {
    if (!item.hasProgress) return '';
    if (item.progressIndeterminate) {
      return `<div class="notif-progress"><div class="notif-progress-fill notif-progress-indeterminate"></div></div>`;
    }
    const pct = item.progressMax > 0 ? Math.min(100, Math.max(0, (item.progress / item.progressMax) * 100)) : 0;
    return `<div class="notif-progress"><div class="notif-progress-fill" style="width:${pct}%"></div></div>`;
  }

  // A real Android package (not a Dual Messenger copy "pkg#10", not a PC-only shared card).
  const APP_PACKAGE_REGEX = /^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$/;
  function canOpenAppOnPc(item) {
    return !!item && !String(item.key || '').startsWith('share-') && APP_PACKAGE_REGEX.test(item.packageName || '');
  }
  function openAppBtnHtml(item, floating) {
    if (!canOpenAppOnPc(item)) return '';
    return `<button class="open-app-btn open-btn${floating ? ' open-btn-floating' : ''}" title="Abrir ${escapeHtml(item.appLabel || 'la app').replace(/"/g, '&quot;')} en la PC (solo esa app)">
      <svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
        <rect x="2" y="3" width="20" height="14" rx="2"/>
        <line x1="8" y1="21" x2="16" y2="21"/>
        <line x1="12" y1="17" x2="12" y2="21"/>
      </svg>
    </button>`;
  }

  function openBtnHtml(floating) {
    return `<button class="open-btn${floating ? ' open-btn-floating' : ''}" title="Abrir en el teléfono">
      <svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
        <path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"/>
        <polyline points="15 3 21 3 21 9"/>
        <line x1="10" y1="14" x2="21" y2="3"/>
      </svg>
    </button>`;
  }

  // Bare "HH:mm" is only unambiguous for something posted today — a notification from
  // yesterday (or earlier) showing just a time reads as "that happened a few minutes ago" when
  // it didn't, exactly the confusion reported: "11:26" shown while it's currently 11:19, with no
  // indication the 11:26 was actually a full day earlier. Compared by calendar day (not a 24h
  // elapsed-time cutoff), since "yesterday at 23:50" is a different day even though it might be
  // under 24h ago.
  function formatTime(postedAt) {
    if (!postedAt) return '';
    try {
      const date = new Date(postedAt);
      const now = new Date();
      const time = date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
      if (isSameCalendarDay(date, now)) return time;

      // Not just the date alone — the actual hour still matters (e.g. "was this a late-night
      // message or a morning one"), it's just the bare "HH:mm" alone that's misleading once the
      // day isn't today. Both together, date first.
      const yesterday = new Date(now);
      yesterday.setDate(now.getDate() - 1);
      if (isSameCalendarDay(date, yesterday)) return `Ayer, ${time}`;

      // Kept short on purpose — this sits in the card's small fixed-width top-right corner
      // (see .card-top-time in app.css) alongside the mute/collapse icons, not a full label.
      const sameYear = date.getFullYear() === now.getFullYear();
      const day = date.toLocaleDateString([], sameYear
        ? { day: 'numeric', month: 'short' }
        : { day: 'numeric', month: 'short', year: '2-digit' });
      return `${day}, ${time}`;
    } catch (e) {
      return '';
    }
  }

  function isSameCalendarDay(a, b) {
    return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
  }

  function cardClassFor(item) {
    return `card${item.style === 'call' ? ' card-call' : ''}`;
  }

  // Notification.color, the app's own requested accent — a thin left border, same treatment
  // .card-call already uses for its own (hardcoded) highlight, just per-notification and
  // app-chosen instead. Absent for the vast majority of notifications (most apps never request
  // one), in which case the border simply doesn't render (see .card's own transparent default).
  function cardAccentStyle(item) {
    return item.accentColor ? ` style="--card-accent:${escapeHtml(item.accentColor)}"` : '';
  }

  function cardHtml(item) {
    return `<div class="${cardClassFor(item)}" data-key="${item.key}" data-package="${item.packageName}"${cardAccentStyle(item)}>${cardInnerHtml(item)}</div>`;
  }

  // Split out from cardHtml so the collapse/expand toggle (see the collapse-btn click handler)
  // can animate height by swapping just a card's own innerHTML in place, instead of tearing
  // down and rebuilding the whole element via renderAll() — which would replace the DOM node
  // outright and give a CSS/JS height transition nothing to actually animate between.
  function cardInnerHtml(item) {
    const iconImg = item.iconDataUrl ? `<img class="app-icon" src="${item.iconDataUrl}">` : '';

    // Contact photo (when the notification has one) is the primary visual, with the
    // app's own icon as a small corner badge — the app icon+name row is dropped in
    // favor of app name above / contact name below, next to the photo.
    const headerHtml = item.avatarDataUrl
      ? `<div class="header-avatar-row">
           <div class="avatar-wrap">
             <img class="avatar" src="${item.avatarDataUrl}">
             ${iconImg ? `<img class="avatar-badge" src="${item.iconDataUrl}">` : ''}
           </div>
           <div class="header-text">
             <div class="app-label">${escapeHtml(item.appLabel)}</div>
             <div class="title">${escapeHtml(item.title)}</div>
           </div>
         </div>`
      : `<div class="header-row">${iconImg}<span class="app-label">${escapeHtml(item.appLabel)}</span></div>
         <div class="title">${escapeHtml(item.title)}</div>`;

    // A card with nothing collapsible never gets stuck "collapsed" with no visible way back —
    // canCollapse hides the button entirely, and collapsed is forced false alongside it so the
    // full (identical-looking either way) content always renders.
    const canCollapse = hasCollapsibleContent(item);
    if (!canCollapse) item.uiCollapsed = false;
    const collapsed = canCollapse && !!item.uiCollapsed;
    const { muteIndex, normal } = splitMuteAction(item.actions || []);

    // The footer (abrir en el dispositivo + the notification's own real actions) stays the same
    // whether the card is collapsed or not — collapsing only hides the message body, never the
    // ability to act on the notification. Eliminar no longer lives down here (see
    // topRightIconsHtml) — abrir is the only thing left in this corner now.
    const actionPillsHtml = normal.map(({ action: a, index: i }) =>
      `<button class="action-btn" data-action-index="${i}" data-has-reply="${a.hasReplyInput}">${escapeHtml(a.title)}</button>`
    ).join('');
    // A footer row exists only to hold real actions (responder/copiar/archivo) alongside abrir —
    // with no other actions at all, a footer that's just a lone button was adding a whole extra
    // row of height (margin-top + button) for nothing. In that case abrir instead floats over the
    // card's own bottom-right corner, at the same level as the last line of the message rather
    // than pushing the card taller.
    const copyBtn = copyBtnHtml(item);
    const copyEmailBtn = copyEmailBtnHtml(item);
    const copyTextBtn = copyTextBtnHtml(item);
    const fileBtns = fileBtnsHtml(item);
    const footerHtml = (normal.length || copyBtn || copyEmailBtn || copyTextBtn || fileBtns)
      ? `<div class="card-footer">
           <div class="card-footer-left">${actionPillsHtml}${copyBtn}${copyEmailBtn}${copyTextBtn}${fileBtns}</div>
           <div class="card-footer-right">${openAppBtnHtml(item, false)}${openBtnHtml(false)}</div>
         </div>`
      : `<div class="open-btns-floating">${openAppBtnHtml(item, true)}${openBtnHtml(true)}</div>`;

    if (collapsed) {
      return `
          ${topRightIconsHtml(muteIndex, true, item.postedAt, true, item.isClearable)}
          ${headerHtml}
          ${progressBarHtml(item)}
          <div class="body-text collapsed-line">${escapeHtml(summaryLine(item))}</div>
          ${footerHtml}`;
    }

    let bodyHtml;
    // Messaging cards get one preview per message, inline (below) — a single card-level preview
    // for those too would just duplicate whichever message's link happened to be first.
    let cardLevelPreview = '';
    if (isTreatedAsMessaging(item)) {
      const shown = visibleMessages(item.messages, item.uiAll);
      let lastImageIdx = -1;
      if (settings.mediaMode === 'lastOnly') {
        for (let i = shown.length - 1; i >= 0; i--) {
          if (shown[i].imageDataUrl) { lastImageIdx = i; break; }
        }
      }
      // Consecutive media-only messages from the same sender (stickers, photos, GIFs) sit side by
      // side in one wrapping row instead of stacking down the card; a text message in between, a
      // captioned photo, or a different sender starts a new block.
      const isShownImage = (m, i) => settings.showMedia && m.imageDataUrl && (settings.mediaMode === 'all' || i === lastImageIdx);
      const blocks = groupMediaRuns(shown, isShownImage);
      bodyHtml = blocks.map(block => {
        if (block.run) {
          const m = block.run[0];
          const senderHtml = m.sender ? `<div class="body-text"><span class="msg-sender">${escapeHtml(m.sender)}</span>:</div>` : '';
          return `
          <div class="msg${m.isMe ? ' msg-mine' : ' msg-theirs'}">
            ${senderHtml}
            <div class="msg-media-row">${block.run.map(r => `<img class="msg-image" src="${r.imageDataUrl}">`).join('')}</div>
          </div>`;
        }
        const m = block.msg;
        const i = block.index;
        const showImage = isShownImage(m, i);
        // Sender name (when shown at all — see NotificationItem.ParseMessages) gets its own bold
        // span, kept separate from the message text itself so only the "who" is emphasized, not
        // the whole line — .msg-mine/.msg-theirs still drives the body-text color split (see
        // app.css) the same way WhatsApp's own chat bubbles differ, just as a text tint here.
        const senderHtml = m.sender ? `<span class="msg-sender">${escapeHtml(m.sender)}</span>: ` : '';
        return `
          <div class="msg${m.isMe ? ' msg-mine' : ' msg-theirs'}">
            <div class="body-text">${senderHtml}${linkifyHtml(m.text)}</div>
            ${showImage ? `<img class="msg-image" src="${m.imageDataUrl}">` : ''}
            ${linkPreviewHtml(urlIn(m.displayText))}
          </div>`;
      }).join('');
    } else {
      bodyHtml = (item.displayLines || []).map(l => `<div class="body-text">${linkifyHtml(l)}</div>`).join('');
      cardLevelPreview = linkPreviewHtml(firstUrl(item));
    }

    // "Ver todos (N)" / "Ver menos": like the phone's expanded notification, reveals every message
    // it still holds, not only the ones the "how many messages" setting keeps on a card.
    const hidden = hiddenMessageCount(item);
    if (hidden > 0 || item.uiAll) {
      bodyHtml += `<button class="show-all-btn">${item.uiAll ? 'Ver menos' : `Ver todos (${item.messages.length})`}</button>`;
    }

    const bigImage = (settings.showMedia && item.imageDataUrl) ? `<img class="big-image" src="${item.imageDataUrl}">` : '';

    const replyAction = normal.find(({ action: a }) => a.hasReplyInput);
    // RemoteInput.getChoices() — canned quick-reply suggestions some apps offer alongside free
    // typing (Gmail's "Sí"/"No"/"Tal vez" being the common example). Tapping one sends
    // immediately, same as tapping it on the phone would — no typing, no separate confirm step.
    // The choice text itself is deliberately NOT put in a data-* attribute (escapeHtml doesn't
    // escape double-quotes, so arbitrary app-provided text could break out of one) — the click
    // handler reads it straight from the button's own rendered textContent instead, which is the
    // exact same already-escaped string.
    const replyChoicesHtml = replyAction && replyAction.action.replyChoices && replyAction.action.replyChoices.length
      ? `<div class="reply-choices">${replyAction.action.replyChoices.map(c =>
          `<button class="reply-choice-btn" data-action-index="${replyAction.index}">${escapeHtml(c)}</button>`
        ).join('')}</div>`
      : '';
    const replyRow = replyAction
      ? `${replyChoicesHtml}<div class="reply-row">
           <input type="text" class="reply-input" placeholder="Escribe una respuesta...">
           <button class="reply-send-btn">Enviar</button>
         </div>`
      : '';

    return `
        ${topRightIconsHtml(muteIndex, false, item.postedAt, canCollapse, item.isClearable)}
        ${headerHtml}
        ${progressBarHtml(item)}
        ${bodyHtml}
        ${bigImage}
        ${cardLevelPreview}
        ${footerHtml}
        ${replyRow}`;
  }

  // Optional (see setting-card-animations): animates the card's own height between its current
  // (collapsed or expanded) content and the toggled one, rather than the instant swap a normal
  // renderAll() does. renderAll() rebuilds every card's DOM node from scratch on every state
  // change, which gives a CSS/JS transition nothing to animate FROM — this instead updates just
  // this one card's innerHTML in place (via cardInnerHtml, the shared piece cardHtml also uses),
  // so the element's identity — and the height transition running on it — survives the toggle.
  // Renders newHtml into a hidden, same-width clone placed right after cardEl to measure the
  // height it would naturally take — without ever showing that content to the user. Needed so
  // the collapsing direction can know its target height up front while still displaying the
  // OLD content during the shrink (see toggleCollapse below).
  function measureNaturalHeight(cardEl, newHtml) {
    const clone = cardEl.cloneNode(false);
    clone.style.position = 'absolute';
    clone.style.visibility = 'hidden';
    clone.style.pointerEvents = 'none';
    clone.style.height = 'auto';
    clone.style.width = cardEl.getBoundingClientRect().width + 'px';
    clone.innerHTML = newHtml;
    cardEl.parentNode.insertBefore(clone, cardEl.nextSibling);
    const height = clone.scrollHeight;
    clone.remove();
    return height;
  }

  function animateCardHeight(cardEl, endHeight, onDone) {
    requestAnimationFrame(() => {
      cardEl.style.transition = 'height 0.22s ease';
      cardEl.style.height = endHeight + 'px';
    });
    cardEl.addEventListener('transitionend', function handler(e) {
      if (e.propertyName !== 'height') return;
      cardEl.style.transition = '';
      cardEl.style.height = '';
      cardEl.style.overflow = '';
      cardEl.removeEventListener('transitionend', handler);
      onDone?.();
    });
  }

  // Optional (see setting-card-animations). The content swap and the height change need to move
  // together, but which one leads depends on direction — expanding reveals content that must
  // already be in the DOM for the growing box to show; collapsing hides content that must stay
  // in place (clipped by overflow:hidden as the box shrinks around it) until the box has
  // actually finished shrinking, or the text was snapping to its final one-line form well before
  // the height caught up.
  function toggleCollapse(cardEl, item) {
    const wasCollapsed = !!item.uiCollapsed;
    item.uiCollapsed = !wasCollapsed;
    updateExpandAllIcon();
    if (!settings.cardAnimations) {
      renderAll();
      return;
    }
    const collapsing = !wasCollapsed;
    const newHtml = cardInnerHtml(item);

    const startHeight = cardEl.getBoundingClientRect().height;
    cardEl.style.height = startHeight + 'px';
    cardEl.style.overflow = 'hidden';
    cardEl.getBoundingClientRect(); // force reflow so the fixed start height actually applies first

    if (collapsing) {
      // Old (expanded) content stays visible — just increasingly clipped — until the shrink
      // finishes, then swaps to the final one-line form.
      const endHeight = measureNaturalHeight(cardEl, newHtml);
      animateCardHeight(cardEl, endHeight, () => { cardEl.innerHTML = newHtml; });
    } else {
      // New (expanded) content goes in now, clipped to the still-small starting height, then
      // the box grows to reveal it.
      cardEl.innerHTML = newHtml;
      animateCardHeight(cardEl, cardEl.scrollHeight);
    }
  }

  // Email virtually never uses Android's MessagingStyle (isMessaging below already catches
  // essentially every real chat app — WhatsApp, Messenger, Telegram, Signal, SMS/RCS, etc. —
  // since Android's own guidelines steer messaging apps toward it), so it needs its own
  // package-name list. Keeping that list scoped to email specifically, rather than trying to
  // also hardcode every messaging app by name, means a newly installed chat app is still
  // caught automatically without this list ever needing an update.
  const EMAIL_PACKAGES = new Set([
    'com.google.android.gm',                // Gmail
    'com.microsoft.office.outlook',          // Outlook
    'com.samsung.android.email.provider',    // Samsung Email
    'com.yahoo.mobile.client.android.mail',  // Yahoo Mail
    'com.fsck.k9',                           // K-9 Mail
    'ch.protonmail.android',                 // Proton Mail
    'me.bluemail.mail',                      // BlueMail
    'org.kman.AquaMail',                     // Aqua Mail
    'com.easilydo.mail',                     // Edison Mail
    'ru.yandex.mail',                        // Yandex Mail
    'com.readdle.spark',                     // Spark
  ]);

  // A few apps' Android notifications legitimately use MessagingStyle (real per-message sender
  // data, a real conversation-shaped UI) for something that isn't actually a 1:1/group chat the
  // user wants treated like one — confirmed for Twitch's whisper/moderation notifications, which
  // arrive with real actions and message text but aren't "messaging" in the sense this feature
  // means. Excluded here rather than trusting item.isMessaging (Android's own template
  // classification) as-is everywhere it's checked.
  const MESSAGING_EXCLUDED_PACKAGES = new Set([
    'tv.twitch.android.app', // Twitch
  ]);

  function isTreatedAsMessaging(item) {
    return !!(item && item.isMessaging && !MESSAGING_EXCLUDED_PACKAGES.has(item.packageName));
  }

  function isMessagingOrEmail(item) {
    return !!(item && (isTreatedAsMessaging(item) || EMAIL_PACKAGES.has(item.packageName)));
  }

  // The button always shows what its NEXT click will do: while any card is open it collapses
  // (arrows up), otherwise it expands (arrows down). Re-derived from the real card state on every
  // render, so new notifications or single-card toggles can't leave it stale.
  function updateExpandAllIcon() {
    const btn = document.getElementById('expand-all-btn');
    if (!btn) return;
    const state = activeState();
    const anyOpen = !!state && [...state.data.values()].some(i => hasCollapsibleContent(i) && !i.uiCollapsed);
    const paths = anyOpen ? 'M6 10l6-6 6 6M6 20l6-6 6 6' : 'M6 4l6 6 6-6M6 14l6 6 6-6';
    btn.innerHTML = `<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="${paths}"/></svg>`;
    btn.title = anyOpen ? 'Contraer todas las notificaciones' : 'Expandir todas las notificaciones';
  }

  function renderAll() {
    if (peerTabActive()) { renderPeerFeed(); return; }
    const state = activeState();
    const order = state ? state.order : [];
    const data = state ? state.data : null;
    const visibleKeys = order.filter(k => {
      const item = data.get(k);
      // Lives exclusively in the persistent call bar now (see renderCallBar) — showing it again
      // here as a regular card would just duplicate it.
      if (!item || item.style === 'call') return false;
      return !settings.messagingOnlyFilter || isMessagingOrEmail(item);
    });
    listEl.innerHTML = visibleKeys.length
      ? visibleKeys.map(k => cardHtml(data.get(k))).join('')
      : `<div class="empty-state">${emptyStateMessage(order.length)}</div>`;    updateExpandAllIcon();
  }

  function emptyStateMessage(totalCount) {
    if (!devices.length) return 'Vincula un dispositivo para empezar';
    if (settings.messagingOnlyFilter && totalCount) return 'Sin notificaciones de mensajería o correo';
    return 'Sin notificaciones';
  }

  // Only the currently-selected device's notification count — not summed across every paired
  // device — so the taskbar badge always matches what's actually showing in the list right now
  // (see notifyCountChanged and MainWindow's "notificationCountChanged" handler).
  function activeDeviceNotificationCount() {
    const state = activeState();
    return state ? state.data.size : 0;
  }

  function notifyCountChanged() {
    postToHost({ action: 'notificationCountChanged', count: activeDeviceNotificationCount() });
  }

  // Keeps state.order always sorted newest-first by the notification's own postedAt — not just
  // "most recently touched first". Plain insertion order (unshift on arrival) fell apart in two
  // real cases: (1) the initial resync right after (re)connecting sends every currently-active
  // notification in whatever order the phone happens to iterate them internally, not by time, so
  // without a real sort they rendered in a visibly random order; (2) a notification that arrives
  // late (a flaky connection, a delayed sync, ...) with an OLDER postedAt than what's already
  // showing must NOT jump to the top just because it was the last one received — it belongs
  // wherever its actual timestamp places it. [key] is assumed to already be in state.data.
  function insertSorted(state, key) {
    const idx = state.order.indexOf(key);
    if (idx !== -1) state.order.splice(idx, 1);
    const postedAt = state.data.get(key)?.postedAt || 0;
    let insertAt = state.order.length;
    for (let i = 0; i < state.order.length; i++) {
      const other = state.data.get(state.order[i]);
      if (!other || (other.postedAt || 0) <= postedAt) { insertAt = i; break; }
    }
    state.order.splice(insertAt, 0, key);
  }

  // WhatsApp (and similar) only keep the most recent handful of messages inside a notification —
  // confirmed live: "(11 mensajes)" in the title with only 7 in the payload. The PC has usually
  // already seen the older ones in earlier updates of the SAME notification, so it remembers them:
  // any message from the previous version that's missing from the new one is kept in front of it.
  // Memory only, and it lives on the card itself — once the notification goes away (read or
  // dismissed on the phone, or deleted here) the card and everything it remembered go with it.
  const MAX_REMEMBERED_MESSAGES = 100;
  function messageIdentity(m) {
    return `${m.ts}|${m.sender || ''}|${m.text || ''}`;
  }
  function mergeRememberedMessages(existing, item) {
    const oldMsgs = existing.messages;
    const newMsgs = item.messages;
    if (!Array.isArray(oldMsgs) || !oldMsgs.length || !Array.isArray(newMsgs) || !newMsgs.length) return;
    // The phone re-sends every message's image on each update; until it lands again, keep
    // showing what this same message already had.
    const byIdentity = new Map(oldMsgs.map(m => [messageIdentity(m), m]));
    for (const m of newMsgs) {
      const prev = byIdentity.get(messageIdentity(m));
      if (!prev) continue;
      if (!m.imageDataUrl && prev.imageDataUrl) m.imageDataUrl = prev.imageDataUrl;
    }
    const present = new Set(newMsgs.map(messageIdentity));
    // Only messages OLDER than the new batch — anything newer that vanished was genuinely removed.
    const oldestNewTs = Math.min(...newMsgs.map(m => m.ts || 0));
    const carried = oldMsgs.filter(m => !present.has(messageIdentity(m)) && (m.ts || 0) <= oldestNewTs);
    if (!carried.length) return;
    item.messages = carried.concat(newMsgs).slice(-MAX_REMEMBERED_MESSAGES);
  }

  // --- Notification history (opt-in, Ajustes): every card is snapshotted to the PC's history
  // store as it arrives/updates (debounced — images trickle in after the card itself), and its
  // removal is recorded with when + why. See Services/HistoryStore.cs. ---
  const HISTORY_SAVE_DELAY_MS = 1500;
  const historyTimers = new Map();

  function historySnapshot(deviceId, item) {
    const dev = devices.find(d => d.deviceId === deviceId);
    return {
      id: item.historyId,
      deviceId,
      deviceName: dev ? dev.name : '',
      key: item.key,
      packageName: item.packageName || '',
      appLabel: item.appLabel || '',
      title: item.title || '',
      displayLines: item.displayLines || [],
      style: item.style || 'plain',
      isGroupConversation: !!item.isGroupConversation,
      messages: (item.messages || []).map(m => ({
        sender: m.sender || '', text: m.text || '', isMe: !!m.isMe, ts: m.ts || 0, imageDataUrl: m.imageDataUrl || null,
      })),
      actions: (item.actions || []).map(a => a.title).filter(Boolean),
      imageDataUrl: item.imageDataUrl || null,
      avatarDataUrl: item.avatarDataUrl || null,
      iconDataUrl: item.iconDataUrl || null,
      accentColor: item.accentColor || null,
      postedAt: item.postedAt || null,
      firstSeenAt: item.firstSeenAt || Date.now(),
      lastUpdatedAt: Date.now(),
    };
  }

  function sendHistorySnapshot(deviceId, key) {
    const item = getDeviceState(deviceId).data.get(key);
    if (!item || !item.historyId) return;
    historyTimers.delete(item.historyId);
    postToHost({ action: 'historyUpsert', entry: historySnapshot(deviceId, item) });
  }

  function scheduleHistorySave(deviceId, key) {
    if (!settings.historyEnabled) return;
    const item = getDeviceState(deviceId).data.get(key);
    if (!item || !item.historyId) return;
    clearTimeout(historyTimers.get(item.historyId));
    historyTimers.set(item.historyId, setTimeout(() => sendHistorySnapshot(deviceId, key), HISTORY_SAVE_DELAY_MS));
  }

  function recordHistoryRemoval(deviceId, item, reason) {
    if (!settings.historyEnabled || !item || !item.historyId) return;
    clearTimeout(historyTimers.get(item.historyId));
    historyTimers.delete(item.historyId);
    postToHost({ action: 'historyUpsert', entry: historySnapshot(deviceId, item) });
    postToHost({ action: 'historyRemoved', id: item.historyId, reason, at: Date.now() });
  }

  window.upsertNotification = function (deviceId, item) {
    const state = getDeviceState(deviceId);
    const existing = state.data.get(item.key);
    if (!existing) {
      item.uiCollapsed = false; // a brand-new notification always starts expanded
      // No separate toast for a call-style item any more — the persistent call bar (see
      // renderCallBar) already surfaces it far more usefully, with photo/name/actions/duration,
      // and stays up for the whole call instead of fading after a few seconds.
    } else {
      // An update to an existing conversation: expand it only if the user's
      // setting says new messages should pop it open; otherwise leave it as
      // the user last left it (collapsed stays collapsed).
      item.uiCollapsed = settings.autoExpandOnUpdate ? false : !!existing.uiCollapsed;
      mergeRememberedMessages(existing, item);
    }
    item.historyId = existing && existing.historyId ? existing.historyId : `${deviceId}|${item.key}|${Date.now()}`;
    item.firstSeenAt = existing && existing.firstSeenAt ? existing.firstSeenAt : Date.now();
    state.data.set(item.key, item);
    insertSorted(state, item.key);
    scheduleHistorySave(deviceId, item.key);
    if (deviceId === activeDeviceId) {
      renderAll();
      if (item.style === 'call') renderCallBar();
      if (!existing) notifyCountChanged();
    }
  };

  // Telecom-session-based call signal from the phone (see docs/PROTOCOL.md's call.state) — fires
  // independently of any notification, covering native SIM AND VoIP app calls (WhatsApp,
  // Telegram, etc.) that never post a CallStyle notification while ringing at all. Can't tell
  // incoming from outgoing at this layer, so the plain-mode wording stays deliberately neutral
  // rather than claiming "entrante". Per-device (like media state) so switching phones never
  // shows one phone's call as if it were another's.
  //
  // Persistent, not a toast — stays up for the actual duration of the call. The safety timeout
  // below guards against a missed/lost "ended" event (e.g. the phone reconnecting mid-call)
  // leaving the bar stuck forever; real calls essentially never run this long.
  const CALL_BAR_SAFETY_TIMEOUT_MS = 10 * 60 * 1000;
  let callBarSafetyTimer = null;

  window.onCallActive = function (deviceId, deviceName) {
    const s = getDeviceState(deviceId);
    if (!s.callActive) { s.callAnswered = false; s.callAnsweredAt = null; s.callSpeaker = false; s.callMuted = false; }
    s.callActive = true;
    s.callDeviceName = deviceName;
    if (deviceId === activeDeviceId) renderCallBar();
    clearTimeout(callBarSafetyTimer);
    callBarSafetyTimer = setTimeout(() => {
      s.callActive = false;
      if (deviceId === activeDeviceId) renderCallBar();
    }, CALL_BAR_SAFETY_TIMEOUT_MS);
  };

  const CALL_CONTROL_FAIL_REASONS = {
    no_permission: 'falta el permiso "Contestar llamadas" en el teléfono',
    not_found: 'no se encontró el botón en la pantalla de llamada del teléfono (¿está visible la pantalla de llamada?)',
    no_call: 'no hay una llamada que controlar',
  };
  window.setCallControlResult = function (success, action, method) {
    const verb = { answer: 'Llamada contestada', hangup: 'Llamada finalizada', speaker: 'Altavoz alternado', mute: 'Micrófono alternado', hold: 'Espera alternada' }[action] || 'Hecho';
    showToast(success ? '📞 ' + verb : '⚠️ No se pudo: ' + (CALL_CONTROL_FAIL_REASONS[method] || method), success ? 2500 : 6000);
  };

  window.onCallEnded = function (deviceId) {
    clearTimeout(callBarSafetyTimer);
    const s = getDeviceState(deviceId);
    s.callActive = false;
    s.callAnswered = false; s.callAnsweredAt = null; s.callSpeaker = false; s.callMuted = false;
    if (deviceId === activeDeviceId) renderCallBar();
  };

  // The phone's call monitor (see docs/PROTOCOL.md's call.status): [answered] flips true the
  // moment the dialer's on-screen call timer first appears — i.e. the other side actually
  // picked up — and [elapsedSeconds] is that timer's value at that instant, so the PC's timer
  // counts real talk time only, not ringing. speaker/muted are the phone's real audio state.
  window.onCallStatus = function (deviceId, answered, elapsedSeconds, speaker, muted) {
    const s = getDeviceState(deviceId);
    if (answered && !s.callAnswered) {
      s.callAnswered = true;
      s.callAnsweredAt = Date.now() - (elapsedSeconds || 0) * 1000;
    }
    s.callSpeaker = speaker;
    s.callMuted = muted;
    if (deviceId === activeDeviceId) renderCallBar();
  };

  // A call that DOES post a real notification (native SIM calls, typically — see
  // docs/PROTOCOL.md's call.state) already carries a photo, sender name, and its own real
  // actions (Contestar/Rechazar/Colgar) through the normal notification pipeline — this renders
  // that same item richly here instead of leaving the user to find it in the list. Falls back to
  // the bare "Llamada en curso" text when only the raw Telecom signal is available (no
  // notification at all, e.g. WhatsApp while still ringing — confirmed from a real test log).
  // Duration uses the notification's own postedAt (Android's real post timestamp for it) rather
  // than tracking a separate client-side start time — one less thing to keep in sync.
  let callBarTimerInterval = null;
  let callBarTimerKey = null;
  // The moment THIS client observed the current call go from ringing to answered — Date.now(),
  // not anything from the wire, and specifically NOT callItem.postedAt: that's when the call
  // notification was first posted, i.e. when it started RINGING, so a timer counting from it
  // was already running (and looked "connected") before the call was ever answered — exactly
  // the bug reported. null while still ringing/not yet answered, so no timer shows at all.
  let callAnsweredAt = null;

  // Android's CallStyle shows an "answer" action ONLY while the call is still ringing
  // (Notification.CallStyle.forIncomingCall) — once answered, the app reposts the notification
  // with just the hang-up action (forOngoingCall). No dedicated "is this ringing" field exists
  // on the wire, but this is a reliable, already-available signal: same regex already used to
  // tell the answer/hang-up buttons apart (see callActionBtnHtml).
  function isCallRinging(callItem) {
    return (callItem.actions || []).some(a => CALL_ANSWER_REGEX.test(a.title || ''));
  }

  function findCallItem(state) {
    if (!state) return null;
    for (const key of state.order) {
      const item = state.data.get(key);
      if (item && item.style === 'call') return item;
    }
    return null;
  }

  function formatCallDuration(postedAt) {
    const elapsed = postedAt ? Math.max(0, Math.floor((Date.now() - postedAt) / 1000)) : 0;
    const hours = Math.floor(elapsed / 3600);
    const mins = Math.floor((elapsed % 3600) / 60);
    const secs = elapsed % 60;
    const base = `${String(mins).padStart(2, '0')}:${String(secs).padStart(2, '0')}`;
    return hours > 0 ? `${String(hours).padStart(2, '0')}:${base}` : base;
  }

  // A real monochrome SVG rather than the 📞 emoji used at first — emoji glyphs render as fixed
  // full-color bitmaps from the font itself, completely ignoring CSS `color`, so there was no way
  // to actually make the handset render white against the colored circle. fill="currentColor"
  // here properly inherits .call-bar-btn's white text color instead.
  function phoneIconSvg(rotated) {
    return `<svg viewBox="0 0 24 24" width="19" height="19" fill="currentColor"${rotated ? ' style="transform:rotate(135deg)"' : ''}><path d="M6.62 10.79c1.44 2.83 3.76 5.14 6.59 6.59l2.2-2.2c.27-.27.67-.36 1.02-.24 1.12.37 2.33.57 3.57.57.55 0 1 .45 1 1V20c0 .55-.45 1-1 1-9.39 0-17-7.61-17-17 0-.55.45-1 1-1h3.5c.55 0 1 .45 1 1 0 1.25.2 2.45.57 3.57.11.35.03.74-.25 1.02l-2.2 2.2z"/></svg>`;
  }

  // Best-effort classification by the action's own visible title, same approach already used
  // for favorite/shuffle detection on the Android side — there's no standardized action id for
  // "answer"/"hang up" to match against instead, every app phrases it differently. Matched
  // actions render as a real phone-app-style circular button (green = answer, red = hang up,
  // the classic rotated-handset look for the latter); anything unrecognized falls back to the
  // same plain pill every other notification action already uses, so it's still usable even if
  // never seen in testing.
  const CALL_ANSWER_REGEX = /contestar|aceptar|answer|accept/i;
  const CALL_HANGUP_REGEX = /colgar|rechazar|finalizar|reject|decline|hang.?up|end.?call/i;

  function callActionBtnHtml(action, index) {
    const title = action.title || '';
    if (CALL_HANGUP_REGEX.test(title)) {
      return `<button class="call-bar-btn call-bar-btn-hangup" title="${escapeHtml(title)}" data-action-index="${index}">${phoneIconSvg(true)}</button>`;
    }
    if (CALL_ANSWER_REGEX.test(title)) {
      return `<button class="call-bar-btn call-bar-btn-answer" title="${escapeHtml(title)}" data-action-index="${index}">${phoneIconSvg(false)}</button>`;
    }
    return `<button class="action-btn" data-action-index="${index}">${escapeHtml(title)}</button>`;
  }

  const SPEAKER_ICON_SVG = `<svg viewBox="0 0 24 24" width="19" height="19" fill="currentColor"><path d="M3 9v6h4l5 5V4L7 9H3zm13.5 3A4.5 4.5 0 0 0 14 8v8a4.47 4.47 0 0 0 2.5-4zM14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77z"/></svg>`;
  const MUTE_ICON_SVG = `<svg viewBox="0 0 24 24" width="19" height="19" fill="currentColor"><path d="M12 14c1.66 0 3-1.34 3-3V5c0-1.66-1.34-3-3-3S9 3.34 9 5v6c0 1.66 1.34 3 3 3zm5.3-3c0 3-2.54 5.1-5.3 5.1S6.7 14 6.7 11H5c0 3.41 2.72 6.23 6 6.72V21h2v-3.28c3.28-.48 6-3.3 6-6.72h-1.7z"/></svg>`;
  const HOLD_ICON_SVG = `<svg viewBox="0 0 24 24" width="19" height="19" fill="currentColor"><path d="M6 19h4V5H6v14zm8-14v14h4V5h-4z"/></svg>`;

  // Speaker / mute / hold — only offered once the call is actually connected (nothing to
  // mute or put on speaker while it's still ringing). Speaker and mute light up from the phone's
  // real reported state; hold has no readable state, so it's a plain button.
  function callToolButtonsHtml(state) {
    if (!state || !state.callAnswered) return '';
    return `
      <button class="call-bar-btn call-bar-btn-tool${state.callSpeaker ? ' active' : ''}" data-call-control="speaker" title="Altavoz">${SPEAKER_ICON_SVG}</button>
      <button class="call-bar-btn call-bar-btn-tool${state.callMuted ? ' active' : ''}" data-call-control="mute" title="${state.callMuted ? 'Activar micrófono' : 'Silenciar'}">${MUTE_ICON_SVG}</button>
      <button class="call-bar-btn call-bar-btn-tool" data-call-control="hold" title="Poner en espera">${HOLD_ICON_SVG}</button>`;
  }

  function renderCallBar() {
    const state = activeState();
    const callItem = findCallItem(state);

    if (callItem) {
      const ringing = isCallRinging(callItem) && !(state && state.callAnswered);
      if (callBarTimerKey !== callItem.key) {
        // A different call than whatever we were tracking — never inherit a stale answered-at
        // stamp from a previous one.
        callBarTimerKey = callItem.key;
        callAnsweredAt = null;
      }
      clearInterval(callBarTimerInterval);
      if (ringing) {
        callAnsweredAt = null; // still not answered — no timer, see the doc comment above
      } else {
        if (callAnsweredAt === null) callAnsweredAt = (state && state.callAnsweredAt) || Date.now(); // ringing→answered transition (the phone's real timer start when it reported one)
        callBarTimerInterval = setInterval(() => {
          const timerEl = callBar.querySelector('.call-bar-timer');
          if (timerEl) timerEl.textContent = formatCallDuration(callAnsweredAt);
        }, 1000);
      }
      const avatarSrc = callItem.avatarDataUrl || callItem.iconDataUrl;
      const avatarHtml = avatarSrc
        ? `<img class="call-bar-avatar" src="${avatarSrc}">`
        : `<div class="call-bar-avatar call-bar-avatar-placeholder">📞</div>`;
      const { normal } = splitMuteAction(callItem.actions || []);
      const actionsHtml = normal.map(({ action: a, index: i }) => callActionBtnHtml(a, i)).join('') + callToolButtonsHtml(state);
      // No dedicated "text" field on the wire DTO — displayLines is the same body-text source
      // every other non-messaging card already renders from (see cardInnerHtml).
      const subText = (callItem.displayLines && callItem.displayLines[0]) || '';
      // Ringing shows no timer at all — a static "Llamada entrante" label instead of a number
      // that would otherwise look like the call is already connected.
      const timerHtml = ringing
        ? `<div class="call-bar-timer call-bar-timer-ringing">Llamada entrante</div>`
        : `<div class="call-bar-timer">${formatCallDuration(callAnsweredAt)}</div>`;
      callBar.innerHTML = `
        <div class="call-bar-rich">
          ${avatarHtml}
          <div class="call-bar-info">
            <div class="call-bar-name">${escapeHtml(callItem.title || callItem.appLabel)}</div>
            <div class="call-bar-sub">${escapeHtml(subText)}</div>
          </div>
          ${timerHtml}
          <div class="call-bar-actions">${actionsHtml}</div>
        </div>`;
      callBar.dataset.key = callItem.key;
      callBar.classList.add('visible');
    } else {
      clearInterval(callBarTimerInterval);
      callBarTimerKey = null;
      callAnsweredAt = null;
      if (state && state.callActive) {
        // Before the phone reports the call connected (its on-screen timer appearing), this is
        // ringing/dialing — Contestar is offered and no timer runs. After, the timer counts real
        // talk time and the speaker/mute/hold tools appear.
        const answered = state.callAnswered;
        const label = answered
          ? `Llamada en curso <span class="call-bar-timer">${formatCallDuration(state.callAnsweredAt)}</span>`
          : `Llamada sonando… (${escapeHtml(state.callDeviceName || '')})`;
        callBar.innerHTML = `
          <div class="call-bar-simple">
            <span class="call-bar-icon">📞</span>
            <span class="call-bar-text">${label}</span>
            <div class="call-bar-actions">
              ${answered ? callToolButtonsHtml(state) : `<button class="call-bar-btn call-bar-btn-answer" data-call-control="answer" title="Contestar">${phoneIconSvg(false)}</button>`}
              <button class="call-bar-btn call-bar-btn-hangup" data-call-control="hangup" title="Colgar / Rechazar">${phoneIconSvg(true)}</button>
            </div>
          </div>`;
        if (answered) {
          clearInterval(callBarTimerInterval);
          callBarTimerInterval = setInterval(() => {
            const timerEl = callBar.querySelector('.call-bar-text .call-bar-timer');
            if (timerEl) timerEl.textContent = formatCallDuration(state.callAnsweredAt);
          }, 1000);
        }
        callBar.dataset.key = '';
        callBar.classList.add('visible');
      } else {
        callBar.innerHTML = '';
        callBar.dataset.key = '';
        callBar.classList.remove('visible');
      }
    }
  }

  // Answer/hang up for the bare-Telecom case (no notification, so no real action to invoke) —
  // asks the phone itself to do it, see docs/PROTOCOL.md's call.control.
  callBar.addEventListener('click', function (e) {
    const ctl = e.target.closest('[data-call-control]');
    if (ctl && activeDeviceId) {
      postToHost({ action: 'callControl', deviceId: activeDeviceId, control: ctl.dataset.callControl });
      return;
    }
    const actionBtn = e.target.closest('.action-btn, .call-bar-btn');
    const key = callBar.dataset.key;
    if (actionBtn && key && activeDeviceId) {
      const actionIndex = parseInt(actionBtn.dataset.actionIndex, 10);
      postToHost({ action: 'invoke', deviceId: activeDeviceId, key, actionIndex, replyText: null });
    }
  });

  // A notification.removed from the phone — covers a dismissal that happened on the phone
  // itself, and (since "borrar todas" cancels each one individually rather than as a single
  // bulk event, see docs/PROTOCOL.md's delete.request.all) every card removed via "Borrar
  // todas" too. Shares the exact same animate-then-remove path as the PC's own delete button
  // (see removeCardWithAnimation below) so every removal path looks consistent, regardless of
  // which side actually triggered it.
  window.removeNotification = function (deviceId, key) {
    removeCardWithAnimation(deviceId, key, 'phone');
  };

  // Reconciles a gap window.removeNotification alone can't cover: a notification dismissed on
  // the phone while it was genuinely disconnected (e.g. Quick Share briefly switching its
  // network) has no connection to send that removal over, so the PC never finds out and the card
  // just sits there forever — deleting it does nothing since the phone has no record of that key
  // any more either. [keys] is the phone's complete current set of active notification keys,
  // sent once per (re)connect (see docs/PROTOCOL.md's notification.sync) — anything this device
  // is showing that ISN'T in that set gets removed. Synthetic "share-" cards (shared text/files
  // from the phone) are never touched here — the phone was never going to know about those in
  // the first place, so their absence from [keys] means nothing.
  window.syncNotificationKeys = function (deviceId, keys) {
    const state = getDeviceState(deviceId);
    const keySet = new Set(keys);
    const stale = state.order.filter(k => !k.startsWith(SHARED_CARD_KEY_PREFIX) && !keySet.has(k));
    stale.forEach(k => removeCardWithAnimation(deviceId, k, 'sync'));
  };

  // Deleting from the PC is optimistic: the card animates and disappears from the list right
  // on click, locally, rather than waiting on the phone's round-trip confirmation (which arrives
  // at unpredictable network-dependent timing — animating on that instead would make the delay
  // itself visible). The real delete request is still sent to the phone as always; when its
  // notification.removed confirmation lands later, window.removeNotification above calls back
  // into this same function, finds the key already gone, and no-ops.
  // [reason] is recorded in the notification history: 'phone' (gone on the phone — read or
  // dismissed there), 'pc' (× on the PC), 'pc-all' (Borrar todas), 'pc-file' (received file
  // deleted), 'sync' (reconciled away after a reconnect).
  function removeCardWithAnimation(deviceId, key, reason = 'phone') {
    const state = getDeviceState(deviceId);
    if (!state.data.has(key)) return;
    recordHistoryRemoval(deviceId, state.data.get(key), reason);
    const wasCall = state.data.get(key).style === 'call';
    const cardEl = settings.cardAnimations && deviceId === activeDeviceId
      ? listEl.querySelector(`.card[data-key="${CSS.escape(key)}"]`)
      : null;
    if (!cardEl) {
      state.data.delete(key);
      state.order = state.order.filter(k => k !== key);
      if (deviceId === activeDeviceId) { renderAll(); if (wasCall) renderCallBar(); notifyCountChanged(); }
      return;
    }
    cardEl.classList.add('deleting');
    setTimeout(() => {
      state.data.delete(key);
      state.order = state.order.filter(k => k !== key);
      if (deviceId === activeDeviceId) { renderAll(); if (wasCall) renderCallBar(); notifyCountChanged(); }
    }, 220);
  }

  // App icons are shared across every device (the same WhatsApp icon looks the
  // same regardless of which phone reported it) — this is the one exception to
  // the per-device split, so it updates every device's matching entries.
  window.setIcon = function (packageName, dataUrl) {
    for (const state of deviceStates.values()) {
      for (const v of state.data.values()) {
        if (v.packageName === packageName) v.iconDataUrl = dataUrl;
      }
      for (const a of state.appList) {
        if (a.packageName === packageName) a.iconDataUrl = dataUrl;
      }
    }
    renderAll();
    if (appsOverlay.classList.contains('open')) renderAppsList();
  };

  window.setAppList = function (deviceId, apps) {
    const state = getDeviceState(deviceId);
    state.appList = apps;
    if (deviceId === activeDeviceId && appsOverlay.classList.contains('open')) renderAppsList();
  };

  window.setAppAllowedState = function (deviceId, packageName, allowed) {
    const state = getDeviceState(deviceId);
    const entry = state.appList.find(a => a.packageName === packageName);
    if (entry) {
      entry.allowed = allowed;
      if (deviceId === activeDeviceId && appsOverlay.classList.contains('open')) renderAppsList();
    }
  };

  window.setAvatar = function (deviceId, key, dataUrl) {
    const state = getDeviceState(deviceId);
    const v = state.data.get(key);
    if (!v) return;
    v.avatarDataUrl = dataUrl;
    scheduleHistorySave(deviceId, key);
    if (deviceId === activeDeviceId) {
      renderAll();
      // The avatar transfer for a call's own item lands asynchronously, after the call bar
      // already rendered from the icon fallback — without this it would just keep showing that
      // fallback for the whole call instead of picking up the real photo once it arrives.
      if (v.style === 'call') renderCallBar();
    }
  };

  window.setImage = function (deviceId, key, dataUrl) {
    const state = getDeviceState(deviceId);
    const v = state.data.get(key);
    if (!v) return;
    v.imageDataUrl = dataUrl;
    scheduleHistorySave(deviceId, key);
    if (deviceId === activeDeviceId) renderAll();
  };

  window.setMessageImage = function (deviceId, key, transferId, dataUrl) {
    const state = getDeviceState(deviceId);
    const v = state.data.get(key);
    if (!v || !v.messages) return;
    const m = v.messages.find(x => x.imageTransferId === transferId);
    if (!m) return;
    m.imageDataUrl = dataUrl;
    scheduleHistorySave(deviceId, key);
    if (deviceId === activeDeviceId) renderAll();
  };

  function applyTheme(dark) {
    document.body.classList.toggle('dark', !!dark);
    themeFab.textContent = dark ? '☀️' : '🌙';
    themeFab.title = dark ? 'Modo claro' : 'Modo oscuro';
  }

  window.setBattery = function (deviceId, level, charging) {
    const state = getDeviceState(deviceId);
    state.battery = { level, charging };
    if (deviceId === activeDeviceId) {
      batteryLabel.textContent = `${charging ? '⚡' : '🔋'} ${level}%`;
      batteryLabel.title = charging ? 'Cargando' : 'Batería';
    }
  };

  // 4 bars, increasing height — filled up to `level` (0-4), the rest dim. Built as an SVG
  // rather than an emoji so it can actually show signal strength, not just "there is wifi".
  // The actual Wi-Fi glyph (radiating arcs + dot), not a generic signal-bars icon —
  // level 0-4 progressively lights up the dot, then each arc from innermost outward.
  function wifiIconSvg(level) {
    const op = on => on ? 1 : 0.25;
    return `<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round">
      <path d="M8.53 16.11a6 6 0 0 1 6.95 0" opacity="${op(level >= 2)}"/>
      <path d="M5 12.55a11 11 0 0 1 14.08 0" opacity="${op(level >= 3)}"/>
      <path d="M1.42 9a16 16 0 0 1 21.16 0" opacity="${op(level >= 4)}"/>
      <circle cx="12" cy="20" r="1.4" fill="currentColor" stroke="none" opacity="${op(level >= 1)}"/>
    </svg>`;
  }

  // Cellular ("líneas"): one small bars icon per active SIM line — dual-SIM phones can report
  // two independent signals, both shown side by side with the carrier name in the tooltip.
  function cellularIconSvg(level) {
    const heights = [5, 8, 11, 14];
    const bars = heights.map((h, i) => {
      const x = i * 4.5;
      const y = 16 - h;
      return `<rect x="${x}" y="${y}" width="3" height="${h}" rx="0.8" fill="currentColor" opacity="${i < level ? 1 : 0.25}"/>`;
    }).join('');
    return `<svg viewBox="0 0 16 16" width="14" height="14">${bars}</svg>`;
  }

  window.setWifi = function (deviceId, ssid, level) {
    const state = getDeviceState(deviceId);
    state.wifi = { ssid, level };
    if (deviceId === activeDeviceId) renderWifiLabel();
  };

  function renderWifiLabel() {
    const state = activeState();
    const wifi = state && state.wifi;
    if (!wifi || wifi.level == null) {
      wifiLabel.innerHTML = '';
      wifiLabel.title = '';
      return;
    }
    wifiLabel.innerHTML = wifiIconSvg(wifi.level);
    wifiLabel.title = `Wi-Fi: ${wifi.ssid || 'red desconocida'} — intensidad ${wifi.level}/4`;
  }

  window.setCellular = function (deviceId, lines) {
    getDeviceState(deviceId).cellular = lines || [];
    if (deviceId === activeDeviceId) renderCellularLabel();
  };

  function renderCellularLabel() {
    const state = activeState();
    const lines = (state && state.cellular) || [];
    cellularLabel.innerHTML = lines.map(line => `
      <span class="cellular-line" title="${escapeHtml(line.carrierName)} — intensidad ${line.level}/4">
        ${cellularIconSvg(line.level)}
      </span>`).join('');
  }

  // Renders the device segmented control (hidden when no devices are paired yet) and
  // keeps the connection dot and battery label in sync with whichever device is active.
  function loadDeviceOrder() {
    try { const o = JSON.parse(localStorage.getItem(DEVICE_ORDER_KEY) || '[]'); return Array.isArray(o) ? o : []; } catch (e) { return []; }
  }
  // Tabs in the user's order; phones not placed yet keep their pairing order, after the rest.
  function applyDeviceOrder() {
    const order = loadDeviceOrder();
    const rank = id => { const i = order.indexOf(id); return i < 0 ? Number.MAX_SAFE_INTEGER : i; };
    devices = devices.map((d, i) => [d, i]).sort((a, b) => (rank(a[0].deviceId) - rank(b[0].deviceId)) || (a[1] - b[1])).map(x => x[0]);
  }
  function saveDeviceOrder() {
    try { localStorage.setItem(DEVICE_ORDER_KEY, JSON.stringify(devices.map(d => d.deviceId))); } catch (e) { }
  }
  function moveDevice(deviceId, toIndex) {
    const from = devices.findIndex(d => d.deviceId === deviceId);
    if (from < 0) return;
    const [d] = devices.splice(from, 1);
    devices.splice(Math.max(0, Math.min(toIndex, devices.length)), 0, d);
    saveDeviceOrder();
    renderDeviceSegments();
  }

  window.setDeviceList = function (list) {
    devices = list || [];
    applyDeviceOrder();
    if (!activeDeviceId || !devices.some(d => d.deviceId === activeDeviceId)) {
      activeDeviceId = devices.length ? devices[0].deviceId : null;
    }
    if (activeDeviceId) localStorage.setItem(ACTIVE_DEVICE_KEY, activeDeviceId);

    // A device can disappear from the list without ever firing removeNotification for its
    // notifications (e.g. eliminado on the PC) — drop its state here too. syncActiveDeviceUi()
    // below re-sends the badge count regardless, so no separate notify is needed here.
    for (const id of Array.from(deviceStates.keys())) {
      if (!devices.some(d => d.deviceId === id)) deviceStates.delete(id);
    }

    syncActiveDeviceUi();
  };

  function renderDeviceSegments() {
    const onPeer = peerTabActive();
    deviceSegmentGroup.style.display = devices.length || peers.length ? 'flex' : 'none';
    deviceSegmentGroup.innerHTML = devices.map(d => `
      <div class="device-segment${!onPeer && d.deviceId === activeDeviceId ? ' active' : ''}" data-device-id="${d.deviceId}"${devices.length > 1 ? ' draggable="true" title="Arrastra para cambiar el orden"' : ''}>
        <span class="segment-dot${d.connected ? ' connected' : ''}"></span>
        ${badgeHtml(d)}
        <span class="segment-label">${escapeHtml(d.name)}</span>
      </div>`).join('') + peers.map(p => {
        const unread = peerUnread.get(p.pcId) || 0;
        return `
      <div class="device-segment peer-segment${onPeer && p.pcId === activePeerTab ? ' active' : ''}" data-peer-id="${escapeHtml(p.pcId)}" title="PC vinculada">
        <span class="segment-dot${p.connected ? ' connected' : ''}"></span>
        <svg class="peer-segment-icon" viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="2" y="4" width="20" height="13" rx="2"/><path d="M8 21h8M12 17v4"/></svg>
        <span class="segment-label">${escapeHtml(p.name)}</span>
        ${unread ? `<span class="peer-segment-unread">${unread > 9 ? '9+' : unread}</span>` : ''}
      </div>`;
      }).join('');
  }

  function badgeHtml(d, extraClass = '') {
    if (!d || !d.badgeText) return '';
    const color = /^#[0-9a-fA-F]{6}$/.test(d.badgeColor || '') ? d.badgeColor : '#7C5CFF';
    return `<span class="device-badge ${extraClass}" style="--badge:${color}" title="Identificador de ${escapeHtml(d.name).replace(/"/g, '&quot;')}">${escapeHtml(d.badgeText)}</span>`;
  }

  // ---- Identificador: the letter/number + color shown before the name and on taskbar icons ----
  const BADGE_COLORS = ['#7C5CFF', '#E53935', '#2E7D32', '#F57C00', '#1565C0', '#8E24AA', '#00897B', '#D81B60', '#6D4C41', '#455A64'];
  const badgeOverlay = document.getElementById('badge-overlay');
  const badgePreview = document.getElementById('badge-preview');
  const badgePreviewName = document.getElementById('badge-preview-name');
  const badgeTextInput = document.getElementById('badge-text-input');
  const badgeSwatches = document.getElementById('badge-swatches');
  let badgeEditing = null; // { deviceId, color }

  function renderBadgeEditor() {
    const text = (badgeTextInput.value || '').replace(/[^\p{L}\p{N}]/gu, '').slice(0, 2).toUpperCase();
    badgePreview.textContent = text || '?';
    badgePreview.style.setProperty('--badge', badgeEditing.color);
    const custom = !BADGE_COLORS.includes(badgeEditing.color.toUpperCase());
    badgeSwatches.innerHTML = BADGE_COLORS.map(c =>
      `<button type="button" class="badge-swatch${c === badgeEditing.color.toUpperCase() ? ' selected' : ''}" data-color="${c}" style="background:${c}" title="${c}"></button>`
    ).join('') + `<label class="badge-swatch badge-swatch-custom${custom ? ' selected' : ''}" title="Otro color"${custom ? ` style="background:${badgeEditing.color}"` : ''}><input type="color" value="${badgeEditing.color}"></label>`;
  }

  function openBadgeEditor(deviceId) {
    const d = devices.find(x => x.deviceId === deviceId);
    if (!d) return;
    badgeEditing = { deviceId, color: (d.badgeColor || '#7C5CFF').toUpperCase() };
    badgeTextInput.value = d.badgeText || '';
    badgePreviewName.textContent = d.name;
    renderBadgeEditor();
    badgeOverlay.classList.add('open');
    setTimeout(() => { badgeTextInput.focus(); badgeTextInput.select(); }, 50);
  }
  function closeBadgeEditor() { badgeOverlay.classList.remove('open'); badgeEditing = null; }

  badgeTextInput.addEventListener('input', () => {
    const clean = badgeTextInput.value.replace(/[^\p{L}\p{N}]/gu, '').slice(0, 2).toUpperCase();
    if (clean !== badgeTextInput.value) badgeTextInput.value = clean;
    renderBadgeEditor();
  });
  badgeTextInput.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); document.getElementById('badge-save').click(); } });
  badgeSwatches.addEventListener('click', e => {
    const sw = e.target.closest('.badge-swatch[data-color]');
    if (!sw || !badgeEditing) return;
    badgeEditing.color = sw.dataset.color;
    renderBadgeEditor();
  });
  badgeSwatches.addEventListener('input', e => {
    if (e.target.type !== 'color' || !badgeEditing) return;
    badgeEditing.color = e.target.value.toUpperCase();
    badgePreview.style.setProperty('--badge', badgeEditing.color);
    const label = e.target.closest('.badge-swatch-custom');
    if (label) label.style.background = badgeEditing.color;
  });
  badgeSwatches.addEventListener('change', e => { if (e.target.type === 'color' && badgeEditing) renderBadgeEditor(); });
  document.getElementById('badge-save').addEventListener('click', () => {
    if (!badgeEditing) return;
    const text = (badgeTextInput.value || '').trim();
    if (!text) { showToast('Escribe una letra o número'); badgeTextInput.focus(); return; }
    postToHost({ action: 'setDeviceBadge', deviceId: badgeEditing.deviceId, text, color: badgeEditing.color });
    showToast('Identificador guardado');
    closeBadgeEditor();
  });
  document.getElementById('badge-reset').addEventListener('click', () => {
    if (!badgeEditing) return;
    postToHost({ action: 'setDeviceBadge', deviceId: badgeEditing.deviceId, text: '', color: '' });
    showToast('Identificador automático');
    closeBadgeEditor();
  });
  document.getElementById('badge-close').addEventListener('click', closeBadgeEditor);
  badgeOverlay.addEventListener('click', e => { if (e.target === badgeOverlay) closeBadgeEditor(); });

  // Renaming still edits inline (replaces the segment's label with a text input), just
  // triggered from the "..." menu now instead of a pencil icon living in the segment itself.
  function startRename(deviceId) {
    const segment = deviceSegmentGroup.querySelector(`.device-segment[data-device-id="${deviceId}"]`);
    if (!segment) return;
    const labelEl = segment.querySelector('.segment-label');
    const device = devices.find(d => d.deviceId === deviceId);
    const current = device ? device.name : '';
    const input = document.createElement('input');
    input.type = 'text';
    input.className = 'segment-rename-input';
    input.maxLength = 40;
    input.value = current;
    labelEl.replaceWith(input);
    input.focus();
    input.select();

    function commit() {
      const name = input.value.trim();
      if (name && name !== current) {
        if (device) device.name = name;
        postToHost({ action: 'renameDevice', deviceId, name });
      }
      renderDeviceSegments();
    }
    input.addEventListener('blur', commit);
    input.addEventListener('keydown', function (e) {
      if (e.key === 'Enter') { e.preventDefault(); input.blur(); }
      if (e.key === 'Escape') { e.preventDefault(); renderDeviceSegments(); }
    });
    input.addEventListener('click', e => e.stopPropagation());
  }

  // Double-click a phone's tab to rename it right there (same as "..." > Renombrar), so a phone
  // is easy to tell apart on the PC.
  deviceSegmentGroup.addEventListener('dblclick', e => {
    const segment = e.target.closest('.device-segment:not(.peer-segment)');
    if (!segment || segment.querySelector('.segment-rename-input')) return;
    e.preventDefault();
    startRename(segment.dataset.deviceId);
  });

  // Drag a tab sideways to reorder; the drop marker shows on the side it will land.
  let draggingDeviceId = null;
  function clearDropMarkers() {
    deviceSegmentGroup.querySelectorAll('.drop-before, .drop-after').forEach(el => el.classList.remove('drop-before', 'drop-after'));
  }
  deviceSegmentGroup.addEventListener('dragstart', e => {
    const segment = e.target.closest('.device-segment');
    if (!segment || segment.querySelector('.segment-rename-input')) { e.preventDefault(); return; }
    draggingDeviceId = segment.dataset.deviceId;
    segment.classList.add('dragging');
    e.dataTransfer.effectAllowed = 'move';
    try { e.dataTransfer.setData('text/plain', draggingDeviceId); } catch (err) { }
  });
  deviceSegmentGroup.addEventListener('dragover', e => {
    if (!draggingDeviceId) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
    const segment = e.target.closest('.device-segment:not(.peer-segment)');
    clearDropMarkers();
    if (!segment || segment.dataset.deviceId === draggingDeviceId) return;
    const r = segment.getBoundingClientRect();
    segment.classList.add(e.clientX < r.left + r.width / 2 ? 'drop-before' : 'drop-after');
  });
  deviceSegmentGroup.addEventListener('dragleave', e => {
    if (!deviceSegmentGroup.contains(e.relatedTarget)) clearDropMarkers();
  });
  deviceSegmentGroup.addEventListener('drop', e => {
    if (!draggingDeviceId) return;
    e.preventDefault();
    const segment = e.target.closest('.device-segment:not(.peer-segment)');
    const dragged = draggingDeviceId;
    clearDropMarkers();
    if (!segment || segment.dataset.deviceId === dragged) return;
    const r = segment.getBoundingClientRect();
    const after = e.clientX >= r.left + r.width / 2;
    const rest = devices.filter(d => d.deviceId !== dragged);
    const target = rest.findIndex(d => d.deviceId === segment.dataset.deviceId);
    moveDevice(dragged, target + (after ? 1 : 0));
  });
  deviceSegmentGroup.addEventListener('dragend', () => {
    draggingDeviceId = null;
    clearDropMarkers();
    deviceSegmentGroup.querySelectorAll('.dragging').forEach(el => el.classList.remove('dragging'));
  });

  deviceSegmentGroup.addEventListener('click', function (e) {
    const segment = e.target.closest('.device-segment');
    if (!segment) return;
    if (segment.dataset.peerId) { selectPeerTab(segment.dataset.peerId); return; }
    const deviceId = segment.dataset.deviceId;
    const fromPeer = peerTabActive();
    if (fromPeer) setActivePeerTab(null);
    // Clicking the identifier of the already-selected phone edits it.
    if (!fromPeer && e.target.closest('.device-badge') && deviceId === activeDeviceId) { openBadgeEditor(deviceId); return; }
    if (fromPeer || deviceId !== activeDeviceId) {
      activeDeviceId = deviceId;
      localStorage.setItem(ACTIVE_DEVICE_KEY, activeDeviceId);
      syncActiveDeviceUi();
    }
  });

  function setActivePeerTab(pcId) {
    activePeerTab = pcId;
    try {
      if (pcId) localStorage.setItem(ACTIVE_PEER_TAB_KEY, pcId); else localStorage.removeItem(ACTIVE_PEER_TAB_KEY);
    } catch (_) { /* storage unavailable */ }
  }

  function selectPeerTab(pcId) {
    if (peerTabActive() && activePeerTab === pcId) return;
    setActivePeerTab(pcId);
    peerUnread.delete(pcId);
    if (!peerChats.has(pcId)) postToHost({ action: 'peerHistory', pcId });
    updatePcsBadge();
    syncActiveDeviceUi();
  }

  // On a PC tab the header describes that PC; every phone-only control (battery, Wi-Fi, sharing,
  // volume, now-playing, call bar, …) is hidden through body.peer-tab in theme.css.
  function renderPeerTabChrome() {
    const onPeer = peerTabActive();
    document.body.classList.toggle('peer-tab', onPeer);
    document.querySelector('#conn-label .conn-label-icon').textContent = onPeer ? '🖥️' : '📱';
    deleteAllBtn.title = onPeer ? 'Quitar todas las tarjetas de esta PC (el chat se conserva)' : 'Borrar todas las notificaciones';
    if (!onPeer) return;
    const p = peers.find(x => x.pcId === activePeerTab);
    connLabelDot.classList.toggle('connected', !!p.connected);
    connLabelText.textContent = p.connected
      ? `PC conectada${p.address ? ' · ' + p.address : ''}`
      : (p.needsRepair ? 'Hay que volver a vincularla' : 'PC sin conexión — reconectando...');
  }

  function syncActiveDeviceUi() {
    const active = devices.find(d => d.deviceId === activeDeviceId);

    connLabelDot.classList.toggle('connected', !!(active && active.connected));
    const state = activeState();
    const net = state && state.network;
    connLabelText.textContent = active
      ? (active.connected
          ? 'Conectado' + (net ? (net.route === 'vpn' ? ' · Tailscale (remoto)' : '') + (net.metered ? ' · datos móviles' : '') : '')
          : 'Desconectado\nreconectando...')
      : 'Sin dispositivo vinculado';

    const battery = state && state.battery.level != null ? state.battery : null;
    batteryLabel.textContent = battery ? `${battery.charging ? '⚡' : '🔋'} ${battery.level}%` : '';
    batteryLabel.title = battery && battery.charging ? 'Cargando' : 'Batería';
    renderWifiLabel();
    renderCellularLabel();

    deviceMenuBtn.disabled = !active;
    deviceMenuRename.disabled = !active;
    deviceMenuApps.disabled = !active;
    deviceMenuWallpaper.disabled = !active;
    deviceMenuShare.disabled = !active;
    screenShareBtn.disabled = !active || !active.connected;
    audioShareBtn.disabled = !active || !active.connected;
    volumeBtn.disabled = !active || !active.connected;
    dndBtn.disabled = !active || !active.connected;
    rotationBtn.disabled = !active || !active.connected;
    deviceMenuRing.disabled = !active || !active.connected;
    deviceMenuRemove.disabled = !active;
    deviceMenuPower.disabled = !active || !active.connected;
    document.getElementById('device-menu-power-label').textContent = 'Desconectar' + (active && !active.connected ? ' (sin conexión)' : '');

    renderDeviceSegments();
    renderAll();
    applyWallpaper();
    renderNowPlaying();
    renderCallBar();
    renderVolume();
    renderDnd();
    renderRotation();
    renderShareButtons();
    renderPeerTabChrome();
    notifyCountChanged();
    if (appsOverlay.classList.contains('open')) renderAppsList();
  }

  // --- Volume popup: real STREAM_MUSIC level for the active device, via AudioManager on the
  // phone (see docs/PROTOCOL.md's volume.state/volume.set) — not a local-only fake slider. ---
  volumeBtn.addEventListener('click', e => {
    e.stopPropagation();
    volumeMenu.classList.toggle('open');
  });
  document.addEventListener('click', () => volumeMenu.classList.remove('open'));
  volumeMenu.addEventListener('click', e => e.stopPropagation());

  let volumeDragging = false;

  function renderVolume() {
    const state = activeState();
    const volume = state ? state.volume : { level: 0, max: 0 };
    volumeSlider.max = volume.max;
    if (!volumeDragging) volumeSlider.value = volume.level;
    const pct = volume.max > 0 ? Math.round((volume.level / volume.max) * 100) : 0;
    volumeLabel.textContent = `${pct}%`;
    volumeSlider.style.setProperty('--volume-pct', `${pct}%`);
  }

  window.setVolume = function (deviceId, level, max) {
    getDeviceState(deviceId).volume = { level, max };
    if (deviceId === activeDeviceId) renderVolume();
  };

  // --- "No molestar" — a plain on/off toggle, not a popup like volume, so it just lights up
  // solid when active (see docs/PROTOCOL.md's dnd.state/dnd.set). Silently no-ops on the phone
  // if Notification Policy Access was never granted — nothing the PC side can detect, so the
  // button just won't visibly change state in that case, same as any other unresponsive toggle
  // would look. ---
  function renderDnd() {
    const state = activeState();
    dndBtn.classList.toggle('active', !!(state && state.dnd));
    dndBtn.title = state && state.dnd ? 'No molestar activo (toca para desactivar)' : 'No molestar del celular';
  }

  window.setDnd = function (deviceId, active) {
    getDeviceState(deviceId).dnd = active;
    if (deviceId === activeDeviceId) renderDnd();
  };

  dndBtn.addEventListener('click', () => {
    if (!activeDeviceId) return;
    const state = activeState();
    const next = !(state && state.dnd);
    postToHost({ action: 'setDnd', deviceId: activeDeviceId, active: next });
  });

  // Auto-rotate toggle — same lit-when-on pattern as No molestar, from the phone's real setting
  // (see docs/PROTOCOL.md's rotation.state/rotation.set). Needs the phone's one-time "Modificar
  // ajustes del sistema" grant; without it the press is ignored and the button doesn't change.
  function renderRotation() {
    const state = activeState();
    const on = !!(state && state.autoRotate);
    rotationBtn.classList.toggle('active', on);
    rotationBtn.title = on ? 'Rotación automática activa (toca para bloquear)' : 'Rotación bloqueada (toca para activar)';
  }

  window.setRotation = function (deviceId, active) {
    getDeviceState(deviceId).autoRotate = active;
    if (deviceId === activeDeviceId) renderRotation();
  };

  rotationBtn.addEventListener('click', () => {
    if (!activeDeviceId) return;
    const state = activeState();
    postToHost({ action: 'setRotation', deviceId: activeDeviceId, active: !(state && state.autoRotate) });
  });

  volumeSlider.addEventListener('pointerdown', () => { volumeDragging = true; });
  volumeSlider.addEventListener('input', () => {
    const max = parseInt(volumeSlider.max, 10) || 0;
    const value = parseInt(volumeSlider.value, 10);
    const pct = max > 0 ? Math.round((value / max) * 100) : 0;
    volumeLabel.textContent = `${pct}%`;
    volumeSlider.style.setProperty('--volume-pct', `${pct}%`);
  });
  volumeSlider.addEventListener('change', () => {
    volumeDragging = false;
    if (activeDeviceId) postToHost({ action: 'setVolume', deviceId: activeDeviceId, level: parseInt(volumeSlider.value, 10) });
  });

  // --- Active transmissions pill (see renderActiveShares) ---
  const activeSharesBtn = document.getElementById('active-shares-btn');
  const activeSharesMenu = document.getElementById('active-shares-menu');
  activeSharesBtn.addEventListener('click', e => { e.stopPropagation(); activeSharesMenu.classList.toggle('open'); });
  document.addEventListener('click', () => activeSharesMenu.classList.remove('open'));
  activeSharesMenu.addEventListener('click', e => {
    e.stopPropagation();
    if (!activeDeviceId) return;
    if (e.target.closest('.active-share-stop-all')) {
      postToHost({ action: 'stopAllShares', deviceId: activeDeviceId });
      activeSharesMenu.classList.remove('open');
      return;
    }
    const ret = e.target.closest('.active-share-return');
    if (ret) { postToHost({ action: 'returnAppShare', deviceId: activeDeviceId, packageName: ret.dataset.package }); return; }
    const btn = e.target.closest('.active-share-stop');
    if (!btn) return;
    if (btn.dataset.kind === 'screen') postToHost({ action: 'stopScreenShare', deviceId: activeDeviceId });
    else if (btn.dataset.kind === 'audio') {
      postToHost({ action: 'stopAudioShare', deviceId: activeDeviceId });
      activeState().audioSharing = false;
      renderShareButtons();
    } else postToHost({ action: 'stopAppShare', deviceId: activeDeviceId, packageName: btn.dataset.package });
  });

  // --- Device "..." menu: rename / apps / disconnect / remove, all acting on activeDeviceId ---
  const deviceMenuMoveLeft = document.getElementById('device-menu-move-left');
  const deviceMenuMoveRight = document.getElementById('device-menu-move-right');
  deviceMenuBtn.addEventListener('click', e => {
    e.stopPropagation();
    const i = devices.findIndex(d => d.deviceId === activeDeviceId);
    deviceMenuMoveLeft.style.display = i > 0 ? '' : 'none';
    deviceMenuMoveRight.style.display = i >= 0 && i < devices.length - 1 ? '' : 'none';
    deviceMenu.classList.toggle('open');
  });
  deviceMenuMoveLeft.addEventListener('click', () => {
    deviceMenu.classList.remove('open');
    const i = devices.findIndex(d => d.deviceId === activeDeviceId);
    if (i > 0) moveDevice(activeDeviceId, i - 1);
  });
  deviceMenuMoveRight.addEventListener('click', () => {
    deviceMenu.classList.remove('open');
    const i = devices.findIndex(d => d.deviceId === activeDeviceId);
    if (i >= 0 && i < devices.length - 1) moveDevice(activeDeviceId, i + 1);
  });
  document.addEventListener('click', () => deviceMenu.classList.remove('open'));
  deviceMenu.addEventListener('click', e => e.stopPropagation());

  document.getElementById('device-menu-badge').addEventListener('click', () => {
    deviceMenu.classList.remove('open');
    if (activeDeviceId) openBadgeEditor(activeDeviceId);
  });
  deviceMenuRename.addEventListener('click', () => {
    deviceMenu.classList.remove('open');
    if (activeDeviceId) startRename(activeDeviceId);
  });
  deviceMenuApps.addEventListener('click', () => {
    deviceMenu.classList.remove('open');
    appsOverlay.classList.add('open');
    renderAppsList();
  });

  // --- Custom wallpaper picker: a user-chosen image, applied through applyWallpaper() exactly
  // like the phone's own auto-extracted wallpaper (same element, same blur/opacity CSS — see
  // customWallpaperKey above). Downscaled before storing: it's only ever shown heavily
  // blurred at low opacity, so keeping the original full-resolution photo would just waste
  // localStorage space for zero visible benefit. ---
  function downscaleImageFile(file, maxDim, quality) {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onerror = () => reject(reader.error || new Error('No se pudo leer el archivo'));
      reader.onload = () => {
        const img = new Image();
        img.onerror = () => reject(new Error('No se pudo leer la imagen'));
        img.onload = () => {
          let { width, height } = img;
          if (width > maxDim || height > maxDim) {
            const scale = maxDim / Math.max(width, height);
            width = Math.round(width * scale);
            height = Math.round(height * scale);
          }
          const canvas = document.createElement('canvas');
          canvas.width = width;
          canvas.height = height;
          canvas.getContext('2d').drawImage(img, 0, 0, width, height);
          resolve(canvas.toDataURL('image/jpeg', quality));
        };
        img.src = reader.result;
      };
      reader.readAsDataURL(file);
    });
  }

  const wallpaperPickerOverlay = document.getElementById('wallpaper-picker-overlay');
  const wallpaperPickerClose = document.getElementById('wallpaper-picker-close');
  const wallpaperPickerPreview = document.getElementById('wallpaper-picker-preview');
  const wallpaperPickerInput = document.getElementById('wallpaper-picker-input');
  const wallpaperPickerChoose = document.getElementById('wallpaper-picker-choose');
  const wallpaperPickerClear = document.getElementById('wallpaper-picker-clear');

  function syncWallpaperPickerUi() {
    const current = getCustomWallpaper(activeDeviceId);
    wallpaperPickerPreview.innerHTML = current
      ? `<img src="${current}">`
      : 'Sin fondo personalizado — usando el que llega automáticamente del celular.';
    wallpaperPickerClear.style.display = current ? '' : 'none';
  }

  deviceMenuWallpaper.addEventListener('click', () => {
    deviceMenu.classList.remove('open');
    syncWallpaperPickerUi();
    wallpaperPickerOverlay.classList.add('open');
  });
  wallpaperPickerClose.addEventListener('click', () => wallpaperPickerOverlay.classList.remove('open'));
  wallpaperPickerOverlay.addEventListener('click', e => {
    if (e.target === wallpaperPickerOverlay) wallpaperPickerOverlay.classList.remove('open');
  });
  wallpaperPickerChoose.addEventListener('click', () => wallpaperPickerInput.click());
  wallpaperPickerInput.addEventListener('change', async () => {
    const file = wallpaperPickerInput.files && wallpaperPickerInput.files[0];
    wallpaperPickerInput.value = '';
    if (!file || !activeDeviceId) return;
    try {
      const dataUrl = await downscaleImageFile(file, 1280, 0.75);
      localStorage.setItem(customWallpaperKey(activeDeviceId), dataUrl);
      syncWallpaperPickerUi();
      applyWallpaper();
    } catch (e) {
      showToast('⚠️ No se pudo cargar la imagen');
    }
  });
  wallpaperPickerClear.addEventListener('click', () => {
    if (!activeDeviceId) return;
    localStorage.removeItem(customWallpaperKey(activeDeviceId));
    syncWallpaperPickerUi();
    applyWallpaper();
  });

  deviceMenuShare.addEventListener('click', () => {
    deviceMenu.classList.remove('open');
    if (!activeDeviceId) return;
    shareTextEl.value = '';
    shareOverlay.classList.add('open');
    shareTextEl.focus();
  });
  // Opens the confirmation popup (wired further below, near the other overlays) instead of
  // connecting immediately — the user reviews/adjusts quality and behavior parameters and
  // explicitly approves before scrcpy actually launches. Once a session IS active, the same
  // button instead stops it directly — same toggle pattern as audioShareBtn below — since until
  // now the only way to stop mirroring from the PC was closing scrcpy's own separate window,
  // with no control over it from NotiLink itself.
  // Each phone has its own screen/audio share session (see ScreenShareService): the header
  // buttons always show the CURRENTLY SELECTED phone's state, so one phone sharing never makes
  // the other's buttons look active, and the other can start its own share alongside it.
  // Header pill listing everything the selected phone is transmitting right now (full screen,
  // audio, each app on its own) — each with its own "Detener", plus "Detener todas". Hidden when
  // nothing is active.
  function renderActiveShares() {
    const wrap = document.getElementById('active-shares-wrap');
    const menu = document.getElementById('active-shares-menu');
    const state = activeState();
    const items = [];
    if (state && state.screenSharing) items.push({ kind: 'screen', label: '🖥️ Pantalla completa' });
    if (state && state.audioSharing) items.push({ kind: 'audio', label: '🔊 Audio del celular' });
    if (state) for (const pkg of state.appShares) {
      const app = state.appList.find(a => a.packageName === pkg);
      const onPhone = state.appShareState.get(pkg) === 'phone';
      items.push({ kind: 'app', pkg, onPhone, label: '📱 ' + (app ? app.label : pkg) + (onPhone ? ' (en el celular)' : '') });
    }
    wrap.style.display = items.length ? '' : 'none';
    document.getElementById('active-shares-count').textContent = String(items.length);
    if (!items.length) { menu.classList.remove('open'); return; }
    const activeDevice = devices.find(d => d.deviceId === activeDeviceId);
    menu.innerHTML = `<div class="active-shares-title">${badgeHtml(activeDevice)}Transmitiendo a la PC</div>` + items.map(it =>
      `<div class="active-share-row"><span>${escapeHtml(it.label)}</span>` +
      (it.onPhone ? `<button class="active-share-return" type="button" data-package="${escapeHtml(it.pkg).replace(/"/g, '&quot;')}">🖥️ Traer a la PC</button>` : '') +
      `<button class="active-share-stop" type="button" data-kind="${it.kind}"${it.pkg ? ` data-package="${escapeHtml(it.pkg).replace(/"/g, '&quot;')}"` : ''}>⏹ Detener</button></div>`
    ).join('') + (items.length > 1 ? '<button class="device-menu-item active-share-stop-all" type="button">⏹ Detener todas</button>' : '');
  }

  function renderShareButtons() {
    const state = activeState();
    const screenOn = !!(state && state.screenSharing);
    const audioOn = !!(state && state.audioSharing);
    screenShareBtn.classList.toggle('active', screenOn);
    screenShareBtn.title = screenOn ? 'Detener compartir pantalla' : 'Compartir pantalla';
    screenShareStatsEl.textContent = screenOn ? (state.screenStats || '') : '';
    audioShareBtn.classList.toggle('active', audioOn);
    renderActiveShares();
    // Plain style reset (not resetAudioWaves) — this can run during startup, before the audio
    // level constants further down this file are initialized.
    if (!audioOn) audioShareBtn.querySelectorAll('.audio-share-icon-waves').forEach(w => { w.style.opacity = ''; w.style.strokeWidth = ''; });
  }

  screenShareBtn.addEventListener('click', () => {
    if (!activeDeviceId) return;
    if (activeState().screenSharing) {
      postToHost({ action: 'stopScreenShare', deviceId: activeDeviceId });
      return; // UI resets once window.setScreenShareStopped fires, after the process actually exits
    }
    syncScreenShareUi();
    syncAppOptionsUi();
    screenShareOverlay.classList.add('open');
    setShareMode(shareMode);
  });
  // See docs/PROTOCOL.md's notification.open.result — always fires after a "notification.open",
  // success or failure, so a granted-but-not-found miss never looks identical to the permission
  // never having been granted at all (both used to just be a silent no-op on the PC's end).
  const OPEN_NOTIFICATION_REASONS = {
    accessibility_not_granted: 'activa el permiso de Accesibilidad en el teléfono (Ajustes → Accesibilidad → NotiLink)',
    not_found: 'no se encontró la notificación en el teléfono',
  };
  window.setOpenNotificationResult = function (success, reason) {
    const message = success
      ? 'Notificación abierta en el teléfono'
      : (OPEN_NOTIFICATION_REASONS[reason] || 'no se pudo abrir en el teléfono');
    showToast((success ? '📱 ' : '⚠️ ') + message, success ? 3200 : 6000);
  };

  window.setScreenShareStatus = function (deviceId, success, message) {
    const state = getDeviceState(deviceId);
    if (success) state.screenSharing = true;
    if (deviceId === activeDeviceId) renderShareButtons();
    showToast((success ? '🖥️ ' : '⚠️ ') + message, success ? 3200 : 6000);
  };

  // A single on/off toggle rather than a confirmation popup like screen share — there's nothing
  // to configure here (no quality knobs make sense for audio-only), so a second click on the
  // same button just stops it. Not tied to a specific device beyond whichever one it was
  // started for: switching the active device tab while audio is running doesn't stop it, it
  // just keeps relaying whatever it already was.
  const audioShareWaves = audioShareBtn.querySelectorAll('.audio-share-icon-waves');

  // Real level, not a canned animation — scrcpy's forwarded phone audio actually plays through a
  // normal Windows audio session under its own process, which this PC can genuinely measure (see
  // Services/AudioLevelMonitor.cs), unlike the media player's own decorative equalizer where
  // NotiLink has no access to that app's real audio data at all.
  // Auto-gain floor/ceiling — a fixed curve still wasn't enough: at the phone's volume around
  // 50% the raw WASAPI peak apparently never even reaches the range that curve visibly reacted
  // to. Rather than guess at yet another fixed exponent, this tracks a slowly-decaying "how loud
  // has it recently gotten" ceiling and scales the current level as a fraction of THAT (the
  // "regla de tres" the user asked for) — so the wave always uses the full visual range,
  // regardless of the phone's absolute volume setting. The ceiling decays slowly so it can still
  // adapt if the user turns the phone's volume up or down mid-session, and never drops below a
  // small floor so near-total silence doesn't get amplified into visual noise.
  const AUDIO_LEVEL_FLOOR = 0.02;
  const AUDIO_LEVEL_CEILING_DECAY = 0.985; // per ~100ms tick
  let audioLevelCeiling = AUDIO_LEVEL_FLOOR;

  function resetAudioWaves() {
    audioLevelCeiling = AUDIO_LEVEL_FLOOR;
    audioShareWaves.forEach(w => { w.style.opacity = ''; w.style.strokeWidth = ''; });
  }
  window.setAudioLevel = function (deviceId, level) {
    if (deviceId !== activeDeviceId || !getDeviceState(deviceId).audioSharing) return;
    const clamped = Math.min(Math.max(level, 0), 1);
    audioLevelCeiling = Math.max(clamped, audioLevelCeiling * AUDIO_LEVEL_CEILING_DECAY, AUDIO_LEVEL_FLOOR);
    const normalized = Math.min(1, clamped / audioLevelCeiling);
    audioShareWaves.forEach(w => {
      w.style.opacity = 0.15 + normalized * 0.85;
      w.style.strokeWidth = 1.5 + normalized * 2;
    });
  };
  window.setAudioLevelUnavailable = function (deviceId) {
    showToast('⚠️ No se pudo medir el nivel de audio real de la transmisión', 6000);
  };

  audioShareBtn.addEventListener('click', () => {
    if (!activeDeviceId) return;
    const state = activeState();
    if (state.audioSharing) {
      postToHost({ action: 'stopAudioShare', deviceId: activeDeviceId });
      state.audioSharing = false;
      renderShareButtons();
      showToast('🔊 Audio detenido');
      return;
    }
    showToast('🔊 Conectando audio...');
    postToHost({ action: 'shareAudio', deviceId: activeDeviceId });
  });
  window.setAudioShareStatus = function (deviceId, success, message) {
    const state = getDeviceState(deviceId);
    if (success) state.audioSharing = true;
    if (deviceId === activeDeviceId) renderShareButtons();
    showToast((success ? '🔊 ' : '⚠️ ') + message, success ? 3200 : 6000);
  };
  window.setAudioShareStopped = function (deviceId) {
    getDeviceState(deviceId).audioSharing = false;
    if (deviceId === activeDeviceId) renderShareButtons();
  };
  window.setScreenShareStats = function (deviceId, fps, resolution) {
    const parts = [];
    if (resolution) parts.push(resolution);
    if (fps != null) parts.push(Math.round(fps) + ' fps');
    getDeviceState(deviceId).screenStats = parts.join(' · ');
    if (deviceId === activeDeviceId) renderShareButtons();
  };
  window.setScreenShareStopped = function (deviceId) {
    const state = getDeviceState(deviceId);
    state.screenSharing = false;
    state.screenStats = '';
    if (deviceId === activeDeviceId) renderShareButtons();
  };
  deviceMenuRing.addEventListener('click', () => {
    deviceMenu.classList.remove('open');
    if (activeDeviceId && !deviceMenuRing.disabled) {
      postToHost({ action: 'ringDevice', deviceId: activeDeviceId });
      showToast('🔔 Sonando el celular...');
    }
  });
  deviceMenuPower.addEventListener('click', () => {
    deviceMenu.classList.remove('open');
    if (activeDeviceId && !deviceMenuPower.disabled) postToHost({ action: 'toggleConnection', deviceId: activeDeviceId, disconnect: true });
  });
  deviceMenuRemove.addEventListener('click', () => {
    deviceMenu.classList.remove('open');
    if (!activeDeviceId) return;
    const active = devices.find(d => d.deviceId === activeDeviceId);
    const name = active ? active.name : 'este dispositivo';
    if (confirm(`¿Eliminar "${name}"? Dejará de poder conectarse hasta que lo vincules de nuevo.`)) {
      deviceStates.delete(activeDeviceId);
      postToHost({ action: 'removeDevice', deviceId: activeDeviceId });
    }
  });

  // Matches MainViewModel.cs's own SharedCardKeyPrefix — synthetic cards (shared text, files
  // received from the phone, ...) live only on the PC, so the phone has nothing to confirm
  // removal for and delete.request.all's round trip would never clear them on its own.
  const SHARED_CARD_KEY_PREFIX = 'share-';
  // Expand/collapse every card at once: if any is open, collapse them all (one line each);
  // otherwise open them all showing every message they hold.
  const expandAllBtn = document.getElementById('expand-all-btn');
  expandAllBtn.addEventListener('click', () => {
    const state = activeState();
    if (!state) return;
    const items = [...state.data.values()];
    const anyOpen = items.some(i => hasCollapsibleContent(i) && !i.uiCollapsed);
    for (const i of items) {
      if (anyOpen) { i.uiCollapsed = true; i.uiAll = false; }
      else { i.uiCollapsed = false; i.uiAll = true; }
    }
    renderAll();
  });

  deleteAllBtn.addEventListener('click', () => {
    if (peerTabActive()) { dismissAllPeerCards(activePeerTab); return; }
    if (!activeDeviceId) return;
    postToHost({ action: 'deleteAll', deviceId: activeDeviceId });
    // Optimistic, same idea as a single card's own "×" (see removeCardWithAnimation's doc
    // comment) — sweep every card right away instead of waiting for each phone-side
    // cancelNotification() to round-trip back as its own separate notification.removed. Waiting
    // meant every card animated out one at a time as its confirmation happened to arrive, which
    // read as broken rather than as one deliberate "delete all". Real notifications the phone
    // can't actually clear (isClearable: false — same gate deleteBtnHtml uses to hide the single
    // "×" for these) are left alone: cancelNotification() silently no-ops for those, so removing
    // them here would just make the PC show nothing for a notification still active on the phone.
    const state = getDeviceState(activeDeviceId);
    for (const key of [...state.order]) {
      const item = state.data.get(key);
      if (key.startsWith(SHARED_CARD_KEY_PREFIX) || !item || item.isClearable !== false) {
        removeCardWithAnimation(activeDeviceId, key, 'pc-all');
      }
    }
  });
  themeFab.addEventListener('click', () => {
    settings.darkMode = !settings.darkMode;
    saveSettings();
    applyTheme(settings.darkMode);
    postToHost({ action: 'themeChanged', dark: settings.darkMode });
    const dm = document.getElementById('setting-dark-mode');
    if (dm) dm.checked = !!settings.darkMode;
  });

  // Hides the whole notification list momentarily — deliberately NOT persisted to localStorage
  // (unlike every other setting here): the app must always start with the list expanded on
  // launch regardless of what was chosen in a previous session, only hiding again once the user
  // actually toggles it this session.
  let listHidden = false;
  listCollapseFab.addEventListener('click', () => {
    listHidden = !listHidden;
    listEl.classList.toggle('list-hidden', listHidden);
    listCollapseFab.classList.toggle('active', listHidden);
    listCollapseFab.title = listHidden ? 'Mostrar notificaciones' : 'Ocultar notificaciones';
  });

  // Apply the persisted theme immediately on load, and tell the host so the
  // native title bar (which the page can't style itself) matches from the start.
  applyTheme(settings.darkMode);
  postToHost({ action: 'themeChanged', dark: settings.darkMode });

  // --- Settings modal ---
  const settingsBtn = document.getElementById('settings-btn');
  const settingsOverlay = document.getElementById('settings-overlay');
  const settingsClose = document.getElementById('settings-close');
  const fontSizeInput = document.getElementById('setting-font-size');
  const fontSizeValue = document.getElementById('setting-font-size-value');
  const mediaSizeInput = document.getElementById('setting-media-size');
  const mediaSizeValue = document.getElementById('setting-media-size-value');
  const lineSpacingInput = document.getElementById('setting-line-spacing');
  const lineSpacingValue = document.getElementById('setting-line-spacing-value');
  const showMediaInput = document.getElementById('setting-show-media');
  const autoExpandInput = document.getElementById('setting-auto-expand');
  const mediaModeRow = document.getElementById('settings-media-mode-row');
  const mediaModeInput = document.getElementById('setting-media-mode');
  const messageModeInput = document.getElementById('setting-message-mode');
  const messageCountRow = document.getElementById('settings-message-count-row');
  const messageCountInput = document.getElementById('setting-message-count');
  const wallpaperIntensityInput = document.getElementById('setting-wallpaper-intensity');
  const wallpaperIntensityValue = document.getElementById('setting-wallpaper-intensity-value');
  const linkPreviewsInput = document.getElementById('setting-link-previews');
  const cardAnimationsInput = document.getElementById('setting-card-animations');
  const historyEnabledInput = document.getElementById('setting-history-enabled');
  const phoneAutoHideInput = document.getElementById('setting-phone-autohide');
  const phoneAutoHideSecondsInput = document.getElementById('setting-phone-autohide-seconds');
  const phoneAutoHideRow = document.getElementById('settings-phone-autohide-row');
  const cardRadiusInput = document.getElementById('setting-card-radius');
  const cardRadiusValue = document.getElementById('setting-card-radius-value');
  const nowPlayingArtSizeInput = document.getElementById('setting-now-playing-art-size');
  const nowPlayingArtSizeValue = document.getElementById('setting-now-playing-art-size-value');

  for (let i = 1; i <= 20; i++) {
    const opt = document.createElement('option');
    opt.value = String(i);
    opt.textContent = String(i);
    messageCountInput.appendChild(opt);
  }

  const accentSwatches = document.getElementById('accent-swatches');
  const accentDailyInput = document.getElementById('setting-accent-daily');
  const darkModeInput = document.getElementById('setting-dark-mode');
  const accentWeek = document.getElementById('accent-week');

  function renderAccentUi() {
    const current = (settings.accent || ACCENTS[0]).toUpperCase();
    const custom = !ACCENTS.includes(current);
    accentSwatches.classList.toggle('disabled', !!settings.accentDaily);
    accentSwatches.innerHTML = ACCENTS.map(c =>
      `<button type="button" class="swatch${!settings.accentDaily && c === current ? ' active' : ''}" style="--sw:${c}" data-color="${c}" title="${c}"></button>`
    ).join('') + `<label class="swatch custom${!settings.accentDaily && custom ? ' active' : ''}" title="Otro color"${custom ? ` style="--sw:${current}"` : ''}><input type="color" value="${current}"></label>`;
    const today = new Date().getDay();
    // Monday first, as people read a week; each day opens a small palette (preset colors or any).
    accentWeek.innerHTML = [1, 2, 3, 4, 5, 6, 0].map(i => {
      const c = dailyAccent(i);
      return `<button type="button" class="week-day${i === today ? ' today' : ''}" data-day="${i}" title="${DAY_NAMES[i]}: toca para cambiar el color">` +
        `<span class="week-dot" style="--sw:${c}">${DAY_NAMES[i].slice(0, 2)}</span></button>`;
    }).join('');
    const customized = Array.isArray(settings.dailyAccents) && settings.dailyAccents.some((c, i) => c && c.toUpperCase() !== DAILY_ACCENTS[i]);
    document.getElementById('accent-week-reset').style.display = customized ? '' : 'none';
    document.getElementById('accent-week-row').style.display = settings.accentDaily ? 'flex' : 'none';
    accentDailyInput.checked = !!settings.accentDaily;
  }
  accentSwatches.addEventListener('click', e => {
    const sw = e.target.closest('.swatch[data-color]');
    if (!sw) return;
    settings.accent = sw.dataset.color;
    settings.accentDaily = false;
    saveSettings(); applyAccent(); renderAccentUi();
  });
  accentSwatches.addEventListener('input', e => {
    if (e.target.type !== 'color') return;
    settings.accent = e.target.value.toUpperCase();
    settings.accentDaily = false;
    saveSettings(); applyAccent();
    const label = e.target.closest('.swatch');
    if (label) label.style.setProperty('--sw', settings.accent);
  });
  accentSwatches.addEventListener('change', e => { if (e.target.type === 'color') renderAccentUi(); });
  // Day color popover: the preset colors (accent palette + the default weekday ones) one tap away,
  // plus "Otro…" for any color with the native picker.
  const weekPop = document.createElement('div');
  weekPop.className = 'week-pop';
  document.body.appendChild(weekPop);
  let weekPopDay = null;
  function setDayColor(day, color) {
    const list = Array.isArray(settings.dailyAccents) ? settings.dailyAccents.slice() : DAILY_ACCENTS.slice();
    list[day] = color.toUpperCase();
    settings.dailyAccents = list;
    saveSettings(); applyAccent();
  }
  function closeWeekPop() { weekPop.classList.remove('open'); weekPopDay = null; }
  function openWeekPop(btn) {
    const day = parseInt(btn.dataset.day, 10);
    if (weekPopDay === day && weekPop.classList.contains('open')) { closeWeekPop(); return; }
    weekPopDay = day;
    const current = dailyAccent(day);
    const presets = [...new Set([...ACCENTS, ...DAILY_ACCENTS].map(c => c.toUpperCase()))];
    weekPop.innerHTML = `<div class="week-pop-title">${DAY_NAMES[day]}</div><div class="color-swatches">` +
      presets.map(c => `<button type="button" class="swatch${c === current ? ' active' : ''}" style="--sw:${c}" data-color="${c}" title="${c}"></button>`).join('') +
      `<label class="swatch custom${presets.includes(current) ? '' : ' active'}" title="Otro color"><input type="color" value="${current}"></label></div>`;
    const r = btn.getBoundingClientRect();
    weekPop.classList.add('open');
    const w = weekPop.offsetWidth;
    weekPop.style.left = Math.max(8, Math.min(window.innerWidth - w - 8, r.left + r.width / 2 - w / 2)) + 'px';
    weekPop.style.top = (r.bottom + 8) + 'px';
  }
  accentWeek.addEventListener('click', e => {
    const btn = e.target.closest('.week-day');
    if (btn) { e.stopPropagation(); openWeekPop(btn); }
  });
  weekPop.addEventListener('click', e => {
    e.stopPropagation();
    const sw = e.target.closest('.swatch[data-color]');
    if (!sw || weekPopDay == null) return;
    setDayColor(weekPopDay, sw.dataset.color);
    closeWeekPop();
    renderAccentUi();
  });
  weekPop.addEventListener('input', e => {
    if (e.target.type !== 'color' || weekPopDay == null) return;
    setDayColor(weekPopDay, e.target.value);
    const dot = accentWeek.querySelector(`.week-day[data-day="${weekPopDay}"] .week-dot`);
    if (dot) dot.style.setProperty('--sw', e.target.value);
  });
  weekPop.addEventListener('change', e => { if (e.target.type === 'color') { closeWeekPop(); renderAccentUi(); } });
  document.addEventListener('click', e => { if (!weekPop.contains(e.target)) closeWeekPop(); });
  document.addEventListener('keydown', e => { if (e.key === 'Escape') closeWeekPop(); });
  document.getElementById('settings-overlay').addEventListener('scroll', closeWeekPop, true);
  document.getElementById('accent-week-reset').addEventListener('click', () => {
    settings.dailyAccents = null;
    saveSettings(); applyAccent(); renderAccentUi();
  });
  accentDailyInput.addEventListener('change', () => {
    settings.accentDaily = accentDailyInput.checked;
    saveSettings(); applyAccent(); renderAccentUi();
  });
  darkModeInput.addEventListener('change', () => {
    if (darkModeInput.checked !== !!settings.darkMode) themeFab.click();
  });

  const npArtColorInput = document.getElementById('setting-np-art-color');
  const npReactiveInput = document.getElementById('setting-np-reactive');
  npArtColorInput.addEventListener('change', () => {
    settings.npArtColor = npArtColorInput.checked;
    saveSettings();
    const st = activeState();
    setWaveColorFromArt(settings.npArtColor && st && st.media ? (st.media.albumArtDataUrl || null) : null);
  });
  npReactiveInput.addEventListener('change', () => {
    settings.npReactive = npReactiveInput.checked;
    saveSettings();
    syncAudioLevels();
    kickWave();
  });

  function syncSettingsUi() {
    renderAccentUi();
    npArtColorInput.checked = settings.npArtColor !== false;
    npReactiveInput.checked = settings.npReactive !== false;
    darkModeInput.checked = !!settings.darkMode;
    fontSizeInput.value = String(settings.fontSize);
    fontSizeValue.textContent = settings.fontSize + 'px';
    mediaSizeInput.value = String(settings.mediaSize);
    mediaSizeValue.textContent = settings.mediaSize + 'px';
    lineSpacingInput.value = String(settings.lineSpacing);
    lineSpacingValue.textContent = settings.lineSpacing + 'px';
    showMediaInput.checked = settings.showMedia;
    autoExpandInput.checked = settings.autoExpandOnUpdate;
    mediaModeRow.style.display = settings.showMedia ? 'flex' : 'none';
    mediaModeInput.value = settings.mediaMode;
    messageModeInput.value = settings.messageMode;
    messageCountRow.style.display = settings.messageMode === 'count' ? 'flex' : 'none';
    messageCountInput.value = String(settings.messageCount);
    wallpaperIntensityInput.value = String(settings.wallpaperIntensity);
    wallpaperIntensityValue.textContent = settings.wallpaperIntensity + '%';
    linkPreviewsInput.checked = settings.showLinkPreviews;
    cardAnimationsInput.checked = settings.cardAnimations;
    historyEnabledInput.checked = settings.historyEnabled;
    phoneAutoHideInput.checked = settings.phoneStatusAutoHide;
    phoneAutoHideSecondsInput.value = String(settings.phoneStatusAutoHideSeconds);
    phoneAutoHideRow.style.display = settings.phoneStatusAutoHide ? 'flex' : 'none';
    cardRadiusInput.value = String(settings.cardRadius);
    cardRadiusValue.textContent = settings.cardRadius + 'px';
    nowPlayingArtSizeInput.value = String(settings.nowPlayingArtSize);
    nowPlayingArtSizeValue.textContent = settings.nowPlayingArtSize + 'px';
  }

  settingsBtn.addEventListener('click', () => {
    syncSettingsUi();
    settingsOverlay.classList.add('open');
  });
  settingsClose.addEventListener('click', () => settingsOverlay.classList.remove('open'));
  settingsOverlay.addEventListener('click', e => {
    if (e.target === settingsOverlay) settingsOverlay.classList.remove('open');
  });

  fontSizeInput.addEventListener('input', () => {
    settings.fontSize = parseInt(fontSizeInput.value, 10);
    fontSizeValue.textContent = settings.fontSize + 'px';
    applySettingsToDom();
    saveSettings();
  });
  mediaSizeInput.addEventListener('input', () => {
    settings.mediaSize = parseInt(mediaSizeInput.value, 10);
    mediaSizeValue.textContent = settings.mediaSize + 'px';
    applySettingsToDom();
    saveSettings();
  });
  lineSpacingInput.addEventListener('input', () => {
    settings.lineSpacing = parseInt(lineSpacingInput.value, 10);
    lineSpacingValue.textContent = settings.lineSpacing + 'px';
    applySettingsToDom();
    saveSettings();
  });
  showMediaInput.addEventListener('change', () => {
    settings.showMedia = showMediaInput.checked;
    mediaModeRow.style.display = settings.showMedia ? 'flex' : 'none';
    saveSettings();
    renderAll();
  });
  autoExpandInput.addEventListener('change', () => {
    settings.autoExpandOnUpdate = autoExpandInput.checked;
    saveSettings();
  });
  mediaModeInput.addEventListener('change', () => {
    settings.mediaMode = mediaModeInput.value;
    saveSettings();
    renderAll();
  });
  messageModeInput.addEventListener('change', () => {
    settings.messageMode = messageModeInput.value;
    messageCountRow.style.display = settings.messageMode === 'count' ? 'flex' : 'none';
    saveSettings();
    renderAll();
  });
  messageCountInput.addEventListener('change', () => {
    settings.messageCount = parseInt(messageCountInput.value, 10);
    saveSettings();
    renderAll();
  });
  wallpaperIntensityInput.addEventListener('input', () => {
    settings.wallpaperIntensity = parseInt(wallpaperIntensityInput.value, 10);
    wallpaperIntensityValue.textContent = settings.wallpaperIntensity + '%';
    applySettingsToDom();
    saveSettings();
  });
  linkPreviewsInput.addEventListener('change', () => {
    settings.showLinkPreviews = linkPreviewsInput.checked;
    saveSettings();
    renderAll();
  });
  cardAnimationsInput.addEventListener('change', () => {
    settings.cardAnimations = cardAnimationsInput.checked;
    saveSettings();
  });
  cardRadiusInput.addEventListener('input', () => {
    settings.cardRadius = parseInt(cardRadiusInput.value, 10);
    cardRadiusValue.textContent = settings.cardRadius + 'px';
    applySettingsToDom();
    saveSettings();
  });
  nowPlayingArtSizeInput.addEventListener('input', () => {
    settings.nowPlayingArtSize = parseInt(nowPlayingArtSizeInput.value, 10);
    nowPlayingArtSizeValue.textContent = settings.nowPlayingArtSize + 'px';
    applySettingsToDom();
    saveSettings();
  });
  applySettingsToDom();

  filterMessagingBtn.classList.toggle('active', settings.messagingOnlyFilter);
  filterMessagingBtn.addEventListener('click', () => {
    settings.messagingOnlyFilter = !settings.messagingOnlyFilter;
    filterMessagingBtn.classList.toggle('active', settings.messagingOnlyFilter);
    saveSettings();
    renderAll();
  });

  document.getElementById('open-logs-btn').addEventListener('click', () => postToHost({ action: 'openLogsFolder' }));

  // The phone keeps the value itself; the PC re-sends it to every phone on connect (see
  // MainViewModel.SetPhoneStatusAutoHide), so a phone that was off when it changed catches up.
  function sendPhonePrefs() {
    postToHost({ action: 'phonePrefs', statusAutoHideSeconds: settings.phoneStatusAutoHide ? settings.phoneStatusAutoHideSeconds : 0 });
  }
  phoneAutoHideInput.addEventListener('change', () => {
    settings.phoneStatusAutoHide = phoneAutoHideInput.checked;
    phoneAutoHideRow.style.display = settings.phoneStatusAutoHide ? 'flex' : 'none';
    saveSettings();
    sendPhonePrefs();
  });
  phoneAutoHideSecondsInput.addEventListener('change', () => {
    settings.phoneStatusAutoHideSeconds = parseInt(phoneAutoHideSecondsInput.value, 10) || 5;
    saveSettings();
    sendPhonePrefs();
  });
  sendPhonePrefs();

  historyEnabledInput.addEventListener('change', () => {
    settings.historyEnabled = historyEnabledInput.checked;
    saveSettings();
    // Turning it on starts with whatever is on screen right now, not just what arrives next.
    if (settings.historyEnabled) {
      for (const [deviceId, st] of deviceStates) for (const key of st.order) scheduleHistorySave(deviceId, key);
    }
  });

  // --- History panel: near-full-screen list of every saved notification, newest first, with
  // filters. Entries are only ever deleted by hand (one by one or all at once). ---
  const historyOverlay = document.getElementById('history-overlay');
  const historyListEl = document.getElementById('history-list');
  const historyCountEl = document.getElementById('history-count');
  const historySearch = document.getElementById('history-search');
  const historyDevice = document.getElementById('history-device');
  const historyApp = document.getElementById('history-app');
  const historyReason = document.getElementById('history-reason');
  const historyFrom = document.getElementById('history-from');
  const historyTo = document.getElementById('history-to');
  const HISTORY_PAGE = 150;
  let historyEntries = [];
  let historyShown = HISTORY_PAGE;

  const HISTORY_REASON_LABELS = {
    phone: 'Se quitó en el celular (leída o descartada allí)',
    pc: 'Eliminada manualmente desde la PC',
    'pc-all': 'Eliminada con «Borrar todas» en la PC',
    'pc-file': 'Archivo eliminado desde la PC',
    sync: 'Se quitó automáticamente (ya no estaba en el celular al reconectar)',
    unknown: 'Sin registro de cierre (la app de PC no estaba abierta)',
  };

  function formatHistoryDate(ms) {
    if (!ms) return '—';
    const d = new Date(ms);
    return d.toLocaleDateString('es-MX', { day: '2-digit', month: '2-digit', year: 'numeric' }) + ' ' +
      d.toLocaleTimeString('es-MX', { hour: '2-digit', minute: '2-digit' });
  }

  function fillSelect(select, values, allLabel) {
    const current = select.value;
    select.innerHTML = `<option value="">${allLabel}</option>` +
      values.map(v => `<option value="${escapeHtml(v.value).replace(/"/g, '&quot;')}">${escapeHtml(v.label)}</option>`).join('');
    if (values.some(v => v.value === current)) select.value = current;
  }

  const historyStatsEl = document.getElementById('history-stats');
  const historyOlderSelect = document.getElementById('history-older-select');

  function formatBytes(n) {
    if (!n) return '0 KB';
    if (n < 1024 * 1024) return Math.max(1, Math.round(n / 1024)) + ' KB';
    if (n < 1024 * 1024 * 1024) return (n / 1024 / 1024).toFixed(1) + ' MB';
    return (n / 1024 / 1024 / 1024).toFixed(2) + ' GB';
  }

  function renderHistoryStats(stats) {
    if (!stats) return;
    historyStatsEl.textContent =
      `Ocupa ${formatBytes(stats.totalBytes)} en disco · ${stats.count} notificaciones guardadas · ` +
      `Hoy: ${stats.todayCount} (${formatBytes(stats.todayBytes)})`;
  }

  window.onHistoryDeleted = function (kind, count, freed, stats) {
    renderHistoryStats(stats);
    if (kind === 'one') showToast('🗑️ Notificación eliminada del historial');
    else if (kind === 'older') showToast(count ? `🗑️ ${count} notificaciones antiguas eliminadas · se liberaron ${formatBytes(freed)}` : 'No había notificaciones tan antiguas', 5000);
    else showToast(`🗑️ Historial borrado (${count}) · se liberaron ${formatBytes(freed)}`, 5000);
  };

  historyOlderSelect.addEventListener('change', () => {
    const days = parseInt(historyOlderSelect.value, 10);
    historyOlderSelect.value = '';
    if (!days) return;
    // "Más de N días" counts from the start of today, so "Ayer y antes" (1) keeps exactly today.
    const cutoff = new Date();
    cutoff.setHours(0, 0, 0, 0);
    cutoff.setDate(cutoff.getDate() - (days - 1));
    const affected = historyEntries.filter(e => (e.firstSeenAt || 0) < cutoff.getTime()).length;
    if (!affected) { showToast('No hay notificaciones tan antiguas'); return; }
    if (!confirm(`Se borrarán ${affected} notificaciones anteriores al ${cutoff.toLocaleDateString('es-MX')}. No se puede deshacer. ¿Continuar?`)) return;
    postToHost({ action: 'historyDeleteOlder', before: cutoff.getTime() });
  });

  window.setHistoryEntries = function (entries, stats) {
    renderHistoryStats(stats);
    historyEntries = (entries || []).sort((a, b) => (b.firstSeenAt || 0) - (a.firstSeenAt || 0));
    const devs = new Map(), apps = new Map();
    historyEntries.forEach(e => {
      if (e.deviceId) devs.set(e.deviceId, e.deviceName || e.deviceId);
      if (e.appLabel) apps.set(e.appLabel, e.appLabel);
    });
    fillSelect(historyDevice, [...devs].map(([value, label]) => ({ value, label })), 'Todos los dispositivos');
    fillSelect(historyApp, [...apps.keys()].sort((a, b) => a.localeCompare(b)).map(v => ({ value: v, label: v })), 'Todas las apps');
    historyShown = HISTORY_PAGE;
    renderHistory();
  };

  function historyMatches(e) {
    if (historyDevice.value && e.deviceId !== historyDevice.value) return false;
    if (historyApp.value && e.appLabel !== historyApp.value) return false;
    const r = historyReason.value;
    if (r === 'active' && (e.removedAt || e.removalReason)) return false;
    if (r === 'pc' && !(e.removalReason || '').startsWith('pc')) return false;
    if (r && r !== 'active' && r !== 'pc' && e.removalReason !== r) return false;
    if (historyFrom.value && (e.firstSeenAt || 0) < new Date(historyFrom.value + 'T00:00:00').getTime()) return false;
    if (historyTo.value && (e.firstSeenAt || 0) > new Date(historyTo.value + 'T23:59:59.999').getTime()) return false;
    const q = historySearch.value.trim().toLowerCase();
    if (q) {
      const hay = [e.appLabel, e.title, ...(e.displayLines || []), ...(e.messages || []).map(m => `${m.sender} ${m.text}`), e.deviceName]
        .join(' ').toLowerCase();
      if (!hay.includes(q)) return false;
    }
    return true;
  }

  function historyEntryHtml(e) {
    const attr = v => escapeHtml(v || '').replace(/"/g, '&quot;');
    const avatar = e.avatarDataUrl || e.iconDataUrl;
    const avatarHtml = avatar ? `<img class="hist-avatar" src="${attr(avatar)}">` : '<div class="hist-avatar hist-avatar-empty">🔔</div>';
    const iconHtml = e.iconDataUrl && e.avatarDataUrl ? `<img class="hist-app-icon" src="${attr(e.iconDataUrl)}">` : '';
    let body = '';
    if (e.messages && e.messages.length) {
      // No whitespace between tags: the message text keeps its own line breaks (pre-wrap on
      // .hist-text only), and template indentation must not turn into blank space.
      body = e.messages.map(m =>
        `<div class="hist-msg${m.isMe ? ' hist-msg-mine' : ''}">` +
        `<span class="hist-text">${m.sender ? `<b>${escapeHtml(m.sender)}:</b> ` : ''}${linkifyHtml((m.text || '').trim())}</span>` +
        (m.imageDataUrl ? `<img class="hist-image" src="${attr(m.imageDataUrl)}">` : '') +
        `</div>`).join('');
    } else {
      body = (e.displayLines || []).map(l => `<div class="hist-msg"><span class="hist-text">${linkifyHtml((l || '').trim())}</span></div>`).join('');
    }
    if (e.imageDataUrl) body += `<img class="hist-image hist-image-big" src="${attr(e.imageDataUrl)}">`;
    const actions = e.actions && e.actions.length
      ? `<div class="hist-actions">${e.actions.map(a => `<span class="hist-action">${escapeHtml(a)}</span>`).join('')}</div>` : '';
    const status = e.removedAt || e.removalReason
      ? `Se quitó: ${e.removedAt ? formatHistoryDate(e.removedAt) : '—'} · ${escapeHtml(HISTORY_REASON_LABELS[e.removalReason] || e.removalReason || '')}`
      : '<span class="hist-active">Aún activa</span>';
    return `
      <div class="hist-entry" data-id="${attr(e.id)}"${e.accentColor ? ` style="--card-accent:${attr(e.accentColor)}"` : ''}>
        ${avatarHtml}
        <div class="hist-main">
          <div class="hist-head">${iconHtml}<span class="hist-app">${escapeHtml(e.appLabel)}</span>
            ${e.deviceName ? `<span class="hist-device">· ${escapeHtml(e.deviceName)}</span>` : ''}</div>
          ${e.title ? `<div class="hist-subject">${escapeHtml(e.title)}</div>` : ''}
          <div class="hist-body">${body}</div>
          ${actions}
          <div class="hist-times">Llegó: ${formatHistoryDate(e.postedAt || e.firstSeenAt)} · ${status}</div>
        </div>
        <button class="hist-delete" title="Eliminar del historial">✕</button>
      </div>`;
  }

  function renderHistory() {
    const filtered = historyEntries.filter(historyMatches);
    historyCountEl.textContent = `(${filtered.length}${filtered.length !== historyEntries.length ? ' de ' + historyEntries.length : ''})`;
    if (!filtered.length) {
      historyListEl.innerHTML = `<div class="empty-state">${historyEntries.length ? 'Ninguna notificación coincide con los filtros' :
        (settings.historyEnabled ? 'Todavía no hay notificaciones guardadas' : 'El historial está desactivado. Actívalo en Ajustes para empezar a guardar notificaciones.')}</div>`;
      return;
    }
    const page = filtered.slice(0, historyShown);
    historyListEl.innerHTML = page.map(historyEntryHtml).join('') +
      (filtered.length > historyShown ? `<button class="history-more" type="button">Mostrar más (${filtered.length - historyShown} restantes)</button>` : '');
  }

  document.getElementById('open-history-btn').addEventListener('click', () => {
    settingsOverlay.classList.remove('open');
    historyListEl.innerHTML = '<div class="empty-state">Cargando…</div>';
    historyOverlay.classList.add('open');
    postToHost({ action: 'historyList' });
  });
  document.getElementById('history-close').addEventListener('click', () => historyOverlay.classList.remove('open'));
  historyOverlay.addEventListener('click', e => { if (e.target === historyOverlay) historyOverlay.classList.remove('open'); });
  [historySearch, historyDevice, historyApp, historyReason, historyFrom, historyTo].forEach(el =>
    el.addEventListener(el === historySearch ? 'input' : 'change', () => { historyShown = HISTORY_PAGE; renderHistory(); }));
  document.getElementById('history-reset').addEventListener('click', () => {
    historySearch.value = ''; historyDevice.value = ''; historyApp.value = ''; historyReason.value = '';
    historyFrom.value = ''; historyTo.value = '';
    historyShown = HISTORY_PAGE;
    renderHistory();
  });
  document.getElementById('history-clear-all').addEventListener('click', () => {
    if (!historyEntries.length) return;
    if (!confirm(`Se borrará TODO el historial (${historyEntries.length} notificaciones y sus imágenes). No se puede deshacer. ¿Continuar?`)) return;
    postToHost({ action: 'historyClear' });
    historyEntries = [];
    renderHistory();
  });
  historyListEl.addEventListener('click', e => {
    if (e.target.closest('.history-more')) { historyShown += HISTORY_PAGE; renderHistory(); return; }
    const img = e.target.closest('img.hist-image');
    if (img) { openLightbox(img.src); return; }
    // Same behavior as in the live cards (see linkifyHtml): links open in the browser by
    // themselves (target=_blank → MainWindow's NewWindowRequested); emails, phone/account
    // numbers and codes copy to the clipboard.
    const copyable = e.target.closest('.copyable-token');
    if (copyable) {
      postToHost({ action: 'copyText', text: copyable.dataset.copy });
      showToast('📋 Copiado');
      return;
    }
    const del = e.target.closest('.hist-delete');
    if (del) {
      const id = del.closest('.hist-entry').dataset.id;
      postToHost({ action: 'historyDelete', id });
      historyEntries = historyEntries.filter(x => x.id !== id);
      renderHistory();
    }
  });

  // --- Apps modal: which apps are allowed to mirror notifications (opened from the
  // device "..." menu's "Apps con notificaciones" item — see deviceMenuApps above) ---
  const appsOverlay = document.getElementById('apps-overlay');
  const appsClose = document.getElementById('apps-close');
  const appsSearch = document.getElementById('apps-search');
  const appsListEl = document.getElementById('apps-list');
  const appsCategoryTabs = document.getElementById('apps-category-tabs');
  let appsCategory = 'all'; // 'all' | 'user' | 'system'

  function renderAppsList() {
    const state = activeState();
    const appList = state ? state.appList : [];
    const query = appsSearch.value.trim().toLowerCase();
    const visible = appList
      .filter(a => appsCategory === 'all' || (appsCategory === 'system' ? a.isSystemApp : !a.isSystemApp))
      .filter(a => !query || a.label.toLowerCase().includes(query));

    // Grid of tiles: big icon, name, kind, and the same toggle; the whole tile toggles too.
    // Muted (allowed = false) tiles are dimmed so what's silenced stands out at a glance.
    appsListEl.innerHTML = visible.length ? visible.map(a => `
      <div class="apps-row apps-tile${a.allowed ? '' : ' muted'}" data-package="${a.packageName}" role="button" tabindex="0"
           title="${escapeHtml(a.label).replace(/"/g, '&quot;')} — ${a.allowed ? 'se reflejan sus notificaciones' : 'silenciada en la PC'}">
        <button class="apps-row-toggle${a.allowed ? ' on' : ''}" role="switch" aria-checked="${a.allowed}" aria-label="Notificaciones de ${escapeHtml(a.label).replace(/"/g, '&quot;')}" tabindex="-1"></button>
        ${a.iconDataUrl ? `<img class="apps-row-icon" src="${a.iconDataUrl}" alt="">` : `<span class="apps-row-icon apps-tile-letter">${escapeHtml((a.label || '?').trim().charAt(0).toUpperCase())}</span>`}
        <span class="apps-row-label">${escapeHtml(a.label)}</span>
        <span class="apps-tile-kind">${a.isSystemApp ? 'Sistema' : 'Instalada'}</span>
      </div>`).join('') : `<div class="empty-state">Sin apps en esta categoría</div>`;
    const on = visible.filter(a => a.allowed).length;
    document.getElementById('apps-count').textContent = visible.length
      ? `${on} de ${visible.length} con notificaciones en la PC`
      : '';
  }

  function setAppsAllowed(entries, allowed) {
    if (!activeDeviceId) return;
    for (const entry of entries) {
      if (entry.allowed === allowed) continue;
      entry.allowed = allowed;
      postToHost({ action: 'setAppAllowed', deviceId: activeDeviceId, packageName: entry.packageName, allowed });
    }
    renderAppsList();
  }
  function visibleApps() {
    const state = activeState();
    const query = appsSearch.value.trim().toLowerCase();
    return (state ? state.appList : [])
      .filter(a => appsCategory === 'all' || (appsCategory === 'system' ? a.isSystemApp : !a.isSystemApp))
      .filter(a => !query || a.label.toLowerCase().includes(query));
  }
  document.getElementById('apps-all-on').addEventListener('click', () => setAppsAllowed(visibleApps(), true));
  document.getElementById('apps-all-off').addEventListener('click', () => {
    const list = visibleApps().filter(a => a.allowed);
    if (list.length > 3 && !confirm(`¿Silenciar ${list.length} apps? Sus notificaciones dejarán de llegar a la PC.`)) return;
    setAppsAllowed(list, false);
  });

  appsClose.addEventListener('click', () => appsOverlay.classList.remove('open'));
  appsOverlay.addEventListener('click', e => {
    if (e.target === appsOverlay) appsOverlay.classList.remove('open');
  });
  appsSearch.addEventListener('input', renderAppsList);
  appsCategoryTabs.addEventListener('click', e => {
    const tab = e.target.closest('.apps-category-tab');
    if (!tab) return;
    appsCategory = tab.dataset.category;
    appsCategoryTabs.querySelectorAll('.apps-category-tab').forEach(t => t.classList.toggle('active', t === tab));
    renderAppsList();
  });

  appsListEl.addEventListener('keydown', e => {
    if ((e.key === 'Enter' || e.key === ' ') && e.target.classList.contains('apps-tile')) { e.preventDefault(); e.target.click(); }
  });
  appsListEl.addEventListener('click', function (e) {
    const toggle = e.target.closest('.apps-row');
    if (!toggle || !activeDeviceId) return;
    const packageName = toggle.dataset.package;
    const state = activeState();
    const entry = state && state.appList.find(a => a.packageName === packageName);
    if (!entry) return;
    entry.allowed = !entry.allowed;
    postToHost({ action: 'setAppAllowed', deviceId: activeDeviceId, packageName, allowed: entry.allowed });
    renderAppsList();
  });

  // --- Drag & drop a file onto the window to send it to the active device (see
  // docs/PROTOCOL.md's file.send). WebView2 hands real OS file drops to the hosted page as
  // standard HTML5 DragEvents, same as any Chromium-based web app — no native WPF drop
  // handling needed. The file is read here (FileReader) and its bytes go to the host as a
  // base64 data: URL, which also happens to be exactly what the preview <img> needs. ---
  const fileDropOverlay = document.getElementById('file-drop-overlay');
  const fileDropPreview = document.getElementById('file-drop-preview');
  const fileDropName = document.getElementById('file-drop-name');
  const fileDropSize = document.getElementById('file-drop-size');
  const fileDropSend = document.getElementById('file-drop-send');
  let pendingDropFile = null; // { fileName, mimeType, dataUrl }

  function formatFileSize(bytes) {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
  }

  function closeFileDrop() {
    fileDropOverlay.classList.remove('open');
    pendingDropFile = null;
  }

  document.addEventListener('dragover', e => e.preventDefault());
  document.addEventListener('drop', e => {
    e.preventDefault();
    // With a PC conversation open, a drop goes to that PC instead of the phone.
    if (pcsHandleDrop(e)) return;
    const file = e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files[0];
    if (!file || !activeDeviceId) return;

    const reader = new FileReader();
    reader.onload = () => {
      const mimeType = file.type || 'application/octet-stream';
      pendingDropFile = { fileName: file.name, mimeType, dataUrl: reader.result };

      fileDropPreview.innerHTML = mimeType.startsWith('image/')
        ? `<img class="file-drop-preview-img" src="${reader.result}">`
        : `<div class="file-drop-preview-generic">📄</div>`;
      fileDropName.textContent = file.name;
      fileDropSize.textContent = formatFileSize(file.size);
      fileDropOverlay.classList.add('open');
    };
    reader.readAsDataURL(file);
  });

  document.getElementById('file-drop-cancel').addEventListener('click', closeFileDrop);
  document.getElementById('file-drop-cancel-x').addEventListener('click', closeFileDrop);
  fileDropOverlay.addEventListener('click', e => { if (e.target === fileDropOverlay) closeFileDrop(); });
  fileDropSend.addEventListener('click', () => {
    if (!pendingDropFile || !activeDeviceId) return;
    postToHost({ action: 'sendFile', deviceId: activeDeviceId, ...pendingDropFile });
    closeFileDrop();
    showToast('📤 Enviando archivo...');
  });

  // --- Share-to-phone modal: sends a link/text to the active device, arrives there as a
  // real native notification (see docs/PROTOCOL.md's share.send). ---
  const shareOverlay = document.getElementById('share-overlay');
  const shareClose = document.getElementById('share-close');
  const shareTextEl = document.getElementById('share-text');
  const shareSendBtn = document.getElementById('share-send-btn');

  shareClose.addEventListener('click', () => shareOverlay.classList.remove('open'));
  shareOverlay.addEventListener('click', e => {
    if (e.target === shareOverlay) shareOverlay.classList.remove('open');
  });
  shareSendBtn.addEventListener('click', () => {
    const text = shareTextEl.value.trim();
    if (!text || !activeDeviceId) return;
    postToHost({ action: 'shareToDevice', deviceId: activeDeviceId, text });
    shareOverlay.classList.remove('open');
    showToast('📤 Enviado al celular');
  });

  // --- Screen share confirmation popup: opened by the header's screen-share button (see
  // syncScreenShareUi above) instead of connecting right away — the user reviews/adjusts
  // scrcpy's quality and behavior parameters and explicitly presses "Compartir" before
  // anything actually launches. Every field here writes straight into `settings` so the
  // choices persist as defaults for next time too. ---
  const screenShareOverlay = document.getElementById('screen-share-overlay');
  const screenShareClose = document.getElementById('screen-share-close');
  const screenShareCancel = document.getElementById('screen-share-cancel');
  const screenShareStart = document.getElementById('screen-share-start');
  const screenShareRecommended = document.getElementById('screen-share-recommended');
  const screenMaxSizeInput = document.getElementById('setting-screen-max-size');
  const screenMaxFpsInput = document.getElementById('setting-screen-max-fps');
  const screenMaxFpsValue = document.getElementById('setting-screen-max-fps-value');
  const screenBitrateInput = document.getElementById('setting-screen-bitrate');
  const screenBitrateValue = document.getElementById('setting-screen-bitrate-value');
  const screenCodecInput = document.getElementById('setting-screen-codec');
  const screenBufferInput = document.getElementById('setting-screen-buffer');
  const screenBufferValue = document.getElementById('setting-screen-buffer-value');
  const screenTurnOffInput = document.getElementById('setting-screen-turnoff');
  const screenStayAwakeInput = document.getElementById('setting-screen-stayawake');
  const screenDimInput = document.getElementById('setting-screen-dim');
  const screenAudioInput = document.getElementById('setting-screen-audio');

  function syncScreenShareUi() {
    screenMaxSizeInput.value = String(settings.screenMaxSize);
    screenMaxFpsInput.value = String(settings.screenMaxFps);
    screenMaxFpsValue.textContent = settings.screenMaxFps + ' fps';
    screenBitrateInput.value = String(settings.screenBitrate);
    screenBitrateValue.textContent = settings.screenBitrate + ' Mbps';
    screenCodecInput.value = settings.screenVideoCodec;
    screenBufferInput.value = String(settings.screenBufferMs);
    screenBufferValue.textContent = settings.screenBufferMs + ' ms';
    screenTurnOffInput.checked = settings.screenTurnOff;
    screenStayAwakeInput.checked = settings.screenStayAwake;
    screenDimInput.checked = settings.screenDimPhone;
    screenAudioInput.checked = settings.screenAudio;
  }

  function closeScreenShareOverlay() { screenShareOverlay.classList.remove('open'); }

  // --- "Solo una app": the chosen app runs on its own virtual display on the phone and only its
  // UI is mirrored (see ScreenShareService.StartAppAsync). Same quality settings as the full share. ---
  const shareModeScreenBtn = document.getElementById('share-mode-screen');
  const shareModeAppBtn = document.getElementById('share-mode-app');
  const shareAppPanel = document.getElementById('share-app-panel');
  const shareAppSearch = document.getElementById('share-app-search');
  const shareAppList = document.getElementById('share-app-list');
  let shareMode = settings.shareMode === 'app' ? 'app' : 'screen';

  function setShareMode(mode) {
    shareMode = mode;
    if (settings.shareMode !== mode) { settings.shareMode = mode; saveSettings(); }
    shareModeScreenBtn.classList.toggle('active', mode === 'screen');
    shareModeAppBtn.classList.toggle('active', mode === 'app');
    shareAppPanel.style.display = mode === 'app' ? '' : 'none';
    screenShareOverlay.querySelectorAll('.share-screen-only').forEach(el => { el.style.display = mode === 'app' ? 'none' : ''; });
    if (mode === 'app') { renderShareAppList(); shareAppSearch.focus(); }
    renderShareRecents();
  }

  // Single-app window options — every change is saved, so the next app opens the same way.
  const APP_OPTION_INPUTS = [
    ['setting-app-display', 'appDisplaySize', 'value'],
    ['setting-app-dpi', 'appDpi', 'number'],
    ['setting-app-ime', 'appImePolicy', 'value'],
    ['setting-app-decorations', 'appDecorations', 'checked'],
    ['setting-app-keep', 'appKeepOnClose', 'checked'],
    ['setting-app-autoreturn', 'appAutoReturn', 'checked'],
    ['setting-app-mirrorphone', 'appMirrorWhileOnPhone', 'checked'],
    ['setting-app-restart', 'appRestart', 'checked'],
    ['setting-app-audio', 'appAudio', 'checked'],
    ['setting-app-ontop', 'appAlwaysOnTop', 'checked'],
    ['setting-app-borderless', 'appBorderless', 'checked'],
  ].map(([id, key, kind]) => ({ el: document.getElementById(id), key, kind }));
  function syncAppOptionsUi() {
    for (const { el, key, kind } of APP_OPTION_INPUTS) {
      if (kind === 'checked') el.checked = !!settings[key];
      else el.value = String(settings[key] ?? '');
    }
  }
  for (const { el, key, kind } of APP_OPTION_INPUTS) {
    el.addEventListener('change', () => {
      settings[key] = kind === 'checked' ? el.checked : kind === 'number' ? parseInt(el.value, 10) || 0 : el.value;
      saveSettings();
    });
  }

  // Shared by the app list, the recents row and a notification's "Abrir la app en la PC".
  function startAppShare(pkg, label, deviceId = activeDeviceId) {
    if (!deviceId) return;
    const state = getDeviceState(deviceId);
    if (state.appShares.has(pkg)) { showToast(`📱 ${label} ya está abierta en la PC`); return; }
    showToast(`📱 Abriendo ${label}...`);
    postToHost(dataSaverOptions(deviceId, {
      action: 'shareApp', deviceId, packageName: pkg, label,
      maxSize: settings.screenMaxSize, maxFps: settings.screenMaxFps, bitRate: settings.screenBitrate,
      videoCodec: settings.screenVideoCodec, bufferMs: settings.screenBufferMs,
      displaySize: settings.appDisplaySize, dpi: settings.appDpi, imePolicy: settings.appImePolicy,
      decorations: settings.appDecorations, keepOnClose: settings.appKeepOnClose, restartApp: settings.appRestart,
      audio: settings.appAudio, alwaysOnTop: settings.appAlwaysOnTop, borderless: settings.appBorderless,
      autoReturn: settings.appAutoReturn, mirrorWhileOnPhone: settings.appMirrorWhileOnPhone,
    }));
    const recents = (settings.recentShareApps[deviceId] || []).filter(r => r.packageName !== pkg);
    recents.unshift({ packageName: pkg, label });
    setShareRecents(deviceId, recents.slice(0, 4));
  }

  // "Recientes" is the same list on the phone (app + widget) and here: whichever side changes it
  // sends the whole list with the time it changed; the newer one wins (docs/PROTOCOL.md share.recents).
  function setShareRecents(deviceId, list) {
    settings.recentShareApps[deviceId] = list;
    settings.recentShareAppsAt = settings.recentShareAppsAt || {};
    settings.recentShareAppsAt[deviceId] = Date.now();
    saveSettings();
    pushShareRecents(deviceId);
  }
  function pushShareRecents(deviceId) {
    const at = (settings.recentShareAppsAt || {})[deviceId] || 0;
    postToHost({ action: 'syncShareRecents', deviceId, apps: settings.recentShareApps[deviceId] || [], updatedAt: at });
  }
  // How the phone is connected (phone.network): shown next to "Conectado", and on mobile data
  // screen sharing uses lighter settings (see dataSaverOptions).
  window.setPhoneNetwork = function (deviceId, info) {
    getDeviceState(deviceId).network = info;
    if (deviceId === activeDeviceId) syncActiveDeviceUi();
  };
  function dataSaverOptions(deviceId, opts) {
    const net = getDeviceState(deviceId).network;
    if (!net || !net.metered) return opts;
    showToast('📶 Datos móviles: calidad reducida para ahorrar datos');
    return Object.assign({}, opts, {
      maxSize: opts.maxSize ? Math.min(opts.maxSize, 1024) : 1024,
      maxFps: opts.maxFps ? Math.min(opts.maxFps, 30) : 30,
      bitRate: Math.min(opts.bitRate || 4, 4),
    });
  }

  window.onPhoneShareRecents = function (deviceId, apps, updatedAt) {
    settings.recentShareAppsAt = settings.recentShareAppsAt || {};
    const localAt = settings.recentShareAppsAt[deviceId] || 0;
    const local = settings.recentShareApps[deviceId] || [];
    const incoming = (apps || []).filter(a => a && a.packageName).map(a => ({ packageName: a.packageName, label: a.label || a.packageName }));
    if (!localAt && local.length) {
      // Never synced yet: keep both sides' apps (phone's first), then tell the phone.
      const merged = [...incoming, ...local.filter(l => !incoming.some(i => i.packageName === l.packageName))].slice(0, 4);
      setShareRecents(deviceId, merged);
    } else if (updatedAt > localAt) {
      settings.recentShareApps[deviceId] = incoming.slice(0, 4);
      settings.recentShareAppsAt[deviceId] = updatedAt;
      saveSettings();
    } else if (updatedAt < localAt) {
      pushShareRecents(deviceId);
    }
    if (deviceId === activeDeviceId) renderShareRecents();
  };

  const shareRecentRow = document.getElementById('share-recent-row');
  const shareRecentList = document.getElementById('share-recent-list');
  function renderShareRecents() {
    const recents = activeDeviceId ? (settings.recentShareApps[activeDeviceId] || []) : [];
    shareRecentRow.style.display = recents.length ? '' : 'none';
    if (!recents.length) return;
    const state = activeState();
    shareRecentList.innerHTML = recents.map(r => {
      const app = state && state.appList.find(a => a.packageName === r.packageName);
      const isOpen = state && state.appShares.has(r.packageName);
      const pkgAttr = escapeHtml(r.packageName).replace(/"/g, '&quot;');
      return `<button class="share-recent-item${isOpen ? ' open' : ''}" type="button" data-package="${pkgAttr}" data-label="${escapeHtml(r.label).replace(/"/g, '&quot;')}" title="${isOpen ? 'Cerrar' : 'Abrir'} ${escapeHtml(r.label).replace(/"/g, '&quot;')}">
        <span class="share-recent-remove" data-remove="${pkgAttr}" title="Quitar de recientes">−</span>
        ${app && app.iconDataUrl ? `<img src="${app.iconDataUrl}">` : '<span class="share-app-noicon">📱</span>'}
        <span class="share-app-label">${escapeHtml(r.label)}</span>
      </button>`;
    }).join('');
  }
  shareRecentList.addEventListener('click', e => {
    const remove = e.target.closest('.share-recent-remove');
    if (remove && activeDeviceId) {
      e.stopPropagation();
      const pkg = remove.dataset.remove;
      setShareRecents(activeDeviceId, (settings.recentShareApps[activeDeviceId] || []).filter(r => r.packageName !== pkg));
      renderShareRecents();
      showToast('Quitada de recientes');
      return;
    }
    const item = e.target.closest('.share-recent-item');
    if (!item || !activeDeviceId) return;
    const pkg = item.dataset.package;
    if (activeState().appShares.has(pkg)) { postToHost({ action: 'stopAppShare', deviceId: activeDeviceId, packageName: pkg }); return; }
    startAppShare(pkg, item.dataset.label || pkg);
    closeScreenShareOverlay();
  });
  shareModeScreenBtn.addEventListener('click', () => setShareMode('screen'));
  shareModeAppBtn.addEventListener('click', () => setShareMode('app'));
  shareAppSearch.addEventListener('input', renderShareAppList);

  function renderShareAppList() {
    const state = activeState();
    // Dual Messenger / work-profile copies ("pkg#10") can't be targeted on another display — only
    // each app's main copy is listed.
    const apps = (state ? state.appList : []).filter(a => a.packageName && !a.packageName.includes('#'));
    if (!apps.length) {
      shareAppList.innerHTML = '<div class="empty-state">La lista de apps del celular todavía no llega. Espera unos segundos con el celular conectado.</div>';
      return;
    }
    const q = shareAppSearch.value.trim().toLowerCase();
    const open = state.appShares;
    const shown = apps
      .filter(a => !q || (a.label || '').toLowerCase().includes(q) || a.packageName.toLowerCase().includes(q))
      .sort((a, b) => (open.has(b.packageName) - open.has(a.packageName)) || (a.label || '').localeCompare(b.label || ''));
    shareAppList.innerHTML = shown.length ? shown.map(a => {
      const pkgAttr = escapeHtml(a.packageName).replace(/"/g, '&quot;');
      const isOpen = open.has(a.packageName);
      return `<button class="share-app-item${isOpen ? ' open' : ''}" type="button" data-package="${pkgAttr}" title="${isOpen ? 'Cerrar' : 'Abrir'} ${escapeHtml(a.label || a.packageName).replace(/"/g, '&quot;')}">
        ${a.iconDataUrl ? `<img src="${a.iconDataUrl}">` : '<span class="share-app-noicon">📱</span>'}
        <span class="share-app-label">${escapeHtml(a.label || a.packageName)}</span>
        ${isOpen ? '<span class="share-app-badge">● Abierta</span>' : ''}
      </button>`;
    }).join('') : '<div class="empty-state">Ninguna app coincide</div>';
  }

  shareAppList.addEventListener('click', e => {
    const item = e.target.closest('.share-app-item');
    if (!item || !activeDeviceId) return;
    const pkg = item.dataset.package;
    const state = activeState();
    if (state.appShares.has(pkg)) {
      postToHost({ action: 'stopAppShare', deviceId: activeDeviceId, packageName: pkg });
      return; // list updates when window.setAppShareStopped arrives
    }
    const app = state.appList.find(a => a.packageName === pkg);
    startAppShare(pkg, app ? app.label : pkg);
    closeScreenShareOverlay();
  });

  window.setAppShareStatus = function (deviceId, pkg, success, message) {
    if (success) getDeviceState(deviceId).appShares.add(pkg);
    if (deviceId === activeDeviceId) renderActiveShares();
    if (deviceId === activeDeviceId && screenShareOverlay.classList.contains('open')) { renderShareAppList(); renderShareRecents(); }
    showToast((success ? '📱 ' : '⚠️ ') + message, success ? 3200 : 6000);
  };
  // The app was opened on the phone's own screen while shared (its PC window goes black), or
  // came back to the PC — see ScreenShareService.MonitorAppAsync.
  window.setAppShareState = function (deviceId, pkg, state) {
    const st = getDeviceState(deviceId);
    st.appShareState.set(pkg, state);
    if (deviceId === activeDeviceId) renderActiveShares();
    const app = st.appList.find(a => a.packageName === pkg);
    const label = app ? app.label : pkg;
    if (state === 'phone') {
      showToast(settings.appAutoReturn
        ? `📱 ${label} se abrió en el celular; regresará a la PC cuando salgas de ella`
        : `📱 ${label} se abrió en el celular — tráela de vuelta desde el indicador de transmisiones`, 6000);
    } else {
      showToast(`🖥️ ${label} volvió a la PC`);
    }
  };

  window.setAppShareStopped = function (deviceId, pkg) {
    getDeviceState(deviceId).appShares.delete(pkg);
    getDeviceState(deviceId).appShareState.delete(pkg);
    if (deviceId === activeDeviceId) renderActiveShares();
    if (deviceId === activeDeviceId && screenShareOverlay.classList.contains('open')) { renderShareAppList(); renderShareRecents(); }
  };
  screenShareClose.addEventListener('click', closeScreenShareOverlay);
  screenShareCancel.addEventListener('click', closeScreenShareOverlay);
  screenShareOverlay.addEventListener('click', e => {
    if (e.target === screenShareOverlay) closeScreenShareOverlay();
  });

  screenMaxSizeInput.addEventListener('change', () => {
    settings.screenMaxSize = parseInt(screenMaxSizeInput.value, 10);
    saveSettings();
  });
  screenMaxFpsInput.addEventListener('input', () => {
    settings.screenMaxFps = parseInt(screenMaxFpsInput.value, 10);
    screenMaxFpsValue.textContent = settings.screenMaxFps + ' fps';
    saveSettings();
  });
  screenBitrateInput.addEventListener('input', () => {
    settings.screenBitrate = parseInt(screenBitrateInput.value, 10);
    screenBitrateValue.textContent = settings.screenBitrate + ' Mbps';
    saveSettings();
  });
  screenCodecInput.addEventListener('change', () => {
    settings.screenVideoCodec = screenCodecInput.value;
    saveSettings();
  });
  screenBufferInput.addEventListener('input', () => {
    settings.screenBufferMs = parseInt(screenBufferInput.value, 10);
    screenBufferValue.textContent = settings.screenBufferMs + ' ms';
    saveSettings();
  });
  screenTurnOffInput.addEventListener('change', () => {
    settings.screenTurnOff = screenTurnOffInput.checked;
    saveSettings();
  });
  screenDimInput.addEventListener('change', () => {
    settings.screenDimPhone = screenDimInput.checked;
    saveSettings();
  });
  screenStayAwakeInput.addEventListener('change', () => {
    settings.screenStayAwake = screenStayAwakeInput.checked;
    saveSettings();
  });
  screenAudioInput.addEventListener('change', () => {
    settings.screenAudio = screenAudioInput.checked;
    saveSettings();
  });

  // Same values as DEFAULT_SETTINGS' screen* fields — kept as its own constant so tweaking the
  // shipped defaults and tweaking what "recommended" resets to are two separate decisions that
  // just happen to currently agree, not accidentally coupled.
  const RECOMMENDED_SCREEN_SHARE = {
    screenMaxSize: 0, screenMaxFps: 60, screenBitrate: 20, screenVideoCodec: 'h264',
    screenBufferMs: 0, screenTurnOff: false, screenDimPhone: true, screenStayAwake: false, screenAudio: false,
  };
  screenShareRecommended.addEventListener('click', () => {
    Object.assign(settings, RECOMMENDED_SCREEN_SHARE);
    syncScreenShareUi();
    saveSettings();
  });
  // Shared by the confirmation popup's "Iniciar" button and a phone-initiated request (see
  // window.onPhoneShareRequest) — both should use whatever quality settings are already saved,
  // the popup's own fields write straight into `settings` on every change (see above), so
  // there's nothing separate to pass through here.
  function startScreenShareWithSavedSettings(deviceId) {
    showToast('🖥️ Conectando...');
    postToHost(dataSaverOptions(deviceId, {
      action: 'shareScreen',
      deviceId,
      maxSize: settings.screenMaxSize,
      maxFps: settings.screenMaxFps,
      bitRate: settings.screenBitrate,
      videoCodec: settings.screenVideoCodec,
      bufferMs: settings.screenBufferMs,
      turnScreenOff: settings.screenTurnOff,
      dimPhone: settings.screenDimPhone,
      stayAwake: settings.screenStayAwake,
      audio: settings.screenAudio,
    }));
  }
  screenShareStart.addEventListener('click', () => {
    if (!activeDeviceId) return;
    closeScreenShareOverlay();
    startScreenShareWithSavedSettings(activeDeviceId);
  });

  // The phone asked to start sharing from its own persistent-notification buttons (see
  // ShareActionReceiver on the Android side) — screen share skips the confirmation popup
  // entirely here and goes straight with whatever's already saved, per the user's own request:
  // walking over to the PC to click "Iniciar" would defeat the point of asking from the phone.
  window.onPhoneShareRequest = function (deviceId, type, pkg, label) {
    if (type === 'app' && pkg) { startAppShare(pkg, label || pkg, deviceId); return; }
    if (type === 'screen') startScreenShareWithSavedSettings(deviceId);
    else if (type === 'audio') {
      showToast('🔊 Conectando audio...');
      postToHost({ action: 'shareAudio', deviceId });
    }
  };

  // --- Pairing modal: QR/IP to connect a new phone. Deliberately still a real modal — opening
  // it is always a deliberate user action ("Vincular dispositivo"), unlike the 6-digit confirm
  // code below, which now lives in its own non-modal toast (see window.showPairingCode). ---
  const pairingBtn = document.getElementById('pairing-btn');
  const pairingOverlay = document.getElementById('pairing-overlay');
  const pairingClose = document.getElementById('pairing-close');
  const pairingQrImg = document.getElementById('pairing-qr-img');
  const pairingIpEl = document.getElementById('pairing-ip');

  pairingBtn.addEventListener('click', () => {
    pairingOverlay.classList.add('open');
  });
  pairingClose.addEventListener('click', () => pairingOverlay.classList.remove('open'));
  pairingOverlay.addEventListener('click', e => {
    if (e.target === pairingOverlay) pairingOverlay.classList.remove('open');
  });

  window.setQrCode = function (dataUrl, ip, others) {
    pairingQrImg.src = dataUrl;
    pairingIpEl.textContent = ip;
    const extra = (others || []).map(o => `${o.ip} (${o.adapter})`).join(' · ');
    const otherEl = document.getElementById('pairing-ip-others');
    if (otherEl) {
      otherEl.textContent = extra ? 'Otras IP de esta PC: ' + extra : '';
      otherEl.style.display = extra ? '' : 'none';
    }
  };

  // --- "Acerca de" — bottom of the settings modal, pushed once from MainWindow at startup
  // (see Services/AppInfo.cs). Version is kept in sync by hand with the Android app's own
  // versionName so both sides read the same number. ---
  window.setAppInfo = function (version, buildTimestamp, author) {
    document.getElementById('about-version').textContent = version;
    document.getElementById('about-build').textContent = buildTimestamp;
    document.getElementById('about-author').textContent = author;
  };

  // Non-modal by design — see index.html's own comment on #pairing-code-toast for why this
  // deliberately doesn't reuse the "Vincular dispositivo" modal (that one stays a real modal,
  // since opening IT is always a deliberate user action; this fires unsolicited whenever a
  // handshake needs a fresh code, which — after a confirmed real bug — could occasionally
  // include an already-paired phone). Auto-dismisses on its own; a genuine first-time pairing
  // still has 15s to glance at both screens and confirm, comfortably more than that takes in
  // practice, while a spurious reappearance for an already-paired phone just flashes by instead
  // of demanding attention like the old full-screen modal did.
  const PAIRING_CODE_TOAST_MS = 15000;
  const pairingCodeToast = document.getElementById('pairing-code-toast');
  const pairingCodeToastValue = document.getElementById('pairing-code-toast-value');
  let pairingCodeToastTimer = null;

  function hidePairingCodeToast() {
    clearTimeout(pairingCodeToastTimer);
    pairingCodeToastTimer = null;
    pairingCodeToast.classList.remove('show');
  }
  pairingCodeToast.addEventListener('click', hidePairingCodeToast);

  window.showPairingCode = function (code) {
    pairingCodeToastValue.textContent = code;
    pairingCodeToast.classList.add('show');
    clearTimeout(pairingCodeToastTimer);
    pairingCodeToastTimer = setTimeout(hidePairingCodeToast, PAIRING_CODE_TOAST_MS);
  };

  window.showPaired = function (deviceId, deviceName) {
    activeDeviceId = deviceId;
    localStorage.setItem(ACTIVE_DEVICE_KEY, deviceId);
    hidePairingCodeToast();
    showToast(`✅ Vinculado con ${deviceName}`);
  };

  // A routine reconnect of an already-paired device — purely informational, so unlike
  // showPaired this never switches the active device tab or touches the pairing modal.
  window.showDeviceReconnected = function (deviceId, deviceName) {
    showToast(`🔄 ${deviceName} reconectado`);
  };

  // --- Detail modal: double-click a card to see every message and every piece of
  // media in full, ignoring the "how many messages"/media settings — a pure read
  // view, so it never touches the phone (no mark-as-read, no action invoked). ---
  const detailOverlay = document.getElementById('detail-overlay');
  const detailClose = document.getElementById('detail-close');
  const detailTitle = document.getElementById('detail-title');
  const detailBody = document.getElementById('detail-body');

  const ZOOM_ICON_SVG = `<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="11" cy="11" r="7"/><line x1="21" y1="21" x2="16.65" y2="16.65"/><line x1="11" y1="8" x2="11" y2="14"/><line x1="8" y1="11" x2="14" y2="11"/></svg>`;

  // Wraps a detail-modal image with a corner zoom button (see .detail-image-wrap in app.css) —
  // both the wrap and the image itself open the lightbox, since "click the image" is the more
  // discoverable gesture but the user specifically also asked for an explicit button.
  function zoomableImageHtml(cls, src) {
    return `<div class="detail-image-wrap" data-zoom-src="${src}">
      <img class="${cls}" src="${src}">
      <button class="detail-zoom-btn" title="Ampliar" data-zoom-src="${src}">${ZOOM_ICON_SVG}</button>
    </div>`;
  }

  function openDetailModal(item) {
    detailTitle.textContent = item.appLabel;

    const iconImg = item.iconDataUrl ? `<img class="detail-icon" src="${item.iconDataUrl}">` : '';
    const avatarImg = item.avatarDataUrl ? `<img class="detail-avatar" src="${item.avatarDataUrl}">` : '';
    const headerBlock = `
      <div class="detail-header-block">
        ${avatarImg || iconImg}
        <div class="detail-header-text">
          <div class="detail-app-row">${avatarImg ? iconImg : ''}<span class="app-label">${escapeHtml(item.appLabel)}</span></div>
          ${item.title ? `<div class="detail-subject">${escapeHtml(item.title)}</div>` : ''}
        </div>
      </div>`;

    let bodyHtml;
    if (isTreatedAsMessaging(item) && item.messages && item.messages.length) {
      bodyHtml = groupMediaRuns(item.messages, m => !!m.imageDataUrl).map(block => {
        if (block.run) {
          const first = block.run[0];
          const senderHtml = first.sender ? `<div class="body-text"><span class="msg-sender">${escapeHtml(first.sender)}</span>:</div>` : '';
          return `
          <div class="msg${first.isMe ? ' msg-mine' : ' msg-theirs'}">
            ${senderHtml}
            <div class="msg-media-row">${block.run.map(r => zoomableImageHtml('msg-image', r.imageDataUrl)).join('')}</div>
          </div>`;
        }
        const m = block.msg;
        const senderHtml = m.sender ? `<span class="msg-sender">${escapeHtml(m.sender)}</span>: ` : '';
        const line = `${senderHtml}${linkifyHtml(m.text)}`;
        return `
          <div class="msg${m.isMe ? ' msg-mine' : ' msg-theirs'}">
            <div class="body-text">${line}</div>
            ${m.imageDataUrl ? zoomableImageHtml('msg-image', m.imageDataUrl) : ''}
          </div>`;
      }).join('');
    } else {
      bodyHtml = (item.displayLines || []).map(l => `<div class="body-text">${linkifyHtml(l)}</div>`).join('');
      if (item.imageDataUrl) bodyHtml += zoomableImageHtml('big-image', item.imageDataUrl);
    }
    detailBody.innerHTML = headerBlock + (bodyHtml || `<div class="empty-state">Sin contenido</div>`);
    detailOverlay.classList.add('open');
  }

  detailClose.addEventListener('click', () => detailOverlay.classList.remove('open'));
  detailOverlay.addEventListener('click', e => {
    if (e.target === detailOverlay) detailOverlay.classList.remove('open');
  });

  // Zoom lightbox — a plain full-size view of one image, opened from either the zoom button or
  // the image itself inside the detail modal (see zoomableImageHtml above). Sits on top of the
  // detail modal (higher z-index, see app.css) rather than inside it, so it isn't clipped by
  // the modal's own overflow-y: auto.
  const imageLightbox = document.getElementById('image-lightbox');
  const imageLightboxImg = document.getElementById('image-lightbox-img');
  const imageLightboxClose = document.getElementById('image-lightbox-close');
  const imageLightboxOpen = document.getElementById('image-lightbox-open');

  // The button/image click gets you to "ver completo" (the fit-to-screen 1x view) — from there,
  // the wheel zooms in further, past whatever size the image would otherwise be capped at.
  const LIGHTBOX_MIN_SCALE = 1;
  const LIGHTBOX_MAX_SCALE = 6;
  let lightboxScale = 1;
  let lightboxTx = 0;
  let lightboxTy = 0;

  // translate + scale around the image's own center. Translation is clamped so the image can be
  // panned to any of its edges but never dragged completely off screen.
  function applyLightboxTransform() {
    if (lightboxScale <= LIGHTBOX_MIN_SCALE) { lightboxTx = 0; lightboxTy = 0; }
    const baseW = imageLightboxImg.offsetWidth, baseH = imageLightboxImg.offsetHeight;
    const maxTx = Math.max(0, (baseW * lightboxScale - window.innerWidth) / 2 + baseW * 0.05);
    const maxTy = Math.max(0, (baseH * lightboxScale - window.innerHeight) / 2 + baseH * 0.05);
    const limitX = Math.max(maxTx, (baseW * (lightboxScale - 1)) / 2);
    const limitY = Math.max(maxTy, (baseH * (lightboxScale - 1)) / 2);
    lightboxTx = Math.min(limitX, Math.max(-limitX, lightboxTx));
    lightboxTy = Math.min(limitY, Math.max(-limitY, lightboxTy));
    imageLightboxImg.style.transform = `translate(${lightboxTx}px, ${lightboxTy}px) scale(${lightboxScale})`;
    imageLightboxImg.style.cursor = lightboxScale > LIGHTBOX_MIN_SCALE ? 'grab' : '';
  }

  // Zooms toward the point under the cursor (like a map): the image point under the mouse stays
  // under the mouse while the scale changes, so the edges are reachable just by pointing there.
  function zoomLightboxAt(clientX, clientY, newScale) {
    newScale = Math.min(LIGHTBOX_MAX_SCALE, Math.max(LIGHTBOX_MIN_SCALE, newScale));
    const rect = imageLightboxImg.getBoundingClientRect();
    const cx = rect.left + rect.width / 2 - lightboxTx;   // untransformed center
    const cy = rect.top + rect.height / 2 - lightboxTy;
    const px = (clientX - cx - lightboxTx) / lightboxScale; // image-space offset under the cursor
    const py = (clientY - cy - lightboxTy) / lightboxScale;
    lightboxTx = clientX - cx - newScale * px;
    lightboxTy = clientY - cy - newScale * py;
    lightboxScale = newScale;
    applyLightboxTransform();
  }

  let lightboxOpenedAt = 0;
  function openLightbox(src) {
    lightboxOpenedAt = Date.now();
    imageLightboxImg.src = src;
    lightboxScale = 1;
    lightboxTx = 0;
    lightboxTy = 0;
    imageLightboxImg.style.transform = '';
    imageLightboxImg.style.cursor = '';
    imageLightbox.classList.add('open');
  }
  function closeLightbox() {
    imageLightbox.classList.remove('open');
    imageLightboxImg.src = '';
  }

  // Only the explicit corner button opens the full-screen lightbox now — the image itself zooms
  // in place with the mouse wheel (below), so a plain click on it no longer hijacks the gesture.
  detailBody.addEventListener('click', e => {
    const zoomBtn = e.target.closest('.detail-zoom-btn');
    if (zoomBtn) openLightbox(zoomBtn.dataset.zoomSrc);
  });

  // Scrolling the wheel UP over an image in the detail modal goes straight to the full-screen
  // view (the small frame is too cramped to appreciate it at 100%) — no click needed. Wheel
  // anywhere else in the modal still scrolls it normally.
  detailBody.addEventListener('wheel', e => {
    const wrap = e.target.closest('.detail-image-wrap');
    if (!wrap || e.deltaY >= 0) return;
    e.preventDefault();
    openLightbox(wrap.dataset.zoomSrc);
    lightboxScale = 1.2;
    applyLightboxTransform();
  }, { passive: false });
  imageLightboxClose.addEventListener('click', closeLightbox);
  // Hands the image to Windows' own Photos app (see MainWindow's "openImage").
  imageLightboxOpen.addEventListener('click', () => {
    if (imageLightboxImg.src) postToHost({ action: 'openImage', src: imageLightboxImg.src });
  });
  imageLightbox.addEventListener('click', e => {
    if (lightboxJustPanned) { lightboxJustPanned = false; return; }
    if (e.target === imageLightbox && Date.now() - lightboxOpenedAt > 400) closeLightbox();
  });

  // Left-drag pans only while zoomed. At normal size the mousedown is left alone, so the
  // browser's own image drag works — drag the picture out to a folder or another app.
  let lightboxPan = null;
  let lightboxJustPanned = false;
  imageLightbox.addEventListener('mousedown', e => {
    if (e.button !== 0 || lightboxScale <= LIGHTBOX_MIN_SCALE) return;
    if (e.target.closest('button')) return;
    e.preventDefault();
    lightboxPan = { x: e.clientX, y: e.clientY, tx: lightboxTx, ty: lightboxTy, moved: false };
    imageLightboxImg.style.cursor = 'grabbing';
  });
  document.addEventListener('mousemove', e => {
    if (!lightboxPan) return;
    const dx = e.clientX - lightboxPan.x, dy = e.clientY - lightboxPan.y;
    if (Math.abs(dx) + Math.abs(dy) > 3) lightboxPan.moved = true;
    lightboxTx = lightboxPan.tx + dx;
    lightboxTy = lightboxPan.ty + dy;
    applyLightboxTransform();
    imageLightboxImg.style.cursor = 'grabbing';
  });
  document.addEventListener('mouseup', () => {
    if (!lightboxPan) return;
    lightboxJustPanned = lightboxPan.moved;
    lightboxPan = null;
    applyLightboxTransform();
  });
  // Double-click: one zoom step toward the cursor, or back to normal size if already zoomed.
  const LIGHTBOX_DBLCLICK_SCALE = 2.5;
  imageLightboxImg.addEventListener('dblclick', e => {
    e.preventDefault();
    if (lightboxScale > LIGHTBOX_MIN_SCALE) {
      lightboxScale = LIGHTBOX_MIN_SCALE;
      applyLightboxTransform();
    } else {
      zoomLightboxAt(e.clientX, e.clientY, LIGHTBOX_DBLCLICK_SCALE);
    }
    const sel = window.getSelection();
    if (sel) sel.removeAllRanges();
  });
  // { passive: false } + preventDefault so scrolling here zooms instead of the page (nothing
  // else on this fixed full-viewport overlay needs to scroll anyway).
  imageLightbox.addEventListener('wheel', e => {
    e.preventDefault();
    // Zooming out past the fit-to-screen size (1x) closes the full-screen view, back to the
    // detail modal — same "scroll down far enough and it goes away" feel as a mobile gallery.
    if (e.deltaY > 0 && lightboxScale <= LIGHTBOX_MIN_SCALE) { closeLightbox(); return; }
    zoomLightboxAt(e.clientX, e.clientY, lightboxScale * (e.deltaY < 0 ? 1.2 : 1 / 1.2));
  }, { passive: false });
  document.addEventListener('keydown', e => {
    if (e.key === 'Escape' && imageLightbox.classList.contains('open')) closeLightbox();
  });

  // Native OS drag-and-drop straight out of a received-file card (onto Explorer, another app,
  // an email compose window, ...) — "arrastro de la notificación y la suelto en alguna carpeta",
  // without needing "Abrir ruta" first. A plain HTML5 drag of the card's own <img data:...>
  // wouldn't hand a drop target outside the browser anything usable (there's no real file behind
  // a data: URL) — this instead detects the drag GESTURE here (mousedown + movement past a small
  // threshold) and asks the WPF host to start a genuine native drag with the file's real path on
  // disk (see MainWindow.StartFileDrag), then gets out of the way entirely.
  let fileDragCandidate = null;
  const FILE_DRAG_THRESHOLD_PX = 6;

  // The browser's own default "drag this image" behavior would otherwise compete with (or show
  // alongside) the native drag started above — suppressed specifically for a received-file
  // card's own content, never elsewhere (a normal image preview, an avatar, etc. keeps whatever
  // default behavior it already had).
  listEl.addEventListener('dragstart', function (e) {
    const card = e.target.closest('.card');
    if (!card) return;
    const state = activeState();
    const item = state && state.data.get(card.dataset.key);
    if (item && item.hasSavedFile) e.preventDefault();
  });

  listEl.addEventListener('mousedown', function (e) {
    if (e.button !== 0) return;
    if (e.target.closest('button, input, a')) return; // never starts a drag from a real control
    const card = e.target.closest('.card');
    if (!card) return;
    const state = activeState();
    const item = state && state.data.get(card.dataset.key);
    if (!item || !item.hasSavedFile) return;
    fileDragCandidate = { key: item.key, startX: e.clientX, startY: e.clientY };
  });
  document.addEventListener('mousemove', function (e) {
    if (!fileDragCandidate) return;
    const dx = e.clientX - fileDragCandidate.startX;
    const dy = e.clientY - fileDragCandidate.startY;
    if (Math.hypot(dx, dy) < FILE_DRAG_THRESHOLD_PX) return;
    const key = fileDragCandidate.key;
    fileDragCandidate = null; // one native drag per gesture — never re-triggers mid-drag
    postToHost({ action: 'startFileDrag', key });
  });
  document.addEventListener('mouseup', () => { fileDragCandidate = null; });

  // A double-click's default is to select the word/element under the cursor — the blue highlight
  // that kept appearing on the image or text right as the detail view opens. Second-and-later
  // presses of a multi-click are cancelled before the browser can select anything; inputs (the
  // reply box) keep their normal behavior so double-click-to-select-a-word still works there.
  listEl.addEventListener('mousedown', function (e) {
    if (e.detail > 1 && !e.target.closest('input, textarea')) e.preventDefault();
  });

  // Ionic-style ripple: a circle expanding out from the exact click point, clipped to the card's
  // own rounded shape by a throwaway overlay (the card itself can't take overflow: hidden without
  // clipping its own children). Pointer-events none so it never intercepts the next click.
  function playCardRipple(card, clientX, clientY) {
    const rect = card.getBoundingClientRect();
    const layer = document.createElement('span');
    layer.className = 'card-ripple-layer';
    const wave = document.createElement('span');
    wave.className = 'card-ripple';
    const size = Math.hypot(Math.max(clientX - rect.left, rect.right - clientX), Math.max(clientY - rect.top, rect.bottom - clientY)) * 2;
    wave.style.width = wave.style.height = size + 'px';
    wave.style.left = (clientX - rect.left - size / 2) + 'px';
    wave.style.top = (clientY - rect.top - size / 2) + 'px';
    layer.appendChild(wave);
    card.appendChild(layer);
    setTimeout(() => layer.remove(), 650);
  }

  listEl.addEventListener('dblclick', function (e) {
    const card = e.target.closest('.card');
    if (!card) return;
    // A double-click that lands on an interactive control (delete/collapse/action/reply)
    // should do whatever that control does, twice — not also pop the detail modal.
    if (e.target.closest('button, input, a')) return;
    const sel = window.getSelection();
    if (sel) sel.removeAllRanges();
    playCardRipple(card, e.clientX, e.clientY);
    const state = activeState();
    const item = state && state.data.get(card.dataset.key);
    if (item) openDetailModal(item);
  });

  listEl.addEventListener('click', function (e) {
    const card = e.target.closest('.card');
    if (!card || !activeDeviceId) return;
    const key = card.dataset.key;
    const state = activeState();

    // Hovering an image in a card shows the pointer (see .msg-image/.big-image in app.css) and a
    // click opens it full screen — same lightbox the detail view uses.
    const cardImage = e.target.closest('img.msg-image, img.big-image');
    if (cardImage) { openLightbox(cardImage.src); return; }

    if (e.target.closest('.delete-btn')) {
      postToHost({ action: 'delete', deviceId: activeDeviceId, key });
      removeCardWithAnimation(activeDeviceId, key, 'pc');
      return;
    }

    if (e.target.closest('.open-app-btn')) {
      const item = state && state.data.get(key);
      if (item && canOpenAppOnPc(item)) startAppShare(item.packageName, item.appLabel || item.packageName);
      return;
    }

    if (e.target.closest('.open-btn')) {
      postToHost({ action: 'openNotification', deviceId: activeDeviceId, key });
      return;
    }

    const copyLinkBtn = e.target.closest('.copy-link-btn');
    if (copyLinkBtn) {
      postToHost({ action: 'copyText', text: copyLinkBtn.dataset.url });
      showToast('🔗 Enlace copiado');
      return;
    }

    const copyEmailBtn = e.target.closest('.copy-email-btn');
    if (copyEmailBtn) {
      postToHost({ action: 'copyText', text: copyEmailBtn.dataset.email });
      showToast('📧 Correo copiado');
      return;
    }

    // Inline-highlighted email/phone/account/reference number (see linkifyHtml) — URLs are real
    // <a> links and open normally instead, never routed through here.
    const copyableToken = e.target.closest('.copyable-token');
    if (copyableToken) {
      postToHost({ action: 'copyText', text: copyableToken.dataset.copy });
      showToast('📋 Copiado');
      return;
    }

    const copyTextBtn = e.target.closest('.copy-text-btn');
    if (copyTextBtn) {
      const item = state && state.data.get(copyTextBtn.dataset.key);
      const text = item && item.displayLines ? item.displayLines.join('\n') : '';
      if (text) {
        postToHost({ action: 'copyText', text });
        showToast('📋 Copiado');
      }
      return;
    }

    const openFileBtn = e.target.closest('.open-file-btn');
    if (openFileBtn) {
      postToHost({ action: 'openFile', key: openFileBtn.dataset.key });
      return;
    }

    const openFilePathBtn = e.target.closest('.open-file-path-btn');
    if (openFilePathBtn) {
      postToHost({ action: 'openFilePath', key: openFilePathBtn.dataset.key });
      return;
    }

    // Deletes the real file from disk immediately — no confirmation dialog, per explicit
    // request. The resulting card removal itself still animates like any other delete.
    const deleteFileBtn = e.target.closest('.delete-file-btn');
    if (deleteFileBtn) {
      const fileKey = deleteFileBtn.dataset.key;
      postToHost({ action: 'deleteFile', key: fileKey });
      if (activeDeviceId) removeCardWithAnimation(activeDeviceId, fileKey, 'pc-file');
      return;
    }

    const muteBtn = e.target.closest('.mute-btn');
    if (muteBtn) {
      const actionIndex = parseInt(muteBtn.dataset.actionIndex, 10);
      postToHost({ action: 'invoke', deviceId: activeDeviceId, key, actionIndex, replyText: null });
      return;
    }

    if (e.target.closest('.show-all-btn')) {
      const item = state && state.data.get(key);
      if (item) { item.uiAll = !item.uiAll; item.uiCollapsed = false; renderAll(); }
      return;
    }

    if (e.target.closest('.collapse-btn')) {
      const item = state && state.data.get(key);
      if (item) toggleCollapse(card, item);
      return;
    }

    const actionBtn = e.target.closest('.action-btn');
    if (actionBtn) {
      const actionIndex = parseInt(actionBtn.dataset.actionIndex, 10);
      if (actionBtn.dataset.hasReply === 'true') {
        const replyRow = card.querySelector('.reply-row');
        if (replyRow) {
          replyRow.classList.add('open');
          replyRow.dataset.actionIndex = String(actionIndex);
          replyRow.querySelector('.reply-input').focus();
        }
      } else {
        postToHost({ action: 'invoke', deviceId: activeDeviceId, key, actionIndex, replyText: null });
      }
      return;
    }

    if (e.target.closest('.reply-send-btn')) {
      const replyRow = card.querySelector('.reply-row');
      const input = replyRow.querySelector('.reply-input');
      const text = input.value.trim();
      if (text) {
        postToHost({ action: 'invoke', deviceId: activeDeviceId, key, actionIndex: parseInt(replyRow.dataset.actionIndex, 10), replyText: text });
      }
      replyRow.classList.remove('open');
      input.value = '';
      return;
    }

    // A canned quick-reply choice (RemoteInput.getChoices()) — sends immediately on tap, the
    // same as tapping it on the phone would, no typing or separate confirm step. The reply text
    // is read from the button's own rendered text rather than a data-* attribute (see
    // replyChoicesHtml's own comment in cardInnerHtml for why).
    const choiceBtn = e.target.closest('.reply-choice-btn');
    if (choiceBtn) {
      const actionIndex = parseInt(choiceBtn.dataset.actionIndex, 10);
      const text = choiceBtn.textContent.trim();
      if (text) postToHost({ action: 'invoke', deviceId: activeDeviceId, key, actionIndex, replyText: text });
      return;
    }
  });

  listEl.addEventListener('keydown', function (e) {
    // Shift+Enter is a line break in the (multi-line) PC-to-PC reply box; Enter sends either way.
    if (e.target.classList.contains('reply-input') && e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      e.target.closest('.reply-row').querySelector('.reply-send-btn').click();
    }
  });

  // Clicking away from an open reply box (without sending) collapses it back to hidden, same as
  // it was before "Responder" was tapped — it used to only ever close on a successful send,
  // staying open indefinitely otherwise. focusout (not blur) is used because it bubbles, so one
  // delegated listener on listEl covers every card; e.relatedTarget is checked against the row
  // itself so tabbing/clicking onto the row's OWN "Enviar" button (which the send handler above
  // already closes on its own) doesn't also get treated as "left the row" here.
  listEl.addEventListener('focusout', function (e) {
    if (!e.target.classList.contains('reply-input')) return;
    const replyRow = e.target.closest('.reply-row');
    if (!replyRow || replyRow.contains(e.relatedTarget)) return;
    replyRow.classList.remove('open');
  });

  // --- PC ↔ PC ("PCs vinculadas", see docs/PROTOCOL.md "PC ↔ PC"): linked PCs, a conversation
  // per PC (text, clipboard, files with live progress, nudges) and the "Añadir PC" flow. The C#
  // side (PeerLinkService) owns the links and the saved history; this only renders it. Files are
  // handed over as real File objects (postMessageWithAdditionalObjects) so C# streams them from
  // disk — never read into the page — which is what lets them be any size. ---
  const ACTIVE_PEER_KEY = 'notilink-active-peer';
  const pcsBtn = document.getElementById('pcs-btn');
  const pcsBtnBadge = document.getElementById('pcs-btn-badge');
  const pcsOverlay = document.getElementById('pcs-overlay');
  const pcsListEl = document.getElementById('pcs-list');
  const pcsSelfEl = document.getElementById('pcs-self');
  const pcsEmpty = document.getElementById('pcs-empty');
  const pcsAddView = document.getElementById('pcs-add-view');
  const pcsChatView = document.getElementById('pcs-chat-view');
  const pcsScanList = document.getElementById('pcs-scan-list');
  const pcsIpInput = document.getElementById('pcs-ip-input');
  const pcsCodeStep = document.getElementById('pcs-code-step');
  const pcsCodeTarget = document.getElementById('pcs-code-target');
  const pcsCodeInput = document.getElementById('pcs-code-input');
  const pcsAddStatus = document.getElementById('pcs-add-status');
  const pcsChatDot = document.getElementById('pcs-chat-dot');
  const pcsChatName = document.getElementById('pcs-chat-name');
  const pcsChatSub = document.getElementById('pcs-chat-sub');
  const pcsRenameInput = document.getElementById('pcs-rename-input');
  const pcsMenu = document.getElementById('pcs-menu');
  const pcsMessages = document.getElementById('pcs-messages');
  const pcsInput = document.getElementById('pcs-input');
  const pcsFileInput = document.getElementById('pcs-file-input');
  const pcsCodeOverlay = document.getElementById('pcs-code-overlay');

  let peerSelf = null;            // {pcId, name, addresses:[{ip, kind}]}
  let activePeerId = localStorage.getItem(ACTIVE_PEER_KEY) || null;
  let pcsView = 'empty';          // 'empty' | 'add' | 'chat'
  let pcsAwaitingNewPeer = null;  // ids known before a pairing started, to select the new one after

  const peerName = pcId => (peers.find(p => p.pcId === pcId) || {}).name || 'Otra PC';
  const pcsIsOpen = () => pcsOverlay.classList.contains('open');

  function sendFilesToPeer(pcId, files) {
    const list = Array.from(files || []);
    if (!list.length || !window.chrome || !window.chrome.webview) return;
    window.chrome.webview.postMessageWithAdditionalObjects({ action: 'peerSendFiles', pcId }, list);
  }

  // A drop goes to a PC when its conversation is open, or when its tab is the selected one.
  function pcsHandleDrop(e) {
    const target = pcsIsOpen() ? (pcsView === 'chat' ? activePeerId : null) : (peerTabActive() ? activePeerTab : null);
    if (!target) return false;
    const files = e.dataTransfer && e.dataTransfer.files;
    if (files && files.length) sendFilesToPeer(target, files);
    return true;
  }

  function updatePcsBadge() {
    let total = 0;
    peerUnread.forEach(n => { total += n; });
    pcsBtnBadge.textContent = total > 9 ? '9+' : String(total);
    pcsBtnBadge.classList.toggle('show', total > 0);
    pcsBtn.classList.toggle('pcs-btn-online', peers.some(p => p.connected));
    // The PC tabs carry the same unread counts — unless a phone tab is mid-rename.
    if (!deviceSegmentGroup.querySelector('.segment-rename-input')) renderDeviceSegments();
  }

  function lastSeenText(p) {
    if (p.connected) return p.address ? `Conectada · ${p.address}` : 'Conectada';
    if (p.needsRepair) return 'Hay que volver a vincularla';
    return p.lastSeen ? `Sin conexión · visto ${formatTime(p.lastSeen)}` : 'Sin conexión';
  }

  function renderPeerList() {
    if (!peers.length) {
      pcsListEl.innerHTML = '<div class="pcs-list-empty">Aún no hay PCs vinculadas.</div>';
    } else {
      pcsListEl.innerHTML = peers.map(p => {
        const unread = peerUnread.get(p.pcId) || 0;
        const active = pcsView === 'chat' && p.pcId === activePeerId;
        return `<button class="pcs-peer${active ? ' active' : ''}" data-pc-id="${escapeHtml(p.pcId)}" type="button">
          <span class="pcs-dot${p.connected ? ' on' : ''}"></span>
          <span class="pcs-peer-text">
            <span class="pcs-peer-name">${escapeHtml(p.name)}</span>
            <span class="pcs-peer-sub">${escapeHtml(lastSeenText(p))}</span>
          </span>
          ${unread ? `<span class="pcs-unread">${unread > 9 ? '9+' : unread}</span>` : ''}
        </button>`;
      }).join('');
    }
    if (peerSelf) {
      const ips = (peerSelf.addresses || []).map(a => `${escapeHtml(a.ip)}${a.kind === 'vpn' ? ' (Tailscale)' : ''}`).join(' · ');
      pcsSelfEl.innerHTML = `<div class="pcs-self-label">Esta PC</div><div class="pcs-self-name">${escapeHtml(peerSelf.name)}</div><div class="pcs-self-ips">${ips || 'Sin red'}</div>`;
    }
  }

  function showPcsView(view) {
    pcsView = view;
    pcsEmpty.style.display = view === 'empty' ? '' : 'none';
    pcsAddView.style.display = view === 'add' ? '' : 'none';
    pcsChatView.style.display = view === 'chat' ? '' : 'none';
    renderPeerList();
  }

  function openPeerChat(pcId) {
    activePeerId = pcId;
    try { localStorage.setItem(ACTIVE_PEER_KEY, pcId); } catch (_) { /* storage unavailable */ }
    peerUnread.delete(pcId);
    updatePcsBadge();
    pcsMenu.classList.remove('open');
    pcsRenameInput.style.display = 'none';
    pcsChatName.style.display = '';
    showPcsView('chat');
    renderChatHeader();
    if (peerChats.has(pcId)) renderPeerMessages(true);
    else { pcsMessages.innerHTML = ''; postToHost({ action: 'peerHistory', pcId }); }
    setTimeout(() => pcsInput.focus(), 0);
  }

  function renderChatHeader() {
    const p = peers.find(x => x.pcId === activePeerId);
    if (!p) return;
    pcsChatName.textContent = p.name;
    pcsChatSub.textContent = lastSeenText(p);
    pcsChatDot.classList.toggle('on', !!p.connected);
    pcsChatView.classList.toggle('offline', !p.connected);
  }

  function openPcsOverlay() {
    pcsOverlay.classList.add('open');
    postToHost({ action: 'peerList' });
    if (activePeerId && peers.some(p => p.pcId === activePeerId)) openPeerChat(activePeerId);
    else if (peers.length) openPeerChat(peers[0].pcId);
    else showPcsView('empty');
  }

  function openAddView() {
    pcsCodeStep.style.display = 'none';
    pcsAddStatus.textContent = '';
    showPcsView('add');
    scanPeers();
  }

  function scanPeers() {
    pcsScanList.innerHTML = '<div class="pcs-scan-empty">Buscando PCs con NotiLink…</div>';
    postToHost({ action: 'peerScan' });
  }

  function startPairing(ip) {
    if (!ip) return;
    pcsAwaitingNewPeer = new Set(peers.map(p => p.pcId));
    pcsCodeStep.style.display = 'none';
    pcsAddStatus.textContent = `Conectando con ${ip}…`;
    postToHost({ action: 'peerPairStart', ip });
  }

  const peerFilePct = it => it.fileSize ? Math.min(100, Math.round((it.transferred || 0) * 100 / it.fileSize)) : 0;
  const peerFileInProgress = it => it.state === 'sending' || it.state === 'receiving';
  function peerFileStateText(it) {
    const pct = peerFilePct(it);
    return {
      sending: `Enviando… ${pct}% · ${formatBytes(it.transferred)} de ${formatBytes(it.fileSize)}`,
      receiving: `Recibiendo… ${pct}% · ${formatBytes(it.transferred)} de ${formatBytes(it.fileSize)}`,
      done: it.outgoing ? `${formatBytes(it.fileSize)} · enviado` : `${formatBytes(it.fileSize)} · guardado en Descargas`,
      failed: 'No se completó (se cortó la conexión o hubo un error)',
      canceled: 'Cancelado',
    }[it.state] || formatBytes(it.fileSize);
  }

  function peerItemHtml(it) {
    const side = it.outgoing ? 'out' : 'in';
    const time = `<span class="pcs-msg-time">${escapeHtml(formatTime(it.at))}</span>`;
    const id = escapeHtml(it.id);
    if (it.kind === 'nudge') {
      const who = it.outgoing ? 'Llamaste la atención' : `${escapeHtml(peerName(activePeerId))} te llamó la atención`;
      return `<div class="pcs-msg-system" data-id="${id}">👋 ${who} · ${escapeHtml(formatTime(it.at))}</div>`;
    }
    if (it.kind === 'file') {
      const pct = peerFilePct(it);
      const inProgress = peerFileInProgress(it);
      const stateText = peerFileStateText(it);
      const actions = inProgress
        ? `<button class="link-btn pcs-file-cancel" data-id="${id}" type="button">Cancelar</button>`
        : it.state === 'done'
          ? `<button class="link-btn pcs-file-open" data-id="${id}" type="button">Abrir</button><button class="link-btn pcs-file-show" data-id="${id}" type="button">Mostrar en carpeta</button>`
          : '';
      return `<div class="pcs-msg pcs-msg-${side} pcs-msg-file${it.state === 'failed' || it.state === 'canceled' ? ' pcs-file-bad' : ''}" data-id="${id}">
        <div class="pcs-file-row">
          <span class="pcs-file-icon">📄</span>
          <span class="pcs-file-text">
            <span class="pcs-file-name">${escapeHtml(it.fileName || 'archivo')}</span>
            <span class="pcs-file-state">${escapeHtml(stateText)}</span>
          </span>
        </div>
        ${inProgress ? `<div class="pcs-progress"><span style="width:${pct}%"></span></div>` : ''}
        <div class="pcs-file-actions">${actions}${time}</div>
      </div>`;
    }
    const clip = it.kind === 'clipboard';
    return `<div class="pcs-msg pcs-msg-${side}${clip ? ' pcs-msg-clip' : ''}" data-id="${id}">
      ${clip ? `<div class="pcs-clip-label">📋 Portapapeles${it.outgoing ? '' : ' · ya copiado en esta PC'}</div>` : ''}
      <div class="pcs-msg-text">${linkifyHtml(it.text || '')}</div>
      <div class="pcs-msg-meta"><button class="link-btn pcs-copy" data-id="${id}" type="button">Copiar</button>${time}</div>
    </div>`;
  }

  function renderPeerMessages(scrollToEnd) {
    const items = peerChats.get(activePeerId) || [];
    const nearBottom = pcsMessages.scrollHeight - pcsMessages.scrollTop - pcsMessages.clientHeight < 80;
    pcsMessages.innerHTML = items.length
      ? items.map(peerItemHtml).join('')
      : '<div class="pcs-scan-empty">Todavía no hay mensajes. Escribe algo, pega tu portapapeles o arrastra archivos aquí.</div>';
    if (scrollToEnd || nearBottom) pcsMessages.scrollTop = pcsMessages.scrollHeight;
  }

  function findPeerItem(id) {
    return (peerChats.get(activePeerId) || []).find(x => x.id === id);
  }

  function sendPeerText() {
    const text = pcsInput.value;
    if (!text.trim() || !activePeerId) return;
    postToHost({ action: 'peerSend', pcId: activePeerId, text });
    pcsInput.value = '';
    autoSizePcsInput();
  }

  function autoSizePcsInput() {
    pcsInput.style.height = 'auto';
    pcsInput.style.height = `${Math.min(pcsInput.scrollHeight, 140)}px`;
  }

  // ---- A linked PC's own tab in the device switcher: what happened with that PC (messages,
  // clipboard, files, nudges, both directions) as cards in the main list, newest first, with the
  // same kind of corner time + "×" and footer pills the phone cards have. Nothing here is a real
  // notification of that PC — only what the two NotiLinks exchanged. "×" only hides the card
  // (peerDismiss); the conversation in "PCs vinculadas" keeps everything. ---
  const PEER_FEED_LIMIT = 150;
  const peerFeedBar = document.getElementById('peer-feed-bar');
  const peerFeedInput = document.getElementById('peer-feed-input');
  const peerFeedFileInput = document.getElementById('peer-feed-file-input');
  const peerFeedViewBtn = document.getElementById('peer-feed-view');
  const peerFeedShareBtn = document.getElementById('peer-feed-share');

  function peerCardTexts(it, name) {
    const inc = !it.outgoing;
    switch (it.kind) {
      case 'clipboard':
        return { icon: '📋', title: inc ? `${name} te pasó su portapapeles` : 'Enviaste tu portapapeles', note: inc ? 'Ya está copiado en esta PC' : '' };
      case 'nudge':
        return { icon: '👋', title: inc ? `${name} te llamó la atención` : 'Llamaste la atención', note: '' };
      case 'file': {
        const title = {
          sending: 'Enviando archivo', receiving: `${name} te está enviando un archivo`,
          done: inc ? `${name} te envió un archivo` : 'Archivo enviado',
          failed: inc ? 'No se pudo recibir el archivo' : 'No se pudo enviar el archivo',
          canceled: 'Transferencia cancelada',
        }[it.state] || 'Archivo';
        return { icon: '📄', title, note: '' };
      }
      default:
        return { icon: '💬', title: inc ? `Mensaje de ${name}` : `Tú → ${name}`, note: '' };
    }
  }

  function peerCardHtml(it) {
    const name = peerName(activePeerTab);
    const { icon, title, note } = peerCardTexts(it, name);
    const id = escapeHtml(it.id);
    let body = '';
    if (it.kind === 'text' || it.kind === 'clipboard') {
      body = `<div class="body-text">${linkifyHtml(it.text || '')}</div>`;
    } else if (it.kind === 'file') {
      body = `<div class="body-text peer-card-file"><b>${escapeHtml(it.fileName || 'archivo')}</b> · ${escapeHtml(peerFileStateText(it))}</div>`;
    }
    if (note) body += `<div class="body-text peer-card-note">${escapeHtml(note)}</div>`;
    const progress = it.kind === 'file' && peerFileInProgress(it)
      ? `<div class="notif-progress"><div class="notif-progress-fill" style="width:${peerFilePct(it)}%"></div></div>`
      : '';

    const pills = [];
    const canReply = !it.outgoing && it.kind !== 'file';
    if (canReply) pills.push(`<button class="action-btn peer-reply-btn" type="button">Responder</button>`);
    if (it.text) pills.push(`<button class="action-btn peer-copy-btn" type="button">📋 Copiar</button>`);
    if (it.kind === 'file' && peerFileInProgress(it)) pills.push(`<button class="action-btn peer-cancel-btn" type="button">Cancelar</button>`);
    if (it.kind === 'file' && it.state === 'done' && it.path) {
      pills.push(`<button class="action-btn peer-open-file-btn" type="button">📄 Abrir archivo</button>`);
      pills.push(`<button class="action-btn peer-show-file-btn" type="button">📁 Abrir ruta</button>`);
    }
    pills.push(`<button class="action-btn peer-chat-btn" type="button">💬 Abrir chat</button>`);
    const replyRow = canReply
      ? `<div class="reply-row">
           <textarea class="reply-input peer-reply-input" rows="1" placeholder="Responder a ${escapeHtml(name)}... (Shift+Enter nueva línea)"></textarea>
           <button class="reply-send-btn">Enviar</button>
         </div>`
      : '';

    return `<div class="card peer-card${it.outgoing ? ' peer-card-out' : ''}" data-peer-item="${id}">
        <span class="card-top-time">${escapeHtml(formatTime(it.at))}</span>
        ${deleteBtnHtml(true).replace('title="Borrar"', 'title="Quitar tarjeta (el chat se conserva)"')}
        <div class="header-row"><span class="peer-card-icon">${icon}</span><span class="app-label">${escapeHtml(name)}</span></div>
        <div class="title">${escapeHtml(title)}</div>
        ${progress}
        ${body}
        <div class="card-footer"><div class="card-footer-left">${pills.join('')}</div></div>
        ${replyRow}
      </div>`;
  }

  function renderPeerFeed() {
    const pcId = activePeerTab;
    const name = peerName(pcId);
    const p = peers.find(x => x.pcId === pcId);
    peerFeedInput.placeholder = p && p.connected ? `Escribe a ${name}…` : `${name} no está conectada ahora mismo`;
    peerFeedBar.classList.toggle('offline', !(p && p.connected));
    const sharing = !!(p && p.sharingScreen);
    peerFeedShareBtn.classList.toggle('active', sharing);
    peerFeedShareBtn.title = sharing ? `Dejar de compartir mi pantalla con ${name}` : `Compartir mi pantalla con ${name}`;
    peerFeedViewBtn.title = p && p.viewingScreen ? `Ya estás viendo la pantalla de ${name}` : `Ver la pantalla de ${name}`;

    // A reply being typed survives the re-render (new card, unrelated update, ...).
    const active = document.activeElement;
    const typingCard = active && active.classList.contains('reply-input') ? active.closest('.peer-card') : null;
    const keep = typingCard ? { id: typingCard.dataset.peerItem, value: active.value } : null;

    const loaded = peerChats.get(pcId);
    const items = (loaded || []).filter(it => !it.dismissed).slice(-PEER_FEED_LIMIT).reverse();
    listEl.innerHTML = items.length
      ? items.map(peerCardHtml).join('')
      : `<div class="empty-state">${loaded ? `Sin actividad con ${escapeHtml(name)}. Escríbele arriba o arrastra archivos aquí para enviárselos.` : 'Cargando…'}</div>`;

    if (keep) {
      const card = listEl.querySelector(`.peer-card[data-peer-item="${CSS.escape(keep.id)}"]`);
      const row = card && card.querySelector('.reply-row');
      if (row) {
        row.classList.add('open');
        const input = row.querySelector('.reply-input');
        input.value = keep.value;
        input.focus();
      }
    }
  }

  function dismissPeerCard(pcId, card) {
    const id = card.dataset.peerItem;
    const item = (peerChats.get(pcId) || []).find(x => x.id === id);
    if (item) item.dismissed = true;
    postToHost({ action: 'peerDismiss', pcId, id });
    if (settings.cardAnimations) {
      card.classList.add('deleting');
      setTimeout(() => { if (peerTabActive() && activePeerTab === pcId) renderPeerFeed(); }, 280);
    } else {
      renderPeerFeed();
    }
  }

  function dismissAllPeerCards(pcId) {
    (peerChats.get(pcId) || []).forEach(x => { x.dismissed = true; });
    postToHost({ action: 'peerDismiss', pcId });
    renderPeerFeed();
  }

  function openPeerChatOverlay(pcId) {
    pcsOverlay.classList.add('open');
    postToHost({ action: 'peerList' });
    openPeerChat(pcId);
  }

  // Capture phase on #list, so a PC card's clicks never reach the phone cards' own delegates
  // (which would read them as phone notifications). Everything else passes straight through.
  function peerCardEvent(e) {
    const card = e.target.closest('.peer-card');
    if (!card || !peerTabActive()) return null;
    e.stopPropagation();
    return card;
  }
  listEl.addEventListener('click', e => {
    const card = peerCardEvent(e);
    if (!card) return;
    const pcId = activePeerTab;
    const id = card.dataset.peerItem;
    const item = (peerChats.get(pcId) || []).find(x => x.id === id);
    const copyable = e.target.closest('.copyable-token');
    if (copyable) { postToHost({ action: 'copyText', text: copyable.dataset.copy }); showToast('📋 Copiado'); return; }
    if (e.target.closest('.delete-btn')) { dismissPeerCard(pcId, card); return; }
    if (e.target.closest('.peer-reply-btn')) {
      const row = card.querySelector('.reply-row');
      if (row) { row.classList.toggle('open'); if (row.classList.contains('open')) row.querySelector('.reply-input').focus(); }
      return;
    }
    if (e.target.closest('.reply-send-btn')) {
      const row = card.querySelector('.reply-row');
      const input = row.querySelector('.reply-input');
      if (!input.value.trim()) return; // sent untrimmed below — formatting kept as typed
      postToHost({ action: 'peerSend', pcId, text: input.value });
      input.value = '';
      row.classList.remove('open');
      return;
    }
    if (e.target.closest('.peer-copy-btn')) {
      if (item && item.text) { postToHost({ action: 'copyText', text: item.text }); showToast('📋 Copiado'); }
      return;
    }
    if (e.target.closest('.peer-open-file-btn')) { postToHost({ action: 'peerOpenFile', pcId, id }); return; }
    if (e.target.closest('.peer-show-file-btn')) { postToHost({ action: 'peerShowFile', pcId, id }); return; }
    if (e.target.closest('.peer-cancel-btn')) { postToHost({ action: 'peerCancelTransfer', pcId, transferId: id }); return; }
    if (e.target.closest('.peer-chat-btn')) openPeerChatOverlay(pcId);
  }, true);
  listEl.addEventListener('dblclick', e => {
    const card = peerCardEvent(e);
    if (!card || e.target.closest('button, input, a')) return;
    const sel = window.getSelection();
    if (sel) sel.removeAllRanges();
    playCardRipple(card, e.clientX, e.clientY);
    openPeerChatOverlay(activePeerTab);
  }, true);
  listEl.addEventListener('mousedown', peerCardEvent, true);
  listEl.addEventListener('dragstart', e => { if (peerCardEvent(e)) e.preventDefault(); }, true);

  // The bar above a PC's cards: write to it, send files / the clipboard, nudge, open the chat.
  // Sent exactly as written — line breaks, tabs and spacing included (only an all-blank message
  // is refused); both sides render it with white-space: pre-wrap.
  function sendPeerFeedText() {
    const text = peerFeedInput.value;
    if (!text.trim() || !peerTabActive()) return;
    postToHost({ action: 'peerSend', pcId: activePeerTab, text });
    peerFeedInput.value = '';
    autoSizePeerFeedInput();
  }
  function autoSizePeerFeedInput() {
    peerFeedInput.style.height = 'auto';
    peerFeedInput.style.height = `${Math.min(peerFeedInput.scrollHeight, 160)}px`;
  }
  document.getElementById('peer-feed-send').addEventListener('click', sendPeerFeedText);
  peerFeedInput.addEventListener('keydown', e => {
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendPeerFeedText(); }
  });
  peerFeedInput.addEventListener('input', autoSizePeerFeedInput);
  document.getElementById('peer-feed-attach').addEventListener('click', () => peerFeedFileInput.click());
  peerFeedFileInput.addEventListener('change', () => {
    if (peerTabActive()) sendFilesToPeer(activePeerTab, peerFeedFileInput.files);
    peerFeedFileInput.value = '';
  });
  document.getElementById('peer-feed-clip').addEventListener('click', () => {
    if (peerTabActive()) postToHost({ action: 'peerSendClipboard', pcId: activePeerTab });
  });
  document.getElementById('peer-feed-nudge').addEventListener('click', () => {
    if (peerTabActive()) postToHost({ action: 'peerNudge', pcId: activePeerTab });
  });
  document.getElementById('peer-feed-chat').addEventListener('click', () => {
    if (peerTabActive()) openPeerChatOverlay(activePeerTab);
  });
  peerFeedViewBtn.addEventListener('click', () => { if (peerTabActive()) viewPeerScreen(activePeerTab); });
  peerFeedShareBtn.addEventListener('click', () => { if (peerTabActive()) togglePeerScreenShare(activePeerTab); });

  // ---- Screen sharing between PCs (PeerLinkService + ScreenCaptureService on the C# side; the
  // viewer is its own native window, not this page). Asking to see another PC's screen needs its
  // OK once — "Permitir siempre" (AllowScreen) — or every time ("Permitir esta vez").
  const pcsScreenOverlay = document.getElementById('pcs-screen-overlay');
  const peerScreenChip = document.getElementById('peer-screen-chip');
  let pendingScreenRequest = null; // { pcId, timer }

  function viewPeerScreen(pcId) {
    const p = peers.find(x => x.pcId === pcId);
    if (!p || !p.connected) { showToast(`⚠️ ${peerName(pcId)} no está conectada ahora mismo`); return; }
    if (p.viewingScreen) { showToast(`Ya estás viendo la pantalla de ${p.name}`); return; }
    postToHost({ action: 'peerScreenView', pcId });
    showToast(`🖥️ Pidiendo la pantalla de ${p.name}… (si no te dio permiso permanente, tiene que aceptar ahí)`, 4200);
  }

  function togglePeerScreenShare(pcId) {
    const p = peers.find(x => x.pcId === pcId);
    if (!p) return;
    if (p.sharingScreen) postToHost({ action: 'peerScreenStop', pcId });
    else if (!p.connected) showToast(`⚠️ ${p.name} no está conectada ahora mismo`);
    else postToHost({ action: 'peerScreenShare', pcId });
  }

  // Header pill while anyone is watching this PC's screen — always visible, whatever tab is
  // selected, and a click stops every share.
  function renderPeerScreenChip() {
    const watchers = peers.filter(p => p.sharingScreen);
    const drivers = peers.filter(p => p.controlledBy);
    peerScreenChip.classList.toggle('show', watchers.length > 0);
    peerScreenChip.textContent = drivers.length
      ? `● ${drivers.map(p => p.name).join(', ')} controla tu PC — clic para detener`
      : watchers.length ? `● Compartiendo pantalla con ${watchers.map(p => p.name).join(', ')}` : '';
  }
  // Stopping the share also ends any control that rode on it.
  peerScreenChip.addEventListener('click', () => {
    peers.filter(p => p.sharingScreen).forEach(p => postToHost({ action: 'peerScreenStop', pcId: p.pcId }));
  });

  // One prompt for both questions: seeing this screen ('screen') and driving this PC ('control').
  function answerScreenRequest(allow, always) {
    if (!pendingScreenRequest) return;
    clearTimeout(pendingScreenRequest.timer);
    const action = pendingScreenRequest.kind === 'control' ? 'peerControlAnswer' : 'peerScreenAnswer';
    postToHost({ action, pcId: pendingScreenRequest.pcId, allow, always });
    pendingScreenRequest = null;
    pcsScreenOverlay.classList.remove('open');
  }
  function askScreenPermission(kind, pcId, name) {
    if (pendingScreenRequest) answerScreenRequest(false, false); // only one question at a time
    const control = kind === 'control';
    document.getElementById('pcs-screen-title').textContent = control ? 'Control remoto' : 'Compartir pantalla';
    document.getElementById('pcs-screen-from').textContent = name;
    document.getElementById('pcs-screen-what').textContent = control
      ? 'quiere controlar tu PC (mouse y teclado).'
      : 'quiere ver tu pantalla.';
    document.getElementById('pcs-screen-note').textContent = control
      ? 'Podrá mover el mouse, hacer clic y escribir en esta PC mientras vea tu pantalla. Lo detienes con el aviso rojo de arriba. "Permitir siempre" no vuelve a preguntar por esta PC; se quita desde su menú ⋯ en PCs vinculadas.'
      : 'Solo podrá verla. "Permitir siempre" no vuelve a preguntar por esta PC; se quita desde su menú ⋯ en PCs vinculadas.';
    // Unanswered for a minute counts as "no" — nobody's at this PC.
    pendingScreenRequest = { kind, pcId, timer: setTimeout(() => answerScreenRequest(false, false), 60000) };
    pcsScreenOverlay.classList.add('open');
  }
  window.onPeerScreenRequest = (pcId, name) => askScreenPermission('screen', pcId, name);
  window.onPeerControlRequest = (pcId, name) => askScreenPermission('control', pcId, name);
  window.onPeerScreenNotice = text => showToast(text, 4200);
  document.getElementById('pcs-screen-always').addEventListener('click', () => answerScreenRequest(true, true));
  document.getElementById('pcs-screen-once').addEventListener('click', () => answerScreenRequest(true, false));
  document.getElementById('pcs-screen-deny').addEventListener('click', () => answerScreenRequest(false, false));

  // ---- host → page
  window.setPeers = function (list, self) {
    const before = pcsAwaitingNewPeer;
    peers = list || [];
    peerSelf = self || peerSelf;
    updatePcsBadge();
    renderPeerScreenChip();
    // Keep a selected PC tab in step: its header line, or back to the phones if it was unpaired.
    if (activePeerTab && !peerTabActive()) setActivePeerTab(null);
    if (peerTabActive() && !peerChats.has(activePeerTab)) postToHost({ action: 'peerHistory', pcId: activePeerTab });
    if (peerTabActive() || document.body.classList.contains('peer-tab')) syncActiveDeviceUi();
    if (before) {
      const added = peers.find(p => !before.has(p.pcId));
      if (added) { pcsAwaitingNewPeer = null; if (pcsIsOpen()) openPeerChat(added.pcId); return; }
    }
    if (pcsView === 'chat' && !peers.some(p => p.pcId === activePeerId)) {
      activePeerId = null;
      showPcsView('empty');
      return;
    }
    renderPeerList();
    if (pcsView === 'chat') renderChatHeader();
  };

  window.setPeerChat = function (pcId, items) {
    peerChats.set(pcId, items || []);
    if (pcsView === 'chat' && pcId === activePeerId) renderPeerMessages(true);
    if (peerTabActive() && pcId === activePeerTab) renderPeerFeed();
  };

  window.onPeerChatItem = function (pcId, item) {
    const items = peerChats.get(pcId);
    let isNew = true;
    if (items) {
      const i = items.findIndex(x => x.id === item.id);
      isNew = i < 0;
      if (i >= 0) items[i] = item; else items.push(item);
    }
    const chatOpen = pcsIsOpen() && pcsView === 'chat' && pcId === activePeerId;
    const tabShown = peerTabActive() && pcId === activePeerTab;
    const viewing = chatOpen || (tabShown && !pcsIsOpen());
    if (chatOpen && items) {
      const existing = !isNew && pcsMessages.querySelector(`[data-id="${CSS.escape(item.id)}"]`);
      if (existing) existing.outerHTML = peerItemHtml(item);
      else renderPeerMessages(item.outgoing);
    }
    if (tabShown && items) {
      // Progress ticks only touch their own card; anything else re-renders the feed.
      const existing = !isNew && listEl.querySelector(`.peer-card[data-peer-item="${CSS.escape(item.id)}"]`);
      if (existing && !existing.querySelector('.reply-row.open')) existing.outerHTML = peerCardHtml(item);
      else if (!existing) renderPeerFeed();
    }
    // A brand-new incoming line (not a progress tick of an existing file) counts as unread.
    const firstSight = !item.outgoing && (item.kind !== 'file' || (item.state === 'receiving' && !item.transferred));
    if (firstSight && (isNew || !items)) {
      if (!viewing) {
        peerUnread.set(pcId, (peerUnread.get(pcId) || 0) + 1);
        updatePcsBadge();
        renderPeerList();
      }
      const from = peerName(pcId);
      if (item.kind === 'text') showToast(`💬 ${from}: ${item.text.length > 80 ? item.text.slice(0, 80) + '…' : item.text}`);
      else if (item.kind === 'clipboard') showToast(`📋 Portapapeles de ${from} copiado en esta PC`);
      else if (item.kind === 'file') showToast(`📥 ${from} te está enviando ${item.fileName}`);
    }
    if (!item.outgoing && item.kind === 'file' && item.state === 'done' && !viewing) showToast(`✅ ${item.fileName} guardado en Descargas`);
    if (item.outgoing && item.kind === 'file' && item.state === 'failed') showToast(`⚠️ No se pudo enviar ${item.fileName}`);
  };

  window.setPeerScanResults = function (list) {
    if (!list || !list.length) {
      pcsScanList.innerHTML = '<div class="pcs-scan-empty">No se encontró ninguna PC con NotiLink en esta red. Revisa que esté abierta, o escribe su IP abajo.</div>';
      return;
    }
    pcsScanList.innerHTML = list.map(d => `<div class="pcs-scan-item">
      <span class="pcs-peer-text"><span class="pcs-peer-name">${escapeHtml(d.name)}</span><span class="pcs-peer-sub">${escapeHtml(d.ip)}</span></span>
      <button class="action-btn-open pcs-scan-pair" data-ip="${escapeHtml(d.ip)}" type="button">${d.alreadyLinked ? 'Volver a vincular' : 'Vincular'}</button>
    </div>`).join('');
  };

  window.onPeerPairCodeNeeded = function (name) {
    if (!pcsIsOpen()) pcsOverlay.classList.add('open');
    if (pcsView !== 'add') showPcsView('add');
    pcsCodeTarget.textContent = name;
    pcsCodeStep.style.display = 'block'; // hidden by default in theme.css, so '' would keep it hidden
    pcsAddStatus.textContent = `Mira la pantalla de ${name}: ahí aparece el código.`;
    pcsCodeInput.value = '';
    setTimeout(() => pcsCodeInput.focus(), 0);
  };

  window.onPeerPairResult = function (ok, message) {
    showToast(ok ? `🖥️ ${message}` : `⚠️ ${message}`, 4200);
    if (pcsView === 'add') pcsAddStatus.textContent = message;
    if (ok) { pcsCodeStep.style.display = 'none'; pcsCodeOverlay.classList.remove('open'); }
    else if (!/intento/.test(message)) pcsCodeStep.style.display = 'none';
    postToHost({ action: 'peerList' });
  };

  window.onPeerPairCode = function (name, code) {
    document.getElementById('pcs-code-from').textContent = name;
    document.getElementById('pcs-code-display').textContent = `${code.slice(0, 3)} ${code.slice(3)}`;
    pcsCodeOverlay.classList.add('open');
  };
  window.onPeerPairCodeDismissed = () => pcsCodeOverlay.classList.remove('open');

  window.onPeerSendFailed = pcId => showToast(`⚠️ ${peerName(pcId)} no está conectada ahora mismo`);
  window.onPeerClipboardEmpty = () => showToast('📋 El portapapeles de esta PC no tiene texto');
  window.onPeerFileMissing = () => showToast('⚠️ Ese archivo ya no está en su ubicación');
  window.onPeerNudge = function (pcId, name) {
    showToast(`👋 ${name} te está llamando la atención`, 5000);
    document.body.classList.remove('pcs-shake');
    void document.body.offsetWidth;
    document.body.classList.add('pcs-shake');
    setTimeout(() => document.body.classList.remove('pcs-shake'), 900);
  };

  // ---- page → host
  pcsBtn.addEventListener('click', openPcsOverlay);
  document.getElementById('device-menu-pcs').addEventListener('click', () => {
    document.getElementById('device-menu').classList.remove('open');
    openPcsOverlay();
  });
  document.getElementById('pcs-close').addEventListener('click', () => pcsOverlay.classList.remove('open'));
  pcsOverlay.addEventListener('click', e => {
    if (e.target === pcsOverlay) pcsOverlay.classList.remove('open');
    if (!e.target.closest('#pcs-menu') && !e.target.closest('#pcs-menu-btn')) pcsMenu.classList.remove('open');
  });
  document.getElementById('pcs-add-btn').addEventListener('click', openAddView);
  document.getElementById('pcs-empty-add').addEventListener('click', openAddView);
  document.getElementById('pcs-scan-btn').addEventListener('click', scanPeers);
  pcsListEl.addEventListener('click', e => {
    const btn = e.target.closest('.pcs-peer');
    if (btn) openPeerChat(btn.dataset.pcId);
  });
  pcsScanList.addEventListener('click', e => {
    const btn = e.target.closest('.pcs-scan-pair');
    if (btn) startPairing(btn.dataset.ip);
  });
  document.getElementById('pcs-ip-connect').addEventListener('click', () => startPairing(pcsIpInput.value.trim()));
  pcsIpInput.addEventListener('keydown', e => { if (e.key === 'Enter') startPairing(pcsIpInput.value.trim()); });
  function submitPairCode() {
    const code = pcsCodeInput.value.replace(/\D/g, '');
    if (code.length !== 6) { pcsAddStatus.textContent = 'El código tiene 6 dígitos.'; return; }
    pcsAddStatus.textContent = 'Comprobando…';
    postToHost({ action: 'peerPairCode', code });
  }
  document.getElementById('pcs-code-send').addEventListener('click', submitPairCode);
  pcsCodeInput.addEventListener('keydown', e => { if (e.key === 'Enter') submitPairCode(); });
  document.getElementById('pcs-code-cancel').addEventListener('click', () => {
    postToHost({ action: 'peerPairCancel' });
    pcsCodeStep.style.display = 'none';
    pcsAddStatus.textContent = '';
    pcsAwaitingNewPeer = null;
  });
  document.getElementById('pcs-code-reject').addEventListener('click', () => {
    postToHost({ action: 'peerPairReject' });
    pcsCodeOverlay.classList.remove('open');
  });

  document.getElementById('pcs-send-btn').addEventListener('click', sendPeerText);
  pcsInput.addEventListener('keydown', e => {
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendPeerText(); }
  });
  pcsInput.addEventListener('input', autoSizePcsInput);
  document.getElementById('pcs-clip-btn').addEventListener('click', () => {
    if (activePeerId) postToHost({ action: 'peerSendClipboard', pcId: activePeerId });
  });
  document.getElementById('pcs-nudge-btn').addEventListener('click', () => {
    if (activePeerId) postToHost({ action: 'peerNudge', pcId: activePeerId });
  });
  document.getElementById('pcs-attach-btn').addEventListener('click', () => pcsFileInput.click());
  pcsFileInput.addEventListener('change', () => {
    if (activePeerId) sendFilesToPeer(activePeerId, pcsFileInput.files);
    pcsFileInput.value = '';
  });
  pcsMessages.addEventListener('click', e => {
    const btn = e.target.closest('button[data-id]');
    if (!btn || !activePeerId) return;
    const id = btn.dataset.id;
    if (btn.classList.contains('pcs-copy')) {
      const item = findPeerItem(id);
      if (item && item.text) { postToHost({ action: 'copyText', text: item.text }); showToast('📋 Copiado'); }
    } else if (btn.classList.contains('pcs-file-open')) {
      postToHost({ action: 'peerOpenFile', pcId: activePeerId, id });
    } else if (btn.classList.contains('pcs-file-show')) {
      postToHost({ action: 'peerShowFile', pcId: activePeerId, id });
    } else if (btn.classList.contains('pcs-file-cancel')) {
      postToHost({ action: 'peerCancelTransfer', pcId: activePeerId, transferId: id });
    }
  });

  document.getElementById('pcs-menu-btn').addEventListener('click', e => {
    e.stopPropagation();
    const p = peers.find(x => x.pcId === activePeerId);
    if (p) {
      document.getElementById('pcs-menu-screen-share').textContent = p.sharingScreen ? 'Dejar de compartir mi pantalla' : 'Compartir mi pantalla';
      document.getElementById('pcs-menu-screen-allow').textContent = p.allowScreen
        ? '✓ Puede ver mi pantalla sin preguntar (quitar)'
        : 'Dejar que vea mi pantalla sin preguntar';
      document.getElementById('pcs-menu-control-allow').textContent = p.allowControl
        ? '✓ Puede controlar mi PC sin preguntar (quitar)'
        : 'Dejar que controle mi PC sin preguntar';
    }
    pcsMenu.classList.toggle('open');
  });
  document.getElementById('pcs-menu-screen-view').addEventListener('click', () => {
    pcsMenu.classList.remove('open');
    if (activePeerId) viewPeerScreen(activePeerId);
  });
  document.getElementById('pcs-menu-screen-share').addEventListener('click', () => {
    pcsMenu.classList.remove('open');
    if (activePeerId) togglePeerScreenShare(activePeerId);
  });
  document.getElementById('pcs-menu-control-allow').addEventListener('click', () => {
    pcsMenu.classList.remove('open');
    const p = peers.find(x => x.pcId === activePeerId);
    if (!p) return;
    postToHost({ action: 'peerControlAllow', pcId: p.pcId, allow: !p.allowControl });
    showToast(p.allowControl
      ? `${p.name} tendrá que pedir permiso para controlar tu PC`
      : `${p.name} podrá controlar tu PC sin preguntar (mientras vea tu pantalla)`);
  });
  document.getElementById('pcs-menu-screen-allow').addEventListener('click', () => {
    pcsMenu.classList.remove('open');
    const p = peers.find(x => x.pcId === activePeerId);
    if (!p) return;
    postToHost({ action: 'peerScreenAllow', pcId: p.pcId, allow: !p.allowScreen });
    showToast(p.allowScreen
      ? `${p.name} tendrá que pedir permiso para ver tu pantalla`
      : `${p.name} podrá ver tu pantalla sin preguntar`);
  });
  document.getElementById('pcs-menu-rename').addEventListener('click', () => {
    pcsMenu.classList.remove('open');
    pcsRenameInput.value = peerName(activePeerId);
    pcsChatName.style.display = 'none';
    pcsRenameInput.style.display = 'inline-block';
    pcsRenameInput.focus();
    pcsRenameInput.select();
  });
  function finishPeerRename(save) {
    if (pcsRenameInput.style.display === 'none') return;
    const name = pcsRenameInput.value.trim();
    pcsRenameInput.style.display = 'none';
    pcsChatName.style.display = '';
    if (save && name && activePeerId) postToHost({ action: 'peerRename', pcId: activePeerId, name });
  }
  pcsRenameInput.addEventListener('keydown', e => {
    if (e.key === 'Enter') finishPeerRename(true);
    if (e.key === 'Escape') { e.stopPropagation(); finishPeerRename(false); }
  });
  pcsRenameInput.addEventListener('blur', () => finishPeerRename(true));
  document.getElementById('pcs-menu-clear').addEventListener('click', () => {
    pcsMenu.classList.remove('open');
    if (activePeerId && confirm('¿Borrar la conversación con esta PC? Solo se borra aquí; los archivos recibidos se quedan en Descargas.'))
      postToHost({ action: 'peerClearHistory', pcId: activePeerId });
  });
  document.getElementById('pcs-menu-unpair').addEventListener('click', () => {
    pcsMenu.classList.remove('open');
    if (activePeerId && confirm(`¿Desvincular ${peerName(activePeerId)}? Para volver a conectarlas habrá que escribir un código nuevo.`)) {
      postToHost({ action: 'peerUnpair', pcId: activePeerId });
      peerChats.delete(activePeerId);
    }
  });

  document.addEventListener('keydown', e => {
    if (e.key !== 'Escape') return;
    if (pcsCodeOverlay.classList.contains('open')) return; // must answer it explicitly
    if (pcsIsOpen()) pcsOverlay.classList.remove('open');
  });

  showPcsView('empty');
})();
