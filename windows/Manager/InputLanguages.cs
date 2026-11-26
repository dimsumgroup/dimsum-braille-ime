using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Dimsum;

sealed record InputLanguageChoice(string Tag, ushort Id, string Label)
{
    public override string ToString() => Label;
}

sealed record UserInputLanguage(string Tag, string[] Tips);

static partial class WindowsSetup
{
    const ushort Taiwan = 0x0404;
    const ushort EnglishHongKong = 0x3c09;
    static string DimsumTip(ushort language) => $"{language:X4}:{Service:B}{Profile:B}";
    static string TaiwanEngineTip => $"{Taiwan:X4}:{Service:B}{EngineProfile:B}";
    static readonly StringComparer IgnoreCase = StringComparer.OrdinalIgnoreCase;

    internal static InputLanguageChoice? LanguageChoice(string tag)
    {
        CultureInfo culture;
        try { culture = CultureInfo.GetCultureInfo(NormalizeLanguage(tag)!); }
        catch (CultureNotFoundException) { return null; }
        if (culture.IsNeutralCulture || culture.TwoLetterISOLanguageName is not ("en" or "zh")) return null;
        int id = culture.Name switch
        {
            "en-HK" => EnglishHongKong,
            "zh-HK" => HongKong,
            "zh-TW" => Taiwan,
            _ => culture.LCID
        };
        if (id is <= 0 or >= ushort.MaxValue or 0x1000 or 0x0c00 or 0x1400) return null;
        return new(culture.Name, (ushort)id, culture.Name switch
        {
            "en-HK" => "英文（香港）",
            "zh-HK" => "中文（香港）",
            "zh-TW" => "中文（台灣）",
            _ => culture.DisplayName
        });
    }

    internal static InputLanguageChoice[] LanguageChoices(IEnumerable<UserInputLanguage> configured) => configured
        .Select(language => language.Tag)
        .Concat(["en-HK", "en-US", "zh-HK", "zh-TW"])
        .Select(LanguageChoice).OfType<InputLanguageChoice>().DistinctBy(choice => choice.Tag, IgnoreCase).ToArray();

    internal static string? ExistingDimsumLanguage(IEnumerable<UserInputLanguage> configured)
    {
        var active = configured.Where(language => Tips(language).Any(IsDimsumTip)).Select(language => language.Tag).ToArray();
        return active.Length == 1 ? active[0] : null;
    }

    internal static bool IsDimsumEnabled(IEnumerable<UserInputLanguage> configured, InputLanguageChoice choice) =>
        configured.Any(language => SameLanguage(language.Tag, choice.Tag) && Tips(language).Contains(DimsumTip(choice.Id), IgnoreCase));

    internal static bool SameLanguage(string? left, string? right) => IgnoreCase.Equals(NormalizeLanguage(left), NormalizeLanguage(right));

    static string? NormalizeLanguage(string? tag) => tag?.Replace("-Hans-", "-", StringComparison.OrdinalIgnoreCase)
        .Replace("-Hant-", "-", StringComparison.OrdinalIgnoreCase);

    static bool IsDimsumTip(string tip) => tip.Length >= 5 && ushort.TryParse(tip.AsSpan(0, 4), NumberStyles.HexNumber,
        CultureInfo.InvariantCulture, out ushort language) && IgnoreCase.Equals(tip, DimsumTip(language));

    static IEnumerable<string> Tips(UserInputLanguage language) => language.Tips ?? [];

    internal static string[] OtherDimsumTips(IEnumerable<UserInputLanguage> configured, InputLanguageChoice keep,
        IEnumerable<string> legacy, bool ownsTaiwan) => configured.SelectMany(Tips).Distinct(IgnoreCase)
        .Where(tip => !IgnoreCase.Equals(tip, DimsumTip(keep.Id)) && (IsDimsumTip(tip) || legacy.Contains(tip, IgnoreCase) ||
            (ownsTaiwan && IgnoreCase.Equals(tip, TaiwanEngineTip)))).ToArray();

    internal static bool OwnsTaiwanTip(int? explicitOwnership, int? oldOwner) => explicitOwnership is int known
        ? known == 1 : oldOwner == 1;

    static bool BundledTaiwanTip()
    {
        foreach (var entry in FindEntries(RegistryHive.LocalMachine).Where(entry => entry.IsEngine))
        {
            using var root = RegistryKey.OpenBaseKey(entry.Hive, entry.View);
            using var key = root.OpenSubKey($@"{UninstallKey}\{entry.Key}");
            if (key is not null && OwnsTaiwanTip(key.GetValue("DimsumOwnTaiwanTip") as int?,
                key.GetValue("DimsumOwnRuntimeData") as int?)) return true;
        }
        return false;
    }

    public static async Task<UserInputLanguage[]> GetUserInputLanguagesAsync()
    {
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell)
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("""
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
            ConvertTo-Json -Compress -InputObject @(Get-WinUserLanguageList | ForEach-Object {
                [PSCustomObject]@{Tag=$_.LanguageTag; Tips=@($_.InputMethodTips)}
            })
            """);
        using var process = Process.Start(start) ?? throw new IOException("未能讀取 Windows 輸入語言。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            var read = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            string data = await read;
            string error = await errors;
            if (process.ExitCode != 0) throw new IOException($"未能讀取 Windows 輸入語言。{error.Trim()}");
            return JsonSerializer.Deserialize<UserInputLanguage[]>(data) ?? [];
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill();
            throw new IOException("讀取 Windows 輸入語言逾時。請再試。");
        }
        catch (JsonException error) { throw new IOException("未能辨認 Windows 輸入語言。", error); }
    }

    public static async Task RemoveTemporaryTaiwanTipAsync()
    {
        if (!BundledTaiwanTip()) return;
        var current = await GetUserInputLanguagesAsync();
        if (!current.SelectMany(Tips).Contains(TaiwanEngineTip, IgnoreCase)) return;
        if (!InstallLayoutOrTip(TaiwanEngineTip, RemoveLayout) ||
            (await GetUserInputLanguagesAsync()).SelectMany(Tips).Contains(TaiwanEngineTip, IgnoreCase))
            throw new IOException("未能移除內附引擎加入嘅中文（台灣）Weasel。可以喺步驟 02 再試。");
    }

    public static async Task EnableOnlyDimsumLanguageAsync(InputLanguageChoice choice, bool makeDefault)
    {
        if (!IsOurProfile(choice.Id))
            await RunAdminAsync("--finish-setup", choice.Id.ToString(CultureInfo.InvariantCulture));
        var previous = await GetUserInputLanguagesAsync();
        if (!IsDimsumEnabled(previous, choice))
        {
            if (!EnableProfileForUser(choice.Id, makeDefault: false))
                throw new IOException($"Windows 未能將點心加到「{choice.Label}」。請揀另一個輸入語言再試。");
            var current = await GetUserInputLanguagesAsync();
            if (!IsDimsumEnabled(current, choice))
            {
                string tip = DimsumTip(choice.Id);
                if (!previous.SelectMany(Tips).Contains(tip, IgnoreCase)) InstallLayoutOrTip(tip, RemoveLayout);
                throw new IOException($"Windows 未能確認點心已加到「{choice.Label}」。請揀另一個輸入語言再試。");
            }
            previous = current;
        }
        if (makeDefault && !SetDefaultLayoutOrTip(DimsumTip(choice.Id), 0))
            throw new IOException("已加入點心。Windows 未能將點心設為預設輸入法。");
        var old = OtherDimsumTips(previous, choice, UserProfiles(legacyOnly: true).ToArray(), BundledTaiwanTip());
        if (old.Length > 0)
        {
            RemoveUserProfiles(old);
            var current = await GetUserInputLanguagesAsync();
            if (!IsDimsumEnabled(current, choice) || current.SelectMany(Tips).Intersect(old, IgnoreCase).Any())
                throw new IOException("已加入點心，但 Windows 仍然顯示其他點心輸入法項目。請再試步驟 02。");
        }
    }
}
