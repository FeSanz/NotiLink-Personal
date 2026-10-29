using NAudio.CoreAudioApi;

namespace NotiLink.App.Services;

/// <summary>
/// Reads the real, live peak audio level of one specific process's own Windows audio session —
/// the same per-app volume meter Windows' own Volume Mixer shows, via WASAPI (wrapped by NAudio,
/// since .NET has no built-in Core Audio API surface). Used to drive a genuinely audio-reactive
/// waveform for the phone audio scrcpy is forwarding to this PC (see ScreenShareService):
/// scrcpy plays that forwarded audio through a normal Windows audio session under its own
/// process id, exactly like any other app, so its real level is something this PC can actually
/// measure — unlike the media player's own equalizer, which is decorative because NotiLink has
/// no access to Spotify's actual audio data, only playback control.
/// </summary>
internal static class AudioLevelMonitor
{
    /// <summary>Fires roughly every 100ms while watching, with the session's current peak level
    /// (0.0-1.0, silence to full scale) — exactly what WASAPI itself reports, never smoothed or
    /// invented here (the UI can smooth/decay it for display if it wants a less jumpy look).</summary>
    public static event Action<float>? LevelChanged;

    /// <summary>Fires once if no matching audio session was ever found within the retry window —
    /// concrete signal for "nothing shows up" reports, since a silent give-up looks identical to
    /// the effect just being too subtle to notice otherwise. Would mean scrcpy's SDL audio
    /// backend isn't registering a normal per-process WASAPI session the way most apps do.</summary>
    public static event Action? SessionNotFound;

    private static System.Threading.Timer? _timer;
    private static AudioSessionControl? _session;
    private static int _watchingProcessId;
    private static int _findAttempts;

    /// <summary>Starts watching [processId]'s own audio session. Safe to call again with a new
    /// id while already watching one — stops the old watch first. The session doesn't
    /// necessarily exist the instant the process starts (it's created once that process actually
    /// opens an audio stream), so this retries finding it for a few seconds before giving up.</summary>
    public static void Start(int processId)
    {
        Stop();
        _watchingProcessId = processId;
        _findAttempts = 0;
        _timer = new System.Threading.Timer(_ => Tick(), null, 0, 100);
    }

    public static void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _session = null;
        _watchingProcessId = 0;
    }

    private static void Tick()
    {
        try
        {
            if (_session == null)
            {
                _session = FindSession(_watchingProcessId);
                if (_session == null)
                {
                    // Retries for ~8s — the process may just not have opened its audio stream
                    // yet, or (if the phone side never actually sent any audio) may never will.
                    if (++_findAttempts > 80)
                    {
                        Stop();
                        SessionNotFound?.Invoke();
                    }
                    return;
                }
            }
            var level = _session.AudioMeterInformation.MasterPeakValue;
            LevelChanged?.Invoke(level);
        }
        catch (Exception)
        {
            // The session can become invalid mid-poll if the process exits between ticks —
            // drop it and let the next tick's FindSession look for a fresh one (or give up, if
            // the watched process is actually gone).
            _session = null;
        }
    }

    private static AudioSessionControl? FindSession(int processId)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = device.AudioSessionManager.Sessions;
        for (var i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            if (session.GetProcessID == (uint)processId) return session;
        }
        return null;
    }
}
