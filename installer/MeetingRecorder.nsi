Unicode true
ManifestDPIAware true
RequestExecutionLevel user

!include "MUI2.nsh"
!include "LogicLib.nsh"

!ifndef VERSION
  !define VERSION "1.0.0"
!endif
!ifndef APP_FILES
  !error "APP_FILES 未定义"
!endif
!ifndef VC_REDIST
  !error "VC_REDIST 未定义"
!endif
!ifndef OUTPUT_FILE
  !define OUTPUT_FILE "MeetingRecorder-Setup.exe"
!endif

Name "MeetingRecorder ${VERSION}"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\MeetingRecorder"
InstallDirRegKey HKCU "Software\MeetingRecorder" "InstallDir"
BrandingText "MeetingRecorder · 本地会议录屏"
SetCompressor /SOLID lzma
ShowInstDetails show
ShowUninstDetails show

VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName" "MeetingRecorder"
VIAddVersionKey /LANG=2052 "FileDescription" "MeetingRecorder 会议录屏安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "MeetingRecorder contributors"

!define MUI_ABORTWARNING
!define MUI_ICON "${NSISDIR}\Contrib\Graphics\Icons\modern-install.ico"
!define MUI_UNICON "${NSISDIR}\Contrib\Graphics\Icons\modern-uninstall.ico"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${APP_FILES}\THIRD-PARTY-NOTICES.txt"
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

Section "MeetingRecorder（必需）" SEC_MAIN
  SectionIn RO
  SetOutPath "$INSTDIR"
  File /r "${APP_FILES}\*.*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\MeetingRecorder" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MeetingRecorder" "DisplayName" "MeetingRecorder"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MeetingRecorder" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MeetingRecorder" "Publisher" "MeetingRecorder contributors"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MeetingRecorder" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MeetingRecorder" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MeetingRecorder" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MeetingRecorder" "NoRepair" 1
  CreateDirectory "$SMPROGRAMS\MeetingRecorder"
  CreateShortcut "$SMPROGRAMS\MeetingRecorder\MeetingRecorder.lnk" "$INSTDIR\MeetingRecorder.exe"
  CreateShortcut "$SMPROGRAMS\MeetingRecorder\卸载 MeetingRecorder.lnk" "$INSTDIR\Uninstall.exe"

  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File /oname=vc_redist.x64.exe "${VC_REDIST}"
  DetailPrint "检查并安装 Microsoft Visual C++ x64 运行库…"
  ExecWait '"$PLUGINSDIR\vc_redist.x64.exe" /install /quiet /norestart' $0
  ${If} $0 != 0
  ${AndIf} $0 != 1638
  ${AndIf} $0 != 3010
    MessageBox MB_ICONEXCLAMATION|MB_OK "VC++ 运行库返回代码 $0。MeetingRecorder 已安装，但首次运行前可能需要手动安装运行库。"
  ${EndIf}
SectionEnd

Section /o "桌面快捷方式" SEC_DESKTOP
  CreateShortcut "$DESKTOP\MeetingRecorder.lnk" "$INSTDIR\MeetingRecorder.exe"
SectionEnd

LangString DESC_SEC_MAIN ${LANG_SIMPCHINESE} "安装 MeetingRecorder、自包含 .NET 10 运行时和 VC++ 运行库。"
LangString DESC_SEC_DESKTOP ${LANG_SIMPCHINESE} "在当前用户桌面创建快捷方式。"
!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_MAIN} $(DESC_SEC_MAIN)
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_DESKTOP} $(DESC_SEC_DESKTOP)
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Section "Uninstall"
  Delete "$DESKTOP\MeetingRecorder.lnk"
  RMDir /r "$SMPROGRAMS\MeetingRecorder"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MeetingRecorder"
  DeleteRegKey HKCU "Software\MeetingRecorder"
  RMDir /r "$INSTDIR"
SectionEnd
