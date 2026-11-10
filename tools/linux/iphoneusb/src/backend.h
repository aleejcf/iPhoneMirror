#pragma once

#include <QObject>
#include <QProcess>
#include <QPointer>
#include <QElapsedTimer>
#include <QHash>
#include <QJsonObject>
#include <QSet>
#include <QTimer>
#include <QTcpServer>
#include <QVariantList>

class QTcpSocket;

class Backend : public QObject {
    Q_OBJECT
    Q_PROPERTY(QVariantList devices READ devices NOTIFY devicesChanged)
    Q_PROPERTY(QVariantList outputDevices READ outputDevices NOTIFY outputDevicesChanged)
    Q_PROPERTY(QVariantList androidQualityProfiles READ androidQualityProfiles CONSTANT)
    Q_PROPERTY(QString selected READ selected WRITE setSelected NOTIFY selectedChanged)
    Q_PROPERTY(QString selectedLabel READ selectedLabel NOTIFY selectedChanged)
    Q_PROPERTY(int selectedIndex READ selectedIndex NOTIFY selectedChanged)
    Q_PROPERTY(QString outputDevice READ outputDevice WRITE setOutputDevice NOTIFY outputDeviceChanged)
    Q_PROPERTY(bool audioEnabled READ audioEnabled WRITE setAudioEnabled NOTIFY audioEnabledChanged)
    Q_PROPERTY(bool microphoneEnabled READ microphoneEnabled WRITE setMicrophoneEnabled NOTIFY microphoneEnabledChanged)
    Q_PROPERTY(bool recordingEnabled READ recordingEnabled WRITE setRecordingEnabled NOTIFY recordingEnabledChanged)
    Q_PROPERTY(bool mediaRecordingActive READ mediaRecordingActive NOTIFY mediaRecordingActiveChanged)
    Q_PROPERTY(bool mediaStreamingActive READ mediaStreamingActive NOTIFY mediaStreamingActiveChanged)
    Q_PROPERTY(bool recordingReviewPending READ recordingReviewPending NOTIFY recordingReviewPendingChanged)
    Q_PROPERTY(bool androidOutputReady READ androidOutputReady NOTIFY androidOutputReadyChanged)
    Q_PROPERTY(bool androidAudioRouteAvailable READ androidAudioRouteAvailable NOTIFY androidAudioRouteAvailableChanged)
    Q_PROPERTY(bool airplayRunning READ airplayRunning NOTIFY runningChanged)
    Q_PROPERTY(bool reverseControlEnabled READ reverseControlEnabled WRITE setReverseControlEnabled NOTIFY reverseControlEnabledChanged)
    Q_PROPERTY(bool reverseControlConnected READ reverseControlConnected NOTIFY reverseControlConnectedChanged)
    Q_PROPERTY(bool automationApiEnabled READ automationApiEnabled WRITE setAutomationApiEnabled NOTIFY automationApiEnabledChanged)
    Q_PROPERTY(QString automationApiKey READ automationApiKey CONSTANT)
    Q_PROPERTY(QString androidQualityProfile READ androidQualityProfile WRITE setAndroidQualityProfile NOTIFY androidQualityProfileChanged)
    Q_PROPERTY(QString iosOutputProfile READ iosOutputProfile WRITE setIosOutputProfile NOTIFY iosOutputProfileChanged)
    Q_PROPERTY(QVariantList iosProjectionModes READ iosProjectionModes CONSTANT)
    Q_PROPERTY(QString iosProjectionMode READ iosProjectionMode WRITE setIosProjectionMode NOTIFY iosProjectionModeChanged)
    Q_PROPERTY(bool running READ running NOTIFY runningChanged)
    Q_PROPERTY(double captureFps READ captureFps NOTIFY captureFpsChanged)
    Q_PROPERTY(QString status READ status NOTIFY statusChanged)
    Q_PROPERTY(QString deviceHealthText READ deviceHealthText NOTIFY deviceHealthChanged)
    Q_PROPERTY(bool deviceHealthWarning READ deviceHealthWarning NOTIFY deviceHealthChanged)
public:
    explicit Backend(QObject *parent = nullptr);
    ~Backend() override;
    QVariantList devices() const { return m_devices; }
    QVariantList outputDevices() const { return m_outputDevices; }
    QVariantList androidQualityProfiles() const;
    QVariantList iosOutputProfiles() const;
    QVariantList iosProjectionModes() const;
    QString selected() const { return m_selected; }
    QString selectedLabel() const;
    int selectedIndex() const;
    QString outputDevice() const { return m_outputDevice; }
    bool audioEnabled() const { return m_audioEnabled; }
    bool microphoneEnabled() const { return m_microphoneEnabled; }
    bool recordingEnabled() const { return m_recordingEnabled; }
    bool mediaRecordingActive() const { return m_mediaRecordingActive; }
    bool mediaStreamingActive() const { return m_mediaStreamingActive; }
    bool recordingReviewPending() const { return m_recordingReviewPending; }
    bool androidOutputReady() const { return m_androidOutputReady && !m_androidPreviewClosing; }
    bool androidAudioRouteAvailable() const { return m_androidAudioRouteAvailable; }
    QString androidQualityProfile() const { return m_androidQualityProfile; }
    QString iosOutputProfile() const { return m_iosOutputProfile; }
    QString iosProjectionMode() const { return m_iosProjectionMode; }
    void setSelected(const QString &value);
    void setOutputDevice(const QString &value);
    void setAudioEnabled(bool enabled);
    void setMicrophoneEnabled(bool enabled);
    void setRecordingEnabled(bool enabled);
    void setAndroidQualityProfile(const QString &profile);
    void setIosOutputProfile(const QString &profile);
    void setIosProjectionMode(const QString &mode);
    void setReverseControlEnabled(bool enabled);
    void setAutomationApiEnabled(bool enabled);
    bool running() const { return m_mirror.state() != QProcess::NotRunning ||
                                  m_airplay.state() != QProcess::NotRunning ||
                                  m_androidPreview.state() != QProcess::NotRunning; }
    double captureFps() const { return m_captureFps; }
    bool airplayRunning() const { return m_airplay.state() != QProcess::NotRunning; }
    bool reverseControlEnabled() const { return m_reverseControlEnabled; }
    bool reverseControlConnected() const { return m_reverseControlConnected; }
    bool automationApiEnabled() const { return m_automationApiEnabled; }
    QString automationApiKey() const { return m_automationApiKey; }
    QString status() const { return m_status; }
    QString deviceHealthText() const { return m_deviceHealthText; }
    bool deviceHealthWarning() const { return m_deviceHealthWarning; }
    Q_INVOKABLE void refresh();
    Q_INVOKABLE void start();
    Q_INVOKABLE void stop();
    Q_INVOKABLE bool startAirPlayReceiver();
    Q_INVOKABLE bool startStreaming(const QString &kind, const QString &destination,
                                    const QString &authorization,
                                    bool includeAndroidAudio = true);
    Q_INVOKABLE bool startMediaRecording();
    Q_INVOKABLE void stopMediaOutput(const QString &kind);
    Q_INVOKABLE void keepCompletedRecording();
    Q_INVOKABLE void discardCompletedRecording();
    Q_INVOKABLE void captureAndroidScreenshot();
    Q_INVOKABLE void sendAndroidNavigation(const QString &action);
    Q_INVOKABLE void sendIosBluetoothShortcut(const QString &action);
    Q_INVOKABLE bool pairAndroid(const QString &pairAddress, const QString &pairCode,
                                 const QString &connectionAddress);
    Q_INVOKABLE bool connectAndroidLegacy(const QString &ipAddress);
    Q_INVOKABLE QString diagnosticsText() const;
    Q_INVOKABLE void copyDiagnostics() const;
    Q_INVOKABLE void copyAutomationApiKey() const;
signals:
    void devicesChanged();
    void outputDevicesChanged();
    void selectedChanged();
    void outputDeviceChanged();
    void audioEnabledChanged();
    void microphoneEnabledChanged();
    void recordingEnabledChanged();
    void mediaRecordingActiveChanged();
    void mediaStreamingActiveChanged();
    void recordingReviewPendingChanged();
    void androidOutputReadyChanged();
    void androidAudioRouteAvailableChanged();
    void androidQualityProfileChanged();
    void iosOutputProfileChanged();
    void iosProjectionModeChanged();
    void reverseControlEnabledChanged();
    void reverseControlConnectedChanged();
    void automationApiEnabledChanged();
    void runningChanged();
    void captureFpsChanged();
    void statusChanged();
    void deviceHealthChanged();
private:
    void setStatus(const QString &status);
    void setCaptureFps(double fps);
    void finishRefreshIfReady();
    void applyRefreshResults();
    void probeIosDeviceName(const QString &id, const QString &serial);
    void refreshOutputDevices();
    void setAndroidOutputReady(bool ready);
    void setReverseControlConnected(bool connected);
    void acceptAutomationConnections();
    QByteArray automationResponse(const QByteArray &request, QTcpSocket *socket);
    void refreshAutomationGeometry();
    void cancelAutomationInput(const QString &deviceId);
    void expireAutomationLeases();
    void startAutomationInput(QTcpSocket *socket, const QString &deviceId,
                              const QString &program, const QStringList &arguments,
                              int timeoutMs);
    void startAutomationScreenshot(QTcpSocket *socket);
    void finishAutomationScreenshot(int exitCode, QProcess::ExitStatus status, bool ios);
    void failAutomationScreenshot(int httpStatus, const QByteArray &code);
    bool startAndroidAudioRoute();
    void stopAndroidAudioRoute();
    void refreshDeviceHealth();
    void updateDeviceHealthText();
    QVariantList m_devices;
    QVariantList m_outputDevices;
    QHash<QString, QString> m_iosDeviceNames;
    QSet<QString> m_iosMetadataAttempted;
    QString m_selected;
    QString m_outputDevice;
    bool m_audioEnabled = true;
    bool m_microphoneEnabled = false;
    bool m_recordingEnabled = false;
    bool m_mediaRecordingActive = false;
    bool m_manualAndroidRecording = false;
    bool m_mediaStreamingActive = false;
    bool m_recordingReviewPending = false;
    bool m_mediaStreamingRequested = false;
    bool m_androidPreviewClosing = false;
    bool m_androidOutputReady = false;
    bool m_androidAudioRouteAvailable = false;
    bool m_reverseControlEnabled = false;
    bool m_reverseControlConnected = false;
    bool m_automationApiEnabled = false;
    QString m_automationApiKey;
    QTcpServer m_automationServer;
    QSet<QTcpSocket *> m_automationSockets;
    QPointer<QTcpSocket> m_automationScreenshotSocket;
    QProcess m_automationScreenshot;
    QProcess m_automationIosScreenshot;
    QProcess m_automationGeometryProbe;
    QTimer m_automationScreenshotTimeout;
    QTimer m_automationGeometryTimer;
    QTimer m_automationGeometryTimeout;
    QTimer m_automationLeaseTimer;
    QJsonObject m_automationGeometry;
    QString m_automationGeometrySerial;
    struct AutomationLease {
        QString token;
        qint64 expiresAtMs = 0;
    };
    QHash<QString, AutomationLease> m_automationLeases;
    QHash<QTcpSocket *, QProcess *> m_automationInputProcesses;
    QHash<QTcpSocket *, QString> m_automationInputDevices;
    QSet<QString> m_automationInputInFlight;
    QByteArray m_automationScreenshotBytes;
    QString m_automationScreenshotPath;
    QElapsedTimer m_automationRateWindow;
    int m_automationRequestsInWindow = 0;
    QElapsedTimer m_automationScreenshotRateWindow;
    int m_automationScreenshotsInWindow = 0;
    QString m_androidQualityProfile = QStringLiteral("high");
    QString m_iosOutputProfile = QStringLiteral("vertical");
    QString m_iosProjectionMode = QStringLiteral("demo");
    QString m_status;
    double m_captureFps = -1.0;
    QString m_iosHealthSerial;
    QString m_androidHealthSerial;
    QString m_deviceHealthText;
    int m_iosHealthPhase = -1;
    int m_androidHealthPhase = -1;
    qint64 m_iosHealthNextPollMs = 0;
    qint64 m_androidHealthNextPollMs = 0;
    int m_iosBatteryPercent = -1;
    int m_iosBatteryCharging = -1;
    double m_iosTemperatureC = -1.0;
    int m_androidBatteryPercent = -1;
    int m_androidBatteryCharging = -1;
    double m_androidTemperatureC = -1.0;
    bool m_deviceHealthWarning = false;
    QProcess m_mirror;
    QProcess m_androidPreview;
    QProcess m_androidAudioLoopback;
    QProcess m_airplay;
    QByteArray m_mirrorStderrBuffer;
    QByteArray m_androidPreviewStderrBuffer;
    QProcess m_androidScreenshot;
    QProcess m_androidWireless;
    QProcess m_iosProbe;
    QProcess m_iosHealthProbe;
    QProcess m_androidHealthProbe;
    QProcess m_androidProbe;
    QTimer m_refresh;
    QTimer m_probeTimeout;
    QTimer m_androidWirelessTimeout;
    QTimer m_iosHealthTimer;
    QTimer m_iosHealthTimeout;
    QTimer m_androidHealthTimeout;
    QTimer m_mirrorStopTimeout;
    QString m_androidScreenshotPath;
    QString m_androidAudioSinkName;
    QString m_recordingPath;
    QString m_lastRecordingPath;
    QString m_androidPairAddress;
    QString m_androidPairCode;
    QString m_androidConnectionAddress;
    int m_androidWirelessPhase = 0;
    bool m_androidWirelessTimedOut = false;
    bool m_recordingStopRequested = false;
    bool m_recordingStopTimedOut = false;
    bool m_outputFailed = false;
    void handleMediaOutputEvent(const QByteArray &line);
    void processMirrorStderr(const QByteArray &data, bool flush = false,
                             QByteArray *buffer = nullptr);
    bool m_refreshActive = false;
    bool m_iosMissing = false;
    bool m_androidMissing = false;
    bool m_iosDeviceListFresh = false;
    bool m_androidDeviceListFresh = false;
    bool m_androidUnauthorized = false;
    bool m_probeTimedOut = false;
};
