<#
.SYNOPSIS
    Builds the PodcastToYoto container image, and optionally pushes it to a local registry and redeploys the server.
    Never pushes unless -Push or -Deploy is given.

.EXAMPLE
    ./build.ps1                                   # test + build podcast-to-yoto:latest and :<git sha>
.EXAMPLE
    ./build.ps1 -Registry localhost:5000 -Push    # ...and push to the registry running on this machine
.EXAMPLE
    ./build.ps1 -Deploy                           # build, copy the image to the server (docker save/scp/load), restart
.EXAMPLE
    ./build.ps1 -Deploy -Registry localhost:5000  # same, but via a registry (push, then compose pull on the server)
.EXAMPLE
    ./build.ps1 -SaveTo podcast-to-yoto.tar       # export for `docker load` on the server (no registry)
#>
[CmdletBinding()]
param(
    [string] $Name = 'podcast-to-yoto',
    [string] $Registry,
    [string] $Platform = 'linux/amd64',
    [switch] $Push,
    [string] $SaveTo,
    [switch] $SkipTests,

    # Deploy: get the image onto $DeployHost (registry push/pull if -Registry, else docker save + scp + docker load),
    # then restart $Service with compose.
    [switch] $Deploy,
    [string] $DeployHost = 'cake',
    [string] $DeployDir = '~/services',
    [string] $Service = 'podcast-to-yoto'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if ($Deploy -and $Registry) { $Push = $true }

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
        if ($LASTEXITCODE -ne 0) { throw "docker push $t failed. Is the registry running? (docker start registry)" }
    }
}

if ($SaveTo) {
    docker save -o $SaveTo $tags
    if ($LASTEXITCODE -ne 0) { throw 'docker save failed.' }
    Write-Host "Saved to $SaveTo"
}

if ($Deploy) {
    if ($Registry) {
        # The server's IMAGE (yoto/.env) points at the registry.
        $remote = "cd $DeployDir && docker compose pull $Service && docker compose up -d $Service && docker image prune -f"
    }
    else {
        # No registry: ship the whole image. scp shows progress; docker load on the server picks up the :latest tag.
        $tar = Join-Path ([IO.Path]::GetTempPath()) "$Name-$sha.tar"
        docker save -o $tar "${repo}:latest"
        if ($LASTEXITCODE -ne 0) { throw 'docker save failed.' }
        try {
            scp $tar "${DeployHost}:/tmp/$Name.tar"
            if ($LASTEXITCODE -ne 0) { throw 'scp failed.' }
        }
        finally {
            Remove-Item $tar -ErrorAction SilentlyContinue
        }
        $remote = "docker load -i /tmp/$Name.tar && rm /tmp/$Name.tar && cd $DeployDir && docker compose up -d $Service && docker image prune -f"
    }

    Write-Host "Deploying on ${DeployHost}: $remote"
    ssh $DeployHost $remote
    if ($LASTEXITCODE -ne 0) { throw 'Remote deploy failed.' }
    Write-Host "Deployed $sha to $DeployHost."
}
