using System;
using System.Collections.Generic;
using System.Windows.Interop;
using RetroCap.Interop;

namespace RetroCap.Services
{
    /// <summary>System-wide hotkeys via RegisterHotKey on a message-only window.</summary>
    public sealed class HotkeyService : IDisposable
    {
        private readonly HwndSource _source;
        private readonly HashSet<int> _registered = new();

        /// <summary>Raised with the id passed to <see cref="Register"/>.</summary>
        public event Action<int>? Pressed;

        public HotkeyService()
        {
            var p = new HwndSourceParameters("RetroCapHotkeySink") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ };
            _source = new HwndSource(p);
            _source.AddHook(WndProc);
        }

        /// <summary>Registers e.g. Ctrl+Alt+A under <paramref name="id"/>. Returns false if another program already owns it.</summary>
        public bool Register(int id, uint modifiers, uint virtualKey)
        {
            Unregister(id);
            if (!Native.RegisterHotKey(_source.Handle, id, modifiers | Native.MOD_NOREPEAT, virtualKey)) return false;
            _registered.Add(id);
            return true;
        }

        public void Unregister(int id)
        {
            if (_registered.Remove(id)) Native.UnregisterHotKey(_source.Handle, id);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && _registered.Contains(wParam.ToInt32()))
            {
                handled = true;
                Pressed?.Invoke(wParam.ToInt32());
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            foreach (var id in _registered) Native.UnregisterHotKey(_source.Handle, id);
            _registered.Clear();
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
    }
}
