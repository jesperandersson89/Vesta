<#
.SYNOPSIS
  Stops billing-affecting compute for the Vesta relay dev environment.
.DESCRIPTION
  Stops the relay Web App and the PostgreSQL Flexible Server. The App Service
  Plan still bills while it exists; for a full cost stop, delete the resource
  group with .github/infra/scripts/dev-teardown.ps1.
#>
[CmdletBinding()]
param(
    [string]$ResourceGroup = 'vesta-dev-rg',
    [string]$WebAppName = 'vestaserver',
    [string]$PostgresServerName = 'vesta-dev-pg'
)

$ErrorActionPreference = 'Stop'

Write-Host "Stopping Web App '$WebAppName'..."
az webapp stop --name $WebAppName --resource-group $ResourceGroup

Write-Host "Stopping PostgreSQL server '$PostgresServerName'..."
az postgres flexible-server stop --name $PostgresServerName --resource-group $ResourceGroup

Write-Host 'Vesta relay dev environment stopped.'
