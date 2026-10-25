param([switch]$RunTests, [switch]$FullTests)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x x64 C# compiler not found. Install the .NET Framework 4.8 developer tools.' }
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName)
$references = @('/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Runtime.Serialization.dll','/r:System.Xml.Linq.dll','/r:System.Core.dll')
$common = @('/nologo','/target:winexe','/platform:x64','/optimize+','/codepage:65001',('/win32manifest:' + (Join-Path $root 'src\app.manifest'))) + $references
foreach ($role in @('Primary','Secondary')) {
    $dir = Join-Path $root ('.artifacts\bin\' + $role)
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $output = Join-Path $dir ('UsbRemoteAssistant.' + $role + '.exe')
    $define = if ($role -eq 'Primary') { '/define:PRIMARY' } else { '/define:SECONDARY' }
    & $compiler @common $define ('/out:' + $output) @sources
    if ($LASTEXITCODE -ne 0) { throw ('Build failed: ' + $role) }
    Copy-Item -LiteralPath (Join-Path $root 'src\app.config') -Destination ($output + '.config') -Force
}
if ($RunTests -or $FullTests) {
    $testDir = Join-Path $root '.artifacts\tests'
    New-Item -ItemType Directory -Path $testDir -Force | Out-Null
    $testExe = Join-Path $testDir 'UsbRemoteAssistant.Tests.exe'
    $testSources = @(Get-ChildItem -LiteralPath (Join-Path $root 'tests') -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName)
    & $compiler @common '/define:PRIMARY,TESTS' ('/out:' + $testExe) @sources @testSources
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    Copy-Item -LiteralPath (Join-Path $root 'src\app.config') -Destination ($testExe + '.config') -Force
    $modes = if ($FullTests) { @('--self-test') } else { @('--public-test','--recovery-test','--board-test') }
    foreach ($mode in $modes) {
        $report = Join-Path $testDir ($mode.TrimStart('-') + '.txt')
        $process = Start-Process -FilePath $testExe -ArgumentList @($mode,('"' + $report + '"')) -WindowStyle Hidden -PassThru -Wait
        Get-Content -LiteralPath $report -Encoding UTF8
        if ($process.ExitCode -ne 0) { throw ('Tests failed: ' + $mode) }
    }
}
Write-Output 'Built Primary and Secondary apps in .artifacts/bin. No personal configuration or third-party binaries were copied.'
