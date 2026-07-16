# OctoConverter MSI 설치 프로그램 빌드 스크립트
# 사용법: PowerShell에서 .\build-installer.ps1
# 필요 도구: .NET SDK, WiX (dotnet tool install --global wix)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$publishDir = Join-Path $root "bin\publish\win-x64"

Write-Host "[1/2] 자가 포함 단일 파일 게시 중..." -ForegroundColor Cyan
dotnet publish (Join-Path $root "OctoConverter.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishReadyToRun=true `
    -p:DebugType=none `
    -o $publishDir -v m -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 실패" }

Write-Host "[2/3] MSI 빌드 중..." -ForegroundColor Cyan
$version = (Get-Item (Join-Path $publishDir "OctoConverter.exe")).VersionInfo.FileVersion
$outDir = Join-Path $PSScriptRoot "output"
New-Item -ItemType Directory -Force $outDir | Out-Null
$msi = Join-Path $outDir "OctoConverterSetup-$version.msi"

wix build (Join-Path $PSScriptRoot "OctoConverter.wxs") -arch x64 -d "ProjectRoot=$root" -o $msi
if ($LASTEXITCODE -ne 0) { throw "wix build 실패 (MSI)" }

Write-Host "[3/3] setup.exe 빌드 중..." -ForegroundColor Cyan
$setupExe = Join-Path $outDir "OctoConverterSetup-$version.exe"
wix build (Join-Path $PSScriptRoot "Bundle.wxs") `
    -ext WixToolset.BootstrapperApplications.wixext `
    -d "ProjectRoot=$root" -d "MsiPath=$msi" -o $setupExe
if ($LASTEXITCODE -ne 0) { throw "wix build 실패 (Bundle)" }

Write-Host "완료:" -ForegroundColor Green
Write-Host "  $msi ($([math]::Round((Get-Item $msi).Length/1MB, 1)) MB)"
Write-Host "  $setupExe ($([math]::Round((Get-Item $setupExe).Length/1MB, 1)) MB)  <- 배포용 권장"
