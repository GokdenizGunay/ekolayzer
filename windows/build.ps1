# Ekolayzer (Windows) derleme betiği: ses motoru (APO, C++/MSVC) + uygulama/kurulum (C#, .NET Framework 4.8)
# Kullanım: powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'build'
New-Item -ItemType Directory -Force $out | Out-Null

# --- MSVC ortamı
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio C++ araçları bulunamadı.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'

function Invoke-Vc([string]$cmd) {
    cmd /c "`"$vcvars`" >nul && $cmd"
    if ($LASTEXITCODE -ne 0) { throw "Derleme başarısız: $cmd" }
}

# --- Ses motoru (APO)
Push-Location (Join-Path $root 'apo')
try {
    Invoke-Vc ("cl /nologo /O2 /fp:fast /GS /guard:cf /MT /EHsc /std:c++17 /utf-8 /W4 /wd4100 /LD EkolayzerApo.cpp " +
               "/Fo`"$out\EkolayzerApo.obj`" /Fe`"$out\EkolayzerApo.dll`" " +
               "/link /DEF:EkolayzerApo.def /GUARD:CF /DYNAMICBASE /NXCOMPAT /OPT:REF /OPT:ICF ole32.lib uuid.lib advapi32.lib")
} finally { Pop-Location }
Remove-Item "$out\EkolayzerApo.exp", "$out\EkolayzerApo.lib", "$out\EkolayzerApo.obj" -ErrorAction SilentlyContinue
Write-Host "APO: $out\EkolayzerApo.dll"

# --- Uygulama + kurulum (tek dosya; APO DLL'i içine gömülür)
$version = [regex]::Match((Get-Content (Join-Path $root 'app\Core.cs') -Raw), 'Version = "([^"]+)"').Groups[1].Value
dotnet build (Join-Path $root 'app\Ekolayzer.csproj') -c Release -nologo -v q "-p:EkzVersion=$version" -o (Join-Path $out 'app')
if ($LASTEXITCODE -ne 0) { throw 'Uygulama derlenemedi.' }
$setup = Join-Path $root "ekolayzer-setup-$version.exe"
Copy-Item (Join-Path $out 'app\Ekolayzer.exe') $setup -Force
$sha = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower()
Write-Host "Kurulum: $setup"
Write-Host "SHA-256: $sha"
