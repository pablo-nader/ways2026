//! Lectura, validacion y persistencia de la configuracion local de la app.
//!
//! La configuracion vive en un archivo JSON dentro del directorio de
//! configuracion de la aplicacion (ver `tauri::Manager::path`). Las
//! funciones de validacion son puras para poder testearlas sin runtime.

use std::fs;
use std::path::PathBuf;

use serde::{Deserialize, Serialize};
use tauri::{AppHandle, Manager};
use url::Url;

const NOMBRE_ARCHIVO_CONFIG: &str = "config.json";

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq, Default)]
pub struct Configuracion {
    pub url_servidor: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub impresora: Option<String>,
}

/// Valida y normaliza la URL del servidor ingresada por el usuario.
///
/// Reglas: esquema `https` con un dominio valido, o esquema `http` con host
/// exactamente `localhost` o `127.0.0.1` (para desarrollo). No se permiten
/// credenciales (`usuario@`), query string, fragmento ni ruta (solo el
/// origen). El host solo puede contener `[A-Za-z0-9.-]`. El resultado se
/// normaliza a `esquema://host[:puerto]` en minusculas y sin barra final.
pub fn normalizar_url_servidor(entrada: &str) -> Result<String, String> {
    let recortada = entrada.trim();

    if recortada.is_empty() {
        return Err("La URL del servidor no puede estar vacia.".to_string());
    }

    let url = Url::parse(recortada).map_err(|_| "La URL del servidor no es valida.".to_string())?;

    let host = url
        .host_str()
        .ok_or_else(|| "La URL del servidor no es valida: falta el dominio.".to_string())?;
    let host_normalizado = host.to_lowercase();

    let host_valido = !host_normalizado.is_empty()
        && host_normalizado
            .chars()
            .all(|c| c.is_ascii_alphanumeric() || c == '.' || c == '-');
    if !host_valido {
        return Err(
            "La URL del servidor no es valida: el dominio contiene caracteres no permitidos."
                .to_string(),
        );
    }

    let esquema = url.scheme();
    let es_https = esquema == "https";
    let es_localhost_dev = esquema == "http"
        && (host_normalizado == "localhost" || host_normalizado == "127.0.0.1");

    if !es_https && !es_localhost_dev {
        return Err(
            "La URL del servidor debe comenzar con https:// (o http://localhost para desarrollo)."
                .to_string(),
        );
    }

    if !url.username().is_empty() || url.password().is_some() {
        return Err("La URL del servidor no puede incluir credenciales.".to_string());
    }

    if url.query().is_some() {
        return Err("La URL del servidor no puede incluir parametros de consulta.".to_string());
    }

    if url.fragment().is_some() {
        return Err("La URL del servidor no puede incluir un fragmento.".to_string());
    }

    let ruta = url.path();
    if !ruta.is_empty() && ruta != "/" {
        return Err("La URL del servidor no puede incluir una ruta.".to_string());
    }

    let puerto = url.port().map(|p| format!(":{p}")).unwrap_or_default();
    Ok(format!("{esquema}://{host_normalizado}{puerto}"))
}

/// Arma la URL completa de la pagina POS a partir del servidor configurado.
pub fn url_pos(url_servidor: &str) -> String {
    format!("{url_servidor}/pos.html")
}

/// Arma la URL local de la pagina de configuracion empaquetada (`ui/index.html`).
///
/// Replica la resolucion que Tauri hace internamente para `WebviewUrl::App`
/// cuando, como en este proyecto, no hay `build.devUrl` ni `build.frontendDist`
/// remoto configurados (ver `tauri.conf.json`): en Windows/Android usa
/// `http(s)://tauri.localhost/` (segun `useHttpsScheme`), y en el resto de
/// plataformas usa `tauri://localhost/`. Al ser una funcion pura derivada de
/// la configuracion, evita depender de una URL capturada en tiempo de
/// ejecucion (que puede no reflejar aun la navegacion real de la ventana).
pub fn url_configuracion_local(es_windows_o_android: bool, usa_https: bool) -> String {
    if es_windows_o_android {
        let esquema = if usa_https { "https" } else { "http" };
        format!("{esquema}://tauri.localhost/")
    } else {
        "tauri://localhost/".to_string()
    }
}

fn ruta_archivo_configuracion(app: &AppHandle) -> Result<PathBuf, String> {
    app.path()
        .app_config_dir()
        .map_err(|error| format!("No se pudo determinar el directorio de configuracion: {error}"))
        .map(|dir| dir.join(NOMBRE_ARCHIVO_CONFIG))
}

/// Lee la configuracion persistida y revalida la URL del servidor.
///
/// Si el archivo esta ausente, corrupto, o contiene una URL que ya no pasa
/// las reglas de `normalizar_url_servidor` (por ejemplo, editado a mano o
/// persistido por una version anterior menos estricta), se devuelve `None`
/// para que la app muestre la pagina local de configuracion en vez de usar
/// un valor invalido.
pub fn leer(app: &AppHandle) -> Option<Configuracion> {
    let ruta = ruta_archivo_configuracion(app).ok()?;
    let contenido = fs::read_to_string(ruta).ok()?;
    let config: Configuracion = serde_json::from_str(&contenido).ok()?;
    let url_servidor = normalizar_url_servidor(&config.url_servidor).ok()?;
    Some(Configuracion {
        url_servidor,
        impresora: config.impresora,
    })
}

pub fn guardar(app: &AppHandle, config: &Configuracion) -> Result<(), String> {
    let url_normalizada = normalizar_url_servidor(&config.url_servidor)?;
    let config = Configuracion {
        url_servidor: url_normalizada,
        impresora: config.impresora.clone(),
    };

    let ruta = ruta_archivo_configuracion(app)?;
    if let Some(dir) = ruta.parent() {
        fs::create_dir_all(dir)
            .map_err(|error| format!("No se pudo crear el directorio de configuracion: {error}"))?;
    }

    let json = serde_json::to_string_pretty(&config)
        .map_err(|error| format!("No se pudo serializar la configuracion: {error}"))?;

    fs::write(&ruta, json)
        .map_err(|error| format!("No se pudo guardar la configuracion: {error}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn acepta_https_y_recorta_barra_final() {
        let resultado = normalizar_url_servidor("https://empresa.aipos.site/").unwrap();
        assert_eq!(resultado, "https://empresa.aipos.site");
    }

    #[test]
    fn acepta_http_localhost_para_desarrollo() {
        let resultado = normalizar_url_servidor("http://localhost:5173/").unwrap();
        assert_eq!(resultado, "http://localhost:5173");
    }

    #[test]
    fn acepta_http_127_0_0_1() {
        let resultado = normalizar_url_servidor("http://127.0.0.1:5173").unwrap();
        assert_eq!(resultado, "http://127.0.0.1:5173");
    }

    #[test]
    fn recorta_espacios_en_blanco() {
        let resultado = normalizar_url_servidor("  https://empresa.aipos.site  ").unwrap();
        assert_eq!(resultado, "https://empresa.aipos.site");
    }

    #[test]
    fn rechaza_url_vacia() {
        assert!(normalizar_url_servidor("").is_err());
        assert!(normalizar_url_servidor("   ").is_err());
    }

    #[test]
    fn rechaza_http_no_localhost() {
        let error = normalizar_url_servidor("http://empresa.aipos.site").unwrap_err();
        assert!(error.contains("https://"));
    }

    #[test]
    fn rechaza_esquema_desconocido() {
        assert!(normalizar_url_servidor("ftp://empresa.aipos.site").is_err());
    }

    #[test]
    fn rechaza_https_sin_dominio() {
        assert!(normalizar_url_servidor("https://").is_err());
    }

    #[test]
    fn rechaza_localhost_con_sufijo_de_dominio() {
        assert!(normalizar_url_servidor("http://localhost.evil.com").is_err());
    }

    #[test]
    fn rechaza_localhost_con_userinfo_apuntando_a_otro_host() {
        assert!(normalizar_url_servidor("http://localhost@evil.com").is_err());
    }

    #[test]
    fn rechaza_127_0_0_1_con_sufijo_de_dominio() {
        assert!(normalizar_url_servidor("http://127.0.0.1.evil.com").is_err());
    }

    #[test]
    fn rechaza_host_comodin() {
        assert!(normalizar_url_servidor("https://*").is_err());
        assert!(normalizar_url_servidor("https://*.evil.com").is_err());
    }

    #[test]
    fn rechaza_credenciales_en_https() {
        assert!(normalizar_url_servidor("https://usuario:clave@empresa.aipos.site").is_err());
    }

    #[test]
    fn rechaza_query_string() {
        assert!(normalizar_url_servidor("https://empresa.aipos.site/?redir=evil.com").is_err());
    }

    #[test]
    fn rechaza_fragmento() {
        assert!(normalizar_url_servidor("https://empresa.aipos.site/#evil").is_err());
    }

    #[test]
    fn rechaza_ruta_no_vacia() {
        assert!(normalizar_url_servidor("https://empresa.aipos.site/algo").is_err());
    }

    #[test]
    fn normaliza_host_a_minusculas() {
        let resultado = normalizar_url_servidor("https://EMPRESA.AIPOS.SITE").unwrap();
        assert_eq!(resultado, "https://empresa.aipos.site");
    }

    #[test]
    fn arma_url_de_pos() {
        assert_eq!(
            url_pos("https://empresa.aipos.site"),
            "https://empresa.aipos.site/pos.html"
        );
    }

    #[test]
    fn arma_url_local_http_en_windows_por_defecto() {
        assert_eq!(
            url_configuracion_local(true, false),
            "http://tauri.localhost/"
        );
    }

    #[test]
    fn arma_url_local_https_en_windows_si_esta_habilitado() {
        assert_eq!(
            url_configuracion_local(true, true),
            "https://tauri.localhost/"
        );
    }

    #[test]
    fn arma_url_local_con_esquema_tauri_fuera_de_windows_o_android() {
        assert_eq!(url_configuracion_local(false, false), "tauri://localhost/");
        assert_eq!(url_configuracion_local(false, true), "tauri://localhost/");
    }
}
