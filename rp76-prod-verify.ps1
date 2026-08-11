#!/usr/bin/env pwsh
# Live prod verification for issue #76 — never prints the token.
[CmdletBinding()]
param(
    [string]$ApiBase = 'https://ca-retailpulse-api.happysand-212b1b0a.eastus.azurecontainerapps.io',
    [string]$Scope   = 'api://b03317ab-a407-49cc-8769-0a15062777b1/access_as_user'
)

$ErrorActionPreference = 'Stop'
$token = az account get-access-token --scope $Scope --query accessToken -o tsv
if ($LASTEXITCODE -ne 0 -or -not $token) { throw "token acquisition failed" }
$headers = @{ Authorization = "Bearer $token"; 'Content-Type' = 'application/json' }

function Clear-ResponseCache {
    for ($i=1; $i -le 5; $i++) {
        try {
            Invoke-RestMethod -Method Delete -Uri "$ApiBase/api/cache" -Headers $headers -TimeoutSec 30 | Out-Null
            return
        } catch {
            $sc = $_.Exception.Response.StatusCode.value__
            if ($sc -eq 429) {
                $wait = 3 * $i
                Write-Host "  cache clear 429; backoff ${wait}s" -ForegroundColor DarkYellow
                Start-Sleep -Seconds $wait
                continue
            }
            Write-Host "  cache clear failed: $_" -ForegroundColor DarkYellow
            return
        }
    }
}

function Invoke-Prompt {
    param([string]$Prompt, [string]$SessionId)
    $body = @{ message = $Prompt; sessionId = $SessionId } | ConvertTo-Json -Compress
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $resp = $null
    for ($i=1; $i -le 4; $i++) {
        try {
            $resp = Invoke-RestMethod -Method Post -Uri "$ApiBase/api/chat" -Headers $headers -Body $body -TimeoutSec 120
            break
        } catch {
            $sc = 0; try { $sc = $_.Exception.Response.StatusCode.value__ } catch {}
            if ($sc -eq 429 -or $sc -ge 500) {
                $wait = 4 * $i
                Write-Host "  chat $sc; backoff ${wait}s" -ForegroundColor DarkYellow
                Start-Sleep -Seconds $wait
                continue
            }
            throw
        }
    }
    if ($null -eq $resp) { throw "chat request failed after retries" }
    $sw.Stop()
    $charts = if ($null -eq $resp.charts) { @() } else { @($resp.charts | Where-Object { $_ -ne $null }) }
    $chartTypes = @($charts | ForEach-Object { $_.type })
    $entities = @{}
    foreach ($c in $charts) {
        foreach ($s in $c.data) { $entities[$s.legend] = $true }
        foreach ($s in $c.data) { foreach ($v in $s.values) { if ($v.label) { $entities[$v.label] = $true } } }
    }
    # Also scan reply text for leaked JSON: crude — check for both { and "type" keyword patterns
    $reply = [string]$resp.reply
    $leaked = ($reply -match '```json' -or $reply -match '"chartSpec"' -or $reply -match '"XAxisTitle"' -or ($reply.Contains('"type"') -and $reply.Contains('"data"') -and $reply.Contains('"legend"')))
    [pscustomobject]@{
        Prompt      = $Prompt
        Ms          = [int]$sw.ElapsedMilliseconds
        ChartCount  = $charts.Count
        ChartType   = ($chartTypes -join ',')
        Entities    = ($entities.Keys | Sort-Object) -join '|'
        DistinctCalls = $resp.toolContext.distinctCalls
        IsChartIntent = $resp.toolContext.isChartIntent
        LeakedJson  = $leaked
        ReplyLen    = $reply.Length
        Reply       = $reply
    }
}

$results = New-Object System.Collections.Generic.List[object]

$spot = @{
    '8'  = 'Compare Coastline Tacos vs Apex Grill depletions across all regions'
    '17' = 'Compare Foundry Home vs Urban Living performance in the West Coast'
    '25' = 'Create a table showing depletion stats for all home improvement brands by region'
    '24' = 'Show a horizontal bar chart ranking all brands by depletion growth rate'
    '1'  = 'Compare depletion trends across all regions for this quarter'
    '14' = 'Compare ClearDesk Technology vs Paper Products sell-through by region'
    '16' = 'Show me Urban Living depletion trends across all regions this quarter'
}

foreach ($k in $spot.Keys) {
    Clear-ResponseCache
    $sid = "rp76-verify-$k-$(Get-Random -Maximum 999999)"
    Write-Host ">> spot #$k session=$sid" -ForegroundColor Cyan
    $r = Invoke-Prompt -Prompt $spot[$k] -SessionId $sid
    $r | Add-Member -NotePropertyName TestId -NotePropertyValue "spot-$k"
    $results.Add($r)
    Write-Host ("   {0} calls={1} chartCount={2} type={3} leaked={4} ms={5}" -f $k, $r.DistinctCalls, $r.ChartCount, $r.ChartType, $r.LeakedJson, $r.Ms)
}

$det = @('19','21','23','26')
$detPrompts = @{
    '19' = 'Create a line chart showing Sierra Gold Tequila depletion trends across all regions'
    '21' = 'Create a pie chart showing market share breakdown for our grocery brands nationally'
    '23' = 'Create a donut chart of Apex Grill variant mix in the Southwest'
    '26' = 'Show a gauge chart for Pinnacle Hardware inventory health in the Midwest'
}

foreach ($k in $det) {
    foreach ($run in 1,2) {
        Clear-ResponseCache
        $sid = "rp76-det-$k-r$run-$(Get-Random -Maximum 999999)"
        Write-Host ">> det #$k run=$run session=$sid" -ForegroundColor Yellow
        $r = Invoke-Prompt -Prompt $detPrompts[$k] -SessionId $sid
        $r | Add-Member -NotePropertyName TestId -NotePropertyValue "det-$k-r$run"
        $results.Add($r)
        Write-Host ("   det#{0} r{1} calls={2} chartCount={3} type={4}" -f $k, $run, $r.DistinctCalls, $r.ChartCount, $r.ChartType)
    }
}

$results | Select-Object TestId, Ms, DistinctCalls, ChartCount, ChartType, LeakedJson, ReplyLen | Format-Table -AutoSize | Out-String | Write-Host

$results | Export-Clixml rp76-verify-results.xml
Write-Host "=== SUMMARY ==="
foreach ($r in $results) {
    Write-Host ("{0}: calls={1} chartCount={2} type='{3}' leaked={4} entities='{5}'" -f $r.TestId, $r.DistinctCalls, $r.ChartCount, $r.ChartType, $r.LeakedJson, $r.Entities)
}
