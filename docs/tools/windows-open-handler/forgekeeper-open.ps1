<#
  forgekeeper-open:// handler. Opens Explorer at a library folder, or at the folder with a file selected.
  Install with install.ps1 (per user, no admin rights). Chrome asks "Open Forgekeeper Open?" the first time.

    forgekeeper-open://open?path=<url-encoded Windows path>    -> Explorer at that folder
    forgekeeper-open://select?path=<url-encoded Windows path>  -> Explorer at the parent folder, file selected

  Safety: only paths under one of $AllowedPrefixes are accepted, ".." is rejected, and nothing is ever
  executed. The worst a malicious link can do is open an Explorer window inside the library.
#>
param([Parameter(Mandatory = $true)][string]$Url)

# Edit to match Library__WindowsPathPrefix (install.ps1 rewrites this line).
$AllowedPrefixes = @('\\samba.k8s.inxaos.com\warehousepool\3d printing', 'W:\3d printing')

Add-Type -AssemblyName System.Web
function Fail($msg) {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show($msg, 'Forgekeeper') | Out-Null
    exit 1
}

if ($Url -notmatch '^forgekeeper-open://(open|select)/?\?(.*)$') { Fail "Unrecognized link: $Url" }
$action = $Matches[1]
$query = [System.Web.HttpUtility]::ParseQueryString($Matches[2])
$path = $query['path']
if ([string]::IsNullOrWhiteSpace($path)) { Fail 'Link has no path.' }
$path = $path -replace '/', '\'
if ($path -match '(^|\\)\.\.(\\|$)') { Fail "Refusing path with '..': $path" }

$ok = $false
foreach ($p in $AllowedPrefixes) {
    $pp = $p.TrimEnd('\')
    if ($path.Equals($pp, 'OrdinalIgnoreCase') -or $path.StartsWith($pp + '\', 'OrdinalIgnoreCase')) { $ok = $true; break }
}
if (-not $ok) { Fail "Path is outside the Forgekeeper library:`n$path" }

if ($action -eq 'select' -and (Test-Path -LiteralPath $path -PathType Leaf)) {
    Start-Process explorer.exe -ArgumentList "/select,`"$path`""
} elseif (Test-Path -LiteralPath $path -PathType Container) {
    Start-Process explorer.exe -ArgumentList "`"$path`""
} else {
    $parent = Split-Path -LiteralPath $path -Parent
    if ($parent -and (Test-Path -LiteralPath $parent -PathType Container)) {
        Start-Process explorer.exe -ArgumentList "`"$parent`""
    } else {
        Fail "Not found (is the share reachable / signed in?):`n$path"
    }
}
