#include "backend.h"

#include <QRegularExpression>
#include <QDateTime>
#include <QCoreApplication>
#include <QElapsedTimer>
#include <QClipboard>
#include <QCryptographicHash>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QJsonDocument>
#include <QJsonArray>
#include <QJsonObject>
#include <QGuiApplication>
#include <QHostAddress>
#include <QStandardPaths>
#include <QUrl>
#include <QSettings>
#include <QRandomGenerator>
#include <QTcpSocket>
#include <QXmlStreamReader>
#include <QSysInfo>
#include <QTemporaryFile>
#include <QThread>
#include <QTimeZone>
#include <QDebug>
#include <algorithm>
#include <cmath>
#include <csignal>
#include <utility>
#include <unistd.h>

namespace {
constexpr auto IosPrefix = "ios:";
constexpr auto AndroidPrefix = "android:";
constexpr auto V4l2LoopbackName = "Dummy video device";

QVariantMap device(QString id, QString label) {
    return {{QStringLiteral("id"), std::move(id)},
            {QStringLiteral("label"), std::move(label)}};
}

bool validAdbEndpoint(const QString &value) {
    const auto endpoint = value.trimmed();
    QString host;
    QString portText;
    if (endpoint.startsWith(QLatin1Char('['))) {
        const auto bracket = endpoint.indexOf(QLatin1Char(']'));
        if (bracket < 2 || bracket + 2 >= endpoint.size() ||
            endpoint.at(bracket + 1) != QLatin1Char(':')) return false;
        host = endpoint.mid(1, bracket - 1);
        portText = endpoint.mid(bracket + 2);
    } else {
        const auto colon = endpoint.lastIndexOf(QLatin1Char(':'));
        if (colon <= 0 || endpoint.left(colon).contains(QLatin1Char(':'))) return false;
        host = endpoint.left(colon);
        portText = endpoint.mid(colon + 1);
    }
    QHostAddress address;
    bool portOk = false;
    const auto port = portText.toUInt(&portOk);
    return address.setAddress(host) && portOk && port > 0 && port <= 65535;
}

int parseBatteryPercent(const QString &output) {
    static const QRegularExpression expression(
        QStringLiteral("(?:^|\\n)BatteryCurrentCapacity:\\s*(\\d{1,3})"));
    const auto match = expression.match(output);
    if (!match.hasMatch()) return -1;
    bool ok = false;
    const int value = match.captured(1).toInt(&ok);
    return ok && value <= 100 ? value : -1;
}

int parseChargingState(const QString &output) {
    static const QRegularExpression expression(
        QStringLiteral("(?:^|\\n)BatteryIsCharging:\\s*(true|false)") ,
        QRegularExpression::CaseInsensitiveOption);
    const auto match = expression.match(output);
    if (!match.hasMatch()) return -1;
    return match.captured(1).compare(QLatin1String("true"), Qt::CaseInsensitive) == 0
        ? 1 : 0;
}

double parseBatteryTemperatureC(const QByteArray &xml) {
    QXmlStreamReader reader(xml);
    bool temperatureKey = false;
    while (!reader.atEnd()) {
        reader.readNext();
        if (!reader.isStartElement()) continue;
        const auto name = reader.name();
        if (name == QLatin1String("key")) {
            temperatureKey = reader.readElementText() == QLatin1String("Temperature");
        } else if (temperatureKey &&
                   (name == QLatin1String("integer") || name == QLatin1String("real"))) {
            bool ok = false;
            const double value = reader.readElementText().toDouble(&ok);
            temperatureKey = false;
            if (ok && value > 0 && value < 10000) return value / 100.0;
        } else if (name == QLatin1String("dict")) {
            // A new dictionary begins a separate scope; do not bind a later
            // numeric value to a Temperature key in a parent dictionary.
            temperatureKey = false;
        }
    }
    return -1.0;
}

int parseAndroidBatteryProperty(const QString &output, const QString &property) {
    const QRegularExpression expression(QStringLiteral("(?:^|\\n)\\s*%1:\\s*(\\d+)\\b")
        .arg(QRegularExpression::escape(property)));
    const auto match = expression.match(output);
    if (!match.hasMatch()) return -1;
    bool ok = false;
    const auto value = match.captured(1).toInt(&ok);
    return ok ? value : -1;
}

int parseAndroidBatteryPercent(const QString &output) {
    const int level = parseAndroidBatteryProperty(output, QStringLiteral("level"));
    const int scale = parseAndroidBatteryProperty(output, QStringLiteral("scale"));
    if (level < 0 || scale <= 0 || level > scale) return -1;
    return std::clamp((level * 100 + scale / 2) / scale, 0, 100);
}

int parseAndroidChargingState(const QString &output) {
    const int status = parseAndroidBatteryProperty(output, QStringLiteral("status"));
    if (status == 2) return 1;
    if (status == 5) return 2;
    return status < 0 ? -1 : 0;
}

double parseAndroidBatteryTemperatureC(const QString &output) {
    static const QRegularExpression expression(
        QStringLiteral("(?:^|\\n)\\s*temperature:\\s*(-?\\d+(?:\\.\\d+)?)\\b"),
        QRegularExpression::CaseInsensitiveOption);
    const auto match = expression.match(output);
    if (!match.hasMatch()) return -1.0;
    bool ok = false;
    const auto raw = match.captured(1);
    const double value = raw.toDouble(&ok);
    if (!ok || !std::isfinite(value)) return -1.0;
    // Android's standard BatteryService reports tenths of a degree Celsius.
    // A few vendor dumps print an already-scaled decimal value instead.
    const double temperatureC = raw.contains(QLatin1Char('.'))
        ? value : value / 10.0;
    return temperatureC > 0.0 && temperatureC <= 100.0
        ? temperatureC : -1.0;
}
}

Backend::Backend(QObject *parent) : QObject(parent) {
    QSettings settings;
    m_audioEnabled = settings.value(QStringLiteral("audioEnabled"), true).toBool();
    m_microphoneEnabled = settings.value(QStringLiteral("microphoneEnabled"), false).toBool();
    m_reverseControlEnabled = settings.value(
        QStringLiteral("reverseControlEnabled"), false).toBool();
    m_automationApiKey = settings.value(QStringLiteral("automationApiKey")).toString();
    if (m_automationApiKey.size() != 64 ||
        !std::all_of(m_automationApiKey.cbegin(), m_automationApiKey.cend(),
                     [](QChar c) { return c.isDigit() ||
                         (c >= QLatin1Char('A') && c <= QLatin1Char('F')) ||
                         (c >= QLatin1Char('a') && c <= QLatin1Char('f')); })) {
        m_automationApiKey.clear();
        auto *random = QRandomGenerator::system();
        for (int i = 0; i < 4; ++i)
            m_automationApiKey += QStringLiteral("%1").arg(random->generate64(), 16, 16, QLatin1Char('0'));
        settings.setValue(QStringLiteral("automationApiKey"), m_automationApiKey);
        settings.sync();
        QFile::setPermissions(settings.fileName(), QFileDevice::ReadOwner | QFileDevice::WriteOwner);
    }
    settings.sync();
    QFile::setPermissions(settings.fileName(), QFileDevice::ReadOwner | QFileDevice::WriteOwner);
    m_automationServer.setMaxPendingConnections(16);
    connect(&m_automationServer, &QTcpServer::newConnection,
            this, &Backend::acceptAutomationConnections);
    m_automationScreenshotTimeout.setSingleShot(true);
    connect(&m_automationScreenshotTimeout, &QTimer::timeout, this, [this] {
        if (m_automationScreenshot.state() != QProcess::NotRunning)
            m_automationScreenshot.kill();
        if (m_automationIosScreenshot.state() != QProcess::NotRunning)
            m_automationIosScreenshot.kill();
        failAutomationScreenshot(504, QByteArrayLiteral("SCREENSHOT_TIMEOUT"));
    });
    m_automationGeometryTimer.setInterval(2000);
    connect(&m_automationGeometryTimer, &QTimer::timeout,
            this, &Backend::refreshAutomationGeometry);
    m_automationGeometryTimeout.setSingleShot(true);
    m_automationGeometryTimeout.setInterval(3000);
    connect(&m_automationGeometryTimeout, &QTimer::timeout, this, [this] {
        if (m_automationGeometryProbe.state() != QProcess::NotRunning)
            m_automationGeometryProbe.kill();
        m_automationGeometry = {};
    });
    connect(&m_automationGeometryProbe, &QProcess::finished, this,
            [this](int code, QProcess::ExitStatus status) {
        m_automationGeometryTimeout.stop();
        if (!m_automationApiEnabled || m_mirror.state() == QProcess::NotRunning || code != 0 ||
            status != QProcess::NormalExit ||
            m_automationGeometrySerial != m_selected.mid(
                static_cast<qsizetype>(QString::fromLatin1(AndroidPrefix).size()))) {
            m_automationGeometry = {};
            return;
        }
        const auto output = QString::fromLocal8Bit(
            m_automationGeometryProbe.readAllStandardOutput());
        static const QRegularExpression arrayFrame(
            QStringLiteral("displayId\\s*=\\s*0[^\\r\\n]*?logicalFrame\\s*=\\s*\\[\\s*(-?\\d+)\\s*,\\s*(-?\\d+)\\s*,\\s*(-?\\d+)\\s*,\\s*(-?\\d+)\\s*\\]"));
        static const QRegularExpression rectFrame(
            QStringLiteral("displayId\\s*=\\s*0[^\\r\\n]*?logicalFrame\\s*=\\s*Rect\\(\\s*(-?\\d+)\\s*,\\s*(-?\\d+)\\s*-\\s*(-?\\d+)\\s*,\\s*(-?\\d+)\\s*\\)"));
        auto match = arrayFrame.match(output);
        if (!match.hasMatch()) match = rectFrame.match(output);
        if (!match.hasMatch()) {
            m_automationGeometry = {};
            return;
        }
        bool ok[4]{};
        const int left = match.captured(1).toInt(&ok[0]);
        const int top = match.captured(2).toInt(&ok[1]);
        const int right = match.captured(3).toInt(&ok[2]);
        const int bottom = match.captured(4).toInt(&ok[3]);
        const int width = right - left;
        const int height = bottom - top;
        if (!ok[0] || !ok[1] || !ok[2] || !ok[3] || width <= 0 || height <= 0 ||
            width > 32768 || height > 32768) {
            m_automationGeometry = {};
            return;
        }
        const auto frame = QStringLiteral("%1,%2,%3,%4")
            .arg(left).arg(top).arg(right).arg(bottom).toUtf8();
        const auto version = QCryptographicHash::hash(frame,
            QCryptographicHash::Sha256).toHex().first(16);
        const QJsonObject newGeometry{{QStringLiteral("left"), left},
            {QStringLiteral("top"), top}, {QStringLiteral("width"), width},
            {QStringLiteral("height"), height},
            {QStringLiteral("version"), QString::fromLatin1(version)},
            {QStringLiteral("coordinateSpace"), QStringLiteral("deviceLogicalPixels")}};
        const auto deviceId = QString::fromLatin1(AndroidPrefix) + m_automationGeometrySerial;
        if (!m_automationGeometry.isEmpty() &&
            m_automationGeometry.value(QStringLiteral("version")) !=
                newGeometry.value(QStringLiteral("version"))) {
            cancelAutomationInput(deviceId);
            m_automationLeases.remove(deviceId);
        }
        m_automationGeometry = newGeometry;
    });
    m_automationGeometryProbe.setStandardErrorFile(QProcess::nullDevice());
    m_automationLeaseTimer.setInterval(250);
    connect(&m_automationLeaseTimer, &QTimer::timeout,
            this, &Backend::expireAutomationLeases);
    connect(&m_automationScreenshot, &QProcess::readyReadStandardOutput, this, [this] {
        if (!m_automationScreenshotPath.isEmpty()) {
            m_automationScreenshot.readAllStandardOutput();
            return;
        }
        m_automationScreenshotBytes += m_automationScreenshot.readAllStandardOutput();
        if (m_automationScreenshotBytes.size() > 32 * 1024 * 1024) {
            m_automationScreenshot.kill();
            failAutomationScreenshot(413, QByteArrayLiteral("SCREENSHOT_TOO_LARGE"));
        }
    });
    connect(&m_automationScreenshot, &QProcess::finished, this,
            [this](int code, QProcess::ExitStatus status) {
        finishAutomationScreenshot(code, status, false);
    });
    connect(&m_automationIosScreenshot, &QProcess::finished, this,
            [this](int code, QProcess::ExitStatus status) {
        finishAutomationScreenshot(code, status, true);
    });
    connect(&m_automationScreenshot, &QProcess::errorOccurred, this,
            [this](QProcess::ProcessError error) {
        if (error == QProcess::FailedToStart)
            failAutomationScreenshot(503, QByteArrayLiteral("SCREENSHOT_UNAVAILABLE"));
    });
    connect(&m_automationIosScreenshot, &QProcess::errorOccurred, this,
            [this](QProcess::ProcessError error) {
        if (error == QProcess::FailedToStart)
            failAutomationScreenshot(503, QByteArrayLiteral("SCREENSHOT_UNAVAILABLE"));
    });
    m_automationApiEnabled = settings.value(QStringLiteral("automationApiEnabled"), false).toBool();
    if (m_automationApiEnabled && !m_automationServer.listen(QHostAddress::LocalHost, 17890)) {
        m_automationApiEnabled = false;
        settings.setValue(QStringLiteral("automationApiEnabled"), false);
        qWarning() << "Automation API could not bind loopback:" << m_automationServer.errorString();
    } else if (m_automationApiEnabled) {
        m_automationGeometryTimer.start();
        m_automationLeaseTimer.start();
        QTimer::singleShot(0, this, &Backend::refreshAutomationGeometry);
    }
    const auto qualityProfile = settings.value(QStringLiteral("androidQualityProfile"),
                                               QStringLiteral("high")).toString();
    static const QStringList allowedQualityProfiles{
        QStringLiteral("original"), QStringLiteral("high"),
        QStringLiteral("balanced"), QStringLiteral("economy")};
    if (allowedQualityProfiles.contains(qualityProfile))
        m_androidQualityProfile = qualityProfile;
    const auto iosProfile = settings.value(QStringLiteral("iosOutputProfile"),
                                           QStringLiteral("vertical")).toString();
    if (iosProfile == QLatin1String("vertical") ||
        iosProfile == QLatin1String("horizontal"))
        m_iosOutputProfile = iosProfile;
    const auto projectionMode = settings.value(QStringLiteral("iosProjectionMode"),
                                               QStringLiteral("demo")).toString();
    if (projectionMode == QLatin1String("demo") ||
        projectionMode == QLatin1String("airplay") ||
        projectionMode == QLatin1String("aisi"))
        m_iosProjectionMode = projectionMode;

    m_mirrorStopTimeout.setSingleShot(true);
    m_mirrorStopTimeout.setInterval(5000);
    connect(&m_mirrorStopTimeout, &QTimer::timeout, this, [this] {
        if (running()) {
            if (!m_recordingPath.isEmpty()) m_recordingStopTimedOut = true;
            m_mirror.kill();
            m_androidPreview.kill();
            m_airplay.kill();
        }
    });
    m_refresh.setInterval(2500);
    connect(&m_refresh, &QTimer::timeout, this, &Backend::refresh);
    m_refresh.start();
    connect(&m_androidAudioLoopback, &QProcess::finished, this,
            [this](int, QProcess::ExitStatus) {
        m_androidAudioSinkName.clear();
        if (m_androidAudioRouteAvailable) {
            m_androidAudioRouteAvailable = false;
            emit androidAudioRouteAvailableChanged();
        }
    });
    m_iosHealthTimer.setInterval(60000);
    connect(&m_iosHealthTimer, &QTimer::timeout, this, &Backend::refreshDeviceHealth);
    m_iosHealthTimer.start();
    m_iosHealthTimeout.setSingleShot(true);
    m_iosHealthTimeout.setInterval(8000);
    connect(&m_iosHealthTimeout, &QTimer::timeout, this, [this] {
        m_iosHealthPhase = -1;
        if (m_iosHealthProbe.state() != QProcess::NotRunning) m_iosHealthProbe.kill();
        updateDeviceHealthText();
    });
    connect(&m_iosHealthProbe, &QProcess::finished, this,
            [this](int code, QProcess::ExitStatus exitStatus) {
        m_iosHealthTimeout.stop();
        if (m_iosHealthPhase < 0 || m_iosHealthSerial.isEmpty()) {
            if (m_selected.startsWith(QLatin1String(IosPrefix)) &&
                m_selected.mid(static_cast<qsizetype>(QString::fromLatin1(IosPrefix).size())) !=
                    m_iosHealthSerial)
                QTimer::singleShot(250, this, &Backend::refreshDeviceHealth);
            return;
        }
        if (m_selected != QLatin1String(IosPrefix) + m_iosHealthSerial) {
            m_iosHealthPhase = -1;
            return;
        }
        const auto output = m_iosHealthProbe.readAllStandardOutput();
        if (code == 0 && exitStatus == QProcess::NormalExit) {
            if (m_iosHealthPhase == 0) {
                const auto text = QString::fromLocal8Bit(output);
                m_iosBatteryPercent = parseBatteryPercent(text);
                m_iosBatteryCharging = parseChargingState(text);
                m_iosHealthPhase = 1;
                m_iosHealthProbe.setProgram(QStandardPaths::findExecutable(
                    QStringLiteral("idevicediagnostics")));
                m_iosHealthProbe.setArguments({QStringLiteral("-u"), m_iosHealthSerial,
                    QStringLiteral("ioreg"), QStringLiteral("IOPower")});
                m_iosHealthTimeout.start();
                m_iosHealthProbe.start();
                return;
            }
            if (m_iosHealthPhase == 1)
                m_iosTemperatureC = parseBatteryTemperatureC(output);
        }
        m_iosHealthPhase = -1;
        updateDeviceHealthText();
    });
    connect(&m_iosHealthProbe, &QProcess::errorOccurred, this,
            [this](QProcess::ProcessError error) {
        if (error != QProcess::FailedToStart || m_iosHealthPhase < 0) return;
        m_iosHealthTimeout.stop();
        if (m_iosHealthPhase == 0) {
            m_iosHealthPhase = 1;
            m_iosHealthProbe.setProgram(QStandardPaths::findExecutable(
                QStringLiteral("idevicediagnostics")));
            m_iosHealthProbe.setArguments({QStringLiteral("-u"), m_iosHealthSerial,
                QStringLiteral("ioreg"), QStringLiteral("IOPower")});
            m_iosHealthTimeout.start();
            m_iosHealthProbe.start();
        } else {
            m_iosHealthPhase = -1;
            updateDeviceHealthText();
        }
    });
    m_iosHealthProbe.setStandardErrorFile(QProcess::nullDevice());
    m_androidHealthTimeout.setSingleShot(true);
    m_androidHealthTimeout.setInterval(8000);
    connect(&m_androidHealthTimeout, &QTimer::timeout, this, [this] {
        m_androidHealthPhase = -1;
        if (m_androidHealthProbe.state() != QProcess::NotRunning)
            m_androidHealthProbe.kill();
        updateDeviceHealthText();
    });
    connect(&m_androidHealthProbe, &QProcess::finished, this,
            [this](int code, QProcess::ExitStatus exitStatus) {
        m_androidHealthTimeout.stop();
        if (m_androidHealthPhase < 0 || m_androidHealthSerial.isEmpty()) {
            if (m_selected.startsWith(QLatin1String(AndroidPrefix)) &&
                m_selected.mid(static_cast<qsizetype>(
                    QString::fromLatin1(AndroidPrefix).size())) != m_androidHealthSerial)
                QTimer::singleShot(250, this, &Backend::refreshDeviceHealth);
            return;
        }
        if (m_selected != QLatin1String(AndroidPrefix) + m_androidHealthSerial) {
            m_androidHealthPhase = -1;
            return;
        }
        if (code == 0 && exitStatus == QProcess::NormalExit) {
            const auto output = QString::fromLocal8Bit(
                m_androidHealthProbe.readAllStandardOutput());
            m_androidBatteryPercent = parseAndroidBatteryPercent(output);
            m_androidBatteryCharging = parseAndroidChargingState(output);
            m_androidTemperatureC = parseAndroidBatteryTemperatureC(output);
        } else {
            m_androidBatteryPercent = -1;
            m_androidBatteryCharging = -1;
            m_androidTemperatureC = -1.0;
        }
        m_androidHealthPhase = -1;
        updateDeviceHealthText();
    });
    connect(&m_androidHealthProbe, &QProcess::errorOccurred, this,
            [this](QProcess::ProcessError error) {
        if (error != QProcess::FailedToStart || m_androidHealthPhase < 0) return;
        m_androidHealthTimeout.stop();
        m_androidHealthPhase = -1;
        updateDeviceHealthText();
    });
    m_androidHealthProbe.setStandardErrorFile(QProcess::nullDevice());
    connect(&m_mirror, &QProcess::started, this, [this] {
        m_androidPreviewClosing = false;
        setStatus(tr("Duplicación activa"));
        emit runningChanged();
        if (!m_selected.startsWith(QLatin1String(AndroidPrefix)) ||
            m_outputDevice.isEmpty()) return;
        const auto serial = m_selected.mid(static_cast<qsizetype>(
            QString::fromLatin1(AndroidPrefix).size()));
        const auto device = m_outputDevice;
        const auto profile = m_iosOutputProfile;
        QTimer::singleShot(1000, this, [this, serial, device, profile] {
            if (m_mirror.state() == QProcess::NotRunning ||
                m_selected != QLatin1String(AndroidPrefix) + serial ||
                m_outputDevice != device ||
                m_androidPreview.state() != QProcess::NotRunning) return;
            m_androidPreviewStderrBuffer.clear();
            setAndroidOutputReady(false);
            const auto shell = QStringLiteral(
                "/usr/lib/iphoneusb/iPhoneMirror.LinuxShell.dll");
            m_androidPreview.setProgram(QStandardPaths::findExecutable(
                QStringLiteral("dotnet")));
            m_androidPreview.setArguments({shell,
                QStringLiteral("--v4l2-preview"), device, profile, serial});
            auto environment = QProcessEnvironment::systemEnvironment();
            if (m_androidAudioRouteAvailable)
                environment.insert(QStringLiteral("IPHONE_MIRROR_ANDROID_AUDIO_SINK"),
                                   m_androidAudioSinkName);
            m_androidPreview.setProcessEnvironment(environment);
            m_androidPreview.setStandardOutputFile(QProcess::nullDevice());
            m_androidPreview.start();
        });
    });
    connect(&m_androidPreview, &QProcess::readyReadStandardError, this, [this] {
        processMirrorStderr(m_androidPreview.readAllStandardError(), false,
                            &m_androidPreviewStderrBuffer);
    });
    connect(&m_androidPreview, &QProcess::errorOccurred, this,
            [this](QProcess::ProcessError error) {
        if (error == QProcess::FailedToStart &&
            m_mirror.state() != QProcess::NotRunning)
            setStatus(tr("Android transmite a OBS; no se pudo abrir su vista previa integrada"));
    });
    connect(&m_androidPreview, &QProcess::finished, this,
            [this](int code, QProcess::ExitStatus exitStatus) {
        processMirrorStderr(m_androidPreview.readAllStandardError(), true,
                            &m_androidPreviewStderrBuffer);
        if (m_mediaStreamingActive) {
            m_mediaStreamingActive = false;
            emit mediaStreamingActiveChanged();
        }
        if (m_mediaRecordingActive) {
            m_mediaRecordingActive = false;
            emit mediaRecordingActiveChanged();
        }
        if (m_manualAndroidRecording) {
            if (QFileInfo(m_recordingPath).size() > 1024) {
                m_lastRecordingPath = m_recordingPath;
                setStatus(tr("Grabación guardada: %1").arg(m_recordingPath));
            } else {
                QFile::remove(m_recordingPath);
                setStatus(tr("No se pudo finalizar la grabación Android"));
            }
            m_recordingPath.clear();
            m_manualAndroidRecording = false;
        }
        m_mediaStreamingRequested = false;
        m_androidPreviewClosing = false;
        setAndroidOutputReady(false);
        emit runningChanged();
        if (m_mirror.state() != QProcess::NotRunning &&
            (code != 0 || exitStatus == QProcess::CrashExit))
            setStatus(tr("Android continúa enviando video a OBS; falló la vista previa integrada"));
        else if (m_mirror.state() == QProcess::NotRunning)
            stopAndroidAudioRoute();
    });
    connect(&m_mirror, &QProcess::readyReadStandardError, this, [this] {
        processMirrorStderr(m_mirror.readAllStandardError());
    });
    connect(&m_mirror, &QProcess::errorOccurred, this,
            [this](QProcess::ProcessError error) {
        qWarning().noquote() << "No se pudo iniciar la captura:" << m_mirror.errorString();
        if (error == QProcess::FailedToStart) {
            setCaptureFps(-1.0);
            setStatus(tr("No se pudo iniciar el motor de duplicación"));
            m_recordingPath.clear();
            stopAndroidAudioRoute();
        }
        emit runningChanged();
    });
    connect(&m_mirror, &QProcess::finished, this,
            [this](int code, QProcess::ExitStatus status) {
        setCaptureFps(-1.0);
        for (const auto &deviceId : m_automationInputInFlight)
            cancelAutomationInput(deviceId);
        m_automationLeases.clear();
        m_automationGeometry = {};
        const bool androidPreviewIsClosing =
            m_androidPreview.state() != QProcess::NotRunning;
        if (!androidPreviewIsClosing) stopAndroidAudioRoute();
        m_androidPreviewClosing = androidPreviewIsClosing;
        if (androidPreviewIsClosing) {
            setAndroidOutputReady(false);
            m_androidPreview.terminate();
            QTimer::singleShot(18000, this, [this] {
                if (m_androidPreview.state() != QProcess::NotRunning)
                    m_androidPreview.kill();
            });
        }
        m_mirrorStopTimeout.stop();
        emit runningChanged();
        const bool deferAndroidRecording = androidPreviewIsClosing &&
            m_manualAndroidRecording;
        if (m_mediaRecordingActive && !deferAndroidRecording) {
            m_mediaRecordingActive = false;
            emit mediaRecordingActiveChanged();
        }
        if (m_mediaStreamingActive && !androidPreviewIsClosing) {
            m_mediaStreamingActive = false;
            emit mediaStreamingActiveChanged();
        }
        if (!androidPreviewIsClosing) m_mediaStreamingRequested = false;
        processMirrorStderr(m_mirror.readAllStandardError(), true);
        const auto error = QString{};
        const auto recordingPath = deferAndroidRecording ? QString{} : m_recordingPath;
        if (!deferAndroidRecording) m_recordingPath.clear();
        const bool recordingWasStoppedCleanly = m_recordingStopRequested &&
            !m_recordingStopTimedOut && !recordingPath.isEmpty() &&
            QFileInfo(recordingPath).size() > 1024;
        if (!deferAndroidRecording) {
            m_recordingStopRequested = false;
            m_recordingStopTimedOut = false;
        }
        if (recordingWasStoppedCleanly) {
            m_lastRecordingPath = recordingPath;
            setStatus(tr("Grabación guardada: %1").arg(recordingPath));
            qInfo().noquote() << "Grabación guardada:" << recordingPath;
        } else if (m_outputFailed && code == 0 && status == QProcess::NormalExit) {
            setStatus(tr("Duplicación detenida; la cámara virtual no estuvo disponible"));
        } else if (code != 0 || status == QProcess::CrashExit) {
            if (!error.isEmpty()) qWarning().noquote() << "Motor de duplicación:" << error;
            setStatus(recordingPath.isEmpty()
                ? tr("La duplicación se detuvo; revisa la conexión del dispositivo")
                : tr("La sesión terminó con error; la grabación puede estar incompleta: %1")
                    .arg(recordingPath));
        } else if (!recordingPath.isEmpty()) {
            if (QFileInfo(recordingPath).size() > 1024) {
                m_lastRecordingPath = recordingPath;
                setStatus(tr("Grabación guardada: %1").arg(recordingPath));
                qInfo().noquote() << "Grabación guardada:" << recordingPath;
            } else {
                setStatus(tr("No se pudo finalizar la grabación de Android"));
                QFile::remove(recordingPath);
            }
        } else if (!m_lastRecordingPath.isEmpty() && code == 0 &&
                   status == QProcess::NormalExit) {
            setStatus(tr("Grabación guardada: %1").arg(m_lastRecordingPath));
        } else {
            setStatus(tr("Duplicación detenida"));
        }
        if (androidPreviewIsClosing)
            setStatus(m_mediaRecordingActive
                ? tr("Finalizando la grabación de Android…")
                : m_mediaStreamingActive
                    ? tr("Finalizando la transmisión de Android…")
                    : tr("Cerrando la vista previa Android…"));
    });
    m_airplay.setProcessChannelMode(QProcess::MergedChannels);
    connect(&m_airplay, &QProcess::started, this, [this] {
        setStatus(tr("Receptor AirPlay listo; conecta desde el Centro de control del iPhone o iPad"));
        emit runningChanged();
    });
    connect(&m_airplay, &QProcess::readyReadStandardOutput, this, [this] {
        const auto output = QString::fromLocal8Bit(m_airplay.readAllStandardOutput()).trimmed();
        if (!output.isEmpty()) qInfo().noquote() << "UxPlay:" << output;
    });
    connect(&m_airplay, &QProcess::errorOccurred, this,
            [this](QProcess::ProcessError error) {
        if (error == QProcess::FailedToStart) {
            setStatus(tr("No se pudo iniciar el receptor AirPlay; revisa UxPlay y GStreamer"));
            emit runningChanged();
        }
    });
    connect(&m_airplay, &QProcess::finished, this,
            [this](int code, QProcess::ExitStatus exitStatus) {
        m_mirrorStopTimeout.stop();
        setStatus(code == 0 && exitStatus == QProcess::NormalExit
            ? tr("Receptor AirPlay detenido")
            : tr("El receptor AirPlay se cerró; revisa sus dependencias"));
        emit runningChanged();
    });
    connect(&m_androidScreenshot, &QProcess::finished, this,
            [this](int code, QProcess::ExitStatus exitStatus) {
        const auto path = m_androidScreenshotPath;
        const auto error = QString::fromLocal8Bit(
            m_androidScreenshot.readAllStandardError()).trimmed();
        m_androidScreenshotPath.clear();
        if (code == 0 && exitStatus == QProcess::NormalExit &&
            QFileInfo(path).size() > 24) {
            setStatus(tr("Captura guardada: %1").arg(path));
            qInfo().noquote() << "Captura Android guardada:" << path;
        } else {
            QFile::remove(path);
            if (!error.isEmpty())
                qWarning().noquote() << "Falló la captura Android:" << error;
            setStatus(tr("No se pudo guardar la captura de Android"));
        }
    });
    connect(&m_androidScreenshot, &QProcess::errorOccurred, this,
            [this](QProcess::ProcessError processError) {
        if (processError != QProcess::FailedToStart) return;
        qWarning().noquote() << "No se pudo iniciar ADB para capturar:"
                             << m_androidScreenshot.errorString();
        QFile::remove(m_androidScreenshotPath);
        m_androidScreenshotPath.clear();
        setStatus(tr("No se pudo iniciar ADB para guardar la captura"));
    });
    m_androidWireless.setProcessChannelMode(QProcess::MergedChannels);
    m_androidWirelessTimeout.setSingleShot(true);
    m_androidWirelessTimeout.setInterval(20000);
    connect(&m_androidWirelessTimeout, &QTimer::timeout, this, [this] {
        if (m_androidWirelessPhase == 0 ||
            m_androidWireless.state() == QProcess::NotRunning) return;
        m_androidWirelessTimedOut = true;
        m_androidWireless.kill();
    });
    connect(&m_androidWireless, &QProcess::started, this, [this] {
        if (m_androidWirelessPhase == 1) {
            m_androidWireless.write((m_androidPairCode + QLatin1Char('\n')).toUtf8());
            m_androidWireless.closeWriteChannel();
        }
    });
    connect(&m_androidWireless, &QProcess::finished, this,
            [this](int code, QProcess::ExitStatus exitStatus) {
        m_androidWirelessTimeout.stop();
        const auto output = QString::fromLocal8Bit(m_androidWireless.readAll()).trimmed();
        if (m_androidWirelessTimedOut) {
            m_androidWirelessPhase = 0;
            m_androidWirelessTimedOut = false;
            m_androidPairCode.clear();
            setStatus(tr("ADB tardó demasiado; revisa la dirección y vuelve a intentar"));
            return;
        }
        const bool success = code == 0 && exitStatus == QProcess::NormalExit;
        if (m_androidWirelessPhase == 1 && success) {
            m_androidWirelessPhase = 2;
            const auto adb = QStandardPaths::findExecutable(QStringLiteral("adb"));
            m_androidWireless.setProgram(adb);
            m_androidWireless.setArguments({QStringLiteral("connect"),
                                             m_androidConnectionAddress});
            m_androidWireless.start();
            m_androidWirelessTimeout.start();
            setStatus(tr("Emparejado; conectando Android por Wi-Fi…"));
            return;
        }
        if (m_androidWirelessPhase == 4 && success) {
            m_androidWirelessPhase = 3;
            const auto adb = QStandardPaths::findExecutable(QStringLiteral("adb"));
            m_androidWireless.setProgram(adb);
            m_androidWireless.setArguments({QStringLiteral("connect"),
                                             m_androidConnectionAddress});
            m_androidWireless.start();
            m_androidWirelessTimeout.start();
            setStatus(tr("ADB TCP/IP activado; conectando Android…"));
            return;
        }
        const bool connected = (m_androidWirelessPhase == 2 ||
                                m_androidWirelessPhase == 3) && success &&
            (output.contains(QStringLiteral("connected to"), Qt::CaseInsensitive) ||
             output.contains(QStringLiteral("already connected"), Qt::CaseInsensitive));
        const bool connectionPhase = m_androidWirelessPhase == 2 ||
                                     m_androidWirelessPhase == 3;
        const bool legacyEnablePhase = m_androidWirelessPhase == 4;
        m_androidWirelessPhase = 0;
        m_androidPairCode.clear();
        if (connected) {
            setStatus(tr("Android conectado por Wi-Fi"));
            refresh();
        } else {
            setStatus(connectionPhase
                ? tr("No se pudo conectar Android por Wi-Fi; revisa el puerto de conexión")
                : (legacyEnablePhase
                    ? tr("No se pudo activar ADB TCP/IP; conecta el teléfono por USB y autoriza esta computadora")
                    : tr("No se pudo emparejar Android; revisa el código y el puerto de emparejamiento")));
        }
    });
    connect(&m_androidWireless, &QProcess::errorOccurred, this,
            [this](QProcess::ProcessError error) {
        if (error != QProcess::FailedToStart || m_androidWirelessPhase == 0) return;
        m_androidWirelessTimeout.stop();
        m_androidWirelessPhase = 0;
        m_androidPairCode.clear();
        setStatus(tr("No se pudo iniciar ADB para la conexión inalámbrica"));
    });
    m_probeTimeout.setSingleShot(true);
    m_probeTimeout.setInterval(2500);
    connect(&m_probeTimeout, &QTimer::timeout, this, [this] {
        if (!m_refreshActive) return;
        m_probeTimedOut = true;
        if (m_iosProbe.state() != QProcess::NotRunning) m_iosProbe.kill();
        if (m_androidProbe.state() != QProcess::NotRunning) m_androidProbe.kill();
        finishRefreshIfReady();
    });
    const auto onProbeFinished = [this] { finishRefreshIfReady(); };
    connect(&m_iosProbe, &QProcess::finished, this,
            [onProbeFinished](int, QProcess::ExitStatus) { onProbeFinished(); });
    connect(&m_androidProbe, &QProcess::finished, this,
            [onProbeFinished](int, QProcess::ExitStatus) { onProbeFinished(); });
    const auto onProbeError = [this](QProcess::ProcessError) {
        QTimer::singleShot(0, this, [this] { finishRefreshIfReady(); });
    };
    connect(&m_iosProbe, &QProcess::errorOccurred, this, onProbeError);
    connect(&m_androidProbe, &QProcess::errorOccurred, this, onProbeError);
    refreshOutputDevices();
    refresh();
}

Backend::~Backend() {
    m_automationServer.close();
    const auto inputProcesses = m_automationInputProcesses.values();
    for (auto *process : inputProcesses) {
        if (process->state() == QProcess::NotRunning) continue;
        process->kill();
        process->waitForFinished(1000);
    }
    m_automationGeometryTimer.stop();
    m_automationGeometryTimeout.stop();
    if (m_automationGeometryProbe.state() != QProcess::NotRunning) {
        m_automationGeometryProbe.kill();
        m_automationGeometryProbe.waitForFinished(1000);
    }
    const auto apiSockets = m_automationSockets.values();
    for (auto *socket : apiSockets) socket->abort();
    if (m_automationScreenshot.state() != QProcess::NotRunning) {
        m_automationScreenshot.kill();
        m_automationScreenshot.waitForFinished(1000);
    }
    if (m_automationIosScreenshot.state() != QProcess::NotRunning) {
        m_automationIosScreenshot.kill();
        m_automationIosScreenshot.waitForFinished(1000);
    }
    if (!m_automationScreenshotPath.isEmpty())
        QFile::remove(m_automationScreenshotPath);
    if (!running()) return;
    stop();
    const int timeout = m_airplay.state() != QProcess::NotRunning ? 5000 : 125000;
    if (m_mirror.state() != QProcess::NotRunning &&
        !m_mirror.waitForFinished(timeout)) {
        qWarning() << "El motor no cerró a tiempo; se forzará su salida.";
        m_mirror.kill();
        m_mirror.waitForFinished(5000);
    }
    if (m_airplay.state() != QProcess::NotRunning &&
        !m_airplay.waitForFinished(5000)) {
        m_airplay.kill();
        m_airplay.waitForFinished(2000);
    }
}

void Backend::handleMediaOutputEvent(const QByteArray &line) {
    QJsonParseError error;
    const auto document = QJsonDocument::fromJson(line, &error);
    if (error.error != QJsonParseError::NoError || !document.isObject()) return;
    const auto object = document.object();
    const auto output = object.value(QStringLiteral("output")).toString();
    const auto state = object.value(QStringLiteral("state")).toString();
    const auto message = object.value(QStringLiteral("message")).toString();
    auto update = [&](bool &property, void (Backend::*signal)(), bool value) {
        if (property == value) return;
        property = value;
        emit (this->*signal)();
    };
    const bool active = state == QLatin1String("active");
    if (output == QLatin1String("recording"))
        update(m_mediaRecordingActive, &Backend::mediaRecordingActiveChanged, active);
    else if (output == QLatin1String("streaming"))
        update(m_mediaStreamingActive, &Backend::mediaStreamingActiveChanged, active);
    else return;
    if (output == QLatin1String("recording") && state == QLatin1String("stopped") &&
        !m_recordingPath.isEmpty()) {
        if (QFileInfo(m_recordingPath).size() > 1024) {
            m_lastRecordingPath = m_recordingPath;
            setStatus(tr("Grabación guardada: %1").arg(m_recordingPath));
            const bool pending = m_mediaStreamingActive;
            if (m_recordingReviewPending != pending) {
                m_recordingReviewPending = pending;
                emit recordingReviewPendingChanged();
            }
        } else {
            setStatus(tr("No se pudo finalizar la grabación"));
            QFile::remove(m_recordingPath);
        }
        m_recordingPath.clear();
        m_manualAndroidRecording = false;
    }
    if (output == QLatin1String("recording") && state == QLatin1String("error")) {
        QFile::remove(m_recordingPath);
        m_recordingPath.clear();
        m_manualAndroidRecording = false;
    }
    if (output == QLatin1String("streaming") &&
        (state == QLatin1String("stopped") || state == QLatin1String("error")))
        m_mediaStreamingRequested = false;
    if (!message.isEmpty() && state == QLatin1String("error"))
        setStatus(message);
    else if (!message.isEmpty() && state == QLatin1String("stopped") &&
             output == QLatin1String("streaming"))
        setStatus(message);
}

void Backend::processMirrorStderr(const QByteArray &data, bool flush,
                                  QByteArray *buffer) {
    auto &pending = buffer ? *buffer : m_mirrorStderrBuffer;
    pending.append(data);
    while (true) {
        const auto newline = pending.indexOf('\n');
        if (newline < 0 && !flush && pending.size() <= 65536) break;
        const auto line = (newline < 0 ? pending
                                      : pending.left(newline)).trimmed();
        if (newline < 0 || newline + 1 >= pending.size())
            pending.clear();
        else
            pending.remove(0, newline + 1);
        if (line.startsWith("MEDIA_OUTPUT_EVENT:")) {
            handleMediaOutputEvent(line.mid(sizeof("MEDIA_OUTPUT_EVENT:") - 1));
            if (pending.isEmpty()) break;
            continue;
        }
        if (line.isEmpty()) {
            if (pending.isEmpty()) break;
            continue;
        }
        if (line.startsWith("PREVIEW_FPS:")) {
            bool ok = false;
            const double fps = QString::fromLatin1(line.mid(
                sizeof("PREVIEW_FPS:") - 1)).toDouble(&ok);
            if (ok && std::isfinite(fps) && fps >= 0.0 && fps <= 120.0)
                setCaptureFps(fps);
            if (pending.isEmpty()) break;
            continue;
        }
        const auto diagnostic = QString::fromLocal8Bit(line);
        if (diagnostic == QLatin1String("ANDROID_V4L2_PREVIEW_READY")) {
            setAndroidOutputReady(true);
        } else if (diagnostic == QLatin1String("REVERSE_CONTROL_STATUS:connected")) {
            setReverseControlConnected(true);
        } else if (diagnostic == QLatin1String("REVERSE_CONTROL_STATUS:disconnected")) {
            setReverseControlConnected(false);
        } else if (diagnostic == QLatin1String("REVERSE_CONTROL_STATUS:failed")) {
            setReverseControlConnected(false);
            setStatus(tr("Duplicación activa · BlueZ no pudo iniciar el control Bluetooth; revisa el diagnóstico"));
        } else if (diagnostic == QLatin1String("ANDROID_V4L2_PREVIEW_RECONNECTING")) {
            setAndroidOutputReady(false);
            setStatus(tr("Reconectando la vista previa Android después de un cambio de video"));
        } else if (diagnostic == QLatin1String("REVERSE_CONTROL_STATUS:ready")) {
            setStatus(tr("Duplicación activa · control Bluetooth listo para emparejar"));
        } else if (diagnostic.contains(QStringLiteral("V4L2 output failed:"),
                                Qt::CaseInsensitive)) {
            m_outputFailed = true;
            qWarning().noquote() << diagnostic;
            setStatus(tr("La cámara virtual no está disponible; la duplicación local sigue activa"));
        } else {
            qWarning().noquote() << "Motor de duplicación:" << diagnostic;
        }
        if (pending.isEmpty()) break;
    }
}

void Backend::setReverseControlConnected(bool connected) {
    if (m_reverseControlConnected == connected) return;
    m_reverseControlConnected = connected;
    emit reverseControlConnectedChanged();
}

void Backend::setAutomationApiEnabled(bool enabled) {
    if (enabled == m_automationApiEnabled) return;
    if (enabled) {
        if (!m_automationServer.listen(QHostAddress::LocalHost, 17890)) {
            setStatus(tr("No se pudo iniciar la API local; el puerto 17890 puede estar ocupado"));
            return;
        }
    } else {
        m_automationServer.close();
        m_automationGeometryTimer.stop();
        m_automationGeometryTimeout.stop();
        m_automationGeometry = {};
        m_automationGeometrySerial.clear();
        m_automationLeases.clear();
        m_automationLeaseTimer.stop();
        for (const auto &deviceId : m_automationInputInFlight)
            cancelAutomationInput(deviceId);
        if (m_automationGeometryProbe.state() != QProcess::NotRunning)
            m_automationGeometryProbe.kill();
        const auto activeSockets = m_automationSockets.values();
        for (auto *socket : activeSockets) socket->abort();
    }
    m_automationApiEnabled = enabled;
    if (enabled) {
        m_automationGeometryTimer.start();
        m_automationLeaseTimer.start();
        QTimer::singleShot(0, this, &Backend::refreshAutomationGeometry);
    }
    QSettings().setValue(QStringLiteral("automationApiEnabled"), enabled);
    emit automationApiEnabledChanged();
}

void Backend::refreshAutomationGeometry() {
    if (!m_automationApiEnabled || !m_selected.startsWith(QLatin1String(AndroidPrefix)) ||
        m_mirror.state() == QProcess::NotRunning) {
        m_automationGeometry = {};
        m_automationGeometrySerial.clear();
        return;
    }
    const auto serial = m_selected.mid(static_cast<qsizetype>(
        QString::fromLatin1(AndroidPrefix).size()));
    if (serial != m_automationGeometrySerial) {
        m_automationGeometry = {};
        m_automationGeometrySerial = serial;
    }
    if (m_automationGeometryProbe.state() != QProcess::NotRunning) return;
    const auto adb = QStandardPaths::findExecutable(QStringLiteral("adb"));
    if (adb.isEmpty()) {
        m_automationGeometry = {};
        return;
    }
    m_automationGeometryProbe.setProgram(adb);
    m_automationGeometryProbe.setArguments({QStringLiteral("-s"), serial,
        QStringLiteral("shell"), QStringLiteral("dumpsys"), QStringLiteral("input")});
    m_automationGeometryTimeout.start(3000);
    m_automationGeometryProbe.start();
}

void Backend::cancelAutomationInput(const QString &deviceId) {
    for (auto it = m_automationInputDevices.cbegin();
         it != m_automationInputDevices.cend(); ++it) {
        if (it.value() != deviceId) continue;
        auto *process = m_automationInputProcesses.value(it.key());
        if (process && process->state() != QProcess::NotRunning)
            process->kill();
    }
}

void Backend::expireAutomationLeases() {
    const auto now = QDateTime::currentMSecsSinceEpoch();
    QStringList expired;
    for (auto it = m_automationLeases.cbegin(); it != m_automationLeases.cend(); ++it)
        if (it->expiresAtMs <= now) expired.push_back(it.key());
    for (const auto &deviceId : expired) {
        m_automationLeases.remove(deviceId);
        cancelAutomationInput(deviceId);
    }
}

void Backend::startAutomationInput(QTcpSocket *socket, const QString &deviceId,
                                   const QString &program, const QStringList &arguments,
                                   int timeoutMs) {
    const QPointer<QTcpSocket> client(socket);
    auto respond = [client](int status, const QByteArray &reason, const QByteArray &code) {
        if (!client || client->state() == QAbstractSocket::UnconnectedState) return;
        const auto bodyObject = status == 202
            ? QJsonObject{{QStringLiteral("status"), QStringLiteral("dispatched")}}
            : QJsonObject{{QStringLiteral("error"), QJsonObject{
                {QStringLiteral("code"), QString::fromLatin1(code)},
                {QStringLiteral("message"), code == "CONTROL_LOCKED"
                    ? QStringLiteral("Ya hay una acción activa para este dispositivo.")
                    : status == 504
                        ? QStringLiteral("ADB excedió el tiempo permitido para la acción.")
                        : QStringLiteral("ADB no pudo completar la acción solicitada.")}}}};
        const auto body = QJsonDocument(bodyObject).toJson(QJsonDocument::Compact);
        client->write("HTTP/1.1 " + QByteArray::number(status) + " " + reason +
            "\r\nContent-Type: application/json\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: " +
            QByteArray::number(body.size()) + "\r\n\r\n" + body);
        client->disconnectFromHost();
    };
    if (m_automationInputInFlight.contains(deviceId)) {
        respond(409, QByteArrayLiteral("Conflict"), QByteArrayLiteral("CONTROL_LOCKED"));
        return;
    }
    auto *process = new QProcess(this);
    process->setProgram(program);
    process->setArguments(arguments);
    process->setStandardOutputFile(QProcess::nullDevice());
    process->setStandardErrorFile(QProcess::nullDevice());
    m_automationInputProcesses.insert(socket, process);
    m_automationInputDevices.insert(socket, deviceId);
    m_automationInputInFlight.insert(deviceId);
    const auto complete = [this, socket = QPointer<QTcpSocket>(socket), process,
                           deviceId, respond](bool succeeded, bool timedOut) {
        if (process->property("automationCompleted").toBool()) return;
        process->setProperty("automationCompleted", true);
        m_automationInputProcesses.remove(socket.data());
        m_automationInputDevices.remove(socket.data());
        m_automationInputInFlight.remove(deviceId);
        if (succeeded)
            respond(202, QByteArrayLiteral("Accepted"), QByteArrayLiteral("INPUT_DISPATCHED"));
        else
            respond(timedOut ? 504 : 503,
                timedOut ? QByteArrayLiteral("Gateway Timeout")
                         : QByteArrayLiteral("Service Unavailable"),
                timedOut ? QByteArrayLiteral("INPUT_TIMEOUT")
                         : QByteArrayLiteral("INPUT_FAILED"));
        process->deleteLater();
    };
    connect(process, &QProcess::finished, this,
            [process, complete](int exitCode, QProcess::ExitStatus status) {
        complete(exitCode == 0 && status == QProcess::NormalExit,
                 process->property("automationTimedOut").toBool());
    });
    connect(process, &QProcess::errorOccurred, this,
            [complete](QProcess::ProcessError error) {
        if (error == QProcess::FailedToStart) complete(false, false);
    });
    QTimer::singleShot(timeoutMs, process, [process] {
        if (process->state() == QProcess::NotRunning) return;
        process->setProperty("automationTimedOut", true);
        process->kill();
    });
    process->start();
}

void Backend::copyAutomationApiKey() const {
    if (auto *clipboard = QGuiApplication::clipboard())
        clipboard->setText(m_automationApiKey);
}

void Backend::acceptAutomationConnections() {
    while (auto *socket = m_automationServer.nextPendingConnection()) {
        if (m_automationSockets.size() >= 32) {
            socket->write("HTTP/1.1 503 Service Unavailable\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
            socket->disconnectFromHost();
            socket->deleteLater();
            continue;
        }
        m_automationSockets.insert(socket);
        socket->setProperty("automationRequest", QByteArray{});
        auto *requestTimeout = new QTimer(socket);
        requestTimeout->setSingleShot(true);
        connect(requestTimeout, &QTimer::timeout, socket, [socket] {
            if (socket->state() != QAbstractSocket::UnconnectedState) socket->disconnectFromHost();
        });
        requestTimeout->start(5000);
        connect(socket, &QTcpSocket::readyRead, this, [this, socket, requestTimeout] {
            if (socket->property("automationRequestHandled").toBool()) return;
            auto request = socket->property("automationRequest").toByteArray();
            request += socket->readAll();
            if (request.size() > 16384) {
                socket->write("HTTP/1.1 413 Payload Too Large\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
                socket->disconnectFromHost();
                return;
            }
            socket->setProperty("automationRequest", request);
            const auto headerEnd = request.indexOf("\r\n\r\n");
            if (headerEnd < 0) return;
            qsizetype bodyLength = 0;
            bool contentLengthSeen = false;
            bool invalidLength = false;
            const auto headerLines = request.left(headerEnd).split('\n');
            for (const auto &rawLine : headerLines.mid(1)) {
                const auto line = rawLine.trimmed();
                const auto separator = line.indexOf(':');
                if (separator <= 0) continue;
                if (line.left(separator).trimmed().toLower() != "content-length") continue;
                if (contentLengthSeen) { invalidLength = true; break; }
                contentLengthSeen = true;
                bool ok = false;
                const auto parsed = line.mid(separator + 1).trimmed().toLongLong(&ok);
                if (!ok || parsed < 0 || parsed > 16384) { invalidLength = true; break; }
                bodyLength = static_cast<qsizetype>(parsed);
            }
            if (invalidLength) {
                socket->setProperty("automationRequestHandled", true);
                requestTimeout->stop();
                socket->write("HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
                socket->disconnectFromHost();
                return;
            }
            const auto expectedLength = headerEnd + 4 + bodyLength;
            if (request.size() < expectedLength) return;
            if (request.size() != expectedLength) {
                socket->setProperty("automationRequestHandled", true);
                requestTimeout->stop();
                socket->write("HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
                socket->disconnectFromHost();
                return;
            }
            socket->setProperty("automationRequestHandled", true);
            requestTimeout->stop();
            if (!m_automationRateWindow.isValid() || m_automationRateWindow.elapsed() >= 1000) {
                m_automationRateWindow.start();
                m_automationRequestsInWindow = 0;
            }
            if (++m_automationRequestsInWindow > 100) {
                socket->write("HTTP/1.1 429 Too Many Requests\r\nRetry-After: 1\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
                socket->disconnectFromHost();
                return;
            }
            const auto requestLine = request.left(request.indexOf("\r\n")).split(' ');
            const auto requestPath = requestLine.value(1);
            if (requestPath.endsWith("/screenshot")) {
                if (!m_automationScreenshotRateWindow.isValid() ||
                    m_automationScreenshotRateWindow.elapsed() >= 1000) {
                    m_automationScreenshotRateWindow.start();
                    m_automationScreenshotsInWindow = 0;
                }
                if (++m_automationScreenshotsInWindow > 5) {
                    socket->write("HTTP/1.1 429 Too Many Requests\r\nRetry-After: 1\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
                    socket->disconnectFromHost();
                    return;
                }
            }
            const auto response = automationResponse(request, socket);
            if (response == QByteArrayLiteral("AUTOMATION_ASYNC_SCREENSHOT")) {
                startAutomationScreenshot(socket);
                return;
            }
            if (response == QByteArrayLiteral("AUTOMATION_ASYNC_INPUT")) return;
            socket->write(response);
            socket->disconnectFromHost();
        });
        connect(socket, &QTcpSocket::disconnected, this, [this, socket] {
            if (auto *process = m_automationInputProcesses.take(socket)) {
                const auto deviceId = m_automationInputDevices.take(socket);
                m_automationInputInFlight.remove(deviceId);
                if (process->state() != QProcess::NotRunning) process->kill();
                process->deleteLater();
            }
            if (m_automationScreenshotSocket == socket) {
                m_automationScreenshotTimeout.stop();
                m_automationScreenshotSocket.clear();
                m_automationScreenshotBytes.clear();
                if (!m_automationScreenshotPath.isEmpty()) {
                    QFile::remove(m_automationScreenshotPath);
                    m_automationScreenshotPath.clear();
                }
                if (m_automationScreenshot.state() != QProcess::NotRunning)
                    m_automationScreenshot.kill();
                if (m_automationIosScreenshot.state() != QProcess::NotRunning)
                    m_automationIosScreenshot.kill();
            }
            m_automationSockets.remove(socket);
            socket->deleteLater();
        });
    }
}

void Backend::startAutomationScreenshot(QTcpSocket *socket) {
    if (m_automationScreenshot.state() != QProcess::NotRunning ||
        m_automationIosScreenshot.state() != QProcess::NotRunning) {
        const QByteArray body = R"({"error":{"code":"CONTROL_LOCKED","message":"A screenshot request is already in progress."}})";
        socket->write("HTTP/1.1 409 Conflict\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: " + QByteArray::number(body.size()) + "\r\n\r\n" + body);
        socket->disconnectFromHost();
        return;
    }
    m_automationScreenshotSocket = socket;
    if ((!m_selected.startsWith(QLatin1String(AndroidPrefix)) &&
         !m_selected.startsWith(QLatin1String(IosPrefix))) ||
        m_mirror.state() == QProcess::NotRunning) {
        failAutomationScreenshot(503, QByteArrayLiteral("SCREENSHOT_UNAVAILABLE"));
        return;
    }
    m_automationScreenshotSocket = socket;
    m_automationScreenshotBytes.clear();
    m_automationScreenshotPath.clear();
    if (m_selected.startsWith(QLatin1String(AndroidPrefix))) {
        const auto adb = QStandardPaths::findExecutable(QStringLiteral("adb"));
        if (adb.isEmpty()) {
            failAutomationScreenshot(503, QByteArrayLiteral("SCREENSHOT_UNAVAILABLE"));
            return;
        }
        const auto serial = m_selected.mid(static_cast<qsizetype>(
            QString::fromLatin1(AndroidPrefix).size()));
        m_automationScreenshot.setProgram(adb);
        m_automationScreenshot.setArguments({QStringLiteral("-s"), serial,
            QStringLiteral("exec-out"), QStringLiteral("screencap"), QStringLiteral("-p")});
    } else {
        const auto tool = QStandardPaths::findExecutable(QStringLiteral("idevicescreenshot"));
        if (tool.isEmpty()) {
            failAutomationScreenshot(503, QByteArrayLiteral("SCREENSHOT_UNAVAILABLE"));
            return;
        }
        QTemporaryFile temporary(QDir::temp().filePath(
            QStringLiteral("iphoneusb-api-screenshot-XXXXXX.png")));
        temporary.setAutoRemove(false);
        if (!temporary.open()) {
            failAutomationScreenshot(503, QByteArrayLiteral("SCREENSHOT_UNAVAILABLE"));
            return;
        }
        m_automationScreenshotPath = temporary.fileName();
        temporary.close();
        const auto udid = m_selected.mid(static_cast<qsizetype>(
            QString::fromLatin1(IosPrefix).size()));
        m_automationIosScreenshot.setProgram(tool);
        m_automationIosScreenshot.setArguments({QStringLiteral("--udid"), udid,
            m_automationScreenshotPath});
        m_automationIosScreenshot.setStandardOutputFile(QProcess::nullDevice());
    }
    if (m_selected.startsWith(QLatin1String(IosPrefix))) {
        m_automationIosScreenshot.setStandardErrorFile(QProcess::nullDevice());
        m_automationScreenshotTimeout.start(15000);
        m_automationIosScreenshot.start();
        return;
    }
    m_automationScreenshot.setStandardErrorFile(QProcess::nullDevice());
    m_automationScreenshotTimeout.start(10000);
    m_automationScreenshot.start();
}

void Backend::finishAutomationScreenshot(int exitCode, QProcess::ExitStatus status,
                                         bool ios) {
    m_automationScreenshotTimeout.stop();
    if (!m_automationScreenshotSocket) {
        m_automationScreenshotBytes.clear();
        return;
    }
    if (ios) {
        m_automationIosScreenshot.readAllStandardOutput();
        QFile image(m_automationScreenshotPath);
        if (image.size() <= 32 * 1024 * 1024 && image.open(QIODevice::ReadOnly))
            m_automationScreenshotBytes = image.readAll();
        QFile::remove(m_automationScreenshotPath);
        m_automationScreenshotPath.clear();
    } else {
        m_automationScreenshotBytes += m_automationScreenshot.readAllStandardOutput();
    }
    if (exitCode != 0 || status != QProcess::NormalExit ||
        m_automationScreenshotBytes.size() < 24 ||
        m_automationScreenshotBytes.size() > 32 * 1024 * 1024 ||
        !m_automationScreenshotBytes.startsWith(QByteArray::fromHex("89504e470d0a1a0a"))) {
        failAutomationScreenshot(503, QByteArrayLiteral("SCREENSHOT_UNAVAILABLE"));
        return;
    }
    auto *socket = m_automationScreenshotSocket.data();
    m_automationScreenshotSocket.clear();
    const auto bytes = std::move(m_automationScreenshotBytes);
    m_automationScreenshotBytes.clear();
    socket->write("HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: " + QByteArray::number(bytes.size()) + "\r\n\r\n" + bytes);
    socket->disconnectFromHost();
}

void Backend::failAutomationScreenshot(int httpStatus, const QByteArray &code) {
    m_automationScreenshotTimeout.stop();
    m_automationScreenshotBytes.clear();
    if (!m_automationScreenshotPath.isEmpty()) {
        QFile::remove(m_automationScreenshotPath);
        m_automationScreenshotPath.clear();
    }
    if (!m_automationScreenshotSocket) return;
    auto *socket = m_automationScreenshotSocket.data();
    m_automationScreenshotSocket.clear();
    const auto status = httpStatus == 504 ? QByteArrayLiteral("504 Gateway Timeout")
        : httpStatus == 413 ? QByteArrayLiteral("413 Payload Too Large")
        : httpStatus == 409 ? QByteArrayLiteral("409 Conflict")
        : QByteArrayLiteral("503 Service Unavailable");
    const auto body = QJsonDocument(QJsonObject{
        {QStringLiteral("error"), QJsonObject{
            {QStringLiteral("code"), QString::fromLatin1(code)},
            {QStringLiteral("message"), QStringLiteral("No se pudo obtener la captura del dispositivo.")}}}})
        .toJson(QJsonDocument::Compact);
    socket->write("HTTP/1.1 " + status + "\r\nContent-Type: application/json\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: " + QByteArray::number(body.size()) + "\r\n\r\n" + body);
    socket->disconnectFromHost();
}

QByteArray Backend::automationResponse(const QByteArray &request, QTcpSocket *socket) {
    const auto headerEnd = request.indexOf("\r\n\r\n");
    const auto headerLines = request.left(headerEnd).split('\n');
    const auto requestLine = headerLines.value(0).trimmed().split(' ');
    const auto method = requestLine.value(0);
    const auto path = requestLine.value(1);
    const auto bodyBytes = headerEnd >= 0 ? request.mid(headerEnd + 4) : QByteArray{};
    auto httpStatus = QByteArrayLiteral("200 OK");
    QJsonObject json;
    if (headerEnd < 0 || requestLine.size() != 3 || requestLine.value(2) != "HTTP/1.1" ||
        (method != "GET" && method != "POST") ||
        (method == "GET" && !bodyBytes.isEmpty())) {
        httpStatus = QByteArrayLiteral("400 Bad Request");
        json = {{QStringLiteral("error"), QStringLiteral("INVALID_REQUEST")}};
    } else {
        QByteArray suppliedKey;
        bool validHost = false;
        bool credentialSeen = false;
        bool hostSeen = false;
        bool sessionSeen = false;
        bool duplicateHeader = false;
        bool hasOrigin = false;
        QByteArray controlSession;
        QByteArray contentType;
        for (const auto &rawLine : headerLines.mid(1)) {
            const auto line = rawLine.trimmed();
            const auto separator = line.indexOf(':');
            if (separator <= 0) continue;
            const auto name = line.left(separator).trimmed().toLower();
            const auto value = line.mid(separator + 1).trimmed();
            if (name == "host") {
                duplicateHeader |= hostSeen;
                hostSeen = true;
                validHost = value == "127.0.0.1:17890" || value == "localhost:17890";
            } else if (name == "x-api-key") {
                duplicateHeader |= credentialSeen;
                credentialSeen = true;
                suppliedKey = value;
            } else if (name == "x-control-session") {
                duplicateHeader |= sessionSeen;
                sessionSeen = true;
                controlSession = value;
            } else if (name == "content-type") {
                contentType = value.toLower();
            } else if (name == "origin") {
                hasOrigin = true;
            }
        }
        if (!validHost || !hostSeen || duplicateHeader) {
            httpStatus = QByteArrayLiteral("400 Bad Request");
            json = {{QStringLiteral("error"), QStringLiteral("INVALID_HOST")}};
        } else if (hasOrigin) {
            httpStatus = QByteArrayLiteral("403 Forbidden");
            json = {{QStringLiteral("error"), QStringLiteral("FORBIDDEN")}};
        } else if (path != "/api/docs" && path != "/api/v1/openapi.json") {
            const auto expected = m_automationApiKey.toLatin1();
            unsigned int difference = static_cast<unsigned int>(suppliedKey.size() ^ expected.size());
            for (qsizetype i = 0; i < expected.size(); ++i) {
                const auto actual = i < suppliedKey.size() ? suppliedKey.at(i) : 0;
                difference |= static_cast<unsigned char>(actual ^ expected.at(i));
            }
            if (difference != 0) {
                httpStatus = QByteArrayLiteral("401 Unauthorized");
                json = {{QStringLiteral("error"), QStringLiteral("UNAUTHORIZED")}};
            } else if (path.startsWith("/api/v1/devices/") &&
                       (path.endsWith("/control/acquire") ||
                        path.endsWith("/control/release"))) {
                const bool acquire = path.endsWith("/control/acquire");
                const auto prefixLength = qsizetype(sizeof("/api/v1/devices/") - 1);
                const auto suffixLength = acquire
                    ? qsizetype(sizeof("/control/acquire") - 1)
                    : qsizetype(sizeof("/control/release") - 1);
                const auto opaqueId = path.mid(prefixLength,
                    path.size() - prefixLength - suffixLength);
                QString internalId;
                for (const auto &entry : m_devices) {
                    const auto candidate = entry.toMap().value(QStringLiteral("id")).toString();
                    const auto hash = QCryptographicHash::hash(candidate.toUtf8(),
                        QCryptographicHash::Sha256).toHex();
                    if (opaqueId == hash) { internalId = candidate; break; }
                }
                if (method != "POST") {
                    httpStatus = QByteArrayLiteral("405 Method Not Allowed");
                    json = {{QStringLiteral("error"), QStringLiteral("METHOD_NOT_ALLOWED")}};
                } else if (!bodyBytes.isEmpty() && !contentType.startsWith("application/json")) {
                    httpStatus = QByteArrayLiteral("415 Unsupported Media Type");
                    json = {{QStringLiteral("error"), QStringLiteral("INVALID_CONTENT_TYPE")}};
                } else if (internalId.isEmpty()) {
                    httpStatus = QByteArrayLiteral("404 Not Found");
                    json = {{QStringLiteral("error"), QStringLiteral("DEVICE_NOT_FOUND")}};
                } else if (!internalId.startsWith(QLatin1String(AndroidPrefix)) ||
                           internalId != m_selected ||
                           m_mirror.state() == QProcess::NotRunning ||
                           m_automationGeometry.isEmpty()) {
                    httpStatus = QByteArrayLiteral("503 Service Unavailable");
                    json = {{QStringLiteral("error"), QJsonObject{
                        {QStringLiteral("code"), QStringLiteral("CONTROL_NOT_AVAILABLE")},
                        {QStringLiteral("message"), QStringLiteral("Se requiere un Android seleccionado, duplicándose, con geometría de pantalla disponible.")}}}};
                } else {
                    const auto now = QDateTime::currentMSecsSinceEpoch();
                    auto lease = m_automationLeases.value(internalId);
                    if (lease.expiresAtMs <= now) {
                        cancelAutomationInput(internalId);
                        m_automationLeases.remove(internalId);
                        lease = {};
                    }
                    if (acquire) {
                        QJsonParseError parseError;
                        const auto document = QJsonDocument::fromJson(bodyBytes, &parseError);
                        const auto requestObject = document.object();
                        const auto durationValue = requestObject.value(QStringLiteral("durationSeconds"));
                        const double requestedDuration = durationValue.isUndefined()
                            ? 30.0 : durationValue.toDouble(-1.0);
                        const bool durationValid =
                            (durationValue.isUndefined() || durationValue.isDouble()) &&
                            std::isfinite(requestedDuration) &&
                            std::floor(requestedDuration) == requestedDuration &&
                            requestedDuration >= 5.0 && requestedDuration <= 300.0;
                        const int duration = durationValid
                            ? static_cast<int>(requestedDuration) : 0;
                        if (parseError.error != QJsonParseError::NoError ||
                            !document.isObject() || !durationValid) {
                            httpStatus = QByteArrayLiteral("422 Unprocessable Content");
                            json = {{QStringLiteral("error"), QJsonObject{
                                {QStringLiteral("code"), QStringLiteral("INVALID_DURATION")},
                                {QStringLiteral("message"), QStringLiteral("durationSeconds debe estar entre 5 y 300.")}}}};
                        } else if (!lease.token.isEmpty() &&
                                   (controlSession.isEmpty() ||
                                    lease.token.toLatin1() != controlSession)) {
                            httpStatus = QByteArrayLiteral("409 Conflict");
                            json = {{QStringLiteral("error"), QJsonObject{
                                {QStringLiteral("code"), QStringLiteral("CONTROL_LOCKED")},
                                {QStringLiteral("message"), QStringLiteral("El dispositivo ya tiene un lease de control activo.")}}}};
                        } else {
                            if (lease.token.isEmpty()) {
                                auto *random = QRandomGenerator::system();
                                lease.token.clear();
                                for (int i = 0; i < 4; ++i)
                                    lease.token += QStringLiteral("%1").arg(
                                        random->generate64(), 16, 16, QLatin1Char('0'));
                            }
                            lease.expiresAtMs = now + qint64(duration) * 1000;
                            m_automationLeases.insert(internalId, lease);
                            json = {{QStringLiteral("sessionToken"), lease.token},
                                {QStringLiteral("expiresAt"), QDateTime::fromMSecsSinceEpoch(
                                    lease.expiresAtMs, QTimeZone::UTC).toString(Qt::ISODateWithMs)}};
                        }
                    } else if (lease.token.isEmpty()) {
                        if (controlSession.size() != 64) {
                            httpStatus = QByteArrayLiteral("409 Conflict");
                            json = {{QStringLiteral("error"), QStringLiteral("CONTROL_LOCKED")}};
                        }
                    } else if (controlSession != lease.token.toLatin1()) {
                        httpStatus = QByteArrayLiteral("409 Conflict");
                        json = {{QStringLiteral("error"), QStringLiteral("CONTROL_LOCKED")}};
                    } else {
                        cancelAutomationInput(internalId);
                        m_automationLeases.remove(internalId);
                        json = {{QStringLiteral("released"), true}};
                    }
                }
            } else if (path.startsWith("/api/v1/devices/") &&
                       path.contains("/input/")) {
                const auto prefixLength = qsizetype(sizeof("/api/v1/devices/") - 1);
                const auto actionMarker = path.indexOf("/input/", prefixLength);
                const auto opaqueId = path.mid(prefixLength, actionMarker - prefixLength);
                const auto action = path.mid(actionMarker + qsizetype(sizeof("/input/") - 1));
                QString internalId;
                for (const auto &entry : m_devices) {
                    const auto candidate = entry.toMap().value(QStringLiteral("id")).toString();
                    const auto hash = QCryptographicHash::hash(candidate.toUtf8(),
                        QCryptographicHash::Sha256).toHex();
                    if (opaqueId == hash) { internalId = candidate; break; }
                }
                if (method != "POST") {
                    httpStatus = QByteArrayLiteral("405 Method Not Allowed");
                    json = {{QStringLiteral("error"), QStringLiteral("METHOD_NOT_ALLOWED")}};
                } else if (!contentType.startsWith("application/json")) {
                    httpStatus = QByteArrayLiteral("415 Unsupported Media Type");
                    json = {{QStringLiteral("error"), QStringLiteral("INVALID_CONTENT_TYPE")}};
                } else if (internalId.isEmpty()) {
                    httpStatus = QByteArrayLiteral("404 Not Found");
                    json = {{QStringLiteral("error"), QStringLiteral("DEVICE_NOT_FOUND")}};
                } else if (action != "tap" && action != "long-press" &&
                           action != "swipe" && action != "key" && action != "text") {
                    httpStatus = QByteArrayLiteral("501 Not Implemented");
                    json = {{QStringLiteral("error"), QJsonObject{
                        {QStringLiteral("code"), QStringLiteral("CAPABILITY_NOT_SUPPORTED")},
                        {QStringLiteral("message"), QStringLiteral("Acción no disponible en el transporte ADB actual.")}}}};
                } else {
                    const auto now = QDateTime::currentMSecsSinceEpoch();
                    auto lease = m_automationLeases.value(internalId);
                    if (lease.expiresAtMs <= now) {
                        cancelAutomationInput(internalId);
                        m_automationLeases.remove(internalId);
                        lease = {};
                    }
                    if (internalId != m_selected ||
                        !internalId.startsWith(QLatin1String(AndroidPrefix)) ||
                        m_mirror.state() == QProcess::NotRunning ||
                        m_automationGeometry.isEmpty()) {
                        httpStatus = QByteArrayLiteral("503 Service Unavailable");
                        json = {{QStringLiteral("error"), QStringLiteral("CONTROL_NOT_AVAILABLE")}};
                    } else if (lease.token.isEmpty() ||
                               controlSession != lease.token.toLatin1()) {
                        httpStatus = QByteArrayLiteral("409 Conflict");
                        json = {{QStringLiteral("error"), QStringLiteral("CONTROL_LOCKED")}};
                    } else {
                        QJsonParseError parseError;
                        const auto document = QJsonDocument::fromJson(bodyBytes, &parseError);
                        const auto input = document.object();
                        const int left = m_automationGeometry.value(QStringLiteral("left")).toInt();
                        const int top = m_automationGeometry.value(QStringLiteral("top")).toInt();
                        const int width = m_automationGeometry.value(QStringLiteral("width")).toInt();
                        const int height = m_automationGeometry.value(QStringLiteral("height")).toInt();
                        auto point = [&](const QString &xName, const QString &yName,
                                         int *x, int *y) {
                            const auto xValue = input.value(xName);
                            const auto yValue = input.value(yName);
                            if (!xValue.isDouble() || !yValue.isDouble()) return false;
                            const double px = xValue.toDouble();
                            const double py = yValue.toDouble();
                            if (!std::isfinite(px) || !std::isfinite(py) ||
                                px < 0 || py < 0 || px >= width || py >= height) return false;
                            *x = left + static_cast<int>(std::floor(px));
                            *y = top + static_cast<int>(std::floor(py));
                            return true;
                        };
                        int x1 = 0, y1 = 0, x2 = 0, y2 = 0;
                        bool valid = parseError.error == QJsonParseError::NoError &&
                                     document.isObject();
                        const auto geometryVersion = input.value(QStringLiteral("geometryVersion"));
                        const bool geometryMismatch = !geometryVersion.isString() ||
                            geometryVersion.toString() !=
                                m_automationGeometry.value(QStringLiteral("version")).toString();
                        valid = valid && !geometryMismatch;
                        QString androidKeyCode;
                        QString apiText;
                        if (action == "key") {
                            static const QHash<QString, QString> namedKeys{
                                {QStringLiteral("ENTER"), QStringLiteral("KEYCODE_ENTER")},
                                {QStringLiteral("ESCAPE"), QStringLiteral("KEYCODE_ESCAPE")},
                                {QStringLiteral("TAB"), QStringLiteral("KEYCODE_TAB")},
                                {QStringLiteral("SPACE"), QStringLiteral("KEYCODE_SPACE")},
                                {QStringLiteral("BACKSPACE"), QStringLiteral("KEYCODE_DEL")},
                                {QStringLiteral("DELETE"), QStringLiteral("KEYCODE_FORWARD_DEL")},
                                {QStringLiteral("LEFT"), QStringLiteral("KEYCODE_DPAD_LEFT")},
                                {QStringLiteral("UP"), QStringLiteral("KEYCODE_DPAD_UP")},
                                {QStringLiteral("RIGHT"), QStringLiteral("KEYCODE_DPAD_RIGHT")},
                                {QStringLiteral("DOWN"), QStringLiteral("KEYCODE_DPAD_DOWN")},
                                {QStringLiteral("HOME"), QStringLiteral("KEYCODE_HOME")},
                                {QStringLiteral("END"), QStringLiteral("KEYCODE_MOVE_END")},
                                {QStringLiteral("PAGEUP"), QStringLiteral("KEYCODE_PAGE_UP")},
                                {QStringLiteral("PAGEDOWN"), QStringLiteral("KEYCODE_PAGE_DOWN")},
                                {QStringLiteral("BACK"), QStringLiteral("KEYCODE_BACK")},
                                {QStringLiteral("MENU"), QStringLiteral("KEYCODE_MENU")},
                                {QStringLiteral("APP_SWITCH"), QStringLiteral("KEYCODE_APP_SWITCH")},
                                {QStringLiteral("VOLUMEUP"), QStringLiteral("KEYCODE_VOLUME_UP")},
                                {QStringLiteral("VOLUMEDOWN"), QStringLiteral("KEYCODE_VOLUME_DOWN")}};
                            const auto key = input.value(QStringLiteral("key")).toString().toUpper();
                            if (key.size() == 1 && key.at(0).isLetter() && key.at(0).unicode() <= 'Z')
                                androidKeyCode = QStringLiteral("KEYCODE_%1").arg(key);
                            else if (key.size() == 1 && key.at(0).isDigit())
                                androidKeyCode = QStringLiteral("KEYCODE_%1").arg(key);
                            else
                                androidKeyCode = namedKeys.value(key);
                            valid = valid && !androidKeyCode.isEmpty();
                        } else if (action == "text") {
                            const auto textValue = input.value(QStringLiteral("text"));
                            if (!textValue.isString()) {
                                valid = false;
                            } else {
                                apiText = textValue.toString();
                                if (apiText.isEmpty() || apiText.size() > 256 ||
                                    apiText.contains(QStringLiteral("%s"))) {
                                    valid = false;
                                }
                                for (const auto character : apiText) {
                                    const auto c = character.unicode();
                                    const bool safe = (c >= 'a' && c <= 'z') ||
                                        (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                                        character == QLatin1Char(' ') ||
                                        QStringLiteral(".,_+-@:/%").contains(character);
                                    if (!safe) { valid = false; break; }
                                }
                            }
                        } else if (action == "swipe")
                            valid = valid && point(QStringLiteral("x1"), QStringLiteral("y1"), &x1, &y1) &&
                                point(QStringLiteral("x2"), QStringLiteral("y2"), &x2, &y2) &&
                                (x1 != x2 || y1 != y2);
                        else if (action != "key" && action != "text")
                            valid = valid && point(QStringLiteral("x"), QStringLiteral("y"), &x1, &y1);
                        int durationMs = action == "long-press" ? 500
                            : action == "text" ? qMax(250, apiText.size() * 50) : 250;
                        if (action == "text" && !input.value(QStringLiteral("duration")).isUndefined())
                            valid = false;
                        if (action != "tap" && action != "key" && action != "text") {
                            const auto duration = input.value(QStringLiteral("duration"));
                            if (!duration.isUndefined() && !duration.isDouble()) {
                                valid = false;
                            } else if (duration.isDouble()) {
                                const double seconds = duration.toDouble();
                                if (!std::isfinite(seconds) || seconds < 0.05 || seconds > 10.0)
                                    valid = false;
                                else durationMs = qRound(seconds * 1000.0);
                            }
                        }
                        if (geometryMismatch) {
                            httpStatus = QByteArrayLiteral("409 Conflict");
                            json = {{QStringLiteral("error"), QJsonObject{
                                {QStringLiteral("code"), QStringLiteral("GEOMETRY_CHANGED")},
                                {QStringLiteral("message"), QStringLiteral("Vuelve a consultar la geometría y adquiere un lease nuevo.")}}}};
                        } else if (!valid || width <= 0 || height <= 0) {
                            httpStatus = QByteArrayLiteral("422 Unprocessable Content");
                            json = {{QStringLiteral("error"), QJsonObject{
                                {QStringLiteral("code"), QStringLiteral("INVALID_REQUEST")},
                                {QStringLiteral("message"), QStringLiteral("Coordenadas o duración no válidas para la geometría actual.")}}}};
                        } else if (lease.expiresAtMs <=
                            QDateTime::currentMSecsSinceEpoch() + durationMs + 5000) {
                            httpStatus = QByteArrayLiteral("409 Conflict");
                            json = {{QStringLiteral("error"), QJsonObject{
                                {QStringLiteral("code"), QStringLiteral("CONTROL_LOCKED")},
                                {QStringLiteral("message"), QStringLiteral("Renueva el lease antes de iniciar una acción que exceda su vigencia.")}}}};
                        } else {
                            const auto adb = QStandardPaths::findExecutable(QStringLiteral("adb"));
                            QStringList arguments{QStringLiteral("-s"),
                                internalId.mid(static_cast<qsizetype>(
                                    QString::fromLatin1(AndroidPrefix).size())),
                                QStringLiteral("shell"), QStringLiteral("input")};
                            if (action == "key") {
                                arguments << QStringLiteral("keyevent") << androidKeyCode;
                            } else if (action == "text") {
                                apiText.replace(QLatin1Char(' '), QStringLiteral("%s"));
                                arguments << QStringLiteral("text") << apiText;
                            } else if (action == "tap") {
                                arguments << QStringLiteral("tap") << QString::number(x1)
                                          << QString::number(y1);
                            } else {
                                arguments << QStringLiteral("swipe") << QString::number(x1)
                                    << QString::number(y1)
                                    << QString::number(action == "swipe" ? x2 : x1)
                                    << QString::number(action == "swipe" ? y2 : y1)
                                    << QString::number(durationMs);
                            }
                            if (adb.isEmpty()) {
                                httpStatus = QByteArrayLiteral("503 Service Unavailable");
                                json = {{QStringLiteral("error"), QStringLiteral("CONTROL_NOT_AVAILABLE")}};
                            } else {
                                startAutomationInput(socket, internalId, adb, arguments,
                                                     durationMs + 5000);
                                return QByteArrayLiteral("AUTOMATION_ASYNC_INPUT");
                            }
                        }
                    }
                }
            } else if (path == "/api/v1/app/status") {
                json = {{QStringLiteral("running"), running()},
                        {QStringLiteral("selectedDevice"), selectedLabel()},
                        {QStringLiteral("recording"), m_mediaRecordingActive},
                        {QStringLiteral("streaming"), m_mediaStreamingActive}};
            } else if (path == "/api/v1/devices") {
                QJsonArray list;
                for (const auto &entry : m_devices) {
                    const auto item = entry.toMap();
                    const auto internalId = item.value(QStringLiteral("id")).toString();
                    const auto opaqueId = QCryptographicHash::hash(internalId.toUtf8(),
                        QCryptographicHash::Sha256).toHex();
                    list.push_back(QJsonObject{
                        {QStringLiteral("id"), QString::fromLatin1(opaqueId)},
                        {QStringLiteral("name"), item.value(QStringLiteral("label")).toString()},
                        {QStringLiteral("platform"), internalId.startsWith(QLatin1String(IosPrefix))
                            ? QStringLiteral("ios") : QStringLiteral("android")}});
                }
                json = {{QStringLiteral("devices"), list}};
            } else if (path.startsWith("/api/v1/devices/") &&
                       path.endsWith("/status")) {
                const auto prefixLength = qsizetype(sizeof("/api/v1/devices/") - 1);
                const auto suffixLength = qsizetype(sizeof("/status") - 1);
                const auto opaqueId = path.mid(prefixLength,
                    path.size() - prefixLength - suffixLength);
                QString internalId;
                QVariantMap selectedDevice;
                for (const auto &entry : m_devices) {
                    const auto item = entry.toMap();
                    const auto candidate = item.value(QStringLiteral("id")).toString();
                    const auto hash = QCryptographicHash::hash(candidate.toUtf8(),
                        QCryptographicHash::Sha256).toHex();
                    if (opaqueId != hash) continue;
                    internalId = candidate;
                    selectedDevice = item;
                    break;
                }
                if (method != "GET") {
                    httpStatus = QByteArrayLiteral("405 Method Not Allowed");
                    json = {{QStringLiteral("error"), QStringLiteral("METHOD_NOT_ALLOWED")}};
                } else if (internalId.isEmpty()) {
                    httpStatus = QByteArrayLiteral("404 Not Found");
                    json = {{QStringLiteral("error"), QJsonObject{
                        {QStringLiteral("code"), QStringLiteral("DEVICE_NOT_FOUND")},
                        {QStringLiteral("message"), QStringLiteral("No se encontró el dispositivo solicitado.")}}}};
                } else {
                    const bool isIos = internalId.startsWith(QLatin1String(IosPrefix));
                    const bool fresh = isIos ? m_iosDeviceListFresh : m_androidDeviceListFresh;
                    const bool selected = internalId == m_selected;
                    const bool capturing = selected &&
                        m_mirror.state() != QProcess::NotRunning;
                    const bool screenshot = capturing;
                    auto lease = m_automationLeases.value(internalId);
                    if (lease.expiresAtMs <= QDateTime::currentMSecsSinceEpoch()) {
                        cancelAutomationInput(internalId);
                        m_automationLeases.remove(internalId);
                        lease = {};
                    }
                    const bool controlAvailable = !isIos && capturing &&
                        !m_automationGeometry.isEmpty() &&
                        !QStandardPaths::findExecutable(QStringLiteral("adb")).isEmpty();
                    json = {{QStringLiteral("id"), QString::fromLatin1(opaqueId)},
                        {QStringLiteral("name"), selectedDevice.value(QStringLiteral("label")).toString()},
                        {QStringLiteral("platform"), isIos ? QStringLiteral("ios") : QStringLiteral("android")},
                        {QStringLiteral("connection"), isIos ? QStringLiteral("usb")
                            : (internalId.mid(sizeof("android:") - 1).contains(QLatin1Char(':'))
                                ? QStringLiteral("wifi") : QStringLiteral("usb"))},
                        {QStringLiteral("online"), fresh},
                        {QStringLiteral("onlineKnown"), fresh},
                        {QStringLiteral("selected"), selected},
                        {QStringLiteral("capturing"), capturing},
                        {QStringLiteral("control"), QJsonObject{
                            {QStringLiteral("available"), controlAvailable},
                            {QStringLiteral("transport"), controlAvailable
                                ? QJsonValue(QStringLiteral("adb")) : QJsonValue(QJsonValue::Null)},
                            {QStringLiteral("acquired"), !lease.token.isEmpty()},
                            {QStringLiteral("expiresAt"), lease.token.isEmpty()
                                ? QJsonValue(QJsonValue::Null)
                                : QJsonValue(QDateTime::fromMSecsSinceEpoch(
                                    lease.expiresAtMs, QTimeZone::UTC).toString(Qt::ISODateWithMs))}}},
                        {QStringLiteral("capabilities"), QJsonObject{
                            {QStringLiteral("tap"), controlAvailable},
                            {QStringLiteral("longPress"), controlAvailable},
                            {QStringLiteral("touchDown"), false},
                            {QStringLiteral("touchUp"), false},
                            {QStringLiteral("swipe"), controlAvailable},
                            {QStringLiteral("touchPath"), false},
                            {QStringLiteral("keyboard"), controlAvailable},
                            {QStringLiteral("textInput"), controlAvailable},
                            {QStringLiteral("clipboardRead"), false},
                            {QStringLiteral("clipboardWrite"), false},
                            {QStringLiteral("screenshot"), screenshot}}},
                        {QStringLiteral("geometry"), m_automationGeometry.isEmpty()
                            ? QJsonValue(QJsonValue::Null)
                            : QJsonValue(m_automationGeometry)}};
                }
            } else if (path.startsWith("/api/v1/devices/") &&
                       path.endsWith("/screenshot")) {
                const auto prefixLength = qsizetype(sizeof("/api/v1/devices/") - 1);
                const auto suffixLength = qsizetype(sizeof("/screenshot") - 1);
                const auto opaqueId = path.mid(prefixLength,
                    path.size() - prefixLength - suffixLength);
                QString internalId;
                for (const auto &entry : m_devices) {
                    const auto candidate = entry.toMap().value(QStringLiteral("id")).toString();
                    const auto hash = QCryptographicHash::hash(candidate.toUtf8(),
                        QCryptographicHash::Sha256).toHex();
                    if (opaqueId == hash) { internalId = candidate; break; }
                }
                if (method != "GET") {
                    httpStatus = QByteArrayLiteral("405 Method Not Allowed");
                    json = {{QStringLiteral("error"), QStringLiteral("METHOD_NOT_ALLOWED")}};
                } else if (internalId.isEmpty()) {
                    httpStatus = QByteArrayLiteral("404 Not Found");
                    json = {{QStringLiteral("error"), QJsonObject{
                        {QStringLiteral("code"), QStringLiteral("DEVICE_NOT_FOUND")},
                        {QStringLiteral("message"), QStringLiteral("No se encontró el dispositivo solicitado.")}}}};
                } else if (internalId == m_selected &&
                    m_mirror.state() != QProcess::NotRunning) {
                    return QByteArrayLiteral("AUTOMATION_ASYNC_SCREENSHOT");
                } else {
                    httpStatus = QByteArrayLiteral("503 Service Unavailable");
                    json = {{QStringLiteral("error"), QJsonObject{
                        {QStringLiteral("code"), QStringLiteral("SCREENSHOT_UNAVAILABLE")},
                        {QStringLiteral("message"), QStringLiteral("Inicia la duplicación del dispositivo seleccionado.")}}}};
                }
            } else if (path.startsWith("/api/v1/devices/")) {
                const auto opaqueId = path.mid(sizeof("/api/v1/devices/") - 1);
                bool found = false;
                for (const auto &entry : m_devices) {
                    const auto item = entry.toMap();
                    const auto internalId = item.value(QStringLiteral("id")).toString();
                    const auto expectedId = QCryptographicHash::hash(internalId.toUtf8(),
                        QCryptographicHash::Sha256).toHex();
                    if (opaqueId != expectedId) continue;
                    json = {{QStringLiteral("id"), QString::fromLatin1(expectedId)},
                            {QStringLiteral("name"), item.value(QStringLiteral("label")).toString()},
                            {QStringLiteral("platform"), internalId.startsWith(QLatin1String(IosPrefix))
                                ? QStringLiteral("ios") : QStringLiteral("android")}};
                    found = true;
                    break;
                }
                if (!found) {
                    httpStatus = QByteArrayLiteral("404 Not Found");
                    json = {{QStringLiteral("error"), QStringLiteral("DEVICE_NOT_FOUND")}};
                }
            } else {
                httpStatus = QByteArrayLiteral("404 Not Found");
                json = {{QStringLiteral("error"), QStringLiteral("NOT_FOUND")}};
            }
        } else if (path == "/api/docs") {
            const QByteArray body = "<!doctype html><html><meta charset=utf-8><title>iPhoneMirror API</title><body><h1>iPhoneMirror Automation API</h1><p>API local con leases exclusivos y control Android por ADB: tap, long-press, swipe y teclas.</p><a href='/api/v1/openapi.json'>OpenAPI 3.1</a></body></html>";
            return "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: " + QByteArray::number(body.size()) + "\r\n\r\n" + body;
        } else {
            QJsonObject info{{QStringLiteral("title"), QStringLiteral("iPhoneMirror Automation API")},
                             {QStringLiteral("version"), QStringLiteral("1.0.0")}};
            QJsonObject paths{
                {QStringLiteral("/api/v1/app/status"), QJsonObject{{QStringLiteral("get"), QJsonObject{{QStringLiteral("summary"), QStringLiteral("Read application status")}, {QStringLiteral("security"), QJsonArray{QJsonObject{{QStringLiteral("ApiKey"), QJsonArray{}}}}}}}}},
                {QStringLiteral("/api/v1/devices"), QJsonObject{{QStringLiteral("get"), QJsonObject{{QStringLiteral("summary"), QStringLiteral("List observed devices")}, {QStringLiteral("security"), QJsonArray{QJsonObject{{QStringLiteral("ApiKey"), QJsonArray{}}}}}}}}},
                {QStringLiteral("/api/v1/devices/{id}"), QJsonObject{{QStringLiteral("get"), QJsonObject{{QStringLiteral("summary"), QStringLiteral("Read an observed device")}, {QStringLiteral("security"), QJsonArray{QJsonObject{{QStringLiteral("ApiKey"), QJsonArray{}}}}}}}}},
                {QStringLiteral("/api/v1/devices/{id}/status"), QJsonObject{{QStringLiteral("get"), QJsonObject{{QStringLiteral("summary"), QStringLiteral("Inspect observed connection, capture, and capabilities")}, {QStringLiteral("security"), QJsonArray{QJsonObject{{QStringLiteral("ApiKey"), QJsonArray{}}}}}}}}},
                {QStringLiteral("/api/v1/devices/{id}/screenshot"), QJsonObject{{QStringLiteral("get"), QJsonObject{{QStringLiteral("summary"), QStringLiteral("Capture selected device as PNG")}, {QStringLiteral("security"), QJsonArray{QJsonObject{{QStringLiteral("ApiKey"), QJsonArray{}}}}}, {QStringLiteral("responses"), QJsonObject{{QStringLiteral("200"), QJsonObject{{QStringLiteral("description"), QStringLiteral("PNG from the active device")}, {QStringLiteral("content"), QJsonObject{{QStringLiteral("image/png"), QJsonObject{{QStringLiteral("schema"), QJsonObject{{QStringLiteral("type"), QStringLiteral("string")}, {QStringLiteral("format"), QStringLiteral("binary")}}}}}}}}}}}}}}}
            };
            paths.insert(QStringLiteral("/api/v1/devices/{id}/control/acquire"),
                QJsonObject{{QStringLiteral("post"), QJsonObject{
                    {QStringLiteral("summary"), QStringLiteral("Acquire or renew Android input lease")},
                    {QStringLiteral("security"), QJsonArray{QJsonObject{{QStringLiteral("ApiKey"), QJsonArray{}}}}},
                    {QStringLiteral("parameters"), QJsonArray{QJsonObject{
                        {QStringLiteral("name"), QStringLiteral("X-Control-Session")},
                        {QStringLiteral("in"), QStringLiteral("header")},
                        {QStringLiteral("required"), false}}}},
                    {QStringLiteral("requestBody"), QJsonObject{
                        {QStringLiteral("required"), true},
                        {QStringLiteral("content"), QJsonObject{{QStringLiteral("application/json"), QJsonObject{
                            {QStringLiteral("schema"), QJsonObject{{QStringLiteral("type"), QStringLiteral("object")},
                                {QStringLiteral("properties"), QJsonObject{{QStringLiteral("durationSeconds"), QJsonObject{
                                    {QStringLiteral("type"), QStringLiteral("integer")},
                                    {QStringLiteral("minimum"), 5}, {QStringLiteral("maximum"), 300},
                                    {QStringLiteral("default"), 30}}}}}}}}}}}}}}}});
            paths.insert(QStringLiteral("/api/v1/devices/{id}/control/release"),
                QJsonObject{{QStringLiteral("post"), QJsonObject{
                    {QStringLiteral("summary"), QStringLiteral("Release Android input lease")},
                    {QStringLiteral("security"), QJsonArray{QJsonObject{{QStringLiteral("ApiKey"), QJsonArray{}}}}},
                    {QStringLiteral("parameters"), QJsonArray{QJsonObject{
                        {QStringLiteral("name"), QStringLiteral("X-Control-Session")},
                        {QStringLiteral("in"), QStringLiteral("header")},
                        {QStringLiteral("required"), true}}}}}}});
            paths.insert(QStringLiteral("/api/v1/devices/{id}/input/{action}"),
                QJsonObject{{QStringLiteral("post"), QJsonObject{
                    {QStringLiteral("summary"), QStringLiteral("Dispatch Android tap, long-press, swipe, key, or text")},
                    {QStringLiteral("description"), QStringLiteral("Touch coordinates use deviceLogicalPixels from status.geometry; include geometryVersion. Android text is limited to safe ASCII/space characters, at most 256 characters; the literal %s sequence is rejected because Android treats it as a space escape.")},
                    {QStringLiteral("security"), QJsonArray{QJsonObject{{QStringLiteral("ApiKey"), QJsonArray{}}}}},
                    {QStringLiteral("parameters"), QJsonArray{QJsonObject{
                        {QStringLiteral("name"), QStringLiteral("X-Control-Session")},
                        {QStringLiteral("in"), QStringLiteral("header")},
                        {QStringLiteral("required"), true}}}}}}});
            const auto inputPath = QStringLiteral("/api/v1/devices/{id}/input/{action}");
            auto inputOperation = paths.value(inputPath).toObject()
                .value(QStringLiteral("post")).toObject();
            auto inputParameters = inputOperation.value(QStringLiteral("parameters")).toArray();
            inputParameters.push_back(QJsonObject{
                {QStringLiteral("name"), QStringLiteral("action")},
                {QStringLiteral("in"), QStringLiteral("path")},
                {QStringLiteral("required"), true},
                {QStringLiteral("schema"), QJsonObject{{QStringLiteral("type"), QStringLiteral("string")},
                    {QStringLiteral("enum"), QJsonArray{QStringLiteral("tap"),
                        QStringLiteral("long-press"), QStringLiteral("swipe"),
                        QStringLiteral("key"), QStringLiteral("text")}}}}});
            inputOperation.insert(QStringLiteral("parameters"), inputParameters);
            QJsonObject inputProperties{
                {QStringLiteral("x"), QJsonObject{{QStringLiteral("type"), QStringLiteral("number")}}},
                {QStringLiteral("y"), QJsonObject{{QStringLiteral("type"), QStringLiteral("number")}}},
                {QStringLiteral("x1"), QJsonObject{{QStringLiteral("type"), QStringLiteral("number")}}},
                {QStringLiteral("y1"), QJsonObject{{QStringLiteral("type"), QStringLiteral("number")}}},
                {QStringLiteral("x2"), QJsonObject{{QStringLiteral("type"), QStringLiteral("number")}}},
                {QStringLiteral("y2"), QJsonObject{{QStringLiteral("type"), QStringLiteral("number")}}},
                {QStringLiteral("duration"), QJsonObject{{QStringLiteral("type"), QStringLiteral("number")},
                    {QStringLiteral("minimum"), 0.05}, {QStringLiteral("maximum"), 10}}},
                {QStringLiteral("key"), QJsonObject{{QStringLiteral("type"), QStringLiteral("string")}}},
                {QStringLiteral("text"), QJsonObject{{QStringLiteral("type"), QStringLiteral("string")},
                    {QStringLiteral("maxLength"), 256}}},
                {QStringLiteral("geometryVersion"), QJsonObject{{QStringLiteral("type"), QStringLiteral("string")}}}};
            inputOperation.insert(QStringLiteral("requestBody"), QJsonObject{
                {QStringLiteral("required"), true},
                {QStringLiteral("content"), QJsonObject{{QStringLiteral("application/json"), QJsonObject{
                    {QStringLiteral("schema"), QJsonObject{{QStringLiteral("type"), QStringLiteral("object")},
                        {QStringLiteral("required"), QJsonArray{QStringLiteral("geometryVersion")}},
                        {QStringLiteral("properties"), inputProperties}}}}}}}});
            inputOperation.insert(QStringLiteral("responses"), QJsonObject{
                {QStringLiteral("202"), QJsonObject{{QStringLiteral("description"), QStringLiteral("ADB command dispatched")}}}});
            paths.insert(inputPath, QJsonObject{{QStringLiteral("post"), inputOperation}});
            QJsonObject security{{QStringLiteral("ApiKey"), QJsonObject{{QStringLiteral("type"), QStringLiteral("apiKey")}, {QStringLiteral("in"), QStringLiteral("header")}, {QStringLiteral("name"), QStringLiteral("X-API-Key")}}}};
            security.insert(QStringLiteral("ControlSession"), QJsonObject{{QStringLiteral("type"), QStringLiteral("apiKey")}, {QStringLiteral("in"), QStringLiteral("header")}, {QStringLiteral("name"), QStringLiteral("X-Control-Session")}});
            QJsonObject components{{QStringLiteral("securitySchemes"), security}};
            QJsonObject contract{{QStringLiteral("openapi"), QStringLiteral("3.1.0")},
                {QStringLiteral("info"), info}, {QStringLiteral("paths"), paths},
                {QStringLiteral("components"), components}};
            const auto body = QJsonDocument(contract).toJson(QJsonDocument::Compact);
            return "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: " + QByteArray::number(body.size()) + "\r\n\r\n" + body;
        }
    }
    const auto body = QJsonDocument(json).toJson(QJsonDocument::Compact);
    return "HTTP/1.1 " + httpStatus + "\r\nContent-Type: application/json\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: " + QByteArray::number(body.size()) + "\r\n\r\n" + body;
}

QString Backend::selectedLabel() const {
    for (const auto &entry : m_devices) {
        const auto item = entry.toMap();
        if (item.value(QStringLiteral("id")).toString() == m_selected)
            return item.value(QStringLiteral("label")).toString();
    }
    return {};
}

QVariantList Backend::androidQualityProfiles() const {
    return {
        device(QStringLiteral("original"), tr("Original · hasta 60 FPS · 8 Mbps")),
        device(QStringLiteral("high"), tr("Alta · máximo 1920 px · 60 FPS · 8 Mbps")),
        device(QStringLiteral("balanced"), tr("Equilibrada · máximo 1280 px · 60 FPS · 5 Mbps")),
        device(QStringLiteral("economy"), tr("Ahorro · máximo 960 px · 30 FPS · 3 Mbps"))
    };
}

QVariantList Backend::iosOutputProfiles() const {
    return {
        device(QStringLiteral("vertical"), tr("Vertical · 9:16 · 1080 × 1920")),
        device(QStringLiteral("horizontal"), tr("Horizontal · 16:9 · 1920 × 1080"))
    };
}

QVariantList Backend::iosProjectionModes() const {
    return {
        device(QStringLiteral("demo"), tr("Estándar · recomendado")),
        device(QStringLiteral("airplay"), tr("AirPlay por USB · experimental")),
        device(QStringLiteral("aisi"), tr("Aisi compatible · experimental"))
    };
}

int Backend::selectedIndex() const {
    for (qsizetype index = 0; index < m_devices.size(); ++index) {
        if (m_devices.at(index).toMap().value(QStringLiteral("id")).toString() == m_selected)
            return static_cast<int>(index);
    }
    return -1;
}

void Backend::setSelected(const QString &value) {
    if (value == m_selected) return;
    for (const auto &deviceId : m_automationInputInFlight)
        cancelAutomationInput(deviceId);
    m_selected = value;
    m_automationGeometry = {};
    m_automationGeometrySerial.clear();
    m_automationLeases.clear();
    if (m_recordingEnabled &&
        !m_selected.startsWith(QLatin1String(AndroidPrefix)) &&
        !m_selected.startsWith(QLatin1String(IosPrefix))) {
        m_recordingEnabled = false;
        emit recordingEnabledChanged();
    }
    emit selectedChanged();
    refreshDeviceHealth();
}

void Backend::refreshDeviceHealth() {
    const bool selectedIos = m_selected.startsWith(QLatin1String(IosPrefix)) &&
        selectedIndex() >= 0;
    const bool selectedAndroid = m_selected.startsWith(QLatin1String(AndroidPrefix)) &&
        selectedIndex() >= 0;
    if (!selectedIos) {
        m_iosHealthTimeout.stop();
        m_iosHealthPhase = -1;
        if (m_iosHealthProbe.state() != QProcess::NotRunning) m_iosHealthProbe.kill();
        m_iosHealthSerial.clear();
        m_iosHealthNextPollMs = 0;
        m_iosBatteryPercent = -1;
        m_iosBatteryCharging = -1;
        m_iosTemperatureC = -1.0;
    }
    if (!selectedAndroid) {
        m_androidHealthTimeout.stop();
        m_androidHealthPhase = -1;
        if (m_androidHealthProbe.state() != QProcess::NotRunning)
            m_androidHealthProbe.kill();
        m_androidHealthSerial.clear();
        m_androidHealthNextPollMs = 0;
        m_androidBatteryPercent = -1;
        m_androidBatteryCharging = -1;
        m_androidTemperatureC = -1.0;
    }
    if (selectedIos) {
        const QString prefix = QString::fromLatin1(IosPrefix);
        const QString serial = m_selected.mid(prefix.size());
        if (serial.isEmpty()) {
            updateDeviceHealthText();
            return;
        }
        if (serial != m_iosHealthSerial) {
            m_iosHealthTimeout.stop();
            m_iosHealthPhase = -1;
            if (m_iosHealthProbe.state() != QProcess::NotRunning)
                m_iosHealthProbe.kill();
            m_iosHealthNextPollMs = 0;
            m_iosBatteryPercent = -1;
            m_iosBatteryCharging = -1;
            m_iosTemperatureC = -1.0;
        }
        if (m_iosHealthProbe.state() == QProcess::NotRunning) {
            const auto nowMs = QDateTime::currentMSecsSinceEpoch();
            if (nowMs >= m_iosHealthNextPollMs) {
                m_iosHealthSerial = serial;
                m_iosHealthNextPollMs = nowMs + 60000;
                m_iosBatteryPercent = -1;
                m_iosBatteryCharging = -1;
                m_iosTemperatureC = -1.0;
                m_iosHealthPhase = 0;
                m_iosHealthProbe.setProgram(QStandardPaths::findExecutable(
                    QStringLiteral("ideviceinfo")));
                m_iosHealthProbe.setArguments({QStringLiteral("-u"), serial,
                    QStringLiteral("-q"), QStringLiteral("com.apple.mobile.battery")});
                m_iosHealthTimeout.start();
                m_iosHealthProbe.start();
            }
        }
    } else if (selectedAndroid) {
        const QString prefix = QString::fromLatin1(AndroidPrefix);
        const QString serial = m_selected.mid(prefix.size());
        if (serial.isEmpty()) {
            updateDeviceHealthText();
            return;
        }
        if (serial != m_androidHealthSerial) {
            m_androidHealthTimeout.stop();
            m_androidHealthPhase = -1;
            if (m_androidHealthProbe.state() != QProcess::NotRunning)
                m_androidHealthProbe.kill();
            m_androidHealthNextPollMs = 0;
            m_androidBatteryPercent = -1;
            m_androidBatteryCharging = -1;
            m_androidTemperatureC = -1.0;
        }
        if (m_androidHealthProbe.state() == QProcess::NotRunning) {
            const auto nowMs = QDateTime::currentMSecsSinceEpoch();
            if (nowMs >= m_androidHealthNextPollMs) {
                const auto adb = QStandardPaths::findExecutable(QStringLiteral("adb"));
                if (adb.isEmpty()) {
                    m_androidHealthPhase = -1;
                    updateDeviceHealthText();
                    return;
                }
                m_androidHealthSerial = serial;
                m_androidHealthNextPollMs = nowMs + 60000;
                m_androidHealthPhase = 0;
                m_androidHealthProbe.setProgram(adb);
                m_androidHealthProbe.setArguments({QStringLiteral("-s"), serial,
                    QStringLiteral("shell"), QStringLiteral("dumpsys"),
                    QStringLiteral("battery")});
                m_androidHealthTimeout.start();
                m_androidHealthProbe.start();
            }
        }
    }
    updateDeviceHealthText();
}

void Backend::updateDeviceHealthText() {
    QStringList values;
    const bool isIos = m_selected.startsWith(QLatin1String(IosPrefix));
    const bool isAndroid = m_selected.startsWith(QLatin1String(AndroidPrefix));
    const int batteryPercent = isIos ? m_iosBatteryPercent : m_androidBatteryPercent;
    const int batteryCharging = isIos ? m_iosBatteryCharging : m_androidBatteryCharging;
    const double temperatureC = isIos ? m_iosTemperatureC : m_androidTemperatureC;
    const int phase = isIos ? m_iosHealthPhase : m_androidHealthPhase;
    if (isIos || isAndroid) {
        if (batteryPercent >= 0) {
            const auto level = batteryPercent <= 10 && batteryCharging != 1
                ? tr("Batería crítica: %1 %")
                : (batteryPercent <= 20 && batteryCharging != 1
                    ? tr("Batería baja: %1 %") : tr("Batería %1 %"));
            values.push_back(level.arg(batteryPercent));
        }
        if (batteryCharging == 1) values.push_back(tr("cargando"));
        else if (batteryCharging == 2) values.push_back(tr("carga completa"));
        if (temperatureC > 0) {
            const auto temperature = temperatureC >= 45.0
                ? tr("Temperatura alta: %1 °C")
                : (temperatureC >= 40.0
                    ? tr("Temperatura elevada: %1 °C") : tr("Temperatura %1 °C"));
            values.push_back(temperature.arg(temperatureC, 0, 'f', 1));
        }
        if (values.isEmpty()) {
            if (phase == 0) values.push_back(tr("Consultando estado del dispositivo…"));
            else if (phase < 0) values.push_back(tr("Estado del dispositivo no disponible"));
        }
    }
    const bool warning = (batteryPercent >= 0 && batteryPercent <= 20 &&
                          batteryCharging != 1) || temperatureC >= 40.0;
    const QString text = values.join(QStringLiteral(" · "));
    if (m_deviceHealthText == text && m_deviceHealthWarning == warning) return;
    m_deviceHealthText = text;
    m_deviceHealthWarning = warning;
    emit deviceHealthChanged();
}

void Backend::setOutputDevice(const QString &value) {
    const bool available = std::any_of(m_outputDevices.cbegin(), m_outputDevices.cend(),
        [&value](const QVariant &entry) {
            return entry.toMap().value(QStringLiteral("id")).toString() == value;
        });
    if (!available || value == m_outputDevice) return;
    m_outputDevice = value;
    emit outputDeviceChanged();
}

void Backend::setAudioEnabled(bool enabled) {
    if (enabled == m_audioEnabled) return;
    m_audioEnabled = enabled;
    QSettings().setValue(QStringLiteral("audioEnabled"), enabled);
    emit audioEnabledChanged();
}

void Backend::setMicrophoneEnabled(bool enabled) {
    if (enabled == m_microphoneEnabled) return;
    m_microphoneEnabled = enabled;
    QSettings().setValue(QStringLiteral("microphoneEnabled"), enabled);
    emit microphoneEnabledChanged();
}

void Backend::setReverseControlEnabled(bool enabled) {
    if (running() || enabled == m_reverseControlEnabled ||
        !m_selected.startsWith(QLatin1String(IosPrefix))) return;
    m_reverseControlEnabled = enabled;
    QSettings().setValue(QStringLiteral("reverseControlEnabled"), enabled);
    emit reverseControlEnabledChanged();
}

void Backend::setRecordingEnabled(bool enabled) {
    if (running() || enabled == m_recordingEnabled) return;
    if (enabled && !m_selected.startsWith(QLatin1String(AndroidPrefix)) &&
        !m_selected.startsWith(QLatin1String(IosPrefix))) return;
    m_recordingEnabled = enabled;
    emit recordingEnabledChanged();
}

void Backend::setAndroidQualityProfile(const QString &profile) {
    if (running() || profile == m_androidQualityProfile) return;
    static const QStringList allowed{QStringLiteral("original"), QStringLiteral("high"),
                                     QStringLiteral("balanced"), QStringLiteral("economy")};
    if (!allowed.contains(profile)) return;
    m_androidQualityProfile = profile;
    QSettings().setValue(QStringLiteral("androidQualityProfile"), profile);
    emit androidQualityProfileChanged();
}

void Backend::setIosOutputProfile(const QString &profile) {
    if (running() || profile == m_iosOutputProfile) return;
    if (profile != QLatin1String("vertical") &&
        profile != QLatin1String("horizontal")) return;
    m_iosOutputProfile = profile;
    QSettings().setValue(QStringLiteral("iosOutputProfile"), profile);
    emit iosOutputProfileChanged();
}

void Backend::setIosProjectionMode(const QString &mode) {
    if (running() || mode == m_iosProjectionMode) return;
    if (mode != QLatin1String("demo") && mode != QLatin1String("airplay") &&
        mode != QLatin1String("aisi")) return;
    m_iosProjectionMode = mode;
    QSettings().setValue(QStringLiteral("iosProjectionMode"), mode);
    emit iosProjectionModeChanged();
}

void Backend::setStatus(const QString &value) {
    if (value == m_status) return;
    m_status = value;
    emit statusChanged();
}

void Backend::setCaptureFps(double fps) {
    if (m_captureFps == fps) return;
    m_captureFps = fps;
    emit captureFpsChanged();
}

QString Backend::diagnosticsText() const {
    const auto checkExecutable = [](const QString &name) {
        return QStandardPaths::findExecutable(name).isEmpty()
            ? QObject::tr("No encontrado") : QObject::tr("Disponible");
    };
    const auto checkFile = [](const QString &path) {
        const QFileInfo info(path);
        return info.exists() ? QObject::tr("Disponible") : QObject::tr("No encontrado");
    };
    const auto runtime = QStandardPaths::writableLocation(QStandardPaths::RuntimeLocation);
    const QString pipewireSocket = QDir(runtime).filePath(QStringLiteral("pipewire-0"));
    const QString waylandSocket = QString::fromLocal8Bit(qgetenv("WAYLAND_DISPLAY"));
    const QString displayServer = !waylandSocket.isEmpty()
        ? tr("Wayland (%1)").arg(QFileInfo(waylandSocket).fileName())
        : (qEnvironmentVariableIsEmpty("DISPLAY") ? tr("No detectado") : tr("X11"));

    QStringList lines{
        tr("iPhoneMirror · diagnóstico local"),
        tr("Versión de interfaz: %1").arg(QCoreApplication::applicationVersion()),
        tr("Sistema: %1 · %2").arg(QSysInfo::prettyProductName(), QSysInfo::currentCpuArchitecture()),
        tr("Sesión gráfica: %1").arg(displayServer),
        QString{},
        tr("Dependencias"),
        tr("idevice_id (libimobiledevice): %1").arg(checkExecutable(QStringLiteral("idevice_id"))),
        tr("adb (android-tools): %1").arg(checkExecutable(QStringLiteral("adb"))),
        tr("scrcpy: %1").arg(checkExecutable(QStringLiteral("scrcpy"))),
        tr("uxplay (AirPlay por Wi-Fi): %1").arg(checkExecutable(QStringLiteral("uxplay"))),
        tr("dotnet: %1").arg(checkExecutable(QStringLiteral("dotnet"))),
        tr("ffmpeg: %1").arg(checkExecutable(QStringLiteral("ffmpeg"))),
        tr("Regla udev de iPhoneMirror: %1").arg(checkFile(
            QStringLiteral("/usr/lib/udev/rules.d/70-iphonemirror.rules"))),
        tr("Núcleo Linux instalado: %1").arg(checkFile(
            QStringLiteral("/usr/lib/iphoneusb/libiPhoneMirror.Core.so"))),
        QString{},
        tr("Servicios y salida"),
        tr("Socket PipeWire: %1").arg(checkFile(pipewireSocket)),
        tr("Dispositivos V4L2 virtuales disponibles en la app: %1")
            .arg(qMax(0, m_outputDevices.size() - 1)),
        tr("Dispositivos detectados: %1 (no se incluyen sus nombres ni identificadores)")
            .arg(m_devices.size()),
        tr("Captura: %1").arg(running() ? tr("Activa") : tr("Detenida")),
        QString{},
        tr("El informe no incluye números de serie, direcciones IP ni códigos de emparejamiento.")
    };
    return lines.join(QLatin1Char('\n'));
}

void Backend::copyDiagnostics() const {
    if (auto *clipboard = QGuiApplication::clipboard())
        clipboard->setText(diagnosticsText());
}

void Backend::refresh() {
    if (running() || m_refreshActive || m_androidWirelessPhase != 0) return;
    refreshOutputDevices();
    m_refreshActive = true;
    m_probeTimedOut = false;
    m_androidUnauthorized = false;
    const auto ideviceId = QStandardPaths::findExecutable(QStringLiteral("idevice_id"));
    const auto adb = QStandardPaths::findExecutable(QStringLiteral("adb"));
    m_iosMissing = ideviceId.isEmpty();
    m_androidMissing = adb.isEmpty();
    if (!m_iosMissing) {
        m_iosProbe.setProgram(ideviceId);
        m_iosProbe.setArguments({QStringLiteral("-l")});
        m_iosProbe.start();
    }
    if (!m_androidMissing) {
        m_androidProbe.setProgram(adb);
        m_androidProbe.setArguments({QStringLiteral("devices"), QStringLiteral("-l")});
        m_androidProbe.start();
    }
    setStatus(tr("Buscando dispositivos…"));
    m_probeTimeout.start();
    finishRefreshIfReady();
}

bool Backend::pairAndroid(const QString &pairAddress, const QString &pairCode,
                          const QString &connectionAddress) {
    if (running() || m_androidWirelessPhase != 0) {
        setStatus(tr("Detén la duplicación o espera a que termine la conexión actual"));
        return false;
    }
    if (!validAdbEndpoint(pairAddress) || !validAdbEndpoint(connectionAddress) ||
        !QRegularExpression(QStringLiteral("^[0-9]{6}$")).match(pairCode).hasMatch()) {
        setStatus(tr("Revisa la IP, los puertos y el código de seis dígitos"));
        return false;
    }
    const auto adb = QStandardPaths::findExecutable(QStringLiteral("adb"));
    if (adb.isEmpty()) {
        setStatus(tr("Instala android-tools para emparejar por Wi-Fi"));
        return false;
    }
    m_androidPairAddress = pairAddress.trimmed();
    m_androidConnectionAddress = connectionAddress.trimmed();
    m_androidPairCode = pairCode;
    m_androidWirelessPhase = 1;
    m_androidWirelessTimedOut = false;
    m_androidWireless.setProgram(adb);
    m_androidWireless.setArguments({QStringLiteral("pair"), m_androidPairAddress});
    m_androidWireless.start();
    m_androidWirelessTimeout.start();
    setStatus(tr("Emparejando Android por Wi-Fi…"));
    return true;
}

bool Backend::connectAndroidLegacy(const QString &ipAddress) {
    if (running() || m_androidWirelessPhase != 0) {
        setStatus(tr("Detén la duplicación o espera a que termine la conexión actual"));
        return false;
    }
    if (!m_selected.startsWith(QLatin1String(AndroidPrefix))) {
        setStatus(tr("Selecciona el Android conectado por USB para prepararlo"));
        return false;
    }
    const auto adb = QStandardPaths::findExecutable(QStringLiteral("adb"));
    if (adb.isEmpty()) {
        setStatus(tr("Instala android-tools para conectar Android por Wi-Fi"));
        return false;
    }
    const auto host = ipAddress.trimmed();
    QHostAddress address;
    if (!address.setAddress(host)) {
        setStatus(tr("Escribe una dirección IP válida del teléfono"));
        return false;
    }
    const auto endpoint = address.protocol() == QAbstractSocket::IPv6Protocol
        ? QStringLiteral("[%1]:5555").arg(host)
        : QStringLiteral("%1:5555").arg(host);
    m_androidConnectionAddress = endpoint;
    m_androidPairCode.clear();
    m_androidWirelessTimedOut = false;
    if (m_selected.mid(static_cast<qsizetype>(QString::fromLatin1(AndroidPrefix).size()))
            .contains(QLatin1Char(':'))) {
        m_androidWirelessPhase = 3;
        m_androidWireless.setProgram(adb);
        m_androidWireless.setArguments({QStringLiteral("connect"), endpoint});
    } else {
        m_androidWirelessPhase = 4;
        const auto serial = m_selected.mid(
            static_cast<qsizetype>(QString::fromLatin1(AndroidPrefix).size()));
        m_androidWireless.setProgram(adb);
        m_androidWireless.setArguments({QStringLiteral("-s"), serial,
                                        QStringLiteral("tcpip"), QStringLiteral("5555")});
    }
    m_androidWireless.start();
    m_androidWirelessTimeout.start();
    setStatus(m_androidWirelessPhase == 4
        ? tr("Preparando Android para ADB TCP/IP…")
        : tr("Conectando Android por Wi-Fi…"));
    return true;
}

void Backend::setAndroidOutputReady(bool ready) {
    if (m_androidOutputReady == ready) return;
    m_androidOutputReady = ready;
    emit androidOutputReadyChanged();
}

bool Backend::startAndroidAudioRoute() {
    stopAndroidAudioRoute();
    if (!m_audioEnabled || m_outputDevice.isEmpty()) return false;

    const auto pactl = QStandardPaths::findExecutable(QStringLiteral("pactl"));
    if (pactl.isEmpty()) return false;
    QProcess pulseProbe;
    pulseProbe.setProgram(pactl);
    pulseProbe.setArguments({QStringLiteral("info")});
    pulseProbe.setStandardOutputFile(QProcess::nullDevice());
    pulseProbe.setStandardErrorFile(QProcess::nullDevice());
    pulseProbe.start();
    if (!pulseProbe.waitForFinished(800) || pulseProbe.exitCode() != 0) return false;

    const auto loopback = QStandardPaths::findExecutable(QStringLiteral("pw-loopback"));
    if (loopback.isEmpty()) return false;
    m_androidAudioSinkName = QStringLiteral("iphonemirror_android_%1")
        .arg(QCoreApplication::applicationPid());
    const auto playbackName = QStringLiteral("playback.") + m_androidAudioSinkName;
    const QJsonObject captureProperties{
        {QStringLiteral("node.name"), m_androidAudioSinkName},
        {QStringLiteral("node.description"), QStringLiteral("iPhoneMirror · audio Android")},
        {QStringLiteral("media.class"), QStringLiteral("Audio/Sink")},
        {QStringLiteral("audio.position"), QJsonArray{QStringLiteral("FL"), QStringLiteral("FR")}},
    };
    const QJsonObject playbackProperties{
        {QStringLiteral("node.name"), playbackName},
        {QStringLiteral("audio.position"), QJsonArray{QStringLiteral("FL"), QStringLiteral("FR")}},
        {QStringLiteral("node.passive"), true},
        {QStringLiteral("stream.dont-remix"), true},
    };
    m_androidAudioLoopback.setProgram(loopback);
    m_androidAudioLoopback.setArguments({
        QStringLiteral("--name"), m_androidAudioSinkName,
        QStringLiteral("--capture-props"), QString::fromUtf8(
            QJsonDocument(captureProperties).toJson(QJsonDocument::Compact)),
        QStringLiteral("--playback-props"), QString::fromUtf8(
            QJsonDocument(playbackProperties).toJson(QJsonDocument::Compact)),
    });
    m_androidAudioLoopback.setStandardOutputFile(QProcess::nullDevice());
    m_androidAudioLoopback.setStandardErrorFile(QProcess::nullDevice());
    m_androidAudioLoopback.start();
    if (!m_androidAudioLoopback.waitForStarted(800)) {
        m_androidAudioSinkName.clear();
        return false;
    }
    QElapsedTimer readyWindow;
    readyWindow.start();
    while (readyWindow.elapsed() < 300 &&
           m_androidAudioLoopback.state() != QProcess::NotRunning)
        QThread::msleep(20);
    if (m_androidAudioLoopback.state() == QProcess::NotRunning) {
        m_androidAudioSinkName.clear();
        return false;
    }
    if (!m_androidAudioRouteAvailable) {
        m_androidAudioRouteAvailable = true;
        emit androidAudioRouteAvailableChanged();
    }
    return true;
}

void Backend::stopAndroidAudioRoute() {
    if (m_androidAudioLoopback.state() != QProcess::NotRunning) {
        m_androidAudioLoopback.terminate();
        if (!m_androidAudioLoopback.waitForFinished(500)) {
            m_androidAudioLoopback.kill();
            m_androidAudioLoopback.waitForFinished(500);
        }
    }
    m_androidAudioSinkName.clear();
    if (m_androidAudioRouteAvailable) {
        m_androidAudioRouteAvailable = false;
        emit androidAudioRouteAvailableChanged();
    }
}

void Backend::refreshOutputDevices() {
    QVariantList outputs{device(QString{}, tr("Vista previa en ventana"))};
    const QDir videoClass(QStringLiteral("/sys/class/video4linux"));
    const auto entries = videoClass.entryList({QStringLiteral("video*")}, QDir::Dirs);
    for (const auto &entry : entries) {
        const auto sysfsPath = QFileInfo(videoClass.filePath(entry)).canonicalFilePath();
        QFile nameFile(videoClass.filePath(entry + QStringLiteral("/name")));
        if (!nameFile.open(QIODevice::ReadOnly | QIODevice::Text)) continue;
        const auto name = QString::fromLocal8Bit(nameFile.readAll()).trimmed();
        const auto devicePath = QStringLiteral("/dev/") + entry;
        // v4l2loopback devices live under /devices/virtual/video4linux and
        // expose max_openers in sysfs. This identifies the driver even when
        // its card label is customized, while excluding physical cameras.
        const bool loopbackDriver = sysfsPath.contains(
                QStringLiteral("/devices/virtual/video4linux/")) &&
            QFileInfo(videoClass.filePath(entry + QStringLiteral("/max_openers"))).exists();
        const bool knownLegacyName = name.contains(QLatin1String(V4l2LoopbackName),
                                                   Qt::CaseInsensitive);
        if ((!loopbackDriver && !knownLegacyName) ||
            !sysfsPath.contains(QStringLiteral("/devices/virtual/video4linux/")) ||
            !QFileInfo(devicePath).isWritable()) continue;
        outputs.push_back(device(devicePath,
            tr("Cámara virtual · %1 (%2)").arg(entry, name)));
    }
    if (outputs == m_outputDevices) return;
    m_outputDevices = std::move(outputs);
    bool selectionStillAvailable = false;
    for (const auto &output : m_outputDevices) {
        if (output.toMap().value(QStringLiteral("id")).toString() == m_outputDevice) {
            selectionStillAvailable = true;
            break;
        }
    }
    if (!selectionStillAvailable) {
        m_outputDevice.clear();
        emit outputDeviceChanged();
    }
    emit outputDevicesChanged();
}

void Backend::finishRefreshIfReady() {
    if (!m_refreshActive || m_iosProbe.state() != QProcess::NotRunning ||
        m_androidProbe.state() != QProcess::NotRunning) return;
    m_probeTimeout.stop();
    m_refreshActive = false;
    applyRefreshResults();
}

void Backend::applyRefreshResults() {
    QVariantList discovered;
    const bool iosProbeSucceeded = !m_iosMissing &&
        m_iosProbe.exitStatus() == QProcess::NormalExit && m_iosProbe.exitCode() == 0;
    const bool androidProbeSucceeded = !m_androidMissing &&
        m_androidProbe.exitStatus() == QProcess::NormalExit &&
        m_androidProbe.exitCode() == 0;
    m_iosDeviceListFresh = iosProbeSucceeded;
    m_androidDeviceListFresh = androidProbeSucceeded;
    if (iosProbeSucceeded) {
        const auto serials = QString::fromLocal8Bit(m_iosProbe.readAllStandardOutput())
            .split(QRegularExpression(QStringLiteral("[\\r\\n]+")), Qt::SkipEmptyParts);
        qsizetype deviceNumber = 0;
        const auto totalIosDevices = static_cast<qsizetype>(std::count_if(
            serials.cbegin(), serials.cend(), [](const QString &serial) {
                return !serial.trimmed().isEmpty();
            }));
        for (const auto &serial : serials) {
            const auto normalized = serial.trimmed();
            if (!normalized.isEmpty()) {
                ++deviceNumber;
                const auto id = QString::fromLatin1(IosPrefix) + normalized;
                const auto name = m_iosDeviceNames.value(id);
                discovered.push_back(device(id, name.isEmpty()
                    ? (totalIosDevices > 1
                        ? tr("iPhone o iPad %1").arg(deviceNumber)
                        : tr("iPhone o iPad"))
                    : tr("%1 · iPhone o iPad").arg(name)));
            }
        }
    }
    if (androidProbeSucceeded) {
        const auto lines = QString::fromLocal8Bit(m_androidProbe.readAllStandardOutput())
            .split(QRegularExpression(QStringLiteral("[\\r\\n]+")), Qt::SkipEmptyParts);
        for (qsizetype index = 1; index < lines.size(); ++index) {
            const auto fields = lines.at(index).simplified().split(QLatin1Char(' '),
                                                                    Qt::SkipEmptyParts);
            if (fields.size() < 2) continue;
            const auto serial = fields.at(0);
            const auto state = fields.at(1);
            if (state == QStringLiteral("unauthorized")) {
                m_androidUnauthorized = true;
                continue;
            }
            if (state != QStringLiteral("device")) continue;
            QString displayName;
            for (qsizetype fieldIndex = 2; fieldIndex < fields.size(); ++fieldIndex) {
                const auto &field = fields.at(fieldIndex);
                if (field.startsWith(QStringLiteral("model:"))) {
                    displayName = field.mid(6).replace(QLatin1Char('_'), QLatin1Char(' '));
                    break;
                }
            }
            if (displayName.isEmpty()) displayName = tr("Dispositivo Android");
            const auto connection = serial.contains(QLatin1Char(':'))
                ? tr("Wi-Fi") : tr("USB");
            discovered.push_back(device(QString::fromLatin1(AndroidPrefix) + serial,
                tr("%1 · %2").arg(displayName, connection)));
        }
    }
    // A transient probe failure is not evidence that the phone was unplugged.
    // Keep that platform's last known rows while applying fresh results from
    // the other probe, so a slow ADB/libimobiledevice response cannot make the
    // device selector flicker or discard a useful selection.
    for (const auto &entry : m_devices) {
        const auto id = entry.toMap().value(QStringLiteral("id")).toString();
        const bool preserveIos = id.startsWith(QLatin1String(IosPrefix)) &&
            !m_iosMissing && !iosProbeSucceeded;
        const bool preserveAndroid = id.startsWith(QLatin1String(AndroidPrefix)) &&
            !m_androidMissing && !androidProbeSucceeded;
        if (preserveIos || preserveAndroid) discovered.push_back(entry);
    }
    const bool changed = discovered != m_devices;
    m_devices = std::move(discovered);
    QSet<QString> currentIosDevices;
    for (const auto &entry : m_devices) {
        const auto id = entry.toMap().value(QStringLiteral("id")).toString();
        if (id.startsWith(QLatin1String(IosPrefix))) currentIosDevices.insert(id);
    }
    for (auto it = m_iosDeviceNames.begin(); it != m_iosDeviceNames.end();) {
        if (!currentIosDevices.contains(it.key())) it = m_iosDeviceNames.erase(it);
        else ++it;
    }
    for (auto it = m_iosMetadataAttempted.begin();
         it != m_iosMetadataAttempted.end();) {
        if (!currentIosDevices.contains(*it)) it = m_iosMetadataAttempted.erase(it);
        else ++it;
    }
    bool selectionChanged = false;
    if (selectedIndex() < 0) {
        const QString next = m_devices.isEmpty()
            ? QString{} : m_devices.front().toMap().value(QStringLiteral("id")).toString();
        selectionChanged = next != m_selected;
        m_selected = next;
        if (m_recordingEnabled &&
            !m_selected.startsWith(QLatin1String(AndroidPrefix)) &&
            !m_selected.startsWith(QLatin1String(IosPrefix))) {
            m_recordingEnabled = false;
            emit recordingEnabledChanged();
        }
    }
    if (changed) emit devicesChanged();
    if (selectionChanged || changed) emit selectedChanged();

    const auto ideviceInfo = QStandardPaths::findExecutable(
        QStringLiteral("ideviceinfo"));
    if (!ideviceInfo.isEmpty()) {
        for (const auto &entry : m_devices) {
            const auto id = entry.toMap().value(QStringLiteral("id")).toString();
            if (!id.startsWith(QLatin1String(IosPrefix)) ||
                m_iosMetadataAttempted.contains(id)) continue;
            const auto serial = id.mid(static_cast<qsizetype>(
                QString::fromLatin1(IosPrefix).size()));
            probeIosDeviceName(id, serial);
        }
    }

    if (m_probeTimedOut) {
        setStatus(m_devices.isEmpty()
            ? tr("La búsqueda tardó demasiado; vuelve a actualizar dispositivos")
            : tr("No se pudo actualizar toda la lista; se conserva el último resultado"));
    } else if (!m_devices.isEmpty()) {
        setStatus(tr("Dispositivo listo para duplicar"));
    } else if (m_androidUnauthorized) {
        setStatus(tr("Autoriza esta computadora en la pantalla del dispositivo Android"));
    } else if (m_iosMissing && m_androidMissing) {
        setStatus(tr("Instala libimobiledevice y android-tools para detectar teléfonos"));
    } else {
        setStatus(tr("Conecta un iPhone/iPad o activa la depuración USB en Android"));
    }
    refreshDeviceHealth();
}

void Backend::probeIosDeviceName(const QString &id, const QString &serial) {
    m_iosMetadataAttempted.insert(id);
    auto *probe = new QProcess(this);
    probe->setProgram(QStandardPaths::findExecutable(QStringLiteral("ideviceinfo")));
    probe->setArguments({QStringLiteral("-u"), serial, QStringLiteral("-k"),
                         QStringLiteral("DeviceName")});
    connect(probe, &QProcess::finished, this,
            [this, probe, id](int code, QProcess::ExitStatus exitStatus) {
        const auto rawName = QString::fromLocal8Bit(
            probe->readAllStandardOutput()).simplified();
        probe->deleteLater();
        if (code != 0 || exitStatus != QProcess::NormalExit || rawName.isEmpty()) {
            QTimer::singleShot(15000, this, [this, id] {
                bool stillConnected = false;
                for (const auto &entry : m_devices) {
                    if (entry.toMap().value(QStringLiteral("id")).toString() == id) {
                        stillConnected = true;
                        break;
                    }
                }
                if (!stillConnected || m_iosDeviceNames.contains(id)) return;
                m_iosMetadataAttempted.remove(id);
                const auto serial = id.mid(static_cast<qsizetype>(
                    QString::fromLatin1(IosPrefix).size()));
                probeIosDeviceName(id, serial);
            });
            return;
        }
        QString name;
        name.reserve(qMin(rawName.size(), 64));
        for (const auto character : rawName) {
            if (!character.isPrint() || character.category() == QChar::Other_Format)
                continue;
            if (name.size() == 64) break;
            name.append(character);
        }
        name = name.simplified();
        if (name.isEmpty()) return;
        m_iosDeviceNames.insert(id, name);
        for (auto &entry : m_devices) {
            auto item = entry.toMap();
            if (item.value(QStringLiteral("id")).toString() != id) continue;
            item.insert(QStringLiteral("label"),
                        tr("%1 · iPhone o iPad").arg(name));
            entry = item;
            emit devicesChanged();
            if (m_selected == id) emit selectedChanged();
            break;
        }
    });
    connect(probe, &QProcess::errorOccurred, this,
            [probe](QProcess::ProcessError error) {
        if (error == QProcess::FailedToStart) probe->deleteLater();
    });
    probe->start();
    QTimer::singleShot(5000, probe, [probe] {
        if (probe->state() != QProcess::NotRunning) probe->kill();
    });
}

void Backend::start() {
    if (running()) {
        if (m_androidPreviewClosing)
            setStatus(tr("Finalizando la vista previa Android anterior; espera un momento antes de iniciar otra sesión"));
        return;
    }
    setCaptureFps(-1.0);
    m_androidPreviewClosing = false;
    setReverseControlConnected(false);
    m_outputFailed = false;
    m_mirrorStderrBuffer.clear();
    m_lastRecordingPath.clear();
    if (m_selected.startsWith(QLatin1String(AndroidPrefix))) {
        setAndroidOutputReady(false);
        const auto scrcpy = QStandardPaths::findExecutable(QStringLiteral("scrcpy"));
        if (scrcpy.isEmpty()) {
            setStatus(tr("Instala scrcpy para duplicar dispositivos Android"));
            return;
        }
        const auto serial = m_selected.mid(static_cast<qsizetype>(QString::fromLatin1(AndroidPrefix).size()));
        QString recordingPath;
        if (m_recordingEnabled) {
            auto pictures = QStandardPaths::writableLocation(QStandardPaths::PicturesLocation);
            if (pictures.isEmpty())
                pictures = QDir::home().filePath(QStringLiteral("Pictures"));
            const auto directory = QDir(pictures).filePath(QStringLiteral("iPhoneMirror"));
            if (!QDir().mkpath(directory)) {
                setStatus(tr("No se pudo crear la carpeta para la grabación"));
                return;
            }
            const auto timestamp = QDateTime::currentDateTime().toString(
                QStringLiteral("yyyyMMdd-HHmmss-zzz"));
            recordingPath = QDir(directory).filePath(
                QStringLiteral("grabacion-android-%1.mkv").arg(timestamp));
            m_recordingPath = recordingPath;
            m_recordingStopRequested = false;
            m_recordingStopTimedOut = false;
        }
        m_mirror.setProgram(scrcpy);
        // Keep Android's mirror as a clean source for OBS: no desktop titlebar,
        // and cap capture at the same 60 FPS ceiling as the iPhone preview.
        // scrcpy preserves the device aspect ratio and follows display rotation.
        QStringList arguments{QStringLiteral("-s"), serial,
            QStringLiteral("--window-title=iPhoneMirror · Android")};
        if (m_androidQualityProfile == QLatin1String("original")) {
            arguments << QStringLiteral("--max-fps=60");
        }
        if (m_androidQualityProfile == QLatin1String("high")) {
            arguments << QStringLiteral("--max-size=1920")
                      << QStringLiteral("--max-fps=60")
                      << QStringLiteral("--video-bit-rate=8M");
        } else if (m_androidQualityProfile == QLatin1String("balanced")) {
            arguments << QStringLiteral("--max-size=1280")
                      << QStringLiteral("--max-fps=60")
                      << QStringLiteral("--video-bit-rate=5M");
        } else if (m_androidQualityProfile == QLatin1String("economy")) {
            arguments << QStringLiteral("--max-size=960")
                      << QStringLiteral("--max-fps=30")
                      << QStringLiteral("--video-bit-rate=3M");
        }
        if (!recordingPath.isEmpty())
            arguments.push_back(QStringLiteral("--record=") + recordingPath);
        if (m_outputDevice.isEmpty()) {
            arguments.push_back(QStringLiteral("--window-borderless"));
        } else {
            arguments.push_back(QStringLiteral("--v4l2-sink=") + m_outputDevice);
            arguments.push_back(QStringLiteral("--no-video-playback"));
        }
        if (!m_audioEnabled) arguments.push_back(QStringLiteral("--no-audio"));
        m_mirror.setArguments(arguments);
        startAndroidAudioRoute();
        auto environment = QProcessEnvironment::systemEnvironment();
        if (m_androidAudioRouteAvailable) {
            environment.insert(QStringLiteral("SDL_AUDIODRIVER"), QStringLiteral("pulseaudio"));
            environment.insert(QStringLiteral("PULSE_SINK"), m_androidAudioSinkName);
        }
        m_mirror.setProcessEnvironment(environment);
        m_mirror.setStandardOutputFile(QProcess::nullDevice());
        m_mirror.start();
        setStatus(recordingPath.isEmpty()
            ? tr("Iniciando duplicación de Android…")
            : tr("Iniciando duplicación y grabación de Android…"));
        return;
    }

    if (!m_selected.startsWith(QLatin1String(IosPrefix))) {
        setStatus(tr("Selecciona un dispositivo conectado"));
        return;
    }
    const QString serial = m_selected.mid(static_cast<qsizetype>(QString::fromLatin1(IosPrefix).size()));
    const QString shell = QStringLiteral("/usr/lib/iphoneusb/iPhoneMirror.LinuxShell.dll");
    const QString core = QStringLiteral("/usr/lib/iphoneusb/libiPhoneMirror.Core.so");
    m_mirror.setProgram(QStandardPaths::findExecutable("dotnet"));
    auto arguments = QStringList{shell, QStringLiteral("--device"), serial};
    QString recordingPath;
    if (m_recordingEnabled) {
        auto pictures = QStandardPaths::writableLocation(QStandardPaths::PicturesLocation);
        if (pictures.isEmpty()) pictures = QDir::home().filePath(QStringLiteral("Pictures"));
        const auto directory = QDir(pictures).filePath(QStringLiteral("iPhoneMirror"));
        if (!QDir().mkpath(directory)) {
            setStatus(tr("No se pudo crear la carpeta para la grabación"));
            return;
        }
        const auto timestamp = QDateTime::currentDateTime().toString(
            QStringLiteral("yyyyMMdd-HHmmss-zzz"));
        recordingPath = QDir(directory).filePath(
            QStringLiteral("grabacion-iphone-%1.mp4").arg(timestamp));
        m_recordingPath = recordingPath;
        m_recordingStopRequested = false;
        m_recordingStopTimedOut = false;
        arguments.push_back(QStringLiteral("--record=") + recordingPath);
    }
    if (!m_audioEnabled) arguments.push_back(QStringLiteral("--no-audio"));
    const int projectionMode = m_iosProjectionMode == QLatin1String("airplay") ? 1
        : m_iosProjectionMode == QLatin1String("aisi") ? 2 : 0;
    arguments.push_back(QStringLiteral("--projection-mode=%1").arg(projectionMode));
    if (!m_outputDevice.isEmpty()) {
        arguments.push_back(QStringLiteral("--v4l2-sink=") + m_outputDevice);
        arguments.push_back(QStringLiteral("--output-profile=") + m_iosOutputProfile);
    }
    m_mirror.setArguments(arguments);
    m_mirror.setStandardOutputFile(QProcess::nullDevice());
    auto env = QProcessEnvironment::systemEnvironment();
    const auto old = env.value("LD_LIBRARY_PATH");
    env.insert("LD_LIBRARY_PATH", core.left(core.lastIndexOf('/')) + (old.isEmpty() ? "" : ":" + old));
    env.insert("IPHONEMIRROR_REVERSE_CONTROL",
        m_reverseControlEnabled ? QStringLiteral("1") : QStringLiteral("0"));
    m_mirror.setProcessEnvironment(env);
    m_mirror.start();
    setStatus(m_outputDevice.isEmpty()
        ? tr("Iniciando duplicación de iPhone…")
        : tr("Iniciando duplicación de iPhone para OBS…"));
}

void Backend::stop() {
    setReverseControlConnected(false);
    for (const auto &deviceId : m_automationInputInFlight)
        cancelAutomationInput(deviceId);
    m_automationLeases.clear();
    if (!running()) return;
    if (m_airplay.state() != QProcess::NotRunning) {
        setStatus(tr("Deteniendo el receptor AirPlay…"));
        m_airplay.terminate();
        m_mirrorStopTimeout.start(5000);
        return;
    }
    if (m_mirror.state() == QProcess::NotRunning &&
        m_androidPreview.state() != QProcess::NotRunning) {
        setStatus(tr("Finalizando la vista previa y transmisión Android…"));
        m_androidPreviewClosing = true;
        m_androidPreview.terminate();
        m_mirrorStopTimeout.start(m_mediaStreamingActive ? 18000 : 5000);
        return;
    }
    if (!m_recordingPath.isEmpty()) {
        if (m_selected.startsWith(QLatin1String(IosPrefix))) {
            m_recordingStopRequested = true;
            m_recordingStopTimedOut = false;
            setStatus(tr("Finalizando la grabación…"));
            m_mirror.terminate();
            m_mirrorStopTimeout.start(125000);
            return;
        }
        const auto pid = m_mirror.processId();
        if (pid > 0 && ::kill(static_cast<pid_t>(pid), SIGINT) == 0) {
            m_recordingStopRequested = true;
            m_recordingStopTimedOut = false;
            setStatus(tr("Finalizando la grabación de Android…"));
            m_mirrorStopTimeout.start(m_mediaStreamingActive ? 18000 : 5000);
            return;
        }
    }
    m_mirror.terminate();
    m_mirrorStopTimeout.start(m_mediaStreamingActive &&
        m_androidPreview.state() != QProcess::NotRunning ? 18000 : 5000);
}

bool Backend::startAirPlayReceiver() {
    if (running()) return false;
    const auto uxplay = QStandardPaths::findExecutable(QStringLiteral("uxplay"));
    if (uxplay.isEmpty()) {
        setStatus(tr("Instala UxPlay desde AUR y sus plugins GStreamer para recibir AirPlay por Wi-Fi"));
        return false;
    }
    m_airplay.setProgram(uxplay);
    // Use a predictable port range so users can allow only the AirPlay receiver
    // through a host firewall. UxPlay otherwise picks dynamic session ports.
    m_airplay.setArguments({QStringLiteral("-n"), QStringLiteral("iPhoneMirror"),
                            QStringLiteral("-p"), QStringLiteral("7000")});
    m_airplay.start();
    setStatus(tr("Iniciando receptor AirPlay por Wi-Fi…"));
    return true;
}

bool Backend::startStreaming(const QString &kind, const QString &destination,
                             const QString &authorization,
                             bool includeAndroidAudio) {
    QProcess *mediaProcess = nullptr;
    if (m_selected.startsWith(QLatin1String(IosPrefix))) {
        mediaProcess = &m_mirror;
    } else if (m_selected.startsWith(QLatin1String(AndroidPrefix)) &&
               !m_outputDevice.isEmpty()) {
        mediaProcess = &m_androidPreview;
    }
    if (!running() || mediaProcess == nullptr ||
        mediaProcess->state() == QProcess::NotRunning || m_mediaStreamingRequested ||
        (mediaProcess == &m_androidPreview &&
            (m_androidPreviewClosing || !m_androidOutputReady))) {
        setStatus(m_selected.startsWith(QLatin1String(AndroidPrefix))
            ? tr("Para transmitir Android, selecciona una cámara virtual y espera el primer cuadro de la vista previa")
            : tr("Inicia primero la duplicación de iPhone o iPad"));
        return false;
    }
    const auto url = QUrl(destination.trimmed());
    const auto scheme = url.scheme().toLower();
    const bool validKind = kind == QLatin1String("rtmp") ||
        kind == QLatin1String("srt") || kind == QLatin1String("whip");
    const bool validScheme = (kind == QLatin1String("rtmp") &&
        (scheme == QLatin1String("rtmp") || scheme == QLatin1String("rtmps"))) ||
        (kind == QLatin1String("srt") && scheme == QLatin1String("srt")) ||
        (kind == QLatin1String("whip") &&
         (scheme == QLatin1String("http") || scheme == QLatin1String("https")));
    if (!validKind || !url.isValid() || !validScheme ||
        destination.size() > 4096 || authorization.size() > 4096) {
        setStatus(tr("Revisa el protocolo y la dirección de transmisión"));
        return false;
    }
    const QString outputKind = kind == QLatin1String("rtmp") ? QStringLiteral("Rtmp")
        : kind == QLatin1String("srt") ? QStringLiteral("Srt") : QStringLiteral("Whip");
    const QJsonObject command{
        {QStringLiteral("command"), QStringLiteral("start")},
        {QStringLiteral("kind"), outputKind},
        {QStringLiteral("destination"), destination.trimmed()},
        {QStringLiteral("authorization"), authorization.trimmed()},
        {QStringLiteral("microphone"), m_microphoneEnabled},
        {QStringLiteral("androidAudio"),
            mediaProcess == &m_androidPreview && includeAndroidAudio &&
            m_androidAudioRouteAvailable},
        {QStringLiteral("androidAudioTarget"), m_androidAudioRouteAvailable
            ? m_androidAudioSinkName : QString{}},
    };
    auto payload = QJsonDocument(command).toJson(QJsonDocument::Compact);
    payload.append('\n');
    if (mediaProcess->write(payload) != payload.size()) {
        setStatus(tr("No se pudo enviar la configuración al motor de transmisión"));
        return false;
    }
    m_mediaStreamingRequested = true;
    setStatus(tr("Conectando la transmisión…"));
    return true;
}

bool Backend::startMediaRecording() {
    const bool ios = m_selected.startsWith(QLatin1String(IosPrefix));
    const bool android = m_selected.startsWith(QLatin1String(AndroidPrefix));
    auto *mediaProcess = ios ? &m_mirror : android ? &m_androidPreview : nullptr;
    if (!running() || mediaProcess == nullptr ||
        mediaProcess->state() == QProcess::NotRunning ||
        (android && (!m_androidOutputReady || m_androidPreviewClosing)) ||
        m_mediaRecordingActive || !m_recordingPath.isEmpty() ||
        m_recordingReviewPending) return false;
    auto pictures = QStandardPaths::writableLocation(QStandardPaths::PicturesLocation);
    if (pictures.isEmpty()) pictures = QDir::home().filePath(QStringLiteral("Pictures"));
    const auto directory = QDir(pictures).filePath(QStringLiteral("iPhoneMirror"));
    if (!QDir().mkpath(directory)) {
        setStatus(tr("No se pudo crear la carpeta para la grabación"));
        return false;
    }
    const auto timestamp = QDateTime::currentDateTime().toString(
        QStringLiteral("yyyyMMdd-HHmmss-zzz"));
    const auto path = QDir(directory).filePath(QStringLiteral("grabacion-%1-%2.mp4")
        .arg(android ? QStringLiteral("android") : QStringLiteral("iphone"), timestamp));
    const QJsonObject command{
        {QStringLiteral("command"), QStringLiteral("start")},
        {QStringLiteral("kind"), QStringLiteral("Recording")},
        {QStringLiteral("destination"), path},
        {QStringLiteral("authorization"), QString{}},
        {QStringLiteral("microphone"), m_microphoneEnabled},
        {QStringLiteral("androidAudio"), android && m_androidAudioRouteAvailable},
        {QStringLiteral("androidAudioTarget"), android && m_androidAudioRouteAvailable
            ? m_androidAudioSinkName : QString{}},
    };
    auto payload = QJsonDocument(command).toJson(QJsonDocument::Compact);
    payload.append('\n');
    if (mediaProcess->write(payload) != payload.size()) {
        setStatus(tr("No se pudo enviar la orden al motor de grabación"));
        return false;
    }
    m_recordingPath = path;
    m_manualAndroidRecording = android;
    setStatus(tr("Iniciando grabación…"));
    return true;
}

void Backend::stopMediaOutput(const QString &kind) {
    if (!running() || (kind != QLatin1String("recording") &&
                       kind != QLatin1String("streaming"))) return;
    const QJsonObject command{
        {QStringLiteral("command"), QStringLiteral("stop")},
        {QStringLiteral("kind"), kind},
    };
    auto payload = QJsonDocument(command).toJson(QJsonDocument::Compact);
    payload.append('\n');
    auto *mediaProcess = m_selected.startsWith(QLatin1String(AndroidPrefix))
        ? &m_androidPreview : &m_mirror;
    if (mediaProcess->write(payload) == payload.size())
        setStatus(kind == QLatin1String("recording")
            ? tr("Finalizando la grabación…") : tr("Deteniendo la transmisión…"));
}

void Backend::keepCompletedRecording() {
    if (!m_recordingReviewPending) return;
    m_recordingReviewPending = false;
    emit recordingReviewPendingChanged();
    setStatus(tr("Grabación conservada: %1").arg(m_lastRecordingPath));
}

void Backend::discardCompletedRecording() {
    if (!m_recordingReviewPending || m_lastRecordingPath.isEmpty()) return;
    if (!QFile::remove(m_lastRecordingPath) && QFileInfo::exists(m_lastRecordingPath)) {
        setStatus(tr("No se pudo eliminar la grabación"));
        return;
    }
    const auto removedPath = m_lastRecordingPath;
    m_lastRecordingPath.clear();
    m_recordingReviewPending = false;
    emit recordingReviewPendingChanged();
    setStatus(tr("Grabación descartada"));
    qInfo().noquote() << "Grabación descartada:" << removedPath;
}

void Backend::captureAndroidScreenshot() {
    if (!running() || !m_selected.startsWith(QLatin1String(AndroidPrefix))) {
        setStatus(tr("Inicia la duplicación de Android para guardar una captura"));
        return;
    }
    if (m_androidScreenshot.state() != QProcess::NotRunning) return;

    const auto adb = QStandardPaths::findExecutable(QStringLiteral("adb"));
    if (adb.isEmpty()) {
        setStatus(tr("Instala android-tools para capturar la pantalla de Android"));
        return;
    }
    auto pictures = QStandardPaths::writableLocation(QStandardPaths::PicturesLocation);
    if (pictures.isEmpty())
        pictures = QDir::home().filePath(QStringLiteral("Pictures"));
    const auto directory = QDir(pictures).filePath(QStringLiteral("iPhoneMirror"));
    if (!QDir().mkpath(directory)) {
        setStatus(tr("No se pudo crear la carpeta de capturas"));
        return;
    }
    const auto timestamp = QDateTime::currentDateTime().toString(
        QStringLiteral("yyyyMMdd-HHmmss-zzz"));
    m_androidScreenshotPath = QDir(directory).filePath(
        QStringLiteral("captura-android-%1.png").arg(timestamp));
    const auto serial = m_selected.mid(
        static_cast<qsizetype>(QString::fromLatin1(AndroidPrefix).size()));
    m_androidScreenshot.setProgram(adb);
    m_androidScreenshot.setArguments({QStringLiteral("-s"), serial,
        QStringLiteral("exec-out"), QStringLiteral("screencap"),
        QStringLiteral("-p")});
    m_androidScreenshot.setStandardOutputFile(m_androidScreenshotPath,
        QIODevice::Truncate);
    m_androidScreenshot.start();
    setStatus(tr("Guardando captura de Android…"));
}

void Backend::sendAndroidNavigation(const QString &action) {
    if (!m_selected.startsWith(QLatin1String(AndroidPrefix)) ||
        m_mirror.state() == QProcess::NotRunning) {
        setStatus(tr("Inicia la duplicación de Android para usar estos controles"));
        return;
    }

    QString keyCode;
    if (action == QLatin1String("back"))
        keyCode = QStringLiteral("KEYCODE_BACK");
    else if (action == QLatin1String("home"))
        keyCode = QStringLiteral("KEYCODE_HOME");
    else if (action == QLatin1String("overview"))
        keyCode = QStringLiteral("KEYCODE_APP_SWITCH");
    else
        return;

    const auto adb = QStandardPaths::findExecutable(QStringLiteral("adb"));
    if (adb.isEmpty()) {
        setStatus(tr("Instala android-tools para controlar Android"));
        return;
    }
    const auto serial = m_selected.mid(
        static_cast<qsizetype>(QString::fromLatin1(AndroidPrefix).size()));
    auto *command = new QProcess(this);
    command->setProgram(adb);
    command->setArguments({QStringLiteral("-s"), serial, QStringLiteral("shell"),
        QStringLiteral("input"), QStringLiteral("keyevent"), keyCode});
    command->setStandardOutputFile(QProcess::nullDevice());
    connect(command, &QProcess::finished, this,
        [this, command, serial, action](int exitCode, QProcess::ExitStatus exitStatus) {
            const auto error = QString::fromLocal8Bit(
                command->readAllStandardError()).trimmed().replace(serial,
                    QStringLiteral("[dispositivo]"));
            if (exitCode != 0 || exitStatus != QProcess::NormalExit) {
                qWarning().noquote() << "Falló el control de navegación Android"
                    << action << error;
                if (m_selected == QLatin1String(AndroidPrefix) + serial)
                    setStatus(tr("Android no aceptó el control de navegación"));
            }
            command->deleteLater();
        });
    connect(command, &QProcess::errorOccurred, this,
        [this, command, serial](QProcess::ProcessError error) {
            if (error != QProcess::FailedToStart) return;
            qWarning().noquote() << "No se pudo iniciar ADB para el control Android";
            if (m_selected == QLatin1String(AndroidPrefix) + serial)
                setStatus(tr("No se pudo iniciar ADB para controlar Android"));
            command->deleteLater();
        });
    QTimer::singleShot(5000, command, [command] {
        if (command->state() != QProcess::NotRunning) command->kill();
    });
    command->start();
}

void Backend::sendIosBluetoothShortcut(const QString &action) {
    if (!m_selected.startsWith(QLatin1String(IosPrefix)) ||
        m_mirror.state() == QProcess::NotRunning || !m_reverseControlEnabled ||
        (action != QLatin1String("home") &&
         action != QLatin1String("appSwitcher"))) return;
    QJsonObject command{
        {QStringLiteral("command"), QStringLiteral("iosShortcut")},
        {QStringLiteral("action"), action},
    };
    auto payload = QJsonDocument(command).toJson(QJsonDocument::Compact);
    payload.append('\n');
    if (m_mirror.write(payload) != payload.size())
        setStatus(tr("No se pudo enviar el control Bluetooth al iPhone/iPad"));
}
