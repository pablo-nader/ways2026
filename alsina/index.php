<?php
session_start();
//error_reporting(0);
date_default_timezone_set('America/Argentina/Buenos_Aires');
require_once './conexion.php';

if(@$conexion = mysqli_connect(HOST, USER, PASSWORD, DATABASE)) {
	if (@$_GET['menu']=='login') { $titulo = 'WAYS - Iniciar Sesion'; }
	else { $titulo = 'Ways - Sistema de Gestion'; }
	if (@$_GET['menu']=='logout') { 
		session_destroy();
		echo '<script language="javascript">window.location="index.php?menu=login"</script>;';
	}
	if(!isset($_SESSION['cliente']['id'])){
		$_SESSION['cliente']['id']='1';
		$_SESSION['cliente']['cliente']='Consumidor Final';
		$_SESSION['cliente']['direccion']='-';
		$_SESSION['cliente']['tel']='-';
		$_SESSION['cliente']['acuerdo']='0.00';
		$_SESSION['cliente']['saldo']='0.00';
	}
	require 'funciones.php';
	
	if(isset($_GET['cargarDireccion'])) {
			$_SESSION['direccion'] = strtoupper($_GET['cargarDireccion']);
			header("Location: index.php?menu=facturacion&opc=ventas");
		}
	?>
	<!doctype html>
	<html lang="es">

	<head>
		<meta charset="utf-8">
		<meta http-equiv="X-UA-Compatible" content="IE=edge">
		<meta name="viewport" content="width=device-width, initial-scale=1">
		<title><?=$titulo;?></title>
		<link rel="shortcut icon" href="./assets/img/favicon.png" type="image/png">
		
		<link rel="stylesheet" href="assets/lib/font-awesome/css/all.css">
		<link rel="stylesheet" href="assets/css/main.css">
		<link rel="stylesheet" href="assets/lib/metismenu/metisMenu.css">
		<link href="https://cdn.jsdelivr.net/npm/bootstrap@5.2.0-beta1/dist/css/bootstrap.min.css" rel="stylesheet" integrity="sha384-0evHe/X+R7YkIZDRvuzKMRqM+OrBnVFBL6DOitfPri4tjfHxaWutUpFmBp4vmVor" crossorigin="anonymous">
		<link rel="stylesheet" href="./assets/css/ways.css">
		<script>
			function validar2(){
				var todo_correcto = true;
				if(document.getElementById('efectivo').value == ''){
					todo_correcto = false;
				}
				if(!todo_correcto){
					alert('No se ingreso el pago. El campo no puede estar vacio');
				}
				return todo_correcto;
			}
			function sorteo(){
				window.open("sorteo.php",'_blank', 'location=no,menubar=no,resizable=no,scrollbars=yes,status=no,titlebar=no,toolbar=no,width=1300,height=600,left=150,top=100');
			}
			function ticket(){
				window.open("ticket.php",'_blank', 'location=no,menubar=no,resizable=no,scrollbars=yes,status=no,titlebar=no,toolbar=no,width=300,height=400,left=550,top=150');
			}
			function reTicket(numero){
				var ticket = numero;
				window.open("reTicket.php?id="+ticket,'_blank', 'location=no,menubar=no,resizable=no,scrollbars=yes,status=no,titlebar=no,toolbar=no,width=300,height=400,left=550,top=150');
			}
			function ticketCC(numero){
				var ticket = numero;
				window.open("ticketCC.php?id="+ticket,'_blank', 'location=no,menubar=no,resizable=no,scrollbars=yes,status=no,titlebar=no,toolbar=no,width=300,height=400,left=550,top=150');
			}
			function ticketRetiro(numero){
				var ticket = numero;
				window.open("ticketRetiro.php?id="+ticket,'_blank', 'location=no,menubar=no,resizable=no,scrollbars=no,status=no,titlebar=no,toolbar=no,width=300,height=400,left=550,top=150');
			}
			function imprimirArticulos(tipo,numero){
				var tipos = tipo;
				var ticket = numero;
				window.open("imprimirArticulos.php?id="+ticket+"&tipo="+tipos,'_blank', 'location=no,menubar=no,resizable=no,scrollbars=no,status=no,titlebar=no,toolbar=no,width=300,height=400,left=550,top=150');
			}
			function foco() {
				document.getElementById("busqueda").focus();
			}
			function foco2() {
				document.getElementById("efectivo").focus();
			}
			window.onkeydown = tecla;
			function tecla(event) {
				var array= [107,109,112,113,114,115,116,117,118,119,120,121,122,123,33,34];
				if(array.includes(event.keyCode)) {
					event.preventDefault();
				}
				num = event.keyCode;
				if(num==33) {
					document.getElementById('busqueda').value='711';
					buscar();
					document.getElementById('cantidad').focus();
				}
				if(num==34) {
					document.getElementById('busqueda').value='688';
					buscar();
					document.getElementById('cantidad').focus();
				}
				if(num==35) {
					document.getElementById('busqueda').value='1337';
					buscar();
					document.getElementById('cantidad').focus();
				}
				if(num==36) {
					document.getElementById('busqueda').value='710';
					buscar();
					document.getElementById('cantidad').focus();
				}
				if(num==45) {
					document.getElementById('busqueda').value='709';
					buscar();
					document.getElementById('cantidad').focus();
				}
				if(num==46) {
					document.getElementById('busqueda').value='697';
					buscar();
					document.getElementById('cantidad').focus();
				}

				if(num==112) {
					$("#buscarCliente").modal("show");
				}
				if(num==113) {
					$("#insertarCodigo").modal("show");
				}
				if(num==114)
					$("#direccion").modal("show");
				if(num==115)
				alert("Pulsaste F4");
				if(num==116)
				alert("Pulsaste F5");
				if(num==117)
				alert("Pulsaste F6");
				if(num==118)
				alert("Pulsaste F7");
				if(num==119)
				alert("Pulsaste F8");
				if(num==120) {
					document.getElementById("siguiente").submit();
				}
				if(num==121){
					var descarte = document.getElementById("descarte").value;
					if(descarte == "Volver (F10)") {
						document.getElementById("descartar").submit();
					}
					else {
						var r = confirm("¿Descartar todos los datos del ticket?");
						if (r == true) {
							document.getElementById("descartar").submit();
						} 	
					}						
				}
				if(num==122)
				alert("Pulsaste F11");
				if(num==123) {
					$("#abrirCaja").modal("show");
					$('#abrirCaja').on('shown.bs.modal', function () {
						$("#detalle").focus();
					});
				}
				if(num==107) {
					document.getElementById("cantidad").focus();
				}
				if(num==109) {
					document.getElementById("busqueda").focus();
				}
			}
			
			function sumar() {
				total = document.getElementById('total').value;
				total = (total == null || total == undefined || total == "") ? 0.00 : total;
				
				efectivo = document.getElementById('efectivo').value;
				efectivo = (efectivo == null || efectivo == undefined || efectivo == "") ? 0.00 : efectivo;
				
				eftar = parseFloat(efectivo) + parseFloat(tarjetas);
				if(+eftar > +total) {
					document.getElementById('tarjetas').value = '0.00';
				}
				tarjetas = document.getElementById('tarjetas').value;
				tarjetas = (tarjetas == null || tarjetas == undefined || tarjetas == "") ? 0.00 : tarjetas;
					
				efcta = parseFloat(efectivo) + parseFloat(c_corriente);
				if(+efcta > +total) {
					document.getElementById('c_corriente').value = '0.00';
				}
				c_corriente = document.getElementById('c_corriente').value;
				c_corriente = (c_corriente == null || c_corriente == undefined || c_corriente == "") ? 0.00 : c_corriente;
			
				
				
				/* Esta es la suma. */
				vuelto = (parseFloat(total) - parseFloat(efectivo) - parseFloat(tarjetas) - parseFloat(c_corriente)) * (-1);
				
				// Colocar el resultado de la suma en el control "span".
				document.getElementById('vuelto').value = vuelto.toFixed(2);
			}
			
			function calcular() {
				total = document.getElementById('total').value;
				total = (total == null || total == undefined || total == "") ? 0.00 : total;
				
				efectivo = document.getElementById('efectivo').value;
				efectivo = (efectivo == null || efectivo == undefined || efectivo == "") ? 0.00 : efectivo;
				if (+efectivo < +total) {
					tarjetas = parseFloat(total) - parseFloat(efectivo);
					document.getElementById('tarjetas').value = tarjetas.toFixed(2);
									
					document.getElementById('c_corriente').value = '0.00';

					
					// Colocar el resultado de la suma en el control "span".
					document.getElementById('vuelto').value = '0.00';
				}
				
			}
			function calcular2() {
				total = document.getElementById('total').value;
				total = (total == null || total == undefined || total == "") ? 0.00 : total;
				
				efectivo = document.getElementById('efectivo').value;
				efectivo = (efectivo == null || efectivo == undefined || efectivo == "") ? 0.00 : efectivo;
				if (+efectivo < +total) {
					c_corriente = parseFloat(total) - parseFloat(efectivo);
					document.getElementById('c_corriente').value = c_corriente.toFixed(2);
						
					document.getElementById('tarjetas').value = '0.00';
								
					document.getElementById('vuelto').value = '0.00';
				}
			}
		</script>
		<link rel="stylesheet/less" type="text/css" href="assets/less/theme.less">

	  </head>

	<?php
		if (@$_SESSION['login']['status'] == 'ready') { include 'body.php'; }
		else if (@$_SESSION['login']['status'] == 'logged') { include 'elegirLocal.php'; }
		else { 
			if (@$_GET['menu'] == 'login') { include 'login.php'; }
			else { echo '<script language="javascript">window.location="index.php?menu=login"</script>;'; }
		}
	?>
	<script src="https://code.jquery.com/jquery-3.6.0.min.js" integrity="sha256-/xUj+3OJU5yExlq6GSYGSHk7tPXikynS7ogEvDej/m4=" crossorigin="anonymous"></script>
		<script>
			function ofertaGrupo(opc) {
				if(opc==1) {
					document.getElementById('precio').disabled=false;
					document.getElementById('cantidad').disabled=false;
					document.getElementById('descuento').disabled=true;
					document.getElementById('descuento').value='';
				}
				else if(opc==2) {
					document.getElementById('precio').disabled=true;
					document.getElementById('precio').value='';
					document.getElementById('cantidad').disabled=true;
					document.getElementById('cantidad').value='';
					document.getElementById('descuento').disabled=false;
				}
				else if(opc==3) {
					var check = document.getElementById('dias').checked;
					if(check==true) {
						document.getElementById('dDesde').disabled=false;
						document.getElementById('dHasta').disabled=false;
					}
					else if(check==false) {
						document.getElementById('dDesde').disabled=true;
						document.getElementById('dHasta').disabled=true;
					}
				}
				else if(opc==4) {
					var check = document.getElementById('horas').checked;
					if(check==true) {
						document.getElementById('hDesde').disabled=false;
						document.getElementById('hHasta').disabled=false;
					}
					else if(check==false) {
						document.getElementById('hDesde').disabled=true;
						document.getElementById('hHasta').disabled=true;
					}
				}
				
			}

			function buscar() {
				var textoBusqueda = $("input#busqueda").val();
			 
				 if (textoBusqueda != "") {
					$.post("buscar.php", {valorBusqueda: textoBusqueda}, function(mensaje) {
						var msj=mensaje.split(",");
						$("#descripcion").val(msj[0]);
						$("#precio").val(msj[1]);
					 }); 
				 } else { 
					$("#resultadoBusqueda").html('');
					};
			};
			//------------------
			$(document).ready(function() {
				$("#mostrarArticulos").html('');
				$("#mostrarArticulos2").html('');
				$("#mostrarClientes").html('');
				$("#mostrarClientes").html('');
				$("#resultadoCombos").html('<table class="table table-striped responsive-table table-hover table-bordered"><thead><tr><th colspan="2">Nombre</th><th>Lista</th><th>Venta</th></tr></thead><tbody></tbody></table></div>');

				$('#buscarCliente').on('shown.bs.modal', function () {
					$("#buscarClientes").focus();
				});
				$('#buscarCliente').on('hidden.bs.modal', function () {
					$("#busqueda").focus();
				});
				$('#insertarCodigo').on('shown.bs.modal', function () {
					$("#buscarArticulos").focus();
				});		
				$('#insertarCodigo').on('hidden.bs.modal', function () {
					$("#busqueda").focus();
				});		
				$('#direccion').on('shown.bs.modal', function () {
					$("#cargarDireccion").focus();
				});
				$('#direccion').on('hidden.bs.modal', function () {
					$("#busqueda").focus();
				});
				$('#ingresarPago').on('shown.bs.modal', function () {
					$("#efectivo").focus();
				});
				$('#ajustePersonalizado').on('shown.bs.modal', function () {
					$("#detalle").focus();
				});				
				$('#ingresarFC').on('shown.bs.modal', function () {
					$("#FC").focus();
				});
				$('#ingresarFCV').on('shown.bs.modal', function () {
					$("#FCV").focus();
				});
			});

			function mostrarArticulos() {
				var textoBusqueda = $("input#buscarArticulos").val();
			 
				 if (textoBusqueda != "") {
					$.post("mostrarArticulos.php", {valorBusqueda: textoBusqueda}, function(mensaje) {
						$("#mostrarArticulos").html(mensaje);
					 }); 
				 } else { 
					$("#mostrarArticulos").html('');
					};
			};
			//------------------

			function mostrarArticulos2() {
				var textoBusqueda = $("input#buscarArticulos2").val();
			 
				 if (textoBusqueda != "") {
					$.post("mostrarArticulos2.php", {valorBusqueda: textoBusqueda}, function(mensaje) {
						$("#mostrarArticulos2").html(mensaje);
					 }); 
				 } else { 
					$("#mostrarArticulos2").html('');
					};
			};
			//------------------

			function mostrarClientes() {
				var textoBusqueda = $("input#buscarClientes").val();
			 
				 if (textoBusqueda != "") {
					$.post("mostrarClientes.php", {valorBusqueda: textoBusqueda}, function(mensaje) {
						$("#mostrarClientes").html(mensaje);
					 }); 
				 } else { 
					$("#mostrarClientes").html('');
					};
			};
			//------------------

			function mostrarClientesCC() {
				var textoBusqueda = $("input#buscarClientes").val();
			 
				 if (textoBusqueda != "") {
					$.post("mostrarClientesCC.php", {valorBusqueda: textoBusqueda}, function(mensaje) {
						$("#mostrarClientes").html(mensaje);
					 }); 
				 } else { 
					$("#mostrarClientes").html('');
					};
			};
			//------------------
			//---

			function combos() {
				var textoBusqueda = $("input#buscarCombos").val();
				 if (textoBusqueda != "") {
					$.post("combos.php", {valorBusqueda: textoBusqueda}, function(mensaje) {
						$("#resultadoCombos").html(mensaje);
					 }); 
				 } else { 
					$("#resultadoCombos").html('<table class="table table-striped responsive-table table-hover table-bordered"><thead><tr><th colspan="2">Nombre</th><th>Lista</th><th>Venta</th></tr></thead><tbody></tbody></table></div>');
					};
			};
			//--
			function calcularCombo() {
				var precioC1 = document.getElementById('precioC1').value;
				var precioC2 = document.getElementById('precioC2').value;
				var precioV1 = document.getElementById('precioV1').value;
				var precioV2 = document.getElementById('precioV2').value;
				var cant1 = document.getElementById('cantidad1').value;
				var cant2 = document.getElementById('cantidad2').value;
				var totalC = (precioC1*cant1) + (precioC2*cant2);
				var totalV = (precioV1*cant1) + (precioV2*cant2);
				document.getElementById('totalCombo').value = totalV.toFixed(2) + " (" + totalC.toFixed(2) + ")";
			};
			
		</script>
	</html>
<?php
} 
else {
	include 'errores/offline.php';
}
?>