using System.Text;

namespace BNLReloadedServer.ControlPanel;

public sealed class BroadcastingTextWriter(TextWriter inner) : TextWriter
{
    public override Encoding Encoding => inner.Encoding;

    public override void Write(char value)
    {
        inner.Write(value);
    }

    public override void Write(string? value)
    {
        inner.Write(value);
        if (!string.IsNullOrEmpty(value))
            ConsoleLogBuffer.Append(value);
    }

    public override void WriteLine(string? value)
    {
        inner.WriteLine(value);
        ConsoleLogBuffer.Append(value ?? string.Empty);
    }
}
