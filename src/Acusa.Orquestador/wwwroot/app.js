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
            ? tabla(["Hora", "Operación", "Empresa", "Estado"], tareas.map((t) => h("tr", null, h("td", { class: "mono" }, hora(t.creada)), h("td", { class: "mono" }, t.tipoProceso),
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
  raiz.append(encabezado("Tareas", "Cada orden que llega por la API, con su resultado o su error.", h("label", { class: "campo" }, h("span", null, "Estado"), filtro)), lista);
  const abiertas = new Set();
  async function cargar() {
    try {
      const ts = await api("/api/sdk/tasks?limite=100" + (filtro.value ? "&estado=" + filtro.value : ""));
      lista.replaceChildren(ts.length
        ? tabla(["Hora", "Operación", "Empresa", "Estado", "Resultado"], ts.map((t) => {
            const detalle = t.error || (t.resultado ? json(t.resultado) : "");
            const abierta = abiertas.has(t.id);
            return h("tr", null, h("td", { class: "mono" }, hora(t.creada)), h("td", { class: "mono" }, t.tipoProceso), h("td", { class: "mono" }, t.empresa),
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

function curl(base, tipo, empresa, payload) {
  return `curl -X POST ${base}/api/sdk/execute \\\n  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \\\n  -d '${JSON.stringify({ tipoProceso: tipo, empresa, payload: JSON.parse(payload) })}'`;
}

function vistaDevs(raiz) {
  const base = location.origin;
  const catalogo = h("div");
  const bloque = (titulo, texto) => h("div", { style: "margin-top:10px" }, h("div", { class: "etiqueta" }, titulo), h("div", { class: "copiable" }, h("code", null, texto), h("button", { class: "boton suave chico", onclick: () => copiar(texto) }, "Copiar")));
  raiz.append(
    encabezado("Desarrolladores", "Su sistema manda órdenes a Acusa Central; un Acusa Worker las ejecuta en CONTPAQi Comercial y devuelve el resultado. Con ?esperar=60 la misma respuesta trae el resultado; sin él, la API responde con un identificador de tarea y usted consulta su estado."),
    h("div", { class: "tarjeta" }, h("h2", null, "Cómo se usa"),
      bloque("1. Obtener un token (dura una hora)", `POST ${base}/api/auth/token   cabecera  x-api-key: <su llave>`),
      bloque("2. Mandar una orden y recibir el resultado en la misma respuesta", `POST ${base}/api/sdk/execute?esperar=60   cabecera  Authorization: Bearer <token>   (opcional: Idempotency-Key: <llave única>)\n{ "tipoProceso": "...", "empresa": "...", "payload": { ... } }\n→ 200 { "tareaId", "estado": "Completada", "resultado": { ... } }    →  422 si falló, con "error"    →  202 si pasan los 60 s (la tarea sigue)`),
      bloque("3. Solo si recibió 202: consultar la tarea", `GET ${base}/api/sdk/tasks/{tareaId}`),
      h("p", { class: "nota", style: "margin-top:10px" }, "También: GET /api/sdk/tasks (lista), GET /api/sdk/workers y GET /api/sdk/status. La empresa también se acepta con el nombre dbName.")),
    h("div", { class: "tarjeta" }, h("h2", null, "Flujo mínimo de timbrado"),
      h("p", { class: "sub" }, "Seis pasos bastan para emitir una factura:"),
      h("ol", { class: "pasos" },
        h("li", null, h("b", null, "Token: "), "POST /api/auth/token."),
        h("li", null, h("b", null, "cliente.crear "), "(o cliente.leer si ya existe)."),
        h("li", null, h("b", null, "producto.crear "), "(o producto.leer si ya existe)."),
        h("li", null, h("b", null, "documento.crear: "), "devuelve concepto, serie y folio."),
        h("li", null, h("b", null, "documento.emitir: "), "timbra el documento con ese concepto, serie y folio."),
        h("li", null, h("b", null, "documento.obtener-archivo: "), "entrega el XML y el PDF; con documento.cancelar se cancela ante el SAT.")),
      h("p", { class: "nota" }, "Solo estas ocho operaciones están disponibles; cualquier otra responde 400. Use nombres que empiecen con PRUEBA en empresas de prueba.")),
    catalogo);

  Promise.all([api("/api/portal/operaciones"), api("/api/portal/empresas")]).then(([ops, empresas]) => {
    const empresa = empresas[0] || "adEMPRESA_DE_PRUEBA";
    catalogo.replaceChildren(h("div", { class: "tarjeta" }, h("h2", null, "Operaciones disponibles"),
      tabla(["Operación", "Qué hace", "Ejemplo"], ops.map((o) => h("tr", null, h("td", { class: "mono" }, o.tipoProceso), h("td", null, o.descripcion),
        h("td", null, h("details", null, h("summary", null, "Ver"), h("pre", null, curl(base, o.tipoProceso, empresa, o.ejemplo)),
          h("button", { class: "boton suave chico", onclick: () => copiar(curl(base, o.tipoProceso, empresa, o.ejemplo)) }, "Copiar"))))))));
  }).catch(falla(catalogo));
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
