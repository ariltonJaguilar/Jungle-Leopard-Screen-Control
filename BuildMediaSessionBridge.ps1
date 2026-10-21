$ErrorActionPreference = 'Stop'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Visual Studio Build Tools com o compilador C++ não foram encontrados.'
}

$vsRoot = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsRoot) { throw 'O compilador C++ x64 do Visual Studio não está instalado.' }

$vcvars = Join-Path $vsRoot 'VC\Auxiliary\Build\vcvars64.bat'
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$sdkIncludeRoot = Join-Path $sdkRoot 'Include'
$sdkVersion = Get-ChildItem -LiteralPath $sdkIncludeRoot -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'cppwinrt\winrt\Windows.Media.Control.h') } |
    Sort-Object { [version]$_.Name } -Descending |
    Select-Object -First 1
if (-not $sdkVersion) { throw 'Os headers C++/WinRT do Windows SDK não foram encontrados.' }

$outputDirectory = Join-Path $PSScriptRoot '.native-build'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$source = Join-Path $PSScriptRoot 'MediaSessionBridge.cpp'
$output = Join-Path $PSScriptRoot 'MediaSessionBridge.dll'
$object = Join-Path $outputDirectory 'MediaSessionBridge.obj'
$sdkInclude = Join-Path $sdkRoot "Include\$($sdkVersion.Name)\cppwinrt"
$importLibrary = Join-Path $outputDirectory 'MediaSessionBridge.lib'
$command = '"{0}" >nul && cl.exe /nologo /std:c++20 /EHsc /MT /O2 /LD /DWIN32_LEAN_AND_MEAN /I"{1}" /Fo"{2}" /Fe"{3}" "{4}" windowsapp.lib /link /IMPLIB:"{5}"' -f $vcvars, $sdkInclude, $object, $output, $source, $importLibrary

& $env:ComSpec /d /s /c $command
if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar MediaSessionBridge.dll (código $LASTEXITCODE)." }
Write-Output "Biblioteca gerada: $output"
