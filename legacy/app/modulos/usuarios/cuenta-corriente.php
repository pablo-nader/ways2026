<?php
	@$id=$_GET['usuario'];
	$id_punto_venta = $_SESSION['login']['punto_venta']['id'];
	if(isset($_GET['pago']) && isset($_GET['usuario'])) {
		if($_GET['pago']=='Cargar') {
			if(is_numeric($_GET['efectivo'])) { $efectivo=number_format($_GET['efectivo'],2,".",""); }
			else { $efectivo='0.00'; }
			if(is_numeric($_GET['tarjetas'])) { $tarjetas=number_format($_GET['tarjetas'],2,".",""); }
			else { $tarjetas='0.00'; }
			$fecha=date("Y-m-d H:i:s");
			$c_corriente=number_format((($tarjetas+$efectivo)*(-1)),2,".","");
			$id_usuario=$_SESSION['login']['id'];
			$cliente=$_GET['usuario'];
			$cargarPago="INSERT INTO ventas (fecha, efectivo, tarjetas, c_corriente, id_usuario, cliente, tipo, actualizada, id_punto_venta) VALUES ('$fecha', '$efectivo', '$tarjetas', '$c_corriente', '$id_usuario', '$cliente', '3', '1', '$id_punto_venta')";
			$cargarPago=mysqli_query($conexion,$cargarPago);
			$actualizarSaldo="UPDATE usuarios SET saldo=saldo+'$c_corriente' WHERE id='$cliente'";
			$actualizarSaldo=mysqli_query($conexion,$actualizarSaldo);
			echo '	<script language="javascript">
						window.location="index.php?menu=usuarios&opc=cc&usuario='.$cliente.'";
					</script>';
		}
	}
	elseif(isset($_GET['ajuste']) && isset($_GET['usuario'])) {
		if($_GET['ajuste']=='Cargar') {
			if(is_numeric($_GET['importe'])) { $importe=number_format($_GET['importe'],2,".",""); }
			else { $efectivo='0.00'; }
			$fecha=date("Y-m-d H:i:s");
			$c_corriente=number_format($importe,2,".","");
			$id_usuario=$_SESSION['login']['id'];
			$cliente=$_GET['usuario'];
			if(empty($_GET['detalle'])) { $detalle='AJUSTE PERSONALIZADO'; }
			else { $detalle=strtoupper($_GET['detalle']); }
			$cargarPago="INSERT INTO ventas (fecha, c_corriente, id_usuario, cliente, tipo, actualizada, obs, id_punto_venta) VALUES ('$fecha', '$c_corriente', '$id_usuario', '$cliente', '5', '1', '$detalle', '$id_punto_venta')";
			$cargarPago=mysqli_query($conexion,$cargarPago);
			$actualizarSaldo="UPDATE usuarios SET saldo=saldo+'$c_corriente' WHERE id='$cliente'";
			$actualizarSaldo=mysqli_query($conexion,$actualizarSaldo);
			echo '	<script language="javascript">
						window.location="index.php?menu=usuarios&opc=cc&usuario='.$cliente.'";
					</script>';
		}
	}
	elseif(isset($_GET['actualizar']) && isset($_GET['usuario'])) {
		if($_GET['actualizar']=='Actualizar') {
			$importe=0;
			$articulos='';
			$obtenerVentas="SELECT * FROM ventas WHERE cliente = $id AND actualizada = 0";
			$obtenerVentas=mysqli_query($conexion,$obtenerVentas);
			if(mysqli_num_rows($obtenerVentas)>0) {
				$listaCliente=mysqli_fetch_array(mysqli_query($conexion,"SELECT lista FROM usuarios WHERE id='$id'"));
				$listaCliente=$listaCliente[0];
				echo $listaCliente;
				while($mostrar=mysqli_fetch_assoc($obtenerVentas)) {
					$ticket=$mostrar['id'];
					$obtenerArticulos="SELECT articulos FROM ventas WHERE id = $ticket";
					$obtenerArticulos=mysqli_fetch_assoc(mysqli_query($conexion, $obtenerArticulos));
					mysqli_query($conexion, "UPDATE ventas SET actualizada = 1 WHERE id = $ticket");
					$array = explode("*",$obtenerArticulos['articulos']);
					foreach($array as $id => $producto) {
						$art = explode("/",$producto);
						$of='OF';
						$pos = strpos($art[0],$of);
						if ($pos === false) {
							$barra=$art[0];
							if($listaCliente==2){ 
								$precio = "	SELECT a.precioEmp 
											FROM articulos a 
												JOIN codigos_barra cb ON a.ID = cb.id_articulo 
											WHERE cb.codigo = '$barra'";
								$precio=mysqli_fetch_array(mysqli_query($conexion,$precio));
								$precioNuevo=$precio[0]*$art[1];
								$precioViejo=$art[3]*$art[1];
								$diferencia=$precioNuevo-$precioViejo;
								$importe=$importe+$diferencia;
								$articulos.=$barra.'/'.$art[1].'/'.$art[2].'/'.$precio[0].'/'.$diferencia.'*';
							}
							else {
								$precio = "	SELECT a.precio 
											FROM articulos a 
												JOIN codigos_barra cb ON a.ID = cb.id_articulo 
											WHERE cb.codigo = '$barra'";
								$precio=mysqli_fetch_array(mysqli_query($conexion,$precio));
								$precioNuevo=$precio[0]*$art[1];
								$precioViejo=$art[3]*$art[1];
								$diferencia=$precioNuevo-$precioViejo;
								$importe=$importe+$diferencia;
								$articulos.=$barra.'/'.$art[1].'/'.$art[2].'/'.$precio[0].'/'.$diferencia.'*';
							}
						}
						else {
							$diferencia=($art[4])*(-1);
							$importe=$importe+$diferencia;
							$articulos.=$barra.'/-/'.$art[1].'/-/'.$diferencia.'*';
						}
					}
				}
				$articulos=trim($articulos,'*');
				$fecha=date("Y-m-d H:i:s");
				$c_corriente=$importe;
				$id_usuario = $_SESSION['login']['id'];
				$cliente=$_GET['usuario'];
				$cargarActualizacion="INSERT INTO ventas (fecha, c_corriente, articulos, id_usuario, cliente, tipo, actualizada, id_punto_venta) VALUES ('$fecha', '$c_corriente', '$articulos', '$id_usuario', '$cliente', '4', '1', '$id_punto_venta')";
				$cargarActualizacion=mysqli_query($conexion,$cargarActualizacion);
				$actualizarSaldo=mysqli_query($conexion,"UPDATE usuarios SET saldo=saldo+'$c_corriente' WHERE id='$cliente'");
			}
			$cliente=$_GET['usuario'];
			echo '	<script language="javascript">
						window.location="index.php?menu=usuarios&opc=cc&usuario='.$cliente.'";
					</script>';
		}
	}
	if(isset($_GET['filtrar']) && isset($_GET['usuario'])) {
		if($_GET['filtrar']=='Filtrar') {
			$desde=$_GET['desde'];
			$hasta=$_GET['hasta'];
			$datos="SELECT * FROM ventas WHERE cliente='$id' AND fecha BETWEEN '$desde' AND '$hasta' ORDER BY fecha DESC";
		}
		elseif($_GET['filtrar']=='Ver Historico') {
			$datos="SELECT * FROM ventas WHERE cliente='$id' ORDER BY fecha DESC";
		}
		else {
			$datos="SELECT * FROM ventas WHERE cliente='$id' AND fecha BETWEEN date_sub(now(), interval 1 month)  AND NOW() ORDER BY fecha DESC";
		}
	}
	else {
		$datos="SELECT * FROM ventas WHERE cliente='$id' AND fecha BETWEEN date_sub(now(), interval 1 month)  AND NOW() ORDER BY fecha DESC";
	}
	$datos2="SELECT * FROM ventas WHERE cliente='$id' AND fecha BETWEEN date_sub(now(), interval 1 month)  AND NOW() ORDER BY fecha DESC";
	$datosCliente="SELECT * FROM usuarios WHERE id='$id'";
	$buscarDatosCliente=mysqli_query($conexion,$datosCliente);
	$mostrarDatosCliente=mysqli_fetch_assoc($buscarDatosCliente);
	$sumatoria=$mostrarDatosCliente['saldo'];
	$disponibilidad=number_format(($mostrarDatosCliente['acuerdo']-$mostrarDatosCliente['saldo']),2,".","");
	$ejecutar=mysqli_query($conexion,$datos);
	if(mysqli_num_rows($ejecutar)==0) { $ejecutar=mysqli_query($conexion,$datos2); }
	$subtitulo.='Cuenta Corriente';
	$contenido.='
	<div class="col-lg-12">
		<table class="table table-striped responsive-table table-hover table-bordered">
			<thead>
				<tr>
					<th>Cliente</th>
					<th>DNI</th>
					<th>Domicilio</th>
					<th>Celular</th>
					<th>Saldo</th>
					<th>Acuerdo</th>
					<th>Disponibilidad</th>
					<th colspan="3">Acciones</th>
				</tr>
			</thead>
			<tbody>
				<tr>
					<td>'.$mostrarDatosCliente['nombre'].' '.$mostrarDatosCliente['apellido'].'</td>
					<td>'.$mostrarDatosCliente['dni'].'</td>
					<td>'.$mostrarDatosCliente['domicilio'].'</td>
					<td>'.$mostrarDatosCliente['cel'].'</td>
					<td style="text-align:right;">'.$mostrarDatosCliente['saldo'].'<span style="float:left;">$</span></td>
					<td style="text-align:right;">'.$mostrarDatosCliente['acuerdo'].'<span style="float:left;">$</span></td>
					<td style="text-align:right;">'.$disponibilidad.'<span style="float:left;">$</span></td>
					<td style="border-right:none;"><a class="disabled"><i class="fa fa-print"></i></a></td>
					<td style="border-right:none;border-left:none;"><a title="Ingresar Pago" data-bs-toggle="modal" data-bs-target="#ingresarPago" href="#"><i class="fa fa-donate"></i></a></td>
					<td style="border-right:none;border-left:none;"><a class="disabled"><i class="fa fa-download"></i></a></td>
				</tr>
				<tr>
					<form method="get" name="filtrar" id="filtrar">
					<input type="hidden" name="menu" value="usuarios">
					<input type="hidden" name="opc" value="cc">
					<input type="hidden" name="usuario" value="'.@$_GET['usuario'].'">
					
					<td><strong>Filtrar</strong></td>
					<td style="text-align:right;" colspan="2"><input type="date" name="desde" id="desde" value="'.@$_GET['desde'].'"><span style="float:left;">Desde : </span></td>
					<td style="text-align:right;" colspan="2"><input type="date" name="hasta" id="hasta" value="'.@$_GET['hasta'].'"><span style="float:left;">Hasta : </span></td>
					<td style="text-align:center;"><input type="submit" name="filtrar" value="Filtrar" class="btn btn-primary rounded-0" style="padding:2px 10px;"></td>
					<td style="text-align:center;"><input type="submit" name="filtrar" value="Ver Historico" class="btn btn-success rounded-0" style="padding:2px 10px;"></td>
					<td style="border-right:none;"><a title="Ajuste Personalizado" data-bs-toggle="modal" data-bs-target="#ajustePersonalizado" href="#"><i class="far fa-edit"></i></a></td>
					<td style="border-right:none;border-left:none;"><a title="Actualizar Precios" data-bs-toggle="modal" data-bs-target="#actualizarPrecios" href="#"><i class="fa fa-undo-alt"></i></a></td>
					<td style="border-right:none;border-left:none;"><a href="index.php?menu=usuarios&opc=editar&usuario='.$_GET['usuario'].'"><i class="fa fa-user-edit"></i></a></td>
					</form>
				</tr>
			</tbody>
		</table>
	</div>
	<div class="col-lg-12">
		<table class="table table-striped responsive-table table-hover table-bordered">
			<thead>
				<tr>
					<th>Ticket</th>
					<th>Fecha</th>
					<th>Usuario</th>
					<th colspan="2">Total</th>
					<th colspan="2">Efectivo</th>
					<th colspan="2">Tarjetas</th>
					<th colspan="2">Saldo</th>
					<th colspan="2">Final</th>
				</tr>
			</thead>
			<tbody>';
			while($mostrar=mysqli_fetch_assoc($ejecutar)) {
				$ticket='0001 - '.str_pad($mostrar['id'], 8, "0", STR_PAD_LEFT);
				$fechahora=explode(" ",$mostrar['fecha']);
				$fecha=explode("-",$fechahora[0]);
				$fecha=$fecha[2].'/'.$fecha[1].'/'.$fecha[0];
				$hora=explode(":",$fechahora[1]);
				$hora=$hora[0].':'.$hora[1];
				$id_usuario = $mostrar['id_usuario'];
				$id_usuario = mysqli_query($conexion, "SELECT user FROM usuarios WHERE id='$id_usuario'");
				$id_usuario = mysqli_fetch_array($id_usuario);
				if($mostrar['tipo'] == 1 || $mostrar['tipo'] == 2) {
					if($mostrar['eliminado'] == 1){
						$contenido.= '
						<tr style="background-color:orange;">
							<td><a href="" onclick="reTicket('.$mostrar['id'].')">'.$ticket.'</a></td>
							<td>'.$fecha.' '.$hora.'</td>
							<td>'.$id_usuario[0].'</td>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;">'.$mostrar['total'].'</td>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;">'.$mostrar['efectivo'].'</td>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;">'.$mostrar['tarjetas'].'</td>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;">'.$mostrar['c_corriente'].'</td>
							<td style="border-right:none;font-weight:bold;">$</td>
							<td style="border-left:none;text-align:right;font-weight:bold;">'.$sumatoria.'</td>
						</tr>';
					}
					else {
						$contenido.= '
						<tr>
							<td><a href="" onclick="reTicket('.$mostrar['id'].')">'.$ticket.'</a></td>
							<td>'.$fecha.' '.$hora.'</td>
							<td>'.$id_usuario[0].'</td>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;">'.$mostrar['total'].'</td>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;">'.$mostrar['efectivo'].'</td>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;">'.$mostrar['tarjetas'].'</td>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;">'.$mostrar['c_corriente'].'</td>
							<td style="border-right:none;font-weight:bold;">$</td>
							<td style="border-left:none;text-align:right;font-weight:bold;">'.$sumatoria.'</td>
						</tr>';	
					}
				}
				elseif($mostrar['tipo'] == 3) {
					$contenido.= '
					<tr>
						<td><a href="" onclick="reTicket('.$mostrar['id'].')">'.$ticket.'</a></td>
						<td>'.$fecha.' '.$hora.'</td>
						<td>'.$id_usuario[0].'</td>
						<td colspan="6" style="text-align:center;">PAGO A CUENTA</td>
						<td style="border-right:none;">$</td>
						<td style="border-left:none;text-align:right;">'.$mostrar['c_corriente'].'</td>
						<td style="border-right:none;font-weight:bold;">$</td>
						<td style="border-left:none;text-align:right;font-weight:bold;">'.$sumatoria.'</td>
					</tr>';
				}
				elseif($mostrar['tipo'] == 4) {
					$contenido.= '
					<tr>
						<td><a href="" onclick="reTicket('.$mostrar['id'].')">'.$ticket.'</a></td>
						<td>'.$fecha.' '.$hora.'</td>
						<td>'.$id_usuario[0].'</td>
						<td colspan="6" style="text-align:center;">ACTUALIZACION DE PRECIOS</td>
						<td style="border-right:none;">$</td>
						<td style="border-left:none;text-align:right;">'.$mostrar['c_corriente'].'</td>
						<td style="border-right:none;font-weight:bold;">$</td>
						<td style="border-left:none;text-align:right;font-weight:bold;">'.$sumatoria.'</td>
					</tr>';
				}
				elseif($mostrar['tipo'] == 5) {
					$contenido.= '
					<tr>
						<td><a href="" onclick="reTicket('.$mostrar['id'].')">'.$ticket.'</a></td>
						<td>'.$fecha.' '.$hora.'</td>
						<td>'.$id_usuario[0].'</td>
						<td colspan="6" style="text-align:center;">'.$mostrar['obs'].'</td>
						<td style="border-right:none;">$</td>
						<td style="border-left:none;text-align:right;">'.$mostrar['c_corriente'].'</td>
						<td style="border-right:none;font-weight:bold;">$</td>
						<td style="border-left:none;text-align:right;font-weight:bold;">'.$sumatoria.'</td>
					</tr>';
				}
				$sumatoria=number_format(($sumatoria-$mostrar['c_corriente']),2,".","");
			}
			$contenido.='</tbody>
		</table>
	</div>';