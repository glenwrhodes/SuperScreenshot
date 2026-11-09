namespace SuperScreenshot;

/// <summary>The system-wide Win+Shift+X shortcut (next to Snipping Tool's Win+Shift+S).</summary>
internal sealed class GlobalHotkey : NativeWindow, IDisposable
{
    public const string Text = "Win+Shift+X";
    const int Id = 0x5353;
    const uint MOD_WIN = 0x8;

    readonly Action pressed;
    readonly bool registered;

    /// <param name="pressed">Called on the UI thread.</param>
    public GlobalHotkey(Action pressed)
    {
        this.pressed = pressed;
        CreateHandle(new CreateParams()); // a hidden window just to receive the hotkey
        registered = Native.RegisterHotKey(Handle, Id, MOD_WIN | Native.MOD_SHIFT | Native.MOD_NOREPEAT, (uint)Keys.X);
        if (!registered)
        {
            DestroyHandle();
            throw new InvalidOperationException("another app is already using it");
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && (int)m.WParam == Id)
        {
            pressed();
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            if (registered) Native.UnregisterHotKey(Handle, Id);
            DestroyHandle();
        }
    }
}
