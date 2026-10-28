$RepoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($GameRoot)) {
    throw 'Specify -GameRoot or set BEAT_SABER_DIR to the Beat Saber installation.'
}
$GameRoot = [IO.Path]::GetFullPath($GameRoot)

function Resolve-RepoBuildPath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $prefix = $RepoRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (!$resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Build and package paths must be inside the repository: $resolved"
    }
    return $resolved
}
