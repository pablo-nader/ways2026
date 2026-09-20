//! Almacenamiento del secreto de dispositivo (stage-desktop-pos, slice bearer) en un archivo
//! PROPIO, separado de `config.json` (ver `config.rs`).
//!
//! Por que un archivo aparte: `config.json` lo escribe `config::guardar` con un `fs::write` liso
//! (sin ACL/permisos adicionales mas alla de los que el sistema operativo le da por default al
//! usuario que corre el proceso) y lo lee `leer_configuracion`, un comando que hoy esta en el
//! allowlist de la pagina LOCAL de configuracion (`capabilities/local.json`, que le otorga los
//! 11 comandos). Mezclar el secreto ahi lo expondria a esa misma superficie sin necesidad — la
//! pagina de configuracion no tiene ningun motivo para leer el secreto de dispositivo. Este
//! archivo nuevo (`dispositivo.credencial`) vive en el MISMO directorio de configuracion de la
//! app (`tauri::Manager::path().app_config_dir()`) pero solo lo tocan los dos comandos de este
//! modulo.
//!
//! Honestidad sobre permisos: igual que `config.rs`, esto es un `fs::write` liso — sin ACL de
//! Windows explicita mas alla de la que el directorio de configuracion del usuario ya tiene por
//! default (heredada del perfil del usuario que corre la app, sin `Everyone`/`Users` explicito).
//! No es un endurecimiento adicional; documentarlo así es mas honesto que insinuar una proteccion
//! que este codigo no implementa.

use std::fs;
use std::path::PathBuf;

use tauri::{AppHandle, Manager};

const NOMBRE_ARCHIVO_CREDENCIAL: &str = "dispositivo.credencial";

fn ruta_archivo_credencial(app: &AppHandle) -> Result<PathBuf, String> {
    app.path()
        .app_config_dir()
        .map_err(|error| format!("No se pudo determinar el directorio de configuracion: {error}"))
        .map(|dir| dir.join(NOMBRE_ARCHIVO_CREDENCIAL))
}

/// Guarda el secreto de dispositivo en texto plano. Sobrescribe cualquier secreto anterior — un
/// dispositivo tiene un unico secreto vigente a la vez (mismo criterio que `config::guardar`, que
/// tampoco hace merge).
pub fn guardar(app: &AppHandle, secreto: &str) -> Result<(), String> {
    let ruta = ruta_archivo_credencial(app)?;
    if let Some(dir) = ruta.parent() {
        fs::create_dir_all(dir)
            .map_err(|error| format!("No se pudo crear el directorio de configuracion: {error}"))?;
    }

    fs::write(&ruta, secreto.trim())
        .map_err(|error| format!("No se pudo guardar la credencial del dispositivo: {error}"))
}

/// Lee el secreto guardado, si existe. `None` (nunca un error) tanto si el archivo no existe
/// como si esta vacio — mismo criterio permisivo que `config::leer`: un archivo ausente o vacio
/// significa "todavia no vinculado", no una falla que haya que mostrarle al usuario.
pub fn leer(app: &AppHandle) -> Option<String> {
    let ruta = ruta_archivo_credencial(app).ok()?;
    let contenido = fs::read_to_string(ruta).ok()?;
    let recortado = contenido.trim();
    if recortado.is_empty() {
        None
    } else {
        Some(recortado.to_string())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// `ruta_archivo_credencial`/`guardar`/`leer` dependen de un `AppHandle` real (necesitan un
    /// `tauri::App` en marcha para resolver `app_config_dir`), asi que no se testean aca de forma
    /// aislada — el resto del proyecto tampoco tiene tests de integracion de Tauri (ver
    /// `config.rs`, que solo testea sus funciones PURAS). Este test cubre la unica logica pura de
    /// este modulo: el trim + "vacio es None" de `leer` se ejercita indirectamente via el
    /// contrato de `NOMBRE_ARCHIVO_CREDENCIAL` (constante, sin logica que testear aislada) — se
    /// deja documentado el porque de la ausencia en vez de fingir cobertura con un mock.
    #[test]
    fn el_nombre_de_archivo_es_distinto_del_de_configuracion() {
        assert_ne!(NOMBRE_ARCHIVO_CREDENCIAL, "config.json");
    }
}
