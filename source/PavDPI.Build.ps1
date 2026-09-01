[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$pavRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$pavBuildRoot = Join-Path $pavRoot 'build'
$pavPayloadRoot = Join-Path $pavBuildRoot 'payload'
$pavReleaseRoot = Join-Path $pavRoot 'release'
$pavCsc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$pavUtf8NoBom = New-Object System.Text.UTF8Encoding($false)

if (-not (Test-Path -LiteralPath $pavCsc -PathType Leaf)) {
    throw "64-bit .NET Framework compiler was not found: $pavCsc"
}

$pavAllowedPrefix = $pavRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
foreach ($pavPath in @($pavBuildRoot, $pavReleaseRoot)) {
    $pavResolved = [System.IO.Path]::GetFullPath($pavPath)
    if (-not $pavResolved.StartsWith($pavAllowedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe build path: $pavResolved"
    }
}

if (Test-Path -LiteralPath $pavBuildRoot) { Remove-Item -LiteralPath $pavBuildRoot -Recurse -Force }
if (Test-Path -LiteralPath $pavReleaseRoot) { Remove-Item -LiteralPath $pavReleaseRoot -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $pavPayloadRoot 'engine') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $pavPayloadRoot 'config') -Force | Out-Null
New-Item -ItemType Directory -Path $pavReleaseRoot -Force | Out-Null

$pavIcon = Join-Path $pavRoot 'assets\PavDPI.ico'
$pavServiceSource = Join-Path $pavRoot 'src\PavDPI.Service\PavDPI.Service.cs'
$pavServiceExe = Join-Path $pavPayloadRoot 'PavDPI Service.exe'
& $pavCsc /nologo /target:exe /platform:x64 /optimize+ /reference:System.ServiceProcess.dll `
    "/win32icon:$pavIcon" "/out:$pavServiceExe" $pavServiceSource
if ($LASTEXITCODE -ne 0) { throw "PavDPI Service compilation failed: $LASTEXITCODE" }

$pavThirdParty = Join-Path $pavRoot 'third_party\engine'
$pavEngineTarget = Join-Path $pavPayloadRoot 'engine\PavDPI Engine.exe'
Copy-Item -LiteralPath (Join-Path $pavThirdParty 'PavDPI Engine.exe') -Destination $pavEngineTarget -Force
Copy-Item -LiteralPath (Join-Path $pavThirdParty 'WinDivert.dll') -Destination (Join-Path $pavPayloadRoot 'engine\WinDivert.dll') -Force
Copy-Item -LiteralPath (Join-Path $pavThirdParty 'WinDivert64.sys') -Destination (Join-Path $pavPayloadRoot 'engine\WinDivert64.sys') -Force
$pavNoticesTarget = Join-Path $pavPayloadRoot 'THIRD-PARTY-NOTICES.txt'
Copy-Item -LiteralPath (Join-Path $pavThirdParty '.notices\THIRD-PARTY-NOTICES.txt') -Destination $pavNoticesTarget -Force
$pavNoticesItem = Get-Item -LiteralPath $pavNoticesTarget -Force
$pavNoticesItem.Attributes = ($pavNoticesItem.Attributes -bor [System.IO.FileAttributes]::Hidden)

$pavExpectedThirdPartyHashes = @{
    'engine\PavDPI Engine.exe' = '7ACDE0DC3D40E448B70B08D661F633A61DDC94E9292EE3DCF447C377162A455C'
    'engine\WinDivert.dll' = '6110BFA44667405179C3E15E12AF1B62037E447ED59B054B19042032995E6C7E'
    'engine\WinDivert64.sys' = 'E69B5BA3F0CD6CFB2983E442636E7F0B342B61B15264B0328317D4559C82CF50'
}
foreach ($pavRelativePath in $pavExpectedThirdPartyHashes.Keys) {
    $pavFullPath = Join-Path $pavPayloadRoot $pavRelativePath
    if (-not (Test-Path -LiteralPath $pavFullPath -PathType Leaf)) { throw "Missing third-party file: $pavRelativePath" }
    $pavHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $pavFullPath).Hash
    if ($pavHash -ne $pavExpectedThirdPartyHashes[$pavRelativePath]) {
        throw "Unexpected third-party hash: $pavRelativePath ($pavHash)"
    }
}

$pavDefaultArguments = '-f 2 -e 2 --reverse-frag --max-payload --set-ttl 7 --blacklist "..\config\targets.txt" --dns-addr 77.88.8.8 --dns-port 1253 --dnsv6-addr 2a02:6b8::feed:0ff --dnsv6-port 1253'
[System.IO.File]::WriteAllText((Join-Path $pavPayloadRoot 'config\profile.args'), $pavDefaultArguments + "`r`n", $pavUtf8NoBom)
[System.IO.File]::WriteAllText((Join-Path $pavPayloadRoot 'config\profile.name'), "Otomatik`r`n", $pavUtf8NoBom)
$pavTargets = @(
    'discord.com',
    'discord.gg',
    'discordapp.com',
    'discordapp.net',
    'discordcdn.com',
    'roblox.com',
    'rbxcdn.com'
)
[System.IO.File]::WriteAllLines((Join-Path $pavPayloadRoot 'config\targets.txt'), $pavTargets, $pavUtf8NoBom)

& $pavServiceExe --self-test
if ($LASTEXITCODE -ne 0) { throw "PavDPI Service self-test failed: $LASTEXITCODE" }

$pavManifestLines = foreach ($pavFile in (Get-ChildItem -LiteralPath $pavPayloadRoot -Recurse -File -Force | Sort-Object FullName)) {
    $pavRelative = $pavFile.FullName.Substring($pavPayloadRoot.Length).TrimStart('\').Replace('\', '/')
    $pavHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $pavFile.FullName).Hash
    "$pavHash  $pavRelative"
}
[System.IO.File]::WriteAllLines((Join-Path $pavPayloadRoot 'manifest.sha256'), $pavManifestLines, $pavUtf8NoBom)

$pavPayloadZip = Join-Path $pavBuildRoot 'PavDPI.Payload.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($pavPayloadRoot, $pavPayloadZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$pavZip = [System.IO.Compression.ZipFile]::OpenRead($pavPayloadZip)
try {
    $pavEntries = $pavZip.Entries | ForEach-Object { $_.FullName.Replace('/', '\') }
    foreach ($pavRequired in @('PavDPI Service.exe','engine\PavDPI Engine.exe','engine\WinDivert.dll','engine\WinDivert64.sys','config\targets.txt','THIRD-PARTY-NOTICES.txt','manifest.sha256')) {
        if ($pavEntries -notcontains $pavRequired) { throw "Payload ZIP entry missing: $pavRequired" }
    }
}
finally { $pavZip.Dispose() }

$pavInstallerSources = Get-ChildItem -LiteralPath (Join-Path $pavRoot 'src\PavDPI.Installer') -Filter '*.cs' | Select-Object -ExpandProperty FullName
$pavInstallerExe = Join-Path $pavReleaseRoot 'PavDPI.exe'
$pavInstallerManifest = Join-Path $pavRoot 'src\PavDPI.Installer\PavDPI.manifest'
$pavResourceOption = "/resource:$pavPayloadZip,PavDPI.Payload.zip"
& $pavCsc /nologo /target:winexe /platform:x64 /optimize+ `
    /reference:System.Windows.Forms.dll `
    /reference:System.Drawing.dll `
    /reference:System.ServiceProcess.dll `
    /reference:System.IO.Compression.dll `
    /reference:System.IO.Compression.FileSystem.dll `
    "/win32icon:$pavIcon" `
    "/win32manifest:$pavInstallerManifest" `
    $pavResourceOption `
    "/out:$pavInstallerExe" `
    $pavInstallerSources
if ($LASTEXITCODE -ne 0) { throw "PavDPI installer compilation failed: $LASTEXITCODE" }

$pavReleaseHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $pavInstallerExe).Hash
[System.IO.File]::WriteAllText((Join-Path $pavReleaseRoot 'SHA256SUMS.txt'), "$pavReleaseHash  PavDPI.exe`r`n", $pavUtf8NoBom)
Write-Output "Build complete: $pavInstallerExe"
Write-Output "SHA256: $pavReleaseHash"
