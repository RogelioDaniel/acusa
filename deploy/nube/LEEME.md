# Acusa Central en la nube

Central (API, cola, portal y `/api-docs`) puede vivir en cualquier proveedor que corra contenedores y ponga HTTPS. El worker NO se mueve:
sigue en el servidor Windows de Comercial y llama hacia Central (saliente), así que no se abre ningún puerto en ese servidor.

## Qué se necesita del proveedor
- Contenedor Linux construido con `deploy/nube/Dockerfile` (contexto: la raíz del repositorio).
- Un volumen persistente montado en `/datos` (ahí vive la base SQLite; sin volumen se pierde al reiniciar).
- HTTPS delante del contenedor (lo ponen casi todos los proveedores). Puerto interno: 8080.
- **Una sola instancia**: la cola usa SQLite, no admite varias réplicas.

## Variables de entorno (secretas, no van en el repositorio)
| Variable | Qué es |
|---|---|
| `Orquestador__SecretoJwt` | Cadena larga y aleatoria (48 caracteres o más) para firmar los tokens |
| `Orquestador__ClaveAdmin` | Clave de administración |
| `Orquestador__CorreoInicial`, `Orquestador__NombreInicial`, `Orquestador__ClaveInicial` | Primera cuenta del portal (solo se aplica si no hay usuarios) |

Ya vienen puestas en la imagen: `Urls`, `Orquestador__BaseDatos=/datos/orquestador.db` y `Orquestador__ProxyInverso=true`.

## Después del primer arranque
1. Abrir `https://<dirección>/` e iniciar sesión; cambiar la contraseña inicial.
2. En el portal: dar de alta la empresa (`dbName`), crear una llave **api** para el sistema de Alain y una llave **worker** para el servidor.
3. En el servidor Windows, apuntar el worker a la dirección de la nube (`Orquestador__Url`, o en el asistente de Worker Desktop) con la llave worker.
4. Probar en `https://<dirección>/api-docs`.

## Seguridad
- La contraseña del certificado (CSD) nunca llega a Central: vive solo en el proceso del worker.
- Las rutas de administración solo responden desde el propio servidor de Central, nunca por internet.
- Las llaves se guardan con hash; se muestran una sola vez al crearlas.
