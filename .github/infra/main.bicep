// Vesta relay — full environment (subscription-scoped).
// Provisions: resource group, Log Analytics + App Insights, Burstable PostgreSQL,
// Key Vault (DB connection string), and the relay Web App wired via managed identity.
//
// Deploy:
//   az deployment sub create \
//     --location <region> \
//     --template-file .github/infra/main.bicep \
//     --parameters .github/infra/params/dev.bicepparam
//
// This stack is Atrium-agnostic: it only knows the relay's own config surface.

targetScope = 'subscription'

@description('Short environment name, e.g. dev or prod. Used in resource names.')
param environmentName string = 'dev'

@description('Azure region for all resources.')
param location string = 'norwayeast'

@description('Base name used to derive resource names.')
param namePrefix string = 'vesta'

@description('Globally-unique name of the relay Web App.')
param webAppName string

@description('PostgreSQL administrator login.')
param postgresAdminLogin string

@description('PostgreSQL administrator password.')
@secure()
param postgresAdminPassword string

@description('PostgreSQL database name for the relay.')
param postgresDatabaseName string = 'vesta'

@description('Base64url Ed25519 public key of the Atrium operator, added to the relay admin allow-list.')
param relayAdminPublicKey string

@description('PostgreSQL compute SKU. Burstable B1ms for dev.')
param postgresSkuName string = 'Standard_B1ms'

@description('PostgreSQL compute tier.')
param postgresSkuTier string = 'Burstable'

@description('App Service Plan SKU.')
param appServicePlanSku string = 'B1'

@description('Enable the background pruning/cleanup hosted services.')
param enableBackgroundSweeps bool = true

@description('Require all client events to be signed.')
param requireSignedEvents bool = true

var tags = {
  application: 'vesta-relay'
  environment: environmentName
  managedBy: 'bicep'
}

var resourceGroupName = '${namePrefix}-${environmentName}-rg'
var workspaceName = '${namePrefix}-${environmentName}-logs'
var appInsightsName = '${namePrefix}-${environmentName}-ai'
var postgresServerName = '${namePrefix}-${environmentName}-pg'
var keyVaultName = '${namePrefix}-${environmentName}-kv'
var appServicePlanName = '${namePrefix}-${environmentName}-plan'

var connectionStringSecretName = 'VestaConnectionString'
var vestaConnectionString = 'Host=${postgres.outputs.fullyQualifiedDomainName};Port=5432;Database=${postgresDatabaseName};Username=${postgresAdminLogin};Password=${postgresAdminPassword};SSL Mode=Require;Trust Server Certificate=true'

resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  scope: resourceGroup
  params: {
    location: location
    workspaceName: workspaceName
    appInsightsName: appInsightsName
    tags: tags
  }
}

module postgres 'modules/postgres.bicep' = {
  name: 'postgres'
  scope: resourceGroup
  params: {
    location: location
    serverName: postgresServerName
    databaseName: postgresDatabaseName
    administratorLogin: postgresAdminLogin
    administratorPassword: postgresAdminPassword
    skuName: postgresSkuName
    skuTier: postgresSkuTier
    tags: tags
  }
}

module keyVault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  scope: resourceGroup
  params: {
    location: location
    keyVaultName: keyVaultName
    secrets: {
      '${connectionStringSecretName}': vestaConnectionString
    }
    tags: tags
  }
}

module relay 'modules/webapp.bicep' = {
  name: 'relay-webapp'
  scope: resourceGroup
  params: {
    location: location
    appServicePlanName: appServicePlanName
    webAppName: webAppName
    skuName: appServicePlanSku
    clientAffinityEnabled: false
    tags: tags
    appSettings: {
      ASPNETCORE_ENVIRONMENT: 'Production'
      APPLICATIONINSIGHTS_CONNECTION_STRING: monitoring.outputs.connectionString
      ConnectionStrings__Vesta: '@Microsoft.KeyVault(VaultName=${keyVault.outputs.keyVaultName};SecretName=${connectionStringSecretName})'
      Admin__BootstrapPublicKeys__0: relayAdminPublicKey
      Protocol__RequireSignedEvents: string(requireSignedEvents)
      Protocol__RequireAppRegistration: 'true'
      EventCleanup__Enabled: string(enableBackgroundSweeps)
      AppQuotaPruner__Enabled: string(enableBackgroundSweeps)
      ChannelDeletionPruner__Enabled: string(enableBackgroundSweeps)
    }
  }
}

module keyVaultAccess 'modules/keyvault-access.bicep' = {
  name: 'keyvault-access'
  scope: resourceGroup
  params: {
    keyVaultName: keyVault.outputs.keyVaultName
    principalId: relay.outputs.principalId
  }
}

output relayHostName string = relay.outputs.defaultHostName
output relayBaseUrl string = 'https://${relay.outputs.defaultHostName}'
output relayWebSocketUrl string = 'wss://${relay.outputs.defaultHostName}/ws'
output keyVaultName string = keyVault.outputs.keyVaultName
output postgresServerName string = postgres.outputs.serverName
