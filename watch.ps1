<#
.SYNOPSIS
    Run and watch the PodcastToYoto project.
#>
[CmdletBinding()]
param(
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet watch --project src/PodcastToYoto.Web --launch-profile http