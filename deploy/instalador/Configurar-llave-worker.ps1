# Guarda la llave del worker que usa el servicio «AcusaWorker» y lo reinicia. Lo ejecuta un administrador del servidor.
#   Clic derecho sobre el archivo -> Ejecutar con PowerShell. Si no es administrador, Windows pide el usuario y la contraseña de uno.
# Solo escribe C:\ProgramData\Acusa\worker-llave.dat y reinicia el servicio AcusaWorker. No toca CONTPAQi ni otros programas.
param(
    [string]$Llave,
    [string]$Central = 'https://acusa-i331.onrender.com',
    [string]$Carpeta = 'C:\ProgramData\Acusa',
    [switch]$SinReinicio
)
$ErrorActionPreference = 'Stop'

# Pide permisos de administrador si hacen falta (y vuelve a abrirse con ellos).
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    $argumentos = "-NoProfile -ExecutionPolicy Bypass -NoExit -File `"$PSCommandPath`" -Central `"$Central`" -Carpeta `"$Carpeta`""
    if ($SinReinicio) { $argumentos += ' -SinReinicio' }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argumentos
    return
}

if (-not $Llave) {
    $segura = Read-Host 'Pegue la llave del worker (empieza con wk_)' -AsSecureString
    $Llave = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($segura))
}
$Llave = $Llave.Trim()
if (-not $Llave.StartsWith('wk_') -or $Llave.Length -lt 30) { throw 'Eso no parece una llave de worker (debe empezar con wk_ y ser larga).' }

# 1. Central debe aceptar la llave antes de guardarla.
try {
    Invoke-RestMethod -Method Post "$Central/api/auth/token" -Headers @{ 'x-api-key' = $Llave } -TimeoutSec 90 | Out-Null
    Write-Host 'Central acepta la llave.' -ForegroundColor Green
} catch {
    $codigo = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
    throw "Central no aceptó la llave (HTTP $codigo). No se guardó nada. Revise que sea la misma que está en Render (Orquestador__LlaveWorkerInicial)."
}

# 2. Se guarda cifrada con DPAPI de la máquina, igual que la guarda la app, y solo SYSTEM y administradores pueden leerla.
Add-Type -AssemblyName System.Security
$cifrada = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($Llave), [Text.Encoding]::UTF8.GetBytes('acusa-worker'), 'LocalMachine')
New-Item -ItemType Directory -Force $Carpeta | Out-Null
$archivo = Join-Path $Carpeta 'worker-llave.dat'
[IO.File]::WriteAllBytes($archivo, $cifrada)
icacls $archivo /inheritance:r /grant 'SYSTEM:F' 'Administrators:F' | Out-Null
Write-Host "Llave guardada en $archivo" -ForegroundColor Green

# 3. Se reinicia el servicio.
if ($SinReinicio) { Write-Host 'Sin reinicio (se pidió -SinReinicio).'; return }
if (Get-Service AcusaWorker -ErrorAction SilentlyContinue) {
    Restart-Service AcusaWorker -Force
    Start-Sleep 20
    Get-Service AcusaWorker | Format-Table Name, Status -AutoSize
    Write-Host 'Listo. En el portal de Acusa Central, Workers debe mostrar el worker en línea en menos de un minuto.' -ForegroundColor Green
} else {
    Write-Warning 'El servicio AcusaWorker no está instalado. Instálelo desde la app (Instalar servicio de Windows).'
}
