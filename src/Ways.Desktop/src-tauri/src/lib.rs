mod comandos;
mod config;
mod impresion;

use tauri::ipc::CapabilityBuilder;
use tauri::Manager;

const ETIQUETA_VENTANA_PRINCIPAL: &str = "main";
const IDENTIFICADOR_CAPACIDAD_REMOTA: &str = "pos-remoto";

/// URL local original de la ventana principal (la pagina de configuracion
/// embebida en el bundle), capturada antes de navegar al servidor remoto
/// configurado. Se usa para poder volver a la configuracion sin depender de
/// una ruta relativa, que se resolveria contra el origen remoto actual.
struct UrlLocalConfiguracion(tauri::Url);

/// Permisos del subconjunto de comandos que la pagina remota (`/pos.html`)
/// puede invocar. Se agregan en runtime, restringidos al origen exacto que
/// el usuario configuro (ver `registrar_capacidad_remota`).
const PERMISOS_REMOTOS: &[&str] = &[
    "allow-imprimir-raw",
    "allow-listar-impresoras",
    "allow-abrir-configuracion",
    "allow-info-app",
];

pub fn ejecutar() {
    tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            enfocar_ventana_principal(app);
        }))
        .plugin(
            tauri_plugin_global_shortcut::Builder::new()
                .with_shortcut("ctrl+shift+F10")
                .expect("el atajo ctrl+shift+F10 deberia ser valido")
                .with_handler(|app, _shortcut, evento| {
                    if evento.state() == tauri_plugin_global_shortcut::ShortcutState::Pressed {
                        let _ = abrir_pagina_configuracion(app);
                    }
                })
                .build(),
        )
        .invoke_handler(tauri::generate_handler![
            comandos::listar_impresoras,
            comandos::listar_impresoras_con_detalle,
            comandos::impresora_predeterminada_de_windows,
            comandos::impresora_predeterminada_detallada,
            comandos::imprimir_raw,
            comandos::imprimir_prueba,
            comandos::guardar_configuracion,
            comandos::leer_configuracion,
            comandos::abrir_configuracion,
            comandos::info_app,
        ])
        .setup(|app| {
            let handle = app.handle().clone();

            let ventana = handle
                .get_webview_window(ETIQUETA_VENTANA_PRINCIPAL)
                .expect("la ventana principal 'main' deberia existir");
            let url_local = ventana.url()?;
            handle.manage(UrlLocalConfiguracion(url_local));

            if let Some(configuracion) = config::leer(&handle) {
                registrar_capacidad_remota(&handle, &configuracion.url_servidor)?;
                navegar_a_pos(&handle, &configuracion.url_servidor)?;
            }
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("error al ejecutar la aplicacion de Ways POS");
}

fn enfocar_ventana_principal(app: &tauri::AppHandle) {
    if let Some(ventana) = app.get_webview_window(ETIQUETA_VENTANA_PRINCIPAL) {
        let _ = ventana.unminimize();
        let _ = ventana.show();
        let _ = ventana.set_focus();
    }
}

/// Otorga a la URL remota configurada acceso exclusivo al subconjunto de
/// comandos POS. Se agrega en runtime porque la URL del servidor la define
/// el usuario en la pagina local de configuracion.
pub(crate) fn registrar_capacidad_remota(
    app: &tauri::AppHandle,
    url_servidor: &str,
) -> tauri::Result<()> {
    let mut constructor = CapabilityBuilder::new(IDENTIFICADOR_CAPACIDAD_REMOTA)
        .local(false)
        .window(ETIQUETA_VENTANA_PRINCIPAL)
        .remote(format!("{url_servidor}/*"));

    for permiso in PERMISOS_REMOTOS {
        constructor = constructor.permission(*permiso);
    }

    app.add_capability(constructor)
}

/// Navega la ventana principal hacia `{url_servidor}/pos.html`.
fn navegar_a_pos(app: &tauri::AppHandle, url_servidor: &str) -> tauri::Result<()> {
    let ventana = app
        .get_webview_window(ETIQUETA_VENTANA_PRINCIPAL)
        .expect("la ventana principal 'main' deberia existir");
    let destino = config::url_pos(url_servidor);
    ventana.eval(format!(
        "window.location.replace({});",
        serde_json::to_string(&destino).unwrap_or_else(|_| "\"\"".to_string())
    ))
}

/// Vuelve a mostrar la pagina local de configuracion (bundle `ui/index.html`)
/// en la ventana principal. Navega a la URL local capturada al arrancar
/// (`UrlLocalConfiguracion`) en vez de una ruta relativa, que se resolveria
/// contra el origen remoto si la ventana ya esta mostrando el POS.
fn abrir_pagina_configuracion(app: &tauri::AppHandle) -> Result<(), String> {
    let ventana = app
        .get_webview_window(ETIQUETA_VENTANA_PRINCIPAL)
        .ok_or_else(|| "No se encontro la ventana principal.".to_string())?;
    let url_local = app
        .try_state::<UrlLocalConfiguracion>()
        .ok_or_else(|| "No se pudo determinar la URL local de configuracion.".to_string())?
        .0
        .clone();
    ventana
        .navigate(url_local)
        .map_err(|error| format!("No se pudo abrir la configuracion: {error}"))
}
