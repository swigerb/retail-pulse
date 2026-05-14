using './main.bicep'

param environmentName = readEnvironmentVariable('AZURE_ENV_NAME', 'dev')
param location = readEnvironmentVariable('AZURE_LOCATION', 'northcentralus')
param openAiEndpoint = readEnvironmentVariable('AZURE_OPENAI_ENDPOINT', '')
param openAiApiKey = readEnvironmentVariable('AZURE_OPENAI_API_KEY', '')
