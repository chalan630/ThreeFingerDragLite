using System.Globalization;
using System.Text.RegularExpressions;
using ThreeFingerDragLite.Touchpad;

namespace ReplayTests;

/// <summary>Parses "HH:mm:ss.fff  contacts: ID:1 123,456, ID:2 ..." lines (VerboseLog output) into timed frames.</summary>
internal static class LogReplay {
    private static readonly Regex Line = new(@"^(\d{2}):(\d{2}):(\d{2})\.(\d{3})\s+contacts:\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex Contact = new(@"ID:(-?\d+)\s+(-?\d+),(-?\d+)", RegexOptions.Compiled);

    public static List<(long ms, TouchpadContact[] contacts)> Parse(IEnumerable<string> lines) {
        var frames = new List<(long, TouchpadContact[])>();
        foreach (string text in lines) {
            Match m = Line.Match(text.Trim());
            if (!m.Success) continue;
            long ms = ((long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60
                        + long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)) * 60
                       + long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)) * 1000
                      + long.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
            var contacts = Contact.Matches(m.Groups[5].Value)
                .Select(c => new TouchpadContact(
                    int.Parse(c.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(c.Groups[2].Value, CultureInfo.InvariantCulture),
                    int.Parse(c.Groups[3].Value, CultureInfo.InvariantCulture)))
                .ToArray();
            frames.Add((ms, contacts));
        }
        return frames;
    }
}
