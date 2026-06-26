<#
.SYNOPSIS
  Starts the Vesta relay dev environment back up after dev-stop.ps1.
#>
[CmdletBinding()]
param(
    [string]$ResourceGroup = 'vesta-dev-rg',
    [string]$WebAppName = 'vestaserver',
    [string]$PostgresServerName = 'vesta-dev-pg'
)

$ErrorActionPreference = 'Stop'

Write-Host "Starting PostgreSQL server '$PostgresServerName'..."
az postgres flexible-server start --name $PostgresServerName --resource-group $ResourceGroup

Write-Host "Starting Web App '$WebAppName'..."
az webapp start --name $WebAppName --resource-group $ResourceGroup

Write-Host 'Vesta relay dev environment started.'
