#pragma once
#include <QFileSystemWatcher>
#include <QObject>
#include <QProcess>
#include <QTimer>
#include <QTimer>

class Theme : public QObject {
    Q_OBJECT
    Q_PROPERTY(QString accent READ accent NOTIFY changed)
    Q_PROPERTY(int radius READ radius NOTIFY changed)
public:
    explicit Theme(QObject *parent = nullptr);
    QString accent() const { return m_accent; }
    int radius() const { return m_radius; }
signals: void changed();
private:
    void reload();
    void refreshRounding();
    QString m_accent = "#FFD60A";
    int m_radius = 0;
    QFileSystemWatcher m_watcher;
    QTimer m_debounce;
    QTimer m_roundingPoll;
    QProcess m_hyprctl;
};
