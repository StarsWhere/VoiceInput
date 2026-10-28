#requires -Version 7.0
[CmdletBinding()]
param([string]$GameRoot = $env:BEAT_SABER_DIR)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build_local_paths.ps1')
$changes = @(& git -C $RepoRoot status --porcelain)
if ($LASTEXITCODE -ne 0 -or $changes.Count) { throw 'Commit changes and initialize submodules before releasing.' }
$revision = (& git -C $RepoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the source revision.' }
$manifests = @(Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -File -Filter '*manifest*.json' | Where-Object { $_.FullName -notmatch '[\\/](Tests|RuntimeChecks|bin|obj)[\\/]' } | ForEach-Object {
    $metadata = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
    if ($metadata.id -notmatch 'Smoke|Validation') { $metadata }
})
if ($manifests.Count -ne 1) { throw 'This repository must contain exactly one mod manifest.' }
$mod = $manifests[0]
$releaseName = $mod.id + '-' + $mod.version + '-bs1.45.2-win-x64'
$releaseRoot = Join-Path $RepoRoot ('releases/' + $releaseName)
if (Test-Path -LiteralPath $releaseRoot) { throw "Release already exists: $releaseRoot" }
$buildRoot = Join-Path $RepoRoot ('.tmp/release-' + [Guid]::NewGuid().ToString('N'))
$output = Join-Path $buildRoot 'output'
$package = Join-Path $buildRoot 'package'
[IO.Directory]::CreateDirectory($output) | Out-Null
& (Join-Path $PSScriptRoot 'build.ps1') -GameRoot $GameRoot -BuildDirectory (Join-Path $buildRoot 'build') -OutputDirectory $output
$dlls = @(Get-ChildItem -LiteralPath (Join-Path $output 'Plugins') -File -Filter '*.dll')
if ($dlls.Count -ne 1 -or $dlls[0].Name -ne ($mod.id+'.dll')) { throw 'A standalone package may contain only its own plugin.' }
$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($dlls[0].FullName))
$resource = @($assembly.GetManifestResourceNames() | Where-Object { $_ -like '*manifest.json' })
if ($resource.Count -ne 1) { throw 'Missing embedded manifest.' }
$reader = [IO.StreamReader]::new($assembly.GetManifestResourceStream($resource[0]))
try { $embedded = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
if ($embedded.id -ne $mod.id -or $embedded.version -ne $mod.version -or $embedded.gameVersion -ne '1.45.2') { throw 'Plugin metadata does not match its source.' }

function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Copy-File([string]$From, [string]$To) {
    [IO.Directory]::CreateDirectory((Split-Path -Parent $To)) | Out-Null
    Copy-Item -LiteralPath $From -Destination $To
}
Copy-File $dlls[0].FullName (Join-Path $package ('Plugins/'+$mod.id+'.dll'))
$allowedLibraries = @('SongPackageIO.dll','NJsonSchema.dll','NJsonSchema.Annotations.dll','Namotion.Reflection.dll','Newtonsoft.Json.dll','TagLibSharp.dll','System.Text.Json.dll','System.Text.Encodings.Web.dll','System.Buffers.dll','System.Threading.Tasks.Extensions.dll','Microsoft.Bcl.AsyncInterfaces.dll','System.IO.Pipelines.dll','System.Memory.dll','System.Numerics.Vectors.dll','System.Runtime.CompilerServices.Unsafe.dll','System.ValueTuple.dll')
if (Test-Path -LiteralPath (Join-Path $output 'Libs')) {
    foreach ($library in Get-ChildItem -LiteralPath (Join-Path $output 'Libs') -File -Filter '*.dll') {
        if ($library.Name -notin $allowedLibraries) { throw "Unexpected library: $($library.Name)" }
        Copy-File $library.FullName (Join-Path $package ('Libs/'+$library.Name))
    }
}
foreach ($name in @('LICENSE','THIRD-PARTY-NOTICES.txt')) { Copy-File (Join-Path $RepoRoot $name) (Join-Path $package $name) }
if (Test-Path -LiteralPath (Join-Path $RepoRoot 'licenses')) { Copy-Item -LiteralPath (Join-Path $RepoRoot 'licenses') -Destination $package -Recurse }
$example = Join-Path $output ($mod.id+'.example.json')
if (Test-Path -LiteralPath $example) { Copy-File $example (Join-Path $package ([IO.Path]::GetFileName($example))) }
$dependencies = @(& git -C $RepoRoot submodule status)
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source dependencies.' }
[ordered]@{id=$mod.id;version=$mod.version;gameBuild='1.45.2_28848';source=$mod.links.'project-source';commit=$revision;sourceDependencies=$dependencies;dependsOn=$mod.dependsOn} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $package 'release.json') -Encoding utf8NoBOM
$packageFiles = @(Get-ChildItem -LiteralPath $package -Recurse -File | Sort-Object FullName)
$packageSums = @($packageFiles | ForEach-Object { (Hash $_.FullName)+'  '+[IO.Path]::GetRelativePath($package,$_.FullName).Replace([char]92,[char]47) })
[IO.File]::WriteAllLines((Join-Path $package 'SHA256SUMS.txt'),$packageSums,[Text.UTF8Encoding]::new($false))
[IO.Directory]::CreateDirectory($releaseRoot) | Out-Null
$archive = Join-Path $releaseRoot ($releaseName+'.zip')
[IO.Compression.ZipFile]::CreateFromDirectory($package,$archive,[IO.Compression.CompressionLevel]::Optimal,$false)
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    foreach ($entry in $zip.Entries) {
        if (!$entry.Name) { continue }
        $stream=$entry.Open(); $sha=[Security.Cryptography.SHA256]::Create()
        try { $actual=[Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant() } finally { $sha.Dispose(); $stream.Dispose() }
        if ($actual -ne (Hash (Join-Path $package $entry.FullName))) { throw "Archive mismatch: $($entry.FullName)" }
    }
} finally { $zip.Dispose() }
# Include the pinned submodule sources, so the source archive can also build offline.
$sourceFiles = @(& git -C $RepoRoot ls-files --recurse-submodules)
if ($LASTEXITCODE -ne 0) { throw 'Source enumeration failed.' }
$sourceZip = [IO.Compression.ZipFile]::Open((Join-Path $releaseRoot ($releaseName+'-source.zip')),[IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($relative in $sourceFiles) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($sourceZip,(Join-Path $RepoRoot $relative),$relative,[IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $sourceZip.Dispose() }
if ($mod.id -eq 'BeatSage') {
    $thirdPartySource = Join-Path $RepoRoot '.tmp/publish-inputs/TagLibSharp-2.3.0-source.zip'
    if (!(Test-Path -LiteralPath $thirdPartySource)) {
        [IO.Directory]::CreateDirectory((Split-Path -Parent $thirdPartySource)) | Out-Null
        Invoke-WebRequest -Uri 'https://codeload.github.com/mono/taglib-sharp/zip/b5ae84f2e84087bf160bb0471420200dd2b5d809' -OutFile $thirdPartySource -TimeoutSec 120
    }
    if ((Hash $thirdPartySource) -ne 'ab6fab7e22c7423e428f591d7525a38ca283d217a49f9772eb9a29cd682c175d') { throw 'TagLibSharp source checksum mismatch.' }
    $runtime = Join-Path $env:USERPROFILE '.nuget/packages/taglibsharp/2.3.0/lib/net462/TagLibSharp.dll'
    if ((Hash $runtime) -ne (Hash (Join-Path $package 'Libs/TagLibSharp.dll'))) { throw 'TagLibSharp runtime must be the unmodified NuGet 2.3.0 library.' }
    Copy-File $thirdPartySource (Join-Path $releaseRoot 'TagLibSharp-2.3.0-source.zip')
}
$checksums = @(Get-ChildItem -LiteralPath $releaseRoot -File | Sort-Object Name | ForEach-Object { (Hash $_.FullName)+'  '+$_.Name })
[IO.File]::WriteAllLines((Join-Path $releaseRoot 'SHA256SUMS.txt'),$checksums,[Text.UTF8Encoding]::new($false))
Write-Output ('Validated standalone plugin and package: '+$releaseRoot)
