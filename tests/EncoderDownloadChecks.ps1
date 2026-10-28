$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $repoRoot ('artifacts/encoder-download-checks/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null

# Load exactly the production download function without running the preparation script.
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $repoRoot 'tools/prepare-encoders.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw 'Encoder preparation script has syntax errors.' }
$function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-VerifiedEncoderArchive' }, $true)
if ($null -eq $function) { throw 'Production verified-download function was not found.' }
. ([scriptblock]::Create($function.Extent.Text))

$script:requests = 0
$script:failures = 0
$script:corruptResponse = $false
$script:timeout = 0
$script:payload = [Text.Encoding]::UTF8.GetBytes('small pinned encoder fixture')
function Invoke-WebRequest {
    [CmdletBinding()]
    param([string]$Uri, [string]$OutFile, [int]$TimeoutSec)
    $script:requests++
    $script:timeout = $TimeoutSec
    if ($script:requests -le $script:failures) {
        [IO.File]::WriteAllText($OutFile, 'truncated response')
        throw [IO.IOException]::new('injected interrupted download')
    }
    $bytes = if ($script:corruptResponse) { [Text.Encoding]::UTF8.GetBytes('untrusted response') } else { $script:payload }
    [IO.File]::WriteAllBytes($OutFile, $bytes)
}
function Start-Sleep { param([int]$Milliseconds) }
$script:checks = 0
function Assert-Check([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
    $script:checks++
}

$reference = Join-Path $fixture 'reference.bin'
[IO.File]::WriteAllBytes($reference, $script:payload)
$hash = (Get-FileHash -LiteralPath $reference -Algorithm SHA256).Hash
$archive = Join-Path $fixture 'encoder.zip'
$script:failures = 1
Get-VerifiedEncoderArchive -Url 'https://invalid.test/pinned.zip' -Archive $archive -ExpectedHash $hash -TimeoutSeconds 7 -Attempts 3
Assert-Check ($script:requests -eq 2 -and $script:timeout -eq 7) 'Interrupted download retries finitely with the requested timeout.'
Assert-Check ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -eq $hash) 'Only the verified complete response is published.'
Assert-Check (@(Get-ChildItem -LiteralPath $fixture -Filter '*.download').Count -eq 0) 'Interrupted partial responses are cleaned.'
$script:requests = 0
Get-VerifiedEncoderArchive -Url 'https://invalid.test/pinned.zip' -Archive $archive -ExpectedHash $hash
Assert-Check ($script:requests -eq 0) 'Verified local cache avoids all network requests.'
[IO.File]::WriteAllText($archive, 'old incomplete cache')
$script:failures = 0
Get-VerifiedEncoderArchive -Url 'https://invalid.test/pinned.zip' -Archive $archive -ExpectedHash $hash
Assert-Check ($script:requests -eq 1 -and (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -eq $hash) 'Damaged old cache repairs from the same pinned source.'

$script:requests = 0
$script:failures = [int]::MaxValue
$missing = Join-Path $fixture 'missing.zip'
$failure = $null
try { Get-VerifiedEncoderArchive -Url 'https://invalid.test/pinned.zip' -Archive $missing -ExpectedHash $hash -Attempts 2 }
catch { $failure = $_.Exception }
Assert-Check ($null -ne $failure -and $failure.InnerException.Message -eq 'injected interrupted download') 'Exhausted retries preserve the original download failure.'
Assert-Check ($script:requests -eq 2 -and -not (Test-Path -LiteralPath $missing)) 'Repeated network failure cannot publish a partial cache.'

$script:requests = 0
$script:failures = 0
$script:corruptResponse = $true
[IO.File]::WriteAllText($archive, 'existing cache preserved on failure')
$failure = $null
try { Get-VerifiedEncoderArchive -Url 'https://invalid.test/pinned.zip' -Archive $archive -ExpectedHash $hash -Attempts 2 }
catch { $failure = $_.Exception }
Assert-Check ($null -ne $failure -and $failure.Message.Contains('SHA-256 mismatch') -and $script:requests -eq 2) 'Hash mismatch is explicit and retry-bounded.'
Assert-Check ([IO.File]::ReadAllText($archive) -eq 'existing cache preserved on failure') 'Unverified responses cannot replace an existing archive.'
Assert-Check (@(Get-ChildItem -LiteralPath $fixture -Filter '*.download').Count -eq 0) 'Failed verification leaves no poisoned temporary download.'
Write-Output "PASS: $script:checks encoder-download checks. No network, microphone, desktop or browser used. Fixtures: $fixture"
