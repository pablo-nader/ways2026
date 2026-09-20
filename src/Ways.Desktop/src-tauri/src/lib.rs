mod comandos;
mod config;
mod credencial;
mod impresion;

use std::sync::Mutex;

use tauri::{Manager, WebviewUrl, WebviewWindowBuilder};

const ETIQUETA_VENTANA_CONFIGURACION: &str = "main";
const ETIQUETA_VENTANA_POS: &str = "pos";

/// URL del servidor que la ventana `pos` tiene actualmente cargada en memoria (la que leyo via
/// `info_app` la ultima vez que se construyo o recargo). `mostrar_ventana_pos` la compara contra
/// la configuracion vigente (`necesita_recargar_pos`) para decidir si hace falta recargar. `None`
/// hasta que la ventana se muestra por primera vez.
#[derive(Default)]
struct UrlServidorCargadaEnPos(Mutex<Option<String>>);

pub fn ejecutar() {
    tauri::Builder::default()
        .manage(UrlServidorCargadaEnPos::default())
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
/// siguientes llamados. Solo se recarga (`window.location.reload()` nativo) cuando la URL del
/// servidor cambio desde la ultima vez que se mostro (ver `necesita_recargar_pos`) -- es la unica
/// forma de que la pagina relea `info_app` (de donde saca la URL del servidor, ver
/// `comandos::info_app`) con el servidor nuevo. Si nada relevante cambio, recargar tiraria sin
/// necesidad el token bearer en memoria y el carrito en curso del cajero (ver `entornoTauri.ts`),
/// por eso alcanza con `show()` + `set_focus()`.
pub(crate) fn mostrar_ventana_pos(app: &tauri::AppHandle) -> tauri::Result<()> {
    if let Some(configuracion) = app.get_webview_window(ETIQUETA_VENTANA_CONFIGURACION) {
        let _ = configuracion.hide();
    }

    let url_actual = config::leer(app).map(|c| c.url_servidor);
    let estado = app.state::<UrlServidorCargadaEnPos>();
    let mut url_cargada = estado.0.lock().unwrap();

    if let Some(pos) = app.get_webview_window(ETIQUETA_VENTANA_POS) {
        if necesita_recargar_pos(url_cargada.as_deref(), url_actual.as_deref()) {
            // reload() nativo y no eval(): la CSP declara script-src 'self' sin unsafe-eval,
            // asi que un script inyectado en la pagina podria quedar bloqueado segun como
            // WebView2 aplique la politica. La API nativa no inyecta nada.
            pos.reload()?;
        }
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

    *url_cargada = url_actual;
    Ok(())
}

/// Decide si hace falta recargar la ventana `pos` antes de mostrarla: solo cuando la URL del
/// servidor cambio desde la ultima vez que se mostro (incluyendo pasar de "sin configurar" a
/// configurada, o viceversa). Funcion pura para poder testearla sin `AppHandle` -- ver los tests
/// de abajo.
fn necesita_recargar_pos(url_anterior: Option<&str>, url_actual: Option<&str>) -> bool {
    url_anterior != url_actual
}

/// Vuelve a mostrar la ventana `main` (pagina LOCAL bundleada de configuracion, ver
/// `capabilities/configuracion.json`) y oculta la del POS si existe. Igual que
/// `mostrar_ventana_pos`: si la ventana `main` ya existe se la recarga siempre (la pagina de
/// configuracion solo lee su estado -- `leer_configuracion`, `info_app` -- en `DOMContentLoaded`,
/// que no vuelve a disparar si la ventana ya existia y solo se la vuelve a mostrar); y si NO
/// existe -- Tauri la destruye al cerrarla con el boton nativo "X" (no hay
/// `prevent_close`/`CloseRequested` en este crate, asi que nunca hay que asumir que sigue viva) --
/// se la reconstruye con `WebviewWindowBuilder`, igual que hace `mostrar_ventana_pos` con `pos`.
pub(crate) fn mostrar_ventana_configuracion(app: &tauri::AppHandle) -> tauri::Result<()> {
    if let Some(pos) = app.get_webview_window(ETIQUETA_VENTANA_POS) {
        let _ = pos.hide();
    }

    if let Some(configuracion) = app.get_webview_window(ETIQUETA_VENTANA_CONFIGURACION) {
        // Idem: reload() nativo, no eval() — ver el comentario en mostrar_ventana_pos.
        configuracion.reload()?;
        configuracion.show()?;
        configuracion.set_focus()?;
    } else {
        WebviewWindowBuilder::new(
            app,
            ETIQUETA_VENTANA_CONFIGURACION,
            WebviewUrl::App("index.html".into()),
        )
        .title("Ways POS")
        .maximized(true)
        .min_inner_size(1024.0, 700.0)
        .resizable(true)
        .build()?;
    }

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    /// judgment-day ronda 1 (FIX 4): `necesita_recargar_pos` es la clausula pura que decide si
    /// `mostrar_ventana_pos` recarga la pagina -- cubre los cuatro casos (igual/distinta URL,
    /// aparece/desaparece la configuracion) para que ninguno pueda mutarse sin romper un assert.
    #[test]
    fn necesita_recargar_pos_solo_cuando_la_url_cambia() {
        assert!(!necesita_recargar_pos(
            Some("https://empresa.aipos.site"),
            Some("https://empresa.aipos.site")
        ));
        assert!(necesita_recargar_pos(
            Some("https://empresa.aipos.site"),
            Some("https://otra-empresa.aipos.site")
        ));
        assert!(necesita_recargar_pos(
            None,
            Some("https://empresa.aipos.site")
        ));
        assert!(necesita_recargar_pos(
            Some("https://empresa.aipos.site"),
            None
        ));
        assert!(!necesita_recargar_pos(None, None));
    }

    /// judgment-day ronda 1 (FIX 1): antes, `mostrar_ventana_configuracion` hacia
    /// `.get_webview_window(...).expect(...)` sobre la ventana `main`. Como Tauri DESTRUYE una
    /// ventana al cerrarla con el boton nativo "X" (no hay `prevent_close`/`CloseRequested` en
    /// este crate), cualquier llamado posterior a esa funcion (el atajo global o "Configuración"
    /// desde el POS) paniqueaba la app entera, de forma permanente.
    ///
    /// No se puede armar un `tauri::AppHandle` real (`Wry`) en un test unitario para ejercer ese
    /// camino de punta a punta: ni `mostrar_ventana_pos` ni `mostrar_ventana_configuracion` son
    /// genericas sobre `R: Runtime` (mismo limite ya documentado abajo, en el test de
    /// capacidades, para `tauri::test::MockRuntime`), y crear una ventana `Wry` real exige un
    /// entorno de GUI que no es razonable levantar en un test automatizado.
    ///
    /// Lo que ESTA prueba prueba, en cambio, en el codigo fuente tal cual esta en disco: que
    /// ninguna busqueda de ventana por handle (`get_webview_window`) tiene un `.expect(` ni un
    /// `.unwrap(` encadenado -- la fuente concreta del panic de esta ronda. Solo se analiza el
    /// codigo de PRODUCCION (todo lo que esta antes de `#[cfg(test)]`): de lo contrario, el texto
    /// crudo de esta misma prueba (que menciona `get_webview_window(` y `.expect(` en sus propios
    /// strings/comentarios para poder chequearlos) se detectaria a si mismo. Limite deliberado:
    /// esta prueba no puede probar que el camino "reconstruir la ventana" funciona en runtime,
    /// solo que la clausula que paniqueaba ya no esta en el archivo.
    #[test]
    fn ninguna_busqueda_de_ventana_usa_expect_o_unwrap() {
        let fuente = include_str!("lib.rs");
        let codigo_de_produccion = fuente
            .split_once("#[cfg(test)]")
            .map(|(produccion, _resto)| produccion)
            .unwrap_or(fuente);

        for statement in codigo_de_produccion.split(';') {
            if statement.contains("get_webview_window(") {
                assert!(
                    !statement.contains(".expect(") && !statement.contains(".unwrap("),
                    "no debe haber .expect()/.unwrap() encadenado a get_webview_window(): {statement}"
                );
            }
        }
    }

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
    ///
    /// judgment-day ronda 1 (FIX 6): la pregunta de fondo detras de este split es si `core:window`
    /// (parte de `core:default`, otorgado por las dos capacidades) deja que la ventana de UNA le
    /// de show/hide/close/focus a la OTRA a pesar del split de `windows`. Resuelto de forma
    /// definitiva contra la fuente real de `tauri` 2.11.5 (crate vendorizado en
    /// `~/.cargo/registry/src/.../tauri-2.11.5/src/window/plugin.rs`, no solo el schema):
    ///
    /// 1. TODOS los comandos generados por los macros `getter!`/`setter!` de ese plugin (incluidos
    ///    `show`, `hide`, `close`, `set_focus`, `destroy`, `minimize`, `maximize`, ...) aceptan un
    ///    `label: Option<String>` y, si viene un label no vacio, `get_window` busca ESA ventana con
    ///    `window.manager().get_window(&l)` -- ignorando por completo desde que ventana se invoco
    ///    el comando. El split de `windows` en las capacidades NO restringe ese argumento: solo
    ///    controla que ventana puede invocar el permiso, no a que ventana apunta despues.
    /// 2. PERO `core:window:default` (lo que realmente trae `core:default`, confirmado contra
    ///    `permissions/window/autogenerated/reference.md` del mismo crate) NO incluye `allow-show`,
    ///    `allow-hide`, `allow-close`, `allow-set-focus`, `allow-destroy`, ni ningun
    ///    minimize/maximize -- esos exigen su propio `core:window:allow-*` explicito, y NINGUNA de
    ///    las dos capacidades de este proyecto lo otorga. La preocupacion del punto 1 (una ventana
    ///    mostrando/ocultando/cerrando/enfocando a la otra) NO es explotable con la configuracion
    ///    actual.
    /// 3. Lo que `core:window:default` SI otorga y SI acepta `label` cruzado: getters de solo
    ///    lectura (posicion/tamano/titulo/monitor/tema/flags is_visible|is_focused|is_maximized|...)
    ///    y un unico comando que muta estado, `internal_toggle_maximize` (el equivalente a
    ///    doble-click en la barra de titulo). Ninguno de los dos frontends de esta app invoca hoy
    ///    ningun comando `core:window` (`rg` sobre `Ways.Desktop` no encuentra ningun uso de
    ///    `@tauri-apps/api/window` ni de `internal_toggle_maximize`) -- es superficie otorgada pero
    ///    dormida, no algo que el codigo propio ejercite.
    /// No se angosta `core:default` en esta ronda: hacerlo bien exigiria enumerar a mano el resto
    /// de los permisos que si hacen falta (`core:path`, `core:event`, `core:webview`, `core:app`,
    /// `core:image`, `core:resources`, `core:menu`, `core:tray`) sin poder probarlo contra un
    /// runtime real en este entorno, mas riesgo que el que justifica esta severidad (SUGGESTION).
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
