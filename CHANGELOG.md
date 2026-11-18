# Changelog

## Sin publicar

- Formato único de error en toda la API: `{ "codigo", "mensaje", "detalles": [ … ], "idCorrelacion" }`, con códigos estables
  (`validacion`, `no_autorizado`, `no_encontrado`, `error_sdk`, `error_interno`, …) y estados HTTP consistentes. Una excepción no
  controlada responde `500` con el mismo formato.
- Los errores del SDK de CONTPAQi se traducen a un mensaje que explica qué corregir; el texto original del SDK queda en `detalles` y el
  código en `codigoSdk`.
- Cada respuesta incluye `X-Correlation-Id` (se conserva el que envía quien llama). Central escribe logs JSON, una línea por evento, con
  el identificador de correlación; el worker escribe `worker-AAAAMMDD.log` en JSON.
- Los logs ocultan llaves de API y de worker, tokens, contraseñas y datos del certificado; no registran cabeceras, cuerpos ni query strings.

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
