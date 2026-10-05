# gitfun

Каталог независимых проектов и вспомогательных файлов.

| Проект | Назначение | Документация |
| --- | --- | --- |
| **Алевтина** (`alevtina/`) | Настройка VPN и маршрутизация ChatGPT/OpenAI через FlClashX на Windows | [Инструкция](alevtina/README.md) |
| **Pika / Pikabaka** | ИИ-помощник для встреч и интервью: расшифровка речи и подсказки | [Отдельный репозиторий](https://github.com/Clover445677/pikabaka) |
| **SMM Brief Generator** (`project/smm2/`) | Веб-приложение для подготовки SMM-ТЗ копирайтеру и дизайнеру с помощью Ollama | [Установка и запуск](project/smm2/README.md) |
| **Windows Update backup** (`wu-backup/`) | Резервные копии настроек реестра служб обновления Windows | [Что это за файлы](wu-backup/README.md) |

## Загрузка проектов

```bash
git clone --recurse-submodules https://github.com/Clover445677/gitfun.git
```

Если репозиторий уже скачан, загрузите Pika:

```bash
git submodule update --init pikabaka
```

`pikabaka/` подключена как подмодуль из отдельного репозитория [Clover445677/pikabaka](https://github.com/Clover445677/pikabaka). Инструкции Pika находятся в его README. У остальных проектов свои инструкции в папках, указанных выше.
