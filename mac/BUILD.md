# 編譯及發行點心粵語點字輸入法

在 macOS 13 或更新版本，開啟此目錄的終端機。先用 `xcrun swiftc --version` 確認 Swift 可用。
如工具尚未安裝，執行 `xcode-select --install`，完成安裝後再繼續。

## 從原始碼編譯

逐步執行，並確認每一步成功。

1. 編譯報讀助手並跑自檢。

   ```bash
   bash build-helper.sh --self-test
   ```

   這一步會產生 `DimsumVoiceHelper.app`。Swift 原始碼使用 `dimsum_vo_helper.swift` 和
   `SharedSettingsView.swift`，音效從 `sounds/only.wav` 複製到 App 資源。成功時會顯示
   `self-test PASS`。

2. 編譯管理器、命令列工具和發行 ZIP。

   ```bash
   bash build-manager.sh
   ```

   這一步會編譯管理器、共用設定畫面、`dimsum_tool` 和兩個背景查配詞指令，並把已編譯的助手
   內嵌到管理器。查配詞指令在 `Contents/Helpers`，Rime 方案、部署腳本和字表在 `Contents/Resources`。Squirrel 安裝包會從
   官方來源下載並核對 SHA-256。離線時可把同一份 `Squirrel-1.1.2.pkg` 放在此目錄供腳本取用。

   產物為 `build/DimsumManager.app` 和 `build/DimsumManager-macOS.zip`。

3. 製作拖曳安裝的 DMG。

   ```bash
   bash build-dmg.sh
   ```

   產物為 `build/DimsumManager-macOS.dmg`，內有 `DimsumManager.app` 和指向
   `/Applications` 的捷徑，以及可直接閱讀的 `安裝及使用說明.txt`。已有發行 ZIP 時，也可執行
   `bash build-dmg.sh "/路徑/DimsumManager-macOS.zip"`，直接使用其中的 App。
   `dmg-layout.dsstore` 是 Finder 圖示排列的範本。

目前腳本使用編譯當時的 Mac 架構和臨時簽署。Apple Silicon、Intel 和公開發行的簽署說明見 README。

## 用家操作及測試

用家從 DMG 把管理器拖到「應用程式」後，在管理器依序執行 **01 安裝 Squirrel**、
**02 安裝／更新點心方案**、**03 啟動報讀助手**。已有 Squirrel 的用家可從 02 開始。
第 02 步已部署候選報讀。既有方案需要單獨重新部署時使用「重新部署候選報讀」。

首次啟動助手時，按 macOS 提示授予「輔助使用」與「輸入監察」。新用家會預設開啟候選配詞報讀，
用家原已儲存的開關值會繼續套用。測試時可在記憶版輸入「不過你」，用數字鍵選「不過」，
確認接著報讀「你」的候選。再輸入只有一個候選的編碼，確認能同時聽到候選及 `only.wav`。

如果正在測試獨立版助手，可在點心方案試打後執行 `bash deploy-candidate-reporting.sh --check`。
它會回報有沒有收到候選報告，並省略輸入內容。單獨試播音效的指令見 README。

## 發行程式更新

管理器從 `DimsumManager-Info.plist` 讀取目前程式版本。發布新版時，讓
`CFBundleShortVersionString` 與 GitHub Release 的數字標籤一致。本版用 `v1.2.1`。
在正式 Release 附上 `DimsumManager-macOS.dmg` 或 `DimsumManager-macOS.zip`。
僅有 Windows 安裝檔、原始碼、草稿或測試版的 Release 不會觸發程式更新提示。

字庫更新來自 `main` 內的 `Dimsum dic and schema/dimsum_cb.dict.yaml`。如更新字庫，請在表頭
保留 `version:` 及字庫修訂日期。管理器比對已部署檔案的 Git 雜湊，並使用版本與日期識別較舊的上游檔案。

發音資料更新來自 `main` 內的 `NVDA dic/`。管理器比對上次成功轉換時記錄的三份來源雜湊。
如要在下一個安裝包附上新版發音資料，先在原始碼目錄用 `bash update_accessibility.sh`
同步本機上游字典並產生發音表。將 `nvda_dic/` 和 `voiceover_output/` 一同納入發行，
包括發音表和 `nvda-source-hashes.json`。

## 驗證更新規則

在 Mac 可用以下命令執行測試。測試使用模擬的 GitHub 回應，涵蓋平台、正式版本、版本次序、
已部署字庫、三份 NVDA 字典、舊發音表，以及 API 查詢失敗時的處理。

```bash
mkdir -p build
xcrun swiftc -swift-version 5 DimsumUpdates.swift tests/UpdateChecks.swift -o build/UpdateChecks
./build/UpdateChecks
```
