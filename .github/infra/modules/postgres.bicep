// Azure Database for PostgreSQL Flexible Server (Burstable for dev) + one database.
// The relay uses raw Npgsql + LISTEN/NOTIFY, so this MUST stay PostgreSQL.

@description('Azure region for the server.')
param location string

@description('Globally-unique Flexible Server name (lowercase letters, digits, hyphens).')
param serverName string

@description('Name of the application database to create on the server.')
param databaseName string

@description('Administrator login for the server.')
param administratorLogin string

@description('Administrator password for the server.')
@secure()
param administratorPassword string

@description('Compute SKU name. Burstable B1ms is the cheapest tier for dev.')
param skuName string = 'Standard_B1ms'

@description('Compute tier. Burstable for dev, GeneralPurpose for prod.')
@allowed([
  'Burstable'
  'GeneralPurpose'
  'MemoryOptimized'
])
param skuTier string = 'Burstable'

@description('Provisioned storage in GB.')
param storageSizeGB int = 32

@description('PostgreSQL major version. Azure Flexible Server max is currently 16.')
param postgresVersion string = '16'

@description('Backup retention in days.')
param backupRetentionDays int = 7

@description('Tags applied to all resources.')
param tags object = {}

resource server 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: serverName
  location: location
  tags: tags
  sku: {
    name: skuName
    tier: skuTier
  }
  properties: {
    version: postgresVersion
    administratorLogin: administratorLogin
    administratorLoginPassword: administratorPassword
    storage: {
      storageSizeGB: storageSizeGB
    }
    backup: {
      backupRetentionDays: backupRetentionDays
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: {
      mode: 'Disabled'
    }
    authConfig: {
      passwordAuth: 'Enabled'
      activeDirectoryAuth: 'Disabled'
    }
  }
}

// Allow other Azure services (App Service) to reach the server.
// The 0.0.0.0 sentinel range is the documented "Allow Azure services" rule.
resource allowAzure 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: server
  name: 'AllowAllAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: server
  name: databaseName
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

output fullyQualifiedDomainName string = server.properties.fullyQualifiedDomainName
output serverName string = server.name
