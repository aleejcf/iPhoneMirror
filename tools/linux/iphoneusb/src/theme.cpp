#include "theme.h"
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QJsonDocument>
#include <QJsonObject>
#include <QRegularExpression>
#include <QStandardPaths>

Theme::Theme(QObject *parent) : QObject(parent) {
    m_debounce.setSingleShot(true); m_debounce.setInterval(100);
    connect(&m_watcher, &QFileSystemWatcher::directoryChanged, &m_debounce, qOverload<>(&QTimer::start));
    connect(&m_watcher, &QFileSystemWatcher::fileChanged, &m_debounce, qOverload<>(&QTimer::start));
    connect(&m_debounce, &QTimer::timeout, this, &Theme::reload);
    m_roundingPoll.setInterval(1500);
    connect(&m_roundingPoll, &QTimer::timeout, this, &Theme::refreshRounding);
    m_roundingPoll.start();
    reload();
}
void Theme::refreshRounding() {
    if (m_hyprctl.state() != QProcess::NotRunning) return;
    const auto executable = QStandardPaths::findExecutable("hyprctl");
    if (executable.isEmpty()) return;
    connect(&m_hyprctl, &QProcess::finished, this, [this](int code, QProcess::ExitStatus status) {
        const auto object = QJsonDocument::fromJson(m_hyprctl.readAllStandardOutput()).object();
        const int value = object.value("int").toInt(0);
        if (code == 0 && status == QProcess::NormalExit && value >= 0 && value != m_radius) {
            m_radius = value;
            emit changed();
        }
    }, Qt::SingleShotConnection);
    m_hyprctl.start(executable, {"-j", "getoption", "decoration:rounding"});
}
void Theme::reload() {
    const auto old = m_watcher.files() + m_watcher.directories();
    if (!old.isEmpty()) m_watcher.removePaths(old);
    const QString dir = QDir::homePath() + "/.local/state/omarchy/current";
    QString ancestor = dir;
    while (!QFileInfo::exists(ancestor) && ancestor != "/") ancestor = QFileInfo(ancestor).absolutePath();
    for (const auto &path : {ancestor, dir, dir + "/theme", dir + "/theme/colors.toml"})
        if (QFileInfo::exists(path)) m_watcher.addPath(path);
    QFile file(dir + "/theme/colors.toml");
    if (file.open(QIODevice::ReadOnly)) {
        const auto match = QRegularExpression(R"(^\s*accent\s*=\s*["'](#[0-9a-fA-F]{6})["'])", QRegularExpression::MultilineOption).match(QString::fromUtf8(file.readAll()));
        if (match.hasMatch() && m_accent != match.captured(1)) { m_accent = match.captured(1); emit changed(); }
    }
}
