# Instala o actualiza Acusa Worker en C:\Program Files\Acusa Worker, crea accesos directos y registra el desinstalador.
# Solo copia archivos de Acusa: no toca CONTPAQi, Montek ni ninguna otra aplicación.
# Actualizar: detiene el servicio, reemplaza los programas, conserva la configuración de C:\ProgramData\Acusa, vuelve a iniciar el
# servicio, espera a que abra CONTPAQi y muestra un resumen. Instalación nueva: abre el asistente de configuración.
# -Automatica: la usa el propio worker al actualizarse solo. Sin ventanas; respalda la versión instalada y, si la nueva no abre
# CONTPAQi, la regresa y anota la versión en Actualizaciones\fallidas.txt para no volver a intentarla.
param([switch]$Desinstalar, [switch]$SinVentanas, [switch]$Automatica)
$ErrorActionPreference = 'Stop'
if ($Automatica) { $SinVentanas = $true }
$version = ((Get-Item (Join-Path $PSScriptRoot 'Acusa.Sdk.Worker.exe')).VersionInfo.ProductVersion -split '\+')[0]
$destino = Join-Path $env:ProgramFiles 'Acusa Worker'
$datos = Join-Path $env:ProgramData 'Acusa'
$claveDes = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AcusaWorker'
$inicio = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Acusa Worker'
$escritorio = Join-Path $env:PUBLIC 'Desktop\Acusa Worker.lnk'
$bitacoraInstalacion = Join-Path $datos 'instalacion.log'
$respaldo = Join-Path $datos 'Respaldo'

function Nuevo-Acceso($ruta, $exe) {
    $w = New-Object -ComObject WScript.Shell
    $a = $w.CreateShortcut($ruta)
    $a.TargetPath = $exe; $a.WorkingDirectory = $destino; $a.IconLocation = (Join-Path $destino 'acusa.ico'); $a.Description = 'Acusa Worker'
    $a.Save()
}

function Mostrar-Mensaje([string]$texto, [string]$tipo = 'Information') {
    New-Item -ItemType Directory -Force $datos | Out-Null
    Add-Content -Path $bitacoraInstalacion -Value ("[{0:yyyy-MM-dd HH:mm:ss}] {1}{2}" -f (Get-Date), $texto, [Environment]::NewLine) -Encoding UTF8
    Write-Output $texto
    if ($SinVentanas) { return }
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show($texto, 'Acusa Worker', 'OK', $tipo) | Out-Null
}

function Leer-Configuracion {
    $archivo = Join-Path $datos 'worker-desktop.json'
    if (-not (Test-Path $archivo)) { return $null }
    try {
        $c = Get-Content $archivo -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not $c.Configurado) { return $null }
        return $c
    } catch { return $null }
}

# Lee la bitácora del worker desde $desde y decide si ya abrió CONTPAQi. Entiende el formato JSON (1.1+) y el de texto (1.0).
function Esperar-Conexion([datetime]$desde, [int]$segundos = 120) {
    $limite = (Get-Date).AddSeconds($segundos)
    $resultado = [pscustomobject]@{ Estado = 'Esperando'; Detalle = 'El servicio no escribió en la bitácora.' }
    while ((Get-Date) -lt $limite) {
        Start-Sleep -Seconds 3
        $archivo = Join-Path $datos ("Worker\logs\worker-{0:yyyyMMdd}.log" -f (Get-Date))
        if (-not (Test-Path $archivo)) { continue }
        $lineas = Get-Content $archivo -Encoding UTF8 -ErrorAction SilentlyContinue
        foreach ($linea in $lineas) {
            $mensaje = $null
            if ($linea.StartsWith('{')) {
                try {
                    $j = $linea | ConvertFrom-Json
                    if ($j.hora -is [datetime]) { $hora = $j.hora } else { $hora = [datetime]::Parse([string]$j.hora, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind) }
                    if ($hora.ToLocalTime() -lt $desde) { continue }
                    $mensaje = [string]$j.mensaje
                } catch { continue }
            } elseif ($linea -match '^(\d\d):(\d\d):(\d\d) ') {
                $hora = (Get-Date).Date.AddHours([int]$Matches[1]).AddMinutes([int]$Matches[2]).AddSeconds([int]$Matches[3])
                if ($hora -lt $desde) { continue }
                $mensaje = $linea
            } else { continue }

            if ($mensaje -match 'Sesión del SDK lista') { return [pscustomobject]@{ Estado = 'Conectado'; Detalle = $mensaje } }
            if ($mensaje -match 'Esperando la clave en el diálogo') { $resultado = [pscustomobject]@{ Estado = 'Error'; Detalle = 'El worker está esperando la ventana de clave de CONTPAQi: la configuración no indica inicio con usuario y contraseña.' } }
            elseif ($mensaje -match '^(fAbreEmpresa|fSetNombrePAQ|fInicializaSDK)\(.* = (-?[1-9]\d*) \(') { $resultado = [pscustomobject]@{ Estado = 'Error'; Detalle = $mensaje } }
            elseif ($mensaje -match 'Esperando la sesión del SDK \(estado: Error\)\. (.*)') { $resultado = [pscustomobject]@{ Estado = 'Error'; Detalle = $Matches[1] } }
            elseif ($mensaje -match 'lanzó (DllNotFoundException|BadImageFormatException|EntryPointNotFoundException)') { $resultado = [pscustomobject]@{ Estado = 'Error'; Detalle = "No se pudo cargar el SDK de CONTPAQi ($($Matches[1])): revise la carpeta de Comercial en la configuración." } }
            elseif ($mensaje -match 'rechazó la llave') { $resultado = [pscustomobject]@{ Estado = 'Error'; Detalle = 'Acusa Central rechazó la llave del worker.' } }
            elseif ($mensaje -match 'fInicioSesionSDK') { $resultado = [pscustomobject]@{ Estado = 'Esperando'; Detalle = 'Iniciando sesión en CONTPAQi con usuario y contraseña.' } }
        }
        if ($resultado.Estado -eq 'Error') { return $resultado }
    }
    return $resultado
}

if ($Desinstalar) {
    if (Get-Service AcusaWorker -ErrorAction SilentlyContinue) { sc.exe stop AcusaWorker | Out-Null; Start-Sleep 3; sc.exe delete AcusaWorker | Out-Null }
    Get-Process -Name 'Acusa.Worker.Desktop', 'Acusa.Sdk.Worker' -ErrorAction SilentlyContinue | Stop-Process -Force
    Remove-Item $inicio -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $escritorio -Force -ErrorAction SilentlyContinue
    Remove-Item $claveDes -Force -ErrorAction SilentlyContinue
    Remove-Item $destino -Recurse -Force -ErrorAction SilentlyContinue
    Mostrar-Mensaje 'Acusa Worker desinstalado. La configuración (C:\ProgramData\Acusa) no se borra.'
    return
}

try {
    $origen = $PSScriptRoot
    $anterior = (Get-ItemProperty $claveDes -ErrorAction SilentlyContinue).DisplayVersion
    if (-not $anterior -and (Test-Path (Join-Path $destino 'Acusa.Worker.Desktop.exe'))) { $anterior = 'anterior' }
    $configuracion = Leer-Configuracion

    # Se detiene el servicio y se cierra la app para poder reemplazar los archivos.
    if (Get-Service AcusaWorker -ErrorAction SilentlyContinue) {
        Stop-Service AcusaWorker -Force -ErrorAction SilentlyContinue
        (Get-Service AcusaWorker).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    Get-Process -Name 'Acusa.Worker.Desktop', 'Acusa.Sdk.Worker' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2

    # Respaldo de la versión instalada, para regresar a ella si la nueva no abre CONTPAQi.
    if ($anterior -and (Test-Path (Join-Path $destino 'Acusa.Sdk.Worker.exe'))) {
        if (Test-Path $respaldo) { Remove-Item $respaldo -Recurse -Force }
        New-Item -ItemType Directory -Force $respaldo | Out-Null
        Copy-Item (Join-Path $destino '*') $respaldo -Recurse -Force
        Set-Content (Join-Path $respaldo 'version.txt') $anterior -Encoding UTF8
    }

    New-Item -ItemType Directory -Force $destino | Out-Null
    foreach ($f in 'Acusa.Worker.Desktop.exe', 'Acusa.Sdk.Worker.exe', 'appsettings.json', 'acusa.ico', 'instalar.ps1', 'servicio.ps1') {
        Copy-Item (Join-Path $origen $f) $destino -Force
    }

    & (Join-Path $destino 'servicio.ps1') -Accion Instalar | Out-Null
    New-Item -ItemType Directory -Force $inicio | Out-Null
    Nuevo-Acceso (Join-Path $inicio 'Acusa Worker.lnk') (Join-Path $destino 'Acusa.Worker.Desktop.exe')
    Nuevo-Acceso $escritorio (Join-Path $destino 'Acusa.Worker.Desktop.exe')

    New-Item -Force $claveDes | Out-Null
    $props = @{
        DisplayName = 'Acusa Worker'; DisplayVersion = $version; Publisher = 'Local Solutions IT'
        DisplayIcon = (Join-Path $destino 'acusa.ico'); InstallLocation = $destino
        UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$destino\instalar.ps1`" -Desinstalar"
    }
    foreach ($k in $props.Keys) { New-ItemProperty -Path $claveDes -Name $k -Value $props[$k] -Force | Out-Null }
    New-ItemProperty -Path $claveDes -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null

    if (-not $configuracion) {
        # Instalación nueva (o sin configurar): el asistente pide la conexión a Central, CONTPAQi y las empresas.
        Mostrar-Mensaje "Acusa Worker $version instalado en $destino.`n`nA continuación se abre el asistente de configuración. Al terminarlo, el servicio se inicia solo."
        Start-Process (Join-Path $destino 'Acusa.Worker.Desktop.exe')
        return
    }

    $desde = Get-Date
    Start-Service AcusaWorker
    $conexion = Esperar-Conexion $desde
    if ($Automatica -and $conexion.Estado -ne 'Conectado' -and (Test-Path (Join-Path $respaldo 'Acusa.Sdk.Worker.exe'))) {
        Stop-Service AcusaWorker -Force -ErrorAction SilentlyContinue
        Get-Process -Name 'Acusa.Sdk.Worker' -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Seconds 2
        Copy-Item (Join-Path $respaldo '*') $destino -Recurse -Force -Exclude 'version.txt'
        New-ItemProperty -Path $claveDes -Name DisplayVersion -Value $anterior -Force | Out-Null
        New-Item -ItemType Directory -Force (Join-Path $datos 'Actualizaciones') | Out-Null
        Add-Content (Join-Path $datos 'Actualizaciones\fallidas.txt') $version -Encoding UTF8
        Start-Service AcusaWorker
        Mostrar-Mensaje "La versión $version no abrió CONTPAQi ($($conexion.Detalle)). Se regresó a la versión $anterior y no se volverá a intentar la $version." 'Warning'
        return
    }

    $empresas = @($configuracion.Empresas) | Where-Object { $_ }
    $de = if ($anterior) { "Se actualizó de la versión $anterior a la $version." } else { "Se instaló la versión $version." }
    $resumen = @(
        $de
        ''
        'Se conservó la configuración:'
        "  • Acusa Central: $($configuracion.Url)"
        "  • Usuario de CONTPAQi: $($configuracion.UsuarioSdk)"
        "  • Empresa principal: $($configuracion.Empresa)"
        "  • Empresas que atiende: $($empresas.Count)"
        ''
        "Servicio AcusaWorker: $((Get-Service AcusaWorker).Status)"
    ) -join "`n"

    switch ($conexion.Estado) {
        'Conectado' { Mostrar-Mensaje "$resumen`nCONTPAQi: conectado.`n`n$($conexion.Detalle)" }
        'Error' { Mostrar-Mensaje "$resumen`nCONTPAQi: NO se pudo conectar.`n`n$($conexion.Detalle)`n`nPuede volver a la versión anterior ejecutando su instalador; la configuración se conserva." 'Warning' }
        default { Mostrar-Mensaje "$resumen`nCONTPAQi: todavía conectando.`n`n$($conexion.Detalle)`n`nAbra Acusa Worker para seguir la bitácora." 'Warning' }
    }
} catch {
    $falla = $_.Exception.Message
    if ($Automatica) {
        # Una actualización automática nunca deja el worker apagado: vuelve a la versión anterior y la reinicia.
        try {
            if (Test-Path (Join-Path $respaldo 'Acusa.Sdk.Worker.exe')) {
                Copy-Item (Join-Path $respaldo '*') $destino -Recurse -Force -Exclude 'version.txt'
                if ($anterior) { New-ItemProperty -Path $claveDes -Name DisplayVersion -Value $anterior -Force | Out-Null }
            }
            New-Item -ItemType Directory -Force (Join-Path $datos 'Actualizaciones') | Out-Null
            Add-Content (Join-Path $datos 'Actualizaciones\fallidas.txt') $version -Encoding UTF8
        } catch { $falla += " / No se pudo restaurar la versión anterior: $($_.Exception.Message)" }
        Start-Service AcusaWorker -ErrorAction SilentlyContinue
    }
    Mostrar-Mensaje "No se pudo completar la instalación de Acusa Worker $version.`n`n$falla" 'Error'
    exit 1
}
