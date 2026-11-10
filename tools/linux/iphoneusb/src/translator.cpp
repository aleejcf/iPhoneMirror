#include "translator.h"
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QGuiApplication>
#include <QJsonDocument>

Translator::Translator(QObject *parent) : QTranslator(parent) {
    m_debounce.setSingleShot(true); m_debounce.setInterval(100);
    connect(&m_watcher, &QFileSystemWatcher::directoryChanged, &m_debounce, qOverload<>(&QTimer::start));
    connect(&m_watcher, &QFileSystemWatcher::fileChanged, &m_debounce, qOverload<>(&QTimer::start));
    connect(&m_debounce, &QTimer::timeout, this, &Translator::reload);
    reload();
}
QString Translator::translate(const char *, const char *source, const char *, int) const {
    return m_strings.value(QString::fromUtf8(source)).toString();
}
void Translator::reload() {
    const auto old = m_watcher.files() + m_watcher.directories();
    if (!old.isEmpty()) m_watcher.removePaths(old);
    const QString path = QDir::homePath() + "/.config/omarchy/language";
    const QString dir = QFileInfo(path).absolutePath();
    if (QFileInfo::exists(dir)) m_watcher.addPath(dir);
    if (QFileInfo::exists(path)) m_watcher.addPath(path);
    QFile languageFile(path);
    const QString language = languageFile.open(QIODevice::ReadOnly)
        ? QString::fromUtf8(languageFile.readLine()).trimmed() : QStringLiteral("en");
    const QString code = language.section('-', 0, 0);
    if (code == m_language && !m_strings.isEmpty()) return;
    m_language = code;
    QFile catalog(QStringLiteral(":/i18n/%1.json").arg(code));
    m_strings = catalog.open(QIODevice::ReadOnly)
        ? QJsonDocument::fromJson(catalog.readAll()).object() : QJsonObject{};
    const QLocale locale(code);
    QLocale::setDefault(locale);
    QGuiApplication::setLayoutDirection(locale.textDirection());
    emit changed();
}
