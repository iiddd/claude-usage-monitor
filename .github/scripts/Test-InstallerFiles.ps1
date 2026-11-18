<#
.SYNOPSIS
    Fails when the dotnet publish output and the files listed in Package.wxs disagree.

.DESCRIPTION
    Package.wxs lists every installed file by hand. A dependency update that adds a DLL would
    otherwise produce an MSI that silently lacks it, and the service would crash at runtime.
    Files that are published but intentionally not installed go in $Ignored, keyed by name with the reason.
#>
param(
    [string]$PublishDir = 'publish',
    [string]$PackageWxs = 'ClaudeUsageServer.Installer/Package.wxs'
)

$ErrorActionPreference = 'Stop'

$Ignored = @{
    'web.config'                                       = 'IIS only'
    'ClaudeUsageServer.staticwebassets.endpoints.json' = 'no static web assets are served'
    'ClaudeUsageServer.pdb'                            = 'debug symbols'
}

$root = (Resolve-Path $PublishDir).Path.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$published = Get-ChildItem -Path $PublishDir -File -Recurse |
    ForEach-Object { $_.FullName.Substring($root.Length) } |
    Where-Object { -not $Ignored.ContainsKey($_) -and $_ -notlike 'appsettings.*.json' } |
    Sort-Object

$listed = [regex]::Matches((Get-Content $PackageWxs -Raw), 'Source="\$\(var\.PublishDir\)([^"]+)"') |
    ForEach-Object { $_.Groups[1].Value } |
    Sort-Object

$missing = @($published | Where-Object { $_ -notin $listed })
$stale = @($listed | Where-Object { $_ -notin $published })

foreach ($file in $missing) { Write-Output "::error::Published file '$file' is not installed by Package.wxs" }
foreach ($file in $stale) { Write-Output "::error::Package.wxs installs '$file', which is no longer published" }

if ($missing.Count -or $stale.Count) { exit 1 }
Write-Output "Package.wxs installs all $($listed.Count) published files."
