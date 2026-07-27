<?php
session_start();
// Conectando, seleccionando la base de datos
$conexion = mysqli_connect('127.0.0.1', 'root', '') or die('No se pudo conectar: ' . mysqli_error());
mysqli_select_db($conexion,'ways') or die('No se pudo seleccionar la base de datos');

//Variable de búsqueda
$consultaBusqueda = $_POST['valorBusqueda'];

//Filtro anti-XSS
$caracteres_malos = array("<", ">", "\"", "'", "/", "<", ">", "'", "/");
$caracteres_buenos = array("& lt;", "& gt;", "& quot;", "& #x27;", "& #x2F;", "& #060;", "& #062;", "& #039;", "& #047;");
$consultaBusqueda = str_replace($caracteres_malos, $caracteres_buenos, $consultaBusqueda);

 
	$filtrado='general'; 
	$consulta = "SELECT * FROM articulos WHERE 	proveedor LIKE '%$consultaBusqueda%' OR
																	nombre LIKE '%$consultaBusqueda%' OR
																	barra LIKE '%$consultaBusqueda%' OR 
																	grupo LIKE '%$consultaBusqueda%' OR
																	marca LIKE '%$consultaBusqueda%' ORDER BY nombre DESC LIMIT 20"; 

//Variable vacía (para evitar los E_NOTICE)
$mensaje = '<table class="table table-striped responsive-table table-hover table-bordered">
				<thead>
					<tr>
						<th colspan="2">Nombre</th>
						<th>Lista</th>
						<th>Venta</th>
					</tr>
				</thead>
				<tbody>';

//Comprueba si $consultaBusqueda está seteado
if (isset($consultaBusqueda)) {

	$consulta2 = mysqli_query($conexion, $consulta);

	//Obtiene la cantidad de filas que hay en la consulta
	$filas = mysqli_num_rows($consulta2);

	//Si no existe ninguna fila que sea igual a $consultaBusqueda, entonces mostramos el siguiente mensaje
	if ($filas === 0) {
		$mensaje.= '</tbody></table></div>';
	} 
	else {
		//Si existe alguna fila que sea igual a $consultaBusqueda, entonces mostramos el siguiente mensaje

		//La variable $resultado contiene el array que se genera en la consulta, así que obtenemos los datos y los mostramos en un bucle
		while($mostrarArticulos = mysqli_fetch_assoc($consulta2)) {
			//Output
			$mensaje.='
					<tr>';
			if(@$_SESSION['combo']['estado']=='editar') {
						$mensaje.='
						<td><a href="index.php?menu=articulos&opc=combos&editarCombo='.@$_SESSION['combo']['id'].'&agregar='.@$mostrarArticulos['barra'].'"><i class="fa fa-plus-circle"></i></a></td>';
					}
					elseif(@$_SESSION['combo']['estado']=='eliminar') {
						$mensaje.='
						<td><a href="index.php?menu=articulos&opc=combos&eliminarCombo='.@$_SESSION['combo']['id'].'&agregar='.@$mostrarArticulos['barra'].'"><i class="fa fa-plus-circle"></i></a></td>';
					}
					else {
						$mensaje.='<td><a href="index.php?menu=articulos&opc=combos&agregar='.@$mostrarArticulos['barra'].'"><i class="fa fa-plus-circle"></i></a></td>';
					}
			$mensaje.= '

						
						<td>'.$mostrarArticulos['nombre'].'</td>
						<td>'.$mostrarArticulos['lista'].'</td>
						<td>'.$mostrarArticulos['precio'].'</td>
					</tr>
			';
		};//Fin while $resultados
		$mensaje.= '</tbody></table></div>
		';
	}; //Fin else $filas

};//Fin isset $consultaBusqueda

//Devolvemos el mensaje que tomará jQuery
echo $mensaje;
?>