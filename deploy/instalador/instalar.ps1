# Instala Acusa Worker en C:\Program Files\Acusa Worker, crea accesos directos y registra el desinstalador.
# Solo copia archivos de Acusa: no toca CONTPAQi, Montek ni ninguna otra aplicación.
param([switch]$Desinstalar)
$ErrorActionPreference = 'Stop'
$destino = Join-Path $env:ProgramFiles 'Acusa Worker'
$claveDes = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AcusaWorker'
$inicio = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Acusa Worker'
$escritorio = Join-Path $env:PUBLIC 'Desktop\Acusa Worker.lnk'

function Nuevo-Acceso($ruta, $exe) {
    $w = New-Object -ComObject WScript.Shell
    $a = $w.CreateShortcut($ruta)
    $a.TargetPath = $exe; $a.WorkingDirectory = $destino; $a.IconLocation = (Join-Path $destino 'acusa.ico'); $a.Description = 'Acusa Worker'
    $a.Save()
}

if ($Desinstalar) {
    if (Get-Service AcusaWorker -ErrorAction SilentlyContinue) { sc.exe stop AcusaWorker | Out-Null; Start-Sleep 3; sc.exe delete AcusaWorker | Out-Null }
    Get-Process -Name 'Acusa.Worker.Desktop', 'Acusa.Sdk.Worker' -ErrorAction SilentlyContinue | Stop-Process -Force
    Remove-Item $inicio -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $escritorio -Force -ErrorAction SilentlyContinue
    Remove-Item $claveDes -Force -ErrorAction SilentlyContinue
    Remove-Item $destino -Recurse -Force -ErrorAction SilentlyContinue
    Write-Output 'Acusa Worker desinstalado. La configuración de cada usuario (%LOCALAPPDATA%\Acusa) no se borra.'
    return
}

$origen = $PSScriptRoot
$yaInstalado = Test-Path (Join-Path $destino 'Acusa.Worker.Desktop.exe')
# Si el servicio está instalado se detiene para reemplazar el archivo y se vuelve a iniciar al terminar.
$servicioActivo = (Get-Service AcusaWorker -ErrorAction SilentlyContinue).Status -eq 'Running'
if (Get-Service AcusaWorker -ErrorAction SilentlyContinue) { Stop-Service AcusaWorker -Force; Start-Sleep 2 }
Get-Process -Name 'Acusa.Worker.Desktop', 'Acusa.Sdk.Worker' -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force $destino | Out-Null
foreach ($f in 'Acusa.Worker.Desktop.exe', 'Acusa.Sdk.Worker.exe', 'appsettings.json', 'acusa.ico', 'instalar.ps1', 'servicio.ps1') {
    Copy-Item (Join-Path $origen $f) $destino -Force
}

# El worker es un servicio de Windows: se registra aquí (arranca con el servidor y se reinicia solo si falla). Si ya hay configuración
# de una instalación anterior, o el servicio estaba corriendo, se inicia; si no, lo inicia la app al terminar el asistente.
& (Join-Path $destino 'servicio.ps1') -Accion Instalar | Out-Null
$hayConfiguracion = Test-Path (Join-Path $env:ProgramData 'Acusa\worker-desktop.json')
if ($servicioActivo -or $hayConfiguracion) { Start-Service AcusaWorker -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force $inicio | Out-Null
Nuevo-Acceso (Join-Path $inicio 'Acusa Worker.lnk') (Join-Path $destino 'Acusa.Worker.Desktop.exe')
Nuevo-Acceso $escritorio (Join-Path $destino 'Acusa.Worker.Desktop.exe')

New-Item -Force $claveDes | Out-Null
$props = @{
    DisplayName = 'Acusa Worker'; DisplayVersion = '1.0.0'; Publisher = 'Local Solutions IT'
    DisplayIcon = (Join-Path $destino 'acusa.ico'); InstallLocation = $destino
    UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$destino\instalar.ps1`" -Desinstalar"
}
foreach ($k in $props.Keys) { New-ItemProperty -Path $claveDes -Name $k -Value $props[$k] -Force | Out-Null }
New-ItemProperty -Path $claveDes -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null

Write-Output ($(if ($yaInstalado) { 'Acusa Worker actualizado en ' } else { 'Acusa Worker instalado en ' }) + $destino)
Start-Process (Join-Path $destino 'Acusa.Worker.Desktop.exe')
