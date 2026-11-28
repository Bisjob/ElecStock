# ElecStock: copies the stock KiCad library (nickname Stock) to this PC and points ELEC_STOCK at it.
# Served by the server with its address filled in; run it on every PC that has KiCad, again after a library change:
#
#   irm __SERVER__/kicad/sync.ps1 | iex
#
# Folder: the user variable ELEC_STOCK when set, else ~\ElecStock-Library (override with $env:ELECSTOCK_KICAD_TARGET;
# $env:ELECSTOCK_NO_PATH_VARIABLE = 1 leaves the user variable alone).
# Only the files a previous sync wrote are replaced or removed (listed in elecstock-library.json): nothing else
# in the folder is touched.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$server = if ($env:ELECSTOCK_SERVER) { $env:ELECSTOCK_SERVER } else { '__SERVER__' }
$server = $server.TrimEnd('/')
$userVar = [Environment]::GetEnvironmentVariable('ELEC_STOCK', 'User')
$target = if ($env:ELECSTOCK_KICAD_TARGET) { $env:ELECSTOCK_KICAD_TARGET } elseif ($userVar) { $userVar } else { Join-Path $HOME 'ElecStock-Library' }
$target = [IO.Path]::GetFullPath($target)
New-Item -ItemType Directory -Force $target | Out-Null

$stateFile = Join-Path $target 'elecstock-library.json'
$localVersion = $null
$owned = @()
if (Test-Path $stateFile) {
    $state = Get-Content $stateFile -Raw | ConvertFrom-Json
    $localVersion = $state.version
    $owned = @($state.files)
}

$remoteVersion = ([string](Invoke-RestMethod "$server/kicad/version" -UseBasicParsing)).Trim()
if ($remoteVersion -eq $localVersion) {
    Write-Host "Stock KiCad library up to date (version $remoteVersion) in $target"
}
else {
    $temp = Join-Path ([IO.Path]::GetTempPath()) ("elecstock-kicad-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $temp | Out-Null
    try {
        $zip = Join-Path $temp 'library.zip'
        Invoke-WebRequest "$server/kicad/library.zip" -OutFile $zip -UseBasicParsing
        $extracted = Join-Path $temp 'library'
        Expand-Archive $zip $extracted
        $manifest = Get-Content (Join-Path $extracted 'elecstock-library.json') -Raw | ConvertFrom-Json
        $files = @($manifest.files)

        $removed = 0
        foreach ($file in $owned) {
            if ($files -notcontains $file) {
                $path = Join-Path $target $file
                if (Test-Path $path) { Remove-Item $path; $removed++ }
            }
        }
        foreach ($file in $files) {
            $dest = Join-Path $target $file
            New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
            Copy-Item (Join-Path $extracted $file) $dest -Force
        }
        Copy-Item (Join-Path $extracted 'elecstock-library.json') $stateFile -Force
        $summary = "Stock KiCad library version $($manifest.version): $($files.Count) files in $target"
        if ($removed) { $summary += ", $removed removed" }
        Write-Host $summary
    }
    finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($userVar -ne $target -and -not $env:ELECSTOCK_NO_PATH_VARIABLE) {
    [Environment]::SetEnvironmentVariable('ELEC_STOCK', $target, 'User')
    $was = if ($userVar) { " (was $userVar)" } else { "" }
    Write-Host "User variable ELEC_STOCK = $target$was. Restart KiCad so it picks it up: it takes precedence over"
    Write-Host "the ELEC_STOCK of KiCad -> Preferences -> Configure Paths."
}
