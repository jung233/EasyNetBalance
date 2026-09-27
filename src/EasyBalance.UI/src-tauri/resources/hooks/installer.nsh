!include "LogicLib.nsh"

!macro NSIS_HOOK_PREINSTALL
  DetailPrint "Stopping the existing EasyBalance service before replacing application files..."
  nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$INSTDIR\resources\stop-service.ps1" -ServiceName EasyBalance'
  Pop $0
  ${If} $0 != "0"
    MessageBox MB_ICONSTOP|MB_OK "EasyNetBalance could not stop the legacy EasyBalance service (exit code: $0). Close EasyNetBalance and retry the installer."
    Abort
  ${EndIf}
  nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$INSTDIR\resources\stop-service.ps1" -ServiceName EasyNetBalance'
  Pop $0
  ${If} $0 != "0"
    MessageBox MB_ICONSTOP|MB_OK "EasyNetBalance could not stop its background service (exit code: $0). Close EasyNetBalance and retry the installer."
    Abort
  ${EndIf}
  nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -Command "$$deadline=[DateTime]::UtcNow.AddSeconds(15); do { $$p=Get-Process -Name EasyNetBalance -ErrorAction SilentlyContinue; if (-not $$p) { exit 0 }; Start-Sleep -Milliseconds 250 } while ([DateTime]::UtcNow -lt $$deadline); exit 1"'
  Pop $0
  ${If} $0 != "0"
    MessageBox MB_ICONSTOP|MB_OK "Close the EasyNetBalance app window, then run the installer again."
    Abort
  ${EndIf}
!macroend

!macro NSIS_HOOK_POSTINSTALL
  DetailPrint "Registering EasyNetBalance background service..."
  nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$INSTDIR\resources\install-service.ps1" -InstallRoot "$INSTDIR"'
  Pop $0
  ${If} $0 != "0"
    MessageBox MB_ICONSTOP|MB_OK "EasyNetBalance could not install or start its background service (exit code: $0). Check that you ran the installer as an administrator, then retry."
    Abort
  ${EndIf}
!macroend

!macro NSIS_HOOK_PREUNINSTALL
  DetailPrint "Stopping EasyNetBalance background service..."
  nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$INSTDIR\resources\uninstall-service.ps1" -InstallRoot "$INSTDIR"'
  Pop $0
  ${If} $0 != "0"
    MessageBox MB_ICONSTOP|MB_OK "EasyNetBalance could not stop or remove its background service (exit code: $0). The application files were left in place."
    Abort
  ${EndIf}
!macroend
