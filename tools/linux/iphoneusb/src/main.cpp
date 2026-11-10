#include <QGuiApplication>
#include <QQmlApplicationEngine>
#include <QQmlContext>
#include <QQuickStyle>
#include <QIcon>
#include "backend.h"
#include "theme.h"
#include "translator.h"

int main(int argc, char **argv) {
    QGuiApplication app(argc, argv);
    app.setApplicationName("iPhoneMirror");
    app.setOrganizationName("iPhoneMirror");
    app.setOrganizationDomain("iphonemirror.local");
    app.setApplicationVersion("0.2.0");
    app.setDesktopFileName("iphoneusb");
    app.setWindowIcon(QIcon(QStringLiteral(":/assets/iPhoneMirror.png")));
    QQuickStyle::setStyle("Material");
    Backend backend;
    Theme theme;
    Translator translator;
    app.installTranslator(&translator);
    QQmlApplicationEngine engine;
    QObject::connect(&translator, &Translator::changed, &engine, &QQmlEngine::retranslate);
    engine.rootContext()->setContextProperty("backend", &backend);
    engine.rootContext()->setContextProperty("theme", &theme);
    engine.rootContext()->setContextProperty("translator", &translator);
    engine.load(QUrl("qrc:/Main.qml"));
    return engine.rootObjects().isEmpty() ? 1 : app.exec();
}
