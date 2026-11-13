"use strict";

/* Acusa Central 1.0.0: portal del orquestador. Sin dependencias ni paso de compilación.
   Todo el texto que viene del servidor entra con textContent (nunca como HTML). */

const VERSION = "1.0.0";
const PRODUCTO = "Acusa Central";

// ---------------------------------------------------------------- utilidades

const $ = (s, r = document) => r.querySelector(s);

function h(tag, attrs, ...hijos) {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v == null || v === false) continue;
    if (k === "class") e.className = v;
    else if (k.startsWith("on")) e.addEventListener(k.slice(2), v);
    else if (v === true) e.setAttribute(k, "");
    else e.setAttribute(k, v);
  }
  for (const c of hijos.flat(Infinity)) {
    if (c == null || c === false) continue;
    e.append(c.nodeType ? c : document.createTextNode(String(c)));
  }
  return e;
}

const marca = (sufijo) => h("div", { class: "marca" }, h("b", null, "A"), h("span", null, "Acusa ", sufijo ? h("small", null, sufijo) : null));
const hora = (iso) => iso ? new Date(iso).toLocaleTimeString("es-MX", { hour: "2-digit", minute: "2-digit", second: "2-digit" }) : "—";
const fechaHora = (iso) => iso ? new Date(iso).toLocaleString("es-MX", { dateStyle: "medium", timeStyle: "short" }) : "—";
function hace(iso) {
  if (!iso) return "nunca";
  const s = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
  if (s < 60) return `hace ${s} s`;
  if (s < 3600) return `hace ${Math.round(s / 60)} min`;
  if (s < 86400) return `hace ${Math.round(s / 3600)} h`;
  return `hace ${Math.round(s / 86400)} d`;
}
const json = (v) => JSON.stringify(v, null, 2);
const estadoTexto = (e) => (e === "EnProceso" ? "En proceso" : e);
const estadoFicha = (e) => h("span", { class: "estado-" + e }, estadoTexto(e));

function aviso(texto) {
  const t = h("div", { class: "toast", role: "status" }, texto);
  document.body.append(t);
  setTimeout(() => t.remove(), 3200);
}

async function copiar(texto) {
  try { await navigator.clipboard.writeText(texto); aviso("Copiado"); }
  catch { aviso("No se pudo copiar; selecciónelo y use Ctrl+C"); }
}

// ---------------------------------------------------------------- sesión y API

const sesion = {
  get token() { try { return sessionStorage.getItem("acusa.token"); } catch { return null; } },
  get usuario() { try { return JSON.parse(sessionStorage.getItem("acusa.usuario") || "null"); } catch { return null; } },
  guardar(token, usuario) { try { sessionStorage.setItem("acusa.token", token); sessionStorage.setItem("acusa.usuario", JSON.stringify(usuario)); } catch { /* sin almacenamiento */ } },
  borrar() { try { sessionStorage.clear(); } catch { /* sin almacenamiento */ } },
};

async function api(ruta, opciones = {}) {
  const r = await fetch(ruta, {
    ...opciones,
    headers: { "Content-Type": "application/json", ...(opciones.headers || {}), ...(sesion.token ? { Authorization: "Bearer " + sesion.token } : {}) },
  });
  if (r.status === 401 && sesion.token) { sesion.borrar(); mostrarEntrada("La sesión venció. Entre de nuevo."); throw new Error("sesión vencida"); }
  if (r.status === 204) return null;
  let cuerpo = null;
  try { cuerpo = await r.json(); } catch { /* sin cuerpo */ }
  if (!r.ok) throw new Error((cuerpo && (cuerpo.error || cuerpo.title)) || `Error ${r.status}`);
  return cuerpo;
}

const falla = (caja) => (e) => { if (e.message !== "sesión vencida") caja.replaceChildren(h("div", { class: "vacio" }, e.message)); };

// ---------------------------------------------------------------- entrada

function mostrarEntrada(mensaje) {
  detenerTemporizadores();
  const error = h("p", { class: "error", role: "alert" }, mensaje || "");
  const correo = h("input", { type: "email", autocomplete: "username", required: true, id: "usuario" });
  const clave = h("input", { type: "password", autocomplete: "current-password", required: true, id: "clave" });
  const boton = h("button", { class: "boton", type: "submit" }, "Entrar");
  const forma = h("form", {
    onsubmit: async (e) => {
      e.preventDefault(); boton.disabled = true; error.textContent = "";
      try {
        const r = await api("/api/auth/login", { method: "POST", body: JSON.stringify({ correo: correo.value, contrasena: clave.value }) });
        sesion.guardar(r.token, r.usuario);
        arrancar();
      } catch (err) { error.textContent = err.message; boton.disabled = false; clave.value = ""; clave.focus(); }
    },
  },
    h("label", { class: "campo", for: "usuario" }, h("span", null, "Correo"), correo),
    h("label", { class: "campo", for: "clave" }, h("span", null, "Contraseña"), clave),
    error, boton);
  $("#app").replaceChildren(h("div", { class: "entrada" }, h("div", { class: "entrada-caja" },
    marca("Central"), h("h1", null, "Entrar"), h("p", null, "Use la cuenta que le dio su administrador."), forma)));
  correo.focus();
}

// ---------------------------------------------------------------- estructura

const RUTAS = [
  { id: "panel", titulo: "Panel", vista: vistaPanel },
  { id: "workers", titulo: "Workers", vista: vistaWorkers },
  { id: "tareas", titulo: "Tareas", vista: vistaTareas },
  { id: "llaves", titulo: "Llaves de API", vista: vistaLlaves },
  { id: "probar", titulo: "Probar", vista: vistaProbar },
  { id: "devs", titulo: "Desarrolladores", vista: vistaDevs },
  { id: "config", titulo: "Configuración", vista: vistaConfig },
];

let temporizadores = [];
function detenerTemporizadores() { temporizadores.forEach(clearInterval); temporizadores = []; }
function cada(ms, fn) { fn(); temporizadores.push(setInterval(fn, ms)); }

function arrancar() {
  if (!sesion.token) { mostrarEntrada(); return; }
  const u = sesion.usuario || {};
  const lateral = h("aside", { class: "lateral" }, marca("Central"),
    RUTAS.map((r) => h("a", { class: "enlace", href: "#/" + r.id, "data-ruta": r.id }, r.titulo)));
  const superior = h("header", { class: "superior" },
    h("span", { class: "cliente" }, u.cliente || "Local Solutions IT"),
    h("span", { class: "punto", id: "punto-estado", title: "Estado de los workers" }),
    h("div", { class: "usuario" }, h("span", null, u.email || u.nombre || ""),
      h("button", { class: "boton suave chico", onclick: () => { sesion.borrar(); mostrarEntrada(); } }, "Cerrar sesión")));
  const pie = h("footer", { class: "pie" }, `${PRODUCTO} · versión ${VERSION}`);
  $("#app").replaceChildren(h("div", { class: "estructura" }, lateral, h("div", { class: "principal" }, superior, h("main", { id: "contenido" }), pie)));
  window.onhashchange = navegar;
  navegar();
}

function navegar() {
  detenerTemporizadores();
  const id = (location.hash || "#/panel").replace(/^#\//, "").split("?")[0];
  const ruta = RUTAS.find((r) => r.id === id) || RUTAS[0];
  document.querySelectorAll(".enlace[data-ruta]").forEach((a) => {
    if (a.dataset.ruta === ruta.id) a.setAttribute("aria-current", "page"); else a.removeAttribute("aria-current");
  });
  document.title = `${ruta.titulo} · ${PRODUCTO}`;
  const contenido = $("#contenido");
  contenido.replaceChildren();
  ruta.vista(contenido);
}

function encabezado(titulo, descripcion, ...acciones) {
  return h("div", { class: "encabezado" }, h("div", null, h("h1", null, titulo), descripcion ? h("p", null, descripcion) : null),
    acciones.length ? h("div", { class: "fila-acciones" }, acciones) : null);
}

/** La petición REST que originó la tarea; las órdenes por /api/sdk/execute no traen ruta propia. La operación interna va como nota. */
const peticionDe = (t) => h("span", null, t.peticion || "POST /api/sdk/execute", h("small", { class: "nota", style: "display:block" }, t.tipoProceso));

const tabla = (cabeceras, filas) => h("div", { class: "tabla-caja" }, h("table", null,
  h("thead", null, h("tr", null, cabeceras.map((t) => h("th", null, t)))), h("tbody", null, filas)));

// ---------------------------------------------------------------- panel

function vistaPanel(raiz) {
  const cuerpo = h("div");
  raiz.append(encabezado("Panel", "Estado de Acusa Central en las últimas 24 horas."), cuerpo);
  cada(3000, async () => {
    try {
      const [r, tareas] = await Promise.all([api("/api/portal/resumen"), api("/api/sdk/tasks?limite=8")]);
      const punto = $("#punto-estado");
      if (punto) punto.className = "punto " + (r.workersTotal === 0 ? "" : r.workersEnLinea > 0 ? "ok" : "mal");
      const dato = (etiqueta, valor, nota) => h("div", { class: "tarjeta" }, h("div", { class: "etiqueta" }, etiqueta), h("div", { class: "valor" }, valor), h("div", { class: "nota" }, nota));
      cuerpo.replaceChildren(
        h("div", { class: "rejilla" },
          dato("Workers en línea", `${r.workersEnLinea} de ${r.workersTotal}`, r.workersTotal === 0 ? "Conecte un worker para empezar." : r.workersEnLinea ? "Atendiendo la cola." : "Ningún worker responde."),
          dato("Tareas completadas", r.completadas, `${r.pendientes + r.enProceso} en espera o en proceso`),
          dato("Tareas fallidas", r.fallidas, r.ultimaTarea ? `Última tarea ${hace(r.ultimaTarea)}` : "Sin tareas aún")),
        h("div", { class: "tarjeta" }, h("h2", null, "Últimas tareas"),
          tareas.length
            ? tabla(["Hora", "Petición", "Empresa", "Estado"], tareas.map((t) => h("tr", null, h("td", { class: "mono" }, hora(t.creada)), h("td", { class: "mono" }, peticionDe(t)),
                h("td", { class: "mono" }, t.empresa), h("td", null, estadoFicha(t.estado)))))
            : h("div", { class: "vacio" }, h("b", null, "Todavía no hay tareas"), "Cuando llegue la primera orden aparecerá aquí.")));
    } catch (e) { falla(cuerpo)(e); }
  });
}

// ---------------------------------------------------------------- workers

function vistaWorkers(raiz) {
  const lista = h("div", { class: "tarjeta" });
  raiz.append(encabezado("Workers", "Cada Acusa Worker corre junto a CONTPAQi Comercial, consulta la cola y ejecuta las órdenes de una empresa."), lista,
    h("div", { class: "tarjeta" }, h("h2", null, "Conectar un worker"),
      h("ol", { class: "pasos" },
        h("li", null, "Registre la empresa en Configuración y cree una llave de tipo worker en Llaves de API."),
        h("li", null, "En el equipo con CONTPAQi Comercial abra Acusa Worker Desktop y siga el asistente."),
        h("li", null, "Dirección de este portal: ", h("span", { class: "mono" }, location.origin)),
        h("li", null, "Al primer latido el worker aparece aquí en línea."))));
  cada(3000, async () => {
    try {
      const ws = await api("/api/sdk/workers");
      lista.replaceChildren(ws.length
        ? tabla(["Estado", "Worker", "Empresa", "Equipo", "Versión", "Última consulta"], ws.map((w) => h("tr", null,
            h("td", null, h("span", { class: "ficha " + (w.enLinea ? "ok" : "mal") }, w.enLinea ? "En línea" : "Sin respuesta")),
            h("td", { class: "mono" }, w.workerId), h("td", { class: "mono" }, w.empresa), h("td", null, w.host || "—"),
            h("td", { class: "mono" }, w.version || "—"), h("td", null, hace(w.ultimaVista)))))
        : h("div", { class: "vacio" }, h("b", null, "Todavía no hay workers"), "Siga los pasos de abajo para conectar el primero."));
    } catch (e) { falla(lista)(e); }
  });
}

// ---------------------------------------------------------------- tareas

function vistaTareas(raiz) {
  const estados = ["", "Pendiente", "EnProceso", "Completada", "Fallida"];
  const filtro = h("select", { "aria-label": "Filtrar por estado" }, estados.map((e) => h("option", { value: e }, e === "" ? "Todos los estados" : estadoTexto(e))));
  const lista = h("div", { class: "tarjeta" });
  raiz.append(encabezado("Tareas", "Cada petición que llega por la API, con su resultado o su error.", h("label", { class: "campo" }, h("span", null, "Estado"), filtro)), lista);
  const abiertas = new Set();
  async function cargar() {
    try {
      const ts = await api("/api/sdk/tasks?limite=100" + (filtro.value ? "&estado=" + filtro.value : ""));
      lista.replaceChildren(ts.length
        ? tabla(["Hora", "Petición", "Empresa", "Estado", "Resultado"], ts.map((t) => {
            const detalle = t.error || (t.resultado ? json(t.resultado) : "");
            const abierta = abiertas.has(t.id);
            return h("tr", null, h("td", { class: "mono" }, hora(t.creada)), h("td", { class: "mono" }, peticionDe(t)), h("td", { class: "mono" }, t.empresa),
              h("td", null, estadoFicha(t.estado)),
              h("td", null, detalle ? h("button", { class: "boton suave chico", onclick: () => { if (abiertas.has(t.id)) abiertas.delete(t.id); else abiertas.add(t.id); cargar(); } }, abierta ? "Ocultar" : "Ver") : "—",
                abierta && detalle ? h("pre", null, detalle) : null));
          }))
        : h("div", { class: "vacio" }, h("b", null, "Sin tareas"), filtro.value ? "No hay tareas con ese estado." : "Cuando llegue la primera orden aparecerá aquí."));
    } catch (e) { falla(lista)(e); }
  }
  filtro.addEventListener("change", cargar);
  cada(3000, cargar);
}

// ---------------------------------------------------------------- llaves

function vistaLlaves(raiz) {
  const lista = h("div", { class: "tarjeta" });
  const nombre = h("input", { placeholder: "Por ejemplo: ERP de bodega", maxlength: "60" });
  const rol = h("select", null, h("option", { value: "api" }, "Sistema (API)"), h("option", { value: "worker" }, "Acusa Worker"));
  const forma = h("form", { class: "tarjeta fila-acciones", onsubmit: async (e) => {
    e.preventDefault();
    try { const r = await api("/api/portal/llaves", { method: "POST", body: JSON.stringify({ rol: rol.value, nombre: nombre.value }) }); nombre.value = ""; mostrarLlave(r.llave, rol.value); cargar(); }
    catch (err) { aviso(err.message); }
  } }, h("label", { class: "campo" }, h("span", null, "Para qué es"), nombre), h("label", { class: "campo", style: "flex:0 1 200px" }, h("span", null, "Tipo"), rol),
    h("button", { class: "boton", type: "submit", style: "margin-bottom:12px" }, "Crear llave"));
  raiz.append(encabezado("Llaves de API", "Una llave identifica a un sistema o a un worker. Se muestra completa una sola vez; después solo queda su inicio."), forma, lista);
  async function cargar() {
    try {
      const ls = await api("/api/portal/llaves");
      lista.replaceChildren(ls.length
        ? tabla(["Nombre", "Tipo", "Llave", "Creada", "Último uso", "Estado", ""], ls.map((l) => h("tr", null, h("td", null, l.nombre || "—"), h("td", null, l.rol === "worker" ? "Worker" : "Sistema"),
            h("td", { class: "mono" }, (l.prefijo || "") + "…"), h("td", null, fechaHora(l.creada)), h("td", null, l.ultimoUso ? hace(l.ultimoUso) : "Sin uso"),
            h("td", null, h("span", { class: "ficha " + (l.activa ? "ok" : "") }, l.activa ? "Activa" : "Revocada")),
            h("td", null, l.activa ? h("button", { class: "boton suave chico", onclick: async () => {
              if (!confirm("¿Revocar esta llave? Quien la use dejará de tener acceso de inmediato.")) return;
              try { await api("/api/portal/llaves/" + l.id, { method: "DELETE" }); aviso("Llave revocada"); cargar(); } catch (err) { aviso(err.message); }
            } }, "Revocar") : null))))
        : h("div", { class: "vacio" }, h("b", null, "No hay llaves"), "Cree una para conectar su primer sistema o worker."));
    } catch (e) { falla(lista)(e); }
  }
  cargar();
}

function mostrarLlave(llave, rol) {
  const d = h("dialog", null,
    h("h2", null, "Su llave nueva"),
    h("p", { class: "sub" }, rol === "worker" ? "Péguela en Acusa Worker." : "Úsela en la cabecera x-api-key de su sistema. Cópiela ahora: no volverá a mostrarse completa."),
    h("div", { class: "copiable" }, h("code", null, llave), h("button", { class: "boton chico", onclick: () => copiar(llave) }, "Copiar")),
    h("div", { class: "fila-acciones", style: "justify-content:flex-end" }, h("button", { class: "boton suave", onclick: () => { d.close(); d.remove(); } }, "Ya la guardé")));
  document.body.append(d);
  d.showModal();
}

// ---------------------------------------------------------------- desarrolladores

function curlDe(base, metodo, ruta, op) {
  const url = base + ruta.replace(/\{[^}]+\}/g, (x) => x.replace(/[{}]/g, "").toUpperCase());
  const cuerpo = op.requestBody && op.requestBody.content && op.requestBody.content["application/json"] && op.requestBody.content["application/json"].example;
  const lineas = [`curl -X ${metodo.toUpperCase()} "${url}"`, `  -H "Authorization: Bearer $LLAVE"`];
  if (cuerpo) { lineas.push(`  -H "Content-Type: application/json"`); lineas.push(`  -d '${JSON.stringify(cuerpo)}'`); }
  return lineas.join(" \\\n");
}

function vistaDevs(raiz) {
  const base = location.origin;
  const catalogo = h("div");
  const bloque = (titulo, texto) => h("div", { style: "margin-top:10px" }, h("div", { class: "etiqueta" }, titulo), h("div", { class: "copiable" }, h("code", null, texto), h("button", { class: "boton suave chico", onclick: () => copiar(texto) }, "Copiar")));
  raiz.append(
    encabezado("Desarrolladores", "Acusa Central es una API REST para crear, timbrar y cancelar facturas electrónicas sobre CONTPAQi Comercial. Cada proceso es un recurso: clientes, productos y documentos, con su timbrado, sus archivos y su cancelación."),
    h("div", { class: "tarjeta" }, h("h2", null, "Cómo se usa"),
      h("p", { class: "sub" }, "Documentación interactiva con probador: ", h("a", { href: "/api-docs/", target: "_blank" }, "/api-docs"), " · Definición OpenAPI: ", h("a", { href: "/api-docs/openapi.yaml", target: "_blank" }, "openapi.yaml"), "."),
      bloque("Dirección base", base),
      bloque("Autenticación", "Authorization: Bearer <llave de API ak_…>"),
      bloque("Flujo de una factura", "POST /clientes  →  POST /productos  →  POST /documentos  →  POST /documentos/{concepto}/{serie}/{folio}/timbrado  →  GET …/xml   (todo bajo /api/empresas/{empresa})"),
      bloque("Respuesta de timbrado", `{ "uuid": "…", "estado": "timbrada", "xml": "<cfdi:Comprobante …>" }`),
      h("p", { class: "nota", style: "margin-top:10px" }, "Cada petición espera al worker hasta 60 s; si no responde, la respuesta es 202 con la tarea. Los errores de validación responden 422 con { \"errors\": [ … ] }. En la sección Probar puede correr la cadena completa con un botón.")),
    catalogo);

  fetch("/api-docs/openapi.json").then((r) => r.json()).then((spec) => {
    const grupos = new Map();
    for (const [ruta, item] of Object.entries(spec.paths)) {
      for (const [metodo, op] of Object.entries(item)) {
        if (!["get", "post", "put", "patch", "delete"].includes(metodo)) continue;
        const tag = (op.tags && op.tags[0]) || "Otras";
        if (!grupos.has(tag)) grupos.set(tag, []);
        grupos.get(tag).push({ metodo, ruta, op });
      }
    }

    catalogo.replaceChildren(...[...grupos.entries()].map(([tag, rutas]) => h("div", { class: "tarjeta" }, h("h2", null, tag),
      tabla(["Método", "Ruta", "Qué hace", "Ejemplo"], rutas.map(({ metodo, ruta, op }) => {
        const c = curlDe(base, metodo, ruta, op);
        return h("tr", null, h("td", { class: "mono" }, metodo.toUpperCase()), h("td", { class: "mono" }, ruta), h("td", null, op.summary || ""),
          h("td", null, h("details", null, h("summary", null, "Ver"), h("pre", null, c), h("button", { class: "boton suave chico", onclick: () => copiar(c) }, "Copiar"))));
      })))));
  }).catch(falla(catalogo));
}

// ---------------------------------------------------------------- probar (cadena de peticiones REST)

function vistaProbar(raiz) {
  const empresaSel = h("select", { "aria-label": "Empresa de pruebas" });
  const cancelar = h("input", { type: "checkbox", id: "chk-cancelar" });
  const salida = h("div", null, h("div", { class: "tarjeta" }, h("div", { class: "vacio" }, h("b", null, "Todavía no se ha corrido"), "Elija la empresa de pruebas y pulse «Correr la cadena».")));
  const boton = h("button", { class: "boton", id: "boton-cadena" }, "Correr la cadena");
  raiz.append(
    encabezado("Probar", "Corre en cadena las peticiones de una factura de $1 (cliente, producto, documento, timbrado, XML y cancelación) y muestra cada respuesta, como una colección de Postman. Necesita un worker en línea y una empresa de pruebas; el timbrado gasta un timbre del PAC."),
    h("div", { class: "tarjeta" },
      h("div", { class: "fila-acciones" }, h("label", { class: "campo" }, h("span", null, "Empresa de pruebas"), empresaSel),
        h("label", { class: "campo", for: "chk-cancelar", style: "flex-direction:row;gap:8px;align-items:center" }, cancelar, h("span", null, "Cancelar la factura al final (motivo 02)")), boton)),
    salida);
  api("/api/portal/empresas").then((es) => {
    empresaSel.replaceChildren(...es.map((e) => h("option", { value: e }, e)));
    const prueba = es.find((e) => /PRUEBA/i.test(e)); if (prueba) empresaSel.value = prueba;
  }).catch(() => {});

  async function llamar(metodo, ruta, cuerpo) {
    const r = await fetch(ruta, { method: metodo, headers: { "Content-Type": "application/json", Authorization: "Bearer " + sesion.token }, body: cuerpo ? JSON.stringify(cuerpo) : undefined });
    const texto = await r.text();
    let datos = texto; try { datos = JSON.parse(texto); } catch { /* respuesta de texto, como el XML */ }
    return { estado: r.status, datos };
  }

  boton.addEventListener("click", async () => {
    const empresa = empresaSel.value;
    if (!empresa) { aviso("Registre una empresa en Configuración."); return; }
    if (!/PRUEBA/i.test(empresa) && !confirm(`La empresa «${empresa}» no parece de pruebas. Timbrar gasta timbres reales. ¿Continuar?`)) return;
    boton.disabled = true;
    salida.replaceChildren();
    const ctx = {};
    const hoy = new Date().toISOString().slice(0, 10);
    const sufijo = String(Date.now()).slice(-6);
    const E = `/api/empresas/${encodeURIComponent(empresa)}`;
    const D = () => `${E}/documentos/${ctx.concepto}/${ctx.serie || "-"}/${ctx.folio}`;
    const pasos = [
      ["Cliente", async () => { const r = await llamar("POST", `${E}/clientes`, { codigo: "PRUEBACL" + sufijo, razonSocial: "PRUEBA CLIENTE SA", rfc: "XAXX010101000", tipoCliente: 1, limiteCredito: 1000 }); ctx.cliente = "PRUEBACL" + sufijo; return { ...r, ruta: `POST ${E}/clientes` }; }],
      ["Producto de $1", async () => { const r = await llamar("POST", `${E}/productos`, { codigo: "PRUEBAPR" + sufijo, nombre: "PRUEBA SERVICIO UN PESO", unidadBase: "Pieza", precio1: 1, claveSat: "81112200", tipoProducto: 3 }); ctx.producto = "PRUEBAPR" + sufijo; return { ...r, ruta: `POST ${E}/productos` }; }],
      ["Documento (sin timbrar)", async () => {
        const r = await llamar("POST", `${E}/documentos`, { concepto: "5", serie: "PRUEBA", clienteCodigo: ctx.cliente, referencia: "PRUEBAFAC" + sufijo, fecha: hoy, formaPago: "03", usoCfdi: "S01", regimenFiscal: "616", movimientos: [{ productoCodigo: ctx.producto, unidades: 1, precio: 1, ivaPorcentaje: 16, retencionIvaPorcentaje: 0, iepsPorcentaje: 0 }] });
        const d = r.datos && r.datos.resultado; if (d) { ctx.concepto = d.concepto; ctx.serie = d.serie; ctx.folio = d.folio; }
        return { ...r, ruta: `POST ${E}/documentos` };
      }],
      ["Timbrado (devuelve el XML en texto plano)", async () => { const r = await llamar("POST", D() + "/timbrado"); return { ...r, ruta: "POST " + D() + "/timbrado" }; }],
      ["XML del documento", async () => { const r = await llamar("GET", D() + "/xml"); return { ...r, ruta: "GET " + D() + "/xml" }; }],
    ];
    if (cancelar.checked) pasos.push(["Cancelación (motivo 02)", async () => { const r = await llamar("POST", D() + "/cancelacion", { motivo: "02" }); return { ...r, ruta: "POST " + D() + "/cancelacion" }; }]);

    let fallo = false;
    for (let i = 0; i < pasos.length && !fallo; i++) {
      const [titulo, correr] = pasos[i];
      const fila = h("div", { class: "tarjeta" }, h("h2", null, `${i + 1}. ${titulo}`), h("p", { class: "sub" }, "Corriendo…"));
      salida.append(fila);
      try {
        const r = await correr();
        const bien = r.estado >= 200 && r.estado < 300;
        let texto = typeof r.datos === "string" ? r.datos : json(r.datos);
        if (texto.length > 6000) texto = texto.slice(0, 6000) + "\n… (recortado)";
        fila.replaceChildren(h("h2", null, `${i + 1}. ${titulo}`), h("p", { class: "mono" }, r.ruta, " → ", h("b", { class: bien ? "estado-Completada" : "estado-Fallida" }, r.estado)), h("pre", null, texto));
        if (!bien) fallo = true;
        else if (r.datos && r.datos.xml) fila.append(h("p", { class: "nota" }, "El campo xml llegó en texto plano (" + r.datos.xml.length + " caracteres)."));
      } catch (e) { fila.replaceChildren(h("h2", null, `${i + 1}. ${titulo}`), h("p", { class: "estado-Fallida" }, "Error: " + e.message)); fallo = true; }
    }
    salida.append(h("div", { class: "tarjeta" }, h("b", null, fallo ? "La cadena se detuvo en el paso que falló." : "Cadena completa.")));
    boton.disabled = false;
  });
}

// ---------------------------------------------------------------- configuración

function vistaConfig(raiz) {
  const u = sesion.usuario || {};
  const actual = h("input", { type: "password", autocomplete: "current-password", required: true });
  const nueva = h("input", { type: "password", autocomplete: "new-password", required: true, minlength: "10" });
  const clave = h("form", { onsubmit: async (e) => {
    e.preventDefault();
    try { await api("/api/portal/clave", { method: "POST", body: JSON.stringify({ actual: actual.value, nueva: nueva.value }) }); actual.value = ""; nueva.value = ""; aviso("Contraseña cambiada"); }
    catch (err) { aviso(err.message); }
  } }, h("label", { class: "campo" }, h("span", null, "Contraseña actual"), actual),
    h("label", { class: "campo" }, h("span", null, "Contraseña nueva (mínimo 10 caracteres)"), nueva), h("button", { class: "boton", type: "submit" }, "Cambiar contraseña"));

  const lista = h("div");
  const nombre = h("input", { placeholder: "adEMPRESA_DE_PRUEBA", maxlength: "80", required: true });
  const alta = h("form", { class: "fila-acciones", onsubmit: async (e) => {
    e.preventDefault();
    try { await api("/api/portal/empresas", { method: "POST", body: JSON.stringify({ nombre: nombre.value }) }); nombre.value = ""; aviso("Empresa registrada"); cargar(); }
    catch (err) { aviso(err.message); }
  } }, h("label", { class: "campo" }, h("span", null, "Nombre de la empresa en CONTPAQi"), nombre), h("button", { class: "boton", type: "submit", style: "margin-bottom:12px" }, "Registrar"));
  async function cargar() {
    try {
      const es = await api("/api/portal/empresas");
      lista.replaceChildren(es.length
        ? tabla(["Empresa", ""], es.map((n) => h("tr", null, h("td", { class: "mono" }, n), h("td", { style: "text-align:right" },
            h("button", { class: "boton suave chico", onclick: async () => {
              if (!confirm(`¿Quitar ${n}? Sus workers dejarán de recibir órdenes de esa empresa.`)) return;
              try { await api("/api/portal/empresas/" + encodeURIComponent(n), { method: "DELETE" }); cargar(); } catch (err) { aviso(err.message); }
            } }, "Quitar")))))
        : h("div", { class: "vacio" }, h("b", null, "No hay empresas registradas"), "Registre la primera para poder mandar órdenes."));
    } catch (e) { falla(lista)(e); }
  }

  raiz.append(encabezado("Configuración", `Su cuenta (${u.email || "—"}) y las empresas que atiende ${PRODUCTO}.`),
    h("div", { class: "tarjeta" }, h("h2", null, "Empresas"), h("p", { class: "sub" }, "Un worker solo atiende empresas registradas aquí. El nombre es el de la carpeta de la empresa en CONTPAQi."), alta, lista),
    h("div", { class: "tarjeta", style: "max-width:460px" }, h("h2", null, "Cambiar contraseña"), clave));
  cargar();
}

// ---------------------------------------------------------------- inicio

(async function inicio() {
  if (!sesion.token) { mostrarEntrada(); return; }
  try { const yo = await api("/api/portal/yo"); sesion.guardar(sesion.token, { nombre: yo.nombre, email: yo.email, cliente: yo.cliente }); arrancar(); }
  catch { if (!sesion.token) return; mostrarEntrada(); }
})();
