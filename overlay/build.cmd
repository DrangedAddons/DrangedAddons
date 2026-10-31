@echo off
rem Builds bin\DrangedOverlay64.dll (and the test programs) with the Visual Studio 2022 Build Tools.
setlocal
cd /d "%~dp0"
rem Not inside an if block: the ")" in "Program Files (x86)" would end it.
if defined VCINSTALLDIR goto have_compiler
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VS=%%i"
rem vcvars64 runs vswhere by name.
set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
:have_compiler
if not exist bin mkdir bin
if not exist build mkdir build

set IMGUI=third_party\imgui
set FLAGS=/nologo /O2 /MT /EHsc /std:c++20 /utf-8 /W3 /DNOMINMAX /DWIN32_LEAN_AND_MEAN /D_CRT_SECURE_NO_WARNINGS ^
 /DVK_USE_PLATFORM_WIN32_KHR /DVK_NO_PROTOTYPES /DIMGUI_IMPL_VULKAN_NO_PROTOTYPES ^
 /I%IMGUI% /I%IMGUI%\backends /Ithird_party\vulkan-headers\include /Fobuild\

cl %FLAGS% /LD src\overlay.cpp src\vulkan_layer.cpp ^
 %IMGUI%\imgui.cpp %IMGUI%\imgui_draw.cpp %IMGUI%\imgui_tables.cpp %IMGUI%\imgui_widgets.cpp ^
 %IMGUI%\backends\imgui_impl_win32.cpp %IMGUI%\backends\imgui_impl_vulkan.cpp ^
 /Fe:bin\DrangedOverlay64.dll /link user32.lib gdi32.lib || exit /b 1
copy /y DrangedOverlay64.json bin\ >nul

cl %FLAGS% tests\vktest.cpp /Fe:bin\vktest.exe /link user32.lib || exit /b 1
echo Built bin\DrangedOverlay64.dll
