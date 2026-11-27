namespace Dimsum;

static class SetupCoordinator
{
    public static async Task<string> RunAsync(Workspace workspace, InputLanguageChoice language, bool repair,
        Action<string> progress, IWin32Window? owner = null, bool confirmed = false)
    {
        workspace.MarkSetupPending(language);
        try
        {
            bool makeDefault = false;
            try
            {
                var current = await WindowsSetup.GetDefaultTipAsync();
                makeDefault = current is not null && WindowsSetup.IsDimsumProfileTip(current);
            }
            catch (IOException) { }
            var result = await new LegacyUpgrade(workspace).RunAsync(owner, language, progress, makeDefault: makeDefault, confirmed: confirmed);
            if (result is not null) return result;

            if (Workspace.FindDeployer() is null)
            {
                progress("正在安裝輸入引擎。");
                await WindowsSetup.InstallEngineAsync();
            }
            return await workspace.CompleteSetupAsync(language, makeDefault: makeDefault, progress: progress, forceDeploy: repair);
        }
        catch
        {
            workspace.MarkSetupPending(language);
            throw;
        }
    }
}
