; Installs the original Weasel 0.17.4 binaries with only shared runtime settings.
Unicode true
!include LogicLib.nsh
!include x64.nsh

Name "點心 Rime 輸入引擎"
OutFile "weasel-installer.exe"
InstallDir "$PROGRAMFILES64\Rime\weasel-0.17.4"
RequestExecutionLevel admin
SilentInstall silent
SilentUnInstall silent
SetCompressor /SOLID lzma

!define WEASEL_REG "Software\Rime\Weasel"
!define UNINSTALL_REG "Software\Microsoft\Windows\CurrentVersion\Uninstall\Weasel"
!define RUN_REG "Software\Microsoft\Windows\CurrentVersion\Run"

Section "Engine"
  SetRegView 32
  ReadRegStr $0 HKLM "${WEASEL_REG}" "WeaselRoot"
  ${If} $0 == ""
    SetRegView 64
    ReadRegStr $0 HKLM "${WEASEL_REG}" "WeaselRoot"
    SetRegView 32
  ${EndIf}
  ; Existing engine folders may contain installed schemas. Upgrade the binaries
  ; in place and preserve those files and the user profile outside this folder.
  ${If} $0 != ""
  ${AndIf} ${FileExists} "$0\WeaselSetup.exe"
    StrCpy $INSTDIR $0
  ${EndIf}
  ReadRegDWORD $1 HKLM "${UNINSTALL_REG}" "DimsumOwnRuntimeData"
  ${IfNot} ${FileExists} "$INSTDIR\data\*.*"
    StrCpy $1 1
  ${EndIf}

  ; Track whether this bundle owns the stock Taiwan switcher entry.
  ClearErrors
  ReadRegDWORD $2 HKLM "${UNINSTALL_REG}" "DimsumOwnTaiwanTip"
  ${If} ${Errors}
    SetRegView 64
    ReadRegStr $3 HKLM "SOFTWARE\Microsoft\CTF\TIP\{A3F4CDED-B1E9-41EE-9CA6-7B4D0DE6CB0A}\LanguageProfile\0x00000404\{3D02CAB6-2B8E-4781-BA20-1C9267529467}" "Description"
    SetRegView 32
    StrCpy $2 0
    ${If} $3 == ""
    ${OrIf} $3 == "點心粵語點字輸入法"
    ${OrIf} $1 == 1
      StrCpy $2 1
    ${EndIf}
  ${EndIf}

  SetOutPath "$INSTDIR"
  SetOverwrite on
  File "LICENSE.txt"
  File "WeaselServer.exe"
  File "WeaselDeployer.exe"
  File "WeaselSetup.exe"
  File "rime.dll"
  File "WinSparkle.dll"
  File "weasel.dll"
  File "weaselx64.dll"
  File "weaselARM.dll"
  File "weaselARM64.dll"
  File "weaselARM64X.dll"

  SetOutPath "$INSTDIR\data"
  SetOverwrite off
  File "data\default.yaml"
  File "data\weasel.yaml"
  File "data\punctuation.yaml"
  File "data\key_bindings.yaml"
  File "data\symbols.yaml"
  SetOverwrite on

  SetOutPath "$INSTDIR"
  ClearErrors
  ExecWait '"$INSTDIR\WeaselSetup.exe" /t' $0
  ${If} ${Errors}
  ${OrIf} $0 != 0
    SetErrorLevel 1
    Abort
  ${EndIf}

  WriteRegStr HKLM "${WEASEL_REG}" "InstallDir" "$INSTDIR\.."
  WriteRegStr HKLM "${UNINSTALL_REG}" "DisplayName" "點心 Rime 輸入引擎"
  WriteRegStr HKLM "${UNINSTALL_REG}" "DisplayVersion" "0.17.4.0"
  WriteRegStr HKLM "${UNINSTALL_REG}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegDWORD HKLM "${UNINSTALL_REG}" "SystemComponent" 1
  WriteRegDWORD HKLM "${UNINSTALL_REG}" "DimsumOwnRuntimeData" $1
  WriteRegDWORD HKLM "${UNINSTALL_REG}" "DimsumOwnTaiwanTip" $2
  WriteUninstaller "$INSTDIR\uninstall.exe"
  SetRegView 64
  WriteRegStr HKLM "${RUN_REG}" "WeaselServer" '"$INSTDIR\WeaselServer.exe"'
  WriteRegStr HKCU "${WEASEL_REG}\Updates" "CheckForUpdates" "0"
SectionEnd

Section "Uninstall"
  SetRegView 32
  ReadRegDWORD $1 HKLM "${UNINSTALL_REG}" "DimsumOwnRuntimeData"
  ExecWait '"$INSTDIR\WeaselSetup.exe" /u'
  DeleteRegKey HKLM "${UNINSTALL_REG}"
  SetRegView 64
  DeleteRegValue HKLM "${RUN_REG}" "WeaselServer"
  SetRegView 32
  SetOutPath "$TEMP"
  Delete /REBOOTOK "$INSTDIR\WeaselServer.exe"
  Delete /REBOOTOK "$INSTDIR\WeaselDeployer.exe"
  Delete /REBOOTOK "$INSTDIR\WeaselSetup.exe"
  Delete /REBOOTOK "$INSTDIR\rime.dll"
  Delete /REBOOTOK "$INSTDIR\WinSparkle.dll"
  Delete /REBOOTOK "$INSTDIR\weasel.dll"
  Delete /REBOOTOK "$INSTDIR\weaselx64.dll"
  Delete /REBOOTOK "$INSTDIR\weaselARM.dll"
  Delete /REBOOTOK "$INSTDIR\weaselARM64.dll"
  Delete /REBOOTOK "$INSTDIR\weaselARM64X.dll"
  Delete "$INSTDIR\LICENSE.txt"
  Delete /REBOOTOK "$INSTDIR\uninstall.exe"
  ${If} $1 == 1
    Delete "$INSTDIR\data\default.yaml"
    Delete "$INSTDIR\data\weasel.yaml"
    Delete "$INSTDIR\data\punctuation.yaml"
    Delete "$INSTDIR\data\key_bindings.yaml"
    Delete "$INSTDIR\data\symbols.yaml"
  ${EndIf}
  RMDir "$INSTDIR\data"
  RMDir "$INSTDIR"
SectionEnd
