#pragma once
#include <QFileSystemWatcher>
#include <QJsonObject>
#include <QTranslator>
#include <QTimer>

class Translator : public QTranslator {
    Q_OBJECT
    Q_PROPERTY(QString language READ language NOTIFY changed)
public:
    explicit Translator(QObject *parent = nullptr);
    QString language() const { return m_language; }
    QString translate(const char *, const char *source, const char * = nullptr, int = -1) const override;
signals: void changed();
private:
    void reload();
    QString m_language = "en";
    QJsonObject m_strings;
    QFileSystemWatcher m_watcher;
    QTimer m_debounce;
};
