function Assert-BundledDdi([string]$Directory) {
    $manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\config\ddi-bundles.json') -Raw |
        ConvertFrom-Json
    foreach ($bundle in $manifest.bundles) {
        foreach ($entry in $bundle.files) {
            $path = Join-Path (Join-Path $Directory $bundle.directory) $entry.name
            if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
                (Get-Item -LiteralPath $path).Length -ne $entry.size -or
                (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) {
                throw "Bundled DDI is missing or corrupt: $path"
            }
        }
    }
}
