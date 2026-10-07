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

## API

- `GET /health`: estado del servicio.
- `GET /api/sync/summary`: conteo de eventos y cajas; requiere `X-Api-Key`.
- `POST /api/sync/events`: recibe eventos de una caja; requiere `X-Api-Key`.
- `GET /api/sync/events?clientId=<id>&take=100`: historial de sincronización; requiere `X-Api-Key`.
- `GET /api/products?clientId=<id>`: catálogo recibido; requiere `X-Api-Key`.
- `GET /api/sales?clientId=<id>&fromUtc=<fecha>&toUtc=<fecha>&take=500`: ventas para reportes; requiere `X-Api-Key`.

La API debe publicarse detrás de HTTPS si se accede desde fuera de la red local. La base central se crea con `EnsureCreated`; prepara una copia antes de actualizar una instalación que contenga información.
