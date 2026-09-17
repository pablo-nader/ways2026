//! Comandos Tauri invocables desde la pagina local de configuracion y,
//! para el subconjunto habilitado por la capacidad remota, desde
//! `{servidor}/pos.html`.

use serde::Serialize;
use tauri::AppHandle;

use crate::config::{self, Configuracion};
use crate::impresion;

#[derive(Serialize)]
pub struct InfoApp {
    pub version: String,
    pub impresora: Option<String>,
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

/// Guarda la configuracion (URL de servidor + impresora) y navega la
/// ventana principal hacia `{servidor}/pos.html`. Rechaza guardar una
/// impresora virtual/de documento, aunque la pagina ya deberia impedir
/// seleccionarla.
#[tauri::command]
pub fn guardar_configuracion(app: AppHandle, configuracion: Configuracion) -> Result<(), String> {
    if let Some(nombre) = configuracion.impresora.as_deref() {
        if !nombre.trim().is_empty() {
            impresion::resolver_impresora_efectiva(Some(nombre))?;
        }
    }

    config::guardar(&app, &configuracion)?;
    let guardada = config::leer(&app)
        .ok_or_else(|| "No se pudo releer la configuracion recien guardada.".to_string())?;

    crate::registrar_capacidad_remota(&app, &guardada.url_servidor).map_err(|error| {
        format!("La configuracion se guardo, pero no se pudo habilitar el acceso remoto: {error}")
    })?;
    crate::navegar_a_pos(&app, &guardada.url_servidor).map_err(|error| {
        format!("La configuracion se guardo, pero no se pudo abrir el punto de venta: {error}")
    })
}

/// Lee la configuracion actual, si existe.
#[tauri::command]
pub fn leer_configuracion(app: AppHandle) -> Option<Configuracion> {
    config::leer(&app)
}

/// Vuelve a mostrar la pagina local de configuracion en la ventana
/// principal.
#[tauri::command]
pub fn abrir_configuracion(app: AppHandle) -> Result<(), String> {
    crate::abrir_pagina_configuracion(&app)
}

/// Informacion basica de la app para mostrar en la pagina de configuracion
/// o para diagnostico desde la pagina remota.
#[tauri::command]
pub fn info_app(app: AppHandle) -> InfoApp {
    let configuracion = config::leer(&app);
    InfoApp {
        version: app.package_info().version.to_string(),
        impresora: configuracion.and_then(|c| c.impresora),
    }
}
