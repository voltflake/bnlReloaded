using BNLReloadedServer.Database;

namespace BNLReloadedServer.ControlPanel;

public static class ConsoleLogBuffer
{
    private const int MaxLines = 10000;
    private const long MaxFileBytes = 5 * 1024 * 1024;

    private static readonly string LogFilePath = Path.Combine(Databases.LogsFolderPath, "console.log");
    private static readonly object LockObj = new();
    private static readonly List<string> Lines = [];

    static ConsoleLogBuffer()
    {
        try
        {
            Directory.CreateDirectory(Databases.LogsFolderPath);
            if (File.Exists(LogFilePath))
            {
                var existing = File.ReadAllLines(LogFilePath);
                Lines.AddRange(existing.Length > MaxLines ? existing[^MaxLines..] : existing);
            }
        }
        catch
        {
            // ignore - persistence is best-effort
        }
    }

    public static void Append(string line)
    {
        lock (LockObj)
        {
            Lines.Add(line);
            if (Lines.Count > MaxLines)
                Lines.RemoveRange(0, Lines.Count - MaxLines);

            try
            {
                if (File.Exists(LogFilePath) && new FileInfo(LogFilePath).Length > MaxFileBytes)
                    File.WriteAllLines(LogFilePath, Lines);
                else
                    File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
            catch
            {
                // ignore - persistence is best-effort
            }
        }
    }

    public static List<string> GetAll()
    {
        lock (LockObj)
        {
            return [..Lines];
        }
    }
}
