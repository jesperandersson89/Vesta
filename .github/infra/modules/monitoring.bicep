// Log Analytics workspace + workspace-based Application Insights.
// Shared telemetry sink for the relay web app.

@description('Azure region for the monitoring resources.')
param location string

@description('Name of the Log Analytics workspace.')
param workspaceName string

@description('Name of the Application Insights component.')
param appInsightsName string

@description('Daily ingestion cap in GB for the workspace (cost guard). Use -1 for no cap.')
param dailyQuotaGb int = 1

@description('Tags applied to all resources.')
param tags object = {}

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: workspaceName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
    workspaceCapping: {
      dailyQuotaGb: dailyQuotaGb
    }
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
    }
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    IngestionMode: 'LogAnalytics'
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

output workspaceId string = workspace.id
output connectionString string = appInsights.properties.ConnectionString
