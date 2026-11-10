# Migración del Automation API a Linux

Este documento registra el contrato observado en `entrega.zip` 1.8.5-pre3 y las
condiciones para implementarlo en Linux. La API de Windows no se puede copiar
directamente: su backend recibe sesiones WPF, portapapeles del sistema y rutas
de entrada propias de cada dispositivo.

## Contrato de Windows que debe conservarse

El host de Windows publica OpenAPI 3.1 y escucha únicamente en IPv4 loopback.
Las solicitudes de dispositivos llevan `X-API-Key`; el control adquiere una
sesión exclusiva y la envía como `X-Control-Session`. El host limita tamaño de
petición, tasa de solicitudes y capturas, restringe orígenes/Host y devuelve
errores JSON estructurados. La documentación pública no requiere credenciales.

Rutas principales observadas en `AutomationApiHost.cs` y
`AutomationOpenApi.cs`:

| Ruta | Función |
| --- | --- |
| `GET /api/v1/app/status` | Estado de la aplicación |
| `GET /api/v1/devices` y `GET /api/v1/devices/{id}` | Dispositivos observados; usa identificadores opacos |
| `GET /api/v1/devices/{id}/status` | Conexión, capacidades, geometría y control adquirido |
| `POST .../control/acquire` y `.../control/release` | Adquirir, renovar y liberar control exclusivo |
| `POST .../input/{action}` | Contrato Windows: tap, swipe, long-press, touch-down/up, touch-path, key y text. Linux implementa tap, swipe, long-press, key y texto ASCII seguro (hasta 256 caracteres) para Android por ADB |
| `GET/PUT .../clipboard` | Leer o escribir el portapapeles del teléfono |
| `GET .../screenshot` | PNG del Android o iPhone/iPad seleccionado mientras se duplica |

Windows define coordenadas como píxeles del cuadro nativo actual. La API Linux
reporta en Android `deviceLogicalPixels`, el sistema de coordenadas que ADB
acepta, además de dimensiones y origen. Un cambio de geometría observado
invalida la sesión de control. Una respuesta de entrada significa “enviado”, no
que la interfaz del teléfono haya confirmado la acción.

## Adaptación de capacidades

| Capacidad | Android Linux actual | iPhone/iPad Linux actual |
| --- | --- | --- |
| Descubrimiento | ADB por USB o red | USB con libimobiledevice |
| Captura PNG | ADB `screencap` del Android seleccionado mientras se duplica | `idevicescreenshot`/Screenshotr del iPhone seleccionado mientras se duplica; depende de que Screenshotr esté disponible en iOS |
| Toque, gestos y teclas | API: lease exclusivo y ADB `input tap/swipe/long-press/keyevent` con coordenadas validadas contra geometría | No hay ruta táctil de automatización equivalente |
| Teclado y texto | API ADB admite teclas comunes y texto ASCII seguro; texto Unicode/clipboard no expuestos | Teclado HID Bluetooth opcional y experimental |
| Portapapeles | No expuesto por la app | No expuesto por la app |

La API Linux debe anunciar capacidades por dispositivo y responder
`CAPABILITY_NOT_SUPPORTED` cuando el transporte no pueda cumplir una operación.
No se deben simular gestos absolutos con las coordenadas del ratón BLE ni
presentar el envío de una orden ADB como confirmación del estado visual.

## Fases de implementación

1. **Parcialmente implementado en 0.2.0-104:** host Qt loopback, clave aleatoria
   persistente, documentación/OpenAPI públicos, límites de solicitud y rutas
   autenticadas de estado y lectura de dispositivos. Los IDs son hashes
   estables; no contienen UDID ni serial ADB. La implementación todavía no cubre
   el conjunto completo de esquemas, permisos ni errores de Windows.
   Incluye PNG de Android (ADB) e iPhone/iPad (Screenshotr) del dispositivo ya
   seleccionado y solo durante una sesión activa; no inicia otra sesión de
   mirroring. En iOS puede requerirse la imagen de desarrollador montada. El host admite hasta
   32 conexiones activas, rechaza solicitudes sobre 16 KiB,
   cierra clientes que no completan cabeceras en cinco segundos y limita a 100
   solicitudes por segundo en total.
2. **Parcialmente implementado:** `GET /devices/{id}/status` informa plataforma,
   conexión, selección, captura y capacidades. La frescura procede del último
   sondeo exitoso; durante un timeout `onlineKnown` es falso. Cuando Android
   está seleccionado y duplicándose, la geometría viene de `dumpsys input` y
   se actualiza cada dos segundos; incluye origen, ancho, alto, versión del
   cuadro y espacio de coordenadas. Si no se puede leer de forma fiable,
   devuelve `null` y no debe asumirse un tamaño.
3. **Parcialmente implementado:** Android expone leases de 5–300 segundos y
   entrada `tap`, `long-press`, `swipe` y teclas permitidas si está seleccionado, duplicándose y
   con geometría disponible. Cada acción debe incluir `geometryVersion`; si
   difiere del cuadro actual, se rechaza. Un cambio de cuadro observado libera
   el lease y cancela el ADB activo. La liberación, vencimiento, desconexión del
   cliente o cierre de sesión también cancela el proceso; se permite solo una
   acción simultánea por dispositivo. El shell de ADB recibe argumentos separados, no una orden
   interpolada. Texto acepta hasta 256 caracteres ASCII de una lista segura,
   convierte espacios al escape `%s` de Android `input text` y rechaza la
   secuencia literal `%s`, que Android interpreta como espacio. Las teclas y
   texto dependen del mapa de teclado del dispositivo. Faltan `touch-down/up`,
   `touch-path`, Unicode/portapapeles y pruebas de carreras al girar/desconectar.
   [Implementación AOSP de `input text`](https://android.googlesource.com/platform/frameworks/base/%2B/9f25b7f/cmds/input/src/com/android/commands/input/Input.java).
4. Adaptar teclado HID iOS solo para acciones que el descriptor permita; marcar
   el soporte como experimental mientras no se valide físicamente.
5. Implementar portapapeles únicamente cuando exista un transporte real para
   leerlo y escribirlo; conservar `CAPABILITY_NOT_SUPPORTED` en los demás casos.

Las rutas HTTP disponibles hoy son `GET /api/v1/app/status`,
`GET /api/v1/devices`, `GET /api/v1/devices/{id}`,
`GET /api/v1/devices/{id}/status` y
`GET /api/v1/devices/{id}/screenshot`. También están disponibles `POST
/api/v1/devices/{id}/control/acquire`, `POST
/api/v1/devices/{id}/control/release` y `POST
/api/v1/devices/{id}/input/{tap,long-press,swipe,key,text}`. Control y captura requieren
un dispositivo seleccionado con duplicación activa; Screenshotr puede no estar
disponible en algunos iPhone/iPad. No hay operaciones remotas de
preparación/captura, entrada táctil continua, Unicode ni portapapeles.

Ejemplo de entrada Android: adquiere un lease con `POST
/api/v1/devices/{id}/control/acquire`, JSON `{"durationSeconds":30}`; conserva el
`sessionToken` de la respuesta. Lee `geometry` de `/status` y envía `POST
/api/v1/devices/{id}/input/tap` con `X-Control-Session`, `Content-Type:
application/json` y JSON `{"x":120,"y":300,"geometryVersion":"..."}`. Usa
coordenadas dentro del ancho y alto reportados. `geometryVersion` es obligatorio.
La respuesta `202 dispatched` significa que el proceso ADB terminó con código
cero, no que el dispositivo haya mostrado el resultado. La app espera el proceso
de forma asíncrona hasta el timeout y lo cancela si el lease vence o el cliente
HTTP se desconecta; solo permite una acción simultánea por dispositivo. Libera con
`POST .../control/release`; el lease también vence solo.
Esta matriz es una especificación de portabilidad, no una afirmación de paridad
funcional.
