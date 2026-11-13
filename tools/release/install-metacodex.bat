@echo off
rem Install MetaCodex on a Quest 3 / 3S over USB, ready to use (no permission prompts): install-metacodex.bat [MetaCodex.apk]
setlocal
set APK=%~1
if "%APK%"=="" set APK=%~dp0MetaCodex.apk
set PKG=com.pnncl.metacodex
where adb >nul 2>nul || (echo adb not found: install Android platform-tools & exit /b 1)
echo == headset & adb wait-for-device & adb devices -l
echo == installing %APK%
adb install -r -g "%APK%" || (echo install failed & exit /b 1)
echo == permissions
for %%P in (android.permission.RECORD_AUDIO com.oculus.permission.USE_SCENE horizonos.permission.HEADSET_CAMERA) do (
  adb shell pm grant %PKG% %%P >nul 2>nul && (echo   granted %%P) || (echo   %%P: asked at first start instead)
)
adb shell appops set %PKG% REQUEST_INSTALL_PACKAGES allow >nul 2>nul && echo   may install the apps it builds
echo == done: open MetaCodex from the Library (Unknown sources)
