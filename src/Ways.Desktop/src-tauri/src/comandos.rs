//! Comandos Tauri invocables desde las dos paginas LOCALES de la app: la de
//! configuracion (ventana `main`, `capabilities/configuracion.json`) y la del
//! punto de venta (ventana `pos`, `capabilities/pos.json`) -- cada una con su
//! propio subconjunto, ver esos dos archivos.

use serde::Serialize;
use tauri::AppHandle;

use crate::config::{self, Configuracion};
use crate::credencial;
use crate::impresion;
use crate::sesion;

#[derive(Serialize)]
pub struct InfoApp {
    pub version: String,
    pub impresora: Option<String>,
    /// stage-desktop-pos, slice 3: la pagina LOCAL del POS (`pos.html`) no tiene otra forma de
    /// enterarse de la URL del servidor -- a diferencia de la vieja pagina remota, ya no corre
    /// DENTRO de ese origen, y no tiene permiso para invocar `leer_configuracion` (exclusivo de la
    /// ventana de configuracion, ver `capabilities/pos.json`). Se reusa `info_app`, que el POS ya
    /// tenia permitido, en vez de agregar un comando nuevo solo para esto.
    pub url_servidor: Option<String>,
}

/// Lista los nombres de las impresoras instaladas en Windows.
#[tauri::command]
pub fn listar_impresoras() -> Result<Vec<String>, String> {
    impresion::listar_impresoras()
}

/// Lista las impresoras instaladas junto con el diagnostico de "es
/// virtual" (PDF, XPS, OneNote, fax, etc.), para que la pagina de
/// configuracion las marque como no seleccionables.
#[tauri::command]
pub fn listar_impresoras_con_detalle() -> Result<Vec<impresion::InfoImpresora>, String> {
    impresion::listar_impresoras_con_detalle()
}

/// Nombre de la impresora predeterminada de Windows, si hay una configurada
/// en el sistema operativo.
#[tauri::command]
pub fn impresora_predeterminada_de_windows() -> Option<String> {
    impresion::impresora_predeterminada()
}

/// Como `impresora_predeterminada_de_windows`, pero indicando ademas si esa
/// impresora predeterminada es virtual, para mostrarlo en la pagina de
/// configuracion.
#[tauri::command]
pub fn impresora_predeterminada_detallada() -> impresion::ImpresoraPredeterminada {
    impresion::impresora_predeterminada_detallada()
}

/// Envia bytes ESC/POS crudos a la impresora configurada, sin dialogo de
/// impresion. Rechaza impresoras virtuales/de documento (PDF, XPS, OneNote,
/// fax) para no romper nunca la garantia de "sin dialogos".
#[tauri::command]
pub fn imprimir_raw(app: AppHandle, bytes: Vec<u8>) -> Result<(), String> {
    let configuracion =
        config::leer(&app).ok_or_else(|| "La aplicacion no esta configurada.".to_string())?;
    let impresora = impresion::resolver_impresora_efectiva(configuracion.impresora.as_deref())?;
    impresion::imprimir_raw(&impresora, &bytes)
}

/// Imprime un ticket de prueba armado en Rust. Si se indica `impresora`
/// (por ejemplo, la seleccionada en el combo de la pagina de configuracion
/// antes de guardar), se usa esa; si no, se usa la de la configuracion
/// guardada o la predeterminada de Windows. Tambien rechaza impresoras
/// virtuales/de documento.
#[tauri::command]
pub fn imprimir_prueba(app: AppHandle, impresora: Option<String>) -> Result<(), String> {
    let nombre_a_usar = match impresora {
        Some(nombre) => Some(nombre),
        None => config::leer(&app).and_then(|c| c.impresora),
    };
    let impresora = impresion::resolver_impresora_efectiva(nombre_a_usar.as_deref())?;
    let ticket = impresion::construir_ticket_prueba();
    impresion::imprimir_raw(&impresora, &ticket)
}

/// Guarda la configuracion (URL de servidor + impresora) y muestra la ventana `pos`. Rechaza
/// guardar una impresora virtual/de documento, aunque la pagina ya deberia impedir seleccionarla.
#[tauri::command]
pub fn guardar_configuracion(app: AppHandle, configuracion: Configuracion) -> Result<(), String> {
    if let Some(nombre) = configuracion.impresora.as_deref() {
        if !nombre.trim().is_empty() {
            impresion::resolver_impresora_efectiva(Some(nombre))?;
        }
    }

    config::guardar(&app, &configuracion)?;
    config::leer(&app)
        .ok_or_else(|| "No se pudo releer la configuracion recien guardada.".to_string())?;

    crate::mostrar_ventana_pos(&app).map_err(|error| {
        format!("La configuracion se guardo, pero no se pudo abrir el punto de venta: {error}")
    })
}

/// Lee la configuracion actual, si existe.
#[tauri::command]
pub fn leer_configuracion(app: AppHandle) -> Option<Configuracion> {
    config::leer(&app)
}

/// Vuelve a mostrar la ventana local de configuracion.
#[tauri::command]
pub fn abrir_configuracion(app: AppHandle) -> Result<(), String> {
    crate::mostrar_ventana_configuracion(&app)
        .map_err(|error| format!("No se pudo abrir la configuracion: {error}"))
}

/// Vuelve a mostrar la ventana del punto de venta usando la configuracion ya guardada, sin
/// persistir ningun cambio. Se usa desde el boton "Volver al POS" de la pagina de configuracion,
/// para no dejar al usuario atrapado ahi cuando solo quiere descartar la edicion en curso.
#[tauri::command]
pub fn volver_a_pos(app: AppHandle) -> Result<(), String> {
    config::leer(&app).ok_or_else(|| "No hay una configuracion guardada.".to_string())?;
    crate::mostrar_ventana_pos(&app)
        .map_err(|error| format!("No se pudo volver al punto de venta: {error}"))
}

/// Informacion basica de la app para mostrar en la pagina de configuracion o para diagnostico
/// desde el POS -- incluye `url_servidor` (ver doc de `InfoApp::url_servidor`).
#[tauri::command]
pub fn info_app(app: AppHandle) -> InfoApp {
    let configuracion = config::leer(&app);
    InfoApp {
        version: app.package_info().version.to_string(),
        impresora: configuracion.as_ref().and_then(|c| c.impresora.clone()),
        url_servidor: configuracion.map(|c| c.url_servidor),
    }
}

/// Guarda el secreto de dispositivo (stage-desktop-pos, slice bearer) que devuelve UNA sola vez
/// `POST /api/dispositivos` en su cuerpo de respuesta — la pagina de vinculacion lo entrega aca
/// para que Rust lo persista en su propio archivo (`credencial::guardar`, nunca en
/// `config.json`), en vez de guardarlo en `localStorage` del lado de JS.
#[tauri::command]
pub fn guardar_credencial_de_dispositivo(app: AppHandle, secreto: String) -> Result<(), String> {
    credencial::guardar(&app, &secreto)
}

/// Devuelve el secreto de dispositivo guardado, si hay uno.
///
/// stage-desktop-pos, slice 3: otorgado a la capacidad LOCAL del POS (`capabilities/pos.json`) --
/// la pagina remota anterior deliberadamente NO lo tenia (judgment-day ronda 1, ver historial de
/// `lib.rs`), porque leerlo desde un origen remoto con `csp: null` lo dejaba expuesto a cualquier
/// script de ese origen. Ahora que `pos.html` es una pagina LOCAL bundleada (nunca expuesta a
/// script remoto) y la app tiene una CSP real (ver `tauri.conf.json`), `AppPos.tsx` lo usa para
/// distinguir "sin red pero ya vinculado" de "nunca vinculado" (ver `AppPos.tsx`).
#[tauri::command]
pub fn leer_credencial_de_dispositivo(app: AppHandle) -> Option<String> {
    credencial::leer(&app)
}

/// Guarda la sesión del cajero (token bearer + vencimiento explícito que ya devolvió
/// `POST /auth/login-dispositivo`, más el snapshot de dispositivo/PV/cajero para reconstruir el
/// shell offline — judgment-day ronda 2, ver el doc-comment de `sesion::SesionDeCajero`) en su
/// propio archivo (`sesion::guardar`, nunca en `dispositivo.credencial` ni en `config.json`) — la
/// página local del POS lo llama al loguearse y, con `token`/`expira_el` vacíos, para limpiarla
/// en los tres casos donde deja de ser válida: logout explícito, un 401 del servidor, y el cierre
/// de turno (ver `entornoTauri.ts` del lado de React y sus llamadores). Esa limpieza se lleva el
/// snapshot puesto, por ser el mismo registro — no hay un comando separado de limpieza ni uno
/// separado para el snapshot.
#[tauri::command]
pub fn guardar_sesion_de_cajero(
    app: AppHandle,
    sesion: sesion::SesionDeCajero,
) -> Result<(), String> {
    sesion::guardar(&app, &sesion)
}

/// Devuelve la sesión de cajero guardada (token + vencimiento + snapshot), si hay una. La validez
/// del vencimiento la decide quien llama (`entornoTauri.ts`, contra el reloj local) — este
/// comando devuelve lo que hay en disco tal cual, igual que `leer_credencial_de_dispositivo`.
#[tauri::command]
pub fn leer_sesion_de_cajero(app: AppHandle) -> Option<sesion::SesionDeCajero> {
    sesion::leer(&app)
}
