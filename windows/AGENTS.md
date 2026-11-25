# Windows 開發交接

試用、建置、簽署同實機驗收步驟見 [README.md](README.md)。目前管理器、安裝程式同 NVDA 附加元件嘅版本都係 1.3.1。

| 功能 | 檔案 |
| --- | --- |
| 管理器介面、捷徑錄製 | `Manager/ManagerForm.cs`、`Manager/ShortcutCapture.cs` |
| 字庫下載、更新提示、部署及回復 | `Manager/Workspace.cs` |
| Windows 輸入語言、移除及舊版遷移 | `Manager/WindowsSetup.cs`、`Manager/EngineRemoval.cs`、`Manager/LegacyUpgrade.cs`、`Manager/MachineLegacyUpgrade.cs` |
| NVDA 安裝、版本及配詞 | `Manager/NvdaAddon.cs`、`Manager/NvdaSession.cs`、`Manager/NvdaDictionaries.cs`、`reporting/` |
| Rime 候選及單候選音效 | `reporting/rime/lua/dimsum_windows_reporter.lua`、`Manager/SingletonSound.cs`、`audio/only.wav` |
| 安裝程式及精簡引擎 | `installer/dimsum.iss`、`installer/engine.nsi`、`prepare-assets.py`、`build.ps1` |

- 程式更新檢查正式 GitHub Release 嘅 `v主版本.次版本.修訂版` 標籤及同版本嘅 `Dimsum_Setup_v版本.exe` 發行附件。資料更新比對 `main` 上四個檔案各自嘅 Git blob ID。查詢在管理器介面進行，字庫同配詞各自顯示結果，由用家按鍵更新。成功部署前保留上一份可用資料。
- `settings.json` 同下載資料放喺 `%LOCALAPPDATA%\DimsumInput`。Rime 用家目錄預設 `%APPDATA%\Rime`，亦會讀已設定嘅 `RimeUserDir`。維持用家自訂詞庫、學習、NVDA 語言同已清空嘅快捷鍵。
- NVDA 用家配詞放喺 NVDA 用家目錄嘅 `DimsumPronunciations`。三個開關可獨立控制單字、標點及表情符號。保留 NVDA 一般候選報讀，補讀模式只補聲調引起嘅報讀缺口。
- 精簡引擎使用固定版本 Weasel 0.17.4.0 原廠二進位檔。`prepare-assets.py` 驗證原始安裝檔 SHA-256；`installer/engine.nsi` 定義重封裝。完整來源位置見 `engine-notices.txt`。
- 調整介面要驗證 NVDA 鍵盤操作、可聚焦欄位同讀出嚟嘅名稱。步驟 01、02 係首次設定，日常功能使用普通按鈕。查配詞快捷鍵嘅編輯、錄製同清除共用 `LookupGesture`，按「儲存報讀設定」先寫入。
- `tests/reporting.py ..` 由呢個目錄用 Python 執行，`tests/reporting.lua` 用 Lua 執行。Windows 實機重點包括更新提示、快捷鍵錄製同清除、換語言、遷移、單候選提示，以及 NVDA 開關各模式嘅行為。
