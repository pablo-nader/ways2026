//! Almacenamiento del token de sesión del cajero (stage-pos-sesion-offline) en un archivo PROPIO,
//! separado tanto de `config.json` (`config.rs`) como de `dispositivo.credencial`
//! (`credencial.rs`).
//!
//! Qué guarda y por qué separado: el secreto de `credencial.rs` identifica el DISPOSITIVO (la
//! caja registradora, vigente durante toda la vida del equipo); este archivo identifica a la
//! PERSONA logueada en ese dispositivo en este momento (el token bearer que emite
//! `POST /auth/login-dispositivo` con `solicitarBearer: true`, más su vencimiento explícito
//! `expiraEl` — ver `AuthEndpoints.SesionDeDispositivoConBearer` del lado del servidor). Mezclar
//! los dos en un solo archivo acoplaría dos ciclos de vida completamente distintos (el
//! dispositivo sobrevive a todos los cajeros que lo usan; la sesión de un cajero termina en el
//! logout, en un cierre de turno, o cuando el servidor la revoca) — separar el archivo hace que
//! limpiar uno nunca pueda pisar el otro por accidente.
//!
//! Vigente un solo par (token, vencimiento) a la vez, igual que `credencial::guardar`: no hay
//! merge, cada `guardar` reemplaza lo anterior entero.
//!
//! El vencimiento (`expira_el`) viaja tal cual lo entregó el servidor (una fecha ISO-8601 en
//! texto) — este módulo nunca lo parsea ni lo compara contra ningún reloj: decidir si ya pasó es
//! responsabilidad del lado de TypeScript (`entornoTauri.ts`, `sesionPersistidaEsValida`), que sí
//! tiene el reloj del sistema a mano sin necesitar otro viaje a Rust. Este archivo es un
//! almacenamiento tonto, igual que `credencial.rs`.
//!
//! Comando de limpieza: NO hay un tercer comando `limpiar_sesion_de_cajero`. Los tres
//! disparadores que exigen borrar la sesión persistida (logout explícito, un 401 del servidor,
//! cierre de turno — ver `entornoTauri.ts` y sus llamadores del lado de React) reusan
//! `guardar_sesion_de_cajero` con `token`/`expira_el` vacíos: `leer` ya trata un token vacío como
//! "no hay nada" (mismo criterio que `credencial::leer`), así que no hace falta un camino de
//! código nuevo ni un permiso nuevo solo para limpiar — menos superficie otorgada en
//! `capabilities/pos.json` que agregar un tercer comando con su propio permiso `allow-*` para una
//! operación que ya es expresable con la que existe.
//!
//! Honestidad sobre permisos: mismo criterio que `credencial.rs` — `fs::write` liso, sin ACL de
//! Windows explícita más allá de la que el directorio de configuración del usuario ya tiene por
//! default (heredada del perfil del usuario que corre la app).

use std::fs;
use std::path::PathBuf;

use serde::{Deserialize, Serialize};
use tauri::{AppHandle, Manager};

const NOMBRE_ARCHIVO_SESION: &str = "sesion.credencial";

/// Token bearer de sesión del cajero + su vencimiento explícito, tal cual los devuelve
/// `POST /auth/login-dispositivo`. `Serialize` (para que `leer_sesion_de_cajero` lo cruce por IPC
/// hacia React) y `Deserialize` (para que `guardar_sesion_de_cajero` lo reciba desde React) — sin
/// `rename_all`, los nombres de campo cruzan tal cual (`token`, `expira_el`), mismo criterio que
/// `config::Configuracion`.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct SesionDeCajero {
    pub token: String,
    pub expira_el: String,
}

fn ruta_archivo_sesion(app: &AppHandle) -> Result<PathBuf, String> {
    app.path()
        .app_config_dir()
        .map_err(|error| format!("No se pudo determinar el directorio de configuracion: {error}"))
        .map(|dir| dir.join(NOMBRE_ARCHIVO_SESION))
}

/// Arma el contenido plano de dos líneas (token, vencimiento) que persiste este archivo —
/// separada de `guardar` para poder testearla junto con `analizar` sin un `AppHandle` real.
fn serializar(sesion: &SesionDeCajero) -> String {
    format!("{}\n{}", sesion.token.trim(), sesion.expira_el.trim())
}

/// Guarda el token de sesión del cajero + su vencimiento explícito. Sobrescribe cualquier sesión
/// anterior — un dispositivo tiene una sola sesión de cajero vigente a la vez (mismo criterio que
/// `credencial::guardar`). Guardar con `token`/`expira_el` vacíos ES el camino de limpieza (ver
/// el doc-comment del módulo): no hay un comando separado para eso.
pub fn guardar(app: &AppHandle, sesion: &SesionDeCajero) -> Result<(), String> {
    let ruta = ruta_archivo_sesion(app)?;
    if let Some(dir) = ruta.parent() {
        fs::create_dir_all(dir)
            .map_err(|error| format!("No se pudo crear el directorio de configuracion: {error}"))?;
    }

    fs::write(&ruta, serializar(sesion))
        .map_err(|error| format!("No se pudo guardar la sesion del cajero: {error}"))
}

/// Parsea el contenido crudo del archivo — pura, sin `AppHandle`, para poder testear el criterio
/// "token vacío es None" (mismo criterio permisivo que `credencial::leer`) sin depender de un
/// runtime de Tauri real.
fn analizar(contenido: &str) -> Option<SesionDeCajero> {
    let mut lineas = contenido.lines();
    let token = lineas.next().unwrap_or("").trim();
    if token.is_empty() {
        return None;
    }
    let expira_el = lineas.next().unwrap_or("").trim();
    Some(SesionDeCajero {
        token: token.to_string(),
        expira_el: expira_el.to_string(),
    })
}

/// Lee la sesión guardada, si hay una. `None` (nunca un error) tanto si el archivo no existe,
/// está vacío, o el token quedó vacío tras recortar espacios — mismo criterio permisivo que
/// `credencial::leer`. La validez del vencimiento NO se decide acá (ver el doc-comment del
/// módulo): este comando devuelve lo que hay en disco tal cual, sin comparar contra ningún reloj.
pub fn leer(app: &AppHandle) -> Option<SesionDeCajero> {
    let ruta = ruta_archivo_sesion(app).ok()?;
    let contenido = fs::read_to_string(ruta).ok()?;
    analizar(&contenido)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Mismo motivo documentado en `credencial.rs`: `ruta_archivo_sesion`/`guardar`/`leer`
    /// dependen de un `AppHandle` real (necesitan un `tauri::App` en marcha para resolver
    /// `app_config_dir`) y no se testean acá de forma aislada. `analizar`/`serializar` (la lógica
    /// pura de parseo/armado) sí se testean, abajo — es una cobertura mejor que la de
    /// `credencial.rs`, que no pudo extraer esa lógica a una función propia sin cambiar su
    /// comportamiento existente (fuera de alcance de esta tarea).
    #[test]
    fn el_nombre_de_archivo_es_distinto_de_los_otros_dos() {
        assert_ne!(NOMBRE_ARCHIVO_SESION, "config.json");
        assert_ne!(NOMBRE_ARCHIVO_SESION, "dispositivo.credencial");
    }

    /// Clausula bajo prueba: `if token.is_empty() { return None }` en `analizar` — un token vacío
    /// (incluida una limpieza deliberada, ver el doc-comment del módulo) es "no hay sesión", nunca
    /// `Some` con un token vacío.
    ///
    /// Mutación probada a mano (ver el reporte de la tarea para la evidencia real observada):
    /// comentar ese `if` hace que este test falle (`analizar("")` pasa a devolver
    /// `Some(SesionDeCajero { token: "", expira_el: "" })` en vez de `None`) — revertido, vuelve
    /// a pasar.
    #[test]
    fn token_vacio_es_none_aunque_haya_una_segunda_linea() {
        assert_eq!(analizar("\n2026-01-01T00:00:00Z"), None);
        assert_eq!(analizar(""), None);
        assert_eq!(analizar("   \n2026-01-01T00:00:00Z"), None);
    }

    #[test]
    fn token_y_vencimiento_presentes_se_leen_completos_y_recortados() {
        assert_eq!(
            analizar("  token-x  \n  2026-01-01T00:00:00Z  "),
            Some(SesionDeCajero {
                token: "token-x".to_string(),
                expira_el: "2026-01-01T00:00:00Z".to_string()
            })
        );
    }

    /// Sin segunda línea (archivo truncado/corrupto a mano) el token sigue siendo válido pero el
    /// vencimiento queda vacío — TypeScript lo trata como vencido (ver `sesionPersistidaEsValida`
    /// en `entornoTauri.ts`), nunca como "sin vencimiento" implícito.
    #[test]
    fn sin_segunda_linea_el_vencimiento_queda_vacio() {
        assert_eq!(
            analizar("token-x"),
            Some(SesionDeCajero {
                token: "token-x".to_string(),
                expira_el: String::new()
            })
        );
    }

    #[test]
    fn serializar_guarda_token_y_vencimiento_en_dos_lineas_recortadas() {
        let sesion = SesionDeCajero {
            token: "  token-x  ".to_string(),
            expira_el: "  2026-01-01T00:00:00Z  ".to_string(),
        };
        assert_eq!(serializar(&sesion), "token-x\n2026-01-01T00:00:00Z");
    }

    /// Round-trip puro (sin `AppHandle`): lo que `serializar` produce, `analizar` lo reconstruye
    /// igual — cubre el camino real de `guardar` → `leer` sin necesitar el filesystem.
    #[test]
    fn round_trip_serializar_analizar() {
        let sesion = SesionDeCajero {
            token: "token-x".to_string(),
            expira_el: "2026-01-01T00:00:00Z".to_string(),
        };
        assert_eq!(analizar(&serializar(&sesion)), Some(sesion));
    }

    /// Limpieza (ver el doc-comment del módulo): serializar la sesión "vacía" que usan los tres
    /// disparadores de limpieza produce contenido que `analizar` lee como `None` — el mismo
    /// camino que un archivo recién creado o nunca escrito.
    #[test]
    fn serializar_de_una_sesion_vacia_se_lee_como_none() {
        let vacia = SesionDeCajero {
            token: String::new(),
            expira_el: String::new(),
        };
        assert_eq!(analizar(&serializar(&vacia)), None);
    }
}
