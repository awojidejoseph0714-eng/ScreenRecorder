using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ScreenRecorder
{
    public class HotkeyManager : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WM_HOTKEY = 0x0312;
        private HwndSource? _hwndSource;
        private readonly IntPtr _hwnd;
        private readonly Dictionary<int, Action> _hotkeyActions = new Dictionary<int, Action>();

        public HotkeyManager(Window window)
        {
            var helper = new WindowInteropHelper(window);
            _hwnd = helper.EnsureHandle(); // Ensure window handle exists
            
            _hwndSource = HwndSource.FromHwnd(_hwnd);
            _hwndSource?.AddHook(HwndHook);
        }

        public bool Register(int id, uint modifiers, uint key, Action action)
        {
            // Unregister first if already registered
            Unregister(id);

            bool success = RegisterHotKey(_hwnd, id, modifiers, key);
            if (success)
            {
                _hotkeyActions[id] = action;
            }
            return success;
        }

        public void Unregister(int id)
        {
            if (_hotkeyActions.ContainsKey(id))
            {
                UnregisterHotKey(_hwnd, id);
                _hotkeyActions.Remove(id);
            }
        }

        public void UnregisterAll()
        {
            var keys = new List<int>(_hotkeyActions.Keys);
            foreach (int id in keys)
            {
                Unregister(id);
            }
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (_hotkeyActions.TryGetValue(id, out var action))
                {
                    action.Invoke();
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            UnregisterAll();
            _hwndSource?.RemoveHook(HwndHook);
            _hwndSource = null;
        }

        // Helper to convert WPF Key to Win32 Virtual Key
        public static uint GetVirtualKey(Key key)
        {
            return (uint)KeyInterop.VirtualKeyFromKey(key);
        }

        // Helper to format hotkey display text
        public static string GetHotkeyText(uint modifiers, uint key)
        {
            var parts = new List<string>();
            if ((modifiers & 0x0002) != 0) parts.Add("Ctrl");
            if ((modifiers & 0x0001) != 0) parts.Add("Alt");
            if ((modifiers & 0x0004) != 0) parts.Add("Shift");
            if ((modifiers & 0x0008) != 0) parts.Add("Win");

            Key wpfKey = KeyInterop.KeyFromVirtualKey((int)key);
            parts.Add(wpfKey.ToString());

            return string.Join(" + ", parts);
        }
    }
}
