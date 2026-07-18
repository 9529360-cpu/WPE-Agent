using System;
using System.IO;

namespace 币安量化机器人.Services;

public class RawStreamRecorder : IRawStreamRecorder
{
    private readonly string _baseDir;

    public RawStreamRecorder(string baseDir = null)
    {
        _baseDir = baseDir ?? Path.Combine(AppContext.BaseDirectory, "Logs", "RawStreams");
        Directory.CreateDirectory(_baseDir);
    }

    public void Record(string channel, string rawMessage)
    {
        try
        {
            var dir = Path.Combine(_baseDir, Sanitize(channel));
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, DateTime.UtcNow.ToString("yyyy-MM-dd_HH") + ".log");
            File.AppendAllText(file, DateTime.UtcNow.ToString("o") + " " + rawMessage + Environment.NewLine);
        }
        catch
        {
            // best-effort recording
        }
    }

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s;
    }
}
