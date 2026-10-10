<#
.SYNOPSIS
    Run the PodcastToYoto project.
#>
[CmdletBinding()]
param(
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet run --project src/PodcastToYoto.Web --launch-profile http