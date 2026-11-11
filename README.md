        # Acusa v1.0.0

Demo inicial de la arquitectura de Acusa: un sistema del cliente manda órdenes de facturación por una API, Acusa Central
las guarda en una cola y un Acusa Worker, instalado junto a CONTPAQi Comercial, las ejecuta con el SDK y devuelve el resultado.

```
Sistema del cliente  ->  Acusa Central (portal + API + cola)  <-  Acusa Worker  ->  CONTPAQi Comercial
        (Programador)          127.0.0.1:5090                     panel :5095        SDK de 32 bits
```

El worker siempre consulta a Central; Central nunca abre una conexión hacia el servidor del cliente.

## Componentes

| Carpeta | Qué es |
|---|---|
| `src\Acusa.Orquestador` | Acusa Central: portal web y API (`127.0.0.1:5090`). |
| `src\Acusa.Sdk.Worker` | Acusa Worker (win-x86) con panel web local en `127.0.0.1:5095`. |
| `src\Acusa.Worker.Desktop` | Acusa Worker Desktop (WPF): asistente de configuración del worker. |
| `src\Acusa.Programador` | Sistema externo de ejemplo en `127.0.0.1:5200`: programaciones, cola de corridas y factura manual. |
| `src\Acusa.Contrato` | Contrato compartido: operaciones, DTO y catálogo. |
| `src\Acusa.Sdk.Application`, `src\Acusa.Sdk.Integration.Contpaqi` | Reglas de negocio y acceso al SDK de CONTPAQi. |
| `tests\Acusa.Sdk.Tests` | Pruebas. |

## Cómo arrancar

Requisitos para compilar: .NET SDK 10. Para operar contra CONTPAQi: Windows con Comercial Premium 12.2.1.

```powershell
dotnet build Acusa.Sdk.slnx
dotnet test
```

En el servidor, con el contenido de `Acusa-v1.zip` descomprimido (carpetas `orquestador\`, `worker\`, `programador\`, `desktop\`):

```powershell
powershell -ExecutionPolicy Bypass -File .\demo-servidor.ps1 -Empresa adEMPRESA_DE_PRUEBA -Programador          # contra CONTPAQi
powershell -ExecutionPolicy Bypass -File .\demo-servidor.ps1 -Simulado -Programador                             # sin CONTPAQi, datos en memoria
```

El script imprime la dirección del portal, el usuario y una contraseña generada en cada arranque. Opciones de escritura:
`-Documentos` (crear documentos) y `-Timbrado` (timbrar y cancelar); ambas solo con registros PRUEBA. Detalle en la cabecera del script.

Publicación (los ejecutables salen de aquí): orquestador, programador y desktop con
`dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`; el worker con
`-r win-x86 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`.

## API expuesta

`POST /api/sdk/execute` acepta solo estas nueve operaciones; cualquier otra responde 400:

`cliente.crear`, `cliente.leer`, `cliente.actualizar`, `producto.crear`, `producto.leer`, `documento.crear`, `documento.emitir`,
`documento.obtener-archivo`, `documento.cancelar`.

Con `?esperar=60` (o el encabezado `Prefer: wait=60`) la misma respuesta trae el resultado: 200 si se completó, 422 si falló y 202 si
pasa el tiempo. Con `Idempotency-Key` una orden repetida no se duplica.

### Rutas de facturación (las del Swagger de Alain)

Central ofrece además, con los mismos caminos, métodos y nombres de campo del Swagger de Alain: `/companies` (con `/addresses`),
`/products`, `/issuing_companies` (con `/invoices`, `stamps`, `cancelate`, `xml`, `document`), `/movements`,
`/invoices/batches_stamp` y `POST /users/sessions`. Acusa guarda compañías, productos y facturas (la factura es un borrador hasta
`stamps`) y manda las órdenes a CONTPAQi por el worker. Fuera, por indicación de Alain: reportes, descargas y usuarios. El
documento preliminar y la prefactura responden 501, y subir el certificado por API también.
Configuración en la sección `Orquestador` (`Facturacion*`): empresa por omisión, prefijos de los códigos generados, conceptos y
unidades que ofrecen los catálogos. Ejemplo ejecutable: `ejemplo-rutas-alain.ps1`.

Además: `POST /api/auth/token`, `GET /api/sdk/tasks/{id}`, `GET /api/sdk/tasks`, `GET /api/sdk/workers`, `GET /api/sdk/status`.
La empresa se indica en `empresa` (o con el alias `dbName`).

Flujo mínimo de timbrado: token, `cliente.crear`, `producto.crear`, `documento.crear` (devuelve concepto, serie y folio),
`documento.emitir`, `documento.obtener-archivo`. La pantalla Desarrolladores del portal lo explica con ejemplos.

## Qué queda fuera de v1

- Operaciones no expuestas por la API (el worker las conserva): `cliente.actualizar`, `producto.actualizar`, `direccion.crear`,
  `direccion.leer`, `documento.leer`, `documento.emitir.lote`, `documento.cancelar.lote`.
- Borrado de información: la plataforma no elimina registros contables.
- Pantalla separada de Empresas: la empresa se registra en Configuración del portal (o la registra el asistente del Worker Desktop).
- Escritura y timbrado solo sobre empresas y registros de prueba (nombre con `PRUEBA`).
