<#
  Registers forgekeeper-open:// for the current user (HKCU, no admin rights needed).
    powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -Prefix 'W:\3d printing'
    powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -Uninstall
#>
param(
    [string[]]$Prefix = @('\\samba.k8s.inxaos.com\warehousepool\3d printing', 'W:\3d printing'),
    [switch]$Uninstall
)

$key = 'HKCU:\Software\Classes\forgekeeper-open'
$dest = Join-Path $env:LOCALAPPDATA 'Forgekeeper'
$script = Join-Path $dest 'forgekeeper-open.ps1'

if ($Uninstall) {
    if (Test-Path $key) { Remove-Item $key -Recurse }
    Write-Host 'Unregistered forgekeeper-open:// (script left in place at' $script ')'
    exit 0
}

New-Item -ItemType Directory -Force -Path $dest | Out-Null
$src = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'forgekeeper-open.ps1')
$list = ($Prefix | ForEach-Object { "'" + ($_ -replace "'", "''") + "'" }) -join ', '
$src = $src -replace '(?m)^\$AllowedPrefixes = .*$', ('$$AllowedPrefixes = @(' + ($list -replace '\$', '$$$$') + ')')
Set-Content -LiteralPath $script -Value $src -Encoding UTF8

New-Item -Force -Path $key | Out-Null
Set-ItemProperty -Path $key -Name '(default)' -Value 'URL:Forgekeeper Open'
Set-ItemProperty -Path $key -Name 'URL Protocol' -Value ''
New-Item -Force -Path "$key\shell\open\command" | Out-Null
$cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`" `"%1`""
Set-ItemProperty -Path "$key\shell\open\command" -Name '(default)' -Value $cmd

Write-Host "Registered forgekeeper-open:// -> $script"
Write-Host "Allowed prefixes: $($Prefix -join '; ')"
Write-Host ('Test: Start-Process "forgekeeper-open://open?path=' + [uri]::EscapeDataString($Prefix[0]) + '"')
