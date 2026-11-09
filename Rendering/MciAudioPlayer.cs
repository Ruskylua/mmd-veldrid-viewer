using System.Runtime.InteropServices;
using System.Text;

namespace SimplePmxVeldrid.Rendering;

internal sealed class MciAudioPlayer : IDisposable
{
    private readonly string _alias = "simplepmx_audio_" + Guid.NewGuid().ToString("N");
    private bool _isOpen;

    public string? SourcePath { get; private set; }

    public bool IsOpen => _isOpen;

    public void Open(string path)
    {
        Close();
        SourcePath = path;

        Send($"""open "{path}" alias {_alias}""");
        _isOpen = true;
        TrySend($"set {_alias} time format milliseconds");
    }

    public void PlayFrom(double seconds, double speed)
    {
        if (!_isOpen) return;

        Seek(seconds);
        SetSpeed(speed);
        Send($"play {_alias}");
    }

    public void Pause()
    {
        if (!_isOpen) return;
        TrySend($"pause {_alias}");
    }

    public void Stop()
    {
        if (!_isOpen) return;
        TrySend($"stop {_alias}");
        Seek(0);
    }

    public void Seek(double seconds)
    {
        if (!_isOpen) return;
        var milliseconds = Math.Max(0, (int)Math.Round(seconds * 1000.0));
        TrySend($"seek {_alias} to {milliseconds}");
    }

    public void SetSpeed(double speed)
    {
        if (!_isOpen) return;

        // MCI speed is 1000 == 1.0x. Some codecs/devices ignore it, so this is best-effort.
        var value = Math.Clamp((int)Math.Round(speed * 1000.0), 250, 2000);
        TrySend($"set {_alias} speed {value}");
    }

    public void Close()
    {
        if (!_isOpen) return;
        TrySend($"stop {_alias}");
        TrySend($"close {_alias}");
        _isOpen = false;
    }

    public void Dispose()
    {
        Close();
    }

    private static void Send(string command)
    {
        var error = mciSendString(command, null, 0, IntPtr.Zero);
        if (error == 0) return;

        var message = new StringBuilder(256);
        _ = mciGetErrorString(error, message, message.Capacity);
        throw new InvalidOperationException(message.Length == 0
            ? $"MCI audio error: {error}"
            : message.ToString());
    }

    private static void TrySend(string command)
    {
        _ = mciSendString(command, null, 0, IntPtr.Zero);
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? returnValue, int returnLength, IntPtr callback);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool mciGetErrorString(int errorCode, StringBuilder errorText, int errorTextSize);
}
