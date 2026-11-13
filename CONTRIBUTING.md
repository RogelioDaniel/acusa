# Cómo contribuir

## Estrategia de ramas

- `main`: versión estable. No se trabaja directamente en ella.
- `dev`: integración. Aquí se juntan las ramas de trabajo antes de pasar a `main`.
- `feature/CS-NN-descripcion-corta`: una rama por historia de Jira, creada desde `dev`. El número es el de la historia
  (por ejemplo, `feature/CS-12-estructura-y-openapi`).

Flujo: `feature/CS-NN-…` → pull request a `dev` → revisión → fusión. De `dev` a `main` se pasa con otro pull request al cerrar un sprint.
Nunca se sube directo a `main` ni a `dev`.

## Commits

Mensajes en español, en presente y en una línea que diga qué cambia, empezando por el número de la historia cuando aplica:
`CS-12: agrega la plantilla de pull request`. Un commit por cambio coherente; el detalle va en el cuerpo del mensaje.

## Pull requests

- Se abren contra `dev` y usan la plantilla de `.github/pull_request_template.md`.
- Deben compilar y pasar las pruebas: `dotnet build Acusa.Sdk.slnx` y `dotnet test Acusa.Sdk.slnx`.
- Un pull request resuelve una historia y explica cómo se probó. Los cambios que escriben en CONTPAQi deben indicar contra qué
  empresa de pruebas se verificaron.

## Convenciones de código

- Formato: el de `.editorconfig`. Se respeta antes de abrir el pull request.
- C# con `nullable` habilitado y sin advertencias nuevas.
- Nombres del dominio de facturación en español (`Documento`, `Partida`, `Movimiento`); infraestructura y tipos técnicos en inglés
  como los nombra .NET.
- Capas: `Contrato` no depende de nada; `Sdk.Application` depende de `Contrato`; `Sdk.Integration.Contpaqi` implementa lo que define la
  aplicación; el worker y Central componen las capas. La API y el worker no llaman al SDK directamente: pasan por la integración.
- Toda llamada al SDK de CONTPAQi pasa por la capa de integración, que registra su traza. El SDK se ejecuta en un solo hilo y un
  solo proceso por servidor.
- Los errores de validación se devuelven como `422` con `{ "errors": [ … ] }`; los fallos de CONTPAQi traducen el código del SDK a un
  mensaje entendible.
- Nunca se escriben contraseñas, llaves ni datos del certificado en registros, respuestas ni el repositorio. Los secretos van en
  variables de entorno o cifrados en el servidor.
- Toda regla de negocio nueva lleva su prueba en `tests/Acusa.Sdk.Tests`. Las pruebas no necesitan CONTPAQi.

## Documentación de la API

La definición OpenAPI vive en `src/Acusa.Orquestador/wwwroot/api-docs/openapi.yaml`; `openapi.json` se genera a partir de ella. Una
ruta nueva se documenta en el mismo pull request que la crea, con ejemplo de cuerpo y respuestas.
