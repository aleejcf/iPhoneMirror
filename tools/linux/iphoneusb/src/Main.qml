import QtQuick
import QtQuick.Controls
import QtQuick.Controls.Material
import QtQuick.Layouts

ApplicationWindow {
    id: win
    width: 1420
    height: 850
    minimumWidth: 1040
    minimumHeight: 650
    visible: true
    title: qsTr("iPhoneMirror")
    color: "#1f1f1f"
    Material.theme: Material.Dark
    Material.accent: "#69b1f8"
    Material.background: "#1f1f1f"
    Material.foreground: "#f5f5f7"
    property color canvas: "#1f1f1f"
    property color panel: "#242424"
    property color raised: "#292929"
    property color border: "#3a3a3d"
    property color text: "#f5f5f7"
    property color muted: "#b7b7bc"
    property color accent: "#69b1f8"
    property color success: "#6ccb8f"
    property color warning: "#ffc55c"
    property color danger: "#c9363e"
    property bool helpVisible: false
    property string activeSection: "devices"
    property bool compact: width < 960 || height < 500
    property bool androidSelected: backend.selected.indexOf("android:") === 0
    property bool androidWifiSelected: androidSelected && backend.selected.substring(8).indexOf(":") >= 0
    property string connectionName: backend.airplayRunning ? qsTr("AirPlay por Wi-Fi")
        : (androidWifiSelected ? qsTr("Android por Wi-Fi")
        : (androidSelected ? qsTr("Android por USB")
        : (backend.selected.indexOf("ios:") === 0 ? qsTr("iPhone/iPad por USB")
        : qsTr("Sin dispositivo"))))

    Shortcut { sequence: "?"; context: Qt.ApplicationShortcut; onActivated: win.helpVisible = !win.helpVisible }
    Shortcut { sequence: "Q"; context: Qt.ApplicationShortcut; onActivated: Qt.quit() }
    Shortcut { sequence: "Space"; context: Qt.ApplicationShortcut; onActivated: backend.running ? backend.stop() : backend.start() }

    RowLayout {
        anchors.fill: parent
        spacing: 0

        Rectangle {
            Layout.preferredWidth: 72
            Layout.fillHeight: true
            color: "#1b1b1d"
            border.color: "#29292c"
            border.width: 1

            ColumnLayout {
                anchors.fill: parent
                anchors.topMargin: 12
                anchors.bottomMargin: 12
                spacing: 8

                Label {
                    text: "≡"
                    color: win.text
                    font.pixelSize: 23
                    horizontalAlignment: Text.AlignHCenter
                    Layout.fillWidth: true
                    Layout.preferredHeight: 42
                }

                Repeater {
                    model: [
                        { icon: "▣", label: qsTr("Dispositivos"), page: "devices" },
                        { icon: "▯", label: qsTr("Teléfonos"), page: "phones" },
                        { icon: "⚙", label: qsTr("Ajustes"), page: "settings" },
                        { icon: "◉", label: qsTr("Grabación"), page: "recording" },
                        { icon: "⚒", label: qsTr("Herramientas"), page: "tools" }
                    ]

                    delegate: Item {
                        required property var modelData
                        Layout.fillWidth: true
                        Layout.preferredHeight: 48
                        opacity: 1.0

                        Rectangle {
                            anchors.fill: parent
                            anchors.leftMargin: 5
                            anchors.rightMargin: 5
                            radius: 10
                            color: win.activeSection === modelData.page ? "#262d34" : (railMouse.containsMouse ? "#29292c" : "transparent")

                            Rectangle {
                                visible: win.activeSection === modelData.page
                                width: 3
                                height: 22
                                radius: 2
                                color: win.accent
                                anchors.left: parent.left
                                anchors.verticalCenter: parent.verticalCenter
                            }

                            Label {
                                anchors.centerIn: parent
                                text: modelData.icon
                                color: win.activeSection === modelData.page ? win.accent : win.muted
                                font.family: "Noto Sans Symbols 2"
                                font.pixelSize: 21
                            }
                        }

                        ToolTip.visible: railMouse.containsMouse
                        ToolTip.text: modelData.label
                        ToolTip.delay: 500
                        MouseArea {
                            id: railMouse
                            anchors.fill: parent
                            hoverEnabled: true
                            onClicked: {
                                win.activeSection = modelData.page
                                if (modelData.page === "phones") deviceDialog.open()
                                else if (modelData.page === "settings") settingsDialog.open()
                                else if (modelData.page === "recording") recordingDialog.open()
                                else if (modelData.page === "tools") diagnosticsDialog.open()
                            }
                        }
                    }
                }

                Item { Layout.fillHeight: true }

                Item {
                    Layout.fillWidth: true
                    Layout.preferredHeight: 46
                    Label {
                        anchors.centerIn: parent
                        text: "⚒"
                        color: win.muted
                        font.family: "Noto Sans Symbols 2"
                        font.pixelSize: 19
                    }
                    ToolTip.visible: diagnosticsMouse.containsMouse
                    ToolTip.text: qsTr("Diagnóstico del sistema")
                    ToolTip.delay: 500
                    MouseArea {
                        id: diagnosticsMouse
                        anchors.fill: parent
                        hoverEnabled: true
                        onClicked: {
                            win.activeSection = "tools"
                            diagnosticsDialog.open()
                        }
                    }
                }

                Item {
                    Layout.fillWidth: true
                    Layout.preferredHeight: 46
                    Label {
                        anchors.centerIn: parent
                        text: "ⓘ"
                        color: win.muted
                        font.family: "Noto Sans Symbols 2"
                        font.pixelSize: 20
                    }
                    ToolTip.visible: infoMouse.containsMouse
                    ToolTip.text: qsTr("Atajos de teclado")
                    MouseArea {
                        id: infoMouse
                        anchors.fill: parent
                        hoverEnabled: true
                        onClicked: win.helpVisible = true
                    }
                }
            }
        }

        ColumnLayout {
            Layout.fillWidth: true
            Layout.fillHeight: true
            spacing: 0

            RowLayout {
                Layout.fillWidth: true
                Layout.preferredHeight: 82
                Layout.leftMargin: 20
                Layout.rightMargin: 24
                spacing: 18

                ColumnLayout {
                    spacing: 3
                    Label {
                        text: qsTr("iPhoneMirror")
                        color: win.text
                        font.family: "Noto Sans"
                        font.pixelSize: win.compact ? 19 : 24
                        font.weight: Font.DemiBold
                    }
                    Label {
                        text: qsTr("Duplicación de pantalla en Linux")
                        color: win.muted
                        font.family: "Noto Sans"
                        font.pixelSize: 12
                        visible: !win.compact
                    }
                }

                Item { Layout.fillWidth: true }

                RowLayout {
                    spacing: 9
                    Layout.maximumWidth: 460
                    visible: !win.compact
                    Rectangle {
                        width: 7
                        height: 7
                        radius: 4
                        color: backend.running ? win.success : (backend.devices.length > 0 ? win.success : win.warning)
                    }
                    Label {
                        Layout.fillWidth: true
                        text: backend.status
                        color: backend.running ? win.success : win.muted
                        font.pixelSize: 12
                        elide: Text.ElideRight
                    }
                    Label {
                        visible: backend.running && backend.selected.indexOf("ios:") === 0 && backend.captureFps >= 0
                        text: qsTr("Vista: %1 FPS").arg(backend.captureFps.toFixed(0))
                        color: backend.captureFps >= 55 ? win.success
                            : backend.captureFps >= 40 ? win.warning : win.danger
                        font.pixelSize: 12
                        font.weight: Font.DemiBold
                    }
                }

                Button {
                    id: androidScreenshotButton
                    visible: backend.running && !backend.airplayRunning && backend.selected.indexOf("android:") === 0
                    text: qsTr("Capturar imagen")
                    enabled: visible && !backend.status.startsWith(qsTr("Guardando captura"))
                    onClicked: backend.captureAndroidScreenshot()
                    implicitWidth: win.compact ? 125 : 140
                    implicitHeight: win.compact ? 40 : 44
                    contentItem: Label {
                        text: androidScreenshotButton.text
                        color: androidScreenshotButton.enabled ? win.text : win.muted
                        font.pixelSize: 12
                        horizontalAlignment: Text.AlignHCenter
                        verticalAlignment: Text.AlignVCenter
                    }
                    background: Rectangle {
                        radius: 10
                        color: androidScreenshotButton.enabled ? "#292929" : "#3a3a3d"
                        border.color: win.border
                        border.width: 1
                    }
                }

                Button {
                    id: captureButton
                    text: backend.running ? qsTr("Detener duplicación") : qsTr("Iniciar duplicación")
                    enabled: backend.running || backend.devices.length > 0
                    onClicked: backend.running ? backend.stop() : backend.start()
                    implicitWidth: win.compact ? 150 : 190
                    implicitHeight: win.compact ? 40 : 44
                    contentItem: Label {
                        text: captureButton.text
                        color: "#ffffff"
                        font.pixelSize: 14
                        font.weight: Font.DemiBold
                        horizontalAlignment: Text.AlignHCenter
                        verticalAlignment: Text.AlignVCenter
                    }
                    background: Rectangle {
                        radius: 12
                        color: !captureButton.enabled ? "#3a3a3d" : (backend.running ? (captureButton.down ? "#a82830" : "#c9363e") : (captureButton.down ? "#2886de" : "#479ef5"))
                        border.color: !captureButton.enabled ? "#454549" : (backend.running ? "#ff979c" : "#78baff")
                        border.width: 1
                    }
                }
            }

            Rectangle { Layout.fillWidth: true; height: 1; color: "#2b2b2e" }

            RowLayout {
                Layout.fillWidth: true
                Layout.fillHeight: true
                Layout.margins: 18
                spacing: 18

                ColumnLayout {
                    Layout.fillWidth: true
                    Layout.fillHeight: true
                    Layout.minimumWidth: 480
                    spacing: 14
                    visible: !win.compact

                    Rectangle {
                        Layout.fillWidth: true
                        Layout.preferredHeight: 58
                        radius: 12
                        color: "transparent"
                        border.color: win.border
                        border.width: 1

                        RowLayout {
                            anchors.fill: parent
                            anchors.leftMargin: 15
                            anchors.rightMargin: 10
                            spacing: 12
                            Label {
                                text: "●"
                                color: backend.running ? win.success : (backend.devices.length > 0 ? win.warning : win.muted)
                                font.pixelSize: 10
                            }
                            Label {
                                Layout.fillWidth: true
                                text: backend.running ? qsTr("La captura está activa en la ventana del dispositivo.") : (backend.devices.length > 0 ? qsTr("El dispositivo está listo para duplicar.") : qsTr("Conecta un iPhone/iPad o activa la depuración USB en Android."))
                                color: backend.running ? win.success : (backend.devices.length > 0 ? win.warning : win.muted)
                                font.pixelSize: 13
                                elide: Text.ElideRight
                            }
                            Button {
                                text: qsTr("Actualizar")
                                onClicked: backend.refresh()
                                implicitHeight: 36
                                contentItem: Label {
                                    text: qsTr("Actualizar")
                                    color: win.text
                                    font.pixelSize: 12
                                    horizontalAlignment: Text.AlignHCenter
                                    verticalAlignment: Text.AlignVCenter
                                }
                                background: Rectangle {
                                    radius: 9
                                    color: parent.down ? "#38383b" : "#292929"
                                    border.color: win.border
                                    border.width: 1
                                }
                            }
                        }
                    }

                    Rectangle {
                        Layout.fillWidth: true
                        Layout.fillHeight: true
                        radius: 14
                        color: "#080809"
                        border.color: "#3c3c40"
                        border.width: 1

                        ColumnLayout {
                            anchors.centerIn: parent
                            width: Math.min(parent.width - 48, 440)
                            spacing: 13

                            Image {
                                Layout.alignment: Qt.AlignHCenter
                                Layout.preferredWidth: 116
                                Layout.preferredHeight: 116
                                source: "qrc:/assets/iPhoneMirror.png"
                                fillMode: Image.PreserveAspectFit
                                opacity: 0.72
                                smooth: true
                            }
                            Label {
                                Layout.fillWidth: true
                                text: backend.airplayRunning ? qsTr("Receptor AirPlay activo") : backend.running ? (backend.outputDevice.length ? qsTr("Salida de cámara activa") : qsTr("Vista previa en directo")) : qsTr("Vista previa del dispositivo")
                                color: win.text
                                font.pixelSize: 23
                                font.weight: Font.DemiBold
                                horizontalAlignment: Text.AlignHCenter
                                wrapMode: Text.Wrap
                            }
                            Label {
                                Layout.fillWidth: true
                                text: backend.airplayRunning ? qsTr("UxPlay recibe la imagen en su propia ventana. En OBS, selecciónala como captura de ventana.") : backend.running ? (backend.outputDevice.length ? (win.androidSelected ? qsTr("Vista limpia integrada y salida de cámara %1 para OBS.").arg(backend.outputDevice) : qsTr("La imagen se está enviando a %1 para OBS.").arg(backend.outputDevice)) : qsTr("La pantalla duplicada se muestra en su ventana de vista previa.")) : qsTr("Conecta un teléfono y comienza la duplicación para abrir la vista previa.")
                                color: win.muted
                                font.pixelSize: 13
                                horizontalAlignment: Text.AlignHCenter
                                wrapMode: Text.Wrap
                            }
                            RowLayout {
                                Layout.alignment: Qt.AlignHCenter
                                spacing: 8
                                visible: backend.running && backend.selected.indexOf("ios:") === 0 && backend.reverseControlConnected
                                Button {
                                    text: qsTr("Inicio")
                                    Accessible.name: qsTr("Ir al inicio del iPhone/iPad")
                                    onClicked: backend.sendIosBluetoothShortcut("home")
                                }
                                Button {
                                    text: qsTr("Multitarea")
                                    Accessible.name: qsTr("Abrir el selector de aplicaciones del iPhone/iPad")
                                    onClicked: backend.sendIosBluetoothShortcut("appSwitcher")
                                }
                            }
                            RowLayout {
                                Layout.alignment: Qt.AlignHCenter
                                spacing: 8
                                visible: backend.running && win.androidSelected && !backend.airplayRunning
                                Button {
                                    text: qsTr("Atrás")
                                    Accessible.name: qsTr("Volver atrás en Android")
                                    onClicked: backend.sendAndroidNavigation("back")
                                }
                                Button {
                                    text: qsTr("Inicio")
                                    Accessible.name: qsTr("Ir al inicio de Android")
                                    onClicked: backend.sendAndroidNavigation("home")
                                }
                                Button {
                                    text: qsTr("Recientes")
                                    Accessible.name: qsTr("Mostrar aplicaciones recientes de Android")
                                    onClicked: backend.sendAndroidNavigation("overview")
                                }
                            }
                            Button {
                                visible: !backend.running
                                enabled: backend.devices.length > 0
                                text: qsTr("Iniciar duplicación")
                                onClicked: backend.start()
                                implicitWidth: 190
                                implicitHeight: 40
                                Layout.alignment: Qt.AlignHCenter
                                contentItem: Label {
                                    text: qsTr("Iniciar duplicación")
                                    color: parent.enabled ? "#101418" : win.muted
                                    font.pixelSize: 13
                                    font.weight: Font.DemiBold
                                    horizontalAlignment: Text.AlignHCenter
                                    verticalAlignment: Text.AlignVCenter
                                }
                                background: Rectangle {
                                    radius: 10
                                    color: parent.enabled ? win.accent : "#3a3a3d"
                                }
                            }
                        }
                    }

                    Rectangle {
                        Layout.fillWidth: true
                        Layout.preferredHeight: 104
                        radius: 12
                        color: "#242424"
                        border.color: win.border
                        border.width: 1

                        ColumnLayout {
                            anchors.fill: parent
                            spacing: 0
                            RowLayout {
                                Layout.fillWidth: true
                                Layout.preferredHeight: 34
                                Layout.leftMargin: 14
                                spacing: 9
                                Label { text: "●"; color: backend.running ? win.success : win.muted; font.pixelSize: 9 }
                                Label {
                                    text: backend.running
                                        ? (win.androidSelected ? qsTr("Duplicación Android activa") : qsTr("Captura USB activa"))
                                        : qsTr("Sin nube · conexión directa al dispositivo")
                                    color: win.muted
                                    font.pixelSize: 11
                                }
                            }
                            Rectangle { Layout.fillWidth: true; height: 1; color: "#343437" }
                            RowLayout {
                                Layout.fillWidth: true
                                Layout.fillHeight: true
                                spacing: 0
                                Repeater {
                                    model: [
                                        { label: qsTr("CONEXIÓN"), value: backend.running ? qsTr("Activa") : (backend.devices.length > 0 ? win.connectionName : qsTr("Local")), detail: win.androidWifiSelected ? qsTr("Red Wi-Fi local") : (win.androidSelected ? qsTr("Cable USB") : qsTr("Cable USB Apple")) },
                                        { label: qsTr("DISPOSITIVO"), value: backend.selectedLabel.length ? backend.selectedLabel : "—", detail: backend.devices.length > 0 ? qsTr("Conectado y autorizado") : qsTr("Esperando conexión") },
                                        { label: qsTr("ESTADO"), value: backend.running ? qsTr("En directo") : (backend.devices.length > 0 ? qsTr("Listo") : qsTr("Desconectado")), detail: backend.running ? (backend.outputDevice.length ? qsTr("Fuente de cámara para OBS") : qsTr("Ventana limpia")) : qsTr("Baja latencia") }
                                    ]
                                    delegate: Item {
                                        required property var modelData
                                        Layout.fillWidth: true
                                        Layout.fillHeight: true
                                        ColumnLayout {
                                            anchors.fill: parent
                                            anchors.leftMargin: 14
                                            anchors.rightMargin: 10
                                            anchors.topMargin: 7
                                            spacing: 1
                                            Label { text: modelData.label; color: win.muted; font.pixelSize: 9; font.letterSpacing: 0.6 }
                                            Label { text: modelData.value; color: win.text; font.pixelSize: 16; font.weight: Font.Medium; elide: Text.ElideMiddle; Layout.fillWidth: true }
                                            Label { text: modelData.detail; color: win.muted; font.pixelSize: 9; elide: Text.ElideRight; Layout.fillWidth: true }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                Rectangle { Layout.fillHeight: true; width: 1; color: "#2b2b2e"; visible: !win.compact }

                Rectangle {
                    Layout.preferredWidth: win.compact ? 1 : 330
                    Layout.minimumWidth: win.compact ? 0 : 300
                    Layout.fillWidth: win.compact
                    Layout.fillHeight: true
                    radius: 14
                    color: "#242424"
                    border.color: "#37373a"
                    border.width: 1

                    ScrollView {
                        anchors.fill: parent
                        anchors.margins: 20
                        clip: true

                        ColumnLayout {
                            width: parent.width
                            spacing: 0

                            Label { text: qsTr("Fuentes"); color: win.text; font.pixelSize: 18; font.weight: Font.DemiBold }
                            Label {
                                Layout.fillWidth: true
                                Layout.topMargin: 5
                                text: qsTr("Selecciona el dispositivo que quieres duplicar.")
                                color: win.muted
                                font.pixelSize: 11
                                wrapMode: Text.Wrap
                            }

                            Label {
                                Layout.topMargin: 24
                                text: qsTr("DISPOSITIVOS")
                                color: win.muted
                                font.pixelSize: 10
                                font.letterSpacing: 0.6
                            }

                            ComboBox {
                                id: devicePicker
                                Layout.fillWidth: true
                                Layout.topMargin: 8
                                model: backend.devices
                                textRole: "label"
                                valueRole: "id"
                                enabled: !backend.running
                                currentIndex: backend.selectedIndex
                                onActivated: backend.selected = currentValue
                                implicitHeight: 44
                                contentItem: Label {
                                    leftPadding: 13
                                    rightPadding: 32
                                    text: backend.devices.length ? devicePicker.currentText : qsTr("No se detectó ningún dispositivo")
                                    color: backend.devices.length ? win.text : win.muted
                                    font.pixelSize: 12
                                    verticalAlignment: Text.AlignVCenter
                                    elide: Text.ElideMiddle
                                }
                                indicator: Label {
                                    x: devicePicker.width - width - 12
                                    y: (devicePicker.height - height) / 2
                                    text: "⌄"
                                    color: win.muted
                                    font.pixelSize: 17
                                }
                                background: Rectangle {
                                    radius: 9
                                    color: "#292929"
                                    border.color: "#414145"
                                    border.width: 1
                                }
                                popup.background: Rectangle {
                                    color: "#292929"
                                    border.color: "#454549"
                                    radius: 9
                                }
                                delegate: ItemDelegate {
                                    width: devicePicker.width
                                    contentItem: Label { text: modelData.label; color: win.text; font.pixelSize: 12; elide: Text.ElideMiddle }
                                    background: Rectangle { color: highlighted ? "#38383b" : "transparent"; radius: 7 }
                                }
                            }

                            Rectangle {
                                Layout.fillWidth: true
                                Layout.topMargin: 12
                                Layout.preferredHeight: 44
                                radius: 9
                                visible: backend.deviceHealthText.length > 0
                                color: backend.deviceHealthWarning ? "#352923" : "#202b27"
                                border.color: backend.deviceHealthWarning ? "#8f573e" : "#34443d"
                                RowLayout {
                                    anchors.fill: parent
                                    anchors.leftMargin: 12
                                    anchors.rightMargin: 12
                                    spacing: 8
                                    Label {
                                        text: backend.deviceHealthWarning ? "⚠" : "♥"
                                        color: backend.deviceHealthWarning ? "#f1a46e" : "#8bc3a6"
                                        font.pixelSize: 13
                                    }
                                    Label {
                                        Layout.fillWidth: true
                                        text: backend.deviceHealthText
                                        color: backend.deviceHealthWarning ? "#f1c2a2" : "#b9d3c5"
                                        font.pixelSize: 11
                                        elide: Text.ElideRight
                                    }
                                }
                            }

                            Button {
                                Layout.fillWidth: true
                                Layout.topMargin: 10
                                text: qsTr("Actualizar dispositivos")
                                onClicked: backend.refresh()
                                implicitHeight: 40
                                contentItem: Label {
                                    text: qsTr("Actualizar dispositivos")
                                    color: win.text
                                    font.pixelSize: 12
                                    horizontalAlignment: Text.AlignHCenter
                                    verticalAlignment: Text.AlignVCenter
                                }
                                background: Rectangle { color: "#2b2b2d"; border.color: "#48484c"; border.width: 1; radius: 9 }
                            }

                            Button {
                                Layout.fillWidth: true
                                Layout.topMargin: 8
                                enabled: !backend.running
                                text: qsTr("Emparejar Android por Wi-Fi")
                                onClicked: wirelessPairDialog.open()
                                implicitHeight: 40
                                contentItem: Label {
                                    text: qsTr("Emparejar Android por Wi-Fi")
                                    color: parent.enabled ? win.text : win.muted
                                    font.pixelSize: 12
                                    horizontalAlignment: Text.AlignHCenter
                                    verticalAlignment: Text.AlignVCenter
                                }
                                background: Rectangle { color: "#2b2b2d"; border.color: "#48484c"; border.width: 1; radius: 9 }
                            }

                            Label {
                                Layout.fillWidth: true
                                Layout.topMargin: 7
                                text: qsTr("Android 11 o posterior · ambos dispositivos deben estar en la misma red Wi-Fi.")
                                color: win.muted
                                font.pixelSize: 10
                                wrapMode: Text.Wrap
                            }

                            Button {
                                Layout.fillWidth: true
                                Layout.topMargin: 8
                                visible: win.androidSelected || backend.selected.indexOf("ios:") === 0
                                enabled: !backend.running
                                text: qsTr("Conectar Android por USB y Wi-Fi")
                                onClicked: legacyWifiDialog.open()
                                implicitHeight: 40
                                contentItem: Label {
                                    text: qsTr("Conectar Android por USB y Wi-Fi")
                                    color: parent.enabled ? win.text : win.muted
                                    font.pixelSize: 12
                                    horizontalAlignment: Text.AlignHCenter
                                    verticalAlignment: Text.AlignVCenter
                                }
                                background: Rectangle { color: "#2b2b2d"; border.color: "#48484c"; border.width: 1; radius: 9 }
                            }
                            Label {
                                Layout.fillWidth: true
                                visible: backend.selected.indexOf("android:") === 0
                                text: qsTr("Compatible con Android 10 y anteriores. ADB TCP/IP no cifra la conexión: úsalo solo en una red de confianza.")
                                color: win.warning
                                font.pixelSize: 10
                                wrapMode: Text.Wrap
                            }

                            Switch {
                                id: androidRecordingSwitch
                                Layout.topMargin: 16
                                enabled: !backend.running && (backend.selected.indexOf("android:") === 0 || backend.selected.indexOf("ios:") === 0)
                                checked: backend.recordingEnabled
                                onToggled: backend.recordingEnabled = checked
                                text: qsTr("Grabar esta sesión")
                                contentItem: Label {
                                    leftPadding: 0
                                    rightPadding: androidRecordingSwitch.indicator.width + androidRecordingSwitch.spacing
                                    text: androidRecordingSwitch.text
                                    color: androidRecordingSwitch.enabled ? win.text : win.muted
                                    font.pixelSize: 12
                                    verticalAlignment: Text.AlignVCenter
                                    wrapMode: Text.Wrap
                                }
                            }
                            Label {
                                Layout.fillWidth: true
                                Layout.leftMargin: 8
                                text: backend.selected.indexOf("ios:") === 0
                                    ? qsTr("Guarda video MP4 y el audio recibido por USB en Imágenes/iPhoneMirror al detener la duplicación.")
                                    : qsTr("Guarda video Matroska y, si está disponible, audio en Imágenes/iPhoneMirror al detener la duplicación.")
                                color: win.muted
                                font.pixelSize: 10
                                wrapMode: Text.Wrap
                            }
                            RowLayout {
                                Layout.fillWidth: true
                                Layout.topMargin: 8
                                visible: backend.running && (backend.selected.indexOf("ios:") === 0 ||
                                    (backend.selected.indexOf("android:") === 0 && backend.outputDevice.length > 0 &&
                                    (backend.androidOutputReady || backend.mediaRecordingActive || backend.mediaStreamingActive)))
                                Button {
                                    Layout.fillWidth: true
                                    visible: !backend.mediaStreamingActive
                                    text: qsTr("Configurar transmisión")
                                    onClicked: streamDialog.open()
                                }
                                Button {
                                    Layout.fillWidth: true
                                    visible: backend.mediaStreamingActive
                                    text: qsTr("Detener transmisión")
                                    onClicked: backend.stopMediaOutput("streaming")
                                }
                                Button {
                                    Layout.fillWidth: true
                                    visible: (backend.selected.indexOf("ios:") === 0 || backend.selected.indexOf("android:") === 0) && !backend.mediaRecordingActive
                                    text: qsTr("Iniciar grabación")
                                    onClicked: backend.startMediaRecording()
                                }
                                Button {
                                    Layout.fillWidth: true
                                    visible: (backend.selected.indexOf("ios:") === 0 || backend.selected.indexOf("android:") === 0) && backend.mediaRecordingActive
                                    text: qsTr("Detener grabación")
                                    onClicked: backend.stopMediaOutput("recording")
                                }
                            }
                            RowLayout {
                                Layout.fillWidth: true
                                visible: backend.recordingReviewPending
                                Label {
                                    Layout.fillWidth: true
                                    text: qsTr("La grabación terminó. ¿Quieres conservarla?")
                                    color: win.muted
                                    wrapMode: Text.Wrap
                                    font.pixelSize: 10
                                }
                                Button {
                                    text: qsTr("Conservar")
                                    onClicked: backend.keepCompletedRecording()
                                }
                                Button {
                                    text: qsTr("Descartar")
                                    onClicked: backend.discardCompletedRecording()
                                }
                            }
                            Label {
                                Layout.fillWidth: true
                                visible: backend.running && (backend.selected.indexOf("ios:") === 0 ||
                                    backend.selected.indexOf("android:") === 0) &&
                                    (backend.mediaRecordingActive || backend.mediaStreamingActive)
                                text: backend.mediaRecordingActive && backend.mediaStreamingActive
                                    ? qsTr("Grabando y transmitiendo")
                                    : backend.mediaRecordingActive ? qsTr("Grabación activa") : qsTr("Transmisión activa")
                                color: win.success
                                font.pixelSize: 11
                            }

                            Rectangle { Layout.fillWidth: true; height: 1; Layout.topMargin: 24; Layout.bottomMargin: 17; color: "#3a3a3d" }

                            RowLayout {
                                Layout.fillWidth: true
                                spacing: 9
                                Rectangle { width: 7; height: 7; radius: 4; color: backend.devices.length > 0 ? win.success : win.warning }
                                Label { text: backend.devices.length > 0 ? win.connectionName : qsTr("Sin dispositivo conectado"); color: win.text; font.pixelSize: 13; font.weight: Font.DemiBold }
                            }
                            Label {
                                Layout.fillWidth: true
                                Layout.topMargin: 7
                                text: backend.status
                                color: win.muted
                                font.pixelSize: 11
                                wrapMode: Text.Wrap
                            }
                            Label {
                                Layout.fillWidth: true
                                Layout.topMargin: 15
                                text: win.androidWifiSelected
                                    ? qsTr("Android: conectado mediante depuración inalámbrica en la red local.")
                                    : (win.androidSelected
                                        ? qsTr("Android: activa depuración USB y autoriza esta computadora.")
                                        : qsTr("iPhone/iPad: desbloquéalo y acepta «Confiar» en esta computadora."))
                                color: win.muted
                                font.pixelSize: 11
                                wrapMode: Text.Wrap
                            }

                            Rectangle { Layout.fillWidth: true; height: 1; Layout.topMargin: 23; Layout.bottomMargin: 17; color: "#3a3a3d" }

                            Label { text: qsTr("Métodos de conexión"); color: win.text; font.pixelSize: 13; font.weight: Font.DemiBold }
                            Label {
                                Layout.fillWidth: true
                                Layout.topMargin: 7
                                text: qsTr("iPhone/iPad se captura por USB. Android usa scrcpy y admite USB o ADB por red.")
                                color: win.muted
                                font.pixelSize: 11
                                wrapMode: Text.Wrap
                            }
                            Label {
                                visible: win.androidSelected || backend.selected.indexOf("ios:") === 0
                                Layout.topMargin: 18
                                text: qsTr("SALIDA DE VIDEO")
                                color: win.muted
                                font.pixelSize: 10
                                font.letterSpacing: 0.6
                            }
                            ComboBox {
                                id: outputPicker
                                visible: win.androidSelected || backend.selected.indexOf("ios:") === 0
                                Layout.fillWidth: true
                                Layout.topMargin: 7
                                model: backend.outputDevices
                                textRole: "label"
                                valueRole: "id"
                                enabled: !backend.running
                                currentIndex: {
                                    for (let i = 0; i < backend.outputDevices.length; ++i) {
                                        if (backend.outputDevices[i].id === backend.outputDevice)
                                            return i
                                    }
                                    return 0
                                }
                                onActivated: backend.outputDevice = currentValue
                                implicitHeight: 42
                                contentItem: Label {
                                    leftPadding: 12
                                    rightPadding: 30
                                    text: outputPicker.currentText
                                    color: win.text
                                    font.pixelSize: 11
                                    verticalAlignment: Text.AlignVCenter
                                    elide: Text.ElideMiddle
                                }
                                background: Rectangle {
                                    radius: 9
                                    color: "#292929"
                                    border.color: "#414145"
                                }
                                delegate: ItemDelegate {
                                    width: outputPicker.width
                                    contentItem: Label { text: modelData.label; color: win.text; font.pixelSize: 11; elide: Text.ElideMiddle }
                                    background: Rectangle { color: highlighted ? "#38383b" : "transparent"; radius: 7 }
                                }
                            }
                            Label {
                                visible: win.androidSelected || backend.selected.indexOf("ios:") === 0
                                Layout.fillWidth: true
                                Layout.topMargin: 6
                                text: backend.outputDevice.length
                                    ? (win.androidSelected
                                        ? qsTr("OBS puede usar esta cámara virtual como fuente de vídeo.")
                                        : qsTr("La captura de iPhone se adapta al perfil y se envía a esta cámara virtual. El audio sigue en la salida de sonido de la computadora."))
                                    : qsTr("Usa la ventana limpia como fuente en OBS. La cámara virtual aparece cuando v4l2loopback está cargado y tienes permiso para usarla.")
                                color: win.muted
                                font.pixelSize: 10
                                wrapMode: Text.Wrap
                            }
                            Label {
                                visible: backend.selected.indexOf("ios:") === 0
                                Layout.topMargin: 16
                                text: qsTr("MODO DE COMPATIBILIDAD USB")
                                color: win.muted
                                font.pixelSize: 10
                                font.letterSpacing: 0.6
                            }
                            ComboBox {
                                id: iosProjectionModePicker
                                visible: backend.selected.indexOf("ios:") === 0
                                Layout.fillWidth: true
                                Layout.topMargin: 7
                                model: backend.iosProjectionModes
                                textRole: "label"
                                valueRole: "id"
                                enabled: !backend.running
                                currentIndex: backend.iosProjectionMode === "airplay" ? 1
                                    : backend.iosProjectionMode === "aisi" ? 2 : 0
                                onActivated: backend.iosProjectionMode = currentValue
                                implicitHeight: 42
                                contentItem: Label {
                                    leftPadding: 12
                                    rightPadding: 30
                                    text: iosProjectionModePicker.currentText
                                    color: win.text
                                    font.pixelSize: 11
                                    verticalAlignment: Text.AlignVCenter
                                    elide: Text.ElideRight
                                }
                                background: Rectangle { radius: 9; color: "#292929"; border.color: "#414145" }
                                delegate: ItemDelegate {
                                    width: iosProjectionModePicker.width
                                    contentItem: Label { text: modelData.label; color: win.text; font.pixelSize: 11; elide: Text.ElideRight }
                                    background: Rectangle { color: highlighted ? "#38383b" : "transparent"; radius: 7 }
                                }
                            }
                            Label {
                                visible: backend.selected.indexOf("ios:") === 0
                                Layout.fillWidth: true
                                Layout.topMargin: 5
                                text: qsTr("Si la pantalla queda negra o no aparece, detén la captura y prueba otro modo. Todos usan la conexión USB.")
                                color: win.muted
                                font.pixelSize: 10
                                wrapMode: Text.Wrap
                            }
                            Label {
                                visible: (win.androidSelected || backend.selected.indexOf("ios:") === 0)
                                    && backend.outputDevice.length > 0
                                Layout.topMargin: 14
                                text: qsTr("PERFIL PARA OBS / TIKTOK")
                                color: win.muted
                                font.pixelSize: 10
                                font.letterSpacing: 0.6
                            }
                            ComboBox {
                                id: iosOutputProfilePicker
                                visible: (win.androidSelected || backend.selected.indexOf("ios:") === 0)
                                    && backend.outputDevice.length > 0
                                Layout.fillWidth: true
                                Layout.topMargin: 7
                                model: backend.iosOutputProfiles
                                textRole: "label"
                                valueRole: "id"
                                enabled: !backend.running
                                currentIndex: backend.iosOutputProfile === "horizontal" ? 1 : 0
                                onActivated: backend.iosOutputProfile = currentValue
                                implicitHeight: 42
                                contentItem: Label {
                                    leftPadding: 12
                                    rightPadding: 30
                                    text: iosOutputProfilePicker.currentText
                                    color: win.text
                                    font.pixelSize: 11
                                    verticalAlignment: Text.AlignVCenter
                                    elide: Text.ElideRight
                                }
                                background: Rectangle {
                                    radius: 9
                                    color: "#292929"
                                    border.color: "#414145"
                                }
                                delegate: ItemDelegate {
                                    width: iosOutputProfilePicker.width
                                    contentItem: Label { text: modelData.label; color: win.text; font.pixelSize: 11; elide: Text.ElideRight }
                                    background: Rectangle { color: highlighted ? "#38383b" : "transparent"; radius: 7 }
                                }
                            }
                            Label {
                                visible: backend.selected.indexOf("android:") === 0
                                Layout.topMargin: 16
                                text: qsTr("CALIDAD DE VIDEO")
                                color: win.muted
                                font.pixelSize: 10
                                font.letterSpacing: 0.6
                            }
                            ComboBox {
                                id: androidQualityPicker
                                visible: backend.selected.indexOf("android:") === 0
                                Layout.fillWidth: true
                                Layout.topMargin: 7
                                model: backend.androidQualityProfiles
                                textRole: "label"
                                valueRole: "id"
                                enabled: !backend.running
                                currentIndex: {
                                    for (let i = 0; i < backend.androidQualityProfiles.length; ++i) {
                                        if (backend.androidQualityProfiles[i].id === backend.androidQualityProfile)
                                            return i
                                    }
                                    return 1
                                }
                                onActivated: backend.androidQualityProfile = currentValue
                                implicitHeight: 42
                                contentItem: Label {
                                    leftPadding: 12
                                    rightPadding: 30
                                    text: androidQualityPicker.currentText
                                    color: win.text
                                    font.pixelSize: 11
                                    verticalAlignment: Text.AlignVCenter
                                    elide: Text.ElideRight
                                }
                                background: Rectangle { radius: 9; color: "#292929"; border.color: "#414145" }
                                delegate: ItemDelegate {
                                    width: androidQualityPicker.width
                                    contentItem: Label { text: modelData.label; color: win.text; font.pixelSize: 11; elide: Text.ElideRight }
                                    background: Rectangle { color: highlighted ? "#38383b" : "transparent"; radius: 7 }
                                }
                            }
                            Label {
                                visible: backend.selected.indexOf("android:") === 0
                                Layout.fillWidth: true
                                Layout.topMargin: 5
                                text: qsTr("La resolución mantiene la proporción original. Puedes bajar el perfil si la captura o la codificación se congelan.")
                                color: win.muted
                                font.pixelSize: 10
                                wrapMode: Text.Wrap
                            }
                            Switch {
                                id: deviceAudioSwitch
                                Layout.fillWidth: true
                                Layout.topMargin: 16
                                enabled: !backend.running
                                checked: backend.audioEnabled
                                onToggled: backend.audioEnabled = checked
                                text: qsTr("Reproducir audio del dispositivo")
                                contentItem: Label {
                                    leftPadding: 0
                                    rightPadding: deviceAudioSwitch.indicator.width + deviceAudioSwitch.spacing
                                    text: deviceAudioSwitch.text
                                    color: deviceAudioSwitch.enabled ? win.text : win.muted
                                    font.pixelSize: 11
                                    verticalAlignment: Text.AlignVCenter
                                    wrapMode: Text.Wrap
                                }
                            }
                            Label {
                                Layout.fillWidth: true
                                text: qsTr("El audio se reproduce en la computadora y puede incluirse en OBS como audio del sistema.")
                                color: win.muted
                                font.pixelSize: 10
                                wrapMode: Text.Wrap
                            }
                            Switch {
                                id: microphoneAudioSwitch
                                Layout.fillWidth: true
                                Layout.topMargin: 8
                                visible: backend.selected.indexOf("ios:") === 0 ||
                                    (backend.selected.indexOf("android:") === 0 && backend.outputDevice.length > 0)
                                enabled: !backend.running && (backend.selected.indexOf("ios:") === 0 ||
                                    (backend.selected.indexOf("android:") === 0 && backend.outputDevice.length > 0))
                                checked: backend.microphoneEnabled
                                onToggled: backend.microphoneEnabled = checked
                                text: qsTr("Mezclar el micrófono de la computadora")
                                contentItem: Label {
                                    leftPadding: 0
                                    rightPadding: microphoneAudioSwitch.indicator.width + microphoneAudioSwitch.spacing
                                    text: microphoneAudioSwitch.text
                                    color: microphoneAudioSwitch.enabled ? win.text : win.muted
                                    font.pixelSize: 11
                                    verticalAlignment: Text.AlignVCenter
                                    wrapMode: Text.Wrap
                                }
                            }
                            Label {
                                visible: backend.selected.indexOf("ios:") === 0 ||
                                    (backend.selected.indexOf("android:") === 0 && backend.outputDevice.length > 0)
                                Layout.fillWidth: true
                                text: qsTr("Usa el micrófono predeterminado de PipeWire. En Android se mezcla en la transmisión de cámara virtual; el audio del teléfono sigue en la salida del escritorio.")
                                color: win.muted
                                font.pixelSize: 10
                                wrapMode: Text.Wrap
                            }
                            Rectangle {
                                Layout.fillWidth: true
                                Layout.topMargin: 15
                                radius: 8
                                color: "#202a32"
                                border.color: "#374a5a"
                                border.width: 1
                                implicitHeight: 39
                                RowLayout {
                                    anchors.fill: parent
                                    anchors.leftMargin: 10
                                    spacing: 8
                                    Label { text: "●"; color: win.accent; font.pixelSize: 9 }
                                    Label { text: qsTr("Baja latencia · conexión directa"); color: "#d8e8f7"; font.pixelSize: 10 }
                                }
                            }

                            Item { Layout.fillHeight: true; Layout.minimumHeight: 18 }
                        }
                    }
                }
            }

            RowLayout {
                Layout.fillWidth: true
                Layout.preferredHeight: 28
                Layout.leftMargin: 20
                Layout.rightMargin: 24
                Label { text: qsTr("Conexión local · sin intermediarios en la nube"); color: win.muted; font.pixelSize: 10 }
                Item { Layout.fillWidth: true }
                Label { text: "iPhoneMirror · Linux"; color: win.muted; font.pixelSize: 10 }
            }
        }
    }

    Dialog {
        id: deviceDialog
        z: 20
        modal: true
        anchors.centerIn: parent
        width: Math.min(500, win.width - 32)
        title: qsTr("Teléfonos conectados")
        standardButtons: Dialog.Close

        contentItem: ColumnLayout {
            spacing: 10
            Label {
                Layout.fillWidth: true
                text: qsTr("Selecciona un teléfono para prepararlo en el panel principal. Los identificadores técnicos no se muestran.")
                color: win.muted
                wrapMode: Text.Wrap
                font.pixelSize: 12
            }
            Button {
                Layout.alignment: Qt.AlignRight
                text: qsTr("Actualizar lista")
                onClicked: backend.refresh()
            }
            Button {
                Layout.fillWidth: true
                enabled: !backend.running
                text: qsTr("Recibir AirPlay por Wi-Fi")
                onClicked: backend.startAirPlayReceiver()
            }
            Label {
                Layout.fillWidth: true
                text: qsTr("El receptor aparece en la red local como «iPhoneMirror». Si el iPhone no lo encuentra o no conecta, revisa mDNS (UDP 5353) y el firewall; UxPlay usa TCP y UDP 7000–7002. UxPlay abre una ventana separada que puedes capturar en OBS.")
                color: win.muted
                wrapMode: Text.Wrap
                font.pixelSize: 11
            }
            Label {
                visible: backend.devices.length === 0
                Layout.fillWidth: true
                text: qsTr("No se encontraron teléfonos. Conecta un iPhone/iPad por USB o autoriza ADB en Android.")
                color: win.muted
                wrapMode: Text.Wrap
            }
            Repeater {
                model: backend.devices
                delegate: Button {
                    required property var modelData
                    Layout.fillWidth: true
                    text: modelData.label
                    enabled: !backend.running
                    highlighted: backend.selected === modelData.id
                    onClicked: {
                        backend.selected = modelData.id
                        win.activeSection = "devices"
                        deviceDialog.close()
                    }
                }
            }
        }
    }

    Dialog {
        id: settingsDialog
        z: 20
        modal: true
        anchors.centerIn: parent
        width: Math.min(500, win.width - 32)
        title: qsTr("Ajustes de captura")
        standardButtons: Dialog.Close

        contentItem: ColumnLayout {
            spacing: 11
            Label {
                Layout.fillWidth: true
                text: qsTr("Estas opciones se aplican al dispositivo seleccionado. La captura USB y la duplicación activa no se reinician al cambiar preferencias.")
                color: win.muted
                wrapMode: Text.Wrap
                font.pixelSize: 12
            }
            Switch {
                Layout.fillWidth: true
                enabled: !backend.running
                text: qsTr("Reproducir audio del dispositivo")
                checked: backend.audioEnabled
                onToggled: backend.audioEnabled = checked
            }
            Switch {
                visible: backend.selected.indexOf("ios:") === 0
                Layout.fillWidth: true
                enabled: !backend.running
                checked: backend.reverseControlEnabled
                onToggled: backend.reverseControlEnabled = checked
                text: qsTr("Controlar iPhone/iPad por Bluetooth")
            }
            Switch {
                Layout.fillWidth: true
                text: qsTr("API local de automatización · solo lectura")
                checked: backend.automationApiEnabled
                onToggled: backend.automationApiEnabled = checked
            }
            Label {
                visible: backend.automationApiEnabled
                Layout.fillWidth: true
                text: qsTr("Escucha solo en esta computadora: http://127.0.0.1:17890. La clave se solicita en cada consulta y no se transmite por la red local.")
                color: win.muted
                wrapMode: Text.Wrap
                font.pixelSize: 11
            }
            RowLayout {
                visible: backend.automationApiEnabled
                Layout.fillWidth: true
                TextField {
                    Layout.fillWidth: true
                    readOnly: true
                    selectByMouse: true
                    text: backend.automationApiKey
                    font.family: "monospace"
                    Accessible.name: qsTr("Clave de la API local")
                }
                Button {
                    text: qsTr("Copiar clave")
                    onClicked: backend.copyAutomationApiKey()
                }
            }
            Label {
                visible: backend.automationApiEnabled
                Layout.fillWidth: true
                text: qsTr("Rutas disponibles: GET /api/v1/app/status y GET /api/v1/devices; documentación en /api/docs.")
                color: win.muted
                wrapMode: Text.Wrap
                font.pixelSize: 11
            }
            Label {
                visible: backend.selected.indexOf("ios:") === 0
                Layout.fillWidth: true
                text: qsTr("Experimental. Anuncia «iPhoneMirror Linux» como teclado y ratón BLE; empareja el iPhone/iPad desde Ajustes → Bluetooth. Requiere BlueZ y un adaptador BLE. Solo se activa en la siguiente sesión.")
                color: win.warning
                wrapMode: Text.Wrap
                font.pixelSize: 11
            }
            Label { text: qsTr("Perfil de calidad Android"); color: win.text; visible: win.androidSelected }
            ComboBox {
                visible: win.androidSelected
                Layout.fillWidth: true
                enabled: !backend.running
                model: backend.androidQualityProfiles
                textRole: "label"
                valueRole: "id"
                currentIndex: {
                    for (let i = 0; i < backend.androidQualityProfiles.length; ++i)
                        if (backend.androidQualityProfiles[i].id === backend.androidQualityProfile) return i
                    return 1
                }
                onActivated: backend.androidQualityProfile = currentValue
            }
            Label { text: qsTr("Perfil de salida OBS / TikTok"); color: win.text; visible: win.androidSelected || backend.selected.indexOf("ios:") === 0 }
            ComboBox {
                visible: win.androidSelected || backend.selected.indexOf("ios:") === 0
                Layout.fillWidth: true
                enabled: !backend.running
                model: backend.iosOutputProfiles
                textRole: "label"
                valueRole: "id"
                currentIndex: backend.iosOutputProfile === "horizontal" ? 1 : 0
                onActivated: backend.iosOutputProfile = currentValue
            }
            Label { text: qsTr("Modo de compatibilidad USB"); color: win.text; visible: backend.selected.indexOf("ios:") === 0 }
            ComboBox {
                visible: backend.selected.indexOf("ios:") === 0
                Layout.fillWidth: true
                enabled: !backend.running
                model: backend.iosProjectionModes
                textRole: "label"
                valueRole: "id"
                currentIndex: backend.iosProjectionMode === "airplay" ? 1
                    : backend.iosProjectionMode === "aisi" ? 2 : 0
                onActivated: backend.iosProjectionMode = currentValue
            }
            Label {
                Layout.fillWidth: true
                text: qsTr("La salida para OBS requiere una cámara virtual V4L2 cargada. Los modos AirPlay y Aisi son métodos alternativos por USB, no recepción inalámbrica AirPlay.")
                color: win.muted
                wrapMode: Text.Wrap
                font.pixelSize: 11
            }
        }
    }

    Dialog {
        id: recordingDialog
        z: 20
        modal: true
        anchors.centerIn: parent
        width: Math.min(500, win.width - 32)
        title: qsTr("Grabación y transmisión")
        standardButtons: Dialog.Close

        contentItem: ColumnLayout {
            spacing: 11
            Label {
                Layout.fillWidth: true
                text: win.androidSelected
                    ? qsTr("Android puede grabarse y transmitirse a la vez desde el lienzo de la cámara virtual V4L2. Cada salida se detiene por separado; el audio del teléfono y el micrófono se agregan cuando PipeWire está disponible.")
                    : qsTr("La grabación y la transmisión pueden ejecutarse a la vez durante una captura iPhone/iPad. Cada salida se finaliza por separado.")
                color: win.muted
                wrapMode: Text.Wrap
                font.pixelSize: 12
            }
            Switch {
                Layout.fillWidth: true
                enabled: !backend.running && (win.androidSelected || backend.selected.indexOf("ios:") === 0)
                checked: backend.recordingEnabled
                onToggled: backend.recordingEnabled = checked
                text: qsTr("Iniciar grabación automáticamente")
            }
            Label {
                Layout.fillWidth: true
                visible: backend.mediaRecordingActive || backend.mediaStreamingActive
                text: backend.mediaRecordingActive && backend.mediaStreamingActive
                    ? qsTr("Grabando y transmitiendo") : backend.mediaRecordingActive
                    ? qsTr("Grabación activa") : qsTr("Transmisión activa")
                color: win.success
                font.weight: Font.DemiBold
            }
            RowLayout {
                Layout.fillWidth: true
                visible: backend.running && (backend.selected.indexOf("ios:") === 0 ||
                    (backend.selected.indexOf("android:") === 0 && backend.outputDevice.length > 0 &&
                     (backend.androidOutputReady || backend.mediaRecordingActive)))
                Button {
                    Layout.fillWidth: true
                    visible: !backend.mediaRecordingActive
                    text: qsTr("Iniciar grabación")
                    onClicked: backend.startMediaRecording()
                }
                Button {
                    Layout.fillWidth: true
                    visible: backend.mediaRecordingActive
                    text: qsTr("Detener grabación")
                    onClicked: backend.stopMediaOutput("recording")
                }
            }
            RowLayout {
                Layout.fillWidth: true
                visible: backend.running && (backend.selected.indexOf("ios:") === 0 ||
                    (backend.selected.indexOf("android:") === 0 && backend.outputDevice.length > 0 &&
                     (backend.androidOutputReady || backend.mediaStreamingActive)))
                Button {
                    Layout.fillWidth: true
                    visible: !backend.mediaStreamingActive
                    text: qsTr("Configurar transmisión")
                    onClicked: { recordingDialog.close(); streamDialog.open() }
                }
                Button {
                    Layout.fillWidth: true
                    visible: backend.mediaStreamingActive
                    text: qsTr("Detener transmisión")
                    onClicked: backend.stopMediaOutput("streaming")
                }
            }
            Label {
                Layout.fillWidth: true
                text: win.androidSelected
                    ? qsTr("La transmisión de Android requiere una cámara virtual seleccionada. Envía el lienzo vertical u horizontal y puede mezclar el audio del teléfono y el micrófono.")
                    : qsTr("iPhone/iPad guarda MP4 en Imágenes/iPhoneMirror. RTMP/RTMPS, SRT y WHIP dependen de las funciones disponibles en FFmpeg.")
                color: win.muted
                wrapMode: Text.Wrap
                font.pixelSize: 11
            }
        }
    }

    Dialog {
        id: streamDialog
        z: 20
        modal: true
        anchors.centerIn: parent
        width: Math.min(460, win.width - 32)
        title: win.androidSelected ? qsTr("Transmitir Android") : qsTr("Transmitir iPhone/iPad")
        standardButtons: Dialog.Ok | Dialog.Cancel
        onAccepted: {
            const kinds = ["rtmp", "srt", "whip"]
            backend.startStreaming(kinds[streamProtocol.currentIndex],
                                       streamDestination.text, streamAuthorization.text,
                                       androidAudioSwitch.checked)
            streamAuthorization.clear()
        }
        onRejected: streamAuthorization.clear()

        contentItem: ColumnLayout {
            spacing: 10
            Label {
                Layout.fillWidth: true
                text: win.androidSelected
                    ? qsTr("Elige el protocolo y pega la dirección del servidor. El audio de Android se captura aparte del resto del escritorio.")
                    : qsTr("Elige el protocolo y pega la dirección del servidor. Puedes transmitir mientras grabas; cada salida se detiene por separado.")
                color: win.muted
                wrapMode: Text.Wrap
                font.pixelSize: 12
            }
            Switch {
                id: androidAudioSwitch
                Layout.fillWidth: true
                visible: win.androidSelected
                enabled: backend.androidAudioRouteAvailable
                checked: true
                text: qsTr("Incluir audio del teléfono Android")
            }
            Label {
                Layout.fillWidth: true
                visible: win.androidSelected && !backend.androidAudioRouteAvailable
                text: qsTr("PipeWire no pudo preparar la ruta aislada de audio; esta transmisión será solo video o micrófono.")
                color: win.muted
                wrapMode: Text.Wrap
                font.pixelSize: 11
            }
            Label { text: qsTr("Protocolo"); color: win.text; font.pixelSize: 12 }
            ComboBox {
                id: streamProtocol
                Layout.fillWidth: true
                model: ["RTMP / RTMPS", "SRT", "WHIP (WebRTC)"]
            }
            Label { text: qsTr("Dirección de transmisión"); color: win.text; font.pixelSize: 12 }
            TextField {
                id: streamDestination
                Layout.fillWidth: true
                placeholderText: streamProtocol.currentIndex === 0 ? "rtmps://servidor/app/clave"
                    : streamProtocol.currentIndex === 1 ? "srt://servidor:puerto?mode=caller"
                    : "https://servidor/whip/endpoint"
                inputMethodHints: Qt.ImhUrlCharactersOnly
                selectByMouse: true
            }
            Label {
                visible: streamProtocol.currentIndex === 2
                text: qsTr("Token de autorización (opcional)")
                color: win.text
                font.pixelSize: 12
            }
            TextField {
                id: streamAuthorization
                Layout.fillWidth: true
                visible: streamProtocol.currentIndex === 2
                echoMode: TextInput.Password
                selectByMouse: true
                placeholderText: qsTr("Bearer token")
            }
            Label {
                Layout.fillWidth: true
                text: qsTr("No se guardan en las preferencias ni en los registros de iPhoneMirror. FFmpeg necesita la dirección y, si aplica, el token mientras transmite.")
                color: win.muted
                wrapMode: Text.Wrap
                font.pixelSize: 10
            }
        }
    }

    Dialog {
        id: wirelessPairDialog
        z: 20
        modal: true
        anchors.centerIn: parent
        width: Math.min(430, win.width - 32)
        title: qsTr("Conectar Android por Wi-Fi")
        standardButtons: Dialog.Ok | Dialog.Cancel
        onAccepted: backend.pairAndroid(pairAddressField.text,
                                        pairCodeField.text,
                                        connectionAddressField.text)

        contentItem: ColumnLayout {
            spacing: 10
            Label {
                Layout.fillWidth: true
                text: qsTr("En Android abre Opciones de desarrollador → Depuración inalámbrica → Emparejar con código. La computadora y el teléfono deben compartir la misma red.")
                color: win.muted
                wrapMode: Text.Wrap
                font.pixelSize: 12
            }
            Label { text: qsTr("Dirección y puerto de emparejamiento"); color: win.text; font.pixelSize: 12 }
            TextField {
                id: pairAddressField
                Layout.fillWidth: true
                placeholderText: "192.168.1.20:37123"
                inputMethodHints: Qt.ImhUrlCharactersOnly
                selectByMouse: true
            }
            Label { text: qsTr("Código de seis dígitos"); color: win.text; font.pixelSize: 12 }
            TextField {
                id: pairCodeField
                Layout.fillWidth: true
                placeholderText: "123456"
                inputMethodHints: Qt.ImhDigitsOnly
                maximumLength: 6
                echoMode: TextInput.Password
                selectByMouse: true
            }
            Label { text: qsTr("Dirección y puerto de conexión"); color: win.text; font.pixelSize: 12 }
            TextField {
                id: connectionAddressField
                Layout.fillWidth: true
                placeholderText: "192.168.1.20:41235"
                inputMethodHints: Qt.ImhUrlCharactersOnly
                selectByMouse: true
            }
            Label {
                Layout.fillWidth: true
                text: qsTr("El puerto de conexión suele ser distinto al de emparejamiento; aparece en la pantalla principal de Depuración inalámbrica.")
                color: win.muted
                wrapMode: Text.Wrap
                font.pixelSize: 11
            }
        }
    }

    Dialog {
        id: legacyWifiDialog
        z: 20
        modal: true
        anchors.centerIn: parent
        width: Math.min(430, win.width - 32)
        title: qsTr("Conexión Android por USB y Wi-Fi")
        standardButtons: Dialog.Ok | Dialog.Cancel
        onAccepted: backend.connectAndroidLegacy(legacyIpField.text)

        contentItem: ColumnLayout {
            spacing: 10
            Label {
                Layout.fillWidth: true
                text: qsTr("Conecta y autoriza el Android por USB, y mantén ambos equipos en la misma red. La app habilitará ADB TCP/IP en el puerto 5555 y conectará la IP indicada.")
                color: win.muted
                wrapMode: Text.Wrap
                font.pixelSize: 12
            }
            Label { text: qsTr("Dirección IP del teléfono"); color: win.text; font.pixelSize: 12 }
            TextField {
                id: legacyIpField
                Layout.fillWidth: true
                placeholderText: "192.168.1.20"
                inputMethodHints: Qt.ImhUrlCharactersOnly
                selectByMouse: true
            }
            Label {
                Layout.fillWidth: true
                text: qsTr("ADB TCP/IP no cifra el tráfico. Usa esta conexión solo en una red Wi-Fi de confianza y vuelve a USB o desactiva la depuración de red al terminar.")
                color: win.warning
                wrapMode: Text.Wrap
                font.pixelSize: 11
            }
        }
    }

    Dialog {
        id: diagnosticsDialog
        z: 20
        modal: true
        anchors.centerIn: parent
        width: Math.min(580, win.width - 32)
        title: qsTr("Diagnóstico del sistema")
        standardButtons: Dialog.Close

        contentItem: ColumnLayout {
            spacing: 10
            Label {
                Layout.fillWidth: true
                text: qsTr("Informe local para resolver problemas de conexión y salida. No incluye identificadores de dispositivos ni direcciones IP.")
                color: win.muted
                wrapMode: Text.Wrap
                font.pixelSize: 11
            }
            ScrollView {
                Layout.fillWidth: true
                Layout.preferredHeight: Math.min(390, win.height * 0.52)
                clip: true
                TextArea {
                    readOnly: true
                    selectByMouse: true
                    wrapMode: TextEdit.Wrap
                    text: backend.diagnosticsText()
                    color: win.text
                    font.family: "monospace"
                    font.pixelSize: 11
                    background: Rectangle { color: "#1b1b1d"; radius: 8; border.color: win.border }
                }
            }
            Button {
                Layout.alignment: Qt.AlignRight
                text: qsTr("Copiar informe")
                onClicked: backend.copyDiagnostics()
                contentItem: Label {
                    text: qsTr("Copiar informe")
                    color: win.text
                    font.pixelSize: 12
                    horizontalAlignment: Text.AlignHCenter
                    verticalAlignment: Text.AlignVCenter
                }
                background: Rectangle { radius: 8; color: "#38383b" }
            }
        }
    }

    Rectangle {
        anchors.fill: parent
        color: "#c9000000"
        visible: win.helpVisible
        z: 10
        MouseArea { anchors.fill: parent; onClicked: win.helpVisible = false }
        Rectangle {
            anchors.centerIn: parent
            width: Math.min(parent.width - 40, 390)
            height: helpColumn.implicitHeight + 44
            radius: 14
            color: "#292929"
            border.color: "#4a4a4e"
            ColumnLayout {
                id: helpColumn
                anchors.fill: parent
                anchors.margins: 22
                spacing: 12
                Label { text: qsTr("Atajos de teclado"); color: win.text; font.pixelSize: 20; font.weight: Font.DemiBold }
                Label { text: "Space     " + qsTr("Iniciar o detener la duplicación"); color: win.muted; font.pixelSize: 12 }
                Label { text: "S             " + qsTr("Captura del iPhone/iPad desde su vista previa"); color: win.muted; font.pixelSize: 12; wrapMode: Text.Wrap }
                Label { text: "?             " + qsTr("Mostrar u ocultar los atajos"); color: win.muted; font.pixelSize: 12 }
                Label { text: "Q            " + qsTr("Salir de iPhoneMirror"); color: win.muted; font.pixelSize: 12 }
                Button {
                    Layout.alignment: Qt.AlignRight
                    text: qsTr("Cerrar")
                    onClicked: win.helpVisible = false
                    contentItem: Label { text: qsTr("Cerrar"); color: win.text; font.pixelSize: 12; horizontalAlignment: Text.AlignHCenter; verticalAlignment: Text.AlignVCenter }
                    background: Rectangle { radius: 8; color: "#3a3a3d" }
                }
            }
        }
    }
}
