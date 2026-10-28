#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$GameRoot = $env:BEAT_SABER_DIR,
    [string]$BuildDirectory = '',
    [string]$OutputDirectory = '',
    [string]$DotNetPath = 'C:/Program Files/dotnet/dotnet.exe'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build_local_paths.ps1')
if ((Get-Content -LiteralPath (Join-Path $GameRoot 'BeatSaberVersion.txt') -Raw).Trim() -ne '1.45.2_28848') { throw 'VoiceInput targets Beat Saber 1.45.2_28848.' }
if (!$BuildDirectory) { $BuildDirectory = Join-Path $RepoRoot '.tmp/voice-input/plugin-build' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $RepoRoot '.tmp/voice-input/package' }
$BuildDirectory = (Resolve-RepoBuildPath $BuildDirectory)
$sharedRoot = Join-Path $RepoRoot 'vendor/LocalModUI'
$sharedBuild = Join-Path $sharedRoot 'scripts/build.ps1'
if (!(Test-Path -LiteralPath $sharedBuild -PathType Leaf)) { throw "LocalModUI submodule is missing: $sharedBuild" }
& $sharedBuild -GameRoot $GameRoot -BuildDirectory (Join-Path $sharedRoot '.tmp/build') -OutputDirectory (Join-Path $sharedRoot '.tmp/package') | Out-Null

$OutputDirectory = (Resolve-RepoBuildPath $OutputDirectory)
if (Test-Path -LiteralPath (Join-Path $OutputDirectory 'Plugins') -PathType Container) {
    $otherPlugins = @(Get-ChildItem -LiteralPath (Join-Path $OutputDirectory 'Plugins') -File -Filter '*.dll' | Where-Object Name -ne 'VoiceInput.dll')
    if ($otherPlugins.Count) { throw 'OutputDirectory contains another plugin DLL; use a clean package directory.' }
}
$source = Join-Path $PSScriptRoot 'voice_input'
$generator = Join-Path $sharedRoot 'scripts/contract_generator/ContractGenerator.csproj'
$generatorOutput = Join-Path $BuildDirectory 'generator/bin'
$generated = Join-Path $BuildDirectory 'generated'
$bin = Join-Path $BuildDirectory 'bin'
$obj = Join-Path $BuildDirectory 'obj'
foreach ($directory in @($generatorOutput, $generated, $bin, $obj)) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
& $DotNetPath build $generator --configuration Release ('-p:OutputPath=' + $generatorOutput + '/') ('-p:BaseIntermediateOutputPath=' + (Join-Path $BuildDirectory 'generator/obj') + '/')
if ($LASTEXITCODE -ne 0) { throw 'Shared contract generator compilation failed.' }
& $DotNetPath (Join-Path $generatorOutput 'BeatSage.ContractGenerator.dll') --schema (Join-Path $source 'Contracts/contracts.schema.json') --endpoints (Join-Path $source 'Contracts/endpoints.json') --output $generated --namespace VoiceInput.Contracts --root-name ContractDocument
if ($LASTEXITCODE -ne 0) { throw 'VoiceInput contract generation failed.' }
$referencePath = Join-Path $env:USERPROFILE '.nuget/packages/microsoft.netframework.referenceassemblies.net472/1.0.3/build/.NETFramework/v4.7.2'
& $DotNetPath build (Join-Path $source 'VoiceInput.csproj') --configuration Release ('-p:GameRoot=' + $GameRoot) ('-p:OutputPath=' + $bin + '/') ('-p:BaseIntermediateOutputPath=' + $obj + '/') ('-p:FrameworkPathOverride=' + $referencePath) ('-p:ContractGeneratedDirectory=' + $generated)
if ($LASTEXITCODE -ne 0) { throw 'VoiceInput compilation failed.' }
$pluginOutput = Join-Path $OutputDirectory 'Plugins'
$libOutput = Join-Path $OutputDirectory 'Libs'
foreach ($directory in @($pluginOutput, $libOutput)) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
foreach ($name in @('VoiceInput.dll', 'VoiceInput.pdb')) { Copy-Item -LiteralPath (Join-Path $bin $name) -Destination (Join-Path $pluginOutput $name) -Force }
# Package the NJsonSchema and Newtonsoft.Json runtime closures, deferring to assemblies
# already supplied by the game itself. The assets file avoids copying stale or
# unrelated DLLs that might remain in a reused build directory.
$assetsPath = Join-Path $obj 'project.assets.json'
if (!(Test-Path -LiteralPath $assetsPath -PathType Leaf)) { throw "NuGet assets file is missing: $assetsPath" }
$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
$target = $assets.targets.PSObject.Properties['.NETFramework,Version=v4.7.2']
if (!$target) { throw 'The NuGet assets file has no net472 target.' }
$packages = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $target.Value.PSObject.Properties) {
    $separator = $entry.Name.LastIndexOf('/')
    if ($separator -lt 1) { throw "Invalid NuGet target entry: $($entry.Name)" }
    $packages[$entry.Name.Substring(0, $separator)] = $entry.Value
}
$pending = [Collections.Generic.Queue[string]]::new()
$pending.Enqueue('NJsonSchema')
$pending.Enqueue('Newtonsoft.Json')
$visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$runtimeNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
while ($pending.Count -gt 0) {
    $packageName = $pending.Dequeue()
    if (!$visited.Add($packageName)) { continue }
    if (!$packages.ContainsKey($packageName)) { throw "NuGet dependency is missing from the assets target: $packageName" }
    $library = $packages[$packageName]
    $dependencies = $library.PSObject.Properties['dependencies']
    if ($dependencies) {
        foreach ($dependency in $dependencies.Value.PSObject.Properties) { $pending.Enqueue($dependency.Name) }
    }
    $runtime = $library.PSObject.Properties['runtime']
    if ($runtime) {
        foreach ($asset in $runtime.Value.PSObject.Properties) {
            $name = [IO.Path]::GetFileName($asset.Name.Replace('/', [IO.Path]::DirectorySeparatorChar))
            if ($name.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)) { [void]$runtimeNames.Add($name) }
        }
    }
    $runtimeTargets = $library.PSObject.Properties['runtimeTargets']
    if ($runtimeTargets) {
        foreach ($asset in $runtimeTargets.Value.PSObject.Properties) {
            if ($asset.Value.assetType -ne 'runtime') { continue }
            $name = [IO.Path]::GetFileName($asset.Name.Replace('/', [IO.Path]::DirectorySeparatorChar))
            if ($name.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath (Join-Path $bin $name) -PathType Leaf)) { [void]$runtimeNames.Add($name) }
        }
    }
}
foreach ($required in @('NJsonSchema.dll', 'NJsonSchema.Annotations.dll', 'Namotion.Reflection.dll', 'Newtonsoft.Json.dll')) {
    if (!$runtimeNames.Contains($required)) { throw "NuGet runtime dependency is missing from the assets file: $required" }
}
$managed = Join-Path $GameRoot 'Beat Saber_Data/Managed'
foreach ($name in $runtimeNames) {
    if ($name -in @('VoiceInput.dll', 'LocalModUI.dll')) { continue }
    if (Test-Path -LiteralPath (Join-Path $managed $name) -PathType Leaf) { continue }
    $sourcePath = Join-Path $bin $name
    if ($name -eq 'Newtonsoft.Json.dll' -and !(Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        $sourcePath = Join-Path $GameRoot 'Libs/Newtonsoft.Json.dll'
    }
    if (!(Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw "NuGet runtime dependency is missing from the build output: $sourcePath" }
    Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $libOutput $name) -Force
}
$schema = Get-Content -LiteralPath (Join-Path $source 'Contracts/contracts.schema.json') -Raw | ConvertFrom-Json
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'VoiceInput.example.json'), (($schema.'x-defaultConfiguration' | ConvertTo-Json -Depth 15) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))

Write-Output (Join-Path $pluginOutput 'VoiceInput.dll')
