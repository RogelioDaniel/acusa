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
Los clientes, productos y documentos se crean dentro de una empresa (`/api/empresas/{empresa}/…`) y la respuesta trae su `id` de
Acusa; el resto de las operaciones usan ese `id` sin repetir la empresa.

| Recurso | Rutas |
|---|---|
| Empresas | `GET /api/empresas/listar` |
| Clientes | `POST /api/empresas/{empresa}/clientes/crear`, `GET …/clientes/buscar?codigo=`, `GET /api/clientes/{id}/consultar`, `PATCH /api/clientes/{id}/actualizar` |
| Direcciones | `GET /api/clientes/{id}/direcciones/consultar`, `POST …/direcciones/crear`, `PATCH …/direcciones/actualizar` (`tipoDireccion` 0 fiscal, 1 envío) |
| Productos | `POST /api/empresas/{empresa}/productos/crear`, `GET …/productos/buscar?codigo=`, `GET /api/productos/{id}/consultar`, `PATCH /api/productos/{id}/actualizar` |
| Documentos | `POST /api/empresas/{empresa}/documentos/crear`, `GET …/documentos/buscar`, `GET /api/documentos/{id}/consultar` |
| Timbrado y cancelación | `POST /api/documentos/{id}/timbrar`, `POST /api/documentos/{id}/cancelar`, `POST /api/documentos/timbrar` y `/cancelar` (varios) |
| Archivos | `GET /api/documentos/{id}/descargar-xml`, `…/descargar-pdf` |
| Tareas | `GET /api/tareas/{tareaId}/consultar`, `GET /api/tareas/pendientes` |

Autenticación: `Authorization: Bearer <llave de API>` (empieza con `ak_`) o un token de `POST /api/auth/token`. Cada petición espera al
worker hasta 30 segundos (`?esperar=N` la reduce; configurable con `Orquestador__FacturacionEsperaSegundos`): si responde, la misma
respuesta trae el resultado; si no, responde `202` con la tarea. El timbrado y la cancelación de varios documentos responden `202` de
inmediato con una tarea por documento (máximo configurable con `Orquestador__MaximoDocumentosPorLote`, 500 por omisión). Con la
cabecera `Idempotency-Key`, repetir una escritura no la duplica. Todo error responde con el mismo cuerpo,
`{ "codigo", "mensaje", "detalles": [ … ], "idCorrelacion" }`; el identificador también viaja en la cabecera `X-Correlation-Id`.

Los impuestos de cada movimiento son `ivaPorcentaje`, `retencionIvaPorcentaje` e `iepsPorcentaje`. `usoCfdi` y `regimenFiscal` son
obligatorios al crear un documento.

## Despliegue

**Acusa Central** corre en un contenedor (`deploy/nube/Dockerfile`, ver `deploy/nube/LEEME.md`). Con hosting sin disco, las llaves, las
empresas y los usuarios iniciales se siembran con variables de entorno (`Orquestador__LlaveWorkerInicial`,
`Orquestador__LlaveApiInicial`, `Orquestador__EmpresasIniciales`, …); cada una admite varias llaves separadas por coma.

**Acusa Worker** se instala en el servidor de Comercial con `AcusaWorker-Setup.exe`, que registra el servicio de Windows `AcusaWorker`
(arranque automático, reinicio si falla). El asistente pide la dirección de Central, la cuenta del portal, el usuario del SDK, la llave
del worker y las empresas. La configuración y los secretos cifrados quedan en `C:\ProgramData\Acusa` y el registro en
`C:\ProgramData\Acusa\Worker\logs\worker-AAAAMMDD.log` (una línea JSON por evento). El instalador se arma con `deploy/instalador/construir-instalador.ps1`.

## Seguridad

- La contraseña del certificado (CSD) y la del SDK nunca viajan en una petición: viven cifradas (DPAPI de la máquina) en el servidor.
- Las llaves se guardan con hash en Central y se muestran una sola vez.
- En una empresa de pruebas, el worker exige que los códigos y la referencia empiecen con `PRUEBA`. La escritura y el timbrado se
  habilitan por empresa.
- El SDK de CONTPAQi admite un solo proceso a la vez por servidor: por eso hay un único worker, que cambia de empresa según la tarea.

## Contribuir

Ver [CONTRIBUTING.md](CONTRIBUTING.md): convenciones de código, estrategia de ramas y plantilla de pull request. Los cambios del
proyecto están en [CHANGELOG.md](CHANGELOG.md).
