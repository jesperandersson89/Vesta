// Key Vault (RBAC-authorized) plus a set of secrets.
// Secrets are passed as a single secure object so values never appear in logs.

@description('Azure region for the vault.')
param location string

@description('Globally-unique Key Vault name (3-24 chars, alphanumeric and hyphens).')
param keyVaultName string

@description('Secret name -> secret value. Marked secure so values are not logged.')
@secure()
param secrets object

@description('Tags applied to the vault.')
param tags object = {}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    enablePurgeProtection: null
    publicNetworkAccess: 'Enabled'
  }
}

resource secretResources 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = [
  for item in items(secrets): {
    parent: vault
    name: item.key
    properties: {
      value: item.value
    }
  }
]

output keyVaultName string = vault.name
output keyVaultUri string = vault.properties.vaultUri
