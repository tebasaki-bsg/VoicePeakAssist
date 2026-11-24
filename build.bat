@echo off
rem VoicePeak 発音調整アシスト - ビルド用スクリプト
rem Windows に最初から入っている .NET Framework 4.x の C# コンパイラ（csc.exe）でビルドします。
chcp 65001 > nul
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo csc.exe が見つかりません（.NET Framework 4.x が必要です）。
  exit /b 1
)

if not exist "VoicePeakAssist" mkdir "VoicePeakAssist"

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /nowarn:414,169,1668 ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll ^
  /resource:src\app.ico,app.ico /win32icon:src\app.ico ^
  /out:VoicePeakAssist\VoicePeakAssist.exe src\*.cs
if errorlevel 1 (
  echo ビルドに失敗しました。
  exit /b 1
)
echo ビルドしました: VoicePeakAssist\VoicePeakAssist.exe
