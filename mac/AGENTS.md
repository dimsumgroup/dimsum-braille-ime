# 接手此儲存庫

先閱讀 [HANDOFF.md](HANDOFF.md) 核對目前功能及驗證範圍。編譯、發行和 Mac 試聽步驟見
[BUILD.md](BUILD.md)。版本控制操作見 [GIT.md](GIT.md)。

- 管理器入口是 `DimsumManager.swift`，助手入口是 `dimsum_vo_helper.swift`。
  兩者共用 `SharedSettingsView.swift` 及同一份用家偏好設定。
- `LastSpokenLookup.swift` 在按下查配詞快捷鍵時讀取最近報讀，並共用助手發音表。
  查詢的原文和配詞答覆不寫入點心日誌。
- `ScreenReaderLookupAction.swift` 亦編譯成背景查配詞指令。讀屏軟件保管按鍵指派，指令只傳遞來源名稱。
- 安裝步驟只在需要執行時顯示固定編號 01、02、03。維護功能的名稱不加編號。
- 更新偵測集中在 `DimsumUpdates.swift`。程式看正式 Mac Release，字庫比對已部署檔案，
  NVDA 字典比對發音表的來源紀錄。更新動作由用家觸發。
- `update_accessibility.sh` 轉換成功後才寫入 `voiceover_output/nvda-source-hashes.json`。
  若更新內附 NVDA 字典，同時重新產生發音表及來源紀錄。
- 候選的次序、選字數字和分段以 Rime 當前候選頁為準。Lua 發佈程式是
  `rime/lua/dimsum_candidate_reporter.lua`。
- 維持 macOS 13 的編譯目標。由於部分 Mac 的命令列工具讀取 SwiftUI `@State` 巨集會出錯，
  現有畫面用明確的 `SwiftUI.State` 儲存。
- 修改 shell 腳本時，用 `${name}` 清楚分隔緊貼中文文字或標點的變數。驗證適用於 macOS 內建 Bash。
- 打包後的 App 資源由簽章保護。方案、設定、更新的發音表以及日誌寫於用家目錄。
  支援診斷宜用狀態或計數，避免附上用家實際輸入的候選報告。
