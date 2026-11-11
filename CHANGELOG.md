# Changelog

## 1.0.0

- Primera versión de Acusa: Acusa Central (portal y API), Acusa Worker (con panel web), Acusa Worker Desktop y Acusa Programador.
- API de integración reducida a lo necesario para un timbrado sencillo: `cliente.crear`, `cliente.leer`, `producto.crear`,
  `producto.leer`, `documento.crear`, `documento.emitir`, `documento.obtener-archivo` y `documento.cancelar`.
  El orquestador rechaza con 400 cualquier otra operación.
- Portal con Panel, Workers, Tareas, Llaves de API, Desarrolladores y Configuración (cuenta y empresas).
- Interfaz web sencilla con una sola hoja de estilos compartida por el portal, el panel del worker y el Programador.
- El worker conserva el cambio entre empresas, el desbloqueo del documento y las guardas de escritura (solo PRUEBA).
- `deploy\demo-servidor.ps1` para levantar la demo en el servidor, con las carpetas `orquestador\`, `worker\`, `programador\` y `desktop\`.
