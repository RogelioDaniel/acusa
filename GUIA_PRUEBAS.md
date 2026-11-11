# Guía de pruebas manuales · Acusa v1

Empresa de pruebas: HERRERIA & ELECTRICOS (base `adEMPRESA_DE_PRUEBA`). No usar otra empresa.

## Direcciones (en el navegador del servidor)

| Qué | Dirección | Acceso |
|---|---|---|
| Acusa Central (portal del cliente y desarrollador) | http://127.0.0.1:5090 | usuario `admin@localsolutionsit.com`; la contraseña está en la ventana de PowerShell que lanzó el script |
| Acusa Programador (sistema externo de ejemplo) | http://127.0.0.1:5200 | sin clave |
| Panel del worker | http://127.0.0.1:5095 | código de acceso en la ventana negra del worker (`Acusa.Sdk.Worker.exe`) |
| CONTPAQi Comercial | ya abierto | usuario SUPERVISOR, sin contraseña |

## Pruebas, en orden

1. **Portal.** Entre a Acusa Central. En Inicio verá el worker en línea y la empresa. En Llaves de API puede crear una llave de tipo Sistema.
2. **Factura manual.** Programador → Factura manual → «Crear y timbrar factura» (cliente PRUEBADEMO01, producto PRUEBAPR01, $1). El estado pasa de Creando a Timbrando y a Timbrada, con UUID. Los botones PDF y XML descargan los archivos.
3. **Verificar en Comercial.** Documentos → Factura (o el reporte de ventas del día): debe aparecer el folio PRUEBA## con el mismo total. Si pide abrirlo, ábralo solo para verlo y ciérrelo con Alt+C; no lo modifique.
4. **Ejecutar ahora.** Programador → Programaciones → «Ejecutar ahora» en cualquier horario. Crea 1 o 2 facturas y las timbra.
5. **Horario automático.** Active «Demostración: cada 5 minutos». Cada 5 minutos aparece una factura nueva sola. **Apáguela al terminar.**
6. **Cola de corridas.** Programador → Cola de corridas: cada corrida con su hora, documentos y resultado.
7. **Tareas en el portal.** En Acusa Central, la lista de tareas muestra cada operación (cliente, documento.crear, documento.emitir) con su tiempo.
8. **Cancelar.** En Factura manual, «Cancelar» sobre una factura de prueba timbrada (avisa al SAT; úselo con moderación).

## Reglas

- Solo se crean documentos con serie PRUEBA, cliente PRUEBADEMO01 y producto PRUEBAPR01.
- Cada factura consume un folio que no se reutiliza, y cada timbrado gasta un timbre del PAC.
- No tocar INSTANT CAPITAL ni otras empresas.
- Para terminar: en la ventana de PowerShell, pulse Enter (detiene worker, orquestador y Programador).
