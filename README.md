# Abasto

Aplicación de escritorio para punto de venta en Windows, desarrollada con C#, .NET 10 y WPF. Cada caja conserva una base SQLite local para operar sin conexión. Cuando la API está disponible, el catálogo y los reportes consultan los datos del servidor y la caja sincroniza sus operaciones pendientes.

## Ejecutar la caja

El ejecutable publicado está en `publish\abasto-win-x64\Abasto.exe`. Para iniciar desde el código fuente se requiere el SDK de .NET 10:

```powershell
dotnet run --project .\Abasto.csproj
```

En una instalación nueva, Abasto solicita crear la cuenta administradora. Cada tienda define su propio nombre de usuario y PIN. Las instalaciones existentes conservan la información que ya tenían.

## Iniciar el backend

Desde el equipo que alojará el servicio, ejecuta:

```powershell
$env:ASPNETCORE_URLS = "http://0.0.0.0:5080"
dotnet run --project .\Backend\Abasto.Backend.csproj
```

El ejecutable publicado está en `publish\backend-win-x64\Abasto.Backend.exe`.

En el primer inicio, el servicio crea una clave API aleatoria y la guarda localmente. Si la caja y el backend usan la misma cuenta de Windows, Abasto detecta la clave y se conecta a `http://localhost:5080`. También se puede configurar la clave con `Backend__ApiKey`.

Para conservar la información de instalaciones previas, los archivos locales siguen en `%LOCALAPPDATA%\SupermercadoPOS`. La base de datos de la caja es `data\pos.db`; la base central SQLite es `Backend\supermercado-central.db`. PostgreSQL también está disponible en el backend mediante estas variables:

```powershell
$env:Database__Provider = "PostgreSql"
$env:ConnectionStrings__CentralDatabase = "Host=servidor;Database=abasto;Username=pos_app;Password=clave"
```

## Conectar cajas

1. Inicia el backend y comprueba que `http://localhost:5080/health` responde.
2. En Abasto, abre **Conectividad**.
3. Configura la URL del servicio y su clave API.
4. Guarda la conexión. La caja enviará automáticamente los cambios pendientes y reintentará la sincronización cuando se interrumpa la red.

La instalación nueva empieza sin productos cargados. El catálogo se crea al registrar los productos reales de la tienda. Las ventas, movimientos de inventario, anulaciones y turnos se encolan localmente hasta que el servidor confirma su recepción.

La base local permite seguir cobrando sin conexión. La API deduplica los eventos por caja y número de evento. Los PIN de usuarios no se sincronizan.

## Devoluciones

En **Reportes**, selecciona una venta y abre **Devolver productos**. El sistema solicita autorización de supervisor, motivo y cantidades, repone el inventario y registra la salida de efectivo en el turno abierto. El efectivo devuelto no puede superar el efectivo recibido originalmente. Los reembolsos de tarjeta y transferencia permanecen bloqueados hasta instalar el adaptador del datáfono de la tienda.

## Periféricos

Abre **Conectividad > Equipos y facturación**. Las impresoras térmicas deben estar instaladas en Windows y aceptar comandos ESC/POS RAW. El cajón se conecta al puerto de apertura de esa impresora. La báscula debe exponer un puerto COM y enviar el peso como una línea de texto; se puede configurar el puerto y los baudios en la aplicación. La báscula se lee desde el punto de venta al seleccionar un producto en kg o g. Los lectores USB que funcionan como teclado siguen usando el campo de código de barras.

El protocolo de datáfono y la pantalla de cliente dependen del fabricante. Abasto no confirma un pago con tarjeta ni abre reembolsos electrónicos mientras no exista un adaptador compatible con el modelo instalado.

## Proveedor de facturación electrónica

El backend incluye un conector HTTPS genérico para solicitar facturas y notas crédito a un servicio externo. El proveedor debe ofrecer rutas REST configurables, aceptar el cuerpo JSON de Abasto, soportar `Idempotency-Key` y devolver un número, folio o UUID del documento. Define estas variables en el equipo servidor; no guardes el token en cada caja:

```powershell
$env:ElectronicInvoicing__ProviderName = "Proveedor contratado"
$env:ElectronicInvoicing__BaseUrl = "https://api.proveedor.example/"
$env:ElectronicInvoicing__AccessToken = "token-del-servidor"
$env:ElectronicInvoicing__InvoicePath = "invoices"
$env:ElectronicInvoicing__CreditNotePath = "credit-notes"
```

Reinicia el backend y comprueba el estado en **Conectividad > Equipos y facturación**. El flujo exige que la venta y, para una nota crédito, la devolución ya se hayan sincronizado. La conexión definitiva con DIAN depende del proveedor autorizado, sus credenciales, la resolución y los datos fiscales del comercio; el conector genérico no sustituye esa configuración ni valida por sí mismo un documento ante DIAN.

## API

- `GET /health`: estado del servicio.
- `GET /api/sync/summary`: conteo de eventos y cajas; requiere `X-Api-Key`.
- `POST /api/sync/events`: recibe eventos de una caja; requiere `X-Api-Key`.
- `GET /api/sync/events?clientId=<id>&take=100`: historial de sincronización; requiere `X-Api-Key`.
- `GET /api/products?clientId=<id>`: catálogo recibido; requiere `X-Api-Key`.
- `GET /api/sales?clientId=<id>&fromUtc=<fecha>&toUtc=<fecha>&take=500`: ventas para reportes; requiere `X-Api-Key`.
- `GET /api/fiscal/status`: estado del conector de facturación; requiere `X-Api-Key`.
- `POST /api/fiscal/invoices`: solicita una factura para una venta sincronizada; requiere `X-Api-Key`.
- `POST /api/fiscal/credit-notes`: solicita una nota crédito para una devolución sincronizada; requiere `X-Api-Key`.

La API debe publicarse detrás de HTTPS si se accede desde fuera de la red local. La base central se crea con `EnsureCreated`; prepara una copia antes de actualizar una instalación que contenga información.
