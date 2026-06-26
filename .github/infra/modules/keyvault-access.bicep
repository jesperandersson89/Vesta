// Grants a principal the "Key Vault Secrets User" role on an existing vault.
// Deployed AFTER the web app so the app's managed-identity principalId exists.

@description('Name of the existing Key Vault to grant access on.')
param keyVaultName string

@description('Object (principal) id of the identity to grant secret read access to.')
param principalId string

// Built-in role: Key Vault Secrets User (read secret values).
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource roleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, principalId, keyVaultSecretsUserRoleId)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}
