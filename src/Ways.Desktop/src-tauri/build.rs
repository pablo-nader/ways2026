fn main() {
    let manifest = tauri_build::AppManifest::new().commands(&[
        "listar_impresoras",
        "listar_impresoras_con_detalle",
        "impresora_predeterminada_de_windows",
        "impresora_predeterminada_detallada",
        "imprimir_raw",
        "imprimir_prueba",
        "guardar_configuracion",
        "leer_configuracion",
        "abrir_configuracion",
        "info_app",
    ]);

    tauri_build::try_build(tauri_build::Attributes::new().app_manifest(manifest))
        .expect("error al generar los permisos de la app");
}
