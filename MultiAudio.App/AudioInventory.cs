using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MultiAudio.App;

/// <summary>A physical audio output (render) endpoint.</summary>
/// <summary>An output device. FormFactor is Windows' EndpointFormFactor (1 speakers, 3 headphones, 5 headset,
/// 9 HDMI/DisplayPort display, ...; -1 unknown). Bus: the driver enumerator (HDAUDIO, USB, BTHENUM, ...).</summary>
public record OutputDevice(string Id, string Name, bool IsDefault, string State, int FormFactor = -1, string Bus = "")
{
    /// <summary>Connected over Bluetooth (A2DP speakers enumerate under BTHENUM, hands-free under BTHHFENUM).</summary>
    public bool IsBluetooth => Bus.StartsWith("BTH", StringComparison.OrdinalIgnoreCase);
    /// <summary>Wired into the PC itself (the on-board HD Audio codec, e.g. Realtek).</summary>
    public bool IsBuiltIn => Bus.Equals("HDAUDIO", StringComparison.OrdinalIgnoreCase);
}

/// <summary>An application currently holding an audio session on some output.</summary>
/// <summary>An app with an audio session. DeviceId/DeviceName: the output it is playing on right now.</summary>
public record AudioApp(int ProcessId, string ProcessName, string DeviceName, string State, string DeviceId);

/// <summary>
/// Read-only discovery of output devices and audio-producing apps via WASAPI.
/// This is Milestone 1: no capture or routing yet, just an accurate inventory.
/// </summary>
public static class AudioInventory
{
    public static List<OutputDevice> GetOutputDevices()
    {
        var result = new List<OutputDevice>();
        using var en = new MMDeviceEnumerator();

        string defaultId = "";
        try
        {
            var def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            defaultId = def.ID;
        }
        catch { /* no default device present */ }

        foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            result.Add(new OutputDevice(
                Id: dev.ID,
                Name: dev.FriendlyName,
                IsDefault: dev.ID == defaultId,
                State: dev.State.ToString(),
                FormFactor: ReadProperty(dev, FormFactorKey) is uint ff ? (int)ff : -1,
                Bus: ReadProperty(dev, EnumeratorKey) as string ?? ""));
        }
        return result;
    }

    private static readonly PropertyKey FormFactorKey = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);  // PKEY_AudioEndpoint_FormFactor
    private static readonly PropertyKey EnumeratorKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24); // PKEY_Device_EnumeratorName

    private static object? ReadProperty(MMDevice dev, PropertyKey key)
    {
        try { var store = dev.Properties; return store.Contains(key) ? store[key].Value : null; }
        catch { return null; } // some drivers don't expose every property
    }

    /// <summary>Live peak meters for every app session, keyed by (process id, device id). Poll them on the UI thread.</summary>
    public static Dictionary<(int Pid, string DeviceId), AudioMeterInformation> GetSessionMeters()
    {
        var map = new Dictionary<(int, string), AudioMeterInformation>();
        using var en = new MMDeviceEnumerator();
        foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            SessionCollection sessions;
            try { sessions = dev.AudioSessionManager.Sessions; } catch { continue; }
            for (int i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var s = sessions[i];
                    int pid = (int)s.GetProcessID;
                    if (pid != 0) map[(pid, dev.ID)] = s.AudioMeterInformation;
                }
                catch { } // session closed mid-loop
            }
        }
        return map;
    }

    private static readonly string OwnProcessName = Process.GetCurrentProcess().ProcessName;

    public static List<AudioApp> GetActiveAudioApps()
    {
        var byPid = new Dictionary<int, AudioApp>();
        using var en = new MMDeviceEnumerator();

        foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            SessionCollection sessions;
            try { sessions = dev.AudioSessionManager.Sessions; }
            catch { continue; }

            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                uint pid;
                try { pid = s.GetProcessID; } catch { continue; }
                if (pid == 0) continue; // system session
                if (pid == Environment.ProcessId) continue; // our own playback sessions: routing ourselves would loop

                // Only surface sessions that are (or were) actually playing.
                var state = s.State.ToString();

                string name;
                try { name = Process.GetProcessById((int)pid).ProcessName; }
                catch { name = "(pid " + pid + ")"; }
                // Another running copy of Audix: its sessions are routed copies, not an app (and routing them would loop).
                if (name == OwnProcessName) continue;

                // Dedupe by process; prefer an Active session's device if we see several.
                if (!byPid.ContainsKey((int)pid) || s.State == AudioSessionState.AudioSessionStateActive)
                    byPid[(int)pid] = new AudioApp((int)pid, name, dev.FriendlyName, state, dev.ID);
            }
        }
        return byPid.Values.OrderBy(a => a.ProcessName).ToList();
    }

    /// <summary>Mute/unmute an app's session on the DEFAULT (system speaker) device.
    /// Used by the per-app "also play on system speaker" toggle.</summary>
    public static void SetAppMuted(int pid, bool muted)
    {
        using var en = new MMDeviceEnumerator();
        MMDevice def;
        try { def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
        catch { return; }

        var sessions = def.AudioSessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            try { if (s.GetProcessID == (uint)pid) s.SimpleAudioVolume.Mute = muted; }
            catch { }
        }
    }

    /// <summary>Set the app's own volume (0..1) on every output it has a session on — the same
    /// setting as its slider in Windows' Volume mixer.</summary>
    public static void SetAppVolume(int pid, float volume)
    {
        volume = Math.Clamp(volume, 0f, 1f);
        using var en = new MMDeviceEnumerator();
        foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            SessionCollection sessions;
            try { sessions = dev.AudioSessionManager.Sessions; } catch { continue; }
            for (int i = 0; i < sessions.Count; i++)
            {
                try { if (sessions[i].GetProcessID == (uint)pid) sessions[i].SimpleAudioVolume.Volume = volume; }
                catch { } // session closed mid-loop
            }
        }
    }

    /// <summary>The app's volume (0..1), from an active session if it has one; 1 if it has none.</summary>
    public static float GetAppVolume(int pid)
    {
        using var en = new MMDeviceEnumerator();
        float? any = null;
        foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            SessionCollection sessions;
            try { sessions = dev.AudioSessionManager.Sessions; } catch { continue; }
            for (int i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var s = sessions[i];
                    if (s.GetProcessID != (uint)pid) continue;
                    if (s.State == AudioSessionState.AudioSessionStateActive) return s.SimpleAudioVolume.Volume;
                    any ??= s.SimpleAudioVolume.Volume;
                }
                catch { }
            }
        }
        return any ?? 1f;
    }

    /// <summary>Whether the app is currently muted on the default (system speaker) device.</summary>
    public static bool GetAppMuted(int pid)
    {
        using var en = new MMDeviceEnumerator();
        MMDevice def;
        try { def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
        catch { return false; }

        var sessions = def.AudioSessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            try { if (s.GetProcessID == (uint)pid) return s.SimpleAudioVolume.Mute; }
            catch { }
        }
        return false;
    }
}
