#Requires -Version 7.0
<#
.SYNOPSIS
    G4 live production acceptance sweep — every curated Prompt-ideas + Charts entry
    against the deployed stack.

.DESCRIPTION
    Issue #59 gate G4. Submits all 26 curated prompts from
    src/RetailPulse.Web/src/constants/prompts.ts to the deployed authenticated
    /api/chat and asserts the G2 acceptance contract per prompt:

      1. HTTP 200
      2. non-empty assistant reply
      3. routed to a specialist (never the council)
      4. at least one tool invocation in the span stream
      5. no leaked chart JSON in the prose reply
      6. chart prompts  -> expected ChartSpec type with >= MinMarks finite marks
         prose  prompts  -> no chart emitted (the #76 Group A chart-on-prose invariant)

    Auth is a delegated Entra token from the caller's own `az login` context.
    The token is never printed.
#>
param(
    [string]$ApiOrigin = $env:AZURE_API_APP_URL,
    [string]$ApiResource = 'api://b03317ab-a407-49cc-8769-0a15062777b1',
    [int]$TimeoutSec = 180,
    [string]$JsonOut
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ApiOrigin)) {
    throw 'ApiOrigin not set. Pass -ApiOrigin or set AZURE_API_APP_URL.'
}
$ApiOrigin = $ApiOrigin.TrimEnd('/')

# ── curated prompt set (verbatim from prompts.ts) ──────────────────────────
# ExpectedChart = $null  -> prose prompt (must NOT emit a chart)
# MinMarks mirrors ChartAcceptanceManifest.Cases
$prompts = @(
    @{ C = 'general';          P = 'Compare depletion trends across all regions for this quarter';                      Chart = $null;           Marks = 0 }
    @{ C = 'general';          P = 'Which brands are growing fastest year-over-year across the portfolio?';             Chart = $null;           Marks = 0 }
    @{ C = 'general';          P = 'Show me field sentiment for our top 3 brands in the Southeast';                     Chart = $null;           Marks = 0 }
    @{ C = 'grocery';          P = 'How are FreshMart depletions trending in the Northeast this quarter?';              Chart = $null;           Marks = 0 }
    @{ C = 'grocery';          P = 'Compare Harvest Table vs FreshMart sell-through rates by region';                   Chart = $null;           Marks = 0 }
    @{ C = 'grocery';          P = 'What is the field sentiment for Harvest Table Meal Kits in the Midwest?';           Chart = $null;           Marks = 0 }
    @{ C = 'qsr';              P = 'How is Apex Grill performing in the Southwest this quarter?';                       Chart = $null;           Marks = 0 }
    @{ C = 'qsr';              P = 'Compare Coastline Tacos vs Apex Grill depletions across all regions';               Chart = 'groupedBar';    Marks = 4 }
    @{ C = 'qsr';              P = 'What is the field sentiment for Coastline Tacos in the West Coast?';                Chart = $null;           Marks = 0 }
    @{ C = 'home-improvement'; P = 'Show me Pinnacle Hardware depletion stats in the Midwest for Q1';                   Chart = $null;           Marks = 0 }
    @{ C = 'home-improvement'; P = 'How is Summit Outdoor performing in the Southeast vs West Coast?';                  Chart = $null;           Marks = 0 }
    @{ C = 'home-improvement'; P = 'What is the field sentiment for Pinnacle Hardware Power Tools in the Southwest?';   Chart = $null;           Marks = 0 }
    @{ C = 'office-supply';    P = 'How are ClearDesk depletions trending in the Northeast this quarter?';              Chart = $null;           Marks = 0 }
    @{ C = 'office-supply';    P = 'Compare ClearDesk Technology vs Paper Products sell-through by region';             Chart = $null;           Marks = 0 }
    @{ C = 'office-supply';    P = 'What is the field sentiment for ClearDesk in the Southeast?';                       Chart = $null;           Marks = 0 }
    @{ C = 'furniture';        P = 'Show me Urban Living depletion trends across all regions this quarter';             Chart = $null;           Marks = 0 }
    @{ C = 'furniture';        P = 'Compare Foundry Home vs Urban Living performance in the West Coast';                Chart = $null;           Marks = 0 }
    @{ C = 'furniture';        P = 'What is the field sentiment for Urban Living in the Pacific Northwest?';            Chart = $null;           Marks = 0 }
    @{ C = 'charts';           P = 'Create a line chart showing Sierra Gold Tequila depletion trends across all regions'; Chart = 'line';        Marks = 2 }
    @{ C = 'charts';           P = 'Show me a bar chart comparing depletion velocity for all spirits brands in the Northeast'; Chart = 'bar';    Marks = 3 }
    @{ C = 'charts';           P = 'Create a pie chart showing market share breakdown for our grocery brands nationally'; Chart = 'pie';         Marks = 2 }
    @{ C = 'charts';           P = 'Show a grouped bar chart comparing FreshMart and Harvest Table across all regions'; Chart = 'groupedBar';    Marks = 12 }
    @{ C = 'charts';           P = 'Create a donut chart of Apex Grill variant mix in the Southwest';                   Chart = 'donut';         Marks = 2 }
    @{ C = 'charts';           P = 'Show a horizontal bar chart ranking all brands by depletion growth rate';           Chart = 'horizontalBar'; Marks = 6 }
    @{ C = 'charts';           P = 'Create a table showing depletion stats for all home improvement brands by region';  Chart = 'table';         Marks = 2 }
    @{ C = 'charts';           P = 'Show a gauge chart for Pinnacle Hardware inventory health in the Midwest';          Chart = 'gauge';         Marks = 1 }
)

function Get-Token {
    $tok = az account get-access-token --resource $ApiResource --query accessToken -o tsv --only-show-errors 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($tok)) {
        throw "Failed to acquire a delegated token for $ApiResource. Run 'az login'."
    }
    return ($tok | Out-String).Trim()
}

# Chart JSON leaking into the prose reply (G2). Matches a fenced json block or a
# bare ChartSpec-shaped object.
function Test-LeakedChartJson([string]$reply) {
    if ([string]::IsNullOrWhiteSpace($reply)) { return $false }
    if ($reply -match '(?s)```\s*json') { return $true }
    if ($reply -match '"type"\s*:\s*"(line|bar|groupedBar|pie|donut|horizontalBar|stackedBar|gauge|table)"') { return $true }
    if ($reply -match '"xAxisTitle"|"yAxisTitle"|"legend"\s*:') { return $true }
    return $false
}

function Measure-Marks($chart) {
    $n = 0
    foreach ($series in @($chart.data)) {
        foreach ($v in @($series.values)) {
            if ($null -ne $v.y -and [double]::TryParse([string]$v.y, [ref]([double]$script:_tmp))) { $n++ }
            elseif ($v.y -is [double] -and -not [double]::IsNaN($v.y)) { $n++ }
        }
    }
    return $n
}

$token = Get-Token
$results = [System.Collections.Generic.List[object]]::new()
$i = 0

# /api/chat is behind the "strict" fixed-window limiter: 10 permits / minute,
# QueueLimit 0. Pace requests so the sweep never trips it, and honour Retry-After
# if a window boundary still bites.
$minSpacing = [TimeSpan]::FromSeconds(7)
$lastSent = [datetime]::MinValue

Write-Host "G4 production sweep - $($prompts.Count) curated prompts against $ApiOrigin" -ForegroundColor Cyan
Write-Host ""

foreach ($p in $prompts) {
    $i++
    $failures = [System.Collections.Generic.List[string]]::new()
    $cacheHit = $false
    $status = 0; $body = $null; $chartTypes = ''; $marks = 0; $agent = ''; $toolCalls = 0

    $sw = [Diagnostics.Stopwatch]::StartNew()
    for ($attempt = 1; $attempt -le 4; $attempt++) {
        $wait = $minSpacing - ([datetime]::UtcNow - $lastSent)
        if ($wait -gt [TimeSpan]::Zero) { Start-Sleep -Milliseconds $wait.TotalMilliseconds }

        $status = 0; $body = $null
        try {
            $lastSent = [datetime]::UtcNow
            $resp = Invoke-WebRequest -Method Post -Uri "$ApiOrigin/api/chat" `
                -Headers @{ Authorization = "Bearer $token"; 'Content-Type' = 'application/json'; Accept = 'application/json' } `
                -Body (@{ message = $p.P } | ConvertTo-Json -Compress) `
                -SkipHttpErrorCheck -TimeoutSec $TimeoutSec
            $status = [int]$resp.StatusCode
            if ($status -eq 200) { $body = $resp.Content | ConvertFrom-Json; break }
            if ($status -eq 429) {
                $retryAfter = 20
                if ($resp.Headers['Retry-After']) { [int]::TryParse(($resp.Headers['Retry-After'] | Select-Object -First 1), [ref]$retryAfter) | Out-Null }
                Write-Host ("          429 - backing off {0}s (attempt {1})" -f $retryAfter, $attempt) -ForegroundColor DarkYellow
                Start-Sleep -Seconds ([Math]::Max(5, $retryAfter))
                continue
            }
            if ($status -eq 401) { $token = Get-Token; continue }
            $errBody = ''
            try { $errBody = ($resp.Content | Out-String).Trim() } catch { }
            if ($errBody) { $failures.Add("HTTP $status body: $($errBody.Substring(0,[Math]::Min(300,$errBody.Length)))") }
            break
        }
        catch { $failures.Add("transport: $($_.Exception.Message)"); break }
    }

    if ($status -ne 200) {
        $failures.Add("HTTP $status")
    }
    else {
        # 2. non-empty reply
        $reply = $body.reply
        if ([string]::IsNullOrWhiteSpace($reply)) { $failures.Add('empty reply') }

        # 3. routed to a specialist
        $agent = if ($body.routing) { "$($body.routing.agentKey)/$($body.routing.intent)" } else { '' }
        if ([string]::IsNullOrWhiteSpace($agent) -or $agent -eq '/') { $failures.Add('no routing metadata') }
        elseif ($agent -match 'council') { $failures.Add("routed to council ($agent)") }

        # 4. at least one tool invocation.
        #    A cache hit legitimately invokes no tools — that is the point of the
        #    cache. The G2 "at least one tool invoked" contract applies to a fresh
        #    execution. A cached turn is instead held to the stronger #170 bar: it
        #    must still carry the routing and charts of the answer it replays,
        #    which the chart assertions below enforce for every prompt either way.
        $cacheHit = @($body.spans | Where-Object { $null -ne $_ -and $_.type -eq 'cache' }).Count -gt 0
        $toolCalls = @($body.spans | Where-Object { $null -ne $_ -and $_.type -eq 'tool_call' }).Count
        if (-not $cacheHit -and $toolCalls -lt 1) { $failures.Add('no tool_call span') }

        # 5. no leaked chart JSON
        if (Test-LeakedChartJson $reply) { $failures.Add('leaked chart JSON in reply') }

        # 6. chart expectations
        # NOTE: @($null) yields a 1-element array in PowerShell, so a null `charts`
        # would otherwise look like "one chart". Filter nulls before counting.
        $charts = @($body.charts | Where-Object { $null -ne $_ })
        $chartTypes = ($charts | ForEach-Object { $_.type }) -join ','
        if ($null -ne $p.Chart) {
            $match = $charts | Where-Object { $_.type -eq $p.Chart } | Select-Object -First 1
            if (-not $match) { $failures.Add("expected chart '$($p.Chart)', saw '[$chartTypes]'") }
            else {
                $marks = Measure-Marks $match
                if ($marks -lt $p.Marks) { $failures.Add("chart '$($p.Chart)' had $marks marks, needs >= $($p.Marks)") }
            }
        }
        elseif ($charts.Count -gt 0) {
            $failures.Add("prose prompt emitted chart(s) [$chartTypes]")
        }
    }

    $sw.Stop()
    $ok = ($failures.Count -eq 0)
    $results.Add([pscustomobject]@{
            N = $i; Category = $p.C; Prompt = $p.P; Pass = $ok
            Status = $status; Agent = $agent; ToolCalls = $toolCalls; CacheHit = $cacheHit
            ExpectedChart = $p.Chart; ChartTypes = $chartTypes; Marks = $marks
            Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
            Failures = ($failures -join '; ')
        })

    $tag = if ($ok) { 'PASS' } else { 'FAIL' }
    $col = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("[{0,2}/{1}] {2}  {3,-6} {4,-34} {5}" -f $i, $prompts.Count, $tag, "$($sw.Elapsed.TotalSeconds.ToString('0.0'))s", $p.C, $p.P.Substring(0, [Math]::Min(52, $p.P.Length))) -ForegroundColor $col
    if (-not $ok) { Write-Host ("          -> " + ($failures -join '; ')) -ForegroundColor Yellow }
}

$passed = @($results | Where-Object Pass).Count
Write-Host ""
Write-Host ("RESULT: {0}/{1} passed" -f $passed, $results.Count) -ForegroundColor $(if ($passed -eq $results.Count) { 'Green' } else { 'Red' })

if ($JsonOut) { $results | ConvertTo-Json -Depth 6 | Set-Content -Path $JsonOut -Encoding utf8; Write-Host "results -> $JsonOut" }

exit $(if ($passed -eq $results.Count) { 0 } else { 1 })
