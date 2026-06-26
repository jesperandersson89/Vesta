using '../main.bicep'

// Dev environment parameters for the Vesta relay stack.
// Secrets are read from environment variables at deploy time so they are never
// committed. Set them before deploying, e.g. in PowerShell:
//   $env:VESTA_PG_PASSWORD = '<strong-password>'
//   $env:VESTA_RELAY_ADMIN_PUBLICKEY = '<base64url-ed25519-public-key>'

param environmentName = 'dev'
param location = 'norwayeast'
param namePrefix = 'vesta'

// Web App names are global; change if this one is taken.
param webAppName = 'vestaserver'

param postgresAdminLogin = 'vestaadmin'
param postgresAdminPassword = readEnvironmentVariable('VESTA_PG_PASSWORD')
param postgresDatabaseName = 'vesta'

param relayAdminPublicKey = readEnvironmentVariable('VESTA_RELAY_ADMIN_PUBLICKEY')

// Cheap dev sizing.
param postgresSkuName = 'Standard_B1ms'
param postgresSkuTier = 'Burstable'
param appServicePlanSku = 'B1'

param enableBackgroundSweeps = true
param requireSignedEvents = true
