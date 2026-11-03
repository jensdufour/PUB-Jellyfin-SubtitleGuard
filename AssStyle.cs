using System.Globalization;
using System.Text.RegularExpressions;

namespace SubtitleGuard;

internal static partial class AssStyle
{
    private const double TextHeightFraction = 0.05;
    private const string StyleFormat = "Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding";

    // Jellyfin's own extractor rewrites Arial to this font, so the standard style matches its other ASS output.
    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex OverrideBlock();

    [GeneratedRegex(@"\{[^}]*\\p[1-9][^}]*\}")]
    private static partial Regex Drawing();

    // Returns null when the file is not a plain V4+ script so callers keep the original bytes.
    internal static IReadOnlyList<string>? Normalize(IEnumerable<string> input)
    {
        var lines = input.ToList();
        if (!lines.Any(line => line.Trim() == "[V4+ Styles]")) return null;
        var eventsFormat = lines.FirstOrDefault(line => line.StartsWith("Format:", StringComparison.Ordinal) && line.Contains("Start", StringComparison.Ordinal));
        if (eventsFormat is null || Regex.Replace(eventsFormat, @"\s+", string.Empty) != "Format:Layer,Start,End,Style,Name,MarginL,MarginR,MarginV,Effect,Text") return null;

        var resX = Resolution(lines, "PlayResX:") ?? 384;
        var resY = Resolution(lines, "PlayResY:") ?? 288;
        var size = Math.Max(1, (int)Math.Round(resY * TextHeightFraction));
        var outline = Math.Max(1, size / 18);
        var marginH = (int)Math.Round(resX * 0.02);
        var marginV = (int)Math.Round(resY * 0.04);

        var result = new List<string> { "[Script Info]" };
        result.AddRange(lines.TakeWhile(line => !line.StartsWith('[') || line.Trim() == "[Script Info]").Where(line =>
            line.Length > 0 && line.Trim() != "[Script Info]" && !line.StartsWith("PlayRes", StringComparison.Ordinal)
            && !line.StartsWith("ScriptType:", StringComparison.Ordinal) && !line.StartsWith("WrapStyle:", StringComparison.Ordinal)
            && !line.StartsWith("ScaledBorderAndShadow:", StringComparison.Ordinal)));
        result.Add("ScriptType: v4.00+");
        result.Add("WrapStyle: 0");
        result.Add("ScaledBorderAndShadow: yes");
        result.Add("PlayResX: " + resX.ToString(CultureInfo.InvariantCulture));
        result.Add("PlayResY: " + resY.ToString(CultureInfo.InvariantCulture));
        result.Add(string.Empty);
        result.Add("[V4+ Styles]");
        result.Add("Format: " + StyleFormat);
        result.Add(string.Create(CultureInfo.InvariantCulture,
            $"Style: Default,Arial Unicode MS,{size},&H00FFFFFF,&H00FFFFFF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,{outline},1,2,{marginH},{marginH},{marginV},1"));
        result.Add(string.Empty);
        result.Add("[Events]");
        result.Add("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        foreach (var line in lines.Where(line => line.StartsWith("Dialogue:", StringComparison.Ordinal)))
        {
            var fields = line[9..].TrimStart().Split(',', 10);
            if (fields.Length != 10) return null;
            if (Drawing().IsMatch(fields[9])) continue;
            var text = OverrideBlock().Replace(fields[9], string.Empty).Replace("\\h", " ", StringComparison.Ordinal).Trim();
            if (text.Length == 0) continue;
            result.Add($"Dialogue: 0,{fields[1]},{fields[2]},Default,,0,0,0,,{text}");
        }

        return result;
    }

    private static int? Resolution(IEnumerable<string> lines, string key)
    {
        var line = lines.FirstOrDefault(value => value.StartsWith(key, StringComparison.Ordinal));
        return line is not null && int.TryParse(line[key.Length..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value : null;
    }
}
