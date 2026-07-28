<?php
	require_once './conexion.php';
	
	$conexion = mysqli_connect(HOST, USER, PASSWORD, DATABASE);

	$consultaBusqueda = $_POST['valorBusqueda'] == '**' ? '999999999' : $_POST['valorBusqueda'];
	if (strlen($consultaBusqueda) < 7) {
		$query = "	SELECT nombre, id_area, precio, barra, ID, id_grupo 
					FROM articulos 
					WHERE ID = '$consultaBusqueda' AND activo='1'";
	} else {
		$query = "	SELECT a.nombre, a.id_area, a.precio, a.barra, a.ID, a.id_grupo 
					FROM articulos a
						JOIN codigos_barra cb ON a.ID = cb.id_articulo
					WHERE cb.codigo = '$consultaBusqueda' AND activo='1'";
	}

	//Filtro anti-XSS
	$caracteres_malos = array("<", ">", "\"", "'", "/", "<", ">", "'", "/");
	$caracteres_buenos = array("& lt;", "& gt;", "& quot;", "& #x27;", "& #x2F;", "& #060;", "& #062;", "& #039;", "& #047;");
	$consultaBusqueda = str_replace($caracteres_malos, $caracteres_buenos, $consultaBusqueda);

	if (isset($consultaBusqueda)) {
		$consulta = mysqli_query($conexion, $query);

		if (mysqli_num_rows($consulta) == 1) {
			$resultados = mysqli_fetch_assoc($consulta);
			echo $resultados['nombre'], ',', $resultados['precio'];
		}
	}
	else echo '';