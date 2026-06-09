# Script de déploiement Ddata PRODUCTION (S3 → Unzip → IIS)
# Lancez en PowerShell Administrateur
# Prérequis : AWS Tools for PowerShell (Install-Module -Name AWSPowerShell.NetCore)

#Requires -RunAsAdministrator

param(
    [string]$S3Bucket = "ddata-deploy",
    [string]$S3Key = "ddata-prod.zip",
    [string]$Region = "eu-west-1",
    [string]$DossierDeploy = "C:\ddata-prod",
    [string]$ZipPath = "$env:TEMP\ddata-prod.zip"
)

$pool = "ddata-prod-pool"
$site = "ddata"
$port = 5000

Write-Host "=== Déploiement Ddata PRODUCTION (depuis S3) ==="

# 1. TÉLÉCHARGER LE ZIP DEPUIS S3
Write-Host "[1/3] Téléchargement depuis S3..."
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Copy-S3Object -BucketName $S3Bucket -Key $S3Key -LocalFile $ZipPath -Region $Region
Write-Host "  Téléchargé : s3://$S3Bucket/$S3Key"

# 2. EXTRAIRE VERS LE DOSSIER DE DÉPLOIEMENT
Write-Host "[2/3] Extraction..."
if (Test-Path $DossierDeploy) { Remove-Item "$DossierDeploy\*" -Recurse -Force }
Expand-Archive -Path $ZipPath -DestinationPath $DossierDeploy -Force

# 3. CONFIGURER LE WEB.CONFIG POUR LA PRODUCTION
Write-Host "[3/3] Configuration web.config..."
$fichier = "$DossierDeploy\web.config"
[xml]$xml = Get-Content $fichier
$asp = $xml.SelectSingleNode("//aspNetCore")
if ($asp) {
    $asp.SetAttribute("stdoutLogEnabled", "true")
    if ($asp.EnvironmentVariables -eq $null) {
        $vars = $xml.CreateElement("environmentVariables")
        $v1 = $xml.CreateElement("environmentVariable")
        $v1.SetAttribute("name", "ASPNETCORE_ENVIRONMENT")
        $v1.SetAttribute("value", "Production")
        $vars.AppendChild($v1) | Out-Null

        $asp.AppendChild($vars) | Out-Null
    }
    $xml.Save($fichier)
}

# 4. CRÉER LE DOSSIER LOGS
Write-Host "[4/5] Création dossier logs..."
$dossierLogs = "$DossierDeploy\logs"
if (-not (Test-Path $dossierLogs)) {
    New-Item -ItemType Directory -Path $dossierLogs -Force | Out-Null
}

# 5. CONFIGURER IIS
Write-Host "[5/5] Configuration IIS..."
Import-Module WebAdministration

# Pool
if (-not (Test-Path "IIS:\AppPools\$pool")) {
    New-Item "IIS:\AppPools\$pool" -Force | Out-Null
}
Set-ItemProperty "IIS:\AppPools\$pool" managedRuntimeVersion ""
Set-ItemProperty "IIS:\AppPools\$pool" startMode "AlwaysRunning"

# Site
if (-not (Test-Path "IIS:\Sites\$site")) {
    New-Item "IIS:\Sites\$site" -PhysicalPath $DossierDeploy -Binding @{protocol="http";bindingInformation="*:${port}:"} -ApplicationPool $pool -Force | Out-Null
} else {
    Set-ItemProperty "IIS:\Sites\$site" physicalPath $DossierDeploy
    Set-ItemProperty "IIS:\Sites\$site" applicationPool $pool
}

# Redémarrage
Restart-WebAppPool $pool

Write-Host ""
Write-Host "=== Terminé ===" -ForegroundColor Green
Write-Host "URL : http://localhost:5000"
Write-Host "Zip : $ZipPath"
Write-Host "Logs : $DossierDeploy\logs\"
