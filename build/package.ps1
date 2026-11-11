<#
.SYNOPSIS
  Builds a signed MSIX of Dump Debugger: the web app, the host, self-contained x64 and x86
  workers, and the sample generator. The package needs no .NET or Windows App SDK install.

.PARAMETER CertificatePath
  A .pfx to sign with. Its subject must match the Publisher in
  src/DumpDebugger.Host/Package.appxmanifest. When omitted, a self-signed development
  certificate (CN=Dump Debugger Dev) is created once and reused from artifacts/signing.

.PARAMETER Install
  After building, trusts the development certificate for the current user and installs the
  package (sideloading must be allowed: Settings > System > For developers).

.EXAMPLE
  ./build/package.ps1 -Install
#>
param(
    [string]$CertificatePath,
    [string]$CertificatePassword = 'dump-debugger-dev',
    [string]$Configuration = 'Release',
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'
New-Item -ItemType Directory -Force $publish | Out-Null

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

Step 'Web app'
Push-Location (Join-Path $root 'web')
npm ci --no-audit --no-fund
npm run build
Pop-Location

Step 'Workers (self-contained, x64 and x86)'
foreach ($rid in 'win-x64', 'win-x86') {
    dotnet publish (Join-Path $root 'src/DumpDebugger.Worker') -c $Configuration -r $rid --self-contained `
        -o (Join-Path $publish "workers/$rid") -nologo -v q
    if ($LASTEXITCODE) { throw "Worker publish failed ($rid)" }
}

Step 'Sample generator (self-contained)'
dotnet publish (Join-Path $root 'src/DumpDebugger.DumpGen') -c $Configuration -r win-x64 --self-contained `
    -o (Join-Path $publish 'dumpgen') -nologo -v q
if ($LASTEXITCODE) { throw 'DumpGen publish failed' }

Step 'Signing certificate'
$thumbprint = $null
if (-not $CertificatePath) {
    $signing = Join-Path $artifacts 'signing'
    New-Item -ItemType Directory -Force $signing | Out-Null
    $CertificatePath = Join-Path $signing 'dump-debugger-dev.pfx'
    if (-not (Test-Path $CertificatePath)) {
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Dump Debugger Dev' `
            -KeyUsage DigitalSignature -FriendlyName 'Dump Debugger development signing' `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
        $secure = ConvertTo-SecureString $CertificatePassword -AsPlainText -Force
        Export-PfxCertificate -Cert $cert -FilePath $CertificatePath -Password $secure | Out-Null
        Export-Certificate -Cert $cert -FilePath (Join-Path $signing 'dump-debugger-dev.cer') | Out-Null
        Write-Host "Created development certificate $CertificatePath"
    }
}

# The MSIX tooling signs from the certificate store (it can't open password-protected PFX
# files), so make sure the certificate is in CurrentUser\My and sign by thumbprint.
$secure = ConvertTo-SecureString $CertificatePassword -AsPlainText -Force
$imported = Import-PfxCertificate -FilePath $CertificatePath -Password $secure -CertStoreLocation 'Cert:\CurrentUser\My'
$thumbprint = $imported.Thumbprint
Write-Host "Signing with $($imported.Subject) ($thumbprint)"

Step 'MSIX'
$packageDir = Join-Path $artifacts 'msix'
dotnet publish (Join-Path $root 'src/DumpDebugger.Host') -c $Configuration -r win-x64 -p:Platform=x64 -p:Packaged=true `
    -p:WorkerX64Dir="$publish\workers\win-x64" -p:WorkerX86Dir="$publish\workers\win-x86" -p:DumpGenDir="$publish\dumpgen" `
    -p:PackageCertificateThumbprint="$thumbprint" `
    -p:AppxPackageDir="$packageDir\" -nologo -v q
if ($LASTEXITCODE) { throw 'Host packaging failed' }

$msix = Get-ChildItem $packageDir -Recurse -Filter '*.msix' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $msix) { throw "No .msix produced under $packageDir" }
$signature = Get-AuthenticodeSignature $msix.FullName
Write-Host "`nPackage: $($msix.FullName) ($([math]::Round($msix.Length / 1MB)) MB)"
Write-Host "Signed by: $($signature.SignerCertificate.Subject) [$($signature.Status)]"

if ($Install) {
    Step 'Install'
    $cer = Join-Path $artifacts 'signing/dump-debugger-dev.cer'
    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    $devMode = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense -eq 1
    if ($admin) {
        # A sideloaded package must chain to a certificate the machine trusts.
        if (Test-Path $cer) { Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null }
        Add-AppxPackage -Path $msix.FullName -ForceUpdateFromAnyVersion
    }
    elseif ($devMode) {
        # Not elevated: register the package contents directly (Developer Mode allows this
        # without trusting the certificate). Same files, same package identity.
        $layout = Join-Path $artifacts 'msix-layout'
        Remove-Item -Recurse -Force $layout -ErrorAction SilentlyContinue
        $zip = Join-Path $env:TEMP 'dump-debugger.zip'
        Copy-Item $msix.FullName $zip -Force
        Expand-Archive $zip -DestinationPath $layout -Force
        Remove-Item (Join-Path $layout 'AppxSignature.p7x'), (Join-Path $layout 'AppxBlockMap.xml'), (Join-Path $layout '[Content_Types].xml') -ErrorAction SilentlyContinue
        Add-AppxPackage -Register (Join-Path $layout 'AppxManifest.xml')
    }
    else {
        throw "Installing needs either an elevated prompt (to trust $cer machine-wide) or Developer Mode (Settings > System > For developers)."
    }

    Get-AppxPackage -Name 'DumpDebugger' | Select-Object Name, Version, InstallLocation | Format-List
}
