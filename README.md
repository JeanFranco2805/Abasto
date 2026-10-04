# Supermercado POS

Aplicación de escritorio Windows en C#/.NET 10 LTS con WPF. Este proyecto es la primera base funcional del POS y guarda sus datos localmente con SQLite.

## Ejecutar

Para abrir la versión compilada en Windows, haz doble clic en `publish\win-x64\SupermercadoPOS.exe`. Es autocontenida y no requiere instalar el SDK ni el runtime de .NET.

Para ejecutar el código fuente durante el desarrollo, se requiere el SDK de .NET 10:

    dotnet run --project .\SupermercadoPOS.csproj

La base local se crea en %LOCALAPPDATA%\SupermercadoPOS\data\pos.db.

## Incluido en esta versión

- Inicio de sesión por usuario y PIN con PIN almacenado como hash.
- Catálogo local con búsqueda por código o nombre y mantenimiento de productos.
- Lectura de escáner tipo teclado: el lector escribe el código y envía Enter.
- Carrito, cantidades por unidad o incrementos de 0,1 kg, cálculo de subtotal e impuesto.
- Captura opcional de nombre y documento del cliente en la venta y en el comprobante.
- Pago con efectivo, tarjeta, transferencia o billetera; admite pagos mixtos y calcula el cambio en efectivo.
- Los medios no monetarios se registran manualmente; aún no se solicita ni valida el cobro en el datáfono.
- Descuento manual por producto con autorización de supervisor y registro del aprobador.
- Retiro de productos del carrito con autorización de supervisor.
- Apertura, entradas y salidas de efectivo, cierre y arqueo de turno.
- Historial de turnos y movimientos de efectivo por turno.
- Suspensión y reanudación de ventas antes de cobrar.
- Anulación de una venta completa pagada en efectivo, dentro del turno abierto, con autorización y reposición de inventario.
- Descuento de inventario dentro de la transacción local de venta.
- Impresión de comprobante mediante una impresora instalada en Windows.
- Copia diaria rotativa de la base local, actualizada después de ventas y movimientos de caja.
- Registro de auditoría y cola local de eventos pendientes para sincronización futura.
- Resumen de ventas del día e historial reciente.
- Reportes filtrados por fechas, por cajero y turno, con ventas por medio de pago y productos más y menos vendidos.
- Pantalla de auditoría con las operaciones recientes.
- Administración de usuarios para perfil Administrador: crear cajeros, supervisores y administradores, restablecer PIN y activar o desactivar usuarios.
- Pantalla de conectividad que muestra la cola local de sincronización y el estado de las copias.
- Atajos de teclado: F2 búsqueda, F3 código de barras, F4 cobrar, F7 gestionar turno, F8 suspender y F9 reanudar.

## Cuentas iniciales de demostración

| Usuario | PIN | Perfil |
| --- | --- | --- |
| cajero | 1111 | Cajero |
| supervisor | 1234 | Supervisor |
| admin | 2468 | Administrador |

Estas cuentas son únicamente para demostración. La aplicación permite cambiar PIN desde el perfil Administrador, pero todavía no configura bloqueo por intentos. No se debe desplegar a clientes con estas credenciales.

## Supuestos y pendientes antes de uso comercial

- Los precios de ejemplo y sus tasas de impuesto son datos de demostración. La configuración tributaria debe validarse con el negocio y con la integración de facturación que se seleccione.
- La venta se guarda sin conexión en el equipo. La cola de sincronización ya tiene una estructura inicial, pero falta implementar el cliente y las reglas de conciliación con un back office/ERP.
- La pantalla de conectividad informa el estado local y los eventos pendientes; no puede enviar datos a una central hasta configurar e implementar el servicio de sincronización.
- El comprobante impreso es un recibo genérico del sistema. No equivale a factura electrónica ni aplica reglas fiscales confirmadas.
- Las interfaces IReceiptPrinter, IScale, IPaymentTerminal, IElectronicInvoicingProvider e IBackOfficeSyncClient definen puntos de integración. Sus implementaciones requieren conocer modelos, controladores, proveedores y protocolos reales.
- No están implementados todavía la factura electrónica/DIAN, promociones automáticas como 2x1, devoluciones parciales/notas crédito, ni la pantalla secundaria.
- La anulación disponible cubre solo ventas pagadas completamente en efectivo dentro del turno abierto; no reemplaza un flujo completo de devolución o nota crédito.
- Cada instalación usa su propia base SQLite. No se debe copiar esa base a una carpeta de red para compartirla entre cajas. La sincronización multi-caja necesita un servicio central y reglas explícitas para conflictos.
- El acceso de supervisor se aplica al mantenimiento de productos, descuentos manuales, retiro de productos del carrito y anulación de ventas en efectivo. La aplicación no incluye apertura de cajón ni un flujo de devolución parcial.
- La base inicial se crea con EnsureCreated; aún no hay un mecanismo de migraciones/actualización para instalaciones con datos.

## Siguiente alcance recomendado

1. Definir reglas de factura/ticket, impuestos, redondeo y contingencia fiscal.
2. Implementar devoluciones parciales y notas crédito con autorizaciones auditadas.
3. Integrar y probar impresora, cajón, báscula y datáfono con los modelos del cliente piloto.
4. Añadir sincronización idempotente con back office y conciliación de inventario entre cajas.
5. Incorporar copias de seguridad con restauración, migraciones y administración segura de usuarios.
