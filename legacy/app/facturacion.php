<?php

if(isset($_GET['cambiarUser'])) {
	$new_user=$_GET['cambiarUser'];
	$consulta="SELECT * FROM usuarios WHERE id='$new_user'";
	$cliente=mysqli_query($conexion,$consulta);
	if(mysqli_num_rows($cliente)==1) {
		$mostrarCliente=mysqli_fetch_assoc($cliente);
		$_SESSION['cliente']['id']=$mostrarCliente['id'];
		$_SESSION['cliente']['cliente']=str_pad($mostrarCliente['id'],4,"0",STR_PAD_LEFT).' - '.$mostrarCliente['nombre'].' '.$mostrarCliente['apellido'];
		$_SESSION['cliente']['direccion']=$mostrarCliente['domicilio'];
		if($mostrarCliente['cel']==0) { $_SESSION['cliente']['tel']=$mostrarCliente['tel']; }
		else { $_SESSION['cliente']['tel']=$mostrarCliente['cel']; }
		$_SESSION['cliente']['acuerdo']=$mostrarCliente['acuerdo'];
		$_SESSION['cliente']['saldo']=$mostrarCliente['saldo'];
		echo '<script language="javascript">window.location="index.php?menu=facturacion&opc=ventas"</script>;';
	}
}


if (@$_GET['opc']=='ventas') {
	$menu='';
	$contenido='';
	
	if(isset($_GET['guardar'])) {
		
		if($_GET['guardar']=='actual' && isset($_SESSION['ticket'])) {
			if(!isset($_SESSION['guardado'])) {
				$_SESSION['guardado'][1]['ticket']=$_SESSION['ticket'];
				$_SESSION['guardado'][1]['cliente']=$_SESSION['cliente'];
				$_SESSION['guardado'][1]['total']=$_SESSION['total'];
				$_SESSION['guardado'][1]['descuento']=$_SESSION['descuento'] ?? 0.00;
				$_SESSION['guardado'][1]['tipo']=$_SESSION['tipo'] ?? 1;
				$_SESSION['guardado'][1]['vuelto']=$_SESSION['vuelto'] ?? 0.00;
				$_SESSION['guardado'][1]['grupo']=$_SESSION['grupo'];
				$_SESSION['guardado'][1]['direccion']=$_SESSION['direccion'] ?? "";
				
				unset($_SESSION['ticket']);
				unset($_SESSION['cliente']);
				unset($_SESSION['total']);
				unset($_SESSION['descuento']);
				unset($_SESSION['tipo']);
				unset($_SESSION['vuelto']);
				unset($_SESSION['grupo']);
				unset($_SESSION['direccion']);
				echo '<script> window.location="index.php?menu=facturacion&opc=ventas";</script>';
			}
			else {
				if(!isset($_SESSION['guardado'][1])) {
					$_SESSION['guardado'][1]['ticket']=$_SESSION['ticket'];
					$_SESSION['guardado'][1]['cliente']=$_SESSION['cliente'];
					$_SESSION['guardado'][1]['total']=$_SESSION['total'];
					$_SESSION['guardado'][1]['descuento']=$_SESSION['descuento'] ?? 0.00;
					$_SESSION['guardado'][1]['tipo']=$_SESSION['tipo'] ?? 1;
					$_SESSION['guardado'][1]['vuelto']=$_SESSION['vuelto'] ?? 0.00;
					$_SESSION['guardado'][1]['grupo']=$_SESSION['grupo'];
					$_SESSION['guardado'][1]['direccion']=$_SESSION['direccion'] ?? "";
					
					unset($_SESSION['ticket']);
					unset($_SESSION['cliente']);
					unset($_SESSION['total']);
					unset($_SESSION['descuento']);
					unset($_SESSION['tipo']);
					unset($_SESSION['vuelto']);
					unset($_SESSION['grupo']);
					unset($_SESSION['direccion']);
					echo '<script> window.location="index.php?menu=facturacion&opc=ventas";</script>';
				}
				elseif(!isset($_SESSION['guardado'][2])) {
					$_SESSION['guardado'][2]['ticket']=$_SESSION['ticket'];
					$_SESSION['guardado'][2]['cliente']=$_SESSION['cliente'];
					$_SESSION['guardado'][2]['total']=$_SESSION['total'];
					$_SESSION['guardado'][2]['descuento']=$_SESSION['descuento'] ?? 0.00;
					$_SESSION['guardado'][2]['tipo']=$_SESSION['tipo'] ?? 1;
					$_SESSION['guardado'][2]['vuelto']=$_SESSION['vuelto'] ?? 0.00;
					$_SESSION['guardado'][2]['grupo']=$_SESSION['grupo'];
					$_SESSION['guardado'][2]['direccion']=$_SESSION['direccion'] ?? "";
					
					unset($_SESSION['ticket']);
					unset($_SESSION['cliente']);
					unset($_SESSION['total']);
					unset($_SESSION['descuento']);
					unset($_SESSION['tipo']);
					unset($_SESSION['vuelto']);
					unset($_SESSION['grupo']);
					unset($_SESSION['direccion']);
					echo '<script> window.location="index.php?menu=facturacion&opc=ventas";</script>';
				}
				elseif(!isset($_SESSION['guardado'][3])) {
					$_SESSION['guardado'][3]['ticket']=$_SESSION['ticket'];
					$_SESSION['guardado'][3]['cliente']=$_SESSION['cliente'];
					$_SESSION['guardado'][3]['total']=$_SESSION['total'];
					$_SESSION['guardado'][3]['descuento']=$_SESSION['descuento'] ?? 0.00;
					$_SESSION['guardado'][3]['tipo']=$_SESSION['tipo'] ?? 1;
					$_SESSION['guardado'][3]['vuelto']=$_SESSION['vuelto'] ?? 0.00;
					$_SESSION['guardado'][3]['grupo']=$_SESSION['grupo'];
					$_SESSION['guardado'][3]['direccion']=$_SESSION['direccion'] = "";
					
					unset($_SESSION['ticket']);
					unset($_SESSION['cliente']);
					unset($_SESSION['total']);
					unset($_SESSION['descuento']);
					unset($_SESSION['tipo']);
					unset($_SESSION['vuelto']);
					unset($_SESSION['grupo']);
					unset($_SESSION['direccion']);
					echo '<script> window.location="index.php?menu=facturacion&opc=ventas";</script>';
				}
				else {
					echo '<script>alert("Ya existen 3 tickets guardados");</script>';
				}
			}
		}
		elseif($_GET['guardar']=='recuperar1') {
			if(isset($_SESSION['ticket'])) {
				//PREVENCION
				//Guardamos el ticket provisoriamente
				$prov1['ticket']=$_SESSION['ticket'];
				$prov1['cliente']=$_SESSION['cliente'];
				$prov1['total']=$_SESSION['total'];
				$prov1['descuento']=$_SESSION['descuento'];
				$prov1['tipo']=$_SESSION['tipo'];
				$prov1['vuelto']=$_SESSION['vuelto'];
				$prov1['grupo']=$_SESSION['grupo'];
				$prov1['direccion']=$_SESSION['direccion'];
				//Recuperamos el ticket guardado como ticket en curso
				$_SESSION['ticket']=$_SESSION['guardado'][1]['ticket'];
				$_SESSION['cliente']=$_SESSION['guardado'][1]['cliente'];
				$_SESSION['total']=$_SESSION['guardado'][1]['total'];
				$_SESSION['descuento']=$_SESSION['guardado'][1]['descuento'];
				$_SESSION['tipo']=$_SESSION['guardado'][1]['tipo'];
				$_SESSION['vuelto']=$_SESSION['guardado'][1]['vuelto'];
				$_SESSION['grupo']=$_SESSION['guardado'][1]['grupo'];
				$_SESSION['direccion']=$_SESSION['guardado'][1]['direccion'];
				//Devolvemos el provisorio a donde antes estaba el ticket guardado
				
				$_SESSION['guardado'][1]['ticket']=$prov1['ticket'];
				$_SESSION['guardado'][1]['cliente']=$prov1['cliente'];
				$_SESSION['guardado'][1]['total']=$prov1['total'];
				$_SESSION['guardado'][1]['descuento']=$prov1['descuento'];
				$_SESSION['guardado'][1]['tipo']=$prov1['tipo'];
				$_SESSION['guardado'][1]['vuelto']=$prov1['vuelto'];
				$_SESSION['guardado'][1]['grupo']=$prov1['grupo'];
				$_SESSION['guardado'][1]['direccion']=$prov1['direccion'];
				
				// No eliminamos sesiones porque las intercambiamos
				echo '<script> window.location="index.php?menu=facturacion&opc=ventas";</script>';
			}
			else {
				$_SESSION['ticket']=$_SESSION['guardado'][1]['ticket'];
				$_SESSION['cliente']=$_SESSION['guardado'][1]['cliente'];
				$_SESSION['total']=$_SESSION['guardado'][1]['total'];
				$_SESSION['descuento']=$_SESSION['guardado'][1]['descuento'];
				$_SESSION['tipo']=$_SESSION['guardado'][1]['tipo'];
				$_SESSION['vuelto']=$_SESSION['guardado'][1]['vuelto'];
				$_SESSION['grupo']=$_SESSION['guardado'][1]['grupo'];
				$_SESSION['direccion']=$_SESSION['guardado'][1]['direccion'];

				unset($_SESSION['guardado'][1]);
				echo '<script> window.location="index.php?menu=facturacion&opc=ventas";</script>';
			}
		}
		elseif($_GET['guardar']=='recuperar2') {
			if(isset($_SESSION['ticket'])) {
				//PREVENCION
				//Guardamos el ticket provisoriamente
				$prov2['ticket']=$_SESSION['ticket'];
				$prov2['cliente']=$_SESSION['cliente'];
				$prov2['total']=$_SESSION['total'];
				$prov2['descuento']=$_SESSION['descuento'];
				$prov2['tipo']=$_SESSION['tipo'];
				$prov2['vuelto']=$_SESSION['vuelto'];
				$prov2['grupo']=$_SESSION['grupo'];
				$prov2['direccion']=$_SESSION['direccion'];
				//Recuperamos el ticket guardado como ticket en curso
				$_SESSION['ticket']=$_SESSION['guardado'][2]['ticket'];
				$_SESSION['cliente']=$_SESSION['guardado'][2]['cliente'];
				$_SESSION['total']=$_SESSION['guardado'][2]['total'];
				$_SESSION['descuento']=$_SESSION['guardado'][2]['descuento'];
				$_SESSION['tipo']=$_SESSION['guardado'][2]['tipo'];
				$_SESSION['vuelto']=$_SESSION['guardado'][2]['vuelto'];
				$_SESSION['grupo']=$_SESSION['guardado'][2]['grupo'];
				$_SESSION['direccion']=$_SESSION['guardado'][2]['direccion'];
				//Devolvemos el provisorio a donde antes estaba el ticket guardado
				
				$_SESSION['guardado'][2]['ticket']=$prov2['ticket'];
				$_SESSION['guardado'][2]['cliente']=$prov2['cliente'];
				$_SESSION['guardado'][2]['total']=$prov2['total'];
				$_SESSION['guardado'][2]['descuento']=$prov2['descuento'];
				$_SESSION['guardado'][2]['tipo']=$prov2['tipo'];
				$_SESSION['guardado'][2]['vuelto']=$prov2['vuelto'];
				$_SESSION['guardado'][2]['grupo']=$prov2['grupo'];
				$_SESSION['guardado'][2]['direccion']=$prov2['direccion'];
				
				// No eliminamos sesiones porque las intercambiamos
				echo '<script> window.location="index.php?menu=facturacion&opc=ventas";</script>';
			}
			else {
				$_SESSION['ticket']=$_SESSION['guardado'][2]['ticket'];
				$_SESSION['cliente']=$_SESSION['guardado'][2]['cliente'];
				$_SESSION['total']=$_SESSION['guardado'][2]['total'];
				$_SESSION['descuento']=$_SESSION['guardado'][2]['descuento'];
				$_SESSION['tipo']=$_SESSION['guardado'][2]['tipo'];
				$_SESSION['vuelto']=$_SESSION['guardado'][2]['vuelto'];
				$_SESSION['grupo']=$_SESSION['guardado'][2]['grupo'];
				$_SESSION['direccion']=$_SESSION['guardado'][2]['direccion'];

				unset($_SESSION['guardado'][2]);
				echo '<script> window.location="index.php?menu=facturacion&opc=ventas";</script>';
			}
		}
		elseif($_GET['guardar']=='recuperar3') {
			if(isset($_SESSION['ticket'])) {
				//PREVENCION
				//Guardamos el ticket provisoriamente
				$prov3['ticket']=$_SESSION['ticket'];
				$prov3['cliente']=$_SESSION['cliente'];
				$prov3['total']=$_SESSION['total'];
				$prov3['descuento']=$_SESSION['descuento'];
				$prov3['tipo']=$_SESSION['tipo'];
				$prov3['vuelto']=$_SESSION['vuelto'];
				$prov3['grupo']=$_SESSION['grupo'];
				$prov3['direccion']=$_SESSION['direccion'];
				//Recuperamos el ticket guardado como ticket en curso
				$_SESSION['ticket']=$_SESSION['guardado'][3]['ticket'];
				$_SESSION['cliente']=$_SESSION['guardado'][3]['cliente'];
				$_SESSION['total']=$_SESSION['guardado'][3]['total'];
				$_SESSION['descuento']=$_SESSION['guardado'][3]['descuento'];
				$_SESSION['tipo']=$_SESSION['guardado'][3]['tipo'];
				$_SESSION['vuelto']=$_SESSION['guardado'][3]['vuelto'];
				$_SESSION['grupo']=$_SESSION['guardado'][3]['grupo'];
				$_SESSION['direccion']=$_SESSION['guardado'][3]['direccion'];
				//Devolvemos el provisorio a donde antes estaba el ticket guardado
				
				$_SESSION['guardado'][3]['ticket']=$prov3['ticket'];
				$_SESSION['guardado'][3]['cliente']=$prov3['cliente'];
				$_SESSION['guardado'][3]['total']=$prov3['total'];
				$_SESSION['guardado'][3]['descuento']=$prov3['descuento'];
				$_SESSION['guardado'][3]['tipo']=$prov3['tipo'];
				$_SESSION['guardado'][3]['vuelto']=$prov3['vuelto'];
				$_SESSION['guardado'][3]['grupo']=$prov3['grupo'];
				$_SESSION['guardado'][3]['direccion']=$prov3['direccion'];
				
				// No eliminamos sesiones porque las intercambiamos
				echo '<script> window.location="index.php?menu=facturacion&opc=ventas";</script>';
			}
			else {
				$_SESSION['ticket']=$_SESSION['guardado'][3]['ticket'];
				$_SESSION['cliente']=$_SESSION['guardado'][3]['cliente'];
				$_SESSION['total']=$_SESSION['guardado'][3]['total'];
				$_SESSION['descuento']=$_SESSION['guardado'][3]['descuento'];
				$_SESSION['tipo']=$_SESSION['guardado'][3]['tipo'];
				$_SESSION['vuelto']=$_SESSION['guardado'][3]['vuelto'];
				$_SESSION['grupo']=$_SESSION['guardado'][3]['grupo'];
				$_SESSION['direccion']=$_SESSION['guardado'][3]['direccion'];

				unset($_SESSION['guardado'][3]);
				echo '<script> window.location="index.php?menu=facturacion&opc=ventas";</script>';
			}
		}
	}
	if((@$_POST['accion']=='Finalizar (F9)')) {
		$efectivo = is_numeric($_POST['efectivo']) ? number_format($_POST['efectivo'], 2, '.', '') : "0.00";
		$tarjetas = is_numeric($_POST['tarjetas']) ? number_format($_POST['tarjetas'], 2, '.', '') : "0.00";
		$c_corriente = is_numeric($_POST['c_corriente']) ? number_format($_POST['c_corriente'], 2, '.', '') : "0.00";
		$vuelto = is_numeric($_POST['vuelto']) ? number_format($_POST['vuelto'], 2, '.', '') : "0.00";

		$pagoTotal = number_format(($efectivo + $tarjetas + $c_corriente + 10), 2, ".", "");
		$total = (number_format($_SESSION['total'], 2, '.', '')) + (number_format(@$_SESSION['descuento'], 2, '.', ''));
		
		if($total == 0.00) {
			$efectivo = $c_corriente = $tarjetas = $vuelto = $saldo = '0.00';
		} elseif($total < 0.00) {
			if(@$cliente == '1') {
				$efectivo = $total;
				$c_corriente = $tarjetas = $vuelto = $saldo = '0.00';
			} else {
				$c_corriente = $total;
				$efectivo = $tarjetas = $vuelto = $saldo = '0.00';
			}
			
		} else {
			$vueltoR = $efectivo + $tarjetas + $c_corriente - $total;
			if ($vuelto == $vueltoR) { 
				if ($vuelto >= 0) {
					$saldo = '0.00';
				} else {
					$saldo = $vuelto; 
				}
			} else { 
				$saldo = $vueltoR - $vuelto; 
			}
		}
			
		if($efectivo == '0.00' && $tarjetas == '0.00' && $c_corriente == '0.00' && $total > 0) {
			echo '	<script>
						alert("No se ingreso el pago!!");
						window.location=window.location.href;
					</script>';
		} elseif($pagoTotal < $total && $total > 0) {
			echo '	<script>
						alert("El total de pagos ingresados no es suficiente para cubrir el costo de la compra.\nLa tolerancia máxima es de $ 10.00.\nDe ser necesario deberá registrar un Usuario.");
						window.location=window.location.href;
					</script>';
		} elseif($saldo > 20) {
			echo '	<script>
						alert("El vuelto no puede ser mayor a 20.00.\n¿Todo esta siendo cargado correctamente?.");
						window.location=window.location.href;
					</script>';
		} elseif($tarjetas > 0 && $vuelto > 0) {
			echo '	<script>
						alert("No es posible dar vuelto en efectivo si el pago es realizado con tarjeta.\nPor favor revisa los datos ingresados.");
						window.location=window.location.href;
					</script>';
		} elseif($c_corriente > 0 && $vuelto > 0) {
			echo '	<script>
						alert("No es posible dar vuelto en efectivo si el pago es realizado en cuenta corriente.\nPor favor revisa los datos ingresados.");
						window.location=window.location.href;
					</script>';
		} else {
			// Inicializamos las variables en 0
			$articulos = '';
			$caja1 = 0.00;
			$caja2 = 0.00;
			$caja3 = 0.00;
			$caja4 = 0.00;
			$caja5 = 0.00;
			$caja6 = 0.00;
			$array = $_SESSION['ticket']; 
			// Recuperamos los valores de la sesion y vemos a que area corresponden
			foreach($array as $id => $producto) { 
				$articulos .= $producto['barra'].'/'.$producto['cantidad'].'/'.$producto['descripcion'].'/'.$producto['precio'].'/'.$producto['total'].'*';
				if($producto['id_area']==1) { $caja1=$caja1+$producto['total']; }
				elseif($producto['id_area']==1) { $caja1=$caja1+$producto['total']; }
				elseif($producto['id_area']==2) { $caja2=$caja2+$producto['total']; }
				elseif($producto['id_area']==3) { $caja3=$caja3+$producto['total']; }
				elseif($producto['id_area']==4) { $caja4=$caja4+$producto['total']; }
				elseif($producto['id_area']==5) { $caja5=$caja5+$producto['total']; }
				elseif($producto['id_area']==6) { $caja6=$caja6+$producto['total']; }
				$consultaStock="UPDATE articulos SET existencia=existencia-'".$producto['cantidad']."' WHERE barra='".$producto['barra']."'";
				$actualizarStock=mysqli_query($conexion,$consultaStock);
			} 
			$articulos=trim($articulos,'*');
			$dia=date('Y/m/d');
			$hora=date('H:i:s');
			$fecha=$dia.' '.$hora;
			$subtotal=number_format($_SESSION['total'],2,'.','');
			@$descuento=number_format($_SESSION['descuento'],2,'.','');
			$id_usuario=$_SESSION['login']['id'];
			$cliente=$_SESSION['cliente']['id'];
			
			
			if($total>=0) $tipo=1;
			else $tipo=2;
			$id_punto_venta = $_SESSION['login']['punto_venta']['id'];
			$consulta="INSERT INTO ventas (fecha, articulos, subtotal, descuento, total, efectivo, tarjetas, c_corriente, vuelto, saldo, id_usuario, cliente, c1, c2, c3, c4, c5, c6, tipo, id_punto_venta) VALUES ('$fecha','$articulos','$subtotal','$descuento','$total','$efectivo','$tarjetas','$c_corriente','$vuelto','$saldo','$id_usuario','$cliente','$caja1','$caja2','$caja3','$caja4','$caja5','$caja6','$tipo', '$id_punto_venta')";
			
			//Si hay valor ingresado para cuenta corriente =>
			if($c_corriente!=0) { 
				$saldoCliente=$_SESSION['cliente']['saldo'];
				$acuerdoCliente=$_SESSION['cliente']['acuerdo'];
				//Si el monto de la compra excede el saldo de la cuenta corriente, mostramos error y volvemos atras
				if($acuerdoCliente!=-1 && ($c_corriente+$saldoCliente)>$acuerdoCliente) {
					echo '
						<script>
							alert("El monto de la compra excede el saldo en cuenta corriente.") ;
							window.location = window.location.href ;
						</script>';
				}
				else { 
					//Si el saldo lo permite, ingresamos los datos de la compra
					if(mysqli_query($conexion,$consulta)) {
						$t_numero = mysqli_insert_id($conexion);
						$id_punto_venta = $_SESSION['login']['punto_venta']['id'];
						$_SESSION['t_numero'] = str_pad($id_punto_venta, 4, "0", STR_PAD_LEFT).' - '.str_pad($t_numero, 8, "0", STR_PAD_LEFT);
						$_SESSION['t_fecha']=$dia.' - '.$hora;
						$_SESSION['tipo']='1';
						$_SESSION['vuelto']=$vuelto;
						
						$nuevoSaldo=$c_corriente+$saldoCliente; 
						$_SESSION['cliente']['new_saldo']=$nuevoSaldo;
						$idUsuario=$_SESSION['cliente']['id'];
						if(mysqli_query($conexion,"UPDATE usuarios SET saldo='$nuevoSaldo' WHERE id='$idUsuario'")) {
							echo '<script type="text/javascript">ticket();</script>';
						}
						else {
							echo '
								<script>
									alert("Ocurrio un error al actualizar el saldo de la cuenta corriente.") ;
									window.location = window.location.href ;
								</script>';
						}
					}		
					else {
						echo '
						<script>
							alert("Ocurrio un error al cargar la compra.") ;
							window.location = window.location.href ;
						</script>';
					}
				}
			}
			
			//Sino, si se inserta la consulta correctamente =>
			elseif(mysqli_query($conexion,$consulta)) {
				//Juntamos los datos para el ticket y abrimos la ventana de impresion
				$t_numero = mysqli_insert_id($conexion);
				$id_punto_venta = $_SESSION['login']['punto_venta']['id'];
				$_SESSION['t_numero'] = str_pad($id_punto_venta, 4, "0", STR_PAD_LEFT).' - '.str_pad($t_numero, 8, "0", STR_PAD_LEFT);
				$_SESSION['t_fecha'] = $dia.' - '.$hora;
				$_SESSION['tipo'] = '1';
				$_SESSION['vuelto'] = $vuelto;
				echo '<script type="text/javascript">ticket();</script>'; 
			}
			else {
				echo '
					<script>
						alert("Ocurrio un error al cargar el ticket en la Base de Datos.") ;
						window.location = window.location.href ;
					</script>';
			}
		}
	}
	elseif(@$_POST['accion']=='Siguiente (F9)'){
		$total=(number_format($_SESSION['total'],2,'.',''))+(number_format(@$_SESSION['descuento'],2,'.',''));
		if (empty($_SESSION['ticket'])) { 
			echo '	<script language="javascript">
						alert("¡¡No se ingreso ningun articulo!!");
						window.location="index.php?menu=facturacion&opc=ventas"
					</script>'; 
		} elseif($total <= 0) {
			$menu.= '
			<a class="btn btn-outline-success rounded-0" onclick="document.getElementById(\'siguiente\').submit();" style="font-weight:bold; text-decoration:italic">Finalizar (F9)</a>
			<form style="display:inline;" autocomplete="off" id="descartar" name="descartar" method="post" action="">
				<input id="descarte" class="btn btn-outline-warning rounded-0" type="submit" value="Volver (F10)" style="font-weight:bold; text-decoration:italic">
				<input type="hidden" value="Volver (F10)" name="accion" />
			</form>';
			$contenido.= '
			<form class="row" autocomplete="off" id="siguiente" name="siguiente" method="post" action="">
				<input type="hidden" value="Finalizar (F9)" name="accion">
				<div class="col-lg-3">
					Efectivo: <input class="form-control rounded-0" type="text" id="efectivo" name="efectivo" value="-"  readonly>
				</div>
				<div class="col-lg-3">
					Tarjetas: <input class="form-control rounded-0" type="text" id="tarjetas" name="tarjetas" value="-" readonly>
				</div>
				<div class="col-lg-3">
					Cuenta Corriente: <input class="form-control rounded-0" type="text" id="c_corriente" name="c_corriente" value="-" readonly>
				</div>
				<div class="col-lg-3">
					Vuelto: <input class="form-control rounded-0" type="text" id="vuelto" name="vuelto" value="-" readonly>
				</div>
			</form>';
		} else {
			$menu.= '
			<a class="btn btn-outline-success rounded-0" onclick="document.getElementById(\'siguiente\').submit();" style="font-weight:bold; text-decoration:italic">Finalizar (F9)</a>
			<form style="display:inline;" autocomplete="off" id="descartar" name="descartar" method="post" action="">
				<input id="descarte" class="btn btn-outline-warning rounded-0" type="submit" value="Volver (F10)" style="font-weight:bold; text-decoration:italic">
				<input type="hidden" value="Volver (F10)" name="accion" />
			</form>';
			$a=1;
			$contenido.= '
			<form class="row" autocomplete="off" id="siguiente" name="siguiente" method="post" action="">
				<input type="hidden" value="Finalizar (F9)" name="accion">
				<div class="col-lg-3 col-xs-6">
					Efectivo: <input class="form-control rounded-0" type="text" id="efectivo" name="efectivo" value="" autofocus="autofocus" onKeyUp="sumar();" onclick="sumar();" tabindex="'.$a++.'">
				</div>
				<div class="col-lg-3 col-xs-6">
					Tarjetas: <input class="form-control rounded-0" type="text" id="tarjetas" name="tarjetas" value="0.00" readonly tabindex="'.$a++.'" onmouseover=\'this.style.cursor = "pointer"\' onclick="calcular();" ondblclick="this.readOnly=false" onmouseout="this.readOnly=true" onKeyUp="sumar();">
				</div>
				<div class="col-lg-3 col-xs-6">
					Cuenta Corriente: '; 
					if($_SESSION['cliente']['id']!=1) { $contenido.='<input class="form-control rounded-0" type="text" id="c_corriente" name="c_corriente" value="0.00" readonly tabindex="'.$a++.'" onmouseover=\'this.style.cursor = "pointer"\' onclick="calcular2();" ondblclick="this.readOnly=false" onmouseout="this.readOnly=true">'; }
					else { $contenido.='<input class="form-control rounded-0" type="text" id="c_corriente" name="c_corriente" value="0.00" readonly>'; }
					$contenido.='
				</div>
				<div class="col-lg-3 col-xs-6">
					Vuelto: <input class="form-control btn-primary rounded-0" type="text" id="vuelto" name="vuelto" value="0.00" tabindex="'.$a++.'">
				</div>
			</form>';
		}
	}
	else {
		if(isset($_GET['numero'])) {
			$newBarra=$_GET['cv'];
			$_SESSION['ticket'][$newBarra]['descripcion']=$_SESSION['ticket'][$newBarra]['descripcion'].' '.$_GET['numero'];
			echo '<script language="javascript">window.location="index.php?menu=facturacion&opc=ventas"</script>;';
		}
		if((@$_POST['accion']=='Descartar (F10)')){
			unset($_SESSION['ticket']);
			unset($_SESSION['total']);
			unset($_SESSION['descuento']);
			unset($_SESSION['cliente']);
			unset($_SESSION['grupo']);
			unset($_SESSION['direccion']);
			echo '<script language="javascript">window.location="index.php?menu=facturacion&opc=ventas"</script>;';
		}
		//REVISAR SESION GRUPO
		//REVISAR COMBO
		elseif (@$_POST['accion'] == 'Eliminar'){
			$barra = $_POST['barra'];
			$barra2 = strpos($barra, "OF");
			$combo = strpos($barra, "COMBO");
			
			if($barra2 === FALSE) {
				if($combo === FALSE) {
					$descontar=$_SESSION['ticket'][$barra]['total'];
					$cantidad=$_SESSION['ticket'][$barra]['cantidad'];
					$grupo=$_SESSION['ticket'][$barra]['grupo'];
					@$_SESSION['grupo'][$grupo]['cantidad'] -= $cantidad;
					@$_SESSION['grupo'][$grupo]['importe'] -= $descontar;
					
					unset($_SESSION['ticket'][$barra]);
					$ofbarra='OF'.$barra;
					if (isset($_SESSION['ticket'][$ofbarra])) {
						$descontar2=$_SESSION['ticket'][$ofbarra]['total'];
						unset($_SESSION['ticket'][$ofbarra]);
					}
					$ofgrupo='OF'.$grupo;
					if (isset($_SESSION['ticket'][$ofgrupo])) {
						$descontar3=$_SESSION['ticket'][$ofgrupo]['total'];
						unset($_SESSION['ticket'][$ofgrupo]);
						comprobarOfertaGrupo($grupo,$_SESSION['grupo'][$grupo],$conexion);
					}
					@$_SESSION['total'] = $_SESSION['total']-$descontar;	
					@$_SESSION['descuento'] = $_SESSION['descuento']-$descontar2-@$descontar3;
				}
				else {
					$descontar2=$_SESSION['ticket'][$barra]['total'];
					unset($_SESSION['ticket'][$barra]);
					@$_SESSION['descuento'] = $_SESSION['descuento']-$descontar2;	
				}
			}
			else {
				$descontar2 = $_SESSION['ticket'][$barra]['total'];
				unset($_SESSION['ticket'][$barra]);
				@$_SESSION['descuento'] = $_SESSION['descuento']-$descontar2;	
			}
						
		}
		elseif((@$_POST['accion']=='Cargar') || (isset($_GET['agregar']))){		
			// Cargamos el producto ya sea agregado por insercion (cargar) o desde el listado (agregar)
			if(@$_POST['accion'] == 'Cargar') { 
				$barra = $_POST['busqueda'];
				//Si el codigo es ** lo reemplazamos por 999999999 (codigo de descuento)
				if(strpos($barra, '*')) {
					if ($barra == '**') {
						$barra='999999999';
					} else {
						$separar = explode('*',$barra);
						$_POST['cantidad'] = $separar[0];
						$barra = $separar[1];
					}
				}
			}
			elseif(isset($_GET['agregar'])) { 
				$barra = $_GET['agregar'];
			}

			if (!empty($barra) && $barra != 0) {
				if (strlen($barra) < 7) {
					$query = "	SELECT nombre, id_area, precio, barra, ID, id_grupo 
								FROM articulos 
								WHERE ID = '$barra' AND activo='1'";
				} else {
					$query = "	SELECT a.nombre, a.id_area, a.precio, a.barra, a.ID, a.id_grupo 
								FROM articulos a
									JOIN codigos_barra cb ON a.ID = cb.id_articulo
								WHERE cb.codigo = '$barra' AND activo='1'";
				}

				$consulta = mysqli_query($conexion, $query);
				if (mysqli_num_rows($consulta) != 0) {
					$cantidad = empty($_POST['cantidad']) || $_POST['cantidad'] == 0 ? 1 : $_POST['cantidad'];
					$cantidad = number_format($cantidad, 2, '.', '');

					$art = mysqli_fetch_assoc($consulta);
					$descripcion = $art['nombre'];
					$id_area = $art['id_area'] ?? "";
					$precio = number_format($art['precio'], 2, '.', '');
					$total = $cantidad * $precio;
					$total = number_format($total, 2, '.', '');
					$barra = $art['barra'];
					$mostrarID = $art['ID'];
					$grupo = $art['id_grupo'] ?? "";

					@$_SESSION['total'] += $total;
					if(isset($_SESSION['grupo'][$grupo])) {
						$_SESSION['grupo'][$grupo]['cantidad'] += $cantidad;
						$_SESSION['grupo'][$grupo]['importe'] += $total;
					}
					else {
						$_SESSION['grupo'][$grupo] = array("cantidad" => $cantidad, "importe" => $total);
					}

					if(isset($_SESSION['ticket'][$barra])) {
						$cantidad = $_SESSION['ticket'][$barra]['cantidad'] + $cantidad;
						$cantidad = number_format($cantidad, 2, '.', '');
						$total = $_SESSION['ticket'][$barra]['precio'] * $cantidad;
						$total = number_format($total, 2, '.', '');
						$contenido2 = array("mostrarID" => $mostrarID, "barra" => $barra, "cantidad" => $cantidad, "descripcion" => $descripcion, "precio" => $precio, "total" => $total, "id_area" => $id_area, "grupo" => $grupo);  
						$_SESSION['ticket'][$barra] = $contenido2;  
						comprobarOfertaGrupo($grupo, $_SESSION['grupo'][$grupo], $conexion);
						comprobarOferta($barra, $cantidad, $conexion);
						if(isset($_GET['agregar'])) { echo '<script> window.location.href = "index.php?menu=facturacion&opc=ventas"; </script>'; }						
					}
					else {
						$contenido2 = array("mostrarID" => $mostrarID, "barra" => $barra, "cantidad" => $cantidad, "descripcion" => $descripcion, "precio" => $precio, "total" => $total, "id_area" => $id_area, "grupo" => $grupo);  
						$_SESSION['ticket'][$barra] = $contenido2;  
						comprobarOfertaGrupo($grupo,$_SESSION['grupo'][$grupo],$conexion);
						comprobarOferta($barra,$cantidad,$conexion);
						if(isset($_GET['agregar'])) { echo '<script> window.location.href = "index.php?menu=facturacion&opc=ventas"; </script>'; }
					}
				}
				else {
					echo '	<script type="text/javascript">
								alert("¡¡El codigo no existe en la Base de Datos!!");
							</script>';
				}
			}
			else {
				echo '	<script type="text/javascript">
							alert("¡¡Tenes que ingresar un codigo!!");
						</script>';
			}
		}
		$menu.='
		<form style="display:inline;" autocomplete="off" id="siguiente" name="siguiente" method="post" action="">		
			<input class="btn btn-outline-success rounded-0" type="submit" value="Siguiente (F9)" style="font-weight:bold; text-decoration:italic">
			<input type="hidden" value="Siguiente (F9)" name="accion">
		</form>	
		<form style="display:inline;" autocomplete="off" id="descartar" name="descartar" method="post" action="">
			<input id="descarte" class="btn btn-outline-danger rounded-0" type="submit" value="Descartar (F10)" style="font-weight:bold; text-decoration:italic">
			<input type="hidden" value="Descartar (F10)" name="accion" />
		</form>';
		if(isset($_SESSION['direccion']) && !empty($_SESSION['direccion']))
			$contenido.='<b>DOMICILIO DE ENTREGA: '.$_SESSION['direccion'].'</b>';
		$contenido.= '
		<div class="form-group row">
			<div class="col-lg-2 mb-3 mt-2">Codigo</div>
			<div class="col-lg-1 mb-3 mt-2">Cantidad</div>
			<div class="col-lg-4 mb-3 mt-2">Descripcion</div>
			<div class="col-lg-2 mb-3 mt-2">Precio</div>
			<div class="col-lg-2 mb-3 mt-2">Total</div>
			<div class="col-lg-1 mb-3 mt-2">&nbsp;</div>
		</div>
		<form autocomplete="off" id="" name="cargar" method="post" action="" class="row">
			<div class="col-lg-2">
				<input name="busqueda" id="busqueda" class="form-control rounded-0 mb-3" type="text" onKeyUp="buscar();" autofocus>
			</div>
			<div class="col-lg-1">
				<input name="cantidad" id="cantidad" class="form-control rounded-0 mb-3" placeholder="1.00" type="text">
			</div>
			
			<div class="col-lg-4">
				<input name="descripcion" id="descripcion" class="form-control rounded-0 mb-3" placeholder="" type="text" readonly>
			</div>
			<div class="col-lg-2">
				<input name="precio" id="precio" class="form-control rounded-0 mb-3" placeholder="" type="text" readonly>
			</div>
			
			<div class="col-lg-2">
				<input class="form-control rounded-0 mb-3" placeholder="" type="text" readonly>
			</div>
			<div class="col-lg-1">
				<input type="hidden" name="accion" value="Cargar">
				<button class="btn btn-primary rounded-0" type="submit"><i class="fas fa-check"></i></button>
			</div>
		</form>';
			
		if($array = @$_SESSION['ticket']) {
			$array = array_reverse($array);
			foreach($array as $id => $producto) { 
				$contenido.= '
			<form autocomplete="off" id="" name="eliminar" method="post" action="" class="row">
				<div class="col-lg-2">
					<input type="hidden" name="producto" value="'.$id.'">
					<input type="hidden" name="barra" value="'.$producto['barra'].'">
					<input type="hidden" name="id_area" value="'.@$id_area.'">
					<input type="hidden" name="descontar" value="'.$producto['total'].'">
					<input class="form-control rounded-0 mb-3" type="text"  value="'.$producto['mostrarID'].'" disabled>
				</div>
				<div class="col-lg-1">
					<input class="form-control rounded-0 mb-3" type="text" value="'.$producto['cantidad'].'" disabled>
				</div>
				<div class="col-lg-4">
					<input class="form-control rounded-0 mb-3" type="text" value="'.$producto['descripcion'].'" disabled>
				</div>
				<div class="col-lg-2">
					<input class="form-control rounded-0 mb-3"  type="text" value="'.$producto['precio'].'" disabled>
				</div>
				<div class="col-lg-2">
					<input class="form-control rounded-0 mb-3" type="text" value="'.$producto['total'].'" disabled>
				</div>
				<div class="col-lg-1">
					<input type="hidden" name="accion" value="Eliminar">
					<button class="btn btn-danger rounded-0 mb-3" type="submit"><i class="fas fa-trash"></i></button>
				</div>
			</form>	'; 
			}
		}
	}
?>

	<div class="col-lg-12" style="min-height:500px;">
		<div class="box inverse">
			<header>
				<div class="icons">
					<button class="btn btn-lg btn-outline-light rounded-0" type="button" data-bs-toggle="modal" data-bs-target="#buscarCliente">
						<i class="fa fa-user"></i>
					</button>
				</div>
				<div class="icons">
					<button class="btn btn-lg btn-outline-light rounded-0" type="button" data-bs-toggle="modal" data-bs-target="#insertarCodigo">
						<i class="fa fa-search"></i>
					</button>
				</div>
				<div class="icons">
					<button class="btn btn-lg btn-outline-light rounded-0" type="button" data-bs-toggle="modal" data-bs-target="#direccion">
						<i class="fas fa-home"></i>
					</button>
				</div>
				<?php 
				if(isset($_SESSION['guardado'][1]) && isset($_SESSION['guardado'][2]) && isset($_SESSION['guardado'][3])) {
				echo '
				<div class="icons">
					<button class="btn btn-lg btn-outline-dark rounded-0" type="button" disabled>
						<i class="fas fa-save"></i>
					</button>
				</div>';
				} else {
				echo '
				<div class="icons">
					<a href="index.php?menu=facturacion&opc=ventas&guardar=actual" class="btn btn-lg btn-outline-light rounded-0">
						<i class="fas fa-save"></i>
					</a>
				</div>';
				}
				?>
				<div class="toolbar">
					<nav class="p-3">
					<?php 
					$t_total = isset($_SESSION['total']) ? number_format($_SESSION['total'], 2, '.', '') : "0.00";
					$t_descuento = isset($_SESSION['descuento']) ? number_format($_SESSION['descuento'], 2, '.', '') : "0.00";
					$t_totalDescuento = number_format($t_total + $t_descuento, 2, ".", "");

					echo '	<span class="btn btn-outline-light rounded-0" style="font-weight:bold; font-style:italic;">SUBTOTAL = $ &nbsp;'.$t_total.'</span>
							<span class="btn btn-outline-light rounded-0" style="font-weight:bold; font-style:italic;">DESCUENTO = $ &nbsp;'.$t_descuento.'</span>
							<span class="btn btn-outline-light rounded-0" style="font-weight:bold; font-style:italic;">TOTAL = $ &nbsp;'.$t_totalDescuento.'</span>
							<input type="hidden" value="'.$t_totalDescuento.'" id="total" name="total">';
					echo $menu;
					?>
					</nav>
				</div>
			</header>
			<div id="div-2" class="body">
				<?php echo $contenido; ?>
			</div>
		</div>
	</div>

<?php
} 
elseif (@$_GET['opc']=='gastos') { 
	if(@$_GET['accion']=='nuevo') {
		if(@$_POST['accion']=='cargar') {
			if($_POST['bulto']==0 && $_POST['unidad']==0) {
				echo '	<script>
							alert("No se ingreso cantidad!!");
						</script>';				
			}
			else {
				if($_POST['descripcion']!=0) {
					$id=$_POST['descripcion'];
					$buscar="SELECT * FROM articulos WHERE ID='$id'";
				}
				else {
					echo $_POST['descripcion'];
					if(strlen($_POST['codigo'])<8) { $cod='codigo'; }
					else { $cod='barra'; }
					$codigo=$_POST['codigo'];
					$buscar='SELECT * FROM articulos WHERE '.$cod.'='.$codigo;
				}
				$ejecutar=mysqli_query($conexion,$buscar);
				$articulo=mysqli_fetch_assoc($ejecutar);
				$id=$articulo['ID'];
				$uBulto=$articulo['uBulto'];
				$nombre=$articulo['nombre'];
				$precio=$articulo['lista'];
				$descuento=$articulo['dtoGral'];
				$costo=$articulo['costo'];
				if($_POST['bulto']==0 || $_POST['bulto']=='') { $bulto=0.00; }
				else { $bulto=$_POST['bulto']; }
				if($_POST['unidad']==0 || $_POST['unidad']=='') { $unidad=0.00; }
				else { $unidad=$_POST['unidad']; }
				if($bulto!=0) { $new_bulto=$bulto*$uBulto; $cantidad=$unidad+$new_bulto; }
				else { $cantidad=$unidad; }
				$new_precio=$precio*$uBulto;
				$new_total=$costo*$cantidad;
				$contenido=array('id' => $id, 'unidad' => $unidad, 'nombre' => $nombre, 'bulto' => $bulto, 'precio' => $new_precio, 'descuento' => $descuento, 'total' => $new_total);
				$_SESSION['compra'][$id]=$contenido;
				//echo '<script>window.location=window.location.href</script>';	
			}
		}
		elseif(@$_POST['accion']=='Descartar (F10)'){
			unset($_SESSION['compra']);
			unset($_SESSION['direccion']);
			echo '<script language="javascript">window.location=window.location.href</script>;';
		}
		if(isset($_GET['facturaNumero'])) { $facturaNumero=$_GET['facturaNumero']; }
		else { $facturaNumero='0000-00000000'; }
		if(isset($_GET['fecha'])) { $fecha=$_GET['fecha']; }
		else { $fecha=date("d/m/Y"); }
		if(isset($_GET['otrosDetalle'])) { $otrosDetalle=$_GET['otrosDetalle']; }
		else { $otrosDetalle=''; }
		echo '
		<div class="col-lg-12" style="min-height:630px;">
			<div class="box inverse">
				<header>
					<div class="icons" style="height:50px;"><i style="font-size:20px;line-height:30px;" class="fa fa-th-large"></i></div>
					<div class="icons" style="height:50px;"><a href="index.php?menu=facturacion&opc=gastos&accion=nuevo" style="color:#fff"><i style="font-size:20px;line-height:30px;" class="fa fa-plus"></i></a></div>
					<h5 style="font-size:18px;line-height:25px;">Gastos</h5>
					<h5 style="font-size:18px;line-height:25px;padding:8px;float:right;">
						<form style="display:inline;" autocomplete="off" id="siguiente" name="siguiente" method="post" action="">		
							<input class="btn btn-success" type="submit" value="Siguiente (F9)">
							<input type="hidden" value="Siguiente (F9)" name="accion">
						</form>	
						<form style="display:inline;" autocomplete="off" id="descartar" name="descartar" method="post" action="">
							<input id="descarte" class="btn btn-danger" type="submit" value="Descartar (F10)">
							<input type="hidden" value="Descartar (F10)" name="accion" />
						</form>
					</h5>
					<!-- .toolbar -->
					<div class="toolbar">
							
						<nav style="padding: 8px;">
							<!--<a href="javascript:;" class="btn btn-default collapse-box"><i class="fa fa-minus"></i></a>
							<a href="javascript:;" class="btn btn-default full-box"><i class="fa fa-expand"></i></a>
							<a href="javascript:;" class="btn btn-danger close-box"><i class="fa fa-times"></i></a> -->
						</nav>
					</div>
					
					<!-- /.toolbar -->
				</header>
				<div class="col-lg-12 body">
					<form method="get" id="seleccionarProveedor" name="seleccionarProveedor">
						<input type="hidden" name="menu" value="facturacion">
						<input type="hidden" name="opc" value="gastos">
						<input type="hidden" name="accion" value="nuevo">
						<div class="col-lg-2" style="font-weight:bold;">Proveedor: </div>
						<div class="col-lg-2">
							<select name="id_proveedor" id="id_proveedor" class="form-control chzn-select" onchange="this.form.submit()">';
								$obtenerProveedor=mysqli_query($conexion,"SELECT id, nombre FROM proveedores ORDER BY nombre");
								while($mostrarProveedor=mysqli_fetch_assoc($obtenerProveedor)) {
									if(@$_GET['id_proveedor']==$mostrarProveedor['id']) {
										echo '<option selected value="'.$mostrarProveedor['id'].'">'.$mostrarProveedor['nombre'].'</option>';
									}
									else {
										echo '<option value="'.$mostrarProveedor['id'].'">'.$mostrarProveedor['nombre'].'</option>';
									}	
								}
								echo '
							</select>
						</div>
						<div class="col-lg-2" style="font-weight:bold;">Factura N°: </div>
						<div class="col-lg-3"><input class="form-control" type="text" name="facturaNumero" id="facturaNumero" value="'.$facturaNumero.'" data-mask="9999-99999999" onchange="this.form.submit()"></div>
						<div class="col-lg-1" style="font-weight:bold;">Fecha: </div>
						<div class="col-lg-2"><input class="form-control" type="text" name="fecha" id="fecha" data-mask="99/99/9999" value="'.$fecha.'" onchange="this.form.submit()"></div>
						<div class="col-lg-12">&nbsp;</div>
						<div class="col-lg-2" style="font-weight:bold;">Observaciones: </div>
						<div class="col-lg-7"><input class="form-control" type="text" name="otrosDetalle" id="otrosDetalle" value="'.$otrosDetalle.'" onchange="this.form.submit()"></div>
						<div class="col-lg-1" style="font-weight:bold;">Área:</div>
						<div class="col-lg-2">ALGUNA</div>
					</form>
				</div>';
				if(isset($_GET['proveedor'])) {
					$id='';
					if(isset($_POST['descripcion'])) { $id=$_POST['descripcion']; }
					if(isset($_POST['codigo'])) { $id=$_POST['codigo']; }
					if($id!='') {
						
					}
					$proveedor=$_GET['proveedor'];
					echo '
				<form method="post" action="" name="cargarArticulo" id="cargarArticulo">
					<div class="col-lg-12">
						<div class="col-lg-2" style="font-weight:bold;">Codigo</div>
						<div class="col-lg-1" style="font-weight:bold;">Unidad</div>
						<div class="col-lg-1" style="font-weight:bold;">Bulto</div>
						<div class="col-lg-3" style="font-weight:bold;">Descripcion</div>
						<div class="col-lg-2" style="font-weight:bold;">Precio</div>
						<div class="col-lg-1" style="font-weight:bold;">Descuento</div>
						<div class="col-lg-2" style="font-weight:bold;">Total</div>
						<div class="col-lg-12">&nbsp;</div>
						<div class="col-lg-2"><input class="form-control" type="text" name="codigo" id="codigo" value="" autofocus></div>
						<div class="col-lg-1"><input class="form-control" type="text" name="unidad" id="unidad" value=""></div>
						<div class="col-lg-1"><input class="form-control" type="text" name="bulto" id="bulto" value=""></div>
						<div class="col-lg-3">
							<select name="descripcion" id="descripcion" class="form-control" onchange="this.form.submit()">
								<option value="0" selected>Seleccionar producto...</option>';
								$obtenerArticulos=mysqli_query($conexion,"SELECT ID,nombre FROM articulos WHERE proveedor='$proveedor' ORDER BY nombre");
								while($mostrarArticulos=mysqli_fetch_assoc($obtenerArticulos)) {
									echo '<option value="'.$mostrarArticulos['ID'].'">'.$mostrarArticulos['nombre'].'</option>';
								}
								echo '
							</select>
							<input type="hidden" name="accion" value="cargar">
						</div>
						<div class="col-lg-2"><input class="form-control" type="text" name="precio" id="precio" value="" disabled></div>
						<div class="col-lg-1"><input class="form-control" type="text" name="descuento" id="descuento" value="" disabled></div>
						<div class="col-lg-2">
							<div class="input-group">
								<input class="form-control" type="text" name="total" id="total" value="" disabled>
								<span class="input-group-addon" style="padding:0 !important;color:green;"><button name="accion" id="accion" value="cargar" style="border:none;background:none;"><span class="glyphicon glyphicon-plus"></span></button></span>
							</div>
						</div>
					</div>
				</form>';
					if($array = @$_SESSION['compra']) {
						$array = array_reverse($array);
						foreach($array as $id => $producto) { 
							echo'
					<div class="col-lg-12">
						<div class="col-lg-12">&nbsp;</div>
						<div class="col-lg-2"><input class="form-control" type="text" name="codigo" id="codigo" value="'.$producto['id'].'" readonly></div>
						<div class="col-lg-1"><input class="form-control" type="text" name="unidad" id="unidad" value="'.$producto['unidad'].'" readonly></div>
						<div class="col-lg-1"><input class="form-control" type="text" name="bulto" id="bulto" value="'.$producto['bulto'].'" readonly></div>
						<div class="col-lg-3"><input class="form-control" type="text" name="descripcion" id="descripcion" value="'.$producto['nombre'].'" readonly></div>
						<div class="col-lg-2"><input class="form-control" type="text" name="precio" id="precio" value="'.$producto['precio'].'" readonly></div>
						<div class="col-lg-1"><input class="form-control" type="text" name="descuento" id="descuento" value="'.$producto['descuento'].'" readonly></div>
						<div class="col-lg-2">
							<div class="input-group">
								<input class="form-control" type="text" name="total" id="total" value="'.$producto['total'].'" readonly>
								<span class="input-group-addon" style="padding:0 !important;color:green;"><button name="accion" id="accion" value="cargar" style="border:none;background:none;"><span class="glyphicon glyphicon-plus"></span></button></span>
							</div>
						</div>
					</div>';
						}
					}
				}
				
				echo '
			</div>
		</div>
		';
	}
	elseif(isset($_GET['eliminar'])) { 
		$id = $_GET['eliminar'];
		$id_punto_venta = $_SESSION['login']['punto_venta']['id'];
		$esCaja = mysqli_fetch_assoc(mysqli_query($conexion, "SELECT * FROM gastos WHERE id_punto_venta = $id_punto_venta AND id = $id"));
		if(@$esCaja['cerrada']==1) { $noticia='<div class="alert alert-danger rounded-0">No se puede eliminar un gasto que no corresponda a una <strong>caja activa</strong></div>'; }
		else { 
			if(mysqli_query($conexion, "DELETE FROM gastos WHERE id_punto_venta = $id_punto_venta AND id = $id")) { 
				$noticia='<div class="alert alert-success rounded-0">El gasto se eliminó correctamente.</div>'; 
			}
			else { 
				$noticia='<div class="alert alert-danger rounded-0">Ocurrió un error al tratar de eliminar los datos.</div>';
			}
		}
	}
	else {
		if (isset($_POST['enviar']) && $_POST['enviar']=='Ingresar Gasto') {
			$nombre=explode("-",$_POST['concepto']);
			$tipo=$nombre[0];
			if($nombre[1]=='*') { $nombre=$_POST['otrosDetalle']; }
			else { $nombre=$nombre[1]; }
			$otrosDetalle=@$_POST['otrosDetalle'];
			$fecha=@$_POST['fecha'];
			if($_POST['importe']=='' || $_POST['importe']==0) $importe='0.00';
			else $importe=number_format($_POST['importe'],2,".","");
			if($_POST['id_area']=='' || $_POST['id_area']==0) $id_area='99';
			else $id_area=$_POST['id_area'];
			$facturaNumero=explode("-",@$_POST['facturaNumero']);
			$facturaNumero=@$facturaNumero[0].@$facturaNumero[1];
			$id_usuario=$_SESSION['login']['id'];
			if($importe==0) { $noticia='<div class="alert alert-warning rounded-0">El importe del gasto no puede ser cero</div>'; }
			else {
				$id_punto_venta = $_SESSION['login']['punto_venta']['id'];
				$ingresarGasto = "INSERT INTO gastos (nombre, fecha, tipo, otrosDetalle, importe, id_area, facturaNumero, id_usuario, id_punto_venta) VALUES ('$nombre','$fecha','$tipo','$otrosDetalle','$importe','$id_area','$facturaNumero','$id_usuario', $id_punto_venta)";
				if(mysqli_query($conexion, $ingresarGasto)) $noticia='<div class="alert alert-success rounded-0">Los datos se ingresaron exitosamente</div>';
				else $noticia='<div class="alert alert-danger rounded-0">Ocurrio un error al procesar los datos</div>';
			}
		}
	}
	echo'
	<div class="col-lg-12" style="min-height:630px;">
		<div class="box inverse">
			<header>
				<div class="icons" style="height:50px;"><i style="font-size:20px;line-height:30px;" class="fa fa-th-large"></i></div>
				<h5 style="font-size:18px;line-height:25px;">Gastos</h5>
			</header>
			<div id="div-2" class="body">
				<div id="stripedTable" class="row">
					<div class="col-lg-6">
						'.@$noticia.'
						<form name="gastos" id="gastos" method="post" action="index.php?menu=facturacion&opc=gastos" autocomplete="off">
							<div class="row">
								<label for="fecha" class="col-lg-4 fw-bold text-end">Fecha</label>
								<div class="col-lg-8">
									<div class="input-group mb-3">
										<input name="fecha" id="fecha" type="date" class="form-control rounded-0" value="'.date("Y-m-d").'">
									</div>
								</div>
								
								<label for="facturaNumero" class="col-lg-4 fw-bold text-end">Factura</label>
								<div class="col-lg-8">
									<div class="input-group mb-3">
										<input name="facturaNumero" id="facturaNumero" type="text" class="form-control rounded-0" aria-describedby="basic-addon1" data-mask="9999-99999999" value="'.@$facturaNumero.'">
										<span class="input-group-text rounded-0" id="basic-addon1"><i class="fas fa-list"></i></span>
									</div>
								</div>

								<label for="concepto" class="col-lg-4 fw-bold text-end">Concepto</label>
								<div class="col-lg-8">
									<div class="input-group mb-3">
										<select name="concepto" id="concepto" data-placeholder="Concepto" class="form-select rounded-0">
											<optgroup label="Otros">
												<option value="99-*" selected>Otros</option>
												<option value="98-*">Sueldos</option>
												<option value="97-*">Viaticos</option>
												<option value="96-*">Impuestos</option>
											</optgroup>
											<optgroup label="Proveedores">';
											$obtenerProveedor=mysqli_query($conexion,"SELECT id,nombre FROM proveedores ORDER BY nombre");
											while($mostrarProveedor=mysqli_fetch_assoc($obtenerProveedor)) {
												echo '
												<option value="'.$mostrarProveedor['id'].'-'.$mostrarProveedor['nombre'].'">'.$mostrarProveedor['nombre'].'</option>';
											}	
											echo '
											</optgroup>
										</select>
									</div>
								</div>

								<label for="otrosDetalle" class="col-lg-4 fw-bold text-end">Detalle</label>
								<div class="col-lg-8">
									<div class="input-group mb-3">
										<input type="text" id="otrosDetalle" name="otrosDetalle" value="'.@$otrosDetalle.'" class="form-control rounded-0">
									</div>
								</div>
								
								<label for="importe" class="col-lg-4 fw-bold text-end">Importe</label>
								<div class="col-lg-8">
									<div class="input-group mb-3">
										<input name="importe" id="importe" type="text" class="form-control rounded-0" aria-describedby="basic-addon3" value="'.@$importe.'">
										<span class="input-group-text rounded-0" id="basic-addon3">$</span>
									</div>
								</div>

								<label for="id_area" class="col-lg-4 fw-bold text-end">Área</label>
								<div class="col-lg-8">
									<div class="input-group mb-3">
										<select name="id_area" id="id_area" data-placeholder="Área" class="form-select rounded-0">';
											$result = mysqli_query($conexion, "SELECT id, nombre FROM areas ORDER BY nombre");
											while ($area = mysqli_fetch_assoc($result)) {
												echo '
												<option value="'.$area['id'].'">'.$area['nombre'].'</option>';
											}	
										echo '
									</select>
									</div>
								</div>

								<div class="col-lg-4"></div>
								<div class="col-lg-8">
									<input type="submit" name="enviar" value="Ingresar Gasto" class="btn btn-success rounded-0">
								</div>
							</div>
						</form>
					</div>
					<div class="col-lg-6">
						<table class="table table-striped responsive-table table-hover table-bordered">
							<thead>
								<tr>
									<th>ID</th>
									<th>Detalle</th>
									<th>Importe</th>
									<th>Área</th>
									<th style="text-align:center"><i class="fa fa-trash"></i></th>
								</tr>
							</thead>
							<tbody>';
								$id_punto_venta = $_SESSION['login']['punto_venta']['id'];
								$query = "SELECT g.*, a.nombre as area 
										  FROM gastos g 
											JOIN areas a ON g.id_area = a.id
										  WHERE id_punto_venta = $id_punto_venta AND cerrada = 0";
								$buscarGastos = mysqli_query($conexion, $query);
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
									echo '
								<tr>
									<td title="'.$fecha.'">'.$mostrarGastos['id'].'</td>
									<td title="'.$tipo.'">'.$mostrarGastos['nombre'].'</td>
									<td title="'.$mostrarGastos['otrosDetalle'].'" style="text-align:right;">'.$mostrarGastos['importe'].'</td>
									<td>'.$mostrarGastos['area'].'</td>
									<td style="text-align:center;"><a href="index.php?menu=facturacion&opc=gastos&eliminar='.$mostrarGastos['id'].'" style="color:red;"><i class="fa fa-trash"></i></a></td>
								</tr>';
								}
								$id_punto_venta = $_SESSION['login']['punto_venta']['id'];
								$buscarTotales = mysqli_query($conexion, "SELECT sum(importe) AS gastos FROM gastos WHERE id_punto_venta = $id_punto_venta AND cerrada = 0 AND tipo <> 95");
								$mostrarTotales = mysqli_fetch_assoc($buscarTotales);	
								$buscarRetiros = mysqli_query($conexion, "SELECT sum(importe) AS retiros FROM gastos WHERE id_punto_venta = $id_punto_venta AND cerrada = 0 AND tipo = 95");
								$mostrarRetiros = mysqli_fetch_assoc($buscarRetiros);
					echo '	
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
				</div>
			</div>
		</div>
	</div>'	;
}
elseif (@$_GET['opc'] == 'caja') {
	$menu = '';
	$contenido = '';
	$id_punto_venta = $_SESSION['login']['punto_venta']['id'];
	$query = "SELECT	tipo, 
						COUNT(1) AS cantidad, 
						MIN(fecha) AS primero, 
						MAX(fecha) AS ultimo, 
						SUM(efectivo-vuelto) AS efectivo,
						SUM(tarjetas) AS tarjetas,
						SUM(c_corriente) AS c_corriente,
						SUM(vuelto) AS vuelto,
						SUM(total) AS total,
						SUM(saldo) AS saldo,
						SUM(c2) AS c2,
						SUM(c3) AS c3,
						SUM(c4) AS c4,
						SUM(c5) AS c5,
						SUM(c6) AS c6
			  FROM 		ventas 
			  WHERE 	id_punto_venta = $id_punto_venta AND cerrada = 0 AND tipo IN (1, 2, 3) AND eliminado = 0";
	$data = mysqli_fetch_assoc(mysqli_query($conexion, $query));
	$saldo = $data['saldo'];
	$data['efectivo'] = number_format($data['efectivo'], 2, ".", "");
	if ($saldo > 0) { 
		$saldo = " (<span style='color:green'>+$saldo</span>)"; 
	} elseif ($saldo < 0) { 
		$saldo = " (<span style='color:red'>$saldo</span>)"; 
	} else { 
		$saldo = " ($saldo)"; 
	}

	$query = "SELECT	SUM(importe) total, 
						id_area
			  FROM		gastos 
			  WHERE 	id_punto_venta = $id_punto_venta AND cerrada = 0
			  GROUP BY 	id_area";
	$result = mysqli_query($conexion, $query);
	$almacen = $cargaVirtual = $cigarrillos = $verduleria = $na = $rotiseria = 0.00;
	while ($data2 = mysqli_fetch_assoc($result)) {
		switch ($data2['id_area']) {
			case 1:
				$na = number_format($data2['total'], 2, ".", "");
				break;
			case 2:
				$almacen = number_format($data2['total'], 2, ".", "");
				break;
			case 3:
				$verduleria = number_format($data2['total'], 2, ".", "");
				break;
			case 4:
				$cigarrillos = number_format($data2['total'], 2, ".", "");
				break;
			case 5:
				$cargaVirtual = number_format($data2['total'], 2, ".", "");
				break;
			case 6:
				$na = number_format($data2['total'], 2, ".", "");
				break;
		}
	}

	$query = "SELECT	SUM(importe) total,
						tipo
			  FROM 		gastos 
			  WHERE 	id_punto_venta = $id_punto_venta AND cerrada = 0
			  GROUP BY 	tipo";
	$result = mysqli_query($conexion, $query);
	$proveedores = $otros = $sueldos = $viaticos = $impuestos = $retiros = 0.00;
	while ($data3 = mysqli_fetch_assoc($result)) {
		switch ($data3['tipo']) {
			case 99:
				$otros = number_format($data3['total'], 2, ".", "");
				break;
			case 98:
				$sueldos = number_format($data3['total'], 2, ".", "");
				break;
			case 97:
				$viaticos = number_format($data3['total'], 2, ".", "");
				break;
			case 96:
				$impuestos = number_format($data3['total'], 2, ".", "");
				break;
			case 95:
				$retiros = number_format($data3['total'], 2, ".", "");
				break;
			default:
				$proveedores = number_format($data3['total'], 2, ".", "");
		}
	}

	$totalG = number_format($almacen + $cargaVirtual + $cigarrillos + $verduleria + $na + $rotiseria, 2, ".", "");
	$naR  = number_format(($na - $retiros), 2, ".", "");	
	$disponible = number_format(($data['efectivo'] - $retiros), 2, ".", "");
	
	if(isset($_GET['retiroEfectivo'])) {
		if(is_numeric($_GET['retiroEfectivo'])) {
			$fecha = date('Y-m-d H:i:s');
			$nombre = 'RETIRO EN EFECTIVO';
			$tipo = 95;
			$id_usuario = $_SESSION['login']['id'];
			$importe = number_format($_GET['retiroEfectivo'], 2, ".", "");
			if (mysqli_query($conexion, "INSERT INTO gastos (fecha, nombre, importe, tipo, id_usuario, id_area, id_punto_venta) 
										 VALUES ('$fecha', '$nombre', $importe, $tipo, $id_usuario, 1, $id_punto_venta)")) {
				$id = mysqli_insert_id($conexion);
				echo '	<script>
							ticketRetiro("'.$id.'");
						</script>';
			}
		}
	}
	if(isset($_GET['eliminar'])) {
		$idT = $_GET['eliminar'];
		$buscar = mysqli_query($conexion, "SELECT * FROM ventas WHERE id = $idT AND id_punto_venta = $id_punto_venta");
		$mostrar = mysqli_fetch_assoc($buscar);
		if($mostrar['cerrada'] == 1) {
			echo '<script>alert("No se puede editar el ticket porque la caja que lo contenía ya está cerrada.")</script>';
		}
		else {
			$array = explode("*", $mostrar['articulos']);
			foreach($array as $id => $producto) {
				$art = explode("/",$producto);
				$of='OF';
				$pos = strpos($art[0],$of);
				//Si no es oferta, lo devuelve al stock, sino no es necesario.
				if ($pos === false) {
					$barra=$art[0];
					$cantidad=$art[1];
					$actualizarStock=mysqli_query($conexion,"UPDATE articulos SET existencia=existencia+'$cantidad' WHERE barra='$barra'");
				}
			}
			$actualizarTicket = mysqli_query($conexion,"UPDATE ventas SET eliminado = 1 WHERE id = $idT");
			//Si el cliente NO es CF, le descontamos el monto del ticket al saldo.
			if($mostrar['cliente']!=1){
				$idCliente=$mostrar['cliente'];
				$c_corriente=$mostrar['c_corriente'];
				mysqli_query($conexion,"UPDATE usuarios SET saldo=saldo-'$c_corriente' WHERE id='$idCliente'");
			}
			echo '<script>window.location= "index.php?menu=facturacion&opc=caja"; </script>';
		}
	}		
	if(isset($_GET['restaurar'])) {
		$idT=$_GET['restaurar'];
		$buscar=mysqli_query($conexion,"SELECT * FROM ventas WHERE id='$idT'");
		$mostrar=mysqli_fetch_assoc($buscar);
		if($mostrar['cerrada']==1) {
			echo '<script>alert("No se puede editar el ticket porque la caja que lo contenía ya está cerrada.")</script>';
		}
		else {
			$array=explode("*",$mostrar['articulos']);
			foreach($array as $id => $producto) {
				$art = explode("/",$producto);
				$of='OF';
				$pos = strpos($art[0],$of);
				//Si no es oferta, lo agrega al stock, sino no es necesario.
				if ($pos === false) {
					$barra=$art[0];
					$cantidad=$art[1];
					$actualizarStock=mysqli_query($conexion,"UPDATE articulos SET existencia=existencia+'$cantidad' WHERE barra='$barra'");
				}
			}
			$actualizarTicket=mysqli_query($conexion,"UPDATE ventas SET eliminado = 0 WHERE id = $idT");
			//Si el cliente NO es CF, le sumamos el monto del ticket al saldo.
			if($mostrar['cliente']!=1){
				$idCliente=$mostrar['cliente'];
				$c_corriente=$mostrar['c_corriente'];
				mysqli_query($conexion,"UPDATE usuarios SET saldo=saldo+'$c_corriente' WHERE id='$idCliente'");
			}
			echo '<script>window.location= "index.php?menu=facturacion&opc=caja"; </script>';
		}
	}
	if(isset($_GET['cambiarUsuario'])) {
		if(@$_POST['cambiarUsuario']=='Cambiar Usuario') {
			$uAnterior=$_POST['uAnterior'];
			$uNuevo=$_POST['uNuevo'];
			$ticket=$_POST['ticket'];
			$c_corriente=$_POST['c_corriente'];
			$consulta="UPDATE ventas SET cliente='$uNuevo' WHERE cliente='$uAnterior' AND id='$ticket'";
			$consulta2="UPDATE usuarios SET saldo=saldo+'$c_corriente' WHERE id='$uNuevo'";
			$consulta3="UPDATE usuarios SET saldo=saldo-'$c_corriente' WHERE id='$uAnterior'";
			if(mysqli_query($conexion,$consulta)) {
				if(mysqli_query($conexion,$consulta2)) {
					if(mysqli_query($conexion,$consulta3)) {
						echo '<script>alert("El ticket se ha editado exitosamente.")</script>';
						echo '<script>window.location= "index.php?menu=facturacion&opc=caja"; </script>';
					}
					else {
						echo '<script>alert("Ocurrio un error al actualizar el saldo del usuario anterior.")</script>';
					}
				}
				else {
					echo '<script>alert("Ocurrio un error al actualizar el saldo del usuario destino.")</script>';
				}
			}
			else {
				echo '<script>alert("Ocurrio un error al cambiar el usuario.")</script>';
			}
			
		}
		else {
			$idT = $_GET['cambiarUsuario'];
			$ticket = mysqli_fetch_assoc(mysqli_query($conexion, "SELECT * FROM ventas WHERE id = $idT AND id_punto_venta = $id_punto_venta"));
			if($ticket['cerrada'] == 1) {
				echo '<script>alert("No se puede editar el ticket porque la caja que lo contenía ya está cerrada.")</script>';
				echo '<script>window.location= "index.php?menu=facturacion&opc=caja"; </script>';
			}
			if($ticket['tipo']==3) {
				echo '<script>alert("No se puede reasignar un pago a otra Cuenta Corriente.")</script>';
				echo '<script>window.location= "index.php?menu=facturacion&opc=caja"; </script>';
			}
			else {
				$menuT='Reasignando Cliente. Ticket N°: '.$idT;
				$menu='';
				$array=explode("*",$ticket['articulos']);
				$contenido='
				<div class="col-lg-6">
					<table class="table table-striped responsive-table table-hover table-bordered">
						<thead>
							<tr>
								<th>Código</th>
								<th>Cantidad</th>
								<th>Descripción</th>
								<th>Precio</th>
								<th>Total</th>
							</tr>
						</thead>
						<tbody>';
				foreach($array as $id => $producto) {
					$art = explode("/",$producto);
					$contenido.='
							<tr>
								<td>'.$art[0].'</td>
								<td>'.$art[1].'</td>
								<td>'.$art[2].'</td>
								<td>'.$art[3].'</td>
								<td>'.$art[4].'</td>
							</tr>';
				}
				$idU=$ticket['cliente'];
				$clienteActual=mysqli_fetch_array(mysqli_query($conexion,"SELECT user FROM usuarios WHERE id='$idU'"));
				$contenido.='
							<tr>
								<th colspan="3">SUBTOTAL:</th>
								<td colspan="2" style="text-align:right;">'.$ticket['subtotal'].'<span style="float:left;">$</span></td>
							</tr>
							<tr>
								<th colspan="3">DESCUENTO:</th>
								<td colspan="2" style="text-align:right;">'.$ticket['descuento'].'<span style="float:left;">$</span></td>
							</tr>
							<tr>
								<th colspan="3">TOTAL:</th>
								<td colspan="2" style="text-align:right;">'.$ticket['total'].'<span style="float:left;">$</span></td>
							</tr>
						</tbody>
					</table>
				</div>
				<div class="col-lg-6">
					<form class="form-horizontal" method="post" action="" name="cambiarUsuario" id="cambiarUsuario">
						<div class="form-group">
							<label for="usuarioAnterior" class="control-label col-lg-4">Usuario Actual: </label>
							<div class="col-lg-8">
								<input type="hidden" name="usuarioAnterior" id="usuarioAnterior" value="'.$idU.'">
								<input class="form-control" type="text" value="'.$clienteActual[0].'" disabled>
							</div>
						</div>	
						<div class="form-group">
							<label for="usuarioNuevo" class="control-label col-lg-4">Usuario Nuevo: </label>
							<div class="col-lg-8">
								<select name="usuarioNuevo" id="usuarioNuevo" class="form-control" onchange="this.form.submit();">';
							$obtenerUsuarios=mysqli_query($conexion,"SELECT id,user FROM usuarios ORDER BY user");
							while($mostrarUsuario=mysqli_fetch_assoc($obtenerUsuarios)) {
								if(@$_POST['usuarioNuevo']==$mostrarUsuario['id']) {
									$contenido.='
									<option selected value="'.$mostrarUsuario['id'].'">'.$mostrarUsuario['user'].'</option>';
								}
								else {
									$contenido.='
									<option value="'.$mostrarUsuario['id'].'">'.$mostrarUsuario['user'].'</option>';
								}	
							}	
							$contenido.='
								</select>
							</div>
						</div>	
					</form>';		
				if(isset($_POST['usuarioNuevo'])) {
					$idUN=$_POST['usuarioNuevo'];
					$usuarioNuevo=mysqli_fetch_assoc(mysqli_query($conexion,"SELECT * FROM usuarios WHERE id='$idUN'"));
					$nuevoSaldo=number_format(($ticket['c_corriente']+$usuarioNuevo['saldo']),2,".","");
					$contenido.='
					<form class="form-horizontal" method="post" action="" name="cambiarUsuario2" id="cambiarUsuario2">				
						<div class="form-group">
							<label for="Acuerdo" class="control-label col-lg-4">Acuerdo: </label>
							<div class="col-lg-8">
								<input style="text-align:right;" class="form-control" type="text" value="$ '.$usuarioNuevo['acuerdo'].'" disabled>
							</div>
						</div>	
						<div class="form-group">
							<label for="saldoActual" class="control-label col-lg-4">Saldo Actual: </label>
							<div class="col-lg-8">
								<input style="text-align:right;" class="form-control" type="text" value="$ '.$usuarioNuevo['saldo'].'" disabled>
							</div>
						</div>';
					if($nuevoSaldo>$usuarioNuevo['acuerdo']) {
						$contenido.='
						<div class="form-group">
							<label for="nuevoSaldo" class="control-label col-lg-4">Nuevo Saldo: </label>
							<div class="col-lg-8">
								<input style="text-align:right;background-color:red;color:white;font-weight:bold;" class="form-control" type="text" value="$ '.$nuevoSaldo.'" disabled>
							</div>
						</div>';
					}
					else {
						$contenido.='
						<div class="form-group">
							<label for="nuevoSaldo" class="control-label col-lg-4">Nuevo Saldo: </label>
							<div class="col-lg-8">
								<input style="text-align:right;" class="form-control" type="text" value="$ '.$nuevoSaldo.'" disabled>
							</div>
						</div>
						<div class="form-group">
							<div class="col-lg-6"></div>
							<div class="col-lg-6">
								<input type="hidden" name="uAnterior" value="'.$idU.'">
								<input type="hidden" name="uNuevo" value="'.$idUN.'">
								<input type="hidden" name="ticket" value="'.$idT.'">
								<input type="hidden" name="c_corriente" value="'.$ticket['c_corriente'].'">
								<input name="cambiarUsuario" class="form-control btn btn-success" type="submit" value="Cambiar Usuario">
							</div>
						</div>';
					}
					$contenido.='
					</form>
					';
				}		
				$contenido.='
				</div>
				';
			}
		}
	}	
	elseif (@$_POST['accion'] == 'Ver Parcial') { 
		$menuT = 'Resumen de Tickets';
		$menu = '
			<a href="" class="btn btn-outline-light rounded-0" style="font-weight:bold;font-style:italic;">TOTAL = '.$data['total'].$saldo.'</a>
			<form style="display:inline;" autocomplete="off" id="siguiente" name="siguiente" method="post" action="">		
				<input class="btn btn-outline-warning rounded-0" type="submit" value="Volver">
				<input type="hidden" value="Volver" name="accion">
			</form>	
			<form style="display:inline;" autocomplete="off" id="siguiente" name="siguiente" method="post" action="">		
					<input class="btn btn-outline-success rounded-0" type="submit" value="Cerrar Caja">
					<input type="hidden" value="Cerrar Caja" name="accion">
			</form>	';
		$contenido = '
			<div class="row">
				<div class="col-lg-12" style="text-align:center"><h3>Total Ingresos</h3><hr></div>
				<div class="col-lg-6">
					<table class="table table-striped responsive-table table-hover table-bordered">
						<tbody>
							<tr>
								<th>Total Almacen</th>
								<td style="border-right:none;">$</td>
								<td style="border-left:none;text-align:right;">'.$data['c2'].'</td>
							</tr>
							<tr>
								<th>Total Verduleria</th>
								<td style="border-right:none;">$</td>
								<td style="border-left:none;text-align:right;">'.$data['c3'].'</td>
							</tr>
							<tr>
								<th>Total Cigarrillos</th>
								<td style="border-right:none;">$</td>
								<td style="border-left:none;text-align:right;">'.$data['c4'].'</td>
							</tr>
							<tr>
								<th>Total Carga Virtual</th>
								<td style="border-right:none;"></td>
								<td style="border-left:none;text-align:right;">'.$data['c5'].'</td>
							</tr>
							<tr>
								<th>Total Rotisería</th>
								<td style="border-right:none;"></td>
								<td style="border-left:none;text-align:right;">'.$data['c6'].'</td>
							</tr>
							<tr>
								<th>Total General</th>
								<td style="border-right:none;font-weight:bold;">$</td>
								<td style="border-left:none;text-align:right;font-weight:bold;">'.$data['total'].'</td>
							</tr>
						</tbody>
					</table>
				</div>
				<div class="col-lg-6">
					<table class="table table-striped responsive-table table-hover table-bordered">
						<tbody>
							<tr>
								<th>Total Efectivo</th>
								<td style="border-right:none;">$</td>
								<td style="border-left:none;text-align:right;"> ('.$retiros.') &nbsp; &nbsp; &nbsp; &nbsp; '.$data['efectivo'].'</td>
							</tr>
							<tr>
								<th>Total Tarjetas</th>
								<td style="border-right:none;">$</td>
								<td style="border-left:none;text-align:right;">'.$data['tarjetas'].'</td>
							</tr>
							<tr>
								<th>Total Cuenta Corriente</th>
								<td style="border-right:none;">$</td>
								<td style="border-left:none;text-align:right;">'.$data['c_corriente'].'</td>
							</tr>
							<tr>
								<th>Saldo</th>
								<td style="border-right:none;">$</td>
								<td style="border-left:none;text-align:right;">'.$data['saldo'].'</td>
							</tr>
							<tr>
								<th>Cantidad de Tickets</th>
								<td style="border-right:none;"></td>
								<td style="border-left:none;text-align:right;">'.$data['cantidad'].'</td>
							</tr>
							<tr>
								<th>Primer Ticket</th>
								<td style="border-right:none;"></td>
								<td style="border-left:none;text-align:right;">'.$data['primero'].'</td>
							</tr>
							<tr>
								<th>Ultimo Ticket</th>
								<td style="border-right:none;"></td>
								<td style="border-left:none;text-align:right;">'.$data['ultimo'].'</td>
							</tr>
						</tbody>
					</table>
				</div>
				<div class="col-lg-12" style="text-align:center"><h3>Total Egresos</h3><hr></div>
				<div class="col-lg-6">
					<table class="table table-striped responsive-table table-hover table-bordered">
						<tbody>
							<tr>
								<th>Total Almacen</th>
								<td style="border-right:none;">$</td>
								<td style="border-left:none;text-align:right;">'.$almacen.'</td>
							</tr>
							<tr>
								<th>Total Verduleria</th>
								<td style="border-right:none;">$</td>
								<td style="border-left:none;text-align:right;">'.$verduleria.'</td>
							</tr>
							<tr>
								<th>Total Cigarrillos</th>
								<td style="border-right:none;">$</td>
								<td style="border-left:none;text-align:right;">'.$cigarrillos.'</td>
							</tr>
							<tr>
								<th>Total Carga Virtual</th>
								<td style="border-right:none;">$</td>
								<td style="border-left:none;text-align:right;">'.$cargaVirtual.'</td>
							</tr>
							<tr>
								<th>Total N/A</th>
								<td style="border-right:none;">$</td>
								<td style="border-left:none;text-align:right;">'.$naR.'</td>
							</tr>
						</tbody>
					</table>
				</div>
				<div class="col-lg-6">
					<table class="table table-striped responsive-table table-hover table-bordered">
						<tbody>
							<tr>
								<th>Total Proveedores</th>
								<td style="border-right:none;">$</td>
								<td style="border-left:none;text-align:right;">'.$proveedores.'</td>
							</tr>
							<tr>
								<th>Total Otros</th>
								<td style="border-right:none;">$</td>
								<td style="border-left:none;text-align:right;">'.$otros.'</td>
							</tr>
							<tr>
								<th>Total Sueldos</th>
								<td style="border-right:none;">$</td>
								<td style="border-left:none;text-align:right;">'.$sueldos.'</td>
							</tr>
							<tr>
								<th>Total Viaticos</th>
								<td style="border-right:none;">$</td>
								<td style="border-left:none;text-align:right;">'.$viaticos.'</td>
							</tr>
							<tr>
								<th>Total Impuestos</th>
								<td style="border-right:none;">$</td>
								<td style="border-left:none;text-align:right;">'.$impuestos.'</td>
							</tr>
							<tr>
								<th>Total Retiros</th>
								<td style="border-right:none;">$</td>
								<td style="border-left:none;text-align:right;">'.$retiros.'</td>
							</tr>
							<tr>
								<th>Total General</th>
								<td style="border-right:none;font-weight:bold;">$</td>
								<td style="border-left:none;text-align:right;font-weight:bold;">'.$totalG.'</td>
							</tr>
						</tbody>
					</table>
				</div>
			</div>
			';
	}
	elseif (@$_POST['accion']=='Cerrar Caja') { 
		$menuT = 'Cerrar Caja';
		$menu = '
			<form style="display:inline;" autocomplete="off" id="siguiente" name="siguiente" method="post" action="">		
				<input class="btn btn-outline-warning rounded-0" type="submit" value="Volver">
				<input type="hidden" value="Volver" name="accion">
			</form>	
			';
		$contenido = '
			<div class="alert alert-danger rounded-0">
			Estas a punto de cerrar esta caja, en caso de proceder:
				<ul>
					<li>No se podran editar los tickets en curso.</li>
					<li>No se podra revertir el cierre de caja.</li>
					<li>No se podra editar la caja.</li>
					<li>No se podran ingresar pagos, ni cobros.</li>
				</ul>
			Si aun asi estas seguro, estos son los datos que se ingresaran en la base de datos:
			</div>
			<form autocomplete="off" id="cerrarCaja" name="cerrarCaja" method="post" action="" class="row">
			<div class="col-lg-12" style="text-align:center"><h3>Total Ingresos</h3><hr></div>
			<div class="col-lg-6">
				<table class="table table-striped responsive-table table-hover table-bordered">
					<tbody>
						<tr>
							<th>Total Almacen</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="c2" id="c2" value="'.$data['c2'].'" readonly></td>
						</tr>
						<tr>
							<th>Total Verduleria</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="c3" id="c3" value="'.$data['c3'].'" readonly></td>
							
						</tr>
						<tr>
							<th>Total Cigarrillos</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="c4" id="c4" value="'.$data['c4'].'" readonly></td>
						</tr>
						<tr>
							<th>Total Carga Virtual</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="c5" id="c5" value="'.$data['c5'].'" readonly></td>
						</tr>
						<tr>
							<th>Total SUBE</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="c6" id="c6" value="'.$data['c6'].'" readonly></td>
						</tr>
						<tr>
							<th>Total General</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="total" id="total" value="'.$data['total'].'" readonly></td>
						</tr>
					</tbody>
				</table>
			</div>
			<div class="col-lg-6">
				<table class="table table-striped responsive-table table-hover table-bordered">
					<tbody>
						<tr>
							<th>Total Efectivo</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="efectivo" id="efectivo" value="'.$data['efectivo'].'" readonly></td>
						</tr>
						<tr>
							<th>Total Tarjetas</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="tarjetas" id="tarjetas" value="'.$data['tarjetas'].'" readonly></td>
						</tr>
						<tr>
							<th>Total Cuenta Corriente</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="c_corriente" id="c_corriente" value="'.$data['c_corriente'].'" readonly></td>
						</tr>
						<tr>
							<th>Saldo</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="saldo" id="saldo" value="'.$data['saldo'].'" readonly></td>
						</tr>
						<tr>
							<th>Cantidad de Tickets</th>
							<td style="border-right:none;"></td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="cantidad" id="cantidad" value="'.$data['cantidad'].'" readonly></td>
						</tr>
						<tr>
							<th>Primer Ticket</th>
							<td style="border-right:none;"></td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="primero" id="primero" value="'.$data['primero'].'" readonly></td>
						</tr>
						<tr>
							<th>Ultimo Ticket</th>
							<td style="border-right:none;"></td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="ultimo" id="ultimo" value="'.$data['ultimo'].'" readonly></td>
						</tr>
					</tbody>
				</table>
			</div>
			<div class="col-lg-12" style="text-align:center"><h3>Total Egresos</h3><hr></div>
			<div class="col-lg-6">
				<table class="table table-striped responsive-table table-hover table-bordered">
					<tbody>
						<tr>
							<th>Total Almacen</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="g_c2" id="g_c2" value="'.$almacen.'" readonly></td>
						</tr>
						<tr>
							<th>Total Verduleria</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="g_c3" id="g_c3" value="'.$verduleria.'" readonly></td>
						</tr>
						<tr>
							<th>Total Cigarrillos</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="g_c4" id="g_c4" value="'.$cigarrillos.'" readonly></td>
						</tr>
						<tr>
							<th>Total Carga Virtual</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="g_c5" id="g_c5" value="'.$cargaVirtual.'" readonly></td>
						</tr>
						<tr>
							<th>Total Rotisería</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="g_c6" id="g_c6" value="'.$rotiseria.'" readonly></td>
						</tr>
						<tr>
							<th>Total N/A</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="g_c1" id="g_c1" value="'.$naR.'" readonly></td>
						</tr>
					</tbody>
				</table>
			</div>
			<div class="col-lg-6">
				<table class="table table-striped responsive-table table-hover table-bordered">
					<tbody>
						<tr>
							<th>Total Proveedores</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="gProveedores" id="gProveedores" value="'.$proveedores.'" readonly></td>
						</tr>
						<tr>
							<th>Total Otros</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="gOtros" id="gOtros" value="'.$otros.'" readonly></td>
						</tr>
						<tr>
							<th>Total Sueldos</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="gSueldos" id="gSueldos" value="'.$sueldos.'" readonly></td>
						</tr>
						<tr>
							<th>Total Viaticos</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="gViaticos" id="gViaticos" value="'.$viaticos.'" readonly></td>
						</tr>
						<tr>
							<th>Total Impuestos</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="gImpuestos" id="gImpuestos" value="'.$impuestos.'" readonly></td>
						</tr>
						<tr>
							<th>Total Retiros</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="gRetiros" id="gRetiros" value="'.$retiros.'" readonly></td>
						</tr>
						<tr>
							<th>Total General</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="gTotal" id="gTotal" value="'.$totalG.'" readonly></td>
						</tr>
						<tr style="background:none;">
							<th style="border-color:#fff;"></th>
							<td style="border-color:#fff;"></td>
							<td style="border-color:#fff;text-align:right;"><input style="margin-left:10px;" type="submit" name="accion" id="accion" value="Continuar" class="btn btn-success rounded-0"></td>
						</tr>
					</tbody>
				</table>
			</div>
		</form>';
	
	}
	elseif (@$_POST['accion']=='Continuar') { 
		$menuT = 'Cerrar Caja';
		$menu = '
			<form style="display:inline;" autocomplete="off" id="siguiente" name="siguiente" method="post" action="">		
				<input class="btn btn-outline-warning rounded-0" type="submit" value="Volver">
				<input type="hidden" value="Volver" name="accion">
			</form>	
			';
		$contenido = '
			<div class="alert alert-warning rounded-0">
			Se cerrara la caja con los siguientes datos:
			</div>
			<form autocomplete="off" id="cerrarCaja" name="cerrarCaja" method="post" action="" class="row">
			<div class="col-lg-12" style="text-align:center"><h3>Total Egresos</h3><hr></div>
			<div class="col-lg-6">
				<table class="table table-striped responsive-table table-hover table-bordered">
					<tbody>
						<tr>
							<th>Total Almacen</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="c2" id="c2" value="'.$_POST['c2'].'" readonly></td>
						</tr>
						<tr>
							<th>Total Verduleria</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="c3" id="c3" value="'.$_POST['c3'].'" readonly></td>
							
						</tr>
						<tr>
							<th>Total Cigarrillos</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="c4" id="c4" value="'.$_POST['c4'].'" readonly></td>
						</tr>
						<tr>
							<th>Total Carga Virtual</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="c5" id="c5" value="'.$_POST['c5'].'" readonly></td>
						</tr>
						<tr>
							<th>Total Rotisería</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="c6" id="c6" value="'.$_POST['c6'].'" readonly></td>
						</tr>
						<tr>
							<th>Total General</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="total" id="total" value="'.$_POST['total'].'" readonly></td>
						</tr>
					</tbody>
				</table>
			</div>
			<div class="col-lg-6">
				<table class="table table-striped responsive-table table-hover table-bordered">
					<tbody>
						<tr>
							<th>Total Efectivo</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="efectivo" id="efectivo" value="'.$_POST['efectivo'].'" readonly></td>
						</tr>
						<tr>
							<th>Total Tarjetas</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="tarjetas" id="tarjetas" value="'.$_POST['tarjetas'].'" readonly></td>
						</tr>
						<tr>
							<th>Total Cuenta Corriente</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="c_corriente" id="c_corriente" value="'.$_POST['c_corriente'].'" readonly></td>
						</tr>
						<tr>
							<th>Saldo</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="saldo" id="saldo" value="'.$_POST['saldo'].'" readonly></td>
						</tr>
						<tr>
							<th>Cantidad de Tickets</th>
							<td style="border-right:none;"></td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="cantidad" id="cantidad" value="'.$_POST['cantidad'].'" readonly></td>
						</tr>
						<tr>
							<th>Primer Ticket</th>
							<td style="border-right:none;"></td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="primero" id="primero" value="'.$_POST['primero'].'" readonly></td>
						</tr>
						<tr>
							<th>Ultimo Ticket</th>
							<td style="border-right:none;"></td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="ultimo" id="ultimo" value="'.$_POST['ultimo'].'" readonly></td>
						</tr>
					</tbody>
				</table>
			</div>
			<div class="col-lg-12" style="text-align:center"><h3>Total Egresos</h3><hr></div>
			<div class="col-lg-6">
				<table class="table table-striped responsive-table table-hover table-bordered">
					<tbody>
						<tr>
							<th>Total Almacen</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="g_c2" id="g_c2" value="'.$_POST['g_c2'].'" readonly></td>
						</tr>	
						<tr>
							<th>Total Verduleria</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="g_c3" id="g_c3" value="'.$_POST['g_c3'].'" readonly></td>
						</tr>
						<tr>
							<th>Total Cigarrillos</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="g_c4" id="g_c4" value="'.$_POST['g_c4'].'" readonly></td>
						</tr>
						<tr>
							<th>Total Carga Virtual</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="g_c5" id="g_c5" value="'.$_POST['g_c5'].'" readonly></td>
						</tr>
						<tr>
							<th>Total Rotisería</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="g_c6" id="g_c6" value="'.$_POST['g_c6'].'" readonly></td>
						</tr>
						<tr>
							<th>Total N/A</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="g_c1" id="g_c1" value="'.$_POST['g_c1'].'" readonly></td>
						</tr>
					</tbody>
				</table>
			</div>
			<div class="col-lg-6">
				<table class="table table-striped responsive-table table-hover table-bordered">
					<tbody>
						<tr>
							<th>Total Proveedores</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="gProveedores" id="gProveedores" value="'.$_POST['gProveedores'].'" readonly></td>
						</tr>
						<tr>
							<th>Total Otros</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="gOtros" id="gOtros" value="'.$_POST['gOtros'].'" readonly></td>
						</tr>
						<tr>
							<th>Total Sueldos</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="gSueldos" id="gSueldos" value="'.$_POST['gSueldos'].'" readonly></td>
						</tr>
						<tr>
							<th>Total Viaticos</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="gViaticos" id="gViaticos" value="'.$_POST['gViaticos'].'" readonly></td>
						</tr>
						<tr>
							<th>Total Impuestos</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="gImpuestos" id="gImpuestos" value="'.$_POST['gImpuestos'].'" readonly></td>
						</tr>
						<tr>
							<th>Total Retiros</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="gRetiros" id="gRetiros" value="'.$_POST['gRetiros'].'" readonly></td>
						</tr>
						<tr>
							<th>Total General</th>
							<td style="border-right:none;">$</td>
							<td style="border-left:none;text-align:right;"><input style="text-align:right;" type="text" name="gTotal" id="gTotal" value="'.$_POST['gTotal'].'" readonly></td>
						</tr>
						<tr style="background:none;">
							<th style="border-color:#fff;"></th>
							<td style="border-color:#fff;"></td>
							<td style="border-color:#fff;text-align:right;"><input style="margin-left:10px;" type="submit" name="accion" id="accion" value="Finalizar" class="btn btn-success rounded-0"></td>
						</tr>
					</tbody>
				</table>
			</div>
			</form>
		';
	}
	elseif (@$_POST['accion']=='Finalizar') { 
		$c2 = $_POST['c2'];
		$c3 = $_POST['c3'];
		$c4 = $_POST['c4'];
		$c5 = $_POST['c5'];
		$c6 = $_POST['c6'];
		
		$g_c1 = $_POST['g_c1'];
		$g_c2 = $_POST['g_c2'];
		$g_c3 = $_POST['g_c3'];
		$g_c4 = $_POST['g_c4'];
		$g_c5 = $_POST['g_c5'];
		$g_c6 = $_POST['g_c6'];
		
		$total = $_POST['total'];
		$efectivo = $_POST['efectivo'];
		$tarjetas = $_POST['tarjetas'];
		$c_corriente = $_POST['c_corriente'];
		$saldo = $_POST['saldo'];

		$gProveedores = $_POST['gProveedores'];
		$gOtros = $_POST['gOtros'];
		$gSueldos = $_POST['gSueldos'];
		$gViaticos = $_POST['gViaticos'];
		$gImpuestos = $_POST['gImpuestos'];
		$gRetiros = $_POST['gRetiros'];
		$gTotal =$_POST['gTotal']-$_POST['gRetiros'];
		$gTotal = number_format($gTotal, 2, ".", "");	
		
		$cantidad = $_POST['cantidad'];
		$primero = $_POST['primero'];
		$ultimo = $_POST['ultimo'];
		$fecha = date('Y/m/d H:i:s');
		$id_usuario = $_SESSION['login']['id'];
		$id_punto_venta = $_SESSION['login']['punto_venta']['id'];
		$cerrarCaja="INSERT INTO cajas (c2, c3, c4, c5, c6, total, efectivo, tarjetas, c_corriente, saldo, cantidad, primero, ultimo, fecha, id_usuario, g_c1, g_c2, g_c3, g_c4, g_c5, g_c6, gProveedores, gOtros, gSueldos, gViaticos, gImpuestos, gTotal, retiros, id_punto_venta) VALUES
									('$c2', '$c3', '$c4', '$c5', '$c6', '$total', '$efectivo', '$tarjetas', '$c_corriente', '$saldo', '$cantidad', '$primero', '$ultimo', '$fecha', '$id_usuario', '$g_c1', '$g_c2', '$g_c3', '$g_c4', '$g_c5', '$g_c6', '$gProveedores', '$gOtros', '$gSueldos', '$gViaticos', '$gImpuestos', '$gTotal', '$retiros', '$id_punto_venta')";
		
		if ($cargarCaja = mysqli_query($conexion, $cerrarCaja)) {
			$t_numero = mysqli_insert_id($conexion);
			$obtenerInicio = mysqli_fetch_array(mysqli_query($conexion, "SELECT final FROM cajaz WHERE id_punto_venta = $id_punto_venta ORDER BY id DESC LIMIT 1"));
			$inicio = $obtenerInicio[0];
			$ingreso = $retiros;
			$egreso = $gTotal;
			$final = number_format(($inicio + $ingreso - $egreso), 2, ".", "");
			$concepto = 'Caja ID: '.$t_numero;
			mysqli_query($conexion, "INSERT INTO cajaz (fecha, concepto, inicio, ingreso, egreso, final, tipo, id_usuario, id_punto_venta) 
									 VALUES ('$fecha', '$concepto', '$inicio', '$ingreso', '$egreso', '$final', 1, $id_usuario, $id_punto_venta)");
			$cerrarTicket="UPDATE ventas SET cerrada = 1, id_caja = $t_numero WHERE cerrada = 0 AND id_punto_venta = $id_punto_venta";
			$cerrarGastos="UPDATE gastos SET cerrada = 1, id_caja = $t_numero WHERE cerrada = 0 AND id_punto_venta = $id_punto_venta";
			if ($cargarTicket = mysqli_query($conexion, $cerrarTicket)) {
				if ($cargarGastos = mysqli_query($conexion, $cerrarGastos)) {
					$menuT = 'Caja Cerrada';
					$menu = '';
					$contenido='
					<div class="alert alert-success rounded-0">
						La caja ha sido cerrada exitosamente y los tickets archivados. <a href="" onclick="ticketCC('.$t_numero.')">Imprimir Comprobante</a>
					</div>'; 
				}
				else {
					$menuT = 'Error';
					$menu = '';
					$contenido = '<div class="alert alert-danger">
						Ocurrio un error:  '.$cerrarGastos.'
					</div>';
				}
			}
			else {
				$menuT = 'Error';
				$menu = '';
				$contenido = '<div class="alert alert-danger">
					Ocurrio un error:  '.$cerrarTicket.'
				</div>';
			}
		}
		else { 
			$menuT = 'Error';
			$menu = '';
			$contenido = '<div class="alert alert-danger">
				Ocurrio un error:  '.$cerrarCaja.'
			</div>';
		}
	}
	else {
		$menuT = 'Tickets sin cerrar';
		$menu = '
			<a class="btn btn-outline-light rounded-0" style="font-weight:bold;font-style:italic;">TOTAL = '.$data['total'].$saldo.'</a>
			<form style="display:inline;" autocomplete="off" id="siguiente" name="siguiente" method="post" action="">		
				<input class="btn btn-outline-warning rounded-0" type="submit" value="Ver Parcial">
				<input type="hidden" value="Ver Parcial" name="accion">
			</form>	
			<form style="display:inline;" autocomplete="off" id="siguiente" name="siguiente" method="post" action="">		
					<input class="btn btn-outline-success rounded-0" type="submit" value="Cerrar Caja">
					<input type="hidden" value="Cerrar Caja" name="accion">
			</form>	';
		$contenido.='<table class="mt-3 table table-striped responsive-table table-hover table-bordered">
						<thead>
							<tr>
								<th>Ticket</th>
								<th>Fecha</th>
								<th>Cliente</th>
								<th colspan="2">Total</th>
								<th colspan="2">Efectivo</th>
								<th colspan="2">Tarjetas</th>
								<th colspan="2">Cuenta</th>
								<th colspan="2">Vuelto</th>
								<th colspan="2">Saldo</th>
								<th colspan="4">Opciones</th>
							</tr>
						</thead>
						<tbody>';
		$id_punto_venta = $_SESSION['login']['punto_venta']['id'];
		$query = "SELECT	v.id,
							v.fecha,
							v.saldo,
							v.total,
							v.efectivo,
							v.tarjetas,
							v.c_corriente,
							v.vuelto,
							v.eliminado,
							u.user
				  FROM 		ventas v
				  	JOIN	usuarios u ON v.cliente = u.id
				  WHERE		cerrada = 0 AND tipo IN (1, 2, 3) AND id_punto_venta = $id_punto_venta ORDER BY id DESC";
		$result = mysqli_query($conexion, $query);
		while ($data = mysqli_fetch_assoc($result)) {
			$fecha = explode(" ", $data['fecha']);
			$dia = explode("-", $fecha[0]);
			$hora = explode(":", $fecha[1]);
			$fecha = $dia[2].'-'.$dia[1].'  '.$hora[0].':'.$hora[1];
			$ticket = str_pad($id_punto_venta, 4, "0", STR_PAD_LEFT).' - '.str_pad($data['id'], 8, "0", STR_PAD_LEFT);
			$saldo = $data['saldo'] == 0 
				? $data['saldo']
				: ($data['saldo'] > 0
					? '<span style="color:green;">+'.$data["saldo"].'</span>'
					: '<span style="color:red;">'.$data["saldo"].'</span>');

			$bg = $data['eliminado'] == 1 ? 'style="background-color:orange;"' : '';
			$accion = $data['eliminado'] == 1
				? '<a style="color:green;" href="index.php?menu=facturacion&opc=caja&restaurar='.$data['id'].'"><i class="fa fa-undo-alt"></i></a>'
				: '<a style="color:red;" href="index.php?menu=facturacion&opc=caja&eliminar='.$data['id'].'"><i class="fa fa-trash"></i></a>';
			$contenido .= 
			'<tr '.$bg.'>
				<td>'.$ticket.'</td>
				<td>'.$fecha.'</td>
				<td>'.$data['user'].'</td>
				<td style="border-right:none;">$</td>
				<td style="text-align:right;border-left:none;">'.$data['total'].'</td>
				<td style="border-right:none;">$</td>
				<td style="text-align:right;border-left:none;">'.$data['efectivo'].'</td>
				<td style="border-right:none;">$</td>
				<td style="text-align:right;border-left:none;">'.$data['tarjetas'].'</td>
				<td style="border-right:none;">$</td>
				<td style="text-align:right;border-left:none;">'.$data['c_corriente'].'</td>
				<td style="border-right:none;">$</td>
				<td style="text-align:right;border-left:none;">'.$data['vuelto'].'</td>
				<td style="border-right:none;">$</td>
				<td style="text-align:right;border-left:none;">'.$saldo.'</td>
				<td style="border-right:none;"><a style="color:blue;" href="" onclick="reTicket('.$data['id'].')"><i class="fa fa-print"></i></a></td>
				<td style="border-left:none;border-right:none;"><a href="index.php?menu=facturacion&opc=caja&editar='.$data['id'].'" style="color:purple;"><i class="fa fa-edit"></i></a></td>
				<td style="border-left:none;border-right:none;"><a style="color:darkslategray;" href="index.php?menu=facturacion&opc=caja&cambiarUsuario='.$data['id'].'"><i class="fa fa-user-friends"></i></a></td>
				<td style="border-left:none;">'.$accion.'</td>
			</tr>';
		}
		$contenido .= '</tbody></table>';
	} ?>
	<div style="min-height:630px;">
		<div class="box inverse">
			<header>
				<div class="icons">
					<a title="Retirar Efectivo" data-bs-toggle="modal" class="wgreen" data-bs-target="#retirarEfectivo">
						<i style="font-size:20px;line-height:30px;color:#fff" class="fa fa-upload"></i>
					</a>
				</div>
				<h5 style="font-size:18px;line-height:25px;"><?php echo $menuT; ?></h5>
				<div class="toolbar">
					<nav style="padding: 8px;">
						<?php echo $menu; ?>
					</nav>
				</div>
			</header>
			<div class="body">
				<div style="min-height:550px;">
					<?php echo $contenido; ?>
				</div>
			</div>
		</div>
	</div>
<?php
}

if(@$_GET['opc']=='ventas') { 
	if(!isset($_SESSION['total'])) { $totalW='0.00'; }
	else { 
		$sbt=$_SESSION['total']+@$_SESSION['descuento'];
		if($sbt<999) { $totalW=number_format($sbt,2,".",""); }
		else { $totalW=number_format($sbt,0,".",""); }
		
	}
	echo '<div class="ways-total color_'.$_SESSION['login']['punto_venta']['id'].'"></div>
	<div class="ways-numero">$ '.$totalW.'</div>';
}
if(isset($_SESSION['guardado'])) {
	if(isset($_SESSION['guardado'][1])) {
	echo '	
		<a href="index.php?menu=facturacion&opc=ventas&guardar=recuperar1" class="btn btn-dark ways-guardado rounded-0 pos1">
			<i class="fa fa-envelope-open-text"></i>
		</a>
		<div class="ways-sub-guardado pos1">
			<strong>1</strong>
		</div>';
	}
	if(isset($_SESSION['guardado'][2])) {
	echo '
	<a href="index.php?menu=facturacion&opc=ventas&guardar=recuperar2" class="btn btn-dark ways-guardado rounded-0 pos2">
		<i class="fa fa-envelope-open-text"></i>
	</a>
	<div class="ways-sub-guardado pos2">
		<strong>2</strong>
	</div>';
	}
	if(isset($_SESSION['guardado'][3])) {
	echo '
	<a href="index.php?menu=facturacion&opc=ventas&guardar=recuperar3" class="btn btn-dark ways-guardado rounded-0 pos3">
		<i class="fa fa-envelope-open-text"></i>
	</a>
	<div class="ways-sub-guardado pos3">
		<strong>3</strong>
	</div>
		';
	}
}
?>

<div id="retirarEfectivo" class="modal fade" data-bs-backdrop="static" tabindex="-1" aria-labelledby="exampleModalLabel" aria-hidden="true">
	<div class="modal-dialog modal-l">
		<div class="modal-content rounded-0">
			<div class="modal-header">
				<h5 class="modal-title" id="exampleModalLabel">Ingrese el monto:</h5>
				<button type="button" class="btn-close rounded-0" data-bs-dismiss="modal" aria-label="Close"></button>
			</div>
			<div class="modal-body">
				<form class="row" name="monto" id="monto" method="get" action="" autocomplete="off">
					<div class="col-lg-6">	
						<input type="hidden" id="menu" name="menu" value="facturacion">
						<input type="hidden" id="opc" name="opc" value="caja">
						<input type="text" id="retiroEfectivo" name="retiroEfectivo" class="form-control mb-3 rounded-0">
					</div>
					<div class="col-lg-6">
						<input type="text" id="disponibilidad" name="disponibilidad" value="Disponible: $ <?php echo $disponible; ?>" class="form-control rounded-0" disabled>
					</div>
					<div class="modal-footer">
						<button type="submit" id="enviar" name="enviar" class="form-control btn btn-success rounded-0">Cargar</button>
					</div>
				</form>
			</div>
		</div>
	</div>	
</div>

<div id="direccion" class="modal fade" data-bs-backdrop="static" tabindex="-1" aria-labelledby="exampleModalLabel" aria-hidden="true">
	<div class="modal-dialog modal-l">
		<div class="modal-content rounded-0">
			<div class="modal-header">
				<h5 class="modal-title" id="exampleModalLabel">Ingrese la Direccion:</h5>
				<button type="button" class="btn-close rounded-0" data-bs-dismiss="modal" aria-label="Close"></button>
			</div>
			<div class="modal-body">
				<form class="row" name="dir" id="dir" method="get" action="" autocomplete="off">
					<div class="col-lg-12">	
						<input type="hidden" id="menu" name="menu" value="facturacion">
						<input type="hidden" id="opc" name="opc" value="ventas">
						<input type="text" id="cargarDireccion" name="cargarDireccion" class="form-control mb-3 rounded-0">
					</div>
					<div class="modal-footer">
						<button type="submit" id="enviar" name="enviar" class="form-control btn btn-success rounded-0">Cargar</button>
					</div>
				</form>
			</div>
		</div>
	</div>	
</div>

<div id="insertarCodigo" class="modal fade" data-bs-backdrop="static" tabindex="-1" aria-labelledby="exampleModalLabel" aria-hidden="true">
	<div class="modal-dialog modal-xl">
		<div class="modal-content rounded-0">
			<div class="modal-header">
				<h5 class="modal-title" id="exampleModalLabel">Ingresá el nombre del artículo:</h5>
				<button type="button" class="btn-close rounded-0" data-bs-dismiss="modal" aria-label="Close"></button>
			</div>
			<div class="modal-body">
				<form class="row" name="codigo" id="codigo" method="get" action="" autocomplete="off">
					<div class="col-lg-12">	
						<input type="hidden" id="menu" name="menu" value="facturacion" class="form-control">
						<input type="hidden" id="opc" name="opc" value="ventas" class="form-control">
						<input type="text" id="buscarArticulos" name="buscarArticulos" value="" class="form-control mb-3 rounded-0" onKeyUp="mostrarArticulos();">
					</div>
				</form>
			</div>
			<div class="modal-footer">
				<div class="col-lg-12" id="mostrarArticulos"></div>
			</div>
		</div>
	</div>	
</div>