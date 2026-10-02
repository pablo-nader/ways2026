//! Auto-actualizacion de la app de escritorio con `tauri-plugin-updater`.
//!
//! Un hilo propio busca una version nueva poco despues del arranque y luego cada
//! `INTERVALO_ENTRE_BUSQUEDAS`; si hay una, la descarga y verifica su firma en segundo plano y
//! avisa a las ventanas con el evento `EVENTO_ACTUALIZACION_DESCARGADA`. Nunca instala sola: la
//! instalacion (que cierra la app y la reabre) solo ocurre cuando el cajero la pide desde el POS
//! con `instalar_actualizacion`, y el POS no lo permite con una venta en curso.

use std::sync::Mutex;
use std::thread;
use std::time::Duration;

use serde::Serialize;
use tauri::{AppHandle, Emitter, Manager};
use tauri_plugin_updater::{Update, UpdaterExt};

pub const EVENTO_ACTUALIZACION_DESCARGADA: &str = "actualizacion-descargada";

const DEMORA_PRIMERA_BUSQUEDA: Duration = Duration::from_secs(60);
const INTERVALO_ENTRE_BUSQUEDAS: Duration = Duration::from_secs(6 * 60 * 60);
const TIMEOUT_BUSQUEDA: Duration = Duration::from_secs(30);
// Una conexion lenta puede tardar varios minutos en bajar el instalador: el timeout corto de la
// busqueda no puede aplicarse a la descarga.
const TIMEOUT_DESCARGA: Duration = Duration::from_secs(30 * 60);

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct ActualizacionDisponible {
    pub version: String,
    pub notas: Option<String>,
}

struct Descarga<T> {
    info: ActualizacionDisponible,
    paquete: T,
}

/// Estado compartido entre el hilo de busqueda y los comandos. Generico sobre el paquete
/// descargado para poder probar las transiciones sin un `Update` real.
pub struct Estado<T> {
    descarga: Option<Descarga<T>>,
    instalando: bool,
}

impl<T> Default for Estado<T> {
    fn default() -> Self {
        Self {
            descarga: None,
            instalando: false,
        }
    }
}

#[derive(Debug, PartialEq, Eq)]
pub enum ErrorAlTomar {
    SinActualizacion,
    YaInstalando,
}

impl<T: Clone> Estado<T> {
    pub fn disponible(&self) -> Option<ActualizacionDisponible> {
        self.descarga.as_ref().map(|d| d.info.clone())
    }

    /// Solo hace falta descargar si la version ofrecida no es la que ya esta descargada.
    pub fn debe_descargar(&self, version_ofrecida: &str) -> bool {
        self.descarga
            .as_ref()
            .map_or(true, |d| d.info.version != version_ofrecida)
    }

    pub fn registrar_descarga(&mut self, info: ActualizacionDisponible, paquete: T) {
        self.descarga = Some(Descarga { info, paquete });
    }

    /// Reserva la descarga para instalarla. Un segundo pedido mientras el primero sigue en curso
    /// se rechaza en vez de lanzar dos instaladores.
    pub fn tomar_para_instalar(&mut self) -> Result<T, ErrorAlTomar> {
        if self.instalando {
            return Err(ErrorAlTomar::YaInstalando);
        }
        let paquete = self
            .descarga
            .as_ref()
            .map(|d| d.paquete.clone())
            .ok_or(ErrorAlTomar::SinActualizacion)?;
        self.instalando = true;
        Ok(paquete)
    }

    /// Libera la reserva cuando la instalacion fallo, para poder reintentarla.
    pub fn instalacion_fallida(&mut self) {
        self.instalando = false;
    }
}

pub type EstadoDeActualizacion = Mutex<Estado<(Update, Vec<u8>)>>;

pub fn iniciar_busqueda_periodica(app: AppHandle) {
    thread::spawn(move || {
        thread::sleep(DEMORA_PRIMERA_BUSQUEDA);
        loop {
            tauri::async_runtime::block_on(buscar_y_descargar(&app));
            thread::sleep(INTERVALO_ENTRE_BUSQUEDAS);
        }
    });
}

/// Los errores de red o de firma se descartan: la proxima busqueda periodica vuelve a intentar,
/// y la app sigue funcionando con la version instalada.
async fn buscar_y_descargar(app: &AppHandle) {
    let Ok(updater) = app.updater_builder().timeout(TIMEOUT_BUSQUEDA).build() else {
        return;
    };
    let Ok(Some(mut update)) = updater.check().await else {
        return;
    };

    let estado = app.state::<EstadoDeActualizacion>();
    if !estado.lock().unwrap().debe_descargar(&update.version) {
        return;
    }

    update.timeout = Some(TIMEOUT_DESCARGA);
    let Ok(bytes) = update.download(|_, _| {}, || {}).await else {
        return;
    };

    let info = ActualizacionDisponible {
        version: update.version.clone(),
        notas: update.body.clone(),
    };
    estado
        .lock()
        .unwrap()
        .registrar_descarga(info.clone(), (update, bytes));
    let _ = app.emit(EVENTO_ACTUALIZACION_DESCARGADA, info);
}

/// Instala la version descargada. En Windows `Update::install` lanza el instalador NSIS en modo
/// pasivo y termina el proceso; el instalador reabre la app al terminar. Solo vuelve si fallo.
pub async fn instalar(app: &AppHandle) -> Result<(), String> {
    let estado = app.state::<EstadoDeActualizacion>();
    let (update, bytes) = estado
        .lock()
        .unwrap()
        .tomar_para_instalar()
        .map_err(|error| match error {
            ErrorAlTomar::SinActualizacion => "No hay ninguna actualizacion descargada.".to_string(),
            ErrorAlTomar::YaInstalando => "La actualizacion ya se esta instalando.".to_string(),
        })?;

    let resultado = tauri::async_runtime::spawn_blocking(move || update.install(bytes))
        .await
        .map_err(|error| error.to_string())
        .and_then(|instalado| instalado.map_err(|error| error.to_string()));

    if let Err(error) = resultado {
        estado.lock().unwrap().instalacion_fallida();
        return Err(format!("No se pudo instalar la actualizacion: {error}"));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn info(version: &str) -> ActualizacionDisponible {
        ActualizacionDisponible {
            version: version.to_string(),
            notas: None,
        }
    }

    #[test]
    fn sin_descarga_no_hay_nada_disponible_ni_para_instalar() {
        let mut estado = Estado::<u8>::default();

        assert_eq!(estado.disponible(), None);
        assert_eq!(
            estado.tomar_para_instalar(),
            Err(ErrorAlTomar::SinActualizacion)
        );
    }

    #[test]
    fn no_vuelve_a_descargar_la_version_ya_descargada() {
        let mut estado = Estado::<u8>::default();
        assert!(estado.debe_descargar("0.4.0"));

        estado.registrar_descarga(info("0.4.0"), 1);

        assert!(!estado.debe_descargar("0.4.0"));
        assert!(estado.debe_descargar("0.5.0"));
        assert_eq!(estado.disponible(), Some(info("0.4.0")));
    }

    #[test]
    fn una_version_mas_nueva_reemplaza_a_la_descargada() {
        let mut estado = Estado::<u8>::default();
        estado.registrar_descarga(info("0.4.0"), 1);

        estado.registrar_descarga(info("0.5.0"), 2);

        assert_eq!(estado.disponible(), Some(info("0.5.0")));
        assert_eq!(estado.tomar_para_instalar(), Ok(2));
    }

    #[test]
    fn un_segundo_pedido_de_instalacion_en_curso_se_rechaza() {
        let mut estado = Estado::<u8>::default();
        estado.registrar_descarga(info("0.4.0"), 1);

        assert_eq!(estado.tomar_para_instalar(), Ok(1));
        assert_eq!(estado.tomar_para_instalar(), Err(ErrorAlTomar::YaInstalando));
    }

    #[test]
    fn una_instalacion_fallida_se_puede_reintentar() {
        let mut estado = Estado::<u8>::default();
        estado.registrar_descarga(info("0.4.0"), 1);
        assert_eq!(estado.tomar_para_instalar(), Ok(1));

        estado.instalacion_fallida();

        assert_eq!(estado.tomar_para_instalar(), Ok(1));
    }
}
