# Publica el worker y la app de escritorio y arma AcusaWorker-Setup.exe (IExpress, incluido en Windows).
# Uso: .\construir-instalador.ps1 [-Salida C:\ruta\carpeta]
param([string]$Salida = (Join-Path $PSScriptRoot '..\..\dist\instalador'))
$ErrorActionPreference = 'Stop'
$v1 = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$trabajo = Join-Path $env:TEMP 'acusa-instalador'
if (Test-Path $trabajo) { Remove-Item $trabajo -Recurse -Force }
New-Item -ItemType Directory $trabajo | Out-Null
New-Item -ItemType Directory -Force $Salida | Out-Null
$Salida = (Resolve-Path $Salida).Path

Set-Location $v1
$pubDesktop = Join-Path $env:TEMP 'acusa-pub-desktop'; $pubWorker = Join-Path $env:TEMP 'acusa-pub-worker'
Remove-Item $pubDesktop, $pubWorker -Recurse -Force -ErrorAction SilentlyContinue
& dotnet publish src\Acusa.Worker.Desktop\Acusa.Worker.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $pubDesktop -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Falló la publicación de la app de escritorio' }
& dotnet publish src\Acusa.Sdk.Worker\Acusa.Sdk.Worker.csproj -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $pubWorker -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Falló la publicación del worker' }
Copy-Item (Join-Path $pubDesktop 'Acusa.Worker.Desktop.exe') $trabajo
Copy-Item (Join-Path $pubWorker 'Acusa.Sdk.Worker.exe') $trabajo
Copy-Item (Join-Path $v1 'src\Acusa.Sdk.Worker\appsettings.json') $trabajo
Copy-Item (Join-Path $PSScriptRoot 'acusa.ico'), (Join-Path $PSScriptRoot 'instalar.ps1'), (Join-Path $PSScriptRoot 'instalar.cmd'), (Join-Path $PSScriptRoot 'servicio.ps1') $trabajo

$archivos = 'Acusa.Worker.Desktop.exe', 'Acusa.Sdk.Worker.exe', 'appsettings.json', 'acusa.ico', 'instalar.ps1', 'instalar.cmd', 'servicio.ps1'
foreach ($a in $archivos) { if (-not (Test-Path (Join-Path $trabajo $a))) { throw "Falta $a" } }

$destinoExe = Join-Path $Salida 'AcusaWorker-Setup.exe'
if (Test-Path $destinoExe) { Remove-Item $destinoExe -Force }
$fuentes = ($archivos | ForEach-Object { "FILE$([array]::IndexOf($archivos, $_))=`"$_`"" }) -join "`r`n"
$lista = ($archivos | ForEach-Object { "%FILE$([array]::IndexOf($archivos, $_))%=" }) -join "`r`n"
$sed = @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=1
HideExtractAnimation=0
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=%InstallPrompt%
DisplayLicense=
FinishMessage=%FinishMessage%
TargetName=%TargetName%
FriendlyName=%FriendlyName%
AppLaunched=%AppLaunched%
PostInstallCmd=<None>
AdminQuietInstCmd=%AppLaunched%
UserQuietInstCmd=%AppLaunched%
SourceFiles=SourceFiles
[Strings]
InstallPrompt=¿Instalar Acusa Worker en este equipo?
FinishMessage=
TargetName=$destinoExe
FriendlyName=Acusa Worker
AppLaunched=cmd /c instalar.cmd
$fuentes
[SourceFiles]
SourceFiles0=$trabajo\
[SourceFiles0]
$lista
"@
$rutaSed = Join-Path $trabajo 'acusa.sed'
[System.IO.File]::WriteAllText($rutaSed, $sed, [System.Text.Encoding]::GetEncoding(1252))
& "$env:WINDIR\System32\iexpress.exe" /N /Q $rutaSed | Out-Null
for ($i = 0; $i -lt 120 -and -not (Test-Path $destinoExe); $i++) { Start-Sleep -Seconds 1 }
if (-not (Test-Path $destinoExe)) { throw 'IExpress no generó el instalador' }
Start-Sleep -Seconds 3
Write-Output ("Instalador: {0} ({1:N1} MB)" -f $destinoExe, ((Get-Item $destinoExe).Length / 1MB))
