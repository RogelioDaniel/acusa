# Changelog

## Worker 1.3.0

- Acusa Worker no se abre dos veces en el mismo servidor: la segunda vez muestra la ventana que ya estaba abierta y avisa.
- El instalador espera a que el servicio y la app terminen antes de reemplazar los archivos y reintenta la copia si siguen ocupados.
  Si la instalación falla, regresa a la versión anterior y vuelve a iniciar el servicio. Si la app estaba abierta, la vuelve a abrir.
- La app reporta la versión 1.3.0 (antes se quedaba en 1.2.0 aunque se actualizara).
- Central responde `400` (no `500`) a un cuerpo que no es UTF-8 válido o que no es un objeto JSON.
- La espera de 30 s es de toda la petición, no de cada paso. Si el paso previo de una factura no alcanza a responder, la factura
  se encola igual y se devuelve su tarea.
- Crear cliente guarda el régimen fiscal y el uso de CFDI que se envían. Las respuestas de cliente ya no traen `email`.

## Sin publicar

- La empresa también va por `id`: `GET /api/empresas/listar` devuelve `[{ id, nombre }]` y las rutas son `/api/empresas/{empresaId}/…`.
  El nombre de la empresa en la ruta responde `404`.

- API REST con identificadores de Acusa: los clientes, productos y documentos se crean dentro de la empresa y se consultan, actualizan,
  timbran y cancelan con su `id` (`/api/clientes/{id}`, `/api/productos/{id}`, `/api/documentos/{id}`), sin repetir la empresa. Búsqueda
  por código (o concepto, serie y folio) para registros que ya existían.
- Rutas con la acción: `/timbrar` y `/cancelar` en lugar de `/timbrado` y `/cancelacion`.
- Timbrado y cancelación de varios documentos asíncronos: responden `202` de inmediato con una tarea por documento; máximo configurable
  (500 por omisión) en lugar de 25. Nueva consulta `GET /api/sdk/tasks/pendientes`.
- Espera máxima de 30 segundos, configurable y reducible con `?esperar=N`.
- Cliente sin correo; código postal y país obligatorios. Direcciones con `tipo` numérico (0 fiscal, 1 envío), consulta de todas las
  direcciones del cliente, alta que responde `409` si ya existe y actualización aparte que conserva los campos que no se envían.

- Formato único de error en toda la API: `{ "codigo", "mensaje", "detalles": [ … ], "idCorrelacion" }`, con códigos estables
  (`validacion`, `no_autorizado`, `no_encontrado`, `error_sdk`, `error_interno`, …) y estados HTTP consistentes. Una excepción no
  controlada responde `500` con el mismo formato.
- Los errores del SDK de CONTPAQi se traducen a un mensaje que explica qué corregir; el texto original del SDK queda en `detalles` y el
  código en `codigoSdk`.
- Cada respuesta incluye `X-Correlation-Id` (se conserva el que envía quien llama). Central escribe logs JSON, una línea por evento, con
  el identificador de correlación; el worker escribe `worker-AAAAMMDD.log` en JSON.
- Los logs ocultan llaves de API y de worker, tokens, contraseñas y datos del certificado; no registran cabeceras, cuerpos ni query strings.
- El worker opera en todas las empresas que el cliente elige en su configuración. Ya no se exige el prefijo `PRUEBA` en códigos, razón
  social, nombres ni referencias, y desaparecen `EmpresasEscritura`, `EmpresasReales`, `ClientesPermitidos` y la casilla «Producción»
  del asistente. Escritura, documentos y timbrado siguen siendo interruptores de la configuración, encendidos por omisión.
- Central registra para el cliente la empresa que su worker consulta por primera vez; solo rechaza nombres mal formados.
- Si Central rechaza una empresa, el worker la salta y sigue con las demás (antes detenía la ronda completa y esperaba 60 s).
- La referencia del documento es opcional.
- Actualización automática del worker (1.2.0). `deploy/instalador/publicar-actualizacion.ps1` empaqueta y firma la versión con la
  llave privada de quien publica (RSA-PSS, SHA-256) y la sube a Central (`POST /api/admin/worker/versiones`, clave de administración).
  Central verifica la firma y solo la distribuye (`GET /api/worker/actualizacion`, `…/{version}/paquete`). Cada 30 minutos, sin
  tareas en curso y fuera de los minutos 00 a 14, el worker descarga la versión más nueva, verifica firma, tamaño y SHA-256, respalda la
  instalada y la instala conservando la configuración. Si la nueva no abre CONTPAQi, regresa a la anterior y no vuelve a intentar esa versión.
- El instalador avisa al terminar: versión anterior y nueva, configuración conservada, estado del servicio y de la conexión a CONTPAQi.
- Corregido: el servicio iniciaba con el diálogo de CONTPAQi en lugar de usuario y contraseña (1.1.0); `instalar.ps1` tenía la marca
  BOM repetida y PowerShell no reconocía sus parámetros (1.1.1).

## 1.1.0 (2026-11-13)

- API REST por recursos bajo `/api/empresas/{empresa}`: clientes (con direcciones), productos, documentos, timbrado individual y por
  lote, XML, PDF y cancelación. Cada petición espera al worker hasta 60 segundos y admite `Idempotency-Key`.
- El timbrado devuelve el UUID y el XML en texto plano. Al crear un documento son obligatorios `usoCfdi` y `regimenFiscal`, y cada
  movimiento admite IVA, retención de IVA e IEPS.
- Los impuestos del movimiento se escriben con la secuencia del SDK (`fEditarMovimiento`, `fSetDatoMovimiento`, `fGuardaMovimiento`) y
  los nombres reales de campo (`CPORCENTAJERETENCION2` (la que usa Montek para la retención de IVA)); antes Comercial los ignoraba y aplicaba los valores por omisión.
- Documentación OpenAPI en `/api-docs` con ejemplos de cuerpo y modo claro y oscuro. El portal incluye una consola para lanzar
  peticiones, secciones de prueba y un importador de colecciones de Postman.
- Cada tarea del portal muestra la petición REST que la originó.
- Central siembra llaves, empresas y usuarios iniciales desde variables de entorno (varias llaves separadas por coma), para hosting sin
  disco.
- El worker es un servicio de Windows: arranca con el servidor, se reinicia si falla y admite una sola instancia por servidor. La
  configuración y los secretos cifrados viven en `C:\ProgramData\Acusa`; solo el propietario o un administrador pueden cambiarlos.
- Instalador `AcusaWorker-Setup.exe` que registra el servicio, y asistente de configuración de siete pasos.

## 1.0.0

- Primera versión de Acusa: Acusa Central (portal y API), Acusa Worker (con panel web), Acusa Worker Desktop y Acusa Programador.
- API de integración reducida a lo necesario para un timbrado sencillo: `cliente.crear`, `cliente.leer`, `producto.crear`,
  `producto.leer`, `documento.crear`, `documento.emitir`, `documento.obtener-archivo` y `documento.cancelar`.
  El orquestador rechaza con 400 cualquier otra operación.
- Portal con Panel, Workers, Tareas, Llaves de API, Desarrolladores y Configuración (cuenta y empresas).
- Interfaz web sencilla con una sola hoja de estilos compartida por el portal, el panel del worker y el Programador.
- El worker conserva el cambio entre empresas, el desbloqueo del documento y las guardas de escritura (solo PRUEBA).
- `deploy\demo-servidor.ps1` para levantar la demo en el servidor, con las carpetas `orquestador\`, `worker\`, `programador\` y `desktop\`.
