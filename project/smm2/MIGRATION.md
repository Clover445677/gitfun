# Миграция базы данных и запуск

## Миграция

При следующем запуске приложение автоматически создаст таблицу `versions` и добавит поле `generations.current_version_id` в существующую SQLite-базу. Существующие записи сохраняются и продолжают работать: их поле `result` остаётся источником ТЗ до первой новой генерации или перегенерации.

Перед обновлением рекомендуется создать резервную копию файла `data/smm_briefs.db`.

Для других СУБД выполните миграцию вручную до запуска приложения:

```sql
ALTER TABLE generations ADD COLUMN current_version_id INTEGER NULL;
CREATE TABLE versions (
  id INTEGER PRIMARY KEY,
  generation_id INTEGER NOT NULL,
  version_number INTEGER NOT NULL,
  copywriter_brief JSON NOT NULL,
  designer_brief JSON NOT NULL,
  is_active BOOLEAN NOT NULL,
  is_matched BOOLEAN NOT NULL,
  created_at DATETIME NOT NULL,
  user_prompt VARCHAR(3000) NOT NULL,
  parent_version_id INTEGER NULL
);
```

## Обновление и запуск

1. Установите зависимости: `pip install -r requirements.txt`.
2. Запустите локальную Ollama и загрузите модель из `OPENAI_MODEL` (по умолчанию `qwen3:14b`).
3. Запустите приложение из папки `smm2`: `uvicorn main:app --host 0.0.0.0 --port 8000`.
4. Откройте `http://localhost:8000`.

Локальный адрес Ollama не изменён: `http://localhost:11434/v1`.
