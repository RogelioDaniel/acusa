# Administra el servicio de Windows «AcusaWorker». Ejecutar como administrador.
#   .\servicio.ps1 -Accion Instalar   registra el servicio (inicio automático, reinicio si falla, después de SQL de COMPAC)
#   .\servicio.ps1 -Accion Estado     muestra estado, cuenta, inicio y proceso
#   .\servicio.ps1 -Accion Iniciar | Detener | Reiniciar
#   .\servicio.ps1 -Accion Quitar     detiene y elimina el servicio (no borra la configuración)
param(
    [ValidateSet('Instalar', 'Estado', 'Iniciar', 'Detener', 'Reiniciar', 'Quitar')][string]$Accion = 'Estado',
    [string]$Exe = (Join-Path $PSScriptRoot 'Acusa.Sdk.Worker.exe')
)
$ErrorActionPreference = 'Stop'
$nombre = 'AcusaWorker'
$esAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($Accion -ne 'Estado' -and -not $esAdmin) { throw 'Abra PowerShell como administrador (clic derecho, Ejecutar como administrador).' }

function Existe { [bool](Get-Service $nombre -ErrorAction SilentlyContinue) }

switch ($Accion) {
    'Instalar' {
        if (-not (Test-Path $Exe)) { throw "No se encontró $Exe" }
        if (-not (Test-Path 'C:\ProgramData\Acusa\worker-desktop.json')) { Write-Warning 'Todavía no hay configuración: abra Acusa Worker y complete el asistente antes de iniciar el servicio.' }
        if (Existe) { sc.exe stop $nombre | Out-Null; Start-Sleep 3; sc.exe delete $nombre | Out-Null; Start-Sleep 2 }
        New-Service -Name $nombre -BinaryPathName "`"$Exe`" --servicio" -DisplayName 'Acusa Worker' `
            -Description 'Ejecuta en CONTPAQi Comercial las ordenes de Acusa Central.' -StartupType Automatic | Out-Null
        sc.exe config $nombre start= delayed-auto obj= LocalSystem | Out-Null
        sc.exe failure $nombre reset= 86400 actions= restart/15000/restart/30000/restart/60000 | Out-Null
        sc.exe failureflag $nombre 1 | Out-Null
        if (Get-Service 'MSSQL$COMPAC' -ErrorAction SilentlyContinue) { sc.exe config $nombre depend= 'MSSQL$COMPAC' | Out-Null }
        Write-Output "Servicio $nombre instalado. Inícielo con: .\servicio.ps1 -Accion Iniciar"
    }
    'Iniciar' { Start-Service $nombre; Write-Output 'Servicio iniciado.' }
    'Detener' { Stop-Service $nombre -Force; Write-Output 'Servicio detenido.' }
    'Reiniciar' { Restart-Service $nombre -Force; Write-Output 'Servicio reiniciado.' }
    'Quitar' {
        if (Existe) { sc.exe stop $nombre | Out-Null; Start-Sleep 3; sc.exe delete $nombre | Out-Null; Write-Output 'Servicio quitado.' } else { Write-Output 'El servicio no está instalado.' }
    }
    'Estado' {
        if (-not (Existe)) { Write-Output 'El servicio AcusaWorker no está instalado.'; return }
        Get-Service $nombre | Select-Object Name, Status, StartType | Format-Table -AutoSize
        sc.exe qc $nombre | Select-String 'START_TYPE|SERVICE_START_NAME|DEPENDENCIES|BINARY_PATH'
        Get-Process -Name 'Acusa.Sdk.Worker' -ErrorAction SilentlyContinue | Select-Object ProcessName, Id, SessionId | Format-Table -AutoSize
    }
}
