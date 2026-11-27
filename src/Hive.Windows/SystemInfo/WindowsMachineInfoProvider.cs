using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Hive.Contracts;

namespace Hive.Windows.SystemInfo;

public sealed record MachineInfoSnapshot(
    string Hostname,
    Guid BootId,
    string OsVersion,
    int CurrentSessionId,
    string? CurrentUsername,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<ObservedSessionDto>? ObservedSessions = null);

/// <summary>
/// Safe, low-overhead machine metadata provider.
/// Never queries Win32_Product, process command lines, or window titles.
/// </summary>
public sealed class WindowsMachineInfoProvider
{
    private readonly Func<DateTimeOffset> _utcNowProvider;
    private readonly Func<long> _tickCount64Provider;

    public WindowsMachineInfoProvider(
        Func<DateTimeOffset>? utcNowProvider = null,
        Func<long>? tickCount64Provider = null)
    {
        _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
        _tickCount64Provider = tickCount64Provider ?? (() => Environment.TickCount64);
    }

    public MachineInfoSnapshot GetMachineInfo()
    {
        var nowUtc = _utcNowProvider();
        var uptimeMs = Math.Max(0, _tickCount64Provider());
        var hostname = Environment.MachineName;
        var bootId = ComputeDeterministicBootId(hostname, nowUtc, uptimeMs);
        var sessionId = Process.GetCurrentProcess().SessionId;

        string? username = null;
        try
        {
            username = WindowsIdentity.GetCurrent().Name;
        }
        catch
        {
            // Keep nullable if identity lookup is unavailable in constrained contexts
        }

        var observedSessions = EnumerateObservedSessions(sessionId, nowUtc);

        return new MachineInfoSnapshot(
            hostname,
            bootId,
            RuntimeInformation.OSDescription,
            sessionId,
            username,
            nowUtc,
            observedSessions);
    }

    public IReadOnlyList<ObservedSessionDto> GetObservedSessions()
    {
        var nowUtc = _utcNowProvider();
        var sessionId = Process.GetCurrentProcess().SessionId;
        return EnumerateObservedSessions(sessionId, nowUtc);
    }

    private static IReadOnlyList<ObservedSessionDto> EnumerateObservedSessions(int fallbackSessionId, DateTimeOffset nowUtc)
    {
        var sessions = new List<ObservedSessionDto>();
        var pSessionInfo = IntPtr.Zero;

        try
        {
            if (NativeMethods.WTSEnumerateSessions(IntPtr.Zero, 0, 1, out pSessionInfo, out var count) &&
                pSessionInfo != IntPtr.Zero &&
                count > 0)
            {
                var structSize = Marshal.SizeOf<NativeMethods.WTS_SESSION_INFO>();
                for (var i = 0; i < count; i++)
                {
                    var currentPtr = IntPtr.Add(pSessionInfo, i * structSize);
                    var info = Marshal.PtrToStructure<NativeMethods.WTS_SESSION_INFO>(currentPtr);
                    var stationName = info.pWinStationName != IntPtr.Zero
                        ? Marshal.PtrToStringUni(info.pWinStationName)
                        : null;

                    if (ShouldSkipSession(info.SessionId, stationName, info.State))
                    {
                        continue;
                    }

                    sessions.Add(new ObservedSessionDto(
                        info.SessionId,
                        UserSid: null,
                        MapSessionState(info.State),
                        nowUtc));
                }
            }
        }
        catch
        {
            // Safe fallback below if WTS API is unavailable in non-interactive or restricted environments.
        }
        finally
        {
            if (pSessionInfo != IntPtr.Zero)
            {
                NativeMethods.WTSFreeMemory(pSessionInfo);
            }
        }

        if (sessions.Count == 0)
        {
            sessions.Add(new ObservedSessionDto(fallbackSessionId, UserSid: null, "Active", nowUtc));
        }

        return sessions;
    }

    private static bool ShouldSkipSession(
        int sessionId,
        string? stationName,
        NativeMethods.WTS_CONNECTSTATE_CLASS state)
    {
        if (sessionId == 0 && string.Equals(stationName, "Services", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return state is NativeMethods.WTS_CONNECTSTATE_CLASS.WTSListen
            or NativeMethods.WTS_CONNECTSTATE_CLASS.WTSReset
            or NativeMethods.WTS_CONNECTSTATE_CLASS.WTSDown
            or NativeMethods.WTS_CONNECTSTATE_CLASS.WTSInit;
    }

    private static string MapSessionState(NativeMethods.WTS_CONNECTSTATE_CLASS state) => state switch
    {
        NativeMethods.WTS_CONNECTSTATE_CLASS.WTSActive => "Active",
        NativeMethods.WTS_CONNECTSTATE_CLASS.WTSConnected => "Connected",
        NativeMethods.WTS_CONNECTSTATE_CLASS.WTSConnectQuery => "ConnectQuery",
        NativeMethods.WTS_CONNECTSTATE_CLASS.WTSShadow => "Shadow",
        NativeMethods.WTS_CONNECTSTATE_CLASS.WTSDisconnected => "Disconnected",
        NativeMethods.WTS_CONNECTSTATE_CLASS.WTSIdle => "Idle",
        _ => "Unknown"
    };

    private static Guid ComputeDeterministicBootId(string hostname, DateTimeOffset nowUtc, long uptimeMs)
    {
        // Round boot timestamp to 10-second buckets so minor clock/tick jitter does not churn BootId within one boot.
        var rawBootTicks = nowUtc.UtcTicks - (uptimeMs * TimeSpan.TicksPerMillisecond);
        const long bucketTicks = 10 * TimeSpan.TicksPerSecond;
        var roundedBootTicks = (long)Math.Round(rawBootTicks / (double)bucketTicks, MidpointRounding.AwayFromZero) * bucketTicks;

        var seed = $"cordon-hive-boot:{hostname.ToUpperInvariant()}:{roundedBootTicks}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes);
    }

    private static class NativeMethods
    {
        internal enum WTS_CONNECTSTATE_CLASS
        {
            WTSActive = 0,
            WTSConnected = 1,
            WTSConnectQuery = 2,
            WTSShadow = 3,
            WTSDisconnected = 4,
            WTSIdle = 5,
            WTSListen = 6,
            WTSReset = 7,
            WTSDown = 8,
            WTSInit = 9
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WTS_SESSION_INFO
        {
            public int SessionId;
            public IntPtr pWinStationName;
            public WTS_CONNECTSTATE_CLASS State;
        }

        [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSEnumerateSessions(
            IntPtr hServer,
            int reserved,
            int version,
            out IntPtr ppSessionInfo,
            out int pCount);

        [DllImport("wtsapi32.dll")]
        internal static extern void WTSFreeMemory(IntPtr pMemory);
    }
}
