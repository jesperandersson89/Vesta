// Linux App Service Plan + Web App for the Vesta relay.
// WebSockets are required (the relay's primary transport is WS /ws).

@description('Azure region.')
param location string

@description('Name of the App Service Plan.')
param appServicePlanName string

@description('Globally-unique Web App name.')
param webAppName string

@description('App Service Plan SKU (B1 is the cheapest tier that supports Always On + WebSockets).')
param skuName string = 'B1'

@description('Application settings to apply (name -> value). May contain Key Vault references.')
param appSettings object

@description('Enable ARR client affinity. Off for the stateless relay.')
param clientAffinityEnabled bool = false

@description('Tags applied to all resources.')
param tags object = {}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: appServicePlanName
  location: location
  tags: tags
  sku: {
    name: skuName
  }
  kind: 'linux'
  properties: {
    reserved: true
  }
}

resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: webAppName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    clientAffinityEnabled: clientAffinityEnabled
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      webSocketsEnabled: true
      alwaysOn: true
      http20Enabled: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        for setting in items(appSettings): {
          name: setting.key
          value: setting.value
        }
      ]
    }
  }
}

output principalId string = webApp.identity.principalId
output defaultHostName string = webApp.properties.defaultHostName
output webAppName string = webApp.name
