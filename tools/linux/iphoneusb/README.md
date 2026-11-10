# iPhoneMirror para Linux

Aplicación de Omarchy para duplicar pantallas de iPhone, iPad y Android en Linux. La captura de iPhone/iPad usa USB; Android usa ADB y el motor scrcpy, por USB o por la red local.

La adaptación Android utiliza el motor `scrcpy` del sistema. Sin cámara virtual seleccionada, abre una ventana limpia sin bordes que puede usarse como captura de ventana en OBS. Si detecta un dispositivo `v4l2loopback` y el usuario puede escribir en él, la app también puede seleccionarlo como salida directa para OBS, incluso si tiene una etiqueta personalizada. En ese modo scrcpy envía el video a `/dev/videoN` y una ventana limpia de iPhoneMirror lee la misma cámara virtual para mostrar la vista previa integrada. FFmpeg debe poder leer el dispositivo; si falla, la salida a OBS continúa y el diagnóstico queda en el registro. El perfil vertical u horizontal conserva la proporción del teléfono y rellena el espacio sobrante. No se ofrecen webcams físicas como destino. La ruta integrada aún requiere validación con Android y OBS reales.

## Uso

1. Conecta el iPhone/iPad por USB y acepta **Confiar en este ordenador**. En Android activa **Opciones de desarrollador → Depuración USB** y autoriza la computadora. Android también puede conectarse por ADB inalámbrico.
2. Abre **iPhoneMirror** desde el lanzador.
3. Selecciona el dispositivo y pulsa **Iniciar duplicación**. Para Android se necesitan `android-tools` y `scrcpy`, que se instalan como dependencias del paquete.

Durante la duplicación de iPhone/iPad, la barra superior muestra los FPS de cuadros nuevos que llegaron a la vista previa. La captura USB tiene un techo de 60 FPS y puede entregar menos según el juego, la carga del teléfono y el rendimiento de decodificación/presentación; el indicador mide la vista, no la frecuencia de refresco física de la pantalla.

La barra lateral abre la lista de teléfonos, los ajustes de captura, los controles de grabación/transmisión y el diagnóstico. Los ajustes también aparecen en el panel principal cuando corresponden al dispositivo seleccionado.

La lista Android muestra el modelo reportado por ADB y el tipo de conexión (USB o Wi-Fi). En iPhone/iPad se muestra el nombre del dispositivo cuando libimobiledevice puede leerlo; si no, se muestra un nombre genérico y se numera cuando hay varios. Los UDID de Apple, seriales ADB e IP:puerto de red se usan internamente y no se muestran en la interfaz.

La tarjeta de estado del dispositivo consulta batería, carga y temperatura en iPhone/iPad y Android. En Android usa `adb shell dumpsys battery`, con sondeo en segundo plano, límite de tiempo y actualización cada minuto; interpreta la temperatura estándar en décimas de °C y también acepta formatos decimales de algunos fabricantes. Valores fuera del rango de 0 a 100 °C se descartan. Algunos fabricantes no exponen la temperatura y en ese caso se muestran solo los datos disponibles.

La **API local de automatización** es opcional. Al activarla en Ajustes, escucha únicamente en `127.0.0.1:17890`; la clave se muestra ahí y se puede copiar. `GET /api/docs` y `GET /api/v1/openapi.json` describen el contrato. Las rutas autenticadas usan identificadores opacos y reportan estado, geometría y capacidades. Android publica la geometría lógica de `dumpsys input` mientras la duplicación está activa, con refresco cada dos segundos; si la lectura falla, el valor sigue siendo `null`. Para Android seleccionado y duplicándose, la API admite leases exclusivos, `tap`, `long-press`, `swipe`, teclas y texto ASCII seguro (hasta 256 caracteres). Los gestos usan el espacio `deviceLogicalPixels`, exigen `geometryVersion` actual y se cancelan al vencer o liberar el lease; el cambio de orientación observado revoca el lease. El texto Unicode y el portapapeles no están disponibles; la secuencia literal `%s` se rechaza porque Android la usa para representar espacios. La respuesta confirma el resultado del proceso ADB, no la ejecución visual de la acción. `GET /api/v1/devices/{id}/screenshot` devuelve un PNG durante una duplicación activa; usa ADB en Android y Screenshotr en iPhone/iPad. En iOS requiere que el dispositivo exponga ese servicio (normalmente con la imagen de desarrollador montada). La API no acepta conexiones desde la red. La paridad restante con Windows está documentada en [`docs/AUTOMATION_API_LINUX.md`](docs/AUTOMATION_API_LINUX.md).

Antes de iniciar, puedes activar o desactivar **Reproducir audio del dispositivo**. El audio se envía a la salida de sonido de la computadora; OBS puede capturarlo como audio del sistema. El ajuste se aplica al iniciar cada sesión.

En iPhone/iPad también puedes activar **Mezclar el micrófono de la computadora** para incluir la fuente predeterminada del escritorio en las grabaciones MP4 y transmisiones. Requiere una sesión PipeWire activa con `pw-record` y AAC para MP4/RTMP/SRT o Opus para WHIP en FFmpeg. La opción es independiente del audio del teléfono y permanece apagada por defecto.

La preferencia de audio y el perfil de calidad Android se guardan localmente para la siguiente ejecución. La grabación automática permanece desactivada al iniciar cada sesión, y los códigos de emparejamiento no se guardan.

El control Bluetooth de iPhone/iPad es experimental y opcional. Si lo habilitas, la computadora anuncia un teclado/ratón BLE; empareja «iPhoneMirror Linux» desde Bluetooth en el dispositivo. Cuando el control está activo, la ventana principal ofrece botones **Inicio** y **Multitarea**; el ratón y teclado también envían informes HID. El teclado envía letras, números, puntuación común, teclas de función, navegación y teclado numérico según el mapa HID estándar; los caracteres que dependen de la distribución del teclado pueden variar. La captura sigue funcionando si BlueZ o el adaptador no permiten registrar el periférico. La compatibilidad de emparejamiento y entrada depende del adaptador BLE y requiere validación con cada dispositivo.

El reenvío de audio de Android requiere Android 11 o posterior; en Android 10 o anterior scrcpy continúa con video y desactiva automáticamente el audio. En Android 11, deja la pantalla desbloqueada al iniciar la duplicación. Consulta los [requisitos de audio de scrcpy](https://github.com/Genymobile/scrcpy/blob/master/doc/audio.md).

Para emparejar Android por Wi-Fi desde la app, el teléfono debe usar Android 11 o posterior y estar en la misma red que la computadora. Abre **Opciones de desarrollador → Depuración inalámbrica → Emparejar con código** e ingresa la dirección/puerto y el código mostrados. Luego ingresa la dirección/puerto de la pantalla principal de Depuración inalámbrica; ese puerto puede ser distinto. Android conserva el emparejamiento, aunque la conexión puede necesitar restablecerse al cambiar de red o reiniciar la depuración inalámbrica. Consulta la [guía oficial de ADB inalámbrico](https://developer.android.com/tools/adb#connect-to-a-device-over-wi-fi-android-11+).

Para Android 10 o anterior, conecta primero el teléfono por USB y selecciónalo en la app. Pulsa **Conectar Android por USB y Wi-Fi**, ingresa la IP del teléfono y la app habilitará ADB TCP/IP en el puerto 5555 y conectará por la red local. Esta modalidad no cifra el tráfico; úsala solo en una red de confianza y desactiva la depuración de red al terminar. Android 11 o posterior también puede usar este modo cuando hay USB inicial, pero se recomienda el emparejamiento inalámbrico moderno. Consulta la [guía oficial de ADB TCP/IP](https://developer.android.com/tools/adb#connect-to-a-device-over-wi-fi-android-10-and-lower).

En **Modo de compatibilidad USB** puedes elegir Estándar, AirPlay por USB o Aisi compatible. Estándar es la opción recomendada; los otros dos modos experimentales sirven para probar otra negociación cuando el dispositivo o una app no muestran imagen. Los tres modos siguen usando el cable USB y el modo elegido se guarda localmente.

Para duplicar iPhone/iPad por la red local, abre **Teléfonos → Recibir AirPlay por Wi-Fi** y selecciona **iPhoneMirror** desde el Centro de control del dispositivo. Esta ruta usa UxPlay como proceso externo y abre su propia ventana; puedes capturar esa ventana en OBS. UxPlay es opcional y se distribuye en AUR (`uxplay`); además necesita los plugins GStreamer adecuados. AirPlay y el audio se reciben en la red local, no pasan por la sesión USB. La ventana externa aún no comparte los controles de grabación/transmisión del motor iOS. El contenido con protección de video de Apple no se puede reflejar con UxPlay; revisa sus limitaciones y configuración en el [proyecto UxPlay](https://github.com/FDH2/UxPlay).

La app fija los puertos TCP y UDP **7000–7002** para que el firewall pueda permitir la sesión de forma predecible. El descubrimiento requiere además mDNS por UDP **5353**. Si el iPhone no ve «iPhoneMirror» o se queda conectando, revisa el firewall del equipo y que el router permita multicast/mDNS entre ambos dispositivos; algunas redes de invitados aíslan a los clientes. UxPlay documenta estos puertos y el uso de `-p` en su [guía oficial](https://github.com/FDH2/UxPlay). No desactives el firewall como solución permanente. Si decides crear reglas, limítalas a la interfaz y subred de confianza y elimina las reglas cuando ya no uses AirPlay.

Con UFW, primero identifica la interfaz y la subred Wi-Fi local con `ip -brief addr`; sustituye `INTERFAZ_WIFI` y `CIDR_LOCAL` por esos valores. Las reglas limitadas a la subred permiten el descubrimiento mDNS y la sesión AirPlay:

```sh
sudo ufw allow in on INTERFAZ_WIFI from CIDR_LOCAL to any port 5353 proto udp
sudo ufw allow in on INTERFAZ_WIFI from CIDR_LOCAL to any port 7000:7002 proto tcp
sudo ufw allow in on INTERFAZ_WIFI from CIDR_LOCAL to any port 7000:7002 proto udp
```

UFW no sustituye los marcadores: reemplázalos antes de ejecutar los comandos. Para retirar las reglas después, usa `sudo ufw status numbered` y borra las entradas correspondientes con `sudo ufw delete NUMERO`.

La vista de iPhone/iPad se abre en una ventana limpia sin controles encima de la captura. También puede enviarse a una cámara V4L2 virtual con perfil fijo vertical 9:16 (1080 × 1920) u horizontal 16:9 (1920 × 1080); el núcleo adapta cada cuadro NV12 conservando la proporción y rellena el espacio sobrante, así el formato de cámara se mantiene al girar el teléfono. El video sale por la cámara virtual y el audio se reproduce en la computadora para que OBS lo capture como audio del sistema. Si la cámara virtual falla al abrirse, la duplicación local sigue activa y el estado de la app lo indica sin mostrar notificaciones emergentes. Para Android, scrcpy puede enviar a V4L2 y la vista integrada lee esa salida; la grabación automática Matroska sigue a cargo de scrcpy, y la grabación MP4 o transmisión manual utiliza el codificador integrado FFmpeg. La cámara virtual requiere que `v4l2loopback` esté instalado y cargado; se detecta desde sysfs y admite nombres personalizados. La compatibilidad de iPhone/iPad depende del modelo y de la versión de iOS; Android depende de ADB, del fabricante y de la versión del sistema. Las rutas V4L2 de ambas plataformas requieren validación física con OBS; no se garantiza la captura de aplicaciones que bloquean la grabación de pantalla ni se ha validado aún cada modelo.

En **Calidad de video** puedes elegir Original, Alta (máximo 1920 px, 60 FPS, 8 Mbps), Equilibrada (1280 px, 60 FPS, 5 Mbps) o Ahorro (960 px, 30 FPS, 3 Mbps). El límite de tamaño conserva la proporción del teléfono; los perfiles reducen uso de codificación y red cuando sea necesario. Solo afecta Android y lo aplica scrcpy tanto a la ventana como a V4L2. Consulta las [opciones oficiales de video de scrcpy](https://github.com/Genymobile/scrcpy/blob/master/doc/video.md).

En la ventana limpia del iPhone/iPad, pulsa **S** para guardar una captura PNG en `Imágenes/iPhoneMirror`. La imagen conserva la resolución y orientación actuales. El proceso de duplicación no se detiene.

En Android, pulsa **Capturar imagen** en la ventana principal. La captura se solicita por ADB y se guarda en la misma carpeta, incluso si la salida está configurada como cámara virtual.

Durante una duplicación Android, la pantalla principal también ofrece **Atrás**, **Inicio** y **Recientes**. Estos botones envían teclas de navegación mediante ADB al dispositivo activo. Con salida V4L2, puedes tocar y arrastrar sobre la imagen limpia para enviar un toque o gesto de deslizamiento a Android; la app consulta la geometría actual de la pantalla y excluye las barras negras del lienzo. Si el teléfono no informa su área lógica, no se envía el gesto. Este control depende de ADB y debe validarse en cada fabricante; para juegos que requieran respuesta rápida, el control táctil directo del teléfono ofrece menor latencia.

El icono de herramientas en la barra lateral abre **Diagnóstico del sistema**. Resume dependencias, sesión gráfica, PipeWire y salidas virtuales detectadas; puedes copiarlo para pedir ayuda. El informe omite nombres e identificadores de dispositivos, direcciones IP y códigos de emparejamiento.

Para grabar Android, activa **Grabar esta sesión** antes de iniciar. Al detener la duplicación, scrcpy finaliza un archivo Matroska (`.mkv`) con video y audio si el dispositivo permite capturar audio. También puedes seleccionar una cámara V4L2, iniciar la duplicación y usar **Iniciar grabación** para comenzar un MP4 durante la sesión; **Detener grabación** finaliza ese archivo sin detener la vista previa ni la transmisión. La grabación manual captura el lienzo que se entrega a OBS y puede incluir el audio de Android y el micrófono cuando PipeWire está disponible. Ambas modalidades guardan en `Imágenes/iPhoneMirror`.

En iPhone/iPad, activa **Grabar esta sesión** antes de iniciar o pulsa **Iniciar grabación** durante la duplicación. Se guarda un MP4 con video y el audio USB disponible. Pulsa **Detener grabación** para finalizarlo sin detener la vista previa ni una transmisión activa; el archivo solo aparece con su nombre final si FFmpeg lo cerró correctamente.

Para transmitir desde iPhone/iPad, inicia la duplicación y pulsa **Configurar transmisión**. La app admite RTMP/RTMPS, SRT y WHIP cuando la compilación instalada de FFmpeg incluye el protocolo y el codificador requeridos. Puedes iniciar una transmisión mientras grabas y detener cada salida por separado. La salida usa el perfil vertical u horizontal seleccionado para OBS. La conexión, el audio, la sincronización y la latencia dependen de FFmpeg, la red y el servicio receptor; no se han validado aún contra una plataforma externa. Las URL con clave y los tokens de WHIP no se guardan en preferencias ni en los registros de iPhoneMirror. FFmpeg los necesita durante la sesión y herramientas del mismo usuario pueden inspeccionar sus argumentos; evita compartir diagnósticos del proceso mientras transmites.

Si detienes la grabación y la transmisión continúa, el panel ofrece **Conservar** o **Descartar** para el archivo recién terminado. Descartar elimina ese archivo; conservarlo deja el MP4 en `Imágenes/iPhoneMirror`.

## Transmitir Android

Para enviar Android a RTMP/RTMPS, SRT o WHIP, selecciona una cámara `v4l2loopback`, inicia la duplicación y después configura la transmisión. El codificador recibe el lienzo vertical u horizontal que también alimenta OBS. Esta ruta de streaming requiere que la cámara virtual esté disponible y que FFmpeg admita el protocolo y el codificador H.264 elegidos.

Si PipeWire Pulse está disponible al iniciar la duplicación Android con una cámara virtual, iPhoneMirror crea una salida de audio virtual que mantiene la reproducción local. En el diálogo de transmisión puedes activar **Incluir audio del teléfono Android** para mezclar esa salida aislada con el video; no captura el resto de sonidos del escritorio. También puedes incluir el micrófono desde Ajustes. Si no hay una sesión Pulse compatible, la opción de audio del teléfono aparece desactivada y la transmisión sigue con video y micrófono. El reenvío de audio de scrcpy depende de la versión de Android y puede no estar disponible en dispositivos Android 10 o anteriores.

## Usar Android como cámara en OBS

La salida V4L2 requiere el módulo `v4l2loopback` correspondiente al kernel que estás ejecutando. La app no instala ni carga módulos de kernel. Para crear una cámara con nombre y número estables (requiere administrador):

```sh
sudo modprobe v4l2loopback devices=1 video_nr=20 card_label="iPhoneMirror" exclusive_caps=1
```

Si `v4l2loopback` ya está cargado, no lo retires mientras otras aplicaciones usen sus cámaras. Si tienes `v4l2loopback-ctl`, puedes crear un dispositivo adicional:

```sh
sudo v4l2loopback-ctl add -n "iPhoneMirror" /dev/video20
```

Después:

1. En iPhoneMirror pulsa **Actualizar dispositivos** y elige **Cámara virtual · video20** en **Salida de Android**. La cámara debe ser escribible por tu usuario.
2. Inicia la duplicación Android. scrcpy comienza a producir video en `/dev/video20`.
3. En OBS añade una fuente **Dispositivo de captura de video** y selecciona **iPhoneMirror**.

Con `exclusive_caps=1`, la cámara anuncia capacidades de productor hasta que scrcpy empieza a escribir; por eso conviene iniciar iPhoneMirror antes de añadirla en OBS. Si no aparece, confirma que el módulo está cargado, actualiza la lista y revisa los permisos de `/dev/video20`. Consulta las [opciones oficiales de v4l2loopback](https://github.com/v4l2loopback/v4l2loopback/blob/main/README.md#options).

## Construcción

El shell USB y el núcleo de captura se comparten con el repositorio en la rama `linux-port`. En este checkout integrado, `./bin/build` y `makepkg` usan automáticamente el código compartido del repositorio raíz. Si copias esta carpeta fuera del repositorio, coloca el checkout `linux-port` como `../iPhoneMirror`.

La integración de la fuente Windows conserva en Linux las mejoras de protocolo que son compartibles. La interfaz WPF, los controladores Windows, DLL, instalador y rutas de captura exclusivas de Windows no se copian; sus funciones requieren adaptadores nativos de Linux.

- `./bin/build` compila la interfaz Qt, el núcleo y LinuxShell sin instalar nada en el sistema.
- Para crear el paquete local de Arch sin instalarlo, entra en `pkgbuild` y ejecuta `makepkg --nodeps`; necesitas tener instaladas las dependencias de compilación declaradas en `PKGBUILD`.
- `./bin/install` instala las herramientas de compilación que falten, construye el paquete y pide autenticación para instalarlo en el sistema.

LinuxShell tiene su propio `src/LinuxShell/global.json` con SDK .NET `10.0.111` y `rollForward: latestFeature`; por eso puede compilar con SDK `10.0.113`. La configuración `global.json` del repositorio raíz se conserva para la solución compartida y no hace falta editarla para construir este port Linux. La resolución del SDK considera `global.json` según el directorio de trabajo y el directorio del proyecto; consulta la [documentación oficial de .NET](https://learn.microsoft.com/dotnet/core/tools/global-json) si cambias de SDK.

## Atajos

| Tecla | Acción |
|---|---|
| Space | Iniciar o detener duplicación |
| S en la vista previa de iPhone/iPad | Guardar captura PNG |
| F11 en la vista previa | Activar o salir de pantalla completa |
| Escape en pantalla completa | Volver al modo ventana; otro Escape cierra la vista previa |
| Capturar imagen (Android) | Guardar captura PNG por ADB |
| ? | Mostrar u ocultar atajos |
| Q | Salir |
