# Git 使用說明

從 GitHub 複製儲存庫後，進入 `mac` 資料夾，即可查看 Mac 原始碼的提交及工作目錄狀態。

```bash
git status
git log --oneline -- .
```

修改時，可以建一個分支，確認改動後才暫存和提交。以下以修改管理器為例。

```bash
git switch -c fix/manager
git diff
git add -- DimsumManager.swift
git commit -m "Update manager workflow"
```

如 Git 要求作者資料，可在本儲存庫設定你的名稱及電郵。

```bash
git config user.name "你的名稱"
git config user.email "你的電郵"
```

`.gitignore` 已排除編譯產物、下載的安裝包及本地日誌。字表、音效和 DMG 版面範本屬於原始資料，
會隨程式一起提交。

準備提交到 GitHub 時，先推送工作分支，再開 Pull Request 供檢查。詳見[貢獻說明](../CONTRIBUTING.md)。
