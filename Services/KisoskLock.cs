using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LabTrackAgent.Services;

/// <summary>
/// While enabled, blocks the shortcuts a student could use to leave the lock screen
/// (Alt+Tab, Alt+Esc, Alt+F4, Alt+Space, Windows key, Ctrl+Esc, Ctrl+Shift+Esc) and turns off
/// Task Manager, "Lock" and "Change password" for the current Windows user.
/// If the registry refuses the change, Task Manager is closed whenever it opens instead.
/// Disable() puts everything back.
/// </summary>
public sealed class KioskLock : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int VK_CONTROL = 0x11;
    private const int VK_TAB = 0x09;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_SPACE = 0x20;
    private const int VK_F4 = 0x73;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const uint LLKHF_ALTDOWN = 0x20;

    private const string PoliciesKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private static readonly string[] PolicyNames =
        { "DisableTaskMgr", "DisableLockWorkstation", "DisableChangePassword" };

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private readonly LowLevelKeyboardProc _proc; // kept in a field so it isn't garbage collected
    private IntPtr _hook = IntPtr.Zero;
    private Timer? _watchdog;

    public KioskLock() => _proc = HookCallback;

    public void Enable()
    {
        if (_hook != IntPtr.Zero) return;

        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);

        // If the registry won't let us turn Task Manager off, close it whenever it opens.
        if (!SetPolicies(true))
            _watchdog = new Timer(_ => CloseTaskManager(), null, 0, 300);
    }

    public void Disable()
    {
        _watchdog?.Dispose();
        _watchdog = null;

        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        SetPolicies(false);
    }

    public void Dispose() => Disable();

    /// <summary>Removes the policies. Safe to call any time, e.g. after a crash left them on.</summary>
    public static void ClearPolicies() => SetPolicies(false);

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var key = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var vk = (int)key.vkCode;
            var altDown = (key.flags & LLKHF_ALTDOWN) != 0;
            var ctrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

            var block =
                vk == VK_LWIN || vk == VK_RWIN                                                  // Windows key
                || (altDown && (vk == VK_TAB || vk == VK_ESCAPE || vk == VK_F4 || vk == VK_SPACE)) // Alt+Tab/Esc/F4/Space
                || (ctrlDown && vk == VK_ESCAPE);                                               // Ctrl+Esc, Ctrl+Shift+Esc

            if (block) return (IntPtr)1; // swallow the key
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>Turns the policies on or off. Returns false if the registry refused.</summary>
    private static bool SetPolicies(bool on)
    {
        try
        {
            if (on)
            {
                using var key = Registry.CurrentUser.CreateSubKey(PoliciesKey);
                foreach (var name in PolicyNames)
                    key.SetValue(name, 1, RegistryValueKind.DWord);
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(PoliciesKey, writable: true);
                if (key is null) return true; // nothing to clear
                foreach (var name in PolicyNames)
                    key.DeleteValue(name, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static void CloseTaskManager()
    {
        foreach (var process in Process.GetProcessesByName("Taskmgr"))
        {
            try { process.Kill(); }
            catch { /* e.g. it runs as administrator and can't be closed from here */ }
            finally { process.Dispose(); }
        }
    }
}