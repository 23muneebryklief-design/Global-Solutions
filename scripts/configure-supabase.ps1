param(
    [Parameter(Mandatory)][string]$DatabaseHost,
    [Parameter(Mandatory)][string]$DatabaseUser,
    [int]$Port = 5432
)
$ErrorActionPreference = 'Stop'
$apiProject = Join-Path (Split-Path $PSScriptRoot -Parent) 'src/GlobalTalentSolutions.Api'
$securePassword = Read-Host 'Supabase database password' -AsSecureString
$passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
try {
    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    $builder['Host'] = $DatabaseHost
    $builder['Port'] = $Port
    $builder['Database'] = 'postgres'
    $builder['Username'] = $DatabaseUser
    $builder['Password'] = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
    $builder['SSL Mode'] = 'VerifyFull'
    $builder['Timeout'] = 10
    $builder['Command Timeout'] = 15
    # Pipe JSON via stdin: the password is not placed in the command line/history.
    @{ 'ConnectionStrings:Supabase' = $builder.ConnectionString } | ConvertTo-Json -Compress |
        dotnet user-secrets set --project $apiProject | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not save the local database configuration.' }
    Write-Output 'Saved Supabase connection in local .NET user secrets. Restart the API.'
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
    $builder = $null
    $securePassword.Dispose()
}
