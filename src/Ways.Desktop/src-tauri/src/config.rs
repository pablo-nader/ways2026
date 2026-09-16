//! Lectura, validacion y persistencia de la configuracion local de la app.
//!
//! La configuracion vive en un archivo JSON dentro del directorio de
//! configuracion de la aplicacion (ver `tauri::Manager::path`). Las
//! funciones de validacion son puras para poder testearlas sin runtime.

use std::fs;
use std::path::PathBuf;

use serde::{Deserialize, Serialize};
use tauri::{AppHandle, Manager};

const NOMBRE_ARCHIVO_CONFIG: &str = "config.json";

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq, Default)]
pub struct Configuracion {
    pub url_servidor: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub impresora: Option<String>,
}

/// Valida y normaliza la URL del servidor ingresada por el usuario.
///
/// Reglas: debe comenzar con `https://`, o con `http://localhost` /
/// `http://127.0.0.1` (para desarrollo). Se recorta la barra final.
pub fn normalizar_url_servidor(entrada: &str) -> Result<String, String> {
    let recortada = entrada.trim();

    if recortada.is_empty() {
        return Err("La URL del servidor no puede estar vacia.".to_string());
    }

    let es_https = recortada.starts_with("https://");
    let es_localhost_dev =
        recortada.starts_with("http://localhost") || recortada.starts_with("http://127.0.0.1");

    if !es_https && !es_localhost_dev {
        return Err(
            "La URL del servidor debe comenzar con https:// (o http://localhost para desarrollo)."
                .to_string(),
        );
    }

    let sin_barra_final = recortada.trim_end_matches('/');

    let prefijo_len = if es_https { "https://".len() } else { "http://".len() };
    if sin_barra_final.len() <= prefijo_len {
        return Err("La URL del servidor no es valida: falta el dominio.".to_string());
    }

    Ok(sin_barra_final.to_string())
}

/// Arma la URL completa de la pagina POS a partir del servidor configurado.
pub fn url_pos(url_servidor: &str) -> String {
    format!("{url_servidor}/pos.html")
}

fn ruta_archivo_configuracion(app: &AppHandle) -> Result<PathBuf, String> {
    app.path()
        .app_config_dir()
        .map_err(|error| format!("No se pudo determinar el directorio de configuracion: {error}"))
        .map(|dir| dir.join(NOMBRE_ARCHIVO_CONFIG))
}

pub fn leer(app: &AppHandle) -> Option<Configuracion> {
    let ruta = ruta_archivo_configuracion(app).ok()?;
    let contenido = fs::read_to_string(ruta).ok()?;
    serde_json::from_str(&contenido).ok()
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
    fn arma_url_de_pos() {
        assert_eq!(
            url_pos("https://empresa.aipos.site"),
            "https://empresa.aipos.site/pos.html"
        );
    }
}
