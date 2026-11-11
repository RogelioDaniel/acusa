"use strict";

/* Panel local de Acusa Worker 1.0.0: Estado, Configuración y Bitácora.
   Solo se sirve en este equipo y todas las llamadas llevan el código de acceso local. */

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

const marca = () => h("div", { class: "marca" }, h("b", null, "A"), h("span", null, "Acusa ", h("small", null, "Worker")));

const hora = (iso) => iso ? new Date(iso).toLocaleTimeString("es-MX", { hour: "2-digit", minute: "2-digit", second: "2-digit" }) : "—";
function hace(iso) {
  if (!iso) return "nunca";
  const s = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
  if (s < 60) return `hace ${s} s`;
  if (s < 3600) return `hace ${Math.round(s / 60)} min`;
  return `hace ${Math.round(s / 3600)} h`;
}
function aviso(texto) {
  const t = h("div", { class: "toast", role: "status" }, texto);
  document.body.append(t);
  setTimeout(() => t.remove(), 3500);
}

// ---------------------------------------------------------------- acceso

const codigo = {
  get() { try { return sessionStorage.getItem("acusa.codigo") || ""; } catch { return ""; } },
  set(v) { try { sessionStorage.setItem("acusa.codigo", v); } catch { /* sin almacenamiento */ } },
  borrar() { try { sessionStorage.removeItem("acusa.codigo"); } catch { /* sin almacenamiento */ } },
};

async function api(ruta, opciones = {}) {
  const r = await fetch(ruta, { ...opciones, headers: { "Content-Type": "application/json", "X-Acusa-Codigo": codigo.get(), ...(opciones.headers || {}) } });
  if (r.status === 401) { codigo.borrar(); pedirCodigo("El código no es correcto."); throw new Error("sin acceso"); }
  let cuerpo = null;
  try { cuerpo = await r.json(); } catch { /* sin cuerpo */ }
  if (!r.ok) throw new Error((cuerpo && cuerpo.error) || `Error ${r.status}`);
  return cuerpo;
}

let temporizadores = [];
function detener() { temporizadores.forEach(clearInterval); temporizadores = []; }
function cada(ms, fn) { fn(); temporizadores.push(setInterval(fn, ms)); }

function pedirCodigo(mensaje) {
  detener();
  const error = h("p", { class: "error", role: "alert" }, mensaje || "");
  const campo = h("input", { type: "text", autocomplete: "off", spellcheck: "false", maxlength: "8", required: true, placeholder: "ABCD2345", style: "font-family:var(--mono);letter-spacing:.15em;text-transform:uppercase" });
  const forma = h("form", { onsubmit: async (e) => {
    e.preventDefault(); codigo.set(campo.value.trim().toUpperCase());
    try { await api("/panel/estado"); arrancar(); } catch { /* api() vuelve a pedir el código */ }
  } },
    h("label", { class: "campo" }, h("span", null, "Código de acceso"), campo),
    h("p", { class: "nota-campo", style: "margin:-4px 0 14px" }, "Aparece en la ventana donde corre el worker, o en el archivo ", h("span", { class: "mono" }, "%LOCALAPPDATA%\\Acusa\\panel-codigo.txt"), "."),
    error, h("button", { class: "boton", type: "submit" }, "Abrir el panel"));
  $("#app").replaceChildren(h("div", { class: "entrada" }, h("div", { class: "entrada-caja" },
    marca(),
    h("h1", null, "Panel local"),
    h("p", null, "Este panel solo se abre en este equipo y pide un código para evitar que otro usuario del servidor cambie la configuración."),
    forma)));
  campo.focus();
}

// ---------------------------------------------------------------- estructura

const VISTAS = [
  { id: "estado", titulo: "Estado", vista: vistaEstado },
  { id: "config", titulo: "Configuración", vista: vistaConfig },
  { id: "bitacora", titulo: "Bitácora", vista: vistaBitacora },
];

function arrancar() {
  const punto = h("span", { class: "punto", id: "punto" });
  const etiqueta = h("span", { id: "etiqueta-conexion" }, "…");
  $("#app").replaceChildren(
    h("header", { class: "barra-sup" },
      marca(),
      h("nav", { class: "pestanas", "aria-label": "Secciones" }, VISTAS.map((v) => h("a", { href: "#/" + v.id, "data-vista": v.id }, v.titulo))),
      h("div", { class: "derecha" }, punto, etiqueta)),
    h("main", { id: "contenido" }));
  window.onhashchange = navegar;
  navegar();
}

function navegar() {
  detener();
  const id = (location.hash || "#/estado").replace(/^#\//, "");
  const vista = VISTAS.find((v) => v.id === id) || VISTAS[0];
  document.querySelectorAll("[data-vista]").forEach((a) => a.dataset.vista === vista.id ? a.setAttribute("aria-current", "page") : a.removeAttribute("aria-current"));
  document.title = `${vista.titulo} · Acusa Worker`;
  const c = $("#contenido");
  c.replaceChildren();
  vista.vista(c);
  // El indicador de la barra superior se actualiza en todas las pantallas.
  cada(3000, async () => {
    try { const e = await api("/panel/estado"); indicador(e); } catch { /* se muestra en la vista */ }
  });
}

const TEXTO_CONEXION = {
  conectado: ["ok", "Conectado a Acusa Central"],
  iniciando: ["", "Iniciando…"],
  "esperando-sdk": ["aviso", "Esperando la sesión del SDK"],
  "sin-conexion": ["mal", "Sin conexión con Acusa Central"],
  "llave-rechazada": ["mal", "Llave rechazada"],
  "sin configurar": ["aviso", "Sin configurar"],
};
function indicador(e) {
  const [clase, texto] = TEXTO_CONEXION[e.estado.conexion] || ["", e.estado.conexion];
  const punto = $("#punto"); if (punto) punto.className = "punto " + (clase === "ok" ? "ok" : clase === "mal" ? "mal" : "");
  const t = $("#etiqueta-conexion"); if (t) t.textContent = texto;
}

function encabezado(titulo, descripcion) {
  return h("div", { class: "encabezado" }, h("div", null, h("h1", null, titulo), descripcion ? h("p", null, descripcion) : null));
}

// ---------------------------------------------------------------- estado

function vistaEstado(raiz) {
  const cuerpo = h("div");
  raiz.append(encabezado("Estado", "Lo que está haciendo este worker ahora mismo."), cuerpo);
  cada(2500, async () => {
    try { pintarEstado(cuerpo, await api("/panel/estado")); }
    catch (e) { if (e.message !== "sin acceso") cuerpo.replaceChildren(h("div", { class: "tarjeta vacio" }, h("b", null, "No se pudo leer el estado"), e.message)); }
  });
}

function pintarEstado(cuerpo, e) {
  const s = e.estado;
  const [clase, texto] = TEXTO_CONEXION[s.conexion] || ["", s.conexion];
  const dato = (etiqueta, valor, nota, chico) => h("div", { class: "tarjeta dato" }, h("div", { class: "etiqueta" }, etiqueta), h("div", { class: "valor" + (chico ? " chico" : "") }, valor), h("div", { class: "nota" }, nota));
  const sdkTexto = !e.sdk ? "Sin iniciar" : ({ Lista: "Lista", Iniciando: "Esperando la clave", NoIniciada: "Sin iniciar", Error: "Con error" }[e.sdk.estado] || e.sdk.estado);
  const recientes = s.recientes.length
    ? h("div", { class: "tabla-caja" }, h("table", null,
        h("thead", null, h("tr", null, ["Hora", "Operación", "Resultado", "Duración"].map((t) => h("th", null, t)))),
        h("tbody", null, s.recientes.map((t) => h("tr", null, h("td", { class: "mono" }, hora(t.hora)), h("td", { class: "mono" }, t.tipo),
          h("td", null, h("span", { class: t.exito ? "estado-Completada" : "estado-Fallida" }, t.exito ? "Completada" : "Fallida"), t.error ? h("div", { class: "nota-campo" }, t.error) : null),
          h("td", { class: "mono" }, `${t.milisegundos} ms`))))))
    : h("div", { class: "vacio" }, h("b", null, "Aún no ha atendido tareas"), "Cuando Acusa Central le mande una orden aparecerá aquí.");

  cuerpo.replaceChildren(...[
    !e.configurado ? h("div", { class: "tarjeta", style: "border-color:var(--ambar)" }, h("h2", null, "Falta configurar este worker"),
      h("p", { class: "sub", style: "margin:4px 0 12px" }, s.conexionDetalle || "Complete la configuración para que empiece a atender órdenes."),
      h("a", { class: "boton", href: "#/config" }, "Ir a Configuración")) : null,
    h("div", { class: "rejilla", style: e.configurado ? "" : "margin-top:18px" },
      dato("Acusa Central", texto, s.ultimoContacto ? `Último contacto ${hace(s.ultimoContacto)}` : (s.conexionDetalle || e.urlOrquestador), true),
      dato("CONTPAQi Comercial", sdkTexto, e.sdk?.mensaje || (e.modo === "Simulado" ? "Modo simulado: no toca CONTPAQi." : "Sesión del SDK de 32 bits."), true),
      dato("Empresa", e.empresa, `${e.modo} · versión ${e.version}`, true),
      dato("Tareas", `${s.procesadas} / ${s.fallidas}`, s.tareaActual ? `En curso: ${s.tareaActual}` : "completadas / fallidas desde que inició")),
    h("div", { class: "tarjeta" }, h("h2", null, "Últimas tareas"), h("p", { class: "sub" }, `Worker ${e.workerId}`), recientes)].filter(Boolean));
  indicador(e);
  void clase;
}

// ---------------------------------------------------------------- configuración

function vistaConfig(raiz) {
  const cuerpo = h("div");
  raiz.append(encabezado("Configuración", "Conexión con Acusa Central y con CONTPAQi Comercial. Los cambios se aplican al reiniciar el worker."), cuerpo);
  Promise.all([api("/panel/config"), api("/panel/empresas-locales")]).then(([c, empresas]) => pintarConfig(cuerpo, c, empresas))
    .catch((e) => { if (e.message !== "sin acceso") cuerpo.replaceChildren(h("div", { class: "tarjeta vacio" }, e.message)); });
}

function pintarConfig(cuerpo, c, empresasLocales) {
  const url = h("input", { value: c.url, placeholder: "https://central.suempresa.com", required: true });
  const llave = h("input", { type: "password", autocomplete: "off", placeholder: c.llaveGuardada ? "Guardada — escriba otra solo para cambiarla" : "wk_…" });
  const resultado = h("div", { hidden: true });
  const probar = h("button", { class: "boton suave", type: "button", onclick: async () => {
    probar.disabled = true; resultado.hidden = false; resultado.className = "resultado-prueba"; resultado.textContent = "Probando…";
    try {
      const r = await api("/panel/probar", { method: "POST", body: JSON.stringify({ url: url.value, llave: llave.value }) });
      resultado.className = "resultado-prueba " + (r.ok ? "ok" : "mal"); resultado.textContent = r.mensaje;
    } catch (err) { resultado.className = "resultado-prueba mal"; resultado.textContent = err.message; }
    probar.disabled = false;
  } }, "Probar conexión");

  const empresa = h("select", null,
    h("option", { value: "" }, "Elija una empresa"),
    ...[...new Set([c.empresa, ...empresasLocales].filter(Boolean))].map((n) => h("option", { value: n, selected: n === c.empresa }, n)));
  const empresaManual = h("input", { placeholder: "o escriba el nombre, por ejemplo adEMPRESA_DE_PRUEBA", value: empresasLocales.includes(c.empresa) ? "" : (c.empresa || "") });
  const rutaSdk = h("input", { value: c.rutaSdk, required: true });
  const inicio = h("select", null, h("option", { value: "Dialogo", selected: c.inicio === "Dialogo" }, "Diálogo: una persona escribe la clave de CONTPAQi"),
    h("option", { value: "Credenciales", selected: c.inicio === "Credenciales" }, "Credenciales guardadas (sin diálogo) — sin comprobar"));
  const usuarioSdk = h("input", { value: c.usuarioSdk, autocomplete: "off" });
  const claveSdk = h("input", { type: "password", autocomplete: "new-password", placeholder: c.claveSdkGuardada ? "Guardada — escriba otra solo para cambiarla" : "" });
  const credenciales = h("div", { class: "dos" },
    h("label", { class: "campo" }, h("span", null, "Usuario de CONTPAQi"), usuarioSdk),
    h("label", { class: "campo" }, h("span", null, "Clave de CONTPAQi"), claveSdk,
      h("p", { class: "nota-campo" }, "Se guarda cifrada para el usuario de Windows que ejecuta el worker. Este inicio todavía no se ha comprobado contra CONTPAQi real.")));
  const alternar = () => { credenciales.hidden = inicio.value !== "Credenciales"; };
  inicio.addEventListener("change", alternar); alternar();
  const escritura = h("input", { type: "checkbox", checked: c.permitirEscritura });
  const documentosPermitidos = h("input", { type: "checkbox", checked: c.permitirDocumentos });
  const timbradoPermitido = h("input", { type: "checkbox", checked: c.permitirTimbrado });
  const claveCertificado = h("input", { type: "password", autocomplete: "new-password", placeholder: c.claveCertificadoGuardada ? "Guardada — escriba otra solo para cambiarla" : "" });
  const workerId = h("input", { value: c.workerId, placeholder: "Opcional. Por omisión: equipo-empresa" });

  const guardar = async (reiniciar) => {
    const nombre = (empresaManual.value.trim() || empresa.value);
    try {
      await api("/panel/config", { method: "POST", body: JSON.stringify({
        url: url.value, workerId: workerId.value, empresa: nombre, rutaSdk: rutaSdk.value, inicio: inicio.value,
        usuarioSdk: usuarioSdk.value, claveSdk: claveSdk.value, llaveWorker: llave.value, permitirEscritura: escritura.checked,
        permitirDocumentos: documentosPermitidos.checked, permitirTimbrado: timbradoPermitido.checked, claveCertificado: claveCertificado.value }) });
      llave.value = ""; claveSdk.value = ""; claveCertificado.value = "";
      if (reiniciar) { await api("/panel/reiniciar", { method: "POST" }); aviso("Reiniciando el worker… Si corre como servicio, vuelve solo; en consola ábralo de nuevo."); }
      else aviso("Configuración guardada. Se aplicará al reiniciar el worker.");
    } catch (err) { aviso(err.message); }
  };

  cuerpo.replaceChildren(h("form", { onsubmit: (e) => { e.preventDefault(); guardar(false); } },
    h("div", { class: "tarjeta" }, h("h2", null, "Acusa Central"), h("p", { class: "sub" }, "La dirección del portal y la llave de worker que creó en Llaves de API."),
      h("div", { class: "dos" }, h("label", { class: "campo" }, h("span", null, "Dirección"), url), h("label", { class: "campo" }, h("span", null, "Llave de worker"), llave)),
      probar, resultado),
    h("div", { class: "tarjeta" }, h("h2", null, "CONTPAQi Comercial"), h("p", { class: "sub" }, "La empresa principal es la que atiende este worker; las demás se agregan con el asistente de configuración."),
      h("div", { class: "dos" },
        h("label", { class: "campo" }, h("span", null, "Empresa que atenderá"), empresa, empresaManual,
          empresasLocales.length ? null : h("p", { class: "nota-campo" }, "No se encontró la carpeta C:\\Compac\\Empresas en este equipo.")),
        h("label", { class: "campo" }, h("span", null, "Carpeta de CONTPAQi Comercial"), rutaSdk)),
      h("label", { class: "campo" }, h("span", null, "Inicio de sesión del SDK"), inicio), credenciales,
      h("label", { class: "interruptor" }, escritura, h("span", null, h("b", null, "Permitir crear y modificar en esta empresa"),
        h("p", { class: "nota-campo" }, "Apagado, el worker solo lee. Encendido, además el código exige registros de prueba (códigos que empiezan con PRUEBA) hasta que se defina la política final.")))),
    h("div", { class: "tarjeta" }, h("h2", null, "Documentos y timbrado"),
      h("p", { class: "sub" }, "Cada candado exige el anterior. Timbrar gasta un timbre del PAC y cancelar avisa al SAT; ninguna de las dos se deshace."),
      h("label", { class: "interruptor" }, documentosPermitidos, h("span", null, h("b", null, "Permitir crear documentos"),
        h("p", { class: "nota-campo" }, "Cada documento consume un folio del concepto, y un folio no se reutiliza."))),
      h("label", { class: "interruptor" }, timbradoPermitido, h("span", null, h("b", null, "Permitir timbrar y cancelar"),
        h("p", { class: "nota-campo" }, "Solo documentos de prueba: su referencia debe empezar con PRUEBA. Un documento ya timbrado no se vuelve a timbrar."))),
      h("label", { class: "campo" }, h("span", null, "Contraseña del certificado (CSD)"), claveCertificado,
        h("p", { class: "nota-campo" }, "Se guarda cifrada para el usuario de Windows que ejecuta el worker. Nunca se envía en una orden ni se escribe en la bitácora."))),
    h("div", { class: "tarjeta" }, h("h2", null, "Este worker"), h("div", { class: "dos", style: "margin-top:10px" }, h("label", { class: "campo" }, h("span", null, "Nombre del worker"), workerId),
      h("div", { class: "dato" }, h("div", { class: "etiqueta" }, "Datos y bitácora"), h("div", { class: "mono", style: "margin-top:8px;word-break:break-all" }, c.carpetaDatos)))),
    h("div", { class: "fila-acciones", style: "margin-top:18px" },
      h("button", { class: "boton", type: "submit" }, "Guardar"),
      h("button", { class: "boton suave", type: "button", onclick: () => guardar(true) }, "Guardar y reiniciar"))));
}

// ---------------------------------------------------------------- bitácora

function vistaBitacora(raiz) {
  const caja = h("div", { class: "bitacora", tabindex: "0", role: "log", "aria-live": "off" });
  const nivel = h("select", { "aria-label": "Nivel" }, h("option", { value: "" }, "Todo"), h("option", { value: "AVISO" }, "Avisos y errores"), h("option", { value: "ERROR" }, "Solo errores"));
  const seguir = h("input", { type: "checkbox", checked: true, id: "seguir" });
  let ultimo = 0; const lineas = [];
  const pinta = () => {
    const filtro = nivel.value;
    caja.replaceChildren(...lineas.filter((l) => !filtro || (filtro === "AVISO" ? l.nivel !== "INFO" : l.nivel === "ERROR")).map((l) =>
      h("div", { class: l.nivel }, h("span", { class: "hora" }, hora(l.hora) + " "), `${l.nivel.padEnd(5)} ${l.origen}: ${l.mensaje}`)));
    if (seguir.checked) caja.scrollTop = caja.scrollHeight;
  };
  nivel.addEventListener("change", pinta);
  raiz.append(encabezado("Bitácora", "Lo que ha hecho el worker, de la más antigua a la más nueva. También se guarda un archivo por día."),
    h("div", { class: "fila-acciones", style: "margin-bottom:12px" },
      h("label", { class: "campo", style: "flex:0 1 240px" }, h("span", null, "Mostrar"), nivel),
      h("label", { class: "interruptor", style: "margin:0 0 4px" }, seguir, h("span", null, "Seguir lo más nuevo")),
      h("button", { class: "boton suave chico", type: "button", onclick: async () => {
        try { await navigator.clipboard.writeText(lineas.map((l) => `${hora(l.hora)} ${l.nivel} ${l.origen}: ${l.mensaje}`).join("\n")); aviso("Bitácora copiada"); } catch { aviso("No se pudo copiar"); }
      } }, "Copiar todo")),
    caja);
  cada(2000, async () => {
    try {
      const nuevas = await api("/panel/bitacora?desde=" + ultimo);
      if (nuevas.length) { lineas.push(...nuevas); ultimo = nuevas[nuevas.length - 1].n; while (lineas.length > 800) lineas.shift(); pinta(); }
    } catch { /* se reintenta */ }
  });
}

// ---------------------------------------------------------------- inicio

(async function inicio() {
  if (!codigo.get()) { pedirCodigo(); return; }
  try { await api("/panel/estado"); arrancar(); } catch { /* api() ya pidió el código */ }
})();
