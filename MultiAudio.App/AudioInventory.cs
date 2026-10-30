using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MultiAudio.App;

/// <summary>A physical audio output (render) endpoint.</summary>
public record OutputDevice(string Id, string Name, bool IsDefault, string State);

/// <summary>An application currently holding an audio session on some output.</summary>
public record AudioApp(int ProcessId, string ProcessName, string DeviceName, string State);

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
                State: dev.State.ToString()));
        }
        return result;
    }

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

                // Only surface sessions that are (or were) actually playing.
                var state = s.State.ToString();

                string name;
                try { name = Process.GetProcessById((int)pid).ProcessName; }
                catch { name = "(pid " + pid + ")"; }

                // Dedupe by process; prefer an Active session's device if we see several.
                if (!byPid.ContainsKey((int)pid) || s.State == AudioSessionState.AudioSessionStateActive)
                    byPid[(int)pid] = new AudioApp((int)pid, name, dev.FriendlyName, state);
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
