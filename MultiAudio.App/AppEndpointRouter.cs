using System.Runtime.InteropServices;

namespace MultiAudio.App;

/// <summary>
/// Exclusive "move" mode: redirects an application's OWN default output device using
/// the same undocumented per-app endpoint API that Windows' "App volume and device
/// preferences" page uses (IAudioPolicyConfigFactory.SetPersistedDefaultAudioEndpoint).
///
/// Unlike our loopback engine (which taps + duplicates), this MOVES the app natively:
/// perfect quality, zero added latency, no echo. It is fully reversible via Reset.
///
/// Interface definitions verified against EarTrumpet (github.com/File-New-Project/EarTrumpet).
/// The interface is IInspectable/WinRT; the two variants differ only by IID (Win10 vs 11).
/// </summary>
public static class AppEndpointRouter
{
    private const string MmdevapiToken = @"\\?\SWD#MMDEVAPI#";
    private const string RenderInterface = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const string AudioPolicyConfigClass = "Windows.Media.Internal.AudioPolicyConfig";

    private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    /// <summary>Redirect the app's own render output to the given MMDevice id.
    /// Returns a human-readable result (HRESULTs + read-back) for diagnostics.</summary>
    public static string Move(int processId, string mmDeviceId)
    {
        var factory = CreateFactory();
        var str = MmdevapiToken + mmDeviceId + RenderInterface;
        WindowsCreateString(str, (uint)str.Length, out IntPtr hstring);
        int h1 = Set(factory, processId, ERole.eMultimedia, hstring);
        int h2 = Set(factory, processId, ERole.eConsole, hstring);
        Marshal.ThrowExceptionForHR(h1 < 0 ? h1 : h2); // no-op when both succeeded
        string readback = Get(factory, processId, ERole.eMultimedia);
        return $"hr(mm)=0x{h1:X8} hr(con)=0x{h2:X8}; now persisted='{Shorten(readback)}'";
    }

    /// <summary>Restore the app to follow the system default (undo a Move).</summary>
    public static string ResetToDefault(int processId)
    {
        var factory = CreateFactory();
        int h1 = Set(factory, processId, ERole.eMultimedia, IntPtr.Zero); // empty == follow default
        int h2 = Set(factory, processId, ERole.eConsole, IntPtr.Zero);
        return $"hr(mm)=0x{h1:X8} hr(con)=0x{h2:X8}";
    }

    /// <summary>Clear EVERY app's persisted per-app default output, returning every app to
    /// the Windows system default. This wipes overrides set by anyone (us or the user).</summary>
    public static string ClearAll()
    {
        var factory = CreateFactory();
        int hr = factory switch
        {
            IAudioPolicyConfigFactoryWin11 f => f.ClearAllPersistedApplicationDefaultEndpoints(),
            IAudioPolicyConfigFactoryWin10 f => f.ClearAllPersistedApplicationDefaultEndpoints(),
            _ => unchecked((int)0x80004002),
        };
        return $"hr=0x{hr:X8}";
    }

    private static int Set(object factory, int pid, ERole role, IntPtr hstring) => factory switch
    {
        IAudioPolicyConfigFactoryWin11 f => f.SetPersistedDefaultAudioEndpoint((uint)pid, EDataFlow.eRender, role, hstring),
        IAudioPolicyConfigFactoryWin10 f => f.SetPersistedDefaultAudioEndpoint((uint)pid, EDataFlow.eRender, role, hstring),
        _ => unchecked((int)0x80004002),
    };

    private static string Get(object factory, int pid, ERole role)
    {
        try
        {
            IntPtr hs = IntPtr.Zero;
            int hr = factory switch
            {
                IAudioPolicyConfigFactoryWin11 f => f.GetPersistedDefaultAudioEndpoint((uint)pid, EDataFlow.eRender, role, out hs),
                IAudioPolicyConfigFactoryWin10 f => f.GetPersistedDefaultAudioEndpoint((uint)pid, EDataFlow.eRender, role, out hs),
                _ => unchecked((int)0x80004002),
            };
            if (hr != 0) return $"(get hr=0x{hr:X8})";
            if (hs == IntPtr.Zero) return "";
            var buf = WindowsGetStringRawBuffer(hs, out uint len);
            return len == 0 ? "" : Marshal.PtrToStringUni(buf, (int)len) ?? "";
        }
        catch (Exception ex) { return "(get threw: " + ex.Message + ")"; }
    }

    [DllImport("combase.dll")]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    private static string Shorten(string s)
    {
        if (string.IsNullOrEmpty(s)) return "(default)";
        int i = s.LastIndexOf('.');
        return i > 0 && i < s.Length - 1 ? s[(i + 1)..].TrimEnd('}') : s;
    }

    private static object CreateFactory()
    {
        // Windows 11 (21H2) is build 22000+.
        bool win11 = Environment.OSVersion.Version.Build >= 22000;
        var iid = win11
            ? typeof(IAudioPolicyConfigFactoryWin11).GUID
            : typeof(IAudioPolicyConfigFactoryWin10).GUID;

        // .NET 8's DllImport marshaller can't turn a string into an HSTRING, so build the
        // class-id HSTRING by hand and pass raw handles; wrap the returned IInspectable* as a RCW.
        WindowsCreateString(AudioPolicyConfigClass, (uint)AudioPolicyConfigClass.Length, out IntPtr hClass);
        RoGetActivationFactory(hClass, ref iid, out IntPtr factoryPtr);
        return Marshal.GetObjectForIUnknown(factoryPtr);
    }

    // ---------------- native interop ----------------

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void RoGetActivationFactory(
        IntPtr activatableClassId,  // HSTRING
        [In] ref Guid iid,
        out IntPtr factory);        // IInspectable**

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string src,
        [In] uint length,
        [Out] out IntPtr hstring);

    // Both interfaces share an identical vtable; only the IID differs. The 19
    // __incomplete__ stubs occupy the vtable slots that precede the methods we call,
    // so the layout must be reproduced EXACTLY and in order.

    [Guid("ab3d4648-e242-459f-b02f-541c70306324")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioPolicyConfigFactoryWin11
    {
        // In IUnknown mode the CLR reserves only the 3 IUnknown slots, but the real
        // object is IInspectable — so declare its 3 methods here to keep the vtable aligned.
        int __inspectable__GetIids();
        int __inspectable__GetRuntimeClassName();
        int __inspectable__GetTrustLevel();
        int __incomplete__add_CtxVolumeChange();
        int __incomplete__remove_CtxVolumeChanged();
        int __incomplete__add_RingerVibrateStateChanged();
        int __incomplete__remove_RingerVibrateStateChange();
        int __incomplete__SetVolumeGroupGainForId();
        int __incomplete__GetVolumeGroupGainForId();
        int __incomplete__GetActiveVolumeGroupForEndpointId();
        int __incomplete__GetVolumeGroupsForEndpoint();
        int __incomplete__GetCurrentVolumeContext();
        int __incomplete__SetVolumeGroupMuteForId();
        int __incomplete__GetVolumeGroupMuteForId();
        int __incomplete__SetRingerVibrateState();
        int __incomplete__GetRingerVibrateState();
        int __incomplete__SetPreferredChatApplication();
        int __incomplete__ResetPreferredChatApplication();
        int __incomplete__GetPreferredChatApplication();
        int __incomplete__GetCurrentChatApplications();
        int __incomplete__add_ChatContextChanged();
        int __incomplete__remove_ChatContextChanged();
        [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, IntPtr deviceId);
        [PreserveSig] int GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, out IntPtr deviceId);
        [PreserveSig] int ClearAllPersistedApplicationDefaultEndpoints();
    }

    [Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioPolicyConfigFactoryWin10
    {
        // In IUnknown mode the CLR reserves only the 3 IUnknown slots, but the real
        // object is IInspectable — so declare its 3 methods here to keep the vtable aligned.
        int __inspectable__GetIids();
        int __inspectable__GetRuntimeClassName();
        int __inspectable__GetTrustLevel();
        int __incomplete__add_CtxVolumeChange();
        int __incomplete__remove_CtxVolumeChanged();
        int __incomplete__add_RingerVibrateStateChanged();
        int __incomplete__remove_RingerVibrateStateChange();
        int __incomplete__SetVolumeGroupGainForId();
        int __incomplete__GetVolumeGroupGainForId();
        int __incomplete__GetActiveVolumeGroupForEndpointId();
        int __incomplete__GetVolumeGroupsForEndpoint();
        int __incomplete__GetCurrentVolumeContext();
        int __incomplete__SetVolumeGroupMuteForId();
        int __incomplete__GetVolumeGroupMuteForId();
        int __incomplete__SetRingerVibrateState();
        int __incomplete__GetRingerVibrateState();
        int __incomplete__SetPreferredChatApplication();
        int __incomplete__ResetPreferredChatApplication();
        int __incomplete__GetPreferredChatApplication();
        int __incomplete__GetCurrentChatApplications();
        int __incomplete__add_ChatContextChanged();
        int __incomplete__remove_ChatContextChanged();
        [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, IntPtr deviceId);
        [PreserveSig] int GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, out IntPtr deviceId);
        [PreserveSig] int ClearAllPersistedApplicationDefaultEndpoints();
    }
}
