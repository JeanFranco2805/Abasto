# Supermercado POS

Aplicación de escritorio Windows en C#/.NET 10 LTS con WPF. Cada caja conserva una base SQLite local para poder vender sin internet. El servicio de esta carpeta `Backend` recibe y guarda la cola en una base central y permite consultar el catálogo, las ventas y los eventos sincronizados.

## Ejecutar la aplicación de caja

Para abrir la versión publicada en Windows, usa `publish\win-x64\SupermercadoPOS.exe`. La base local se guarda en `%LOCALAPPDATA%\SupermercadoPOS\data\pos.db`.

Para compilar desde el código fuente se requiere el SDK de .NET 10:

```powershell
dotnet run --project .\SupermercadoPOS.csproj
```

## Levantar la API central

En el equipo que alojará el backend, desde la carpeta del proyecto:

```powershell
$env:Backend__ApiKey = "reemplaza-por-una-clave-larga-y-privada"
$env:ASPNETCORE_URLS = "http://0.0.0.0:5080"
dotnet run --project .\Backend\Backend.csproj
```

La base central SQLite se crea en `Backend\data\supermercado-central.db`. Para usar PostgreSQL en el servidor, configura las variables antes de iniciar la API:

```powershell
$env:Database__Provider = "PostgreSql"
$env:ConnectionStrings__CentralDatabase = "Host=servidor;Database=supermercado_pos;Username=pos_app;Password=clave"
```

El proceso de la API también necesita `Backend__ApiKey`. Para una instalación accesible fuera de la red local, coloca la API detrás de HTTPS y restringe el acceso de red al servidor.

## Conectar las cajas

1. Inicia el backend y confirma que `http://localhost:5080/health` muestra `status: ok` desde el mismo servidor.
2. En cada caja abre **Conectividad**.
3. Escribe la URL de la API (`http://IP-DEL-SERVIDOR:5080` en la red local o su URL HTTPS) y la misma clave configurada en el servidor.
4. Pulsa **Guardar conexión**. La caja envía automáticamente eventos pendientes y vuelve a intentarlo cada 45 segundos.

La primera conexión genera un identificador persistente para la caja y envía una copia inicial del catálogo. Cada venta, cambio de inventario, anulación y operación de turno queda en la cola local hasta que la central confirme su recepción. La API deduplica por identificador de caja y evento, de modo que un reintento tras un corte de red no duplica la venta.

La URL base de la API debe terminar en la dirección del servicio, no en `/api`. La clave de la caja se conserva en `%LOCALAPPDATA%\SupermercadoPOS\backend.json`; no se sincronizan los PIN de los cajeros.

## Rutas de la API

- `GET /health`: estado del proceso, sin autenticación.
- `GET /api/sync/summary`: conteo de eventos y cajas, requiere `X-Api-Key`.
- `POST /api/sync/events`: recibe lotes de hasta 100 eventos, requiere `X-Api-Key`.
- `GET /api/sync/events?clientId=<id>&take=100`: historial central, requiere `X-Api-Key`.
- `GET /api/products?clientId=<id>`: catálogo recibido, requiere `X-Api-Key`.
- `GET /api/sales?clientId=<id>&take=100`: ventas recibidas, requiere `X-Api-Key`.

## Notas operativas

- La API usa SQLite por defecto para que pueda ejecutarse sin servicios adicionales. PostgreSQL está disponible para alojar una central con varias cajas; configúralo antes de iniciar el backend. [El proveedor oficial de Npgsql ofrece soporte para EF Core 10](https://www.npgsql.org/efcore/release-notes/10.0.html).
- La base local de cada caja sigue operando sin conexión; una interrupción de internet no bloquea el cobro.
- La base central se inicializa con `EnsureCreated`; antes de actualizar un backend que ya tenga datos, conserva una copia de seguridad. Aún no se incluye un sistema de migraciones versionadas.
- Esta API sincroniza y consulta productos, ventas y eventos de caja. No reemplaza la facturación electrónica DIAN, el datáfono, ni reglas de conciliación entre cajas que modifiquen el mismo inventario.
- Configura claves distintas de las credenciales de demostración y no publiques la API por HTTP abierto en internet.

## Cuentas iniciales de demostración

| Usuario | PIN | Perfil |
| --- | --- | --- |
| cajero | 1111 | Cajero |
| supervisor | 1234 | Supervisor |
| admin | 2468 | Administrador |
