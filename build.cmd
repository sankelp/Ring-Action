@echo off
setlocal
set FX=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
"%FX%\csc.exe" /nologo /target:winexe /optimize+ /platform:anycpu /debug- ^
  /win32icon:icons/brand/RingAction.ico /win32manifest:app.manifest /resource:ui.html,ui.html /resource:icons/brand/tray-white-32.png,tray-white-32.png /resource:icons/brand/tray-black-32.png,tray-black-32.png ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll ^
  /out:RingAction.exe RingAction.cs
endlocal
