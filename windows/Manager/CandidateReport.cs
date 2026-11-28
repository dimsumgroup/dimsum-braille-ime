using System.Globalization;
using System.Text;

namespace Dimsum;

sealed record CandidateReport(string Session, long Sequence, string Schema, string Event, int Selected,
    (string Label, string Word)[] Items)
{
    const int MaximumBytes = 65536;
    static readonly UTF8Encoding Utf8 = new(false, true);

    public static CandidateReport? Parse(byte[] bytes)
    {
        string[] fields = Utf8.GetString(bytes).Split('\0');
        if (fields.Length < 7 || fields.Length % 2 == 0 || fields[0] != "dimsum-win-1" ||
            !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out long sequence) ||
            fields[3] is not ("dimsum_cb" or "dimsum_cb_std" or "dimsum_full") ||
            fields[4] is not ("page" or "browse" or "tone" or "same" or "clear") ||
            !int.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out int selected))
            return null;

        var items = new (string Label, string Word)[(fields.Length - 7) / 2];
        for (int index = 0; index < items.Length; index++)
            items[index] = (fields[7 + index * 2], fields[8 + index * 2]);
        return new(fields[1], sequence, fields[3], fields[4], selected, items);
    }

    public static (DateTime Stamp, CandidateReport Report)? ReadLatest(string directory, string prefix, DateTime since)
    {
        (DateTime Stamp, CandidateReport Report)? latest = null;
        for (int slot = 1; slot <= 3; slot++)
        {
            string path = Path.Combine(directory, prefix + slot);
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                DateTime stamp = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
                if (stamp < since || stream.Length > MaximumBytes || stamp < latest?.Stamp) continue;
                var bytes = new byte[(int)stream.Length];
                stream.ReadExactly(bytes);
                if (Parse(bytes) is { } report &&
                    (latest is null || stamp != latest.Value.Stamp || report.Sequence > latest.Value.Report.Sequence))
                    latest = (stamp, report);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException) { }
        }
        return latest;
    }

    public string Format(bool showNumbers, Func<string, bool, string> describe)
    {
        if (Event == "clear" || Items.Length == 0) return "";
        bool browse = Event == "browse" && Selected > 0 && Selected <= Items.Length;
        string Render((string Label, string Word) item)
        {
            string word = describe(item.Word, browse);
            return showNumbers && Schema != "dimsum_full" ? item.Label + " " + word : word;
        }
        return browse ? Render(Items[Selected - 1]) : string.Join("，", Items.Select(Render));
    }
}
