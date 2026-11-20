# FEAT-RT-023: build a local test ZIP from a fresh integration checkout and the pinned renderer fork.
# Uses the same prerequisites as ci/build-windows.ps1 (LLVM_ROOT and .NET SDK included).
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Push-Location (Split-Path $PSScriptRoot -Parent)
$previous = @{}
foreach ($name in 'CI_COMMIT_SHA', 'CI_CHANNEL', 'RENDERER_SHA', 'RYGT_RUNTIME_TOOLS', 'PATH', 'LIB') {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
try {
    $policy = (Resolve-Path 'renderer/openxr-integration.json').Path
    $pin = Get-Content -Raw -LiteralPath $policy | ConvertFrom-Json
    if ($pin.commit -cnotmatch '^[0-9a-f]{40}$') { throw 'The integration renderer must be pinned to a commit' }
    $env:RENDERER_SHA = $pin.commit
    $env:CI_COMMIT_SHA = & git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Could not identify the integration checkout' }
    $env:CI_CHANNEL = 'ci'
    if (!$env:RYGT_RUNTIME_TOOLS) { $env:RYGT_RUNTIME_TOOLS = Join-Path (Get-Location) 'build/ci-runtime-tools' }
    & "$PSScriptRoot/build-windows.ps1" -Target Launcher
    & "$PSScriptRoot/build-windows.ps1" -Target Renderer -OpenXr -RendererPolicy $policy
    & "$PSScriptRoot/build-windows.ps1" -Target Runtime
    & "$PSScriptRoot/build-windows.ps1" -Target Tools
    & python "$PSScriptRoot/package.py" --renderer-policy $policy
    if ($LASTEXITCODE -ne 0) { throw 'OpenXR package verification failed' }
} finally {
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    Pop-Location
}
