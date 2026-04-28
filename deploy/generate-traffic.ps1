#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Generates sample API traffic for the AI Gateway Dev Portal dashboards.
.DESCRIPTION
    Sends varied chat completion requests through APIM to populate Logs, Tokens,
    Requests, Performance, and Availability data in Azure Monitor.
#>
param(
    [string]$GatewayUrl = "https://bsapim-dev-northcentralus-001.azure-api.net",
    [string]$ApiKey = "9d946992317b434e9602d1a9de7583dc",
    [string]$Model = "gpt-5.4-mini",
    [string]$ApiVersion = "2025-03-01-preview",
    [int]$BatchCount = 3
)

$ErrorActionPreference = "Continue"
$endpoint = "$GatewayUrl/inference/openai/deployments/$Model/chat/completions?api-version=$ApiVersion"

$headers = @{
    "api-key"      = $ApiKey
    "Content-Type" = "application/json"
}

# Diverse retail-themed prompts to generate varied token counts and response types
$prompts = @(
    # Short queries (low tokens)
    @{ role = "user"; content = "What is Sierra Gold Tequila?" }
    @{ role = "user"; content = "List the top 3 premium tequila brands." }
    @{ role = "user"; content = "What's the difference between reposado and anejo?" }

    # Medium queries (moderate tokens)
    @{ role = "user"; content = "Analyze the market position of Sierra Gold Tequila in the premium spirits segment. Include key competitors and pricing strategy." }
    @{ role = "user"; content = "Describe the distribution challenges for premium spirits in the Florida market, focusing on on-premise vs off-premise channels." }
    @{ role = "user"; content = "What are the key consumer trends in the tequila category for 2025? How should brands position premium bourbon against competitors?" }

    # Long/complex queries (higher tokens)
    @{ role = "user"; content = "You are a brand analytics expert. Provide a comprehensive quarterly review of Sierra Gold Tequila performance across configured regions. Cover: 1) Year-over-year depletion trends, 2) Competitive landscape vs key competitors, 3) On-premise velocity in key metros, 4) Distributor sentiment and inventory levels, 5) Recommended promotional strategies for Q3." }
    @{ role = "user"; content = "Create a detailed SWOT analysis for the tenant brand portfolio. For each brand, assess market share trajectory, consumer perception shifts, and competitive threats." }

    # Analytics queries
    @{ role = "user"; content = "What KPIs should a spirits brand manager track for depletion analysis?" }
    @{ role = "user"; content = "Explain the concept of 'weeks on hand' inventory metric in beverage distribution." }
    @{ role = "user"; content = "How do seasonal trends affect tequila sales in different US regions?" }

    # Edge cases for variety
    @{ role = "user"; content = "Compare Summit Vodka vs competitor vodka market performance." }
    @{ role = "user"; content = "What is the typical margin structure for premium spirits at a bar?" }
    @{ role = "user"; content = "Summarize recent trends in emerging competitor brands and their market impact." }
)

$systemMessage = @{
    role    = "system"
    content = "You are Retail Pulse, an AI analytics assistant for retail and CPG brands. You provide insights on brand performance, market trends, and distribution analytics."
}

# Different max_completion_tokens values for variety
$tokenLimits = @(100, 200, 300, 500, 150, 250, 400, 600, 100, 200, 300, 150, 250, 200, 350)

$totalRequests = $prompts.Count * $BatchCount
$completed = 0
$succeeded = 0
$failed = 0

Write-Host "`n🥃 Retail Pulse Traffic Generator" -ForegroundColor Cyan
Write-Host "=================================" -ForegroundColor Cyan
Write-Host "Endpoint: $endpoint"
Write-Host "Total requests to send: $totalRequests"
Write-Host "Batches: $BatchCount x $($prompts.Count) prompts`n"

for ($batch = 1; $batch -le $BatchCount; $batch++) {
    Write-Host "--- Batch $batch of $BatchCount ---" -ForegroundColor Yellow

    for ($i = 0; $i -lt $prompts.Count; $i++) {
        $prompt = $prompts[$i]
        $maxTokens = $tokenLimits[$i % $tokenLimits.Count]

        # Vary temperature across requests
        $temps = @(0.3, 0.5, 0.7, 0.9, 0.4, 0.6, 0.8, 0.5, 0.3, 0.7, 0.6, 0.4, 0.8, 0.5, 0.7)
        $temp = $temps[$i % $temps.Count]

        $body = @{
            messages              = @($systemMessage, $prompt)
            max_completion_tokens = $maxTokens
            temperature           = $temp
        } | ConvertTo-Json -Depth 5

        $completed++
        $shortPrompt = $prompt.content.Substring(0, [Math]::Min(60, $prompt.content.Length))
        Write-Host "  [$completed/$totalRequests] $shortPrompt..." -NoNewline

        try {
            $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
            $response = Invoke-RestMethod -Uri $endpoint -Method Post -Headers $headers -Body $body -TimeoutSec 60
            $stopwatch.Stop()

            $tokens = if ($response.usage) { "$($response.usage.prompt_tokens)/$($response.usage.completion_tokens)" } else { "n/a" }
            Write-Host " ✅ ${tokens} tokens, $($stopwatch.ElapsedMilliseconds)ms" -ForegroundColor Green
            $succeeded++
        }
        catch {
            Write-Host " ❌ $($_.Exception.Message)" -ForegroundColor Red
            $failed++
        }

        # Small delay between requests to spread data over time (and avoid rate limiting)
        Start-Sleep -Milliseconds 2000
    }

    if ($batch -lt $BatchCount) {
        Write-Host "`nWaiting 5s between batches..." -ForegroundColor Gray
        Start-Sleep -Seconds 5
    }
}

Write-Host "`n=================================" -ForegroundColor Cyan
Write-Host "✅ Completed: $succeeded succeeded, $failed failed out of $totalRequests total" -ForegroundColor Cyan
Write-Host "Data will appear in Azure Monitor within 5-10 minutes." -ForegroundColor Yellow
Write-Host "Check the portal at: https://lemon-field-072928310.7.azurestaticapps.net" -ForegroundColor Yellow
