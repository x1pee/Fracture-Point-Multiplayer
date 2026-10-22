param(
    [Parameter(Mandatory=$true)]
    [string]$GameDirectory,
    [string]$LoaderRoot
)
$ErrorActionPreference = "Stop"
$gamePath = (Resolve-Path -LiteralPath $GameDirectory).Path
if (-not $LoaderRoot) { $LoaderRoot = Join-Path $gamePath "BepInEx" }
$loaderPath = (Resolve-Path -LiteralPath $LoaderRoot).Path
if (-not (Test-Path -LiteralPath (Join-Path $loaderPath "interop/Assembly-CSharp.dll"))) {
    throw "BepInEx interop assemblies are missing. Launch the supported game with BepInEx once."
}
dotnet build (Join-Path $PSScriptRoot "src/FractureTogether.csproj") -c Release "-p:LoaderRoot=$loaderPath"
if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
Write-Host "Plugin: src/bin/Release/net6.0/FracturePointMultiplayer.dll"