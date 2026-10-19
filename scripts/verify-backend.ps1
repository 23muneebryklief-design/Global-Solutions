param([string]$Configuration = 'Debug', [int]$Port = 5187)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$apiDll = Join-Path $repoRoot "src/GlobalTalentSolutions.Api/bin/$Configuration/net10.0/GlobalTalentSolutions.Api.dll"
$job = Start-Job -ScriptBlock {
    param($dll, $port)
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = "http://127.0.0.1:$port"
    # This smoke test intentionally starts without a hosted database secret.
    $env:ConnectionStrings__Supabase = ''
    Set-Location (Split-Path $dll -Parent)
    dotnet $dll
} -ArgumentList $apiDll, $Port
try {
    $base = "http://127.0.0.1:$Port"
    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        try {
            $health = Invoke-WebRequest "$base/api/health/live" -TimeoutSec 2
            if ($health.StatusCode -eq 200) { $ready = $true; break }
        } catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $ready) { throw 'API did not start.' }
    if ((Invoke-WebRequest "$base/api/health/ready" -SkipHttpErrorCheck).StatusCode -ne 503) { throw 'Unconfigured database should report not ready.' }
    $swagger = Invoke-RestMethod "$base/swagger/v1/swagger.json"
    if ($swagger.info.title -ne 'Global Talent Solutions API') { throw 'OpenAPI document missing.' }
    if (-not $swagger.paths.'/api/enquiries'.post) { throw 'Public enquiry endpoint missing from OpenAPI.' }
    if ($swagger.paths.'/api/enquiries'.post.security.Count -gt 0) { throw 'Public enquiry documentation incorrectly requires a token.' }
    $adminEnquiryPath = $swagger.paths.PSObject.Properties | Where-Object { $_.Name.TrimEnd('/') -eq '/api/admin/enquiries' } | Select-Object -First 1
    if ($adminEnquiryPath.Value.get.security.Count -eq 0) { throw 'Admin enquiry documentation must require a bearer token.' }
    $portalPath = '/api/portal/conversations/{id}/messages'
    if (-not $swagger.paths.$portalPath.post -or $swagger.paths.$portalPath.get.security.Count -eq 0) { throw 'Protected conversation routes missing from OpenAPI.' }
    if (-not $swagger.paths.'/api/portal/conversations'.get -or -not $swagger.paths.'/api/admin/enquiries/{id}/onboarding'.post) { throw 'Onboarding or portal list route missing.' }
    $portal = Invoke-WebRequest "$base/api/portal/conversations/$([guid]::NewGuid())/messages" -SkipHttpErrorCheck
    if ($portal.StatusCode -ne 401 -or $portal.Headers['Cache-Control'] -notcontains 'no-store') { throw 'Portal must reject anonymous access and prevent caching.' }
    $missing = Invoke-WebRequest "$base/api/not-found" -SkipHttpErrorCheck
    if ($missing.StatusCode -ne 404) { throw 'Expected 404.' }
    $problemText = if ($missing.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($missing.Content) } else { $missing.Content }
    $problem = $problemText | ConvertFrom-Json
    if ($problem.status -ne 404 -or -not $problem.traceId) { throw "Problem details missing status or trace ID: $problemText" }
    if ($missing.Headers['X-Content-Type-Options'] -ne 'nosniff') { throw 'Security header missing.' }
    Write-Output 'PASS: liveness, unconfigured database readiness (503), OpenAPI, problem details, and response headers.'
    $passed = $true
} finally {
    Stop-Job $job
    $logs = Receive-Job $job
    if (-not $passed) { $logs | Write-Output }
    Remove-Job $job
}
