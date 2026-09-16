// Punto de entrada del binario en Windows: oculta la consola en release.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    ways_desktop_lib::ejecutar();
}
