# OctoConverter 설치 파일 빌드 스크립트
# 사용법: powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 [-SkipWebsite]
# 결과물: installer\output\OctoConverter-Setup-<버전>.exe
# 빌드가 끝나면 update-website.ps1을 호출해 octo-brain.com 배포 섹션(웹사이트 릴리스 + store.html)을 자동 갱신한다.
# -SkipWebsite 를 주면 웹사이트 갱신을 건너뛴다 (로컬 테스트 빌드용).
# Git 최신 커밋 기준 원클릭 릴리즈(프로젝트 GitHub 릴리스 포함)는 release.bat / installer\release.ps1 을 사용한다.

param(
    [switch]$SkipWebsite,
    [switch]$RequireSignedRelease,
    [string]$CoAuthor = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$csproj = Join-Path $root "OctoConverter.csproj"
$publishDir = Join-Path $root "bin\Release\Publish"

# Signing configuration is external to this repository. Never embed credentials.
$signingEnabled = $env:OCTO_CODESIGN -eq '1'
if (($RequireSignedRelease -or -not $SkipWebsite) -and -not $signingEnabled) {
    throw 'Signed releases require OCTO_CODESIGN=1.'
}
$signProvider = if ($env:OCTO_SIGN_PROVIDER) { $env:OCTO_SIGN_PROVIDER } else { 'ArtifactSigning' }
$signToolPath = $env:OCTO_SIGNTOOL
if ($signingEnabled) {
    if ($signProvider -notin @('ArtifactSigning', 'CertificateStore')) { throw 'Unsupported OCTO_SIGN_PROVIDER.' }
    if (-not $signToolPath) {
        $signToolPath = Get-ChildItem -LiteralPath "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter signtool.exe -Recurse |
            Where-Object { $_.Directory.Name -eq 'x64' } | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $signToolPath -or -not [IO.Path]::IsPathRooted($signToolPath) -or -not (Test-Path -LiteralPath $signToolPath -PathType Leaf)) {
        throw 'Set OCTO_SIGNTOOL to the absolute Windows SDK signtool.exe path.'
    }
    if ($signProvider -eq 'ArtifactSigning') {
        foreach ($externalPath in @($env:OCTO_SIGN_DLIB, $env:OCTO_SIGN_METADATA)) {
            if (-not $externalPath -or -not [IO.Path]::IsPathRooted($externalPath) -or -not (Test-Path -LiteralPath $externalPath -PathType Leaf)) {
                throw 'Set OCTO_SIGN_DLIB and OCTO_SIGN_METADATA to existing absolute external file paths.'
            }
        }
    } elseif ($env:OCTO_CERT_THUMBPRINT -notmatch '^(?i:[0-9a-f]{40})$') {
        throw 'Set OCTO_CERT_THUMBPRINT to the signing certificate SHA-1 thumbprint.'
    }
}

# 1) csproj에서 버전 읽기
$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { $version = "1.0.0" }
$version = "$version".Trim()
Write-Host "== OctoConverter v$version 설치 파일 빌드 ==" -ForegroundColor Cyan

# 2) 게시 (자체 포함 - 대상 PC에 .NET 설치 불필요)
Write-Host "[1/2] dotnet publish..." -ForegroundColor Yellow
$publishDir = [IO.Path]::GetFullPath($publishDir)
$expectedPublishDir = [IO.Path]::GetFullPath((Join-Path $root 'bin\Release\Publish'))
if ($publishDir -ne $expectedPublishDir -or -not $publishDir.StartsWith([IO.Path]::GetFullPath($root) + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Publish cleanup path is outside the expected project output directory.'
}
if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
dotnet publish $csproj -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=none -o $publishDir -v q -nologo
if ($LASTEXITCODE -ne 0) { throw "게시 실패 (exit $LASTEXITCODE)" }
& (Join-Path $PSScriptRoot 'check-runtime-notices.ps1') -PublishDirectory $publishDir

if ($signingEnabled) {
    # Only sign our executable; runtime DLLs and external conversion tools retain their vendor signatures.
    & (Join-Path $PSScriptRoot 'sign-file.ps1') -Path (Join-Path $publishDir 'OctoConverter.exe') `
        -Provider $signProvider -SignTool $signToolPath -Thumbprint $env:OCTO_CERT_THUMBPRINT `
        -Dlib $env:OCTO_SIGN_DLIB -Metadata $env:OCTO_SIGN_METADATA
} else {
    Write-Warning 'Unsigned local build. Public releases must use -RequireSignedRelease.'
}

# 3) Inno Setup 컴파일
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6을 찾을 수 없습니다. winget install -e --id JRSoftware.InnoSetup 으로 설치하세요." }

Write-Host "[2/2] Inno Setup 컴파일..." -ForegroundColor Yellow
$isccArgs = @("/DAppVersion=$version")
if ($signingEnabled) {
    # Keep the same PowerShell host and module environment when Inno signs its uninstaller.
    $signingShell = Join-Path $PSHOME $(if ($PSVersionTable.PSEdition -eq 'Core') { 'pwsh.exe' } else { 'powershell.exe' })
    $signFile = Join-Path $PSScriptRoot 'sign-file.ps1'
    $signCommand = '$q' + $signingShell + '$q -NoProfile -ExecutionPolicy Bypass -File $q' + $signFile + '$q -Path $f -Provider $q' + $signProvider + '$q -SignTool $q' + $signToolPath + '$q'
    if ($signProvider -eq 'CertificateStore') { $signCommand += ' -Thumbprint $q' + $env:OCTO_CERT_THUMBPRINT + '$q' }
    else { $signCommand += ' -Dlib $q' + $env:OCTO_SIGN_DLIB + '$q -Metadata $q' + $env:OCTO_SIGN_METADATA + '$q' }
    $isccArgs += '/DSignedBuild'
    $isccArgs += "/SOctoSign=$signCommand"
}
$isccArgs += (Join-Path $PSScriptRoot 'OctoConverter.iss')
& $iscc @isccArgs | Select-Object -Last 10
if ($LASTEXITCODE -ne 0) { throw "설치 파일 컴파일 실패 (exit $LASTEXITCODE)" }

$setup = Join-Path $PSScriptRoot "output\OctoConverter-Setup-$version.exe"
if (Test-Path $setup) {
    if ($signingEnabled) {
        & (Join-Path $PSScriptRoot 'verify-signature.ps1') -Path $setup -SignTool $signToolPath
    }
    $checksum = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($setup) + "`n"
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'output\SHA256SUMS.txt'), $checksum, [Text.UTF8Encoding]::new($false))
    $mb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
    Write-Host "완료: $setup ($mb MB)" -ForegroundColor Green
} else {
    throw "설치 파일이 생성되지 않았습니다."
}

# 4) octo-brain.com 배포 섹션 갱신 (실패해도 설치 파일 빌드 자체는 성공으로 둔다)
if (-not $SkipWebsite) {
    Write-Host "[3/3] octo-brain.com 배포 갱신..." -ForegroundColor Yellow
    try {
        & (Join-Path $PSScriptRoot "update-website.ps1") -AppName "OctoConverter" -Version $version -InstallerPath $setup -CoAuthor $CoAuthor
    } catch {
        Write-Host "경고: 웹사이트 갱신 실패 - $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "      수동 실행: powershell -ExecutionPolicy Bypass -File installer\update-website.ps1 -AppName OctoConverter -Version $version -InstallerPath `"$setup`"" -ForegroundColor Red
    }
}
