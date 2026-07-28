<?php
// Datos de conexión a la base del sistema legacy.
//
// El archivo original tenía las credenciales del hosting escritas a mano. Ahora llegan por
// variables de entorno, así la misma imagen corre contra cualquier servicio MySQL sin
// reconstruirla y ninguna contraseña queda dentro de la imagen ni del repositorio.
//
// Variables esperadas: DB_HOST, DB_PORT, DB_NAME, DB_USER, DB_PASSWORD.

function ways_env($nombre, $porDefecto = '')
{
    $valor = getenv($nombre);

    return ($valor === false || $valor === '') ? $porDefecto : $valor;
}

define("HOST", ways_env('DB_HOST', 'mysql'));
define("USER", ways_env('DB_USER', 'ways'));
define("PASSWORD", ways_env('DB_PASSWORD', ''));
define("DATABASE", ways_env('DB_NAME', 'c1890978_alsina'));

// Todas las llamadas a mysqli_connect() del legacy omiten el puerto, así que se define acá
// una sola vez en lugar de tocar cada archivo.
ini_set('mysqli.default_port', ways_env('DB_PORT', '3306'));

// El charset de la conexión NO se fija acá a propósito. El hosting viejo trabajaba con
// conexión latin1 sobre tablas latin1, y por eso los nombres de artículos están guardados
// como bytes UTF-8 dentro de columnas latin1. Eso se reproduce en el servicio MySQL con
// `init_connect = 'SET NAMES latin1'` (ver legacy/config/mysql-ways.cnf). Forzar utf8 acá
// rompería los acentos de todo el catálogo.
