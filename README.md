# Acusa

API REST para crear, timbrar y cancelar facturas electrónicas sobre CONTPAQi Comercial. Un sistema del cliente llama a Acusa Central; un
Acusa Worker, instalado en el servidor de Comercial, ejecuta cada petición con el SDK de CONTPAQi y devuelve el resultado.

```
Sistema del cliente  ->  Acusa Central (API REST + portal + cola)  <-  Acusa Worker (servicio de Windows)  ->  CONTPAQi Comercial
```

El worker siempre consulta a Central: Central nunca abre una conexión hacia el servidor del cliente.

## Capas

| Proyecto | Capa | Qué contiene |
|---|---|---|
| `src/Acusa.Orquestador` | API | Acusa Central: API REST, cola de tareas, portal web y documentación OpenAPI. |
| `src/Acusa.Contrato` | Contrato | Operaciones, DTO y reglas compartidas entre Central y el worker. |
| `src/Acusa.Sdk.Application` | Aplicación | Reglas de negocio de clientes, productos y documentos, y la política de escritura. |
| `src/Acusa.Sdk.Integration.Contpaqi` | Integración con CONTPAQi | Llamadas nativas al SDK (`MGWServicios.dll`, 32 bits), con su traza. |
| `src/Acusa.Sdk.Worker` | Worker | Servicio que toma tareas de Central y las ejecuta con el SDK. |
| `src/Acusa.Worker.Desktop` | Aplicación de escritorio | Asistente de configuración y panel del servicio del worker. |
| `src/Acusa.Programador` | Ejemplo | Sistema externo de ejemplo que consume la API. |
| `tests/Acusa.Sdk.Tests` | Pruebas | Pruebas unitarias y de integración sin CONTPAQi. |

## Compilar y probar

Requisitos: .NET SDK 10. Para operar contra CONTPAQi: Windows con Comercial Premium 12.2.1.

```powershell
dotnet build Acusa.Sdk.slnx
dotnet test Acusa.Sdk.slnx
```

## API

La definición completa está en `src/Acusa.Orquestador/wwwroot/api-docs/openapi.yaml` y se publica con Swagger UI en `/api-docs`.
La empresa de CONTPAQi va en la ruta: `/api/empresas/{empresa}/…`.

| Recurso | Rutas |
|---|---|
| Clientes | `POST …/clientes`, `GET` y `PATCH …/clientes/{codigo}`, `POST` y `GET …/clientes/{codigo}/direcciones` |
| Productos | `POST …/productos`, `GET` y `PATCH …/productos/{codigo}` |
| Documentos | `POST …/documentos`, `GET …/documentos/{concepto}/{serie}/{folio}` |
| Timbrado | `POST …/documentos/{concepto}/{serie}/{folio}/timbrado` (devuelve el UUID y el XML en texto plano), `POST …/documentos/timbrado` (lote de hasta 25) |
| Archivos | `GET …/{concepto}/{serie}/{folio}/xml` y `…/pdf` |
| Cancelación | `POST …/{concepto}/{serie}/{folio}/cancelacion`, `POST …/documentos/cancelacion` (lote) |

Autenticación: `Authorization: Bearer <llave de API>` (empieza con `ak_`) o un token de `POST /api/auth/token`. Cada petición espera al
worker hasta 60 segundos: si responde, la misma respuesta trae el resultado; si no, responde `202` con la tarea
(`GET /api/sdk/tasks/{id}`). Con la cabecera `Idempotency-Key`, repetir una escritura no la duplica. Los errores de validación
responden `422` con `{ "errors": [ … ] }`.

Los impuestos de cada movimiento son `ivaPorcentaje`, `retencionIvaPorcentaje` e `iepsPorcentaje`. `usoCfdi` y `regimenFiscal` son
obligatorios al crear un documento.

## Despliegue

**Acusa Central** corre en un contenedor (`deploy/nube/Dockerfile`, ver `deploy/nube/LEEME.md`). Con hosting sin disco, las llaves, las
empresas y los usuarios iniciales se siembran con variables de entorno (`Orquestador__LlaveWorkerInicial`,
`Orquestador__LlaveApiInicial`, `Orquestador__EmpresasIniciales`, …); cada una admite varias llaves separadas por coma.

**Acusa Worker** se instala en el servidor de Comercial con `AcusaWorker-Setup.exe`, que registra el servicio de Windows `AcusaWorker`
(arranque automático, reinicio si falla). El asistente pide la dirección de Central, la cuenta del portal, el usuario del SDK, la llave
del worker y las empresas. La configuración y los secretos cifrados quedan en `C:\ProgramData\Acusa` y el registro en
`C:\ProgramData\Acusa\Worker\logs\worker-AAAAMMDD.log`. El instalador se arma con `deploy/instalador/construir-instalador.ps1`.

## Seguridad

- La contraseña del certificado (CSD) y la del SDK nunca viajan en una petición: viven cifradas (DPAPI de la máquina) en el servidor.
- Las llaves se guardan con hash en Central y se muestran una sola vez.
- En una empresa de pruebas, el worker exige que los códigos y la referencia empiecen con `PRUEBA`. La escritura y el timbrado se
  habilitan por empresa.
- El SDK de CONTPAQi admite un solo proceso a la vez por servidor: por eso hay un único worker, que cambia de empresa según la tarea.

## Contribuir

Ver [CONTRIBUTING.md](CONTRIBUTING.md): convenciones de código, estrategia de ramas y plantilla de pull request. Los cambios del
proyecto están en [CHANGELOG.md](CHANGELOG.md).
