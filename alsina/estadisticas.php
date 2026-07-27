<?php
$subtitulo='';
$contenido='';
$mensaje='';
if(@$_GET['opc']=='cajaZ') {
	if(isset($_GET['FC'])) {
		$fc=$_GET['FC'];
		if(is_numeric($fc)) {
			$fc=number_format($fc,2,".","");
			$id_punto_venta = $_SESSION['login']['punto_venta']['id'];
			$datos = mysqli_fetch_assoc(mysqli_query($conexion, "SELECT * FROM cajaz WHERE id_punto_venta = $id_punto_venta ORDER BY FECHA DESC LIMIT 1"));
			$inicial = $datos['final'];
			$diferencia = $fc-$inicial;
			if ($diferencia < 0) {
				$ingreso = '0.00';
				$egreso = number_format($diferencia, 2, ".", "");
			}
			else {
				$ingreso = number_format($diferencia, 2, ".", "");
				$egreso = '0.00';
			}
			$fecha=date("Y-m-d H:i:s");
			$concepto='Ajuste de Caja';
			$tipo='2';
			$id_usuario = $_SESSION['login']['id'];
			$id_punto_venta = $_SESSION['login']['punto_venta']['id'];

			$cargarAjuste="INSERT INTO cajaz (fecha, concepto, inicio, ingreso, egreso, final, tipo, id_usuario, id_punto_venta) 
							VALUES 	('$fecha', '$concepto', '$inicial', '$ingreso', '$egreso', '$fc', '$tipo', '$id_usuario', '$id_punto_venta')";
			if(mysqli_query($conexion,$cargarAjuste)) {
				echo '<script>window.location="index.php?menu=estadisticas&opc=cajaZ";</script>';
			}
			else {
				echo $cargarAjuste;
			}
		}
	}
	$id_punto_venta = $_SESSION['login']['punto_venta']['id'];
	$cajaZ = mysqli_query($conexion, "SELECT * FROM cajaz WHERE id_punto_venta = $id_punto_venta ORDER BY FECHA DESC LIMIT 20");
	$contenido.='
	<div class="col-lg-12">
		<table class="table table-striped responsive-table table-hover table-bordered">
			<thead>
				<tr>
					<th>
						Fecha
						<span style="float:right;">
							<a title="Ingresar FC"data-bs-toggle="modal" data-bs-target="#ingresarFC" href="#">
								<i class="fa fa-hand-holding-usd"></i>
							</a>
						</span>
					</th>
					<th>Concepto</th>
					<th>Inicial</th>
					<th>Ingreso</th>
					<th>Egreso</th>
					<th>Final</th>
					<th>Usuario</th>
				</tr>
			</thead>
			<tbody>';
	while ($mostrarCajaZ = mysqli_fetch_assoc($cajaZ)) {
		$contenido.='
				<tr>
					<td>'.$mostrarCajaZ['fecha'].'</td>';
		if($mostrarCajaZ['tipo']==1) { 
			$reemplazar = $mostrarCajaZ['concepto'];
			$id = intval(preg_replace('/[^0-9]+/', '', $reemplazar), 10); 
		
			$contenido.='<td><a href="index.php?menu=estadisticas&opc=cajas&ver='.$id.'">'.$mostrarCajaZ['concepto'].'</a></td>';
		}			
		else {
			$contenido.='<td>'.$mostrarCajaZ['concepto'].'</td>';
		}			
		if($mostrarCajaZ['inicio']<0) { $contenido.='<td style="text-align:right;color:red;">'.$mostrarCajaZ['inicio'].'<span style="float:left;color:#333;">$</span></td>'; }
		else { $contenido.='<td style="text-align:right;">'.$mostrarCajaZ['inicio'].'<span style="float:left;">$</span></td>'; }
		if($mostrarCajaZ['tipo']=='2') {
			if($mostrarCajaZ['ingreso']!=0) {
				$contenido.='
				<td colspan="2" style="text-align:right;color:green;">'.$mostrarCajaZ['ingreso'].'<span style="float:left;color#333;">$</span></td>';
			} 
			elseif($mostrarCajaZ['egreso']!=0) {
				$contenido.='
				<td colspan="2" style="text-align:right;color:red;">'.$mostrarCajaZ['egreso'].'<span style="float:left;color#333;">$</span></td>';
			}
			else {
				$contenido.='
				<td colspan="2" style="text-align:right;">0.00<span style="float:left;">$</span></td>';
			}
		}
		else {
			$contenido.='
				<td style="text-align:right;">'.$mostrarCajaZ['ingreso'].'<span style="float:left;">$</span></td>
				<td style="text-align:right;">'.$mostrarCajaZ['egreso'].'<span style="float:left;">$</span></td>';	
		}
		if($mostrarCajaZ['final']<0) { $contenido.='<td style="text-align:right;color:red;">'.$mostrarCajaZ['final'].'<span style="float:left;color:#333;">$</span></td>'; }
		else { $contenido.='<td style="text-align:right;">'.$mostrarCajaZ['final'].'<span style="float:left;">$</span></td>'; }
		$op = $mostrarCajaZ['id_usuario'];
		$id_usuario = mysqli_fetch_array(mysqli_query($conexion, "SELECT user FROM usuarios WHERE id = '$op'"));
		$contenido.='
					<td style="text-align:center;">'.$id_usuario[0].'</td>
				</tr>
		';
	}				
	$contenido.='
			</tbody>
		</table>
	</div>
	';
}
elseif (@$_GET['opc'] == 'cajas') {
	if(isset($_GET['ver'])) {
		$id = $_GET['ver'];
		$id_punto_venta = $_SESSION['login']['punto_venta']['id'];
		$ver = mysqli_query($conexion, "SELECT * FROM cajas WHERE id_punto_venta = $id_punto_venta AND id = $id");
		if(mysqli_num_rows($ver)!=1) {
			$contenido='
			<div class="col-lg-12">
				<div class="alert alert-danger rounded-0">
					El ID ingresado no corresponde a ningún cierre de caja registrado en el sistema. <a href="javascript:window.history.go(-2)">Volver.</a>
				</div>
			</div>';
		}
		else {
			$mostrar=mysqli_fetch_assoc($ver);
			$desde=$mostrar['primero'];
			$hasta=$mostrar['ultimo'];
			$id_punto_venta = $_SESSION['login']['punto_venta']['id'];
			$busquedaC = "SELECT * FROM cajas WHERE id_punto_venta = $id_punto_venta AND id = $id";
			$mostrarCaja=mysqli_fetch_assoc(mysqli_query($conexion,$busquedaC));
			$totalV=number_format(($mostrarCaja['c1']+$mostrarCaja['c2']+$mostrarCaja['c3']+$mostrarCaja['c4']),2,".","");
			$totalG=number_format(($mostrarCaja['g_c1']+$mostrarCaja['g_c2']+$mostrarCaja['g_c3']+$mostrarCaja['g_c4']),2,".","");
			$diferencia=number_format((($mostrarCaja['efectivo']-$mostrarCaja['retiros'])*-1),2,".","");
			$fecha=explode(" ",$mostrarCaja['fecha']);
			$n_fecha=explode("-",$fecha[0]);
			$n_hora=explode(":",$fecha[1]);
			$n_fecha[0]=substr($n_fecha[0],2,2);
			$fecha=$n_fecha[2].'/'.$n_fecha[1].'/'.$n_fecha[0].' '.$n_hora[0].':'.$n_hora[1];
			$op = $mostrarCaja['id_usuario'];
			$id_usuario=mysqli_fetch_array(mysqli_query($conexion, "SELECT user FROM usuarios WHERE id = '$op'"));
			
			
			$contenido.='
			<div class="col-lg-12">
				<table class="table table-striped responsive-table table-hover table-bordered">
					<thead>
						<tr>
							<th>N° Cierre</th>
							<td>'.$id.'</td>
							<th>Fecha / Hora</th>
							<td>'.$fecha.'</td>
							<th>Tickets</th>
							<td>'.$mostrarCaja['cantidad'].'</td>
							<th>Usuario</th>
							<td>'.$id_usuario[0].'</td>
							<th><a title="Imprimir Cierre de Caja" href="#" onclick="ticketCC('.$id.'); return false;"><li class="fa fa-print"></li></a></th>
						</tr>
					</thead>
				</table>
				<table style="border:none;" class="table table-striped responsive-table table-hover table-bordered">
					<thead>
						<tr>
							<th style="border-top:solid 1px rgb(221,221,221);">Almacen</th>
							<td style="text-align:right;border-top:solid 1px rgb(221,221,221);">'.$mostrarCaja['c1'].'<span style="float:left;">$</span></td>
							<td style="text-align:right;border-top:solid 1px rgb(221,221,221);">'.$mostrarCaja['g_c1'].'<span style="float:left;">$</span></td>
							<td style="border-bottom:none;border-top:none;">&nbsp;</td>
							<th style="border-top:solid 1px rgb(221,221,221);">Efectivo</th>
							<td style="text-align:right;border-top:solid 1px rgb(221,221,221);">'.$mostrarCaja['efectivo'].'<span style="float:left;">$</span></td>
						</tr>
						<tr>
							<th>Verduleria</th>
							<td style="text-align:right;">'.$mostrarCaja['c2'].'<span style="float:left;">$</span></td>
							<td style="text-align:right;">'.$mostrarCaja['g_c2'].'<span style="float:left;">$</span></td>
							<td style="border-bottom:none;border-top:none;">&nbsp;</td>
							<th>Tarjetas</th>
							<td style="text-align:right;">'.$mostrarCaja['tarjetas'].'<span style="float:left;">$</span></td>
						</tr>
						<tr>
							<th>Fiambreria</th>
							<td style="text-align:right;">'.$mostrarCaja['c3'].'<span style="float:left;">$</span></td>
							<td style="text-align:right;">'.$mostrarCaja['g_c3'].'<span style="float:left;">$</span></td>
							<td style="border-bottom:none;border-top:none;">&nbsp;</td>
							<th>C. Corriente</th>
							<td style="text-align:right;">'.$mostrarCaja['c_corriente'].'<span style="float:left;">$</span></td>
						</tr>
						<tr>
							<th>Cigarrillos</th>
							<td style="text-align:right;">'.$mostrarCaja['c4'].'<span style="float:left;">$</span></td>
							<td style="text-align:right;">'.$mostrarCaja['g_c4'].'<span style="float:left;">$</span></td>
							<td style="border-bottom:none;border-top:none;">&nbsp;</td>
							<th>Saldo</th>
							<td style="text-align:right;">'.$mostrarCaja['saldo'].'<span style="float:left;">$</span></td>
						</tr>
						<tr>
							<th>Otros</th>
							<td style="text-align:right;">-<span style="float:left;">$</span></td>
							<td style="text-align:right;">'.$mostrarCaja['g_c7'].'<span style="float:left;">$</span></td>
							<td style="border-bottom:none;border-top:none;">&nbsp;</td>
							<th>Retiros</th>
							<td style="text-align:right;">'.$mostrarCaja['retiros'].'<span style="float:left;">$</span></td>
						</tr>
						<tr>
							<th>Total</th>
							<td style="text-align:right;">'.$mostrarCaja['total'].'<span style="float:left;">$</span></td>
							<td style="text-align:right;">'.$mostrarCaja['gTotal'].'<span style="float:left;">$</span></td>
							<td style="border-bottom:none;border-top:none;">&nbsp;</td>
							<th>Diferencia</th>
							<td style="text-align:right;">'.$diferencia.'<span style="float:left;">$</span></td>
						</tr>
					</thead>
				</table>
			</div>';
			$id_punto_venta = $_SESSION['login']['punto_venta']['id'];
			$busquedaT = "SELECT * FROM ventas WHERE id_punto_venta = $id_punto_venta AND id_caja = $id";
			$buscarTickets = mysqli_query($conexion, $busquedaT);
			$contenido.='
			<div class="col-lg-6">
				<table class="table table-striped responsive-table table-hover table-bordered">
					<thead>
						<tr>
							<th>Ticket</th>
							<th>Total</th>
							<th>Cliente</th>
							<th>Saldo</th>
							<th style="text-align:center;"><i class="fa fa-print"></i></th>
						</tr>
					</thead>
					<tbody>';
			while($mostrarTickets=mysqli_fetch_assoc($buscarTickets)) {
				$fecha=explode(" ",$mostrarTickets['fecha']);
				$hora=explode(":",$fecha[1]);
				$hora=$hora[0].':'.$hora[1];
				$fecha=explode("-",$fecha[0]);
				$fecha=$fecha[2].'/'.$fecha[1].'/'.$fecha[0];
				$ticket=str_pad($mostrarTickets['id'],8,"0",STR_PAD_LEFT);
				$idCliente=$mostrarTickets['cliente'];
				$cliente=mysqli_fetch_array(mysqli_query($conexion,"SELECT user FROM usuarios WHERE id='$idCliente'"));
				if($mostrarTickets['eliminado']==1){
					$contenido.='
						<tr style="background-color:orange;">
							<td title="'.$fecha.' '.$hora.'">'.$ticket.'</td>
							<td style="text-align:right;" title="Efectivo: $ '.$mostrarTickets['efectivo'].' &#10;Tarjetas: $ '.$mostrarTickets['tarjetas'].' &#10;C. Corriente: $ '.$mostrarTickets['c_corriente'].' &#10;">'.$mostrarTickets['total'].'<span style="float:left;">$</span></td>
							<td >'.$cliente[0].'</td>
							<td style="text-align:right;" title="Vuelto: $ '.$mostrarTickets['vuelto'].'">'.$mostrarTickets['saldo'].'<span style="float:left;">$</span></td>
							<td style="text-align:center;"><a href="" onclick="reTicket('.$mostrarTickets['id'].')"><i class="fa fa-print"></i></a></td>
						</tr>';
				}
				else {
					$contenido.='
						<tr>
							<td title="'.$fecha.' '.$hora.'">'.$ticket.'</td>
							<td style="text-align:right;" title="Efectivo: $ '.$mostrarTickets['efectivo'].' &#10;Tarjetas: $ '.$mostrarTickets['tarjetas'].' &#10;C. Corriente: $ '.$mostrarTickets['c_corriente'].' &#10;">'.$mostrarTickets['total'].'<span style="float:left;">$</span></td>
							<td >'.$cliente[0].'</td>
							<td style="text-align:right;" title="Vuelto: $ '.$mostrarTickets['vuelto'].'">'.$mostrarTickets['saldo'].'<span style="float:left;">$</span></td>
							<td style="text-align:center;"><a href="" onclick="reTicket('.$mostrarTickets['id'].')"><i class="fa fa-print"></i></a></td>
						</tr>';	
				}
			}			
			$contenido.='
					</tbody>
				</table>
			</div>
			<div class="col-lg-6">
				<table class="table table-striped responsive-table table-hover table-bordered">
					<thead>
						<tr>
							<th>ID</th>
							<th>Detalle</th>
							<th>Importe</th>
							<th>Área</th>
						</tr>
					</thead>
					<tbody>';
						$id_punto_venta = $_SESSION['login']['punto_venta']['id'];
						$busqueda = "SELECT * FROM gastos WHERE id_punto_venta = $id_punto_venta AND id_caja = $id";
						$buscarGastos = mysqli_query($conexion, $busqueda);
						while($mostrarGastos=mysqli_fetch_assoc($buscarGastos)) {
							$fecha=explode(" ",$mostrarGastos['fecha']);
							$fecha=explode("-",$fecha[0]);
							$fecha=$fecha[2].'/'.$fecha[1].'/'.$fecha[0];
							if($mostrarGastos['tipo']<90) { $tipo='Proveedor'; }
							elseif($mostrarGastos['tipo']==99) { $tipo='Otros'; }
							elseif($mostrarGastos['tipo']==98) { $tipo='Sueldos'; }
							elseif($mostrarGastos['tipo']==97) { $tipo='Viaticos'; }
							elseif($mostrarGastos['tipo']==96) { $tipo='Impuestos'; }
							elseif($mostrarGastos['tipo']==95) { $tipo='Retiros'; }
							if($mostrarGastos['id_area']==99) { $area='N/A'; }
							else {
								$c=$mostrarGastos['id_area'];
								$c=mysqli_fetch_array(mysqli_query($conexion,"SELECT nombre FROM areas WHERE id='$c'"));
								$area=$c[0];
							}
							$contenido.= '
						<tr>
							<td title="'.$fecha.'">'.$mostrarGastos['id'].'</td>
							<td title="'.$tipo.'">'.$mostrarGastos['nombre'].'</td>
							<td title="'.$mostrarGastos['otrosDetalle'].'" style="text-align:right;">'.$mostrarGastos['importe'].'</td>
							<td>'.$area.'</td>
						</tr>';
						}
						$id_punto_venta = $_SESSION['login']['punto_venta']['id'];
						$buscarTotales = mysqli_query($conexion, "SELECT sum(importe) AS gastos FROM gastos WHERE id_punto_venta = $id_punto_venta AND id_caja = $id AND tipo <> 95 ");
						$mostrarTotales = mysqli_fetch_assoc($buscarTotales);	
						$buscarRetiros = mysqli_query($conexion, "SELECT sum(importe) AS retiros FROM gastos WHERE id_punto_venta = $id_punto_venta AND id_caja = '$id' AND tipo = 95");
						$mostrarRetiros = mysqli_fetch_assoc($buscarRetiros);
			$contenido.= '	
						<tr>
							<th colspan="2">Total Gastos: </th>
							<th colspan="3">$ '.@$mostrarTotales['gastos'].' </th>
						</tr>
						<tr>
							<th colspan="2">Total Retiros: </th>
							<th colspan="3">$ '.@$mostrarRetiros['retiros'].'</th>
						</tr>
					</tbody>
				</table>
			</div>
			';
			
		}
	}
	else {
		$id_punto_venta = $_SESSION['login']['punto_venta']['id'];
		$buscar = mysqli_query($conexion, "SELECT * FROM cajas WHERE id_punto_venta = $id_punto_venta ORDER BY fecha DESC");
		$contenido.='
		<div class="col-lg-12">
			<table class="table table-striped responsive-table table-hover table-bordered">
				<thead>
					<tr>
						<th>Fecha</th>
						<th>Ventas</th>
						<th>Gastos</th>
						<th>Caja</th>
						<th>Retiros</th>
						<th>Diferencia</th>
						<th>Tickets</th>
						<th>Usuario</th>
						<th colspan="2">Acciones</th>
					</tr>
				</thead>
				<tbody>';
		while($mostrar=mysqli_fetch_assoc($buscar)) {
			$fecha=explode(" ",$mostrar['fecha']);
			$n_fecha=explode("-",$fecha[0]);
			$n_hora=explode(":",$fecha[1]);
			$n_fecha[0]=substr($n_fecha[0],2,2);
			$fecha=$n_fecha[2].'/'.$n_fecha[1].'/'.$n_fecha[0].' '.$n_hora[0].':'.$n_hora[1];
			$sumatoria=number_format(($mostrar['efectivo']+$mostrar['tarjetas']+$mostrar['c_corriente']+$mostrar['saldo']),2,".","");
			$diferencia=number_format(($mostrar['efectivo']-$mostrar['retiros'])*(-1),2,".","");
			if($diferencia>0) { $color='green'; }
			elseif($diferencia<0) { $color='red'; }
			else { $color='rgb(51, 51, 51)'; }
			$op = $mostrar['id_usuario'];
			$id_usuario = mysqli_fetch_array(mysqli_query($conexion, "SELECT user FROM usuarios WHERE id = '$op'"));
			$contenido.='
				<tr>
					<td title="Ticket N° '.$mostrar['id'].'">'.$fecha.'</td>
					<td style="text-align:right;" title="Almacen: $ '.$mostrar['c1'].'&#10;Verduleria: $ '.$mostrar['c2'].'&#10;Fiambreria: $ '.$mostrar['c3'].'&#10;Cigarrillos: $ '.$mostrar['c4'].'">'.$mostrar['total'].'<span style="float:left;">$</span></td>
					<td style="text-align:right;" title="Almacen: $ '.$mostrar['g_c1'].'&#10;Verduleria: $ '.$mostrar['g_c2'].'&#10;Fiambreria: $ '.$mostrar['g_c3'].'&#10;Cigarrillos: $ '.$mostrar['g_c4'].'&#10;Otros: $ '.$mostrar['g_c7'].'">'.$mostrar['gTotal'].'<span style="float:left;">$</span></td>
					<td style="text-align:right;" title="Efectivo: $ '.$mostrar['efectivo'].'&#10;Tarjetas: $ '.$mostrar['tarjetas'].'&#10;C. Corriente: $ '.$mostrar['c_corriente'].'&#10;Saldo: $ '.$mostrar['saldo'].'">'.$sumatoria.'<span style="float:left;">$</span></td>
					<td style="text-align:right;">'.$mostrar['retiros'].'<span style="float:left;">$</span></td>
					<td style="text-align:right;color:'.@$color.';">'.$diferencia.'<span style="float:left;">$</span></td>
					<td style="text-align:right;">'.$mostrar['cantidad'].'</td>
					<td>'.@$id_usuario[0].'</td>
					<td style="text-align:center;"><a title="Ver Detalle" style="color:orange;" href="index.php?menu=estadisticas&opc=cajas&ver='.$mostrar['id'].'"><li class="fa fa-search"></li></a></td>
					<td style="text-align:center;"><a title="Imprimir Cierre de Caja" style="color:blue;" href="" onclick="ticketCC('.$mostrar['id'].')"><li class="fa fa-print"></li></a></td>
				</tr>
			';
		}
		$contenido.='
				</tbody>
			</table>
		</div>';
	}
}
elseif (@$_GET['opc'] == 'cajaV') {
	$id_punto_venta = $_SESSION['login']['punto_venta']['id'];
	$buscarCajas = "SELECT * FROM cajav WHERE id_punto_venta = $id_punto_venta ORDER BY FECHA DESC LIMIT 20";
	$datos = mysqli_query($conexion, $buscarCajas);
	
	if(isset($_GET['cargar'])) {
		if(is_numeric($_GET['FCV']) && is_numeric($_GET['FCC']) && is_numeric($_GET['FCS']) && is_numeric($_GET['FCE'])) {
			$iniciales = mysqli_fetch_assoc(mysqli_query($conexion, "SELECT * FROM cajav WHERE id_punto_venta = $id_punto_venta ORDER BY FECHA DESC LIMIT 1"));
			$FCV=$_GET['FCV'];
			$FCC=$_GET['FCC'];
			$FCS=$_GET['FCS'];
			$FCE=$_GET['FCE'];
			$fecha=date("Y-m-d H:i:s");
			$concepto="Ajuste de Caja";
			$inicial=$iniciales['final'];
		
			$vInicial=$iniciales['vFinal'];
			$vVentas=number_format(($FCV-$vInicial),2,".","");
			$vCantidad='0';
			$vAdicionales='0.00';
			$vDeposito='0.00';
			$vComisiones='0.00';
			$vFinal=$FCV;
			
			$cInicial=$iniciales['cFinal'];
			$cVentas=number_format(($FCC-$cInicial),2,".","");
			$cCantidad='0';
			$cAdicionales='0.00';
			$cDeposito='0.00';
			$cComisiones='0.00';
			$cFinal=$FCC;
			
			$sInicial=$iniciales['sFinal'];
			$sVentas=number_format(($FCS-$sInicial),2,".","");
			$sCantidad='0';
			$sDeposito='0.00';
			$sFinal=$FCS;
			
			$eInicial=$iniciales['eFinal'];
			$eAjuste=number_format(($FCE-$eInicial),2,".","");
			$eFinal=$FCE;
		
			$final=number_format(($vFinal+$cFinal+$sFinal+$eFinal),2,".","");
			$diferencia=number_format(($final-$inicial),2,".","");
						
			$tipo=2;
			$id_usuario = $_SESSION['login']['id'];
			$id_punto_venta = $_SESSION['login']['punto_venta']['id'];
			
			$cargarAjuste="INSERT INTO cajav (fecha, concepto, inicial, vInicial, vVentas, vCantidad, vAdicionales, vDepositos, vComisiones, vFinal, cInicial, cVentas, cCantidad,
											cAdicionales, cDepositos, cComisiones, cFinal, sInicial, sVentas, sCantidad, sDepositos, sFinal, eInicial, eAjuste, eFinal, final, diferencia, tipo, id_usuario, id_punto_venta) 
								VALUES 		('$fecha','$concepto','$inicial','$vInicial','$vVentas','$vCantidad','$vAdicionales','$vDeposito','$vComisiones','$vFinal','$cInicial',
											'$cVentas','$cCantidad','$cAdicionales','$cDeposito','$cComisiones','$cFinal','$sInicial','$sVentas','$sCantidad','$sDeposito','$sFinal',
											'$eInicial','$eAjuste','$eFinal','$final','$diferencia','$tipo','$id_usuario', '$id_punto_venta') ";
			if(mysqli_query($conexion,$cargarAjuste)) {
				echo '<script>window.location="index.php?menu=estadisticas&opc=cajaV"</script>';
			}
			else {
				$contenido.='
				<div class="col-lg-12">
					<div class="alert alert-danger rounded-0">
						Ocurrió un error al cargar el ajuste de caja. La consulta ejecutada fue:<br>
						<strong>'.$cargarCaja.'</strong><br>
						<a href="javascript:window.history.go(-2)">Volver.</a>
					</div>
				</div>';
			}
		}
		else {
			$contenido.='<div class="col-lg-12">
							<div class="alert alert-danger rounded-0">
								Los datos ingresados son incorrectos. <strong>Sólo se admiten números. </strong>
								<a href="javascript:window.history.go(-1)">Volver.</a>
							</div>
						</div>';
		}
	}
	elseif(isset($_GET['nueva'])) {
		if(@$_POST['cargar']=='Cargar Datos') {
			$id_punto_venta = $_SESSION['login']['punto_venta']['id'];
			$iniciales=mysqli_fetch_assoc(mysqli_query($conexion,"SELECT * FROM cajav WHERE id_punto_venta = $id_punto_venta ORDER BY fecha DESC LIMIT 1"));
			$fecha=date("Y-m-d H:i:s");
			$concepto='Caja';
			$inicial=$iniciales['final'];
			
			$vInicial=$iniciales['vFinal'];
			if(is_numeric($_POST['importeVirtual'])) $vVentas=number_format($_POST['importeVirtual'],2,".","");
			else $vVentas='0.00';
			if(!is_numeric($_POST['cantidadVirtual'])) $_POST['cantidadVirtual']='0';
			if(!is_numeric($_POST['cantidadDTV'])) $_POST['cantidadDTV']='0';
			$vCantidad=$_POST['cantidadVirtual']+$_POST['cantidadDTV'];
			$vAdicionales=number_format((($_POST['cantidadVirtual']*5)+($_POST['cantidadDTV']*5)),2,".","");
			if(is_numeric($_POST['depositoVirtual'])) $vDeposito=number_format($_POST['depositoVirtual'],2,".","");
			else $vDeposito='0.00';
			if(is_numeric($_POST['comisionesVirtual'])) $vComisiones=number_format($_POST['comisionesVirtual'],2,".","");
			else $vComisiones='0.00';
			$vFinal=number_format(($vInicial-$vVentas+$vDeposito+$vComisiones),2,".","");
			
			$cInicial=$iniciales['cFinal'];
			if(is_numeric($_POST['importeClaro'])) $cVentas=number_format($_POST['importeClaro'],2,".","");
			else $cVentas='0.00';
			if(!is_numeric($_POST['cantidadClaro'])) $_POST['cantidadClaro']='0';
			$cCantidad=$_POST['cantidadClaro'];
			$cAdicionales=number_format(($_POST['cantidadClaro']*5),2,".","");
			if(is_numeric($_POST['depositoClaro'])) $cDeposito=number_format($_POST['depositoClaro'],2,".","");
			else $cDeposito='0.00';
			if(is_numeric($_POST['comisionesClaro'])) $cComisiones=number_format($_POST['comisionesClaro'],2,".","");
			else $cComisiones='0.00';
			$cFinal=number_format(($cInicial-$cVentas+$cDeposito+$cComisiones),2,".","");
			
			$sInicial=$iniciales['sFinal'];
			if(is_numeric($_POST['importeSube'])) $sVentas=number_format($_POST['importeSube'],2,".","");
			else $sVentas='0.00';
			if(!is_numeric($_POST['cantidadSube'])) $_POST['cantidadSube']='0';
			$sCantidad=$_POST['cantidadSube'];
			if(is_numeric($_POST['depositoSube'])) $sDeposito=number_format($_POST['depositoSube'],2,".","");
			else $sDeposito='0.00';
			$sFinal=number_format(($sInicial-$sVentas+$sDeposito),2,".","");
			
			$eInicial=$iniciales['eFinal'];
			$eAjuste='0.00';
			$eFinal=number_format(($eInicial+$vVentas+$cVentas+$sVentas-$vDeposito-$cDeposito-$sDeposito+$vAdicionales+$cAdicionales),2,".","");
			
			$final=number_format(($vFinal+$cFinal+$sFinal+$eFinal),2,".","");
			$diferencia=number_format(($final-$inicial),2,".","");
			$tipo=1;
			$id_usuario = $_SESSION['login']['id'];
			
			$cargarCaja="INSERT INTO cajav 	(fecha, concepto, inicial, vInicial, vVentas, vCantidad, vAdicionales, vDepositos, vComisiones, vFinal, cInicial, cVentas, cCantidad,
											cAdicionales, cDepositos, cComisiones, cFinal, sInicial, sVentas, sCantidad, sDepositos, sFinal, eInicial, eAjuste, eFinal, final, diferencia, tipo, id_usuario, id_punto_venta) 
								VALUES 		('$fecha','$concepto','$inicial','$vInicial','$vVentas','$vCantidad','$vAdicionales','$vDeposito','$vComisiones','$vFinal','$cInicial',
											'$cVentas','$cCantidad','$cAdicionales','$cDeposito','$cComisiones','$cFinal','$sInicial','$sVentas','$sCantidad','$sDeposito','$sFinal',
											'$eInicial','$eAjuste','$eFinal','$final','$diferencia','$tipo','$id_usuario', '$id_punto_venta') ";
			if(mysqli_query($conexion,$cargarCaja)) {
				$id = mysqli_insert_id($conexion);
				$actualizarCaja = "UPDATE cajav SET concepto = 'Caja N° $id' WHERE id = '$id'";
				if(mysqli_query($conexion, $actualizarCaja)) {
					echo '<script>window.location="index.php?menu=estadisticas&opc=cajaV"</script>';
				}
				else {
					$contenido.='
				<div class="col-lg-12">
					<div class="alert alert-danger rounded-0">
						Ocurrió un error al actualizar los datos de la caja. La consulta ejecutada fue:<br>
						<strong>'.$actualizarCaja.'</strong><br>
						<a href="javascript:window.history.go(-2)">Volver.</a>
					</div>
				</div>';
				}
			}
			else {
				$contenido.='
				<div class="col-lg-12">
					<div class="alert alert-danger rounded-0">
						Ocurrió un error al cargar la caja. La consulta ejecutada fue:<br>
						<strong>'.$cargarCaja.'</strong><br>
						<a href="javascript:window.history.go(-2)">Volver.</a>
					</div>
				</div>';
			}
		}
		else {
			$contenido.='
			<div class="col-lg-12">
				<form method="post" action="index.php?menu=estadisticas&opc=cajaV&nueva=nueva" name="cargarVirtuales" id="cargarVirtuales">
				<table class="table table-striped responsive-table table-hover table-bordered">
					<thead>
						<tr>
							<th>Detalle</th>
							<th>Cantidad</th>
							<th>Importe</th>
							<th>Comisiones</th>
							<th>Depósitos</th>
						</tr>
					</thead>
					<tbody>
						<tr>
							<td>Virtual</td>
							<td style="max-width:150px;">
								Virtual: <input style="width:45px;" class="formW" type="text" name="cantidadVirtual" id="cantidadVirtual" value="" autofocus> &nbsp; 
								DirecTV: <input style="width:45px;" class="formW" type="text" name="cantidadDTV" id="cantidadDTV" value="0">
							</td>
							<td style="text-align:right;"><input class="formW" type="text" name="importeVirtual" id="importeVirtual" value="0.00"><span style="float:left;">$</span></td>
							<td style="text-align:right;"><input class="formW" type="text" name="comisionesVirtual" id="comisionesVirtual" value="0.00"><span style="float:left;">$</span></td>
							<td style="text-align:right;"><input class="formW" type="text" name="depositoVirtual" id="depositoVirtual" value="0.00"><span style="float:left;">$</span></td>
						</tr>
						<tr>
							<td>Claro</td>
							<td style="text-align:right;"><input class="formW" type="text" name="cantidadClaro" id="cantidadClaro" value="0"></td>
							<td style="text-align:right;"><input class="formW" type="text" name="importeClaro" id="importeClaro" value="0.00"><span style="float:left;">$</span></td>
							<td style="text-align:right;"><input class="formW" type="text" name="comisionesClaro" id="comisionesClaro" value="0.00"><span style="float:left;">$</span></td>
							<td style="text-align:right;"><input class="formW" type="text" name="depositoClaro" id="depositoClaro" value="0.00"><span style="float:left;">$</span></td>
						</tr>
						<tr>
							<td>SUBE</td>
							<td style="text-align:right;"><input class="formW" type="text" name="cantidadSube" id="cantidadSube" value="0"></td>
							<td style="text-align:right;"><input class="formW" type="text" name="importeSube" id="importeSube" value="0.00"><span style="float:left;">$</span></td>
							<td style="text-align:right;"><input class="formW" type="text" value="-" disabled></td>
							<td style="text-align:right;"><input class="formW" type="text" name="depositoSube" id="depositoSube" value="0.00"><span style="float:left;">$</span></td>
						</tr>
						
					</tbody>
				</table>
					<div class="col-lg-10"></div>
					<div class="col-lg-2" style="text-align:right;"><input type="submit" class="btn btn-success" value="Cargar Datos" name="cargar" id="cargar"></div>
				</form>
			</div>
			';
		}
	}
	elseif(isset($_GET['id'])) {
		$id=$_GET['id'];
		$contenido.='
		<div class="col-lg-6">
			<table class="table table-striped responsive-table table-hover table-bordered">
				<thead>
					<tr>
						<th colspan="6" style="text-align:center;">SALDOS</th>
						<th style="text-align:center;"><a href="index.php?menu=estadisticas&opc=cajaV&nueva=nueva"><i class="fa fa-plus"></i></a></th>
					</tr>
					<tr>
						<th>Fecha</th>
						<th>Virtual</th>
						<th>Claro</th>
						<th>SUBE</th>
						<th>Efectivo</th>
						<th>Total</th>
						<th><i class="fa fa-angle-double-right"></i></th>
					</tr>
				</thead>
				<tbody>';
		while($mostrar=mysqli_fetch_assoc($datos)) {
			$fecha=explode(" ",$mostrar['fecha']);
			$nFecha=explode("-",$fecha[0]);
			$fecha=$nFecha[2].'-'.$nFecha[1];
			$contenido.='
					<tr>
						<td>'.$fecha.'</td>
						<td style="text-align:right;">'.$mostrar['vFinal'].'<span style="float:left;"">$</span></td>
						<td style="text-align:right;">'.$mostrar['cFinal'].'<span style="float:left;"">$</span></td>
						<td style="text-align:right;">'.$mostrar['sFinal'].'<span style="float:left;"">$</span></td>
						<td style="text-align:right;">'.$mostrar['eFinal'].'<span style="float:left;"">$</span></td>
						<td style="text-align:right;">'.$mostrar['final'].'<span style="float:left;"">$</span></td>';
			if($mostrar['id']==$_GET['id']) {
				$contenido.='
						<th style="text-align:center;"><a href="index.php?menu=estadisticas&opc=cajaV"><i class="fa fa-angle-double-left"></i></a></th>';
			}
			else {
				$contenido.='
						<th style="text-align:center;"><a href="index.php?menu=estadisticas&opc=cajaV&id='.$mostrar['id'].'"><i class="fa fa-angle-double-right"></i></a></th>';
			}
			$contenido.='
					</tr>';
		}		
		$contenido.='	
				</tbody>
			</table>
		</div>';
		$id_punto_venta = $_SESSION['login']['punto_venta']['id'];
		$buscar = mysqli_query($conexion, "SELECT * FROM cajav WHERE id_punto_venta = $id_punto_venta AND id = $id");
		if(mysqli_num_rows($buscar) == 1) {
			$mostrar=mysqli_fetch_assoc($buscar);
			$contenido.='
		<div class="col-lg-3">
			<table class="table table-striped responsive-table table-hover table-bordered">
				<thead>
					<tr>
						<th colspan="2" style="text-align:center;">VIRTUAL</th>
					</tr>
				</thead>
				<tbody>
					<tr>
						<td>Inicial</td>
						<td style="text-align:right;">'.$mostrar['vInicial'].'<span style="float:left;">$</span></td>
					</tr>
					<tr>
						<td>Ventas</td>
						<td style="text-align:right;">'.$mostrar['vVentas'].'<span style="float:left;">$</span></td>
					</tr>
					<tr>
						<td>Comisiones</td>
						<td style="text-align:right;">'.$mostrar['vComisiones'].'<span style="float:left;">$</span></td>
					</tr>
					<tr>
						<th>Final</th>
						<th style="text-align:right;">'.$mostrar['vFinal'].'<span style="float:left;">$</span></th>
					</tr>
				</tbody>
			</table>
			<table class="table table-striped responsive-table table-hover table-bordered">
				<thead>
					<tr>
						<th colspan="2" style="text-align:center;">CLARO</th>
					</tr>
				</thead>
				<tbody>
					<tr>
						<td>Inicial</td>
						<td style="text-align:right;">'.$mostrar['cInicial'].'<span style="float:left;">$</span></td>
					</tr>
					<tr>
						<td>Ventas</td>
						<td style="text-align:right;">'.$mostrar['cVentas'].'<span style="float:left;">$</span></td>
					</tr>
					<tr>
						<td>Comisiones</td>
						<td style="text-align:right;">'.$mostrar['cComisiones'].'<span style="float:left;">$</span></td>
					</tr>
					<tr>
						<th>Final</th>
						<th style="text-align:right;">'.$mostrar['cFinal'].'<span style="float:left;">$</span></th>
					</tr>
				</tbody>
			</table>
			<table class="table table-striped responsive-table table-hover table-bordered">
				<thead>
					<tr>
						<th colspan="2" style="text-align:center;">SUBE</th>
					</tr>
				</thead>
				<tbody>
					<tr>
						<td>Inicial</td>
						<td style="text-align:right;">'.$mostrar['sInicial'].'<span style="float:left;">$</span></td>
					</tr>
					<tr>
						<td>Ventas</td>
						<td style="text-align:right;">'.$mostrar['sVentas'].'<span style="float:left;">$</span></td>
					</tr>
					<tr>
						<th>Final</th>
						<th style="text-align:right;">'.$mostrar['sFinal'].'<span style="float:left;">$</span></th>
					</tr>
				</tbody>
			</table>
		</div>
		<div class="col-lg-3">
			<table class="table table-striped responsive-table table-hover table-bordered">
				<thead>
					<tr>
						<th colspan="2" style="text-align:center;">EFECTIVO</th>
					</tr>
				</thead>
				<tbody>
					<tr>
						<td>Inicial</td>
						<td style="text-align:right;">'.$mostrar['inicial'].'<span style="float:left;">$</span></td>
					</tr>
					<tr>
						<td>Ventas Virtual</td>
						<td style="text-align:right;">'.$mostrar['vVentas'].'<span style="float:left;">$</span></td>
					</tr>
					<tr>
						<td>Adicionales Virtual</td>
						<td style="text-align:right;">'.$mostrar['vAdicionales'].'<span style="float:left;">$</span></td>
					</tr>
					<tr>
						<td>Deposito Virtual</td>
						<td style="text-align:right;">'.$mostrar['vDepositos'].'<span style="float:left;">$</span></td>
					</tr>
					<tr>
						<td>Ventas Claro</td>
						<td style="text-align:right;">'.$mostrar['cVentas'].'<span style="float:left;">$</span></td>
					</tr>
					<tr>
						<td>Adicionales Claro</td>
						<td style="text-align:right;">'.$mostrar['cAdicionales'].'<span style="float:left;">$</span></td>
					</tr>
					<tr>
						<td>Deposito Claro</td>
						<td style="text-align:right;">'.$mostrar['cDepositos'].'<span style="float:left;">$</span></td>
					</tr>
					<tr>
						<td>Ventas SUBE</td>
						<td style="text-align:right;">'.$mostrar['sVentas'].'<span style="float:left;">$</span></td>
					</tr>
					<tr>
						<td>Deposito SUBE</td>
						<td style="text-align:right;">'.$mostrar['sDepositos'].'<span style="float:left;">$</span></td>
					</tr>
					<tr>
						<th>Final</th>
						<th style="text-align:right;">'.$mostrar['final'].'<span style="float:left;">$</span></th>
					</tr>
				</tbody>
			</table>
		</div>
		';
		}
		else {
			$contenido.='
				<div class="col-lg-6">
					<div class="alert alert-danger rounded-0">
						Ocurrió un error al mostrar la información.<br>
					</div>
				</div>';
		}
	}
	else {
		$contenido.='
		<div class="col-lg-12">
			<table class="table table-striped responsive-table table-hover table-bordered">
				<thead>
					<tr>
						<th colspan="7" style="text-align:center;">SALDOS<span style="float:right;"><a title="Ingresar FC" data-bs-toggle="modal" data-bs-target="#ingresarFCV" href="#"><i class="fa fa-hand-holding-usd"></i></a></span></th>
						<th style="text-align:center;"><a href="index.php?menu=estadisticas&opc=cajaV&nueva=nueva"><i class="fa fa-plus"></i></a></th>
					</tr>
					<tr>
						<th>Fecha</th>
						<th>Concepto</th>
						<th>Virtual</th>
						<th>Claro</th>
						<th>SUBE</th>
						<th>Efectivo</th>
						<th>Total</th>
						<th style="text-align:center;"><i class="fa fa-angle-double-right"></i></th>
					</tr>
				</thead>
				<tbody>';
		while($mostrar=mysqli_fetch_assoc($datos)) {
			$contenido.='
					<tr>
						<td>'.$mostrar['fecha'].'</td>
						<td>'.$mostrar['concepto'].'</td>
						<td style="text-align:right;">'.$mostrar['vFinal'].'<span style="float:left;"">$</span></td>
						<td style="text-align:right;">'.$mostrar['cFinal'].'<span style="float:left;"">$</span></td>
						<td style="text-align:right;">'.$mostrar['sFinal'].'<span style="float:left;"">$</span></td>
						<td style="text-align:right;">'.$mostrar['eFinal'].'<span style="float:left;"">$</span></td>
						<td style="text-align:right;">'.$mostrar['final'].'<span style="float:left;"">$</span></td>
						<th style="text-align:center;"><a href="index.php?menu=estadisticas&opc=cajaV&id='.$mostrar['id'].'"><i class="fa fa-angle-double-right"></i></a></th>
					</tr>
			';
		}		
		$contenido.='	
				</tbody>
			</table>
		</div>
		';
	}
}
else {
	$id_punto_venta = $_SESSION['login']['punto_venta']['id'];
	$datos = mysqli_query($conexion, "SELECT SUM(efectivo) AS efectivo, SUM(tarjetas) AS tarjetas, SUM(c_corriente) AS c_corriente, SUM(total) AS ventas, SUM(c1) AS almacen, SUM(c2) AS verduleria, SUM(c3) AS fiambreria, SUM(c4) AS cigarrillos, DATE(fecha) AS fecha FROM ventas WHERE id_punto_venta = $id_punto_venta AND fecha BETWEEN DATE_SUB(CURDATE(), INTERVAL 7 DAY) AND CURDATE() GROUP BY DATE(fecha)");
	$datos2 = mysqli_query($conexion, "SELECT SUM(importe) AS gastos, DATE(fecha) AS fecha FROM gastos WHERE id_punto_venta = $id_punto_venta AND tipo <> 95 AND fecha BETWEEN DATE_SUB(CURDATE(), INTERVAL 7 DAY) AND CURDATE() GROUP BY DATE(fecha)");

	$ventas='';
	$ventasPromedio='0';
	$gastosPromedio='0';
	$almacen='';
	$verduleria='';
	$fiambreria='';
	$cigarrillos='';
	$efectivo='';
	$tarjetas='';
	$c_corriente='';
	$fecha='';
	$gastos='';

	while($mostrarDatos=mysqli_fetch_assoc($datos)) {
	$ventas.=$mostrarDatos['ventas'].',';
	$ventasPromedio=$ventasPromedio+$mostrarDatos['ventas']; 
	$almacen.=$mostrarDatos['almacen'].',';
	$verduleria.=$mostrarDatos['verduleria'].',';
	$fiambreria.=$mostrarDatos['fiambreria'].',';
	$cigarrillos.=$mostrarDatos['cigarrillos'].',';
	$efectivo.=$mostrarDatos['efectivo'].',';
	$tarjetas.=$mostrarDatos['tarjetas'].',';
	$c_corriente.=$mostrarDatos['c_corriente'].',';

	$dia=date("N",strtotime($mostrarDatos['fecha']));
	if($dia==1) { $dia="Lun"; }
	elseif($dia==2) { $dia="Mar"; }
	elseif($dia==3) { $dia="Mie"; }
	elseif($dia==4) { $dia="Jue"; }
	elseif($dia==5) { $dia="Vie"; }
	elseif($dia==6) { $dia="Sab"; }
	elseif($dia==7) { $dia="Dom"; }
	$dia2=explode("-",$mostrarDatos['fecha']);
	$dia3=$dia.' '.$dia2[2];
	$fecha.='"'.$dia3.'",';
	}
	while($mostrarDatos=mysqli_fetch_assoc($datos2)) {
	$gastos.=$mostrarDatos['gastos'].',';
	$gastosPromedio=$gastosPromedio+$mostrarDatos['gastos']; 
	}

	$ventasPromedio=number_format(($ventasPromedio/7),2,".","");
	$ventasPromedio=$ventasPromedio.','.$ventasPromedio.','.$ventasPromedio.','.$ventasPromedio.','.$ventasPromedio.','.$ventasPromedio.','.$ventasPromedio;
	$gastosPromedio=number_format(($gastosPromedio/7),2,".","");
	$gastosPromedio=$gastosPromedio.','.$gastosPromedio.','.$gastosPromedio.','.$gastosPromedio.','.$gastosPromedio.','.$gastosPromedio.','.$gastosPromedio;
	$contenido.= '
	<div class="row">
		<div class="col-lg-12">
			<div class="col-lg-6">
				<canvas id="ventasGastos"></canvas>
			</div>
			<div class="col-lg-6">
				<canvas id="categorias"></canvas>
			</div>
			<div class="col-lg-6">
				<canvas id="ventasGastosPromedio"></canvas>
			</div>
			<div class="col-lg-6">
				<canvas id="ventasFormaPago"></canvas>
			</div>
		</div>
	</div>

	<script>
		var ventasGastos = {
			type: "line",
			data: {
				labels: ['.$fecha.'],
				datasets: [{
					label: "Ventas",
					backgroundColor: window.chartColors.red,
					borderColor: window.chartColors.red,
					data: [
						'.$ventas.'
					],
					fill: false,
				}, {
					label: "Gastos",
					fill: false,
					backgroundColor: window.chartColors.blue,
					borderColor: window.chartColors.blue,
					data: [
						'.$gastos.'
					],
				}]
			},
			options: {
				responsive: true,
				title: {
					display: true,
					text: "Ventas VS Gastos (Ultima semana)"
				},
				tooltips: {
					mode: "index",
					intersect: false,
				},
				hover: {
					mode: "nearest",
					intersect: true
				},
				scales: {
					xAxes: [{
						display: true,
						scaleLabel: {
							display: true,
							labelString: "Fecha"
						}
					}],
					yAxes: [{
						display: true,
						scaleLabel: {
							display: true,
							labelString: "$"
						}
					}]
				}
			}
		};
		
		var ventasGastosPromedio = {
			type: "line",
			data: {
				labels: ['.$fecha.'],
				datasets: [{
					label: "Ventas",
					backgroundColor: window.chartColors.red,
					borderColor: window.chartColors.red,
					data: [
						'.$ventasPromedio.'
					],
					fill: false,
				}, {
					label: "Gastos",
					fill: false,
					backgroundColor: window.chartColors.blue,
					borderColor: window.chartColors.blue,
					data: [
						'.$gastosPromedio.'
					],
				}]
			},
			options: {
				responsive: true,
				title: {
					display: true,
					text: "Ventas VS Gastos Promedio (Ultima semana)"
				},
				tooltips: {
					mode: "index",
					intersect: false,
				},
				hover: {
					mode: "nearest",
					intersect: true
				},
				scales: {
					xAxes: [{
						display: true,
						scaleLabel: {
							display: true,
							labelString: "Fecha"
						}
					}],
					yAxes: [{
						display: true,
						scaleLabel: {
							display: true,
							labelString: "$"
						}
					}]
				}
			}
		};
		
		var categorias = {
			type: "line",
			data: {
				labels: ['.$fecha.'],
				datasets: [{
					label: "Almacen",
					backgroundColor: window.chartColors.red,
					borderColor: window.chartColors.red,
					data: [
						'.$almacen.'
					],
					fill: false,
				}, {
					label: "Verduleria",
					fill: false,
					backgroundColor: window.chartColors.blue,
					borderColor: window.chartColors.blue,
					data: [
						'.$verduleria.'
					],
				}, {
					label: "Fiambreria",
					fill: false,
					backgroundColor: window.chartColors.grey,
					borderColor: window.chartColors.grey,
					data: [
						'.$fiambreria.'
					],
				}, {
					label: "Cigarrillos",
					fill: false,
					backgroundColor: window.chartColors.orange,
					borderColor: window.chartColors.orange,
					data: [
						'.$cigarrillos.'
					],
				}]
			},
			options: {
				responsive: true,
				title: {
					display: true,
					text: "Ventas por categoria (Ultima semana)"
				},
				tooltips: {
					mode: "index",
					intersect: false,
				},
				hover: {
					mode: "nearest",
					intersect: true
				},
				scales: {
					xAxes: [{
						display: true,
						scaleLabel: {
							display: true,
							labelString: "Fecha"
						}
					}],
					yAxes: [{
						display: true,
						scaleLabel: {
							display: true,
							labelString: "$"
						}
					}]
				}
			}
		};

		var ventasFormaPago = {
			type: "line",
			data: {
				labels: ['.$fecha.'],
				datasets: [{
					label: "Efectivo",
					backgroundColor: window.chartColors.red,
					borderColor: window.chartColors.red,
					data: [
						'.$efectivo.'
					],
					fill: false,
				}, {
					label: "Tarjeta",
					fill: false,
					backgroundColor: window.chartColors.blue,
					borderColor: window.chartColors.blue,
					data: [
						'.$tarjetas.'
					],
				}, {
					label: "Cuenta Corriente",
					fill: false,
					backgroundColor: window.chartColors.grey,
					borderColor: window.chartColors.grey,
					data: [
						'.$c_corriente.'
					],
				}]
			},
			options: {
				responsive: true,
				title: {
					display: true,
					text: "Ventas por forma de pago (Ultima semana)"
				},
				tooltips: {
					mode: "index",
					intersect: false,
				},
				hover: {
					mode: "nearest",
					intersect: true
				},
				scales: {
					xAxes: [{
						display: true,
						scaleLabel: {
							display: true,
							labelString: "Fecha"
						}
					}],
					yAxes: [{
						display: true,
						scaleLabel: {
							display: true,
							labelString: "$"
						}
					}]
				}
			}
		};
		
		window.onload = function() {
			var gra1 = document.getElementById("ventasGastos").getContext("2d");
			var gra2 = document.getElementById("categorias").getContext("2d");
			var gra3 = document.getElementById("ventasGastosPromedio").getContext("2d");
			var gra4 = document.getElementById("ventasFormaPago").getContext("2d");
			window.myLine = new Chart(gra1, ventasGastos);
			window.myLine = new Chart(gra2, categorias);
			window.myLine = new Chart(gra3, ventasGastosPromedio);
			window.myLine = new Chart(gra4, ventasFormaPago);
		};

		var colorNames = Object.keys(window.chartColors);
		document.getElementById("addDataset").addEventListener("click", function() {
			var colorName = colorNames[config.data.datasets.length % colorNames.length];
			var newColor = window.chartColors[colorName];
			var newDataset = {
				label: "Dataset " + config.data.datasets.length,
				backgroundColor: newColor,
				borderColor: newColor,
				data: [],
				fill: false
			};

			for (var index = 0; index < config.data.labels.length; ++index) {
				newDataset.data.push(randomScalingFactor());
			}

			config.data.datasets.push(newDataset);
			window.myLine.update();
		});

	</script>';
}
?>
<div class="box">
	<header>
		<div class="icons iconsW">
			<a style="color:#333;" title="Inicio" class="btn-lg" href="index.php?menu=estadisticas">
				<i class="fa fa-home"></i>
				<span class="menuW">Inicio</span>
			</a>
		</div>
		<div class="icons iconsW">
			<a style="color:#333;" title="Ver Cajas" class="btn-lg" href="index.php?menu=estadisticas&opc=cajas">
				<i class="far fa-clipboard"></i>
				<span class="menuW">Ver Cajas</span>
			</a>
		</div>
		<div class="icons iconsW">
			<a style="color:#333;" title="Caja General" class="btn-lg" href="index.php?menu=estadisticas&opc=cajaZ">
				<i class="fa fa-clipboard-list"></i>
				<span class="menuW">Caja General</span>
			</a>
		</div>
		<div class="icons iconsW">
			<a style="color:#333;" title="Caja Virtual" class="btn-lg" href="index.php?menu=estadisticas&opc=cajaV">
				<i class="fas fa-clipboard"></i>
				<span class="menuW">Caja Virtual</span>
			</a>
		</div>
	</header>
	<div class="body" style="min-height:400px;">
		<div class="row">
			<?php echo $contenido; ?>
		</div>
	</div>
</div>

<div id="ingresarFC" class="modal fade" data-bs-backdrop="static" tabindex="-1" aria-labelledby="exampleModalLabel" aria-hidden="true">
	<div class="modal-dialog modal-xl">
		<div class="modal-content rounded-0">
			<div class="modal-header">
				<h5 class="modal-title" id="exampleModalLabel">Ingrese el Fondo de Caja actual:</h5>
				<button type="button" class="btn-close rounded-0" data-bs-dismiss="modal" aria-label="Close"></button>
			</div>
			<div class="modal-body">
				<form class="row mb-3" name="fondoCaja" id="fondoCaja" method="get" action="" autocomplete="off">
					<div class="col-lg-2"></div>
					<div class="col-lg-8">
						<input type="hidden" id="menu" name="menu" value="estadisticas" class="form-control">
						<input type="hidden" id="opc" name="opc" value="cajaZ" class="form-control">
						<input type="number" step="0.01" id="FC" name="FC" value="" class="form-control rounded-0" required>
					</div>
					<div class="col-lg-2"></div>
				</form>
			</div>
		</div>
	</div>
</div>

<div id="ingresarFCV" class="modal fade" data-bs-backdrop="static" tabindex="-1" aria-labelledby="exampleModalLabel" aria-hidden="true">
	<div class="modal-dialog modal-xl">
		<div class="modal-content rounded-0">
			<div class="modal-header">
				<h5 class="modal-title" id="exampleModalLabel">Ingrese el Fondo de Caja actual:</h5>
				<button type="button" class="btn-close rounded-0" data-bs-dismiss="modal" aria-label="Close"></button>
			</div>
			<div class="modal-body">
				<form class="row" name="fondoCajaV" id="fondoCajaV" method="get" action="" autocomplete="off">
					<div class="col-lg-2"></div>
					<input type="hidden" id="menu" name="menu" value="estadisticas" class="form-control">
					<input type="hidden" id="opc" name="opc" value="cajaV" class="form-control">
					<div class="col-lg-2" style="text-align:center;">
						<input type="text" id="FCV" name="FCV" value="" class="form-control rounded-0">
						<label for="">Virtual</label>
					</div>
					<div class="col-lg-2" style="text-align:center;">
						<input type="text" id="FCC" name="FCC" value="" class="form-control rounded-0">
						<label for="">Claro</label>
					</div>
					<div class="col-lg-2" style="text-align:center;">
						<input type="text" id="FCS" name="FCS" value="" class="form-control rounded-0">
						<label for="">SUBE</label>
					</div>
					<div class="col-lg-2" style="text-align:center;">
						<input type="text" id="FCE" name="FCE" value="" class="form-control rounded-0">
						<label for="">Efectivo</label>
					</div>
					<div class="col-lg-1">
						<input type="submit" id="cargar" name="cargar" value="Cargar" class="btn btn-success rounded-0">
					</div>
					<div class="col-lg-1"></div>
				</form>
			</div>
		</div>
	</div>
</div>