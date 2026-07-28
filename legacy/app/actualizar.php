<div class="box">
	<header>
		<div class="icons iconsW">
			<a style="color:#333;" title="Inicio" class="btn-lg" href="">
				<i class="fa fa-home"></i>
				<span class="menuW">Inicio</span>
			</a>
		</div>
		<div class="icons iconsW">
			<a style="color:#333;" title="Ver Cajas" class="btn-lg" href="">
				<i class="far fa-clipboard"></i>
				<span class="menuW">Ver Cajas</span>
			</a>
		</div>
		<div class="icons iconsW">
			<a style="color:#333;" title="Caja General" class="btn-lg" href="">
				<i class="fa fa-clipboard-list"></i>
				<span class="menuW">Caja General</span>
			</a>
		</div>
		<div class="icons iconsW">
			<a style="color:#333;" title="Caja Virtual" class="btn-lg" href="">
				<i class="fas fa-clipboard"></i>
				<span class="menuW">Caja Virtual</span>
			</a>
		</div>
		<div class="icons iconsW ayuda">
			<a style="color:#333;" title="Ayuda" class="btn-lg" href="">
				<i class="far fa-question-circle"></i>
				<span class="menuW">Ayuda</span>
			</a>
		</div>
	</header>
	<div class="body" style="min-height:400px;">
		<div class="row">
			<div class="col-lg-12">
			
<?php
// Sincronización de catálogo contra el servidor de otro local. La dirección remota y sus
// credenciales estaban escritas acá; ahora llegan por entorno y la pantalla queda apagada
// mientras SYNC_REMOTE_HOST no esté definida. Sin esa guarda, un host remoto inalcanzable
// deja colgado un worker de Apache durante todo el timeout de conexión cada vez que se
// abre la opción del menú.
require_once __DIR__ . '/conexion.php';

if (ways_env('SYNC_REMOTE_HOST') === '') {
	echo "
			<div class='alert alert-warning'>
				La sincronizacion con el servidor remoto esta deshabilitada.
				Para habilitarla, configura SYNC_REMOTE_HOST, SYNC_REMOTE_USER,
				SYNC_REMOTE_PASSWORD y SYNC_REMOTE_DB en el servicio de la aplicacion.
			</div>
			</div>
		</div>
	</div>
</div>";
	return;
}

$conRemota=mysqli_connect(ways_env('SYNC_REMOTE_HOST'), ways_env('SYNC_REMOTE_USER'), ways_env('SYNC_REMOTE_PASSWORD'), ways_env('SYNC_REMOTE_DB', 'ways'));
$conLocal=mysqli_connect(HOST, USER, PASSWORD, DATABASE);

// ACTUALIZAR ARTICULOS
$consulta="SELECT MAX(ID) FROM articulos";
$resultado = mysqli_fetch_assoc(mysqli_query($conRemota, $consulta));
$maximo = $resultado['MAX(ID)'];

for($i=1;$i<=$maximo;$i++) {
	$consultar=mysqli_query($conLocal, "SELECT * FROM articulos WHERE ID='$i'");
	$consultarRemoto=mysqli_query($conRemota, "SELECT * FROM articulos WHERE ID='$i'");
	
	if(mysqli_num_rows($consultar)!=1) { // si no hay un resultado crea el articulo, sino lo edita
		
		if(mysqli_num_rows($consultarRemoto)!=1) { // si tampoco existe, lo saltea, sino lo crea
			echo "
			<div class='alert alert-warning'>
				El Articulo ID: $i no existe en el servidor REMOTO.
			</div>";
		}
		else {
			$remota=mysqli_fetch_assoc($consultarRemoto);
			$crear="INSERT INTO articulos (codigo, barra, nombre, nombreOferta, lista, dtoGral, costo, costoOferta, precio, precioOferta, precioCant, precioEmp, tolerancia, caja, proveedor, marca, grupo, OfertaDia, OfertaDiaDesde, OfertaDiaHasta, OfertaHora, OfertaHoraDesde, OfertaHoraHasta, OfertaCant, OfertaCantN, producto, existenciaMinima, uBulto, reposicion) VALUES ('".$remota['codigo']."', '".$remota['barra']."', '".$remota['nombre']."', '".$remota['nombreOferta']."', '".$remota['lista']."', '".$remota['dtoGral']."', '".$remota['costo']."', '".$remota['costoOferta']."', '".$remota['precio']."', '".$remota['precioOferta']."', '".$remota['precioCant']."', '".$remota['precioEmp']."', '".$remota['tolerancia']."', '".$remota['caja']."', '".$remota['proveedor']."', '".$remota['marca']."', '".$remota['grupo']."', '".$remota['OfertaDia']."', '".$remota['OfertaDiaDesde']."', '".$remota['OfertaDiaHasta']."', '".$remota['OfertaHora']."', '".$remota['OfertaHoraDesde']."', '".$remota['OfertaHoraHasta']."', '".$remota['OfertaCant']."', '".$remota['OfertaCantN']."', '".$remota['producto']."', '".$remota['existenciaMinima']."', '".$remota['uBulto']."', '".$remota['reposicion']."')";
			if(mysqli_query($conLocal, $crear)) {
				echo "
				<div class='alert alert-success'>
					El Articulo ID: $i ha sido agregado al servidor LOCAL.
				</div>";
			}
			else {
				echo "
				<div class='alert alert-warning'>
					El Articulo ID: $i NO HA SIDO MODIFICADO, CONSULTA: -".$crear."-.
				</div>";
			}
		}
	}
	else {
		$local=mysqli_fetch_assoc($consultar);
		$remota=mysqli_fetch_assoc($consultarRemoto);
		$cambios=false;
		if($local['codigo']!=$remota['codigo']) { $cambios=true; }
		elseif($local['barra']!=$remota['barra']) { $cambios=true; }
		elseif($local['nombre']!=$remota['nombre']) { $cambios=true; }
		elseif($local['nombreOferta']!=$remota['nombreOferta']) { $cambios=true; }
		elseif($local['lista']!=$remota['lista']) { $cambios=true; }
		elseif($local['dtoGral']!=$remota['dtoGral']) { $cambios=true; }
		elseif($local['costo']!=$remota['costo']) { $cambios=true; }
		elseif($local['costoOferta']!=$remota['costoOferta']) { $cambios=true; }
		elseif($local['precio']!=$remota['precio']) { $cambios=true; }
		elseif($local['precioOferta']!=$remota['precioOferta']) { $cambios=true; }
		elseif($local['precioCant']!=$remota['precioCant']) { $cambios=true; }
		elseif($local['precioEmp']!=$remota['precioEmp']) { $cambios=true; }
		elseif($local['tolerancia']!=$remota['tolerancia']) { $cambios=true; }
		elseif($local['caja']!=$remota['caja']) { $cambios=true; }
		elseif($local['proveedor']!=$remota['proveedor']) { $cambios=true; }
		elseif($local['marca']!=$remota['marca']) { $cambios=true; }
		elseif($local['grupo']!=$remota['grupo']) { $cambios=true; }
		elseif($local['OfertaDia']!=$remota['OfertaDia']) { $cambios=true; }
		elseif($local['OfertaDiaDesde']!=$remota['OfertaDiaDesde']) { $cambios=true; }
		elseif($local['OfertaDiaHasta']!=$remota['OfertaDiaHasta']) { $cambios=true; }
		elseif($local['OfertaHora']!=$remota['OfertaHora']) { $cambios=true; }
		elseif($local['OfertaHoraDesde']!=$remota['OfertaHoraDesde']) { $cambios=true; }
		elseif($local['OfertaHoraHasta']!=$remota['OfertaHoraHasta']) { $cambios=true; }
		elseif($local['OfertaCant']!=$remota['OfertaCant']) { $cambios=true; }
		elseif($local['OfertaCantN']!=$remota['OfertaCantN']) { $cambios=true; }
		elseif($local['producto']!=$remota['producto']) { $cambios=true; }
		elseif($local['existenciaMinima']!=$remota['existenciaMinima']) { $cambios=true; }
		elseif($local['reposicion']!=$remota['reposicion']) { $cambios=true; }
		elseif($local['uBulto']!=$remota['uBulto']) { $cambios=true; }
		elseif($local['activo']!=$remota['activo']) { $cambios=true; }
		
		if($cambios) {
			$consulta = "UPDATE articulos SET 
						codigo='".$remota['codigo']."', 
						barra='".$remota['barra']."', 
						nombre='".$remota['nombre']."', 
						nombreOferta='".$remota['nombreOferta']."', 
						lista='".$remota['lista']."', 
						dtoGral='".$remota['dtoGral']."', 
						costo='".$remota['costo']."', 
						costoOferta='".$remota['costoOferta']."', 
						precio='".$remota['precio']."', 
						precioOferta='".$remota['precioOferta']."', 
						precioCant='".$remota['precioCant']."', 
						precioEmp='".$remota['precioEmp']."', 
						tolerancia='".$remota['tolerancia']."', 
						caja='".$remota['caja']."', 
						proveedor='".$remota['proveedor']."', 
						marca='".$remota['marca']."', 
						grupo='".$remota['grupo']."', 
						OfertaDia='".$remota['OfertaDia']."', 
						OfertaDiaDesde='".$remota['OfertaDiaDesde']."', 
						OfertaDiaHasta='".$remota['OfertaDiaHasta']."', 
						OfertaHora='".$remota['OfertaHora']."', 
						OfertaHoraDesde='".$remota['OfertaHoraDesde']."', 
						OfertaHoraHasta='".$remota['OfertaHoraHasta']."', 
						OfertaCant='".$remota['OfertaCant']."', 
						OfertaCantN='".$remota['OfertaCantN']."', 
						producto='".$remota['producto']."', 
						existenciaMinima='".$remota['existenciaMinima']."', 
						reposicion='".$remota['reposicion']."', 
						uBulto='".$remota['uBulto']."', 
						activo='".$remota['activo']."' WHERE ID='$i';";
			if(mysqli_query($conLocal, $consulta)) {
				echo "
				<div class='alert alert-success'>
					El Articulo ID: $i ha sido modificado en el servidor LOCAL.
				</div>";
			}
			else {
				echo "
				<div class='alert alert-warning'>
					El Articulo ID: $i NO HA SIDO EDITADO, ERROR EN LA CONSULTA : -".$consulta."-
				</div>";			
			}
		}
		else {
			echo "
			<div class='alert alert-info'>
				El Articulo ID: $i no ha sido modificado.
			</div>";
		}
	}
}


// ACTUALIZAR GRUPOS
$consulta="SELECT MAX(ID) FROM grupos";
$resultado = mysqli_fetch_assoc(mysqli_query($conRemota, $consulta));
$maximo = $resultado['MAX(ID)'];

for($i=1;$i<=$maximo;$i++) {
	$consultar=mysqli_query($conLocal, "SELECT * FROM grupos WHERE ID='$i'");
	$consultarRemoto=mysqli_query($conRemota, "SELECT * FROM grupos WHERE ID='$i'");
	
	if(mysqli_num_rows($consultar)!=1) { // si no hay un resultado crea el grupo, sino lo edita
		
		if(mysqli_num_rows($consultarRemoto)!=1) { // si tampoco existe, lo saltea, sino lo crea
			echo "
			<div class='alert alert-warning'>
				El GRUPO ID: $i no existe en el servidor REMOTO.
			</div>";
		}
		else {
			$remota=mysqli_fetch_assoc($consultarRemoto);
			$crear="INSERT INTO grupos (nombre, margen, ofertaCantidad, ofertaDirecta, descripcion, cantidad, precio, descuento, dias, dDesde, dHasta, horas, hDesde, hHasta) VALUES 
			('".$remota['nombre']."', '".$remota['margen']."', '".$remota['ofertaCantidad']."', '".$remota['ofertaDirecta']."', '".$remota['descripcion']."', '".$remota['cantidad']."', 
			'".$remota['precio']."', '".$remota['descuento']."', '".$remota['dias']."', '".$remota['dDesde']."', '".$remota['dHasta']."', '".$remota['horas']."', '".$remota['hDesde']."', '".$remota['hHasta']."')";
			if(mysqli_query($conLocal, $crear)) {
				echo "
				<div class='alert alert-success'>
					El Grupo ID: $i ha sido agregado al servidor LOCAL.
				</div>";
			}
			else {
				echo "
				<div class='alert alert-warning'>
					El Grupo ID: $i NO HA SIDO MODIFICADO, CONSULTA: -".$crear."-.
				</div>";
			}
		}
	}
	else {
		$local=mysqli_fetch_assoc($consultar);
		$remota=mysqli_fetch_assoc($consultarRemoto);
		$cambios=false;
		if($local['nombre']!=$remota['nombre']) { $cambios=true; }
		elseif($local['margen']!=$remota['margen']) { $cambios=true; }
		elseif($local['ofertaCantidad']!=$remota['ofertaCantidad']) { $cambios=true; }
		elseif($local['ofertaDirecta']!=$remota['ofertaDirecta']) { $cambios=true; }
		elseif($local['descripcion']!=$remota['descripcion']) { $cambios=true; }
		elseif($local['cantidad']!=$remota['cantidad']) { $cambios=true; }
		elseif($local['precio']!=$remota['precio']) { $cambios=true; }
		elseif($local['descuento']!=$remota['descuento']) { $cambios=true; }
		elseif($local['dias']!=$remota['dias']) { $cambios=true; }
		elseif($local['dDesde']!=$remota['dDesde']) { $cambios=true; }
		elseif($local['dHasta']!=$remota['dHasta']) { $cambios=true; }
		elseif($local['horas']!=$remota['horas']) { $cambios=true; }
		elseif($local['hDesde']!=$remota['hDesde']) { $cambios=true; }
		elseif($local['hHasta']!=$remota['hHasta']) { $cambios=true; }
		
		
		if($cambios) {
			$consulta = "UPDATE grupos SET 
						nombre='".$remota['nombre']."', 
						margen='".$remota['margen']."', 
						ofertaCantidad='".$remota['ofertaCantidad']."', 
						ofertaDirecta='".$remota['ofertaDirecta']."', 
						descripcion='".$remota['descripcion']."', 
						cantidad='".$remota['cantidad']."', 
						precio='".$remota['precio']."', 
						descuento='".$remota['descuento']."', 
						dias='".$remota['dias']."', 
						dDesde='".$remota['dDesde']."', 
						dHasta='".$remota['dHasta']."', 
						horas='".$remota['horas']."', 
						hDesde='".$remota['hDesde']."', 
						hHasta='".$remota['hHasta']."' WHERE ID='$i';";
			if(mysqli_query($conLocal, $consulta)) {
				echo "
				<div class='alert alert-success'>
					El Grupò ID: $i ha sido modificado en el servidor LOCAL.
				</div>";
			}
			else {
				echo "
				<div class='alert alert-warning'>
					El Grupò ID: $i NO HA SIDO EDITADO, ERROR EN LA CONSULTA : -".$consulta."-
				</div>";			
			}
		}
		else {
			echo "
			<div class='alert alert-info'>
				El Grupò ID: $i no ha sido modificado.
			</div>";
		}
	}
}

// ACTUALIZAR MARCAS
$consulta="SELECT MAX(ID) FROM marcas";
$resultado = mysqli_fetch_assoc(mysqli_query($conRemota, $consulta));
$maximo = $resultado['MAX(ID)'];

for($i=1;$i<=$maximo;$i++) {
	$consultar=mysqli_query($conLocal, "SELECT * FROM marcas WHERE ID='$i'");
	$consultarRemoto=mysqli_query($conRemota, "SELECT * FROM marcas WHERE ID='$i'");
	
	if(mysqli_num_rows($consultar)!=1) { // si no hay un resultado crea la marca, sino lo edita
		
		if(mysqli_num_rows($consultarRemoto)!=1) { // si tampoco existe, lo saltea, sino lo crea
			echo "
			<div class='alert alert-warning'>
				La MARCA ID: $i no existe en el servidor REMOTO.
			</div>";
		}
		else {
			$remota=mysqli_fetch_assoc($consultarRemoto);
			$crear="INSERT INTO marcas (nombre, proveedor, grupo) VALUES 
			('".$remota['nombre']."', '".$remota['proveedor']."', '".$remota['grupo']."')";
			if(mysqli_query($conLocal, $crear)) {
				echo "
				<div class='alert alert-success'>
					La Marca ID: $i ha sido agregada al servidor LOCAL.
				</div>";
			}
			else {
				echo "
				<div class='alert alert-warning'>
					La Marca ID: $i NO HA SIDO MODIFICADA, CONSULTA: -".$crear."-.
				</div>";
			}
		}
	}
	else {
		$local=mysqli_fetch_assoc($consultar);
		$remota=mysqli_fetch_assoc($consultarRemoto);
		$cambios=false;
		if($local['nombre']!=$remota['nombre']) { $cambios=true; }
		elseif($local['proveedor']!=$remota['proveedor']) { $cambios=true; }
		elseif($local['grupo']!=$remota['grupo']) { $cambios=true; }
		
		
		if($cambios) {
			$consulta = "UPDATE marcas SET 
						nombre='".$remota['nombre']."', 
						proveedor='".$remota['proveedor']."', 
						grupo='".$remota['grupo']."' WHERE ID='$i';";
			if(mysqli_query($conLocal, $consulta)) {
				echo "
				<div class='alert alert-success'>
					La Marca ID: $i ha sido modificada en el servidor LOCAL.
				</div>";
			}
			else {
				echo "
				<div class='alert alert-warning'>
					La Marca ID: $i NO HA SIDO EDITADA, ERROR EN LA CONSULTA : -".$consulta."-
				</div>";			
			}
		}
		else {
			echo "
			<div class='alert alert-info'>
				La Marca ID: $i no ha sido modificada.
			</div>";
		}
	}
}

?>
			
			</div>
		</div>
	</div>
</div>