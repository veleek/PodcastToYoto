<#
.SYNOPSIS
    Builds the PodcastToYoto container image. Never pushes unless -Push is given.

.EXAMPLE
    ./build.ps1                                   # podcast-to-yoto:latest + podcast-to-yoto:<git sha>
.EXAMPLE
    ./build.ps1 -Registry mrfantastic:5000 -Push  # tag for a local registry and push there
.EXAMPLE
    ./build.ps1 -SaveTo podcast-to-yoto.tar       # export for `docker load` on the server
#>
[CmdletBinding()]
param(
    [string] $Name = 'podcast-to-yoto',
    [string] $Registry,
    [string] $Platform = 'linux/amd64',
    [switch] $Push,
    [string] $SaveTo,
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $SkipTests) {
    dotnet test PodcastToYoto.slnx --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

$sha = (git rev-parse --short HEAD).Trim()
if (git status --porcelain) { $sha = "$sha-dirty" }

$repo = if ($Registry) { "$($Registry.TrimEnd('/'))/$Name" } else { $Name }
$tags = @("${repo}:latest", "${repo}:$sha")

$tagArgs = $tags | ForEach-Object { '-t', $_ }
docker build --platform $Platform @tagArgs --label "org.opencontainers.image.revision=$sha" .
if ($LASTEXITCODE -ne 0) { throw 'docker build failed.' }
Write-Host "Built: $($tags -join ', ')"

if ($Push) {
    if (-not $Registry) { throw '-Push requires -Registry.' }
    foreach ($t in $tags) {
        docker push $t
        if ($LASTEXITCODE -ne 0) { throw "docker push $t failed." }
    }
}

if ($SaveTo) {
    docker save -o $SaveTo $tags
    if ($LASTEXITCODE -ne 0) { throw 'docker save failed.' }
    Write-Host "Saved to $SaveTo"
}
