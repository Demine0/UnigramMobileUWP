# Adds the toolchain pieces the hosted windows-2022 image does not ship:
# the v141 (MSVC 14.16) x64/ARM compilers with UWP support, used by vcpkg,
# the TDLib code generator and the app's native projects, and the Windows
# 10 SDK 10.0.18362 that every project targets.

$ErrorActionPreference = "Stop"

$vsPath = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath
Write-Output "Visual Studio: $vsPath"

$components = @(
    "Microsoft.VisualStudio.Component.VC.v141.x86.x64",
    "Microsoft.VisualStudio.Component.VC.v141.ARM",
    "Microsoft.VisualStudio.ComponentGroup.UWP.VC.v141",
    "Microsoft.VisualStudio.Component.VC.Tools.ARM"
)
$arguments = @("modify", "--installPath", "`"$vsPath`"", "--quiet", "--norestart", "--nocache")
foreach ($component in $components) {
    $arguments += @("--add", $component)
}
$process = Start-Process -FilePath "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vs_installer.exe" `
    -ArgumentList $arguments -Wait -PassThru -NoNewWindow
if ($process.ExitCode -notin @(0, 3010)) {
    throw "vs_installer exited with code $($process.ExitCode)."
}

if (-not (Test-Path "${env:ProgramFiles(x86)}\Windows Kits\10\Include\10.0.18362.0")) {
    $installer = Join-Path $env:RUNNER_TEMP "winsdksetup.exe"
    Invoke-WebRequest -Uri "https://go.microsoft.com/fwlink/?linkid=2083338" -OutFile $installer
    $process = Start-Process -FilePath $installer -ArgumentList @("/features", "+", "/quiet", "/norestart") -Wait -PassThru
    if ($process.ExitCode -notin @(0, 3010)) {
        throw "Windows SDK 18362 setup exited with code $($process.ExitCode)."
    }
}

foreach ($required in @(
    "VC\Auxiliary\Build\14.16",
    "VC\Tools\MSVC\14.16.27023\bin\Hostx64\arm\cl.exe"
)) {
    if (-not (Test-Path (Join-Path $vsPath $required))) {
        Get-ChildItem (Join-Path $vsPath "VC\Tools\MSVC") | ForEach-Object { Write-Output "Installed MSVC: $($_.Name)" }
        throw "Missing toolchain piece after install: $required"
    }
}
if (-not (Test-Path "${env:ProgramFiles(x86)}\Windows Kits\10\Include\10.0.18362.0")) {
    throw "Windows SDK 10.0.18362.0 is not installed."
}

"VS_PATH=$vsPath" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8
