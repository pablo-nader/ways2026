mod comandos;
mod config;
mod credencial;
mod impresion;

use tauri::ipc::CapabilityBuilder;
use tauri::Manager;

const ETIQUETA_VENTANA_PRINCIPAL: &str = "main";
const IDENTIFICADOR_CAPACIDAD_REMOTA: &str = "pos-remoto";

/// Permisos del subconjunto de comandos que la pagina remota (`/pos.html`)
/// puede invocar. Se agregan en runtime, restringidos al origen exacto que
/// el usuario configuro (ver `registrar_capacidad_remota`).
///
/// stage-desktop-pos, slice bearer: se agregaron `allow-guardar-credencial-de-dispositivo` y
/// `allow-leer-credencial-de-dispositivo` a proposito y de forma MINIMA — son los unicos dos
/// comandos nuevos que `/pos.html` (todavia remoto en este slice) necesita para persistir/leer
/// el secreto de dispositivo (ver `comandos.rs`, `credencial.rs`). Nota honesta sobre el otro
/// lado de la superficie: `capabilities/local.json` le otorga a la pagina LOCAL de configuracion
/// los 11 comandos existentes en bloque (`core:default` + un `allow-*` por comando, sin
/// distincion fina) — un allowlist correcto pero mas ancho del que esa pagina en rigor necesita.
/// Funciona hoy porque la pagina local es de confianza (bundleada con la app) y porque el shell
/// del POS sigue siendo remoto; la slice 3 (que va a mover `/pos.html` a una pagina LOCAL) tiene
/// que separar ese bloque en capacidades mas finas antes de que la superficie local también
/// incluya el POS — mezclar los dos en una sola capacidad local de 13 comandos en ese momento
/// repetiria, a mayor escala, la misma laxitud que hoy es inocua.
const PERMISOS_REMOTOS: &[&str] = &[
    "allow-imprimir-raw",
    "allow-listar-impresoras",
    "allow-abrir-configuracion",
    "allow-info-app",
    "allow-guardar-credencial-de-dispositivo",
    "allow-leer-credencial-de-dispositivo",
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
            comandos::volver_a_pos,
            comandos::info_app,
            comandos::guardar_credencial_de_dispositivo,
            comandos::leer_credencial_de_dispositivo,
        ])
        .setup(|app| {
            let handle = app.handle().clone();

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
/// en la ventana principal. La URL se recalcula en cada llamada a partir de
/// la configuracion de Tauri (ver `config::url_configuracion_local`) en vez
/// de depender de un valor capturado al arrancar: `ventana.url()` en
/// `setup()` puede no reflejar aun la navegacion real (la ventana recien se
/// esta creando), lo que dejaba la app mostrando una pantalla en blanco sin
/// forma de volver a la configuracion.
fn abrir_pagina_configuracion(app: &tauri::AppHandle) -> Result<(), String> {
    let ventana = app
        .get_webview_window(ETIQUETA_VENTANA_PRINCIPAL)
        .ok_or_else(|| "No se encontro la ventana principal.".to_string())?;

    let usa_https = app
        .config()
        .app
        .windows
        .iter()
        .find(|ventana_config| ventana_config.label == ETIQUETA_VENTANA_PRINCIPAL)
        .map(|ventana_config| ventana_config.use_https_scheme)
        .unwrap_or(false);
    let es_windows_o_android = cfg!(windows) || cfg!(target_os = "android");

    let url_local = config::url_configuracion_local(es_windows_o_android, usa_https)
        .parse::<tauri::Url>()
        .map_err(|error| format!("La URL local de configuracion no es valida: {error}"))?;

    ventana
        .navigate(url_local)
        .map_err(|error| format!("No se pudo abrir la configuracion: {error}"))
}
