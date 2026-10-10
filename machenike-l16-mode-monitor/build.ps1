param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$modeCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $modeCompiler)) {
    throw 'The Windows .NET Framework C# compiler was not found.'
}
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'artifacts' }
$modeBuildDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $modeBuildDirectory -Force | Out-Null
$modeSource = Join-Path $PSScriptRoot 'src\ModeMonitor.cs'
$modeOutput = Join-Path $modeBuildDirectory 'ModeMonitor.exe'
& $modeCompiler /nologo /target:winexe /platform:x64 /optimize+ /reference:System.Drawing.dll /reference:System.Windows.Forms.dll "/out:$modeOutput" $modeSource
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output "Built: $modeOutput"
