# Builds the Windows release zip on this machine: self-contained Remy.exe plus license notices.
# Usage (from the repository root):  powershell -ExecutionPolicy Bypass -File windows/scripts/package-release.ps1
# Output: windows/out/release/Remy-Windows-<version>-x64.zip and its SHA-256.
$ErrorActionPreference = 'Stop'

$windows = Split-Path -Parent $PSScriptRoot
$repo = Split-Path -Parent $windows

# Use the user-local SDK if present, otherwise dotnet on PATH.
$localSdk = Join-Path $env:LOCALAPPDATA 'dotnet-sdk'
if (Test-Path (Join-Path $localSdk 'dotnet.exe')) { $env:DOTNET_ROOT = $localSdk; $dotnet = Join-Path $localSdk 'dotnet.exe' }
else { $dotnet = (Get-Command dotnet).Source; $env:DOTNET_ROOT = Split-Path -Parent $dotnet }

[xml]$props = Get-Content (Join-Path $windows 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
$name = "Remy-Windows-$version-x64"
$outRoot = Join-Path $windows 'out\release'
$stage = Join-Path $outRoot $name
$zip = Join-Path $outRoot "$name.zip"

$dirty = git -C $repo status --porcelain -- windows
if ($dirty) { Write-Warning 'windows/ has uncommitted changes; the zip will not match a commit.' }
$commit = (git -C $repo rev-parse --short HEAD).Trim()

if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
if (Test-Path $zip) { Remove-Item -Force $zip }
New-Item -ItemType Directory -Force $stage | Out-Null

Write-Host "Testing..."
& $dotnet test (Join-Path $windows 'Remy.Windows.slnx') -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; no release built.' }

Write-Host "Publishing Remy $version ($commit)..."
& $dotnet publish (Join-Path $windows 'src\Remy.App\Remy.App.csproj') -c Release --self-contained -nologo -v q -o $stage `
    -p:DebugType=None -p:DebugSymbols=false -p:SourceRevisionId=$commit
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

# Notices: Remy and upstream licenses, the Windows component list, each component's license, the .NET runtime's.
$notices = Join-Path $stage 'Notices'
New-Item -ItemType Directory -Force $notices | Out-Null
Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $notices 'LICENSE.txt')
Copy-Item (Join-Path $repo 'app\LICENSE') (Join-Path $notices 'Dictate-Anywhere-LICENSE.txt')
Copy-Item (Join-Path $repo 'ATTRIBUTION.md') $notices
Copy-Item (Join-Path $windows 'THIRD_PARTY_NOTICES.md') $notices
Copy-Item -Recurse (Join-Path $windows 'licenses\*') $notices
$runtimeNotices = Join-Path $notices '.NET'
New-Item -ItemType Directory -Force $runtimeNotices | Out-Null
foreach ($f in 'LICENSE.txt', 'ThirdPartyNotices.txt') { Copy-Item (Join-Path $env:DOTNET_ROOT $f) $runtimeNotices }

@"
Remy for Windows $version (preview)

1. Run Remy.exe. Windows may say "Windows protected your PC" because the app
   is not code-signed: click "More info", then "Run anyway".
2. On the Speech Model page, download Parakeet v2 (about 660 MB, one time).
3. Click into any text field, hold Ctrl+Win, speak, and let go.

Keep this folder together; Remy.exe needs the files next to it.
Settings: %APPDATA%\Remy. Models, history and logs: %LOCALAPPDATA%\Remy.
Licenses and third-party notices: the Notices folder.
Source: https://github.com/brysonjl/remy (commit $commit)
"@ | Set-Content -Encoding utf8 (Join-Path $stage 'README.txt')

Write-Host "Zipping..."
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
"$hash  $name.zip" | Set-Content -Encoding ascii "$zip.sha256"
'{0}  ({1:N0} MB)' -f $zip, ((Get-Item $zip).Length / 1MB) | Write-Host
"SHA-256 $hash" | Write-Host
