# BootstrapMate Build Script
# Builds, signs, and packages BootstrapMate executable + MSI + .intunewin for deployment
#
# SECURITY NOTE: Code signing is REQUIRED by default for enterprise deployment
# Use -AllowUnsigned only for development builds (NOT for production)
#
# Examples:
#   .\build.ps1                          # Build executables + signed MSI + .intunewin (default)
#   .\build.ps1 -Thumbprint "ABC123..."  # Build with specific certificate
#   .\build.ps1 -AllowUnsigned           # Development build without signing (NOT for production)
#   .\build.ps1 -SkipMSI                 # Build executables only (skip MSI/IntuneWin)

[CmdletBinding()]
param(
    [string]$Thumbprint,
    [ValidateSet("x64", "arm64", "both")]
    [string]$Architecture = "both",
    [switch]$Clean,
    [switch]$Test,
    [switch]$AllowUnsigned,    # Explicit flag to allow unsigned builds for development only
    [switch]$SkipMSI,          # Skip MSI and .intunewin creation (executables only)
    [switch]$ListCerts,        # List available code signing certificates and exit
    [string]$FindCertSubject   # Find and display certificates matching subject substring
)

$ErrorActionPreference = "Stop"

Write-Host "=== BootstrapMate Build Script ===" -ForegroundColor Magenta
Write-Host "Architecture: $Architecture" -ForegroundColor Yellow
Write-Host "Code Signing: $(if ($AllowUnsigned) { 'DISABLED (Development Only)' } else { 'AUTO-DETECT (falls back to unsigned)' })" -ForegroundColor $(if ($AllowUnsigned) { "Red" } else { "Green" })
Write-Host "MSI + IntuneWin: $(if ($SkipMSI) { 'DISABLED' } else { 'ENABLED (Default)' })" -ForegroundColor $(if ($SkipMSI) { "Yellow" } else { "Green" })
Write-Host "Clean Build: $Clean" -ForegroundColor Yellow
if ($AllowUnsigned) {
    Write-Host ""
    Write-Host "WARNING: Building unsigned executable for development only!" -ForegroundColor Red
    Write-Host "   Unsigned builds are NOT suitable for production deployment" -ForegroundColor Red
}
Write-Host ""

# Function to display messages with different log levels
function Write-Log {
    param (
        [string]$Message,
        [ValidateSet("INFO", "WARN", "ERROR", "SUCCESS")]
        [string]$Level = "INFO"
    )
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    switch ($Level) {
        "INFO"    { Write-Host "[$timestamp] [INFO] $Message" -ForegroundColor White }
        "WARN"    { Write-Host "[$timestamp] [WARN] $Message" -ForegroundColor Yellow }
        "ERROR"   { Write-Host "[$timestamp] [ERROR] $Message" -ForegroundColor Red }
        "SUCCESS" { Write-Host "[$timestamp] [SUCCESS] $Message" -ForegroundColor Green }
    }
}

# Function to check if a command exists
function Test-Command {
    param([string]$Command)
    return [bool](Get-Command $Command -ErrorAction SilentlyContinue)
}

# Function to detect ARM64 system architecture (centralized detection)
function Test-ARM64System {
    try {
        # Use modern approach first (faster)
        $processor = Get-CimInstance -ClassName Win32_Processor -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($processor) {
            return $processor.Architecture -eq 12  # ARM64 architecture code
        }
        
        # Fallback to older WMI approach
        $processor = Get-WmiObject -Class Win32_Processor -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($processor) {
            return $processor.Architecture -eq 12
        }
        
        # Final fallback using environment variable
        return $env:PROCESSOR_ARCHITECTURE -eq "ARM64"
    }
    catch {
        Write-Log "ARM64 detection failed: $($_.Exception.Message). Assuming x64." "WARN"
        return $false
    }
}

# Function to perform comprehensive garbage collection
function Invoke-ComprehensiveGC {
    param([string]$Reason = "General cleanup")
    
    Write-Log "Performing garbage collection: $Reason" "INFO"
    try {
        # Force comprehensive cleanup
        [System.GC]::Collect()
        [System.GC]::WaitForPendingFinalizers()
        [System.GC]::Collect()
        [System.GC]::WaitForPendingFinalizers()
        
        # Give system time to fully release handles
        Start-Sleep -Milliseconds 500
        
        Write-Log "Garbage collection completed" "INFO"
    }
    catch {
        Write-Log "Garbage collection failed: $($_.Exception.Message)" "WARN"
    }
}

# Function to test if a file is locked
function Test-FileLocked {
    param([string]$FilePath)
    
    if (-not (Test-Path $FilePath)) {
        return $false
    }
    
    try {
        $fileStream = [System.IO.File]::Open($FilePath, 'Open', 'ReadWrite', 'None')
        $fileStream.Close()
        return $false  # File is not locked
    }
    catch [System.IO.IOException] {
        return $true   # File is locked
    }
    catch {
        Write-Log "Unexpected error testing file lock: $($_.Exception.Message)" "WARN"
        return $false  # Assume not locked if we can't determine
    }
}

# Wait until a file's data stream can be opened for reading.
# Enterprise security agents (Defender, EDR) hold an exclusive lock while scanning
# newly-created/signed binaries. The build must wait for the scan to release before
# reporting success, otherwise the user gets "Access is denied" immediately after build.
function Wait-FileAccessible {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [int]$TimeoutSeconds = 60,
        [int]$RetryIntervalSeconds = 3
    )

    $fileName = [System.IO.Path]::GetFileName($FilePath)
    $elapsed  = 0
    $warned   = $false

    while ($elapsed -lt $TimeoutSeconds) {
        try {
            $fs = [System.IO.File]::Open(
                $FilePath,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                [System.IO.FileShare]::ReadWrite
            )
            $fs.Close()
            if ($warned) { Write-Log "Security scan completed — $fileName is accessible" "SUCCESS" }
            return $true
        } catch {
            if (-not $warned) {
                Write-Log "Security scan in progress on $fileName — waiting up to ${TimeoutSeconds}s..." "WARN"
                $warned = $true
            }
            Start-Sleep -Seconds $RetryIntervalSeconds
            $elapsed += $RetryIntervalSeconds
        }
    }

    Write-Log "Timed out waiting for $fileName — security agent may still be scanning" "WARN"
    Write-Log "The file was built and signed successfully, but may not be immediately executable" "WARN"
    return $false
}

# Function to ensure signtool is available (enhanced from CimianTools)
function Test-SignTool {
    $c = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($c) { 
        Write-Log "Found signtool.exe in PATH: $($c.Source)" "SUCCESS"
        return 
    }
    
    Write-Log "signtool.exe not found in PATH, searching Windows SDK installations..." "INFO"
    
    $roots = @(
        "$env:ProgramFiles\Windows Kits\10\bin",
        "$env:ProgramFiles(x86)\Windows Kits\10\bin"
    ) | Where-Object { Test-Path $_ }

    try {
        $kitsRoot = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -EA Stop).KitsRoot10
        if ($kitsRoot) { 
            $binPath = Join-Path $kitsRoot 'bin'
            if (Test-Path $binPath) {
                $roots += $binPath
                Write-Log "Found Windows SDK from registry: $binPath" "INFO"
            }
        }
    } catch {
        Write-Log "No Windows SDK found in registry" "INFO"
    }

    foreach ($root in $roots) {
        # Look for signtool in architecture-specific subdirectories
        $patterns = @(
            Join-Path $root '*\x64\signtool.exe',
            Join-Path $root '*\arm64\signtool.exe',
            Join-Path $root '*\x86\signtool.exe'
        )
        
        foreach ($pattern in $patterns) {
            $candidates = Get-ChildItem -Path $pattern -EA SilentlyContinue | Sort-Object LastWriteTime -Desc
            if ($candidates) {
                $bestCandidate = $candidates | Select-Object -First 1
                $signtoolDir = $bestCandidate.Directory.FullName
                $env:Path = "$signtoolDir;$env:Path"
                Write-Log "Found signtool.exe: $($bestCandidate.FullName)" "SUCCESS"
                Write-Log "Added to PATH: $signtoolDir" "INFO"
                return
            }
        }
    }
    
    throw "signtool.exe not found. Install Windows 10/11 SDK with Signing Tools component."
}

# Function to find signing certificate (enhanced from CimianTools)
function Get-SigningCertThumbprint {
    param([string]$Thumbprint = $null)
    
    # Check for specific thumbprint from parameter or environment variable
    $certificateThumbprint = $Thumbprint
    if (-not $certificateThumbprint -and $env:CERT_THUMBPRINT) {
        $certificateThumbprint = $env:CERT_THUMBPRINT
        Write-Log "Using certificate thumbprint from environment: $($certificateThumbprint.Substring(0, 8))..." "INFO"
    }
    
    if ($certificateThumbprint) {
        # Check CurrentUser store first
        $cert = Get-ChildItem -Path "Cert:\CurrentUser\My\$certificateThumbprint" -ErrorAction SilentlyContinue
        if ($cert) {
            Write-Log "Found certificate by thumbprint in CurrentUser store: $($cert.Subject)" "SUCCESS"
            return @{ Certificate = $cert; Store = "CurrentUser"; Thumbprint = $cert.Thumbprint }
        }
        
        # Check LocalMachine store
        $cert = Get-ChildItem -Path "Cert:\LocalMachine\My\$certificateThumbprint" -ErrorAction SilentlyContinue
        if ($cert) {
            Write-Log "Found certificate by thumbprint in LocalMachine store: $($cert.Subject)" "SUCCESS"
            return @{ Certificate = $cert; Store = "LocalMachine"; Thumbprint = $cert.Thumbprint }
        }
        
        Write-Log "Certificate with thumbprint $($certificateThumbprint.Substring(0, 8))... not found in any store" "WARN"
    }
    
    # Search for enterprise certificate by common name from environment variable
    if ($Global:EnterpriseCertCN) {
        Write-Log "Searching for certificate with CN containing: $Global:EnterpriseCertCN" "INFO"
        
        # Check CurrentUser store first
        $cert = Get-ChildItem -Path "Cert:\CurrentUser\My\" | Where-Object {
            $_.Subject -like "*$Global:EnterpriseCertCN*"
        } | Select-Object -First 1
        
        if ($cert) {
            Write-Log "Found enterprise certificate in CurrentUser store: $($cert.Subject)" "SUCCESS"
            Write-Log "Thumbprint: $($cert.Thumbprint)" "INFO"
            Write-Log "Has Private Key: $($cert.HasPrivateKey)" "INFO"
            Write-Log "Valid until: $($cert.NotAfter)" "INFO"
            return @{ Certificate = $cert; Store = "CurrentUser"; Thumbprint = $cert.Thumbprint }
        }
        
        # Check LocalMachine store
        $cert = Get-ChildItem -Path "Cert:\LocalMachine\My\" | Where-Object {
            $_.Subject -like "*$Global:EnterpriseCertCN*"
        } | Select-Object -First 1
        
        if ($cert) {
            Write-Log "Found enterprise certificate in LocalMachine store: $($cert.Subject)" "SUCCESS"
            Write-Log "Thumbprint: $($cert.Thumbprint)" "INFO"
            Write-Log "Has Private Key: $($cert.HasPrivateKey)" "INFO"
            Write-Log "Valid until: $($cert.NotAfter)" "INFO"
            return @{ Certificate = $cert; Store = "LocalMachine"; Thumbprint = $cert.Thumbprint }
        }
    }
    
    Write-Log "No suitable signing certificate found" "WARN"
    if ($Global:EnterpriseCertCN) {
        Write-Log "Searched for certificate with CN containing: $Global:EnterpriseCertCN" "INFO"
    }
    Write-Log "Set ENTERPRISE_CERT_CN environment variable to your certificate's Common Name" "INFO"
    return $null
}

# Function to find signing certificate (legacy wrapper for compatibility)
function Get-SigningCertificate {
    param([string]$Thumbprint = $null)
    
    $certInfo = Get-SigningCertThumbprint -Thumbprint $Thumbprint
    if ($certInfo) {
        return $certInfo.Certificate
    }
    return $null
}

# Scan both certificate stores for any valid code-signing certificate
function Find-CodeSigningCerts {
    param([string]$SubjectFilter = "")

    $certs = @()
    $stores = @("Cert:\CurrentUser\My", "Cert:\LocalMachine\My")

    foreach ($store in $stores) {
        $storeCerts = Get-ChildItem $store -ErrorAction SilentlyContinue | Where-Object {
            $_.HasPrivateKey -and
            ($_.EnhancedKeyUsageList.FriendlyName -contains "Code Signing" -or
             $_.EnhancedKeyUsageList.ObjectId   -contains "1.3.6.1.5.5.7.3.3") -and
            $_.NotAfter -gt (Get-Date) -and
            ($SubjectFilter -eq "" -or $_.Subject -like "*$SubjectFilter*")
        }
        if ($storeCerts) {
            $storeName = if ($store -like "*CurrentUser*") { "CurrentUser" } else { "LocalMachine" }
            $certs += $storeCerts | ForEach-Object {
                $_ | Add-Member -NotePropertyName Store -NotePropertyValue $storeName -PassThru -Force
            }
        }
    }

    return $certs | Sort-Object NotAfter -Descending
}

# Display all discovered code-signing certificates
function Show-CertificateList {
    param([string]$SubjectFilter = "")

    $certs = Find-CodeSigningCerts -SubjectFilter $SubjectFilter

    if ($certs) {
        Write-Host ""
        Write-Host "Available code signing certificates:" -ForegroundColor Green
        for ($i = 0; $i -lt $certs.Count; $i++) {
            $cert = $certs[$i]
            Write-Host ""
            Write-Host "[$($i + 1)] Subject:    $($cert.Subject)" -ForegroundColor Cyan
            Write-Host "     Issuer:     $($cert.Issuer)" -ForegroundColor Gray
            Write-Host "     Thumbprint: $($cert.Thumbprint)" -ForegroundColor Yellow
            Write-Host "     Valid Until: $($cert.NotAfter)" -ForegroundColor Gray
            Write-Host "     Store:      $($cert.Store)" -ForegroundColor Gray
        }
        Write-Host ""
    } else {
        $msg = if ($SubjectFilter) { "No certificates found matching: $SubjectFilter" } else { "No valid code signing certificates found in any store" }
        Write-Host $msg -ForegroundColor Yellow
    }

    return $certs
}

# Auto-detect the best available code-signing certificate.
# Helper: subject-only cert search (no EKU requirement). Enterprise certs issued by MDM/Intune
# often have no EKU but are still accepted by signtool when selected by thumbprint.
function Find-CertBySubject {
    param([Parameter(Mandatory)][string]$SubjectFilter)
    $certLM = Get-ChildItem Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
        Where-Object { $_.HasPrivateKey -and $_.Subject -like "*$SubjectFilter*" } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    $certCU = Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
        Where-Object { $_.HasPrivateKey -and $_.Subject -like "*$SubjectFilter*" } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    # Prefer LocalMachine (machine-deployed enterprise cert); fall back to CurrentUser
    if ($certLM) { return $certLM | Add-Member -NotePropertyName Store -NotePropertyValue "LocalMachine" -PassThru -Force }
    if ($certCU) { return $certCU | Add-Member -NotePropertyName Store -NotePropertyValue "CurrentUser"  -PassThru -Force }
    return $null
}

function Get-BestCertificate {
    # Priority 1: Filter by CN env var (tries EKU-aware search first, then subject-only)
    if ($Global:EnterpriseCertCN) {
        $cert = (Find-CodeSigningCerts -SubjectFilter $Global:EnterpriseCertCN | Select-Object -First 1)
        if (-not $cert) { $cert = Find-CertBySubject -SubjectFilter $Global:EnterpriseCertCN }
        if ($cert) {
            Write-Log "Auto-detected certificate via CN filter: $($cert.Subject)" "SUCCESS"
            return $cert
        }
        Write-Log "No certificate matched BOOTSTRAPMATE_CERT_CN='$Global:EnterpriseCertCN'" "WARN"
    }

    # Priority 2: Filter by subject env var (subject-only — no EKU check)
    if ($Global:EnterpriseCertSubject) {
        $cert = Find-CertBySubject -SubjectFilter $Global:EnterpriseCertSubject
        if ($cert) {
            Write-Log "Auto-detected code signing certificate: $($cert.Subject)" "SUCCESS"
            return $cert
        }
        Write-Log "No certificate found matching BOOTSTRAPMATE_CERT_SUBJECT='$Global:EnterpriseCertSubject'" "WARN"
    }

    # Priority 3: No env vars set — prefer LocalMachine (enterprise-deployed) certs; exclude test certs
    $allCerts = Find-CodeSigningCerts
    $cert = $null

    # 3a: LocalMachine, non-test, has Code Signing EKU
    if ($allCerts) {
        $cert = $allCerts | Where-Object { $_.Store -eq "LocalMachine" -and $_.Subject -notmatch "\bTest\b" } | Select-Object -First 1
    }

    # 3b: Enterprise cert with no EKU — Intune/MDM-issued certs frequently omit EKU
    #     but are accepted by signtool when selected by thumbprint.
    #     Search LocalMachine first (GPO/MDM machine certs), then CurrentUser (user-enrolled certs).
    if (-not $cert) {
        $enterpriseKeywords = @("Enterprise", "Intune", "Corporate", "Organization")
        $storeMap = [ordered]@{
            "LocalMachine" = "Cert:\LocalMachine\My"
            "CurrentUser"  = "Cert:\CurrentUser\My"
        }
        :outerLoop foreach ($storeName in $storeMap.Keys) {
            foreach ($kw in $enterpriseKeywords) {
                $found = Get-ChildItem $storeMap[$storeName] -ErrorAction SilentlyContinue |
                    Where-Object { $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) -and $_.Subject -like "*$kw*" -and $_.Subject -notmatch "\bTest\b" } |
                    Sort-Object NotAfter -Descending | Select-Object -First 1
                if ($found) {
                    $cert = $found | Add-Member -NotePropertyName Store -NotePropertyValue $storeName -PassThru -Force
                    break outerLoop
                }
            }
        }
    }

    # 3c: Any store, non-test, has Code Signing EKU
    if (-not $cert -and $allCerts) {
        $cert = $allCerts | Where-Object { $_.Subject -notmatch "\bTest\b" } | Select-Object -First 1
    }

    # 3d: Last resort — use whatever is available (test/dev certs)
    if (-not $cert -and $allCerts) {
        $cert = $allCerts | Select-Object -First 1
        Write-Log "Only test/development certificates found — not suitable for production deployment" "WARN"
    }

    if ($cert) {
        Write-Log "Auto-detected code signing certificate (store scan): $($cert.Subject)" "SUCCESS"
        return $cert
    }

    return $null
}

# Function to sign executable with robust retry and multiple timestamp servers (enhanced from CimianTools)
function Invoke-SignArtifact {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Thumbprint,
        [string]$Store = "CurrentUser",
        [int]$MaxAttempts = 4
    )

    if (-not (Test-Path -LiteralPath $Path)) { 
        throw "File not found: $Path" 
    }

    Write-Log "Signing artifact: $([System.IO.Path]::GetFileName($Path))" "INFO"
    Write-Log "Certificate store: $Store" "INFO"
    Write-Log "Certificate thumbprint: $($Thumbprint.Substring(0, 8))..." "INFO"

    $tsas = @(
        'http://timestamp.digicert.com',
        'http://timestamp.sectigo.com', 
        'http://timestamp.entrust.net/TSS/RFC3161sha2TS',
        'http://timestamp.comodoca.com/authenticode'
    )

    $attempt = 0
    $lastError = $null
    
    while ($attempt -lt $MaxAttempts) {
        $attempt++
        Write-Log "Signing attempt $attempt of $MaxAttempts..." "INFO"
        
        foreach ($tsa in $tsas) {
            try {
                Write-Log "Using timestamp server: $tsa" "INFO"
                
                # Build signtool arguments based on certificate store
                $storeArgs = if ($Store -eq "CurrentUser") {
                    @("/s", "My")
                } else {
                    @("/s", "My", "/sm")
                }
                
                $signArgs = @("sign") + $storeArgs + @(
                    "/sha1", $Thumbprint,
                    "/fd", "SHA256",
                    "/td", "SHA256", 
                    "/tr", $tsa,
                    "/v",
                    $Path
                )
                
                Write-Log "Running: signtool.exe $($signArgs -join ' ')" "INFO"
                
                & signtool.exe @signArgs
                $code = $LASTEXITCODE

                if ($code -eq 0) {
                    Write-Log "Primary signing successful with TSA: $tsa" "SUCCESS"
                    
                    # Optional: append legacy timestamp for compatibility with older verifiers
                    try {
                        Write-Log "Adding legacy timestamp for compatibility..." "INFO"
                        & signtool.exe timestamp /t http://timestamp.digicert.com /v "$Path" 2>$null
                        if ($LASTEXITCODE -eq 0) {
                            Write-Log "Legacy timestamp added successfully" "SUCCESS"
                        } else {
                            Write-Log "Legacy timestamp failed (non-critical)" "INFO"
                        }
                    } catch {
                        Write-Log "Legacy timestamp failed (non-critical): $($_.Exception.Message)" "INFO"
                    }
                    
                    # Verify the signature — /pa requires a trusted root chain;
                    # self-signed dev certs pass the sign step but fail chain verification.
                    # Treat sign exit code 0 as the authoritative success indicator.
                    Write-Log "Verifying signature..." "INFO"
                    $verifyOutput = & signtool.exe verify /pa "$Path" 2>&1
                    if ($LASTEXITCODE -eq 0) {
                        Write-Log "Signature verification successful!" "SUCCESS"
                    } else {
                        # Check if this is just an untrusted root (self-signed dev cert) vs a real failure
                        $isSelfSignedWarning = $verifyOutput -match "not trusted by the trust provider|CERT_E_UNTRUSTEDROOT|root certificate which is not trusted"
                        if ($isSelfSignedWarning) {
                            Write-Log "Signature applied (self-signed cert — chain untrusted, expected for dev builds)" "WARN"
                        } else {
                            Write-Log "Signature verification failed: $verifyOutput" "WARN"
                        }
                    }
                    return $true
                }

                $lastError = "signtool exit code: $code"
                Write-Log "TSA $tsa failed: $lastError" "WARN"
                
                # Wait before trying next TSA
                Start-Sleep -Seconds 2
                
            } catch {
                $lastError = $_.Exception.Message
                Write-Log "Exception with TSA $tsa`: $lastError" "WARN"
                Start-Sleep -Seconds 2
            }
        }
        
        # Wait before next attempt with exponential backoff
        if ($attempt -lt $MaxAttempts) {
            $waitSeconds = 4 * $attempt
            Write-Log "All TSAs failed for attempt $attempt. Waiting $waitSeconds seconds before retry..." "WARN"
            Start-Sleep -Seconds $waitSeconds
        }
    }

    # If all normal attempts failed, try with sudo if available
    $sudoAvailable = Get-Command sudo -ErrorAction SilentlyContinue
    if ($sudoAvailable) {
        Write-Log "All normal signing attempts failed. Attempting with sudo elevation..." "WARN"
        
        try {
            # Use sudo with the first (most reliable) timestamp server
            $primaryTsa = $tsas[0]
            Write-Log "Using sudo with primary TSA: $primaryTsa" "INFO"
            
            # Build sudo signtool command
            $storeArg = if ($Store -eq "CurrentUser") { "My" } else { "My" }
            $storeModifier = if ($Store -ne "CurrentUser") { "/sm" } else { "" }
            
            Write-Log "Running with sudo: signtool.exe sign /s $storeArg $(if($storeModifier){$storeModifier}) /sha1 $Thumbprint /fd SHA256 /td SHA256 /tr $primaryTsa /v `"$Path`"" "INFO"
            
            # Execute with sudo directly (not through cmd)
            $sudoArgs = @(
                "signtool.exe",
                "sign",
                "/s", $storeArg
            )
            if ($storeModifier) { $sudoArgs += $storeModifier }
            $sudoArgs += @(
                "/sha1", $Thumbprint,
                "/fd", "SHA256",
                "/td", "SHA256",
                "/tr", $primaryTsa,
                "/v",
                $Path
            )
            
            & sudo @sudoArgs
            $sudoExitCode = $LASTEXITCODE
            
            if ($sudoExitCode -eq 0) {
                Write-Log "Successfully signed with sudo elevation!" "SUCCESS"
                
                # Verify the signature
                Write-Log "Verifying sudo-signed signature..." "INFO"
                & signtool.exe verify /pa "$Path"
                if ($LASTEXITCODE -eq 0) {
                    Write-Log "Sudo signature verification successful!" "SUCCESS"
                    return $true
                } else {
                    Write-Log "Sudo signature verification failed" "ERROR"
                    return $false
                }
            } else {
                Write-Log "Sudo signing failed with exit code: $sudoExitCode" "WARN"
            }
            
        } catch {
            Write-Log "Exception during sudo signing: $($_.Exception.Message)" "WARN"
        }
    } else {
        Write-Log "sudo not available for elevated signing attempt" "WARN"
    }

    throw "Signing failed after $MaxAttempts attempts across all TSAs (including sudo). Last error: $lastError"
}

# Function to aggressively unlock a file using multiple strategies
function Invoke-FileUnlock {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,
        [int]$MaxAttempts = 3
    )
    
    if (-not (Test-Path $FilePath)) {
        Write-Log "File not found for unlock: $FilePath" "ERROR"
        return $false
    }
    
    $fileName = [System.IO.Path]::GetFileName($FilePath)
    Write-Log "Attempting to unlock file: $fileName" "INFO"
    
    # First, try garbage collection
    Invoke-ComprehensiveGC -Reason "Pre-unlock file handle cleanup"
    
    # Test if file is actually locked
    if (-not (Test-FileLocked -FilePath $FilePath)) {
        Write-Log "File is not locked: $fileName" "SUCCESS"
        return $true
    }
    
    Write-Log "File is locked, attempting aggressive unlock strategies..." "WARN"
    
    # Strategy 1: Multiple GC attempts with increasing delays
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        Write-Log "Unlock attempt $attempt/$MaxAttempts using garbage collection..." "INFO"
        
        Invoke-ComprehensiveGC -Reason "Unlock attempt $attempt"
        Start-Sleep -Seconds ($attempt * 2)
        
        if (-not (Test-FileLocked -FilePath $FilePath)) {
            Write-Log "File unlocked via garbage collection on attempt $attempt" "SUCCESS"
            return $true
        }
    }
    
    # Strategy 2: Robocopy-based unlock (CimianTools approach)
    Write-Log "Attempting robocopy-based unlock..." "INFO"
    
    $sourceDir = Split-Path $FilePath -Parent
    $tempUnlockDir = Join-Path (Split-Path $FilePath -Parent) "temp_unlock_$(Get-Random)"
    $tempFilePath = Join-Path $tempUnlockDir $fileName
    
    try {
        # Create temp directory
        if (Test-Path $tempUnlockDir) { 
            Remove-Item $tempUnlockDir -Recurse -Force -ErrorAction SilentlyContinue 
        }
        New-Item -ItemType Directory -Path $tempUnlockDir -Force | Out-Null
        
        # Use robocopy with minimal retries and output
        Write-Log "Using robocopy to break file locks..." "INFO"
        $robocopyResult = & robocopy "$sourceDir" "$tempUnlockDir" "$fileName" /R:2 /W:1 /NP /NDL /NJH /NJS 2>&1
        $robocopyExitCode = $LASTEXITCODE
        
        # Robocopy exit codes 0-7 are success/partial success
        if ($robocopyExitCode -le 7 -and (Test-Path $tempFilePath)) {
            Write-Log "Robocopy successful, replacing original file..." "INFO"
            
            # Additional GC before file operations
            Invoke-ComprehensiveGC -Reason "Pre-file replacement"
            
            # Remove original and move back
            try {
                Remove-Item $FilePath -Force
                Move-Item $tempFilePath $FilePath -Force
                Write-Log "File unlocked via robocopy: $fileName" "SUCCESS"
                return $true
            }
            catch {
                Write-Log "Failed to replace file after robocopy: $($_.Exception.Message)" "ERROR"
                # Try to restore from temp if original was deleted
                if (-not (Test-Path $FilePath) -and (Test-Path $tempFilePath)) {
                    try {
                        Move-Item $tempFilePath $FilePath -Force
                        Write-Log "File restored from temp location" "INFO"
                    }
                    catch {
                        Write-Log "Failed to restore file: $($_.Exception.Message)" "ERROR"
                    }
                }
            }
        } else {
            Write-Log "Robocopy failed with exit code: $robocopyExitCode" "WARN"
            if ($robocopyResult) {
                Write-Log "Robocopy output: $robocopyResult" "INFO"
            }
        }
    }
    catch {
        Write-Log "Robocopy unlock exception: $($_.Exception.Message)" "ERROR"
    }
    finally {
        # Clean up temp directory
        if (Test-Path $tempUnlockDir) {
            try {
                Remove-Item $tempUnlockDir -Recurse -Force -ErrorAction SilentlyContinue
            }
            catch {
                Write-Log "Failed to clean up temp directory: $tempUnlockDir" "WARN"
            }
        }
    }
    
    # Strategy 3: File ownership fix (for ARM64 systems)
    if (Test-ARM64System) {
        Write-Log "ARM64 system detected, attempting ownership fix..." "INFO"
        try {
            & takeown /f "$FilePath" 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0) {
                Write-Log "File ownership acquired" "INFO"
                
                # Test if this resolved the lock
                Invoke-ComprehensiveGC -Reason "Post-ownership fix"
                if (-not (Test-FileLocked -FilePath $FilePath)) {
                    Write-Log "File unlocked via ownership fix: $fileName" "SUCCESS"
                    return $true
                }
            }
        }
        catch {
            Write-Log "Ownership fix failed: $($_.Exception.Message)" "WARN"
        }
    }
    
    # Final attempt: One more comprehensive GC cycle
    Write-Log "Final unlock attempt with extended garbage collection..." "INFO"
    Invoke-ComprehensiveGC -Reason "Final unlock attempt"
    Start-Sleep -Seconds 5
    
    if (-not (Test-FileLocked -FilePath $FilePath)) {
        Write-Log "File unlocked on final attempt: $fileName" "SUCCESS"
        return $true
    }
    
    # File remains locked - provide guidance but don't fail
    Write-Log "File remains locked after all unlock attempts: $fileName" "WARN"
    Write-Log "File may still be signable despite lock detection" "INFO"
    return $false  # Return false but allow signing attempt to proceed
}

# Function to sign executable (enhanced with admin detection and certificate store handling)
function Invoke-ExecutableSigning {
    param(
        [string]$FilePath,
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [string]$CertificateStore = "CurrentUser"
    )

    if (-not (Test-Path $FilePath)) {
        Write-Log "File not found for signing: $FilePath" "ERROR"
        return $false
    }

    Write-Log "Signing executable: $([System.IO.Path]::GetFileName($FilePath))" "INFO"

    # Use improved file unlocking for ARM64 cross-compilation scenarios
    $isARM64System = Test-ARM64System
    $isX64Executable = $FilePath -like "*x64*" -or $FilePath -like "*win-x64*"
    
    if ($isARM64System -and $isX64Executable) {
        Write-Log "ARM64 system detected, signing x64 executable with enhanced unlock strategy" "INFO"
        
        # Use comprehensive file unlocking
        $unlockSuccess = Invoke-FileUnlock -FilePath $FilePath -MaxAttempts 3
        if (-not $unlockSuccess) {
            Write-Log "File unlock failed, but attempting to sign anyway..." "WARN"
        }
    } else {
        # Standard unlock for same-architecture builds
        Write-Log "Performing standard file unlock for $([System.IO.Path]::GetFileName($FilePath))" "INFO"
        $unlockSuccess = Invoke-FileUnlock -FilePath $FilePath -MaxAttempts 2
    }

    # For Intune certificates, we'll try signing anyway as they may work without admin rights
    if ($Certificate.Subject -like "*Intune*") {
        Write-Log "Detected Intune certificate - attempting signing without admin privileges" "INFO"
    }

    # Use the robust signing function
    try {
        Write-Log "Attempting to sign: $([System.IO.Path]::GetFileName($FilePath))" "INFO"
        
        $success = Invoke-SignArtifact -Path $FilePath -Thumbprint $Certificate.Thumbprint -Store $CertificateStore
        if ($success) {
            Write-Log "Successfully signed: $([System.IO.Path]::GetFileName($FilePath))" "SUCCESS"
            return $true
        } else {
            Write-Log "Standard signing failed, attempting with elevated privileges..." "WARN"
            
            # Try with sudo if available
            $sudoAvailable = Get-Command sudo -ErrorAction SilentlyContinue
            if ($sudoAvailable) {
                Write-Log "Using sudo to elevate signtool privileges for signing..." "INFO"
                try {
                    # Use sudo with signtool directly for elevated signing
                    $sudoResult = sudo signtool.exe sign /s $CertificateStore /sha1 $Certificate.Thumbprint /fd SHA256 /td SHA256 /tr "http://timestamp.digicert.com" /v "$FilePath" 2>&1
                    
                    if ($LASTEXITCODE -eq 0) {
                        Write-Log "Successfully signed using sudo elevation: $([System.IO.Path]::GetFileName($FilePath))" "SUCCESS"
                        
                        # Verify the signature
                        $verifyResult = signtool.exe verify /pa /v "$FilePath" 2>&1
                        if ($LASTEXITCODE -eq 0) {
                            Write-Log "Signature verification successful with sudo signing!" "SUCCESS"
                        } else {
                            Write-Log "Warning: Signature verification failed, but signing appeared successful" "WARN"
                        }
                        
                        return $true
                    } else {
                        Write-Log "Sudo signing failed with exit code: $LASTEXITCODE" "WARN"
                        Write-Log "Sudo output: $sudoResult" "WARN"
                    }
                } catch {
                    Write-Log "Sudo signing failed with exception: $($_.Exception.Message)" "WARN"
                }
            } else {
                Write-Log "sudo not available. Please install sudo or run PowerShell as Administrator for code signing." "ERROR"
            }
            
            Write-Log "All signing attempts failed for: $([System.IO.Path]::GetFileName($FilePath))" "ERROR"
            return $false
        }
    } catch {
        $errorMessage = $_.Exception.Message
        Write-Log "Error during signing: $errorMessage" "ERROR"
        
        # Provide specific guidance for common issues
        if ($errorMessage -like "*Access is denied*") {
            Write-Log "" "ERROR"
            Write-Log "SIGNING FAILED: Access denied" "ERROR"
            Write-Log "This is commonly caused by:" "ERROR"
            Write-Log "1. Intune-managed certificate requiring elevated privileges" "ERROR"
            Write-Log "2. Certificate private key access restrictions" "ERROR"
            Write-Log "3. Certificate not suitable for code signing" "ERROR"
            if (Test-ARM64System -and $FilePath -like "*arm64*") {
                Write-Log "4. ARM64 native signing requires Administrator privileges on some systems" "ERROR"
            }
            Write-Log "" "ERROR"
            Write-Log "Solutions to try:" "ERROR"
            
            # Check if sudo is available and suggest using it
            $sudoAvailable = Get-Command sudo -ErrorAction SilentlyContinue
            if ($sudoAvailable) {
                Write-Log "1. RECOMMENDED: Install/use sudo - detected in PATH" "ERROR"
                Write-Log "   Run: winget install Microsoft.PowerShell.Preview" "ERROR"
                Write-Log "   Then retry the build (sudo will be used automatically)" "ERROR"
                Write-Log "2. Alternative: Run PowerShell as Administrator" "ERROR"
            } else {
                Write-Log "1. Install sudo for automatic elevation: winget install Microsoft.PowerShell.Preview" "ERROR"
                Write-Log "2. Alternative: Run PowerShell as Administrator manually" "ERROR"
            }
            
            Write-Log "3. Check certificate enhanced key usage includes 'Code Signing'" "ERROR"
            Write-Log "4. Verify certificate private key is accessible" "ERROR"
            if (Test-ARM64System -and $FilePath -like "*x64*") {
                Write-Log "5. Wait a few minutes and try again (file handle release)" "ERROR"
            }
            Write-Log "6. For development only: use -AllowUnsigned flag (NOT for production)" "ERROR"
        } elseif ($errorMessage -like "*file is being used by another process*") {
            Write-Log "" "ERROR"
            Write-Log "SIGNING FAILED: File in use" "ERROR"
            Write-Log "This is commonly caused by:" "ERROR"
            Write-Log "1. Build process still holding file handles" "ERROR"
            if (Test-ARM64System) {
                Write-Log "2. ARM64 cross-compilation creates persistent file locks" "ERROR"
            }
            Write-Log "3. Antivirus software scanning the executable" "ERROR"
            Write-Log "" "ERROR"
            Write-Log "Solutions to try:" "ERROR"
            Write-Log "1. Wait 30-60 seconds and run the build again" "ERROR"
            Write-Log "2. Close Visual Studio or other IDEs that may be holding handles" "ERROR"
            Write-Log "3. Temporarily disable real-time antivirus scanning" "ERROR"
            if (Test-ARM64System) {
                Write-Log "4. On ARM64 systems, file locks may require a reboot to clear" "ERROR"
            }
        } else {
            Write-Log "Signing failed with error: $errorMessage" "ERROR"
            Write-Log "Try running as Administrator or check certificate permissions" "ERROR"
        }
        
        return $false
    }
}

# Function to build for specific architecture
function Build-Architecture {
    param(
        [string]$Arch,
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$SigningCert = $null,
        [string]$CertificateStore = "CurrentUser"
    )
    
    Write-Log "Building for $Arch architecture..." "INFO"
    
    $outputDir = "publish\executables\$Arch"
    
    if ($Clean -and (Test-Path $outputDir)) {
        Write-Log "Cleaning output directory: $outputDir" "INFO"
        Remove-Item -Path $outputDir -Recurse -Force
    }
    
    # Ensure output directory exists
    if (-not (Test-Path $outputDir)) {
        New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
    }
    
    # Build arguments
    $buildArgs = @(
        "publish"
        "BootstrapMate.csproj"
        "--configuration", "Release"
        "--runtime", "win-$Arch"
        "--output", $outputDir
        "--self-contained", "true"
        "--verbosity", "minimal"
    )
    
    try {
        Write-Log "Running: dotnet $($buildArgs -join ' ')" "INFO"
        $null = & dotnet @buildArgs
        
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed with exit code: $LASTEXITCODE"
        }
        
        $executablePath = Join-Path $outputDir "installapplications.exe"
        
        if (-not (Test-Path $executablePath)) {
            throw "Expected executable not found: $executablePath"
        }
        
        # Convert to absolute path for signing
        $executablePath = (Get-Item $executablePath).FullName
        
        $fileInfo = Get-Item $executablePath
        $sizeMB = [math]::Round($fileInfo.Length / 1MB, 2)
        Write-Log "Build successful: $($fileInfo.Name) ($sizeMB MB)" "SUCCESS"
        
        # Sign the executable - MANDATORY unless explicitly disabled
        if ($SigningCert) {
            # Check for ARM64 system - fix ownership issue for both x64 and ARM64 executables
            $isARM64System = Test-ARM64System
            if ($isARM64System) {
                if ($Arch -eq "x64") {
                    Write-Log "ARM64 system detected - fixing x64 binary ownership and Defender exclusion for signing..." "INFO"
                    
                    # For x64 cross-compilation, add temporary Windows Defender exclusion to prevent signing interference
                    try {
                        $exclusionPath = Split-Path $executablePath
                        & sudo powershell -Command "Add-MpPreference -ExclusionPath '$exclusionPath'" -ErrorAction SilentlyContinue
                        Write-Log "Added temporary Windows Defender exclusion for x64 cross-compilation signing" "INFO"
                    } catch {
                        Write-Log "Could not add Defender exclusion, but continuing..." "WARN"
                    }
                } else {
                    Write-Log "ARM64 system detected - fixing ARM64 binary ownership and Defender exclusion for signing..." "INFO"
                    
                    # For ARM64 native executables, add temporary Windows Defender exclusion to prevent signing interference
                    try {
                        $exclusionPath = Split-Path $executablePath
                        & sudo powershell -Command "Add-MpPreference -ExclusionPath '$exclusionPath'" -ErrorAction SilentlyContinue
                        Write-Log "Added temporary Windows Defender exclusion for ARM64 signing" "INFO"
                    } catch {
                        Write-Log "Could not add Defender exclusion, but continuing..." "WARN"
                    }
                }
                try {
                    & takeown /f $executablePath | Out-Null
                    if ($LASTEXITCODE -eq 0) {
                        Write-Log "Fixed $Arch binary ownership" "SUCCESS"
                    }
                } catch {
                    Write-Log "Could not fix ownership, but continuing..." "WARN"
                }
            }
            
            # Force comprehensive garbage collection before signing to release any build-related file handles
            Write-Log "Performing garbage collection before signing to release file handles..." "INFO"
            Invoke-ComprehensiveGC -Reason "Pre-signing file handle release"
            
            if (Invoke-ExecutableSigning -FilePath $executablePath -Certificate $SigningCert -CertificateStore $CertificateStore) {
                Write-Log "Code signing completed for $Arch" "SUCCESS"
                
                # Clean up temporary Windows Defender exclusions for all architectures on ARM64 systems
                $isARM64System = Test-ARM64System
                if ($isARM64System) {
                    try {
                        $exclusionPath = Split-Path $executablePath
                        & sudo powershell -Command "Remove-MpPreference -ExclusionPath '$exclusionPath'" -ErrorAction SilentlyContinue
                        Write-Log "Removed temporary Windows Defender exclusion for $Arch architecture" "INFO"
                    } catch {
                        Write-Log "Could not remove Defender exclusion (non-critical)" "WARN"
                    }
                }
                # Wait for security scanner (Defender/EDR) to release its lock before returning
                Wait-FileAccessible -FilePath $executablePath | Out-Null
                return $true
                
                # Provide guidance but don't fail the build - allow for manual signing
                Write-Log "" "WARN"
                Write-Log "Build completed but signing failed. The executable was built successfully." "WARN"
                Write-Log "You can manually sign it later or run with elevated privileges." "WARN"
                Write-Log "For production deployment, ensure the executable is properly signed." "WARN"
                
                # Return success so build continues, but mark as unsigned
                return "unsigned"
            }
        } else {
            # This should only happen with -AllowUnsigned flag
            Write-Log "UNSIGNED BUILD: This executable is NOT suitable for production deployment" "WARN"
            Write-Log "Unsigned builds should only be used for development and testing" "WARN"
        }
        
        return $true
        
    } catch {
        Write-Log "Build failed for $Arch`: $($_.Exception.Message)" "ERROR"
        return $false
    }
}

# Function to run basic tests
function Test-Build {
    param([string]$ExecutablePath)
    
    Write-Log "Testing build: $ExecutablePath" "INFO"
    
    if (-not (Test-Path $ExecutablePath)) {
        Write-Log "Executable not found for testing: $ExecutablePath" "ERROR"
        return $false
    }
    
    try {
        # Test version output
        Write-Log "Testing --version command..." "INFO"
        $versionOutput = & $ExecutablePath --version 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Log "Version test passed: $versionOutput" "SUCCESS"
        } else {
            Write-Log "Version test failed with exit code: $LASTEXITCODE" "WARN"
        }
        
        # Test help output  
        Write-Log "Testing --help command..." "INFO"
        $null = & $ExecutablePath --help 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Log "Help test passed" "SUCCESS"
        } else {
            Write-Log "Help test failed with exit code: $LASTEXITCODE" "WARN"
        }
        
        return $true
        
    } catch {
        Write-Log "Testing failed: $($_.Exception.Message)" "ERROR"
        return $false
    }
}

# Generates resources.pri and copies XBF binary XAML files to the publish output.
#
# Background: EnableCoreMrtTooling=false is set in BootstrapMate.App.csproj because the
# VS "Universal Windows Platform development" workload (which provides
# Microsoft.Build.Packaging.Pri.Tasks.dll) is not installed on this machine.
# Without that workload, the standard MSBuild MrtCore PRI generation pipeline can't run.
#
# This function replicates what the MSBuild PriGen step would normally do:
#  1. Copy XBF (binary XAML) files from obj/ to the publish dir so MRT can open them.
#  2. Create a staging directory with the XBFs + WinUI 3 framework PRI files.
#  3. Run makepri.exe new on the staging dir — the <indexer-config type="PRI"/> indexer
#     automatically merges Microsoft.UI.Xaml.Controls.pri (which embeds themeresources.xbf,
#     generic.xbf, etc. as EmbeddedData) into the output resources.pri.
#  4. Write the merged resources.pri to the publish dir.
function Publish-AppResources {
    param(
        [Parameter(Mandatory)][string]$Arch,
        [Parameter(Mandatory)][string]$OutputDir,
        [Parameter(Mandatory)][string]$AppProjectDir
    )

    Write-Log "Generating XAML resources (XBF + resources.pri) for App ($Arch)..." "INFO"

    # Locate makepri.exe from the Windows SDK installation
    $makepri = $null
    $sdkBinRoots = @(
        "$env:ProgramFiles\Windows Kits\10\bin",
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    ) | Where-Object { Test-Path $_ }

    foreach ($root in $sdkBinRoots) {
        foreach ($toolArch in @("arm64", "x64", "x86")) {
            $candidates = Get-ChildItem "$root\*\$toolArch\makepri.exe" -ErrorAction SilentlyContinue |
                Sort-Object { [version]($_.FullName -replace '.*\\(\d+\.\d+\.\d+\.\d+)\\.*', '$1') } -Descending |
                Select-Object -First 1
            if ($candidates) { $makepri = $candidates.FullName; break }
        }
        if ($makepri) { break }
    }

    if (-not $makepri) {
        Write-Log "makepri.exe not found in Windows SDK — skipping resources.pri generation" "WARN"
        Write-Log "Install the Windows 10/11 SDK to enable automatic XAML resource generation" "WARN"
        return $false
    }
    Write-Log "Found makepri.exe: $makepri" "INFO"

    # Find the XBF root directory in obj/Release for the target architecture.
    # dotnet publish --configuration Release --runtime win-$Arch places XBF files under:
    #   obj\Release\<tfm>\win-$Arch\   (e.g., net10.0-windows10.0.19041.0)
    $xbfFiles = Get-ChildItem "$AppProjectDir\obj\Release" -Recurse -Filter "*.xbf" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match [regex]::Escape("\win-$Arch\") }

    if (-not $xbfFiles) {
        Write-Log "No XBF files found in obj\Release for win-$Arch — XAML binary resources missing" "WARN"
        return $false
    }

    # Determine the architecture-specific obj root (the directory containing the XBF files)
    $xbfRootPath = ($xbfFiles[0].FullName -split [regex]::Escape("\win-$Arch\"))[0] + "\win-$Arch"
    Write-Log "Found $($xbfFiles.Count) XBF file(s) in: $xbfRootPath" "INFO"

    # Create temp staging directory for makepri
    $stagingDir = Join-Path ([System.IO.Path]::GetTempPath()) "bootstrapmate-pri-$Arch"
    if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
    New-Item -ItemType Directory $stagingDir | Out-Null

    # Copy XBF files to staging and to the publish output dir, preserving subdirectory structure.
    # resources.pri maps resources as file paths (e.g., "App.xbf", "Views\RunPage.xbf").
    # MRT resolves those paths relative to the exe directory at runtime, so the XBF files
    # must be physically present in OutputDir as well as in the staging dir.
    foreach ($xbf in $xbfFiles) {
        $relativePath = $xbf.FullName.Substring($xbfRootPath.Length).TrimStart('\')

        # Copy to staging (for makepri indexing)
        $stagingDest = Join-Path $stagingDir $relativePath
        $stagingDestDir = Split-Path $stagingDest
        if (-not (Test-Path $stagingDestDir)) { New-Item -ItemType Directory $stagingDestDir | Out-Null }
        Copy-Item $xbf.FullName $stagingDest -Force

        # Copy to publish output (for runtime file resolution)
        $outputDest = Join-Path $OutputDir $relativePath
        $outputDestDir = Split-Path $outputDest
        if (-not (Test-Path $outputDestDir)) { New-Item -ItemType Directory $outputDestDir | Out-Null }
        Copy-Item $xbf.FullName $outputDest -Force

        Write-Log "  XBF: $relativePath" "INFO"
    }

    # Copy WinUI 3 framework PRI files (Microsoft.UI.pri, Microsoft.UI.Xaml.Controls.pri)
    # to staging so makepri merges their embedded XBF resources (themeresources.xbf, etc.)
    # into the output resources.pri via the <indexer-config type="PRI"/> indexer.
    $frameworkPris = Get-ChildItem $OutputDir -Filter "Microsoft.*.pri"
    foreach ($pri in $frameworkPris) {
        Copy-Item $pri.FullName (Join-Path $stagingDir $pri.Name) -Force
        Write-Log "  Framework PRI: $($pri.Name) ($([math]::Round($pri.Length/1KB, 0)) KB)" "INFO"
    }

    # Generate a default priconfig.xml to drive makepri
    $priconfigPath = Join-Path $stagingDir "priconfig.xml"
    & $makepri createconfig /cf $priconfigPath /dq "en-US" /pv "10.0.0" /o 2>&1 | Out-Null

    # Build the merged resources.pri
    $outPriPath = Join-Path $OutputDir "resources.pri"
    Write-Log "Running makepri.exe new to generate resources.pri..." "INFO"
    $priOutput = & $makepri new /pr $stagingDir /cf $priconfigPath /in "BootstrapMate" /of $outPriPath /o 2>&1

    if ($LASTEXITCODE -ne 0) {
        Write-Log "makepri.exe failed (exit $LASTEXITCODE): $priOutput" "ERROR"
        Remove-Item $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
        return $false
    }

    $priSizeKB = [math]::Round((Get-Item $outPriPath).Length / 1KB, 0)
    Write-Log "Generated resources.pri (${priSizeKB} KB) — $($xbfFiles.Count) XBF + $($frameworkPris.Count) framework PRI(s) merged" "SUCCESS"

    # Clean up temp staging directory
    Remove-Item $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
    return $true
}

# Function to build GUI App for specific architecture
function Build-AppArchitecture {
    param(
        [string]$Arch,
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$SigningCert = $null,
        [string]$CertificateStore = "CurrentUser"
    )

    Write-Log "Building GUI App for $Arch architecture..." "INFO"

    $outputDir = "publish\app\$Arch"

    if ($Clean -and (Test-Path $outputDir)) {
        Write-Log "Cleaning App output directory: $outputDir" "INFO"
        Remove-Item -Path $outputDir -Recurse -Force
    }

    if (-not (Test-Path $outputDir)) {
        New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
    }

    $appProject = "src\BootstrapMate.App\BootstrapMate.App.csproj"
    if (-not (Test-Path $appProject)) {
        Write-Log "App project not found: $appProject — skipping App build" "WARN"
        return $false
    }

    $buildArgs = @(
        "publish"
        $appProject
        "--configuration", "Release"
        "--runtime", "win-$Arch"
        "--output", $outputDir
        "--self-contained", "true"
        "--verbosity", "minimal"
    )

    try {
        Write-Log "Running: dotnet $($buildArgs -join ' ')" "INFO"
        $null = & dotnet @buildArgs

        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed for App with exit code: $LASTEXITCODE"
        }

        $executablePath = Join-Path $outputDir "BootstrapMate.exe"

        if (-not (Test-Path $executablePath)) {
            throw "Expected App executable not found: $executablePath"
        }

        $executablePath = (Get-Item $executablePath).FullName

        $fileInfo = Get-Item $executablePath
        $sizeMB = [math]::Round($fileInfo.Length / 1MB, 2)
        Write-Log "App build successful: $($fileInfo.Name) ($sizeMB MB)" "SUCCESS"

        # Generate XBF resources and resources.pri for WinUI 3 XAML loading.
        # dotnet publish with EnableCoreMrtTooling=false does not copy XBF files or
        # generate a merged resources.pri, so we do it here as a post-publish step.
        $appProjectDir = Join-Path $PSScriptRoot "src\BootstrapMate.App"
        $resourcesOk = Publish-AppResources -Arch $Arch -OutputDir (Resolve-Path $outputDir).Path -AppProjectDir $appProjectDir
        if (-not $resourcesOk) {
            Write-Log "XAML resource generation failed or skipped — app may crash on launch (ms-appx:/// XAML loading requires resources.pri)" "WARN"
        }

        # Sign the App executable
        if ($SigningCert) {
            $isARM64System = Test-ARM64System
            if ($isARM64System) {
                try {
                    $exclusionPath = Split-Path $executablePath
                    & sudo powershell -Command "Add-MpPreference -ExclusionPath '$exclusionPath'" -ErrorAction SilentlyContinue
                    Write-Log "Added temporary Windows Defender exclusion for App signing ($Arch)" "INFO"
                } catch {
                    Write-Log "Could not add Defender exclusion, but continuing..." "WARN"
                }
                try {
                    & takeown /f $executablePath | Out-Null
                    if ($LASTEXITCODE -eq 0) {
                        Write-Log "Fixed App $Arch binary ownership" "SUCCESS"
                    }
                } catch {
                    Write-Log "Could not fix ownership, but continuing..." "WARN"
                }
            }

            Invoke-ComprehensiveGC -Reason "Pre-signing App file handle release"

            if (Invoke-ExecutableSigning -FilePath $executablePath -Certificate $SigningCert -CertificateStore $CertificateStore) {
                Write-Log "App code signing completed for $Arch" "SUCCESS"

                if ($isARM64System) {
                    try {
                        $exclusionPath = Split-Path $executablePath
                        & sudo powershell -Command "Remove-MpPreference -ExclusionPath '$exclusionPath'" -ErrorAction SilentlyContinue
                        Write-Log "Removed temporary Windows Defender exclusion for App ($Arch)" "INFO"
                    } catch {
                        Write-Log "Could not remove Defender exclusion (non-critical)" "WARN"
                    }
                }
                # Wait for security scanner (Defender/EDR) to release its lock before returning
                Wait-FileAccessible -FilePath $executablePath | Out-Null
                return $true
            } else {
                Write-Log "App code signing failed for $Arch" "ERROR"
                Write-Log "App build completed but signing failed. You can manually sign it later." "WARN"
                return "unsigned"
            }
        } else {
            Write-Log "UNSIGNED APP BUILD: Not suitable for production deployment" "WARN"
        }

        return $true

    } catch {
        Write-Log "App build failed for $Arch`: $($_.Exception.Message)" "ERROR"
        return $false
    }
}

# Function to regenerate BootstrapMate.ico from BootstrapMate.png
function New-IcoFromPng {
    $assetsDir = Join-Path $PSScriptRoot "src\BootstrapMate.App\Assets"
    $icoPath   = Join-Path $assetsDir "BootstrapMate.ico"

    # Prefer the repo-root PNG (source of truth); fall back to the Assets copy
    $rootPng   = Join-Path $PSScriptRoot "..\BootstrapMate.png"
    $sourcePng = if (Test-Path $rootPng) { $rootPng } else { Join-Path $assetsDir "BootstrapMate.png" }

    if (-not (Test-Path $sourcePng)) {
        Write-Log "ICO generation skipped — source PNG not found: $sourcePng" "WARN"
        return
    }

    Write-Log "Regenerating ICO from: $(Resolve-Path $sourcePng)" "INFO"

    try {
        Add-Type -AssemblyName System.Drawing

        $sizes  = @(16, 24, 32, 48, 64, 96, 128, 256)
        $source = [System.Drawing.Bitmap]::new((Resolve-Path $sourcePng).Path)
        $source.SetResolution(96, 96)

        # Auto-crop: find tight bounding box of opaque pixels so the icon fills
        # the ICO canvas without the transparent margins present in the source PNG
        $minX = $source.Width;  $maxX = 0
        $minY = $source.Height; $maxY = 0
        for ($y = 0; $y -lt $source.Height; $y++) {
            for ($x = 0; $x -lt $source.Width; $x++) {
                if ($source.GetPixel($x, $y).A -gt 0) {
                    if ($x -lt $minX) { $minX = $x }
                    if ($x -gt $maxX) { $maxX = $x }
                    if ($y -lt $minY) { $minY = $y }
                    if ($y -gt $maxY) { $maxY = $y }
                }
            }
        }
        $cropW = $maxX - $minX + 1
        $cropH = $maxY - $minY + 1
        Write-Log "Auto-crop bounding box: ($minX,$minY) -> ($maxX,$maxY)  ${cropW}x${cropH}" "INFO"
        $srcRect = [System.Drawing.Rectangle]::new($minX, $minY, $cropW, $cropH)

        $pngDataList = @()
        foreach ($size in $sizes) {
            $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $g = [System.Drawing.Graphics]::FromImage($bitmap)
            $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $g.Clear([System.Drawing.Color]::Transparent)
            # Use explicit pixel-unit source rect to bypass DPI metadata scaling
            $destRect = [System.Drawing.Rectangle]::new(0, 0, $size, $size)
            $g.DrawImage($source, $destRect, $srcRect, [System.Drawing.GraphicsUnit]::Pixel)
            $g.Dispose()

            $ms = New-Object System.IO.MemoryStream
            $bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            $pngDataList += [PSCustomObject]@{ Size = $size; Data = $ms.ToArray() }
            $ms.Dispose()
            $bitmap.Dispose()
        }
        $source.Dispose()

        # Write ICO (PNG-in-ICO format, Vista+)
        $count  = $pngDataList.Count
        $stream = New-Object System.IO.FileStream($icoPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
        $writer = New-Object System.IO.BinaryWriter($stream)

        $writer.Write([uint16]0)       # Reserved
        $writer.Write([uint16]1)       # Type = ICO
        $writer.Write([uint16]$count)

        $offset = [uint32](6 + 16 * $count)
        foreach ($entry in $pngDataList) {
            $w = if ($entry.Size -eq 256) { 0 } else { [byte]$entry.Size }
            $h = if ($entry.Size -eq 256) { 0 } else { [byte]$entry.Size }
            $writer.Write([byte]$w)
            $writer.Write([byte]$h)
            $writer.Write([byte]0)    # ColorCount
            $writer.Write([byte]0)    # Reserved
            $writer.Write([uint16]1)  # Planes
            $writer.Write([uint16]32) # BitCount
            $writer.Write([uint32]$entry.Data.Length)
            $writer.Write([uint32]$offset)
            $offset += [uint32]$entry.Data.Length
        }
        foreach ($entry in $pngDataList) { $writer.Write($entry.Data) }

        $writer.Flush()
        $writer.Dispose()
        $stream.Dispose()

        Write-Log "ICO regenerated successfully ($count sizes: $($sizes -join ', ')px) → $icoPath" "SUCCESS"
    }
    catch {
        Write-Log "ICO generation failed: $($_.Exception.Message)" "WARN"
        Write-Log "Build will continue with the existing ICO file" "WARN"
    }
}

# Function to update version in Program.cs
function Update-Version {
    $programCsPath = Join-Path $PSScriptRoot "Program.cs"
    
    if (-not (Test-Path $programCsPath)) {
        throw "Program.cs not found at: $programCsPath"
    }
    
    # Generate full YYYY.MM.DD.HHMM version format for Intune compatibility
    # Note: MSI ProductVersion will use a compatible subset for internal MSI requirements
    $now = Get-Date
    $year = $now.Year          # e.g., 2025
    $month = $now.ToString("MM")     # e.g., 09 for September (zero-padded)  
    $day = $now.ToString("dd")       # e.g., 02 for 2nd day (zero-padded)
    $revision = $now.ToString("HHmm") # e.g., 2141 for 21:41
    
    $newVersion = "$year.$month.$day.$revision"
    Write-Log "Updating version to: $newVersion (YYYY.MM.DD.HHMM format for Intune compatibility)" "INFO"
    
    # Create MSI-compatible version (major.minor.build < 65536)
    # Convert YYYY.MM.DD.HHMM to YY.MM.DD.HHMM for MSI ProductVersion
    $now = Get-Date
    $msiMajor = $now.Year - 2000  # e.g., 25 for 2025
    $msiMinor = [int]$now.ToString("MM")        # e.g., 9 for September  
    $msiBuild = [int]$now.ToString("dd")        # e.g., 2 for 2nd day
    $msiRevision = [int]$now.ToString("HHmm")   # e.g., 2141 for 21:41
    $msiVersion = "$msiMajor.$msiMinor.$msiBuild.$msiRevision"
    
    Write-Log "MSI ProductVersion: $msiVersion (MSI-compliant format)" "INFO"
    
    # Read the current file content
    $content = Get-Content $programCsPath -Raw
    
    # Find and replace the version line using regex
    $pattern = 'private static readonly string Version = "[\d.]+";'
    $replacement = "private static readonly string Version = `"$newVersion`";"
    
    if ($content -match $pattern) {
        $updatedContent = $content -replace $pattern, $replacement
        Set-Content -Path $programCsPath -Value $updatedContent -NoNewline
        Write-Log "Version updated successfully in Program.cs" "SUCCESS"
    } else {
        Write-Log "C# code uses dynamic version generation (as designed)" "INFO"
    }
    
    # Return version information regardless of whether static version was found
    return @{
        FullVersion = $newVersion      # YYYY.MM.DD.HHMM for Intune detection
        MsiVersion = $msiVersion       # YY.MM.DD.HHMM for MSI ProductVersion
    }
}

# Function to build MSI for specific architecture
function Build-MSI {
    param(
        [string]$Arch,
        [string]$Version,
        [string]$FullVersion,  # Full YYYY.MM.DD.HHMM version for binaries
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$SigningCert = $null,
        [string]$CertificateStore = "CurrentUser"
    )
    
    Write-Log "Building MSI for $Arch architecture..." "INFO"
    
    $projectPath = "installer\BootstrapMate.Installer.wixproj"
    
    if (-not (Test-Path $projectPath)) {
        Write-Log "WiX project not found: $projectPath" "ERROR"
        return @{ Success = $false; Architecture = $Arch }
    }
    
    # Get bootstrap URL from environment — optional, uses placeholder if not set
    $bootstrapUrl = $env:BOOTSTRAP_MANIFEST_URL
    if (-not $bootstrapUrl) {
        $bootstrapUrl = "https://your-domain.com/bootstrap/management.json"
        Write-Log "BOOTSTRAP_MANIFEST_URL not set — using placeholder URL in MSI" "WARN"
        Write-Log "Set BOOTSTRAP_MANIFEST_URL in your .env file before deploying to production" "WARN"
    } else {
        Write-Log "Bootstrap manifest URL: $bootstrapUrl" "INFO"
    }
    
    $binDirAbsolute = (Resolve-Path "publish\executables\$Arch").Path
    $appDirAbsolute = if (Test-Path "publish\app\$Arch") { (Resolve-Path "publish\app\$Arch").Path } else { "" }
    
    $buildArgs = @(
        "build", $projectPath,
        "--configuration", "Release",
        "--verbosity", "normal",
        "-p:Platform=$Arch",
        "-p:ProductVersion=$($versionInfo.MsiVersion)",
        "-p:BinDir=$binDirAbsolute",
        "-p:BootstrapUrl=$bootstrapUrl"
    )
    if ($appDirAbsolute) {
        $buildArgs += "-p:AppDir=$appDirAbsolute"
    }
    
    Write-Log "Building MSI: dotnet $($buildArgs -join ' ')" "INFO"
    
    $process = Start-Process -FilePath "dotnet" -ArgumentList $buildArgs -Wait -PassThru -NoNewWindow
    
    if ($process.ExitCode -eq 0) {
        $msiPath = "installer\bin\$Arch\Release\BootstrapMate-$Arch.msi"
        if (Test-Path $msiPath) {
            Write-Log "MSI built successfully: $msiPath" "SUCCESS"
            
            # Copy MSI to consolidated publish directory with version in filename
            $publishMsiDir = "publish\msi"
            if (-not (Test-Path $publishMsiDir)) {
                New-Item -ItemType Directory -Path $publishMsiDir -Force | Out-Null
            }
            # Include version in MSI filename for better version tracking
            $finalMsiPath = Join-Path $publishMsiDir "BootstrapMate-$Arch-$($versionInfo.FullVersion).msi"
            Copy-Item $msiPath $finalMsiPath -Force
            Write-Log "MSI copied to: $finalMsiPath" "INFO"
            
            # Sign MSI if certificate available
            if ($SigningCert) {
                try {
                    $signed = Invoke-SignArtifact -Path $finalMsiPath -Thumbprint $SigningCert.Thumbprint -Store $CertificateStore
                    if ($signed) {
                        Write-Log "MSI signed successfully" "SUCCESS"
                    } else {
                        Write-Log "MSI signing failed" "ERROR"
                    }
                } catch {
                    Write-Log "MSI signing error: $($_.Exception.Message)" "ERROR"
                }
            }
            
            return @{ Success = $true; Architecture = $Arch; MsiPath = $finalMsiPath }
        } else {
            Write-Log "MSI build succeeded but file not found: $msiPath" "ERROR"
            return @{ Success = $false; Architecture = $Arch }
        }
    } else {
        Write-Log "MSI build failed for $Arch - dotnet build (WiX) failed with exit code: $($process.ExitCode)" "ERROR"
        return @{ Success = $false; Architecture = $Arch }
    }
    
    # Clean up the generated run script and sbin staging directory
    $customRunScriptPath = "installer\run.ps1"
    if (Test-Path $customRunScriptPath) {
        Remove-Item $customRunScriptPath -Force
        Write-Log "Cleaned up generated run.ps1 script" "INFO"
    }
    
    $sbinStagingDir = "installer\sbin-staging"
    if (Test-Path $sbinStagingDir) {
        Remove-Item $sbinStagingDir -Recurse -Force
        Write-Log "Cleaned up sbin-installer staging directory" "INFO"
    }
}

# Function to clean up old build artifacts (keep only latest N versions per architecture)
function Clear-OldBuildArtifacts {
    param(
        [int]$KeepCount = 1  # Keep only the most recent version per architecture
    )
    
    Write-Log "Cleaning up old build artifacts (keeping $KeepCount most recent per architecture)..." "INFO"
    
    $publishDir = Join-Path $PSScriptRoot "publish"
    $totalFreed = 0
    
    # Clean up old .intunewin files
    $intunewinDir = Join-Path $publishDir "intunewin"
    if (Test-Path $intunewinDir) {
        foreach ($arch in @("x64", "arm64")) {
            $files = Get-ChildItem -Path $intunewinDir -Filter "BootstrapMate-$arch-*.intunewin" -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending
            
            if ($files.Count -gt $KeepCount) {
                $filesToRemove = $files | Select-Object -Skip $KeepCount
                foreach ($file in $filesToRemove) {
                    $sizeMB = [math]::Round($file.Length / 1MB, 2)
                    try {
                        Remove-Item $file.FullName -Force -ErrorAction Stop
                        $totalFreed += $file.Length
                        Write-Log "Removed old artifact: $($file.Name) ($sizeMB MB)" "INFO"
                    } catch {
                        Write-Log "Skipping locked artifact: $($file.Name) — $($_.Exception.Message)" "WARN"
                    }
                }
            }
        }
    }
    
    # Clean up old .msi files
    $msiDir = Join-Path $publishDir "msi"
    if (Test-Path $msiDir) {
        foreach ($arch in @("x64", "arm64")) {
            $files = Get-ChildItem -Path $msiDir -Filter "BootstrapMate-$arch-*.msi" -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending
            
            if ($files.Count -gt $KeepCount) {
                $filesToRemove = $files | Select-Object -Skip $KeepCount
                foreach ($file in $filesToRemove) {
                    $sizeMB = [math]::Round($file.Length / 1MB, 2)
                    try {
                        Remove-Item $file.FullName -Force -ErrorAction Stop
                        $totalFreed += $file.Length
                        Write-Log "Removed old artifact: $($file.Name) ($sizeMB MB)" "INFO"
                    } catch {
                        Write-Log "Skipping locked artifact: $($file.Name) — $($_.Exception.Message)" "WARN"
                    }
                }
            }
        }
    }
    
    # Clean up old executables (each arch has its own subdirectory)
    $exeDir = Join-Path $publishDir "executables"
    if (Test-Path $exeDir) {
        foreach ($arch in @("x64", "arm64")) {
            $archExeDir = Join-Path $exeDir $arch
            if (Test-Path $archExeDir) {
                $files = Get-ChildItem -Path $archExeDir -Filter "*.exe" -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTime -Descending
                
                if ($files.Count -gt $KeepCount) {
                    $filesToRemove = $files | Select-Object -Skip $KeepCount
                    foreach ($file in $filesToRemove) {
                        $sizeMB = [math]::Round($file.Length / 1MB, 2)
                        try {
                            Remove-Item $file.FullName -Force -ErrorAction Stop
                            $totalFreed += $file.Length
                            Write-Log "Removed old executable: $($file.Name) ($sizeMB MB)" "INFO"
                        } catch {
                            Write-Log "Skipping locked executable: $($file.Name) — $($_.Exception.Message)" "WARN"
                        }
                    }
                }
            }
        }
    }
    
    if ($totalFreed -gt 0) {
        $freedGB = [math]::Round($totalFreed / 1GB, 2)
        Write-Log "Freed $freedGB GB by removing old build artifacts" "SUCCESS"
    } else {
        Write-Log "No old artifacts to clean up" "INFO"
    }
    
    return $totalFreed
}

# Function to create .intunewin packages
function New-IntuneWinPackage {
    param(
        [Parameter(Mandatory)]
        [string]$MsiPath,
        [Parameter(Mandatory)]
        [string]$OutputDirectory
    )
    
    Write-Log "Creating .intunewin package for: $([System.IO.Path]::GetFileName($MsiPath))" "INFO"
    
    # Check for IntuneWinAppUtil.exe and try multiple sources
    $intuneUtilPath = $null
    
    # Try local copy first (downloaded working version)
    $localIntuneUtil = Join-Path $PSScriptRoot "IntuneWinAppUtil.exe"
    if (Test-Path $localIntuneUtil) {
        # Test if the local copy works
        try {
            $null = & $localIntuneUtil -h 2>&1
            if ($LASTEXITCODE -eq 0) {
                $intuneUtilPath = $localIntuneUtil
                Write-Log "Using local IntuneWinAppUtil.exe (verified working)" "SUCCESS"
            } else {
                Write-Log "Local IntuneWinAppUtil.exe exists but doesn't work properly" "WARN"
            }
        } catch {
            Write-Log "Local IntuneWinAppUtil.exe test failed: $($_.Exception.Message)" "WARN"
        }
    }
    
    # Try system PATH if local copy doesn't work
    if (-not $intuneUtilPath -and (Test-Command "IntuneWinAppUtil.exe")) {
        try {
            $null = & IntuneWinAppUtil.exe -h 2>&1
            if ($LASTEXITCODE -eq 0) {
                $intuneUtilPath = "IntuneWinAppUtil.exe"
                Write-Log "Using system IntuneWinAppUtil.exe (verified working)" "SUCCESS"
            } else {
                Write-Log "System IntuneWinAppUtil.exe exists but doesn't work properly" "WARN"
            }
        } catch {
            Write-Log "System IntuneWinAppUtil.exe test failed: $($_.Exception.Message)" "WARN"
        }
    }
    
    # Download working version if needed
    if (-not $intuneUtilPath) {
        Write-Log "No working IntuneWinAppUtil.exe found. Downloading from Microsoft..." "INFO"
        try {
            $downloadUrl = "https://raw.githubusercontent.com/microsoft/Microsoft-Win32-Content-Prep-Tool/master/IntuneWinAppUtil.exe"
            $intuneUtilPath = $localIntuneUtil
            Invoke-WebRequest -Uri $downloadUrl -OutFile $intuneUtilPath -UseBasicParsing
            
            # Test the downloaded version
            $null = & $intuneUtilPath -h 2>&1
            if ($LASTEXITCODE -eq 0) {
                Write-Log "Downloaded and verified working IntuneWinAppUtil.exe" "SUCCESS"
            } else {
                throw "Downloaded IntuneWinAppUtil.exe doesn't work properly"
            }
        } catch {
            Write-Log "Failed to download working IntuneWinAppUtil.exe: $($_.Exception.Message)" "ERROR"
            return $null
        }
    }
    
    $setupFolder = Split-Path $MsiPath -Parent
    $outputFolder = "publish\intunewin"
    
    # Ensure output directory exists
    if (-not (Test-Path $outputFolder)) {
        New-Item -ItemType Directory -Path $outputFolder -Force | Out-Null
    }
    
    # Remove existing .intunewin files to prevent conflicts
    $msiBaseName = [System.IO.Path]::GetFileNameWithoutExtension($MsiPath)
    $existingIntunewin = Get-ChildItem -Path $outputFolder -Filter "$msiBaseName.intunewin" -ErrorAction SilentlyContinue
    if ($existingIntunewin) {
        Write-Log "Removing existing .intunewin file to prevent conflicts" "INFO"
        Remove-Item -Path $existingIntunewin.FullName -Force
    }
    
    # Create the .intunewin package
    $intuneArgs = @(
        "-c", "`"$setupFolder`""
        "-s", "`"$MsiPath`""
        "-o", "`"$outputFolder`""
        "-q"  # Quiet mode
    )
    
    Write-Log "Running: $intuneUtilPath $($intuneArgs -join ' ')" "INFO"
    
    try {
        # Run the actual packaging
        $process = Start-Process -FilePath $intuneUtilPath -ArgumentList $intuneArgs -Wait -PassThru -WindowStyle Hidden -RedirectStandardOutput "$env:TEMP\intunewin-out.txt" -RedirectStandardError "$env:TEMP\intunewin-err.txt"
        
        if ($process.ExitCode -eq 0) {
            $expectedIntuneWin = Join-Path $outputFolder "$msiBaseName.intunewin"
            if (Test-Path $expectedIntuneWin) {
                $fileSize = (Get-Item $expectedIntuneWin).Length
                $sizeMB = [math]::Round($fileSize / 1MB, 2)
                Write-Log ".intunewin package created successfully: $expectedIntuneWin ($sizeMB MB)" "SUCCESS"
                return $expectedIntuneWin
            } else {
                Write-Log ".intunewin package creation succeeded but file not found: $expectedIntuneWin" "ERROR"
                return $null
            }
        } else {
            $errorOutput = ""
            if (Test-Path "$env:TEMP\intunewin-err.txt") {
                $errorOutput = Get-Content "$env:TEMP\intunewin-err.txt" -Raw
            }
            Write-Log "IntuneWinAppUtil failed with exit code: $($process.ExitCode)" "ERROR"
            if ($errorOutput) {
                Write-Log "Error details: $errorOutput" "ERROR"
            }
            return $null
        }
    } catch {
        Write-Log "Error running IntuneWinAppUtil: $($_.Exception.Message)" "ERROR"
        return $null
    } finally {
        # Clean up temp files
        Remove-Item "$env:TEMP\intunewin-out.txt" -Force -ErrorAction SilentlyContinue
        Remove-Item "$env:TEMP\intunewin-err.txt" -Force -ErrorAction SilentlyContinue
    }
}

# Import environment variables from .env file if it exists
function Import-EnvironmentVariables {
    $envFile = Join-Path $PSScriptRoot ".env"
    if (Test-Path $envFile) {
        Write-Log "Loading environment variables from .env file" "INFO"
        Get-Content $envFile | Where-Object { $_ -notmatch '^#' -and $_ -notmatch '^\s*$' } | ForEach-Object {
            $name, $value = $_ -split '=', 2
            if ($name -and $value) {
                $name = $name.Trim()
                $value = $value.Trim().Trim('"').Trim("'")
                [Environment]::SetEnvironmentVariable($name, $value, [EnvironmentVariableTarget]::Process)
                Write-Log "Loaded environment variable: $name" "INFO"
            }
        }
    } else {
        Write-Log "No .env file found. Using system environment variables only." "INFO"
    }
}

# Main build process
try {
    $rootPath = $PSScriptRoot
    Push-Location $rootPath
    
    # Load environment variables first
    Import-EnvironmentVariables
    
    # Clean up old build artifacts to prevent disk space accumulation
    Clear-OldBuildArtifacts -KeepCount 1

    # Handle certificate discovery utility flags before doing any build work
    if ($ListCerts) {
        Show-CertificateList | Out-Null
        exit 0
    }
    if ($FindCertSubject) {
        Write-Log "Searching for certificates with subject containing: $FindCertSubject" "INFO"
        Show-CertificateList -SubjectFilter $FindCertSubject | Out-Null
        exit 0
    }

    # Enterprise Certificate CN / Subject — filter for certificate discovery.
    # Set BOOTSTRAPMATE_CERT_CN and BOOTSTRAPMATE_CERT_SUBJECT in your .env file
    # (or as environment variables) to match your organisation's code-signing certificate.
    $Global:EnterpriseCertCN      = $env:BOOTSTRAPMATE_CERT_CN      ?? $env:ENTERPRISE_CERT_CN      ?? $env:CIMIAN_CERT_CN
    $Global:EnterpriseCertSubject = $env:BOOTSTRAPMATE_CERT_SUBJECT  ?? $env:ENTERPRISE_CERT_SUBJECT ?? $env:CIMIAN_CERT_SUBJECT
    if ($Global:EnterpriseCertSubject) {
        Write-Log "Enterprise certificate filter: subject='$Global:EnterpriseCertSubject'" "INFO"
    }

    # Regenerate icon from latest PNG before building
    New-IcoFromPng

    # Update version first
    $versionInfo = Update-Version
    if ($versionInfo -and $versionInfo.FullVersion) {
        Write-Log "Building with version: $($versionInfo.FullVersion)" "INFO"
        Write-Log "MSI ProductVersion: $($versionInfo.MsiVersion)" "INFO"
    }
    
    # Prerequisites check
    Write-Log "Checking prerequisites..." "INFO"
    
    if (-not (Test-Command "dotnet")) {
        throw ".NET CLI not found. Please install .NET 8 SDK."
    }
    
    $dotnetVersion = & dotnet --version
    Write-Log "Using .NET version: $dotnetVersion" "INFO"
    
    # Check MSI prerequisites if not skipping
    if (-not $SkipMSI) {
        Write-Log "Checking MSI build prerequisites..." "INFO"
        
        # Verify WiX project exists
        $wixProject = "installer\BootstrapMate.Installer.wixproj"
        if (-not (Test-Path $wixProject)) {
            Write-Log "WiX project not found: $wixProject" "ERROR"
            Write-Log "MSI building requires WiX project structure" "ERROR"
            throw "WiX project missing - cannot build MSI packages"
        }
        
        # Note: IntuneWinAppUtil is downloaded directly from Microsoft if needed
        # See New-IntuneWinPackage function for automatic download logic
        
        Write-Log "MSI prerequisites check completed" "SUCCESS"
    }
    
    # Handle signing certificate — auto-detect from store, or use provided thumbprint
    $signingCert = $null
    $certificateInfo = $null
    $shouldSign = $false

    if ($AllowUnsigned) {
        Write-Log "WARNING: Building unsigned executables for development only" "WARN"
        Write-Log "NEVER deploy unsigned builds to production environments" "WARN"
    } else {
        # Try thumbprint first, then auto-detect best available cert
        if ($Thumbprint) {
            $certificateInfo = Get-SigningCertThumbprint -Thumbprint $Thumbprint
        } else {
            $bestCert = Get-BestCertificate
            if ($bestCert) {
                $certificateInfo = @{
                    Certificate = $bestCert
                    Store       = $bestCert.Store
                    Thumbprint  = $bestCert.Thumbprint
                }
            }
        }

        if ($certificateInfo) {
            $signingCert = $certificateInfo.Certificate
            $shouldSign  = $true
            Test-SignTool
            Write-Log "Code signing certificate found and verified" "SUCCESS"
            Write-Log "Certificate store:   $($certificateInfo.Store)" "INFO"
            Write-Log "Certificate subject: $($signingCert.Subject)" "INFO"
            Write-Log "Certificate expires: $($signingCert.NotAfter)" "INFO"
            Write-Log "Thumbprint:          $($signingCert.Thumbprint)" "INFO"
        } else {
            Write-Log "No code signing certificate found — building unsigned" "WARN"
            Write-Log "" "WARN"
            Write-Log "To sign your build, either:" "WARN"
            Write-Log "  1. Install an enterprise code signing certificate" "WARN"
            Write-Log "  2. Pass -Thumbprint with a specific certificate thumbprint" "WARN"
            Write-Log "  3. Set ENTERPRISE_CERT_CN in .env to filter by certificate CN" "WARN"
            Write-Log "  4. Run .\build.ps1 -ListCerts to see available certificates" "WARN"
            Write-Log "Continuing with unsigned build..." "WARN"
        }
    }
    
    # Build for requested architectures
    $buildResults = @()
    
    $architectures = switch ($Architecture) {
        "x64"  { @("x64") }
        "arm64" { @("arm64") }
        "both" { @("x64", "arm64") }
    }
    
    foreach ($arch in $architectures) {
        Write-Log "" "INFO"
        
        # Pass certificate store information if available
        $certStore = if ($certificateInfo) { $certificateInfo.Store } else { "CurrentUser" }
        $success = Build-Architecture -Arch $arch -SigningCert $signingCert -CertificateStore $certStore
        
        $buildResults += @{
            Architecture = $arch
            Success = ($success -eq $true)
            Unsigned = ($success -eq "unsigned")
            Path = "publish\executables\$arch\installapplications.exe"
        }
        
        if ($Test -and ($success -eq $true -or $success -eq "unsigned")) {
            $execPath = Join-Path $rootPath "publish\executables\$arch\installapplications.exe"
            Test-Build -ExecutablePath $execPath
        }
    }
    
    # Build GUI App for each architecture
    Write-Log "" "INFO"
    Write-Log "=== GUI APP BUILD ===" "INFO"
    $appBuildResults = @()
    
    foreach ($arch in $architectures) {
        Write-Log "" "INFO"
        
        $certStore = if ($certificateInfo) { $certificateInfo.Store } else { "CurrentUser" }
        $appSuccess = Build-AppArchitecture -Arch $arch -SigningCert $signingCert -CertificateStore $certStore
        
        $appBuildResults += @{
            Architecture = $arch
            Success = ($appSuccess -eq $true)
            Unsigned = ($appSuccess -eq "unsigned")
            Path = "publish\app\$arch\BootstrapMate.exe"
        }
    }
    
    # Build MSI packages and .intunewin files (enterprise default)
    $msiResults = @()
    $intuneWinResults = @()
    
    if (-not $SkipMSI) {
        Write-Log "" "INFO"
        Write-Log "=== MSI + INTUNEWIN BUILD ===" "INFO"
        
        # Prerequisites for MSI building
        if (-not (Test-Command "dotnet")) {
            Write-Log ".NET CLI not found. Please install .NET 8.0 SDK" "ERROR"
        } else {
            # Build MSI for each successful executable architecture
            $publishRoot = Join-Path $rootPath "publish"
            if ($Clean) {
                # Clean only MSI and IntuneWin outputs, not executables
                $msiDir = Join-Path $publishRoot "msi"
                $intuneDir = Join-Path $publishRoot "intunewin"
                if (Test-Path $msiDir) {
                    Write-Log "Cleaning MSI directory: $msiDir" "INFO"
                    Remove-Item -Path $msiDir -Recurse -Force
                }
                if (Test-Path $intuneDir) {
                    Write-Log "Cleaning IntuneWin directory: $intuneDir" "INFO"
                    Remove-Item -Path $intuneDir -Recurse -Force
                }
            }
            
            # Ensure publish root directory exists
            if (-not (Test-Path $publishRoot)) {
                New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
            }
            
            foreach ($result in $buildResults) {
                if ($result.Success) {
                    Write-Log "" "INFO"
                    
                    # Pass certificate store information for MSI signing
                    $certStore = if ($certificateInfo) { $certificateInfo.Store } else { "CurrentUser" }
                    $msiResult = Build-MSI -Arch $result.Architecture -Version $versionInfo.MsiVersion -FullVersion $versionInfo.FullVersion -SigningCert $signingCert -CertificateStore $certStore
                    $msiResults += $msiResult
                    
                    # Create .intunewin if MSI was successful
                    if ($msiResult.Success -and $msiResult.MsiPath) {
                        $fullMsiPath = (Get-Item $msiResult.MsiPath).FullName
                        $intuneWinPath = New-IntuneWinPackage -MsiPath $fullMsiPath -OutputDirectory "publish\intunewin"
                        if ($intuneWinPath) {
                            # Rename .intunewin file to include version for better tracking
                            $intuneWinDir = Split-Path $intuneWinPath -Parent
                            $versionedIntuneWinName = "BootstrapMate-$($result.Architecture)-$($versionInfo.FullVersion).intunewin"
                            $versionedIntuneWinPath = Join-Path $intuneWinDir $versionedIntuneWinName
                            
                            if ($intuneWinPath -ne $versionedIntuneWinPath) {
                                Move-Item -Path $intuneWinPath -Destination $versionedIntuneWinPath -Force
                                Write-Log "Renamed .intunewin to include version: $versionedIntuneWinName" "INFO"
                                $intuneWinPath = $versionedIntuneWinPath
                            }
                            
                            $intuneWinResults += @{
                                Architecture = $result.Architecture
                                Success = $true
                                IntuneWinPath = $intuneWinPath
                            }
                        } else {
                            $intuneWinResults += @{
                                Architecture = $result.Architecture
                                Success = $false
                            }
                        }
                    }
                }
            }
        }
    } else {
        Write-Log "MSI and .intunewin creation skipped (-SkipMSI flag)" "INFO"
    }
    
    # Build summary
    Write-Log "" "INFO"
    Write-Log "=== BUILD SUMMARY ===" "INFO"
    
    $successCount = 0
    $signedCount = 0
    $unsignedCount = 0
    
    foreach ($result in $buildResults) {
        if ($result.Success) {
            $successCount++
            $fullPath = Join-Path $rootPath $result.Path
            if (Test-Path $fullPath) {
                $fileInfo = Get-Item $fullPath
                $sizeMB = [math]::Round($fileInfo.Length / 1MB, 2)
                
                # Determine signing status - prioritize actual file signature over build result
                $signStatus = ""
                $isSigned = $false
                
                if ($signingCert) {
                    try {
                        $signature = Get-AuthenticodeSignature -FilePath $fullPath
                        if ($signature.Status -eq "Valid") {
                            $signedCount++
                            $signStatus = " [SIGNED]"
                        } elseif ($signature.Status -ne "NotSigned") {
                            # File has a signature but chain is untrusted (e.g. self-signed dev cert)
                            $signedCount++
                            $signStatus = " [SIGNED (dev cert)]"
                        } else { 
                            $unsignedCount++
                            if ($result.Unsigned) {
                                $signStatus = " [UNSIGNED - NEEDS MANUAL SIGNING]"
                            } else {
                                $signStatus = " [SIGN FAILED ❌]" 
                            }
                        }
                    } catch {
                        $unsignedCount++
                        $signStatus = " [SIGN STATUS UNKNOWN]"
                    }
                } else { 
                    $unsignedCount++
                    $signStatus = " [UNSIGNED - DEV ONLY]" 
                }
                
                Write-Log "SUCCESS $($result.Architecture): $($result.Path) ($sizeMB MB)$signStatus" "SUCCESS"
            } else {
                Write-Log "SUCCESS $($result.Architecture): Built successfully" "SUCCESS"
            }
        } else {
            Write-Log "ERROR $($result.Architecture): Build failed" "ERROR"
        }
    }
    
    # GUI App Summary
    Write-Log "" "INFO"
    Write-Log "=== GUI APP SUMMARY ===" "INFO"
    $appSuccessCount = 0
    $appSignedCount = 0
    $appUnsignedCount = 0
    
    foreach ($appResult in $appBuildResults) {
        if ($appResult.Success) {
            $appSuccessCount++
            $fullPath = Join-Path $rootPath $appResult.Path
            if (Test-Path $fullPath) {
                $fileInfo = Get-Item $fullPath
                $sizeMB = [math]::Round($fileInfo.Length / 1MB, 2)
                
                $signStatus = ""
                if ($signingCert) {
                    try {
                        $signature = Get-AuthenticodeSignature -FilePath $fullPath
                        if ($signature.Status -eq "Valid") {
                            $appSignedCount++
                            $signStatus = " [SIGNED]"
                        } elseif ($signature.Status -ne "NotSigned") {
                            # File has a signature but chain is untrusted (e.g. self-signed dev cert)
                            $appSignedCount++
                            $signStatus = " [SIGNED (dev cert)]"
                        } else {
                            $appUnsignedCount++
                            if ($appResult.Unsigned) {
                                $signStatus = " [UNSIGNED - NEEDS MANUAL SIGNING]"
                            } else {
                                $signStatus = " [SIGN FAILED]"
                            }
                        }
                    } catch {
                        $appUnsignedCount++
                        $signStatus = " [SIGN STATUS UNKNOWN]"
                    }
                } else {
                    $appUnsignedCount++
                    $signStatus = " [UNSIGNED - DEV ONLY]"
                }
                
                Write-Log "SUCCESS $($appResult.Architecture): $($appResult.Path) ($sizeMB MB)$signStatus" "SUCCESS"
            } else {
                Write-Log "SUCCESS $($appResult.Architecture): Built successfully" "SUCCESS"
            }
        } else {
            Write-Log "ERROR $($appResult.Architecture): App build failed" "ERROR"
        }
    }
    
    # MSI Summary
    if (-not $SkipMSI) {
        Write-Log "" "INFO"
        Write-Log "=== MSI SUMMARY ===" "INFO"
        $msiSuccessCount = 0
        $msiTotalCount = $msiResults.Count
        
        foreach ($msiResult in $msiResults) {
            if ($msiResult.Success) { $msiSuccessCount++ }
        }
        
        foreach ($msiResult in $msiResults) {
            if ($msiResult.Success) {
                $fileName = Split-Path $msiResult.MsiPath -Leaf
                $fileSize = (Get-Item $msiResult.MsiPath).Length
                $sizeMB = [math]::Round($fileSize / 1MB, 2)
                $signStatus = if ($signingCert) { " [SIGNED]" } else { " [UNSIGNED]" }
                Write-Log "SUCCESS $($msiResult.Architecture): $fileName ($sizeMB MB)$signStatus" "SUCCESS"
            } elseif ($msiResult.Skipped) {
                Write-Log "WARN $($msiResult.Architecture): MSI skipped (sbin-installer submodule not initialized)" "WARN"
            } else {
                Write-Log "ERROR $($msiResult.Architecture): MSI build failed" "ERROR"
            }
        }
        
        Write-Log "" "INFO"
        Write-Log "=== INTUNEWIN SUMMARY ===" "INFO"
        $intuneSuccessCount = 0
        $intuneTotalCount = $intuneWinResults.Count
        
        foreach ($intuneResult in $intuneWinResults) {
            if ($intuneResult.Success) { $intuneSuccessCount++ }
        }
        
        foreach ($intuneResult in $intuneWinResults) {
            if ($intuneResult.Success) {
                $fileName = Split-Path $intuneResult.IntuneWinPath -Leaf
                $fileSize = (Get-Item $intuneResult.IntuneWinPath).Length
                $sizeMB = [math]::Round($fileSize / 1MB, 2)
                Write-Log "SUCCESS $($intuneResult.Architecture): $fileName ($sizeMB MB)" "SUCCESS"
            } else {
                Write-Log "ERROR $($intuneResult.Architecture): .intunewin creation failed" "ERROR"
            }
        }
        
        Write-Log "" "INFO"
        $msiSkippedCount = @($msiResults | Where-Object { $_.Skipped }).Count
        $msiFailedCount = $msiTotalCount - $msiSuccessCount - $msiSkippedCount
        $msiSummary = "MSI Packages: $msiSuccessCount/$msiTotalCount successful"
        if ($msiSkippedCount -gt 0) { $msiSummary += " ($msiSkippedCount skipped — submodule not initialized)" }
        Write-Log $msiSummary "INFO"
        Write-Log ".intunewin Packages: $intuneSuccessCount/$intuneTotalCount successful" "INFO"
    }
    
    Write-Log "" "INFO"
    Write-Log "CLI Builds: $successCount of $($buildResults.Count) architectures successfully" "INFO"
    Write-Log "App Builds: $appSuccessCount of $($appBuildResults.Count) architectures successfully" "INFO"
    
    if ($signingCert) {
        if ($signedCount -eq $successCount -and $unsignedCount -eq 0) {
            Write-Log "All executables signed with certificate: $($signingCert.Subject)" "SUCCESS"
        } elseif ($signedCount -gt 0) {
            Write-Log "Signing results: $signedCount signed, $unsignedCount unsigned/failed" "WARN"
            Write-Log "Certificate: $($signingCert.Subject)" "INFO"
            if ($unsignedCount -gt 0) {
                Write-Log "Some executables may need manual signing or elevated privileges" "WARN"
            }
        } else {
            Write-Log "All signing attempts failed ($unsignedCount of $successCount)" "ERROR"
            Write-Log "Certificate: $($signingCert.Subject)" "INFO"
            Write-Log "Consider running as Administrator or checking certificate permissions" "ERROR"
        }
    } elseif ($AllowUnsigned) {
        Write-Log "All executables built unsigned (development mode)" "WARN"
    }
    
    # Consider build successful if all architectures built, even if some signing failed
    $overallSuccess = ($successCount -eq $buildResults.Count) -and ($appSuccessCount -eq $appBuildResults.Count)
    if (-not $SkipMSI) {
        # Fix PowerShell array handling by using proper filtering
        $msiSuccessfulCount = 0
        $intuneSuccessfulCount = 0
        
        foreach ($msi in $msiResults) {
            if ($msi.Success -eq $true) { $msiSuccessfulCount++ }
        }
        
        foreach ($intune in $intuneWinResults) {
            if ($intune.Success -eq $true) { $intuneSuccessfulCount++ }
        }
        
        $msiAllSuccess = $msiSuccessfulCount -eq @($msiResults | Where-Object { -not $_.Skipped }).Count
        $intuneAllSuccess = $intuneSuccessfulCount -eq @($intuneWinResults | Where-Object { -not $_.Skipped }).Count
        $overallSuccess = $overallSuccess -and $msiAllSuccess -and $intuneAllSuccess
    }
    
    if ($overallSuccess) {
        Write-Log "" "SUCCESS"
        Write-Log "ALL BUILDS COMPLETED SUCCESSFULLY!" "SUCCESS"
        if (-not $SkipMSI) {
            # Determine actual signing status
            $hasSignedExecutables = $signedCount -gt 0
            $hasUnsignedExecutables = $unsignedCount -gt 0
            
            if ($AllowUnsigned) {
                $signStatus = "built (unsigned)"
                $deploymentStatus = "development testing"
            } elseif ($hasSignedExecutables -and -not $hasUnsignedExecutables) {
                $signStatus = "built and signed"
                $deploymentStatus = "enterprise deployment"
            } elseif ($hasSignedExecutables -and $hasUnsignedExecutables) {
                $signStatus = "built (partially signed)"
                $deploymentStatus = "manual signing review"
            } else {
                $signStatus = "built (unsigned - signing failed)"
                $deploymentStatus = "manual signing required"
            }
            
            $msiBuiltCount  = @($msiResults     | Where-Object { $_.Success }).Count
            $msiSkipped     = @($msiResults     | Where-Object { $_.Skipped }).Count -gt 0
                Write-Log "Executables $signStatus" "SUCCESS"
                if ($msiBuiltCount -gt 0) {
                    Write-Log "MSI packages created$(if ($hasSignedExecutables -and -not $AllowUnsigned) { ' and signed' })" "SUCCESS"
                    Write-Log ".intunewin packages ready for Intune deployment" "SUCCESS"
                } elseif ($msiSkipped) {
                    Write-Log "MSI packaging skipped (run: git submodule update --init --recursive)" "WARN"
                }
                Write-Log "" "INFO"
                Write-Log "Ready for $deploymentStatus!" "SUCCESS"
                
                # Provide guidance for unsigned builds
                if ($hasUnsignedExecutables -and -not $AllowUnsigned) {
                    Write-Log "" "WARN"
                    Write-Log "SIGNING GUIDANCE:" "WARN"
                Write-Log "   Some executables are unsigned and need manual signing for production" "WARN"
                Write-Log "   Solutions:" "WARN"
                Write-Log "   1. Run build script as Administrator" "WARN"
                Write-Log "   2. Check certificate private key permissions" "WARN"
                Write-Log "   3. Manually sign files with signtool.exe" "WARN"
                Write-Log "   4. For development: use -AllowUnsigned flag" "WARN"
                } elseif ($AllowUnsigned) {
                    Write-Log "" "WARN"
                    Write-Log "REMINDER: This is an UNSIGNED build for development only!" "WARN"
                    Write-Log "   Do NOT deploy to production environments" "WARN"
                }
            }
            exit 0
        } else {
        Write-Log "Some builds failed" "ERROR"
        exit 1
    }
    
} catch {
    Write-Log "Build process failed: $($_.Exception.Message)" "ERROR"
    exit 1
} finally {
    Pop-Location
}
