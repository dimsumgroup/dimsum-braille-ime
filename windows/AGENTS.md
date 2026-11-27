# Windows 開發交接

試用、建置、簽署同實機驗收步驟見 [README.md](README.md)。目前管理器、安裝程式同 NVDA 附加元件嘅版本都係 1.3.4。管理器嘅「使用說明」會開啟安裝目錄內附嘅 `安裝及使用說明_Win.txt`，建置時保留原檔內容同換行。

| 功能 | 檔案 |
| --- | --- |
| 管理器介面、捷徑錄製 | `Manager/ManagerForm.cs`、`Manager/ShortcutCapture.cs` |
| 字庫下載、更新提示、處理及回復 | `Manager/Workspace.cs` |
| Windows 輸入語言、移除及舊版遷移 | `Manager/WindowsSetup.cs`、`Manager/EngineRemoval.cs`、`Manager/LegacyUpgrade.cs`、`Manager/MachineLegacyUpgrade.cs` |
| NVDA 安裝、版本及配詞 | `Manager/NvdaAddon.cs`、`Manager/NvdaSession.cs`、`Manager/NvdaDictionaries.cs`、`reporting/` |
| Rime 候選及單候選音效 | `reporting/rime/lua/dimsum_windows_reporter.lua`、`Manager/SingletonSound.cs`、`audio/only.wav` |
| 安裝程式及精簡引擎 | `installer/dimsum.iss`、`installer/engine.nsi`、`prepare-assets.py`、`build.ps1` |

- 程式更新檢查正式 GitHub Release 嘅 v主版本.次版本.修訂版 標籤及同版本 Dimsum_Setup_v版本.exe 附件。資料更新比對主分支四個檔案嘅 Git blob ID。每次開管理器會一併檢查程式同兩組資料。有更新時集中喺一個視窗提示，兩組資料可以各自處理同回復。
- settings.json 同下載資料放喺 %LOCALAPPDATA%\DimsumInput。Rime 用家目錄預設 %APPDATA%\Rime，維護 RimeUserDir、自訂詞庫、學習資料、NVDA 語言同已清空嘅快捷鍵。
- Rime 候選自動報讀每個字嘅第一組配詞。逐個瀏覽讀高亮候選每個字嘅全部配詞。Page Up 同 Page Down 完成後從 Rime 取新一頁候選，NVDA 由計時器統一發出候選報讀。獨立勾選項控制候選編號、配詞同提示音。附加組件相容舊設定檔嘅 ReportingMode: compact。
- UseDimsumReviewDescription 預設關閉，只控制 NVDA 檢閱當前字元指令第二下。LookupGesture 獨立查最近報讀，清空時解除綁定。可由管理器另存三份配詞及備份 NVDA 現用字典。
- 舊版遷移順序係停用已核對嘅自動化、更新內附引擎、處理字庫、核對 Windows 設定、清理舊檔案同登記。保留 Rime 用家資料原位，只寫重試所需嘅檢查點。單一移除流程另會移走 NVDA 附加組件。
- 精簡引擎使用固定版本 Weasel 0.17.4.0 原廠二進位檔。prepare-assets.py 驗證 SHA-256，installer/engine.nsi 定義重封裝。來源見 engine-notices.txt。
- 安裝器經 SetupCoordinator 喺完成前加入繁體中文（香港）。互動安裝會向正在使用 NVDA 而未有點心附加組件，或者仍用緊舊版附加組件嘅用家作邀請。完成頁預設唔開管理器，可勾選「開啟 Dimsum Manager」。設定未完成時，管理器提供「繼續設定點心」。完成後可以「輸入法設定修復」同選擇已驗證嘅語言。
- Manager/InputLanguages.cs 先驗證選定語言喺 Get-WinUserLanguageList 同 TSF 已啟用，先移除其他點心項目。台灣 Weasel 須核對內附引擎嘅安裝標記。更完整實機測試清單見 Windows README。
