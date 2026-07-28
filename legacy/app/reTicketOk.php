<?php
	session_start();
	if(@$_SESSION['tipo']=='1') {
		unset($_SESSION['ticket']);
		unset($_SESSION['total']);
		unset($_SESSION['descuento']);
		unset($_SESSION['tipo']);
		unset($_SESSION['cliente']);
	}
	echo '<script language="javascript">window.location="index.php?menu=facturacion&opc=ventas"</script>;';