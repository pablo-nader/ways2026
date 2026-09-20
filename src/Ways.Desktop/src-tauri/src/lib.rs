mod comandos;
mod config;
mod credencial;
mod impresion;

use tauri::{Manager, WebviewUrl, WebviewWindowBuilder};

const ETIQUETA_VENTANA_CONFIGURACION: &str = "main";
const ETIQUETA_VENTANA_POS: &str = "pos";

pub fn ejecutar() {
    tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            enfocar_ventana_activa(app);
        }))
        .plugin(
            tauri_plugin_global_shortcut::Builder::new()
                .with_shortcut("ctrl+shift+F10")
                .expect("el atajo ctrl+shift+F10 deberia ser valido")
                .with_handler(|app, _shortcut, evento| {
                    if evento.state() == tauri_plugin_global_shortcut::ShortcutState::Pressed {
                        let _ = mostrar_ventana_configuracion(app);
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

            if config::leer(&handle).is_some() {
                mostrar_ventana_pos(&handle)?;
            } else {
                mostrar_ventana_configuracion(&handle)?;
            }
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("error al ejecutar la aplicacion de Ways POS");
}

/// Enfoca la ventana que esta actualmente visible (la del POS si existe y esta visible, si no la
/// de configuracion) cuando el usuario vuelve a abrir la app con `tauri-plugin-single-instance`.
fn enfocar_ventana_activa(app: &tauri::AppHandle) {
    if let Some(pos) = app.get_webview_window(ETIQUETA_VENTANA_POS) {
        if pos.is_visible().unwrap_or(false) {
            let _ = pos.unminimize();
            let _ = pos.show();
            let _ = pos.set_focus();
            return;
        }
    }

    if let Some(configuracion) = app.get_webview_window(ETIQUETA_VENTANA_CONFIGURACION) {
        let _ = configuracion.unminimize();
        let _ = configuracion.show();
        let _ = configuracion.set_focus();
    }
}

/// Muestra la ventana `pos` (pagina LOCAL bundleada `pos.html`, ver `capabilities/pos.json`) y
/// oculta la de configuracion. La ventana `pos` se crea una sola vez y se reutiliza en los
/// siguientes llamados -- pero SIEMPRE se recarga (`window.location.reload()`) antes de mostrarla:
/// es la unica forma de que la pagina relea `info_app` (de donde saca la URL del servidor, ver
/// `comandos::info_app`) despues de que la configuracion cambio, en vez de seguir operando con el
/// servidor viejo cacheado en memoria desde el primer arranque.
pub(crate) fn mostrar_ventana_pos(app: &tauri::AppHandle) -> tauri::Result<()> {
    if let Some(configuracion) = app.get_webview_window(ETIQUETA_VENTANA_CONFIGURACION) {
        let _ = configuracion.hide();
    }

    if let Some(pos) = app.get_webview_window(ETIQUETA_VENTANA_POS) {
        // reload() nativo y no eval(): la CSP declara script-src 'self' sin unsafe-eval,
        // asi que un script inyectado en la pagina podria quedar bloqueado segun como
        // WebView2 aplique la politica. La API nativa no inyecta nada.
        pos.reload()?;
        pos.show()?;
        pos.set_focus()?;
    } else {
        WebviewWindowBuilder::new(
            app,
            ETIQUETA_VENTANA_POS,
            WebviewUrl::App("pos.html".into()),
        )
        .title("Ways POS")
        .maximized(true)
        .min_inner_size(1024.0, 700.0)
        .resizable(true)
        .build()?;
    }

    Ok(())
}

/// Vuelve a mostrar la ventana `main` (pagina LOCAL bundleada de configuracion, ver
/// `capabilities/configuracion.json`) y oculta la del POS si existe. Igual que
/// `mostrar_ventana_pos`, siempre recarga: la pagina de configuracion solo lee su estado
/// (`leer_configuracion`, `info_app`) en `DOMContentLoaded`, que no vuelve a disparar si la
/// ventana ya existia y solo se la vuelve a mostrar.
pub(crate) fn mostrar_ventana_configuracion(app: &tauri::AppHandle) -> tauri::Result<()> {
    if let Some(pos) = app.get_webview_window(ETIQUETA_VENTANA_POS) {
        let _ = pos.hide();
    }

    let configuracion = app
        .get_webview_window(ETIQUETA_VENTANA_CONFIGURACION)
        .expect(
        "la ventana de configuracion 'main' deberia existir siempre (declarada en tauri.conf.json)",
    );
    // Idem: reload() nativo, no eval() — ver el comentario en mostrar_ventana_pos.
    configuracion.reload()?;
    configuracion.show()?;
    configuracion.set_focus()?;
    Ok(())
}

#[cfg(test)]
mod tests {
    /// stage-desktop-pos, slice 3: prueba que las dos capacidades LOCALES (`configuracion.json`,
    /// ventana `main`, y `pos.json`, ventana `pos`) no se solapan en los comandos sensibles que
    /// esta slice separo a proposito -- ninguna le da a la ventana equivocada un comando que no
    /// necesita.
    ///
    /// Lo que ESTA prueba prueba: que el JSON de cada archivo de capacidad, tal cual esta en
    /// disco, tiene (o no tiene) ciertas entradas en su array `permissions`, y que cada uno declara
    /// exactamente su propia ventana en `windows`.
    ///
    /// Lo que NO prueba (limite deliberado, mismo motivo documentado en la version anterior de
    /// esta prueba sobre `PERMISOS_REMOTOS`): no ejercita `RuntimeAuthority::resolve_access`, el
    /// resolvedor real de Tauri que en definitiva decide si un comando corre en una ventana dada.
    /// Armar esa resolucion real requiere un `AppHandle`/`Webview` sobre el runtime real `Wry`
    /// (los comandos de `comandos.rs` no son genericos sobre `R: Runtime`), lo que no compila
    /// contra `tauri::test::MockRuntime` ni es razonable de levantar en un test unitario. Esta
    /// prueba depende, a proposito, de que Tauri siga cargando los archivos de
    /// `capabilities/*.json` tal cual estan en disco (comportamiento estandar de Tauri 2, no algo
    /// que este codigo controle) -- si algun dia se agrega post-procesamiento de esos archivos
    /// antes de que Tauri los cargue, esta prueba deja de ser un espejo fiel y hay que revisarla a
    /// mano.
    #[test]
    fn las_capacidades_locales_no_se_solapan_en_los_comandos_sensibles() {
        let configuracion: serde_json::Value =
            serde_json::from_str(include_str!("../capabilities/configuracion.json")).unwrap();
        let pos: serde_json::Value =
            serde_json::from_str(include_str!("../capabilities/pos.json")).unwrap();

        fn permisos(valor: &serde_json::Value) -> Vec<String> {
            valor["permissions"]
                .as_array()
                .expect("permissions deberia ser un array")
                .iter()
                .map(|p| {
                    p.as_str()
                        .expect("cada permiso deberia ser un string")
                        .to_string()
                })
                .collect()
        }

        let permisos_configuracion = permisos(&configuracion);
        let permisos_pos = permisos(&pos);

        assert_eq!(configuracion["windows"], serde_json::json!(["main"]));
        assert_eq!(pos["windows"], serde_json::json!(["pos"]));

        let exclusivos_de_pos = [
            "allow-imprimir-raw",
            "allow-listar-impresoras",
            "allow-abrir-configuracion",
            "allow-guardar-credencial-de-dispositivo",
            "allow-leer-credencial-de-dispositivo",
        ];
        for permiso in exclusivos_de_pos {
            assert!(
                !permisos_configuracion.contains(&permiso.to_string()),
                "la capacidad de configuracion no deberia tener '{permiso}'"
            );
            assert!(
                permisos_pos.contains(&permiso.to_string()),
                "la capacidad del pos deberia tener '{permiso}'"
            );
        }

        let exclusivos_de_configuracion =
            ["allow-guardar-configuracion", "allow-leer-configuracion"];
        for permiso in exclusivos_de_configuracion {
            assert!(
                !permisos_pos.contains(&permiso.to_string()),
                "la capacidad del pos no deberia tener '{permiso}'"
            );
            assert!(
                permisos_configuracion.contains(&permiso.to_string()),
                "la capacidad de configuracion deberia tener '{permiso}'"
            );
        }
    }
}
