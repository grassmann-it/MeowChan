namespace MeowChan;

using System;
using System.IO;

public static class Utils
{
    public static string ToHumanReadableSize(long bytes)
    {
        return MeowChan.Desktop.SizeText.Format(bytes);
    }
}
public static class PathSanitizer
{
    public static string SanitizePath(string path)
    {
        // Remove invalid characters
        char[] invalidChars = Path.GetInvalidPathChars();
        foreach (char c in invalidChars)
        {
            path = path.Replace(c.ToString(), "");
        }

        return path;
    }
}

