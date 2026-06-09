# Script de déploiement Ddata PRODUCTION (S3 → Unzip → IIS)
# Lancez en PowerShell Administrateur

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

# 0. CONFIGURATION AWS AUTOMATIQUE
Write-Host "[0/5] Vérification AWS..."

# Installer le module AWS si absent
if (-not (Get-Module -ListAvailable -Name AWSPowerShell.NetCore)) {
    Write-Host "  Installation du module AWSPowerShell.NetCore..."
    Install-PackageProvider -Name NuGet -Force -ErrorAction SilentlyContinue | Out-Null
    Set-PSRepository -Name PSGallery -InstallationPolicy Trusted -ErrorAction SilentlyContinue
    Install-Module -Name AWSPowerShell.NetCore -Force -AllowClobber
}
Import-Module AWSPowerShell.NetCore -ErrorAction Stop

# Vérifier si des credentials AWS sont configurés
$canConnect = $false
try {
    $null = Get-AWSCredential -ErrorAction Stop
    $canConnect = $true
} catch {
    # Rien
}

if (-not $canConnect) {
    Write-Host "  Aucun credential AWS trouvé. Configuration requise :" -ForegroundColor Yellow
    $accessKey = Read-Host "  Access Key ID"
    $secretKey = Read-Host "  Secret Access Key" -AsSecureString
    $profileName = "ddata-deploy"
    Set-AWSCredential -AccessKey $accessKey -SecretKey $secretKey -StoreAs $profileName
    Set-AWSRegion -Region $Region
    Write-Host "  Credentials sauvegardés (profile: $profileName)" -ForegroundColor Green
}

Set-AWSRegion -Region $Region

# 1. TÉLÉCHARGER LE ZIP DEPUIS S3
Write-Host "[1/5] Téléchargement depuis S3..."
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Copy-S3Object -BucketName $S3Bucket -Key $S3Key -LocalFile $ZipPath -Region $Region
Write-Host "  Téléchargé : s3://$S3Bucket/$S3Key"

# 2. EXTRAIRE VERS LE DOSSIER DE DÉPLOIEMENT
Write-Host "[2/5] Extraction..."
if (Test-Path $DossierDeploy) { Remove-Item "$DossierDeploy\*" -Recurse -Force }
Expand-Archive -Path $ZipPath -DestinationPath $DossierDeploy -Force

# 3. CONFIGURER LE WEB.CONFIG POUR LA PRODUCTION
Write-Host "[3/5] Configuration web.config..."
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
