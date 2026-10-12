<#
.SYNOPSIS
    Run and watch the PodcastToYoto project.
#>
[CmdletBinding()]
param(
    [switch]$NonInteractive
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$extraArgs = @()
if ($NonInteractive) {
    $extraArgs += "--non-interactive"
}

dotnet watch --project src/PodcastToYoto.Web --launch-profile http @extraArgs