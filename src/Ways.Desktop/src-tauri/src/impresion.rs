//! Impresion RAW en impresoras termicas via el spooler de Windows (winspool),
//! y construccion de la trama ESC/POS del ticket de prueba.

use std::ffi::c_void;

use serde::Serialize;
use windows::core::{PCWSTR, PWSTR};
use windows::Win32::Foundation::{HANDLE, GENERIC_WRITE};
use windows::Win32::Graphics::Printing::{
    ClosePrinter, EndDocPrinter, EndPagePrinter, EnumPrintersW, GetDefaultPrinterW, GetPrinterW,
    OpenPrinterW, StartDocPrinterW, StartPagePrinter, WritePrinter, DOC_INFO_1W,
    PRINTER_ACCESS_RIGHTS, PRINTER_DEFAULTSW, PRINTER_ENUM_CONNECTIONS, PRINTER_ENUM_LOCAL,
    PRINTER_INFO_2W, PRINTER_INFO_4W,
};

const ESC: u8 = 0x1B;
const GS: u8 = 0x1D;

/// Codifica una cadena a CP858 (variante de CP850 con el signo Euro),
/// que es la tabla que entienden la mayoria de las impresoras termicas
/// ESC/POS al recibir el comando `ESC t 19`. Los caracteres sin mapeo
/// conocido se reemplazan por `?`.
pub fn codificar_cp858(texto: &str) -> Vec<u8> {
    texto
        .chars()
        .map(|caracter| {
            if caracter.is_ascii() {
                return caracter as u8;
            }
            match caracter {
                'ü' => 0x81,
                'é' => 0x82,
                'â' => 0x83,
                'ä' => 0x84,
                'à' => 0x85,
                'å' => 0x86,
                'ç' => 0x87,
                'ê' => 0x88,
                'ë' => 0x89,
                'è' => 0x8A,
                'ï' => 0x8B,
                'î' => 0x8C,
                'ì' => 0x8D,
                'Ä' => 0x8E,
                'Å' => 0x8F,
                'É' => 0x90,
                'æ' => 0x91,
                'Æ' => 0x92,
                'ô' => 0x93,
                'ö' => 0x94,
                'ò' => 0x95,
                'û' => 0x96,
                'ù' => 0x97,
                'ÿ' => 0x98,
                'Ö' => 0x99,
                'Ü' => 0x9A,
                'á' => 0xA0,
                'í' => 0xA1,
                'ó' => 0xA2,
                'ú' => 0xA3,
                'ñ' => 0xA4,
                'Ñ' => 0xA5,
                'ª' => 0xA6,
                'º' => 0xA7,
                '¿' => 0xA8,
                '¡' => 0xAD,
                'Á' => 0xB5,
                'Â' => 0xB6,
                'À' => 0xB7,
                'Í' => 0xD6,
                'Î' => 0xD7,
                'Ï' => 0xD8,
                'Ó' => 0xE0,
                'ß' => 0xE1,
                'Ô' => 0xE2,
                'Ò' => 0xE3,
                'õ' => 0xE4,
                'Õ' => 0xE5,
                'Ú' => 0xE9,
                'Û' => 0xEA,
                'Ù' => 0xEB,
                'ý' => 0xEC,
                'Ý' => 0xED,
                '€' => 0xD5,
                _ => b'?',
            }
        })
        .collect()
}

/// Construye la trama ESC/POS del ticket de prueba: inicializa la impresora,
/// selecciona la pagina de codigos CP858, centra el texto, imprime el
/// mensaje, avanza papel y hace un corte parcial.
pub fn construir_ticket_prueba() -> Vec<u8> {
    let mut trama = Vec::new();

    // ESC @ - inicializa la impresora.
    trama.extend_from_slice(&[ESC, b'@']);
    // ESC t 19 - selecciona la pagina de codigos CP858.
    trama.extend_from_slice(&[ESC, b't', 19]);
    // ESC a 1 - centra el texto.
    trama.extend_from_slice(&[ESC, b'a', 1]);

    trama.extend_from_slice(&codificar_cp858("Ways POS\n"));
    trama.extend_from_slice(&codificar_cp858("Prueba de impresión\n"));

    // ESC a 0 - vuelve la alineacion a la izquierda.
    trama.extend_from_slice(&[ESC, b'a', 0]);
    // ESC d 3 - avanza 3 lineas de papel.
    trama.extend_from_slice(&[ESC, b'd', 3]);
    // GS V 66 0 - corte parcial de papel.
    trama.extend_from_slice(&[GS, b'V', 66, 0]);

    trama
}

fn cadena_utf16(texto: &str) -> Vec<u16> {
    texto.encode_utf16().chain(std::iter::once(0)).collect()
}

/// Devuelve el nombre de la impresora predeterminada de Windows, si hay una.
pub fn impresora_predeterminada() -> Option<String> {
    unsafe {
        let mut necesarios: u32 = 0;
        // Primera llamada: solo para obtener el tamano de buffer requerido.
        let _ = GetDefaultPrinterW(PWSTR::null(), &mut necesarios);
        if necesarios == 0 {
            return None;
        }

        let mut buffer: Vec<u16> = vec![0; necesarios as usize];
        let exito = GetDefaultPrinterW(PWSTR(buffer.as_mut_ptr()), &mut necesarios);
        if exito.0 == 0 {
            return None;
        }

        let fin = buffer.iter().position(|&c| c == 0).unwrap_or(buffer.len());
        Some(String::from_utf16_lossy(&buffer[..fin]))
    }
}

/// Enumera las impresoras instaladas (locales y conexiones de red).
pub fn listar_impresoras() -> Result<Vec<String>, String> {
    unsafe {
        let flags = PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS;
        let nombre_nulo = PCWSTR::null();

        let mut necesarios: u32 = 0;
        let mut devueltos: u32 = 0;

        // Primera llamada para conocer el tamano de buffer necesario.
        let _ = EnumPrintersW(flags, nombre_nulo, 4, None, &mut necesarios, &mut devueltos);
        if necesarios == 0 {
            return Ok(Vec::new());
        }

        let mut buffer: Vec<u8> = vec![0; necesarios as usize];
        EnumPrintersW(
            flags,
            nombre_nulo,
            4,
            Some(&mut buffer),
            &mut necesarios,
            &mut devueltos,
        )
        .map_err(|error| format!("No se pudo enumerar las impresoras: {error}"))?;

        let info = buffer.as_ptr() as *const PRINTER_INFO_4W;
        let mut nombres = Vec::with_capacity(devueltos as usize);
        for i in 0..devueltos as isize {
            let entrada = &*info.offset(i);
            if entrada.pPrinterName.is_null() {
                continue;
            }
            nombres.push(entrada.pPrinterName.to_string().unwrap_or_default());
        }
        Ok(nombres)
    }
}

/// Impresora con informacion sobre si es una impresora "de tickets" real o
/// una impresora virtual/de documento (PDF, XPS, OneNote, fax, etc.).
#[derive(Serialize, Debug, Clone, PartialEq, Eq)]
pub struct InfoImpresora {
    pub nombre: String,
    pub es_virtual: bool,
}

/// Impresora predeterminada de Windows, con el mismo diagnostico de
/// "es virtual" que [`InfoImpresora`], para mostrarla en la pagina de
/// configuracion.
#[derive(Serialize, Debug, Clone, PartialEq, Eq)]
pub struct ImpresoraPredeterminada {
    pub nombre: Option<String>,
    pub es_virtual: bool,
}

struct DetalleImpresora {
    driver: String,
    puerto: String,
}

/// Determina si una impresora es virtual/de documento (no imprime tickets
/// fisicos) a partir de su driver, su puerto y, como ultimo recurso, su
/// nombre. Es una funcion pura para poder testearla sin depender de
/// impresoras reales instaladas.
///
/// El driver y el puerto son la senal mas confiable (por ejemplo, el driver
/// "Microsoft Print To PDF" o un puerto "PORTPROMPT:" identifican una
/// impresora virtual sin importar como el usuario la haya renombrado). El
/// nombre se usa solo como respaldo cuando no se pudo consultar el driver o
/// el puerto.
pub fn es_impresora_virtual(nombre: &str, driver: &str, puerto: &str) -> bool {
    let nombre = nombre.to_lowercase();
    let driver = driver.to_lowercase();
    let puerto = puerto.to_lowercase();

    const PUERTOS_VIRTUALES: &[&str] = &["portprompt:", "nul:", "file:", "xpsport:"];
    if PUERTOS_VIRTUALES
        .iter()
        .any(|prefijo| puerto.starts_with(prefijo))
    {
        return true;
    }

    const CLAVES_DRIVER: &[&str] = &[
        "microsoft print to pdf",
        "microsoft xps document writer",
        "microsoft shared fax driver",
        "send to onenote",
        "onenote",
        "journal note writer",
        "xps",
        "pdf",
    ];
    if CLAVES_DRIVER.iter().any(|clave| driver.contains(clave)) {
        return true;
    }

    const CLAVES_NOMBRE: &[&str] = &[
        "microsoft print to pdf",
        "microsoft xps document writer",
        "send to onenote",
        "onenote",
        "fax",
        "pdf",
        "xps document writer",
    ];
    CLAVES_NOMBRE.iter().any(|clave| nombre.contains(clave))
}

/// Consulta el driver y el puerto de una impresora ya instalada via
/// `GetPrinterW` (nivel 2). Devuelve `None` si la impresora no existe o si
/// la consulta falla (por ejemplo, sin permisos).
fn detalles_impresora(nombre: &str) -> Option<DetalleImpresora> {
    unsafe {
        let nombre_utf16 = cadena_utf16(nombre);
        let mut handle = HANDLE::default();
        OpenPrinterW(PCWSTR(nombre_utf16.as_ptr()), &mut handle, None).ok()?;

        let mut necesarios: u32 = 0;
        let _ = GetPrinterW(handle, 2, None, &mut necesarios);
        if necesarios == 0 {
            let _ = ClosePrinter(handle);
            return None;
        }

        let mut buffer: Vec<u8> = vec![0; necesarios as usize];
        let resultado = GetPrinterW(handle, 2, Some(&mut buffer), &mut necesarios);
        let _ = ClosePrinter(handle);
        resultado.ok()?;

        let info = &*(buffer.as_ptr() as *const PRINTER_INFO_2W);
        let driver = if info.pDriverName.is_null() {
            String::new()
        } else {
            info.pDriverName.to_string().unwrap_or_default()
        };
        let puerto = if info.pPortName.is_null() {
            String::new()
        } else {
            info.pPortName.to_string().unwrap_or_default()
        };

        Some(DetalleImpresora { driver, puerto })
    }
}

/// Evalua si una impresora ya instalada es virtual, consultando su driver y
/// puerto reales cuando es posible y, si no, recurriendo solo al nombre.
fn evaluar_si_es_virtual(nombre: &str) -> bool {
    match detalles_impresora(nombre) {
        Some(detalle) => es_impresora_virtual(nombre, &detalle.driver, &detalle.puerto),
        None => es_impresora_virtual(nombre, "", ""),
    }
}

/// Como [`impresora_predeterminada`], pero incluyendo el diagnostico de
/// impresora virtual, para mostrarlo en la pagina de configuracion.
pub fn impresora_predeterminada_detallada() -> ImpresoraPredeterminada {
    match impresora_predeterminada() {
        Some(nombre) => {
            let es_virtual = evaluar_si_es_virtual(&nombre);
            ImpresoraPredeterminada {
                nombre: Some(nombre),
                es_virtual,
            }
        }
        None => ImpresoraPredeterminada {
            nombre: None,
            es_virtual: false,
        },
    }
}

/// Enumera las impresoras instaladas junto con el diagnostico de "es
/// virtual" (driver + puerto via `EnumPrintersW` nivel 2, con respaldo por
/// nombre).
pub fn listar_impresoras_con_detalle() -> Result<Vec<InfoImpresora>, String> {
    unsafe {
        let flags = PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS;
        let nombre_nulo = PCWSTR::null();

        let mut necesarios: u32 = 0;
        let mut devueltos: u32 = 0;

        let _ = EnumPrintersW(flags, nombre_nulo, 2, None, &mut necesarios, &mut devueltos);
        if necesarios == 0 {
            return Ok(Vec::new());
        }

        let mut buffer: Vec<u8> = vec![0; necesarios as usize];
        EnumPrintersW(
            flags,
            nombre_nulo,
            2,
            Some(&mut buffer),
            &mut necesarios,
            &mut devueltos,
        )
        .map_err(|error| format!("No se pudo enumerar las impresoras: {error}"))?;

        let info = buffer.as_ptr() as *const PRINTER_INFO_2W;
        let mut resultado = Vec::with_capacity(devueltos as usize);
        for i in 0..devueltos as isize {
            let entrada = &*info.offset(i);
            if entrada.pPrinterName.is_null() {
                continue;
            }
            let nombre = entrada.pPrinterName.to_string().unwrap_or_default();
            let driver = if entrada.pDriverName.is_null() {
                String::new()
            } else {
                entrada.pDriverName.to_string().unwrap_or_default()
            };
            let puerto = if entrada.pPortName.is_null() {
                String::new()
            } else {
                entrada.pPortName.to_string().unwrap_or_default()
            };
            let es_virtual = es_impresora_virtual(&nombre, &driver, &puerto);
            resultado.push(InfoImpresora { nombre, es_virtual });
        }
        Ok(resultado)
    }
}

/// Resuelve la impresora efectiva a partir de la configurada (si hay) o la
/// predeterminada de Windows, y rechaza impresoras virtuales/de documento
/// para preservar la garantia de "nunca un dialogo".
pub fn resolver_impresora_efectiva(configurada: Option<&str>) -> Result<String, String> {
    let candidata = match configurada.map(str::trim).filter(|nombre| !nombre.is_empty()) {
        Some(nombre) => nombre.to_string(),
        None => impresora_predeterminada().ok_or_else(|| {
            "No hay una impresora configurada ni una predeterminada en Windows.".to_string()
        })?,
    };

    if evaluar_si_es_virtual(&candidata) {
        return Err(format!(
            "La impresora \"{candidata}\" no es una impresora de tickets; configura una en Configuracion (Ctrl+Shift+F10)."
        ));
    }

    Ok(candidata)
}

/// Envia bytes crudos (RAW) a la impresora indicada a traves del spooler de
/// Windows, sin mostrar ningun dialogo de impresion.
pub fn imprimir_raw(nombre_impresora: &str, datos: &[u8]) -> Result<(), String> {
    if nombre_impresora.trim().is_empty() {
        return Err("No hay una impresora configurada.".to_string());
    }

    unsafe {
        let nombre_utf16 = cadena_utf16(nombre_impresora);
        let mut handle = HANDLE::default();

        let defaults = PRINTER_DEFAULTSW {
            pDatatype: PWSTR::null(),
            pDevMode: std::ptr::null_mut(),
            DesiredAccess: PRINTER_ACCESS_RIGHTS(GENERIC_WRITE.0),
        };

        OpenPrinterW(
            PCWSTR(nombre_utf16.as_ptr()),
            &mut handle,
            Some(&defaults),
        )
        .map_err(|_| {
            format!("No se encontro la impresora \"{nombre_impresora}\" o no se pudo abrir.")
        })?;

        let resultado = imprimir_raw_con_handle(handle, datos);

        let _ = ClosePrinter(handle);

        resultado
    }
}

unsafe fn imprimir_raw_con_handle(handle: HANDLE, datos: &[u8]) -> Result<(), String> {
    let mut nombre_documento = cadena_utf16("Ticket Ways POS");
    let mut tipo_dato = cadena_utf16("RAW");

    let doc_info = DOC_INFO_1W {
        pDocName: PWSTR(nombre_documento.as_mut_ptr()),
        pOutputFile: PWSTR::null(),
        pDatatype: PWSTR(tipo_dato.as_mut_ptr()),
    };

    let id_documento = StartDocPrinterW(handle, 1, &doc_info as *const DOC_INFO_1W);
    if id_documento == 0 {
        return Err("No se pudo iniciar el trabajo de impresion.".to_string());
    }

    if StartPagePrinter(handle).0 == 0 {
        let _ = EndDocPrinter(handle);
        return Err("No se pudo iniciar la pagina de impresion.".to_string());
    }

    let mut escritos: u32 = 0;
    let escribio = WritePrinter(
        handle,
        datos.as_ptr() as *const c_void,
        datos.len() as u32,
        &mut escritos,
    );

    let _ = EndPagePrinter(handle);
    let _ = EndDocPrinter(handle);

    if escribio.0 == 0 || escritos as usize != datos.len() {
        return Err("No se pudieron escribir todos los datos en la impresora.".to_string());
    }

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn el_ticket_empieza_con_secuencia_de_init_y_codepage() {
        let ticket = construir_ticket_prueba();
        assert_eq!(&ticket[0..2], &[ESC, b'@']);
        assert_eq!(&ticket[2..5], &[ESC, b't', 19]);
        assert_eq!(&ticket[5..8], &[ESC, b'a', 1]);
    }

    #[test]
    fn el_ticket_termina_con_avance_y_corte_parcial() {
        let ticket = construir_ticket_prueba();
        let largo = ticket.len();
        assert_eq!(&ticket[largo - 4..], &[GS, b'V', 66, 0]);
        assert_eq!(&ticket[largo - 7..largo - 4], &[ESC, b'd', 3]);
    }

    #[test]
    fn el_ticket_contiene_el_texto_esperado_en_cp858() {
        let ticket = construir_ticket_prueba();
        let esperado = codificar_cp858("Prueba de impresión\n");
        let contiene = ticket
            .windows(esperado.len())
            .any(|ventana| ventana == esperado.as_slice());
        assert!(contiene, "el ticket deberia contener el texto de prueba en CP858");
    }

    #[test]
    fn codifica_vocales_acentuadas_y_enie() {
        let codificado = codificar_cp858("áéíóúñÑ");
        assert_eq!(codificado, vec![0xA0, 0x82, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5]);
    }

    #[test]
    fn codifica_signos_de_apertura() {
        let codificado = codificar_cp858("¿¡");
        assert_eq!(codificado, vec![0xA8, 0xAD]);
    }

    #[test]
    fn pasa_ascii_sin_modificar() {
        let codificado = codificar_cp858("Ways POS 123");
        assert_eq!(codificado, "Ways POS 123".as_bytes());
    }

    #[test]
    fn reemplaza_caracteres_desconocidos() {
        let codificado = codificar_cp858("漢字");
        assert_eq!(codificado, vec![b'?', b'?']);
    }

    #[test]
    fn detecta_impresoras_virtuales_por_puerto() {
        assert!(es_impresora_virtual("Cualquier Nombre", "Algun Driver", "PORTPROMPT:"));
        assert!(es_impresora_virtual("Cualquier Nombre", "Algun Driver", "nul:"));
        assert!(es_impresora_virtual("Cualquier Nombre", "Algun Driver", "FILE:"));
    }

    #[test]
    fn detecta_impresoras_virtuales_por_driver() {
        assert!(es_impresora_virtual(
            "Impresora renombrada",
            "Microsoft Print To PDF",
            "PORTPROMPT:"
        ));
        assert!(es_impresora_virtual(
            "Impresora renombrada",
            "Microsoft XPS Document Writer v4",
            "XPSPort:"
        ));
        assert!(es_impresora_virtual(
            "Impresora renombrada",
            "Send To Microsoft OneNote Driver",
            "OneNote:"
        ));
    }

    #[test]
    fn detecta_impresoras_virtuales_por_nombre_como_respaldo() {
        assert!(es_impresora_virtual("Microsoft Print to PDF", "", ""));
        assert!(es_impresora_virtual("Microsoft XPS Document Writer", "", ""));
        assert!(es_impresora_virtual("Fax", "", ""));
        assert!(es_impresora_virtual("Send To OneNote 2016", "", ""));
    }

    #[test]
    fn no_marca_impresoras_termicas_reales_como_virtuales() {
        assert!(!es_impresora_virtual(
            "EPSON TM-T20III Receipt",
            "EPSON TM-T20III Receipt5",
            "USB001"
        ));
        assert!(!es_impresora_virtual(
            "POS-80C",
            "POS58 Printer",
            "USB002"
        ));
    }

    #[test]
    fn resolver_impresora_efectiva_rechaza_virtual_configurada_por_nombre() {
        let error = resolver_impresora_efectiva(Some("Microsoft Print to PDF")).unwrap_err();
        assert!(error.contains("no es una impresora de tickets"));
        assert!(error.contains("Ctrl+Shift+F10"));
    }

    #[test]
    fn resolver_impresora_efectiva_usa_nombre_configurado_no_virtual() {
        // Sin acceso a impresoras reales en CI, solo validamos que un
        // nombre no presente en las listas de deteccion no se rechaza por
        // el respaldo de nombre (la consulta real de driver/puerto falla
        // silenciosamente a "no encontrada" y cae al respaldo por nombre).
        let resultado = resolver_impresora_efectiva(Some("EPSON TM-T20III Receipt"));
        assert_eq!(resultado, Ok("EPSON TM-T20III Receipt".to_string()));
    }

    #[test]
    fn resolver_impresora_efectiva_sin_configuracion_ni_predeterminada_da_error_claro() {
        // En un entorno sin impresoras (como CI), no hay predeterminada.
        if impresora_predeterminada().is_none() {
            let error = resolver_impresora_efectiva(None).unwrap_err();
            assert!(error.contains("predeterminada"));
        }
    }
}
