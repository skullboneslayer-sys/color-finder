using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace ColorFinder;

internal sealed class SavedColorStore(string path)
{
    public IReadOnlyList<ColorValue> Load() => WithLock<IReadOnlyList<ColorValue>>(() => ReadColors(ReadLines()));

    // Periodic UI refresh must never wait for another instance.
    public bool TryLoad(out IReadOnlyList<ColorValue> colors)
    {
        colors = [];
        using var mutex = new Mutex(false, @"Local\ColorFinder.SavedColors");
        try
        {
            if (!mutex.WaitOne(0)) return false;
        }
        catch (AbandonedMutexException)
        {
            // The abandoned lock is now owned by this thread.
        }
        try
        {
            colors = ReadColors(ReadLines());
            return true;
        }
        finally { mutex.ReleaseMutex(); }
    }

    private string[] ReadLines() => ReadLines(path);

    private static string[] ReadLines(string file)
    {
        try { return File.ReadAllLines(file); }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }

    private static List<ColorValue> ReadColors(string[] lines)
    {
        var colors = new List<ColorValue>();
        var seen = new HashSet<ColorValue>();
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (line.Length == 7 && line[0] == '#' &&
                ColorValue.TryParse(line, out var color) && seen.Add(color))
                colors.Add(color);
        }
        return colors;
    }

    public IReadOnlyList<ColorValue> Save(ColorValue color) => WithLock<IReadOnlyList<ColorValue>>(() =>
    {
        var colors = ReadColors(ReadLines());
        if (colors.Contains(color)) return colors;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Start a new line so an interrupted write cannot damage earlier picks.
        File.AppendAllText(path, Environment.NewLine + color.Hex);
        colors.Insert(0, color);
        return colors;
    });

    public IReadOnlyList<ColorValue> Remove(ColorValue color) => WithLock<IReadOnlyList<ColorValue>>(() =>
    {
        var lines = ReadLines();
        var hex = color.Hex;
        var remaining = Array.FindAll(lines, line => !string.Equals(line, hex, StringComparison.OrdinalIgnoreCase));
        if (remaining.Length != lines.Length) ReplaceLines(remaining);
        return ReadColors(remaining);
    });

    public IReadOnlyList<ColorValue> Move(ColorValue color, ColorValue destination, out bool moved)
    {
        var applied = false;
        var snapshot = WithLock<IReadOnlyList<ColorValue>>(() =>
        {
            var colors = ReadColors(ReadLines());
            var from = colors.IndexOf(color);
            var to = colors.IndexOf(destination);
            if (from < 0 || to < 0) return colors;
            if (from != to)
            {
                colors.RemoveAt(from);
                colors.Insert(to, color);
                // Load reads newest-first, so store the display order in reverse.
                var stored = new List<ColorValue>(colors);
                stored.Reverse();
                ReplaceLines(stored.ConvertAll(item => item.Hex));
            }
            applied = true;
            return colors;
        });
        moved = applied;
        return snapshot;
    }

    private void ReplaceLines(IEnumerable<string> lines) => WriteLines(path, lines);

    private static void WriteLines(string destination, IEnumerable<string> lines)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temporary, lines);
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        }
        finally { DeleteTemporary(temporary); }
    }

    private static void DeleteTemporary(string temporary)
    {
        try { File.Delete(temporary); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private T WithLock<T>(Func<T> access)
    {
        using var mutex = new Mutex(false, @"Local\ColorFinder.SavedColors");
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(1)))
                throw new IOException("Saved colors are busy. Try again.");
        }
        catch (AbandonedMutexException)
        {
            // Windows has granted the lock. A partial final line is ignored on load.
        }
        try
        {
            return access();
        }
        finally { mutex.ReleaseMutex(); }
    }
}
