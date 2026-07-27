<?php
    require_once './conexion.php';
	
	$conexion = mysqli_connect(HOST, USER, PASSWORD, DATABASE);

    $id = $_POST['id'];
    $codigo = $_POST['barcode'];
    $query = "SELECT * FROM codigos_barra WHERE codigo = '$codigo'";
    $consulta = mysqli_query($conexion, $query);

    if (mysqli_num_rows($consulta) == 1) {
        $resultado = mysqli_fetch_assoc($consulta);
        if ($resultado['id_articulo'] == $id) {
            echo "ERROR:El código ingresado ya está registrado para este artículo.";
        } else {
            echo "ERROR:El código ingresado ya está registrado para otro artículo.";
        }
    } else {
        $query = "INSERT INTO codigos_barra (codigo, id_articulo) VALUES ('$codigo', '$id')";
        if(mysqli_query($conexion, $query)) {
            echo "EXITO:El código se agregó correctamente.";
        }
        else {
            echo "ERROR:Ocurrió un error al cargar el código.";
        }
    }