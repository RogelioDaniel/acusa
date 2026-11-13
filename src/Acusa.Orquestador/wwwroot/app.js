"use strict";

/* Acusa Central 1.0.0: portal del orquestador. Sin dependencias ni paso de compilación.
   Todo el texto que viene del servidor entra con textContent (nunca como HTML). */

const VERSION = "1.0.0";
const PRODUCTO = "Acusa Central";

// ---------------------------------------------------------------- utilidades

const $ = (s, r = document) => r.querySelector(s);

// Tema claro u oscuro: el elegido se recuerda; sin elección, el del sistema.
const temaActual = () => document.documentElement.dataset.tema || "claro";
function aplicarTema(t) {
  document.documentElement.dataset.tema = t;
  try { localStorage.setItem("acusa.tema", t); } catch { /* sin almacenamiento */ }
}
const botonTema = () => {
  const b = h("button", { class: "boton-tema", type: "button", "aria-label": "Cambiar entre modo claro y oscuro" });
  const texto = () => { b.textContent = temaActual() === "oscuro" ? "Modo claro" : "Modo oscuro"; };
  b.addEventListener("click", () => { aplicarTema(temaActual() === "oscuro" ? "claro" : "oscuro"); texto(); });
  texto();
  return b;
};

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
    marca("Central"), h("h1", null, "Entrar"), h("p", null, "Use la cuenta que le dio su administrador."), forma, h("div", { style: "margin-top:14px;text-align:right" }, botonTema()))));
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
    h("div", { class: "usuario" }, botonTema(), h("span", null, u.email || u.nombre || ""),
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

// ---------------------------------------------------------------- consola de peticiones (Desarrolladores y Probar)

/** Datos que se comparten entre peticiones: la empresa elegida, el último documento creado y el token propio, si lo hay. */
const memoria = (() => {
  let m = {};
  try { m = JSON.parse(sessionStorage.getItem("acusa.memoria") || "{}"); } catch { /* sin almacenamiento */ }
  return {
    get: (k) => m[k],
    set(k, v) { m[k] = v; try { sessionStorage.setItem("acusa.memoria", JSON.stringify(m)); } catch { /* sin almacenamiento */ } },
    borrar(k) { delete m[k]; try { sessionStorage.setItem("acusa.memoria", JSON.stringify(m)); } catch { /* sin almacenamiento */ } },
  };
})();

const tokenActual = () => memoria.get("token") || sesion.token;
let _spec = null;
const cargarSpec = () => (_spec ||= fetch("/api-docs/openapi.json").then((r) => { if (!r.ok) throw new Error("No se pudo cargar la definición de la API."); return r.json(); }));

/** Operaciones de la definición, en orden, con su etiqueta. */
function operaciones(spec) {
  const r = [];
  for (const [ruta, item] of Object.entries(spec.paths)) {
    for (const [metodo, op] of Object.entries(item)) {
      if (["get", "post", "put", "patch", "delete"].includes(metodo)) r.push({ metodo: metodo.toUpperCase(), ruta, op, tag: (op.tags && op.tags[0]) || "Otras" });
    }
  }
  return r;
}

const ejemploDe = (op) => op.requestBody && op.requestBody.content && op.requestBody.content["application/json"] && op.requestBody.content["application/json"].example;
const parametrosRuta = (ruta) => [...ruta.matchAll(/\{([^}]+)\}/g)].map((x) => x[1]);

/** En una empresa de pruebas, los códigos de los ejemplos llevan el prefijo PRUEBA que exige el worker. */
function paraEmpresa(texto) {
  const e = memoria.get("empresa") || "";
  if (!/PRUEBA/i.test(e)) return texto;
  return texto.replaceAll("CLI0001", "PRUEBACL01").replaceAll("SERV0001", "PRUEBAPR01").replaceAll("FAC-0001", "PRUEBAFAC01").replace(/"serie": "A"/g, '"serie": "PRUEBA"').replace(/"serie":"A"/g, '"serie":"PRUEBA"');
}

async function enviar(metodo, url, cuerpo, extra = {}) {
  const cabeceras = { ...(extra.cabeceras || {}) };
  if (url.startsWith("/")) cabeceras.Authorization = "Bearer " + tokenActual();
  if (cuerpo != null && cuerpo !== "" && !cabeceras["Content-Type"]) cabeceras["Content-Type"] = "application/json";
  const t0 = performance.now();
  const r = await fetch(url, { method: metodo, headers: cabeceras, body: cuerpo ? cuerpo : undefined });
  const tipo = r.headers.get("content-type") || "";
  let datos, blob = null;
  if (tipo.includes("pdf")) { blob = await r.blob(); datos = `Archivo PDF (${blob.size} bytes)`; }
  else { datos = await r.text(); if (tipo.includes("json")) { try { datos = JSON.stringify(JSON.parse(datos), null, 2); } catch { /* texto */ } } }
  return { estado: r.status, ms: Math.round(performance.now() - t0), tipo, datos, blob, ubicacion: r.headers.get("location") };
}

/** Guarda lo que las respuestas dejan para la siguiente petición (concepto, serie y folio del documento). */
function recordar(metodo, ruta, cuerpoTexto, res) {
  if (res.estado < 200 || res.estado >= 300) return;
  try {
    const j = JSON.parse(res.datos);
    const d = (j && j.resultado) || j || {};
    const doc = d.documento || d;
    if (doc.folio != null && doc.concepto != null) { memoria.set("concepto", String(doc.concepto)); memoria.set("serie", doc.serie ? String(doc.serie) : "-"); memoria.set("folio", String(doc.folio)); }
  } catch { /* respuesta de texto */ }
  if (metodo === "POST" && /\/(clientes|productos)$/.test(ruta)) {
    try { const c = JSON.parse(cuerpoTexto); if (c.codigo) memoria.set(/\/clientes$/.test(ruta) ? "codigoCliente" : "codigoProducto", c.codigo); } catch { /* sin cuerpo */ }
  }
}

function vistaResultado(res) {
  const bien = res.estado >= 200 && res.estado < 300;
  const cuerpo = res.blob
    ? h("div", null, h("p", { class: "nota" }, res.datos), h("button", { class: "boton suave chico", onclick: () => { const a = h("a", { href: URL.createObjectURL(res.blob), download: "documento.pdf" }); document.body.append(a); a.click(); a.remove(); } }, "Descargar PDF"))
    : h("pre", null, res.datos.length > 20000 ? res.datos.slice(0, 20000) + "\n… (recortado)" : res.datos || "(sin contenido)");
  return h("div", { class: "resultado" },
    h("p", { class: "mono" }, h("b", { class: bien ? "estado-Completada" : res.estado === 202 ? "estado-EnProceso" : "estado-Fallida" }, res.estado), `  ${res.ms} ms`, res.ubicacion ? `  ·  seguir en ${res.ubicacion}` : ""),
    cuerpo);
}

/** Una petición lista para lanzar: parámetros de ruta, cuerpo editable y resultado. */
function consola({ metodo, ruta, op }) {
  const campos = {};
  const params = parametrosRuta(ruta);
  const filaParams = params.length ? h("div", { class: "dos" }, params.map((p) => {
    const recordado = p === "codigo" ? memoria.get(ruta.includes("/clientes") ? "codigoCliente" : "codigoProducto") : memoria.get(p);
    const i = h("input", { value: recordado || "", "aria-label": p, spellcheck: "false" });
    campos[p] = i;
    return h("label", { class: "campo" }, h("span", null, p), i);
  })) : null;
  const ej = ejemploDe(op);
  const area = ej ? h("textarea", { spellcheck: "false", "aria-label": "Cuerpo de la petición" }) : null;
  if (area) area.value = paraEmpresa(JSON.stringify(ej, null, 2));
  const llave = metodo === "POST" ? h("input", { placeholder: "Idempotency-Key (opcional)", "aria-label": "Idempotency-Key" }) : null;
  const salida = h("div");
  const boton = h("button", { class: "boton" }, "Lanzar");
  const destino = () => ruta.replace(/\{([^}]+)\}/g, (_, p) => encodeURIComponent((campos[p] && campos[p].value.trim()) || "{" + p + "}"));

  boton.addEventListener("click", async () => {
    const url = destino();
    if (/\{[^}]+\}/.test(url)) { aviso("Complete los parámetros de la ruta."); return; }
    if (area) { try { JSON.parse(area.value); } catch { aviso("El cuerpo no es un JSON válido."); return; } }
    boton.disabled = true; salida.replaceChildren(h("p", { class: "nota" }, "Enviando…"));
    try {
      const cab = llave && llave.value.trim() ? { "Idempotency-Key": llave.value.trim() } : {};
      const res = await enviar(metodo, url, area ? area.value : null, { cabeceras: cab });
      recordar(metodo, url, area ? area.value : "", res);
      salida.replaceChildren(vistaResultado(res));
    } catch (e) { salida.replaceChildren(h("p", { class: "err" }, "No se pudo enviar: " + e.message)); }
    boton.disabled = false;
  });

  return h("div", { class: "consola" },
    h("p", { class: "mono" }, h("b", null, metodo), "  ", ruta),
    filaParams, area ? h("label", { class: "campo" }, h("span", null, "Cuerpo (JSON)"), area) : null,
    h("div", { class: "fila-acciones" }, boton, llave ? h("label", { class: "campo", style: "margin:0;flex:0 1 280px" }, llave) : null),
    salida);
}

/** Tarjeta de sesión: usa la sesión del portal o cambia una llave de API por un token. */
function tarjetaSesion() {
  const estado = h("p", { class: "sub" });
  const llave = h("input", { type: "password", placeholder: "Llave de API (ak_…)", autocomplete: "off", "aria-label": "Llave de API" });
  const mostrar = () => {
    estado.textContent = memoria.get("token")
      ? "Usando el token obtenido con su llave de API (dura una hora)."
      : "Usando su sesión del portal. Las peticiones se firman solas.";
  };
  const obtener = h("button", { class: "boton" }, "Obtener token");
  obtener.addEventListener("click", async () => {
    obtener.disabled = true;
    try {
      const r = await fetch("/api/auth/token", { method: "POST", headers: { "x-api-key": llave.value.trim() } });
      const j = await r.json().catch(() => ({}));
      if (!r.ok) throw new Error("La llave no es válida o fue revocada.");
      memoria.set("token", j.token); llave.value = ""; mostrar(); aviso("Token listo");
    } catch (e) { aviso(e.message); }
    obtener.disabled = false;
  });
  const sesionPortal = h("button", { class: "boton suave", onclick: () => { memoria.borrar("token"); mostrar(); } }, "Usar mi sesión del portal");
  mostrar();
  return h("div", { class: "tarjeta" }, h("h2", null, "Sesión"), estado,
    h("div", { class: "fila-acciones" }, h("label", { class: "campo", style: "margin:0" }, llave), obtener, sesionPortal));
}

// ---------------------------------------------------------------- desarrolladores

function curlDe(base, metodo, ruta, op) {
  const url = base + ruta.replace(/\{([^}]+)\}/g, (_, x) => x.toUpperCase());
  const cuerpo = ejemploDe(op);
  const lineas = [`curl -X ${metodo} "${url}"`, `  -H "Authorization: Bearer $LLAVE"`];
  if (cuerpo) { lineas.push(`  -H "Content-Type: application/json"`); lineas.push(`  -d '${JSON.stringify(cuerpo)}'`); }
  return lineas.join(" \\\n");
}

function vistaDevs(raiz) {
  const base = location.origin;
  const catalogo = h("div");
  const bloque = (titulo, texto) => h("div", { style: "margin-top:10px" }, h("div", { class: "etiqueta" }, titulo), h("div", { class: "copiable" }, h("code", null, texto), h("button", { class: "boton suave chico", onclick: () => copiar(texto) }, "Copiar")));
  raiz.append(
    encabezado("Desarrolladores", "API REST para crear, timbrar y cancelar facturas electrónicas sobre CONTPAQi Comercial."),
    h("div", { class: "tarjeta" }, h("h2", null, "Cómo se usa"),
      h("p", { class: "sub" }, "Documentación interactiva: ", h("a", { href: "/api-docs/", target: "_blank" }, "/api-docs"), " · Definición OpenAPI: ", h("a", { href: "/api-docs/openapi.yaml", target: "_blank" }, "openapi.yaml"), "."),
      bloque("Dirección base", base),
      bloque("Autenticación", "Authorization: Bearer <llave de API ak_…>"),
      bloque("Flujo de una factura", "POST /clientes  →  POST /productos  →  POST /documentos  →  POST /documentos/{concepto}/{serie}/{folio}/timbrado  →  GET …/xml   (todo bajo /api/empresas/{empresa})"),
      bloque("Respuesta de timbrado", `{ "uuid": "…", "estado": "timbrada", "xml": "<cfdi:Comprobante …>" }`),
      h("p", { class: "nota", style: "margin-top:10px" }, "Cada petición espera al worker hasta 60 s; si no responde, la respuesta es 202 con la tarea. Los errores de validación responden 422 con { \"errors\": [ … ] }.")),
    tarjetaSesion(),
    catalogo);

  cargarSpec().then((spec) => {
    const grupos = new Map();
    for (const o of operaciones(spec)) { if (!grupos.has(o.tag)) grupos.set(o.tag, []); grupos.get(o.tag).push(o); }
    catalogo.replaceChildren(...[...grupos.entries()].map(([tag, ops]) => h("div", { class: "tarjeta" }, h("h2", null, tag),
      ...ops.map(({ metodo, ruta, op }) => {
        const c = curlDe(base, metodo, ruta, op);
        const panel = h("div", { hidden: true, style: "margin:8px 0 12px" });
        const verCurl = h("details", null, h("summary", null, "curl"), h("pre", null, c), h("button", { class: "boton suave chico", onclick: () => copiar(c) }, "Copiar"));
        const lanzar = h("button", { class: "boton chico", onclick: () => { if (panel.hidden && !panel.firstChild) panel.append(consola({ metodo, ruta, op })); panel.hidden = !panel.hidden; lanzar.textContent = panel.hidden ? "Lanzar" : "Cerrar"; } }, "Lanzar");
        return h("div", { class: "factura" }, h("div", { class: "fila-acciones", style: "align-items:center" },
          h("span", { class: "mono", style: "min-width:64px;font-weight:700" }, metodo), h("span", { class: "mono", style: "flex:1 1 320px" }, ruta), h("span", { style: "flex:1 1 220px" }, op.summary || ""), lanzar),
          verCurl, panel);
      }))));
  }).catch(falla(catalogo));
}

// ---------------------------------------------------------------- probar

const SECCIONES = [
  { id: "clientes", titulo: "Clientes", descripcion: "Alta, consulta y actualización de clientes y sus direcciones.", filtro: (o) => o.tag === "Clientes" },
  { id: "productos", titulo: "Productos", descripcion: "Alta, consulta y actualización de productos y servicios.", filtro: (o) => o.tag === "Productos" },
  { id: "facturas", titulo: "Facturas", descripcion: "Creación y consulta de documentos, individuales o por lote.", filtro: (o) => o.tag === "Documentos" && !/timbrado|xml|pdf|cancelacion/.test(o.ruta) },
  { id: "timbrado", titulo: "Timbrado y archivos", descripcion: "Timbrado con XML en texto plano, XML y PDF.", filtro: (o) => o.tag === "Documentos" && /timbrado|xml|pdf/.test(o.ruta) },
  { id: "cancelacion", titulo: "Cancelación", descripcion: "Cancelación de documentos timbrados, individual o por lote.", filtro: (o) => o.tag === "Documentos" && /cancelacion/.test(o.ruta) },
  { id: "flujo", titulo: "Flujo completo", descripcion: "Cliente, producto, factura, timbrado, XML y cancelación en una sola corrida." },
  { id: "postman", titulo: "Colección de Postman", descripcion: "Cargue una colección y ejecute sus peticiones aquí." },
];

function vistaProbar(raiz) {
  const empresaSel = h("select", { "aria-label": "Empresa" });
  const cuerpo = h("div");
  const pestanas = h("div", { class: "pestanas", role: "tablist" });
  let actual = SECCIONES[0].id;
  raiz.append(
    encabezado("Probar", "Ejecute las peticiones de la API sobre una empresa de CONTPAQi.", h("label", { class: "campo", style: "margin:0;min-width:240px" }, h("span", null, "Empresa"), empresaSel)),
    h("div", { class: "tarjeta", style: "padding:8px 10px" }, pestanas), cuerpo);

  const pintarPestanas = () => pestanas.replaceChildren(...SECCIONES.map((s) => h("button", { role: "tab", "aria-selected": String(s.id === actual), onclick: () => { actual = s.id; pintarPestanas(); mostrar(); } }, s.titulo)));

  async function mostrar() {
    const s = SECCIONES.find((x) => x.id === actual);
    cuerpo.replaceChildren(h("p", { class: "sub" }, s.descripcion));
    if (s.id === "flujo") { cuerpo.append(flujoCompleto()); return; }
    if (s.id === "postman") { cuerpo.append(importadorPostman()); return; }
    try {
      const spec = await cargarSpec();
      cuerpo.append(tarjetaSesion(), ...operaciones(spec).filter(s.filtro).map((o) => h("div", { class: "tarjeta" }, h("h2", null, o.op.summary || o.ruta), o.op.description ? h("p", { class: "sub" }, o.op.description) : null, consola(o))));
    } catch (e) { falla(cuerpo)(e); }
  }

  api("/api/portal/empresas").then((es) => {
    empresaSel.replaceChildren(...es.map((e) => h("option", { value: e }, e)));
    const guardada = memoria.get("empresa");
    empresaSel.value = es.includes(guardada) ? guardada : (es.find((e) => /PRUEBA/i.test(e)) || es[0] || "");
    memoria.set("empresa", empresaSel.value);
    empresaSel.addEventListener("change", () => { memoria.set("empresa", empresaSel.value); mostrar(); });
    pintarPestanas(); mostrar();
  }).catch(falla(cuerpo));
}

/** Cliente, producto, documento, timbrado, XML y cancelación, con la respuesta de cada paso. */
function flujoCompleto() {
  const cancelar = h("input", { type: "checkbox", id: "chk-cancelar" });
  const salida = h("div");
  const boton = h("button", { class: "boton" }, "Correr el flujo");
  boton.addEventListener("click", async () => {
    const empresa = memoria.get("empresa");
    if (!empresa) { aviso("Registre una empresa en Configuración."); return; }
    if (!/PRUEBA/i.test(empresa) && !confirm(`La empresa «${empresa}» no parece de pruebas. Timbrar gasta timbres reales. ¿Continuar?`)) return;
    boton.disabled = true; salida.replaceChildren();
    const ctx = {};
    const hoy = new Date().toISOString().slice(0, 10);
    const sufijo = String(Date.now()).slice(-6);
    const E = `/api/empresas/${encodeURIComponent(empresa)}`;
    const D = () => `${E}/documentos/${ctx.concepto}/${ctx.serie || "-"}/${ctx.folio}`;
    const llamar = async (m, ruta, c) => { const r = await enviar(m, ruta, c ? JSON.stringify(c) : null); recordar(m, ruta, c ? JSON.stringify(c) : "", r); return { ...r, ruta: `${m} ${ruta}` }; };
    const pasos = [
      ["Cliente", async () => { ctx.cliente = "PRUEBACL" + sufijo; return llamar("POST", `${E}/clientes`, { codigo: ctx.cliente, razonSocial: "PRUEBA CLIENTE SA", rfc: "XAXX010101000", tipoCliente: 1, limiteCredito: 1000 }); }],
      ["Producto de $1", async () => { ctx.producto = "PRUEBAPR" + sufijo; return llamar("POST", `${E}/productos`, { codigo: ctx.producto, nombre: "PRUEBA SERVICIO UN PESO", unidadBase: "Pieza", precio1: 1, claveSat: "81112200", tipoProducto: 3 }); }],
      ["Documento", async () => {
        const r = await llamar("POST", `${E}/documentos`, { concepto: "5", serie: "PRUEBA", clienteCodigo: ctx.cliente, referencia: "PRUEBAFAC" + sufijo, fecha: hoy, formaPago: "03", usoCfdi: "S01", regimenFiscal: "616", movimientos: [{ productoCodigo: ctx.producto, unidades: 1, precio: 1, ivaPorcentaje: 16, retencionIvaPorcentaje: 0, iepsPorcentaje: 0 }] });
        try { const d = JSON.parse(r.datos).resultado; ctx.concepto = d.concepto; ctx.serie = d.serie; ctx.folio = d.folio; } catch { /* el siguiente paso no se corre */ }
        return r;
      }],
      ["Timbrado (XML en texto plano)", () => llamar("POST", D() + "/timbrado")],
      ["XML del documento", () => llamar("GET", D() + "/xml")],
    ];
    if (cancelar.checked) pasos.push(["Cancelación (motivo 02)", () => llamar("POST", D() + "/cancelacion", { motivo: "02" })]);

    let fallo = false;
    for (let i = 0; i < pasos.length && !fallo; i++) {
      const [titulo, correr] = pasos[i];
      const fila = h("div", { class: "tarjeta" }, h("h2", null, `${i + 1}. ${titulo}`), h("p", { class: "sub" }, "Enviando…"));
      salida.append(fila);
      try {
        const r = await correr();
        fila.replaceChildren(h("h2", null, `${i + 1}. ${titulo}`), h("p", { class: "mono" }, r.ruta), vistaResultado(r));
        if (r.estado < 200 || r.estado >= 300) fallo = true;
      } catch (e) { fila.replaceChildren(h("h2", null, `${i + 1}. ${titulo}`), h("p", { class: "err" }, "No se pudo enviar: " + e.message)); fallo = true; }
    }
    salida.append(h("div", { class: "tarjeta" }, h("b", null, fallo ? "El flujo se detuvo en el paso que falló." : "Flujo completo.")));
    boton.disabled = false;
  });
  return h("div", null,
    h("div", { class: "tarjeta" }, h("div", { class: "fila-acciones", style: "align-items:center" }, boton,
      h("label", { for: "chk-cancelar", style: "display:flex;gap:8px;align-items:center" }, cancelar, "Cancelar la factura al final (motivo 02)")),
      h("p", { class: "nota" }, "Factura de $1 más IVA. El timbrado gasta un timbre del PAC.")),
    salida);
}

/** Carga una colección de Postman (v2.x) y ejecuta sus peticiones contra este servidor. Los scripts de la colección no se ejecutan. */
function importadorPostman() {
  const zona = h("div");
  const archivo = h("input", { type: "file", accept: ".json,application/json", "aria-label": "Colección de Postman" });
  archivo.addEventListener("change", async () => {
    const f = archivo.files && archivo.files[0];
    if (!f) return;
    try { pintar(JSON.parse(await f.text())); } catch { zona.replaceChildren(h("p", { class: "err" }, "El archivo no es una colección de Postman válida.")); }
  });

  function aplanar(items, ruta = []) {
    return (items || []).flatMap((it) => it.item ? aplanar(it.item, [...ruta, it.name]) : [{ ...it, carpeta: ruta.join(" / ") }]);
  }

  function pintar(col) {
    const vars = {};
    for (const v of col.variable || []) vars[v.key] = v.value ?? "";
    vars.base_url ??= location.origin;
    const entradas = {};
    const tarjetaVars = h("div", { class: "tarjeta" }, h("h2", null, "Variables"), h("p", { class: "sub" }, "Se sustituyen en las rutas, las cabeceras y los cuerpos como {{variable}}."),
      h("div", { class: "dos" }, Object.keys(vars).map((k) => { const i = h("input", { value: vars[k], "aria-label": k, spellcheck: "false" }); entradas[k] = i; return h("label", { class: "campo" }, h("span", null, k), i); })));
    const sustituir = (t) => String(t ?? "").replace(/\{\{([^}]+)\}\}/g, (_, k) => (entradas[k.trim()] ? entradas[k.trim()].value : "{{" + k + "}}"));
    const peticiones = aplanar(col.item);
    const filas = peticiones.map((p) => {
      const req = p.request || {};
      const metodo = (req.method || "GET").toUpperCase();
      const urlCruda = typeof req.url === "string" ? req.url : (req.url && req.url.raw) || "";
      const salida = h("div");
      const lanzar = async () => {
        let url = sustituir(urlCruda).trim();
        const origen = location.origin;
        if (url.startsWith(origen)) url = url.slice(origen.length);
        if (!url.startsWith("/")) { salida.replaceChildren(h("p", { class: "err" }, "Solo se envían peticiones a este servidor (" + origen + ").")); return { estado: 0 }; }
        const cab = {};
        for (const c of req.header || []) { if (!c.disabled && !/^authorization$/i.test(c.key)) cab[c.key] = sustituir(c.value); }
        const crudo = req.body && req.body.mode === "raw" ? sustituir(req.body.raw) : null;
        salida.replaceChildren(h("p", { class: "nota" }, "Enviando…"));
        try { const r = await enviar(metodo, url, crudo, { cabeceras: cab }); recordar(metodo, url, crudo || "", r); salida.replaceChildren(vistaResultado(r)); return r; }
        catch (e) { salida.replaceChildren(h("p", { class: "err" }, "No se pudo enviar: " + e.message)); return { estado: 0 }; }
      };
      const boton = h("button", { class: "boton chico", onclick: lanzar }, "Lanzar");
      return { lanzar, nodo: h("div", { class: "factura" }, h("div", { class: "fila-acciones", style: "align-items:center" },
        h("span", { class: "mono", style: "min-width:64px;font-weight:700" }, metodo), h("span", { style: "flex:1 1 300px" }, (p.carpeta ? p.carpeta + " · " : "") + p.name), boton), h("p", { class: "mono nota" }, urlCruda), salida) };
    });
    const todo = h("button", { class: "boton", onclick: async () => { todo.disabled = true; for (const f of filas) { const r = await f.lanzar(); if (r.estado < 200 || r.estado >= 300) break; } todo.disabled = false; } }, "Ejecutar todo en orden");
    zona.replaceChildren(
      h("div", { class: "tarjeta" }, h("h2", null, col.info && col.info.name || "Colección"), h("p", { class: "nota" }, `${peticiones.length} peticiones. Los scripts de prueba de la colección no se ejecutan.`), todo),
      tarjetaVars, h("div", { class: "tarjeta" }, filas.map((f) => f.nodo)));
  }

  return h("div", null, tarjetaSesion(), h("div", { class: "tarjeta" }, h("h2", null, "Colección"), h("p", { class: "sub" }, "Archivo .json exportado de Postman (Collection v2.1)."), archivo), zona);
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
