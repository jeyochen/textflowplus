; 该脚本使用 HM VNISEdit 脚本编辑器向导产生
; TextFlowPlus.exe 打包脚本
; 安装程序初始定义常量
!define PRODUCT_NAME "TextFlowPlus"
!define PRODUCT_VERSION "1.0.0.0"
!define PRODUCT_PUBLISHER "actci Co., Ltd"
!define PRODUCT_WEB_SITE "http://www.app2s.cn"
!define PRODUCT_UNINST_KEY "Software\actci\${PRODUCT_NAME}\Uninstall"
!define PRODUCT_INST_KEY "Software\actci\${PRODUCT_NAME}\install"
;把注册表信息写入到 HKEY_CURRENT_USER 这样就不需要管理员权限了
;!define PRODUCT_REG_ROOT_KEY "HKLM"
!define PRODUCT_REG_ROOT_KEY "HKCU"
!define PRODUCT_STARTMENU_REGVAL "NSIS:StartMenuDir"

;使用 unicode 编码防止乱码
Unicode true
SetCompressor lzma
;SetCompress off

;设置安装程序执行级别为用户级别，这样就不需要管理员权限了。
RequestExecutionLevel admin

;设置安装包属性里的详细信息
VIProductVersion "${PRODUCT_VERSION}" ;版本号，格式为 X.X.X.X (若使用则本条必须)
VIAddVersionKey  "ProductName" "${PRODUCT_NAME}" ;产品名称
VIAddVersionKey  "Comments" "多功能文本编辑器" ;备注
VIAddVersionKey  "CompanyName" "${PRODUCT_PUBLISHER}" ;公司名称
VIAddVersionKey  "LegalTrademarks" "" ;合法商标
VIAddVersionKey  "LegalCopyright" "Copyright (c) ${PRODUCT_PUBLISHER}" ;合法版权
VIAddVersionKey  "FileDescription" "${PRODUCT_NAME} Setup File" ;文件描述 (标准信息)
VIAddVersionKey  "FileVersion" "${PRODUCT_VERSION}" ;文件版本
VIAddVersionKey  "ProductVersion" "${PRODUCT_VERSION}" ;产品版本

; ------ MUI 现代界面定义 (1.67 版本以上兼容) ------
!include LogicLib.nsh
!include "MUI2.nsh"
!include "StrFunc.nsh"
${StrStr}

; 检测进程是否运行（替代 nsProcess 插件）：结果变量 = 0 表示运行中，非 0 表示未运行
!macro FindProcess ProcessName ResultVar
  nsExec::ExecToStack 'tasklist /NH /FI "IMAGENAME eq ${ProcessName}"'
  Pop $R0
  Pop $R1
  ${StrStr} $R0 "$R1" "${ProcessName}"
  StrCpy ${ResultVar} 605
  ${If} $R0 != ""
    StrCpy ${ResultVar} 0
  ${EndIf}
!macroend


; MUI 预定义常量
!define MUI_ABORTWARNING
!define MUI_ICON "Assets/app.ico"
!define MUI_UNICON "uninstall.ico"

; 语言选择窗口常量设置 - 不保存语言选择，每次都提示
;!define MUI_LANGDLL_REGISTRY_ROOT "HKCU"
;!define MUI_LANGDLL_REGISTRY_KEY "Software\HY\Cura\install"
;!define MUI_LANGDLL_REGISTRY_VALUENAME "InstallLang"
!define MUI_LANGDLL_ALLLANGUAGES

; 欢迎页面
!insertmacro MUI_PAGE_WELCOME
; 许可协议页面
!insertmacro MUI_PAGE_LICENSE "软件许可协议.rtf"
; 安装目录选择页面
!insertmacro MUI_PAGE_DIRECTORY
; 开始菜单设置页面
var ICONS_GROUP
!define MUI_STARTMENUPAGE_NODISABLE
!define MUI_STARTMENUPAGE_DEFAULTFOLDER "${PRODUCT_NAME}"
!define MUI_STARTMENUPAGE_REGISTRY_ROOT "${PRODUCT_REG_ROOT_KEY}"
!define MUI_STARTMENUPAGE_REGISTRY_KEY "${PRODUCT_UNINST_KEY}"
!define MUI_STARTMENUPAGE_REGISTRY_VALUENAME "${PRODUCT_STARTMENU_REGVAL}"
!insertmacro MUI_PAGE_STARTMENU Application $ICONS_GROUP

; 安装过程页面
!insertmacro MUI_PAGE_INSTFILES
; 安装完成页面
!insertmacro MUI_PAGE_FINISH

; 安装卸载过程页面
!insertmacro MUI_UNPAGE_INSTFILES

; 主界面语言
!insertmacro MUI_LANGUAGE "SimpChinese"

; 安装预释放文件
;!insertmacro MUI_RESERVEFILE_LANGDLL
;!insertmacro MUI_RESERVEFILE_INSTALLOPTIONS
; ------ MUI 现代界面定义结束 ------

Name "${PRODUCT_NAME} ${PRODUCT_VERSION}"
OutFile "${PRODUCT_NAME}_install.exe"
InstallDir "C:\actci\${PRODUCT_NAME}"
ShowInstDetails show
ShowUnInstDetails show
var UninstallLang

Section "MainSection" SEC01
  SetOutPath "$INSTDIR"
  SetOverwrite ifnewer
  File /r "bin\Release\net8.0\win-x64\publish\*.*"

; 创建开始菜单快捷方式
  !insertmacro MUI_STARTMENU_WRITE_BEGIN Application
  CreateDirectory "$SMPROGRAMS\$ICONS_GROUP"
  CreateShortCut "$SMPROGRAMS\$ICONS_GROUP\${PRODUCT_NAME}.lnk" "$INSTDIR\TextFlowPlus.exe"
  CreateShortCut "$SMPROGRAMS\$ICONS_GROUP\uninst.lnk" "$INSTDIR\uninst.exe"
  CreateShortCut "C:\Users\Public\Desktop\${PRODUCT_NAME}.lnk" $INSTDIR\TextFlowPlus.exe
  !insertmacro MUI_STARTMENU_WRITE_END
SectionEnd

Section -AdditionalIcons
  !insertmacro MUI_STARTMENU_WRITE_BEGIN Application
  CreateDirectory "$SMPROGRAMS\$ICONS_GROUP"
  CreateShortCut "$SMPROGRAMS\$ICONS_GROUP\Uninstall.lnk" "$INSTDIR\uninst.exe"
  !insertmacro MUI_STARTMENU_WRITE_END
SectionEnd

Section -Post
  WriteUninstaller "$INSTDIR\uninst.exe"
  SetRegView 64
  WriteRegStr ${PRODUCT_REG_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "DisplayName" "$(^Name)"
  SetRegView 64
  WriteRegStr ${PRODUCT_REG_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "UninstallString" "$INSTDIR\uninst.exe"
  SetRegView 64
  WriteRegStr ${PRODUCT_REG_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  SetRegView 64
  WriteRegStr ${PRODUCT_REG_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "URLInfoAbout" "${PRODUCT_WEB_SITE}"
  SetRegView 64
  WriteRegStr ${PRODUCT_REG_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "Publisher" "${PRODUCT_PUBLISHER}"
  SetRegView 64
  WriteRegStr ${PRODUCT_REG_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "Publisher" "${PRODUCT_PUBLISHER}"
  SetRegView 64
  WriteRegStr ${PRODUCT_REG_ROOT_KEY} "${PRODUCT_INST_KEY}" "InstallPath" $INSTDIR
  ; 保存安装语言选择
  WriteRegStr ${PRODUCT_REG_ROOT_KEY} "${PRODUCT_INST_KEY}" "InstallLang" "$LANGUAGE"

  SetRegView 64
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_NAME}" "DisplayName" "$(^Name)"
  SetRegView 64
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_NAME}" "UninstallString" "$INSTDIR\uninst.exe"
  SetRegView 64
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_NAME}" "Publisher" "${PRODUCT_PUBLISHER}"
  SetRegView 64
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_NAME}"  "DisplayIcon" "$INSTDIR\TextFlowPlus.exe,0"
  ; 注册到鼠标右键菜单（文件 / 文件夹 / 文件夹背景）
  SetRegView 64
  WriteRegStr HKCU "Software\Classes\*\shell\TextFlowPlus" "" "用 TextFlowPlus 打开"
  SetRegView 64
  WriteRegStr HKCU "Software\Classes\*\shell\TextFlowPlus" "Icon" "$INSTDIR\TextFlowPlus.exe,0"
  SetRegView 64
  WriteRegStr HKCU "Software\Classes\*\shell\TextFlowPlus\command" "" '"$INSTDIR\TextFlowPlus.exe" "%1"'

  SetRegView 64
  WriteRegStr HKCU "Software\Classes\Directory\shell\TextFlowPlus" "" "用 TextFlowPlus 打开"
  SetRegView 64
  WriteRegStr HKCU "Software\Classes\Directory\shell\TextFlowPlus" "Icon" "$INSTDIR\TextFlowPlus.exe,0"
  SetRegView 64
  WriteRegStr HKCU "Software\Classes\Directory\shell\TextFlowPlus\command" "" '"$INSTDIR\TextFlowPlus.exe" "%1"'

  SetRegView 64
  WriteRegStr HKCU "Software\Classes\Directory\Background\shell\TextFlowPlus" "" "用 TextFlowPlus 打开"
  SetRegView 64
  WriteRegStr HKCU "Software\Classes\Directory\Background\shell\TextFlowPlus" "Icon" "$INSTDIR\TextFlowPlus.exe,0"
  SetRegView 64
  WriteRegStr HKCU "Software\Classes\Directory\Background\shell\TextFlowPlus\command" "" '"$INSTDIR\TextFlowPlus.exe" "%V"'
SectionEnd

#-- 根据 NSIS 脚本编辑规则，所有 Function 区段必须放置在 Section 区段之后编写，以避免安装程序出现未可预知的问题。--#

/******************************
 *  以下是安装程序的卸载部分  *
 ******************************/

Section Uninstall
  !insertmacro MUI_STARTMENU_GETFOLDER "Application" $ICONS_GROUP

  Delete "C:\Users\Public\Desktop\${PRODUCT_NAME}.lnk"
  Delete "$SMPROGRAMS\$ICONS_GROUP\${PRODUCT_NAME}.lnk"
  Delete "$SMPROGRAMS\$ICONS_GROUP\uninst.lnk"
  RMDir "$SMPROGRAMS\$ICONS_GROUP"

  RMDir /r "$INSTDIR\inference"
  RMDir /r "$INSTDIR\zh-Hans"
  Delete    "$INSTDIR\*.*"

  ;RMDir "$INSTDIR"

  SetRegView 64
  DeleteRegKey ${PRODUCT_REG_ROOT_KEY} "${PRODUCT_UNINST_KEY}"
  DeleteRegKey ${PRODUCT_REG_ROOT_KEY} "${PRODUCT_INST_KEY}"
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_NAME}"
  ; 删除鼠标右键菜单注册
  SetRegView 64
  DeleteRegKey HKCU "Software\Classes\*\shell\TextFlowPlus"
  SetRegView 64
  DeleteRegKey HKCU "Software\Classes\Directory\shell\TextFlowPlus"
  SetRegView 64
  DeleteRegKey HKCU "Software\Classes\Directory\Background\shell\TextFlowPlus"
  SetAutoClose true
SectionEnd

#-- 根据 NSIS 脚本编辑规则，所有 Function 区段必须放置在 Section 区段之后编写，以避免安装程序出现未可预知的问题。--#
Function un.onInit
  StrCpy $UninstallLang "${LANG_SIMPCHINESE}" ; 默认简体中文
FunctionEnd

Function un.onUninstSuccess
  HideWindow
  MessageBox MB_ICONINFORMATION|MB_OK "$(^Name) 已成功地从您的计算机移除。"
FunctionEnd

Function .onInstSuccess
    # 安装成功后执行
FunctionEnd

!define PROCESS_NAME "TextFlowPlus.exe"
Var ProcessFound
Function .onInit
	!insertmacro MUI_LANGDLL_DISPLAY

	;检查程序是否退出
    !insertmacro FindProcess TextFlowPlus.exe $ProcessFound 
	${If} $ProcessFound = 0
        MessageBox MB_OK|MB_ICONINFORMATION "${PROCESS_NAME}正在运行，请关闭 ${PROCESS_NAME} 后再安装。"
        Quit ; 终止安装程序
    ${EndIf}

    ;读取安装时写入注册表的卸载程序
    SetRegView 64
    ReadRegStr $0 "${PRODUCT_REG_ROOT_KEY}" "${PRODUCT_UNINST_KEY}" "UninstallString"
    ${If} $0 != ""
        ${If} $LANGUAGE == ${LANG_SIMPCHINESE}
            MessageBox MB_OKCANCEL|MB_ICONINFORMATION "检测到已安装的版本，需要先卸载。$\n点击'确定'继续卸载，'取消'退出安装。" IDOK uninstall
        ${EndIf}
        Quit ; 用户选择取消，退出安装

        uninstall:
            ; 执行卸载程序（异步执行）
            Exec '$0'

            ; 等待卸载完成（通过检测 uninst.exe 进程是否存在）
            StrCpy $1 0
            look_check_uninstall:
                IntOp $1 $1 + 1
                ${If} $1 > 30
                    ; 超时退出（90 秒）
                    ${If} $LANGUAGE == ${LANG_SIMPCHINESE}
                        MessageBox MB_ICONSTOP|MB_OK "卸载超时（超过 90 秒），安装程序将退出。"
                    ${EndIf}
                    Quit
                ${EndIf}

                ; 检查卸载程序进程是否还在运行
                !insertmacro FindProcess uninst.exe $ProcessFound 
                ${If} $ProcessFound != 0
                    Goto unistall_done
                ${EndIf}

                Sleep 3000 ; 等待 3 秒
                Goto look_check_uninstall

            unistall_done:
                ; 等待一小段时间确保文件释放
                Sleep 2000
    ${EndIf}

    ; 删除旧的安装注册表键
    SetRegView 64
    DeleteRegKey ${PRODUCT_REG_ROOT_KEY} "${PRODUCT_INST_KEY}"
FunctionEnd
