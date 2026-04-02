# Auto-detect paths based on script location
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepositoryRoot = Split-Path -Parent $ScriptDir

$SourceDir = Join-Path $ScriptDir "Content"
$PluginToolboxPath = Join-Path $RepositoryRoot "PluginToolbox\PluginToolbox.csproj"
$BuildProps = Join-Path $RepositoryRoot "Build.props"

# Parse ModDeployDir from Build.props
if (Test-Path $BuildProps) {
    [xml]$props = Get-Content $BuildProps
    $DestDir = (Select-Xml -Xml $props -XPath "//ModDeployDir").Node.InnerText
    if (-not $DestDir) {
        Write-Host "Error: ModDeployDir not found in Build.props" -ForegroundColor Red
        exit 1
    }
}
else {
    Write-Host "Error: Build.props not found at $BuildProps" -ForegroundColor Red
    exit 1
}

# Build and run PluginToolbox
$confirm = Read-Host "Build and run PluginToolbox? (Y/N)"
if ($confirm -in @("Y", "y")) {
    Write-Host "`nBuilding and running PluginToolbox..." -ForegroundColor Cyan
    dotnet run --project $PluginToolboxPath -- "--build"
}

Write-Host "=== Content Sync ===" -ForegroundColor Cyan
Write-Host "Source : $SourceDir"
Write-Host "Target : $DestDir`n"

# List files without copying
$listArgs = @($SourceDir, $DestDir, "/E", "/L", "/NP", "/NDL", "/NC", "/NJH", "/NJS")
$output = robocopy @listArgs 2>&1
$files = $output | Where-Object { $_ -match "^\s{1,}\d+\s" }

Write-Host "Files to sync:" -ForegroundColor Yellow
$files | ForEach-Object { Write-Host "  $(([regex]::Matches($_, '\S.*$') | Select-Object -Last 1).Value)" }

if ($files.Count -eq 0) {
    Write-Host "  (none)" -ForegroundColor Gray
}

Write-Host "`nTotal: $($files.Count) items`n"

$confirm = Read-Host "Proceed with sync? (Y/N)"
if ($confirm -notin @("Y", "y")) {
    Write-Host "Cancelled." -ForegroundColor Yellow
    exit 0
}

# Sync
Write-Host "`nSyncing..." -ForegroundColor Green
$syncArgs = @($SourceDir, $DestDir, "/E", "/MIR", "/R:3", "/W:1")
robocopy @syncArgs | Out-Null
$exitCode = $LASTEXITCODE

if ($exitCode -ge 8) {
    Write-Host "Sync failed (code: $exitCode)" -ForegroundColor Red
    exit $exitCode
}

Write-Host "Sync complete`n" -ForegroundColor Green

