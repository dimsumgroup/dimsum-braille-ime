# 點心粵語點字輸入法 macOS 接手說明

接手時先讀本文和 [AGENTS.md](AGENTS.md)，用 `git status` 查看工作目錄，
再依 [BUILD.md](BUILD.md) 在 Mac 編譯及試打。

## 現有功能

| 範圍 | 目前行為 |
| --- | --- |
| 管理器 | 01 安裝 Squirrel、02 安裝／更新點心方案、03 啟動報讀助手。02 已部署候選報讀，維護區可獨立重新部署 |
| 版本與更新 | 程式 V1.2.1，發行團隊 Dimsum Group。啟動時獨立檢查正式 Mac 發行版、已部署 Rime 字庫及 NVDA 發音資料 |
| 報讀設定 | 新用家預設「標準」並開啟候選配詞報讀，既有偏好值保留。精簡讀候選、標準在候選前讀數字 |
| 查詢最近報讀的配詞 | 可在讀屏軟件指派讀屏鍵＋撇號。點心的獨立快捷鍵預設為 Control＋Option＋Command＋撇號，已儲存的設定保留。單字讀全部配詞，詞句逐字讀第一組 |
| 配詞 | 自動報讀時讀每字第一組，保留同組以逗號分隔的稱呼。以 G/H 或方向鍵瀏覽時讀目前候選的全部配詞 |
| 單一候選 | 播放隨 App 打包的 `sounds/only.wav`，並同時朗讀候選 |
| 分段選字 | 監聽 Rime 候選報告。用數字鍵選完前段後，接著報讀下一段 |
| 方案選單 | Control＋反引號可觸發選單報讀，上下鍵可聽目前選中的方案 |

## 資料流及部署

- `DimsumUpdates.swift` 查詢 GitHub Releases、主字庫及三份 NVDA 字典的內容雜湊。
  用家可在管理器重新檢查，或查看目前已部署字庫的版本、日期及雜湊。
- 「更新發音」執行 `update_accessibility.sh --pull`，從同一個上游 Git commit 下載
  `characterDescriptions.dic`、`symbols.dic` 和 `cldr.dic`。轉換成功後寫入新發音表以及
  `voiceover_output/nvda-source-hashes.json`，記錄來源字典及 JSON 發音表的 Git 雜湊。
  即使 Rime 更新已同步 NVDA 快取，提示仍會維持到發音表轉換完成。
  舊發音表如與內附版本相同，可沿用內附來源紀錄。其他舊發音表可由用家按「更新發音」建立紀錄。

- `build-helper.sh` 編譯助手和共用設定，將音效放進助手 App，並可執行自檢。
  `build-manager.sh` 再編譯管理器和 `dimsum_tool`，把工具及助手放在 `Contents/Helpers`。
  安裝腳本、Lua 和字表在 `Contents/Resources`。
- `install.sh`、`update.sh` 和管理器的獨立部署入口都共用
  `deploy-candidate-reporting.sh`。它會備份、安裝 Lua 回報元件、編譯 Rime 方案和重新載入 Squirrel。
- Lua 把當前候選頁寫到 `~/Library/Rime/dimsum-reporting/candidates`。新版報告提供選字段落位置、
  候選數、事件及版本資訊。助手監聽這個目錄，報讀新候選頁，即使顯示中的整串組字沒有改變。
  這是「不過你」選定「不過」之後能接讀「你」的關鍵。
- 助手與管理器的共用偏好位置是 `~/Library/Application Support/DimsumVoiceOver/`。
  更新的發音表也放在用家資料目錄，可在重新啟動助手後使用。
- 「準備讀屏查配詞指令」把兩個已簽署的背景 App 安裝及登記到 `~/Applications/點心查配詞`。
  用家在讀屏軟件用原有的「打開應用程式」功能指派 App。App 由 `ScreenReaderLookupAction.swift`
  建立，送給助手的通知只帶指定的讀屏軟件名稱。
- `LastSpokenLookup.swift` 在收到查詢時才取得報讀文字。VoiceOver 用 AppleScript 的
  `content of last phrase`。TurnOver 用「複製最近報讀」指令並還原剪貼簿。
  助手使用自身粵語語音讀結果，保留讀屏軟件的原報讀。
- `build-dmg.sh` 支援已編譯的管理器 App 或發行 ZIP。產生的 DMG 內含 App 及
  `/Applications` 捷徑和用家版 `安裝及使用說明.txt`，Finder 排列由 `dmg-layout.dsstore` 提供。

## 已確認及接手時要試的項目

- 使用者回報修正分段報讀和提示音的版本在 Mac 上已可用，也已成功編譯和打包管理器。
- 在 Linux 工作區已檢查 Swift 語法與報讀邏輯、以 Rime 測試分段選字，以及檢查部署與發行的目錄配置。
- DMG 打包腳本的 App、ZIP 兩個輸入流程以及捷徑目標和 Finder 排列資料已檢查。
  實際掛載並核對 Finder 顯示仍需在 Mac 上確認。
- 啟動更新提示與安裝按鈕的 VoiceOver 次序需要在 macOS 試用。實際上游的 `v1.1.2` 只附 Windows 安裝檔，
  可確認它不會被顯示為 Mac 程式更新。另以有 Mac 安裝檔的測試 Release 驗證提示流程。
- 在 Mac 測試發音更新時，確認「檢查更新」列出有差異的 NVDA 檔名。按「更新發音」後再檢查，
  確認提示消失，並重新啟動報讀助手試聽。
- 在 Mac 分別用 VoiceOver 和 TurnOver 試查「過」、詞語「日本」及完整句子。
  確認兩個背景指令安裝及升級後均可在讀屏軟件選用，觸發時編輯器仍在前景。
  測試 Caps Lock 和 Control＋Option、首次授權、指定讀屏來源、還原剪貼簿，以及自訂和停用快捷鍵。
- 下次發行時，在一台 Mac 上驗證 `build-helper.sh --self-test`、`build-manager.sh`、
  `build-dmg.sh`，並從 DMG 拖曳安裝。再以全新設定走 01→02→03，以既有安裝走獨立的候選報讀部署。

## 已遇到的編譯問題

Mac 上曾遇過 SwiftUI 的 `StateMacro` 外掛找不到。共用設定畫面及管理器改用明確的
`SwiftUI.State` 儲存。管理器腳本也曾在 Unicode 省略號前擴展錯誤的變數名稱。
現行腳本用 `${pkg_name}` 標明變數界線。Swift 的 deprecated `onChange` 和未使用的
`NSApplication.shared` 訊息是已見過的編譯警告。
