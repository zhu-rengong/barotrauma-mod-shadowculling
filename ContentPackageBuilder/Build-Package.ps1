[Flags()] enum BuildOS { None = 0; Windows = 1; Linux = 2; Mac = 4; All = 8 }
[Flags()] enum BuildTarget { None = 0; Client = 1; Server = 2; All = 4 }
enum BuildConfiguration { Debug; Release }

$osMap = @{
    "win-x64"   = [BuildOS]::Windows
    "linux-x64" = [BuildOS]::Linux
    "osx-x64"   = [BuildOS]::Mac
}

$platforms = [BuildOS]::All
$targets = [BuildTarget]::Client
$config = [BuildConfiguration]::Release

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$srcDir = Join-Path $scriptDir "Content"
$binaryDir = Join-Path $srcDir "bin"
$userPropsPath = Join-Path $repoRoot "UserBuildData.props"

$projects = @(
    @{ Path = "ClientProject\WindowsClient.csproj"; Runtime = "win-x64"; Target = [BuildTarget]::Client },
    @{ Path = "ClientProject\LinuxClient.csproj"; Runtime = "linux-x64"; Target = [BuildTarget]::Client },
    @{ Path = "ClientProject\OSXClient.csproj"; Runtime = "osx-x64"; Target = [BuildTarget]::Client },
    @{ Path = "ServerProject\WindowsServer.csproj"; Runtime = "win-x64"; Target = [BuildTarget]::Server },
    @{ Path = "ServerProject\LinuxServer.csproj"; Runtime = "linux-x64"; Target = [BuildTarget]::Server },
    @{ Path = "ServerProject\OSXServer.csproj"; Runtime = "osx-x64"; Target = [BuildTarget]::Server }
) | ForEach-Object {
    $_.OS = $osMap[$_.Runtime]; $_
} | Where-Object {
    ($platforms -eq [BuildOS]::All -or ($platforms -band $_.OS)) -and
    ($targets -eq [BuildTarget]::All -or ($targets -band $_.Target))
} | Select-Object *

if ($projects.Count -gt 0) {
    $confirm = Read-Host "Build $($projects.Count) project(s) for Platforms($platforms) / Targets($targets) $($config)? (Y/N)"
    if ($confirm -in @("Y", "y")) {
        Write-Host "`nBuilding projects..." -ForegroundColor Cyan

        foreach ($proj in $projects) {
            $projPath = Join-Path $repoRoot $proj.Path
            Write-Host "Building $($proj.OS)$($proj.Target)..." -ForegroundColor Cyan
            dotnet @(
                "build"
                $projPath
                "--configuration"
                $config
                "--runtime"
                $proj.Runtime
                "-p:RuntimeFrameworkVersion=8.0.0"
                "-clp:ErrorsOnly;Summary"
            )
        }

        Write-Host "Build complete!" -ForegroundColor Green
    }
}

if (Test-Path $userPropsPath) {
    [xml]$xml = Get-Content $userPropsPath
    $destDir = (Select-Xml -Xml $xml -XPath "//ModDeployDir").Node.InnerText
    if (-not $destDir) {
        Write-Host "Error: ModDeployDir not found in Build.props" -ForegroundColor Red
        exit 1
    }
}
else {
    Write-Host "Error: Build.props not found at $userPropsPath" -ForegroundColor Red
    exit 1
}

Write-Host "=== Content Sync ===" -ForegroundColor Cyan
Write-Host "Source : $srcDir"
Write-Host "Target : $destDir`n"

$listArgs = @($srcDir, $destDir, "/E", "/L", "/NP", "/NDL", "/NC", "/NJH", "/NJS")
$excludeBinary = (Read-Host "Do you want to exclude binary files? (Y/N)") -in @("Y", "y")
if ($excludeBinary) {
    $listArgs += @("/XD", $binaryDir)
}
$out = robocopy @listArgs 2>&1

$files = $out | Where-Object { $_ -match "^\s{1,}\d+\s" }

Write-Host "Files to sync:" -ForegroundColor Yellow
$files | ForEach-Object { Write-Host "  $( ([regex]::Matches($_, '\S.*$') | Select-Object -Last 1).Value )" }

if ($files.Count -eq 0) {
    Write-Host "  (none)" -ForegroundColor Gray
}

Write-Host "`nTotal: $( $files.Count ) items`n"

$confirmSync = Read-Host "Proceed with sync? (Y/N)"
if ($confirmSync -notin @("Y", "y")) {
    Write-Host "Cancelled." -ForegroundColor Yellow
    exit 0
}

Write-Host "`nSyncing..." -ForegroundColor Green
$syncArgs = @($srcDir, $destDir, "/E", "/MIR", "/R:3", "/W:1")
if ($excludeBinary) {
    $syncArgs += @("/XD", $binaryDir)
}
robocopy @syncArgs | Out-Null
$exitCode = $LASTEXITCODE

if ($exitCode -ge 8) {
    Write-Host "Sync failed (code: $exitCode)" -ForegroundColor Red
    exit $exitCode
}

Write-Host "Sync complete`n" -ForegroundColor Green