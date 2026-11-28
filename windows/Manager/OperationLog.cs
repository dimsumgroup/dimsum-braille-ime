using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Dimsum;

sealed class OperationLog(Workspace workspace)
{
    public const string Title = "操作記錄及版本";
    string Pathname => Path.Combine(workspace.DataDirectory, "operations.log");
    static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public void Record(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        try
        {
            var previous = File.Exists(Pathname) ? File.ReadAllLines(Pathname).TakeLast(59) : [];
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {Redact(message)}";
            Workspace.WriteAtomic(Pathname, Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, previous.Append(line))));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public string Read()
    {
        try { return File.Exists(Pathname) ? File.ReadAllText(Pathname) : "暫時未有操作記錄。"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return "未能讀取操作記錄。"; }
    }

    public string Report(bool defaultInput)
    {
        var settings = workspace.Settings;
        string? deployer = Workspace.FindDeployer();
        var nvda = NvdaSession.Find()?.Directory ?? NvdaDictionaries.FindInstallation();
        return $"""
            {Title}
            管理器 {typeof(Workspace).Assembly.GetName().Version!.ToString(3)}
            Windows {RuntimeInformation.OSDescription} · {RuntimeInformation.OSArchitecture}
            Rime 引擎 {VersionOf(deployer)}
            NVDA {VersionOf(nvda is null ? null : Path.Combine(nvda, "nvda.exe"))}
            點心輸入法語言 {settings.InputLanguageTag ?? "未設定"}
            候選報讀方式 {settings.CandidateSpeechOutput?.ToString() ?? "自動偵測"}
            Windows 發音聲線 {settings.CandidateVoiceId ?? "預設"}
            聆聽候選時不報讀數字 {Enabled(settings.OmitCandidateNumbers)}
            只有一個候選時不播放聲音 {Enabled(!workspace.SoundEnabled)}
            輸入候選不讀配詞解釋 {Enabled(!settings.PronounceCandidates)}
            將點心設為目前預設輸入法 {Enabled(defaultInput)}
            單按 Shift 切換中英文 {Enabled(settings.ShiftSwitch)}
            在 NVDA 開啟點心配詞解釋 {Enabled(settings.UseDimsumReviewDescription)}
            在 JAWS 開啟點心配詞解釋 {Enabled(settings.UseJawsSpeechLookup)}
            在 Windows 朗讀程式（Narrator）開啟點心配詞解釋 {Enabled(settings.UseNarratorSpeechLookup)}
            查配詞快捷鍵 {(settings.LookupGesture.Length == 0 ? "未設定" : settings.LookupGesture)}
            {workspace.Status()}

            最近操作記錄
            {Read()}
            """;
    }

    public void Export(string destination, bool defaultInput) => File.WriteAllText(destination, Report(defaultInput), Utf8);

    internal static string Redact(string message)
    {
        message = Regex.Replace(message, """[A-Za-z]:[\\/][^\r\n「」"<>|]+|\\\\[^\r\n「」"<>|]+""", "[本機路徑]");
        return message.Replace("\r\n", " · ").Replace('\n', ' ').Replace('\r', ' ');
    }

    static string Enabled(bool value) => value ? "已勾選" : "未勾選";

    static string VersionOf(string? executable)
    {
        try { return executable is null ? "未偵測到" : FileVersionInfo.GetVersionInfo(executable).ProductVersion ?? "未知"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return "無法讀取"; }
    }
}
