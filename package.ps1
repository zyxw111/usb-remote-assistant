$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = '2.0.1'
$release = Join-Path $root '.artifacts\release'
New-Item -ItemType Directory -Path $release -Force | Out-Null
$docs = @('README.md','LICENSE','THIRD_PARTY_NOTICES.md','SECURITY.md')
$sourceFiles = @('build.ps1','package.ps1','.gitignore','CONTRIBUTING.md','UsbRemoteAssistant.Primary.csproj','UsbRemoteAssistant.Secondary.csproj') + $docs
Add-Type -AssemblyName System.IO.Compression
function Write-Bundle([string]$path, [System.Collections.IDictionary]$entries) {
    $stream = [IO.File]::Open($path, [IO.FileMode]::Create)
    try {
        $zip = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($name in ($entries.Keys | Sort-Object)) {
                $source = $entries[$name]
                if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw ('Missing release input: ' + $source) }
                $entry = $zip.CreateEntry($name.Replace('\','/'))
                $output = $entry.Open(); $input = [IO.File]::OpenRead($source)
                try { $input.CopyTo($output) } finally { $input.Dispose(); $output.Dispose() }
            }
        } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }
}
$source = @{}
foreach ($file in $sourceFiles) { $source[$file] = Join-Path $root $file }
foreach ($directory in @('src','tests','docs','examples')) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root $directory) -Recurse -File) {
        if ($file.Extension -notin @('.cs','.config','.manifest','.md')) { throw ('Unexpected source input: ' + $file.Name) }
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\','/')
        $source[$relative] = $file.FullName
    }
}
Write-Bundle (Join-Path $release ('UsbRemoteAssistant-' + $version + '-source.zip')) $source
foreach ($role in @('Primary','Secondary')) {
    $files = @{}
    foreach ($doc in $docs) { $files[$doc] = Join-Path $root $doc }
    $exe = 'UsbRemoteAssistant.' + $role + '.exe'
    $files[$exe] = Join-Path $root ('.artifacts\bin\' + $role + '\' + $exe)
    $files[$exe + '.config'] = $files[$exe] + '.config'
    Write-Bundle (Join-Path $release ('UsbRemoteAssistant-' + $version + '-' + $role + '.zip')) $files
}
$checksums = foreach ($name in @('source','Primary','Secondary')) {
    $file = 'UsbRemoteAssistant-' + $version + '-' + $name + '.zip'
    $hash = (Get-FileHash -LiteralPath (Join-Path $release $file) -Algorithm SHA256).Hash.ToLowerInvariant()
    $hash + '  ' + $file
}
[IO.File]::WriteAllLines((Join-Path $release 'SHA256SUMS.txt'), [string[]]$checksums, [Text.UTF8Encoding]::new($false))
Write-Output ('Created public release packages in ' + $release)
