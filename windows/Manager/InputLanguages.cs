using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Globalization;
using System.Text;
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
            "zh-HK" => "繁體中文（香港）",
            "zh-TW" => "中文（台灣）",
            _ => culture.DisplayName
        });
    }

    internal static InputLanguageChoice[] LanguageChoices(IEnumerable<UserInputLanguage> configured) => configured
        .Select(language => language.Tag)
        .Concat(["en-HK", "en-US", "zh-HK", "zh-TW"])
        .Select(LanguageChoice).OfType<InputLanguageChoice>().DistinctBy(choice => choice.Tag, IgnoreCase).ToArray();

    internal static bool IsDimsumEnabled(IEnumerable<UserInputLanguage> configured, InputLanguageChoice choice) =>
        configured.Any(language => SameLanguage(language.Tag, choice.Tag) && Tips(language).Contains(DimsumTip(choice.Id), IgnoreCase));

    internal static bool SameLanguage(string? left, string? right) => IgnoreCase.Equals(NormalizeLanguage(left), NormalizeLanguage(right));

    static string? NormalizeLanguage(string? tag) => tag?.Replace("-Hans-", "-", StringComparison.OrdinalIgnoreCase)
        .Replace("-Hant-", "-", StringComparison.OrdinalIgnoreCase);

    internal static bool IsDimsumProfileTip(string tip) => tip.Length >= 5 && ushort.TryParse(tip.AsSpan(0, 4), NumberStyles.HexNumber,
        CultureInfo.InvariantCulture, out ushort language) && IgnoreCase.Equals(tip, DimsumTip(language));

    static IEnumerable<string> Tips(UserInputLanguage language) => language.Tips ?? [];

    internal static string[] OtherDimsumTips(IEnumerable<UserInputLanguage> configured, InputLanguageChoice keep,
        IEnumerable<string> legacy, bool ownsTaiwan) => configured.SelectMany(Tips).Distinct(IgnoreCase)
        .Where(tip => !IgnoreCase.Equals(tip, DimsumTip(keep.Id)) && (IsDimsumProfileTip(tip) || legacy.Contains(tip, IgnoreCase) ||
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

    const string LanguageListStart = "DIMSUM-LANGUAGES-1";
    const string LanguageListEnd = "DIMSUM-END";

    internal static UserInputLanguage[] ParseUserInputLanguages(string output)
    {
        using var reader = new StringReader(output);
        while (reader.ReadLine() is { } line)
        {
            if (line.Trim().TrimStart('\uFEFF') != LanguageListStart) continue;
            var languages = new List<UserInputLanguage>();
            string? tag = null;
            var tips = new List<string>();
            while (reader.ReadLine() is { } record)
            {
                if (record == LanguageListEnd)
                {
                    if (tag is not null) languages.Add(new(tag, tips.ToArray()));
                    return languages.ToArray();
                }
                if (record.StartsWith("L:", StringComparison.Ordinal) && record.Length > 2)
                {
                    if (tag is not null) languages.Add(new(tag, tips.ToArray()));
                    tag = record[2..];
                    tips.Clear();
                }
                else if (tag is not null && record.StartsWith("T:", StringComparison.Ordinal) && record.Length > 2)
                    tips.Add(record[2..]);
                else
                    break;
            }
            break;
        }
        throw new IOException("未能辨認 Windows 輸入語言。PowerShell 傳回嘅語言清單格式不完整。");
    }

    public static async Task<UserInputLanguage[]> GetUserInputLanguagesAsync() => ParseUserInputLanguages(await ReadLanguageRecordsAsync());

    static Task<string> ReadLanguageRecordsAsync()
    {
        // Windows PowerShell can wrap collections in different JSON objects.
        // Emit each language and each key as a separate plain-text record.
        return RunLanguageQueryAsync($$"""
            $ErrorActionPreference = 'Stop';
            $languages = Get-WinUserLanguageList;
            [Console]::WriteLine('{{LanguageListStart}}');
            foreach ($language in $languages) {
                [Console]::WriteLine('L:' + [string]$language.LanguageTag);
                foreach ($tip in $language.InputMethodTips) {
                    [Console]::WriteLine('T:' + [string]$tip);
                }
            };
            [Console]::WriteLine('{{LanguageListEnd}}');
            """.ReplaceLineEndings(" "));
    }

    static async Task<string> RunLanguageQueryAsync(string command)
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
        start.ArgumentList.Add(command);
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
            return data;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill();
            throw new IOException("讀取 Windows 輸入語言逾時。請再試。");
        }
    }

    public static async Task<(string[] Verified, string? Notice)> EnableOnlyDimsumLanguageAsync(InputLanguageChoice choice, bool makeDefault)
    {
        var previous = await GetUserInputLanguagesAsync();
        if (!IsOurProfile(choice.Id))
            await RunAdminAsync("--finish-setup", choice.Id.ToString(CultureInfo.InvariantCulture));
        string[] verified = LanguageChoices(previous).Where(language => ProfileAvailable(previous, language))
            .Select(language => language.Tag).ToArray();
        var current = await EnsureLanguageEnabledAsync(previous, choice);
        if (!ProfileAvailable(current, choice))
            throw new IOException($"Windows 未能確認點心已加到「{choice.Label}」。請先喺 Windows 設定加入「{choice.Label}」，再按「繼續設定點心」。");

        var old = OtherDimsumTips(current, choice, UserProfiles(legacyOnly: true).ToArray(), BundledTaiwanTip());
        string? notice = null;
        if (old.Length > 0)
        {
            // Some Windows profiles stay in the language list after native removal.
            // Update the user's language list directly, with native removal as a fallback.
            try { await RemoveFromLanguageListAsync(old); }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { }
            current = await GetUserInputLanguagesAsync();
            var remaining = current.SelectMany(Tips).Intersect(old, IgnoreCase).ToArray();
            if (remaining.Length > 0)
            {
                RemoveUserProfiles(remaining);
                current = await GetUserInputLanguagesAsync();
            }
            current = await EnsureLanguageEnabledAsync(current, choice);
            if (!ProfileAvailable(current, choice))
                throw new IOException($"整理舊輸入法項目後，Windows 未能重新啟用「{choice.Label}」下嘅點心。請按「繼續設定點心」重試。");
            if (current.SelectMany(Tips).Intersect(old, IgnoreCase).Any())
                notice = "新版點心已啟用。Windows 仍然顯示舊輸入法項目，可以登出再登入後按 Windows 鍵加空格核對。";
        }
        if (makeDefault) await SetDimsumDefaultAsync(choice);
        return (verified, notice);
    }

    static bool ProfileAvailable(UserInputLanguage[] configured, InputLanguageChoice choice) =>
        IsDimsumEnabled(configured, choice) && EnabledInTsf(choice.Id);

    static async Task<UserInputLanguage[]> EnsureLanguageEnabledAsync(UserInputLanguage[] configured, InputLanguageChoice choice)
    {
        if (ProfileAvailable(configured, choice)) return configured;
        InstallLayoutOrTip(DimsumTip(choice.Id), 0);
        var current = await GetUserInputLanguagesAsync();
        if (!IsDimsumEnabled(current, choice))
            current = await AddToLanguageListAsync(choice);
        for (int attempt = 0; attempt < 2 && !ProfileAvailable(current, choice); attempt++)
        {
            await Task.Delay(300);
            current = await GetUserInputLanguagesAsync();
        }
        return current;
    }

    static Task<string> RemoveFromLanguageListAsync(string[] old)
    {
        string oldTips = string.Join(",", old.Select(tip => $"'{tip}'"));
        return RunLanguageQueryAsync($$"""
            $ErrorActionPreference = 'Stop';
            $list = Get-WinUserLanguageList;
            $old = @({{oldTips}});
            $changed = $false;
            foreach ($language in $list) {
                $removed = $false;
                foreach ($tip in @($language.InputMethodTips)) {
                    if ($tip -in $old) {
                        [void]$language.InputMethodTips.Remove($tip);
                        $removed = $true;
                    }
                };
                if ($removed) {
                    $changed = $true;
                    if ($language.InputMethodTips.Count -eq 0) {
                        $defaults = (New-WinUserLanguageList $language.LanguageTag)[0].InputMethodTips;
                        foreach ($tip in $defaults) {
                            if ($tip -notin $old) { $language.InputMethodTips.Add($tip) }
                        }
                    }
                }
            };
            if ($changed) { Set-WinUserLanguageList -LanguageList $list -Force };
            """.ReplaceLineEndings(" "));
    }

    static async Task<UserInputLanguage[]> AddToLanguageListAsync(InputLanguageChoice choice)
    {
        string match = choice.Tag.StartsWith("zh-", StringComparison.OrdinalIgnoreCase)
            ? choice.Tag[..3] + "*-" + choice.Tag[3..] : choice.Tag;
        await RunLanguageQueryAsync($$"""
            $ErrorActionPreference = 'Stop';
            $list = Get-WinUserLanguageList;
            $language = $list | Where-Object { $_.LanguageTag -eq '{{choice.Tag}}' -or $_.LanguageTag -like '{{match}}' } | Select-Object -First 1;
            if ($null -eq $language) {
                $language = (New-WinUserLanguageList '{{choice.Tag}}')[0];
                $language.InputMethodTips.Clear();
                $list.Add($language);
            };
            if (-not $language.InputMethodTips.Contains('{{DimsumTip(choice.Id)}}')) {
                $language.InputMethodTips.Add('{{DimsumTip(choice.Id)}}');
                Set-WinUserLanguageList -LanguageList $list -Force;
            };
            """.ReplaceLineEndings(" "));
        return await GetUserInputLanguagesAsync();
    }

    internal static bool EnabledInTsf(ushort language)
    {
        var type = Type.GetTypeFromCLSID(new Guid("33C53A50-F456-4884-B049-85FD643ECFED"), throwOnError: true)!;
        var profiles = (IInputProcessorProfiles)Activator.CreateInstance(type)!;
        try
        {
            return profiles.GetProfile(InputProcessorProfile, language, Service, Profile, 0, out var value) >= 0 && (value.Flags & 2) != 0;
        }
        finally { Marshal.FinalReleaseComObject(profiles); }
    }

    static bool ValidTip(string? tip) => !string.IsNullOrEmpty(tip) &&
        System.Text.RegularExpressions.Regex.IsMatch(tip, @"\A[0-9a-fA-F]{4}:(?:[0-9a-fA-F]{8}|(?:\{[0-9a-fA-F-]{36}\}){2})\z");

    public static async Task<string?> GetDefaultTipAsync()
    {
        string output = await RunLanguageQueryAsync("$ErrorActionPreference = 'Stop'; $tip = Get-WinDefaultInputMethodOverride; " +
            "if ($tip -is [string]) { $value = $tip } else { $value = $tip.InputMethodTip }; " +
            "[Console]::WriteLine('DIMSUM-DEFAULT:' + [string]$value);");
        string? match = output.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("DIMSUM-DEFAULT:", StringComparison.Ordinal));
        if (match is null) throw new IOException("未能讀取 Windows 預設輸入法。");
        string tip = match["DIMSUM-DEFAULT:".Length..];
        if (tip.Length == 0) return null;
        return ValidTip(tip) ? tip : throw new IOException("未能辨認 Windows 預設輸入法。");
    }

    public static async Task SetDimsumDefaultAsync(InputLanguageChoice language)
    {
        string tip = DimsumTip(language.Id);
        await SetDefaultTipAsync(tip);
        if (!IgnoreCase.Equals(tip, await GetDefaultTipAsync()))
            throw new IOException("Windows 未能確認點心已設為預設輸入法。");
    }

    static Task<string> SetDefaultTipAsync(string tip) =>
        RunLanguageQueryAsync($"$ErrorActionPreference = 'Stop'; Set-WinDefaultInputMethodOverride -InputTip '{tip}'");

    public static async Task RestoreDefaultInputAsync(string? previous)
    {
        var current = await GetUserInputLanguagesAsync();
        if (ValidTip(previous) && !IsDimsumProfileTip(previous!) && current.SelectMany(Tips).Contains(previous!, IgnoreCase))
        {
            await SetDefaultTipAsync(previous!);
            if (IgnoreCase.Equals(previous, await GetDefaultTipAsync())) return;
        }
        await RunLanguageQueryAsync("$ErrorActionPreference = 'Stop'; Set-WinDefaultInputMethodOverride");
        if (await GetDefaultTipAsync() is { } active && IsDimsumProfileTip(active))
            throw new IOException("Windows 未能解除點心嘅預設輸入法設定。");
    }
}
