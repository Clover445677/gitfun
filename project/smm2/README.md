# SMM Brief Generator

Веб-приложение для подготовки SMM-технических заданий копирайтеру и дизайнеру с помощью локальной языковой модели в Ollama.

## Возможности

- Генерация ТЗ и анализ референсов.
- История генераций и версии результатов.
- Отдельная перегенерация заданий копирайтеру и дизайнеру.
- Импорт контент-плана из Google Sheets.
- Экспорт в DOCX и PDF.

## Требования

- Python 3.12 — версия, используемая в Dockerfile проекта.
- Ollama с загруженной моделью.
- Современный браузер.

## Локальный запуск

```bash
git clone https://github.com/Clover445677/gitfun.git
cd gitfun/project/smm2
python -m venv .venv
```

Активируйте виртуальное окружение:

```bash
# macOS / Linux
source .venv/bin/activate
```

```powershell
# Windows PowerShell
.venv\Scripts\Activate.ps1
```

Установите зависимости:

```bash
python -m pip install -r requirements.txt
```

Скопируйте `.env.example` в `.env`:

```bash
# macOS / Linux
cp .env.example .env
```

```powershell
# Windows PowerShell
Copy-Item .env.example .env
```

В примере окружения указана модель `llama3.2:3b`. Загрузите её и запустите Ollama:

```bash
ollama pull llama3.2:3b
ollama serve
```

Если Ollama уже работает, повторный запуск `ollama serve` не нужен. В другом терминале, из папки `project/smm2` с активированным окружением, запустите приложение:

```bash
python -m uvicorn main:app --host 127.0.0.1 --port 8000
```

- Интерфейс: [http://localhost:8000](http://localhost:8000).
- Документация API: [http://localhost:8000/docs](http://localhost:8000/docs).

## Настройки

| Переменная | Назначение |
| --- | --- |
| `OPENAI_MODEL` | Модель Ollama. В `.env.example` — `llama3.2:3b`; без переменной код использует `qwen3:14b` |
| `DATABASE_URL` | База данных; по умолчанию `sqlite:///./data/smm_briefs.db` |
| `FONT_PATH` | Путь к TTF-шрифту с кириллицей для PDF, если системный шрифт не найден |
| `GOOGLE_APPLICATION_CREDENTIALS` | Путь к JSON-ключу сервисного аккаунта Google; нужен для интеграции с таблицами |

Адрес Ollama в текущем коде фиксирован: `http://localhost:11434/v1`. Ключ платного OpenAI API для этого подключения не требуется.

## Документация и структура

| Путь | Назначение |
| --- | --- |
| [main.py](main.py) | FastAPI, генерация, интеграции и экспорт |
| [models.py](models.py) | Модели базы данных |
| [schemas.py](schemas.py) | Схемы запросов и ответов |
| [prompts.py](prompts.py) | Промпты для модели |
| [static/index.html](static/index.html) | Веб-интерфейс |
| [Подключение Google Sheets](GOOGLE_SHEETS_SETUP.md) | Настройка доступа к контент-плану |
| [Миграция базы данных](MIGRATION.md) | Обновление существующей базы |
| [Dockerfile](Dockerfile) и [docker-compose.yml](docker-compose.yml) | Конфигурация контейнера |

## Особенности текущей конфигурации

- Запускать команды приложения следует из `project/smm2`.
- Готовые BAT-файлы используют путь `H:\gitfun\project\smm2`; перед использованием на другом компьютере его нужно изменить.
- Docker Compose запускает только приложение. В контейнере `localhost` указывает на сам контейнер, поэтому текущий адрес Ollama не подключает его автоматически к Ollama на хосте. Для такого запуска потребуется настройка адреса в коде и сети.
- Для обновления существующей базы сначала прочитайте руководство по миграции.
