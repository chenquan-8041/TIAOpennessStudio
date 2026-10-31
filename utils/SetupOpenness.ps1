# This script configures permissions for TIA Portal Openness
# Must be run as Administrator!

# 1. Check for Administrator privileges
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Warning "Please run this script as Administrator!"
    exit
}

Write-Host "Configuring TIA Portal Openness environment..." -ForegroundColor Cyan

# =============================================================================
# Task 1: Add current user to "Siemens TIA Openness" group
# =============================================================================
$groupName = "Siemens TIA Openness"
$currentUser = $env:USERNAME
$domain = $env:USERDOMAIN
$isMember = $false

# Check if group exists
if ([ADSI]::Exists("WinNT://./$groupName,group")) {
    Write-Host "Found user group: $groupName" -ForegroundColor Green
    
    # Check if user is already in group
    $group = [ADSI]"WinNT://./$groupName,group"
    foreach ($member in $group.Members()) {
        if ($member.Name -eq $currentUser) {
            $isMember = $true
            break
        }
    }

    if ($isMember) {
        Write-Host "User '$currentUser' is already in '$groupName' group." -ForegroundColor Yellow
    } else {
        try {
            # Add user to group
            $group.Add("WinNT://$domain/$currentUser,user")
            Write-Host "Successfully added user '$currentUser' to '$groupName'." -ForegroundColor Green
            Write-Warning "Note: You need to log off and log on again for group permissions to take effect!"
        } catch {
            Write-Error "Failed to add user: $_"
        }
    }
} else {
    Write-Error "User group '$groupName' not found. Please verify TIA Portal Openness is installed."
}

# =============================================================================
# Task 2: Handle Openness Firewall Whitelist
# =============================================================================
# Add whitelist entry for TIAOpennessStudio.exe

# Define program path (script is in utils/, repo root is one level up)
$exePath = "$PSScriptRoot\..\TIAOpennessStudio\bin\Debug\net48\TIAOpennessStudio.exe"

if (Test-Path $exePath) {
    Write-Host "Processing firewall whitelist..." -ForegroundColor Cyan
    
    # Calculate SHA256 Hash
    $fileStream = [System.IO.File]::OpenRead($exePath)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    $hashBytes = $sha256.ComputeHash($fileStream)
    $fileStream.Close()
    $base64Hash = [Convert]::ToBase64String($hashBytes)
    $fileInfo = Get-Item $exePath
    $lastWriteTime = $fileInfo.LastWriteTimeUtc.ToString("yyyy\/MM\/dd HH:mm:ss.fff")

    # Define registry paths
    $versions = @("14.0", "15.0", "15.1", "16.0", "17.0", "18.0")
    
    foreach ($version in $versions) {
        $regPath = "HKLM:\SOFTWARE\Siemens\Automation\Openness\$version\Whitelist\TIAOpennessStudio.exe\Entry"
        
        # Only write if parent path exists
        if (Test-Path "HKLM:\SOFTWARE\Siemens\Automation\Openness\$version") {
            try {
                if (-not (Test-Path $regPath)) {
                    New-Item -Path $regPath -Force | Out-Null
                }
                
                Set-ItemProperty -Path $regPath -Name "FileHash" -Value $base64Hash
                Set-ItemProperty -Path $regPath -Name "DateModified" -Value $lastWriteTime
                Set-ItemProperty -Path $regPath -Name "Path" -Value $exePath
                
                Write-Host "Added whitelist entry for TIA Portal V$version." -ForegroundColor Green
            } catch {
                Write-Warning "Failed to write registry (V$version): $_"
            }
        }
    }
} else {
    Write-Warning "Executable not found: $exePath"
    Write-Warning "Please build the project first."
}

Write-Host "`nConfiguration complete!" -ForegroundColor Cyan
if (-not $isMember) {
    Write-Warning "Please log off and log on again to apply user group changes."
}
Pause

