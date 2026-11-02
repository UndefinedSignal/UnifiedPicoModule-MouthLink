param([switch]$SkipBuild, [string]$SdkSource = $env:VRCFT_SDK_SOURCE)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release -SdkSource $SdkSource }
$metadata = Get-Content (Join-Path $root 'module.json') -Raw | ConvertFrom-Json
# VRCFT 5.4.5 binds this string directly to HyperlinkButton.NavigateUri.
# Both an empty string and null crashed the registry page during headset acceptance.
$modulePageUri = $null
if ([string]::IsNullOrWhiteSpace($metadata.ModulePageUrl) -or
    -not [Uri]::TryCreate($metadata.ModulePageUrl, [UriKind]::Absolute, [ref]$modulePageUri) -or
    $modulePageUri.Scheme -notin @('http', 'https', 'file')) {
    throw 'ModulePageUrl must be a non-empty absolute http/https/file URI; VRCFT 5.4.5 crashes on empty/null values. Use https://example.com/ only as a local preview placeholder.'
}
$dll = Join-Path $root 'bin/Release/net10.0/UnifiedPicoModule.dll'
if (-not (Test-Path $dll)) { throw 'Release module DLL missing. Build first.' }
$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
# Never overwrite earlier packages or use a broad recursive deletion.
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$stage = Join-Path $artifacts "stage-$stamp"
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item $dll $stage
# ONNX Runtime for the built-in MouthLink model (VRCFaceTracking 6 no longer ships one). Official
# Microsoft CPU build, pinned by version and SHA-256; the module loads MouthLink/onnxruntime.dll.
$ortVersion = '1.23.2'
$ortZipSha = '0B38DF9AF21834E41E73D602D90DB5CB06DBD1CA618948B8F1D66D607AC9F3CD'
$ortDllSha = 'DEC964AB1EE36CC9B0AE247D13B376627992FC57DEC0454354017AB8FD84F1EA'
$ortCache = Join-Path $root ".sdk/onnxruntime-win-x64-$ortVersion.zip"
New-Item -ItemType Directory -Force -Path (Split-Path $ortCache) | Out-Null
if (-not (Test-Path $ortCache)) {
    Invoke-WebRequest -UseBasicParsing -OutFile $ortCache `
        -Uri "https://github.com/microsoft/onnxruntime/releases/download/v$ortVersion/onnxruntime-win-x64-$ortVersion.zip"
}
if ((Get-FileHash $ortCache -Algorithm SHA256).Hash -ne $ortZipSha) {
    Remove-Item $ortCache
    throw 'ONNX Runtime archive SHA-256 mismatch; refusing to package it.'
}
$ortStage = Join-Path $stage 'MouthLink'
New-Item -ItemType Directory -Path $ortStage | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$ortZip = [IO.Compression.ZipFile]::OpenRead($ortCache)
try {
    foreach ($pair in @(@("onnxruntime-win-x64-$ortVersion/lib/onnxruntime.dll", 'onnxruntime.dll'),
                        @("onnxruntime-win-x64-$ortVersion/LICENSE", 'onnxruntime-LICENSE.txt'),
                        @("onnxruntime-win-x64-$ortVersion/ThirdPartyNotices.txt", 'onnxruntime-ThirdPartyNotices.txt'))) {
        $entry = $ortZip.GetEntry($pair[0])
        if ($null -eq $entry) { throw "Missing $($pair[0]) in the ONNX Runtime archive." }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $ortStage $pair[1]), $true)
    }
} finally { $ortZip.Dispose() }
if ((Get-FileHash (Join-Path $ortStage 'onnxruntime.dll') -Algorithm SHA256).Hash -ne $ortDllSha) {
    throw 'onnxruntime.dll SHA-256 mismatch.'
}
foreach ($file in @('module.json','README.md','README_zh.md','CHANGELOG.md','CHANGELOG_zh.md','CONTRIBUTING.md','CONTRIBUTING_zh.md','THIRD_PARTY_NOTICES.md','THIRD_PARTY_NOTICES_zh.md','LICENSE')) {
    Copy-Item (Join-Path $root $file) $stage
}
# Do not recursively copy docs: agent-only records can contain personal paths/history.
$brandingStage = Join-Path $stage 'assets/branding'
New-Item -ItemType Directory -Path $brandingStage -Force | Out-Null
# Explicit release assets only; never package generated design drafts.
foreach ($asset in @('logo-dark.svg','logo-light.svg','module-icon.svg','logo-dark.png','logo-light.png','module-icon.png')) {
    Copy-Item (Join-Path $root "assets/branding/$asset") $brandingStage
}
$publicDocs = @('configuration.md','configuration_zh.md','postprocessing.md','postprocessing_zh.md','protocol.md','protocol_zh.md','development.md','development_zh.md','privacy.md','privacy_zh.md')
$docsStage = Join-Path $stage 'docs'
New-Item -ItemType Directory -Path $docsStage | Out-Null
foreach ($document in $publicDocs) { Copy-Item (Join-Path $root "docs/$document") $docsStage }
$toolDocs = Join-Path $stage 'tools/ConfigTool'
New-Item -ItemType Directory -Path $toolDocs | Out-Null
foreach ($toolDoc in @('README.md','README_zh.md')) { Copy-Item (Join-Path $PSScriptRoot "ConfigTool/$toolDoc") $toolDocs }
# A developer's runtime JSON can contain IPs and migration paths. Always export clean
# model defaults, never package that file or personal migration reports.
$configTool = Join-Path $PSScriptRoot 'ConfigTool/bin/Release/net10.0/UnifiedPicoConfigTool.dll'
if (-not (Test-Path $configTool)) { throw 'Release configuration tool missing. Build first.' }
& dotnet $configTool defaults (Join-Path $stage 'UnifiedPicoModule.json')
if ($LASTEXITCODE -ne 0) { throw 'Clean default configuration export failed.' }
$configStage = Join-Path $stage 'config'
New-Item -ItemType Directory -Path $configStage | Out-Null
Copy-Item (Join-Path $stage 'UnifiedPicoModule.json') (Join-Path $configStage 'UnifiedPicoModule.example.json')
Copy-Item (Join-Path $root '.sdk/sdk-provenance.json') $stage
$zip = Join-Path $artifacts "UnifiedPicoModule-$($metadata.Version)-$stamp.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
"$hash  $([IO.Path]::GetFileName($zip))" | Set-Content "$zip.sha256" -Encoding utf8
Write-Host "Package: $zip"
Write-Host "SHA256: $hash"
Write-Host "Package contains no VRCFaceTracking SDK/Core DLLs; MouthLink/onnxruntime.dll is ONNX Runtime $ortVersion (Microsoft). No module installed or published."
