# 點心粵語點字輸入法 — macOS 版（Squirrel）

本資料夾提供在 **macOS** 上安裝「點心粵語點字輸入法」所需的全部檔案與一鍵部署腳本。
方案與詞庫沿用 Rime 格式。macOS 版另有 Squirrel 外觀設定和候選報讀元件。

維護者可閱讀 [編譯與發行](BUILD.md)、[Git 使用說明](GIT.md)和[接手說明](HANDOFF.md)。
給接手 agent 的入口是 [AGENTS.md](AGENTS.md)。

## 在 Mac 自行編譯及打包

使用 macOS 13 或更新版本。先用 `xcrun swiftc --version` 確認 Swift 編譯器可用。
如未安裝 Xcode 命令列工具，執行 `xcode-select --install`。

下載或複製本儲存庫後，在終端機進入 `mac` 資料夾，逐步執行以下命令。

1. **編譯報讀助手。**

   ```bash
   bash build-helper.sh --self-test
   ```

   這一步以 `dimsum_vo_helper.swift` 和 `SharedSettingsView.swift` 產生 `DimsumVoiceHelper.app`，
   並把 `sounds/only.wav` 放進助手資源。看到 `self-test PASS` 後再繼續。

2. **編譯管理器並產生發行 ZIP。**

   ```bash
   bash build-manager.sh
   ```

   這一步以 `DimsumManager.swift`、`SharedSettingsView.swift` 和 `DimsumUpdates.swift` 編譯管理器，再編譯安裝和更新腳本使用的
   `dimsum_tool`。管理器的 `Contents/Helpers` 放助手和工具，`Contents/Resources` 放 Rime 方案、
   Lua 元件、安裝與部署腳本、發音表、NVDA 字典和 Squirrel 安裝包。
   腳本會驗證整個 App 的簽署，再從打包後的位置跑一次助手自檢。

   完成後，`build/DimsumManager.app` 可直接打開測試，
   `build/DimsumManager-macOS.zip` 是給用家的發行包。
   首次打包會下載 [Squirrel 1.1.2 官方安裝包](https://github.com/rime/squirrel/releases/tag/1.1.2)，
   並核對 SHA-256。也可先把同一份 `Squirrel-1.1.2.pkg` 放在本資料夾，供離線編譯使用。
   隨附 Squirrel 按 GPLv3 發行，授權全文見 `licenses/Squirrel-GPL-3.0.txt`，
   對應原始碼見[Squirrel 1.1.2 原始碼及建置檔案](https://github.com/rime/squirrel/tree/1.1.2)。

目前的編譯腳本按編譯用 Mac 的架構產生程式，並作臨時簽署，供本機試用。跨 Apple Silicon 和 Intel
發行需為兩種架構各自編譯，或製作通用版。公開發行時可配合 Developer ID 簽署及 Apple 公證。

## 建立拖曳安裝的 DMG

已有 `build/DimsumManager.app` 或 `build/DimsumManager-macOS.zip` 時，可直接在本資料夾執行：

```bash
bash build-dmg.sh
```

如發行 ZIP 存在其他位置，可用 `bash build-dmg.sh "/路徑/DimsumManager-macOS.zip"` 指定。腳本會驗證
App 簽署，把管理器、指向 `/Applications` 的捷徑和 `安裝及使用說明.txt` 放入 `build/DimsumManager-macOS.dmg`。

## 用家如何安裝

1. 打開 `DimsumManager-macOS.dmg`，把 `DimsumManager.app` 拖到「應用程式」捷徑，再從「應用程式」打開它。
   使用 ZIP 版時，解壓後把 App 拖到「應用程式」資料夾即可。
2. 按 **01 安裝 Squirrel**。管理器會開啟 macOS 安裝程式。裝好 Squirrel 的用家可直接從 02 開始。
3. 按 **02 安裝／更新點心方案**。管理器會備份既有同名方案、安裝字庫和候選報讀元件，
   然後編譯方案並重新載入 Squirrel。
4. 按 **03 啟動報讀助手**。第一次請在「系統設定 → 私隱與保安」允許報讀助手的「輔助使用」
   與「輸入監察」，再重新啟動助手。新用家預設會聽到候選的配詞。

**重新部署候選報讀** 在「更新與維護」。如果點心方案已經安裝好，只需補裝或重新部署報讀元件，
可以直接按「重新部署候選報讀」。

## 程式、字庫及發音資料更新

管理器啟動時會檢查 GitHub。只有附 macOS 安裝檔的正式發行版才會提示程式更新。
字庫檢查會比較 GitHub 主字庫和你已部署的檔案。有更新時可選「查看新版程式」或「更新字庫」。

發音資料會獨立檢查。GitHub 上的 `characterDescriptions.dic`、`symbols.dic` 或 `cldr.dic`
有任何一份與目前發音表的來源不同，管理器就會顯示「有新版發音資料」。按「更新發音」後，
管理器會下載三份字典並重新產生發音表。完成後，重新啟動報讀助手即可套用。

你也可按「檢查更新」重新檢查，或用「版本資訊」查閱本機的程式及字庫版本。
如果舊版發音表的來源沒有紀錄，可以按一次「更新發音」建立紀錄。

## 查詢讀屏軟件最近報讀的配詞

聽完 VoiceOver 或 TurnOver 報讀後，可用已在讀屏軟件指派的快捷鍵查配詞，建議用 **讀屏鍵＋撇號（'）**。
一個字會讀全部配詞。詞語或句子會順序讀每個字的第一組配詞。例如「日本」會讀「日子，本來」。

在 DimsumManager 按一次「準備讀屏查配詞指令」。完成後 Finder 會顯示個人「應用程式」中的
`~/Applications/點心查配詞` 資料夾和兩個查配詞 App。再於你使用的讀屏軟件指派按鍵。
先啟動點心語音報讀助手，然後在文字編輯等 App 試用。

- **VoiceOver**：在「VoiceOver 工具程式 → 指令 → 編輯」新增自訂的「打開應用程式」指令。
  從個人「應用程式」的「點心查配詞」資料夾選 `點心查配詞（VoiceOver）.app`，
  用「聆聽按鍵」按 **VO＋撇號** 登記。在「一般」允許 AppleScript 控制，
  並在首次查詢時按 macOS 提示允許自動化。授權後重新聽一句再查。
- **TurnOver**：從按鍵設定的「Give a key to open an app」選 `點心查配詞（TurnOver）`，
  按 **TurnOver 鍵＋撇號** 登記。若撇號已指派給其他指令，可選用另一組鍵。
  請確認 Control＋Option＋Shift＋C 能執行「複製最近報讀」。
  如果目前的鍵盤模式沒有這組按鍵，可在 TurnOver 的按鍵設定指派它給該複製指令。
  查詢會暫用剪貼簿並在取得文字後還原原有內容。

點心本身也有獨立的查配詞快捷鍵，預設為 **Control＋Option＋Command＋撇號（'）**。
已儲存的快捷鍵會繼續套用，可在「報讀助手設定」更改或清空。清空後，點心會讓原本的按鍵組合傳給其他程式，
而從讀屏軟件已指派的查配詞指令仍可按該指派使用。

新用家的候選報讀預設為「標準」，會先讀選字數字，再讀候選。
「朗讀整行」的快捷鍵預設留空，可在「報讀助手設定」自行設定。已儲存的設定會繼續套用。

## ⌨️ 使用方式

| 操作 | 按鍵 |
|------|------|
| 切換三個點心方案 | `Control` + `` ` ``（反引號，數字 1 左側） |
| 六點並擊輸入（記憶/標準版） | 左手 `F D S` ／ 右手 `J K L` 同時按下並放開 |
| 一陽指版 | 逐鍵敲擊點碼 |
| 直接上字 | `Space` |
| 瀏覽候選字 | `↓`／`H`（下）、`↑`／`G`（上） |
| 快速選字 | 鍵盤上方實體數字鍵 `1`–`9` |

> 三個方案的差異與完整指法，請見原始項目的 `USAGE_GUIDE.md`。

## 🗑 解除安裝

```bash
./uninstall.sh
```

預設僅移除「點心」相關檔案（方案、詞庫、編譯快取），
Squirrel 與其他 Rime 方案不受影響。
`default.custom.yaml` 中的 dimsum 方案條目也會一併清除。

若要連同 Squirrel 一併移除：

```bash
./uninstall.sh --full
```

`--full` 模式會額外停用 Squirrel 輸入法、刪除 `/Library/Input Methods/Squirrel.app`、
清除 `pkgutil` 安裝記錄。


## 🔄 更新方案與詞庫

```bash
bash update.sh
```

`update.sh` 會從儲存庫根目錄的 `Dimsum dic and schema/` 同步四個核心檔案
（`dimsum_cb.dict.yaml`、`dimsum_cb.schema.yaml`、
`dimsum_cb_std.schema.yaml`、`dimsum_full.schema.yaml`）
到 `mac/rime/` 目錄，再自動部署到 `~/Library/Rime/`。

僅在檔案有變更時才進行更新；若全部為最新則直接退出。
更新流程包括：備份既有檔案 → 複製新檔 → 清除快取 → 重新編譯 → 觸發重新載入。

## 🔄 用終端機更新報讀字典

```bash
bash update_accessibility.sh --pull
```

這會從 GitHub 下載 NVDA 字典，並把轉換後的發音表寫入
`~/Library/Application Support/DimsumVoiceOver/voiceover_output/`。
在原始碼目錄執行 `bash update_accessibility.sh` 可使用本機字典重新產生發行用的發音表及來源紀錄。

## ❓ 疑難排解

- **候選窗不出現**：登出再登入一次；或確認已「重新部署」。
- **切換不到點心方案**：確認 `default.custom.yaml` 已正確部署且重新部署過。
- **並擊無反應**：確認使用的是「記憶版」或「標準版」；「一陽指版」不使用並擊。

## 開發時單獨測試助手

如想在編譯管理器之前先測試助手，請在本資料夾編譯助手、部署候選報讀，再直接打開助手。
以下命令適用於已安裝 Squirrel 及點心方案的 Mac。

```bash
bash build-helper.sh --self-test
bash deploy-candidate-reporting.sh
open ./DimsumVoiceHelper.app
```

## 試聽新版候選報讀

1. 在精簡模式下輸入並聽候選。切換標準模式，每個候選前會多讀對應的選字數字。
2. 開啟「候選用配詞報讀（例如「日」讀作「日子」）」。輸入時每個字讀第一組配詞。
   按上下方向鍵（並擊方案也可按 G/H），聽目前候選各字的全部配詞。
3. 在記憶版輸入「不過」等只有一個候選的編碼，確認 `only.wav` 短提示音與候選報讀可以同時聽到。
4. 輸入「不過你」，按「不過」旁的數字。助手應自動接讀剩餘段落的候選。也可先選「不」、再選「過」，確認每一段都能接讀。
5. 按 Control＋反引號打開方案選單，確認目前方案及方向鍵移動的報讀。

若要單獨測試音效，可在終端機執行：

```bash
./DimsumVoiceHelper.app/Contents/MacOS/DimsumVoiceHelper --test-candidate-sound
```

若仍聽不到下一段，先在點心方案試打，再執行 `bash deploy-candidate-reporting.sh --check`。
這項檢查會顯示是否收到新版候選報告，並省略輸入與候選內容。
