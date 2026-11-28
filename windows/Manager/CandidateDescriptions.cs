using System.Globalization;
using System.Text;

namespace Dimsum;

sealed class CandidateDescriptions(string data)
{
    static readonly string Bundled = Path.Combine(AppContext.BaseDirectory, "assets", "pronunciations");
    readonly HashSet<int> emoji = ReadEmoji();
    string? active;
    Dictionary<string, string[]> descriptions = [];

    public string Describe(string word, bool allGroups)
    {
        Refresh();
        return Format(word, allGroups);
    }

    public string? Lookup(string text)
    {
        Refresh();
        return text.EnumerateRunes().Any(rune => descriptions.ContainsKey(rune.ToString()))
            ? Format(text, !Rune.TryGetRuneAt(text, 0, out Rune rune) || rune.Utf16SequenceLength == text.Length)
            : null;
    }

    void Refresh()
    {
        try
        {
            string path = Workspace.ActivePronunciations(data) ?? Bundled;
            if (path != active)
            {
                try { descriptions = Read(path); }
                catch (Exception error) when (path != Bundled && error is IOException or UnauthorizedAccessException)
                {
                    descriptions = Read(Bundled);
                }
                active = path;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    string Format(string word, bool allGroups)
    {
        var parts = new List<string>();
        foreach (Rune rune in word.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) continue;
            string character = rune.ToString();
            parts.Add(descriptions.TryGetValue(character, out var groups) ?
                allGroups ? string.Join("，", groups) : groups[0] : character);
        }
        return parts.Count == 0 ? word : string.Join("，", parts);
    }

    Dictionary<string, string[]> Read(string path)
    {
        var result = new Dictionary<string, string[]>();
        foreach (string line in File.ReadLines(Path.Combine(path, NvdaDictionaries.Names[0])))
        {
            string[] fields = line.Split('\t');
            string key = fields[0];
            if (fields.Length < 2 || key.Length == 0 || !Rune.TryGetRuneAt(key, 0, out Rune rune) || rune.Utf16SequenceLength != key.Length ||
                !IsDescribedRune(rune) ||
                rune.Value > 127 && emoji.Contains(rune.Value))
                continue;

            string[] groups = fields.Skip(1).SelectMany(field => field.Split(' '))
                .Select(group => group.Trim()).Where(group => group is not ("" or "-")).ToArray();
            if (groups.Length > 0) result.TryAdd(key, groups);
        }
        foreach (string name in NvdaDictionaries.Names.Skip(1))
        {
            bool inSymbols = false;
            foreach (string line in File.ReadLines(Path.Combine(path, name)))
            {
                if (line is "symbols:" or "complexSymbols:") inSymbols = line == "symbols:";
                else if (inSymbols && !line.StartsWith('#'))
                {
                    string[] fields = line.Split('\t', 3);
                    if (fields.Length < 2) continue;
                    string key = fields[0];
                    if (key.Length == 2 && key[0] == '\\')
                        key = key[1] switch { '0' => "\0", 't' => "\t", 'n' => "\n", 'r' => "\r",
                            'f' => "\f", 'v' => "\v", _ => key[1..] };
                    string meaning = fields[1].Trim();
                    if (key.Length > 0 && Rune.TryGetRuneAt(key, 0, out Rune rune) && rune.Utf16SequenceLength == key.Length &&
                        meaning is not ("" or "-"))
                        result.TryAdd(key, [meaning]);
                }
            }
        }
        return result;
    }

    static bool IsDescribedRune(Rune rune) => Rune.IsLetter(rune) || Rune.IsNumber(rune) ||
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    static HashSet<int> ReadEmoji()
    {
        using var reader = new StreamReader(typeof(CandidateDescriptions).Assembly.GetManifestResourceStream("Dimsum.EmojiData")!);
        var points = new HashSet<int>();
        while (reader.ReadLine() is { } line)
        {
            string[] fields = line.Split('#', 2)[0].Split(';');
            if (fields.Length != 2 || fields[1].Trim() != "Emoji") continue;
            string[] range = fields[0].Trim().Split("..");
            for (int code = Convert.ToInt32(range[0], 16); code <= Convert.ToInt32(range[^1], 16); code++)
                points.Add(code);
        }
        return points;
    }
}
