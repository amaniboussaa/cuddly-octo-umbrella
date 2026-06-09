# Script de déploiement Ddata PRODUCTION (Build → Zip → Unzip → IIS)
# Lancez en PowerShell Administrateur

#Requires -RunAsAdministrator

param(
    [string]$DossierPublish = "C:\ddata-prod-publish",
    [string]$DossierDeploy = "C:\ddata-prod",
    [string]$ZipPath = "$env:TEMP\ddata-prod.zip"
)

$racine = $PSScriptRoot
$pool = "ddata-prod-pool"
$site = "ddata"
$port = 5000

Write-Host "=== Déploiement Ddata PRODUCTION ==="

# 1. BUILD + PUBLISH
Write-Host "[1/5] Build + Publish..."
dotnet publish "$racine\src\Ddata\Ddata.csproj" -c Release -o $DossierPublish

# 2. ZIP
Write-Host "[2/5] Compression..."
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Compress-Archive -Path "$DossierPublish\*" -DestinationPath $ZipPath

# 3. UNZIP VERS LE DOSSIER DE DÉPLOIEMENT
Write-Host "[3/5] Extraction..."
if (Test-Path $DossierDeploy) { Remove-Item "$DossierDeploy\*" -Recurse -Force }
Expand-Archive -Path $ZipPath -DestinationPath $DossierDeploy -Force

# 4. CONFIGURER LE WEB.CONFIG POUR LA PRODUCTION
Write-Host "[4/5] Configuration web.config..."
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
        $v2 = $xml.CreateElement("environmentVariable")
        $v2.SetAttribute("name", "Development__DisableKerberos")
        $v2.SetAttribute("value", "true")
        $vars.AppendChild($v2) | Out-Null

        $asp.AppendChild($vars) | Out-Null
    }
    $xml.Save($fichier)
}

# 5. CRÉER LE DOSSIER LOGS
Write-Host "[5/5] Création dossier logs..."
$dossierLogs = "$DossierDeploy\logs"
if (-not (Test-Path $dossierLogs)) {
    New-Item -ItemType Directory -Path $dossierLogs -Force | Out-Null
}

# 6. CONFIGURER IIS
Write-Host "[6/6] Configuration IIS..."
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
