<#
.SYNOPSIS
  Tears down the entire Vesta relay dev environment (full cost stop).
.DESCRIPTION
  Deletes the resource group and every resource in it. Everything is reproducible
  from Bicep, so re-running the deployment recreates the environment.
  Note: the Key Vault is soft-deleted and retained for 7 days; purge manually if
  you need to immediately reuse the same vault name.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ResourceGroup = 'vesta-dev-rg'
)

$ErrorActionPreference = 'Stop'

if ($PSCmdlet.ShouldProcess($ResourceGroup, 'Delete resource group and all resources')) {
    Write-Host "Deleting resource group '$ResourceGroup'..."
    az group delete --name $ResourceGroup --yes
    Write-Host 'Vesta relay dev environment torn down.'
}
